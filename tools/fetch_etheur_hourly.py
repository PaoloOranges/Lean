#!/usr/bin/env python3
"""Fetch public hourly (1h) ETHEUR candles and write a Lean trade-data zip at
Data/crypto/coinbase/hour/etheur_trade.zip.

The PaoloHourETHEURAlgorithm backtest subscribes ETHEUR at Resolution.Hour on
Market.Coinbase, but the repo ships no hourly file and Coinbase Exchange has
since delisted the ETHEUR pair (GET /products/ETHEUR -> 404, and it is absent
from /products), so the original Coinbase-sourced bars can no longer be
refetched. Binance still serves the ETHEUR pair publicly, and its hourly OHLCV
is economically equivalent for a price-following backtest, so this script
pages Binance /api/v3/klines (1000 bars/req) and emits the Lean-consolidated
hour format used by the repo's other hour files:

  zip         : Data/crypto/coinbase/hour/etheur_trade.zip
  zip entry   : etheur.csv
  line format : 20240101 01:00,open,high,low,close,volume
                (time = bar END time, matching Lean's consolidated storage)

Usage:
  python3 tools/fetch_etheur_hourly.py [--start 2023-12-18] [--end 2024-08-03]
                                       [--out Data/crypto/coinbase/hour/etheur_trade.zip]
"""
import argparse
import datetime as dt
import io
import json
import os
import sys
import time
import urllib.error
import urllib.request
import zipfile

API = "https://api.binance.com/api/v3/klines"
SYMBOL = "ETHEUR"
INTERVAL = "1h"
PER_PAGE = 1000
HOUR_MS = 3_600_000


def fetch_window(start: dt.datetime, end: dt.datetime):
    """Return {open_time_ms: (o,h,l,c,v)} covering [start, end), deduped."""
    bars = {}
    cur_ms = int(start.timestamp() * 1000)
    end_ms = int(end.timestamp() * 1000)
    headers = {"User-Agent": "lean-devcontainer-fetch/1.0", "Accept": "*/*"}
    while cur_ms < end_ms:
        url = (f"{API}?symbol={SYMBOL}&interval={INTERVAL}"
               f"&startTime={cur_ms}&endTime={end_ms - 1}&limit={PER_PAGE}")
        last_err = None
        rows = []
        for attempt in range(4):
            try:
                with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=30) as r:
                    rows = json.loads(r.read().decode())
                for row in rows:
                    o_ms, o, h, l, c, v = int(row[0]), row[1], row[2], row[3], row[4], row[5]
                    bars[o_ms] = (o, h, l, c, v)
                last_err = None
                break
            except (urllib.error.URLError, TimeoutError, json.JSONDecodeError, ValueError, IndexError) as e:
                last_err = e
                time.sleep(1.5 * (attempt + 1))
        if last_err is not None:
            raise RuntimeError(f"failed to fetch window {url}: {last_err}")
        if not rows:
            break  # no more data beyond cur_ms
        cur_ms = int(rows[-1][0]) + HOUR_MS
        time.sleep(0.25)  # well under Binance weight limits
    return bars


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--start", default="2023-12-18")
    ap.add_argument("--end", default="2024-08-03")
    ap.add_argument("--out", default="Data/crypto/coinbase/hour/etheur_trade.zip")
    args = ap.parse_args(argv)

    start = dt.datetime.strptime(args.start, "%Y-%m-%d").replace(tzinfo=dt.timezone.utc)
    end = dt.datetime.strptime(args.end, "%Y-%m-%d").replace(tzinfo=dt.timezone.utc)
    bars = fetch_window(start, end)
    if not bars:
        print("no candles returned", file=sys.stderr)
        return 1

    lines = []
    for o_ms in sorted(bars):
        o, h, l, c, v = bars[o_ms]
        end_t = dt.datetime.fromtimestamp(o_ms / 1000 + 3600, dt.timezone.utc)
        lines.append(f"{end_t:%Y%m%d %H:%M},{o},{h},{l},{c},{v}")

    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as zf:
        zf.writestr("etheur.csv", "\n".join(lines) + "\n")
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "wb") as f:
        f.write(buf.getvalue())
    first = dt.datetime.fromtimestamp(min(bars) / 1000, dt.timezone.utc)
    last = dt.datetime.fromtimestamp(max(bars) / 1000, dt.timezone.utc)
    print(f"wrote {len(bars)} hourly bars {first:%Y-%m-%d}..{last:%Y-%m-%d} -> {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
