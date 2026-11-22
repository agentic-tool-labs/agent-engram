import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register } from 'claude-code'
import { EDIT_TOOLS, SHARED_INITIAL, modApi } from '../shared/client'
import type { ModIo } from '../shared/client'
import { ANY_SESSION_START, ANY_TURN_COMPLETE } from '../shared/events'
import {
  AUTO_EVIDENCE,
  DIGEST_INITIAL,
  DIGEST_SYSTEM,
  EVIDENCE,
  NO_SELECTION,
  PANE,
  digestAutoSave,
  digestEvery,
  evidenceWith,
  fingerprint,
  lessonsBlock,
  parseReflection,
  settle,
  shouldDigest,
  transcriptWindow,
} from './digest'
import type { Lesson, Verdict } from './digest'
import type { DigestCandidate } from '../shared/state'
import { once } from '../shared/guard'

const MOD = 'digest'

// The scanner reads an atom's reference only from a const of the file that uses it.
const SHARED = atom({ plugin: 'engram', key: 'shared' } as const, SHARED_INITIAL)
const DIGEST = atom({ plugin: 'engram', key: 'digest' } as const, DIGEST_INITIAL)

// Pure forwards to the engine; behaviour lives in the client (see shared/binding.template.ts).
const bindIo = ($: EngineInterface): ModIo => ({
  run: (argv, opts) => $.process.run(argv, opts),
  fetch: (url, init) => $.http.fetch(url, init),
  sleep: (ms, opts) => $.clock.sleep(ms, opts),
  now: () => $.clock.now(),
  sessionId: () => $.session.id(),
  pluginRoot: $.plugin.root,
  readShared: () => read($, SHARED),
  updateShared: (fn) => update($, SHARED, fn),
})

/** The main session's agent type, when the engine gives one; otherwise the server files under `main`. */
async function roleOf($: EngineInterface): Promise<string | undefined> {
  try {
    const { agentType } = await $.session.info()
    return agentType === undefined || agentType.trim() === '' ? undefined : agentType
  } catch {
    return undefined
  }
}

type RememberReply = { handle: string; id: number; created: boolean; notice?: string }

/** One `remember` per statement, addressed by its kind; reports the saved handles and which statements failed, and why. */
async function rememberAll($: EngineInterface, items: readonly DigestCandidate[], marker: string) {
  const io = bindIo($)
  const sessionId = await io.sessionId()
  const role = await roleOf($)
  const saved: string[] = []
  const savedLines: string[] = []
  const failed: string[] = []
  const failedTexts = new Set<string>()
  for (const { text, kind, quote } of items) {
    // Agent scope needs the quote `settle` found in a user row; a lesson without one is a session note.
    const isLesson = kind === 'lesson' && quote !== undefined
    const body: Record<string, string> = { session_id: sessionId, statement: text, evidence: evidenceWith(marker, isLesson ? quote : undefined) }
    if (isLesson) {
      body['scope'] = 'agent'
      if (role !== undefined) body['agent'] = role
    } else if (kind === 'project') {
      body['scope'] = 'project'
      body['project_dir'] = await $.session.cwd()
    }
    const res = await modApi<RememberReply>(io, 'remember', body, { mod: MOD })
    if (res.ok) {
      const notice = res.value.notice === undefined ? '' : ` (${res.value.notice})`
      saved.push(`[${res.value.handle}]${notice}`)
      savedLines.push(`[${res.value.handle}] ${text}${notice}`)
    } else {
      failed.push(`Not saved: "${text}" (${res.reason})`)
      failedTexts.add(text)
    }
  }
  return { saved, savedLines, failed, failedTexts }
}

/** Reads what was said since the last digest, asks for statements, drops the ones recall already covers. */
async function runDigest($: EngineInterface, autoSave: boolean): Promise<void> {
  try {
    const messages = await $.session.messages()
    const { seenMessages, lastSeen } = await read($, DIGEST)
    // The mark counts only while the row before it is still the one last read; a compaction
    // that rewrote the conversation moves rows under the count, and then everything is new.
    const isMarkValid = seenMessages > 0 && seenMessages <= messages.length && fingerprint(messages[seenMessages - 1]!) === lastSeen
    const fresh = isMarkValid ? messages.slice(seenMessages) : messages
    const last = messages.at(-1)
    await update($, DIGEST, (s) => ({ ...s, seenMessages: messages.length, lastSeen: last === undefined ? '' : fingerprint(last) }))

    const window = transcriptWindow(fresh)
    if (window.length === 0) return
    const transcript = window.map((k) => k.line).join('\n\n')

    const io = bindIo($)
    const sessionId = await io.sessionId()
    const lessons = await currentLessons($, sessionId)
    const reply = await $.model.complete({ model: 'haiku', system: DIGEST_SYSTEM, prompt: lessonsBlock(lessons) + transcript, maxTokens: 600 })
    if (!reply.isAnswered) return
    const reflection = parseReflection(reply.text)
    if (reflection === undefined) return

    const rows = window.map((k) => k.row)
    await proposeOrSave($, sessionId, reflection.save.map((p) => settle(p, rows)), autoSave)
    if (autoSave) await sendVerdicts($, sessionId, reflection.verdicts, lessons)
  } catch {
    // A digest is a convenience; nothing it hits may surface.
  }
}

/** The role's lessons as the server would deliver them now; none when the call fails. */
async function currentLessons($: EngineInterface, sessionId: string): Promise<Lesson[]> {
  const role = await roleOf($)
  const body: Record<string, string> = role === undefined ? { session_id: sessionId } : { session_id: sessionId, agent: role }
  const res = await modApi<{ current: Lesson[] }>(bindIo($), 'lessons', body, { mod: MOD })
  return res.ok && Array.isArray(res.value.current) ? res.value.current : []
}

/** One `lesson-feedback` per lesson the reply judged, at most once per fact per session; a failure is dropped. */
async function sendVerdicts($: EngineInterface, sessionId: string, verdicts: readonly Verdict[], lessons: readonly Lesson[]): Promise<void> {
  try {
    const offered = new Set(lessons.map((l) => l.id))
    // ponytail: the judged set lives in mod state, so a mod reload may send one verdict twice; a store-side check if that matters.
    let due: Verdict[] = []
    await update($, DIGEST, (s) => {
      const judged = s.judgedSession === sessionId ? (s.judged ?? []) : []
      due = verdicts.filter((v) => offered.has(v.id) && !judged.includes(v.id))
      return { ...s, judgedSession: sessionId, judged: [...judged, ...due.map((v) => v.id)] }
    })
    const io = bindIo($)
    for (const { id, verdict } of due) {
      await modApi(io, 'lesson-feedback', { session_id: sessionId, fact_id: id, verdict }, { mod: MOD })
    }
  } catch {
    // A verdict is a judgement worth having, never one worth failing over.
  }
}

/** Drops what recall already covers, then saves the rest or puts it in the review pane. */
async function proposeOrSave(
  $: EngineInterface,
  sessionId: string,
  proposed: readonly Omit<DigestCandidate, 'ticked'>[],
  autoSave: boolean,
): Promise<void> {
  try {
    if (proposed.length === 0) return
    const io = bindIo($)
    const known = await Promise.all(
      proposed.map(async ({ text }) => {
        const found = await modApi(io, 'recall', { session_id: sessionId, query: text, budget_tokens: 100 }, { mod: MOD })
        return found.ok && found.value.coverage === 'high'
      }),
    )
    const candidates: DigestCandidate[] = proposed.filter((_, i) => !known[i]).map((p) => ({ ...p, ticked: false }))
    if (candidates.length === 0) return

    if (autoSave) {
      const { savedLines, failed, failedTexts } = await rememberAll($, candidates, AUTO_EVIDENCE)
      // A statement that did not save stays for /engram-digest-review; nothing retries on its own.
      const kept = candidates.filter((c) => failedTexts.has(c.text))
      if (kept.length > 0) await update($, DIGEST, (s) => ({ ...s, candidates: kept }))
      // One toast: a plugin's new toast replaces its previous one, so saved lines and failures travel together.
      const lines = [...savedLines.map((l) => `Auto-saved ${l}`), ...failed, ...(kept.length > 0 ? ['/engram-digest-review to retry'] : [])]
      $.ui.toast(lines.join('\n'))
      return
    }

    await update($, DIGEST, (s) => ({ ...s, candidates }))
    await $.ui.open({ id: PANE, title: 'Memory candidates' })
    $.ui.toast(`${candidates.length} memory candidates — /engram-digest-review`)
  } catch {
    // A digest is a convenience; nothing it hits may surface.
  }
}

/** Saves the ticked candidates, one `remember` each, and reports the outcome in a single toast. */
async function saveTicked($: EngineInterface): Promise<void> {
  const { candidates } = await read($, DIGEST)
  const ticked = candidates.filter((c) => c.ticked)
  if (ticked.length === 0) return

  const { saved, failed, failedTexts } = await rememberAll($, ticked, EVIDENCE)
  // Rows that did not save stay, unticked, so the user can press Save again; nothing retries on its own.
  const kept = candidates.filter((c) => failedTexts.has(c.text)).map((c) => ({ ...c, ticked: false }))
  await update($, DIGEST, (s) => ({ ...s, candidates: kept }))
  if (kept.length === 0) await $.ui.close({ id: PANE })
  const lines = saved.length > 0 ? [`Saved ${saved.join(', ')}`, ...failed] : failed
  $.ui.toast(lines.join('\n'))
}

async function skipAll($: EngineInterface): Promise<void> {
  await update($, DIGEST, (s) => ({ ...s, candidates: [] }))
  await $.ui.close({ id: PANE })
}

export const register: Register = (on, options) => {
  const every = digestEvery(options)
  const autoSave = digestAutoSave(options)

  on('session.start', ANY_SESSION_START, async ($, e, next) => {
    const go = once(next)
    try {
      // The event is shared with other mods; a failure here must not stop their hooks.
      try {
        await $.command.register({
          name: 'engram-digest-review',
          description: 'Review the memory candidates the auto-digest proposed',
        })
        await $.command.register({
          name: 'engram-remember-selection',
          description: 'Put the selected text in the prompt as "Remember this: …" so you can edit it and submit',
        })
      } catch {
        // The commands are missing, nothing else is affected.
      }
      return go(e)
    } catch {
      return go.fallback(e)
    }
  })

  on('tool.call', { tool: EDIT_TOOLS }, async ($, e, next) => {
    const go = once(next)
    try {
      const ran = await go(e)
      await update($, DIGEST, (s) => ({ ...s, editedThisTurn: true }))
      return ran
    } catch {
      return go.fallback(e)
    }
  })

  on('turn.complete', ANY_TURN_COMPLETE, async ($, e, next) => {
    const go = once(next)
    try {
      if (every === 0 || e.agentId !== undefined || e.isAborted || e.reason !== 'answer') return go(e)

      // The event is shared with other mods; a failure here must not stop their hooks.
      try {
        let isDue = false
        await update($, DIGEST, (s) => {
          const turns = s.turnsSinceDigest + 1
          isDue = shouldDigest(every, turns, s.editedThisTurn)
          return { ...s, turnsSinceDigest: isDue ? 0 : turns, editedThisTurn: false }
        })
        if (isDue) $.clock.after(0, () => runDigest($, autoSave))
      } catch {
        // This turn is not counted.
      }
      return go(e)
    } catch {
      return go.fallback(e)
    }
  })

  on('command.run', { command: 'engram-digest-review' }, async ($, e, next) => {
    const go = once(next)
    try {
      const { candidates } = await read($, DIGEST)
      if (candidates.length === 0) return { text: 'No memory candidates to review.' }
      await $.ui.open({ id: PANE, title: 'Memory candidates', focus: true })
      return {}
    } catch {
      return go.fallback(e)
    }
  })

  on('command.run', { command: 'engram-remember-selection' }, async ($, e, next) => {
    const go = once(next)
    try {
      const selected = await $.ui.selection()
      if (selected === undefined || selected.text.trim() === '') {
        $.ui.toast(NO_SELECTION)
        return {}
      }
      await $.prompt.fill({ text: `Remember this: "${selected.text}"` })
      return {}
    } catch {
      return go.fallback(e)
    }
  })

  on('ui.render', { component: 'Pane', requestId: PANE }, async ($, e, next) => {
    const go = once(next)
    try {
      const { Box, Button, Text } = $.ui.resolve(e)
      const { candidates } = await read($, DIGEST)
      if (candidates.length === 0) return <Text dimColor>No memory candidates.</Text>

      return (
        <Box flexDirection="column">
          <Text dimColor>Tick what is worth keeping. Nothing is saved until you press Save.</Text>
          {candidates.map((c, i) => (
            <Box key={`row${i}`}>
              <Button
                key={`tick${i}`}
                plain
                label={c.ticked ? '[x]' : '[ ]'}
                onPress={() =>
                  update($, DIGEST, (s) => ({
                    ...s,
                    candidates: s.candidates.map((one, j) => (j === i ? { ...one, ticked: !one.ticked } : one)),
                  }))
                }
              />
              <Text>{` ${c.kind ?? 'note'} · ${c.text}`}</Text>
            </Box>
          ))}
          <Box>
            <Button key="save" label="Save" variant="primary" onPress={() => saveTicked($)} />
            <Text> </Text>
            <Button key="skip" label="Skip" onPress={() => skipAll($)} />
          </Box>
        </Box>
      )
    } catch {
      return go.fallback(e)
    }
  })
}
