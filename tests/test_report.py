"""Run report (P1.A.23): Markdown helpers, SVG, and reproducibility on real data."""

from __future__ import annotations

import json

import pandas as pd
import pytest

from itrade.backtest.report import equity_svg, fmt, md_table
from itrade.config import project_root


def test_md_table_and_fmt():
    df = pd.DataFrame({"reason": ["a", "b"], "count": [3, 1]}).set_index("reason")
    assert md_table(df).splitlines() == [
        "| reason | count |",
        "|---|---|",
        "| a | 3 |",
        "| b | 1 |",
    ]
    assert md_table(pd.DataFrame()) == "_none_"
    assert fmt(float("nan")) == "—" and fmt(0.1234, pct=True) == "12.3%" and fmt(1.5) == "1.500"


def test_equity_svg_is_well_formed():
    eq = pd.DataFrame({"date": pd.bdate_range("2020-01-01", periods=5), "equity": [1, 2, 3, 2, 4]})
    svg = equity_svg(eq, eq.assign(equity=[1, 1, 1, 1, 1]))
    assert svg.startswith("<svg") and svg.endswith("</svg>") and svg.count("<polyline") == 2


DATA = project_root() / "data" / "curated" / "manifest.json"


@pytest.mark.skipif(not DATA.exists(), reason="needs local curated data (itrade ingest)")
def test_report_is_complete_and_reproducible(tmp_path):
    from itrade.backtest.report import write_report
    from itrade.backtest.runner import run_variant

    a = write_report(run_variant(period="development"), tmp_path / "a")
    b = write_report(run_variant(period="development"), tmp_path / "b")
    assert a.run_id == b.run_id
    for name in ["report.md", "trades.csv", "skipped.csv", "equity.csv", "equity.svg", "meta.json"]:
        assert (a.directory / name).exists(), name
    ma = json.loads((a.directory / "meta.json").read_text(encoding="utf-8"))
    mb = json.loads((b.directory / "meta.json").read_text(encoding="utf-8"))
    for key in ["summary", "expectancy_ci", "win_rate_ci", "trades_sha256", "strategy_version"]:
        assert ma[key] == mb[key], key
