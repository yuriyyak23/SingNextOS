# 01. Executive Audit

## Verdict

Текущее направление SingNextOS **архитектурно совместимо** с наиболее сильными идеями HybridCPU-v2, особенно после итераций B1–C4: capability authority отделена от клиентских токенов, region ownership имеет generation-bound transfer и revocable borrow, SIP protocol metadata генерируется детерминированно и fail-closed, runtime проверяет payload shape до state mutation, а KernelNoHeap/admission proofs отделяют trusted kernel contour от обычного managed-кода.

Однако для «бескомпромиссной ОС» недостаточно просто использовать больше опкодов HybridCPU. Наиболее ценная часть ISE — не VMX-инструкции сами по себе, а **модель authority**: neutral runtime domains, explicit admission, typed grants, evidence visibility, staged execution/publication, deterministic legality, owner/domain guards, replay and commit fences.

Следовательно, новая архитектурная формула должна быть:

```text
.NET-like public API
  -> generated typed SIP contract
  -> capability + ownership IPC
  -> privileged SingNextOS kernel authority
  -> typed HybridCPU Platform Authority Bridge
  -> external HybridCPU neutral runtime owners / ISE
  -> physical lanes / memory / accelerators
```

Не:

```text
app -> syscall -> VMX/VMCS manager -> hardware
```

и не:

```text
compiler metadata -> trusted hardware effect
```

## What already aligns strongly

### 1. Authority is runtime-owned

SingNextOS уже держит `CapabilityAuthority`, `RegionAuthority` и `ChannelRegistry` за privileged kernel boundary. SIP/driver analyzer запрещает self-mint/delegate authority. Это напрямую соответствует HybridCPU правилу: compatibility, metadata и projection не владеют production state; authoritative decision остаётся за neutral runtime owner.

### 2. Generation and revocation are first-class

Capabilities в SingNextOS привязаны к subject domain, generation и revocation epoch. Region handles и borrow leases также generation-bound. Termination domain приводит к revocation/reclaim/close. Это хорошо совпадает с HybridCPU подходом, где live domain/grant/restore epoch и owner identity являются частью admission, а stale state должен fail closed.

### 3. Ownership transfer maps naturally to domain-backed memory

`OwnedBuffer<T>`, `OwnedRegion<T>` и revocable `BorrowLease<T>` уже дают правильный software-level semantics для будущего HybridCPU domain memory/IOMMU/DMA binding. Это существенно сильнее модели «общая память + дескриптор»: владение можно связать с platform domain generation и разрешать zero-copy только после двухсторонней проверки.

### 4. SIP contracts behave like runtime admission descriptors

После C1–C4 generated SIP metadata фиксирует ownership shape, bounded size, request type/cardinality и response shape metadata. Runtime rejects malformed request before sequence/state/ownership mutation. Это тот же класс дисциплины, что в HybridCPU: наличие carrier/metadata не означает execution; каждый следующий boundary revalidates.

### 5. Deterministic artifacts are compatible with HybridCPU evidence culture

SingProcess manifests, protocol digests и AdmissionProof детерминированы. HybridCPU similarly treats legality certificates, replay evidence, telemetry and generated projections as inspectable artifacts. Оба проекта выигрывают от общей схемы: every trusted artifact has stable identity, version and digest, but artifact identity is not a live capability.

## Principal corrections required

### Correction A — replace “Hypervisor-as-Firmware owns everything” with “Platform Authority Bridge”

Прежняя Белая книга делала нативный гипервизор центральным владельцем boot/MMU/IOMMU/DMA/snapshots и предполагала, что он является базой всех SIP. Для HybridCPU-v2 это слишком VM-centric framing. Virtualization WhiteBook прямо отделяет neutral runtime authority от frozen VMX compatibility frontend.

SingNextOS следует сохранить идею минимальной trusted machine layer, но не реализовывать собственный VMCS/VMX authority manager. Ядро должно получать непрозрачные domain/address-space/I/O/compute grants через bridge к существующим neutral owners HybridCPU.

### Correction B — “domain” is not synonymous with “SIP”

Сегодня `DomainRegistry` SingNextOS фактически группирует processes by `DomainId`. Для полного ISE использования этого мало. В HybridCPU execution, memory, I/O, lane6, lane7, vector stream, nested and secure domains имеют разные owners and policies.

Новая модель должна различать:

- **SIP security domain** — OS-level principal/process group;
- **platform execution domain** — external scheduling/execution authority;
- **platform memory domain** — address-space/translation/region authority;
- **platform I/O domain** — DMA/IOMMU/device authority;
- **compute/lane service domains** — MatrixTile/DSC/L7 scoped authority;
- **nested domain** — child-domain composition;
- **secure/confidential domain** — optional future-gated external authority.

`DomainId` может оставаться стабильным OS identity, но не должен притворяться физическим/ISE domain handle.

### Correction C — compiler intent stays non-authoritative

Историческая WhiteBook предлагала Bartok-RS, новые IR, automatic barriers, PTX/SPIR-V/HDL backends и compiler borrow checker. В текущем проекте это не только преждевременно, но и нарушает black-box boundary. HybridCPU прямо закрепляет, что compiler intent / encoded opcode != runtime authority.

SingNextOS должен продолжать локальные analyzers/generators/admission tools, не требуя модификации внешнего compiler/runtime. Даже если будущий compiler начинает эмитить richer ISE carriers, kernel/platform runtime всё равно revalidates authority.

### Correction D — zero-copy is domain-scoped, not universal shared virtual memory

Старая идея «единое виртуальное пространство CPU/GPU/FPGA» должна быть заменена на explicit owned-region grants, IOMMU/domain bindings и controlled sharing. SecureCompute модель особенно ясно запрещает считать raw pointer/buffer ID authority.

### Correction E — SecureCompute is a feature gate, not a marketing promise

HybridCPU SecureCompute documentation прямо классифицирует текущий contour как readiness/policy hardening, а не production activation. Поэтому SingNextOS не должен объявлять `ConfidentialProcess`, secure migration или private DMA как доступные просто потому, что descriptor types существуют.

Correct behavior today:

```text
platform secure capability absent or not production-positive
  -> ConfidentialDomain.Open() fails closed / feature unavailable
```

### Correction F — use all ISE functionality through typed services, not through public opcodes

Полный функционал ISE можно задействовать, не раскрывая topology:

- scalar/vector operations — обычный compiled compute;
- MatrixTile — typed matrix compute service;
- DSC1 lane6 — bulk owned-buffer transformations;
- L7-SDC — scoped accelerator/device command service;
- YIELD/WFE/SEV/barriers — scheduler/event platform primitives;
- virtualization/nested — domain service;
- replay/telemetry/evidence — diagnostics and admission evidence;
- VMX — isolated compatibility service only;
- SecureCompute — optional neutral secure-domain service when production-positive externally.

## What “use the whole ISE” means

Не требуется, чтобы каждое приложение вызывало каждый opcode. Требуется, чтобы архитектура ОС **не блокировала** применение code-confirmed contours, а каждому классу ISE соответствовала безопасная typed abstraction, owner и policy boundary.

Это означает три уровня использования:

1. **Transparent execution:** normal ALU/LSU/vector instructions are compiler/runtime concern; OS only sets domain and resource policy.
2. **Explicit compute/system services:** MatrixTile, DSC, accelerators, event/barrier functions are requested through capabilities and typed descriptors.
3. **Privileged neutral domain control:** virtualization, nested domains, memory translation, IOMMU, evidence and secure-domain activation are kernel/platform bridge operations.

## Audit scorecard

| Area | SingNextOS today | HybridCPU direction | Audit result |
|---|---|---|---|
| Privileged authority | Kernel-owned | neutral owner first | Strong alignment |
| Capabilities | rights + generation + revocation + delegation | grant-first + policy/evidence filters | Strong alignment; needs platform binding |
| Ownership | move + region generation + revocable borrow | owner/domain guarded memory & shared buffer | Strong alignment |
| SIP contracts | generated, deterministic, fail-closed | descriptor/admission discipline | Strong alignment |
| Domains | process grouping | multiple neutral runtime owners | Major expansion needed |
| Memory/IOMMU/DMA | abstract resource kinds | explicit domain/binding authority | External bridge required |
| Virtualization | not yet platform-integrated | neutral virtualization; VMX compatibility only | Direction must stay neutral |
| SecureCompute | not implemented | policy/readiness, production backend denied | Must remain feature-gated |
| Matrix/DSC/L7 | no OS abstractions yet | scoped code-confirmed contours | High-value integration candidates |
| Replay/evidence | local deterministic tests/proofs | rich runtime evidence | Add read-only platform evidence bridge |
| Driver manifests | capability requirements | ISE device/accelerator descriptors are scoped | Keep simple now; do not overclaim UHDL |
| Compiler | black-box external | compiler intent non-authoritative | Correct boundary |

## Executive decision

SingNextOS should **not** pivot away from its current capability/ownership/SIP foundation. It should deepen it by adding a new fourth/fifth-layer split:

```text
kernel policy authority
  -> HybridCPU platform binding authority
```

The next architectural work after core SIP hardening should therefore be domain/platform contracts, not filesystems, GUI, a VMX manager, custom compiler IR, or a universal device DSL.
