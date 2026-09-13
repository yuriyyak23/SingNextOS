# 02. Philosophy And Authority Alignment

## The common architectural idea

Самое глубокое совпадение SingNextOS и HybridCPU-v2 находится не в синтаксисе API и не в конкретных инструкциях. Оба проекта постепенно сходятся к одной модели:

> **данные, metadata, carrier, descriptor, token и evidence не являются authority; authority принадлежит именованному runtime owner, который проверяет live context и только затем разрешает эффект.**

Для SingNextOS это должно стать не просто security guideline, а основным правилом всей системы — от SIP message до DMA, accelerator submit, nested domain и будущего confidential execution.

## Four kinds of objects

WhiteBook предлагает различать четыре класса объектов во всех API:

### 1. Intent

Intent описывает, что caller хотел сделать.

Примеры:

- SIP message id + typed payload;
- compute operation descriptor;
- request to map an owned region for DMA;
- nested-domain creation request;
- compiler-produced instruction carrier.

Intent никогда не является разрешением.

### 2. Authority

Authority — это live runtime capability/grant/owner-bound lease, который может быть проверен в момент эффекта.

В SingNextOS уже есть хорошая база:

- `CapabilityDescriptorV1`: issuer, subject domain, rights, generation, revocation epoch;
- `RegionHandle`: RegionId + generation;
- `BorrowLeaseHandle`: region generation + independent borrow generation;
- process generation;
- kernel-owned mint/delegate/revoke paths.

Для ISE-интеграции понадобятся внешние opaque authority objects — platform domain lease, memory binding, DMA grant, compute session — но они не должны заменять локальные kernel capabilities.

### 3. Evidence

Evidence объясняет, подтверждает или измеряет решение, но не разрешает его.

Примеры HybridCPU:

- legality certificate;
- replay evidence;
- telemetry;
- measurement descriptor;
- VMX projection;
- completion projection;
- support-status row.

Примеры SingNextOS:

- manifest digest;
- protocol digest;
- admission proof;
- generated response metadata.

Архитектурный запрет: нельзя преобразовывать evidence в authority простым наличием объекта или совпадением digest.

### 4. Publication

Publication — момент, когда эффект становится архитектурно/системно видимым:

- SIP state/sequence advancement;
- ownership transfer becoming effective;
- DMA memory commit;
- accelerator result commit;
- VM completion;
- process state transition;
- secure-domain output publication.

Publication должна иметь отдельный owner/fence и не выводиться из «backend вернул success».

## The publication ladder

HybridCPU особенно полезен SingNextOS своей дисциплиной разделения стадий. Для hardware-backed операций рекомендуется универсальная лестница:

```text
request/intention
  -> local SingNextOS contract validation
  -> local capability/ownership validation
  -> external platform-domain admission
  -> external operation-specific grant/admission
  -> execution/staging
  -> completion object/evidence
  -> publication/commit authorization
  -> visible state update
```

Некоторые операции заканчиваются раньше. Read-only evidence projection, например, не требует backend effect или commit. Но нельзя пропускать ступень просто потому, что операция «обычно безопасна».

## Mapping HybridCPU boundaries to SingNextOS

| HybridCPU principle | SingNextOS equivalent | Required evolution |
|---|---|---|
| neutral runtime owner | privileged kernel authorities | add platform owner bindings, not VMX managers |
| `DomainRuntimeContext` | `ProcessHandle` + `DomainId` + manifest | add opaque platform domain lease + epoch |
| typed capability requirement | `CapabilityRequirementV1` | extend resource classes for platform domains/compute/evidence |
| evidence visibility policy | admission proof / generated metadata are currently local evidence | add explicit evidence projection capability, never ambient telemetry |
| execution domain | process scheduling/lifecycle | separate OS process identity from external execution-domain binding |
| memory domain | `RegionAuthority` | bind regions to external address-space/IOMMU authority |
| I/O domain | MMIO/IRQ/DMA capability kinds | introduce explicit external I/O-domain lease and DMA binding |
| nested domain | not yet modeled | add kernel-mediated child domain composition contract |
| secure domain | not yet modeled | optional external secure-domain capability, production-positive only |
| completion/retire fences | SIP validates before queue/state mutation | preserve; use staged commit for hardware services |

## Why the kernel is not a SIP

HybridCPU neutral runtime model must not be interpreted as «все взаимно недоверенные компоненты одинаковы». SingNextOS kernel является privileged policy root for OS objects:

- creates/adopts processes;
- owns local capability authority;
- owns region authority;
- owns channel lifecycle;
- decides which external platform grants may be projected to a process.

System services remain SIPs because they are replaceable, least-privileged principals. Kernel is the authority that creates and validates those principals.

This keeps the trusted computing base explicit and avoids a circular model where a service would need permission from itself to mint the capability used to call itself.

## Compatibility projection is always downstream

HybridCPU VMX refactor establishes a highly valuable rule for SingNextOS legacy support:

```text
neutral fact / neutral operation
  -> explicit compatibility projection
```

never:

```text
VMX/legacy state
  -> trusted OS fact
```

Therefore future Windows/Linux/UEFI/VMX compatibility should be implemented as a constrained service over kernel/platform domain authority. It may expose VMCS-shaped fields, exit reasons or legacy device models, but those objects remain projection vocabulary.

## Compiler, generator and analyzer role

SingNextOS source generators and analyzers should continue to become stricter, because they reduce malformed intent. But they must never become the sole security boundary.

Correct relationship:

```text
analyzer/generator/admission verifier
  -> prove producer intent and static shape
  -> emit stable metadata/digest
  -> runtime independently checks live authority
```

This exactly matches HybridCPU's compiler/runtime separation: deterministic producer metadata is useful, but live capability/domain/root/epoch decisions stay runtime-owned.

## Fail-closed taxonomy

To keep failures inspectable, SingNextOS should eventually preserve a typed rejection taxonomy similar in spirit to HybridCPU scheduler/runtime rejections. At minimum platform bridge failures should distinguish:

- no platform support;
- domain not bound;
- stale domain generation;
- missing local capability;
- external grant denied;
- wrong owner;
- wrong region generation;
- unsupported request shape;
- evidence not visible;
- backend unavailable;
- staged result cancelled;
- commit/publication denied;
- restore/replay epoch stale.

One generic `PlatformFailure` would hide security boundaries and make conformance testing weaker.

## Non-inversion rules

The following implications are explicitly forbidden:

```text
opcode exists                  != operation is OS-authorized
compiler emitted carrier       != runtime admitted it
platform admitted request      != local kernel capability exists
local kernel capability exists != external platform grant exists
backend produced result        != result may be published
telemetry says success         != authority exists
VMX projection exists          != VMX owns state
measurement exists             != secure execution is active
buffer ID exists               != DMA/borrow authority exists
```

These rules are the core philosophical alignment between SingNextOS and HybridCPU-v2 and should govern all future subsystems.