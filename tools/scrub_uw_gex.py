"""
Scrape the Unusual Whales greek-exposure grid out of saved page HTML into a CSV.

The grid is react-data-grid: every cell is a <div role="gridcell" aria-colindex="N">, and a row is
just a run of those with colindex 1..6. There is no reliable row wrapper to key on in a partial
paste, so rows are reassembled by watching colindex reset to 1.

    col 1  date (MM/DD/YYYY)
    col 2  call gamma
    col 3  put gamma      (negative)
    col 4  net gamma      (verified: col2 + col3 == col4)
    col 5  put/call ratio (red when > 1, green when < 1)
    col 6  empty

Values carry K/M/B/T suffixes inside a nested <span>, and col 5 wraps its number in a
text-danger/text-success div, so the numeric text has to be pulled from the cell's full inner
text rather than a direct child.

USAGE
    python scrub_uw_gex.py input.html [-o out.csv]
    ... | python scrub_uw_gex.py -            # read stdin

A fragment that begins mid-row loses that row's date; such rows are DROPPED and counted rather
than silently mis-dated. Rows whose col2+col3 does not reconcile to col4 are also flagged --
that check is the main defence against the parser drifting a column.
"""
import argparse
import csv
import re
import sys

CELL = re.compile(
    r'<div\b[^>]*\brole="gridcell"[^>]*\baria-colindex="(\d+)"[^>]*>(.*?)</div>\s*(?=<div\b[^>]*role="gridcell"|</div>|$)',
    re.S,
)
TAG = re.compile(r"<[^>]+>")
SUFFIX = {"K": 1e3, "M": 1e6, "B": 1e9, "T": 1e12}


def cell_text(html: str) -> str:
    """Inner text of a cell with tags stripped and entities/whitespace normalised."""
    txt = TAG.sub("", html)
    txt = txt.replace("&nbsp;", " ").replace("&minus;", "-").replace("&#8722;", "-")
    return " ".join(txt.split())


def to_number(txt: str):
    """'-430.85K' -> -430850.0 ; '1.18' -> 1.18 ; '' -> None."""
    t = txt.replace(",", "").replace("$", "").replace("−", "-").strip()
    if not t:
        return None
    mult = 1.0
    if t and t[-1].upper() in SUFFIX:
        mult = SUFFIX[t[-1].upper()]
        t = t[:-1]
    m = re.fullmatch(r"[-+]?\d*\.?\d+(?:[eE][-+]?\d+)?", t)
    if not m:
        return None
    return float(t) * mult


DATE_RE = re.compile(r"\b(\d{2}/\d{2}/\d{4})\b")


def parse(html: str):
    rows, cur, dropped, mismatched = [], {}, 0, 0

    # A copy-pasted fragment often begins PART-WAY THROUGH the first cell's opening tag, so the
    # date is present as bare text but not inside anything the cell regex can match. Recover it
    # from whatever precedes the first well-formed gridcell.
    first = CELL.search(html)
    head = html[: first.start()] if first else ""
    if not re.search(r'role="gridcell"[^>]*aria-colindex="1"', head):
        d = DATE_RE.search(TAG.sub(" ", head))
        if d:
            cur[1] = d.group(1)

    def flush():
        nonlocal dropped, mismatched
        if not cur:
            return
        if 1 not in cur or not re.fullmatch(r"\d{2}/\d{2}/\d{4}", cur[1]):
            dropped += 1          # fragment started mid-row: no date, so the row is unusable
            return
        call = to_number(cur.get(2, ""))
        put = to_number(cur.get(3, ""))
        net = to_number(cur.get(4, ""))
        ratio = to_number(cur.get(5, ""))
        if call is None or put is None or net is None:
            dropped += 1
            return
        # column-drift guard: the grid's own arithmetic must reconcile
        ok = abs((call + put) - net) <= max(1.0, abs(net) * 0.01)
        if not ok:
            mismatched += 1
        mm, dd, yyyy = cur[1].split("/")
        rows.append({
            "date": f"{yyyy}-{mm}-{dd}",
            "call_gamma": call,
            "put_gamma": put,
            "net_gamma": net,
            "put_call_ratio": ratio if ratio is not None else "",
            "reconciles": "yes" if ok else "NO",
        })

    for m in CELL.finditer(html):
        idx = int(m.group(1))
        # A new row starts either at colindex 1, or at ANY colindex already filled. The second case
        # matters: if a row's date cell is mangled the index never resets to 1, and keying only on
        # that silently MERGES two rows -- taking the call leg from one and the put leg from the
        # next. The reconcile check catches it after the fact; this prevents it.
        if cur and (idx == 1 or idx in cur):
            flush()
            cur = {}
        cur[idx] = cell_text(m.group(2))
    flush()
    return rows, dropped, mismatched


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("input", help="HTML file, or - for stdin")
    ap.add_argument("-o", "--out", default=None, help="CSV output (default: stdout)")
    a = ap.parse_args()

    html = sys.stdin.read() if a.input == "-" else open(a.input, encoding="utf-8", errors="replace").read()
    rows, dropped, mismatched = parse(html)

    rows.sort(key=lambda r: r["date"])
    seen, dedup = set(), []
    for r in rows:
        if r["date"] in seen:
            continue
        seen.add(r["date"])
        dedup.append(r)

    fields = ["date", "call_gamma", "put_gamma", "net_gamma", "put_call_ratio", "reconciles"]
    fh = open(a.out, "w", newline="", encoding="utf-8") if a.out else sys.stdout
    w = csv.DictWriter(fh, fieldnames=fields)
    w.writeheader()
    w.writerows(dedup)
    if a.out:
        fh.close()

    print(
        f"parsed {len(dedup)} dated rows"
        + (f" ({len(rows) - len(dedup)} duplicate dates collapsed)" if len(rows) != len(dedup) else "")
        + f"; dropped {dropped} undated/partial"
        + (f"; {mismatched} FAILED the call+put==net check" if mismatched else "; all reconcile"),
        file=sys.stderr,
    )
    if dedup:
        print(f"range {dedup[0]['date']} -> {dedup[-1]['date']}", file=sys.stderr)


if __name__ == "__main__":
    main()
