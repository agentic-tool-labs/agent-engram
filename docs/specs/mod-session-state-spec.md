# Per-session mod state, and the sentinel's late invariants — spec

Status: FINAL, approved for build (2026-10-08). Author: Architect (peer request from engram-1a). Plugin `1.3.11` → **`1.3.12`**.

Implementer: implementor
Implementer: docs-writer (README.md only, after the code is built, §7)
Reviewer: reviewer

## TL;DR

- **"Session" means `$.session.id()`.**
  - The per-session fields of the Lens, toasts and sentinel atoms are valid only while the atom's stored `session` equals the current id.
  - A shared pure helper resets them lazily, at the moment of use, when the ids differ.
  - A plain reload keeps the id, so nothing resets. `/clear` changes it, so they reset.
- **`/engram-undo-capture` becomes scoped to this session** as a side effect: `shown` now holds only this session's toasted handles.
- **The sentinel remembers which invariant handles it has announced** to each agent for each path, and announces only the ones that agent has not seen. An invariant added later is therefore announced on that agent's next edit of the file.
- **The sentinel atom changes encoding**, so it gets a shape tag. The Lens and toasts changes are compatible, so they need none.

## 1. The two behaviours (confirmed in code, f7e5683 and 053917d)

**1. One-shot flags live for the saved state, not the session.**
- Lens `autoOpened` is set at `plugin/mods/lens/index.tsx:86-89` and initialised in `plugin/mods/lens/model.ts` `LENS_INITIAL`.
- toasts `shown` (`plugin/mods/toasts/index.ts:9`, `:78-86`) only grows.
- Neither ever resets. As a result:
  - `lens_auto_open` opens the Lens once per saved state;
  - the `/engram-undo-capture to forget` hint appears only while `shown` is empty;
  - `/engram-undo-capture` forgets `shown[last]`, which may belong to an earlier session.
- The sentinel's `seen` and `failedAt` (`plugin/mods/sentinel/index.tsx:10`) have the same defect. The main agent's key prefix is `''`, so a file it touched in an earlier session is never announced to it again after `/clear`. This is in scope (Jim, via engram-1a).

**2. The sentinel claims a file that has no invariants.**
- `plan()` claims `${agentId ?? ''}\0${path}` into `seen` inside `update()` (`sentinel/index.tsx:148-154`), *before* `if (!claimed || facts.length === 0) return undefined` (`:155`).
- A file touched while it has zero invariants is therefore claimed with nothing announced. An invariant added later is never announced to that agent.

## 2. What a session is

- A session is the value of `$.session.id()`.
- **Plain hot reload:** the id is kept, so **nothing resets**. The Lens is not reopened and the undo hint is not re-added.
- **`/clear`:** the host notes say the id changes, so per-session fields reset.
- **Resume:** whatever id the host hands back is handled by the same comparison.
- Whether `/clear` really changes the id, and whether resume keeps it, is **E17**. The design is correct under either answer (§9).

**Rejected: an unconditional reset in `session.start`.** `session.start` re-fires on every hot reload, so it would reopen the Lens and re-add the hint on each reload. That is precisely what a reload must not do.

## 3. Mechanism: one pure helper, applied lazily

A new pure function in `plugin/mods/shared/` (with its own test file there; the name is the Implementor's call):
- **Input:** an atom value, the current session id, and the initial values of that atom's per-session fields.
- **Output:**
  - the value unchanged when `value.session === id`;
  - otherwise the value with those fields set to their initial values and `session` set to `id`.
- It takes the id **as a value**, never `$`. The mod scanner refuses `$` passed across an import (`shared/guard.ts`).
- **It is applied lazily**, inside every `update()` or read that consults a per-session field, and not at `session.start`. A stale field is then never acted on, even if a `session.start` was missed or raced. The three mods share this one implementation, and no mod writes its own comparison.

| Atom | Per-session fields | Not per-session (unchanged) |
|---|---|---|
| `lens` | `autoOpened` | `recalls`, `history`, `selected`, `paneOpen`. `recalls` surviving `/clear` is a possible follow-up, out of scope |
| `toasts` | `shown` | — |
| `sentinel` | `seen`, `failedAt` | — |

## 4. Toasts and `/engram-undo-capture`

These follow from §3 with no extra logic:
- the hint appears on the first capture toasted in each session;
- `/engram-undo-capture` acts on the newest handle toasted in **this** session (`captures` already filters by `session_id`). With none toasted this session it replies `No captured memory to forget` and makes **no** `forget` call.

## 5. Sentinel: announced handles per (session, agent, path)

**Ruling: track the announced handles.** Neither of the two offered shapes was taken:
- *Claim only when facts > 0* leaves the same bug one step later: an invariant added after a file's first announcement is never announced.
- *Count-based re-announce* misses a replacement, where one invariant is removed and another added at the same count.

**Encoding.** `SentinelState.seen` becomes `Record<string, string[]>`.
- The key is unchanged: `${agentId ?? ''}\0${path}`.
- The value is the invariant handles already announced to that agent for that path.

**Behaviour on each Write/Edit** (`MultiEdit` is still ignored):
- Do the `path-facts` lookup every time. There is no early return on a known key, because only a lookup can reveal new handles.
  - This is the per-edit frequency D76 budgeted for `path-facts`.
  - It is bounded by the existing 300 ms timeout (`LOOKUP_TIMEOUT_MS`).
  - Most files have zero invariants and were looked up on first touch anyway, so the added cost is lookups on files already seen.
- **No handle outside the set** → return `undefined`: allow, silently.
- **New handles** → announce only those, add them to the set, then act per `sentinel_mode` as today (`inform` or `deny-once`).
- **Release.** When the underlying call is refused by something else or throws (`:178-186`, and the outer catch at `:160-164`), remove exactly the handles *this call* announced. They are then announced on the next attempt. This keeps today's intent in the new encoding.

**deny-once.**
- The first attempt is refused with the new handles, and they are recorded.
- The model's re-issued edit looks up again, finds nothing new, and proceeds.
- The user-visible behaviour is identical to today.

**Subagents.** A different `agentId` means a separate set. An invariant already told to the main agent is still announced to a subagent that edits the file, because it has its own context. This is unchanged.

**Lookup failure.** Unchanged: set `failedAt[key]`, record nothing, skip that key for 60 s (`FAILURE_SKIP_MS`), then retry. `failedAt` is per-session (§3).

## 6. Atoms written before the change

- **`lens` and `toasts`:** they gain an optional `session?: string`. That is a compatible change. A missing `session` reads as a different session, so the old per-session fields reset on first use. **No shape tag.**
- **`sentinel`:** `seen` changes type, so it gets a shape tag.
  - The constant is `SENTINEL_SHAPE = "sentinel-2"`, defined once in `plugin/mods/sentinel/index.tsx`.
  - The atom is declared as `Shaped<SentinelState>` in `plugin/mods/shared/state.d.ts`, the host's mechanism, as with `TAIL_SHAPE`.
  - An atom with a missing or different shape is declined: fresh state, never read field by field, never migrated.
  - Cost: at most one re-announcement per file.

## 7. Files

### Implementor (one commit, plugin `1.3.12`)

| File | Change |
|---|---|
| `plugin/mods/shared/<new helper>.ts` + its test | §3 helper; test H1 |
| `plugin/mods/shared/state.d.ts` | `LensState.session?: string`; `ToastsState.session?: string`; `SentinelState { session?: string; seen: Record<string, string[]>; failedAt: Record<string, number> }`; `PluginState['engram'].sentinel: Shaped<SentinelState>`. Edit only these keys' types |
| `plugin/mods/lens/index.tsx` | Apply the helper around the `autoOpened` read and write (`:86-89`) |
| `plugin/mods/lens/lens.test.ts` | L1, L2 |
| `plugin/mods/toasts/index.ts` | Apply the helper in the capture update (`:78-86`) and the undo read (`:97-111`) |
| `plugin/mods/toasts/index.test.ts` | T1–T3 |
| `plugin/mods/sentinel/index.tsx` | §5 plan, record and release (`~:109-186`); `SENTINEL_SHAPE`; the helper on `seen`/`failedAt` |
| `plugin/mods/sentinel/index.test.ts` | S1–S9 |
| `plugin/.claude-plugin/plugin.json`, `.claude-plugin/marketplace.json` | `1.3.12` |
| `docs/engram-implementation-plan.md` | Add one line to **D79**: *"The host does not add the plugin prefix to mod commands. This was observed live: a mod command registered as `lens` was reached as a bare `/lens`. It is the citable source for the claim in `plugin/mods/lens/index.tsx`'s comment and in README.md."* |

### Docs-writer (README.md, after the code is built and green)

- **Sentinel, Limits bullet (~`README.md:766`).** Now: "The sentinel ignores `MultiEdit`, and it never announces a file the model touched before an invariant existed for it, to that agent." It becomes: "The sentinel ignores `MultiEdit`."
- **Sentinel behaviour.** Add, outside Limits, in the sentinel's description: "Each invariant is announced to each agent once per session; one added later is announced on that agent's next edit of the file."
- **Toasts (~`README.md:645-655`).** Now: "the first one ever shown adds `/engram-undo-capture to forget`, and the command retracts the most recent capture shown in a toast." It becomes: "the first one shown in each session adds `/engram-undo-capture to forget`, and the command retracts the most recent capture toasted in this session."
- **Lens auto-open.** Wherever README.md describes `lens_auto_open`, it opens the Lens "once per session". If that text sits under Limits, move it out of Limits.

No server, C# or other mod file changes.

## 8. Tests (each falsification: break the guarded line, see red, restore; falsify against a committed tree and confirm with `git diff --quiet`)

| # | File | Assertion | Falsification |
|---|---|---|---|
| H1 | shared helper test | A mismatched or missing `session` resets the listed fields and stamps the id; a matching `session` returns the value untouched | Ignore the id → red |
| L1 | lens.test.ts | `lens_auto_open`: opens on session A's first recall, not on its second. After the fake session id changes to B, the next recall opens it again | Drop the session keying → no open in B, red |
| L2 | lens.test.ts | A reload (same id, `session.start` re-fired) followed by a recall does not reopen | Reset in `session.start` → reopens, red |
| T1 | toasts/index.test.ts | The undo hint appears on session A's first toast only, and again on session B's first toast | No keying → no hint in B, red |
| T2 | toasts/index.test.ts | A reload with the same id gives no new hint on the next toast | Reset in `session.start` → hint re-added, red |
| T3 | toasts/index.test.ts | In session B with nothing toasted, `/engram-undo-capture` replies `No captured memory to forget` and the fake records **no** `forget` call, even though A toasted handles | Unkeyed → forgets A's handle, red |
| S1 | sentinel/index.test.ts | A touch while `path-facts` returns `[]` announces nothing; a later touch after it returns `[f1]` announces `f1` | Record a claim before the zero check (today's code) → red |
| S2 | sentinel/index.test.ts | The same `[f1]` on a second touch announces nothing | Never record handles → re-announces, red |
| S3 | sentinel/index.test.ts | `[f1]` announced, then `[f2]` (a replacement) → only `f2` is announced | Count-based → nothing announced, red. Announce-all → `f1` repeated, red |
| S4 | sentinel/index.test.ts | `deny-once`: the first edit is denied naming `f1`; the re-issued edit proceeds (`go` runs, no deny) | Re-announce on re-issue → denied again, red |
| S5 | sentinel/index.test.ts | After an announcement, the underlying call is refused or throws → the next touch announces `f1` again | Drop the release → red |
| S6 | sentinel/index.test.ts | `f1` announced to the main agent; a subagent touching the same path is announced `f1` | Key without `agentId` → red |
| S7 | sentinel/index.test.ts | A lookup failure → no announcement, no lookup for that key within 60 s, a retry after 60 s | Drop the `failedAt` check → a lookup within 60 s, red |
| S8 | sentinel/index.test.ts | After the session id changes, a touch re-announces `f1` to the main agent | No keying on `seen` → red |
| S9 | sentinel/index.test.ts | An atom with no shape, or with an old `string[]` `seen`, is declined: fresh state, no throw | Read the old atom → throws or misbehaves, red |

Run `claude plugin test plugin` from the repo root, then `scripts/plugin-typecheck.sh`. Exit 2 means the engine types are missing, which is a skip, not a pass.

## 9. Invariants and negative cases

Must not change:
- **No hook verb, server or C# change.**
- **Sentinel behaviour other than §5 stays the same.** That covers mode handling (`off`, `inform`, `deny-once`, unknown → `inform`), the `MultiEdit` exclusion, the lookup timeout, and the `deny-once` message text.
- **Toasts capture lookup is unchanged:** `captures` on prompt submit with the −1 s `since`.
- **Lens recall rendering, history and `paneOpen` are unchanged.**
- **The tail mod is untouched.**

| Input | Expected |
|---|---|
| Plain hot reload, same id | Nothing resets: no Lens reopen, no new hint, no re-announcement |
| `/clear`, new id | All per-session fields reset on first use |
| Resume, same id (if E17 says so) | State continues |
| Resume, new id (if E17 says so) | Fields reset: at most one extra auto-open, one extra hint, one re-announcement per file. Acceptable |
| Atom written by 1.3.11 (no `session`) | lens/toasts fields reset on first use; sentinel atom declined |
| File with no invariants, touched repeatedly | A lookup each touch, never an announcement |
| Invariant added after first touch | Announced on that agent's next edit |
| Invariant replaced | Only the new one announced |
| Same file, a subagent after the main agent | Announced to the subagent |
| Server down | `failedAt` skip for 60 s per key, as today |
| `/engram-undo-capture` twice in a row | Unchanged: the second reports "already forgotten" (out of scope) |

## 10. NEEDS-EVIDENCE (live; does not gate the build)

| ID | Check | Then |
|---|---|---|
| E17 | In a live Claude Code session with the engram plugin, log `$.session.id()` from a mod (or read the `session_id` on its `mod-call` records in `telemetry.jsonl`, which carry the Claude Code id). Record the id: (a) before and after a plugin hot reload; (b) before and after `/clear`; (c) in a session, then after quitting and resuming it with `claude --resume`. Probe hygiene: read-only on `telemetry.jsonl`; set `ENGRAM_HOME` to a `mktemp -d` home if any `engram` command is run | Expected: (a) same, (b) different, (c) same. If (b) is the same id, `/clear` does not reset per-session state, and the README wording "once per session" must say "until the session id changes"; the docs-writer adjusts it. If (c) differs, nothing changes (§9 row). Record the result in D79 |

## 11. Decisions and their owners

- **Session keying over a reset in `session.start`.** Architect. Reason: reload safety.
- **Sentinel `seen`/`failedAt` made per-session.** Architect extension, confirmed in scope by Jim via engram-1a.
- **Announced-handle sets over claim-on-facts or counts.** Architect. Reason: the only option that announces an added *or* replaced invariant exactly once.
- **Lens `recalls` keep across `/clear`.** Not changed; possible follow-up, not asked for.
