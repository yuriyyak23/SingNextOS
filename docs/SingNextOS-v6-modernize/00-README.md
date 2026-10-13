# SingNextOS v6 — рабочий план модернизации

Актуализирован 2026-10-13. Цель — завершать связные implementation блоки реальных consumers с сохранением capability/lifecycle invariants.

Читать по порядку:

1. [Состояние и очередь](01-STATE-AND-BACKLOG.md) — текущие exits и открытые задачи.
2. [Порядок работы](02-IMPLEMENTATION-ORDER.md) — выбор consumer и границы блока.
3. [Dev validation](03-DEV-VALIDATION.md) — необходимая проверка по риску изменения.

[Промпт продолжения](04-ROOT-CONTINUATION-PROMPT.md) — handoff на 2026-10-13: текущий DMA original payload retention блок pending и remaining producer/adoption/backing/recovery scope. Он не вторая очередь; source/assets и файл01 authoritative.

Затем читать только применимые owners/contracts/phase/ADR из [нормативного пакета](../SingNextOS-v6-roadmap-reworked-2026-09-23/00-README.md). Phase описывает target semantics, а не текущий список отсутствующего кода. Исторические «next», counts и commands относятся к inputs соответствующего отчёта; текущую очередь задаёт файл 01.

Reports читать по выбранному consumer, без загрузки всей истории. План хранит очередь и навигацию; команды, counts и failed history остаются в соответствующих artifacts. Source fix или focused pass при pending integration verification не означает закрытый блок. Для documentation-only задачи достаточно review по файлу 03; runtime очередь и оформление runtime records при этом не запускаются.

SingPlus.Boot и его Samples, Java-only consumers исключены. Разрешённый sibling scope описан в [cross-project delta](../SingNextOS-v6-roadmap-reworked-2026-09-23/38-CROSS-PROJECT-DELTA.md): ExternalRuntime/Contracts/Tests/Tools и external tools в NonRTL; ISA NONE. Новые v6 gates OFF до qualification exact deployment.

[Qualification](../SingNextOS-v6-roadmap-reworked-2026-09-23/45-PROOF-EVIDENCE-INDEX.md) — отдельный контур. Полная история, proof matrix и physical/production campaign не требуются для каждой dev итерации.
