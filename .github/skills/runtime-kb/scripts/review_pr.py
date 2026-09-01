"""Standalone PR review tool — no Copilot CLI or MCP server required.

Fetches a PR's diff via `gh`, pulls relevant historical context from a local
runtime-kb database (built with sync_kb.py), assembles a review prompt, and
calls any OpenAI-compatible chat completions endpoint to generate a review.
Only `gh` (authenticated) and Python's standard library are required — no
`pip install` needed. This is intentionally independent of any specific AI
CLI/agent tooling so it can be shared and run by anyone with a `gh` login
and an API key for their LLM provider of choice.

Usage:
    # Assemble the prompt and print it without calling any LLM (safe, free, no key needed):
    python review_pr.py --pr 131077 --db compression-tar --dry-run

    # Generate a review with an OpenAI-compatible endpoint:
    set OPENAI_API_KEY=sk-...
    python review_pr.py --pr 131077 --db compression-tar --output review.md

    # Generate AND post as a (human-attributed, AI-disclosed) PR review comment:
    python review_pr.py --pr 131077 --db compression-tar --post

Environment variables:
    OPENAI_API_KEY   API key for the chat completions endpoint (required unless --dry-run)
    OPENAI_BASE_URL  Base URL, default https://api.openai.com/v1 (works with Azure OpenAI-
                     compatible gateways, local vLLM/Ollama-with-OpenAI-shim, etc.)
    OPENAI_MODEL     Model name, default gpt-4o-mini
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import urllib.error
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from kb_common import resolve_db_path  # noqa: E402

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

MAX_DIFF_CHARS = 60_000  # keep prompts within a reasonable context budget

AI_DISCLOSURE = (
    "\n\n> [!NOTE]\n> This review was generated with AI assistance "
    "(runtime-kb `review_pr.py`, historical context grounded in the local knowledge base)."
)


def run_gh(args: list[str]) -> str:
    result = subprocess.run(
        ["gh", *args], capture_output=True, text=True,
        encoding="utf-8", errors="replace", timeout=60,
    )
    if result.returncode != 0:
        raise SystemExit(f"gh {' '.join(args)} failed:\n{result.stderr}")
    return result.stdout


def fetch_pr(repo: str, number: int) -> dict:
    out = run_gh(["pr", "view", str(number), "--repo", repo,
                  "--json", "title,body,number,url,files,state,author"])
    return json.loads(out)


def fetch_diff(repo: str, number: int) -> str:
    diff = run_gh(["pr", "diff", str(number), "--repo", repo])
    if len(diff) > MAX_DIFF_CHARS:
        diff = diff[:MAX_DIFF_CHARS] + "\n\n... [diff truncated for length] ..."
    return diff


def kb_context(db_name: str, pr: dict, limit_per_file: int = 5) -> str:
    """Pull KB hits (search-by-file + title search) into plain text context."""
    db_path = resolve_db_path(db_name)
    if not db_path.exists():
        return f"(No knowledge base found at {db_path} — skipping historical context. " \
               f"Run sync_kb.py --db {db_name} first.)"

    import sqlite3
    conn = sqlite3.connect(db_path)
    conn.row_factory = sqlite3.Row

    sections = []
    seen_numbers = set()

    for f in pr.get("files", [])[:15]:  # cap file count to keep prompt bounded
        path = f.get("path", "")
        filename = Path(path).name
        rows = conn.execute(
            """SELECT DISTINCT items.number, items.title, items.state, items.is_pr, items.url
               FROM files JOIN items ON items.repo = files.repo AND items.number = files.item_number
               WHERE files.path LIKE ? ORDER BY items.updated_at DESC LIMIT ?""",
            (f"%{filename}%", limit_per_file),
        ).fetchall()
        for r in rows:
            if r["number"] in seen_numbers or r["number"] == pr["number"]:
                continue
            seen_numbers.add(r["number"])
            kind = "PR" if r["is_pr"] else "Issue"
            sections.append(f"- [{kind} #{r['number']}, {r['state']}] {r['title']} ({r['url']}) — touched {path}")

    title_words = " ".join(w for w in (pr.get("title") or "").split() if len(w) > 3)
    if title_words:
        rows = conn.execute(
            """SELECT items.* FROM items_fts JOIN items ON items.id = items_fts.item_id
               WHERE items_fts MATCH ? AND items.number != ? ORDER BY rank LIMIT 8""",
            (title_words, pr["number"]),
        ).fetchall()
        for r in rows:
            if r["number"] in seen_numbers:
                continue
            seen_numbers.add(r["number"])
            kind = "PR" if r["is_pr"] else "Issue"
            sections.append(f"- [{kind} #{r['number']}, {r['state']}] {r['title']} ({r['url']})")

    conn.close()
    if not sections:
        return "(No related historical items found in the knowledge base for these files/title.)"
    return "\n".join(sections)


def load_guidance(path: str | None) -> str:
    if not path:
        return ""
    p = Path(path)
    if not p.exists():
        print(f"[review] WARNING: guidance file not found: {p}", file=sys.stderr)
        return ""
    return p.read_text(encoding="utf-8")


def build_prompt(pr: dict, diff: str, history: str, guidance: str) -> str:
    parts = [
        f"You are reviewing PR #{pr['number']} in dotnet/runtime: \"{pr['title']}\"",
        f"URL: {pr['url']}\nAuthor: {pr.get('author', {}).get('login', 'unknown')}",
        "\n## PR description\n" + (pr.get("body") or "(none)"),
    ]
    if guidance:
        parts.append("\n## Domain review guidance to apply\n" + guidance)
    parts.append(
        "\n## Related historical issues/PRs found in the local knowledge base\n"
        "(cite these explicitly if they reveal prior design decisions, rejected approaches, "
        "or recurring bugs relevant to this diff)\n" + history
    )
    parts.append("\n## Diff to review\n```diff\n" + diff + "\n```")
    parts.append(
        "\n## Instructions\n"
        "Review this diff for correctness, API design/breaking changes, resource lifecycle, "
        "null safety, allocations/perf, thread safety, cross-platform behavior, and test coverage. "
        "For each finding: state severity (critical/major/minor), file:line, why it matters "
        "(cite a KB item number if it supports the point), and a suggested fix. "
        "If nothing of note applies from the KB, say so rather than forcing a citation. "
        "End with a one-paragraph overall recommendation."
    )
    return "\n".join(parts)


def call_llm(prompt: str, model: str, api_key: str, base_url: str) -> str:
    payload = json.dumps({
        "model": model,
        "messages": [
            {"role": "system", "content": "You are an expert .NET runtime code reviewer."},
            {"role": "user", "content": prompt},
        ],
        "temperature": 0.2,
    }).encode("utf-8")
    req = urllib.request.Request(
        f"{base_url.rstrip('/')}/chat/completions",
        data=payload,
        headers={"Authorization": f"Bearer {api_key}", "Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=120) as resp:
            data = json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        raise SystemExit(f"LLM call failed: {e.code} {e.reason}\n{e.read().decode('utf-8', 'replace')}")
    return data["choices"][0]["message"]["content"]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--pr", type=int, required=True)
    ap.add_argument("--repo", default="dotnet/runtime")
    ap.add_argument("--db", required=True, help="runtime-kb database name for historical context")
    ap.add_argument("--guidance-file", help="Optional path to a domain instructions .md to include verbatim")
    ap.add_argument("--model", default=os.environ.get("OPENAI_MODEL", "gpt-4o-mini"))
    ap.add_argument("--output", help="Write review markdown here instead of stdout")
    ap.add_argument("--dry-run", action="store_true", help="Assemble and print the prompt only; no LLM call")
    ap.add_argument("--post", action="store_true", help="Post the generated review as a PR comment via gh")
    args = ap.parse_args()

    pr = fetch_pr(args.repo, args.pr)
    diff = fetch_diff(args.repo, args.pr)
    history = kb_context(args.db, pr)
    guidance = load_guidance(args.guidance_file)
    prompt = build_prompt(pr, diff, history, guidance)

    if args.dry_run:
        print(prompt)
        print(f"\n\n[review] (dry run) prompt length: {len(prompt)} chars. No LLM call made.", file=sys.stderr)
        return

    api_key = os.environ.get("OPENAI_API_KEY")
    if not api_key:
        raise SystemExit("error: OPENAI_API_KEY is not set. Use --dry-run to preview the prompt without a key.")
    base_url = os.environ.get("OPENAI_BASE_URL", "https://api.openai.com/v1")

    review = call_llm(prompt, args.model, api_key, base_url)
    review_with_disclosure = review + AI_DISCLOSURE

    if args.output:
        Path(args.output).write_text(review_with_disclosure, encoding="utf-8")
        print(f"[review] Written to {args.output}")
    else:
        print(review_with_disclosure)

    if args.post:
        tmp = Path(args.output) if args.output else Path("._review_tmp.md")
        if not args.output:
            tmp.write_text(review_with_disclosure, encoding="utf-8")
        run_gh(["pr", "review", str(args.pr), "--repo", args.repo, "--comment", "--body-file", str(tmp)])
        if not args.output:
            tmp.unlink(missing_ok=True)
        print(f"[review] Posted as a comment on PR #{args.pr}")


if __name__ == "__main__":
    main()
