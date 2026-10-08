import { expect, test } from 'claude-code/testing'
import {
  MAX_TRANSCRIPT_CHARS,
  buildTranscript,
  digestEvery,
  parseCandidates,
  shouldDigest,
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

test('parseCandidates: a JSON array of strings, fenced or bare', () => {
  expect(parseCandidates('["a fact", "another"]')).toEqual(['a fact', 'another'])
  expect(parseCandidates('```json\n["fenced"]\n```')).toEqual(['fenced'])
  expect(parseCandidates('[]')).toEqual([])
})

test('parseCandidates: prose, objects and malformed JSON yield nothing', () => {
  expect(parseCandidates('Here are some memories: [1]')).toEqual([])
  expect(parseCandidates('{"a": "b"}')).toEqual([])
  expect(parseCandidates('["unterminated')).toEqual([])
  expect(parseCandidates('')).toEqual([])
})

test('parseCandidates: drops non-strings, empties, over-long and duplicates; keeps the first 5', () => {
  const long = 'x'.repeat(301)
  const edge = 'y'.repeat(300)
  expect(parseCandidates(JSON.stringify([1, null, '', '   ', long, edge, 'dup', ' dup ', { a: 1 }]))).toEqual([edge, 'dup'])
  expect(parseCandidates(JSON.stringify(['1', '2', '3', '4', '5', '6', '7']))).toEqual(['1', '2', '3', '4', '5'])
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
