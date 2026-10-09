# Memory Tail auto-open: diagnosis, fix and 1.3.14 diagnostic

Implementer: implementor
Reviewer: reviewer

Status: r4. Spec only; nothing here has been run. The diagnostic is gated on `tail_auto_open === true` (§4, r3). Plugin 1.3.13 → 1.3.14. Branch `mods/live-memory-tail`.

r2 closes two gaps the Implementor reported:

1. **No shared slash-command check existed.** Section 3.1 item 2 now names a shared predicate for "the person's own prompt". The primer switches to it.
2. **The spec was silent on prompt origin.** Only composer-origin prompts may open the pane or mark the session.

The changes are in §3.1 item 2, §4 D2, §5 and §6 (T9, T10).

r3 resolves a conflict between the diagnostic and the unchanged-tests rule. The whole diagnostic is now gated on `tail_auto_open === true` (§4). D2 is raised only before the session is marked, and has no `option off` form. The raw panes call is separate (§4). T11 is added.

r4 applies three Reviewer nits:

1. T8 loses its toast arm. That arm could not go red, because the engine swallows a throwing `ui.toast` handler itself; the Reviewer measured it at 479 passing, 0 failing. The guard stays, as defence in depth.
2. The status line and §8 now state the r3 gate.
3. T6b is added.

## TL;DR

- **Symptom.** `tail_auto_open = true` never opens the Memory Tail. The Lens auto-opens and `/engram-tail` by hand works.
- **Cause A: `/clear` (certain from the host types).**
  - The mod event `session.start` never fires on `/clear`.
  - The "SessionStart hook" the user saw is the *classic settings hook*, whose source is `clear`. It is not the mod event.
- **Cause B: restart (certain mechanism; whether it applies depends on the user's terminal width).**
  - The open runs from `$.clock.after(0)` inside `session.start`. That makes it *unasked*.
  - An unasked open is drawn only from 144 terminal columns, or 110 for a pane the person has opened before.
  - Below that floor it resolves `{ isPlaced: false, reason }`. It does not reject, so nothing is caught and nothing is shown.
- **The fix.**
  - Move the auto-open out of `session.start` and into the `prompt.submit` hook.
  - It fires on the first real prompt of each Claude Code session id. That open is *asked*, so it is drawn at any width.
  - Keying it on the session id covers `/clear`, resume and restart alike.
- **1.3.14 also ships temporary diagnostic toasts.** They tell the remaining candidates apart in one live run, and are removed in 1.3.15.

## 1. Evidence

All of the following are host types (engine 2.1.295) at `plugin/.claude-plugin/types/claude-code/index.d.ts`. That file is abbreviated **F** below.

| Line(s) in F | What the types say | Bears on |
|---|---|---|
| 4323-4325 | `session.start`: "Fires once per process for each loaded plugin, before the first prompt, then once per fresh load of one (**never `/clear`**)" | A |
| 11054-11055 | "`clear` is how a hook sees a `/clear`: the conversation ends, the process goes on under a new session id, and **no `session.start` fires for it**." | A |
| 11059-11060 | After a `/clear` or a resume, the process goes on under another `$.session.id()` | Fix key |
| 11645 | `source: 'startup' \| 'resume' \| 'clear' \| 'compact' \| 'fork'` belongs to the *classic* SessionStart input. That is the hook the user saw fire. | A |
| 14120-14128 (UiOpenResult) | "Asked (the hook of a command the person typed **or a prompt they entered**, a Button, Input or Select they worked; **never a timer, `session.start`**, a queued prompt, nor `focus`) a pane is placed at any width … Unasked it is placed from 144 terminal columns (110 for an id the person opened from this plugin before … and has not closed by hand since) and waits undrawn below that, no `ui.render` raised" | B, Fix |
| 2478-2500 (`ui.open`) | Resolves `{ isPlaced: false, reason }` when the pane waits undrawn. A rejection is not documented. | B, cause 3 |
| 14134-14157 | `reason` names "the floor it fell under … and the width now" | Diagnostic |
| 3077-3091 (`command.register`) | "Registering a name again replaces it; a built-in's name is refused." | Cause 2 |
| 2515-2527 (`ui.panes`) | "The engine's record, not the module's"; it lists this plugin's open panes, with `isPlaced` false while a pane waits undrawn | Cause 1 |
| 4092-4103 (`prompt.submit`) | Fires when a prompt is submitted, before the turn starts. `next(e)` runs the hooks beneath it and the UserPromptSubmit settings hooks. "a broken plugin never blocks one" | Fix |
| 2438-2447 (`ui.toast`) | `toast(text, { timeoutMs })` returns void and is held while a `holdToasts` pane is shown | Diagnostic |

The code paths involved:

- `plugin/mods/tail/index.tsx:152-175`: the `session.start` handler.
- `index.tsx:165`: the auto-open, `$.clock.after(0, () => void open($, scope).catch(() => undefined))`.
- `index.tsx:114-131`: `open()`. It writes `paneOpen: opened.isPlaced` and starts polling only when the pane is placed.
- `plugin/mods/tail/model.ts:362-365`: `decide()`.

The Lens takes a different route:

- It opens from its `tool.call` hook (`plugin/mods/lens/index.tsx:93-98`), during a turn the person's prompt started.
- It is keyed per session with `forSession` (`plugin/mods/shared/session.ts:10`) and `LENS_SESSION = { autoOpened: false }` (`lens/index.tsx:14`).

## 2. Ranking of the candidates

| # | Candidate | Verdict | Why |
|---|---|---|---|
| A | No mod `session.start` on `/clear` | **Certain** | F:4325 and F:11055 say so in so many words. The auto-open code is never reached after `/clear`. The tail "keeping working across `/clear`" with the option off is the same fact: nothing re-ran, and the chain simply continued. |
| B | Unasked open below the width floor (peer's cause 3, refined) | **Certain mechanism; whether it applied is unverified** | A timer and `session.start` are both named as never asked (F:14121). `ui.open` *resolves* undrawn rather than rejecting, so `.catch(() => undefined)` is never even reached. `open()` then records `paneOpen: false` and starts no chain. Whether the user's terminal was under 144 columns (or 110) is not known. |
| 1 | `ui.panes()` still lists the pane after restart | Unlikely | A restart is a new process, and the panes list is the engine's record for this process. On `/clear` the question is moot (cause A). The diagnostic prints the answer. |
| 2 | `command.register` throws on a repeated `session.start` | Unlikely | The types say a repeated register *replaces*, and only a built-in name is refused. The diagnostic prints the result. |
| 3' | `ui.open` rejects | Unlikely as a rejection | The undrawn case is a resolution (B). A genuine rejection is undocumented. The diagnostic prints it if it happens. |

A cause the brief did not list: **the Lens may work for the same reason the Tail fails.**
- If the Lens's `tool.call` open is treated as asked, it works at any width.
- If it is unasked, it may still clear the 110 floor, because the person once opened the Lens by hand.
- The Tail's open loses on both counts. Section 6 settles which.

## 3. The fix

### 3.1 Behaviour

1. **`session.start` no longer opens anything.**
   - Delete the auto-open at `index.tsx:165`.
   - Its other behaviour is unchanged: register the command, then `decide('session.start', …)` → resume, nothing, or clear (stop the chain and set `paneOpen: false`).
2. **A new `prompt.submit` hook in `plugin/mods/tail/index.tsx`** does the auto-open. It must meet all of the following:
   - **Observation only.**
     - It passes the event on unchanged, through the existing `once(next)` / `go.fallback` pattern used by every other hook in the file.
     - It never rewrites or drops a prompt.
     - Every failure inside it fails open: the prompt still goes through, exactly once.
   - **Option gate.** It does nothing unless `options.tail_auto_open === true`. The manifest declares the option boolean, and the Lens checks it the same way.
   - **Only the person's own prompt counts (amended in r2).** A prompt may open the pane or mark the session only when both of these hold:
     - `e.origin?.kind === 'composer'`. A plugin-origin prompt, or one with no origin, neither opens nor marks.
     - The text is not a slash command (`e.text.startsWith('/')` is false).

     Why each half matters:
     - **Origin.** A prompt a plugin submits is not "a prompt they entered" (F:14120). An open made there is therefore unasked, and is left undrawn below the width floor. It would also mark the session, so the person's first real prompt would not retry, which reproduces the bug.
     - **Slash.** If `/engram-tail` itself arrives through `prompt.submit` as the first input, an auto-open would place the pane and the command's toggle would then close it at once.

     **One implementation.**
     - Add a single exported predicate for "this `prompt.submit` input is the person's own prompt and not a slash command". Its home is `plugin/mods/shared/events.ts`, beside `ANY_PROMPT_SUBMIT` (:8).
     - The tail uses it.
     - `plugin/mods/primer/index.ts:64-65` is switched to use it as well. Today that code inlines exactly these two checks on the same event, so the tail would otherwise be a third copy of one rule.
     - The primer keeps its own `MIN_PROMPT_CHARS` check. That is a primer-only threshold, not part of "the person's own prompt".
     - The primer's behaviour must not change. Its existing tests, including `primer.test.ts:171-175` and `:327`, stay green *unmodified*. That is the equivalence proof.

     **The toasts check at `plugin/mods/toasts/index.ts:68` (`e.prompt.startsWith('/')`) is not touched.**
     - It reads a different input (`e.prompt`, not `e.text` on `prompt.submit`), so it is not the same rule on the same event.
     - §5 keeps the toasts mod unchanged.
     - Unifying it is a separate change, out of scope here.
   - **Once per Claude Code session id.**
     - The session is `$.session.id()`. The flag is reset lazily with `forSession` (`shared/session.ts:10`), the pattern the Lens uses.
     - The session is marked before the open is attempted, so a failed or undrawn open is not retried on every prompt. This matches the Lens.
     - A new id after `/clear`, a resume or a restart makes the next real prompt eligible again.
   - **Only when the pane is not on screen.**
     - Ask the existing `isShown($, state.paneOpen)`.
     - If it is shown: mark the session and do nothing else. Do not route through `decide('toggle', …)`, because that would *close* a pane that is up and polling.
     - If it is not shown: call the existing `open($, scope)`. That is the one implementation of opening, and it resets cursors and starts the chain when the pane is placed.
   - **The open is awaited inside the hook, before the prompt is passed on.**
     - Not via `$.clock.after`: a timer is unasked (F:14121), which is the defect.
     - Before `next(e)`, so that the open happens while the person's prompt is what the hook is answering.
     - The extra delay before the turn is the cost of one `ui.open` plus one `ui.panes`. That is accepted.
3. **Where the per-session flag lives.**
   - Use a new, separate atom `{ plugin: 'engram', key: 'tail-session' }` holding `{ session?: string; autoOpened: boolean }`.
   - Give it its own shape constant through the existing `Shaped` mechanism, as the other atoms do. Declare its type in `plugin/mods/shared/state.d.ts`, beside `TailState`.
   - Do **not** put it in `TailState`. Two reasons:
     - `open()` (`index.tsx:120-128`) rebuilds the tail atom from an explicit field list. A per-session field added there would be dropped by every open. The next prompt would then see no session, reset `autoOpened`, and reopen a pane the person had just closed with `/engram-tail`.
     - Bumping `TAIL_SHAPE` to carry the field would decline every saved tail atom on upgrade (R7), which loses the rows and cursors of an open pane.
4. **Manifest text.**
   - In `plugin/.claude-plugin/plugin.json`, change the `tail_auto_open` description to say it opens when the first prompt of a session is sent, not "at session start".
   - Proposed wording: "Open the live memory tail pane when you send the first prompt of a session (including after /clear), instead of waiting for the tail command."
5. **Version.** Bump `1.3.13` → `1.3.14` wherever the plugin version is recorded. That is `plugin/.claude-plugin/plugin.json:3`, plus any marketplace manifest that repeats it; grep for it.

### 3.2 Known limitation, deliberately not fixed

**A pane left undrawn and later placed has no poll chain.** This is unchanged by this spec. An open can still resolve undrawn, for example on a queued prompt (F:14121) or if section 6 shows `prompt.submit` is not treated as asked. If the terminal is then widened, the host draws the pane, but no chain runs.

Recovery is by hand:
- `isShown` turns true and `running` is false.
- So `/engram-tail` *resumes* the pane (`decide` → `resume`).

Fixing it would mean starting a chain from `ui.render`, which fires on reloads and redraws too. That would add a second start path beside `session.start`'s resume.

**Re-open trigger:** a live report of a Tail pane that is drawn but stuck on "Connecting".

## 4. Diagnostic (1.3.14 only; removed in 1.3.15)

This adds no new option.

**r3: every part of the diagnostic is gated on the existing `options.tail_auto_open === true`.** That covers both toasts, every write to the `tail-diag` atom, and the `/engram-tail` output suffix. With the option off, 1.3.14 behaves exactly as if the diagnostic did not exist.

Why the gate:

- Only a user with the option on can show the bug, so nothing is lost by it.
- Under r2 the diagnostic ran for every user. All mods load as one plugin, so its toast and suffix leaked into four unchanged tests in other mods and the tail's own suite:
  - `tail.test.tsx:1015` (R2);
  - two toasts tests: "a reload keeps the session…" and "a command that cannot register…";
  - sentinel S8.

  That contradicted §5 and §6.
- Editing those tests to filter the lines out was rejected. It would have changed the toasts and sentinel test files, which §5 keeps untouched, and those edits would then have to be reverted in 1.3.15.

Every diagnostic line begins `tail diag:` so that the removal can be checked with a grep.

| Id | Where | Text (illustrative, not prescriptive) |
|---|---|---|
| D1 | Every `session.start` the tail hook sees | `tail diag: session.start sid=<first 8> register=<ok\|threw: msg> panes=<id:isPlaced,… \| none \| threw: msg> running=<bool> action=<resume\|nothing\|clear>` |
| D2 | `prompt.submit`: the first *real* prompt per session id, and any prompt the hook skipped as a slash command | `tail diag: first prompt sid=<8> shown=<bool> open=<placed \| undrawn: <reason> \| threw: msg \| skipped: shown \| skipped: slash \| skipped: origin <kind\|none>>`. Raised only while the session is not yet marked, so at most one per skipped prompt before the first real one and none after it. There is no `option off` form: with the option off, D2 does not exist (r3). |

Rules for the diagnostic:

- **Each line is shown with `$.ui.toast` (`timeoutMs` 15000).**
- **Each line is also appended to a separate diagnostic atom.**
  - The atom is `{ plugin: 'engram', key: 'tail-diag' }`, at most 10 lines, newest last. It has no shape constant, because it is temporary.
  - **Every `/engram-tail` output text gets the stored lines appended under a `tail diag:` heading.**
  - Reason: a toast raised during `session.start`, before the first prompt, may never be seen. The command output is a channel the person can always read.
- **D1 must report `register` separately.**
  - This needs the register call to be distinguishable from the `decide` step that follows it.
  - Do *not* change what happens after a register failure: the diagnostic only observes.
- **The `ui.panes()` answer is recorded raw** (ids and `isPlaced`), not just the boolean `isShown` returns. That lets cause 1 be read directly.
  - It comes from a separate `ui.panes()` call made by the diagnostic.
  - `isShown` is left unchanged (confirmed in r3).
  - The two calls could in principle disagree. That is acceptable for a temporary observation.
- **The diagnostic must never change an outcome.**
  - Every diagnostic step sits inside its own fail-open guard.
  - A throw from `toast` or the atom write costs only the line.
- **Removal in 1.3.15** means all of the following:
  - delete both toasts;
  - delete the `tail-diag` atom and every write to it;
  - delete the `/engram-tail` output suffix;
  - delete the diagnostic tests T7 and T8;
  - afterwards, `grep -rn "tail diag" plugin/` returns nothing.

## 5. Invariants and negative cases

What must NOT change:

- `decide()` and its R1 table (`model.ts:362-365`): untouched.
- `session.start`'s resume, nothing and clear behaviour, and R2, R9 and R12: unchanged. The one exception is the auto-open test (below).
- `/engram-tail` toggle semantics: unchanged. Its output only gains the diagnostic suffix in 1.3.14.
- `open()`, `resume()`, `start()`, `stop()` and `isShown()`: reused, not modified.
- `TAIL_SHAPE` stays `tail-3`, and `TailState` gains no field.
- The `tool.call` and `ui.render` hooks: unchanged.
- No prompt is ever rewritten, dropped or delayed by more than the open itself.
- The Lens, toasts and sentinel mods: untouched.
- The primer's behaviour: unchanged. Only its two inline checks move behind the shared predicate (r2).

Neighbouring inputs. The rule under test is "on a prompt, open when not shown".

| Input | Expected |
|---|---|
| First real prompt, option true, pane not on screen | `open()` runs, awaited inside the hook. The prompt then passes unchanged. |
| Second real prompt, same session id | Nothing opens, even if the person closed the pane in between |
| First real prompt after the session id changes (`/clear`, resume) with no `session.start` | Opens again (pane not shown) |
| First real prompt, option false or absent | Nothing opens and nothing is marked |
| First real prompt, pane already shown and polling | Session marked. No `ui.open`, no `ui.close`, and the chain is not stopped. |
| First real prompt, pane shown but nothing polling (after a reload) | Session marked. Nothing else: `session.start`'s resume owns this case. |
| Prompt that is a slash command (incl. `/engram-tail`, `/clear`) | No open and no mark. The next real prompt is still eligible. |
| Prompt with `origin.kind` other than `composer` (a plugin's prompt) | No open and no mark. The next composer prompt is still eligible. |
| Prompt with no `origin` | Same as the row above |
| Primer: any prompt (composer, plugin-origin, no origin, slash, short) | Exactly as before r2 |
| `ui.panes()` throws | `isShown` falls back to the atom flag (existing). The rule applies to that answer. |
| `ui.open` throws or rejects | The prompt still passes, exactly once. The session stays marked. |
| `ui.open` resolves `{ isPlaced: false }` | `paneOpen` false and no chain (existing `open()`). The session stays marked, so there is no retry on later prompts. |
| Hook body throws before `next` | `next(e)` still called exactly once with the original `e` |
| Queued prompt (typed during a turn) | Same rule. The host may answer undrawn (F:14121); that falls under section 3.2. |
| `session.start` with option true and pane not on screen | Clears as before. **No `ui.open`.** |

## 6. Tests

Tests go in `plugin/mods/tail/tail.test.tsx`, using its existing `world()` and `startSession` harness. The harness already answers `ui.open` and `ui.panes` with hooks beneath (see R2 at :1033). If the `claude-code/testing` kit cannot fire `prompt.submit` or stub `$.session.id()` per call, stop and report: that is a spec gap, not a reason to test `model.ts` alone.

| Id | Asserts | Falsification (on a committed tree, with `git diff --quiet` checked before trusting the arm) |
|---|---|---|
| T1 | `session.start` with option true and pane not on screen makes **no** `ui.open` call. This **replaces** the existing test `R2: tail_auto_open opens a pane that is not on screen` (:1033), whose expectation is now wrong. | Restore the `$.clock.after(0, … open …)` line → red |
| T2 | First real prompt, option true, not shown → exactly one `ui.open` for `engram-tail`, issued *before* the hook calls `next`. `next` receives the original event. | Move the open into `$.clock.after(0, …)` → red (open not before `next`). Delete the open → red. |
| T3 | Second prompt, same id, after the pane was closed with `/engram-tail` → no `ui.open` | Replace the `forSession` check with "always eligible" → red |
| T4 | Prompt after `$.session.id()` changes, with no `session.start` between → `ui.open` again | Key the flag on a plain boolean, not the session → red |
| T5 | Option false → no `ui.open` over three prompts. Slash-command prompt first, then a real one → exactly one `ui.open`, on the real one. | Drop the option check → red. Drop the slash skip → red. |
| T6 | Pane shown and polling at the first prompt → no `ui.open`, no `ui.close`, and polling continues at two seconds | Route through `decide('toggle', …)` → red (pane closed) |
| T7 (diag) | A stubbed undrawn open (`{ isPlaced: false, reason: 'R' }`) yields a toast containing `undrawn: R`, and the next `/engram-tail` output contains that line | Delete the atom append → red on the output half |
| T6b (r4) | Pane already shown at the first prompt, so the session is marked with no open. The person then closes it with `/engram-tail`. The next composer prompt in the same session → no `ui.open`. | Make the "already shown" path return without marking the session → red |
| T8 (diag/fail-open) | `ui.open` throwing → `next` is called exactly once with the original event | Remove the guard around the open → red |

**T8 no longer tests a throwing `ui.toast` (r4).**
- The engine swallows a throwing `ui.toast` handler itself, so removing the guard leaves that arm green (the Reviewer's arm A: 479 passing, 0 failing). An assertion that cannot fail is dropped.
- The guard around the toast stays. It is defence in depth against the host's documented `void` toast, and the Reviewer has already ruled to keep it.

| T9 (r2) | A plugin-origin prompt, then a prompt with no origin, then a composer prompt, all in one session → exactly one `ui.open`, on the composer prompt | Drop the origin half of the shared predicate → red. Restore it and confirm green before trusting the arm. |
| T10 (r2) | The tail and the primer route through the one predicate | Break the origin half *inside the shared predicate*. Both T9 and the primer's existing origin test (`primer.test.ts:171-175`) must go red. If only one reddens, the other mod still has a private copy. |

| T11 (r3, diag gate) | With the option off, `session.start`, a composer prompt and `/engram-tail` together produce no toast containing `tail diag:` and no `tail diag:` in the command output | Remove the gate → red. The four tests named in §4 (r3) must go red too; confirm that, then restore. |

The existing R-series, M-series and W-series tests stay green unchanged, apart from T1 replacing R2 (:1033). The primer's test file is not edited. Neither are the toasts and sentinel test files (r3).

If any *existing* test other than R2 (:1033) runs with `tail_auto_open: true` and breaks on the diagnostic, stop and report it. Do not filter it.

## 7. NEEDS-EVIDENCE: one live run on 1.3.14 (the user)

Run it on a real terminal; this needs no Engram store work. Before starting, read the width from inside Claude Code with `! tput cols`.

1. **Restart, narrow.**
   - Make the terminal **under 110 columns**.
   - Quit and start `claude` with `tail_auto_open = true`. Send one ordinary prompt.
   - Expected: the pane opens, and the D2 toast says `open=placed`.
   - Then run `/engram-tail` twice: the first run closes the pane and prints the stored diag lines, the second reopens it.
   - Record the D1 line: the panes answer, `register` and `action`.
2. **`/clear`.**
   - Close the pane with `/engram-tail`, run `/clear`, and send one ordinary prompt.
   - Expected: **no new D1 line** (cause A confirmed), and a D2 line with a new `sid` and `open=placed`.
3. **Wide control (optional).** Repeat step 1 at 150 or more columns. Expected: the same as step 1.

| Observation | Decides |
|---|---|
| D2 `open=placed` in step 1 at under 110 columns | A `prompt.submit` open is asked. Fix confirmed; ship 1.3.15 without the diagnostic. |
| D2 `open=undrawn: …` in step 1 | `prompt.submit` is not treated as asked. Re-dispatch the Architect with the `reason` text. The fallback options are an open on the first Engram `tool.call` (the Lens's route) or accepting wide-terminal-only, and that choice is the user's. |
| A D1 line appears after `/clear` | The types are wrong about `/clear`. Re-dispatch; cause A is withdrawn. |
| D1 shows `register=threw` or `panes=engram-tail:true` on a fresh start | Cause 2 or cause 1 is also live. Re-dispatch with the line; the fix still stands. |
| No D2 line at all after a real prompt | The `prompt.submit` hook is not reached. Re-dispatch; check the matcher. |

## 8. Decisions

- **Made:**
  - Auto-open moves from launch to the first real prompt, so on a wide terminal it now appears one prompt later than the option's old description promised.
  - Single path, no session-start attempt kept. Two opening paths would mean two states to reason about for one option.
  - Separate atom, not a `TailState` field.
  - The diagnostic in 1.3.14 adds no new option. It is gated on the existing `tail_auto_open === true` (r3; §4), so users with the option off see nothing.
- **Left to the user:**
  - whether "opens when the first prompt is sent" is acceptable as the option's behaviour;
  - if step 1 shows `undrawn`, which fallback to take.
- **Not decided (out of scope):** any fix for section 3.2.
