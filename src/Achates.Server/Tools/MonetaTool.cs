using System.Text;
using System.Text.Json;
using Achates.Agent.Tools;
using Achates.Providers.Completions.Content;
using static Achates.Providers.Util.JsonSchemaHelpers;

namespace Achates.Server.Tools;

internal sealed class MonetaTool(
    string agentId, string agentFile, string loadedDefinitionHash,
    Func<MonetaConfig?>? readConfig = null,
    Func<string, string, string, CancellationToken, Task<string>>? run = null,
    ILogger? logger = null) : AgentTool
{
    private static readonly SemaphoreSlim Slots = new(2);
    private static readonly HashSet<string> Fields =
        ["action", "year", "month", "kind", "account_id", "start", "end", "payee", "category", "group_by", "limit", "offset"];
    private static readonly HashSet<string> Actions =
        ["info", "accounts", "transactions", "spending", "budget", "assumptions", "formulas"];
    private static readonly JsonElement Schema = ObjectSchema(new Dictionary<string, JsonElement>
    {
        ["action"] = StringEnum(["info", "accounts", "transactions", "spending", "budget", "assumptions", "formulas"],
            "info lists budget years. accounts gives ledger balances (investment cash only). transactions searches recorded transactions. spending groups Moneta actuals, including income. budget returns the app's year grid. assumptions and formulas explain definitions."),
        ["year"] = NumberSchema("Required for spending, budget, formulas; integer 1–9999."),
        ["month"] = NumberSchema("spending only: integer 1–12; omit for the whole year."),
        ["kind"] = StringEnum(["budget", "actual", "difference"], "budget only: grid kind; default budget."),
        ["account_id"] = NumberSchema("transactions only: account id from accounts."),
        ["start"] = StringSchema("transactions only: inclusive recorded start date YYYY-MM-DD."),
        ["end"] = StringSchema("transactions only: inclusive recorded end date YYYY-MM-DD."),
        ["payee"] = StringSchema("transactions only: case-insensitive substring, up to 200 characters."),
        ["category"] = StringSchema("transactions only: category substring, including split categories; up to 200 characters."),
        ["group_by"] = StringEnum(["category", "payee"], "spending only; default category. Income and expenses may coexist."),
        ["limit"] = NumberSchema("Maximum rows, integer 1–200; default 100. All actions except info."),
        ["offset"] = NumberSchema("Pagination offset, integer 0–1000000; default 0. Follow next_offset until null."),
    }, required: ["action"]);

    public override string Name => "moneta";
    public override string Label => "Moneta Finances";
    public override string Description =>
        "Read approved Moneta financial data. Amounts are exact decimal strings; null is unavailable. " +
        "Use the built-in budget/actual/difference grids for calculations. Results are private financial data. " +
        "No writes, SQL, database paths, or authorization changes are available.";
    public override JsonElement Parameters => Schema;

    internal bool IsApproved(MonetaConfig? config) =>
        MonetaAccess.IsApproved(config, agentId, agentFile, loadedDefinitionHash);

    public override async Task<AgentToolResult> ExecuteAsync(string toolCallId,
        Dictionary<string, object?> arguments, CancellationToken cancellationToken = default,
        Func<AgentToolResult, Task>? onProgress = null)
    {
        string? action = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(35));
        var acquired = false;
        try
        {
            await Slots.WaitAsync(deadline.Token);
            acquired = true;
            var config = (readConfig ?? MonetaAccess.LoadConfig)();
            if (!MonetaAccess.IsApproved(config, agentId, agentFile, loadedDefinitionHash))
                return Result("access_denied", "Moneta access requires owner approval of this agent's current definition.");
            if (!Path.IsPathFullyQualified(config!.Executable ?? "") || !Path.IsPathFullyQualified(config.Database ?? ""))
                return Result("not_configured", "The owner must configure absolute Moneta executable and database paths.");
            var request = JsonSerializer.Serialize(arguments);
            if (Encoding.UTF8.GetByteCount(request) > 16_384 || arguments.Keys.Any(key => !Fields.Contains(key)))
                return Result("invalid_request", "Unknown parameters or request exceeds 16 KiB.");
            using var doc = JsonDocument.Parse(request);
            if (!doc.RootElement.TryGetProperty("action", out var value) || value.ValueKind != JsonValueKind.String ||
                !Actions.Contains(action = value.GetString()!))
                return Result("invalid_request", "A supported action is required.");
            var response = run is null
                ? await MonetaProcess.RunAsync(config.Executable!, config.Database!, request, deadline.Token)
                : await run(config.Executable!, config.Database!, request, deadline.Token);
            // Revocation or definition edits during a query also suppress the result.
            var current = (readConfig ?? MonetaAccess.LoadConfig)();
            if (!MonetaAccess.IsApproved(current, agentId, agentFile, loadedDefinitionHash) ||
                current!.Database != config.Database || current.Executable != config.Executable)
                return Result("access_denied", "Moneta authorization changed during the request; result withheld.");
            logger?.LogInformation("Moneta read by {Agent}: {Action} completed", agentId, action);
            return new AgentToolResult { Content = [new CompletionTextContent { Text = response }] };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Result("query_timeout", "Moneta query timed out or exceeded output limits."); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Database errors, stderr, filters, file paths and financial results never enter server logs.
            logger?.LogWarning("Moneta read by {Agent}: request failed ({FailureType})", agentId, ex.GetType().Name);
            return Result("query_failed", "Moneta query failed. The owner should check approval, CLI installation, and database configuration.");
        }
        finally { if (acquired) Slots.Release(); }
    }

    private AgentToolResult Result(string code, string message)
    {
        logger?.LogInformation("Moneta read by {Agent}: {Outcome}", agentId, code);
        return new AgentToolResult { Content = [new CompletionTextContent
        {
            Text = JsonSerializer.Serialize(new { protocol_version = 1, error = new { code, message } }),
        }] };
    }
}
