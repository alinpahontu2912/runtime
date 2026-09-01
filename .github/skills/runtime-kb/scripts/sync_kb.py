"""Sync GitHub issues/PRs/comments/reviews for one or more area labels into a local
knowledge-base SQLite db. Supports multiple independent KBs (one db file per --db name).

Usage:
    python sync_kb.py --repo dotnet/runtime --label area-System.IO.Compression \\
        --label area-System.Formats.Tar --db compression-tar

    # Re-run any time to pick up new/updated items incrementally:
    python sync_kb.py --db compression-tar

Incremental sync: the labels/repo used to build a KB are remembered in sync_meta,
so subsequent runs only need --db (they replay the same repo/labels) unless you
pass --repo/--label explicitly to override.
"""
from __future__ import annotations

import argparse
import sys
import time
from datetime import datetime, timedelta, timezone

from kb_common import gh_jsonl, is_ms, open_db, upsert_fts

OVERLAP = timedelta(days=1)  # re-fetch a small overlap window to catch late edits


def fetch_search_items(repo: str, label: str, since: str | None, max_items: int | None) -> list[dict]:
    q = f"repo:{repo}+label:{quote_label(label)}"
    if since:
        q += f"+updated:>={since}"
    args = [f"search/issues?q={q}&per_page=100", "--jq", ".items[]"]
    items = gh_jsonl(args)
    if max_items:
        items = items[:max_items]
    return items


def quote_label(label: str) -> str:
    # gh api search syntax wants quotes around labels containing spaces/dots are fine unquoted,
    # but quote defensively for labels with spaces.
    return f'"{label}"' if " " in label else label


def upsert_item(conn, repo: str, raw: dict):
    number = raw["number"]
    is_pr = raw.get("pull_request") is not None
    labels = ",".join(l["name"] for l in raw.get("labels", []))
    assoc = raw.get("author_association")
    cur = conn.execute(
        """
        INSERT INTO items (repo, number, is_pr, title, state, merged, author, author_association,
                            is_ms, created_at, updated_at, closed_at, labels, body, url)
        VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
        ON CONFLICT(repo, number) DO UPDATE SET
            title=excluded.title, state=excluded.state, author_association=excluded.author_association,
            is_ms=excluded.is_ms, updated_at=excluded.updated_at, closed_at=excluded.closed_at,
            labels=excluded.labels, body=excluded.body
        """,
        (repo, number, int(is_pr), raw.get("title"), raw.get("state"), None,
         (raw.get("user") or {}).get("login"), assoc, is_ms(assoc),
         raw.get("created_at"), raw.get("updated_at"), raw.get("closed_at"), labels,
         raw.get("body") or "", raw.get("html_url")),
    )
    row = conn.execute("SELECT id FROM items WHERE repo=? AND number=?", (repo, number)).fetchone()
    item_id = row["id"]
    upsert_fts(conn, "items_fts", "item_id", item_id, title=raw.get("title") or "", body=raw.get("body") or "")
    return item_id, is_pr


def sync_comments(conn, repo: str, number: int, is_pr: bool):
    conn.execute("DELETE FROM comments WHERE repo=? AND item_number=?", (repo, number))
    conn.execute("DELETE FROM files WHERE repo=? AND item_number=?", (repo, number))

    rows = []
    for c in gh_jsonl([f"repos/{repo}/issues/{number}/comments?per_page=100", "--jq", ".[]"]):
        assoc = c.get("author_association")
        rows.append((repo, number, "issue_comment", (c.get("user") or {}).get("login"),
                      assoc, is_ms(assoc), c.get("created_at"), c.get("body") or "", None, c.get("html_url")))

    if is_pr:
        for r in gh_jsonl([f"repos/{repo}/pulls/{number}/reviews?per_page=100", "--jq", ".[]"]):
            assoc = r.get("author_association")
            rows.append((repo, number, "review", (r.get("user") or {}).get("login"),
                         assoc, is_ms(assoc), r.get("submitted_at"), r.get("body") or "", None, r.get("html_url")))
        for rc in gh_jsonl([f"repos/{repo}/pulls/{number}/comments?per_page=100", "--jq", ".[]"]):
            assoc = rc.get("author_association")
            rows.append((repo, number, "review_comment", (rc.get("user") or {}).get("login"),
                         assoc, is_ms(assoc), rc.get("created_at"), rc.get("body") or "",
                         rc.get("path"), rc.get("html_url")))
        for f in gh_jsonl([f"repos/{repo}/pulls/{number}/files?per_page=100", "--jq", ".[]"]):
            conn.execute(
                "INSERT OR IGNORE INTO files (repo, item_number, path) VALUES (?,?,?)",
                (repo, number, f.get("filename")),
            )

    for row in rows:
        cur = conn.execute(
            """INSERT INTO comments (repo, item_number, kind, author, author_association, is_ms,
                                      created_at, body, path, url) VALUES (?,?,?,?,?,?,?,?,?,?)""",
            row,
        )
        upsert_fts(conn, "comments_fts", "comment_id", cur.lastrowid, body=row[7])


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--db", required=True, help="Short KB name (e.g. compression-tar) or a .db path")
    ap.add_argument("--repo", help="owner/repo, defaults to dotnet/runtime or remembered value")
    ap.add_argument("--label", action="append", dest="labels", help="Area label; repeatable. Items matching ANY label are included")
    ap.add_argument("--since", help="Override incremental cutoff (YYYY-MM-DD); default is auto from last sync")
    ap.add_argument("--full", action="store_true", help="Ignore last-sync bookkeeping and refetch everything")
    ap.add_argument("--max-items", type=int, help="Cap number of items per label (testing/dry-run)")
    ap.add_argument("--no-comments", action="store_true", help="Skip fetching comments/reviews/files (faster, metadata only)")
    args = ap.parse_args()

    conn = open_db(args.db)

    meta_row = conn.execute("SELECT * FROM sync_meta WHERE kb_key = ?", (args.db,)).fetchone()
    repo = args.repo or (meta_row["repo"] if meta_row else "dotnet/runtime")
    labels = args.labels or (meta_row["labels"].split(",") if meta_row else None)
    if not labels:
        sys.exit("error: --label is required for a new KB (e.g. --label area-System.IO.Compression)")

    since = None
    if not args.full:
        if args.since:
            since = args.since
        elif meta_row and meta_row["last_synced_at"]:
            last = datetime.fromisoformat(meta_row["last_synced_at"]) - OVERLAP
            since = last.strftime("%Y-%m-%d")

    print(f"[kb] Syncing {repo} labels={labels} since={since or 'ALL TIME'} -> db={args.db}")

    all_items: dict[int, dict] = {}
    for label in labels:
        found = fetch_search_items(repo, label, since, args.max_items)
        print(f"[kb]   label '{label}': {len(found)} matching items")
        for raw in found:
            all_items[raw["number"]] = raw  # de-dupe union across labels

    print(f"[kb] {len(all_items)} unique items to upsert")
    for i, (number, raw) in enumerate(all_items.items(), 1):
        item_id, is_pr = upsert_item(conn, repo, raw)
        if not args.no_comments:
            sync_comments(conn, repo, number, is_pr)
        conn.commit()
        if i % 20 == 0:
            print(f"[kb]   ...{i}/{len(all_items)} processed")
            time.sleep(0.5)  # be a good citizen re: secondary rate limits

    now = datetime.now(timezone.utc).isoformat()
    conn.execute(
        "INSERT INTO sync_meta (kb_key, repo, labels, last_synced_at) VALUES (?,?,?,?) "
        "ON CONFLICT(kb_key) DO UPDATE SET last_synced_at=excluded.last_synced_at, repo=excluded.repo, labels=excluded.labels",
        (args.db, repo, ",".join(labels), now),
    )
    conn.commit()
    print(f"[kb] Done. Last synced at {now}")


if __name__ == "__main__":
    main()
