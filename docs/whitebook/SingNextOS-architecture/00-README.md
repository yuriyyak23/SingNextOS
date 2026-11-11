# SingNextOS — архитектурный whitebook

Дата: 2026-10-15, Europe/Moscow. Книга описывает архитектурный комплекс по исходникам, project graph и docs: subsystems, logical owners, сквозные lifetimes, tooling и направления v6. Это архитектурный снимок; actual source и единственная implementation очередь modernize/01 имеют приоритет.

Наличие интерфейса, модели, проекта или главы не означает полноценную executable подсистему. В книге отдельно отмечены bounded executing adoption, нормативные требования и открытые ограничения. Boot/Samples включены в архитектурную навигацию, но их implementation scope не меняется.

## Содержание

- [Архитектура и authority](01-ARCHITECTURE.md)
- [Подсистемы runtime](02-RUNTIME-SUBSYSTEMS.md)
- [Platform, devices и execution](03-PLATFORM-EXECUTION.md)
- [Contracts, SDK, admission, boot и tooling](04-DEVELOPER-AND-BOOT.md)
- [Сквозные сценарии и failure semantics](05-LIFECYCLES.md)
- [Направления развития и ограничения](06-DIRECTIONS.md)
- [Verification и evidence](07-VERIFICATION.md)
- [Матрица подсистем и архитектурные границы](11-SUBSYSTEM-MATRIX.md)
- [Каталог проектов](08-PROJECT-CATALOG.md)
- [Атлас исходников](09-SOURCE-ATLAS.md)
- [Термины](10-GLOSSARY.md)

Читать сначала архитектуру, затем нужную подсистему и её lifetime. Атлас обеспечивает навигацию по всем maintained C# исходникам src/contracts/sdk, каталог — по проектам src/contracts/sdk/tools/tests. Generated build outputs исключены. Список файлов не является proof completeness.

Для реализации: [единственная очередь](../../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md), [dev policy](../../SingNextOS-v6-modernize/03-DEV-VALIDATION.md). Нормативные [owners](../../SingNextOS-v6-roadmap-reworked-2026-09-23/02-AUTHORITY-OWNER-MAP.md), [invariants](../../SingNextOS-v6-roadmap-reworked-2026-09-23/04-NORMATIVE-INVARIANTS.md), [claims](../../SingNextOS-v6-roadmap-reworked-2026-09-23/05-FEATURE-GATES-AND-CLAIMS.md) сохраняют силу. Phase — target semantics, не absent-code checklist.

Gates OFF; no ProductionQualified; Java-only skipped; ISA NONE. General unknown/history/backing остаётся Partial. Trusted cold continuation и positive physical remote isolation не доказаны. Эта книга не вводит второй backlog или обязательную proof campaign.
