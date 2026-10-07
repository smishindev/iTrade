"""Bootstrap and Wilson intervals (spec §7, §11)."""

from __future__ import annotations

import math

import numpy as np
import pytest

from itrade.backtest.stats import bootstrap_mean, wilson


def test_wilson_30_of_50_matches_the_review_example():
    w = wilson(30, 50, level=0.95)
    assert w.estimate == 0.6
    assert w.low == pytest.approx(0.4617, abs=5e-4) and w.high == pytest.approx(0.7239, abs=5e-4)


def test_wilson_edges():
    assert wilson(0, 10).low == pytest.approx(0.0, abs=1e-12) and wilson(0, 10).high > 0
    assert wilson(10, 10).high == pytest.approx(1.0, abs=1e-12)
    assert math.isnan(wilson(0, 0).estimate)


def test_bootstrap_is_reproducible_and_brackets_the_mean():
    rng = np.random.default_rng(1)
    r = rng.normal(0.1, 1.0, 400)
    a = bootstrap_mean(r, level=0.90, samples=5000, seed=7)
    b = bootstrap_mean(r, level=0.90, samples=5000, seed=7)
    assert a == b
    assert a.low < a.estimate < a.high


def test_bootstrap_width_close_to_normal_theory():
    rng = np.random.default_rng(2)
    r = rng.normal(0.0, 1.0, 900)
    ci = bootstrap_mean(r, level=0.90, samples=10_000)
    expected_half = 1.645 * r.std(ddof=1) / math.sqrt(len(r))
    assert (ci.high - ci.low) / 2 == pytest.approx(expected_half, rel=0.08)


def test_bootstrap_empty():
    assert math.isnan(bootstrap_mean(np.array([])).estimate)
