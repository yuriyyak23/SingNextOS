# CXL: актуальная карта SingNextOS

Сверено по локальному исходному дереву 24.10.2026. Этот каталог содержит только
действующие CXL contracts, runtime seams и boot boundaries. Старые линейные
roadmap, baseline SHA, package pins и прошлые числовые результаты удалены: они
не описывают текущий dirty worktree и не являются квалификацией нового запуска.

* [Runtime CXL seams](cxl-refactoring-roadmap/README.md) — существующие owners,
  providers, Type-3/Type-2, lifecycle, security и remaining gates.
* [CXL boot: current contracts](BOOT-CXL-CURRENT.md) — DirectBootCapsule,
  HybridBootInfo, QEMU EFI lab и фактические границы boot integration.
* [Cross-project boundary](CROSS-PROJECT-CXL-BOUNDARY.md) — граница между
  SingNextOS и HybridCPU ExternalRuntime без устаревших планов ISE.
* [Черновой whitebook](whitebook/README.md) — общее описание CXL, QEMU,
  BootCapsule, SingBoot и runtime authority во время перестройки проекта.
* [QEMU CXL laboratory](../QEMU_CXL/README.md) — Windows/QEMU/EDK2 profile,
  native EFI evidence и commands.

## Правило доказательств

Source presence, model tests, QEMU device creation, native EFI diagnostics,
runtime authority admission и kernel handoff — разные уровни доказательства.
Ни один из них не повышает другой автоматически. Следующий implementation block
выбирается из [единственной v6 очереди](../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md)
по actual consumer и разрешённому scope.
