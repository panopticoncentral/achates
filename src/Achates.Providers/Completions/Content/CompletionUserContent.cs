using System.Text.Json.Serialization;

namespace Achates.Providers.Completions.Content;

/// <summary>
/// Content block that may appear in user messages or tool results.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CompletionTextContent), "text")]
[JsonDerivedType(typeof(CompletionImageContent), "image")]
[JsonDerivedType(typeof(CompletionAudioInputContent), "audio_input")]
[JsonDerivedType(typeof(CompletionFileContent), "file")]
public abstract record CompletionUserContent : CompletionContent;
