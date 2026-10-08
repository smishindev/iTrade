# Гипотеза H2 (ETF_TREND_V2) — валидация 2017–2020 (P1.H2.12)

_8 октября 2026. Все 18 заранее записанных вариантов, каждый с 1 000 прогонов случайного контроля. Журнал —
[research-log-H2.md](../research-log-H2.md), строки #4–#21. Таблица и правило выбора — вывод
`uv run itrade compare --strategy etf_trend_v2 --period validation`. Издержки включены, счёт $3 280._

| Variant | Type | Trades | Exp. R (90% CI) | Win % | Costs R | Max DD | Exec. | Control pct | CAGR |
|---|---|---|---|---|---|---|---|---|---|
| rot_base | candidate | 81 | -0.007 (-0.240 … +0.247) | 38.3% | 0.044 | 18.6% | 98% | 4.6 | +1.2% |
| rot_mom63 | candidate | 91 | +0.149 (-0.062 … +0.384) | 51.6% | 0.043 | 13.2% | 100% | 43.1 | +2.7% |
| rot_mom252 | candidate | 67 | +0.023 (-0.233 … +0.316) | 35.8% | 0.042 | 14.4% | 96% | 15.8 | +0.9% |
| rot_top2 | candidate | 54 | +0.045 (-0.179 … +0.285) | 44.4% | 0.040 | 12.3% | 100% | 12.2 | +1.2% |
| rot_top4 | candidate | 98 | +0.080 (-0.154 … +0.332) | 37.8% | 0.045 | 20.2% | 96% | 12.8 | +2.7% |
| rot_no_trend | candidate | 81 | -0.008 (-0.245 … +0.249) | 38.3% | 0.044 | 19.9% | 96% | 2.0 | +1.3% |
| rot_stop2 | candidate | 91 | -0.016 (-0.215 … +0.206) | 31.9% | 0.045 | 19.3% | 98% | 4.2 | +0.7% |
| rot_stop4 | candidate | 75 | +0.051 (-0.216 … +0.339) | 42.7% | 0.044 | 16.3% | 97% | 11.6 | +2.1% |
| brk_base | candidate | 93 | -0.059 (-0.338 … +0.267) | 31.2% | 0.083 | 12.4% | 85% | 7.6 | -0.7% |
| brk_20_10 | candidate | 146 | +0.103 (-0.080 … +0.308) | 39.0% | 0.080 | 7.6% | 84% | 49.1 | +1.3% |
| brk_100_40 | candidate | 64 | -0.114 (-0.521 … +0.425) | 18.8% | 0.083 | 14.1% | 84% | 1.3 | -0.8% |
| brk_trend | candidate | 93 | +0.004 (-0.267 … +0.312) | 32.3% | 0.082 | 10.0% | 85% | 23.3 | -0.1% |
| brk_stop3 | candidate | 74 | +0.120 (-0.154 … +0.409) | 43.2% | 0.072 | 7.6% | 94% | 59.3 | +1.0% |
| brk_risk1 | candidate | 93 | -0.048 (-0.328 … +0.281) | 31.2% | 0.073 | 15.3% | 85% | 5.7 | -0.6% |
| rot_fractional | candidate | 80 | +0.044 (-0.186 … +0.291) | 40.0% | 0.042 | 17.6% | 98% | 15.6 | +2.4% |
| brk_fractional | candidate | 93 | +0.012 (-0.304 … +0.375) | 31.2% | 0.079 | 10.8% | 87% | 13.5 | -0.1% |
| rot_costs_x2 | diagnostic | 81 | -0.052 (-0.285 … +0.203) | 37.0% | 0.089 | 20.0% | 98% | 4.4 | +0.5% |
| brk_costs_x2 | diagnostic | 94 | -0.182 (-0.442 … +0.125) | 28.7% | 0.168 | 15.2% | 84% | 8.2 | -1.9% |

Market reference (SPY buy & hold, total return), validation period: CAGR +15.7%. Equal-weight universe: CAGR +11.4%.

**Selection rule → `rot_mom63`**

- rot_mom63 (rotation:rotation.momentum_sessions): +43.100 pct vs rot_base +4.600, beats by 10.0 pct; family ['rot_base', 'rot_mom252'] all > 0 → selected
- rot_mom252 (rotation:rotation.momentum_sessions): +15.800 pct vs rot_base +4.600, beats by 10.0 pct; family ['rot_mom63', 'rot_base'] all > 0 → selected
- rot_top2 (rotation:rotation.top_n): +12.200 pct vs rot_base +4.600, does not beat by 10.0 pct; family ['rot_base', 'rot_top4'] all > 0 → no
- rot_top4 (rotation:rotation.top_n): +12.800 pct vs rot_base +4.600, does not beat by 10.0 pct; family ['rot_top2', 'rot_base'] all > 0 → no
- rot_no_trend (rotation:rotation.trend_filter): +2.000 pct vs rot_base +4.600, does not beat by 10.0 pct; family ['rot_base'] all > 0 → no
- rot_stop2 (rotation:rotation.stop_atr): +4.200 pct vs rot_base +4.600, does not beat by 10.0 pct; family ['rot_base', 'rot_stop4'] all > 0 → no
- rot_stop4 (rotation:rotation.stop_atr): +11.600 pct vs rot_base +4.600, does not beat by 10.0 pct; family ['rot_stop2', 'rot_base'] all > 0 → no
- rot_fractional (rotation:risk.share_granularity): +15.600 pct vs rot_base +4.600, beats by 10.0 pct; family ['rot_base'] all > 0 → not eligible until P1.B.10 confirms fractional shares with a stop
- brk_20_10 (breakout:breakout.entry_sessions): +49.100 pct vs brk_base +7.600, beats by 10.0 pct; family ['brk_base', 'brk_100_40'] not all > 0 → no
- brk_100_40 (breakout:breakout.entry_sessions): +1.300 pct vs brk_base +7.600, does not beat by 10.0 pct; family ['brk_20_10', 'brk_base'] not all > 0 → no
- brk_trend (breakout:breakout.trend_filter): +23.300 pct vs brk_base +7.600, beats by 10.0 pct; family ['brk_base'] not all > 0 → no
- brk_stop3 (breakout:breakout.stop_atr): +59.300 pct vs brk_base +7.600, beats by 10.0 pct; family ['brk_base'] not all > 0 → no
- brk_risk1 (breakout:breakout.risk_per_trade): +5.700 pct vs brk_base +7.600, does not beat by 10.0 pct; family ['brk_base'] not all > 0 → no
- brk_fractional (breakout:risk.share_granularity): +13.500 pct vs brk_base +7.600, does not beat by 10.0 pct; family ['brk_base'] not all > 0 → not eligible until P1.B.10 confirms fractional shares with a stop
- between groups (higher control percentile on total P&L): rot_mom63 43.1, brk_base 7.6 → rot_mom63
- early rejection: CAGR +2.74% (must be > 0%), control 43.1 (must be >= 80) → **H2 rejected, final period not run**

**Final-period criteria evaluated on validation for `rot_mom63` (information only):**

- ✓ expectancy > 0R
- ✗ 90% lower bound > 0
- ✓ trades >= 60
- ✗ control percentile >= 95
- ✓ rot_costs_x2 CAGR > 0
- ✓ most neighbours > 0
- ✓ executable >= 70%
- ✓ max drawdown <= 20%
- ✓ CAGR after costs > 0


## Что это значит

- **По правилу, записанному до запусков (спецификация §8), H2 отклоняется без финального прогона.** Выбранный
  правилом вариант `rot_mom63` заработал +2,7% в год, но его перцентиль случайного контроля **43** при пороге 80:
  случайный выбор ETF с теми же фильтрами, размером, стопами и инерцией зарабатывал столько же.
- **Ни один из 16 кандидатов не лучше случайного выбора:** самый высокий перцентиль — 59 (`brk_stop3`). У ротации
  перцентили 2–43: например, `rot_base` заработал $157 за 4 года при медиане контроля $529 (10–90%: $256…$830).
- **Доходность всех вариантов −0,8…+2,7% в год** при SPY +15,7% и равновзвешенном наборе +11,4% (оба без налога до
  продажи). Средняя загрузка капитала 30–45% — часть отставания отсюда, но и в пересчёте на вложенное H2 не догоняет.
- Средний R на сделку от −0,11 до +0,15, у всех нижняя граница блочного 90% интервала < 0.
- **Издержки решены:** 0,04R (ротация) и 0,07–0,08R (пробой) на сделку против 0,19R у H1; исполнимо 84–100% сигналов.
  Проблема H2 — не издержки и не исполнимость, а отсутствие преимущества выбора.
- Все сделки `rot_base`, `rot_mom63`, `brk_base`, `brk_stop3` (339) заново выведены `itrade review` — расхождений нет.

## Ограничения

- Валидация — 4 года (2017–2020), включая резкие развороты конца 2018 и марта 2020 — неудобные для трендовых правил.
  Это не довод «за» H2: правило раннего отказа записано заранее именно на этот период.
- Периоды разработки и валидации уже были видены на H1 (записано в пре-регистрации).
- Смещение выживших (H1 §13) завышает результат — вывод «не лучше случайного» не меняет.
- Контроль ротации откалиброван на синтетическом рынке без эффекта (20% ≥ 80, 5% ≥ 95); на реальных данных
  перцентили ротации 2–43 — смещение «против стратегии» возможно, но и при его наличии до порога 80 далеко.
