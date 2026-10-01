# Code-fact source read: `engram_expand … details` returns the live source of an indexed code fact

Implementer: implementor
Reviewer: reviewer

Status: rev 1 (Architect). Builds on `docs/specs/code-fact-readability.md` (D74, branch
`ah/code-fact-readability`). Where this spec and that one disagree about the expand note, this one
wins. Every such point is named in §6.

Decision record: **D75** in `docs/engram-implementation-plan.md` (D74 is the highest heading at
plan.md:5302; confirm `D75` is unused before writing it).

> **Amendment 1 (rev 2): rulings on review sr-review (20260930-203339-c4gc)**
>
> 1. **Blocking: physical containment must fail closed.** §3 step 2 is rewritten. It adds a strict canonicalisation (cap of 32 link resolutions; any uninspectable link or the cap means refuse), adds rows R2b–R2d, and adds falsification arms F8 and F8b.
> 2. **Should-fix: tier-2 lines are counted by `'\n'`, not by Roslyn's line map.** This changes the tier-2 row in §2, the sidecar protocol, and S5 (lone CR and NEL rows). §4 gains an out-of-range guard. New AC D12 and arms F9 and F10.
> 3. **§3 step 0:** `file missing` now requires that nothing exists at the path. A directory falls through to `not a regular file`.
> 4. **`FileFreshness.Verdict` gains `Root`.** The physical check uses it; the root is no longer recovered from the file path by string surgery (§3).
> 5. **D75 wording is fixed** (new §12): the containment paragraph, the `'\n'` rule, and the residual risk from the indexer reading through symlinks.
> 6. **D2 gains a C# member row**, kept beside the markdown row.
>
> Rework steps are in §8 under "Rework (rev 2)". Section numbers are unchanged.

---

## 0. Goal and decisions

**Goal.** Expanding an indexed code fact (`Scope == "code" && Regenerable`) returns the current
source of what the fact describes, read from disk at call time. Today the call returns a gist plus
"read it at <path>", which costs the model a second tool call and gives it no line range. After
this change it can act on the answer without another call.

**Rejected, and still rejected (do not reopen).**
- Storing chunks. That copies readable source and grows the store by roughly the size of the repo.
- Storing line ranges in any fact column, body, evidence or side table. A stored line number turns
  every edit above a symbol into a new fact version.

Spans in this spec are **transient**: they are computed from the live file on each call and never
written anywhere.

| # | Decision | Why |
|---|---|---|
| 1 | **Locate by re-running the indexer's own analysis on the live file.** Tiers 0, 1 and 2 report each entity's line span transiently. Expand looks up the fact's entity path in that result. | The indexer decides fragment identity (`DeepTier.Fragments`, `CodePaths.Slug`, the heading stack). Reusing it means the span is attributed by the exact rule that named the fact, with no second implementation to drift. Rejected alternative: anchoring by searching the live file for the stored `declared-as` text. That is a second identity rule. It shows the wrong code confidently when the text appears twice in a file (nested types, overload-like duplicates), and it fails as soon as the signature line is edited. Re-analysis survives edits to the body and signature and fails only on a rename, and then it fails honestly (decision #4). |
| 2 | **The source goes in the `details` view and nowhere else.** `evidence` and `source` point at it. | `details` is already the paged "everything" view (D64), with `offset` and `budget_tokens`. One place, one pager, and no new view name to pay for in the tool-surface budget. |
| 3 | **No per-line numbers.** One header line states `lines a–b of N`. | Prefixing every line costs about 15% more tokens on code. The header gives the start line, which is what a follow-up `Read` with an offset needs. |
| 4 | **Fallback ladder, never a guess.** Use the entity's span. If there is none, show the whole file with the reason in the header. If the file cannot be read, show no source and give the reason. | Wrong code presented as the symbol is worse than the whole file labelled honestly. |
| 5 | **A span is the declaration node only.** Leading doc comments are not included. | Tree-sitter keeps comments as sibling nodes and Roslyn keeps them as trivia, so including them would mean two different rules. The Roslyn `<summary>` is already the symbol's `about` fact. User question Q1. |
| 6 | **Containment is checked twice, with two distinct jobs.** The lexical check sits in the one resolver (`FileFreshness.Check`), so no expand view ever displays an escaping path. The physical check (symlinks resolved, regular file) sits next to the read. | Each check has a test that only it can fail (§7, AC-R1 and AC-R2). A test cannot tell apart two checks that guard the same path; see the D53 lesson in CLAUDE.md. |
| 7 | **The description is paid for by trimming; the ceiling stays 6550.** | D17 and the earlier ceiling ruling. Arithmetic in §5. |

**Not changing:**
- schema;
- `CodeAnalyzer.AnalyzerVersion` and `schema_meta.code_index_version` (bumping either forces a re-read, and spans are not stored);
- any fact body, predicate, evidence or path the indexer writes;
- the recall line, budget and markers (D30, D44, D57, D64);
- the `history` and `related` views;
- the expand pager's algorithm;
- `MemoryBrowser`;
- `MaxDefinitionChars = 6550` and `ExpectedToolCount = 11`.

---

## 1. Facts this design rests on (read, not run)

- **Positions exist only transiently today.**
  - Tier 0 regex: `match.Index` gives a line through `LineOf` (CodeAnalyzer.cs:75-76, :228), and the line is then discarded.
  - Markdown: the loop in `AnalyzeDocument` (CodeAnalyzer.cs:130-190) knows each heading's line index.
  - Tier 1: `declNodes[symbol]` holds the declaration node, with start and end bytes, and is used only for call attribution (TreeSitter.cs:95-96, ~205).
  - Tier 2: the sidecar has `MemberDeclarationSyntax` but emits only call lines (Program.cs:308).
  - Only `DeepCall.Line` is ever carried (DeepAnalysis.cs:18).
- **Records:**
  - `CodeCandidate(EntityPath, Kind, DisplayName, Predicate, Body, Object?, AnalyzerTier)` (CodeAnalyzer.cs:12);
  - `DeepSymbol(Name, Kind, Declaration, Doc, Scope, Params)` (DeepAnalysis.cs:11);
  - `DeepAnalysis(Path, Symbols, Imports, Error, Calls, Inherits, Tier)`.
- **Entry points:**
  - Tier 0 `CodeAnalyzer.Analyze(fileEntityPath, content, LanguageDefinition)` is static.
  - Tier 1 `TreeSitter.Analyze(language, rel, content)` is an instance method that needs `TreeSitter.Locate` and `TryCreate`, and is IDisposable.
  - Tier 2 `RoslynSidecar.Analyze(sidecarPath, [(rel, content)], timeout)` takes a batch, and a one-element list works. It spawns one process per call; the plan doc (plan.md:63) puts the spawn at about 300 ms. That figure is not recently measured.
- **The indexer pipeline per file** is `CodeAnalyzer.Analyze`, then `DeepTier.Merge` when deep output exists (CodeIndexer.cs:505-545). Content is read with `File.ReadAllText(Path.Combine(root, rel))` (CodeIndexer.cs:417/478/524).
- **Fragments:**
  - A symbol is `Scope/Name`, plus `(params)` only when two collide; the first wins on a residual duplicate.
  - A section is the slugged heading stack joined with `/`; duplicate fragments merge under the first occurrence.
  - The universal tier (tier 0) writes top-level names only. See docs/engram-path-grammar.md:47-70.
- **Index filters:** `IndexFilter.Inspect` applies `max_file_bytes` (default 1,000,000; IndexingSettings.cs:75/186), a NUL byte within the first `HeadBytes = 8192` means binary, and a mean line length over 400 bytes means generated.
- **`FileFreshness.Check`** (FileFreshness.cs:88-111):
  - It turns `repo_registry.disk_path` (where `detached_at IS NULL`) and the relative path into `Path.Combine(diskPath, rel)`.
  - It has **no containment check**.
  - Missing is decided by `File.Exists`, and Stale by mtime against `indexed_at` with more than 1 s of slack.
  - It returns `Verdict(State, Behind, File?)`.
- **Containment idiom already in the tree:** `Path.GetRelativePath(root, full)`, then reject the result if it is rooted or starts with `..`. It appears at MemoryGuardPathMatcher.cs:29-33 and SpoolQueue.cs:195-196. `PathCanonicalizer.Canonical` resolves symlinked directories through `Directory.ResolveLinkTarget` (PathCanonicalizer.cs:17-37).
- **Expand today:**
  - views at EngramMcpTools.cs:392-400;
  - `ExpandDetails` at :1740-1786, which pages `Body (+ "\n\n" + Details) (+ "\n\n" + note)` by `budget_tokens × TokenEstimator.CharactersPerToken`, cuts at word boundaries, and uses the footer `showing chars {o}–{e} of {L} · continue with offset: {e}`;
  - `IndexedCodeNote.Build` in src/Engram.Core/IndexedCodeNote.cs.

---

## 2. Spans (transient)

**Contract.** For one file's content, the single-file analysis reports, for every entity path it produces, an optional **span**: 1-based, inclusive line numbers `(start, end)` into that content. Lines are split on `'\n'`, so a `"\r\n"` file numbers the same as its `"\n"` twin.

| Entity kind | Span |
|---|---|
| file (path with no `#`) | `1 … N`, where N is the line count. A trailing newline does not add an empty last line. |
| symbol, from tier 1 | The start and end lines of the declaration node that tier 1 already pairs with the symbol's name node for call attribution. Byte offsets become lines through the existing UTF-8-aware helper (`LineNumberAt`/`NewlineOffsets`, TreeSitter.cs:552-570), never by counting chars. |
| symbol, from tier 2 | The declaration syntax node's `Span`: attributes and modifiers included, leading trivia (doc comments) excluded. The sidecar emits the lines and the client parses them. **(rev 2)** The sidecar counts lines by `'\n'` in the source text it was sent: start = 1 + the number of `'\n'` before `Span.Start`; end = 1 + the number of `'\n'` before `Span.End − 1` (the span's last character). It must **not** use `GetLineSpan` or `SourceText.Lines`, because Roslyn also breaks lines on a lone `\r` and on U+0085 (NEL); the review probe reproduced both. Every such character above a member would shift its span away from the `'\n'` numbering the reader uses. |
| symbol, from tier 0 (regex, top level only) | Start is the line of the regex match. End is the line before the next tier-0 declaration's start line in the same file, or N, with trailing blank lines then dropped. |
| section (markdown) | From the heading's line to the line before the next heading at the same or a shallower level, or N, with trailing blank lines dropped. Headings are recognised exactly as the analyzer already recognises them (fenced code included or excluded as it does today). For a duplicate fragment, the first occurrence's range. |

**Which span wins.** For an entity path, the deep-tier span applies if the deep tier produced that entity and supplied a span. Otherwise the tier-0 span applies if tier 0 produced it. Otherwise there is no span.

**Single implementation.**
- The composition "content, then tier 0, then optional deep, then `DeepTier.Merge`" must exist **once**.
- `CodeIndexer.ProcessFile` and the source reader both call it.
- Deep analysis for the source reader uses the same `TreeSitter`/`RoslynSidecar` entry points, located by the same `Locate` calls with the same inputs that the in-server `engram_index_repo` path uses.
- The implementor decides how spans are carried (fields, a side map, etc.).

**Spans may not influence what the indexer writes:**
- not any fact's body, evidence, predicate, path, `analyzer_tier`, or the unchanged-body skip;
- not any equality used for change detection or dedup.

If spans become a field on a record compared by value, every comparison that decides a write must still compare exactly what it compares today. Guard: AC-S7.

**Sidecar protocol.**
- Add per-symbol start and end line fields to the symbol JSON. The change is additive. The values are `'\n'`-counted as in the tier-2 row above (rev 2).
- The client treats absent fields as "no span", so an older installed sidecar degrades to the reason `analyzer unavailable` and never errors.
- The sidecar timeout for a source read is a fixed **5 s** constant, not config. A timeout or error counts as "deep tier unavailable".

---

## 3. The source reader

**Input:** the fact, plus the `FileFreshness.Verdict` that `IndexedCodeNote` already obtains (the one `FileFreshness.Check` call; no second `repo_registry` query).

**Output:** either a source block (§4) or an unavailability reason. It never throws out of expand: any I/O exception becomes `unreadable`.

**Order of checks**, each with its reason string:

| Step | Check | Reason if it fails |
|---|---|---|
| 0 | Verdict `Unknown`, or `File` is null | `location unknown` |
| 0 | Verdict `Missing` **and nothing exists at the path**, neither a file nor a directory (rev 2). A directory at the path also reads as `Missing` to `File.Exists`, so it falls through to step 2, then step 3, and is reported as `not a regular file`. | `file missing` |
| 1 | **Lexical containment, in `FileFreshness.Check` itself.** The relative path must have no empty, `.` or `..` segment and must not be rooted. `GetFullPath(Combine(disk_path, rel))` must be contained in `GetFullPath(disk_path)` by the existing idiom (§1). A failure makes `Check` return `Verdict.Unknown`, so no view ever prints an escaping path. **(rev 2)** `Verdict` gains a trailing optional `Root`: the `GetFullPath(disk_path)` that this check compared against. It is set whenever `File` is set and null otherwise. Nothing else changes in `Verdict`. | (reported as `location unknown`) |
| 2 | **Physical containment, which fails closed (rev 2).** Both the root (`Verdict.Root`, never recovered from `File` by string slicing) and the file are canonicalised **strictly**. Every symlink on the path is resolved, the file's own final segment included. The resolved file must be contained in the resolved root, by the same helper as step 1. Strict canonicalisation returns **no result** (the read is refused) when either: (a) it would follow more than **32** link resolutions in total for one path (32 is macOS's MAXSYMLINKS, the lower kernel limit; the OS itself cannot open a longer chain there), or (b) any component's link status or target cannot be inspected, i.e. the inspection throws. It never returns a partially resolved path. A root under a symlinked directory still passes. | `outside the repo` |
| 3 | Must be a regular file (not a directory, FIFO, socket or device). Enforceability on FIFOs depends on E2. | `not a regular file` |
| 4 | Size must not exceed the configured `max_file_bytes`, the same setting `IndexFilter` uses. **Never read more than `max_file_bytes + 1` bytes**, so a file that grows between stat and read still reports too large. | `over {max_file_bytes} bytes` |
| 5 | No NUL in the first `HeadBytes` bytes, reusing `IndexFilter`'s rule or constant, not a copy. | `binary` |

- **Decoding** must match the indexer (`File.ReadAllText` semantics: UTF-8, BOM detected and stripped), so spans and the analyzer agree on line numbers.
- **Analysis:** the single-file analysis of §2 runs on the content just read. Never read the file twice for one call.
- **Strict canonicalisation (rev 2).**
  - It lives beside the lenient one in `PathCanonicalizer`, as `TryCanonical`, returning a nullable string. The name was proposed by the Reviewer and is adopted.
  - The lenient `Canonical` keeps its exact current behaviour for its existing callers (queue matching, `IndexCommand`): its depth cap of 8, and keeping a component as spelled when inspection throws. Its existing tests stay unmodified and green.
  - Both entry points share one resolution walk. The only differences are the cap and what happens at the cap or on a throw: lenient continues as spelled; strict returns null. Do not write a second walk.
  - The physical containment check calls only `TryCanonical`, and null means `outside the repo`.
- **Shared helper:** the lexical containment check must not be a third inline copy of the idiom. Put one helper in Engram.Core and use it for steps 1 and 2. Migrating MemoryGuardPathMatcher and SpoolQueue onto it is **out of scope**; leave those files untouched.

---

## 4. View behaviour

**`details` on an indexed code fact.** The text is built fresh on each call, then paged by the existing pager unchanged:

```
{Body}[\n\n{Details}]

Indexed code: {location} ({subject path})
[{navigate hint line}]
{SOURCE BLOCK  |  UNAVAILABLE BLOCK}
```

- `{location}` is `CodePaths.LocationText`, as today.
- The hint line is today's rule: symbol gets `defined_at | members | callers`, file gets `imports`, section gets none.
- **SOURCE BLOCK:**

  ```
  Source: {resolved file} · lines {a}–{b} of {N}[ · changed since indexed][ · {fallback}, whole file]
  {lines a..b of the live file, verbatim, each trailing '\r' removed, joined by '\n'}
  ```

  - ` · changed since indexed` appears iff the verdict is `Stale`.
  - `{fallback}` is one of:
    - `analyzer unavailable`: the file's language tier is at least 1 and the deep tier produced no analysis or no span for the entity;
    - `not in the current file`: the entity path is absent from an analysis that ran at the needed tier.

    Either fallback shows `a = 1, b = N`.
  - `{resolved file}` is `Verdict.File`, the path shown everywhere else, not the symlink-resolved one.
  - **Out-of-range guard (rev 2).** A span with `Start < 1`, `End < Start` or `End > N` is treated as **no span**, whatever tier produced it. The result is the whole file with `{fallback}` = `analyzer unavailable`. Building `details` never indexes outside the file's lines and never throws on any span value.
- **UNAVAILABLE BLOCK:**

  ```
  [Read it at {file}[ ({label})].  |  Was at {file} (missing).]
  Source unavailable: {reason}.
  ```

  The first line is today's freshness line, with its existing rules, omitted when `File` is null.
- The "Engram keeps only this indexed gist …" sentence is **not** in `details`.
- Header lines come before the code, so page 1 always carries location, hint and range.
- Paging stays stateless (D64). Each page call re-reads and re-analyzes, and offsets assume the file did not change between calls. This is accepted and recorded in D75.

**`evidence` and `source` on an indexed code fact.** Today's note (§2 of the readability spec, with the Amendment 2 "Was at" rule) is unchanged except for the gist sentence:
- If `File` is non-null and the state is not `Missing`: `Engram keeps only this indexed gist (~60 tokens); the details view reads the current source.`
- Otherwise, today's sentence is kept verbatim. Never point at a call that can only fail; this is the same principle as the "Was at" fix.
- No source text appears in these views.

**Unchanged:**
- `details` on any other fact (byte-identical);
- `history`, `related`, unknown-view text;
- the recall line.

---

## 5. Tool description (budget)

The current total is about 6,537 against a ceiling of 6,550; the implementor measures the exact figure first. These are the only description edits:

| Where | From | To | Δ |
|---|---|---|---|
| `engram_expand` description, last sentence | `The details view returns everything the handle holds, paged by budget_tokens and offset.` | `The details view returns everything the handle holds (code: live source), paged by budget_tokens and offset.` | +20 |
| `offset` param | `Character offset to continue a paged details view from. Defaults to 0.` | `Character offset to continue a paged view from. Defaults to 0.` | −8 |
| `budget_tokens` param | `Maximum tokens returned per call. Defaults to 800.` | `Max tokens per call. Defaults to 800.` | −13 |

- Net −1. Update `docs/mcp-tool-descriptions.golden.txt` to match exactly.
- If the measured total after the change exceeds 6550, **stop and report**. Do not re-baseline and do not improvise trims.
- `(code: …)` echoes the `code` scope label the recall line already shows (`(code · repo:rel · …)`).

---

## 6. Supersessions of `code-fact-readability.md`

- **§2.3 note, gist sentence:**
  - In `evidence`/`source`, the sentence changes per §4.
  - In `details`, it is dropped, and the freshness line moves into the UNAVAILABLE BLOCK.
  - In `details`, the SOURCE BLOCK's header carries the path.
- **AC1.1 theory `Expand_IndexedCodeFact_EveryNoteView_CarriesAllElements`:**
  - The `evidence` and `source` rows still assert location, disk path, the gist sentence (new wording) and the hint.
  - The `details` row asserts location, the disk path inside the `Source:` header, the hint, and the source block, and asserts the gist sentence is **absent**.
- **The details paging test** (pages > 3, joined == whole) is rebuilt over a code fact whose text now includes source. Per-page length and page count are still asserted.
- **D74** is not edited. D75 records what changed and why.

---

## 7. Acceptance criteria

**Spans (§2).** Integration tier, fixture repos under a SandboxHome.

| AC | Requirement |
|---|---|
| S1 | Tier-0 declaration spans follow §2: the next-declaration bound, trailing blank lines dropped, and the last declaration running to N. |
| S2 | Section spans: nested sections (the parent includes its children), a sibling ends the span, trailing blank lines are dropped, and a duplicate fragment takes the first range. |
| S3 | The file span is `1…N`, and the trailing-newline case is exact. |
| S4 | Tier-1 TypeScript, with exact `(start, end)`. Covers: a function; a class method; an exported const arrow; a declaration with multi-byte UTF-8 characters on earlier lines (fails if bytes are counted as chars); a CRLF file. |
| S5 | Tier-2 C#, with exact `(start, end)`. Covers: an Allman-brace method; a method with attributes (starts at the attribute line); an expression-bodied member; a member of a nested type (`Outer/Inner/M`); colliding overloads (`M(int)` and `M(string)` each get their own span); a doc-commented method (starts at the declaration, not the `///`). **(rev 2)** Two more rows, each asserting the exact `'\n'`-counted span: a lone `\r` inside a block comment above a member, and a U+0085 inside a block comment above a member. Both are in RoslynSpanTests and run against the real sidecar. |
| S6 | Precedence: an entity that both tiers produce takes the deep span. An entity the deep tier produced with no span (older sidecar, simulated by a fixture JSON without the fields) reports no span. |
| S7 | **No line ever reaches the store.** Index a fixture repo, insert 5 blank lines at the top of every file, re-index, and assert **zero** new fact versions and identical live bodies and evidence. Also assert that the fact set from indexing the fixture repo equals the set produced before this change, against a committed expected snapshot or the existing indexer tests left unmodified. `AnalyzerVersion` and `code_index_version` are unchanged. |

S4 and S5 need real grammars and the sidecar. They must **run, not skip**, in the implementor's verification: point `ENGRAM_TREE_SITTER_DIR`/`ENGRAM_ROSLYN_SIDECAR` at the installed copies. A skip is not a pass, and the report must list them as executed.

**Reader and security (§3).**

| AC | Requirement |
|---|---|
| R1 | An entity whose relative path contains `..` (or an empty segment, or is rooted) makes `FileFreshness.Check` return `Unknown`. No expand view prints a path, and `details` says `Source unavailable: location unknown.` |
| R2 | A symlink inside the repo pointing at a file outside it gives `outside the repo`, and none of the outside file's bytes appear in the output. A symlink inside the repo pointing at a file inside it returns content. A repo registered through a symlinked parent directory returns content. |
| R2b (rev 2) | Ten nested in-repo directory links, `L0 → L1/x`, …, `L8 → L9/x`, with `L9 → <outside dir>`. The file is read through `L0/…`. Result: `outside the repo`, and none of the outside file's bytes appear. This is the Reviewer's reproduction, which the lenient depth cap of 8 lets through. |
| R2c (rev 2) | The same shape with **33** links, so the chain exceeds the strict cap. Result: `outside the repo`. |
| R2d (rev 2) | Ten nested in-repo directory links ending **inside** the repo. Content is returned, which proves the cap of 32 does not refuse a legitimate deep chain. |

The throw branch of strict canonicalisation has no row: there is no deterministic fixture that is also safe under root. The Reviewer verifies it by reading. It must return null and never a partially resolved path.
| R3 | A directory at the file's path gives `not a regular file`. The FIFO case depends on E2. |
| R4 | A file over the configured `max_file_bytes` (set small in the test's config) gives `over N bytes`. A stream-level test or seam proves at most `max+1` bytes are read. |
| R5 | A NUL byte in the head gives `binary`. |
| R6 | An unreadable file (permissions `UnixFileMode.None`) gives `unreadable`. Expand returns normally. |

**Views (§4).**

| AC | Requirement |
|---|---|
| D1 | A symbol fact on a fresh file: page 1 contains, in order, the Body, the `Indexed code:` line, the hint, `Source: … · lines a–b of N`, and exactly lines a..b. Assert the block text by equality. |
| D2 | Stale: after indexing, edit a line **inside** the method. The header has ` · changed since indexed`, and the shown text contains the edited line. **(rev 2)** Two rows: keep the existing markdown-section row, which runs everywhere, and add a C# member row through the existing `SidecarOnly` gate. The C# row edits a line inside the method body and asserts that the span still covers the method and shows the edited line. |
| D3 | Rename the symbol in the file. The output is the whole file plus ` · not in the current file, whole file`. |
| D4 | The deep tier is unavailable (grammar dir or sidecar path set to a nonexistent location in the test) for a member fact. The output is the whole file plus ` · analyzer unavailable, whole file`. |
| D5 | A section fact gives the section range. A file fact gives `1…N`. |
| D6 | Paging over a long span with a small `budget_tokens`: more than 3 pages, the concatenation equals the unpaged text, each page's length ≤ budget, and the footer format is unchanged. |
| D7 | `details` on a non-code fact is byte-identical to before (existing tests unmodified and green). |
| D8 | `evidence`/`source` have the new gist sentence when `File` is non-null and the state is not Missing, the old sentence otherwise, and never any source text. |
| D9 | `history`/`related` are unchanged. |
| D10 | The golden file matches §5 exactly. `McpToolSurfaceBudgetTests` is green with 6550 and 11 unchanged. |
| D11 | Expand writes nothing: `fact`, `entity` and `repo_registry` row counts and `max(id)` are equal before and after a `details` call. |
| D12 (rev 2) | Drive `details` on a C# member through a **stub sidecar** that answers the fact's file with `endLine` = N + 50. The stub is an executable script reached through the same sidecar-location input the integration tests already override; if a stub mechanism already exists, reuse it. Result: the whole file, ` · analyzer unavailable, whole file`, and expand returns normally. A second row with `startLine` = 0 gives the same result. |

**Falsification**, per CLAUDE.md: against a committed tree, with `git diff --quiet` checked before each arm.

| Arm | Break | Must redden |
|---|---|---|
| F1 | Put the span start line into the `declared-as` evidence or body | S7 |
| F2 | Delete the lexical check in `FileFreshness` | R1 (not R2) |
| F3 | Delete the physical check | R2 (not R1) |
| F4 | Count UTF-16 chars instead of bytes in the tier-1 line conversion | S4's multi-byte case |
| F5 | Use the node's `FullSpan` (with trivia) in the sidecar | S5's doc-comment case |
| F6 | Drop the Stale suffix | D2 |
| F7 | Make `evidence` always say "the details view reads the current source" | D8's Missing row |
| F8 (rev 2) | Make the physical check call the lenient `Canonical` | R2b and R2c (R2, R2d and R1 stay green) |
| F8b (rev 2) | Make `TryCanonical` return the as-spelled remainder at the cap instead of null | R2c only |
| F9 (rev 2) | Make the sidecar compute lines with `GetLineSpan` | S5's lone-CR and NEL rows only |
| F10 (rev 2) | Remove the out-of-range guard | D12 (it throws or reddens) |

---

## 8. Steps (each one builds, passes, and is committed alone)

1. **Single-file analysis and tier-0 spans.**
   - Extract the one composition (§2) used by `ProcessFile`.
   - Add spans for tier-0 declarations, sections and files.
   - Tests: S1–S3, S7.
   - No behaviour change visible through MCP.
2. **Tier-1 spans.** Tree-sitter declaration-node lines. Tests: S4, S6 (tier-1 half).
3. **Tier-2 spans.**
   - Add the sidecar fields and the optional client parse.
   - Tests: S5, S6 (missing fields).
   - Rebuild and publish the sidecar with the binary as the repo's build already does. Do not change the install scripts unless the sidecar's publish step does not already cover it; if it does not, stop and report.
4. **Reader and containment.**
   - Lexical check in `FileFreshness.Check`.
   - The shared containment helper.
   - Physical, regular-file, size, binary and bounded read.
   - Tests: R1–R6, and the existing FileFreshness tests stay green.
5. **Views and description.**
   - §4 composition.
   - Note sentence changes.
   - §5 description trims and golden file.
   - The §6 test updates.
   - Tests: D1–D11.
   - Falsification arms F1–F7.
6. **D75** in `docs/engram-implementation-plan.md`. Record:
   - the two rejected storage options and why;
   - the re-analysis decision and the rejected text-anchor alternative;
   - that spans are transient (S7 is the guard);
   - the containment split and why each check has its own test;
   - stateless paging, and that offsets assume an unchanged file;
   - the E1/E2 results;
   - the description arithmetic.

**Verification for every step:**
- `dotnet test` with the log captured to a file;
- report pass and skip counts per tier;
- tier 3 must run, against a published `./out`, and is not skipped;
- `ENGRAM_HOME` is sandboxed for any by-hand binary call;
- after the run, no leftover sidecar or testhost processes.

**Rework (rev 2).** These go on top of HEAD 666dd32. Each one builds and is committed alone.

- **R-a, containment fails closed.**
  - `PathCanonicalizer.TryCanonical`, using the shared walk.
  - `Verdict.Root`, set in `Check`.
  - The physical check uses `Root` and `TryCanonical`.
  - §3 step 0 "nothing at the path".
  - Rows R2b–R2d. Arms F8 and F8b.
- **R-b, tier-2 `'\n'` lines.**
  - Sidecar line counting per §2.
  - The out-of-range guard per §4.
  - S5 lone-CR and NEL rows, and D12 (two rows).
  - Arms F9 and F10.
  - Rebuild the sidecar.
- **R-c, the D2 C# row.** Through `SidecarOnly`.
- **R-d, D75 text** replaced per §12.

For each: run the targeted suites with grammars and the sidecar set, so S4, S5, D2 C# and D12 run rather than skip, then the full suite. Report pass and skip counts per tier.

---

## 9. NEEDS-EVIDENCE (the implementor runs these; the results go back to the Architect)

**E1, details latency.** Acceptance evidence; it does not block steps 1–5.
- **Setup:** published binary, sandboxed `ENGRAM_HOME`, real grammars and sidecar.
- **Index** a fixture or a checkout of this repo into the sandbox.
- **Measure** through the same MCP harness tier 3 uses:
  - 20 `details` calls on a C# member fact and 20 on a TS function fact, alternating the order of the two arms;
  - a calibration arm of 20 `details` calls on a non-code fact.
- **Report** p50 and p95 per arm.
- **Decides:**
  - C# p95 − calibration p95 ≤ 1,000 ms: accept as designed.
  - Above that: report to the Architect before sign-off. The design stands. A held sidecar process would be a separate spec; do not build one here.

**E2, FIFO detection under .NET 10 (macOS; Linux too if available).** Sandbox-safe probe:

```sh
T=$(mktemp -d); [ -n "$T" ] && [ -d "$T" ] || exit 1
mkfifo "$T/p"; printf 'x' > "$T/f"; mkdir "$T/d"
cat > "$T/probe.cs" <<'EOF'
foreach (var p in args) {
  var i = new FileInfo(p);
  Console.WriteLine($"{p}: attrs={i.Attributes} exists={i.Exists} linkTarget={i.LinkTarget ?? "-"}");
}
EOF
dotnet run "$T/probe.cs" -- "$T/p" "$T/f" "$T/d"
```

- Report the three lines verbatim.
- **Decides:**
  - If the FIFO's attributes are distinguishable from the regular file's (for example, `Normal`/`Archive` versus anything else), step 4 refuses non-regular files by attribute, and R3 adds a FIFO row.
  - If they are not distinguishable, step 4 refuses directories only, and D75 records the residual risk. That risk is: a FIFO at an indexed path would block the read. It is accepted because git cannot track FIFOs and the indexer's own read has the same exposure.

---

## 10. Open questions for the user (recommended defaults are already in the design)

- **Q1. Should a symbol's span include its leading doc comment?** Default: **no**, for one rule across tree-sitter and Roslyn, since the Roslyn summary is already an `about` fact.
- **Q2. Should every source line carry a line-number prefix?** Default: **no**, because the header carries the range. It would cost about 15% more tokens.
- **Q3. Is a per-call sidecar spawn on C# `details` (about 300 ms, unmeasured; E1) acceptable?** Default: **yes**, since one tool call is far cheaper than an extra model turn.
- **Q4. Should `evidence`/`source` inline the source too?** Default: **no**. There is one place for it (`details`); the other two point at it.

## 11. Risks

- **Spans that leak into fact bodies or comparisons** recreate exactly the rejected design. S7 and F1 are the guard. Read every value-equality use of any record that gains a span field.
- **Older installed sidecar:** C# members fall back to the whole file (`analyzer unavailable`) until it is reinstalled. Correct but degraded. It is not surfaced in `doctor`; that is out of scope.
- **Description headroom** is about 14 chars after §5. The next description change must trim.
- **Residual TOCTOU:** a symlink swapped between the physical check and the open. This is accepted: it needs write access to the checkout, and anyone with that already controls what the model reads.
- **The containment idiom stays in three places** (two legacy copies plus the new helper). Migrating the legacy two is a follow-up, not part of this work.
- **(rev 2) The indexer reads through symlinks with no containment check.** This predates this spec and is out of its scope. A tracked `x.md → <file outside the checkout>` is listed by `git ls-files`, read by `CodeIndexer`, and its lead sentences (≤60 tokens) become a stored gist. That can include secrets, which recall then hands to the model. The physical check in this spec guards only the full read in `details`. **Ruling: record it in D75 as a residual, and fix it in a separate spec, recommended as the next item.** It changes what the scanner admits and interacts with deletion semantics (D53), so it needs its own design and tests. → NEEDS-ARCHITECT (a new brief), at the Orchestrator's discretion.

---

## 12. D75 text (rev 2): exact replacement paragraphs

Replace D75's containment paragraph with **A**. Add **B** and **C** after it. Leave the rest of D75 as committed. Copy the wording verbatim; the Implementor may only re-wrap lines.

**A. Containment.**
> Containment is checked twice, and the two checks have different jobs. The lexical check lives in `FileFreshness.Check`. A relative path with an empty, `.` or `..` segment, a rooted one, or one whose full path leaves the repo root makes the verdict `Unknown`, so no expand view ever prints a path outside the repo. The physical check sits beside the read. It canonicalises the root and the file strictly: every symlink on the way to the file is resolved, the file's own last segment included, and it refuses anything that does not land inside the resolved root. Strict means fail closed. A link that cannot be inspected, or a chain of more than 32 links (macOS's own limit), counts as outside. The lenient canonicaliser the queue uses stops at eight links and carries on with the rest of the path as spelled, and that is a fail-open: ten nested in-repo directory links ending outside the checkout were measured reading the outside file as inside. The physical check would also refuse a `..` escape, because full-path normalisation folds it before the comparison. What stops the two checks from being one rule written twice is their tests. The lexical check is asserted on the verdict's state and path, which every view prints. The physical check is asserted on the read, through symlink chains containing no `..` at all. Delete either check and only its own test reddens.

**B. Line numbering.**
> Every tier numbers lines the same way, by counting `'\n'`. Tier 0 splits on it, tree-sitter's newline table counts it in UTF-8 bytes, and the Roslyn sidecar counts it in the text before the node's start and before the node's last character. The sidecar does not use Roslyn's line map, which also breaks on a lone `\r` and on U+0085. A probe with one lone `\r` in a comment above a method had Roslyn report line 5 where the reader counts line 4, so `details` would have shown the wrong lines as the symbol, with no label. A span that still falls outside the file is treated as no span: the whole file, labelled `analyzer unavailable`. A disagreement between the sidecar and the reader can therefore neither mislabel code nor throw.

**C. Residual, outside this decision.**
> The indexer itself reads through symlinks with no containment check. A tracked link to a file outside the checkout is listed by `git ls-files`, read, and summarised into a stored gist of up to 60 tokens, which recall can return. This decision's physical check guards only the full read in `details`. Closing the indexer's path changes what the scanner admits and interacts with the rule that a partial scan never deletes (D53). It is therefore its own change, and not part of this one.
