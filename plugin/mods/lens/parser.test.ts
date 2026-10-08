import { expect, test } from 'claude-code/testing'

import { parseDigest } from './parser'

const header = (rest = 'coverage: high', facts = 1) => `RECALL "q" · ${facts} facts · 10/500 tokens · ${rest}`

test('a digest without the header is unparsed and kept raw', () => {
  const parsed = parseDigest('hello\n[f1] body (project · 0d)')

  expect(parsed.parsed).toBe(false)
  expect(parsed.facts).toEqual([])
  expect(parsed.raw).toBe('hello\n[f1] body (project · 0d)')
})

test('empty text is unparsed, not an error', () => {
  expect(parseDigest('').parsed).toBe(false)
})

test('a header with no availability note yields no notes', () => {
  expect(parseDigest(header()).notes).toEqual([])
})

test('both lane notes are kept verbatim, including parentheses in the reason', () => {
  const parsed = parseDigest(
    header('coverage: none · overlap lane did not run (token index not built yet) · vector lane did not run (sqlite-vec (v0.1) missing)', 0),
  )

  expect(parsed.coverage).toBe('none')
  expect(parsed.notes).toEqual([
    'overlap lane did not run (token index not built yet)',
    'vector lane did not run (sqlite-vec (v0.1) missing)',
  ])
})

test('a query containing quotes and the separator still parses', () => {
  const parsed = parseDigest('RECALL "say "hi" · there" · 2 facts · 5/500 tokens · coverage: partial')

  expect(parsed.parsed).toBe(true)
  expect(parsed.query).toBe('say "hi" · there')
  expect(parsed.factCount).toBe(2)
})

test('markers come from the last parenthesis group, whatever the body holds', () => {
  const [fact] = parseDigest(
    [header(), '[f12] Call foo(bar) (then baz) to bind. (code · r:src/a.cs · 3d · v10 · judged · +1.6k · pinned)'].join('\n'),
  ).facts

  expect(fact!.handle).toBe('f12')
  expect(fact!.id).toBe(12)
  expect(fact!.body).toBe('Call foo(bar) (then baz) to bind.')
  expect(fact!.versions).toBe(10)
  expect(fact!.judged).toBe(true)
  expect(fact!.pinned).toBe(true)
  expect(fact!.withheld).toBe('1.6k')
  expect(fact!.location).toBe('r:src/a.cs')
})

test('a fact with no markers has versions 1 and none of the flags', () => {
  const [fact] = parseDigest([header(), '[f1] plain (project · 0d)'].join('\n')).facts

  expect(fact).toMatchObject({ versions: 1, pinned: false, judged: false })
  expect(fact!.withheld).toBeUndefined()
  expect(fact!.location).toBeUndefined()
})

test('a code fact with no location text does not mistake the age for one', () => {
  const [fact] = parseDigest([header(), '[f1] gist (code · 0d)'].join('\n')).facts

  expect(fact!.location).toBeUndefined()
})

test('a word that only contains a marker is not the marker', () => {
  const [fact] = parseDigest([header(), '[f1] body (project · 0d · v2x · prejudged · +abc)'].join('\n')).facts

  expect(fact).toMatchObject({ versions: 1, judged: false, pinned: false })
  expect(fact!.withheld).toBeUndefined()
  expect(fact!.meta).toBe('project · 0d · v2x · prejudged · +abc')
})

test('unknown meta tokens stay in meta and are ignored', () => {
  const [fact] = parseDigest([header(), '[f1] body (session · p1 · agent-7 · 2d)'].join('\n')).facts

  expect(fact!.meta).toBe('session · p1 · agent-7 · 2d')
  expect(fact!.versions).toBe(1)
})

test('lines that match no rule are skipped, and gaps are read', () => {
  const parsed = parseDigest(
    [header('coverage: partial'), 'noise', '[f1] a (project · 0d)', '[nope] b', 'gaps: only partial matches', '→ engram_remember what you discover'].join('\n'),
  )

  expect(parsed.facts.map((f) => f.handle)).toEqual(['f1'])
  expect(parsed.gaps).toBe('only partial matches')
})

test('a fact line without a closing parenthesis keeps its whole text as the body', () => {
  const [fact] = parseDigest([header(), '[f1] body with no markers'].join('\n')).facts

  expect(fact).toMatchObject({ body: 'body with no markers', meta: '' })
})
