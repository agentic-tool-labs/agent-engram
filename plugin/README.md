# Engram for Claude Code

This plugin connects Claude Code to Engram, a local temporal memory store. It adds:

- an MCP server entry, `engram`, at `http://127.0.0.1:7433/`;
- hooks that prime each session and subagent with what memory holds, guard writes to Claude Code's
  file-based memory, and queue edited files for re-indexing;
- ten `/engram:` slash commands (`status`, `start`, `stop`, `restart`, `doctor`, `recall`,
  `remember`, `forget`, `digest`, `statusline`);
- mods: panes, toasts and a status band that show what memory is doing.

Memory lives in a SQLite store under `~/.engram`, and the server listens on `127.0.0.1` only.

## What it needs

- **The `engram` binary.** The plugin ships none. Its hooks look for `$ENGRAM_BIN`, then
  `$HOME/.local/bin/engram`, then `engram` on `PATH`.
- **An initialised home.** Until `engram init` has run, every `engram hook` command does nothing.
- **A running server.** Session start runs `engram start`, which is idempotent; `/engram:start`
  starts it by hand.

## Documentation

Everything else (installing, the hooks and mods in detail, configuration and limits) is in the
repository's top-level README: [`../README.md`](../README.md), in particular the sections
"The Claude Code plugin" and "Claude Code mods".
