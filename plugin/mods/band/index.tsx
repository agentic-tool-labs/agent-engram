import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register, Timer } from 'claude-code'
import { SHARED_INITIAL, engramCli, modApi } from '../shared/client'
import { ANY_SESSION_START } from '../shared/events'
import type { ModIo } from '../shared/client'
import type { EmbedStatus } from '../shared/state'
import {
  BAND_INITIAL,
  LENS_INITIAL,
  SLOW_POLL_MS,
  asEmbedStatus,
  backlogText,
  coverageText,
  currentRecall,
  parseEmbedStatus,
  parseServerStatus,
  pollIntervalMs,
  rememberPrompt,
  statusText,
  wantsRememberButton,
} from './lines'
import { once } from '../shared/guard'

const SHARED = atom({ plugin: 'engram', key: 'shared' } as const, SHARED_INITIAL)
const LENS = atom({ plugin: 'engram', key: 'lens' } as const, LENS_INITIAL)
const BAND = atom({ plugin: 'engram', key: 'band' } as const, BAND_INITIAL)

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

type ServerStatus = { server: string; version: string }

/**
 * One look at the server, or at the CLI when the server cannot answer; a failed or unparsable
 * answer leaves nothing to show. Only the server's answer may be polled fast: it is cached for every
 * session, while the CLI is a process per session per poll.
 *
 * The status entry always comes from `status --json`, because only the CLI can say its version
 * differs from the server's, and it is asked at the slow cadence whatever the backlog is doing.
 */
async function poll($: EngineInterface, wantsStatusEntry: boolean, cached: { status?: ServerStatus; at?: number }): Promise<number> {
  const io = bindIo($)
  const served = await modApi<unknown>(io, 'embed-status', {}, { mod: 'band' })
  const fromServer = served.ok ? asEmbedStatus(served.value) : undefined
  let backlog: EmbedStatus | undefined
  if (fromServer !== undefined) {
    backlog = fromServer
  } else {
    const embedRun = await engramCli(io, ['embed', '--status', '--json'])
    backlog = embedRun?.exitCode === 0 ? parseEmbedStatus(embedRun.stdout) : undefined
  }
  const now = await io.now()
  if (wantsStatusEntry && (cached.at === undefined || now - cached.at >= SLOW_POLL_MS)) {
    cached.status = parseServerStatus((await engramCli(io, ['status', '--json']))?.stdout)
    cached.at = now
  }
  const status = wantsStatusEntry ? cached.status : undefined
  const polledAt = await io.now()
  await update($, BAND, (band) => ({ ...band, backlog, status, polledAt }))
  if (wantsStatusEntry) $.ui.status(statusText(status, backlog))
  return fromServer !== undefined ? pollIntervalMs(backlog) : SLOW_POLL_MS
}

export const register: Register = (on, options) => {
  const wantsStatusEntry = options.status_entry === true
  let timer: Timer | undefined
  let generation = 0
  const serverStatus: { status?: ServerStatus; at?: number } = {}

  const stop = () => {
    generation++
    timer?.cancel()
    timer = undefined
  }

  on('session.start', ANY_SESSION_START, async ($, e, next) => {
    const go = once(next)
    try {
      stop()
      const mine = generation
      // A line pinned by an earlier load outlives it, so turning the option off must replace it.
      if (!wantsStatusEntry) $.ui.status(undefined)
      const tick = async () => {
        if (mine !== generation) return
        let delay = pollIntervalMs(undefined)
        try {
          delay = await poll($, wantsStatusEntry, serverStatus)
        } catch {
          // A failed poll must not end the loop; the slow cadence is the retry.
        }
        if (mine === generation) timer = $.clock.after(delay, () => void tick())
      }
      void tick()
      return go(e)
    } catch {
      return go.fallback(e)
    }
  })

  on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => {
    const go = once(next)
    try {
      if (e.props.hasSurvey) return go(e)

      const lens = await read($, LENS)
      const band = await read($, BAND)
      const recall = currentRecall(lens)
      const lines = [recall === undefined ? undefined : coverageText(recall), backlogText(band.backlog)].filter(
        (line): line is string => line !== undefined,
      )
      if (lines.length === 0) return go(e)

      const { Box, Button, Text } = $.ui.resolve(e)
      const offersButton = recall !== undefined && wantsRememberButton(recall, band, e.props.isWorking)
      const shown = lines.slice(0, Math.max(1, e.props.maxRows))

      return (
        <Box flexDirection="column">
          {shown.map((line, i) => (
            <Box key={`line-${i}`}>
              <Text dimColor>{line} </Text>
              {i === 0 && recall !== undefined && offersButton ? (
                <Button
                  key="remember"
                  label="Remember the answer"
                  onPress={async () => {
                    // Two presses before a redraw both run; only the one that records the id submits.
                    // `update` retries its function, so `fresh` is decided on every attempt.
                    let fresh = false
                    await update($, BAND, (b) => {
                      fresh = !b.pressed.includes(recall.toolUseId)
                      return fresh ? { ...b, pressed: [...b.pressed, recall.toolUseId] } : b
                    })
                    if (fresh) await $.prompt.submit({ text: rememberPrompt(recall.query) })
                  }}
                />
              ) : null}
            </Box>
          ))}
          {await go(e)}
        </Box>
      )
    } catch {
      return go.fallback(e)
    }
  })
}
