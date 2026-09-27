# Moneta financial access

The `moneta` tool invokes Moneta's Swift `moneta-read` executable. Moneta owns financial calculations; Achates owns agent approval. Both run on the same Mac, with the database accessible locally. The Moneta app does not need to be running.

## Build

From the Moneta repository:

```sh
swift build --package-path client -c release --product moneta-read
```

The binary is `client/.build/release/moneta-read`. Use its absolute path or copy it to a stable installation directory. Rebuild it when updating Moneta's financial logic. The CLI currently requires macOS 26 or later.

Build and restart the Achates server normally. This feature does not change the Apple wire protocol and requires no Apple client rebuild: `tools.list` supplies Moneta to the existing picker. Moneta's UI does not require rebuilding either.

## Grant access

1. Add `moneta` to the intended agent's Tools list, save, and reload it (the app's editor does this automatically). Complete its other edits before approving it.
2. Calculate the SHA-256 fingerprint of its saved UTF-8 `AGENT.md` text. For the default data root and an agent ID of `finance`:

   ```sh
   python3 - "$HOME/.achates/agents/finance/AGENT.md" <<'PY'
   import hashlib, pathlib, sys
   text = pathlib.Path(sys.argv[1]).read_bytes().decode('utf-8-sig')
   print(hashlib.sha256(text.encode('utf-8')).hexdigest())
   PY
   ```

3. Merge this into the owner-managed `config.yaml`, retaining existing tool settings:

   ```yaml
   tools:
     moneta:
       executable: /absolute/path/to/moneta-read
       database: /absolute/path/to/ledger.db
       approved_agents:
         finance: "PASTE_64_CHARACTER_SHA256_HERE"
   ```

The key is the agent's directory ID, not its display name. Paths must be absolute; `~` is not expanded for this tool. `ACHATES_HOME` and `ACHATES_CONFIG_PATH` continue to select data/config locations. The fingerprint is an integrity pin, not a secret token. Only the owner should be able to change these settings, the executable, and the database path through local filesystem access.

Missing/empty grants deny all financial requests. Grants are reread on every call, so adding/removing a grant needs no restart. Renaming or editing any part of `AGENT.md` invalidates approval, including edits by `profile` or `agent_manager`. Review the updated definition, reload it, and update its fingerprint to approve again. Old runtimes cannot inherit a newer definition's approval. Revocation during a query withholds its result but cannot recall data already returned.

No agent is approved automatically. Adding `moneta` to its tools is necessary but insufficient; agents cannot self-approve through `agent_manager`.

## Queries and limits

Actions are `info`, `accounts`, `transactions`, `spending`, `budget`, `assumptions`, and `formulas`. Start with `info` for budget years and `accounts` for IDs. Use `budget` with `kind: budget`, `actual`, or `difference` for Moneta's calculated year grids. Dates use `YYYY-MM-DD`; decimals are exact strings. Null means unavailable, not zero.

Transactions accept optional account/date/payee/category filters. List results accept `limit` (1–200, default 100) and `offset`; follow `next_offset` until null. Each request uses a fresh, consistent in-memory SQLite snapshot. Restart pagination after underlying edits/imports.

Account balances include all recorded entries, including future dates. Investment balances are cash only. Actuals honor stored exclusions, split re-dating, non-budget tags, and normalization rules. `spending` includes income as well as expenses; grouped payee totals are activity totals, not net cash flow. Budget rows, helpers, section totals, grand totals, and balance sections are distinct and must not all be summed together. Budget evaluation failures withhold calculated grids and return diagnostics. Source transaction IDs, periods, missing-data counts, and schema/read metadata accompany results.

Requests cannot supply SQL, paths, executable arguments, or authorization changes. Achates uses a subprocess argument list and stdin JSON. Limits are 16 KiB input, 256 KiB output, 30 seconds executing, 35 seconds including queueing, and two concurrent reads. Cancellation kills the subprocess tree. Logs contain only agent ID, action/outcome, and exception type; they omit filters, results, paths, and stderr. See Moneta's `client/READ-CLI.md` for the full protocol.

## Conversations and retained data

When either participant has Moneta assigned or an entry in the approval map, both must currently have valid approval to chat. The check runs before loading target history/core memory or streaming the initiating message. Ordinary agent pairs are unaffected with valid config; unreadable/malformed config denies chats until repaired. Delegated chat runtimes do not receive Moneta and cannot make fresh financial queries. Direct conversations and approved agents' scheduled jobs can use it.

Read-only protects the database from writes. Results still enter the chosen model's context and saved conversations; agents may retain them in memory or pass them to other enabled tools. Approval is not an OS sandbox or comprehensive data-flow isolation. For a finance-specific agent, consider `**Shared Memory:** false` before approval and enable only the tools it needs. Revoking access does not erase prior sessions, shared memory, or previously disclosed data. Model-default changes also affect where future results are processed.

## Tests

Ordinary tests cover approvals, edits, revocation, chat boundaries, literal arguments, malformed responses, and subprocess limits. To include the actual CLI integration, build Moneta first, then run from Achates:

```sh
MONETA_SOURCE_DIR=/absolute/path/to/moneta \
MONETA_READ_EXECUTABLE=/absolute/path/to/moneta/client/.build/release/moneta-read \
dotnet test Achates.slnx
```

That integration creates and removes an invented database. It never reads the configured personal database. Without the variables, this one test is reported as skipped.
