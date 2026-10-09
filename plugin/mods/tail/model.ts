import type { TailGroup, TailRow, TailState } from '../shared/state'
import type { ApiFailure, TailEventRecord, TailRequest, TailResponse } from '../shared/types'
import { AUTO_EVIDENCE, EVIDENCE } from '../digest/digest'
import { clock, stamp } from '../lens/model'
import { parseDigest } from '../lens/parser'

export const MAX_ROWS = 200
export const POLL_MS = 2_000
export const RETRY_MS = 15_000
const CLIP = 120
const MAX_HANDLES = 500

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

export function eventText(record: TailEventRecord): string {
  const quoted = (value: string | null | undefined) => (value === null || value === undefined ? undefined : `"${clip(value)}"`)
  const present = (parts: (string | null | undefined)[]) => parts.filter((p): p is string => p !== null && p !== undefined && p !== '')

  switch (record.kind) {
    case 'recall':
      return present([quoted(record.query), record.fact_count === null || record.fact_count === undefined ? undefined : `${record.fact_count} facts`, record.coverage]).join(' · ')
    case 'mod-call':
      return present([
        `${record.mod ?? '?'} · ${record.tool ?? '?'}`,
        ...(record.tool === 'recall' ? [quoted(record.query), record.coverage] : []),
      ]).join(' · ')
    case 'session-start':
    case 'subagent-start':
      return present([
        record.long_term_fact_count === null || record.long_term_fact_count === undefined ? undefined : `${record.long_term_fact_count} facts`,
        record.tokens_returned === null || record.tokens_returned === undefined ? undefined : `${record.tokens_returned} tokens`,
        record.kind === 'subagent-start' ? record.agent_type : undefined,
      ]).join(' · ')
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
      text: `${clip(w.body)}${w.replaces === null ? '' : ` ← ${w.replaces}`}`,
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
      text: `${r.origin} ${clip(r.body)} — ${r.reason}`,
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
    body = [`"${clip(query)}"`, digest.parsed ? `${digest.factCount ?? digest.facts.length} facts` : undefined, digest.parsed ? digest.coverage : undefined]
      .filter((p): p is string => p !== undefined)
      .join(' · ')
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

/** Today's rows read `HH:mm:ss`; any other day carries its date. */
export function whenText(ms: number, nowMs: number): string {
  return sameDay(new Date(ms), new Date(nowMs)) ? clock(ms) : stamp(Math.floor(ms / 1000))
}

/** One row as a line: `•` when it is known to be this session's. */
export function rowLine(row: TailRow, handles: readonly string[], nowMs: number): string {
  if (row.kind === 'marker') return `  ${row.text}`
  const known = row.mine || (row.handle !== undefined && handles.includes(row.handle))
  const parts = [whenText(row.ms, nowMs), row.label, row.handle, row.text, row.retracted ? '(retracted)' : undefined]
  return `${known ? '•' : ' '} ${parts.filter((p): p is string => p !== undefined && p !== '').join(' ')}`
}

export type Failure = { status: string; delayMs: number; stop: boolean }

/** What a failed request leaves on the status line and how the loop carries on. */
export function failureOutcome(failure: ApiFailure): Failure {
  switch (failure.reason) {
    case 'server-down':
      return { status: 'Engram server not reachable — /engram:start', delayMs: RETRY_MS, stop: false }
    case 'timeout':
      return { status: 'Engram server slow to answer', delayMs: RETRY_MS, stop: false }
    case 'not-initialised':
      return { status: 'Engram home not initialised — engram init', delayMs: RETRY_MS, stop: false }
    case 'not-found':
      return {
        status: 'the running Engram server predates the memory tail — update, then /engram:restart',
        delayMs: RETRY_MS,
        stop: true,
      }
    case 'unsupported':
      return { status: 'the running Engram server has no mod API', delayMs: RETRY_MS, stop: true }
    case 'bad-request':
      return { status: `memory tail request rejected: ${failure.detail ?? 'no detail'}`, delayMs: RETRY_MS, stop: false }
    default:
      return { status: 'memory tail error', delayMs: RETRY_MS, stop: false }
  }
}

/** The request for the next poll: no cursors until the first answer has set them. */
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

export const FEED_UNAVAILABLE = 'activity feed unavailable — showing writes only'
