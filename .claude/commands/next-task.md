---
description: Взять следующую доступную задачу дорожной карты, показать карточку и выполнить после подтверждения
argument-hint: "[--all]"
allowed-tools: Bash(python tools/roadmap.py:*), PowerShell(python tools/roadmap.py:*)
---

Работаем по `docs/roadmap/` (правила — `docs/roadmap/README.md`). Отвечай владельцу по-русски.

1. Выполни `python tools/roadmap.py next $ARGUMENTS`.
2. Если доступны задачи владельца (👤) — коротко напомни, какие и что в них сделать
   (`python tools/roadmap.py show <ID>`).
3. Для первой задачи Claude (🤖 / 🤝): `python tools/roadmap.py show <ID>`, затем в 3–5 строках:
   что изменится, в каких файлах, как поймём, что готово, оценка времени.
   Спроси через AskUserQuestion: «Выполнить» / «Другую задачу» / «Не сейчас».
4. После «Выполнить» используй `/task <ID>`-процедуру (см. `.claude/commands/task.md`).
