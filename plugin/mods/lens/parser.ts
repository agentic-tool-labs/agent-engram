import type { LensFact, LensRecall } from '../shared/state'

export type ParsedDigest = Pick<
  LensRecall,
  'parsed' | 'query' | 'coverage' | 'factCount' | 'notes' | 'gaps' | 'facts' | 'raw'
>

const HEADER = /^RECALL "(.*)" · (\d+) facts · \d+\/\d+ tokens · coverage: (high|partial|none)(.*)$/
const NOTE = / · ((?:overlap|vector) lane did not run \(.*?\))(?= · (?:overlap|vector) lane did not run \(|$)/g
const FACT = /^\[(f\d+)\] (.*)$/
const AGE = /^\d+d$/

const unparsed = (raw: string): ParsedDigest => ({ parsed: false, query: '', notes: [], facts: [], raw })

/** The text inside the last balanced parenthesis group when the line ends with one. */
function trailingGroup(rest: string): { body: string; meta: string } | undefined {
  if (!rest.endsWith(')')) return undefined
  let depth = 0
  for (let i = rest.length - 1; i >= 0; i--) {
    if (rest[i] === ')') depth++
    else if (rest[i] === '(' && --depth === 0) {
      return { body: rest.slice(0, i).trimEnd(), meta: rest.slice(i + 1, -1) }
    }
  }
  return undefined
}

function parseFact(handle: string, rest: string): LensFact {
  const group = trailingGroup(rest)
  const body = group?.body ?? rest
  const meta = group?.meta ?? ''
  const tokens = meta === '' ? [] : meta.split(' · ')

  let versions = 1
  let withheld: string | undefined
  for (const token of tokens) {
    const version = /^v(\d+)$/.exec(token)
    if (version) versions = Number(version[1])
    const more = /^\+(\d+(?:\.\d+)?k?)$/.exec(token)
    if (more) withheld = more[1]
  }

  // A code fact's second token is its location unless it is already the age.
  const location = tokens[0] === 'code' && tokens[1] !== undefined && !AGE.test(tokens[1]) ? tokens[1] : undefined

  return {
    handle,
    id: Number(handle.slice(1)),
    body,
    meta,
    versions,
    ...(withheld === undefined ? {} : { withheld }),
    pinned: tokens.includes('pinned'),
    judged: tokens.includes('judged'),
    ...(location === undefined ? {} : { location }),
  }
}

/**
 * Reads the recall digest the server prints. Never throws: text without the header comes back with
 * `parsed: false` and only the raw text, and a line that matches no rule is skipped.
 */
export function parseDigest(raw: string): ParsedDigest {
  const lines = raw.split('\n')
  const header = HEADER.exec(lines[0] ?? '')
  if (header === null) return unparsed(raw)

  const facts: LensFact[] = []
  let gaps: string | undefined
  for (const line of lines.slice(1)) {
    const fact = FACT.exec(line)
    if (fact) facts.push(parseFact(fact[1]!, fact[2]!))
    else if (line.startsWith('gaps: ')) gaps = line.slice('gaps: '.length)
  }

  return {
    parsed: true,
    query: header[1]!,
    coverage: header[3] as 'high' | 'partial' | 'none',
    factCount: Number(header[2]),
    notes: [...header[4]!.matchAll(NOTE)].map((m) => m[1]!),
    ...(gaps === undefined ? {} : { gaps }),
    facts,
    raw,
  }
}
