"""Uncertainty of backtest statistics (spec §7, §11): bootstrap and Wilson intervals."""

from __future__ import annotations

import math
from dataclasses import dataclass
from statistics import NormalDist

import numpy as np


@dataclass(frozen=True)
class Interval:
    estimate: float
    low: float
    high: float
    level: float


def bootstrap_mean(
    values: np.ndarray, level: float = 0.90, samples: int = 10_000, seed: int = 20261007
) -> Interval:
    """Percentile bootstrap of the mean (trades resampled with replacement, fixed seed).

    Treats trades as independent — overlapping trades in correlated ETFs make the true
    interval wider; the report says so next to the number."""
    x = np.asarray(values, dtype=float)
    if len(x) == 0:
        return Interval(math.nan, math.nan, math.nan, level)
    rng = np.random.default_rng(seed)
    means = rng.choice(x, size=(samples, len(x)), replace=True).mean(axis=1)
    tail = (1 - level) / 2
    lo, hi = np.quantile(means, [tail, 1 - tail])
    return Interval(float(x.mean()), float(lo), float(hi), level)


def block_bootstrap_mean(
    values: np.ndarray,
    blocks: np.ndarray,
    level: float = 0.90,
    samples: int = 10_000,
    seed: int = 20261007,
) -> Interval:
    """Bootstrap of the mean resampling whole blocks (e.g. calendar months of exit) with
    replacement, so trades that overlap in time and move together stay together (ETF_TREND_V2
    review S4). Each resample's mean = total of its blocks / number of their trades."""
    x = np.asarray(values, dtype=float)
    if len(x) == 0:
        return Interval(math.nan, math.nan, math.nan, level)
    _, idx = np.unique(np.asarray(blocks), return_inverse=True)
    sums = np.bincount(idx, weights=x)
    sizes = np.bincount(idx).astype(float)
    rng = np.random.default_rng(seed)
    pick = rng.integers(0, len(sums), size=(samples, len(sums)))
    means = sums[pick].sum(axis=1) / sizes[pick].sum(axis=1)
    tail = (1 - level) / 2
    lo, hi = np.quantile(means, [tail, 1 - tail])
    return Interval(float(x.mean()), float(lo), float(hi), level)


def wilson(successes: int, n: int, level: float = 0.95) -> Interval:
    """Wilson score interval for a proportion (NIST e-Handbook 7.2.4.1)."""
    if n == 0:
        return Interval(math.nan, math.nan, math.nan, level)
    z = NormalDist().inv_cdf(1 - (1 - level) / 2)
    p = successes / n
    denom = 1 + z * z / n
    centre = (p + z * z / (2 * n)) / denom
    half = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / denom
    return Interval(p, centre - half, centre + half, level)
