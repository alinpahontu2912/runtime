"""Scaffold a new custom agent (reviewer or design-advisor) wired to a specific
runtime-kb database.

Usage:
    # Diff/PR reviewer — applies after code is written:
    python generate_agent.py --name compression-tar --db compression-tar \\
        --display-name "IO.Compression & Formats.Tar" \\
        --scope "src/libraries/System.IO.Compression*, src/libraries/System.Formats.Tar*" \\
        --description "Reviews changes to System.IO.Compression and System.Formats.Tar using historical KB context."

    # Design advisor — helps decide on API shape/behavior BEFORE code is written:
    python generate_agent.py --name compression-tar --db compression-tar --kind design-advisor \\
        --display-name "IO.Compression & Formats.Tar" \\
        --scope "src/libraries/System.IO.Compression*, src/libraries/System.Formats.Tar*" \\
        --description "Helps make design decisions for System.IO.Compression and System.Formats.Tar, grounded in historical KB context."

Writes .github/agents/<name>-<kind-suffix>.agent.md by default (use --output to override).
Refuses to overwrite an existing file unless --force is passed.
"""
from __future__ import annotations

import argparse
from pathlib import Path

SKILL_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = SKILL_DIR.parent.parent.parent  # .github/skills/runtime-kb -> repo root
DEFAULT_AGENTS_DIR = REPO_ROOT / ".github" / "agents"

TEMPLATES = {
    "reviewer": SKILL_DIR / "templates" / "reviewer.agent.md.tmpl",
    "design-advisor": SKILL_DIR / "templates" / "design-advisor.agent.md.tmpl",
}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--name", required=True, help="Short kebab-case agent name, e.g. compression-tar")
    ap.add_argument("--db", required=True, help="KB db name this agent will query (from sync_kb.py --db)")
    ap.add_argument("--kind", choices=sorted(TEMPLATES), default="reviewer",
                    help="reviewer = reviews diffs/PRs after code is written; design-advisor = helps decide API/behavior before code is written")
    ap.add_argument("--display-name", required=True, help="Human-readable area name, e.g. 'IO.Compression & Formats.Tar'")
    ap.add_argument("--scope", required=True, help="Comma-separated path globs this agent covers")
    ap.add_argument("--description", required=True, help="One-line agent description (used in front matter)")
    ap.add_argument("--output", help="Output path; defaults to .github/agents/<name>-<kind-suffix>.agent.md")
    ap.add_argument("--force", action="store_true", help="Overwrite if the output file already exists")
    args = ap.parse_args()

    agent_name = f"{args.name}-{args.kind}"
    output = Path(args.output) if args.output else DEFAULT_AGENTS_DIR / f"{agent_name}.agent.md"
    if output.exists() and not args.force:
        raise SystemExit(f"error: {output} already exists. Pass --force to overwrite.")

    text = TEMPLATES[args.kind].read_text(encoding="utf-8")
    text = (text
            .replace("{{NAME}}", args.name)
            .replace("{{DISPLAY_NAME}}", args.display_name)
            .replace("{{DB_NAME}}", args.db)
            .replace("{{SCOPE_PATHS}}", args.scope)
            .replace("{{DESCRIPTION}}", args.description))

    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(text, encoding="utf-8")
    print(f"[kb] Generated {args.kind} agent: {output}")
    print(f"[kb] Invoke it with: @{agent_name}")


if __name__ == "__main__":
    main()
