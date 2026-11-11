# Security и multi-host retention

Сверено по локальному коду 24.10.2026. Статус — source review; новый test run
в этой документационной актуализации не выполнялся.

CxlSecurityAuthority уже проверяет exact device/fabric/Region authority,
evidence generation и optional provider-neutral trust predicate. Model/emulated
assurance не удовлетворяет RequireHardwareAttestation. IDE/authenticated evidence
не создаёт владение Region и не равна ProductionSecure.

CxlMultiHostGate допускает ограниченные bindings, но Fence/host disappearance
лишь закрывают local admission. BeginReclaim/CompleteReclaim отказывают без
original remote isolation producer; uses/Region/charges остаются pinned и при
process teardown. Distributed writable ownership не реализовано: включение bool
его не создаёт. Старое «fence -> reclaim -> rebind» как завершённый protocol
удалено; нужен remote no-further-use producer и независимый local consumption.

## Действующий код

* [CxlSecurityAuthority.cs](../../../src/Runtime/SingPlus.Runtime/Cxl/CxlSecurityAuthority.cs)
* [CxlDeviceTrustEvidenceAdapter.cs](../../../src/Runtime/SingPlus.Runtime/Cxl/CxlDeviceTrustEvidenceAdapter.cs)
* [CxlMultiHostGate.cs](../../../src/Runtime/SingPlus.Runtime/Cxl/CxlMultiHostGate.cs)

## Проверки при изменении поведения

Ниже существующие тестовые consumers, а не заявление о новом pass:

* [CxlSecurityAndMultiHostTests.cs](../../../tests/SingPlus.Tests/Runtime/CxlSecurityAndMultiHostTests.cs)
* [CxlMultiHostLifetimeTests.cs](../../../tests/SingPlus.Tests/Runtime/CxlMultiHostLifetimeTests.cs)

[Индекс CXL](../README.md) · [Очередь модернизации](../../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md)
