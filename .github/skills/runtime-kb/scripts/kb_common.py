"""Shared helpers for the runtime-kb skill: DB schema, gh CLI wrapper, path resolution.

Do NOT create ad-hoc replacements for these helpers — import from here.
"""
from __future__ import annotations

import json
import sqlite3
import subprocess
import sys
import time
from pathlib import Path

SKILL_DIR = Path(__file__).resolve().parent.parent
KBS_DIR = SKILL_DIR / "kbs"

MS_ASSOCIATIONS = {"MEMBER", "OWNER", "COLLABORATOR"}

SCHEMA = """
CREATE TABLE IF NOT EXISTS items (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    repo                TEXT NOT NULL,
    number              INTEGER NOT NULL,
    is_pr               INTEGER NOT NULL,
    title               TEXT,
    state               TEXT,
    merged              INTEGER,
    author              TEXT,
    author_association  TEXT,
    is_ms               INTEGER,
    created_at          TEXT,
    updated_at          TEXT,
    closed_at           TEXT,
    labels              TEXT,
    body                TEXT,
    url                 TEXT,
    UNIQUE(repo, number)
);

CREATE TABLE IF NOT EXISTS comments (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    repo                TEXT NOT NULL,
    item_number         INTEGER NOT NULL,
    kind                TEXT NOT NULL,  -- issue_comment | review | review_comment
    author              TEXT,
    author_association  TEXT,
    is_ms               INTEGER,
    created_at          TEXT,
    body                TEXT,
    path                TEXT,           -- file path, for review_comment only
    url                 TEXT
);
CREATE INDEX IF NOT EXISTS idx_comments_item ON comments(repo, item_number);

CREATE TABLE IF NOT EXISTS files (
    repo         TEXT NOT NULL,
    item_number  INTEGER NOT NULL,
    path         TEXT NOT NULL,
    UNIQUE(repo, item_number, path)
);
CREATE INDEX IF NOT EXISTS idx_files_path ON files(path);

CREATE TABLE IF NOT EXISTS sync_meta (
    kb_key          TEXT PRIMARY KEY,   -- repo|sorted,labels
    repo            TEXT,
    labels          TEXT,
    last_synced_at  TEXT
);

-- Standalone (non-external-content) FTS5 tables: each row carries its own
-- item_id/comment_id reference column so results join back to the source
-- table explicitly. (External-content FTS5 tables trigger a delete-time
-- corruption bug on some SQLite builds — standalone tables avoid it.)
CREATE VIRTUAL TABLE IF NOT EXISTS items_fts USING fts5(
    item_id UNINDEXED, title, body
);
CREATE VIRTUAL TABLE IF NOT EXISTS comments_fts USING fts5(
    comment_id UNINDEXED, body
);
"""


def resolve_db_path(name: str) -> Path:
    """Resolve a short KB name (e.g. "compression-tar") to its db file path.

    Also accepts an absolute/relative path ending in .db directly.
    """
    if name.endswith(".db"):
        return Path(name).resolve()
    KBS_DIR.mkdir(parents=True, exist_ok=True)
    return KBS_DIR / f"{name}.db"


def open_db(name: str) -> sqlite3.Connection:
    path = resolve_db_path(name)
    is_new = not path.exists()
    conn = sqlite3.connect(path)
    conn.row_factory = sqlite3.Row
    conn.executescript(SCHEMA)
    conn.commit()
    if is_new:
        print(f"[kb] Created new knowledge base: {path}", file=sys.stderr)
    return conn


def is_ms(author_association: str | None) -> int:
    return 1 if (author_association or "").upper() in MS_ASSOCIATIONS else 0


def gh_jsonl(args: list[str], retries: int = 3) -> list[dict]:
    """Run `gh api --paginate --jq '<query>[]'`-style args and parse newline-delimited JSON.

    `args` must already include --jq producing one JSON object per line.
    Retries on transient failures (secondary rate limits, network blips).
    """
    last_err = None
    for attempt in range(retries):
        try:
            result = subprocess.run(
                ["gh", "api", "--paginate", *args],
                capture_output=True, text=True, timeout=120, check=True,
                encoding="utf-8", errors="replace",
            )
            items = []
            for line in result.stdout.splitlines():
                line = line.strip()
                if line:
                    items.append(json.loads(line))
            return items
        except subprocess.CalledProcessError as e:
            last_err = e
            stderr = (e.stderr or "").lower()
            if "rate limit" in stderr or "abuse" in stderr:
                time.sleep(5 * (attempt + 1))
                continue
            if "404" in stderr or "not found" in stderr:
                return []
            time.sleep(2)
    print(f"[kb] WARNING: gh api call failed after retries: {args} :: {last_err}", file=sys.stderr)
    return []


def upsert_fts(conn: sqlite3.Connection, table: str, key_col: str, key_val: int, **fields):
    conn.execute(f"DELETE FROM {table} WHERE {key_col} = ?", (key_val,))
    cols = ", ".join([key_col, *fields.keys()])
    placeholders = ", ".join(["?"] * (len(fields) + 1))
    conn.execute(f"INSERT INTO {table}({cols}) VALUES ({placeholders})", (key_val, *fields.values()))
