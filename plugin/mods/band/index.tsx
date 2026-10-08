import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register, Timer } from 'claude-code'
import { SHARED_INITIAL, engramCli } from '../shared/client'
import { ANY_SESSION_START } from '../shared/events'
import type { ModIo } from '../shared/client'
import {
  BAND_INITIAL,
  LENS_INITIAL,
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

/** One look at the CLI; a failed or unparsable answer leaves nothing to show. */
async function poll($: EngineInterface, wantsStatusEntry: boolean): Promise<number> {
  const io = bindIo($)
  const embedRun = await engramCli(io, ['embed', '--status', '--json'])
  const backlog = embedRun?.exitCode === 0 ? parseEmbedStatus(embedRun.stdout) : undefined
  const status = wantsStatusEntry ? parseServerStatus((await engramCli(io, ['status', '--json']))?.stdout) : undefined
  const polledAt = await io.now()
  await update($, BAND, (band) => ({ ...band, backlog, status, polledAt }))
  if (wantsStatusEntry) $.ui.status(statusText(status, backlog))
  return pollIntervalMs(backlog)
}

export const register: Register = (on, options) => {
  const wantsStatusEntry = options.status_entry === true
  let timer: Timer | undefined
  let generation = 0

  const stop = () => {
    generation++
    timer?.cancel()
    timer = undefined
  }

  on('session.start', ANY_SESSION_START, async ($, e, next) => {
    stop()
    const mine = generation
    const tick = async () => {
      if (mine !== generation) return
      let delay = pollIntervalMs(undefined)
      try {
        delay = await poll($, wantsStatusEntry)
      } catch {
        // A failed poll must not end the loop; the slow cadence is the retry.
      }
      if (mine === generation) timer = $.clock.after(delay, () => void tick())
    }
    void tick()
    return next(e)
  })

  on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => {
    if (e.props.hasSurvey) return next(e)

    const lens = await read($, LENS)
    const band = await read($, BAND)
    const recall = currentRecall(lens)
    const lines = [recall === undefined ? undefined : coverageText(recall), backlogText(band.backlog)].filter(
      (line): line is string => line !== undefined,
    )
    if (lines.length === 0) return next(e)

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
      </Box>
    )
  })
}
