# iMessage-style Agent List

Redesign the iOS app's `AgentListView` to match iMessage's conversation list pattern: each agent row shows a last message preview and timestamp, with a large title navigation bar.

## Changes

### 1. Server: Enrich `agents.list` RPC

In `MobileTransport`, after building the agent list from `agents.list`, look up the latest session per agent and extract preview data.

Each agent in the response gains two optional fields:

```json
{
  "name": "paul",
  "description": "Personal assistant",
  "tools": ["session", "memory", ...],
  "last_message": "I've added that to your calendar for Friday at 3pm.",
  "last_activity": "2026-03-19T14:34:00Z"
}
```

- `last_message`: Plain text preview of the last message in the agent's most recent session. For user messages, use the text directly. For assistant messages, extract only `CompletionTextContent` blocks (skip thinking, tool calls, etc.) and join them. Replace newlines with spaces. Truncate to 100 characters at a word boundary, append "..." if truncated. Null if no sessions exist.
- `last_activity`: ISO 8601 timestamp of that message. Null if no sessions exist.
- Use `MobileSessionStore.GetLatestSessionAsync` (or equivalent) to find the latest session, then read the last message from it.
- All existing fields (`name`, `description`, `tools`) remain unchanged in the response.

### 2. iOS: `Agent` model

Add two optional properties:

```swift
let lastMessage: String?
let lastActivity: Date?
```

Parse `last_message` and `last_activity` from the enriched `agents.list` response in `Agent.from(_:)`.

### 3. iOS: `AgentListView` redesign

**Navigation bar:**
- `.navigationBarTitleDisplayMode(.large)` for the large "Agents" title
- Settings gear button remains in `.topBarTrailing`

**`AgentRow` layout:**
- 60px circle avatar with gradient, agent initials (24pt semibold)
- Right of avatar: name (17pt, weight 700) with timestamp right-aligned on the same line
- Below name: two-line message preview in secondary color (15pt)
- No chevron, no description line
- Vertical padding for comfortable tap targets

**Timestamp formatting** (device local timezone):
- Today: time only ("2:34 PM")
- Yesterday: "Yesterday"
- This week: weekday name ("Monday")
- Older: short date ("3/14/26")

**Empty/no-message state:** When an agent has no `lastMessage`, show the agent description as the preview text in the same secondary style. If description is also empty, show "No messages yet".

## Out of scope

- Search/filter bar
- Swipe actions on rows
- Unread indicators or badges
- Pull-to-refresh (agents are loaded on connect)
