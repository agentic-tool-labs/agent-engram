import type { HistoryVersion, LensRecall, LensState } from '../shared/state'

export const MAX_RECALLS = 20

export const LENS_INITIAL: LensState = {
  recalls: [],
  history: {},
  selected: {},
  paneOpen: false,
  autoOpened: false,
}

export const addRecall = (state: LensState, recall: LensRecall): LensState => ({
  ...state,
  recalls: [recall, ...state.recalls].slice(0, MAX_RECALLS),
})

const pad = (n: number) => String(n).padStart(2, '0')

/** Local `HH:mm:ss` of an epoch in milliseconds. */
export function clock(ms: number): string {
  const d = new Date(ms)
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`
}

/** Local `yyyy-MM-dd HH:mm:ss` of an epoch in seconds, the resolution the store keeps. */
export function stamp(seconds: number): string {
  const d = new Date(seconds * 1000)
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${clock(seconds * 1000)}`
}

export function headerLine(r: LensRecall): string {
  const parts = [`"${r.query}"`]
  if (r.isError) parts.push('error')
  else if (!r.parsed) parts.push('unparsed')
  else parts.push(r.coverage ?? '', `${r.factCount ?? r.facts.length} facts`)
  if (r.agentId !== undefined) parts.push(`subagent ${r.agentId.slice(0, 8)}`)
  parts.push(clock(r.at))
  return parts.join(' · ')
}

export function versionLine(v: HistoryVersion, index: number): string {
  const closed = v.valid_to === null ? '' : ` · closed ${stamp(v.valid_to)}: ${v.closed_reason ?? 'no reason recorded'}`
  return `v${index + 1} · ${stamp(v.valid_from)}${closed}`
}

/** The version the scrubber shows: the stored choice clamped into range, else the newest. */
export function selectedIndex(state: LensState, handle: string, count: number): number {
  const chosen = state.selected[handle]
  return Math.min(Math.max(chosen ?? count - 1, 0), count - 1)
}
