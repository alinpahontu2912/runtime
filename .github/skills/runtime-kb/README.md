# Runtime KB

A shareable Copilot CLI skill that builds local, per-area knowledge bases from
GitHub issue/PR/review history for any label in a repo (defaults to
`dotnet/runtime`), and scaffolds a history-aware code review agent on top of
one. No hosted MCP server, no repo admin rights, no GitHub Action permissions
required — everything runs through the `gh` CLI and Python's standard library.

## What it does

- Syncs issues, PRs, comments, reviews, and changed-file lists for one or more
  area labels into a local SQLite database with full-text search.
- Supports **multiple independent knowledge bases at once** — one file per
  area (e.g. `compression-tar.db`, `system-net.db`).
- Generates a `@<name>-reviewer` custom agent wired to a specific KB, so
  reviews are grounded in prior design decisions and known recurring issues
  instead of a cold read of the diff.
- Also ships a standalone review script that works without Copilot CLI at
  all — just `gh` + Python + an LLM API key.

## Prerequisites

1. **`gh` CLI, authenticated**: `gh auth status` should show you logged in.
2. **Python 3.9+** — no `pip install` needed, everything uses the standard
   library only.
3. (Only for the standalone reviewer's live LLM call) an API key for any
   OpenAI-compatible chat completions endpoint.

## One-time setup

```bash
cd .github/skills/runtime-kb
```

All commands below assume you're in this directory. Every knowledge base you
build is stored under `kbs/<name>.db` (gitignored — it's generated data, not
source, so it's safe to delete and rebuild at any time).

## Commands

### `scripts/sync_kb.py` — build or refresh a knowledge base

| Command | What it does |
|---|---|
| `python scripts/sync_kb.py --repo dotnet/runtime --label area-System.IO.Compression --label area-System.Formats.Tar --db compression-tar` | First-time build: fetches **all** matching issues/PRs (labels are OR'd, not AND'd) plus their comments, reviews, review comments, and changed files, into `kbs/compression-tar.db`. |
| `python scripts/sync_kb.py --db compression-tar` | Incremental refresh: re-reads the repo/labels remembered from the first sync and only fetches items updated since the last sync (with a 1-day overlap buffer). Use this any time the KB feels stale. |
| `python scripts/sync_kb.py --db compression-tar --full` | Ignores the incremental bookkeeping and re-fetches full history from scratch. |
| `python scripts/sync_kb.py --db some-area --label area-Foo --max-items 20 --no-comments` | Fast dry run for testing a new area before committing to a full sync — caps item count and skips comment/review fetching. |

### `scripts/query_kb.py` — query a knowledge base

All subcommands take `--db <name>` **before** the subcommand name.

| Command | What it does |
|---|---|
| `python scripts/query_kb.py --db compression-tar stats` | Prints KB overview: date range, issue/PR counts (open/closed), comment counts, top contributors (with `[MS]` tag for Microsoft-affiliated authors), and last-synced timestamp. |
| `python scripts/query_kb.py --db compression-tar search "brotli decoder state" --limit 10` | Full-text search across issue/PR titles and bodies. Filter with `--type issue\|pr\|all`, `--state open\|closed\|all`, `--ms-only`. |
| `python scripts/query_kb.py --db compression-tar search-comments "symlink traversal" --ms-only` | Full-text search across comments, reviews, and review comments — useful for finding *why* a decision was made, not just *what* changed. |
| `python scripts/query_kb.py --db compression-tar recent --limit 15` | Lists the most recently updated items. Filter with `--type`/`--state`. |
| `python scripts/query_kb.py --db compression-tar thread 130342` | Prints a full issue/PR: description plus every comment/review/review-comment in chronological order — the "read the whole history" view. |
| `python scripts/query_kb.py --db compression-tar related 130342` | Finds other items with similar titles (e.g. earlier attempts at the same fix). |
| `python scripts/query_kb.py --db compression-tar search-by-file "TarReader.cs"` | Finds every PR that touched a given file — the fastest way to answer "has this file caused problems before?" |

### `scripts/generate_agent.py` — scaffold a review or design-advisor agent

| Command | What it does |
|---|---|
| `python scripts/generate_agent.py --name compression-tar --db compression-tar --display-name "IO.Compression & Formats.Tar" --scope "src/libraries/System.IO.Compression*, src/libraries/System.Formats.Tar*" --description "Reviews changes to System.IO.Compression and System.Formats.Tar, grounded in historical KB context."` | Writes `.github/agents/compression-tar-reviewer.agent.md`, wired to query the `compression-tar` KB before reviewing a diff/PR. Invoke it in the CLI as `@compression-tar-reviewer`. Add `--force` to regenerate after editing the template. |
| Same command + `--kind design-advisor` | Writes `.github/agents/compression-tar-design-advisor.agent.md` instead — a **different kind of agent** for making design decisions *before* code exists (API shape, defaults, architecture trade-offs). Mines the KB broadly for precedent (prior attempts, maintainer opinions, open related requests) and ends with an explicit recommendation plus its strongest counter-argument. Invoke as `@compression-tar-design-advisor`. |

### `scripts/review_pr.py` — standalone reviewer (no Copilot CLI needed)

| Command | What it does |
|---|---|
| `python scripts/review_pr.py --pr 131077 --db compression-tar --dry-run` | Assembles the full review prompt (PR diff + KB history for the changed files/title + optional guidance doc) and prints it — no API key needed, no cost, good for sanity-checking what context the reviewer would see. |
| `python scripts/review_pr.py --pr 131077 --db compression-tar --output review.md` | Same, but actually calls an LLM (`OPENAI_API_KEY` env var required; `OPENAI_BASE_URL`/`OPENAI_MODEL` optional overrides for Azure OpenAI, local Ollama-with-OpenAI-shim, etc.) and writes the review to a file. |
| `python scripts/review_pr.py --pr 131077 --db compression-tar --guidance-file ../../instructions/compression.instructions.md --output review.md` | Same, plus includes a domain instructions file verbatim in the prompt. |
| `python scripts/review_pr.py --pr 131077 --db compression-tar --post` | Generates the review and posts it as a PR comment via `gh pr review --comment`, automatically appending an AI-generated-content disclosure note. |

## Typical end-to-end workflow

```bash
cd .github/skills/runtime-kb

# 1. Build the KB once
python scripts/sync_kb.py --repo dotnet/runtime \
  --label area-System.IO.Compression --label area-System.Formats.Tar \
  --db compression-tar

# 2. Generate the review agent
python scripts/generate_agent.py --name compression-tar --db compression-tar \
  --display-name "IO.Compression & Formats.Tar" \
  --scope "src/libraries/System.IO.Compression*, src/libraries/System.Formats.Tar*" \
  --description "Reviews changes to System.IO.Compression and System.Formats.Tar, grounded in historical KB context."

# 3a. Review from inside Copilot CLI
#     @compression-tar-reviewer review PR #12345

# 3b. ...or review standalone, no Copilot CLI required
python scripts/review_pr.py --pr 12345 --db compression-tar --dry-run

# 4. Keep the KB current (run any time; cheap incremental refresh)
python scripts/sync_kb.py --db compression-tar
```

## Adding a second (or third) area

Nothing above is compression/tar-specific — repeat step 1–2 with a different
`--db` name and different `--label`(s) to get an independent KB and agent for
any other area, e.g.:

```bash
python scripts/sync_kb.py --repo dotnet/runtime --label area-System.Net.Http --db system-net-http
python scripts/generate_agent.py --name system-net-http --db system-net-http \
  --display-name "System.Net.Http" --scope "src/libraries/System.Net.Http*" \
  --description "Reviews changes to System.Net.Http, grounded in historical KB context."
```

All KBs coexist independently under `kbs/`; nothing needs to be torn down to
add another one.

## Notes and limitations

- Labels within one KB are combined with **OR**, not AND.
- GitHub's search API caps results at 1000 per query — for areas bigger than
  that, rely on incremental syncs rather than one giant `--full` run.
- `[MS]`/`--ms-only` is a heuristic based on GitHub's `author_association`
  (MEMBER/OWNER/COLLABORATOR) at comment time — treat it as "likely
  maintainer-endorsed", not a verified employee roster.
- Do not add ad-hoc scripts to `scripts/` for one-off queries — use
  `python -c '...'` against the `kbs/*.db` file directly, or add a proper
  subcommand to `query_kb.py` if it's reusable.
