import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register } from 'claude-code'

import { ENGRAM_TOOLS, SHARED_INITIAL, modApi } from '../shared/client'
import { ANY_SESSION_START, ANY_TURN_START } from '../shared/events'
import type { ModIo } from '../shared/client'
import type { LensRecall } from '../shared/state'
import { LENS_INITIAL, addRecall, headerLine, selectedIndex, versionLine } from './model'
import { parseDigest } from './parser'
import { once } from '../shared/guard'

const PANE = 'engram-lens'
const TITLE = 'Memory Lens'

const LENS = atom({ plugin: 'engram', key: 'lens' } as const, LENS_INITIAL)
const SHARED = atom({ plugin: 'engram', key: 'shared' } as const, SHARED_INITIAL)

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

// The host does not namespace a mod's commands and the plugin test kit rejects a colon in the name, so the plugin name is joined with a hyphen.
const LENS_COMMAND = 'engram-lens'

export const register: Register = (on, options) => {
  on('session.start', ANY_SESSION_START, async ($, e, next) => {
    const go = once(next)
    try {
      try {
        await $.command.register({ name: LENS_COMMAND, description: 'Show or hide the Memory Lens pane' })
      } catch {
        // another mod's session.start must still run
      }

      return go(e)
    } catch {
      return go.fallback(e)
    }
  })

  on('turn.start', ANY_TURN_START, async ($, e, next) => {
    const go = once(next)
    try {
      try {
        await update($, LENS, (s) => ({ ...s, currentTurnId: e.turnId }))
      } catch {
        // another mod's turn.start must still run
      }

      return go(e)
    } catch {
      return go.fallback(e)
    }
  })

  on('tool.call', { tool: ENGRAM_TOOLS.recall }, async ($, e, next) => {
    const go = once(next)
    try {
      const ran = await go(e)

      // The Lens only watches: whatever goes wrong here, the model still gets `ran` untouched.
      try {
        if ('deny' in ran && typeof ran.deny === 'string') return ran

        const text = 'text' in ran && typeof ran.text === 'string' ? ran.text : ''
        const isError = 'isError' in ran && ran.isError === true
        const state = await read($, LENS)
        const base = {
          toolUseId: e.tool_use_id,
          ...(e.agentId === undefined ? {} : { agentId: e.agentId }),
          ...(state.currentTurnId === undefined ? {} : { turnId: state.currentTurnId }),
          at: await bindIo($).now(),
        }
        const queryArg = (e as { query?: unknown }).query
        const recall: LensRecall = isError
          ? { ...base, query: typeof queryArg === 'string' ? queryArg : '', parsed: false, isError, notes: [], facts: [], raw: text }
          : { ...base, ...parseDigest(text), isError }

        await update($, LENS, (s) => addRecall(s, recall))

        if (options.lens_auto_open === true && !state.autoOpened) {
          await update($, LENS, (s) => ({ ...s, autoOpened: true }))
          const opened = await $.ui.open({ id: PANE, title: TITLE })
          await update($, LENS, (s) => ({ ...s, paneOpen: opened.isPlaced }))
        }
      } catch {
        // fail open
      }

      return ran
    } catch {
      return go.fallback(e)
    }
  })

  on('command.run', { command: LENS_COMMAND }, async ($, e, next) => {
    const go = once(next)
    try {
      const state = await read($, LENS)

      if (state.paneOpen) {
        await $.ui.close({ id: PANE })
        await update($, LENS, (s) => ({ ...s, paneOpen: false }))

        return { text: 'Memory Lens closed.' }
      }

      const opened = await $.ui.open({ id: PANE, title: TITLE })
      await update($, LENS, (s) => ({ ...s, paneOpen: opened.isPlaced }))

      return { text: opened.isPlaced ? 'Memory Lens opened.' : `Memory Lens could not open: ${opened.reason}` }
    } catch {
      return go.fallback(e)
    }
  })

  on('ui.render', { component: 'Pane', requestId: PANE }, async ($, e, next) => {
    const go = once(next)
    try {
      const { Box, Button, Text } = $.ui.resolve(e)
      const state = await read($, LENS)

      const loadHistory = async (handle: string) => {
        const current = (await read($, LENS)).history[handle]
        if (current !== undefined && current !== 'failed') return

        await update($, LENS, (s) => ({ ...s, history: { ...s.history, [handle]: 'loading' as const } }))
        const result = await modApi(bindIo($), 'history', { fact_id: handle }, { mod: 'lens' })
        // A server that is down or slow may be back by the next press; one that lacks the op or
        // does not know the handle will answer the same way again.
        const retriable = !result.ok && (result.reason === 'server-down' || result.reason === 'timeout')
        await update($, LENS, (s) => ({
          ...s,
          history: {
            ...s.history,
            [handle]: result.ok ? result.value : retriable ? ('failed' as const) : ('unavailable' as const),
          },
        }))
      }

      const step = (handle: string, count: number, by: number) =>
        update($, LENS, (s) => ({
          ...s,
          selected: { ...s.selected, [handle]: Math.min(Math.max(selectedIndex(s, handle, count) + by, 0), count - 1) },
        }))

      if (state.recalls.length === 0) {
        return <Text dimColor>No Engram recalls yet.</Text>
      }

      return (
        <Box flexDirection="column" gap={1}>
          {state.recalls.map((recall) => (
            <Box key={recall.toolUseId} flexDirection="column">
              <Text bold>{headerLine(recall)}</Text>
              {recall.isError || !recall.parsed ? <Text dimColor>{recall.raw}</Text> : null}
              {recall.notes.map((note) => (
                <Text key={note} dimColor>{note}</Text>
              ))}
              {recall.facts.map((fact) => {
                const view = state.history[fact.handle]
                return (
                  <Box key={fact.handle} flexDirection="column">
                    <Text>
                      {fact.handle} {fact.body}
                      <Text dimColor> ({fact.meta})</Text>
                    </Text>
                    {fact.versions > 1 && (view === undefined || view === 'failed') ? (
                      <Button key={`h-${recall.toolUseId}-${fact.handle}`} label="History" onPress={() => loadHistory(fact.handle)} />
                    ) : null}
                    {view === 'loading' ? <Text dimColor>loading history…</Text> : null}
                    {view === 'failed' ? <Text dimColor>history lookup failed (server down or slow) — press History to retry</Text> : null}
                    {view === 'unavailable' ? <Text dimColor>history unavailable (server down / older Engram)</Text> : null}
                    {typeof view === 'object' ? (
                      <Box flexDirection="column" paddingLeft={2}>
                        {view.versions.map((v, i) => (
                          <Text key={`v${i}`} dimColor={i !== selectedIndex(state, fact.handle, view.versions.length)}>{versionLine(v, i)}</Text>
                        ))}
                        <Text>{view.versions[selectedIndex(state, fact.handle, view.versions.length)]?.body ?? ''}</Text>
                        {view.versions.length > 1 ? (
                          <Box>
                            <Button
                              key={`prev-${recall.toolUseId}-${fact.handle}`}
                              label="◀ Prev"
                              onPress={() => step(fact.handle, view.versions.length, -1)}
                            />
                            <Button
                              key={`next-${recall.toolUseId}-${fact.handle}`}
                              label="Next ▶"
                              onPress={() => step(fact.handle, view.versions.length, 1)}
                            />
                          </Box>
                        ) : null}
                      </Box>
                    ) : null}
                  </Box>
                )
              })}
              {recall.gaps === undefined ? null : <Text dimColor>gaps: {recall.gaps}</Text>}
            </Box>
          ))}
        </Box>
      )
    } catch {
      return go.fallback(e)
    }
  })
}
