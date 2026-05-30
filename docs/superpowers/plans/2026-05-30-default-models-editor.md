# Default Models Editor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the Apple app read and edit the global default models (`models.base` / `models.thinking`) in `~/.achates/config.yaml`, applying changes live without a server restart.

**Architecture:** Two new WebSocket RPCs (`config.get_models`, `config.set_models`) on `MobileTransport` delegate the write to a `GatewayService.SetDefaultModelsAsync` method that mutates the in-memory `AchatesConfig`, persists via `ConfigLoader.Save`, updates the transport's cached default-model labels, and selectively reloads only the agents that rely on the changed global (computed by a unit-tested `DefaultModelReload` helper). The app adds a `DefaultModelsView` (reusing `ModelBrowseView`) reached from Settings → System.

**Tech Stack:** C# / .NET 10 (server), xUnit (tests), SwiftUI (Apple app), YamlDotNet (config).

---

## File Structure

**Server (create):**
- `src/Achates.Server/DefaultModelReload.cs` — static helper: given each agent's config and which globals changed, returns the set of agent names needing reload.

**Server (modify):**
- `src/Achates.Server/Mobile/MobileTransport.cs` — new `SetDefaultModelsFunc` property, two RPC handlers, two dispatch entries.
- `src/Achates.Server/GatewayService.cs` — `SetDefaultModelsAsync` method; wire it into `SetDefaultModelsFunc` at startup.

**Server tests (create):**
- `tests/Achates.Tests/ConfigLoaderTests.cs` — Models round-trip / clear.
- `tests/Achates.Tests/DefaultModelReloadTests.cs` — selective-reload predicate.

**App (create):**
- `apple/Achates/Views/DefaultModelsView.swift` — the editor screen (auto-included; project uses synchronized folders).

**App (modify):**
- `apple/Achates/Views/ModelBrowseView.swift` — optional `nilMeansNone` flag for correct "None" semantics.
- `apple/Achates/AppState.swift` — `loadDefaultModels()` / `saveDefaultModels(...)`.
- `apple/Achates/Views/SettingsView.swift` — new "Default Models" link in the System section.

**Docs (modify):**
- `CLAUDE.md`, `docs/configuration.md`.

---

## Task 1: `DefaultModelReload` selective-reload helper

**Files:**
- Create: `src/Achates.Server/DefaultModelReload.cs`
- Test: `tests/Achates.Tests/DefaultModelReloadTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Achates.Tests/DefaultModelReloadTests.cs`:

```csharp
using Achates.Server;

namespace Achates.Tests;

public class DefaultModelReloadTests
{
    private static (string, AgentConfig) Agent(string name, string? model = null,
        string? thinkingModel = null, params string[] tools) =>
        (name, new AgentConfig { Model = model, ThinkingModel = thinkingModel, Tools = [.. tools] });

    [Fact]
    public void BaseChange_ReloadsOnlyAgentsWithoutModelOverride()
    {
        var agents = new[]
        {
            Agent("a"),                          // relies on global base -> reload
            Agent("b", model: "x/custom"),       // overrides base -> skip
        };

        var result = DefaultModelReload.AgentsToReload(agents, baseChanged: true, thinkingChanged: false);

        Assert.Contains("a", result);
        Assert.DoesNotContain("b", result);
    }

    [Fact]
    public void ThinkingChange_ReloadsOnlyThinkAgentsWithoutThinkingOverride()
    {
        var agents = new[]
        {
            Agent("noThink"),                                   // no think tool -> skip
            Agent("thinkGlobal", tools: "think"),               // think + relies on global -> reload
            Agent("thinkOwn", thinkingModel: "x/t", tools: "think"), // overrides -> skip
        };

        var result = DefaultModelReload.AgentsToReload(agents, baseChanged: false, thinkingChanged: true);

        Assert.Equal(["thinkGlobal"], result.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void BothChanged_AgentReloadedOnce()
    {
        var agents = new[] { Agent("a", tools: "think") }; // qualifies for both

        var result = DefaultModelReload.AgentsToReload(agents, baseChanged: true, thinkingChanged: true);

        Assert.Single(result);
        Assert.Contains("a", result);
    }

    [Fact]
    public void NothingChanged_Empty()
    {
        var agents = new[] { Agent("a", tools: "think") };

        var result = DefaultModelReload.AgentsToReload(agents, baseChanged: false, thinkingChanged: false);

        Assert.Empty(result);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Achates.Tests --filter DefaultModelReloadTests`
Expected: FAIL — compile error, `DefaultModelReload` does not exist.

- [ ] **Step 3: Write the helper**

Create `src/Achates.Server/DefaultModelReload.cs`:

```csharp
namespace Achates.Server;

/// <summary>
/// Computes which agents must be reloaded when a global default model changes.
/// An agent relies on the global base when it has no per-agent <see cref="AgentConfig.Model"/>;
/// it relies on the global thinking model when it has no <see cref="AgentConfig.ThinkingModel"/>
/// AND has the <c>think</c> tool (only those resolve a thinking model).
/// </summary>
public static class DefaultModelReload
{
    public static HashSet<string> AgentsToReload(
        IEnumerable<(string Name, AgentConfig Config)> agents,
        bool baseChanged,
        bool thinkingChanged)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (!baseChanged && !thinkingChanged)
            return result;

        foreach (var (name, cfg) in agents)
        {
            if (baseChanged && string.IsNullOrWhiteSpace(cfg.Model))
                result.Add(name);

            if (thinkingChanged
                && string.IsNullOrWhiteSpace(cfg.ThinkingModel)
                && cfg.Tools?.Contains("think") == true)
                result.Add(name);
        }

        return result;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Achates.Tests --filter DefaultModelReloadTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Achates.Server/DefaultModelReload.cs tests/Achates.Tests/DefaultModelReloadTests.cs
git commit -m "feat: add DefaultModelReload selective-reload helper"
```

---

## Task 2: `ConfigLoader` Models round-trip test

This locks down the persistence behavior `SetDefaultModelsAsync` relies on (set values survive a save/load, clearing to null drops them). `ConfigLoader.Save` already uses `OmitNull`, so a null field is omitted and re-loads as null.

**Files:**
- Test: `tests/Achates.Tests/ConfigLoaderTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Achates.Tests/ConfigLoaderTests.cs`:

```csharp
using Achates.Server;

namespace Achates.Tests;

public class ConfigLoaderTests
{
    private static string TempConfigPath() =>
        Path.Combine(Path.GetTempPath(), $"achates-cfg-{Guid.NewGuid():N}.yaml");

    [Fact]
    public void Models_RoundTrip_PreservesBaseAndThinking()
    {
        var path = TempConfigPath();
        try
        {
            var config = new AchatesConfig
            {
                Provider = new ProviderConfig { Name = "openrouter" },
                Models = new ModelsConfig { Base = "anthropic/claude-sonnet-4.6", Thinking = "anthropic/claude-opus-4.7" },
            };
            Environment.SetEnvironmentVariable("ACHATES_CONFIG_PATH", path);

            ConfigLoader.Save(config);
            var loaded = ConfigLoader.Load();

            Assert.Equal("anthropic/claude-sonnet-4.6", loaded.Models?.Base);
            Assert.Equal("anthropic/claude-opus-4.7", loaded.Models?.Thinking);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACHATES_CONFIG_PATH", null);
            File.Delete(path);
        }
    }

    [Fact]
    public void Models_NullThinking_OmittedAndLoadsAsNull()
    {
        var path = TempConfigPath();
        try
        {
            var config = new AchatesConfig
            {
                Provider = new ProviderConfig { Name = "openrouter" },
                Models = new ModelsConfig { Base = "anthropic/claude-sonnet-4.6", Thinking = null },
            };
            Environment.SetEnvironmentVariable("ACHATES_CONFIG_PATH", path);

            ConfigLoader.Save(config);
            var text = File.ReadAllText(path);
            var loaded = ConfigLoader.Load();

            Assert.DoesNotContain("thinking", text);
            Assert.Equal("anthropic/claude-sonnet-4.6", loaded.Models?.Base);
            Assert.Null(loaded.Models?.Thinking);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACHATES_CONFIG_PATH", null);
            File.Delete(path);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it passes (already-supported behavior)**

Run: `dotnet test tests/Achates.Tests --filter ConfigLoaderTests`
Expected: PASS (2 tests). `ConfigLoader` already round-trips and omits nulls; this test documents and guards it. If it fails, stop and investigate before continuing.

- [ ] **Step 3: Commit**

```bash
git add tests/Achates.Tests/ConfigLoaderTests.cs
git commit -m "test: guard ConfigLoader models round-trip and null-omit behavior"
```

---

## Task 3: `GatewayService.SetDefaultModelsAsync`

**Files:**
- Modify: `src/Achates.Server/GatewayService.cs` (add method; wire into transport near the existing `_mobileTransport.DefaultModelId` assignment ~line 134)

This method has no direct unit test (it touches the live agents map / file system); it is covered by the `DefaultModelReload` and `ConfigLoader` tests plus manual verification in Task 7. Build is the gate.

- [ ] **Step 1: Add the method**

In `src/Achates.Server/GatewayService.cs`, add this public method (place it near `ReloadAgentAsync`):

```csharp
/// <summary>
/// Applies a change to the global default models. Each field is updated only when
/// its <c>*Set</c> flag is true; an empty/whitespace value clears the default to null.
/// Persists the config, refreshes the transport's cached default labels, and reloads
/// only the agents that rely on a changed global (see <see cref="DefaultModelReload"/>).
/// </summary>
public async Task SetDefaultModelsAsync(
    string? baseModel, bool baseSet,
    string? thinkingModel, bool thinkingSet,
    CancellationToken ct)
{
    config.Models ??= new ModelsConfig();

    var baseChanged = false;
    if (baseSet)
    {
        var v = string.IsNullOrWhiteSpace(baseModel) ? null : baseModel;
        baseChanged = config.Models.Base != v;
        config.Models.Base = v;
    }

    var thinkingChanged = false;
    if (thinkingSet)
    {
        var v = string.IsNullOrWhiteSpace(thinkingModel) ? null : thinkingModel;
        thinkingChanged = config.Models.Thinking != v;
        config.Models.Thinking = v;
    }

    if (!baseChanged && !thinkingChanged)
        return;

    ConfigLoader.Save(config);

    if (_mobileTransport is not null)
    {
        _mobileTransport.DefaultModelId = config.Models.Base;
        _mobileTransport.DefaultThinkingModelId = config.Models.Thinking;
    }

    var agentConfigs = AgentLoader.LoadAgents(_achatesHome!);
    var toReload = DefaultModelReload.AgentsToReload(
        agentConfigs.Select(kv => (kv.Key, kv.Value)),
        baseChanged, thinkingChanged);

    foreach (var name in toReload)
    {
        try
        {
            await ReloadAgentAsync(name, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Default-models change: reload of agent '{Name}' failed", name);
        }
    }

    logger.LogInformation(
        "Default models updated (base={Base}, thinking={Thinking}); reloaded {Count} agent(s)",
        config.Models.Base ?? "<none>", config.Models.Thinking ?? "<none>", toReload.Count);
}
```

- [ ] **Step 2: Wire it into the transport at startup**

In `StartAsync`, immediately after the existing lines:

```csharp
        _mobileTransport.DefaultModelId = config.Models?.Base;
        _mobileTransport.DefaultThinkingModelId = config.Models?.Thinking;
```

add:

```csharp
        _mobileTransport.SetDefaultModelsFunc = SetDefaultModelsAsync;
```

(Note: `_mobileTransport.SetDefaultModelsFunc` is added to `MobileTransport` in Task 4. If implementing in this order, the build will fail until Task 4 is done — that is expected; the build-gate step is at the end of Task 4.)

- [ ] **Step 3: Commit**

```bash
git add src/Achates.Server/GatewayService.cs
git commit -m "feat: add GatewayService.SetDefaultModelsAsync with selective reload"
```

---

## Task 4: `MobileTransport` RPCs (`config.get_models`, `config.set_models`)

**Files:**
- Modify: `src/Achates.Server/Mobile/MobileTransport.cs`

- [ ] **Step 1: Add the `SetDefaultModelsFunc` property**

Near the existing delegate properties (just after `DefaultThinkingModelId` ~line 103), add:

```csharp
    /// <summary>
    /// Applies a change to the global default models. Args:
    /// (baseModel, baseSet, thinkingModel, thinkingSet, ct). A <c>*Set</c> flag of
    /// false means "leave that field unchanged"; an empty value with the flag true
    /// clears the default. Wired by <c>GatewayService</c>.
    /// </summary>
    public Func<string?, bool, string?, bool, CancellationToken, Task>? SetDefaultModelsFunc { get; set; }
```

- [ ] **Step 2: Add the two dispatch entries**

In `DispatchRequestAsync`'s `switch`, next to `"models.list"`:

```csharp
                "config.get_models" => HandleConfigGetModels(request),
                "config.set_models" => await HandleConfigSetModelsAsync(request, ct),
```

- [ ] **Step 3: Add the two handlers**

Add near `HandleModelsListAsync`:

```csharp
    private ResponseFrame HandleConfigGetModels(RequestFrame request)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            @base = DefaultModelId,
            thinking = DefaultThinkingModelId,
        }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }

    private async Task<ResponseFrame> HandleConfigSetModelsAsync(RequestFrame request, CancellationToken ct)
    {
        var p = request.Params;

        string? baseModel = null;
        var baseSet = false;
        string? thinkingModel = null;
        var thinkingSet = false;

        if (p.ValueKind == JsonValueKind.Object)
        {
            if (p.TryGetProperty("base", out var baseEl) &&
                baseEl.ValueKind is JsonValueKind.String or JsonValueKind.Null)
            {
                baseSet = true;
                baseModel = baseEl.ValueKind == JsonValueKind.String ? baseEl.GetString() : null;
            }

            if (p.TryGetProperty("thinking", out var thinkingEl) &&
                thinkingEl.ValueKind is JsonValueKind.String or JsonValueKind.Null)
            {
                thinkingSet = true;
                thinkingModel = thinkingEl.ValueKind == JsonValueKind.String ? thinkingEl.GetString() : null;
            }
        }

        if (SetDefaultModelsFunc is not null && (baseSet || thinkingSet))
            await SetDefaultModelsFunc(baseModel, baseSet, thinkingModel, thinkingSet, ct);

        var payload = JsonSerializer.SerializeToElement(new { ok = true }, JsonOptions);
        return ResponseFrame.Success(request.Id, payload);
    }
```

- [ ] **Step 4: Build the whole solution**

Run: `dotnet build Achates.slnx`
Expected: Build succeeds (this is the gate for Tasks 3 + 4 together).

- [ ] **Step 5: Run the full server test suite**

Run: `dotnet test Achates.slnx`
Expected: PASS — all tests including the new `DefaultModelReloadTests` and `ConfigLoaderTests`.

- [ ] **Step 6: Commit**

```bash
git add src/Achates.Server/Mobile/MobileTransport.cs
git commit -m "feat: add config.get_models and config.set_models RPCs"
```

---

## Task 5: `ModelBrowseView` — `nilMeansNone` flag

The shared picker hardcodes a "Default" row whose footer says "Falls back to models.base". On the default-models screen there is no higher-level fallback, so nil must read as "None". Add an opt-in flag; existing call sites keep current behavior via the default value.

**Files:**
- Modify: `apple/Achates/Views/ModelBrowseView.swift`

- [ ] **Step 1: Add the property**

After `let defaultModel: String?` add:

```swift
    /// When true, a nil selection means "no model at all" (used by the global
    /// default-models editor) rather than "fall back to a higher-level default".
    var nilMeansNone: Bool = false
```

- [ ] **Step 2: Update the "Default" row label + subtitle**

Replace the first `Section { Button { ... } label: { ... } } footer: { ... }` block's label text and footer with the conditional variants. Specifically, the title `Text("Default")` becomes:

```swift
                                    Text(nilMeansNone ? "None" : "Default")
                                        .foregroundStyle(.primary)
```

the `else` branch subtitle (`Text("No default configured")`) becomes:

```swift
                                        Text(nilMeansNone ? "No model — agents must set their own" : "No default configured")
                                            .font(.caption)
                                            .foregroundStyle(.secondary)
```

and the footer `Text("Falls back to models.base in ~/.achates/config.yaml.")` becomes:

```swift
                        Text(nilMeansNone
                            ? "Agents without their own model setting will have no model."
                            : "Falls back to models.base in ~/.achates/config.yaml.")
```

- [ ] **Step 3: Build to verify the existing call sites still compile**

Run:
```bash
xcodebuild -project apple/Achates.xcodeproj -scheme Achates -destination 'platform=iOS Simulator,name=iPhone 16' build
```
Expected: BUILD SUCCEEDED (AgentEditView's two `ModelBrowseView(...)` call sites omit `nilMeansNone` and default to false — behavior unchanged).

- [ ] **Step 4: Commit**

```bash
git add apple/Achates/Views/ModelBrowseView.swift
git commit -m "feat: add nilMeansNone flag to ModelBrowseView"
```

---

## Task 6: App — AppState methods, `DefaultModelsView`, Settings link

**Files:**
- Modify: `apple/Achates/AppState.swift`
- Create: `apple/Achates/Views/DefaultModelsView.swift`
- Modify: `apple/Achates/Views/SettingsView.swift`

- [ ] **Step 1: Add AppState methods**

In `apple/Achates/AppState.swift`, after `loadAvailableModels()`:

```swift
    func loadDefaultModels() async throws -> (base: String?, thinking: String?) {
        guard let payload = try await client?.sendRequest(method: "config.get_models") else {
            throw AgentEditError.notConnected
        }
        return (payload["base"]?.stringValue, payload["thinking"]?.stringValue)
    }

    func saveDefaultModels(base: String?, thinking: String?) async throws {
        guard let client else { throw AgentEditError.notConnected }
        _ = try await client.sendRequest(method: "config.set_models", params: [
            "base": .string(base ?? ""),
            "thinking": .string(thinking ?? ""),
        ])
    }
```

- [ ] **Step 2: Create `DefaultModelsView`**

Create `apple/Achates/Views/DefaultModelsView.swift`:

```swift
import SwiftUI

/// Settings → System → Default Models. Edits the global `models.base` /
/// `models.thinking` in ~/.achates/config.yaml. A nil value means "no
/// server-wide default" and renders as "None".
struct DefaultModelsView: View {
    @Environment(AppState.self) private var appState

    @State private var base: String?
    @State private var thinking: String?
    @State private var originalBase: String?
    @State private var originalThinking: String?
    @State private var isLoading = true
    @State private var isSaving = false
    @State private var errorMessage: String?
    @State private var showError = false

    private var isDirty: Bool { base != originalBase || thinking != originalThinking }

    var body: some View {
        Group {
            if isLoading {
                ProgressView()
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                Form {
                    Section {
                        NavigationLink {
                            ModelBrowseView(
                                selectedModel: $base,
                                title: "Default Model",
                                defaultModel: nil,
                                nilMeansNone: true)
                        } label: {
                            row(label: "Default Model", value: base)
                        }

                        NavigationLink {
                            ModelBrowseView(
                                selectedModel: $thinking,
                                title: "Default Thinking Model",
                                defaultModel: nil,
                                nilMeansNone: true)
                        } label: {
                            row(label: "Default Thinking Model", value: thinking)
                        }
                    } footer: {
                        Text("Defaults for agents that don't set their own model. Stored in ~/.achates/config.yaml.")
                    }
                }
            }
        }
        .navigationTitle("Default Models")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .toolbar {
            ToolbarItem(placement: .confirmationAction) {
                Button {
                    Task { await save() }
                } label: {
                    if isSaving {
                        ProgressView().controlSize(.small)
                    } else {
                        Text("Save")
                    }
                }
                .disabled(!isDirty || isSaving)
            }
        }
        .alert("Error", isPresented: $showError) {
            Button("OK") {}
        } message: {
            Text(errorMessage ?? "Unknown error")
        }
        .task { await load() }
    }

    @ViewBuilder
    private func row(label: String, value: String?) -> some View {
        HStack {
            Text(label)
            Spacer()
            Text(value.map(shortModelName) ?? "None")
                .foregroundStyle(.secondary)
        }
    }

    private func load() async {
        isLoading = true
        do {
            let loaded = try await appState.loadDefaultModels()
            base = loaded.base
            thinking = loaded.thinking
            originalBase = loaded.base
            originalThinking = loaded.thinking
        } catch {
            errorMessage = error.localizedDescription
            showError = true
        }
        isLoading = false
    }

    private func save() async {
        isSaving = true
        do {
            try await appState.saveDefaultModels(base: base, thinking: thinking)
            originalBase = base
            originalThinking = thinking
        } catch {
            errorMessage = error.localizedDescription
            showError = true
        }
        isSaving = false
    }
}
```

- [ ] **Step 3: Add the Settings link**

In `apple/Achates/Views/SettingsView.swift`, inside the `Section("System")` block, after the Scheduled Jobs `NavigationLink`, add:

```swift
                    NavigationLink {
                        DefaultModelsView()
                    } label: {
                        Label("Default Models", systemImage: "cpu")
                    }
```

- [ ] **Step 4: Build the app**

Run:
```bash
xcodebuild -project apple/Achates.xcodeproj -scheme Achates -destination 'platform=iOS Simulator,name=iPhone 16' build
```
Expected: BUILD SUCCEEDED.

- [ ] **Step 5: Commit**

```bash
git add apple/Achates/AppState.swift apple/Achates/Views/DefaultModelsView.swift apple/Achates/Views/SettingsView.swift
git commit -m "feat: add Default Models editor to app Settings"
```

---

## Task 7: Manual verification + docs

**Files:**
- Modify: `CLAUDE.md`, `docs/configuration.md`

- [ ] **Step 1: Manual end-to-end check**

1. Start the server: `dotnet run --project src/Achates.Server`
2. In the app: Settings → System → Default Models. Confirm current `models.base` / `models.thinking` display (or "None" if unset).
3. Change the Default Model to a different model, tap Save.
4. Confirm `~/.achates/config.yaml` now shows the new `models: base:` value.
5. Confirm an agent **without** a `**Model:**` override uses the new model on its next turn (check the server log line `Agent '<name>' reloaded with model <id>` and/or a new turn's cost ledger entry), and that an agent **with** an override is unaffected (no reload log line for it).

- [ ] **Step 2: Update `CLAUDE.md`**

In the RPC methods list (the `MobileTransport` bullet beginning "RPC methods:"), add `config.get_models`, `config.set_models` to the enumeration. In the `MobileTransport` / settings description, note: "Default models (`models.base` / `models.thinking`) are editable from the app under Settings → System → Default Models (`config.get_models` / `config.set_models` RPCs); changes persist to `config.yaml` and live-reload affected agents."

- [ ] **Step 3: Update `docs/configuration.md`**

Where `models.base` / `models.thinking` are documented, add a note: "These defaults can also be edited from the Apple app (Settings → System → Default Models) without restarting the server — the change is written back to `config.yaml` and agents that don't override the model are reloaded automatically."

- [ ] **Step 4: Commit**

```bash
git add CLAUDE.md docs/configuration.md
git commit -m "docs: document Default Models editor and config.*_models RPCs"
```

---

## Self-Review Notes

- **Spec coverage:** RPC pair (Tasks 4), GatewayService delegate + persist + transport-label update + selective reload (Task 3, helper Task 1), nil/"None" handling (Task 5 + DefaultModelsView Task 6), Settings entry (Task 6), AppState methods (Task 6), docs (Task 7), tests (Tasks 1–2). All spec sections mapped.
- **Cross-task type consistency:** `SetDefaultModelsFunc` signature `(string?, bool, string?, bool, CancellationToken)` matches `SetDefaultModelsAsync(baseModel, baseSet, thinkingModel, thinkingSet, ct)` and the handler's call. `DefaultModelReload.AgentsToReload(IEnumerable<(string,AgentConfig)>, bool, bool)` matches its caller in `SetDefaultModelsAsync`. `loadDefaultModels`/`saveDefaultModels` names match `DefaultModelsView` usage. `nilMeansNone` default `false` preserves existing `ModelBrowseView` call sites.
- **Build ordering:** Tasks 3 and 4 are mutually dependent at compile time (the wiring line references the property added in Task 4). The build/test gate lives at the end of Task 4; the commit in Task 3 is allowed to be a non-building intermediate. If the executor prefers a single building commit, do Task 4 Steps 1–3 before Task 3 Step 2.
```
