"""ETF_TREND_V2 look-ahead detector (review S6): decisions at d ignore every bar after d, and a
peeking variant is caught."""

from __future__ import annotations

import pandas as pd
import pytest

from itrade.backtest.causality import trend_causality_violations
from itrade.strategies.etf_trend_v2 import is_month_end, prepare_trend_instrument
from test_review_trend import GROUPS, SESSIONS, bars
from test_trend_rules import setup

RAW = {t: bars(i, 0.0006 * (i - 2)) for i, t in enumerate(GROUPS)}
MEMBER = {t: pd.Series(True, index=SESSIONS) for t in GROUPS}
MONTH_ENDS = [d for d in SESSIONS[260:-1] if is_month_end(d, SESSIONS)][:8]
OTHER_DAYS = list(SESSIONS[300:600:40])


@pytest.mark.parametrize("variant", ["rot_base", "rot_mom252", "brk_base", "brk_trend"])
def test_decisions_do_not_depend_on_the_future(variant):
    _, p, _, _ = setup(variant)
    dates = MONTH_ENDS if variant.startswith("rot") else OTHER_DAYS + MONTH_ENDS[:3]
    assert trend_causality_violations(RAW, MEMBER, SESSIONS, p, GROUPS, dates) == []


def test_a_peeking_momentum_is_caught():
    _, p, _, _ = setup("rot_base")

    def peeking(b, member, sessions, params):
        out = prepare_trend_instrument(b, member, sessions, params)
        # lookahead-ok: deliberately peeks 5 sessions ahead to prove the detector works
        return out.assign(mom=out["close_star"].shift(-5) / out["close_star"] - 1)

    found = trend_causality_violations(RAW, MEMBER, SESSIONS, p, GROUPS, MONTH_ENDS, peeking)
    assert found
