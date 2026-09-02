# SingNextOS × HybridCPU-v2 ISE Architecture WhiteBook

**Status:** architecture audit and development-direction record  
**SingNextOS baseline:** `3415327ebb822387eca18aef51251c9f658342a7`  
**HybridCPU-v2 baseline:** `38bf0614d8a58e2543b4a956ccc23bb22e1a8170`

## Purpose

Этот набор документов фиксирует аудит соответствия текущего направления SingNextOS философии, техникам и фактическим границам HybridCPU-v2 / ISE, а также формулирует архитектуру новой ОС, которая максимально использует возможности ISE CPU без переноса аппаратной runtime-authority в программные compatibility-слои.

Главный вывод: SingNextOS уже совпадает с HybridCPU по наиболее важным принципам — fail-closed admission, typed capabilities, generation/revocation, ownership transfer, deterministic manifests, separation of metadata from authority и privileged kernel authority. Ключевое требуемое уточнение — воспринимать HybridCPU virtualization/domains/memory/I/O/lane/SecureCompute как **neutral external platform authority**, а не как набор VMX-инструкций, которыми ядро должно управлять напрямую.

## Source corpus

Аудит выполнен по фактическим `master`-состояниям двух репозиториев и по прежней приложенной Белой книге Singularity+.

HybridCPU-v2 источники:

- `Documentation/WhiteBook/**`
- `Documentation/Virtualization WhiteBook/**`
- `Documentation/SecureCompute WhiteBook/**`
- `Documentation/Stream WhiteBook/**`
- `Documentation/ISE_Instructions_By_Lane_CodeConfirmed.md`

SingNextOS источники:

- `contracts/SingPlus.Contracts/**`
- `src/Runtime/SingPlus.Runtime/**`
- `sdk/SingPlus.Analyzers/**`
- `sdk/SingPlus.Generators/**`
- `tools/SingPlus.Admission/**`
- `docs/external-requirements/**`

Исторический источник идей: приложенная пользователем 7-страничная Белая книга Singularity+, где исходное видение объединяло ownership, SIP, capability security, firmware-level hypervisor, manifest-driven drivers, heterogeneous compute и .NET-подобный API. Этот документ используется как vision-source, но не как описание текущего состояния кода.

## Status vocabulary

Чтобы не повторять ошибку «наличие API/опкода = готовая функция», WhiteBook использует пять статусов:

- **SingNextOS implemented** — уже есть в текущем SingNextOS и проверяется локальными гарантиями.
- **ISE code-confirmed** — подтверждено текущим HybridCPU-v2 ISE runtime/docs, но ещё не означает доступный SingNextOS platform ABI.
- **Integration candidate** — архитектурно подходит SingNextOS и должно подключаться только через локальный typed black-box bridge.
- **Projection/evidence only** — может описывать или измерять состояние, но не является authority и не разрешает эффект.
- **Future-gated / denied** — текущий HybridCPU прямо не предоставляет production-positive contour; SingNextOS обязан fail closed и не эмулировать наличие гарантии.

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

## Normative architectural rules

1. **Kernel authority remains kernel authority.** SingNextOS kernel is the privileged OS policy owner. It is not modeled as an untrusted SIP.
2. **HybridCPU is a black-box platform.** SingNextOS does not fork or reimplement HybridCPU ISE, compiler, backend, loader, VMX, SecureCompute or ISA internals.
3. **Neutral owner first.** A VMX/VMCS name, opcode, lane number, descriptor, telemetry record or evidence object never grants OS authority by itself.
4. **Two independent approvals for hardware effects.** A hardware effect requires both SingNextOS kernel capability/policy and a positive external platform-domain admission/grant. Either side may deny.
5. **Ownership before sharing.** Large data crosses SIP boundaries through generation-bound ownership transfer or revocable borrow. Shared mutable memory remains an explicit, bounded exception.
6. **Admission is not publication.** Decode, metadata validation, capability admission, backend result, completion and retire/commit are distinct stages. A positive earlier stage never implies a later one.
7. **No physical topology in public API.** Applications never select HybridCPU lanes or raw opcodes. They request typed compute/system capabilities; external runtime owns legality and placement.
8. **SecureCompute is advertised only when real.** Until external HybridCPU exposes a named production-positive secure-domain activation path, SingNextOS reports confidential execution as unavailable.
9. **Evidence is not authority.** Replay, telemetry, measurement and attestation projections may feed diagnostics or policy decisions, but they cannot mint capabilities, transfer ownership or publish effects.
10. **Compatibility is downstream.** VMX/legacy guest compatibility may be added as an isolated service over neutral domain authority; it cannot become the substrate of SingNextOS isolation.

## Scope boundary

Эта WhiteBook меняет направление архитектуры и требования к будущему bridge-слою, но **не изменяет HybridCPU-v2** и не утверждает наличие ещё не опубликованного внешнего ABI. Любая конкретная привязка к ISE остаётся `External Blocked`, пока существующий HybridCPU platform interface не будет подтверждён интеграционным тестом.
