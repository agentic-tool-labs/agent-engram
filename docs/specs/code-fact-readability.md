# Code-fact readability — spec

Implementer: implementor
Reviewer: reviewer

Branch: `ah/code-fact-readability` (already checked out). Status: ready to implement; no blocking
evidence items. The per-problem decisions are in §0. The user may reverse any of Q1–Q4 in §8;
each reversal is local to one step.

**Amendment 1 (2026-09-30, after the implementor reported three gaps):**
- **Tool-surface ceiling (G1).** `McpToolSurfaceBudgetTests.MaxDefinitionChars` stays **6550**. It is not re-baselined.
  - Step 3's description text is replaced with exact wording that nets **+20** chars (§6.2).
  - Step 4 is **deleted**: the recall description sentence duplicated what `engram_navigate`'s own description and `repo` parameter already say (§4).
- **CLI miss (G2).** The CLI miss change is removed as unreachable (§6.2).
- **Examples and AC4.7 (G3).** The §1 elision example is corrected, and AC4.7 is restated to what `Pack` guarantees (§3.4).

**Amendment 2 (2026-09-30, Architect sign-off on the review):**
- **Spec text corrected.** D67 becomes D74 (§7). The §3.2 item 4 note on `FactCatalog.ToCannedFact` is corrected.
- **Fix-now items for the implementor, in one follow-up commit:**
  - **(a) Missing wording.** A `Missing` verdict reads `Was at <path> (missing).` (§2.3 item 2). Add test `Expand_IndexedCodeFact_MissingFile_SaysWasAtNotRead` (§2.6).
  - **(b) One display form.** `IndexedCodeNote.Build` must derive the location once, through the §1 derivation.
    - It may not re-format `{repo}:{rel}` inline.
    - `CodePaths.LocationText` and `ElidedLocationText` are the only producers of the display form (§1).
    - Take the repo and rel for the navigate hint from the same single `CodePaths.LocationOf` result.
  - **(c) AC1.1 coverage.** AC1.1 is asserted per view, as one theory over `details`/`evidence`/`source` with a registered disk path (§2.6).

Decisions this spec relies on (see `CLAUDE.md` and `docs/engram-implementation-plan.md`):
D2 (path is addressing), D8 (derived state only; code facts are regenerable), D30 (`explain`
describes the ranker that runs), D44 (coverage comes from lane agreement), D57 (`· vN`), D59/D60
(the single ranking statement, `seed_k` per lane), and D64 (`· +N`). This spec adds no schema,
no migration, no fact writes and no `AnalyzerVersion` bump.

---

## 0. Decisions

| # | Problem | Decision |
|---|---------|----------|
| 1 | `engram_expand` on a code handle repeats the 60-token gist | **IN.** Indexer-written code facts get one shared *indexed-code note* in the `evidence`, `source` and `details` views. The note gives the repo and file, the on-disk path when it is known, how fresh that file is, that Engram holds only the gist, and a navigate hint. **Not stored: full chunks and line ranges** (§2.1). |
| 2 | `engram_browse` miss gives no hint | **IN.** On a miss, show the nearest ancestor that exists and its children. When that ancestor is `/`, its children are the top-level roots. The tool description names the code path form. |
| 3 | `engram_recall` has no `repo` filter | **OUT. No description change** (amended). `engram_navigate`'s description already claims "code by name", and its `repo` parameter says "Restrict matches to one repo's indexed code". #4 puts the repo on every code line. Rationale in §4. |
| 4 | A recall line for a code fact does not say which file | **IN.** The line becomes `(code · <repo>:<rel path> · 48d …)`, left-elided to 64 chars. The budget charges it automatically, because the estimate is taken on the finished line. |
| 5 | YAML/Helm files are indexed as one prose fact | **OUT (deferred).** Rationale and bounds for a future spec are in §5. |

**Scope cuts, named:**
- Full-chunk storage.
- Line ranges.
- A recall `repo` filter.
- A YAML analyzer.
- Listing roots on every browse miss (they appear only when the nearest ancestor is `/`).
- Location on session or prior-session lines (those facts are not code).
- Deduplicating the same file indexed in several repos or worktrees. This spec makes that visible but does not fix it.

**Why the location must name the repo (evidence gathered while writing this spec):** the recall
`code fact expand gist browse miss …` returned four live `code` facts with identical bodies
(`f113437`, `f343`, `f237560`, `f61194`). Expanding `f113437` gives
`/projects/engram/code/engram-2/docs/mcp-tool-descriptions.golden.txt`, and `f343`'s evidence is
`docs/mcp-tool-descriptions.golden.txt @ 87d3f8ba`. `ux_fact_live` allows one live fact per
subject and predicate, so these four are the same relative path under different repo segments. A
bare relative path would render all four identically.

These hits were also the golden *text file's* prose impression, not the tool's code. The location
token makes that visible at a glance.

---

## 1. Shared building block: one code-location derivation

**Facts:**
- `CodePaths.SplitRepoPath(path)` (`src/Engram.Core/CodePaths.cs:83`) returns `(RepoPath, RelativePath)` or null.
- It drops `#fragment`.
- `RepoPath` is `/projects/<project>/code/<repo>`.
- It returns null when the path has fewer than 6 `/`-segments.
- The repo slug is the last segment of `RepoPath`, and `CodePaths.Slug` restricts it to `[a-z0-9-]`.

**Requirement:**
- There is exactly one Core derivation of *(repo slug, relative path)* from a subject path. It is built on `SplitRepoPath`, not a second parser.
- The recall formatter (§3) uses it, and so does the expand note (§2).
- The display form is `<repo slug>:<relative path>`.
- The implementor chooses where it lives (`CodePaths` is the natural home).

**Elision (recall line only; expand always shows the full form).** The maximum is 64 UTF-16 chars
for the whole `<repo>:<rel>` token. If the token is longer:
1. Drop leading directory segments of `<rel>` one whole segment at a time. Replace them with `…/`, where `…` is U+2026, the same ellipsis `TruncateBody` uses.
2. Stop at the first result of 64 chars or fewer.
3. Never drop the repo slug or the final segment (the file name).
4. If `<repo>:…/<file name>` is still over 64, emit exactly that. A name is never cut mid-segment.
5. A `<rel>` with no directory is never elided.

Illustrative: `svc:src/main/java/com/acme/billing/invoice/InvoiceService.java`, which is 62 chars,
stays whole. The same path in a repo named `acme-billing-service` (79 chars) becomes
`acme-billing-service:…/acme/billing/invoice/InvoiceService.java`, which is 63 chars. That is
the first whole-segment drop that fits. *(Amended: the earlier example dropped one segment too
many, contradicting the rule; the rule governs.)*

The 64-char limit is a `const`, not config. It is an assumption; E1 in §7 can revise it but does not block.

---

## 2. Problem 1 — `engram_expand` on code facts

### 2.1 Decision and what is rejected

**Facts:**
- `Expand` (`src/Engram.Cli/EngramMcpTools.cs:346-385`) dispatches the views: `evidence` (:1653-1665), `source` (:1667-1677) and `details` (:1717-1759).
- There is no CLI twin, because no `ExpandCommand` exists.
- Code facts are written at `CodeIndexer.cs:625-638` with `Scope: "code"`, `LearnedVia: "observed"`, `Regenerable: true`, `Evidence: "{rel} @ {sha8}"` (:546) and `Details` never set.
- Subject kinds are `"file"`, `"symbol"` and `"section"` (`CodeAnalyzer.cs:58,86,119,177,185`).
- `StoredFact` (`FactStore.cs:22-38`) carries `SubjectId`, `SubjectPath`, `SubjectName`, `Scope`, `Regenerable` and `Evidence`, but **not** the subject kind.

**Rejected: storing full chunks.** I agree with the orchestrator:
- The chunk is a copy of a file the agent can read directly.
- It would grow the store by roughly the source size of every indexed repo.
- Every edit would churn a supersession row.
- The gist exists to be a pointer. Making the pointer work (disk path plus navigate) is cheaper and never stale in the direction that matters.

**Rejected: line ranges.**
- `CodeCandidate` (`CodeAnalyzer.cs:12`) carries no span, so adding one means new extraction work.
- Writing a line into a fact (body or evidence) turns every edit above a symbol into a new version of that symbol's fact. That is history churn with no belief change.
- The only clean home is a new derived table, which is a separate spec if it is ever wanted.

### 2.2 Which facts get the note

A fact is an **indexed code fact** iff `Scope == "code"` **and** `Regenerable == true`.

A `code`-scope fact produced by `engram_revise` is authored truth, not a gist. It gets
`Regenerable: false` and `LearnedVia: "stated"` (`EngramMcpTools.cs:455-468`), and it must not be
described as one. Every other fact's views stay byte-identical to today.

### 2.3 The indexed-code note (one builder, three views)

The note contains, in this order:
1. **Location.** The full `<repo>:<rel>` from §1, and the subject path.
2. **Where to read it.** The resolved on-disk file path and its freshness verdict, when known.
   - These must come from `FileFreshness.Check` (`src/Engram.Core/FileFreshness.cs:65`). It is already the one place that maps an entity path to `repo_registry.disk_path` (detached repos excluded) and then to a file on disk.
   - Extend its result so the caller can get the resolved file path alongside the verdict. For example, the `Verdict` can carry the path when one was resolved.
   - **Do not write a second `repo_registry` query.**
   - Show `Fresh` without a label, and `Stale` using the verdict's existing `Label` (`Read it at <path> (stale).`).
   - *(Amended after review.)* **`Missing` must not tell the model to read the file.** Phrase it as `Was at <path> (missing).`, keeping the `Label` word. A pointer to a file that is gone costs the reader a failed tool call. The disk path is still worth showing, because it says where the file was.
   - When no path is resolved (Unknown: detached, unregistered, or no `file_state` row), omit the disk path and keep the rest.
   - Do not change `Check`'s never-throws contract or the behavior of its existing callers.
3. **The gist statement.** Engram keeps only this indexed gist (~60 tokens), not the source, so there is nothing further to expand; read the file.
4. **Navigate hint.** This depends on the subject entity's `kind`, read from `entity.kind` by `SubjectId`. Never infer the kind from the path: sections and symbols both use `#`.
   - `symbol`: `engram_navigate` with query = `SubjectName`, relations `defined_at` / `members` / `callers`, `repo` = the slug.
   - `file`: `engram_navigate` with query = `<rel>`, relation `imports`, `repo` = the slug.
   - `section` (markdown heading) or any other kind: no navigate hint.

Illustrative wording, not prescriptive:
```
Indexed code: engram:src/Engram.Core/MemoryBrowser.cs (/projects/engram/code/engram/src/Engram.Core/MemoryBrowser.cs#Browse)
Read it at /Users/jim/git/repos/engram/src/Engram.Core/MemoryBrowser.cs (stale).
Engram keeps only this indexed gist (~60 tokens), not the source — there is nothing more to expand.
Structure: engram_navigate "Browse" defined_at | members | callers, repo "engram".
```

### 2.4 View behaviour for indexed code facts

| View | Behaviour |
|------|-----------|
| `evidence` | Today's text unchanged, then a newline, then the note. |
| `source` | The origin phrase becomes `indexed from <repo>:<rel>` regardless of `Sitting`, replacing "recorded outside any tracked session — seeded, indexed, or written by the CLI". The rest of the sentence is unchanged ("learned via …", "currently believed" / "closed"). Then a newline and the note. |
| `details` | The paged document becomes `Body + "\n\n" + note` instead of `Body` alone. Code facts have no `Details`. Paging, offsets, the `showing chars x–y of N` footer and the `budget_tokens` contract then hold unchanged, because the note is inside the paged text. |
| `history`, `related` | Unchanged. |

### 2.5 Acceptance criteria

- **AC1.1** For an indexed code `symbol` fact whose repo is registered with a disk path, `details`, `evidence` and `source` each contain:
  - `<repo>:<rel>`;
  - the resolved disk file path;
  - the gist statement;
  - `engram_navigate`, the symbol's `SubjectName`, and the repo slug.
- **AC1.2** `source` for an indexed code fact does not contain `outside any tracked session`, and does contain `indexed from <repo>:<rel>`.
- **AC1.3** For a `file` subject, the hint uses `imports` with `<rel>`. For a `section` subject, there is no `engram_navigate` hint.
- **AC1.4** For a detached or unregistered repo, there is no disk path, and the location and gist statement are still present.
- **AC1.5** A `code`-scope fact with `Regenerable == false` gets output byte-identical to today in all five views.
- **AC1.6** Non-code facts are byte-identical to today in all five views. The existing `McpBrowseExpandReviseTests` expand tests pass unmodified.
- **AC1.7** Paging `details` for an indexed code fact with a small `budget_tokens` and following each continuation offset concatenates to exactly `Body + "\n\n" + note`.
- **AC1.8** `FileFreshness.Check`'s existing callers and tests behave exactly as before.
- **AC1.9** *(Amendment 2.)* When the indexed file is gone from disk (`Missing`), the note contains `Was at <path> (missing).` and does not contain `Read it at`.

### 2.6 Tests

Tier 2, in `tests/Engram.Integration.Tests/McpBrowseExpandReviseTests.cs`, against a sandbox home:
- Seed through `FactStore.Remember` with `FactWrite` values that match `CodeIndexer.cs:625-638`.
- Use subject `/projects/p/code/r/src/a.cs#Foo`, kind `symbol`, and a sibling `file` subject `/projects/p/code/r/src/a.cs`.
- For the disk path cases, add a `repo_registry` row plus a `file_state` row pointing at a temp directory holding `src/a.cs`. Use existing registry writers if they fit; otherwise use plain SQL in the test.

Falsify each test by breaking the named thing, confirming the test goes red, then restoring:

| Test | Falsify by |
|------|-----------|
| `Expand_IndexedCodeFact_Details_SaysGistOnlyAndWhereToRead` (AC1.1) | removing the note from `details` |
| `Expand_IndexedCodeFact_Source_NamesTheIndexedFile` (AC1.2) | leaving the old origin phrase |
| `Expand_IndexedCodeFact_Evidence_KeepsEvidenceAndAddsNote` (AC1.1, 1.6) | dropping the original evidence line |
| `Expand_CodeFile_HintsImports_Section_HasNoHint` (AC1.3) | inferring kind from `#` instead of `entity.kind` |
| `Expand_IndexedCodeFact_DetachedRepo_OmitsDiskPathOnly` (AC1.4) | making a null disk path throw or suppress the whole note |
| `Expand_RevisedCodeScopeFact_GetsNoGistNote` (AC1.5) | keying the note on `Scope` alone |
| `Expand_IndexedCodeFact_Details_PagesAcrossTheNote` (AC1.7) | appending the note outside the paged text |
| `FileFreshness` tests, existing (AC1.8) | control only; must stay green |
| *(Amendment 2)* `Expand_IndexedCodeFact_EveryNoteView_CarriesAllElements`: one theory over `details`/`evidence`/`source` with a registered disk path (AC1.1). It may replace the per-view element asserts in the three tests above. | giving one view its own note text instead of `IndexedCodeNote.Build` |
| *(Amendment 2)* `Expand_IndexedCodeFact_MissingFile_SaysWasAtNotRead`: register the repo and `file_state`, but no file on disk (AC1.9) | using the `Read it at` phrasing for `Missing` |

---

## 3. Problem 4 — code location on the recall line

### 3.1 Facts

- `RecallEngine.FormatFactLine` (`src/Engram.Core/RecallEngine.cs:636-644`) renders `[{Id}] {body} ({Scope} · {AgeDays}d{version}{judged}{marker})`.
- Production recall is `RecallRanker.Pack`. `RecallRanker.ReadCandidate` (`src/Engram.Core/RecallRanker.cs:300-339`) reads `path` (:315), builds a `CannedFact` at :325 without it, formats the line, then charges `TokenEstimator.Estimate(line)` (:339).
- `RecallEngine.Pack` / `BuildCandidates` (:405-490) is **internal and test-only**, the object ranker. Its `CannedFact`s come from the caller's `facts` list (`Entry.LongTerm`, :95, :419-427), and it also charges `Estimate(line)` (:~489).
- `RetrievalExplainer` renders no lines itself. `ExplainCommand` prints `candidate.Line`, so `explain` shows whatever the shared formatter produced (D30 parity by construction).
- `PrimerBuilder` does not call `FormatFactLine`.
- The only production `new CannedFact(` is `RecallRanker.cs:325`. Tests construct it at `PrimerBuilderTests.cs:176,190,234` and `RecallEngineTests.cs:159-263`. The seed corpus uses target-typed `new(` at `CannedFacts.cs:46-93`.
- No test in `tests/` contains `(code ·`.

### 3.2 Change

1. `CannedFact` (`src/Engram.Core/CannedFacts.cs:10-21`) gains an **optional trailing member carrying the fact's subject path, defaulting to null**. It is optional so that the seed corpus, `PrimerBuilderTests` and existing `RecallEngineTests` sites compile unchanged.
2. `RecallRanker.ReadCandidate` passes `path` into that member for `FactOrigin.LongTerm`.
   - Assumption to verify: the ranking statement's `path` column is the subject entity's path for long-term rows.
   - If it is not, select the subject path explicitly; do not derive it.
3. `FormatFactLine` renders ` · <location>` **immediately after the scope** when `Scope == "code"` and the path splits (§1, elided form). Otherwise the line is byte-for-byte unchanged. Result: `(code · engram:src/Engram.Core/MemoryBrowser.cs · 48d · v3 · judged · +1.2k)`.
   - The location keys on **scope**, not on regenerability. The file a fact addresses is true of a revised code-scope fact too (D2: path is addressing).
4. The object ranker gets its `CannedFact`s from `FactCatalog.ToCannedFact` (`FactCatalog.cs:54`), which must set the subject path too. The implementor did this at `FactCatalog.cs:118`, and that is what keeps AC4.8's equivalence meaningful.
   - The other caller of `ToCannedFact`, `PrimerSummary.cs:102`, is unaffected: the primer never calls `FormatFactLine`, whose only callers are `RecallEngine.cs:618` and `RecallRanker.cs:324`.
   - *(Amended after review: this previously said the object ranker needed no production change.)*

### 3.3 Budget, coverage and markers

- **Budget.** Both routes charge `TokenEstimator.Estimate` on the *finished* line (`RecallRanker.cs:339`, `RecallEngine.cs:~489`), so the location is charged by construction and packing logic does not change.
  - Invariant: the location is rendered inside `FormatFactLine`, never appended after the estimate.
  - Cost: ~13 tokens for a typical 46-char token, at most ~18 at the 64-char cap. On a code-heavy recall at the default 500-token budget this is about one fewer line.
  - Accepted: on a code line, the file is the most decision-relevant token (see §0 evidence).
- **Coverage (D44/D60).** Unaffected. It is computed from lane ranks, never from line text.
- **Markers (D57 `· vN`, `· judged`, D64 `· +N`).** They keep their order and stay the tail of the paren group, because the location precedes the age.
  - Collision: a repo slug that looks like a marker (a repo named `v2`) renders `· v2:…`. Only a `Contains(" · v2")` check without the closing `)` would misread it. Existing tests assert `· v2)`.
  - Do not add a marker check of the loose form.

### 3.4 Acceptance criteria

- **AC4.1** A long-term `code` fact with path `/projects/p/code/r/src/a.cs#Foo` renders exactly `[fN] <body> (code · r:src/a.cs · 3d)`.
- **AC4.2** With versions, judged and withheld content, it renders exactly `(code · r:src/a.cs · 3d · v2 · judged · +1.2k)`.
- **AC4.3** Non-`code` scope, even with a code-shaped path, is byte-identical to today.
- **AC4.4** A `code` fact whose path does not split renders as today.
- **AC4.5** Elision follows §1 exactly:
  - at 64 chars, unchanged;
  - at 65, the first segment is dropped;
  - an over-long file name gives `r:…/<name>`;
  - a directory-less `rel` is never elided.
- **AC4.6** Through `RecallRanker.Pack` on real SQLite, a seeded code fact's line carries its location.
- **AC4.7** For a corpus of code facts with long paths, the summed `TokenEstimator.Estimate` of the packed fact lines is at most `budget`.
  - *(Amended.)* `Pack` does not charge its header or footer against the budget; that is by design and predates this spec. So `Estimate(result.Text) <= budget` was never an invariant.
- **AC4.8** `RecallRankerEquivalenceTests` stays green and covers at least one `code`-scope fact. If its corpus has none, add a case with one, feeding the object ranker the same subject path.

### 3.5 Tests

| Test | Tier | Falsify by |
|------|------|-----------|
| `FormatFactLine_CodeFact_ShowsRepoAndFileBeforeAge` (AC4.1) | unit, `RecallEngineTests.cs` | not rendering the location |
| `FormatFactLine_CodeFact_MarkersStayAfterAge` (AC4.2) | unit | placing the location after the markers |
| `FormatFactLine_NonCodeScopeWithCodePath_IsUnchanged` (AC4.3) | unit | keying on path splittability instead of scope |
| `FormatFactLine_CodeFact_UnsplittablePath_RendersAsBefore` (AC4.4) | unit | throwing or rendering an empty `· ·` |
| `…Elision_*` boundary vectors (AC4.5) | unit | an off-by-one in the limit; cutting mid-name |
| `Pack_CodeFact_LineCarriesLocation` (AC4.6) | tier 2, whichever integration file already drives `RecallRanker.Pack` | passing null for the path in `ReadCandidate` |
| `Pack_CodeFactsWithLongPaths_NeverExceedBudget` (AC4.7) | tier 2 | computing the estimate on a location-less line, then appending the location |
| `RecallRankerEquivalenceTests` code case (AC4.8) | tier 2 | leaving the object ranker's code fact pathless; the equivalence must go red |

---

## 4. Problem 3 — recall `repo` filter: rejected, documented

**Rationale:**
1. **The filter would have to sit inside each lane.** D60 caps each lane at `seed_k` (32).
   - Post-filtering 32 seeds by repo starves the result. It then reports `coverage: none/partial` about a store that holds the answer, which is exactly the false-empty D44 and D60's availability note exist to prevent.
2. **Filtering in-lane is a ranker redesign.** It changes the single ranking statement (D59) for the FTS lane, the `fact_token` overlap lane and the sqlite-vec KNN lane.
   - `vec0` KNN has no path predicate, so it means over-fetching or partitioning.
   - It needs D58/D60 latency re-measured at 50k.
   - `RetrievalExplainer` would have to mirror it (D30).
3. **The semantics are unresolved.** Does `repo` drop every non-code fact (decisions, session notes)? Either answer surprises someone.
4. **The need is already served.**
   - `engram_navigate` takes `repo` (`EngramMcpTools.cs:733-739`, `RepoNeedle` :1532) for code by name.
   - #4 puts the repo on every code line.

**Deliverable: none** (amended; this previously appended a sentence to `engram_recall`'s description).
- Tool definitions are serialized into every session (D17), and `McpToolSurfaceBudgetTests` holds them at 6550 chars with ~33 of headroom.
- A ~79-char sentence saying "use engram_navigate with repo for code by name" would repeat what the tool surface already says:
  - `engram_navigate`'s description opens "Looking for code by name … reach for this BEFORE Read/Grep/Glob";
  - its `repo` parameter reads "Restrict matches to one repo's indexed code, by its slug."
- It would therefore spend budget on every session for no new information.
- **AC3.1:** `engram_recall`'s description and parameters are byte-identical to before this spec.

---

## 5. Problem 5 — YAML/Helm extraction: deferred

**Rationale:**
1. **A bump is store-wide.**
   - `CodeAnalyzer.AnalyzerVersion` (`CodeAnalyzer.cs:36`, now 6) feeds one store-wide stamp: `CodeIndexer.CurrentVersion` (:80), stored in `schema_meta.code_index_version`.
   - A bump forces a full re-read of the *next* repo indexed (:166-170), and the stamp is rewritten after that one repo's apply (:330-332).
   - Other repos are then not version-forced. This is documented as "D-code-nav gap b" (:75-76).
   - A readability fix should not carry a store-wide re-extraction whose reach across repos is itself unsettled.
2. **There is no YAML parser.** Core references only Sqlite and LLamaSharp. Helm `templates/*.yaml` hold Go template directives and are not valid YAML, so extraction would be a hand-written line scanner.
3. **It is new capability, not readability.**
   - Today `.yaml` resolves to `LanguageRegistry.Text` (`LanguageRegistry.cs:262,482`) and gets one `about` prose fact (`CodeAnalyzer.cs:51-58`).
   - With #1 and #4, that fact now names its file and points at it on disk.

**Bounds for a future spec (non-binding):**
- top-level keys only (indent 0);
- at most 40 keys per file;
- skip `{{ … }}` lines;
- one `declared-as` fact per key through the existing `Cap`;
- Tier 0 (no calls or imports);
- a prerequisite: settle whether a bump reaches files in repos other than the first one re-indexed. That is a NEEDS-EVIDENCE item for that spec, not this one.

---

## 6. Problem 2 — browse miss: nearest existing ancestor

### 6.1 Facts

- `MemoryBrowser.Browse` (`src/Engram.Core/MemoryBrowser.cs:34`) returns null iff no entity lies at the path or beneath it (:146-148).
  - "Beneath" means the next character after the prefix is `/` or `#` (:67-75).
  - `/` normalizes to prefix `""`, so `Browse(conn, "/", 1)` lists the top-level roots (:39-52).
  - Trailing `/` is trimmed. A missing leading `/` is not fixed.
  - Its `substr` predicate cannot use `entity.path`'s unique index, so every call scans `entity`.
- `entity.path` is `TEXT NOT NULL UNIQUE` with no COLLATE, so BINARY (`docs/engram-schema.sql:66-77`).
- The MCP miss text is at `EngramMcpTools.cs:311-315`, and the CLI miss is at `src/Engram.Cli/BrowseCommand.cs:75`. Only the `MemoryBrowser` data layer is shared; formatting is per surface.
- MCP renders children through `AppendChildren` (`EngramMcpTools.cs:1546`).

### 6.2 Change

1. **A new public `MemoryBrowser` member returns the nearest ancestor path for which `Browse` would be non-null, or null when the store holds no entity at all.**
   - **Normalization** is identical to `Browse`'s (trim trailing `/`; empty means `/`). Share it rather than copy it.
   - **Candidate ancestors** are successive prefixes of the normalized path, each cut at its last `/` or `#`. The walk ends at `/`, which is always the final candidate. So `a.cs#Foo` yields `a.cs` first, and a path with no leading `/` ends at `/`.
   - **Membership rule** is the same as `Browse`'s: an entity exactly at the prefix, or one continuing with `/` or `#`. It must be evaluated with **index-range probes**, not `substr` scans. The range idiom already in use at `FactStore.cs:416` and `DirectiveFacts.cs:98` fits, for example `path >= p||'/' AND path < p||'0'` and `path >= p||'#' AND path < p||'$'`, plus the exact match. For `/`, membership means any entity exists.
   - **Cost** is at most (segments + 1) indexed probes, followed by exactly one `Browse` of the found ancestor. That is the same cost as browsing that ancestor directly.
   - **Do not touch** `Browse`'s span-slicing loop (:94-142). See the `CLAUDE.md` allocation note, which says the test suite is blind to changes there.
2. **MCP** (`EngramMcpTools.Browse`, miss branch):
   - Keep `Nothing in memory under {path}.`
   - Follow it with the nearest ancestor's header in the existing `— N facts here, M under it` shape, then its depth-1 children rendered by the **existing** `AppendChildren`, including `…and N more`. Top facts are not listed.
   - Then one line naming the code path form and that `engram_recall` needs no path.
   - When the ancestor is null (empty store), keep today's message exactly.
3. **CLI: no change** (amended). `BrowseCommand.Loop` starts at `/` and descends only into listed paths, so its miss branch (`:75`) fires only on an empty store. There the ancestor is null and the text would be unchanged anyway.
   - A CLI ancestor hint would be dead code, so remove any `BrowseCommand` change and its test from the working tree. `BrowseCommand.cs` stays byte-identical to `HEAD`.
   - If `engram browse` ever takes a start path, that change owns the hint.
4. **Tool surface: exact wording** (amended; the ceiling stays 6550).
   - **Tool description** (`EngramMcpTools.cs:288-291`): only the final sentence changes.
     - From: `Paths look like /people/jim or /projects/acme.`
     - To: `Paths look like /people/jim or /projects/acme/code/<repo>/<file>#Symbol (indexed code).`
     - That is **+41 chars**: the inserted text is `/code/<repo>/<file>#Symbol (indexed code)`.
     - The first two sentences stay verbatim.
     - `acme` serves as both the example project and the path prefix, so the old `/projects/acme` example is still shown.
   - **`path` parameter description**:
     - From: `The memory path to list, e.g. /projects/acme.`
     - To: `The memory path to list.`
     - That is **−21 chars**. The examples now live once, in the tool description.
   - **`depth` parameter description**: unchanged.
   - **Net: +20 chars.** Against the implementor's measured pre-step-3 baseline of ~6517, that gives ~6537 ≤ 6550.
   - **Do not edit `MaxDefinitionChars` or its comment.** If the measured total still exceeds 6550, stop and report the measured number rather than re-baselining.
   - Update `docs/mcp-tool-descriptions.golden.txt` in the same commit.
   - **Why not re-baseline:** the ceiling's own comment reserves re-baselines for a deliberate feature whose cost cannot be carried. This one can be carried at +20. The replaced example still teaches the top-level form (`/people/jim`, `/projects/acme…`), and the code form now costs one example rather than a sentence.

Illustrative MCP miss:
```
Nothing in memory under /projects/engram/code/engram/src/Foo.cs.
Nearest path that exists: /projects/engram/code/engram/src — 0 facts here, 812 under it
  Engram.Core — 700 facts
  Engram.Cli — 112 facts
Indexed code lives at /projects/<project>/code/<repo>/<file path>; engram_recall searches by content and needs no path.
```

### 6.3 Acceptance criteria

- **AC2.1** A deep miss returns the deepest existing prefix.
- **AC2.2** Sibling-prefix trap: with only `/projects/engram-docs/x` stored, a miss at `/projects/engram/y` returns `/projects`.
- **AC2.3** Fragment miss: a miss at `…/a.cs#Missing` with `…/a.cs` stored returns `…/a.cs`, not its directory.
- **AC2.4** No leading `/`: a miss at `projects/x` returns `/`, and MCP then lists the roots.
- **AC2.5** A trailing `/` gives the same answer as none.
- **AC2.6** An empty store gives null, and the MCP and CLI text are byte-identical to today.
- **AC2.7 Equivalence.** For every path in a fixture, (probe says a member exists) == (`Browse(path, 1) is not null`). The fixture covers every seeded path, every ancestor of each, the misses above, the `-docs` sibling, `#` children and `/`.
- **AC2.8** The MCP miss output contains the ancestor path and at least one child name. At the top level, it contains the root names.
- **AC2.9** *(Amended.)* `src/Engram.Cli/BrowseCommand.cs` and its tests are unchanged from `HEAD`.
- **AC2.10** The golden file matches the exact wording in §6.2 item 4. `McpToolSurfaceBudgetTests` passes with `MaxDefinitionChars` still 6550, and `ToolCount_IsDeliberate` still expects 11.

### 6.4 Tests

| Test | Tier and file | Falsify by |
|------|---------------|-----------|
| AC2.1–2.6 as `NearestAncestor_*` | tier 2, beside `MemoryBrowserRootTests` (real SQLite) | per case: splitting on `/` only (2.3); a range without the separator, e.g. `< p||'~'` (2.2); not ending at `/` (2.4) |
| `NearestAncestor_AgreesWithBrowseMembership` (AC2.7) | tier 2 | changing the probe's separator set, which red-flags drift between the two rules |
| update `Browse_WhereNothingIs_SaysSoInsteadOfInventingStructure` (`McpBrowseExpandReviseTests.cs:62`), add a top-level-miss case (AC2.8) | tier 2 | restoring the old miss string |
| golden + `McpToolSurfaceBudgetTests` (AC2.10) | existing | — (both are exact/ceiling guards already) |

---

## 7. Implementor steps

Each step builds with warnings-as-errors, passes the full suite, and is committed on its own.
Commit messages explain why, per `CLAUDE.md`. No comment narrates the change.

1. **Location plus recall line (#4).** §1 and §3: the `CannedFact` member, `ReadCandidate`, `FormatFactLine`, the elision helper, and the tests in 3.5, including the equivalence case.
2. **Expand note (#1).** §2: the `FileFreshness.Check` result extension, the kind lookup, the note builder, the three views, and the tests in 2.6. Depends on step 1's location derivation.
3. **Browse miss (#2).** §6: the Core ancestor member, the MCP miss branch, the exact description and `path` wording in §6.2 item 4, the golden file, and the tests in 6.4. No CLI change.
4. *(Deleted by amendment 1; §4 now delivers nothing.)* Numbering is kept so reports stay comparable.
5. **Decision record.** Append **D74 — code-fact readability** (*amended: D67 was already taken; D74 is the next free number*) to `docs/engram-implementation-plan.md` (follow the existing D-entry format), stating the reasoning rather than the diff:
   - the location token and why it names the repo;
   - the indexed-code note, and that `FileFreshness` is the one disk resolver;
   - the rejected chunk and line-range storage;
   - the ancestor probe and its equivalence guard;
   - the rejected recall `repo` filter, and that no description sentence was added for it because `engram_navigate` already says it;
   - that the tool-surface ceiling was held at 6550 by trimming instead of re-baselining (§6.2 item 4);
   - the deferred YAML work.

   Add a `CLAUDE.md` invariant paragraph only if the reviewer asks; none is required.

**Verification, for every step:**
- `dotnet build` clean.
- `dotnet test` over all tiers with tier 3 driven by a freshly published `./out/engram`. Report the skip count, not only the pass count; a skipped tier 3 is not a pass.
- `ENGRAM_HOME` is sandboxed for any manual binary run. Nothing touches the real `~/.engram`.
- Each falsification in the tables is performed against a **committed** tree. Check it with `git diff --quiet` before trusting a red arm (D60's lesson), then restore it.
- If an existing tier-3 test pins recall, browse or expand text that this spec changes, update it in the same step and say so in the report. Grep found no `(code ·` in tests, but tier 3 was not read line by line.

**NEEDS-EVIDENCE:** none blocking. Two optional items can run inside step 1 and step 3; neither
reverses a decision without coming back to the Architect:
- **E1 (tunes the §1 cap).** On a `VACUUM INTO` copy of the real store, opened read-only with `ENGRAM_HOME` pointing at a sandbox, compute the length distribution of `<repo>:<rel>` over live `code`-scope subjects. Report p50, p90 and p99.
  - p90 ≤ 64: the cap stands.
  - p50 > 64: come back to the Architect before changing the form.
  - Anything between: report it and keep 64.
- **E2 (confirms the miss cost).** On the same copy, time `engram browse` on a top-level miss (ancestor `/`) against `engram browse /`. They should be within noise, because the miss adds only indexed probes. A miss slower by more than 50 ms is a finding to report.

---

## 8. Open questions for the user

- **Q1 — location form.**
  - Recommended: `<repo>:<rel>`, because of the four identical-path facts in §0.
  - The bug report asked for a bare `<rel path>`.
  - If the user prefers bare, delete the slug from the recall form. Expand keeps the full form either way.
- **Q2 — roots.**
  - Chosen: they appear only when nothing below `/` matched, because a deeper ancestor's children are strictly more useful.
  - Listing roots on every miss costs a `Browse("/")` (a full scan) per miss.
- **Q3 — recall `repo` filter.**
  - Recommended rejected (§4).
  - If the user wants it anyway, it needs its own spec. That spec would cover in-lane filtering across all three lanes, D30 parity, the coverage semantics, and a 50k latency re-measurement.
- **Q4 — YAML.** Deferred (§5). Confirm, or ask for the future spec.

## 9. Risks

- **Recall density.** Code-heavy recalls lose about one line at the 500-token default (§3.3).
- **Disk paths are as of the last index.** The freshness label is how that is disclosed; it is never a promise.
- **Duplicate repos/worktrees stay duplicated.** The fix makes them visible, which may prompt a request to dedupe; that is out of scope.
- **The golden file is touched only in step 3** (amended). Tool-surface headroom after step 3 is ~13 chars, so the next description change will need its own trim or a re-baseline argued in its spec.
- **`FileFreshness.Check` is on navigate's path.** Extending its result must not add I/O or change any verdict (AC1.8).
- **The ancestor probe's range bounds assume BINARY collation on `entity.path`** (true today). The AC2.7 equivalence test is what catches a future collation change.
