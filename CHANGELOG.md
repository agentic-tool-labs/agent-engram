# Changelog

## 1.4.0 — 2026-11-21

Everything merged since 1.3.18 (PRs #8–#28).

### Upgrade notes

- **Schema v19 → v20.** The first open migrates the store, and takes a full `pre-v20` snapshot
  first. The migration adds the indexes that compact's foreign-key deletes need (#15).
- Restart the server after installing (`engram stop`, then `engram start`), so it runs the new
  binary.
- `engram doctor` now warns about superseded registry paths that still hold indexed files. To
  clear them, run `engram compact --retired`, check the dry run, then add `--apply`. To remove them
  automatically, set `[indexing] auto_remove_retired = true` (#16, #21).

### Memory

- Each agent role has its own memory that persists across sessions. `scope:"agent"` writes it, and
  the subagent primer delivers that role's five newest notes (#22).
- `engram_remember` with `scope:"project"` files a note under the project its `project_dir`
  names. When git cannot name the repo, the note is saved as a session note and the reply says
  why (#20, #25).
- The `capture` mod captures prompts on `prompt.submit` (#9).
- `engram reorganize` moves the ah2 ledger out of user memory, dry run first (#12).

### Performance

- Primer hooks at real store size: on a 621,904-fact store, `subagent-start` went from 516 ms to
  176 ms and `session-start` from 715 ms to 208 ms (#10).
- Recall is faster, and gains a `types` filter. Tool ledgers no longer show up in recall (#11).
- A file's facts are read through `ix_fact_path`: a full index of one repo went from 545 s to
  8.9 s (#17).
- `compact --apply` finishes. Before this, the foreign-key deletes scanned the whole fact table for
  every row (#15).
- Background work (embedding, maintenance) yields to the model's requests, for at most 10 s per
  batch (#8).

### Backups and recovery

- Scheduled snapshots leave out vectors, and the journal holds authored facts only. On the real
  store a snapshot went from 2.4 GB to 830 MB (#13). Closed code history is left out too (#19).
- `backup replay` keeps a forgotten fact's retraction (#18). It pairs each journal record with at
  most one row, and syncs forget reasons (#26).

### Fixes

- `repair` no longer reports phantom missing lexical-index rows. Before this fix, every
  `repair --apply` rebuilt the whole index (#14).
- A git failure no longer creates a new registry path, and old paths are retired (#16).
- `compact --retired` removes indexed state under retired paths, with automatic removal available
  as an opt-in (#21).
- The lexical, overlap and vector lanes take their eligibility rule from one definition (#24).

### Tests

- Four end-to-end tests that failed under load now measure the thing they guard (#23).
- Fixed flakes in the replay seed baseline and in test sink port binding (#27, #28).
