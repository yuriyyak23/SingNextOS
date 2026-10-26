# Contracts, SDK, admission, boot и tooling

Contracts — shared vocabulary, SDK/Analyzers/Generators — compilation profiles, diagnostics и generated sentries. SIP/Kernel profiles задают ограничения конкретной сборки. SingPlus.System представляет отдельный SDK project surface; полнота API устанавливается по source atlas, не по имени System.

StaticAdmission включает AdmissionVerifier, AdmissionProof, IlReader, ManagedAsyncPeQualification и ManagedAsyncFrontendContract из tools как единый compilation owner. Static proof acceptance не runtime permission. Canonical source/dependency/toolchain digest не заменяет fresh mutable owner/source checks.

Compiler/lowering evidence проверяется отдельным verifier. Mandatory unknown extensions deny. PCL является optimization/evidence track; compiler proof не current legality. Contract evolution additive; parser/schema/security dependency integrity сохраняются. Local Platform.Abstractions original model interfaces не означают external Contracts/schema package bump.

## Boot и HAL

SingPlus.Kernel содержит KernelEntryPoint, boot stage и console/HAL interfaces. SingPlus.Kernel.Hal.Host — отдельный host HAL project. SingNext.Boot.Core/Capsule, SingPlus.Boot и Platform.HybridCpu.Boot различны; нельзя объединять semantics по слову Boot.

Runtime Boot importer/admission принимает boot evidence, которое не заменяет runtime capabilities/Region ownership. Evidence ingestion и production boot qualification различны. SingPlus.Boot/Samples и dependent Boot.DebugHost исключены из текущего modernization scope; distinct SingNext.Boot требует fresh shared-graph attribution.

## Tool landscape

GuestHost, ComputeHost, DmaCopyHost, IrqEventHost и VirtualIoHost — executable hosts/consumers. Admission, LoweringEvidenceEmitter, HybridCpuQualification и SingCapQualification обслуживают соответствующие admission/evidence contours. NativeAOT fixtures и PowerShell qualification scripts имеют собственные inputs/limits. NeutralRuntime Contracts/AuthorityCore/Model и ExecutableAdapter образуют отдельные graph boundaries.

Tests/SingPlus.Tests — applicable main runtime graph; Platform.HybridCpu.Tests и adapter/provider tests проверяют свои contours, не заменяют main. Java-only исключение не блокирует независимые .NET/PowerShell/native/model consumers.

## External sibling

Разрешённый development scope: HybridCPU_ExternalRuntime, Contracts, Tests, Tools и compiler/external tools в новой папке либо HybridCPU_ISE/NonRTL. Actual Git metadata проверяется отдельно: filesystem path не Git identity.

Consumer adoption требует project/assets/global/toolchain → новый immutable package при необходимости → actual SingNextOS use/verification. Existing version не переиздаётся; global Version override не распространяется на dependencies.

Core ISA/opcode/encoding/register/memory/pipeline/retire/firmware/reset/legality независимы. Machine change требует отдельного ADR/authorization; NonRTL location не разрешает machine change.

## Источники

- [StaticAdmission project](../../../src/Admission/SingPlus.StaticAdmission/SingPlus.StaticAdmission.csproj)
- [SIP project](../../../src/Sip/SingPlus.Sip/SingPlus.Sip.csproj)
- [Kernel entry](../../../src/Kernel/SingPlus.Kernel/Boot/KernelEntryPoint.cs)
- [Boot admission](../../../src/Runtime/SingPlus.Runtime/Boot/HybridBootAuthorityAdmission.cs)
- [Полный каталог проектов](08-PROJECT-CATALOG.md)
