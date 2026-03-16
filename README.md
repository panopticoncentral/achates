# Achates

A personal AI assistant that runs on your own devices and answers on the channels you use.

## Outlook Mail & Calendar Setup

Achates can read your Outlook email and calendar via the Microsoft Graph API. There are two setup paths depending on your account type.

### Option A: Personal Microsoft Account (Outlook.com, Hotmail, Live)

This uses **device code flow** — you sign in once in a browser, and Achates caches the token for future use. Only read-only access is requested.

#### 1. Register the app

1. Go to the [Azure Portal](https://portal.azure.com) and sign in with your Microsoft account
2. Navigate to **Microsoft Entra ID** → **App registrations** → **New registration**
3. Fill in:
   - **Name**: Achates (or whatever you like)
   - **Supported account types**: select **Personal Microsoft accounts only**
   - **Redirect URI**: select **Public client/native (mobile & desktop)** and enter `https://login.microsoftonline.com/common/oauth2/nativeclient`
4. Click **Register**
5. On the Overview page, copy the **Application (client) ID**

#### 2. Add API permissions

1. Go to **API permissions** → **Add a permission** → **Microsoft Graph** → **Delegated permissions**
2. Add these permissions:
   - `Mail.Read`
   - `Calendars.Read`
   - `Contacts.Read`
3. Click **Add permissions**

No admin consent is needed — you consent for yourself during sign-in.

#### 3. Configure Achates

In `~/.achates/config.yaml`:

```yaml
tools:
  graph:
    personal:
      client_id: <your-application-client-id>

agents:
  myagent:
    tools: [session, memory, mail, calendar]
```

That's it — no `client_secret`, `tenant_id`, or `user_email` needed. The name (`personal` here) is up to you.

#### 4. First run

On startup, the server authenticates eagerly and shows a device code prompt:

```
Graph sign-in required: To sign in, use a web browser to open
https://microsoft.com/devicelogin and enter the code ABCD1234
```

If re-authentication is needed later (e.g. token expired), the sign-in message is also sent through your chat (Telegram, WebSocket, etc.).

Open the URL, enter the code, and sign in with your Microsoft account. You'll see a consent screen listing "Read your mail", "Read your calendars", and "Read your contacts". Accept it.

The token is cached at `~/.achates/graph-token-cache.bin` and reused automatically on future runs. You won't need to sign in again unless the refresh token expires (typically 90 days of inactivity).

---

### Option B: Work or School Account (Microsoft 365)

This uses **client credentials flow** — fully automatic, no interactive sign-in. Requires admin access to your Microsoft 365 tenant.

#### 1. Register the app

1. Go to the [Azure Portal](https://portal.azure.com) and sign in with your work/school account
2. Navigate to **Microsoft Entra ID** → **App registrations** → **New registration**
3. Fill in:
   - **Name**: Achates
   - **Supported account types**: select **Accounts in this organizational directory only** (single tenant)
   - **Redirect URI**: leave blank
4. Click **Register**
5. On the Overview page, copy:
   - **Application (client) ID** → this is your `client_id`
   - **Directory (tenant) ID** → this is your `tenant_id`

#### 2. Create a client secret

1. Go to **Certificates & secrets** → **New client secret**
2. Add a description and pick an expiry (e.g. 12 or 24 months)
3. Click **Add**
4. **Copy the Value immediately** — it's only shown once

#### 3. Add API permissions

1. Go to **API permissions** → **Add a permission** → **Microsoft Graph** → **Application permissions**
2. Add these permissions:
   - `Mail.Read` — read mail in all mailboxes
   - `Calendars.Read` — read calendars in all mailboxes
   - `Contacts.Read` — read contacts in all mailboxes
3. Click **Add permissions**
4. Click **Grant admin consent for [your tenant]** and confirm

If you don't have admin rights, ask your tenant administrator to grant consent.

#### 4. Configure Achates

In `~/.achates/config.yaml`:

```yaml
tools:
  graph:
    work:
      tenant_id: <your-directory-tenant-id>
      client_id: <your-application-client-id>
      client_secret: <your-client-secret>
      user_email: you@yourcompany.com

agents:
  myagent:
    tools: [session, memory, mail, calendar]
```

To keep the secret out of the config file, set the `GRAPH_CLIENT_SECRET` environment variable instead and omit `client_secret` from the YAML.

The `user_email` specifies whose mailbox to access — it must be a user in the same tenant.

#### 5. Run

No interactive sign-in needed. The agent can access mail and calendar immediately on startup.

---

### Using Both Accounts

You can configure both a personal and work account on the same agent:

```yaml
tools:
  graph:
    personal:
      client_id: <personal-app-client-id>
    work:
      tenant_id: <your-directory-tenant-id>
      client_id: <work-app-client-id>
      client_secret: <your-client-secret>
      user_email: you@yourcompany.com

agents:
  myagent:
    tools: [session, memory, mail, calendar]
```

When multiple accounts are configured, the mail and calendar tools gain an `account` parameter. The agent is told which accounts are available and will select the right one based on context, or you can ask explicitly (e.g. "check my work calendar").

---

## Apple Notes Setup

Achates can access a restricted folder in Apple Notes on macOS. The Notes tool can list note titles, read a note by exact title, create new notes, rename notes, and replace note contents inside one configured folder.

In `~/.achates/config.yaml`:

```yaml
tools:
  notes:
    folder: Achates

agents:
  myagent:
    tools: [session, memory, notes]
```

If `notes.folder` is omitted, Achates defaults to the `Achates` folder. The tool will refuse access to notes outside that folder and will error if multiple accounts contain folders with the same name.

## Web Search & Fetch Setup

Achates can search the web and fetch page content. Web search uses the [Brave Search API](https://brave.com/search/api/).

### 1. Get a Brave Search API key

1. Go to [brave.com/search/api](https://brave.com/search/api/) and create an account
2. Subscribe to a plan (the free tier gives 2,000 queries/month)
3. Copy your API key

### 2. Configure

Set the `BRAVE_API_KEY` environment variable, or add it to your config:

```yaml
tools:
  web_search:
    brave_api_key: BSA...

agents:
  myagent:
    tools: [session, memory, web_search, web_fetch]
```

`web_fetch` works without an API key — it only needs `web_search` to have Brave configured.

### Tools

**web_search** — Search the web
- Returns a numbered list of results with title, URL, and description
- Params: `query` (required), `count` (1-20, default 5)

**web_fetch** — Fetch and extract readable content from a URL
- Uses Readability extraction for HTML, returns plain text
- Params: `url` (required), `max_chars` (default 20,000, max 50,000)

## iMessage Setup (macOS only)

Achates can read your iMessage conversations directly from the local Messages database. This is read-only — it cannot send messages.

### 1. Publish the server binary

The iMessage database requires Full Disk Access (FDA). Rather than granting FDA to your terminal (which would give it to every process you launch), publish a standalone binary and grant FDA to just that:

```bash
dotnet publish src/Achates.Server -c Release -o ~/.achates/bin
```

### 2. Grant Full Disk Access

1. Open **System Settings** > **Privacy & Security** > **Full Disk Access**
2. Click `+`
3. Press `Cmd+Shift+G` and type `~/.achates/bin/Achates.Server`
4. Add it and ensure the toggle is on

### 3. Configure

In `~/.achates/config.yaml`, add `imessage` to your agent's tools:

```yaml
agents:
  myagent:
    tools: [session, memory, imessage]
```

### 4. Run from the published binary

```bash
~/.achates/bin/Achates.Server
```

You must run the published binary (not `dotnet run`) for FDA to apply.

### Tools

**imessage** — Read iMessage conversations
- `chats`: list recent conversations with last message preview
- `read`: view messages from a specific chat (by chat ID from the chats list)
- `search`: full-text search across all messages

## Withings Health Data Setup

Achates can query health data from Withings — weight, body composition, blood pressure, sleep, and activity.

### 1. Register a Withings app

1. Go to [developer.withings.com](https://developer.withings.com) and create a developer account
2. Create a new application
3. Set the **Callback URL** to `http://localhost:5000/withings/callback`
4. Copy your **Client ID** and **Consumer Secret**

### 2. Configure

In `~/.achates/config.yaml`:

```yaml
tools:
  withings:
    client_id: <your-client-id>
    client_secret: <your-consumer-secret>

agents:
  myagent:
    tools: [session, memory, health]
```

To keep the secret out of the config file, set the `WITHINGS_CLIENT_SECRET` environment variable instead and omit `client_secret` from the YAML.

The `redirect_uri` defaults to `http://localhost:5000/withings/callback`. Override it in config if your server runs on a different port.

### 3. First run

On first use, the health tool returns an authorization URL. Open it in a browser, sign in with your Withings account, and authorize the app. You'll be redirected back to your local server, and you'll see a "Connected!" confirmation page.

Tokens are cached at `~/.achates/withings-tokens.json` and refresh automatically (access tokens last 3 hours, refresh tokens last 1 year).

### Tools

**health** — Query Withings health data
- `weight`: body composition (weight, fat ratio, muscle mass, bone mass)
- `blood_pressure`: systolic/diastolic BP and heart rate
- `sleep`: sleep stages (awake, light, deep, REM) with durations
- `activity`: steps, distance, calories, active time, heart rate
- `authorize`: get the authorization URL (called automatically if not yet authorized)
- Params: `action` (required), `days` (lookback period, default 7)

## Tools Reference

| Tool | Description | Config required |
|------|-------------|-----------------|
| `session` | Current time, model info, timezone | None |
| `memory` | Persistent agent memory across sessions | None |
| `todo` | Manage a Markdown todo list | `tools.todo.file` path |
| `notes` | Access Apple Notes (macOS only) | Optional `tools.notes.folder` |
| `mail` | Read Outlook email | `tools.graph` account(s) |
| `calendar` | View Outlook calendar | `tools.graph` account(s) |
| `web_search` | Search the web via Brave Search | `BRAVE_API_KEY` or `tools.web_search.brave_api_key` |
| `web_fetch` | Fetch and extract web page content | None |
| `cost` | Query usage costs (summary, recent, breakdown) | None |
| `imessage` | Read iMessage conversations (macOS only) | Full Disk Access on published binary; `tools.graph` for contact names |
| `health` | Query Withings health data (weight, BP, sleep, activity) | `tools.withings` client_id and client_secret |
| `chat` | Talk to other agents (discovery + ping-pong conversation) | At least 2 agents configured |
| `cron` | Create and manage scheduled tasks | None |
