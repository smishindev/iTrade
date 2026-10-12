---
description: Оценить издержки покупки моделью издержек (комиссия, спред, конвертация валюты)
argument-hint: "<сумма USD> <цена USD> [ТИКЕР]"
allowed-tools: Bash(uv run itrade:*), Bash(uv run --directory python itrade:*), PowerShell(uv run itrade:*), PowerShell(uv run --directory python itrade:*)
---

Аргументы: `$ARGUMENTS` = сумма покупки в USD, цена акции в USD, необязательно тикер.
Если суммы нет — спроси её (типичный ежемесячный взнос владелец называет сам). Если нет цены — последний close:
`uv run --directory python itrade sql "select close from bars where ticker='<T>' order by date desc limit 1"`.

Запусти `uv run --directory python itrade costs --notional <N> --price <P> [--ticker <T>]` и объясни владельцу по-русски
(навык `cost-model`):
- стоимость одной покупки в $ и в процентах от суммы: комиссия (минимальная комиссия IBKR важна при малых суммах),
  спред и проскальзывание;
- отдельно — конвертация ₪ → $ (минимум ≈ $2 за конвертацию): она платится за пополнение, а не за каждую покупку;
- сколько целых акций получится на эту сумму и какой остаток не будет вложен;
- что все числа — допущения из `config/costs.toml`, а не счёт брокера.

Правило «издержки цикла ≤ 0,15R» и расчёты в R относились к активной торговле (план v3, архив) и здесь не применяются.
