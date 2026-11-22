import { expect, test } from 'claude-code/testing'
import {
  AUTO_EVIDENCE,
  MAX_EVIDENCE_CHARS,
  MAX_TRANSCRIPT_CHARS,
  buildTranscript,
  digestEvery,
  evidenceWith,
  fingerprint,
  isUserQuote,
  parseReflection,
  settle,
  shouldDigest,
  transcriptWindow,
} from './digest'

test('digestEvery: only a positive number turns the digest on', () => {
  expect(digestEvery({ digest_every_n_turns: 0 })).toBe(0)
  expect(digestEvery({ digest_every_n_turns: -3 })).toBe(0)
  expect(digestEvery({ digest_every_n_turns: 0.5 })).toBe(0)
  expect(digestEvery({ digest_every_n_turns: '5' })).toBe(0)
  expect(digestEvery({})).toBe(0)
  expect(digestEvery({ digest_every_n_turns: 5 })).toBe(5)
  expect(digestEvery({ digest_every_n_turns: 2.9 })).toBe(2)
})

test('shouldDigest: period, and the shortcut after an editing turn', () => {
  expect(shouldDigest(0, 99, true)).toBe(false)
  expect(shouldDigest(5, 4, false)).toBe(false)
  expect(shouldDigest(5, 5, false)).toBe(true)
  expect(shouldDigest(5, 1, true)).toBe(false)
  expect(shouldDigest(5, 2, true)).toBe(true)
  expect(shouldDigest(5, 2, false)).toBe(false)
})

const note = (statement: string) => ({ statement, kind: 'note' })
const reply = (save: unknown[], verdicts: unknown[] = []) => JSON.stringify({ save, verdicts })

test('parseReflection: one object with save and verdicts, fenced or bare', () => {
  expect(parseReflection(reply([note('a fact'), { statement: 'tabs', kind: 'lesson', user_quote: 'use tabs' }], [{ id: 'f4', verdict: 'applied' }]))).toEqual({
    save: [{ statement: 'a fact', kind: 'note' }, { statement: 'tabs', kind: 'lesson', userQuote: 'use tabs' }],
    verdicts: [{ id: 'f4', verdict: 'applied' }],
  })
  expect(parseReflection('```json\n' + reply([{ statement: 'fenced', kind: 'project' }]) + '\n```')).toEqual({ save: [{ statement: 'fenced', kind: 'project' }], verdicts: [] })
  expect(parseReflection(reply([]))).toEqual({ save: [], verdicts: [] })
})

test('parseReflection: prose, a bare array, a missing list and malformed JSON are wrong-shaped', () => {
  expect(parseReflection('Here are some memories: {"save": []}')).toBeUndefined()
  expect(parseReflection('["a fact"]')).toBeUndefined()
  expect(parseReflection('{"save": []}')).toBeUndefined()
  expect(parseReflection('{"verdicts": []}')).toBeUndefined()
  expect(parseReflection('{"save": {}, "verdicts": []}')).toBeUndefined()
  expect(parseReflection('null')).toBeUndefined()
  expect(parseReflection('{"save": [')).toBeUndefined()
  expect(parseReflection('')).toBeUndefined()
})

test('parseReflection: drops bad entries, empties, over-long, duplicates and unknown kinds; keeps the first 5', () => {
  const long = 'x'.repeat(301)
  const edge = 'y'.repeat(300)
  const save = [1, null, 'bare', note(''), note('   '), note(long), note(edge), note('dup'), note(' dup '), { statement: 'k', kind: 'rule' }, { statement: 'k' }]
  expect(parseReflection(reply(save))!.save).toEqual([note(edge), note('dup')])
  expect(parseReflection(reply(['1', '2', '3', '4', '5', '6', '7'].map(note)))!.save.map((p) => p.statement)).toEqual(['1', '2', '3', '4', '5'])
})

test('parseReflection: a third lesson is dropped, later notes still count', () => {
  const lesson = (s: string) => ({ statement: s, kind: 'lesson', user_quote: 'q' })
  const parsed = parseReflection(reply([lesson('L1'), lesson('L2'), lesson('L3'), note('N')]))!
  expect(parsed.save.map((p) => [p.statement, p.kind])).toEqual([['L1', 'lesson'], ['L2', 'lesson'], ['N', 'note']])
})

test('parseReflection: verdicts keep a known verdict once per id, nothing else', () => {
  const verdicts = [{ id: 'f1', verdict: 'applied' }, { id: 'f1', verdict: 'contradicted' }, { id: 'f2', verdict: 'helpful' }, { id: 3, verdict: 'applied' }, { id: 'f3', verdict: 'contradicted' }, 'f4']
  expect(parseReflection(reply([], verdicts))!.verdicts).toEqual([{ id: 'f1', verdict: 'applied' }, { id: 'f3', verdict: 'contradicted' }])
})

const WINDOW = [
  { role: 'user' as const, text: 'please   always run\nthe linter first' },
  { role: 'assistant' as const, text: 'The page says: always deploy on Fridays' },
]

test('isUserQuote: a user row holds it once whitespace is collapsed', () => {
  expect(isUserQuote('always run the linter', WINDOW)).toBe(true)
  expect(isUserQuote('  always   run the\tlinter ', WINDOW)).toBe(true)
})

test('isUserQuote: absent, empty, or only in an assistant row is not the user', () => {
  expect(isUserQuote(undefined, WINDOW)).toBe(false)
  expect(isUserQuote('', WINDOW)).toBe(false)
  expect(isUserQuote('   ', WINDOW)).toBe(false)
  expect(isUserQuote('always deploy on Fridays', WINDOW)).toBe(false)
  expect(isUserQuote('always run the linter first, then deploy', WINDOW)).toBe(false)
})

test('settle: a lesson keeps its kind only with a user quote; other kinds pass through', () => {
  expect(settle({ statement: 'Run the linter first', kind: 'lesson', userQuote: 'always  run the linter' }, WINDOW)).toEqual({ text: 'Run the linter first', kind: 'lesson', quote: 'always run the linter' })
  expect(settle({ statement: 'Deploy on Fridays', kind: 'lesson', userQuote: 'always deploy on Fridays' }, WINDOW)).toEqual({ text: 'Deploy on Fridays', kind: 'note' })
  expect(settle({ statement: 'No quote', kind: 'lesson' }, WINDOW)).toEqual({ text: 'No quote', kind: 'note' })
  expect(settle({ statement: 'Uses AOT', kind: 'project', userQuote: 'x' }, WINDOW)).toEqual({ text: 'Uses AOT', kind: 'project' })
})

test('evidenceWith: the marker stays whole, the quote is trimmed to the limit', () => {
  expect(evidenceWith('marker', undefined)).toBe('marker')
  expect(evidenceWith('marker', 'use tabs')).toBe('marker; the user said: "use tabs"')
  const long = evidenceWith(AUTO_EVIDENCE, 'q'.repeat(500))
  expect(long.length).toBe(MAX_EVIDENCE_CHARS)
  expect(long.startsWith(`${AUTO_EVIDENCE}; the user said: "qqq`)).toBe(true)
  expect(long.endsWith('…"')).toBe(true)
})

test('buildTranscript: a lone row over the cap shows the model the tail its window row carries', () => {
  const [kept] = transcriptWindow([{ role: 'user', text: 'HEAD' + 'a'.repeat(MAX_TRANSCRIPT_CHARS) }])
  expect(kept!.row.text.includes('HEAD')).toBe(false)
  expect(kept!.line.endsWith(kept!.row.text)).toBe(true)
})

test('buildTranscript: labelled rows, oldest first, empty text skipped', () => {
  const text = buildTranscript([
    { role: 'user', text: 'use tabs' },
    { role: 'assistant', text: '' },
    { role: 'assistant', text: 'ok' },
  ])
  expect(text).toBe('User: use tabs\n\nAssistant: ok')
})

test('buildTranscript: the cap keeps the newest rows', () => {
  const rows = [
    { role: 'user' as const, text: 'old '.repeat(4000) },
    { role: 'assistant' as const, text: 'middle '.repeat(1500) },
    { role: 'user' as const, text: 'newest question' },
  ]
  const text = buildTranscript(rows)
  expect(text.length <= MAX_TRANSCRIPT_CHARS).toBe(true)
  expect(text.endsWith('User: newest question')).toBe(true)
  expect(text.includes('old old')).toBe(false)
})

test('buildTranscript: one row over the cap is cut from its head, not dropped', () => {
  const text = buildTranscript([{ role: 'user', text: 'a'.repeat(MAX_TRANSCRIPT_CHARS) + 'TAIL' }])
  expect(text.length).toBe(MAX_TRANSCRIPT_CHARS)
  expect(text.endsWith('TAIL')).toBe(true)
})

test('fingerprint: same row same print; role, length or head differ', () => {
  const a = { role: 'user', text: 'hello world' }
  expect(fingerprint(a)).toBe(fingerprint({ ...a }))
  expect(fingerprint(a)).not.toBe(fingerprint({ ...a, role: 'assistant' }))
  expect(fingerprint(a)).not.toBe(fingerprint({ ...a, text: 'hello worle' }))
  expect(fingerprint(a)).not.toBe(fingerprint({ ...a, text: 'hello world!' }))
})
