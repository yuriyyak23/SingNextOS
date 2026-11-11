# Cross-project contracts и non-goals

Сверено по локальному коду 24.10.2026. Статус — source review; новый test run
в этой документационной актуализации не выполнялся.

SingNextOS adapter уже потребляет versioned HybridCPU ExternalRuntime packages.
Точные versions читать из csproj + lock/assets/deployed output. Sibling source
edit не обновляет NuGet consumer.

Compiler/ISE semantics не квалифицируются из SingNextOS source review. Runtime
принимает semantic requests и хранит own operation/Region/provider authority;
raw CXL topology не входит в ordinary application ABI. Не создавать новый ISA
lane или CXL legality source. Direct coherent output/physical assurance требуют
своих producers. Изменение shared ABI требует согласованного scope и consumers.

## Действующий код

* [SingPlus.Runtime.csproj](../../../src/Runtime/SingPlus.Runtime/SingPlus.Runtime.csproj)
* [HybridCpu_ExecutableAdapter.csproj](../../../tools/HybridCpu_ExecutableAdapter/HybridCpu_ExecutableAdapter.csproj)
* [HybridCpuExternalOperationProvider.cs](../../../src/Runtime/SingPlus.Runtime/ExternalOperations/HybridCpuExternalOperationProvider.cs)

## Проверки при изменении поведения

Ниже существующие тестовые consumers, а не заявление о новом pass:

* [HybridCpuExternalOperationProviderTests.cs](../../../tests/SingPlus.Tests/Runtime/HybridCpuExternalOperationProviderTests.cs)

[Индекс CXL](../README.md) · [Очередь модернизации](../../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md)
