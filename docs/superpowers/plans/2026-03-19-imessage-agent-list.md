# iMessage-style Agent List Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Redesign the iOS agent list screen to look like iMessage's conversation list — each agent row shows a last message preview and timestamp, with a large title nav bar.

**Architecture:** Add `Description` to `AgentDefinition`, enrich the `agents.list` RPC response with `last_message` and `last_activity` from the latest session, then redesign the iOS `AgentListView` to display the new data in an iMessage-style layout.

**Tech Stack:** .NET 10 (server), Swift/SwiftUI (iOS)

**Spec:** `docs/superpowers/specs/2026-03-19-imessage-agent-list-design.md`

---

### Task 1: Add Description to AgentDefinition

**Files:**
- Modify: `src/Achates.Server/AgentDefinition.cs:13-24`
- Modify: `src/Achates.Server/GatewayService.cs:102-113`

- [ ] **Step 1: Add Description property to AgentDefinition**

In `src/Achates.Server/AgentDefinition.cs`, add:

```csharp
public string? Description { get; init; }
```

after the `MemoryPath` property (line 19).

- [ ] **Step 2: Set Description when creating AgentDefinition in GatewayService**

In `src/Achates.Server/GatewayService.cs`, at the `new AgentDefinition` block (~line 102), add:

```csharp
Description = agentConfig.Description,
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build Achates.slnx`
Expected: Build succeeds with no errors.

- [ ] **Step 4: Commit**

```bash
git add src/Achates.Server/AgentDefinition.cs src/Achates.Server/GatewayService.cs
git commit -m "Add Description to AgentDefinition"
```

---

### Task 2: Enrich agents.list RPC with last message preview

**Files:**
- Modify: `src/Achates.Server/Mobile/MobileTransport.cs:148-152,203-214`

The current `HandleAgentsList` is synchronous and returns only name, model, tools. We need to:
1. Make it async
2. Add `description` from the new `AgentDefinition.Description`
3. Look up latest session per agent via `sessionStore.GetLatestSessionAsync`
4. Extract the last message text and timestamp

- [ ] **Step 1: Change HandleAgentsList to async**

In `src/Achates.Server/Mobile/MobileTransport.cs`, update the dispatch at line 152:

```csharp
"agents.list" => await HandleAgentsListAsync(request, ct),
```

- [ ] **Step 2: Replace HandleAgentsList with HandleAgentsListAsync**

Replace the `HandleAgentsList` method (lines 203-214) with:

```csharp
private async Task<ResponseFrame> HandleAgentsListAsync(RequestFrame request, CancellationToken ct)
{
    var agentList = new List<object>();

    foreach (var (name, def) in agents)
    {
        var (lastMessage, lastActivity) = await GetLastMessagePreviewAsync(name, ct);

        agentList.Add(new
        {
            name,
            description = def.Description ?? "",
            model = def.Model.Id,
            tools = def.Tools.Select(t => t.Name).ToArray(),
            last_message = lastMessage,
            last_activity = lastActivity,
        });
    }

    var payload = JsonSerializer.SerializeToElement(new { agents = agentList }, JsonOptions);
    return ResponseFrame.Success(request.Id, payload);
}

private async Task<(string? message, string? activity)> GetLastMessagePreviewAsync(
    string agentName, CancellationToken ct)
{
    var session = await sessionStore.GetLatestSessionAsync(agentName, ct);
    if (session is null) return (null, null);

    // Walk messages in reverse to find the last user or assistant message
    for (var i = session.Messages.Count - 1; i >= 0; i--)
    {
        var msg = session.Messages[i];
        string? text = null;

        switch (msg)
        {
            case UserMessage { Hidden: false } user:
                text = user.Text;
                break;

            case AssistantMessage assistant:
                text = string.Join(" ", assistant.Content
                    .OfType<CompletionTextContent>()
                    .Select(c => c.Text));
                break;

            default:
                continue;
        }

        if (string.IsNullOrWhiteSpace(text))
            continue;

        // Clean up: replace newlines with spaces, collapse whitespace
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

        // Truncate at word boundary
        if (text.Length > 100)
        {
            var truncated = text[..100];
            var lastSpace = truncated.LastIndexOf(' ');
            text = (lastSpace > 50 ? truncated[..lastSpace] : truncated) + "...";
        }

        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(msg.Timestamp)
            .ToString("o");

        return (text, timestamp);
    }

    return (null, null);
}
```

- [ ] **Step 3: Add using for CompletionTextContent**

Add `using Achates.Providers.Completions.Content;` to the top of `MobileTransport.cs`. The existing `using Achates.Providers.Completions;` does not cover the `Content` sub-namespace where `CompletionTextContent` lives.

- [ ] **Step 4: Build and verify**

Run: `dotnet build Achates.slnx`
Expected: Build succeeds.

- [ ] **Step 5: Manual test**

Start the server (`dotnet run --project src/Achates.Server`), connect via WebSocket, send `agents.list` request. Verify response includes `last_message`, `last_activity`, and `description` fields.

- [ ] **Step 6: Commit**

```bash
git add src/Achates.Server/Mobile/MobileTransport.cs
git commit -m "Enrich agents.list with last message preview and description"
```

---

### Task 3: Update iOS Agent model

**Files:**
- Modify: `ios/Achates/Models/Agent.swift`

- [ ] **Step 1: Add lastMessage and lastActivity properties**

In `ios/Achates/Models/Agent.swift`, update the `Agent` struct:

```swift
struct Agent: Identifiable, Sendable, Equatable {
    let id: String
    let name: String
    let description: String
    let tools: [String]
    let lastMessage: String?
    let lastActivity: Date?

    var initials: String {
        let parts = name.split(separator: " ")
        if parts.count >= 2 {
            return String(parts[0].prefix(1) + parts[1].prefix(1)).uppercased()
        }
        return String(name.prefix(2)).uppercased()
    }

    private static let isoFormatter: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    static func from(_ payload: [String: JSONValue]) -> Agent? {
        guard let name = payload["name"]?.stringValue else { return nil }
        let description = payload["description"]?.stringValue ?? ""
        let tools: [String] = payload["tools"]?.arrayValue?.compactMap(\.stringValue) ?? []
        let lastMessage = payload["last_message"]?.stringValue

        var lastActivity: Date?
        if let activityStr = payload["last_activity"]?.stringValue {
            lastActivity = isoFormatter.date(from: activityStr)
                ?? ISO8601DateFormatter().date(from: activityStr)
        }

        return Agent(id: name, name: name, description: description, tools: tools,
                     lastMessage: lastMessage, lastActivity: lastActivity)
    }

    static func fromList(_ payload: [String: JSONValue]) -> [Agent] {
        guard let agentArray = payload["agents"]?.arrayValue else { return [] }
        return agentArray.compactMap { value -> Agent? in
            guard let dict = value.objectValue else { return nil }
            return Agent.from(dict)
        }
    }
}
```

- [ ] **Step 2: Build in Xcode**

Open the project in Xcode and build to verify no compile errors.

- [ ] **Step 3: Commit**

```bash
git add ios/Achates/Models/Agent.swift
git commit -m "Add lastMessage and lastActivity to iOS Agent model"
```

---

### Task 4: Redesign AgentListView

**Files:**
- Modify: `ios/Achates/Views/AgentListView.swift`

- [ ] **Step 1: Replace AgentRow with iMessage-style layout**

Replace the entire `AgentRow` struct (lines 58-85) with:

```swift
private struct AgentRow: View {
    let agent: Agent

    var body: some View {
        HStack(spacing: 14) {
            ZStack {
                Circle()
                    .fill(.blue.gradient)
                    .frame(width: 60, height: 60)
                Text(agent.initials)
                    .font(.system(size: 24, weight: .semibold))
                    .foregroundStyle(.white)
            }

            VStack(alignment: .leading, spacing: 4) {
                HStack {
                    Text(agent.name.capitalized)
                        .font(.system(size: 17, weight: .bold))
                    Spacer()
                    if let date = agent.lastActivity {
                        Text(formatTimestamp(date))
                            .font(.system(size: 14))
                            .foregroundStyle(.secondary)
                    }
                }

                Text(previewText)
                    .font(.system(size: 15))
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
            }
        }
        .padding(.vertical, 6)
    }

    private var previewText: String {
        if let msg = agent.lastMessage, !msg.isEmpty {
            return msg
        }
        if !agent.description.isEmpty {
            return agent.description
        }
        return "No messages yet"
    }

    private func formatTimestamp(_ date: Date) -> String {
        let calendar = Calendar.current
        if calendar.isDateInToday(date) {
            let formatter = DateFormatter()
            formatter.dateFormat = "h:mm a"
            return formatter.string(from: date)
        } else if calendar.isDateInYesterday(date) {
            return "Yesterday"
        } else if let weekAgo = calendar.date(byAdding: .day, value: -6, to: calendar.startOfDay(for: Date())),
                  date >= weekAgo {
            let formatter = DateFormatter()
            formatter.dateFormat = "EEEE"
            return formatter.string(from: date)
        } else {
            let formatter = DateFormatter()
            formatter.dateStyle = .short
            return formatter.string(from: date)
        }
    }
}
```

- [ ] **Step 2: Add large title display mode**

In the `AgentListView` body, add `.navigationBarTitleDisplayMode(.large)` after `.navigationTitle("Agents")`:

```swift
.navigationTitle("Agents")
.navigationBarTitleDisplayMode(.large)
```

- [ ] **Step 3: Build in Xcode**

Build and verify no compile errors.

- [ ] **Step 4: Visual test on simulator**

Run on iOS simulator. Verify:
- Large "Agents" title that collapses on scroll
- 60px circle avatar with gradient
- Bold agent name with timestamp right-aligned
- Two-line message preview in secondary color
- Settings gear in toolbar
- Fallback text when no messages exist

- [ ] **Step 5: Commit**

```bash
git add ios/Achates/Views/AgentListView.swift
git commit -m "Redesign AgentListView to iMessage-style conversation list"
```
