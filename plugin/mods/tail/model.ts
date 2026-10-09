import type { TailGroup, TailRow, TailState } from '../shared/state'
import type { ApiFailure, TailEventRecord, TailRequest, TailResponse } from '../shared/types'
import { AUTO_EVIDENCE, EVIDENCE } from '../digest/digest'
import { clock, stamp } from '../lens/model'
import { parseDigest } from '../lens/parser'

export const MAX_ROWS = 200
export const POLL_MS = 2_000
export const RETRY_MS = 15_000
// A cap on each free-text payload before layout, not a layout width: the host cuts a line to the
// real pane width, which the mod cannot read.
const CLIP = 120
const MAX_HANDLES = 500
const MAX_DETAIL_CHARS = 60

export const GROUPS: readonly { id: TailGroup; title: string }[] = [
  { id: 'writes', title: 'Writes' },
  { id: 'retractions', title: 'Retractions' },
  { id: 'reads', title: 'Reads' },
  { id: 'remember', title: 'Remember' },
  { id: 'mods', title: 'Mods' },
  { id: 'sessions', title: 'Sessions' },
  { id: 'maintenance', title: 'Maintenance' },
  { id: 'other', title: 'Other' },
]

export const TAIL_INITIAL: TailState = {
  paneOpen: false,
  filterOpen: false,
  rows: [],
  callSeq: 0,
  markerSeq: 0,
  handles: [],
  groups: {
    writes: true,
    retractions: true,
    reads: true,
    remember: true,
    mods: true,
    sessions: false,
    maintenance: false,
    other: true,
  },
}

/** Which toggle a kind or tool name belongs to; anything unlisted is `other`. */
const GROUP_OF: Record<string, TailGroup> = {}
for (const name of ['recall', 'browse', 'expand', 'timeline', 'navigate', 'judge']) GROUP_OF[name] = 'reads'
for (const name of ['remember', 'revise', 'user-prompt', 'pin', 'unpin']) GROUP_OF[name] = 'remember'
GROUP_OF['forget'] = 'retractions'
GROUP_OF['mod-call'] = 'mods'
for (const name of [
  'session-start',
  'subagent-start',
  'session-open',
  'pre-compact',
  'post-compact',
  'memory-guard',
  'lookup-nudge',
  'tool-observed',
  'file-touched',
]) {
  GROUP_OF[name] = 'sessions'
}
for (const name of ['index', 'index_repo', 'embedding', 'sync', 'enrollment', 'report', 'server-start', 'server-stop', 'digest']) {
  GROUP_OF[name] = 'maintenance'
}

export const groupOf = (name: string): TailGroup => (Object.hasOwn(GROUP_OF, name) ? GROUP_OF[name]! : 'other')

/** Newest first: time, then rank, then id. */
export function compareRows(a: TailRow, b: TailRow): number {
  return b.ms - a.ms || b.rank - a.rank || b.id - a.id
}

/** One line: whitespace collapsed, cut at `CLIP` characters and marked. */
export function clip(text: string): string {
  const flat = text.replace(/\s+/g, ' ').trim()
  return flat.length <= CLIP ? flat : `${flat.slice(0, CLIP)}…`
}

/** Adds rows, replacing any with the same key, and keeps the newest `MAX_ROWS`. */
export function insertRows(rows: readonly TailRow[], added: readonly TailRow[]): TailRow[] {
  const byKey = new Map<string, TailRow>()
  for (const row of rows) byKey.set(row.key, row)
  for (const row of added) byKey.set(row.key, row)
  return [...byKey.values()].sort(compareRows).slice(0, MAX_ROWS)
}

/** Marks every write row for `handle` as retracted. */
export function markRetracted(rows: readonly TailRow[], handle: string): TailRow[] {
  return rows.map((row) => (row.kind === 'write' && row.handle === handle && !row.retracted ? { ...row, retracted: true } : row))
}

const withHandles = (handles: readonly string[], added: readonly (string | undefined)[]): string[] => {
  const merged = [...handles]
  for (const handle of added) if (handle !== undefined && !merged.includes(handle)) merged.push(handle)
  return merged.slice(-MAX_HANDLES)
}

const marker = (state: TailState, ms: number, text: string): TailRow => ({
  key: `m${state.markerSeq}`,
  ms,
  rank: 3,
  id: state.markerSeq,
  kind: 'marker',
  group: null,
  label: '',
  text,
  mine: false,
  retracted: false,
})

type Server = { ms: number }

const present = (parts: (string | null | undefined)[]) => parts.filter((p): p is string => p !== null && p !== undefined && p !== '')

/** The fields of an activity row, shortest and most fixed first, free text last. */
export function eventText(record: TailEventRecord): string {
  const quoted = (value: string | null | undefined) => (value === null || value === undefined ? undefined : `"${clip(value)}"`)
  const has = (value: number | null | undefined): value is number => value !== null && value !== undefined
  const coverage = (lead: boolean) =>
    record.coverage === undefined || record.coverage === null ? undefined : lead ? `· ${record.coverage}` : record.coverage

  switch (record.kind) {
    case 'recall':
      return present([has(record.fact_count) ? `${record.fact_count} facts` : undefined, coverage(has(record.fact_count)), quoted(record.query)]).join(' ')
    case 'mod-call':
      return present([`${record.mod ?? '?'} · ${record.tool ?? '?'}`, ...(record.tool === 'recall' ? [coverage(true), quoted(record.query)] : [])]).join(' ')
    case 'session-start':
    case 'subagent-start':
      return present([
        present([has(record.long_term_fact_count) ? `${record.long_term_fact_count} facts` : undefined, has(record.tokens_returned) ? `${record.tokens_returned} tok` : undefined]).join(' · '),
        record.kind === 'subagent-start' ? record.agent_type : undefined,
      ]).join(' ')
    default:
      return present([quoted(record.query), record.tool, record.path, record.phase, record.repo, record.relation, record.decision, record.mode]).join(' · ')
  }
}

function eventRow(epoch: string, seq: number, record: TailEventRecord, sessionId: string): TailRow {
  const parsed = Date.parse(record.timestamp)
  return {
    key: `e${epoch}:${seq}`,
    ms: Number.isNaN(parsed) ? 0 : parsed,
    rank: 2,
    id: seq,
    kind: 'event',
    group: groupOf(record.kind),
    label: record.kind,
    text: eventText(record),
    mine: record.session_id === sessionId,
    retracted: false,
  }
}

/** The sorted, deduped response folded into state: new rows, markers and the next cursors. */
export function applyResponse(state: TailState, res: TailResponse, sessionId: string, local: Server): TailState {
  const first = state.after === undefined
  let next: TailState = { ...state, status: undefined }

  const added: TailRow[] = []
  const notes: string[] = []

  if (first) {
    next = { ...next, after: res.head, closedAfter: res.now }
    if (res.events !== null) next = { ...next, epoch: res.events.epoch, eventAfter: res.events.head }
    return next
  }

  if (res.head < (state.after ?? 0)) {
    notes.push(`store rewound — showing writes from f${res.head}`)
  }
  next = { ...next, after: res.head }

  for (const w of res.writes.rows) {
    added.push({
      key: `w${w.id}`,
      ms: w.created_at * 1000,
      rank: 0,
      id: w.id,
      kind: 'write',
      group: 'writes',
      label: w.origin === 'note' && w.evidence === EVIDENCE ? 'digest' : w.origin === 'note' && w.evidence === AUTO_EVIDENCE ? 'digest·auto' : w.origin,
      handle: w.handle,
      qualifier: w.replaces === null ? undefined : `← ${w.replaces}`,
      text: clip(w.body),
      mine: w.this_session,
      retracted: !w.live,
    })
  }
  if (res.writes.skipped !== null && res.writes.skipped > 0) notes.push(`… ${res.writes.skipped} more writes not shown`)

  let closedAfter = state.closedAfter ?? res.now
  const retracted: string[] = []
  for (const r of res.retractions.rows) {
    closedAfter = Math.max(closedAfter, r.retracted_at)
    retracted.push(r.handle)
    added.push({
      key: `r${r.id}`,
      ms: r.retracted_at * 1000,
      rank: 1,
      id: r.id,
      kind: 'retraction',
      group: 'retractions',
      label: 'retract',
      handle: r.handle,
      qualifier: r.origin,
      text: `${clip(r.body)} — ${r.reason}`,
      mine: r.this_session,
      retracted: false,
    })
  }
  next = { ...next, closedAfter }

  if (res.events !== null) {
    if (state.epoch !== undefined && state.epoch !== res.events.epoch) {
      notes.push('event feed restarted')
    } else {
      for (const e of res.events.rows) added.push(eventRow(res.events.epoch, e.seq, e.record, sessionId))
      if (res.events.skipped !== null && res.events.skipped > 0) notes.push(`… ${res.events.skipped} more events not shown`)
    }
    next = { ...next, epoch: res.events.epoch, eventAfter: res.events.head }
  }

  let rows = insertRows(next.rows, added)
  for (const handle of retracted) rows = markRetracted(rows, handle)
  let seq = next.markerSeq
  for (const text of notes) {
    seq += 1
    rows = insertRows(rows, [marker({ ...next, markerSeq: seq }, local.ms, text)])
  }
  const mineHandles = added.filter((row) => row.mine && row.handle !== undefined).map((row) => row.handle)
  return { ...next, rows, markerSeq: seq, handles: withHandles(next.handles, mineHandles) }
}

/** What an Engram MCP tool call looks like on the pane, from the call and its result. */
export function callRow(
  state: TailState,
  tool: string,
  input: Readonly<Record<string, unknown>>,
  resultText: string,
  ms: number,
): { state: TailState; row: TailRow } {
  const label = tool.slice(tool.lastIndexOf('engram_') + 'engram_'.length)
  const text = (value: unknown) => (typeof value === 'string' && value !== '' ? value : undefined)
  const firstHandle = /\[(f\d+)\]/.exec(resultText)?.[1]
  let handle: string | undefined
  let body: string

  if (label === 'recall') {
    const digest = parseDigest(resultText)
    const query = text(input['query']) ?? ''
    const facts = digest.parsed ? `${digest.factCount ?? digest.facts.length} facts` : undefined
    body = present([facts, digest.parsed && digest.coverage !== undefined ? `· ${digest.coverage}` : undefined, `"${clip(query)}"`]).join(' ')
  } else if (label === 'remember' || label === 'revise') {
    handle = firstHandle
    body = clip(text(input['statement']) ?? '')
  } else if (label === 'forget') {
    handle = text(input['fact_id'])
    body = ''
  } else {
    const fields = [text(input['query']), text(input['relation']), text(input['fact_id']) ?? text(input['handle'])]
    body = fields
      .filter((f): f is string => f !== undefined)
      .map(clip)
      .join(' · ')
  }

  const seq = state.callSeq + 1
  const row: TailRow = {
    key: `c${seq}`,
    ms,
    rank: 2,
    id: seq,
    kind: 'call',
    group: groupOf(label),
    label,
    ...(handle === undefined ? {} : { handle }),
    text: body,
    mine: true,
    retracted: false,
  }
  const rows = label === 'forget' && handle !== undefined ? markRetracted(state.rows, handle) : state.rows
  return {
    row,
    state: {
      ...state,
      callSeq: seq,
      rows: insertRows(rows, [row]),
      handles: withHandles(state.handles, [handle]),
    },
  }
}

export function isShown(row: TailRow, groups: TailState['groups']): boolean {
  return row.group === null || groups[row.group]
}

const sameDay = (a: Date, b: Date) =>
  a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate()

/** The local `yyyy-MM-dd` of an epoch in milliseconds: the date part of lens's `stamp`. */
const dateOf = (ms: number) => stamp(Math.floor(ms / 1000)).slice(0, 10)

export type RowLayout = { head: string; detail?: string }

/**
 * A row as up to two lines, most important first, so a host that cuts a line at the pane width
 * always removes the least important part. The head is the session mark, the state mark, the time
 * and the label, then a qualifier where there is one; the detail is the handle then the free text.
 */
export function layoutRow(row: TailRow, handles: readonly string[]): RowLayout {
  if (row.kind === 'marker') return { head: `  ${row.text}` }

  const known = row.mine || (row.handle !== undefined && handles.includes(row.handle))
  const marks = `${known ? '•' : ' '}${row.kind === 'retraction' || row.retracted ? '✗' : ' '}`
  const head = present([`${marks} ${clock(row.ms)}`, row.label, row.qualifier]).join(' ')
  const detail = present([row.handle, row.text]).join(' ')
  return detail === '' ? { head } : { head, detail }
}

export type DisplayItem =
  | { kind: 'separator'; key: string; text: string }
  | { kind: 'row'; row: TailRow; layout: RowLayout }

/**
 * The rows the filter lets through, each laid out, with a date line above the first when it is not
 * today and wherever the date changes between neighbours. Separators belong to the display only.
 */
export function displayItems(rows: readonly TailRow[], groups: TailState['groups'], handles: readonly string[], nowMs: number): DisplayItem[] {
  const items: DisplayItem[] = []
  let previous: Date | undefined
  for (const row of rows) {
    if (!isShown(row, groups)) continue
    const when = new Date(row.ms)
    const needsSeparator = previous === undefined ? !sameDay(when, new Date(nowMs)) : !sameDay(when, previous)
    if (needsSeparator) items.push({ kind: 'separator', key: `sep-${row.key}`, text: `── ${dateOf(row.ms)} ──` })
    items.push({ kind: 'row', row, layout: layoutRow(row, handles) })
    previous = when
  }
  return items
}

export const filterLabel = (state: TailState): string =>
  `Filter ${GROUPS.filter((g) => state.groups[g.id]).length}/${GROUPS.length} ${state.filterOpen ? '▴' : '▾'}`

export const toggleLabel = (on: boolean, title: string): string => `${on ? '✓' : '·'} ${title}`

/** What stands in place of rows: nothing recorded yet, or everything filtered out. */
export function emptyText(state: TailState): string {
  const hidden = state.rows.filter((row) => row.kind !== 'marker').length
  return hidden > 0 ? `All ${hidden} rows hidden by the filter.` : 'No activity yet.'
}

/** The next poll's request: no cursors until the first answer has set them. */
export function requestBody(state: TailState, sessionId: string, scope: 'session' | 'all'): TailRequest {
  if (state.after === undefined) return { session_id: sessionId, scope }
  return {
    session_id: sessionId,
    scope,
    after: state.after,
    ...(state.closedAfter === undefined ? {} : { closed_after: state.closedAfter }),
    ...(state.epoch === undefined ? {} : { event_epoch: state.epoch }),
    ...(state.eventAfter === undefined ? {} : { event_after: state.eventAfter }),
  }
}

export type Failure = { status: string; delayMs: number; stop: boolean }

/**
 * What a failed request leaves on the status line and how the loop carries on. The action comes
 * first and every word is short, because the status is the one line the host may wrap. A second
 * line, after a newline, is server text and is drawn clipped.
 */
export function failureOutcome(failure: ApiFailure): Failure {
  switch (failure.reason) {
    case 'server-down':
      return { status: 'Server down · /engram:start', delayMs: RETRY_MS, stop: false }
    case 'timeout':
      return { status: 'Server slow · retrying', delayMs: RETRY_MS, stop: false }
    case 'not-initialised':
      return { status: 'Not initialised · engram init', delayMs: RETRY_MS, stop: false }
    case 'not-found':
      return { status: 'Server too old for the tail · update, then /engram:restart', delayMs: RETRY_MS, stop: true }
    case 'unsupported':
      return { status: 'Server has no mod API', delayMs: RETRY_MS, stop: true }
    case 'bad-request':
      return {
        status: failure.detail === undefined ? 'Tail request rejected:' : `Tail request rejected:\n${failure.detail.slice(0, MAX_DETAIL_CHARS)}`,
        delayMs: RETRY_MS,
        stop: false,
      }
    default:
      return { status: 'Tail error · retrying', delayMs: RETRY_MS, stop: false }
  }
}

export const FEED_UNAVAILABLE = 'Activity feed off · writes only'
