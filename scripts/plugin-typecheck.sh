#!/usr/bin/env bash
# The plugin's type-check gate. Run from the repo root:  scripts/plugin-typecheck.sh
#
# It checks the mods against the types of the engine that is running it, or stops:
#   exit 0  validate and tsc both passed
#   exit 1  validate or tsc failed (a code problem)
#   exit 2  the engine's types could not be found (an environment problem; tsc was NOT run)
#
# Type sources, in order, for the version `claude --version` reports:
#   1. plugin/.claude-plugin/types/ when its .engine-version stamp names that version
#   2. the engine's own lay-down (sandboxed `claude -p /help --plugin-dir plugin`)
#   3. the claude-code.d.ts the engine writes under its bundled-skills directory for that version,
#      which exists only after a process of that version has loaded the plugin-authoring skill
# plugin/.claude-plugin/types/ is git-ignored; no .d.ts is committed, because a copy goes stale on
# every engine update and then checks the mods against an API the running engine no longer has.
#
# Test seams (the tests set these; nothing else should):
#   PLUGIN_TYPECHECK_PLUGIN_DIR  plugin directory to check (default: plugin)
#   PLUGIN_TYPECHECK_VERSION     version to look types up for (default: claude --version)

set -u

plugin=${PLUGIN_TYPECHECK_PLUGIN_DIR:-plugin}
types="$plugin/.claude-plugin/types"
stamp="$types/.engine-version"
typescript=typescript@5.6.3

say() { printf 'plugin-typecheck: %s\n' "$*"; }

cannot_find_types() {
  {
    printf 'plugin-typecheck: cannot type-check — no engine types for Claude Code %s. tsc was not run.\n' "$version"
    printf '  tried: reuse of %s (stamp %s)\n' "$types" "$(cat "$stamp" 2>/dev/null || echo absent)"
    printf '         the engine lay-down (claude -p /help --plugin-dir %s)\n' "$plugin"
    printf '         the engine'"'"'s bundled claude-code.d.ts under %s\n' "${bundle_roots[*]}"
    printf '  the bundled file exists once a process of this exact version has loaded the plugin-authoring skill;\n'
    printf '  run `claude -p /plugin-authoring --max-turns 1` once (it makes a model call) and re-run this script.\n'
    printf '  Recipe: spec engram-mods/10-shared-mod-client.md, section "Test plan", type-check gate.\n'
  } >&2
  exit 2
}

if ! command -v claude >/dev/null 2>&1; then
  version=${PLUGIN_TYPECHECK_VERSION:-unknown}
  bundle_roots=()
  printf 'plugin-typecheck: `claude` is not on PATH, so there is no engine to take types from.\n' >&2
  cannot_find_types
fi

version=${PLUGIN_TYPECHECK_VERSION:-$(claude --version 2>/dev/null | awk 'NR==1 {print $1}')}
[ -n "$version" ] || version=unknown

uid=$(id -u)
bundle_roots=("/private/tmp/claude-$uid/bundled-skills" "/tmp/claude-$uid/bundled-skills")
[ -n "${TMPDIR:-}" ] && bundle_roots+=("${TMPDIR%/}/claude-$uid/bundled-skills")

have_types() { [ -f "$types/claude-code/index.d.ts" ] && [ -f "$types/tsconfig.json" ]; }
stamped() { [ "$(cat "$stamp" 2>/dev/null | awk 'NR==1 {print $1}')" = "$version" ]; }

if have_types && stamped; then
  say "types: reused from $types ($(cat "$stamp"))"
else
  # Files of another version's types must not satisfy the checks below.
  rm -f "$types/claude-code/index.d.ts" "$types/tsconfig.json" "$stamp"

  source=
  home=$(mktemp -d) || { say "mktemp failed"; exit 2; }
  ENGRAM_HOME=$home ENGRAM_BIN=/usr/bin/true claude -p /help --plugin-dir "$plugin" </dev/null >/dev/null 2>&1
  if have_types; then
    source="engine lay-down"
  else
    # Newest first: every candidate for one version has the same content, so which one is arbitrary.
    bundled=
    for root in "${bundle_roots[@]}"; do
      for candidate in $(ls -t "$root/$version"/*/plugin-authoring/types/claude-code.d.ts 2>/dev/null); do
        bundled=$candidate
        break 2
      done
    done
    [ -n "$bundled" ] || cannot_find_types

    mkdir -p "$types/claude-code" || cannot_find_types
    cp "$bundled" "$types/claude-code/index.d.ts" || cannot_find_types
    cat >"$types/tsconfig.json" <<'JSON'
{
  "compilerOptions": {
    "target": "es2023",
    "lib": ["es2023"],
    "module": "esnext",
    "moduleResolution": "bundler",
    "strict": true,
    "noUncheckedIndexedAccess": true,
    "noEmit": true,
    "skipLibCheck": true,
    "jsx": "react",
    "jsxFactory": "h",
    "jsxFragmentFactory": "Fragment",
    "typeRoots": ["."],
    "types": ["claude-code"]
  }
}
JSON
    source="engine bundled file $bundled"
  fi

  have_types || cannot_find_types
  printf '%s (%s)\n' "$version" "$source" >"$stamp"
  say "types: $source"
fi

say "engine $version"

validate=$(claude plugin validate "$plugin" 2>&1)
printf '%s\n' "$validate"
# validate exits 0 on failure, so its output is the only signal.
if printf '%s\n' "$validate" | grep -q -E '✘|Validation failed'; then
  say "validate failed"
  exit 1
fi

say "tsc ($typescript)"
npx --yes -p "$typescript" tsc -p "$plugin" || { say "tsc failed"; exit 1; }
say "ok"
