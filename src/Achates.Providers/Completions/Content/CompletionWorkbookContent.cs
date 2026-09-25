namespace Achates.Providers.Completions.Content;

/// <summary>An original workbook retained for the client; only Preview enters model context.</summary>
public sealed record CompletionWorkbookContent : CompletionUserContent
{
    public const string ExcelMime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public required string Data { get; init; }
    public required string FileName { get; init; }
    public string MimeType => ExcelMime;
    public required string WorkbookId { get; init; }
    public required string Preview { get; init; }
}
