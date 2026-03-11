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
3. Click **Add permissions**

No admin consent is needed — you consent for yourself during sign-in.

#### 3. Configure Achates

In `~/.achates/config.yaml`:

```yaml
agents:
  myagent:
    tools: [session, memory, mail, calendar]
    graph:
      personal:
        client_id: <your-application-client-id>
```

That's it — no `client_secret`, `tenant_id`, or `user_email` needed. The name (`personal` here) is up to you.

#### 4. First run

On startup, the server authenticates eagerly and shows a device code prompt:

```
Graph sign-in required: To sign in, use a web browser to open
https://microsoft.com/devicelogin and enter the code ABCD1234
```

If re-authentication is needed later (e.g. token expired), the sign-in message is also sent through your chat (Telegram, WebSocket, etc.).

Open the URL, enter the code, and sign in with your Microsoft account. You'll see a consent screen listing "Read your mail" and "Read your calendars". Accept it.

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
3. Click **Add permissions**
4. Click **Grant admin consent for [your tenant]** and confirm

If you don't have admin rights, ask your tenant administrator to grant consent.

#### 4. Configure Achates

In `~/.achates/config.yaml`:

```yaml
agents:
  myagent:
    tools: [session, memory, mail, calendar]
    graph:
      work:
        tenant_id: <your-directory-tenant-id>
        client_id: <your-application-client-id>
        client_secret: <your-client-secret>
        user_email: you@yourcompany.com
```

To keep the secret out of the config file, set the `GRAPH_CLIENT_SECRET` environment variable instead and omit `client_secret` from the YAML.

The `user_email` specifies whose mailbox to access — it must be a user in the same tenant.

#### 5. Run

No interactive sign-in needed. The agent can access mail and calendar immediately on startup.

---

### Using Both Accounts

You can configure both a personal and work account on the same agent:

```yaml
agents:
  myagent:
    tools: [session, memory, mail, calendar]
    graph:
      personal:
        client_id: <personal-app-client-id>
      work:
        tenant_id: <your-directory-tenant-id>
        client_id: <work-app-client-id>
        client_secret: <your-client-secret>
        user_email: you@yourcompany.com
```

When multiple accounts are configured, the mail and calendar tools gain an `account` parameter. The agent is told which accounts are available and will select the right one based on context, or you can ask explicitly (e.g. "check my work calendar").

---

### Available Tools

Once configured, the agent has two tools:

**mail** — Read Outlook email
- `list` — Recent messages (params: `count`, `folder`)
- `read` — Full message by ID
- `search` — Search messages using KQL syntax

**calendar** — View Outlook calendar
- `upcoming` — Events in the next N days (params: `days`, `count`)
- `read` — Full event details by ID
- `availability` — Free/busy status for a time range (params: `start`, `end`)

## Apple Notes Setup

Achates can access a restricted folder in Apple Notes on macOS. The Notes tool can list note titles, read a note by exact title, create new notes, rename notes, and replace note contents inside one configured folder.

In `~/.achates/config.yaml`:

```yaml
agents:
  myagent:
    tools: [session, memory, notes]
    notes:
      folder: Achates
```

If `notes.folder` is omitted, Achates defaults to the `Achates` folder. The tool will refuse access to notes outside that folder and will error if multiple accounts contain folders with the same name.
