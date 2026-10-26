# CXL: граница SingNextOS и HybridCPU

Сверено по локальному SingNextOS 24.10.2026. Этот документ не утверждает статус
внешнего репозитория HybridCPU ISE: его исходники не являются источником истины
для текущего SingNextOS deployment.

`SingPlus.Runtime` и executable adapter потребляют versioned
`HybridCPU.ExternalRuntime.Contracts`; точные версии нужно читать из actual
`.csproj`, lock/assets и outputs выбранных consumers. Правка sibling source не
обновляет пакетный consumer.

SingNextOS owns region authority, admission, submission, completion, visibility,
publication и release. `HybridCpuExternalOperationProvider` переводит semantic
ExternalRuntime requests в этот lifecycle и не раскрывает CXL physical topology.
Compiler/CPU intent может описывать semantic requirement, но не создаёт provider
authority, CXL route, memory ownership либо permission на replay.

Existing local contracts already distinguish provider unavailable, closure and
effect containment. Type-2/Type-3 model execution remains repository evidence;
it does not establish physical CXL, production secure attestation, remote
no-further-use или `DirectCoherentWrite`.

При изменении shared ABI сначала установить actual owner/scope и affected package
consumers, затем проверить exact version adoption and negative lifecycle cases.
Новый lane, ISA identity или CXL-specific public handle не является допустимым
обходом этой границы.

* [HybridCpuExternalOperationProvider.cs](../../src/Runtime/SingPlus.Runtime/ExternalOperations/HybridCpuExternalOperationProvider.cs)
* [External operation contracts](../../contracts/SingPlus.Contracts/ExternalOperations.cs)
* [CXL provider contracts](../../src/Platform/SingPlus.Platform.Abstractions/CxlProviderContracts.cs)
* [Runtime project references](../../src/Runtime/SingPlus.Runtime/SingPlus.Runtime.csproj)
* [Adapter project references](../../tools/HybridCpu_ExecutableAdapter/HybridCpu_ExecutableAdapter.csproj)
* [Adapter tests](../../tests/SingPlus.Tests/Runtime/HybridCpuExternalOperationProviderTests.cs)
* [Current runtime seams](cxl-refactoring-roadmap/README.md)
