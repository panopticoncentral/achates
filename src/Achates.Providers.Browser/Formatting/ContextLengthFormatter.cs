namespace Achates.Providers.Browser.Formatting;

public static class ContextLengthFormatter
{
    public static string Format(int contextLength) => contextLength switch
    {
        >= 1_000_000 => $"{contextLength / 1_000_000d:0.#}M",
        >= 1_000 => $"{contextLength / 1_000d:0.#}K",
        _ => contextLength.ToString()
    };
}
