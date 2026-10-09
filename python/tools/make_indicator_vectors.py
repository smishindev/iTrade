"""Generate the reference indicator vectors of docs/STRATEGY_ETF_PULLBACK_V1.md §14.

Deliberately independent from src/itrade: plain loops in exact rational arithmetic
(fractions.Fraction), transcribing §2 of the spec step by step. Both implementations (Python
spike A, C# core) must reproduce these values to 1e-9. Output:
tests/fixtures/indicators/<name>.csv (values with 10 decimals, empty = undefined). Re-running
must reproduce the committed files byte for byte (tests check this).

Usage:  python tools/make_indicator_vectors.py [--steps]
"""

from __future__ import annotations

import csv
import io
import sys
from fractions import Fraction as F
from pathlib import Path

OUT = Path(__file__).resolve().parents[1] / "tests" / "fixtures" / "indicators"

# 20 consecutive XNYS sessions (2024-01-15 is Martin Luther King Jr. Day — no session).
DATES = [
    "2024-01-02", "2024-01-03", "2024-01-04", "2024-01-05", "2024-01-08",
    "2024-01-09", "2024-01-10", "2024-01-11", "2024-01-12", "2024-01-16",
    "2024-01-17", "2024-01-18", "2024-01-19", "2024-01-22", "2024-01-23",
    "2024-01-24", "2024-01-25", "2024-01-26", "2024-01-29", "2024-01-30",
]  # fmt: skip


def bars_from_closes(closes: list[str], open_step: str, wick: str) -> list[dict]:
    """open = previous close + open_step (first bar: open = close);
    high/low = max/min(open, close) ± wick."""
    out = []
    prev = None
    for i, c in enumerate(closes):
        close = F(c)
        open_ = close if prev is None else prev + F(open_step)
        out.append(
            {
                "date": DATES[i],
                "open": open_,
                "high": max(open_, close) + F(wick),
                "low": min(open_, close) - F(wick),
                "close": close,
                "volume": 1_000_000 + 10_000 * i,
            }
        )
        prev = close
    return out


def make_series() -> dict[str, list[dict]]:
    rising = bars_from_closes(
        ["100.00", "101.00", "100.50", "102.00", "103.00", "102.50", "104.00",
         "105.00", "104.00", "106.00", "107.00", "106.50", "108.00", "109.00",
         "108.00", "110.00", "111.00", "110.50", "112.00", "113.00"],
        open_step="0.20", wick="0.50",
    )  # fmt: skip
    falling = bars_from_closes(
        ["100.00", "99.00", "99.50", "98.00", "97.00", "97.50", "96.00",
         "95.00", "96.00", "94.00", "93.00", "93.50", "92.00", "91.00",
         "92.00", "90.00", "89.00", "89.50", "88.00", "87.00"],
        open_step="-0.20", wick="0.50",
    )  # fmt: skip
    gap = bars_from_closes(
        ["50.00", "50.40", "50.20", "50.80", "51.00", "50.60", "51.20",
         "51.50", "51.10", "51.80", "52.00", "51.60", "52.20", "52.40",
         "49.20", "49.60", "49.30", "50.10", "51.80", "51.60"],
        open_step="0.10", wick="0.30",
    )  # fmt: skip
    # Bar 14: gap down far below the previous low (and through a stop placed under 51.00).
    gap[14].update(open=F("49.00"), high=F("49.40"), low=F("48.50"))
    # Bar 18: gap up above the previous high.
    gap[18].update(open=F("51.50"), high=F("52.00"), low=F("51.30"))
    return {"rising": rising, "falling": falling, "gap": gap}


def sma(closes: list[F], n: int) -> list[F | None]:
    return [
        None if i < n - 1 else sum(closes[i - n + 1 : i + 1], F(0)) / n for i in range(len(closes))
    ]


def rsi_wilder(closes: list[F], n: int) -> list[F | None]:
    out: list[F | None] = [None] * len(closes)
    gains = [F(0)] + [max(closes[i] - closes[i - 1], F(0)) for i in range(1, len(closes))]
    losses = [F(0)] + [max(closes[i - 1] - closes[i], F(0)) for i in range(1, len(closes))]
    ag = al = None
    for i in range(1, len(closes)):
        if i == n:
            ag = sum(gains[1 : n + 1], F(0)) / n
            al = sum(losses[1 : n + 1], F(0)) / n
        elif i > n:
            ag = (ag * (n - 1) + gains[i]) / n
            al = (al * (n - 1) + losses[i]) / n
        if ag is not None:
            if al == 0:
                out[i] = F(100) if ag > 0 else F(50)
            else:
                out[i] = 100 - F(100) / (1 + ag / al)
    return out


def true_range(bars: list[dict]) -> list[F | None]:
    tr: list[F | None] = [None]
    for i in range(1, len(bars)):
        h, lo, pc = bars[i]["high"], bars[i]["low"], bars[i - 1]["close"]
        tr.append(max(h - lo, abs(h - pc), abs(lo - pc)))
    return tr


def atr_wilder(bars: list[dict], n: int) -> list[F | None]:
    tr = true_range(bars)
    out: list[F | None] = [None] * len(bars)
    atr = None
    for i in range(1, len(bars)):
        if i == n:
            atr = sum(tr[1 : n + 1], F(0)) / n
        elif i > n:
            atr = (atr * (n - 1) + tr[i]) / n
        out[i] = atr
    return out


def adv(bars: list[dict], w: int) -> list[F | None]:
    dv = [b["close"] * b["volume"] for b in bars]
    return [None if i < w - 1 else sum(dv[i - w + 1 : i + 1], F(0)) / w for i in range(len(bars))]


def fmt(x: F | int | None, places: int = 10) -> str:
    if x is None:
        return ""
    if isinstance(x, int):
        return str(x)
    q = round(x * 10**places) / F(10**places)  # banker's rounding of an exact rational is fine here
    sign = "-" if q < 0 else ""
    q = abs(q)
    whole = q.numerator // q.denominator
    frac = (q - whole) * 10**places
    return f"{sign}{whole}.{int(frac):0{places}d}"


COLUMNS = [
    "date",
    "open",
    "high",
    "low",
    "close",
    "volume",
    "sma3",
    "sma5",
    "rsi2",
    "atr14",
    "adv5",
]


def compute(bars: list[dict]) -> list[dict]:
    closes = [b["close"] for b in bars]
    ind = {
        "sma3": sma(closes, 3),
        "sma5": sma(closes, 5),
        "rsi2": rsi_wilder(closes, 2),
        "atr14": atr_wilder(bars, 14),
        "adv5": adv(bars, 5),
    }
    return [{**b, **{k: v[i] for k, v in ind.items()}} for i, b in enumerate(bars)]


def render(bars: list[dict]) -> str:
    buf = io.StringIO()
    w = csv.writer(buf, lineterminator="\n")
    w.writerow(COLUMNS)
    for r in compute(bars):
        w.writerow(
            [r["date"]]
            + [fmt(r[c], 2) for c in ("open", "high", "low", "close")]
            + [r["volume"]]
            + [fmt(r[c]) for c in COLUMNS[6:]]
        )
    return buf.getvalue()


def write_all() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    for name, bars in make_series().items():
        (OUT / f"{name}.csv").write_text(render(bars), encoding="utf-8", newline="\n")


def print_steps() -> None:
    """Markdown step tables for the spec (RSI(2) and ATR(14) seed on the 'rising' series)."""
    bars = make_series()["rising"]
    closes = [b["close"] for b in bars]
    rsi = rsi_wilder(closes, 2)
    print("| i | C | Δ | G | Λ | AG | AL | RSI(2) |\n|---|---|---|---|---|---|---|---|")
    ag = al = None
    for i in range(0, 7):
        d = None if i == 0 else closes[i] - closes[i - 1]
        g = None if d is None else max(d, F(0))
        lo = None if d is None else max(-d, F(0))
        if i == 2:
            ag = (max(closes[1] - closes[0], F(0)) + g) / 2
            al = (max(closes[0] - closes[1], F(0)) + lo) / 2
        elif i > 2:
            ag = (ag + g) / 2
            al = (al + lo) / 2
        print(
            f"| {i} | {fmt(closes[i], 2)} | {fmt(d, 2)} | {fmt(g, 2)} | {fmt(lo, 2)} | "
            f"{fmt(ag, 4)} | {fmt(al, 4)} | {fmt(rsi[i], 4)} |"
        )
    tr = true_range(bars)
    atr = atr_wilder(bars, 14)
    print("\nTR_1..TR_14:", ", ".join(fmt(x, 2) for x in tr[1:15]))
    print("ATR_14(14) =", fmt(atr[14], 6), "; ATR_14(15) = (13*ATR + TR_15)/14 =", fmt(atr[15], 6))


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    if "--steps" in sys.argv:
        print_steps()
    else:
        write_all()
        print(f"written to {OUT}")
