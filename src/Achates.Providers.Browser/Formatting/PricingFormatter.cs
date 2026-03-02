namespace Achates.Providers.Browser.Formatting;

public static class PricingFormatter
{
    public static string FormatPerToken(string? price)
    {
        if (string.IsNullOrWhiteSpace(price))
            return "N/A";

        if (!decimal.TryParse(price, out var perToken))
            return price;

        if (perToken == 0m)
            return "Free";

        var perMillion = perToken * 1_000_000m;
        return $"${perMillion:0.##}/M";
    }
}
