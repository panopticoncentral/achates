namespace Achates.Providers.Models;

/// <summary>
/// Modalities that models may accept or return.
/// </summary>
[Flags]
public enum ModelModalities
{
    /// <summary>
    /// Text.
    /// </summary>
    Text = 0x0000,

    /// <summary>
    /// An image.
    /// </summary>
    Image = 0x0001,

    /// <summary>
    /// A file.
    /// </summary>
    File = 0x0002,

    /// <summary>
    /// Audio.
    /// </summary>
    Audio = 0x0004,

    /// <summary>
    /// Video.
    /// </summary>
    Video = 0x0008,

    /// <summary>
    /// Embedding.
    /// </summary>
    Embeddings = 0x0010
}
