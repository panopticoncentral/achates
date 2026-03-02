namespace Achates.Providers.Browser.Formatting;

public static class DateTimeFormatter
{
    public static string FromUnixTimestamp(long unixTimestamp)
    {
        var dto = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp);
        return dto.ToString("yyyy-MM-dd");
    }
}
