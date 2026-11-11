# Provider selection и dependency model

Сверено по локальному коду 24.10.2026. Статус — source review; новый test run
в этой документационной актуализации не выполнялся.

ComputePlanner и provider-neutral ComputeIntent/ComputePlan уже существуют.
Capability query сверяет provider ID/generation, Region uses и semantic constraints.
Planning не выдаёт permission выполнить hardware effect; admission/revalidation
остаются в existing owners. Staged path сохраняется по умолчанию.

Dependency graph, operation shape, locality и required capabilities проверяются
перед выбором. Не зашивать CXL topology, link rate или provider index в application
ABI. Изменять selection только по конкретному consumer/risk, с проверкой fallback,
unsatisfied constraints, stale provider и malformed DAG.

## Действующий код

* [ComputePlanning.cs](../../../contracts/SingPlus.Contracts/ComputePlanning.cs)
* [ComputePlanner.cs](../../../src/Runtime/SingPlus.Runtime/Compute/ComputePlanner.cs)

## Проверки при изменении поведения

Ниже существующие тестовые consumers, а не заявление о новом pass:

* [ComputePlannerTests.cs](../../../tests/SingPlus.Tests/Runtime/ComputePlannerTests.cs)

[Индекс CXL](../README.md) · [Очередь модернизации](../../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md)
