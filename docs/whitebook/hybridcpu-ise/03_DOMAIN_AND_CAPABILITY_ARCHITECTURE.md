# 03. Domain And Capability Architecture

## Current SingNextOS state

Текущий `DomainRegistry` намеренно минимален: `DomainId` сопоставляется с набором `ProcessHandle`. Когда последний process уходит, kernel revokes domain capabilities, returns foreign loans, reclaims owned regions и closes channels.

Это уже полезный **OS security principal boundary**, но его нельзя отождествлять с полным HybridCPU runtime domain model.

HybridCPU различает execution, memory, I/O, lane, nested и secure owners, каждый со своими admission/policy/evidence правилами. Поэтому расширять `DomainRegistry` полями «VMCS», «EPT», «lane6 handle» или raw platform IDs было бы неправильным: он превратился бы в ещё один platform state store.

## Target model: OS domain plus opaque platform bindings

Целевая структура:

```text
SingDomain
  identity: DomainId
  lifecycle generation
  processes
  local capabilities
  local regions/channels
  optional opaque platform bindings:
      ExecutionDomainLease
      MemoryDomainLease
      IoDomainLease
      ComputeDomainLease(s)
      NestedDomainLease
      SecureDomainLease (future-gated)
```

`Lease` здесь — архитектурное понятие, а не требование к конкретному HybridCPU ABI. Реальное представление должно быть определено существующим внешним platform interface. В SingNextOS оно должно быть opaque и generation-bound.

## Domain identity versus domain authority

`DomainId` является локальной стабильной идентичностью. Он может присутствовать в manifests, capabilities, logs и SIP ownership.

Но:

```text
DomainId != platform execution handle
DomainId != address-space handle
DomainId != IOMMU domain
DomainId != SecureCompute grant
DomainId != nested virtualization authority
```

Kernel может сопоставить один `DomainId` нескольким внешним leases после успешного platform admission.

## Domain binding lifecycle

Рекомендуемая общая lifecycle-модель:

1. **Create local domain identity.** Никакой hardware authority ещё нет.
2. **Admit manifest.** Проверяются local profile, contracts, static requirements.
3. **Bind required platform domains.** Kernel запрашивает только те external authorities, которые нужны execution role и capability manifest.
4. **Publish local capabilities.** Capability появляется у SIP только после успешной platform binding policy.
5. **Run.** Каждая hardware operation повторно проверяет local capability и external live lease/grant.
6. **Revoke/terminate.** Сначала закрывается new work, затем borrows/DMA/compute attempts drain or cancel, затем external leases revoke, потом local resources reclaim.
7. **Generation advance.** Старые handles/leases cannot re-enter.

Для crash/fault path fail-closed cleanup важнее graceful success. Если внешний owner не может подтвердить корректный drain, ресурс остаётся quarantined/denied, а не передаётся новому domain как будто clean.

## Dual-authority rule

Для любой аппаратной операции требуется пересечение двух независимых решений:

```text
LocalAllowed = SingNextOS capability + process/domain generation + ownership
PlatformAllowed = HybridCPU neutral domain/grant/admission
EffectAllowed = LocalAllowed && PlatformAllowed
```

Это не дублирование. У двух систем разные обязанности:

- SingNextOS решает **кто в ОС** имеет право попросить действие.
- HybridCPU решает **может ли конкретный live hardware/runtime domain** выполнить его сейчас.

Пример DMA:

```text
OwnedRegion<byte> belongs to Domain A
+ DmaCapability(Read/Write/Map) issued to A
+ platform MemoryDomainLease(A)
+ platform IoDomainLease(A)
+ current region->IOMMU mapping grant
+ exact range/direction
= DMA request may enter staging
```

Ни `DmaCapability` без platform binding, ни platform buffer handle без SingNextOS authority не достаточны.

## Capability taxonomy evolution

Текущий набор `ResourceKind` уже хорошо выражает kernel service, memory region, channel, device, MMIO, IRQ и DMA. Для полного HybridCPU использования WhiteBook рекомендует **не добавлять raw opcodes**, а при необходимости расширять семантические resource classes.

Возможные будущие классы:

- `ExecutionDomain`
- `MemoryDomain`
- `IoDomain`
- `ComputeService`
- `MatrixTile`
- `DmaStreamCompute`
- `AcceleratorCommand`
- `VirtualMachine`
- `NestedDomain`
- `PlatformEvidence`
- `ConfidentialDomain`

Это candidates, а не обязательные enum values текущей итерации. Перед кодированием нужно определить реальный platform bridge и минимальный набор use cases.

## Rights must stay semantic

Rights должны выражать OS operation, а не ISA bit:

Хорошо:

- `Read`
- `Write`
- `Map`
- `Signal`
- `Configure`
- `Transfer`
- `Delegate`
- потенциально `Execute`, `Submit`, `Inspect`, `Checkpoint`, если появится реальная необходимость.

Плохо:

- `Lane6`
- `VMXON`
- `VMREAD`
- `ACCEL_SUBMIT_OPCODE`
- `MTILE_MACC_BIT`

Physical implementation and opcode selection belong to external runtime/compiler.

## Delegation

Текущая SingNextOS delegation already has strong properties:

- source must be valid for delegator domain;
- source must include `Delegate`;
- delegated rights are non-empty subset;
- target generation is bound into the new capability.

При появлении platform capabilities delegation must not blindly clone an external opaque handle. Kernel should either:

1. ask the platform owner to derive a subordinate grant, or
2. keep the external owner binding at the kernel and mint a narrower local capability that is revalidated through the original external domain policy on use.

Which model is possible depends on external HybridCPU ABI. The second model is safer as a default because no platform token escapes kernel authority.

## Revocation and domain epochs

SingNextOS capability revocation epoch is already valuable. Platform bindings should add a separate platform generation/epoch rather than overload local capability epoch.

Recommended validation tuple:

```text
LocalCapabilityIdentity
LocalSubjectDomain
LocalProcessGeneration
LocalRevocationEpoch
PlatformLeaseIdentity (opaque)
PlatformDomainGeneration/Epoch (opaque or locally mirrored)
Operation-specific grant generation
```

A mismatch in any live component denies operation.

## Nested domains

HybridCPU nested architecture is neutral child-domain composition, not mutable shadow VMCS authority. SingNextOS should mirror this model.

A future nested API should conceptually be:

```text
ParentDomainCapability
  -> kernel policy filters requested resources
  -> platform creates/adopts child domain intent
  -> child gets bounded execution/memory/I/O grants
  -> optional legacy VMX projection is layered afterward
```

Parent cannot delegate more authority than it owns. Child cannot infer host/root evidence. Shadow compatibility state cannot mint resources.

This model can support:

- application sandboxes;
- compatibility containers;
- nested OS guests;
- service enclaves;
- test/fault-injection environments.

It does not require exposing VMX to ordinary applications.

## Process manifests and platform requirements

`SingProcessManifestV1` already carries role, memory profile, resource limits, capabilities and contract identities. A later manifest version may need declarative platform requirements, but these should remain semantic and optional.

Possible shape:

```text
PlatformExecutionProfile:
  Ordinary | ComputeIntensive | DeviceService | VirtualMachine | Confidential
RequiredPlatformServices:
  MatrixTile, BulkCompute, AcceleratorControl, EvidenceRead
```

It should **not** carry:

- lane masks;
- raw opcode allowlists;
- VMCS fields;
- host pointers;
- raw IOMMU IDs;
- external grant tokens.

Manifest is requested configuration and admission input, not live platform authority.

## Kernel authority surface

Public SIP code should never get `CapabilityAuthority` or platform authority managers. Instead:

```text
public .NET-like API
 -> typed SIP service
 -> kernel validates local authority
 -> kernel/platform bridge invokes external neutral owner
```

The bridge itself belongs in the privileged kernel/platform integration layer and should be impossible to reference from `Sip`/`Driver` profiles except through generated typed contracts.

## Domain architecture decision

Do not turn current `DomainRegistry` into a giant HybridCPU mirror. Preserve it as local OS identity/lifecycle authority and introduce a separate, narrow **PlatformDomainBindingAuthority** behind the kernel boundary when a real external contract becomes available.

This separation is critical: it lets SingNextOS stay architecture-coherent even when HybridCPU changes internal descriptors or physical topology, while still allowing the OS to exploit the full runtime domain model.