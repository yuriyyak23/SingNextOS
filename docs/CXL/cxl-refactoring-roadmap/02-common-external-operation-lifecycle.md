# Общий external-operation lifecycle

Сверено по локальному коду 24.10.2026. Статус — source review; новый test run
в этой документационной актуализации не выполнялся.

ExternalOperationAuthority уже владеет Prepared -> Admitted -> Submitted ->
DeviceComplete -> Visible -> Published -> Released. Cancellation, provider loss
и release — разные операции. Completion не заменяет visibility/publication.

ReleaseUsesAtomically выполняется до terminal Released. Ambiguous publication
или in-flight callback запрещают release. ProviderUnavailable, timeout, NotFound
и request cancellation сами не доказывают provider closure/no-further-use.
При изменении сохранять original operation/binding/use correlations, final source
revalidation, atomic local consequence и cleanup после отказа publication.
Не вводить отдельный CXL lifecycle.

## Действующий код

* [ExternalOperations.cs](../../../contracts/SingPlus.Contracts/ExternalOperations.cs)
* [ExternalOperationAuthority.cs](../../../src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs)
* [RuntimeKernel.ExternalOperations.cs](../../../src/Runtime/SingPlus.Runtime/ExternalOperations/RuntimeKernel.ExternalOperations.cs)

## Проверки при изменении поведения

Ниже существующие тестовые consumers, а не заявление о новом pass:

* [ExternalOperationLifecycleTests.cs](../../../tests/SingPlus.Tests/Runtime/ExternalOperationLifecycleTests.cs)

[Индекс CXL](../README.md) · [Очередь модернизации](../../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md)
