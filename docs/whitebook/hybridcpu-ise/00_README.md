# SingNextOS × HybridCPU-v2 ISE Architecture WhiteBook

**Status:** architecture audit and development-direction record  
**SingNextOS baseline:** `af791aba4e25615cef09b3933f34efca62296304`  
**Baseline note:** this is the `master` state after the local Platform Authority Bridge + host provider merge.  
**HybridCPU-v2 baseline:** `38bf0614d8a58e2543b4a956ccc23bb22e1a8170`

## Purpose

Этот набор документов фиксирует аудит соответствия текущего SingNextOS философии, техникам и фактическим границам HybridCPU-v2 / ISE, а также задаёт архитектурное направление Singularity Plus (Sing+) без переноса hardware/runtime authority в compatibility-слои.

Главный вывод остаётся прежним: SingNextOS совпадает с HybridCPU по наиболее важным принципам — fail-closed admission, typed capabilities, generation/revocation, ownership transfer, deterministic manifests, separation of metadata from authority и privileged kernel authority. После baseline `af791aba...` в репозитории также существует **локальный Platform Authority Bridge v1** с host provider для neutral domain binding и direct owned-region mapping. Это local architecture proof, а не доказательство существующего HybridCPU IOMMU/DMA/coherence ABI.

Нативная application/API формула Sing+ теперь зафиксирована явно:

```text
.NET-like ergonomic public API
        ↓
generated typed SIP contracts
        ↓
capabilities + ownership IPC
        ↓
minimal privileged kernel authority
        ↓
Platform Authority Bridge
        ↓
HybridCPU domain/device mechanisms
```

Ключевой API-принцип:

> **source familiarity, not binary compatibility.**

Win32/POSIX/Wine могут существовать только как future downstream compatibility/personality services. Они не определяют native kernel, IPC, filesystem/network/process или GUI architecture.

## Source corpus

Аудит и последующая актуализация выполнены по текущему `master` SingNextOS, по существующей HybridCPU whitebook corpus и по приложенным архитектурным материалам о native `.NET-like` API, capability/ownership IPC и стандартной UI/GUI contract subsystem.

HybridCPU-v2 sources:

- `Documentation/WhiteBook/**`
- `Documentation/Virtualization WhiteBook/**`
- `Documentation/SecureCompute WhiteBook/**`
- `Documentation/Stream WhiteBook/**`
- `Documentation/ISE_Instructions_By_Lane_CodeConfirmed.md`

SingNextOS sources:

- `contracts/SingPlus.Contracts/**`
- `src/Sip/SingPlus.Sip/**`
- `src/Runtime/SingPlus.Runtime/**`
- `src/Platform/SingPlus.Platform.Abstractions/**`
- `src/Platform/SingPlus.Platform.Host/**`
- `sdk/SingPlus.Analyzers/**`
- `sdk/SingPlus.Generators/**`
- `tools/SingPlus.Admission/**`
- `tests/SingPlus.Tests/Platform/**`
- `docs/external-requirements/**`

Исторические и приложенные архитектурные материалы используются как **vision source**, но не как implementation evidence. Особенно это важно для GUI contracts, general `SharedGrant`, DMA/GPU presentation и high-level File/Network/UI façades: если соответствующего кода нет в baseline, WhiteBook маркирует их как target/future.

## Status vocabulary

WhiteBook использует следующие статусы:

- **SingNextOS implemented** — существует в текущем SingNextOS и подтверждается локальными runtime/contracts/tests.
- **SingNextOS local/host-backed** — локальный abstraction/bridge и host provider реализованы, но это не доказывает hardware-backed HybridCPU integration.
- **ISE code-confirmed** — подтверждено текущим HybridCPU-v2 ISE runtime/docs, но ещё не означает доступный SingNextOS platform ABI.
- **Integration candidate / target architecture** — семантика определена и согласована с текущими primitives, но соответствующий service/provider ещё не реализован.
- **Projection/evidence only** — объект описывает/измеряет состояние, но не является authority.
- **Future-gated / external-blocked** — production-positive external contour отсутствует или не подтверждён; SingNextOS обязан fail closed.

## Reading order

1. [`01_EXECUTIVE_AUDIT.md`](01_EXECUTIVE_AUDIT.md)
2. [`02_PHILOSOPHY_AND_AUTHORITY_ALIGNMENT.md`](02_PHILOSOPHY_AND_AUTHORITY_ALIGNMENT.md)
3. [`03_DOMAIN_AND_CAPABILITY_ARCHITECTURE.md`](03_DOMAIN_AND_CAPABILITY_ARCHITECTURE.md)
4. [`04_MEMORY_OWNERSHIP_DMA_SECURE_IO.md`](04_MEMORY_OWNERSHIP_DMA_SECURE_IO.md)
5. [`05_ISE_FULL_FEATURE_UTILIZATION.md`](05_ISE_FULL_FEATURE_UTILIZATION.md)
6. [`06_VIRTUALIZATION_AND_SECURE_COMPUTE.md`](06_VIRTUALIZATION_AND_SECURE_COMPUTE.md)
7. [`07_PLATFORM_BRIDGE_AND_EXTERNAL_CONTRACTS.md`](07_PLATFORM_BRIDGE_AND_EXTERNAL_CONTRACTS.md)
8. [`08_DELTA_FROM_PREVIOUS_WHITEBOOK.md`](08_DELTA_FROM_PREVIOUS_WHITEBOOK.md)
9. [`09_DEVELOPMENT_DIRECTION.md`](09_DEVELOPMENT_DIRECTION.md)
10. [`10_SOURCE_AND_TRACEABILITY.md`](10_SOURCE_AND_TRACEABILITY.md)
11. [`11_EXTERNAL_AUDIT_CLAIM_REVIEW.md`](11_EXTERNAL_AUDIT_CLAIM_REVIEW.md)
12. [`12_NATIVE_API_AND_UI_CONTRACTS.md`](12_NATIVE_API_AND_UI_CONTRACTS.md)

Для вопросов о native application API, desktop environment ABI, UI security и zero-copy presentation читать `12_NATIVE_API_AND_UI_CONTRACTS.md` вместе с chapters 02–04 и 07.

## Normative architectural rules

1. **Kernel authority remains kernel authority.** SingNextOS kernel is the privileged OS policy owner. It is not modeled as an ordinary untrusted/replaceable SIP.
2. **HybridCPU is a black-box platform.** SingNextOS does not fork or reimplement HybridCPU ISE, compiler, backend, loader, VMX, SecureCompute or ISA internals.
3. **Neutral owner first.** A VMX/VMCS name, opcode, lane number, descriptor, telemetry record, path, PID, device name or evidence object never grants OS authority by itself.
4. **Two independent approvals for hardware effects.** A hardware effect requires both SingNextOS local capability/ownership policy and a positive external platform admission/grant. Either side may deny.
5. **Ownership before sharing.** Large mutable data crosses SIP boundaries through generation-bound ownership transfer or bounded borrow/grant. Shared mutable memory remains an explicit exception.
6. **Admission is not publication.** Decode, metadata validation, capability admission, backend result, completion and retire/commit are distinct stages.
7. **No physical topology in public API.** Applications do not select HybridCPU lanes or raw opcodes. They request typed semantic services.
8. **High-level OS API is typed-service-first.** Filesystem, networking, process management, GUI and comparable facilities belong primarily in typed SIP contracts, not a giant syscall surface.
9. **Source familiarity, not binary compatibility.** Native Sing+ APIs may look idiomatic to .NET developers without inheriting Win32/POSIX/CoreCLR OS assumptions.
10. **UI is a standard contract subsystem.** Display, compositor, window management, input, clipboard, font/text, accessibility, notifications and shell are separable service roles; KDE/GNOME/Plasma-like implementations do not define the Sing+ ABI.
11. **GUI uses the same capability ledger.** Screen capture, global input, clipboard, display configuration, global shortcuts and foreign-window authority require explicit scoped capabilities; no parallel GUI token system is introduced.
12. **Surface presentation follows ownership semantics.** `Present` transfers or bounds access to a surface buffer until completion; unrestricted shared writable framebuffer is not the base IPC model.
13. **No global-coherence assumption.** Software correctness must not depend on universal CPU/GPU/device coherent shared mutable state. Concrete coherence/remap/DMA claims require platform evidence.
14. **SecureCompute is advertised only when real.** Until external HybridCPU exposes a production-positive secure-domain activation path, SingNextOS reports confidential execution as unavailable.
15. **Evidence is not authority.** Replay, telemetry, measurement and attestation projections cannot mint capabilities, transfer ownership or publish effects.
16. **Compatibility is downstream.** Win32/POSIX/Wine/VMX/legacy guest compatibility may be isolated behind compatibility/personality SIPs over native services; it cannot become the substrate of native Sing+ isolation or GUI/API design.

## Current Platform Authority Bridge boundary

At the SingNextOS baseline, the local bridge already provides two explicit feature bits:

```text
NeutralDomainBinding
DirectOwnedRegionMapping
```

and preserves separate local/provider identities and generations. `RuntimeKernel.MapPlatformOwnedRegion` validates the local `MemoryRegion` capability and exact region ownership before forwarding to a provider. The host provider exists for deterministic tests.

Not proven by that implementation:

- a HybridCPU provider;
- actual page remap/IOMMU binding;
- DMA engine semantics;
- global/device cache coherence;
- GPU/display presentation;
- Matrix/DSC/L7 provider integration;
- virtualization/evidence/SecureCompute provider support.

Those remain governed by the external requirements and status gates in this WhiteBook.

## Scope boundary

This WhiteBook documents architecture and development direction. It does **not** modify HybridCPU-v2 and does not infer missing external ABI from internal HybridCPU types or opcodes. Likewise, the UI chapter specifies target contracts without claiming that GUI services already exist in SingNextOS source.

Every present-tense claim must trace to current SingNextOS source/tests or to a current production-positive external contract. Otherwise the correct classification is target, unproven, external-blocked or future-gated.