import { expect, test } from 'claude-code/testing'
import {
  AUTO_EVIDENCE,
  MAX_EVIDENCE_CHARS,
  MAX_TRANSCRIPT_CHARS,
  buildTranscript,
  digestEvery,
  evidenceWith,
  MAX_OWN_PROMPT_CHARS,
  fingerprint,
  isOwnQuote,
  numberedPrompts,
  parseLessons,
  parseReflection,
  recordPrompt,
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
  expect(parseReflection(reply([note('a fact'), { statement: 'AOT', kind: 'project' }], [{ id: 'f4', verdict: 'applied' }]))).toEqual({
    save: [{ statement: 'a fact', kind: 'note' }, { statement: 'AOT', kind: 'project' }],
    verdicts: [{ id: 'f4', verdict: 'applied' }],
  })
  expect(parseReflection('```json\n' + reply([{ statement: 'fenced', kind: 'project' }]) + '\n```')).toEqual({ save: [{ statement: 'fenced', kind: 'project' }], verdicts: [] })
  expect(parseReflection(reply([]))).toEqual({ save: [], verdicts: [] })
})

test('parseReflection: verdicts is optional and defaults to none', () => {
  expect(parseReflection('{"save": [{"statement": "a fact", "kind": "note"}]}')).toEqual({ save: [{ statement: 'a fact', kind: 'note' }], verdicts: [] })
})

test('parseReflection: prose, a bare array, a missing save, a non-array list and malformed JSON are wrong-shaped', () => {
  expect(parseReflection('Here are some memories: {"save": []}')).toBeUndefined()
  expect(parseReflection('["a fact"]')).toBeUndefined()
  expect(parseReflection('{"save": [], "verdicts": {}}')).toBeUndefined()
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

test('parseReflection: a lesson entry in the first call is dropped, notes beside it are kept', () => {
  const lesson = { statement: 'Always run the linter first', kind: 'lesson', user_quote: 'always run the linter first' }
  expect(parseReflection(reply([lesson, note('N')]))!.save).toEqual([note('N')])
})

test('parseReflection: verdicts keep a known verdict once per id, nothing else', () => {
  const verdicts = [{ id: 'f1', verdict: 'applied' }, { id: 'f1', verdict: 'contradicted' }, { id: 'f2', verdict: 'helpful' }, { id: 3, verdict: 'applied' }, { id: 'f3', verdict: 'contradicted' }, 'f4']
  expect(parseReflection(reply([], verdicts))!.verdicts).toEqual([{ id: 'f1', verdict: 'applied' }, { id: 'f3', verdict: 'contradicted' }])
})

const OWN = ['please   always run\nthe linter before every commit', 'and keep commits small']

test('isOwnQuote: found in one own prompt once whitespace is collapsed, and long enough', () => {
  expect(isOwnQuote('always run the linter before', OWN)).toBe(true)
  expect(isOwnQuote('  always   run the\tlinter before ', OWN)).toBe(true)
})

test('isOwnQuote: the floor is 20 characters and 4 words', () => {
  const prompts = ['abcdefghi jklmnopq rs tuvwxyz', 'one two three fourteenth-word-here', 'a b c d e f g h i j k']
  expect(isOwnQuote('abcd efgh ijkl mnop', ['abcd efgh ijkl mnop and more'])).toBe(false) // 19 characters, 4 words
  expect(isOwnQuote('abcd efgh ijkl mnopq', ['abcd efgh ijkl mnopq and more'])).toBe(true) // 20 characters, 4 words
  expect(isOwnQuote('abcdefghi jklmnopq rs', prompts)).toBe(false) // 21 characters, 3 words
  expect(isOwnQuote('one two three fourteenth-word-here', prompts)).toBe(true) // 4 words
  expect(isOwnQuote('a b c d e f g h i j k', prompts)).toBe(true) // 21 characters, 11 words
  expect(isOwnQuote('always', OWN)).toBe(false)
})

test('isOwnQuote: not in any one prompt, or spanning two, is dropped', () => {
  expect(isOwnQuote('always deploy on Fridays please', OWN)).toBe(false)
  expect(isOwnQuote('before every commit and keep commits', OWN)).toBe(false)
  expect(isOwnQuote('always run the linter before', [])).toBe(false)
})

test('parseLessons: at most 2, each quoting an own prompt; anything else is dropped, never demoted', () => {
  const lesson = (statement: string, user_quote: unknown) => ({ statement, user_quote })
  const text = JSON.stringify({
    lessons: [
      lesson('Run the linter first', 'always run the linter before'),
      lesson('Short quote', 'always'),
      lesson('No quote', undefined),
      lesson('Not theirs', 'always deploy on Fridays please'),
      lesson('Keep commits small', 'and keep commits small'),
      lesson('Third', 'linter before every commit'),
    ],
  })
  expect(parseLessons(text, OWN)).toEqual([
    { text: 'Run the linter first', kind: 'lesson', quote: 'always run the linter before' },
    { text: 'Keep commits small', kind: 'lesson', quote: 'and keep commits small' },
  ])
})

test('parseLessons: a wrong shape yields none', () => {
  for (const reply of ['', 'prose', '[]', '{"save": []}', '{"lessons": {}}', '{"lessons": [']) expect(parseLessons(reply, OWN)).toEqual([])
})

test('recordPrompt: keeps the newest 12,000 characters, a lone long prompt by its tail', () => {
  let record: string[] = []
  for (let i = 0; i < 5; i++) record = recordPrompt(record, String(i).repeat(3000))
  expect(record.map((t) => t[0])).toEqual(['1', '2', '3', '4'])
  const long = recordPrompt(record, 'HEAD' + 'z'.repeat(MAX_OWN_PROMPT_CHARS))
  expect(long.length).toBe(1)
  expect(long[0]!.length).toBe(MAX_OWN_PROMPT_CHARS)
  expect(long[0]!.includes('HEAD')).toBe(false)
})

test('numberedPrompts: numbered, oldest first', () => {
  expect(numberedPrompts(['first', 'second'])).toBe('1. first\n\n2. second')
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
