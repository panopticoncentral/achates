using System.Globalization;
using System.Text.Json;
using Achates.Providers.Completions;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Events;
using Achates.Providers.Completions.Messages;
using Achates.Providers.Models;
using Achates.Providers.OpenRouter.Chat;
using Achates.Providers.OpenRouter.Models;
using Achates.Providers.Util;

namespace Achates.Providers.OpenRouter;

internal sealed class OpenRouterProvider : IModelProvider
{
    public string Id => "openrouter";

    public string EnvironmentKey => "OPENROUTER_API_KEY";

    public HttpClient HttpClient { private get; set; } = null!;

    public string Key { private get; set; } = string.Empty;

    public CompletionEventStream GetCompletions(Model model, CompletionContext completionContext, CompletionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var client = new OpenRouterClient(HttpClient, Key);
        return CompletionEventStream.Create(stream =>
            ProcessStreamAsync(client, stream, model, completionContext, options, cancellationToken));
    }

    public async Task<IReadOnlyList<Model>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        var client = new OpenRouterClient(HttpClient, Key);
        var orModels = await client.GetModelsAsync(cancellationToken).ConfigureAwait(false);

        var models = new List<Model>(orModels.Count);
        models.AddRange(orModels.Select(or => new Model
        {
            Id = or.Id,
            Name = or.Name,
            Description = or.Description,
            Provider = this,
            Cost = MapCost(or.Pricing),
            ContextWindow = or.ContextLength,
            Input = MapModalities(or.Architecture?.InputModalities),
            Output = MapModalities(or.Architecture?.OutputModalities),
            Parameters = MapParameters(or.SupportedParameters),
        }));

        return models;
    }

    public async Task<byte[]?> GenerateImageAsync(string modelId, string prompt,
        CancellationToken cancellationToken = default)
    {
        var client = new OpenRouterClient(HttpClient, Key);

        var request = new OpenRouterChatCompletionRequest
        {
            Model = modelId,
            Messages =
            [
                new OpenRouterChatMessage
                {
                    Role = "user",
                    Content = JsonSerializer.SerializeToElement(prompt),
                },
            ],
            Modalities = ["image"],
        };

        var response = await client.CreateOpenRouterChatCompletionAsync(request, cancellationToken)
            .ConfigureAwait(false);

        var message = response?.Choices.FirstOrDefault()?.Message;
        if (message is null)
            return null;

        // Try the dedicated images field first
        if (message.Images is { Count: > 0 })
        {
            var url = message.Images[0].ImageUrl?.Url;
            if (url is { Length: > 0 })
            {
                var (_, data) = ParseDataUrl(url);
                return Convert.FromBase64String(data);
            }
        }

        // Fall back to content array (some models return images as content parts)
        if (message.Content is { ValueKind: JsonValueKind.Array } contentArray)
        {
            foreach (var part in contentArray.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var typeProp) &&
                    typeProp.GetString() is "image_url" &&
                    part.TryGetProperty("image_url", out var imgObj) &&
                    imgObj.TryGetProperty("url", out var urlProp) &&
                    urlProp.GetString() is { Length: > 0 } dataUrl)
                {
                    var (_, data) = ParseDataUrl(dataUrl);
                    return Convert.FromBase64String(data);
                }
            }
        }

        return null;
    }

    // ---- Streaming ----

    private sealed class BlockTracker
    {
        public readonly List<CompletionContent> Blocks = [];
        public CompletionContent? Current;
        public string? PartialArgs;
        public string? AudioId;
        public string? AudioData;
        public string? AudioTranscript;
        public string? AudioFormat;
        public int LastIndex => Blocks.Count - 1;
    }

    /// <summary>
    /// Drives the SSE streaming loop, converting OpenRouter chat completion chunks into
    /// provider-agnostic completion events pushed onto the stream.
    ///
    /// The assistant message (<paramref name="output"/>) is progressively built up:
    /// its Content list (owned by the tracker) is mutated in place as blocks arrive,
    /// while usage and stop reason are updated via immutable record copies.
    ///
    /// Each chunk may contain any combination of:
    ///   - Usage data (typically the final chunk) → updates token counts and cost
    ///   - A finish reason → maps to a CompletionStopReason
    ///   - Text, reasoning, or tool call deltas → delegated to typed handlers that
    ///     manage block lifecycle (start/delta/end events) through the BlockTracker
    ///
    /// On success, emits a CompletionDoneEvent with the fully-assembled message.
    /// On failure, emits a CompletionErrorEvent and marks the reason as Error or Aborted.
    /// The stream is always ended (via End()) so consumers never hang.
    /// </summary>
    private static async Task ProcessStreamAsync(
        OpenRouterClient client,
        CompletionEventStream stream,
        Model model,
        CompletionContext completionContext,
        CompletionOptions? options,
        CancellationToken cancellationToken)
    {
        var tracker = new BlockTracker();

        var output = new CompletionAssistantMessage
        {
            Content = tracker.Blocks,
            Model = model.Id,
            CompletionUsage = CompletionUsage.Empty,
            CompletionStopReason = CompletionStopReason.Stop,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        try
        {
            var request = BuildOpenRouterChatRequest(model, completionContext, options);
            stream.Push(new CompletionStartEvent { Partial = output });

            await foreach (var chunk in client.StreamOpenRouterChatCompletionAsync(request, cancellationToken).ConfigureAwait(false))
            {
                if (chunk.Usage is { } usage)
                {
                    output = output with { CompletionUsage = MapUsage(usage, model) };
                }

                if (chunk.Choices is not { Count: > 0 })
                {
                    continue;
                }

                var choice = chunk.Choices[0];

                if (choice.FinishReason is not null)
                {
                    output = output with { CompletionStopReason = MapStopReason(choice.FinishReason) };
                }

                var delta = choice.Delta;

                if (delta.Content is { Length: > 0 } text)
                {
                    ProcessTextDelta(stream, tracker, output, text);
                }

                if (delta.Reasoning is { Length: > 0 } reasoning)
                {
                    ProcessThinkingDelta(stream, tracker, output, reasoning);
                }

                if (delta.ToolCalls is { Count: > 0 } toolCalls)
                {
                    ProcessToolCallDeltas(stream, tracker, output, toolCalls);
                }

                if (delta.Images is { Count: > 0 } images)
                {
                    ProcessImageBlocks(stream, tracker, output, images);
                }

                if (delta.Audio is { } audio)
                {
                    ProcessAudioDelta(stream, tracker, output, audio, options);
                }
            }

            // Close any in-progress block and finalize the stream
            FinishCurrentBlock(stream, tracker, output);
            cancellationToken.ThrowIfCancellationRequested();

            stream.Push(new CompletionDoneEvent { Reason = output.CompletionStopReason, CompletionMessage = output });
            stream.End();
        }
        catch (Exception ex)
        {
            output = output with
            {
                CompletionStopReason = ex is OperationCanceledException ? CompletionStopReason.Aborted : CompletionStopReason.Error,
                ErrorMessage = ex.Message,
            };
            stream.Push(new CompletionErrorEvent { Reason = output.CompletionStopReason, Error = output });
            stream.End();
        }
    }

    // ---- Chunk processing ----

    private static CompletionUsage MapUsage(OpenRouterChatUsage usage, Model model)
    {
        var cachedTokens = 0;
        var reasoningTokens = 0;

        if (usage.PromptTokensDetails is { ValueKind: JsonValueKind.Object } ptd
            && ptd.TryGetProperty("cached_tokens", out var ct))
        {
            cachedTokens = ct.GetInt32();
        }

        if (usage.CompletionTokensDetails is { ValueKind: JsonValueKind.Object } ctd
            && ctd.TryGetProperty("reasoning_tokens", out var rt))
        {
            reasoningTokens = rt.GetInt32();
        }

        var inputTokens = usage.PromptTokens - cachedTokens;
        var outputTokens = usage.CompletionTokens + reasoningTokens;

        var result = new CompletionUsage
        {
            Input = inputTokens,
            Output = outputTokens,
            CacheRead = cachedTokens,
            CacheWrite = 0,
            TotalTokens = inputTokens + outputTokens + cachedTokens,
            Cost = new CompletionUsageCost(),
        };
        return result with { Cost = result.CalculateCost(model) };
    }

    private static void FinishCurrentBlock(
        CompletionEventStream stream, BlockTracker tracker, CompletionAssistantMessage output)
    {
        if (tracker.Current is null)
        {
            return;
        }

        switch (tracker.Current)
        {
            case CompletionTextContent tc:
                stream.Push(new CompletionTextEndEvent
                {
                    ContentIndex = tracker.LastIndex,
                    Content = tc.Text,
                    Partial = output,
                });
                break;
            case CompletionThinkingContent th:
                stream.Push(new CompletionThinkingEndEvent
                {
                    ContentIndex = tracker.LastIndex,
                    Content = th.Thinking,
                    Partial = output,
                });
                break;
            case CompletionToolCall tc:
            {
                var finalArgs = PartialJsonParser.ParseStreamingJson(tracker.PartialArgs);
                tracker.Blocks[tracker.LastIndex] = tc with { Arguments = finalArgs };
                stream.Push(new CompletionToolCallEndEvent
                {
                    ContentIndex = tracker.LastIndex,
                    CompletionToolCall = (CompletionToolCall)tracker.Blocks[tracker.LastIndex],
                    Partial = output,
                });
                break;
            }
            case CompletionAudioContent ac:
            {
                var final = ac with
                {
                    Id = tracker.AudioId,
                    Data = tracker.AudioData ?? "",
                    Transcript = string.IsNullOrEmpty(tracker.AudioTranscript) ? null : tracker.AudioTranscript,
                };
                tracker.Blocks[tracker.LastIndex] = final;
                stream.Push(new CompletionAudioEndEvent
                {
                    ContentIndex = tracker.LastIndex,
                    Content = final,
                    Partial = output,
                });
                break;
            }
        }

        tracker.Current = null;
        tracker.PartialArgs = null;
        tracker.AudioId = null;
        tracker.AudioData = null;
        tracker.AudioTranscript = null;
        tracker.AudioFormat = null;
    }

    private static void ProcessTextDelta(
        CompletionEventStream stream, BlockTracker tracker, CompletionAssistantMessage output, string delta)
    {
        if (tracker.Current is not CompletionTextContent)
        {
            FinishCurrentBlock(stream, tracker, output);
            var block = new CompletionTextContent { Text = "" };
            tracker.Blocks.Add(block);
            tracker.Current = block;
            stream.Push(new CompletionTextStartEvent { ContentIndex = tracker.LastIndex, Partial = output });
        }

        var tc = (CompletionTextContent)tracker.Current;
        var updated = tc with { Text = tc.Text + delta };
        tracker.Blocks[tracker.LastIndex] = updated;
        tracker.Current = updated;
        stream.Push(new CompletionTextDeltaEvent
        {
            ContentIndex = tracker.LastIndex,
            Delta = delta,
            Partial = output,
        });
    }

    private static void ProcessThinkingDelta(
        CompletionEventStream stream, BlockTracker tracker, CompletionAssistantMessage output, string delta)
    {
        if (tracker.Current is not CompletionThinkingContent)
        {
            FinishCurrentBlock(stream, tracker, output);
            var block = new CompletionThinkingContent { Thinking = "" };
            tracker.Blocks.Add(block);
            tracker.Current = block;
            stream.Push(new CompletionThinkingStartEvent { ContentIndex = tracker.LastIndex, Partial = output });
        }

        var th = (CompletionThinkingContent)tracker.Current;
        var updated = th with { Thinking = th.Thinking + delta };
        tracker.Blocks[tracker.LastIndex] = updated;
        tracker.Current = updated;
        stream.Push(new CompletionThinkingDeltaEvent
        {
            ContentIndex = tracker.LastIndex,
            Delta = delta,
            Partial = output,
        });
    }

    private static void ProcessToolCallDeltas(
        CompletionEventStream stream, BlockTracker tracker, CompletionAssistantMessage output,
        IReadOnlyList<OpenRouterChatDeltaToolCall> deltas)
    {
        foreach (var toolCallDelta in deltas)
        {
            var tcId = toolCallDelta.Id;

            if (tracker.Current is not CompletionToolCall
                || (tcId is not null && tracker.Current is CompletionToolCall existing && existing.Id != tcId))
            {
                FinishCurrentBlock(stream, tracker, output);
                tracker.Blocks.Add(new CompletionToolCall
                {
                    Id = tcId ?? "",
                    Name = toolCallDelta.Function?.Name ?? "",
                    Arguments = [],
                });
                tracker.Current = tracker.Blocks[tracker.LastIndex];
                tracker.PartialArgs = "";
                stream.Push(new CompletionToolCallStartEvent { ContentIndex = tracker.LastIndex, Partial = output });
            }

            var currentTc = (CompletionToolCall)tracker.Current!;

            if (tcId is not null)
            {
                currentTc = currentTc with { Id = tcId };
                tracker.Blocks[tracker.LastIndex] = currentTc;
                tracker.Current = currentTc;
            }

            if (toolCallDelta.Function?.Name is { } funcName)
            {
                currentTc = currentTc with { Name = funcName };
                tracker.Blocks[tracker.LastIndex] = currentTc;
                tracker.Current = currentTc;
            }

            var argsDelta = "";
            if (toolCallDelta.Function?.Arguments is { } funcArgs)
            {
                argsDelta = funcArgs;
                tracker.PartialArgs += funcArgs;
                currentTc = currentTc with { Arguments = PartialJsonParser.ParseStreamingJson(tracker.PartialArgs) };
                tracker.Blocks[tracker.LastIndex] = currentTc;
                tracker.Current = currentTc;
            }

            stream.Push(new CompletionToolCallDeltaEvent
            {
                ContentIndex = tracker.LastIndex,
                Delta = argsDelta,
                Partial = output,
            });
        }
    }

    private static void ProcessImageBlocks(
        CompletionEventStream stream, BlockTracker tracker, CompletionAssistantMessage output,
        IReadOnlyList<OpenRouterChatContentPart> images)
    {
        FinishCurrentBlock(stream, tracker, output);

        foreach (var img in images)
        {
            if (img.ImageUrl?.Url is not { Length: > 0 } url)
            {
                continue;
            }

            var (mimeType, data) = ParseDataUrl(url);
            var block = new CompletionImageContent { Data = data, MimeType = mimeType };
            tracker.Blocks.Add(block);
            stream.Push(new CompletionImageEvent
            {
                ContentIndex = tracker.LastIndex,
                Image = block,
                Partial = output,
            });
        }
    }

    private static void ProcessAudioDelta(
        CompletionEventStream stream, BlockTracker tracker, CompletionAssistantMessage output,
        OpenRouterChatAudioDelta audio, CompletionOptions? options)
    {
        if (tracker.Current is not CompletionAudioContent)
        {
            FinishCurrentBlock(stream, tracker, output);
            var format = options?.Audio?.Format ?? "pcm16";
            var block = new CompletionAudioContent { Data = "", Format = format };
            tracker.Blocks.Add(block);
            tracker.Current = block;
            tracker.AudioData = "";
            tracker.AudioTranscript = "";
            tracker.AudioFormat = format;
            stream.Push(new CompletionAudioStartEvent { ContentIndex = tracker.LastIndex, Partial = output });
        }

        if (audio.Id is { Length: > 0 } id)
        {
            tracker.AudioId = id;
        }

        string? dataDelta = null;
        string? transcriptDelta = null;

        if (audio.Data is { Length: > 0 } data)
        {
            dataDelta = data;
            tracker.AudioData += data;
        }

        if (audio.Transcript is { Length: > 0 } transcript)
        {
            transcriptDelta = transcript;
            tracker.AudioTranscript += transcript;
        }

        if (dataDelta is not null || transcriptDelta is not null)
        {
            var ac = (CompletionAudioContent)tracker.Current!;
            var updated = ac with
            {
                Data = tracker.AudioData ?? "",
                Transcript = tracker.AudioTranscript,
            };
            tracker.Blocks[tracker.LastIndex] = updated;
            tracker.Current = updated;

            stream.Push(new CompletionAudioDeltaEvent
            {
                ContentIndex = tracker.LastIndex,
                DataDelta = dataDelta,
                TranscriptDelta = transcriptDelta,
                Partial = output,
            });
        }
    }

    private static (string mimeType, string data) ParseDataUrl(string url)
    {
        // data:image/png;base64,iVBOR...
        if (url.StartsWith("data:", StringComparison.Ordinal))
        {
            var semicolon = url.IndexOf(';');
            var comma = url.IndexOf(',');
            if (semicolon > 5 && comma > semicolon)
            {
                return (url[5..semicolon], url[(comma + 1)..]);
            }
        }

        // Fallback: treat the whole URL as data with unknown type
        return ("image/png", url);
    }

    // ---- Request building ----

    private static OpenRouterChatCompletionRequest BuildOpenRouterChatRequest(
        Model model,
        CompletionContext completionContext,
        CompletionOptions? options)
    {
        var messages = ConvertMessages(model, completionContext);
        var tools = completionContext.Tools is { Count: > 0 } ? ConvertTools(completionContext.Tools) : null;

        // If no tools defined but conversation has tool history, send empty tools array
        if (tools is null && HasToolHistory(completionContext.Messages))
        {
            tools = [];
        }

        JsonElement? toolChoice = null;
        if (options?.ToolChoice is not null)
        {
            toolChoice = options.ToolChoice.Type switch
            {
                ToolChoiceType.Auto => JsonSerializer.SerializeToElement("auto"),
                ToolChoiceType.None => JsonSerializer.SerializeToElement("none"),
                ToolChoiceType.Required => JsonSerializer.SerializeToElement("required"),
                ToolChoiceType.Function => JsonSerializer.SerializeToElement(new
                {
                    type = "function", function = new { name = options.ToolChoice.Name },
                }),
                _ => null
            };
        }

        OpenRouterChatReasoningConfig? reasoning = null;
        if (options?.ReasoningEffort is not null
            && model.Parameters.HasFlag(ModelParameters.Reasoning))
        {
            reasoning = new OpenRouterChatReasoningConfig { Effort = options.ReasoningEffort };
        }

        OpenRouterChatResponseFormat? responseFormat = null;
        if (options?.ResponseFormat is { } rf)
        {
            responseFormat = new OpenRouterChatResponseFormat
            {
                Type = rf.Type,
                JsonSchema = rf.JsonSchema is { } schema
                    ? new OpenRouterChatJsonSchema
                    {
                        Name = "response",
                        Schema = schema,
                    }
                    : null,
            };
        }

        IReadOnlyList<string>? modalities = null;
        OpenRouterChatAudioConfig? audioConfig = null;
        if (options?.Audio is not null && model.Output.HasFlag(ModelModalities.Audio))
        {
            modalities = ["text", "audio"];
            audioConfig = new OpenRouterChatAudioConfig
            {
                Voice = options.Audio.Voice ?? "alloy",
                Format = options.Audio.Format ?? "pcm16",
            };
        }

        return new OpenRouterChatCompletionRequest
        {
            Model = model.Id,
            Messages = messages,
            Modalities = modalities,
            Audio = audioConfig,
            Stream = true,
            StreamOptions = new OpenRouterChatStreamOptions { IncludeUsage = true },
            Temperature = options?.Temperature,
            TopP = options?.TopP,
            TopK = options?.TopK,
            MinP = options?.MinP,
            TopA = options?.TopA,
            LogitBias = options?.LogitBias?.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            FrequencyPenalty = options?.FrequencyPenalty,
            PresencePenalty = options?.PresencePenalty,
            RepetitionPenalty = options?.RepetitionPenalty,
            MaxCompletionTokens = options?.MaxTokens,
            Seed = options?.Seed,
            Stop = options?.Stop,
            Logprobs = options?.Logprobs,
            TopLogprobs = options?.TopLogprobs,
            Tools = tools,
            ToolChoice = toolChoice,
            ParallelToolCalls = options?.ParallelToolCalls,
            ResponseFormat = responseFormat,
            Reasoning = reasoning,
        };
    }

    // ---- Message conversion ----

    private static List<OpenRouterChatMessage> ConvertMessages(Model model, CompletionContext completionContext)
    {
        var result = new List<OpenRouterChatMessage>();

        // System prompt
        if (!string.IsNullOrEmpty(completionContext.SystemPrompt))
        {
            var role = model.Parameters.HasFlag(ModelParameters.Reasoning) ? "developer" : "system";
            result.Add(new OpenRouterChatMessage
            {
                Role = role,
                Content = JsonSerializer.SerializeToElement(
                    UnicodeSanitizer.SanitizeSurrogates(completionContext.SystemPrompt)),
            });
        }

        foreach (var msg in completionContext.Messages)
        {
            switch (msg)
            {
                case CompletionUserTextMessage textMsg:
                    result.Add(ConvertUserTextMessage(textMsg));
                    break;
                case CompletionUserContentMessage blocksMsg:
                    result.Add(ConvertUserBlocksMessage(blocksMsg, model));
                    break;
                case CompletionAssistantMessage assistant:
                    var converted = ConvertAssistantMessage(assistant);
                    if (converted is not null)
                    {
                        result.Add(converted);
                    }

                    break;
                case CompletionToolResultMessage toolResult:
                    result.Add(ConvertToolResult(toolResult));
                    break;
            }
        }

        return result;
    }

    private static OpenRouterChatMessage ConvertUserTextMessage(CompletionUserTextMessage textMsg)
    {
        return new OpenRouterChatMessage
        {
            Role = "user",
            Content = JsonSerializer.SerializeToElement(
                UnicodeSanitizer.SanitizeSurrogates(textMsg.Text)),
        };
    }

    private static OpenRouterChatMessage ConvertUserBlocksMessage(CompletionUserContentMessage contentMsg, Model model)
    {
        var parts = new List<OpenRouterChatContentPart>();
        foreach (var block in contentMsg.Content)
        {
            switch (block)
            {
                case CompletionTextContent tc:
                    parts.Add(new OpenRouterChatContentPart
                    {
                        Type = "text",
                        Text = UnicodeSanitizer.SanitizeSurrogates(tc.Text),
                    });
                    break;
                case CompletionImageContent img when model.Input.HasFlag(ModelModalities.Image):
                    parts.Add(new OpenRouterChatContentPart
                    {
                        Type = "image_url",
                        ImageUrl = new OpenRouterChatImageUrl
                        {
                            Url = $"data:{img.MimeType};base64,{img.Data}",
                        },
                    });
                    break;
                case CompletionFileContent file when model.Input.HasFlag(ModelModalities.File):
                    parts.Add(new OpenRouterChatContentPart
                    {
                        Type = "file",
                        File = new OpenRouterChatFileData
                        {
                            FileName = file.FileName ?? "file",
                            FileData = $"data:{file.MimeType};base64,{file.Data}",
                        },
                    });
                    break;
                case CompletionAudioInputContent audio when model.Input.HasFlag(ModelModalities.Audio):
                    parts.Add(new OpenRouterChatContentPart
                    {
                        Type = "input_audio",
                        InputAudio = new OpenRouterChatInputAudio
                        {
                            Data = audio.Data,
                            Format = audio.Format,
                        },
                    });
                    break;
            }
        }

        return new OpenRouterChatMessage
        {
            Role = "user",
            Content = JsonSerializer.SerializeToElement(parts, OpenRouterJsonContext.Default.IReadOnlyListOpenRouterChatContentPart),
        };
    }

    private static OpenRouterChatMessage? ConvertAssistantMessage(CompletionAssistantMessage completionAssistant)
    {
        var textBlocks = completionAssistant.Content.OfType<CompletionTextContent>()
            .Where(b => !string.IsNullOrWhiteSpace(b.Text))
            .ToList();

        var thinkingBlocks = completionAssistant.Content.OfType<CompletionThinkingContent>()
            .Where(b => !string.IsNullOrWhiteSpace(b.Thinking))
            .ToList();

        var toolCalls = completionAssistant.Content.OfType<CompletionToolCall>().ToList();
        var imageBlocks = completionAssistant.Content.OfType<CompletionImageContent>().ToList();
        var audioBlocks = completionAssistant.Content.OfType<CompletionAudioContent>().ToList();

        // Skip empty assistant messages
        if (textBlocks.Count == 0 && toolCalls.Count == 0 && imageBlocks.Count == 0 && audioBlocks.Count == 0)
        {
            return null;
        }

        JsonElement? content = null;
        if (textBlocks.Count > 0)
        {
            var text = string.Join("\n", textBlocks.Select(b =>
                UnicodeSanitizer.SanitizeSurrogates(b.Text)));
            content = JsonSerializer.SerializeToElement(text);
        }

        List<OpenRouterChatToolCall>? chatToolCalls = null;
        if (toolCalls.Count > 0)
        {
            chatToolCalls = toolCalls.Select(tc => new OpenRouterChatToolCall
            {
                Id = tc.Id,
                Type = "function",
                Function = new OpenRouterChatToolCallFunction
                {
                    Name = tc.Name,
                    Arguments = JsonSerializer.Serialize(tc.Arguments),
                },
            }).ToList();
        }

        string? reasoning = null;
        if (thinkingBlocks.Count > 0)
        {
            reasoning = string.Join("\n", thinkingBlocks.Select(b => b.Thinking));
        }

        List<OpenRouterChatContentPart>? chatImages = null;
        if (imageBlocks.Count > 0)
        {
            chatImages = imageBlocks.Select(img => new OpenRouterChatContentPart
            {
                Type = "image_url",
                ImageUrl = new OpenRouterChatImageUrl
                {
                    Url = $"data:{img.MimeType};base64,{img.Data}",
                },
            }).ToList();
        }

        // For audio responses, include the transcript as text content rather than
        // referencing by audio ID — IDs are OpenAI-specific and may not survive proxying.
        if (audioBlocks.Count > 0)
        {
            var transcript = string.Join("\n", audioBlocks
                .Where(a => !string.IsNullOrWhiteSpace(a.Transcript))
                .Select(a => a.Transcript));
            if (!string.IsNullOrWhiteSpace(transcript))
            {
                var existingText = content?.ValueKind == JsonValueKind.String
                    ? content.Value.GetString() + "\n"
                    : "";
                content = JsonSerializer.SerializeToElement(existingText + transcript);
            }
        }

        return new OpenRouterChatMessage
        {
            Role = "assistant",
            Content = content,
            ToolCalls = chatToolCalls,
            Reasoning = reasoning,
            Images = chatImages,
        };
    }

    private static OpenRouterChatMessage ConvertToolResult(CompletionToolResultMessage completionToolResult)
    {
        var text = string.Join("\n",
            completionToolResult.Content.OfType<CompletionTextContent>().Select(c => c.Text));

        return new OpenRouterChatMessage
        {
            Role = "tool",
            Content = JsonSerializer.SerializeToElement(
                UnicodeSanitizer.SanitizeSurrogates(text.Length > 0 ? text : "(empty)")),
            ToolCallId = completionToolResult.ToolCallId,
        };
    }

    private static List<OpenRouterChatTool> ConvertTools(IReadOnlyList<CompletionTool> tools)
    {
        return tools.Select(tool => new OpenRouterChatTool
        {
            Type = "function",
            Function = new OpenRouterChatFunction
            {
                Name = tool.Name,
                Description = tool.Description,
                Parameters = tool.Parameters,
            },
        }).ToList();
    }

    // ---- Helpers ----

    private static bool HasToolHistory(IReadOnlyList<CompletionMessage> messages)
    {
        foreach (var msg in messages)
        {
            switch (msg)
            {
                case CompletionToolResultMessage:
                case CompletionAssistantMessage assistant
                    when assistant.Content.Any(b => b is CompletionToolCall):
                    return true;
            }
        }
        return false;
    }

    private static CompletionStopReason MapStopReason(string reason) => reason switch
    {
        "stop" => CompletionStopReason.Stop,
        "length" => CompletionStopReason.Length,
        "function_call" or "tool_calls" => CompletionStopReason.ToolUse,
        "content_filter" => CompletionStopReason.Error,
        _ => CompletionStopReason.Stop,
    };

    // ---- Model mapping ----

    private static ModelCost MapCost(OpenRouterPricing? pricing)
    {
        if (pricing is null)
        {
            return new ModelCost { Prompt = 0, Completion = 0 };
        }

        return new ModelCost
        {
            Prompt = ParseDecimal(pricing.Prompt),
            Completion = ParseDecimal(pricing.Completion),
            Request = ParseNullableDecimal(pricing.Request),
            Image = ParseNullableDecimal(pricing.Image),
            ImageToken = ParseNullableDecimal(pricing.ImageToken),
            ImageOutput = ParseNullableDecimal(pricing.ImageOutput),
            Audio = ParseNullableDecimal(pricing.Audio),
            AudioOutput = ParseNullableDecimal(pricing.AudioOutput),
            InputAudioCache = ParseNullableDecimal(pricing.InputAudioCache),
            WebSearch = ParseNullableDecimal(pricing.WebSearch),
            InternalReasoning = ParseNullableDecimal(pricing.InternalReasoning),
            InputCacheRead = ParseNullableDecimal(pricing.InputCacheRead),
            InputCacheWrite = ParseNullableDecimal(pricing.InputCacheWrite),
            Discount = pricing.Discount,
        };
    }

    private static ModelModalities MapModalities(IReadOnlyList<string>? modalities)
    {
        if (modalities is null or { Count: 0 })
        {
            return ModelModalities.Text;
        }

        var result = ModelModalities.Text;
        foreach (var modality in modalities)
        {
            result |= modality switch
            {
                "image" => ModelModalities.Image,
                "file" => ModelModalities.File,
                "audio" => ModelModalities.Audio,
                "video" => ModelModalities.Video,
                "embeddings" => ModelModalities.Embeddings,
                _ => 0,
            };
        }

        return result;
    }

    private static ModelParameters MapParameters(IReadOnlyList<string>? parameters)
    {
        if (parameters is null or { Count: 0 })
        {
            return ModelParameters.Temperature;
        }

        var result = ModelParameters.Temperature;
        foreach (var param in parameters)
        {
            result |= param switch
            {
                "temperature" => ModelParameters.Temperature,
                "top_p" => ModelParameters.TopP,
                "top_k" => ModelParameters.TopK,
                "min_p" => ModelParameters.MinP,
                "top_a" => ModelParameters.TopA,
                "frequency_penalty" => ModelParameters.FrequencyPenalty,
                "presence_penalty" => ModelParameters.PresencePenalty,
                "repetition_penalty" => ModelParameters.RepetitionPenalty,
                "max_tokens" => ModelParameters.MaxTokens,
                "logit_bias" => ModelParameters.LogitBias,
                "logprobs" => ModelParameters.LogProbs,
                "top_logprobs" => ModelParameters.TopLogProbs,
                "seed" => ModelParameters.Seed,
                "response_format" => ModelParameters.ResponseFormat,
                "structured_outputs" => ModelParameters.StructuredOutputs,
                "stop" => ModelParameters.Stop,
                "tools" => ModelParameters.Tools,
                "tool_choice" => ModelParameters.ToolChoice,
                "parallel_tool_calls" => ModelParameters.ParallelToolCalls,
                "include_reasoning" => ModelParameters.IncludeReasoning,
                "reasoning" => ModelParameters.Reasoning,
                "reasoning_effort" => ModelParameters.ReasoningEffort,
                "web_search_options" => ModelParameters.WebSearchOptions,
                "verbosity" => ModelParameters.Verbosity,
                _ => 0,
            };
        }

        return result;
    }

    private static decimal ParseDecimal(string? value) =>
        decimal.TryParse(value, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static decimal? ParseNullableDecimal(string? value) =>
        decimal.TryParse(value, CultureInfo.InvariantCulture, out var result) ? result : null;
}
