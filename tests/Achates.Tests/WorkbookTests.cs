using System.IO.Compression;
using System.Text.Json;
using Achates.Agent;
using Achates.Agent.Messages;
using Achates.Providers.Completions.Content;
using Achates.Providers.Completions.Messages;
using Achates.Server.Mobile;
using Achates.Server.Tools;
using Achates.Server.Workbooks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Achates.Tests;

public sealed class WorkbookTests
{
    // Synthetic data only. Explicit cached results ensure the tests detect accidental recalculation.
    internal static byte[] CreateWorkbook(bool date1904 = false)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new Workbook(new WorkbookProperties { Date1904 = date1904 });
            var strings = workbook.AddNewPart<SharedStringTablePart>();
            strings.SharedStringTable = new SharedStringTable(new SharedStringItem(new Text("Item")));
            var styles = workbook.AddNewPart<WorkbookStylesPart>();
            styles.Stylesheet = new Stylesheet(
                new NumberingFormats(new NumberingFormat { NumberFormatId = 164, FormatCode = "$#,##0.00" }),
                new CellFormats(new CellFormat { NumberFormatId = 0 }, new CellFormat { NumberFormatId = 164 },
                    new CellFormat { NumberFormatId = 14 }));
            var sheet = workbook.AddNewPart<WorksheetPart>();
            sheet.Worksheet = new Worksheet(new SheetData(
                new Row(
                    new Cell { CellReference = "A1", DataType = CellValues.SharedString, CellValue = new CellValue("0") },
                    new Cell { CellReference = "B1", DataType = CellValues.InlineString, InlineString = new InlineString(new Text("Amount")) }),
                new Row(
                    new Cell { CellReference = "A2", DataType = CellValues.InlineString, InlineString = new InlineString(new Text("Sample")) },
                    new Cell { CellReference = "B2", StyleIndex = 1, CellValue = new CellValue("12.5") },
                    new Cell { CellReference = "C2", CellFormula = new CellFormula("B2*2"), CellValue = new CellValue("99") },
                    new Cell { CellReference = "D2", CellFormula = new CellFormula("B2*3") },
                    new Cell { CellReference = "E2", StyleIndex = 2, CellValue = new CellValue("45000") },
                    new Cell { CellReference = "F2", DataType = CellValues.Boolean, CellValue = new CellValue("1") },
                    new Cell { CellReference = "G2", DataType = CellValues.Error, CellValue = new CellValue("#DIV/0!") }),
                new Row(
                    new Cell { CellReference = "B3", CellFormula = new CellFormula("B2+1") { FormulaType = CellFormulaValues.Shared, SharedIndex = 0, Reference = "B3:B4" }, CellValue = new CellValue("13.5") }),
                new Row(
                    new Cell { CellReference = "B4", CellFormula = new CellFormula { FormulaType = CellFormulaValues.Shared, SharedIndex = 0 }, CellValue = new CellValue("14.5") }),
                new Row(new Cell { CellReference = "Z100", DataType = CellValues.InlineString, InlineString = new InlineString(new Text("Distant data")) })));
            var empty = workbook.AddNewPart<WorksheetPart>();
            empty.Worksheet = new Worksheet(new SheetData());
            workbook.Workbook.Append(new Sheets(
                new Sheet { Id = workbook.GetIdOfPart(sheet), SheetId = 1, Name = "Sales" },
                new Sheet { Id = workbook.GetIdOfPart(empty), SheetId = 2, Name = "Empty", State = SheetStateValues.Hidden }));
        }
        return stream.ToArray();
    }

    internal static CompletionWorkbookContent Attachment(byte[] bytes)
    {
        var parameters = JsonSerializer.SerializeToElement(new
        {
            attachments = new[] { new { mime = CompletionWorkbookContent.ExcelMime, data = Convert.ToBase64String(bytes), filename = "sample.xlsx" } },
        });
        var parsed = AttachmentParser.Parse(parameters, out var error);
        Assert.Null(error);
        return Assert.IsType<CompletionWorkbookContent>(Assert.Single(parsed!));
    }

    [Fact]
    public void Upload_preserves_original_and_only_preview_enters_model_context()
    {
        var bytes = CreateWorkbook();
        var attachment = Attachment(bytes);
        Assert.Equal(bytes, Convert.FromBase64String(attachment.Data));
        var user = new UserMessage { Text = "Review", Content = [attachment] };
        var converted = Assert.IsType<CompletionUserContentMessage>(Assert.Single(MessageConversion.DefaultConvertToLlm([user])));
        Assert.All(converted.Content, part => Assert.IsType<CompletionTextContent>(part));
        Assert.Contains("Sales", ((CompletionTextContent)converted.Content[1]).Text);
        Assert.DoesNotContain(attachment.Data, JsonSerializer.Serialize(converted));
        Assert.True(SessionCompactor.EstimateMessageTokens(user) > attachment.Preview.Length / 4 - 2);
        CompletionUserContent original = attachment;
        var restored = Assert.IsType<CompletionWorkbookContent>(JsonSerializer.Deserialize<CompletionUserContent>(JsonSerializer.Serialize(original)));
        Assert.Equal(attachment, restored);
        using var preview = JsonDocument.Parse(attachment.Preview);
        var sheets = preview.RootElement.GetProperty("sheets");
        Assert.Equal(100, sheets[0].GetProperty("rows").GetInt32());
        Assert.Equal(26, sheets[0].GetProperty("columns").GetInt32());
        Assert.Equal(6, sheets[0].GetProperty("preview").GetArrayLength());
        Assert.True(sheets[0].GetProperty("preview_truncated").GetBoolean());
        Assert.Equal(0, sheets[1].GetProperty("rows").GetInt32());
    }

    [Theory]
    [InlineData(false, "1900")]
    [InlineData(true, "1904")]
    public void Reader_preserves_values_formulas_cached_results_formats_and_date_system(bool date1904, string system)
    {
        using var reader = new WorkbookReader(CreateWorkbook(date1904));
        using var result = JsonDocument.Parse(reader.Read("Sales", "A1:G4"));
        Assert.Equal(system, result.RootElement.GetProperty("date_system").GetString());
        var cells = result.RootElement.GetProperty("cells").EnumerateArray().ToDictionary(c => c.GetProperty("address").GetString()!);
        Assert.Equal("Item", cells["A1"].GetProperty("value").GetString());
        Assert.Equal("Amount", cells["B1"].GetProperty("value").GetString());
        Assert.Equal("$#,##0.00", cells["B2"].GetProperty("number_format").GetString());
        Assert.Equal("B2*2", cells["C2"].GetProperty("formula").GetString());
        Assert.Equal("99", cells["C2"].GetProperty("value").GetString());
        Assert.Equal("missing", cells["D2"].GetProperty("cached_result").GetString());
        Assert.Equal("45000", cells["E2"].GetProperty("value").GetString());
        Assert.Equal("mm-dd-yy", cells["E2"].GetProperty("number_format").GetString());
        Assert.Equal("#DIV/0!", cells["G2"].GetProperty("value").GetString());
        Assert.Equal("B3", cells["B4"].GetProperty("shared_formula_anchor").GetString());
        Assert.Equal("B2+1", cells["B4"].GetProperty("shared_formula").GetString());
        Assert.DoesNotContain("Z100", cells.Keys);
        Assert.Contains("Distant data", reader.Read("Sales", "Z100"));
    }

    [Theory]
    [InlineData("A1:XFD1048576")]
    [InlineData("A1:A501")]
    [InlineData("A0")]
    [InlineData("XFE1")]
    [InlineData("A1048577")]
    [InlineData("B2:A1")]
    [InlineData("Sales!A1")]
    public void Invalid_or_excessive_ranges_fail_clearly(string range)
    {
        using var reader = new WorkbookReader(CreateWorkbook());
        Assert.Throws<InvalidDataException>(() => reader.Read("Sales", range));
    }

    [Fact]
    public void Invalid_and_expansion_heavy_uploads_are_rejected()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var part = zip.CreateEntry("large.xml").Open()) part.Write(new byte[17 * 1024 * 1024]);
        foreach (var bytes in new[] { new byte[] { 1, 2, 3 }, stream.ToArray(), new byte[WorkbookReader.MaxBytes + 1] })
        {
            var parameters = JsonSerializer.SerializeToElement(new { attachments = new[] {
                new { mime = CompletionWorkbookContent.ExcelMime, data = Convert.ToBase64String(bytes) } } });
            Assert.Null(AttachmentParser.Parse(parameters, out var error));
            Assert.NotNull(error);
        }
    }

    [Fact]
    public async Task Originals_survive_reload_and_compaction_are_session_scoped_and_deleted_with_session()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var sessions = new MobileSessionStore(directory.FullName);
            var store = new WorkbookStore(sessions.GetWorkbookDirectory("agent", "session"));
            var bytes = CreateWorkbook();
            var attachment = Attachment(bytes);
            await store.SaveAsync([attachment], CancellationToken.None);
            await store.SaveAsync([attachment], CancellationToken.None); // safe retry
            await sessions.SaveAsync("agent", new MobileSession { Id = "session", Messages = [new UserMessage { Text = "Review", Content = [attachment] }] });
            var restored = (await sessions.LoadAsync("agent", "session"))!;
            Assert.Equal(attachment, Assert.Single(Assert.IsType<UserMessage>(Assert.Single(restored.Messages)).Content!));
            await sessions.SaveAsync("agent", MobileSession.WithMessages(restored, "session", [new SummaryMessage { Summary = "Earlier conversation" }]));
            var reopened = new WorkbookStore(sessions.GetWorkbookDirectory("agent", "session"));
            Assert.Single(await reopened.ListAsync(CancellationToken.None));
            Assert.Equal(bytes, await reopened.ReadAsync(attachment.WorkbookId, CancellationToken.None));
            var tool = new WorkbookTool(reopened);
            var result = await tool.ExecuteAsync("read", new() { ["action"] = "read", ["workbook_id"] = attachment.WorkbookId, ["sheet"] = "Sales", ["range"] = "B2" });
            Assert.Contains("12.5", Assert.IsType<CompletionTextContent>(Assert.Single(result.Content)).Text);
            var other = new WorkbookTool(new WorkbookStore(sessions.GetWorkbookDirectory("agent", "other")));
            result = await other.ExecuteAsync("read", new() { ["action"] = "describe", ["workbook_id"] = attachment.WorkbookId });
            Assert.Contains("not found", Assert.IsType<CompletionTextContent>(Assert.Single(result.Content)).Text);
            await Assert.ThrowsAsync<InvalidDataException>(() => reopened.ReadAsync("../escape", CancellationToken.None));
            await sessions.DeleteAsync("agent", "session");
            Assert.Empty(await reopened.ListAsync(CancellationToken.None));
        }
        finally { directory.Delete(true); }
    }
}
