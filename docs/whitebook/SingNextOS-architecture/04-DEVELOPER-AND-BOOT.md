# Contracts, developer tooling and boot

## Compilation boundaries

[`SingPlus.Contracts`](../../../contracts/SingPlus.Contracts/SingPlus.Contracts.csproj) provides shared protocol vocabulary, distinct from live runtime permission. [Kernel SDK](../../../sdk/SingPlus.Kernel.Sdk/SingPlus.Kernel.Sdk.csproj), [SIP SDK](../../../sdk/SingPlus.Sip.Sdk/SingPlus.Sip.Sdk.csproj), [system API](../../../sdk/SingPlus.System/SingPlus.System.csproj), [analyzers](../../../sdk/SingPlus.Analyzers/SingPlus.Analyzers.csproj), and [generators](../../../sdk/SingPlus.Generators/SingPlus.Generators.csproj) form source-facing profiles and generated sentries.

[`Directory.Build.props`](../../../Directory.Build.props) sets C# 13, deterministic compilation, and profile-conditional analyzers/generators; preview syntax is opt-in. The [pinned SDK](../../../global.json) is `11.0.100-rc.2.26504.105` with `rollForward: disable`, not the earlier `rc.1`. [Runtime project](../../../src/Runtime/SingPlus.Runtime/SingPlus.Runtime.csproj) targets `net11.0` and depends on `HybridCPU.ExternalRuntime.Contracts` `[1.36.4]`, not the earlier `[1.35.0]`.

## Static admission and compiler evidence

[`SingPlus.StaticAdmission.csproj`](../../../src/Admission/SingPlus.StaticAdmission/SingPlus.StaticAdmission.csproj) explicitly compiles five linked files from `tools/SingPlus.Admission`: `AdmissionVerifier.cs`, `AdmissionProof.cs`, `IlReader.cs`, `ManagedAsyncPeQualification.cs`, and `ManagedAsyncFrontendContract.cs`. This is a single compilation owner for those sources, not five different runtime authorities.

[`V6CompilerLoweringEvidenceVerifier`](../../../src/Runtime/SingPlus.Runtime/V6/V6CompilerLoweringEvidenceVerifier.cs) and [semantic-admission code](../../../src/Runtime/SingPlus.Runtime/VNext/SemanticAdmissionSentry.cs) deal with compiler/semantic evidence. Evidence acceptance cannot grant runtime rights or override a stale provider incarnation. Unknown mandatory extension/semantic requirement must deny the corresponding claim.

## Boot and HAL are distinct from OS-wide qualification

- [KernelEntryPoint](../../../src/Kernel/SingPlus.Kernel/Boot/KernelEntryPoint.cs): entry stage orchestration; staged `Run` prints **host-debug runtime ready**.
- [Host HAL](../../../src/Kernel/Hal/SingPlus.Kernel.Hal.Host/HostKernelConsole.cs): host console implementation.
- [SingNext.Boot.Core](../../../src/Kernel/Boot/SingNext.Boot.Core/SingNext.Boot.Core.csproj): independent core boot-state implementation.
- [SingNext.Boot.Capsule](../../../src/Kernel/Boot/SingNext.Boot.Capsule/SingNext.Boot.Capsule.csproj): capsule-oriented boot components.
- [SingPlus.Boot](../../../src/Kernel/Boot/SingPlus.Boot/SingPlus.Boot.csproj): debug/demo/bootstrap route with separate samples.
- [HybridCPU boot adapter](../../../src/Platform/SingPlus.Platform.HybridCpu.Boot/SingPlus.Platform.HybridCpu.Boot.csproj): separate platform-boot integration.
- [Boot information importer](../../../src/Runtime/SingPlus.Runtime/Boot/HybridBootInfoImporter.cs): consumes boot evidence, which is not a runtime capability.

A boot project, stage, capsule, or sample is not evidence that all runtime subsystems have been loaded or isolated in a qualified physical image.

## Executable hosts, tests and qualification tools

The current tree includes [GuestHost](../../../tools/SingPlus.GuestHost/SingPlus.GuestHost.csproj), [ComputeHost](../../../tools/SingPlus.ComputeHost/SingPlus.ComputeHost.csproj), [DMA copy host](../../../tools/SingPlus.DmaCopyHost/SingPlus.DmaCopyHost.csproj), [IRQ event host](../../../tools/SingPlus.IrqEventHost/SingPlus.IrqEventHost.csproj), [Virtual-I/O host](../../../tools/SingPlus.VirtualIoHost/SingPlus.VirtualIoHost.csproj), [admission tooling](../../../tools/SingPlus.Admission/SingPlus.Admission.csproj), and [HybridCPU qualification](../../../tools/SingPlus.HybridCpuQualification/SingPlus.HybridCpuQualification.csproj).

[`tests/SingPlus.Tests`](../../../tests/SingPlus.Tests/SingPlus.Tests.csproj) is the central managed suite; [platform tests](../../../tests/SingPlus.Platform.HybridCpu.Tests/SingPlus.Platform.HybridCpu.Tests.csproj) and [adapter tests](../../../tools/HybridCpu_ExecutableAdapter.Tests/HybridCpu_ExecutableAdapter.Tests.csproj) target narrower contours. A passing provider suite cannot be represented as a full runtime, physical, or security qualification.

## Package discipline

A sibling source edit is not a package upgrade for consumers. Check exact project references, resolved assets, lockfiles and provider schema. If an external dependency changes, publish/adopt a new immutable package tuple and re-run affected consumers. The source tree does not, by itself, authorize changes to HybridCPU ISA, opcode encoding or machine legality.
