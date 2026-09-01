"""Query a local runtime-kb knowledge base. Subcommands mirror a typical GitHub-history
MCP tool surface (search, search-comments, recent, thread, related, search-by-file, stats)
so the output is familiar to consume from an agent.

Usage:
    python query_kb.py stats --db compression-tar
    python query_kb.py search "brotli decoder state" --db compression-tar --limit 10
    python query_kb.py search-comments "symlink traversal" --db compression-tar --ms-only
    python query_kb.py recent --db compression-tar --limit 15
    python query_kb.py thread 12345 --db compression-tar
    python query_kb.py related 12345 --db compression-tar
    python query_kb.py search-by-file "TarReader" --db compression-tar
"""
from __future__ import annotations

import argparse
import sys

from kb_common import open_db

# Windows consoles are frequently on a legacy codepage (cp1252) that can't
# encode arbitrary unicode from GitHub content; force UTF-8 stdout so output
# never crashes or mangles into replacement characters.
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")


def fmt_item_row(r) -> str:
    kind = "PR" if r["is_pr"] else "Issue"
    ms = " [MS]" if r["is_ms"] else ""
    return f"#{r['number']} ({kind}, {r['state']}) {r['title']} - @{r['author']}{ms}  {r['url']}"


def cmd_stats(conn, args):
    row = conn.execute(
        """SELECT
             SUM(is_pr=0) AS issues, SUM(is_pr=0 AND state='open') AS open_issues,
             SUM(is_pr=1) AS prs, SUM(is_pr=1 AND state='open') AS open_prs,
             MIN(created_at) AS earliest, MAX(updated_at) AS latest
           FROM items"""
    ).fetchone()
    n_comments = conn.execute("SELECT COUNT(*) c, SUM(is_ms) ms FROM comments").fetchone()
    meta = conn.execute("SELECT * FROM sync_meta LIMIT 1").fetchone()
    print(f"# Knowledge Base Statistics ({args.db})")
    print()
    if meta:
        print(f"Repo: {meta['repo']}   Labels: {meta['labels']}")
        print(f"Last synced: {meta['last_synced_at']}")
    print(f"Date range: {row['earliest']} -> {row['latest']}")
    print()
    print(f"Issues: {row['issues'] or 0} ({row['open_issues'] or 0} open)")
    print(f"Pull Requests: {row['prs'] or 0} ({row['open_prs'] or 0} open)")
    print(f"Comments/reviews: {n_comments['c'] or 0} ({n_comments['ms'] or 0} from Microsoft)")
    print()
    print("## Top contributors")
    for c in conn.execute(
        """SELECT author, is_ms, COUNT(*) n FROM (
             SELECT author, is_ms FROM items
             UNION ALL SELECT author, is_ms FROM comments
           ) GROUP BY author ORDER BY n DESC LIMIT 15"""
    ):
        tag = " [MS]" if c["is_ms"] else ""
        print(f"  {c['author']}{tag}: {c['n']}")


def cmd_search(conn, args):
    sql = """SELECT items.* FROM items_fts JOIN items ON items.id = items_fts.item_id
              WHERE items_fts MATCH ?"""
    params = [args.query]
    if args.type != "all":
        sql += " AND items.is_pr = ?"
        params.append(1 if args.type == "pr" else 0)
    if args.state != "all":
        sql += " AND items.state = ?"
        params.append(args.state)
    if args.ms_only:
        sql += " AND items.is_ms = 1"
    sql += " ORDER BY rank LIMIT ?"
    params.append(args.limit)
    rows = conn.execute(sql, params).fetchall()
    if not rows:
        print("No matching items.")
        return
    for r in rows:
        print(fmt_item_row(r))


def cmd_search_comments(conn, args):
    sql = """SELECT comments.*, items.title AS item_title FROM comments_fts
             JOIN comments ON comments.id = comments_fts.comment_id
             JOIN items ON items.repo = comments.repo AND items.number = comments.item_number
             WHERE comments_fts MATCH ?"""
    params = [args.query]
    if args.ms_only:
        sql += " AND comments.is_ms = 1"
    sql += " ORDER BY rank LIMIT ?"
    params.append(args.limit)
    rows = conn.execute(sql, params).fetchall()
    if not rows:
        print("No matching comments.")
        return
    for r in rows:
        ms = " [MS]" if r["is_ms"] else ""
        snippet = (r["body"] or "").replace("\n", " ")[:200]
        print(f"#{r['item_number']} \"{r['item_title']}\" - {r['kind']} by @{r['author']}{ms} ({r['created_at']})")
        print(f"    {snippet}")


def cmd_recent(conn, args):
    sql = "SELECT * FROM items WHERE 1=1"
    params = []
    if args.type != "all":
        sql += " AND is_pr = ?"
        params.append(1 if args.type == "pr" else 0)
    if args.state != "all":
        sql += " AND state = ?"
        params.append(args.state)
    sql += " ORDER BY updated_at DESC LIMIT ?"
    params.append(args.limit)
    for r in conn.execute(sql, params):
        print(fmt_item_row(r))


def cmd_thread(conn, args):
    item = conn.execute("SELECT * FROM items WHERE number = ?", (args.number,)).fetchone()
    if not item:
        print(f"No item #{args.number} in this KB.")
        return
    kind = "PR" if item["is_pr"] else "Issue"
    print(f"# {kind} #{item['number']}: {item['title']}")
    print(f"State: {item['state']}   Author: @{item['author']}   Labels: {item['labels']}")
    print(f"{item['url']}")
    print()
    print(item["body"] or "(no description)")
    print()
    print("## Timeline")
    for c in conn.execute(
        "SELECT * FROM comments WHERE repo=? AND item_number=? ORDER BY created_at",
        (item["repo"], item["number"]),
    ):
        ms = " [MS]" if c["is_ms"] else ""
        path = f" ({c['path']})" if c["path"] else ""
        print(f"--- {c['kind']}{path} by @{c['author']}{ms} at {c['created_at']} ---")
        print(c["body"] or "")
        print()


def cmd_related(conn, args):
    item = conn.execute("SELECT * FROM items WHERE number = ?", (args.number,)).fetchone()
    if not item:
        print(f"No item #{args.number} in this KB.")
        return
    query_text = (item["title"] or "").replace('"', " ")
    rows = conn.execute(
        """SELECT items.* FROM items_fts JOIN items ON items.id = items_fts.item_id
           WHERE items_fts MATCH ? AND items.number != ?
           ORDER BY rank LIMIT ?""",
        (query_text, args.number, args.limit),
    ).fetchall()
    if not rows:
        print("No related items found.")
        return
    for r in rows:
        print(fmt_item_row(r))


def cmd_search_by_file(conn, args):
    rows = conn.execute(
        """SELECT DISTINCT items.* FROM files JOIN items
           ON items.repo = files.repo AND items.number = files.item_number
           WHERE files.path LIKE ? ORDER BY items.updated_at DESC LIMIT ?""",
        (f"%{args.pattern}%", args.limit),
    ).fetchall()
    if not rows:
        print("No PRs found touching matching files.")
        return
    for r in rows:
        print(fmt_item_row(r))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--db", required=True, help="Short KB name or .db path")
    sub = ap.add_subparsers(dest="cmd", required=True)

    sub.add_parser("stats")

    p = sub.add_parser("search")
    p.add_argument("query")
    p.add_argument("--type", choices=["issue", "pr", "all"], default="all")
    p.add_argument("--state", choices=["open", "closed", "all"], default="all")
    p.add_argument("--ms-only", action="store_true")
    p.add_argument("--limit", type=int, default=20)

    p = sub.add_parser("search-comments")
    p.add_argument("query")
    p.add_argument("--ms-only", action="store_true")
    p.add_argument("--limit", type=int, default=20)

    p = sub.add_parser("recent")
    p.add_argument("--type", choices=["issue", "pr", "all"], default="all")
    p.add_argument("--state", choices=["open", "closed", "all"], default="all")
    p.add_argument("--limit", type=int, default=20)

    p = sub.add_parser("thread")
    p.add_argument("number", type=int)

    p = sub.add_parser("related")
    p.add_argument("number", type=int)
    p.add_argument("--limit", type=int, default=10)

    p = sub.add_parser("search-by-file")
    p.add_argument("pattern")
    p.add_argument("--limit", type=int, default=20)

    args = ap.parse_args()
    conn = open_db(args.db)

    dispatch = {
        "stats": cmd_stats, "search": cmd_search, "search-comments": cmd_search_comments,
        "recent": cmd_recent, "thread": cmd_thread, "related": cmd_related,
        "search-by-file": cmd_search_by_file,
    }
    dispatch[args.cmd](conn, args)


if __name__ == "__main__":
    main()
