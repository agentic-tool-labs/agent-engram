# Live memory tail — spec

Status: **FINAL, approved for build** (r4, 2026-10-08). Author: Architect (peer requests from engram-1a).
Base: branch `mods/prefixed-commands-and-auto-save` @ `ccfe878`.

Implementer: implementor
Reviewer: reviewer

## Revision log

- **r1.** A write-only tail over `fact`.
- **r2.**
  - Jim's answers Q1–Q4 applied: scope defaults to `session`, there is no backlog, retractions are shown, and there is no provenance column.
  - Activity scope added: a telemetry event ring fed by the one `TelemetryTail` reader.
  - One schema index added.
- **r3.** §5a push vs poll.
- **r4 (final).** Q5 and Q6 are closed (§14).
  - Push designs and the `data_version` fast path are **deferred**: future work, not this build.
  - Build order added (§15).
  - **Defect fixed while finalising.** MCP tools identify sessions by the transport `Mcp-Session-Id` (`ServeCommand.cs:304-322`), not Claude Code's session id, so the two never meet (D43). This was measured on the real log: 0 of 28 `recall` and 0 of 45 `remember` records share a `session_id` with any of 48 `session-start` ids, while 41 of 41 `mod-call` records do.
  - r2's server-side `scope=session` would therefore have hidden the model's own recalls, remembers, revisions and forgets, which is most of what Jim wants to watch.
  - The fix (§6): under `scope=session` the mod observes this session's Engram MCP tool calls in-process through `tool.call`, as Lens already does for recall. The server's session filter still covers everything keyed by the Claude Code id.
  - The "stop rather than substitute" assumption on `Forget` is replaced: each caller stamps the session id it actually has.
  - Smaller changes:
    - Event times render `HH:mm:ss`, so lens's formatter is reused unchanged.
    - The formatter is imported from `lens/model.ts`, not moved.

- **r5 (Reviewer ruling on the build).**
  - F2: §5.1's "a hanging subscriber cannot delay the tail" is weakened to the true bound. Option (a): no code change, bound recorded in D79.
  - F4: M1's falsification replaced with one that can fail.
  - F5: body clip fixed at 120 characters.
  - §4/S9: `INDEXED BY` plus the unary `+` are stated as load-bearing; the planner does not pick the index unaided.

- **r6 (narrow panes; Jim's screenshot of a ~24-column pane).** §6.9 added, presentation only.
  - Rows become a fixed head line (marks, time, label) plus an optional detail line (short fields first, free text last). Both lines use host `truncate-end`.
  - The filter becomes one `Filter n/8 ▾` button that discloses a vertical list of toggles.
  - Status texts are shortened to wrap-safe forms.
  - Day separators replace dated timestamps.
  - The host has no popup and no width reader, so the layout is width-agnostic.
  - ce26162's button strip is deleted. Plugin version 1.3.7.

- **r7 (spec matched to the r6 build, 392a39e).** No code change.
  - Head marks are adjacent.
  - The `qualifier` field on `TailRow` is confirmed.
  - The empty state wraps too (W6, W11).
  - Unknown long labels are never cut by the mod.
  - Day separators over marker rows are intended.
  - The M-test rewrite rule is broadened to all r5-string tests.
  - W2's falsification is now W2-specific.

- **r8 (reload bug, live 1.3.7).** §6.10.
  - The toggle and `session.start` decide from **shown** (`$.ui.panes()`, falling back to the atom) and **running** (a module-level flag a reload resets).
  - Resume keeps the saved cursors, so nothing is missed and nothing replays.
  - A heartbeat line shows liveness, and the empty state is split into connecting and watching forms.
  - The atom carries a `shape` tag, and a mismatched atom is declined.
  - `not-found`/`unsupported` retry every 300 s instead of stopping, and the shared client's sticky `unsupported` flag becomes a 300 s back-off.
  - E3 is answered for hot reload; E2 is now load-bearing, with a safe failure. E16 is added.
  - Plugin version 1.3.8.

- **r9 (spec matched to the r8 build, 21f0809).** §6.10.9.
  - Connecting line above existing rows: confirmed.
  - A declined atom with a shown pane is a plain open: text fixed.
  - **Open clears rows and `lastOkAt`; resume keeps them** (code change, 1.3.9).
  - `TAIL_SHAPE` / `Shaped<TailState>` recorded.
  - Call rows gate on `paneOpen`, not `running`: confirmed.

- **r10 (spec matched to 9fc358d).** No code change. M18 is downgraded to a behaviour test; R11's closed case and R2 are named as the `paneOpen` guards. The M4 rewrite is recorded.

- **r11 (Reviewer nits on r8/r9).** §6.10.10.
  - `session.start` on a not-shown pane also `stop()`s.
  - `unsupported` polls at 15 s; the true recovery bound is stated, and the short-circuit must come before port lookup.
  - R3 gains a third toggle that guards `running = false`.
  - Clearing `running` on a throwing reschedule is declined.
  - Plugin version 1.3.10.

## TL;DR

- `/engram-tail` toggles the **Memory Tail** pane: a live, newest-first log of memory activity.
- One lookup op, `POST /mod/v1/tail`, returns three independently-cursored lists:

| List | Source | Cursor | Dedupe key |
|---|---|---|---|
| writes | `fact` | rowid (exact) | `w<id>` |
| retractions | `supersession` with `new_fact_id IS NULL` | `created_at` with 10 s slack | `r<old_fact_id>` |
| events | in-memory ring fed by the server's one `TelemetryTail` | `(epoch, seq)` | `e<epoch>:<seq>` |

- Under `scope=session` (the default), the mod adds rows for this session's own Engram MCP tool calls from `tool.call`. These are keyed `c<n>`, and they are the only exact way to attribute MCP activity to a Claude Code session.
- `tail` writes no telemetry and never appears in its own feed.
- Every kind keeps its own label. The filter starts with Maintenance and Sessions off.
- No hook changes. The `file-touched` and `session-start` budgets cannot move.

## 1. Goal

Jim wants to watch memory activity as it happens, and toasts replace each other. The tail shows:
- fact writes from every writer;
- retractions;
- every recorded activity kind.

There is no CLI `remember` verb. The CLI writers are `directive`, `invariant`, `import`, `backup replay` and sync.

## 2. Sources of truth

| What | Source | Why |
|---|---|---|
| Fact writes | `fact` by rowid | Every insert goes through `FactStore.Remember` (`FactStore.cs:775`) or the journal replay insert (`FactJournal.cs:575-589`). Writes are `BEGIN IMMEDIATE` (D4), so rowids are assigned in commit order inside the writer lock, and a reader at N never later finds an unseen id ≤ N. Telemetry cannot serve here: no record carries a fact id or body, and CLI writers emit none. |
| Retractions | `supersession` with `new_fact_id IS NULL` | `FactStore.Forget` (`FactStore.cs:140/159`) is the one retraction write. It sets `valid_to` and inserts this row in one transaction. There is **no `forget` telemetry kind**, and `engram_forget` writes none. |
| Activity | `telemetry.jsonl` through the server's one `TelemetryTail` into a ring | Recall, remember and the rest have no store row. A file reader inside the op would be a second reader, and CLAUDE.md's webhook section forbids that without revisiting `DurableAppend` starvation (§5.3). |
| This session's MCP calls (scope `session`) | the mod's own `tool.call` hook | MCP telemetry and MCP-written facts carry the transport session id, which cannot be mapped to a Claude Code session (D43). The mod sees its own session's tool calls directly. |

Rejected:
- **SSE or other server push.** See §5a: the mod cannot hold a stream.
- **A `created_at` cursor for writes.** It is unindexed, ties at the second, and moves when the clock steps.
- **A provenance column.** Q4.
- **Fact ids in telemetry.** Q5.
- **Mapping transport ids to Claude Code ids by time and query proximity.** That is a guess. It is ambiguous when two sessions act in the same second, and a wrong attribution is worse than none.

## 3. Writes

Response part: `head` plus `writes: { rows, skipped }`.

- `head` is `max(fact.id)` over every row: any `regenerable`, any validity. It is `0` for an empty store. The mod's next `after` is always `head`, **never the last row's id**.
- Rows are `id > after AND regenerable = 0`, filtered by scope, newest `limit`, id descending. In a burst the oldest rows are dropped, not paged.
- `after` omitted means a first read: no rows and `skipped: null`. There is no backlog.
- `skipped` is the exact count of qualifying rows in `(after, head]` that were not returned. It is bounded by the burst, never the corpus.
- **Fields:** `handle` (`fN`), `id`, `created_at` (unix s), `origin`, `body`, `evidence`, `replaces`, `live`, `this_session`.
  - `details` are never returned.
  - `live` is `valid_to IS NULL` at read time.
  - `replaces` is the handle of the earlier row of the same thread whose `superseded_by` equals this id. Resolve it through `ix_fact_thread` or `ix_supersession_new`, **never** by filtering `fact.superseded_by`, which has no index.
- **Scope:**
  - `session` matches `fact.session_id` → `session.external_id = session_id`. That covers captures, digest saves, mod `remember` and the compaction harvester. The model's own notes are keyed to the transport id, so they arrive through §6.4 instead.
  - `all` returns every row.
  - `this_session` means "keyed to this Claude Code id".

**Origin.** One Core function computes it; first match wins:

| # | Rule | `origin` |
|---|---|---|
| 1 | path under `/directives/` | `directive` |
| 2 | predicate `invariant` | `invariant` |
| 3 | `/sessions/<n>/<agent>/…` with the PostCompact harvester's agent constant (`HookCommand.cs:664`; reference the constant, never retype it) | `compaction` |
| 4 | `replaces` non-null | `revision` |
| 5 | path under `UserFacts.Root + "/"` and `learned_via='stated'` | `capture` |
| 6 | path under `/sessions/` | `note` |
| 7 | otherwise | `other` |

Rule 4 keys on `superseded_by`. `Forget` never sets it, so a forget followed by a verbatim recapture stays `capture`.

**Digest label.** The mod relabels `note` as `digest` or `digest·auto` when `evidence` equals `EVIDENCE` or `AUTO_EVIDENCE`. Import both from `plugin/mods/digest/digest.ts:4-5`, where they are already exported. Never copy them. The label is cosmetic.

## 4. Retractions

Response part: `retractions: { rows }`.

**Definition.** A `supersession` row with `new_fact_id IS NULL` whose `old_fact_id` names a `regenerable = 0` fact.
- `CodeIndexer`'s forgets (`CodeIndexer.cs:644`, `:691`) are excluded.
- A close with a successor is not a retraction. It shows as the successor's `revision` write.

**Cursor.** `closed_after`, in unix seconds. `supersession`'s rowid is `old_fact_id`, not insertion order.
- **Window and slack.** Return rows with `created_at >= closed_after - 10`. A writer stamps `now` before acquiring the lock and can wait up to `busy_timeout` (5000 ms), so a retraction may commit after a later-stamped one was already read. 10 s covers that plus rounding.
- **Dedupe** by `old_fact_id`. It is the primary key, so a fact retracts at most once.
- **Next cursor.** `max(closed_after, max created_at returned)`.
- **First read.** No `closed_after` means no rows. The response's `now` (server unix seconds) becomes the first cursor.
- **Limit.** Newest `limit` in the window.

**Index.** This is the one schema change: `ix_supersession_retracted ON supersession(created_at) WHERE new_fact_id IS NULL`. Schema version 16 → 17. Without the index every 2 s poll scans every retraction ever made, a set that grows with each reindex.

**The planner does not pick this index unaided** *(r5, Reviewer ruling; r1–r4 assumed it would)*. The retraction statement must:
- name the index with `INDEXED BY ix_supersession_retracted`;
- use a unary `+` to disable the competing index, as built and recorded in D79.

Both are load-bearing. A comment at the statement says why, and S9 holds each by its own falsification row. Because of `INDEXED BY`, a store missing the index fails at prepare rather than silently scanning. That is acceptable: the store always migrates on open (`OpenInitialized`), and S14 covers the migration.

**Fields.** `handle` (the retracted fact), `id`, `retracted_at`, `reason`, `body`, `origin` (of the retracted fact, by §3's function), `this_session`.

**Who retracted.** `FactStore.Forget`, both overloads, gains an optional session row id, written to `supersession.session_id`.
- **MCP `engram_forget`** passes the session row for its `McpSessionId`. That is a transport id, and that is correct: it is the id space every other MCP write already uses (`EngramMcpTools.cs:231`).
- **Mod `forget`** (`ModApi.cs:192`) passes the row for the request's `session_id`, which is a Claude Code id.
- Both resolve or create the session row exactly as `SessionFacts.Append` does for their id.
- **CLI, `SessionFacts` and `CodeIndexer`** pass none. Their row is unchanged from today.
- **Scope.** `session` matches `supersession.session_id` → `external_id = session_id`. That covers `/engram-undo-capture` and other mod forgets. The model's MCP forgets arrive through §6.4.

**The mod side.** A retraction, or an observed `forget` call, for a handle that has a write row in the ring also marks that row retracted.

**Ceiling.** `Sync.ApplyClose` (`Sync.cs:1135`) with an unsynced successor writes no `supersession` row, so a remote forget is not shown.

## 5. Events

Response part: `events: { epoch, head, rows, skipped } | null`.

### 5.1 One reader, shared output

**Today.** `WebhookService` (`src/Engram.Cli/WebhookService.cs`):
- is registered unconditionally (`ServeCommand.cs:127`);
- returns early without a URL (`:60-63`);
- otherwise tails from EOF (`:66`) every 500 ms (`:41`), reading at most 64 raw lines per poll (`:46`) through `TelemetryTail` (`FileShare.ReadWrite|Delete`, per-read open, `TelemetryTail.cs:92-93`).

**Required.**
- **Exactly one `TelemetryTail` over `telemetry.jsonl` per server process.** Its parsed records (`Telemetry.TryParse`) feed two consumers: webhook delivery and the ring.
- **A new Core type owns the reader, the ring and the demand gate.** It is registered as a singleton in `ServeCommand.cs`. `WebhookService` consumes it, and `HandleModCall` passes it to `ModApi.Execute`.
- **The reader runs while either holds:**
  - a webhook URL is configured, from server start, as today;
  - a `tail` request arrived within the last **10 s**. The first such request starts the reader at EOF if it is not running. It stops when demand lapses and no webhook is configured.
- **Each batch reaches the ring before webhook delivery runs.** A hanging subscriber therefore never delays records already read. It *can* delay the **next** read, because the one loop both reads and delivers. The bound: at most the delivery timeout × the number of unmuted URLs that hang, per poll. Each failing URL gets at most one attempt per poll and is then muted (2 s doubling to 30 s), so one persistently hanging URL costs about one timeout of ring staleness each time its mute expires. Record this bound in D79.
  - *(r5, Reviewer F2; option (a) chosen, no code change.)* A pump independent of delivery, with its own tick and batches dropped while delivery is busy, was rejected for two reasons. It changes webhook semantics: today a slow subscriber delays the others' delivery, and the alternative would *drop* their batches, which §10 forbids. And the stall only exists when a webhook URL is configured *and* hangs, while the tail's own cadence is 2 s. Reopen if E7 or live use shows lag that matters.
- **Unchanged:**
  - start at EOF at server start;
  - no cursor or resume;
  - drop on failure;
  - at most one failing attempt per URL per poll;
  - per-URL doubling mute;
  - the loop catches its own exceptions;
  - the startup log line comes after the mark;
  - 64 lines per poll and 500 ms.

### 5.2 Ring and cursor

- The ring holds **1024** records in memory (≈ 660 B each on the real log).
- `seq` is monotonic from 1 within an **epoch**. The epoch is an opaque token that changes whenever the reader (re)starts at EOF.
- **Cursor requests:**
  - `event_epoch` omitted or not current → no rows, `skipped: null`, plus the current `epoch` and `head`;
  - same epoch → records with `seq > event_after`, newest `limit`, descending.
- `skipped` counts records lost to ring overflow plus any beyond `limit`.
- `events: null` when no feed was passed to `Execute`, or the telemetry path cannot be resolved.
- **Row:** `{ seq, record }`. `record` is the parsed `TelemetryRecord`, serialised with the existing source-gen context: the log line's fields, verbatim (D55).
- **Scope:** `session` is `record.session_id == session_id`, exact. That selects `mod-call` and the Claude-keyed hook kinds. MCP kinds (`recall`, `remember`, `revise`, `browse`, `expand`, `judge`, `navigate`, `session-open`) carry transport ids and appear only under `all`; under `session` they come from §6.4.

### 5.3 Starvation

`DurableAppend` writers open with `FileShare.None`:
- every caller except `file-touched` retries for 500 ms;
- `file-touched` gives up after one try and drops its record (D56).

r4 changes **when** the one reader runs, not how. With no webhook and no open tail, no reader runs, as today. With a tail open, the exposure equals today's webhook-configured exposure. The cost, measured after the build (E6), goes into D79 and CLAUDE.md.

## 5a. Push vs poll — DEFERRED (future work, not this build)

- **Known:** `$.http.fetch` has no streaming body, no timeout option and no cancellation, and the host aborts it at 30 s. The shared client clamps every call to 2 s.
  - So the mod can neither hold a stream nor long-poll. No SSE endpoint is built.
- **Every future push design is a wake-up only.** Data still comes from one `tail` read, so a missed wake-up costs latency, never data. Each design keeps a 30 s poll heartbeat.

| If… | Design | Rank |
|---|---|---|
| E10: a mod can watch a file | Watch `engram.db-wal` (changes on every commit from any process) and `telemetry.jsonl`. Debounce 250 ms, at most one read in flight. No server change. | 1 |
| E8: the engine accepts an inbound connection or event | The existing webhook is the sender, with kinds `*`. Writes still need E10 or the heartbeat. | 2 |
| E9: `$.process.run` streams a long-lived child | One child per open pane prints a line per change. Needs a new CLI verb and a guaranteed kill on close, reload or end (orphan risk). | 3 |
| All no | Polling only, which this build already is | — |

**Deferred fast path.** A dedicated held connection reads `PRAGMA data_version`; an unchanged value plus an unchanged `seq` answers empty without touching a page. Build it only if E1 shows an idle poll above 1 ms of server time. It must be a held connection: `data_version` is per connection, and pooling hands back different ones.

## 6. The `tail` op and the mod

### 6.1 Request (invalid → 400 `bad_request`)

| Field | Rule |
|---|---|
| `session_id` | required, `RequireSession` |
| `after`, `closed_after` | optional int ≥ 0 |
| `event_epoch` | optional string ≤ 64 |
| `event_after` | optional int ≥ 0; ignored without `event_epoch` |
| `scope` | optional `"session"` \| `"all"`, **default `"session"`** |
| `limit` | optional 1..50, default 20, applied to each list |

### 6.2 Response

```
{ head, now,
  writes: { rows, skipped },
  retractions: { rows },
  events: { epoch, head, rows: [ { seq, record } ], skipped } | null }
```

### 6.3 What the op must not do

- **No telemetry**, on success or failure. `tail` is a lookup (D76), and a 2 s poll that logged itself would add ~1,800 records an hour to the file that D18/D43 and the webhook read.
- **No write transaction.**
- **Cost:**
  - writes: rowid seek;
  - retractions: seek on `ix_supersession_retracted`;
  - `head`: `max(id)`;
  - events: in memory;
  - never `SCAN fact` or `SCAN supersession`.
- **No remember↔fact join (Q5, closed).** The `remember`, `revise` and `user-prompt` records are `{timestamp, session_id, kind}` only. The event row and the write row are adjacent (§6.6). D76 stays unamended.

### 6.4 This session's MCP calls (mod, `scope=session` only)

Register **one** `tool.call` hook whose matcher selects every Engram MCP tool. Use a `tool` pattern built from the existing Engram tool-name constants that lens uses (`ENGRAM_TOOLS`). That is a matcher, not a matcher-less registration, so it does not collide with lens's `{ tool: ENGRAM_TOOLS.recall }`. Follow `lens/index.tsx`'s handler shape and `shared/guard.ts`: never throw, call `next` once, and return the original result.

For each call, the mod records one **call row**, key `c<n>` (n a per-session counter), with these fields:
- **time:** the hook's own clock, in ms.
- **label:** the tool name with the `engram_` prefix removed (`recall`, `remember`, `revise`, `forget`, `browse`, `expand`, `judge`, `navigate`, `pin`, `unpin`, `index_repo`, …).
- **Content:**
  - `recall`: `"<query>"`, plus fact count and coverage parsed from the result with **lens's `parseDigest`** (`lens/parser.ts`). No second parser.
  - `remember` / `revise`: the statement from the input, and the handle as the first `[fN]` in the result text. If there is none, show no handle.
  - `forget`: the handle from the input. It also marks a matching write row retracted.
  - every other tool: the label plus whichever of `query`, `relation`, `fact_id`/handle are present in the input.
- **Handles:** the handles seen here go into a per-session set, used in §6.6.

The hook records call rows only while `tail_scope = "session"`. Under `all`, the telemetry events already contain these calls, and adding call rows would duplicate them. The hook is registered in both scopes and simply no-ops under `all`.

Call rows are recorded whether or not the pane is open. They are cheap and in-process, so opening the pane mid-session shows only rows recorded *after* it opened. To honour the no-backlog rule (Q2), the mod discards call rows while the pane is closed.

### 6.5 Mod files, events, loop

| File | Change |
|---|---|
| `plugin/mods/tail/index.tsx` | new; exports `register` |
| `plugin/mods/tail/model.ts` | new; pure merge, order, filter, row-shape logic |
| `plugin/mods/tail/tail.test.tsx` | new; M1–M18 |
| `plugin/hooks/register.tsx` | add `tail(on, options)` to the composer list |
| `plugin/mods/shared/state.d.ts` | add key `tail` to the single `PluginState['engram']` literal |
| `plugin/mods/shared/types.ts` | add `tail` to `ModOps` |
| `plugin/.claude-plugin/plugin.json` | options §8; version `1.3.5` |
| `.claude-plugin/marketplace.json` | version `1.3.5`, mirroring how ccfe878 bumped it |

No `plugin/commands/` file (lens has none). Lens, digest and toasts files are not edited: the tail imports `clock` and `stamp` from `lens/model.ts:21,27`, `parseDigest` from `lens/parser.ts`, and `EVIDENCE` and `AUTO_EVIDENCE` from `digest/digest.ts:4-5`.

**Events:**
- `command.run` `{ command: 'engram-tail' }`: toggle the pane as `lens/index.tsx:104-119` does. Pane id `engram-tail`, title `Memory Tail`.
- `session.start` with `ANY_SESSION_START`: if `tail_auto_open`, open the pane via `$.clock.after(0, …)`.
- `tool.call` per §6.4.

**Loop.** A `$.clock.after` chain that runs only while the mod believes the pane is open:
- 2000 ms after a success, 15000 ms after a failure;
- uses band's generation counter plus `stop()` pattern (`band/index.tsx`).

**Requests:**
- **Every call** goes through `modApi(io, 'tail', …, { mod: 'tail' })` from the shared client. The mod never calls `fetch` and never spawns `engram`.
- **The first request after opening** sends no cursors and `scope`. It adopts `head`, `now`, `events.epoch` and `events.head` as cursors and renders nothing from it.
- **Later requests** send all four cursors plus `scope`.

**Stale-open ceiling.** There is no pane-closed event. A pane closed by hand keeps polling until `/engram-tail` is run or the session ends. Comment this in the code.

### 6.6 Merge, order, dedupe, render

**Ring.** One ring of **200** rows, keyed `w` / `r` / `e` / `c`. An existing key is replaced, never duplicated.

**Sort key**, descending: `(time_ms, rank, id)`.

| Row | `time_ms` | rank |
|---|---|---|
| write | `created_at × 1000` | 0 |
| retraction | `retracted_at × 1000` | 1 |
| event | the record's `timestamp` | 2 |
| call row | the hook clock | 2 |

The tie order exists because telemetry is appended after the write it reports, so within one second the activity sits above its write. Late arrivals are inserted at their sorted position.

**Markers:**
- `head < after` → the marker `store rewound — showing writes from f<head>`, and set `after = head`.
- Epoch changed → the marker `event feed restarted`; adopt the new epoch and head.
- `skipped > 0` → one marker, `… N more <writes|events> not shown`.

**Trim** to 200 by the lowest sort key.

**"This session" marker `•`.** Shown on a row when `this_session` is true, or when its handle is in §6.4's handle set. The mark means *known* to be this session; an unmarked row may still be (D43). Nothing is dimmed.

**Row shapes.** *(r6: the **content** below still holds. **Layout, order within a row, and degradation at narrow widths** are now governed by §6.9, which supersedes this table wherever the two differ.)* The label is always the exact kind, origin or tool name:

| Row | Shows |
|---|---|
| write | handle, time, origin label, body on one line clipped to **120 characters** plus `…` (one constant in `plugin/mods/tail/model.ts`, also used for retraction bodies, call-row statements and queries), `← fN` when `replaces`, `(retracted)` when not live or retracted later. *(r5, Reviewer F5: lens has no clip helper.)* |
| retraction | handle, time, `retract`, the retracted fact's origin, body, `— <reason>` |
| `recall` event | `"<query>"`, `fact_count` facts, `coverage` |
| `mod-call` event | `mod · tool`; for `tool = recall` also `"<query>"` and `coverage`. **No fact count** (D76) |
| `session-start` / `subagent-start` | `long_term_fact_count` facts, `tokens_returned` tokens, `agent_type` for subagents |
| any other event | the kind plus whichever of `query, tool, path, phase, repo, relation, decision, mode` are present, in that order |
| call row | §6.4 |

**Time.** Local zone, `HH:mm:ss` via lens's `clock`. Dates other than today use lens's `stamp` (`yyyy-MM-dd HH:mm:ss`). Do not write a formatter.

### 6.7 Filter control

*(r6: the **control** is now the disclosure described in §6.9.2, which replaces the one-row button strip. The groups, their members and their defaults below are unchanged.)* Filtering is client-side, the state lives in the `tail` atom, and the server returns every kind in scope. Groups exist only for toggling; a row's label is always its own kind.

| Group | Members | Default |
|---|---|---|
| Writes | write rows | on |
| Retractions | retraction rows; `forget` call rows | on |
| Reads | `recall`, `browse`, `expand`, `timeline`, `navigate`, `judge` (events or call rows) | on |
| Remember | `remember`, `revise`, `user-prompt`, `pin`, `unpin` | on |
| Mods | `mod-call` | on |
| Sessions | `session-start`, `subagent-start`, `session-open`, `pre-compact`, `post-compact`, `memory-guard`, `lookup-nudge`, `tool-observed`, `file-touched` | **off** (Q6) |
| Maintenance | `index`, `index_repo`, `embedding`, `sync`, `enrollment`, `report`, `server-start`, `server-stop`, `digest` | **off** (Q6) |
| Other | any kind or tool not listed | on |

### 6.8 Failure behaviour

The tick never throws (`shared/guard.ts` pattern) and always reschedules unless stopped. Rows are kept through every failure, a success clears the status line, and **errors never toast**.

| Client reason | Status line | Loop |
|---|---|---|
| `server-down` | `Engram server not reachable — /engram:start` | 15 s |
| `timeout` | `Engram server slow to answer` | 15 s |
| `not-initialised` | `Engram home not initialised — engram init` | 15 s |
| `not-found` | `the running Engram server predates the memory tail — update, then /engram:restart` | **300 s** (r8; was stop) |
| `unsupported` | `the running Engram server has no mod API` | **15 s** (r11; r8 300 s; was stop) |
| `bad-request` | `memory tail request rejected: <detail>` | 15 s |
| `error` | `memory tail error` | 15 s |
| success with `events: null` | `activity feed unavailable — showing writes only` | 2 s |

*(r6: the status **texts** displayed are the short forms in §6.9.3. The reasons and the loop behaviour above are unchanged.)*

An unknown op returns 404 `not_found`, which the shared client maps to `not-found`. `tail` never returns `not_found` for anything else, so here that reason means "op missing". Do not change the client.

### 6.9 Presentation at narrow widths (r6)

This section is presentation only. Data, cursors, scope, groups, filter defaults, sort order, ring size and the failure loop are unchanged.

**What the host offers** (engine types `plugin/.claude-plugin/types/claude-code/index.d.ts`, stamped 2.1.295):
- **Elements:** `Box`, `Text`, `Button`, `Input`, `Select`, `Link`, `Code`, `Markdown`, `Client`, `Raster`, `Image`.
- **Box props** include `flexDirection`, `flexWrap`, `minWidth` and `overflow`.
- **`Text` takes `wrap`.**
- **No popup, menu, modal, overlay or collapsible element.**
- **No pane width reader.** `UiPane` has no width, and `columns` exists only on a `Client` module's region. The `bodyColumns` the brief mentions does not exist.

So the mod **cannot compute a layout for a given width**. The design has to be width-agnostic: every line is either something that fits the narrowest pane (24 columns), or a `Text` the host truncates (`wrap="truncate-end"`), with the information laid out most-important-first so truncation always removes the least important part.

#### 6.9.1 Rows: a fixed head line and an optional detail line

Every row renders as up to two `Text` lines in a column, **both `wrap="truncate-end"`**, so the host never wraps a row mid-token.

**Head** (always present). Its exact shape is `<session mark><state mark> HH:mm:ss <label>[ <qualifier>]`. The two marks are **adjacent**, with no space between them, and a single space separates each later field. That is what makes the fixed prefix 12 characters. *(r7: r6 said "single spaces between", which contradicted the 12.)*

| # | Field | Width | Values |
|---|---|---|---|
| 1 | session mark | 1 | `•` known this session (§6.6), else a space |
| 2 | state mark | 1 | `✗` for a retraction row or a write row now retracted, else a space |
| 3 | time | 8 | `HH:mm:ss` via lens's `clock`; always today's form (see day separators) |
| 4 | label | whole | The exact kind, origin or tool name, **never cut or abbreviated by the mod**. Every known label is ≤ 14 characters. An unknown longer kind renders whole and the host's `truncate-end` cuts the line. *(r7 ruling: a mod-side cut would display a label that is not the stored kind, against D18/D43's exact-label rule, while a host cut shows its `…`.)* |
| 5 | qualifier | rest | only where present: `← fN` (write that replaces), the retracted fact's origin (retraction) |

At 24 columns the fixed prefix (marks, spaces, time) is 12 characters. Labels of up to 11 characters always show whole. A 12-character label shows whole only when no qualifier follows. Longer labels such as `subagent-start` lose their tail. At 80 and 120 the whole head shows.

**Detail** (only when there is something to say; a kind-only event such as `remember`, `user-prompt` or `pre-compact` renders **head only**, never a blank line). Short, fixed-format fields come first and free text comes last:

| Row | Detail, left to right |
|---|---|
| write | `fN`, then body |
| retraction | `fN`, then body, then `— <reason>` |
| `recall` event or call row | `<count> facts`, then `· <coverage>`, then `"<query>"` |
| `mod-call` event | `<mod> · <tool>`, then for `recall` `· <coverage>`, then `"<query>"` (no count, D76) |
| `remember` / `revise` call row | `fN` (if known), then the statement |
| `forget` call row | `fN` |
| `session-start` / `subagent-start` | `<n> facts · <m> tok`, then `agent_type` |
| any other event or call | present fields in §6.6's fixed order, as `value` joined by ` · ` (values only, no keys) |

At 24 columns a recall keeps `7 facts · high` and loses the query. A write keeps its handle and the start of its body.

**Clip.** The 120-character `CLIP` (r5) stays as a **payload cap** applied to each free-text field before layout. It is not a layout width: the host's `truncate-end` cuts to the real pane width, which the mod cannot know.

**Qualifier encoding** *(r7, confirmed as built).* The `TailRow` type in `plugin/mods/shared/state.d.ts` carries an optional `qualifier` string: `← fN` for a write that replaces, or the retracted fact's origin for a retraction. The head is built from it, kept separate from the detail text. This is a second permitted edit to the `tail` key's types besides `filterOpen`.

**Day separators.** The time column is always `HH:mm:ss`. Between two adjacent displayed rows whose local dates differ, and above the first row when its date is not today, render one separator line: `── yyyy-MM-dd ──`. The date is the first 10 characters of lens's `stamp`; do not write another formatter. Separators are presentation only: not in the ring, not counted, never filtered. *(r7 ruling: they apply to marker rows too, such as `event feed restarted`, `store rewound` and `… N more`. That is intended: markers carry a time and sort like any row, and a marker on a new day belongs under that day's separator.)*

**Rejected:**
- *A single line with the body after the head.* At 24 columns the head alone fills the line and the body disappears.
- *Host word-wrap for bodies.* An unbounded number of lines per row, and the row count stops being predictable (the D52 lesson).
- *A `Client` module to read `columns`.* A second rendering model for one number.
- *Evidence-gated upgrade E14:* one-line rows on wide panes via `flexWrap` + `minWidth`.

#### 6.9.2 Filter: one button that discloses a vertical list

**Collapsed (default).** The header is one line containing one `Button`, labelled `Filter <on>/<8> ▾`, for example `Filter 6/8 ▾`. That is at most 14 characters, so it fits 24 columns with the button's own frame.

**Expanded.** Pressing the button toggles a `filterOpen` flag in the `tail` atom. The button shows `▴` while open. Directly beneath it, a `Box flexDirection="column"` holds the eight group toggles, **one `Button` per line**, labelled `✓ <Group>` or `· <Group>`.
- The longest label is `✓ Maintenance`, 13 characters.
- Pressing a toggle flips that group and keeps the list open.
- Pressing `Filter … ▴` collapses it.
- Rows render below the list while it is open.

**Why this over the alternatives:**
- *Jim's popup idea.* The host has no popup or menu element. The only candidate is `Select`, whose presentation (a closed dropdown or an inline list) and repeated-select behaviour are unconfirmed (**E11**). The disclosure needs only `Box`, `Text` and `Button`, which the tail already uses, so it is the build.
- *If E11 shows `Select` renders as a closed popup and accepts repeated selections,* an Architect amendment may replace the expanded list with one `Select` whose options are the eight `✓/·` labels and whose `onSelect` toggles one group. The collapsed button and the atom state stay.
- *Preset cycling* (All / Writes only / …) was rejected: it loses per-group control, which Q6's defaults presuppose.
- *A second pane* was rejected: its placement and size are unknown, and it would need its own open/close tracking. That is the stale-open problem twice.

`filterOpen` defaults to `false`, lives in the `tail` atom (`plugin/mods/shared/state.d.ts`), and survives as long as the atom does (E3).

#### 6.9.3 Status and empty states

These render on their own line under the header, as `Text wrap="wrap"`. Statuses and empty states are the **only** elements allowed to wrap, because each must be readable whole. *(r7: r6 said only the status could wrap; the build wraps both, and that is correct.)* To make host word-wrap safe, every token (space-separated) in both is **at most 20 characters**, and in a status the action comes first:

| Reason | Text |
|---|---|
| `server-down` | `Server down · /engram:start` |
| `timeout` | `Server slow · retrying` |
| `not-initialised` | `Not initialised · engram init` |
| `not-found` | `Server too old for the tail · update, then /engram:restart` |
| `unsupported` | `Server has no mod API` |
| `bad-request` | `Tail request rejected:` then the server's `detail` cut to 60 characters |
| `error` | `Tail error · retrying` |
| `events: null` | `Activity feed off · writes only` |

Empty states (no rows to show):
- **Nothing recorded since opening:** `No activity yet.`
- **Rows exist but the filter hides all of them:** `All <n> rows hidden by the filter.` This is computed from the ring, so the user knows to open the filter.

The `bad-request` detail is server text and may hold a long token. It renders in its own `Text wrap="truncate-end"` on the next line rather than inside the wrapping status.

#### 6.9.4 Files (one commit, plugin `1.3.7`)

| File | Change |
|---|---|
| `plugin/mods/tail/model.ts` | (a) A pure row-layout that returns the head string and an optional detail string per §6.9.1. (b) The day-separator insertion over the displayed list. (c) The filter label (`Filter n/8 ▾/▴`). (d) The §6.9.3 status texts. (e) `CLIP` kept as the payload cap |
| `plugin/mods/tail/index.tsx` | The header renders the single Filter button. When `filterOpen`, a `Box flexDirection="column"` of eight toggle buttons follows, then the status line, then the empty state or the rows. Each row is a column `Box` holding the head `Text` and, if present, the detail `Text`, both `wrap="truncate-end"`. **Delete the interim `<Box flexWrap="wrap">` button strip from ce26162.** |
| `plugin/mods/shared/state.d.ts` | The `tail` key gains `filterOpen: boolean`, and `TailRow` gains optional `qualifier: string` (r7). Edit only the `tail` key's types |
| `plugin/mods/tail/tail.test.tsx` | Tests below. Update or remove ce26162's wrap-strip assertions, which assert the deleted layout |
| `plugin/.claude-plugin/plugin.json`, `.claude-plugin/marketplace.json` | Version `1.3.7` |

No server, C# or other mod file changes.

**The ce26162 interim fix is replaced.** Its wrapping button strip is deleted. Its `truncate-end` on row text is kept and subsumed by §6.9.1, which applies it to both lines.

#### 6.9.5 Tests (`plugin/mods/tail/tail.test.tsx`, `claude plugin test plugin`)

The test kit's ability to render at a set width is unconfirmed (**E13**). The guards are therefore asserted at two levels.

**(1) Pure layout at the three widths.**
- Feed the row-layout outputs through a test-local `truncateEnd(line, width)`, which cuts to `width − 1` characters plus `…`. That is a model of the host, used only as the test's ruler.
- Check at widths 24, 80 and 120.

**(2) The mounted Pane's element structure.**
- Which elements exist.
- Their `wrap` props.
- Their arrangement in rows and columns.

| # | Guard | Assertion | Falsification |
|---|---|---|---|
| W1 | Marks and time survive at 24 | For every row kind, `truncateEnd(head, 24)` starts with the two marks, a space and `HH:mm:ss` | Put the label before the time → red |
| W2 | Labels ≤ 11 survive at 24 | Every label of ≤ 11 characters, with a qualifier present, appears whole in `truncateEnd(head, 24)` (the ruler keeps a line of ≤ 24 whole and otherwise cuts to 23 + `…`) | Put two spaces between the time and the label, making the prefix 13 characters → W2 red, W1 (marks, space, time) stays green. *(r7: the r6 arm, "add a third mark column", reddens W1 as well, so it cannot tell the two guards apart.)* |
| W3 | Recall degrades to count and coverage | At 24, a recall's truncated detail contains `facts · <coverage>` and not the query | Put the query first → red |
| W4 | Write keeps its handle | At 24, a write's truncated detail starts with `fN ` | Put the body before the handle → red |
| W5 | Wide panes lose nothing | At 120, every head and detail built from fields at or under the CLIP equals its untruncated form whenever its length ≤ 119 | Cap the head at 24 characters in the model → red |
| W6 | No row line can wrap | The mounted Pane: every row line `Text` has `wrap="truncate-end"`; the only `wrap="wrap"` Texts are the status line and the empty state (r7) | Remove `truncate-end` from the detail → red |
| W7 | No blank detail | A `remember` event renders exactly one row line | Always render the detail Text → red |
| W8 | Filter collapsed by default and narrow | The header holds exactly one Button, label `Filter 6/8 ▾` under the default groups, at most 14 characters | Default `filterOpen: true`, or restore the strip → red |
| W9 | Expanded list is vertical | After pressing Filter: eight toggle Buttons, each the only Button in its own row (parent Box `flexDirection="column"`), each label ≤ 13 characters | Lay toggles out in a row → red |
| W10 | A toggle keeps the list open and updates the count | Press `· Sessions` → label `✓ Sessions`, header `Filter 7/8 ▴`, list still shown | Collapse on toggle → red |
| W11 | Status and empty-state tokens wrap safely | Every §6.9.3 status and empty-state text: each space-separated token is ≤ 20 characters (r7: empty states included) | Restore the r4 long text with a 21+ character token, or add one → red |
| W12 | Filtered-empty explains itself | Ring of 5 `index` rows (Maintenance off) → `All 5 rows hidden by the filter.` | Show `No activity yet.` → red |
| W13 | Day separator | Two rows on different local dates → exactly one `── yyyy-MM-dd ──` between them; none between same-day rows; separators are not counted in W12's `n` | Drop the separator, or emit one per row → red |
| W14 | CLIP is a payload cap, not a layout width | A 500-character body: the detail before host truncation is `fN ` + 120 characters + `…` | Remove the cap → red |

Existing M-tests keep **every data assertion**: which rows appear, cursors, dedupe, order, filtering, failure cadence and markers. Only their expected **string forms** change to §6.9's head and detail shape. *(r7, matching the build: r6 named only M11 and M12, but M4, M7, M8, M9, M11–M17 and M19 all asserted r5 line strings and were rewritten this way.)* A rewrite that drops or weakens a data assertion is a defect.

#### 6.9.6 NEEDS-EVIDENCE (live; none gate the build)

| ID | Question | Then |
|---|---|---|
| E11 | Does `Select` render as a closed popup that opens on activation, or as an inline list? Does `onSelect` fire again on the same value? | Popup plus repeated fire → amendment: `Select` replaces the expanded list |
| E12 | In a real pane, does `Text wrap="wrap"` break only at spaces? Does `truncate-end` mark the cut with `…`? | Mid-token breaks → amendment: status also becomes `truncate-end` with a short form |
| E13 | Can the plugin test kit mount a Pane at a given width and return the rendered lines? | Yes → add W-tests that assert real rendered lines at 24/80/120 alongside (1) |
| E14 | Does a row `Box flexDirection="row" flexWrap="wrap"`, with a non-shrinking head and a detail `Box minWidth={30} flexGrow={1}`, put the detail beside the head on wide panes and below it on narrow ones? | Yes → amendment: one-line rows on wide panes |
| E15 | Does `$.ui.open({ columns })` set or floor a docked pane's width? | Recorded only; the design does not depend on it |

### 6.10 Surviving a plugin reload (r8)

**Bug, from live use of 1.3.7.** A plugin hot reload while the pane is open leaves it frozen and silent. The host cancels pending `$.clock` waits and resets module variables, but keeps `$.state`. The loop (a `$.clock.after` chain whose `timer`/`generation` live at module level in `plugin/mods/tail/index.tsx`) dies. `TAIL.paneOpen` stays true. `session.start` re-fires but only honours `tail_auto_open`. The toggle reads `paneOpen`, so the first press closes the pane and only the second restarts polling. A live pane and a dead one both say `No activity yet.`

**Host facts this rests on** (host docs, read by the Implementor; confirm live as E3):
- `$.state` survives a hot reload.
- `$.clock` waits and module variables do not.
- `session.start` fires again after a reload.

#### 6.10.1 Two facts, two sources

The toggle and `session.start` decide from two facts and never from `paneOpen` alone.

| Fact | Source | Why |
|---|---|---|
| **shown**: the pane is on screen | `$.ui.panes()`: an entry with id `engram-tail` and `isPlaced: true`. If the call throws or rejects, fall back to the atom's `paneOpen` | The atom outlives the pane (a manual close has no event). `panes()` asks the host. Whether it reflects a manual close is E2 |
| **running**: a poll chain is alive in *this* module instance | A module-level boolean, set when the chain starts and cleared by `stop()` | It must live at module level precisely so a reload resets it. That is what makes it truthful after the host kills the timers |

#### 6.10.2 Decision table

One pure function in `model.ts` maps (trigger, shown, running) to an action. `index.tsx` performs the action.

| Trigger | shown | running | Action | Command reply |
|---|---|---|---|---|
| `session.start` | yes | no | **resume** | — |
| `session.start` | no | — | `stop()` the chain if one is running (r11), set `paneOpen = false`; then, if `tail_auto_open`, open and start as today | — |
| `session.start` | yes | yes | nothing | — |
| `/engram-tail` | yes | yes | close the pane, `stop()`, `paneOpen = false` | `Memory Tail closed.` |
| `/engram-tail` | yes | no | **resume** | `Memory Tail resumed.` |
| `/engram-tail` | no | — | open, start (first request without cursors, as today) | `Memory Tail opened.` |

**Resume** keeps the atom's cursors (`after`, `closed_after`, `event_epoch`, `event_after`), rows, filter and `filterOpen`. It inserts one marker row, `resumed`, at the current time, and starts the chain whose first request **sends the saved cursors**.

Resuming from the saved cursors, rather than resetting to head as the brief suggested, was chosen because the atom survived. Those cursors are exactly the "no replay, no gap" state:
- Writes and retractions that landed during the reload arrive on the first poll. A burst over `limit` shows the usual `… N more` marker.
- The server's event epoch is unaffected by a plugin reload, so missed events still in the ring arrive too.
- If the server restarted meanwhile, the epoch differs and the existing `event feed restarted` marker fires.
- Rows already shown stay, and dedupe by key absorbs any overlap.

Resetting to head would drop exactly the activity Jim reloads to see.

#### 6.10.3 Heartbeat and the two empty states

**Heartbeat.** The atom gains `lastOkAt` (ms), set on every successful poll and only then.
- Whenever `lastOkAt` is set, a dim line `Updated HH:mm:ss` renders directly under the header (and under the filter list when it is open), above the status line.
- It is `Text dimColor wrap="truncate-end"`, 16 characters, within §6.9's 24-column rule. It never wraps.
- On failure it keeps its last value while the status line explains. A dead or failing loop therefore shows a time that stops advancing.
- The time uses lens's `clock`.

**Empty states** (§6.9.3), replacing the single `No activity yet.`:

| When | Text |
|---|---|
| no successful poll since the chain (re)started | `Connecting to Engram…` |
| at least one successful poll, nothing to show | `No activity yet.` |
| rows exist, all filtered out | `All <n> rows hidden by the filter.` (unchanged) |

Every token is ≤ 20 characters, and both texts are covered by W11. "Before the first successful poll" is a module-level fact that resets on reload, so a resumed pane says `Connecting…` until its first answer. The rows it already holds still render.

#### 6.10.4 A declined atom (`shape`)

The `tail` atom gains `shape`, a string-literal constant defined once in `model.ts`, with current value `tail-3`. Bump it whenever `TailState` changes incompatibly.
- On every read, an atom whose `shape` is missing or different is **declined**. It is treated as absent and replaced by a fresh state: no rows, null cursors, `filterOpen: false`, default filter, and `paneOpen` taken from `shown`.
- It is never read field by field and never migrated.
- If `shown`, the chain starts as a fresh open: no cursors, no backlog.
- This is the host types' recommended guard for an atom written by an older module version, which a reload makes routine.

#### 6.10.5 404s: visible, and not permanent

Today, `not-found` (the server lacks `tail`) and `unsupported` (the server lacks `/mod/v1`) stop the loop for good. Worse, `unsupported` sets the shared client's `shared.unsupported` flag, which is sticky for the session and survives a reload because it lives in `$.state`. That disables every mod's API even after the server is upgraded.

**Tail.** Neither reason stops the loop, and the status line (§6.9.3 texts) stays up until a success clears it.
- `not-found` (a real request each time) reschedules at **300 s**.
- `unsupported` reschedules at **15 s** (r11). While the shared back-off is unexpired those calls are short-circuited and cost nothing.
- Bounds:
  - After an upgrade-and-restart, the tail recovers within about **300 s + 15 s of the last 404 any mod received**, and that 404 is no later than the upgrade.
  - For `not-found`: within 300 s of the restart.
- No reload is needed.
- *(r11, Reviewer nit 2: r8's "within 5 minutes" was wrong for `unsupported`. Any mod's 404 re-arms the shared back-off, so a 300 s tail poll could land just inside a re-armed window and wait almost two windows, about 10 minutes.)*

**Shared client** (`plugin/mods/shared/client.ts`). The flag becomes a back-off that expires 300 s after it was set, the same pattern as the existing 60 s `noPortAt`.
- While unexpired, calls return `unsupported` without a request, as today.
- After expiry the next call goes to the network and re-arms the flag if the 404 recurs.
- Store the time it was set in `SharedState` (`plugin/mods/shared/state.d.ts`, `shared` key only).
- Every reader of `shared.unsupported` across `plugin/mods` goes through the client's own check, never the raw field. The Implementor finds them with `grep -rn "unsupported" plugin/mods`.

This is the one change outside the tail mod. It is justified because a 404 describes the server instance that answered, not the session, and recovery for every mod is the point.

#### 6.10.6 E2 and E3, now

- **E3 is answered by host docs for hot reload:** state survives, timers and module variables do not. §6.10 is built on that. What stays open is `/clear` and resume (does the atom persist across a session-id change?). Whichever way it falls, §6.10.2 handles it: a fresh atom means a declined or absent state, and a fresh open.
- **E2 is now load-bearing, but with a safe failure.** Does `$.ui.panes()` drop or unplace `engram-tail` after a manual close?
  - Yes: the stale-open ceiling (§6.5) goes away. `session.start` stops resuming closed panes, and the toggle opens rather than resumes.
  - No: a manually closed pane reads as shown, so the toggle **resumes** instead of reopening. The reply `Memory Tail resumed.` then names what happened, a second press closes, and a third opens. Record which in D79.

#### 6.10.7 Files (one commit, plugin `1.3.8`)

| File | Change |
|---|---|
| `plugin/mods/tail/model.ts` | The decision table (§6.10.2). The `shape` constant and the decline rule. The heartbeat text. The two empty texts. The 300 s cadence for `not-found`/`unsupported` |
| `plugin/mods/tail/index.tsx` | Module-level `running`. `shown` via `$.ui.panes()` with the atom fallback. `session.start` and the command perform the table's action and reply. The resume marker. Set `lastOkAt` on success. Render the heartbeat line. Connecting vs watching empty state |
| `plugin/mods/shared/state.d.ts` | `tail` key: `shape`, `lastOkAt`. `shared` key: the expiring `unsupported` timestamp, replacing the boolean |
| `plugin/mods/shared/client.ts` | Expiring `unsupported` (§6.10.5) |
| `plugin/mods/shared/client.test.ts` | C1 |
| any other `plugin/mods/**` reader of `shared.unsupported` | Read through the client's check |
| `plugin/mods/tail/tail.test.tsx` | R1–R9. M13 updated (300 s, not stop) |
| `plugin/.claude-plugin/plugin.json`, `.claude-plugin/marketplace.json` | `1.3.8` |

No server or C# change.

#### 6.10.8 Tests

| # | File | Assertion | Falsification |
|---|---|---|---|
| R1 | tail.test.tsx | Decision table, all six rows, as a pure function | Decide the toggle from `paneOpen` instead of `running` → (toggle, shown, not running) yields close → red |
| R2 | tail.test.tsx | Mounted: open the pane (polls run). Simulate a reload: cancel the chain the way the host does and reset the module's `running`, keeping the atom. Fire `session.start` with `panes()` reporting the pane placed → polling resumes at 2 s, and the first resumed request carries the saved `after`/`closed_after`/`event_epoch`/`event_after` | Reset cursors on resume → the request omits `after`, red. Resume nothing on `session.start` → no fetch, red |
| R3 | tail.test.tsx | Same, but via `/engram-tail` with the pane placed and nothing running → reply `Memory Tail resumed.` and polling resumes; the pane is not closed | Close on that press → red |
| R4 | tail.test.tsx | Resume keeps the ring's rows and inserts exactly one `resumed` marker | Clear rows, or insert one marker per poll → red |
| R5 | tail.test.tsx | Heartbeat: after a success, `Updated HH:mm:ss` renders as `dimColor` and `truncate-end`, ≤ 16 characters. It advances on the next success and is unchanged after a failure while the status renders | Update `lastOkAt` on failure → red |
| R6 | tail.test.tsx | `Connecting to Engram…` before the first success, `No activity yet.` after; both in W11's token check | Single text → red |
| R7 | tail.test.tsx | An atom with `shape` missing or `tail-2` is declined: no old rows render and the first request sends no cursors | Read the old atom → the old cursor is sent, red |
| R8 | tail.test.tsx | `not-found` and `unsupported` keep the loop: next fetch at 300 s, status persists, a later success clears it | Stop on either → red |
| R9 | tail.test.tsx | `session.start` with `panes()` rejecting falls back to `paneOpen` | Treat a rejection as not shown → no resume, red |
| C1 | client.test.ts | After an unsupported 404, calls short-circuit for 300 s, then the next call reaches the fake server; a recurring 404 re-arms the flag | Keep the boolean sticky → no request after 300 s, red |

**If the test kit cannot simulate a reload** (cancelling waits while keeping state), R2's mounted half becomes **E16**. In that case R1, R3 and R4 still hold the logic, since R3 needs only a module that never started its chain plus a placed pane.

| ID | Question | Then |
|---|---|---|
| E16 | Can the plugin test kit simulate a hot reload: cancel `$.clock` waits, reset module state, keep `$.state`? | No → R2's mounted half is checked live once, and the result is recorded in D79 |

#### 6.10.9 Rules settled against the build (r9, plugin 21f0809)

These supersede the §6.10 text above wherever the two differ.

1. **Connecting line placement** *(confirmed as built)*.
   - While the current chain has had no successful answer: with no rows, `Connecting to Engram…` is the empty state; with rows (including the `resumed` marker), the same text renders as a line **above the rows**, below the heartbeat and status.
   - Same element rule as the empty state: `Text wrap="wrap"`, tokens ≤ 20. W6's list of wrapping elements reads "status, empty state, connecting line".
   - Why: a resumed pane always has at least its marker row, so an empty-state-only rule could never show the line at all.
2. **A declined atom with a shown pane is a plain open, not a resume** *(text fixed to match the build)*. Declining (§6.10.4) happens first and yields fresh state. The decision table then sees nothing to resume, so the chain starts exactly like `/engram-tail` on a not-shown pane: no cursors, a first request carrying none, no `resumed` marker. The table's "shown + not running → resume" row applies only to an accepted atom.
3. **Open on a not-shown pane starts a fresh view** *(ruling: **code change**)*.
   - `open()` (the not-shown row of the table, and auto-open) **clears the ring's rows and `lastOkAt`**. It keeps the filter and `filterOpen`.
   - Resume keeps everything (§6.10.2).
   - Why:
     - Open resets the cursors to head, so any rows kept from an earlier viewing would sit directly above a silent gap, reading as continuous history that is not.
     - A kept `lastOkAt` would show an old `Updated` time as if current.
     - Q2 says opening shows new activity only, and M18 already discards call rows made while closed.
   - The build currently keeps both; change it.
4. **Shape mechanism** *(recorded as built)*. The atom's shape is the constant `TAIL_SHAPE` in `plugin/mods/tail/model.ts`, and the atom's type is declared as `Shaped<TailState>` in `plugin/mods/shared/state.d.ts`. That is the host types' own mechanism for a versioned atom, and nothing hand-rolled replaces it.
5. **Call-row recording follows `paneOpen`** *(confirmed as built)*.
   - §6.4's `tool.call` hook records call rows only while the atom's `paneOpen` is true and `tail_scope` is `session`.
   - `paneOpen` becomes true on open and resume. It becomes false on close, and when `session.start` finds the pane not shown.
   - It deliberately does **not** gate on `running`. Between a reload and the re-fired `session.start` the chain is dead but the pane is up, and calls made then must appear once the pane resumes. That is the same no-gap promise resume makes for server data.
   - If E2 shows `panes()` misses a manual close, recording continues into an invisible pane's ring. That is bounded at 200 rows and harmless.

6. **M18 is no longer a guard on the `tool.call` gate** *(r10 ruling; no code change)*. Since item 3, `open()` clears rows, so M18 (call rows made while closed do not appear on open) passes whatever the hook records. Its claim stays true, but it now overlaps R10.
   - The `paneOpen` gate is guarded by **R11's closed case**, which inspects the pane without opening it, so the clear cannot mask anything. **R2** also guards it, via resumed call rows.
   - M18 stays as a behaviour test, and its falsification entry is withdrawn.
   - Reshaping M18 to look at the ring before an open was rejected. It would duplicate R11's closed case, which is two tests for one guard, the kind of pair where either can be deleted with the suite still green.
7. **M4 rewritten** *(recorded)*. Its last assertion ("a pane opened again after being closed … keeps what it drew") encoded the behaviour item 3 replaced. It now asserts fresh cursors **and** a cleared view. That is the same rule as §6.9.5: data assertions are kept unless the spec changed the data behaviour, and here it did.

#### 6.10.10 Reviewer nits on r8/r9 (r11)

1. **`session.start` on a not-shown pane also `stop()`s a running chain** *(confirmed; **code change**)*. If E2 is yes, a manual close followed by `/clear` re-fires `session.start` in the same module instance. The code then *knows* the pane is gone, and must not keep polling it every 2 s until the next toggle. There is no data effect, because `open()` resets anyway.
   - File: `plugin/mods/tail/index.tsx` (the not-shown branch of `session.start`).
   - Test **R12**: with a chain running and `panes()` reporting the pane absent, firing `session.start` leaves no `tail` fetch over the next 60 s. Falsification: drop the `stop()` → fetches continue, red.
2. **`unsupported` polls at 15 s** *(choice; **code change**)*. See §6.10.5.
   - Why 15 s over restating the bound as "≤ 10 minutes": short-circuited calls send nothing, so the faster cadence is free and brings recovery back to the back-off plus 15 s after the last 404.
   - **Precondition:** the shared client's unexpired-`unsupported` check comes **before** port lookup, so a short-circuited call neither fetches nor spawns `engram status`. If the built order spawns first, fix the order in `plugin/mods/shared/client.ts` rather than slowing the cadence.
   - Files: `plugin/mods/tail/model.ts` (cadence), `plugin/mods/shared/client.ts` (only if the order is wrong).
   - Tests:
     - **R8** updated: `unsupported` → next fetch attempt at 15 s, `not-found` → 300 s. Falsification: one shared 300 s → red.
     - **C1** extended: during the back-off a call records **zero** `fetchCalls` **and zero** `processCalls`. Falsification: resolve the port before the check → `processCalls` > 0, red.
3. **`stop()`'s `running = false` gets a guard** *(test only)*. **R3** gains a third toggle. The kit's static `panes()` still reports the pane placed, which models E2 = no, so after "closed" the third press must reply `Memory Tail resumed.`, not `Memory Tail closed.` Falsification: delete `running = false` from `stop()` → the third press replies "closed", red.
4. **Clearing `running` when the reschedule itself throws** *(declined)*. If `$.clock.after` throws, the chain ends with `running = true`. The next toggle then closes a dead pane, and the press after reopens it with a working chain. A reload also resets the module.
   - Recovery is two presses with no data effect, the failure is unobserved and improbable, and a guard for it could only be tested by forcing the host clock to throw.
   - Reopen if it is ever seen.

Files for this round: `plugin/mods/tail/index.tsx`, `plugin/mods/tail/model.ts`, `plugin/mods/tail/tail.test.tsx` (R3, R8, R12), `plugin/mods/shared/client.test.ts` (C1), and `plugin/mods/shared/client.ts` only if the check order is wrong. Plugin version **1.3.10** in `plugin/.claude-plugin/plugin.json` and `.claude-plugin/marketplace.json`.

Files for item 3: `plugin/mods/tail/index.tsx` (`open()` clears rows and `lastOkAt`), `plugin/mods/tail/tail.test.tsx` (R10, R11), and plugin version `1.3.9` in `plugin/.claude-plugin/plugin.json` and `.claude-plugin/marketplace.json`.

| # | Assertion | Falsification |
|---|---|---|
| R10 | Open, receive rows, close, reopen → no earlier rows render, no `Updated` line until the first success, filter and `filterOpen` unchanged. Resume (R4) still keeps rows | Keep rows on open → red. Clear rows on resume → R4 red |
| R11 | With `paneOpen` true and the chain not running (simulated reload), an `engram_remember` tool call records a call row that renders after resume. With `paneOpen` false it records none | Gate on `running` → the row is missing, red. Ignore `paneOpen` → a row is recorded while closed, red. *(r10: M18 no longer reddens here; see item 6.)* |

## 8. Options

| Option | Type | Default | Meaning |
|---|---|---|---|
| `tail_auto_open` | boolean | `false` | Open the pane at session start |
| `tail_scope` | `"session"` \| `"all"` | `"session"` | `session`: Claude-keyed writes, retractions and events, plus this session's MCP calls observed in-process. `all`: everything from every session, the CLI and the server, from the server only |

Cadence, ring sizes, slack and filter defaults are constants, not options.

## 9. Relation to Lens and toasts

Lens shows the facts a recall returned. The tail shows a recall as one row among all the other activity.

The tail reuses lens's `clock`, `stamp` and `parseDigest`, and edits no lens file.

toasts is unchanged.

## 10. Invariants and negative cases

Must not change:
- **Hooks.** No hook verb changes. `file-touched` never opens the DB, and `session-start`/`subagent-start` do no new work.
- **Reader.** Exactly one telemetry file reader per server process; none when there is neither a webhook nor tail demand. No file read inside the op.
- **Webhook.** Delivery semantics and `WebhookServiceTests.cs` are unchanged.
- **Other ops and telemetry.** The other seven ops and their telemetry are unchanged. No new telemetry kind, and no new `TelemetryRecord` field.
- **`Forget`.** Called without a session, it writes exactly today's row.
- **Schema.** Exactly one additive partial index.
- **Lens, digest and toasts.** Their files and outputs are unchanged.

Neighbouring inputs (S = `scope=session`, A = `scope=all`):

| Input | S shows | A shows |
|---|---|---|
| CodeIndexer inserts or forgets a code fact | nothing (`head` advances) | nothing |
| `engram invariant add` / `remove --apply` | nothing (no session) | `invariant` write / `retract` |
| `engram directive` / `remove` | nothing | `directive` / `retract` |
| user-prompt capture | `capture` write + `user-prompt` event | same |
| `/engram-undo-capture` (mod `forget`) | `retract` + `mod-call` (toasts · forget) | same |
| model `engram_forget` | `forget` call row (marks the write retracted) | `retract` (no event: no kind exists) |
| model `engram_remember` | `remember` call row with handle and statement | `note` write + `remember` event, adjacent |
| model `engram_remember` repeat (no new row) | `remember` call row with the existing handle | `remember` event only |
| model `engram_revise` / `Restate` | `revise`/`remember` call row | `revision` write (`replaces`) + event |
| model `engram_recall` | `recall` call row (query, count, coverage via `parseDigest`) | `recall` event |
| subagent's Engram MCP call | call row (`tool.call` fires for subagents, as lens shows) | event |
| forget then verbatim recapture | `retract` then `capture` | same |
| digest auto-save | `digest·auto` write + `mod-call` (digest · remember) | same |
| PostCompact harvester | `compaction` write; `post-compact` event (Sessions group, off by default) | same |
| sync close with an unsynced successor | not shown | not shown (ceiling) |
| `backup replay` of 5,000 facts | — | newest 20 + skipped marker |
| `embedding` record (`session_id "server"`) | — | Maintenance (off by default) |
| the `tail` poll itself | never | never |
| lens `history` lookup | none | none |
| server restart while open | cursors survive; event marker | same |
| pane closed, MCP calls made, pane reopened | no rows from the closed period | — |

*(r8: no reason stops the loop any more. `not-found` and `unsupported` slow it to 300 s, per §6.10.4.)*

## 11. Tests (each guard falsified: break it, see red, restore; falsify against a committed tree and check `git diff --quiet`, per D60)

### Tier 2: `tests/Engram.Integration.Tests/`

| # | File | Test | Falsification |
|---|---|---|---|
| S1 | ModApiTests.cs | One fact per writer from §10 plus a code fact: correct origins; code fact absent but counted in `head` | Drop `regenerable = 0` |
| S2 | ModApiTests.cs | Forget then recapture reads `capture`; `Restate` reads `revision` with `replaces` | Rule 4 changed to "an earlier row exists" |
| S3 | ModApiTests.cs | First read returns no rows in any list, plus `head`, `now`, `epoch`, `events.head` | Return a backlog |
| S4 | ModApiTests.cs | 60 writes, limit 50 → newest 50 descending, `skipped: 10` | Page oldest-first |
| S5 | ModApiTests.cs | `scope` omitted = `session`; `all` adds the other session's rows with `this_session: false` | Default to `all` |
| S6 | ModApiTests.cs | Mod `forget` → retraction under `session`; a null-session forget appears only under `all` | Ignore `supersession.session_id` |
| S7 | ModApiTests.cs | A code-fact forget is not a retraction; a supersede is not a retraction | Drop the `regenerable` join; drop `new_fact_id IS NULL` |
| S8 | ModApiTests.cs | A retraction stamped `T-7` committed after a read at `closed_after = T` comes back on the next read | Slack 0 |
| S9 | ModApiTests.cs | `EXPLAIN QUERY PLAN`: rowid seek; `ix_supersession_retracted`; an index for `replaces`; no `SCAN fact`/`SCAN supersession` | (i) Drop `ix_supersession_retracted` in the fixture. (ii) Remove `INDEXED BY` → plan red. (iii) Remove the unary `+` → plan red. If (ii) or (iii) leaves S9 green, that construct is not load-bearing: drop it and correct D79 to match. |
| S10 | ModApiTests.cs | `tail` writes no telemetry (file size unchanged across a success, a 400 and a first read) | Add `RecordCall` |
| S11 | ModApiTests.cs | Each invalid field → 400 | Remove each check |
| S12 | ModApiTests.cs | `"tail"` added to every per-op guard theory (`[InlineData]`) | Existing guard falsifications redden the `tail` row; remove the Content-Type check once |
| S13 | ModApiTests.cs | Events: same epoch → `seq > after`; other epoch → none; overflow past 1024 → exact `skipped`; `session` filters `record.session_id`; no feed → `events: null` | Return rows on epoch mismatch; ignore scope |
| S14 | SchemaMigrationTests.cs | A store **genuinely lacking** the index (drop it first) migrates 16 → 17, snapshots first, and gains it | A no-op migration (the fixture must lack the index; D60) |
| S15 | FactStoreTests.cs | `Forget` with no session writes today's row (`session_id` null); with a session, stamps it | Drop the stamping |
| S16 | ModApiTests.cs | Mod `forget` stamps the request's Claude session row | Pass null |
| F1 | new feed test file beside WebhookServiceTests.cs | No URL and no demand → the reader is never created or opened (observe through a seam, not timing) | Start it unconditionally |
| F2 | same | Demand starts the reader at EOF; appended records reach the ring; demand lapsing >10 s stops it; the next demand gets a new epoch | Never stop; reuse the epoch |
| F3 | same | Webhook plus demand share **one** reader: one open of `telemetry.jsonl` per poll | Give the ring its own `TelemetryTail` |
| F4 | same | A hanging subscriber does not delay ring delivery | Deliver before appending to the ring |
| F5 | WebhookServiceTests.cs, TelemetryTailTests.cs | Unmodified and green | — |

Use a real barrier, not the return of `StartAsync`, before writing records (D55). Update any test that pins schema version 16 (search `SchemaMigrationTests.cs`, `DiagnosticsTests.cs`, `tests/Engram.EndToEnd.Tests/DoctorCommandTests.cs`).

**MCP forget stamping.** Add a test wherever `engram_forget` is already tested (find it with `grep -rln "engram_forget\|Forget(" tests`). It asserts the transport session row is stamped. Falsification: pass null.

### Tier 3: `tests/Engram.EndToEnd.Tests/ModApiE2ETests.cs` (published binary)

- `AllSevenOps_…` becomes eight. `tail` returns its shape over HTTP and adds no telemetry line. Filter by kind; never count total lines (D56).
- Against a real `serve`: a `tail` first read, then `engram timeline` (a CLI telemetry writer), then a second read shows the event under `all`.
- Host 403 and missing `X-Engram-Mod` 400 for `tail`.
- `NoReflectionJsonTests` stays green. Falsification: remove one source-gen registration and the AOT shape test fails.
- Read the skip count: a skipped tier 3 is not a pass.

### Plugin: `claude plugin test plugin` from the repo root (`plugin/mods/tail/tail.test.tsx`, `installFakeEngine`, `mock.clock`)

| # | Test | Falsification |
|---|---|---|
| M1 | The composer loads with the tail registered; `composer.test.ts` asserts `engram-tail` is registered (`toContain('engram-tail')`) | Remove `tail(on, options)` from `plugin/hooks/register.tsx` → red. *(r5, Reviewer F4: the earlier "register without a matcher" falsification could not fail, because the engine refuses only a **second** matcher-less registration.)* |
| M2 | Pane never opened → zero `tail` fetches over 60 s | Start the loop on `session.start` |
| M3 | Open → one fetch every 2 s; close → none after | Drop the cancel |
| M4 | First fetch sends no cursors; the fake returns rows anyway and none render | Render first-fetch rows |
| M5 | Second fetch sends `after = head`, `closed_after = now`, `event_epoch`/`event_after` | Send the last row id; omit `event_epoch` |
| M6 | Default `scope: "session"`; the option sends `"all"` | Hard-code `"all"` |
| M7 | Overlapping rows across polls (retraction window, same `seq`) render once | Append without dedupe |
| M8 | Order: event at S.5 above write at S; retraction at S above write at S; event at S.0 above write at S | Rank events below writes |
| M9 | A retraction, or a `forget` call row, marks a known write `(retracted)` | — |
| M10 | Epoch change → marker, no replay; `head < after` → rewound marker | Keep the old epoch |
| M11 | Filter defaults: Sessions and Maintenance off, the rest on; toggling Reads hides `recall` only; unknown kind `"zzz"` → Other, on | Default Sessions on; drop unknown kinds |
| M12 | Row shapes: `recall` event shows query/count/coverage; `mod-call` recall shows no count; generic fields in order | Render a count on `mod-call` |
| M13 | Failure table: each reason → its line and cadence; `not-found` stops; `events: null` keeps 2 s | Treat `not-found` as transient |
| M14 | `digest` / `digest·auto` come from the imported constants | — (the Reviewer checks there is no copy) |
| M15 | Today renders `HH:mm:ss`; yesterday renders dated | — |
| M16 | `skipped > 0` → one marker; ring capped at 200 | Remove the trim |
| M17 | `scope=session`, pane open: an `engram_remember` tool call → a `remember` call row with the input statement and the `[fN]` handle from the result; `engram_recall` → count and coverage via `parseDigest`; the handle gets the `•` marker on a later write row | Skip the handle parse; record call rows under `all` (duplicates the event) |
| M18 | Call rows made while the pane was closed do not appear on open | Keep them |

Then run `scripts/plugin-typecheck.sh`. Exit 2 means the engine types are missing: a skip, not a pass.

## 12. Docs

- **`docs/engram-implementation-plan.md`.** Add **D79 — Live memory tail** (D78 is currently the last). It covers:
  - the three sources plus the in-process call rows, and why each;
  - the transport-vs-Claude session finding (0/73 vs 41/41) and why scope splits on it;
  - the no-telemetry rule and the write-cursor proof;
  - the retraction slack derivation and its index;
  - the demand-gated reader and the starvation revisit;
  - Q5 refused, Q6 defaults;
  - push deferred (§5a);
  - post-build checks E1/E5/E6/E7, listed as pending with their decision rules, and filled in when measured.

  Amend D76's op list (eight ops) and D55 (the reader also runs on tail demand), each with a pointer to D79.
- **`CLAUDE.md`.**
  - Line 832 paragraph: the lookups list gains `tail`.
  - Line 712 paragraph (webhook): one sentence saying the one reader now also runs while a memory tail is polling (D79), and that "do not add a second reader" still holds.
- **`docs/engram-schema.sql`.** The partial index.

## 13. Post-build checks (run by the Reviewer or engram-1a after the build; they do not gate starting)

**Probe hygiene:**
- `T=$(mktemp -d)`, check it is non-empty, then `export ENGRAM_HOME="$T"` before any `./out/engram`.
- Copy the real store read-only with `sqlite3 ~/.engram/engram.db "VACUUM INTO '$T/engram.db'"`. Never `cp`.
- Git writes go through `git -C "$T/…"`.
- Kill any server you started; check `pgrep -fl engram`.

| ID | Measure | Then |
|---|---|---|
| E1 | `tail` loopback latency at 5,308 and 50,097 live facts: idle poll, first read, and a poll after `engram index --apply` adds ~45k code facts. Alternate the arms and calibrate same against same (D56). Report p50/p95 and server CPU | Idle p50 > 5 ms → Architect amendment (cadence 5 s). > 1 ms → consider §5a's fast path. Burst > 50 ms → Architect |
| E5 | On the `VACUUM INTO` copy: `supersession` rows, rows with `new_fact_id IS NULL`, and the retraction query time with and without the index | Record in D79 |
| E6 | Telemetry loss with the reader on vs off: 20 and 50 concurrent `file-touched` plus a retrying writer in a loop. Count `file-touched` records lost (spool losses must be 0) and retrying-kind records lost | Retrying-kind loss > 0 → Architect. Otherwise record it in D79 and CLAUDE.md |
| E7 | Event rate at idle and under an index burst plus the IndexFreshness loop: records/s, reader lag behind EOF (cap 128/s), pane rows/s under `all` | Sustained lag > 5 s → Architect |
| E2 | Live session: does `$.ui.panes()` stop listing `engram-tail` after a manual close? | Yes → amendment: the tick checks it |
| E3 | Do `$.state` atoms survive a module reload, `/clear` and resume? | Document; a reset gives cursors at head, which is acceptable |
| E4 | Does a pane follow its bottom edge? | Yes → amendment: newest last |
| E8–E10 | §5a | Future work only |

## 14. Closed questions

- **Q1** Scope default: `session`. Jim.
- **Q2** Backlog: none. Jim.
- **Q3** Retractions: shown. Jim.
- **Q4** Provenance: computed, no column. Jim.
- **Q5** No remember↔fact join and no handle in telemetry; D76 is unamended. Jim. Under `scope=session` the call rows carry the handle anyway, from the tool result, exactly.
- **Q6** Filter starts with Maintenance and Sessions off, everything else on. Jim.

## 15. Build order (each step is one commit and leaves the tree building and green)

**1. Schema index.**
- Files: `src/Engram.Core/EngramDatabase.cs` (`SchemaVersion` 16 → 17; a new `if (from < 17)` step after the one at `:512-529` that creates `ix_supersession_retracted` and writes `schema_version` "17"; the snapshot is already automatic), `docs/engram-schema.sql`.
- Tests: S14 in `SchemaMigrationTests.cs`; update every version-16 pin.

**2. `Forget` session stamping.**
- Files: `src/Engram.Core/FactStore.cs` (both `Forget` overloads gain an optional session row id), `src/Engram.Cli/EngramMcpTools.cs` (`engram_forget` passes its `McpSessionId`'s row), `src/Engram.Core/ModApi.cs` (`forget` passes the request session's row).
- Tests: S15 (`FactStoreTests.cs`), S16 (`ModApiTests.cs`), MCP forget stamping.

**3. Telemetry feed: one reader, ring, demand gate.**
- Files: a new Core type in `src/Engram.Core/` (reader, ring, epoch, demand gate), `src/Engram.Cli/WebhookService.cs` (consume the shared reader instead of owning one), `src/Engram.Cli/ServeCommand.cs` (register the singleton).
- No op uses the ring yet. Behaviour equals today: the reader runs only with a webhook.
- Tests: F1–F5.

**4. `tail` op and the origin function.**
- Files: `src/Engram.Core/ModApi.cs` (`Operations` += `"tail"`; the op; the origin function; `Execute` gains an optional feed parameter, and when it is absent `events` is null), `src/Engram.Core/ModApiModels.cs` (request fields, response records, `[JsonSerializable]` registrations beside `:113-117`), `src/Engram.Cli/ServeCommand.cs:294` (pass the feed).
- Tests: S1–S13 (`ModApiTests.cs`), tier 3 (`ModApiE2ETests.cs`); `ModApiRecallSeamTests.cs` still compiles unchanged.

**5. Plugin mod.**
- Files: the §6.5 table.
- Tests: M1–M18, `composer.test.ts`, `scripts/plugin-typecheck.sh`.

**6. Docs.**
- `docs/engram-implementation-plan.md` (D79, D76 and D55 pointers) and `CLAUDE.md` (lines 712 and 832), per §12.

**Stop and report, rather than decide, if:**
- any webhook test needs editing;
- the engine refuses the §6.4 `tool.call` registration;
- `engram_forget`'s `McpSessionId` cannot resolve a session row the way `SessionFacts.Append` does;
- a plan check (S9) shows a scan.

## 16. Risks

- **Events cursor.** It is per epoch. Never compare `seq` across epochs.
- **Writes cursor.** Advance it to `head`, never to the last row id.
- **Corpus scans.** `replaces` and retractions are the two places a naive query becomes a corpus scan. S9 is the guard.
- **When the one reader runs.** Changing this is the riskiest edit. F1–F5 guard it now, and E6 measures it after the build.
- **Two id spaces.** Call rows (§6.4) are how `scope=session` sees the model at all. A transport id must never be matched against a Claude Code id anywhere.
