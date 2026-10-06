---
description: Оценить издержки сделки моделью издержек и показать их в R
argument-hint: "<сумма USD> <цена USD> [ТИКЕР]"
allowed-tools: Bash(uv run itrade:*), PowerShell(uv run itrade:*)
---

Аргументы: `$ARGUMENTS` = сумма позиции в USD, цена акции в USD, необязательно тикер.
Если суммы нет — 300 (типичная позиция при риске 0,25% от ~$3 280). Если нет цены — последний close:
`uv run itrade sql "select close from bars where ticker='<T>' order by date desc limit 1"`.

Запусти `uv run itrade costs --notional <N> --price <P> [--ticker <T>]` и объясни владельцу по-русски
(навык `cost-model`):
- стоимость одной заявки и полного цикла в $ и в R (1R ≈ $8,2 при риске 0,25%);
- проходит ли правило «издержки цикла ≤ 0,15R»;
- сколько акций получится при целых долях и не превращается ли размер в 0.
