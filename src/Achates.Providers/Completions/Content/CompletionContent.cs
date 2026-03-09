using System.Text.Json.Serialization;

namespace Achates.Providers.Completions.Content;

/// <summary>
/// Base type for all content blocks in messages.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CompletionTextContent), "text")]
[JsonDerivedType(typeof(CompletionToolCall), "tool_call")]
[JsonDerivedType(typeof(CompletionThinkingContent), "thinking")]
[JsonDerivedType(typeof(CompletionImageContent), "image")]
[JsonDerivedType(typeof(CompletionAudioContent), "audio")]
[JsonDerivedType(typeof(CompletionAudioInputContent), "audio_input")]
[JsonDerivedType(typeof(CompletionFileContent), "file")]
public abstract record CompletionContent
{
}
