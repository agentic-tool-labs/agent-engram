import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register, Timer } from 'claude-code'

import { ENGRAM_TOOLS, SHARED_INITIAL, modApi } from '../shared/client'
import { ANY_SESSION_START } from '../shared/events'
import { once } from '../shared/guard'
import type { ModIo } from '../shared/client'
import {
  FEED_UNAVAILABLE,
  GROUPS,
  POLL_MS,
  RETRY_MS,
  TAIL_INITIAL,
  applyResponse,
  callRow,
  failureOutcome,
  isShown,
  requestBody,
  rowLine,
} from './model'

const PANE = 'engram-tail'
const TITLE = 'Memory Tail'
const TAIL_COMMAND = 'engram-tail'

const TAIL = atom({ plugin: 'engram', key: 'tail' } as const, TAIL_INITIAL)
const SHARED = atom({ plugin: 'engram', key: 'shared' } as const, SHARED_INITIAL)

// Every Engram MCP tool shares the prefix the existing constants carry, so the pattern is built
// from them rather than retyped.
const ANY_ENGRAM_TOOL = { tool: new RegExp('^' + ENGRAM_TOOLS.recall.slice(0, -'recall'.length)) }

// Forwards only: the client owns every default, cache and error path.
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

type Outcome = { delayMs: number; stop: boolean }

/** One request and what it did to the pane; nothing is applied if the pane closed meanwhile. */
async function poll($: EngineInterface, scope: 'session' | 'all', current: () => boolean): Promise<Outcome> {
  const io = bindIo($)
  const state = await read($, TAIL)
  const sessionId = await io.sessionId()
  const result = await modApi(io, 'tail', requestBody(state, sessionId, scope), { mod: 'tail' })
  if (!current()) return { delayMs: POLL_MS, stop: true }

  const nowMs = await io.now()
  if (result.ok) {
    const response = result.value
    await update($, TAIL, (s) => {
      const applied = applyResponse(s, response, sessionId, { ms: nowMs })
      return response.events === null ? { ...applied, status: FEED_UNAVAILABLE } : applied
    })
    return { delayMs: POLL_MS, stop: false }
  }

  const outcome = failureOutcome(result)
  await update($, TAIL, (s) => ({ ...s, status: outcome.status }))
  return { delayMs: outcome.delayMs, stop: outcome.stop }
}

let timer: Timer | undefined
let generation = 0

function stop() {
  generation++
  timer?.cancel()
  timer = undefined
}

// There is no pane-closed event, so a pane the person closes by hand keeps polling until
// /engram-tail runs again or the session ends; the cost is one cheap request every two seconds.
function start($: EngineInterface, scope: 'session' | 'all') {
  stop()
  const mine = generation
  const tick = async () => {
    if (mine !== generation) return
    let outcome: Outcome = { delayMs: RETRY_MS, stop: false }
    try {
      outcome = await poll($, scope, () => mine === generation)
    } catch {
      // A failed tick must not end the loop; the slow cadence is the retry.
    }
    if (!outcome.stop && mine === generation) timer = $.clock.after(outcome.delayMs, () => void tick())
  }
  void tick()
}

async function open($: EngineInterface, scope: 'session' | 'all') {
  const opened = await $.ui.open({ id: PANE, title: TITLE })
  // A fresh open starts from the store's head: the cursors are dropped so the first request carries
  // none, and what happened while the pane was shut is never fetched. Rows already drawn stay.
  await update($, TAIL, (s) => ({
    paneOpen: opened.isPlaced,
    rows: s.rows,
    callSeq: s.callSeq,
    markerSeq: s.markerSeq,
    handles: s.handles,
    groups: s.groups,
  }))
  if (opened.isPlaced) start($, scope)
  return opened
}

export const register: Register = (on, options) => {
  const scope = options.tail_scope === 'all' ? 'all' : 'session'

  on('session.start', ANY_SESSION_START, async ($, e, next) => {
    const go = once(next)
    try {
      try {
        await $.command.register({ name: TAIL_COMMAND, description: 'Show or hide the Memory Tail pane' })
        if (options.tail_auto_open === true) $.clock.after(0, () => void open($, scope).catch(() => undefined))
      } catch {
        // another mod's session.start must still run
      }

      return go(e)
    } catch {
      return go.fallback(e)
    }
  })

  on('tool.call', ANY_ENGRAM_TOOL, async ($, e, next) => {
    const go = once(next)
    try {
      const ran = await go(e)

      // Observation only: whatever goes wrong here, the model still gets `ran` untouched. Under
      // scope all the telemetry events already carry these calls, so recording them would show
      // each twice.
      try {
        if (scope === 'session' && !('deny' in ran && typeof ran.deny === 'string')) {
          const state = await read($, TAIL)
          if (state.paneOpen) {
            const text = 'text' in ran && typeof ran.text === 'string' ? ran.text : ''
            const ms = await $.clock.now()
            await update($, TAIL, (s) => callRow(s, e.tool, e as unknown as Record<string, unknown>, text, ms).state)
          }
        }
      } catch {
        // fail open
      }

      return ran
    } catch {
      return go.fallback(e)
    }
  })

  on('command.run', { command: TAIL_COMMAND }, async ($, e, next) => {
    const go = once(next)
    try {
      const state = await read($, TAIL)

      if (state.paneOpen) {
        stop()
        await $.ui.close({ id: PANE })
        await update($, TAIL, (s) => ({ ...s, paneOpen: false }))

        return { text: 'Memory Tail closed.' }
      }

      const opened = await open($, scope)

      return { text: opened.isPlaced ? 'Memory Tail opened.' : `Memory Tail could not open: ${opened.reason}` }
    } catch {
      return go.fallback(e)
    }
  })

  on('ui.render', { component: 'Pane', requestId: PANE }, async ($, e, next) => {
    const go = once(next)
    try {
      const { Box, Button, Text } = $.ui.resolve(e)
      const state = await read($, TAIL)
      const nowMs = await $.clock.now()
      const shown = state.rows.filter((row) => isShown(row, state.groups))

      return (
        <Box flexDirection="column">
          <Box flexWrap="wrap">
            {GROUPS.map((group) => (
              // A button that does not fit starts the next line whole instead of being split inside.
              <Box key={`gb-${group.id}`} flexShrink={0}>
                <Button
                  key={`g-${group.id}`}
                  label={`${group.title} ${state.groups[group.id] ? '✓' : '✗'}`}
                  onPress={() => update($, TAIL, (s) => ({ ...s, groups: { ...s.groups, [group.id]: !s.groups[group.id] } }))}
                />
              </Box>
            ))}
          </Box>
          {state.status === undefined ? null : <Text dimColor>{state.status}</Text>}
          {shown.length === 0 ? (
            <Text dimColor>No memory activity yet.</Text>
          ) : (
            shown.map((row) => (
              <Text key={row.key} dimColor={row.kind === 'marker'} wrap="truncate-end">
                {rowLine(row, state.handles, nowMs)}
              </Text>
            ))
          )}
        </Box>
      )
    } catch {
      return go.fallback(e)
    }
  })
}
