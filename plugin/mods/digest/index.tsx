import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register } from 'claude-code'
import { EDIT_TOOLS, SHARED_INITIAL, modApi } from '../shared/client'
import type { ModIo } from '../shared/client'
import { ANY_SESSION_START, ANY_TURN_COMPLETE } from '../shared/events'
import {
  DIGEST_INITIAL,
  DIGEST_SYSTEM,
  EVIDENCE,
  NO_SELECTION,
  PANE,
  buildTranscript,
  digestEvery,
  fingerprint,
  parseCandidates,
  shouldDigest,
} from './digest'

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

/** Reads what was said since the last digest, asks for statements, drops the ones recall already covers. */
async function runDigest($: EngineInterface): Promise<void> {
  try {
    const messages = await $.session.messages()
    const { seenMessages, lastSeen } = await read($, DIGEST)
    // The mark counts only while the row before it is still the one last read; a compaction
    // that rewrote the conversation moves rows under the count, and then everything is new.
    const isMarkValid = seenMessages > 0 && seenMessages <= messages.length && fingerprint(messages[seenMessages - 1]!) === lastSeen
    const fresh = isMarkValid ? messages.slice(seenMessages) : messages
    const last = messages.at(-1)
    await update($, DIGEST, (s) => ({ ...s, seenMessages: messages.length, lastSeen: last === undefined ? '' : fingerprint(last) }))

    const transcript = buildTranscript(fresh)
    if (transcript === '') return

    const reply = await $.model.complete({ model: 'haiku', system: DIGEST_SYSTEM, prompt: transcript, maxTokens: 600 })
    if (!reply.isAnswered) return
    const proposed = parseCandidates(reply.text)
    if (proposed.length === 0) return

    const io = bindIo($)
    const sessionId = await io.sessionId()
    const known = await Promise.all(
      proposed.map(async (statement) => {
        const found = await modApi(io, 'recall', { session_id: sessionId, query: statement, budget_tokens: 100 }, { mod: MOD })
        return found.ok && found.value.coverage === 'high'
      }),
    )
    const candidates = proposed.filter((_, i) => !known[i]).map((text) => ({ text, ticked: false }))
    if (candidates.length === 0) return

    await update($, DIGEST, (s) => ({ ...s, candidates }))
    await $.ui.open({ id: PANE, title: 'Memory candidates' })
    $.ui.toast(`${candidates.length} memory candidates — /digest-review`)
  } catch {
    // A digest is a convenience; nothing it hits may surface.
  }
}

/** Saves the ticked candidates, one `remember` each, and reports the outcome in a single toast. */
async function saveTicked($: EngineInterface): Promise<void> {
  const { candidates } = await read($, DIGEST)
  const ticked = candidates.filter((c) => c.ticked)
  if (ticked.length === 0) return

  const io = bindIo($)
  const sessionId = await io.sessionId()
  const saved: string[] = []
  const failed: string[] = []
  const failedTexts = new Set<string>()
  for (const { text } of ticked) {
    const res = await modApi(io, 'remember', { session_id: sessionId, statement: text, evidence: EVIDENCE }, { mod: MOD })
    if (res.ok) saved.push(`[${res.value.handle}]`)
    else {
      failed.push(`Not saved: "${text}" (${res.reason})`)
      failedTexts.add(text)
    }
  }
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

  on('session.start', ANY_SESSION_START, async ($, e, next) => {
    // The event is shared with other mods; a failure here must not stop their hooks.
    try {
      await $.command.register({
        name: 'digest-review',
        description: 'Review the memory candidates the auto-digest proposed',
      })
      await $.command.register({
        name: 'remember-selection',
        description: 'Put the selected text in the prompt as "Remember this: …" so you can edit it and submit',
      })
    } catch {
      // The commands are missing, nothing else is affected.
    }
    return next(e)
  })

  on('tool.call', { tool: EDIT_TOOLS }, async ($, e, next) => {
    const ran = await next(e)
    await update($, DIGEST, (s) => ({ ...s, editedThisTurn: true }))
    return ran
  })

  on('turn.complete', ANY_TURN_COMPLETE, async ($, e, next) => {
    if (every === 0 || e.agentId !== undefined || e.isAborted || e.reason !== 'answer') return next(e)

    // The event is shared with other mods; a failure here must not stop their hooks.
    try {
      let isDue = false
      await update($, DIGEST, (s) => {
        const turns = s.turnsSinceDigest + 1
        isDue = shouldDigest(every, turns, s.editedThisTurn)
        return { ...s, turnsSinceDigest: isDue ? 0 : turns, editedThisTurn: false }
      })
      if (isDue) $.clock.after(0, () => runDigest($))
    } catch {
      // This turn is not counted.
    }
    return next(e)
  })

  on('command.run', { command: 'digest-review' }, async ($) => {
    const { candidates } = await read($, DIGEST)
    if (candidates.length === 0) return { text: 'No memory candidates to review.' }
    await $.ui.open({ id: PANE, title: 'Memory candidates', focus: true })
    return {}
  })

  on('command.run', { command: 'remember-selection' }, async ($) => {
    const selected = await $.ui.selection()
    if (selected === undefined || selected.text.trim() === '') {
      $.ui.toast(NO_SELECTION)
      return {}
    }
    await $.prompt.fill({ text: `Remember this: "${selected.text}"` })
    return {}
  })

  on('ui.render', { component: 'Pane', requestId: PANE }, async ($, e) => {
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
            <Text> {c.text}</Text>
          </Box>
        ))}
        <Box>
          <Button key="save" label="Save" variant="primary" onPress={() => saveTicked($)} />
          <Text> </Text>
          <Button key="skip" label="Skip" onPress={() => skipAll($)} />
        </Box>
      </Box>
    )
  })
}
