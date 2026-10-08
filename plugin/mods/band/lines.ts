import type { BandState, EmbedStatus, LensRecall, LensState } from '../shared/state'

export const LENS_INITIAL: LensState = { recalls: [], history: {} }
export const BAND_INITIAL: BandState = { pressed: [] }

export const FAST_POLL_MS = 5_000
export const SLOW_POLL_MS = 60_000

/** The recall this turn made: the newest whose turn is the current one, if it carries a coverage. */
export function currentRecall(lens: LensState): LensRecall | undefined {
  if (lens.currentTurnId === undefined) return undefined
  const found = lens.recalls.find((r) => r.turnId === lens.currentTurnId)
  return found !== undefined && found.coverage !== undefined ? found : undefined
}

/** `memory <coverage> · <N> facts`, then each availability note verbatim. */
export function coverageText(recall: LensRecall): string {
  const count = recall.factCount ?? recall.facts.length
  const head = `memory ${recall.coverage} · ${count} ${count === 1 ? 'fact' : 'facts'}`
  return [head, ...recall.notes].join(' · ')
}

/** Offered only when memory had nothing, once per recall, and never mid-turn. */
export function wantsRememberButton(recall: LensRecall, band: BandState, isWorking: boolean): boolean {
  return recall.coverage === 'none' && !isWorking && !band.pressed.includes(recall.toolUseId)
}

export const rememberPrompt = (query: string) => `Once you've worked out the answer to "${query}", save it to memory.`

/** `embedding <embedded>/<total>`, then the eta and the CLI's own words for a backlog that is not running. */
export function backlogText(backlog: EmbedStatus | undefined): string | undefined {
  if (backlog === undefined || backlog.remaining <= 0) return undefined
  const parts = [`embedding ${backlog.embedded}/${backlog.total}`]
  if (backlog.eta) parts.push(backlog.eta)
  if (backlog.backlog.reason) parts.push(backlog.backlog.reason)
  else if (backlog.backlog.state !== 'running') parts.push(backlog.backlog.state)
  return parts.join(' · ')
}

/** `undefined` when the output is not an embed status object. */
export function parseEmbedStatus(stdout: string | undefined): EmbedStatus | undefined {
  if (stdout === undefined) return undefined
  try {
    const v = JSON.parse(stdout) as Partial<EmbedStatus> | null
    if (
      v !== null &&
      typeof v === 'object' &&
      typeof v.embedded === 'number' &&
      typeof v.total === 'number' &&
      typeof v.remaining === 'number' &&
      typeof v.backlog === 'object' &&
      v.backlog !== null &&
      typeof v.backlog.state === 'string'
    ) {
      return v as EmbedStatus
    }
  } catch {
    return undefined
  }
  return undefined
}

/** `status --json` exits 1 unless the server is running, so only its stdout is read. */
export function parseServerStatus(stdout: string | undefined): { server: string; version: string } | undefined {
  if (stdout === undefined) return undefined
  try {
    const v = JSON.parse(stdout) as { Server?: unknown; Version?: unknown } | null
    if (v !== null && typeof v === 'object' && typeof v.Server === 'string') {
      return { server: v.Server, version: typeof v.Version === 'string' ? v.Version : '' }
    }
  } catch {
    return undefined
  }
  return undefined
}

export function statusText(
  status: { server: string; version: string } | undefined,
  backlog: EmbedStatus | undefined,
): string | undefined {
  if (status === undefined) return undefined
  const head = status.version === '' ? `engram ${status.server}` : `engram ${status.server} ${status.version}`
  return backlog !== undefined && backlog.remaining > 0 ? `${head} · embed ${backlog.embedded}/${backlog.total}` : head
}

/** Fast while a backlog is draining under a running loop; otherwise, and after any failure, slow. */
export function pollIntervalMs(backlog: EmbedStatus | undefined): number {
  return backlog !== undefined && backlog.remaining > 0 && backlog.backlog.state === 'running' ? FAST_POLL_MS : SLOW_POLL_MS
}
