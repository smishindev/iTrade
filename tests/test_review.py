"""Trade review re-derives trades independently of the simulator (P1.A.26)."""

from __future__ import annotations

from decimal import Decimal as D

from itrade.backtest.review import check_trade, trade_svg, write_review
from test_engine import SESSIONS, SP, run, scenario


def one_trade(**kwargs):
    market, prepared = scenario(**kwargs)
    res = run(market, prepared)
    assert len(res.trades) == 1
    return res.trades.iloc[0].to_dict(), market["AAA"], prepared["AAA"]


def failed(trade, market, prepared):
    return {c.name for c in check_trade(trade, market, prepared, SESSIONS, SP) if not c.ok}


OPENS = [20.0, 20.10, 20.0, 20.0, 20.40, 20.0, 20.0, 20.0, 20.0, 20.0, 20.0, 20.0]


def test_simulated_trades_pass_every_check():
    assert failed(*one_trade(opens=OPENS)) == set()
    lows = [19.9, 19.0] + [19.9] * 10  # stop on the entry day
    trade, market, prepared = one_trade(lows=lows, exit_day=99)
    assert trade["exit_reason"] == "stop"
    assert failed(trade, market, prepared) == set()


def test_tampered_trades_are_caught():
    trade, market, prepared = one_trade(opens=OPENS)
    assert "entry at the open" in failed(trade | {"entry_price": D("20.05")}, market, prepared)
    assert "limit = C + 0.5 ATR" in failed(trade | {"limit": D("20.30")}, market, prepared)
    assert "exit: exit_sma" in failed(trade | {"exit_price": D("20.50")}, market, prepared)
    assert "R = P&L / risk" in failed(trade | {"r": trade["r"] + 1}, market, prepared)


def test_missed_earlier_exit_is_caught():
    trade, market, prepared = one_trade(opens=OPENS)
    prepared = prepared.copy()
    prepared.loc[SESSIONS[1], "sma_exit"] = 19.0  # exit signal one day earlier than simulated
    bad = failed(trade, market, prepared)
    assert "nothing missed before the exit" in bad


def test_signal_that_was_not_a_signal_is_caught():
    trade, market, prepared = one_trade(opens=OPENS)
    prepared = prepared.copy()
    prepared.loc[SESSIONS[0], "rsi"] = 30.0
    assert any(name.startswith("pullback") for name in failed(trade, market, prepared))


def test_review_files(tmp_path):
    trade, market, prepared = one_trade(opens=OPENS)
    svg = trade_svg(trade, market, prepared, SESSIONS)
    assert svg.startswith("<svg") and "stop" in svg
    import pandas as pd

    n, bad = write_review(
        pd.DataFrame([trade]), {"AAA": market}, {"AAA": prepared}, SESSIONS, SP, tmp_path
    )
    assert (n, bad) == (1, 0)
    text = (tmp_path / "review.md").read_text(encoding="utf-8")
    assert "AAA" in text and "✗" not in text
    assert len(list(tmp_path.glob("*.svg"))) == 1
