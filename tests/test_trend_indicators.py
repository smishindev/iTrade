"""ETF_TREND_V2 indicators (P1.H2.03): hand vectors and no look-ahead."""

from __future__ import annotations

import numpy as np
import pandas as pd

from itrade.strategies.indicators import momentum, prior_high, prior_low

C = pd.Series([10.0, 11.0, 9.0, 12.0, 8.0, 13.0])


def test_momentum_by_hand():
    m = momentum(C, 2)
    assert np.isnan(m[0]) and np.isnan(m[1])
    assert m[2] == 9.0 / 10.0 - 1.0 and m[5] == 13.0 / 12.0 - 1.0


def test_channels_exclude_the_current_bar():
    hi, lo = prior_high(C, 3), prior_low(C, 3)
    assert hi[:3].isna().all() and lo[:3].isna().all()
    assert (hi[3], lo[3]) == (11.0, 9.0)  # bars 0..2, not bar 3 itself
    assert (hi[5], lo[5]) == (12.0, 8.0)  # bars 2..4
    assert C[5] > hi[5]  # a breakout can only be seen because t is excluded


def test_no_look_ahead():
    rng = np.random.default_rng(1)
    base = pd.Series(100 + rng.normal(0, 1, 300).cumsum())
    t = 200
    changed = base.copy()
    changed[t + 1 :] = changed[t + 1 :] * 3 + 7
    for f, n in ((momentum, 126), (prior_high, 55), (prior_low, 20)):
        a, b = f(base, n), f(changed, n)
        assert np.allclose(a[: t + 1], b[: t + 1], equal_nan=True), f.__name__
