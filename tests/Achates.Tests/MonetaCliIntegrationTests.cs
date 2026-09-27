using System.Security.Cryptography;
using System.Text.Json;
using Achates.Providers.Completions.Content;
using Achates.Server;
using Achates.Server.Tools;
using Microsoft.Data.Sqlite;

namespace Achates.Tests;

/// <summary>Opt-in, real Swift process test. All database contents are invented here.</summary>
public sealed class MonetaCliIntegrationTests
{
    [MonetaCliFact]
    public async Task Approved_tool_queries_real_cli_and_preserves_database()
    {
        var root = Path.Combine(Path.GetTempPath(), $"moneta synthetic {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var database = Path.Combine(root, "test ledger.db");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = database, Pooling = false }.ToString()))
            {
                connection.Open();
                void Execute(string sql)
                {
                    using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
                }
                Execute("CREATE TABLE schema_migration(version INTEGER PRIMARY KEY); INSERT INTO schema_migration VALUES(6);");
                var source = Environment.GetEnvironmentVariable("MONETA_SOURCE_DIR")!;
                foreach (var migration in Directory.GetFiles(Path.Combine(source, "src", "Moneta.Qif", "Convert", "Migrations"), "*.sql").Order())
                    Execute(File.ReadAllText(migration));
                Execute("""
                    INSERT INTO account(id,name,type,raw_type) VALUES(1,'Example Checking','Bank','Bank');
                    INSERT INTO category(id,name,tax_related,is_income,is_expense) VALUES(1,'Example Income',0,1,0);
                    INSERT INTO banking_transaction(id,account_id,date,amount,payee,category_id)
                        VALUES(1,1,'2026-01-01','123.45','Example Employer',1);
                    INSERT INTO budget_setting(key,value) VALUES
                        ('first_year','2026'),('taxable_investment_accounts','[]'),('excluded_categories','[]'),('excluded_accounts','[]');
                    INSERT INTO assumption(id,ordinal,name,description,value) VALUES(1,1,'Monthly Income','Invented income','1000');
                    INSERT INTO budget_section(id,ordinal,name,type) VALUES(1,1,'Income','Income');
                    INSERT INTO budget_row(id,section_id,ordinal,name) VALUES(1,1,1,'Salary');
                    INSERT INTO budget_row_source(id,budget_row_id,category_name) VALUES(1,1,'Example Income');
                    INSERT INTO budget_row_year(id,budget_row_id,year,is_yearly) VALUES(1,1,2026,0);
                    INSERT INTO budget_row_expression(id,budget_row_year_id,ordinal,expression) VALUES(1,1,1,'1000');
                    """);
            }
            var agentFile = Path.Combine(root, "AGENT.md");
            File.WriteAllText(agentFile, "# Synthetic finance agent\n");
            var hash = MonetaAccess.DefinitionHash(agentFile);
            var config = new MonetaConfig
            {
                Executable = Environment.GetEnvironmentVariable("MONETA_READ_EXECUTABLE"), Database = database,
                ApprovedAgents = new() { ["synthetic"] = hash },
            };
            var before = SHA256.HashData(File.ReadAllBytes(database));
            var tool = new MonetaTool("synthetic", agentFile, hash, () => config);
            async Task<JsonElement> Query(string action, params (string, object)[] fields)
            {
                var args = fields.ToDictionary(p => p.Item1, p => (object?)p.Item2);
                args["action"] = action;
                var result = await tool.ExecuteAsync("integration", args);
                return JsonDocument.Parse(((CompletionTextContent)result.Content[0]).Text).RootElement.Clone();
            }
            var budget = await Query("budget", ("year", 2026));
            Assert.Equal("12000", budget.GetProperty("rows")[0].GetProperty("total").GetString());
            var actual = await Query("budget", ("year", 2026), ("kind", "actual"));
            Assert.Equal("123.45", actual.GetProperty("rows")[0].GetProperty("total").GetString());
            foreach (var action in new[] { "info", "accounts", "transactions", "assumptions" })
                Assert.False((await Query(action)).TryGetProperty("error", out _));
            foreach (var action in new[] { "spending", "formulas" })
                Assert.False((await Query(action, ("year", 2026))).TryGetProperty("error", out _));
            var invalid = await Query("budget", ("year", 2099));
            Assert.Equal("missing_budget_year", invalid.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(database)));
        }
        finally { Directory.Delete(root, true); }
    }
}

public sealed class MonetaCliFactAttribute : FactAttribute
{
    public MonetaCliFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MONETA_READ_EXECUTABLE")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MONETA_SOURCE_DIR")))
            Skip = "Set MONETA_READ_EXECUTABLE and MONETA_SOURCE_DIR to run the synthetic cross-project integration test.";
    }
}
