---
name: runtime-kb
description: >
  Build and query local, per-area knowledge bases of GitHub issues, PRs, comments, and
  reviews for any label/area in a GitHub repo (defaults to dotnet/runtime), and scaffold
  a domain-specific code review agent grounded in that history. Use when asked to build a
  knowledge base for an area, refresh/update a stale knowledge base, create a
  history-aware review agent for a component, or when a review agent's KB is out of date.
  Supports maintaining multiple independent KBs at once (one per area).
---

# Runtime Knowledge Base

## Overview

This skill turns GitHub history for any area of a repo into a local, queryable
SQLite database, and can scaffold a custom review agent that consults that
database before reviewing changes. It generalizes the pattern used by
domain-specific review agents (e.g. `extensions-reviewer`) so any contributor
can build one for their own area without needing a hosted MCP server.

**Deterministic steps are scripted. Agent does the review reasoning.**

- **Python scripts** (`scripts/`) handle all deterministic work: fetching
  issues/PRs/comments/reviews via `gh api`, storing them in SQLite with FTS5
  full-text search, and generating a new review agent file from a template.
- **Agent** handles all non-deterministic work: deciding what to search for,
  interpreting KB results, and writing the actual review.

Everything runs through the `gh` CLI (already authenticated in this
environment) and Python's standard library (`sqlite3`) — no extra
dependencies, no server to host, no repo admin rights required. Share this
skill by copying the `runtime-kb` folder; the only prerequisites are Python 3
and an authenticated `gh` CLI.

## Multiple KBs

Every KB is a separate file under `kbs/<name>.db` (gitignored — these are
generated data, not source). You can maintain as many as you like at once,
e.g. `compression-tar.db`, `system-net.db`, `jit.db` — just use a different
`--db <name>` for each. A KB remembers which repo/labels it was built from
(`sync_meta` table), so re-syncing later only needs `--db <name>`.

## Commands

| Script | Purpose |
|--------|---------|
| `scripts/sync_kb.py` | Create or incrementally refresh a KB from one or more area labels |
| `scripts/query_kb.py` | Query a KB: `stats`, `search`, `search-comments`, `recent`, `thread`, `related`, `search-by-file` |
| `scripts/generate_agent.py` | Scaffold a `<name>-reviewer.agent.md` (diff review) or `<name>-design-advisor.agent.md` (pre-code design decisions) wired to a specific KB |
| `scripts/review_pr.py` | Standalone reviewer: fetch a PR diff + KB context, call any OpenAI-compatible LLM, optionally post the review — works without Copilot CLI or any agent runtime |

### 1. Build (or refresh) a knowledge base

```bash
cd .github/skills/runtime-kb

# First sync (full history) — labels are OR'd together, not AND'd
python scripts/sync_kb.py --repo dotnet/runtime \
  --label area-System.IO.Compression --label area-System.Formats.Tar \
  --db compression-tar

# Later: cheap incremental refresh (remembers repo/labels automatically)
python scripts/sync_kb.py --db compression-tar

# Force a full re-sync ignoring bookkeeping
python scripts/sync_kb.py --db compression-tar --full

# Fast dry run while testing (metadata only, no comments/reviews/files)
python scripts/sync_kb.py --db some-area --label area-Foo --max-items 20 --no-comments
```

A full sync of a small-to-medium area (few hundred issues/PRs) takes a few
minutes because comments/reviews/files are fetched per item. Use
`--max-items`/`--no-comments` to validate quickly before committing to a full
run.

### 2. Query it

```bash
python scripts/query_kb.py stats --db compression-tar
python scripts/query_kb.py search "brotli decoder state machine" --db compression-tar --limit 10
python scripts/query_kb.py search-comments "symlink traversal" --db compression-tar --ms-only
python scripts/query_kb.py recent --db compression-tar --type pr --limit 15
python scripts/query_kb.py thread 12345 --db compression-tar
python scripts/query_kb.py related 12345 --db compression-tar
python scripts/query_kb.py search-by-file "TarReader" --db compression-tar
```

`--ms-only` filters to Microsoft-affiliated authors (based on GitHub's
`author_association`: MEMBER/OWNER/COLLABORATOR) — a proxy for
maintainer-endorsed context, useful for surfacing design decisions rather
than community discussion noise.

### 3. Generate a review agent for the area

```bash
python scripts/generate_agent.py \
  --name compression-tar \
  --db compression-tar \
  --display-name "IO.Compression & Formats.Tar" \
  --scope "src/libraries/System.IO.Compression*, src/libraries/System.Formats.Tar*" \
  --description "Reviews changes to System.IO.Compression and System.Formats.Tar, grounded in historical KB context."
```

This writes `.github/agents/compression-tar-reviewer.agent.md`. Invoke it
with `@compression-tar-reviewer` on any diff/PR — it will query
`query_kb.py` for relevant history before writing its review. Re-run with
`--force` to regenerate after editing the template.

### 4. Generate a design-advisor agent instead

Use `--kind design-advisor` for a different agent that helps make **design
decisions before code exists** (API shape, behavior/default choices,
architecture trade-offs) rather than reviewing an already-written diff:

```bash
python scripts/generate_agent.py \
  --name compression-tar --db compression-tar --kind design-advisor \
  --display-name "IO.Compression & Formats.Tar" \
  --scope "src/libraries/System.IO.Compression*, src/libraries/System.Formats.Tar*" \
  --description "Helps make design decisions for System.IO.Compression and System.Formats.Tar, grounded in prior proposals, rejected approaches, and maintainer opinions in the KB."
```

Writes `.github/agents/compression-tar-design-advisor.agent.md`, invoked as
`@compression-tar-design-advisor`. It mines the KB broadly for precedent
(prior attempts, maintainer opinions, open related requests, naming/breaking-
change/perf constraints) and ends with an explicit recommendation and its
strongest counter-argument — rather than a diff-by-diff review.

## End-to-end workflow

```bash
cd .github/skills/runtime-kb
python scripts/sync_kb.py --repo dotnet/runtime --label area-System.IO.Compression --label area-System.Formats.Tar --db compression-tar
python scripts/generate_agent.py --name compression-tar --db compression-tar \
  --display-name "IO.Compression & Formats.Tar" \
  --scope "src/libraries/System.IO.Compression*, src/libraries/System.Formats.Tar*" \
  --description "Reviews changes to System.IO.Compression and System.Formats.Tar, grounded in historical KB context."
# Then, in the CLI: @compression-tar-reviewer review the current diff
```

## Standalone review tool (no Copilot CLI / no agent runtime required)

`scripts/review_pr.py` is a fully standalone alternative to the `@<name>-reviewer`
agent — useful for sharing with anyone who has `gh` + Python but not this CLI, or
for wiring into a plain shell/cron job. It needs no `pip install`, no MCP server,
and no repo admin rights; it only shells out to `gh` and calls any
OpenAI-compatible chat completions endpoint directly.

```bash
# Preview the assembled prompt (diff + KB history + guidance) with no API key and no cost:
python scripts/review_pr.py --pr 131077 --db compression-tar \
  --guidance-file ../../instructions/compression.instructions.md --dry-run

# Generate a real review (needs OPENAI_API_KEY; OPENAI_BASE_URL/OPENAI_MODEL optional overrides
# for Azure OpenAI, local Ollama-with-OpenAI-shim, etc.):
set OPENAI_API_KEY=sk-...
python scripts/review_pr.py --pr 131077 --db compression-tar --output review.md

# Generate AND post as a PR comment (adds an AI-disclosure note automatically):
python scripts/review_pr.py --pr 131077 --db compression-tar --post
```

It pulls historical context the same way the generated agent does (search-by-file
+ title search against the KB), so both surfaces share one source of truth.

## Notes and limitations

- Labels are combined with **OR** (union), not AND — an item matching any one
  of the given labels is included. This is what you want for grouping related
  areas into one KB.
- GitHub's search API caps results at 1000 per query; if an area has more
  open+closed items than that, narrow with `--since`/incremental syncs rather
  than one giant full sync.
- `is_ms` is a heuristic (`author_association` at comment time), not a
  verified employee roster — treat it as "likely maintainer-endorsed", not
  gospel.
- Do NOT create new files in `scripts/` for one-off queries — use
  `python -c '...'` against the `kbs/*.db` file directly, or add a proper
  subcommand to `query_kb.py` if it's reusable.
