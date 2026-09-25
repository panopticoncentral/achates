using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Achates.Server.Workbooks;

/// <summary>Reads saved SpreadsheetML data without evaluating formulas or following external links.</summary>
internal sealed class WorkbookReader : IDisposable
{
    public const int MaxBytes = 8 * 1024 * 1024;
    public const int MaxRangeCells = 500;
    public const string Limitations = "Values are saved workbook data. Formula results may be stale or missing; formulas are never recalculated. " +
        "Numeric values are raw (including Excel date serials); interpret them using number_format and date_system. " +
        "Charts, macros, external data refresh, and editing are not supported. Blank cells are omitted. " +
        "Shared formulas expose the master formula and its anchor rather than an expanded formula for each cell.";

    private readonly SpreadsheetDocument _document;
    private readonly WorkbookPart _workbook;
    private readonly Dictionary<string, SheetDataInfo> _sheets = new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] _strings;
    private readonly CellFormat[] _formats;
    private readonly Dictionary<uint, string> _customFormats;
    private static readonly Regex AddressPattern = new(@"^([A-Za-z]{1,3})([1-9][0-9]{0,6})$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public WorkbookReader(byte[] bytes)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Workbook exceeds the 8 MB limit.");
        // Bound expansion before the SDK loads XML parts. Never extract archive paths to disk.
        using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
        {
            if (zip.Entries.Count > 4096 || zip.Entries.Sum(e => e.Length) > 64L * 1024 * 1024 ||
                zip.Entries.Any(e => e.Length > 16L * 1024 * 1024))
                throw new InvalidDataException("Workbook is too complex (expanded size or part count exceeds the limit).");
        }
        _document = SpreadsheetDocument.Open(new MemoryStream(bytes), false,
            new OpenSettings { MaxCharactersInPart = 16 * 1024 * 1024 });
        try
        {
            _workbook = _document.WorkbookPart ?? throw new InvalidDataException("Workbook part is missing.");
            _strings = _workbook.SharedStringTablePart?.SharedStringTable.Elements<SharedStringItem>()
                .Select(s => string.Concat(s.Descendants<Text>().Select(t => t.Text))).ToArray() ?? [];
            var styles = _workbook.WorkbookStylesPart?.Stylesheet;
            _formats = styles?.CellFormats?.Elements<CellFormat>().ToArray() ?? [];
            _customFormats = styles?.NumberingFormats?.Elements<NumberingFormat>()
                .ToDictionary(f => f.NumberFormatId!.Value, f => f.FormatCode?.Value ?? "") ?? [];
            var sheets = _workbook.Workbook.Sheets?.Elements<Sheet>().ToArray() ?? [];
            if (sheets.Length == 0 || sheets.Length > 128)
                throw new InvalidDataException("Workbook must contain between 1 and 128 sheets.");
            var totalCells = 0;
            foreach (var sheet in sheets)
            {
                var name = sheet.Name?.Value ?? throw new InvalidDataException("Sheet name is missing.");
                if (name.Length > 31) throw new InvalidDataException("Sheet name exceeds Excel's 31-character limit.");
                var part = _workbook.GetPartById(sheet.Id?.Value ?? "");
                if (part is not WorksheetPart worksheet) continue; // chart sheets are not cell grids
                var cells = new Dictionary<(int Row, int Column), Cell>();
                var shared = new Dictionary<uint, (string Address, string Formula)>();
                int inferredRow = 0;
                foreach (var row in worksheet.Worksheet.GetFirstChild<SheetData>()?.Elements<Row>() ?? [])
                {
                    inferredRow = row.RowIndex is { } rowIndex ? checked((int)rowIndex.Value) : inferredRow + 1;
                    int inferredColumn = 0;
                    foreach (var cell in row.Elements<Cell>())
                    {
                        if (++totalCells > 250_000) throw new InvalidDataException("Workbook exceeds the 250,000 stored-cell limit.");
                        var position = cell.CellReference?.Value is { } address
                            ? ParseAddress(address) : (Row: inferredRow, Column: inferredColumn + 1);
                        inferredColumn = position.Column;
                        cells.Add(position, cell);
                        if (cell.CellFormula is { SharedIndex: { } index } formula && !string.IsNullOrEmpty(formula.Text))
                            shared[index.Value] = (Address(position.Row, position.Column), formula.Text);
                    }
                }
                _sheets.Add(name, new(name, sheet.State?.Value.ToString() ?? "visible", cells, shared));
            }
        }
        catch { _document.Dispose(); throw; }
    }

    public string Describe(string id, string fileName) => JsonSerializer.Serialize(new
    {
        workbook_id = id, file_name = fileName, date_system = DateSystem, limitations = Limitations,
        instruction = "Use the workbook tool with this workbook_id to read a sheet or an A1 range. Use action=list to find workbooks after conversation compaction.",
        sheets = _sheets.Values.Select((s, index) => new
        {
            name = s.Name, state = s.State,
            rows = s.Cells.Count == 0 ? 0 : s.Cells.Keys.Max(p => p.Row),
            columns = s.Cells.Count == 0 ? 0 : s.Cells.Keys.Max(p => p.Column),
            stored_cells = s.Cells.Count,
            preview = s.Cells.OrderBy(p => p.Key.Row).ThenBy(p => p.Key.Column).Take(index < 4 ? 6 : 0)
                .Select(p => CellInfo(s, p.Key, p.Value, 120)),
            preview_truncated = s.Cells.Count > (index < 4 ? 6 : 0),
        }),
    }, JsonOptions);

    public string Read(string sheetName, string? range)
    {
        if (!_sheets.TryGetValue(sheetName, out var sheet))
            throw new InvalidDataException("Sheet not found. Use action=describe for the exact sheet names.");
        var effectiveRange = string.IsNullOrWhiteSpace(range) ? "A1:J50" : range.Trim();
        var ends = effectiveRange.Split(':');
        if (ends.Length is < 1 or > 2) throw new InvalidDataException("Use a single A1 cell or rectangular range, such as B2:F20.");
        var start = ParseAddress(ends[0]);
        var end = ParseAddress(ends[^1]);
        if (end.Row < start.Row || end.Column < start.Column ||
            (long)(end.Row - start.Row + 1) * (end.Column - start.Column + 1) > MaxRangeCells)
            throw new InvalidDataException($"Range must be ordered and contain at most {MaxRangeCells} cells. Request smaller ranges.");
        var cells = sheet.Cells.Where(p => p.Key.Row >= start.Row && p.Key.Row <= end.Row &&
            p.Key.Column >= start.Column && p.Key.Column <= end.Column)
            .OrderBy(p => p.Key.Row).ThenBy(p => p.Key.Column)
            .Select(p => CellInfo(sheet, p.Key, p.Value, 4000)).ToList();
        var result = JsonSerializer.Serialize(new
        {
            sheet = sheet.Name, range = effectiveRange, date_system = DateSystem,
            limitations = Limitations, cells,
        }, JsonOptions);
        if (result.Length > 64_000)
            throw new InvalidDataException("Range output exceeds 64,000 characters. Request a smaller range.");
        return result;
    }

    private string DateSystem => _workbook.Workbook.WorkbookProperties?.Date1904?.Value == true ? "1904" : "1900";

    private object CellInfo(SheetDataInfo sheet, (int Row, int Column) position, Cell cell, int maxText)
    {
        var value = cell.CellValue?.Text;
        var type = cell.DataType?.Value;
        var kind = type?.ToString() ?? "Number";
        if (type == CellValues.SharedString)
        {
            if (!int.TryParse(value, out var index) || index < 0 || index >= _strings.Length)
                throw new InvalidDataException("Invalid shared string reference.");
            value = _strings[index];
            kind = "String";
        }
        else if (type == CellValues.InlineString)
        {
            value = string.Concat(cell.InlineString?.Descendants<Text>().Select(t => t.Text) ?? []);
            kind = "String";
        }
        var style = cell.StyleIndex?.Value ?? 0;
        var formatId = style < _formats.Length ? _formats[style].NumberFormatId?.Value ?? 0 : 0;
        var format = _customFormats.GetValueOrDefault(formatId) ?? BuiltInFormat(formatId);
        var formula = cell.CellFormula;
        var shared = formula?.SharedIndex is { } sharedIndex
            ? sheet.SharedFormulas.GetValueOrDefault(sharedIndex.Value) : default;
        return new
        {
            address = Address(position.Row, position.Column), type = kind,
            value = Clip(value, maxText), value_truncated = value?.Length > maxText,
            formula = Clip(formula?.Text, maxText), formula_truncated = formula?.Text.Length > maxText,
            formula_kind = formula?.FormulaType?.Value.ToString(),
            formula_range = Clip(formula?.Reference?.Value, maxText),
            shared_formula_anchor = shared.Address, shared_formula = Clip(shared.Formula, maxText),
            shared_formula_truncated = shared.Formula?.Length > maxText,
            cached_result = formula is null ? null : value is null ? "missing" : "saved; may be stale",
            number_format_id = formatId, number_format = Clip(format, maxText), number_format_truncated = format.Length > maxText,
        };
    }

    private static string? Clip(string? text, int limit) => text?.Length > limit ? text[..limit] : text;

    private static string BuiltInFormat(uint id) => id switch
    {
        0 => "General", 1 => "0", 2 => "0.00", 3 => "#,##0", 4 => "#,##0.00",
        9 => "0%", 10 => "0.00%", 11 => "0.00E+00", 12 => "# ?/?", 13 => "# ??/??",
        14 => "mm-dd-yy", 15 => "d-mmm-yy", 16 => "d-mmm", 17 => "mmm-yy",
        18 => "h:mm AM/PM", 19 => "h:mm:ss AM/PM", 20 => "h:mm", 21 => "h:mm:ss", 22 => "m/d/yy h:mm",
        37 => "#,##0 ;(#,##0)", 38 => "#,##0 ;[Red](#,##0)", 39 => "#,##0.00;(#,##0.00)",
        40 => "#,##0.00;[Red](#,##0.00)", 45 => "mm:ss", 46 => "[h]:mm:ss", 47 => "mmss.0",
        48 => "##0.0E+0", 49 => "@", _ => $"built-in {id} (locale-dependent)",
    };

    private static (int Row, int Column) ParseAddress(string address)
    {
        var match = AddressPattern.Match(address);
        if (!match.Success) throw new InvalidDataException("Invalid cell address. Use A1 notation without a sheet prefix.");
        int column = 0;
        foreach (var c in match.Groups[1].Value.ToUpperInvariant()) column = column * 26 + c - 'A' + 1;
        var row = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        if (column > 16384 || row > 1048576) throw new InvalidDataException("Cell address exceeds Excel's grid limits.");
        return (row, column);
    }

    private static string Address(int row, int column)
    {
        var letters = "";
        while (column > 0) { column--; letters = (char)('A' + column % 26) + letters; column /= 26; }
        return letters + row.ToString(CultureInfo.InvariantCulture);
    }

    public void Dispose() => _document.Dispose();
    private sealed record SheetDataInfo(string Name, string State, Dictionary<(int Row, int Column), Cell> Cells,
        Dictionary<uint, (string Address, string Formula)> SharedFormulas);
}
