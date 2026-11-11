# MutationEpoch и диапазоны RegionUse

Сверено по локальному коду 24.10.2026. Статус — source review; новый test run
в этой документационной актуализации не выполнялся.

Реализованы MutationEpoch, RegionUse handles/modes/ranges и authority-mediated
admission/release. Прежнее ограничение «только whole-region conflict» устарело:
ValidateUseRequest проверяет пересечение диапазонов, разрешая совместимые или
непересекающиеся uses; есть SubrangeConflictMatrixAllowsDisjointUsesAndRejectsOverlap.
При этом separate platform mapping reservation может резервировать весь Region.

Нельзя выводить из subrange use независимую subrange ownership/MOVE authority.
Произвольная запись через managed Span не наблюдается как authority transition.
Invalidated unreleased use и quarantined damage блокируют пересекающееся admission.
SharedReadMostly и DirectCoherentWrite по-прежнему отвергаются как future-gated.
Epoch exhaustion не должен разрешать повторное использование старой authority.
Сохранять original use/borrow/process lifetime при late trace и teardown.

## Действующий код

* [Regions.cs](../../../contracts/SingPlus.Contracts/Regions.cs)
* [RegionAuthority.cs](../../../src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs)

## Проверки при изменении поведения

Ниже существующие тестовые consumers, а не заявление о новом pass:

* [RegionUseTests.cs](../../../tests/SingPlus.Tests/Ownership/RegionUseTests.cs)

[Индекс CXL](../README.md) · [Очередь модернизации](../../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md)
