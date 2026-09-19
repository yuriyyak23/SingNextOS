# Техническое задание: Post-CXL Operability and Service Runtime Modernization

## 1. Назначение

Настоящее ТЗ определяет следующий крупный, но intentionally low/medium-cost этап эволюции SingNextOS после завершения CXL, Virtualization, SecureCompute и HybridCPU-v2 integration направлений.

Цель — не добавить ещё один аппаратно-специфичный стек, а превратить уже сформированный capability/ownership/runtime фундамент в операционно зрелую ОС для реальных сервисов, тестирования, диагностики, безопасного восстановления и дальнейшего расширения provider ecosystem.

ТЗ охватывает десять направлений:

1. Capability-aware Service Supervisor.
2. Authority/Capability Inspector + provenance graph.
3. Typed deadlines / cancellation / timeout contracts.
4. Resource budgets / quotas / admission QoS.
5. Deterministic tracing + record/replay at OS boundaries.
6. Safe high-performance IPC v2.
7. Declarative component/service manifests.
8. Ordinary-domain checkpoint/restore.
9. Unified structured telemetry/evidence projection.
10. Provider conformance / fault-injection framework.

## 2. Исходные предпосылки

План строится так, как будто предыдущие refactoring roadmaps уже закрыты архитектурно и предоставляют стабильные semantic contracts. Фактическое отсутствие части этих изменений в текущем исходном коде не должно снижать требования данного ТЗ.

Предполагаются существующими:

- process/domain generation identities;
- capability ledger и typed resource identities;
- `OwnedRegion`, MOVE, borrow, `RegionUse`, `MutationEpoch`;
- platform mappings, device leases, DMA/external operation lifecycles;
- explicit separation `completion != visibility != publication != reclaim`;
- provider effect states `NotAccepted`, accepted/active, closed/contained, ambiguous/uncontained;
- VirtualDomain/SecureDomain exact bindings;
- CXL как provider substrate, а не новый authority root;
- generation-bound provider resources;
- HybridCPU-v2 external execution/replay integration;
- fail-closed teardown/reconfiguration semantics.

Если реализация обнаруживает отсутствие этих предпосылок, исправление выполняется в owning subsystem с сохранением уже утверждённых invariants; нельзя вводить local shortcut только ради продвижения нового roadmap.

## 3. Архитектурная позиция

Новые подсистемы являются **operability/control-plane composition**, а не новым источником полномочий.

```text
Manifest / Supervisor / Budget / Deadline / Trace / Telemetry
                         |
                         v
              existing SingNextOS authority
                         |
                         v
     Process / Capability / Region / Device / ExternalOperation
                         |
                         v
     HybridCPU / CXL / Virtualization / SecureCompute providers
```

Ни manifest, ни supervisor, ни inspector, ни trace, ни telemetry не могут обходить kernel authority validation.

## 4. Глобальные invariants

### 4.1 Authority

- capability остаётся единственным локальным разрешением на конкретный OS resource effect;
- manifest описывает желаемые права, но сам не является capability;
- budget разрешает объём использования, но не право использовать ресурс;
- health state не разрешает restart/rebind автоматически без supervisor authority;
- trace/replay record не даёт права повторить effect;
- telemetry/evidence не материализует новые права;
- checkpoint не восстанавливает provider authority без нового provider admission;
- Inspector является read-only projection и не получает hidden mutation endpoint.

### 4.2 Generation exactness

Любые долгоживущие operational objects должны иметь identity + generation/epoch либо быть однозначно привязаны к generation-bearing owner:

- service instance;
- supervisor-managed dependency binding;
- budget lease/reservation;
- deadline/cancellation scope;
- trace session;
- checkpoint image;
- telemetry subscription;
- provider fault-injection session.

Stale object должен fail closed до следующего effect.

### 4.3 External effects

Новые control-plane abstractions не имеют права считать operation закрытой только из-за timeout, cancellation request, service crash или supervisor restart.

Для effect-capable path должны сохраняться различия:

```text
NotStarted
Admitted
Submitted
DeviceComplete
Visible
Published
ProviderClosed / ProviderEffectContained
Released
```

Timeout может менять локальную policy, но не физический факт существования provider effect.

### 4.4 Restart

Restart — это replacement:

```text
ServiceGeneration N -> drain/contain -> terminal
ServiceGeneration N+1 -> fresh admission
```

Запрещено silently reusing:

- capabilities предыдущей generation;
- region handles после ownership mutation;
- device/provider leases;
- external operation receipts;
- checkpoint/provider tokens;
- cancellation/deadline scopes;
- debug/telemetry subscriptions, если policy не пересоздала их явно.

### 4.5 Zero-copy

Semantic IPC contract выражает ownership/data-transfer intent. Физическая оптимизация может быть copy, remap, page donation, borrow mapping или scatter-gather.

`zeroCopy=true` не должен стать обязательным ABI обещанием.

### 4.6 Replay

OS record/replay хранит causal/effect evidence. HybridCPU replay certificates могут коррелировать CPU-side replay, но:

```text
replay certificate != runtime permission
trace record != capability
checkpoint record != provider lease
```

## 5. Направление 1 — Capability-aware Service Supervisor

### 5.1 Цель

Создать privileged service orchestration layer для автозапуска, dependency ordering, health supervision, generation-safe restart, graceful drain и failure containment.

### 5.2 Объекты

Предлагаемые conceptual contracts:

```text
ServiceId
ServiceGeneration
ServiceInstanceHandle
ServiceDefinition
ServiceDependency
ServiceDependencyBinding
ServiceHealthSnapshot
ServiceRestartPolicy
ServiceDrainPolicy
ServiceFailurePolicy
ServiceReplacementReceipt
```

`ServiceInstanceHandle` должен ссылаться на exact process/domain generation.

### 5.3 Требования

Supervisor обязан:

- валидировать manifest до запуска;
- строить acyclic dependency graph;
- различать hard dependency и optional dependency;
- поддерживать readiness и liveness как observations, не authority;
- запускать зависимые сервисы только после explicit readiness policy;
- при failure сначала инициировать drain/containment старой generation;
- блокировать replacement, если старый external effect остаётся uncontained и replacement мог бы нарушить exclusivity;
- выпускать новые capabilities только через обычный kernel grant path;
- не копировать capability set старого процесса механически;
- поддерживать bounded restart policy и backoff без бесконечного restart storm;
- иметь crash-loop state и operator-visible reason;
- поддерживать planned replace для upgrade;
- не превращать service name в capability identity.

### 5.4 Health

Health model минимум:

```text
Starting
Ready
Degraded
Draining
Unhealthy
Failed
Quarantined
Stopped
```

Health — observation. `Ready` не доказывает provider closure, ownership или secure state.

## 6. Направление 2 — Authority Inspector + Provenance Graph

### 6.1 Цель

Сделать capability-native систему диагностируемой без нарушения isolation.

### 6.2 Граф

Graph nodes могут включать:

- process/domain;
- capability;
- region;
- borrow lease;
- region use;
- backing lease;
- platform mapping;
- device lease;
- DMA grant;
- ExternalOperation;
- VirtualDomain;
- SecureDomain;
- service instance;
- budget reservation;
- checkpoint pin.

Graph edges:

```text
owns
minted-by
delegated-to
borrowed-by
mapped-into
backed-by
pins
requires
waiting-for-closure
authorized-by
quarantined-by
replaced-by
```

### 6.3 Требования безопасности

- default process может видеть только собственную projection;
- privileged inspector требует отдельной debug/inspection capability;
- sensitive host topology, security backend diagnostics и tenant-crossing facts должны redacted by policy;
- graph DTO не содержит reusable live capability material;
- inspector не имеет mutation API;
- snapshot маркируется generation/time/consistency class;
- возможен explicit `WhyBlocked(resource)` query для reclaim/move/teardown diagnosis.

## 7. Направление 3 — Typed deadlines / cancellation / timeout

### 7.1 Цель

Единая семантика временных ограничений и cancellation для IPC, device/DMA, CXL, compute, VM operations, provider calls, service shutdown и teardown.

### 7.2 Модель

```text
Deadline
DeadlineClockClass
CancellationScopeId
CancellationGeneration
CancellationRequest
CancellationDisposition
TimeoutDisposition
```

Cancellation disposition минимум:

```text
CancelledBeforeEffect
CancellationRequested
TooLateEffectMayExist
CompletedBeforeCancellation
ProviderClosurePending
ProviderEffectContained
Unsupported
```

### 7.3 Правила

- timeout не равен cancellation success;
- submitted effect нельзя локально освободить без closure/containment proof;
- cancellation request idempotent;
- cancellation scope нельзя reuse после generation change;
- deadlines используют monotonic runtime time, wall clock не должен определять correctness;
- parent deadline может ограничивать child deadline;
- child не может продлить strict parent deadline;
- teardown может инициировать cancellation, но reclaim следует прежним closure rules.

## 8. Направление 4 — Resource budgets / quotas / admission QoS

### 8.1 Цель

Добавить bounded resource usage и multi-service fairness без нового scheduler authority model.

### 8.2 Budget dimensions

Минимальный набор:

- CPU execution budget;
- owned memory budget;
- pinned/platform-mapped memory budget;
- RegionUse budget;
- IPC in-flight bytes/messages;
- external-operation count;
- device/DMA outstanding operations;
- guest memory;
- checkpoint storage;
- trace/telemetry buffer;
- provider-specific opaque cost units только если они provider-neutral по смыслу.

### 8.3 Hierarchy

```text
SystemBudget
 -> ServiceBudget
   -> Process/DomainBudget
     -> OperationReservation
```

Для child:

```text
ChildBudget <= ParentBudget
```

### 8.4 Требования

- reservation до effect;
- release только после соответствующего lifecycle closure;
- crash не должен автоматически вернуть outstanding external-effect quota;
- budget exhaustion — typed admission failure;
- quota не является capability;
- priority/latency hints advisory и не могут bypass authority;
- scheduler/provider может игнорировать hint, но не semantic limit;
- accounting exact на generation boundaries.

## 9. Направление 5 — Deterministic tracing + record/replay

### 9.1 Цель

Получить causal diagnosis и reproducibility на semantic OS boundaries.

### 9.2 События

Обязательные trace families:

- process/service lifecycle;
- capability mint/delegate/revoke;
- MOVE/borrow/return/revoke;
- RegionUse acquire/release;
- mapping/device lease admission/closure;
- ExternalOperation lifecycle;
- provider generation change/reset;
- virtual/secure domain transitions;
- IPC send/receive correlation;
- budget reserve/release;
- cancellation/deadline events;
- publication and teardown barriers.

### 9.3 Не трассировать как authoritative state

Trace может хранить identifiers/digests/semantic results, но не должен хранить reusable secret capability tokens или provider credentials.

### 9.4 Replay modes

- diagnostic replay: проверка causal sequence;
- model replay: воспроизведение через deterministic model providers;
- HybridCPU-correlated replay: корреляция CPU replay evidence с OS semantic events;
- live re-effect replay запрещён без нового admission.

## 10. Направление 6 — Safe high-performance IPC v2

### 10.1 Цель

Снизить overhead service decomposition, сохраняя ownership semantics.

### 10.2 Transport-independent operations

```text
SendCopy<T>
SendMove<TPayload>
SendBorrowRead<TPayload>
SendScatterGather
RequestReply
Stream/Batch bounded variant
```

Названия conceptual; финальный API выбирается по текущему runtime style.

### 10.3 Ownership semantics

- MOVE инвалидирует sender ownership только после proven transfer admission;
- failed pre-effect send оставляет ownership у sender;
- ambiguous handoff должен иметь explicit recovery/containment semantics;
- borrow read имеет bounded lifetime и не разрешает mutation;
- scatter-gather сохраняет exact segment identity/range;
- reply не означает автоматический возврат moved ownership;
- cancellation IPC request не отменяет уже committed downstream provider effect.

### 10.4 Fast paths

Разрешены оптимизации:

- inline copy;
- shared read mapping;
- page donation/remap;
- scatter-gather descriptors;
- queue batching;
- proven same-domain optimizations.

Выбор fast path не наблюдаем приложением как semantic guarantee.

## 11. Направление 7 — Declarative manifests

### 11.1 Цель

Сделать service/component requirements machine-readable и пригодными для supervisor/admission tooling.

### 11.2 Manifest sections

```text
identity/version
entry point
hard dependencies
optional dependencies
requested capability classes
feature requirements
platform requirements
budget requests/limits
restart/drain policy
checkpoint policy
telemetry visibility
security/evidence requirements
compatibility constraints
```

### 11.3 Правила

- manifest is request, not grant;
- unknown mandatory feature => admission failure;
- unknown optional feature => explicit degraded/unsupported decision;
- schema versioned;
- normalized digest может использоваться как evidence/config identity, но не capability;
- runtime materializes exact grants independently;
- supervisor logs requested vs granted deltas.

## 12. Направление 8 — Ordinary-domain checkpoint/restore

### 12.1 Scope

Поддерживается только ordinary logical SIP/domain state.

Первый релиз не включает:

- confidential/private secure state;
- live CXL provider state;
- live DMA/device operations;
- uncontained external effects;
- raw provider leases;
- transparent migration между hosts;
- opaque host evidence.

### 12.2 Classification

Каждый runtime resource должен быть классифицирован:

```text
Checkpointable
RecreateOnRestore
RequiresDrain
NonCheckpointable(reason)
```

### 12.3 Restore

Restore создаёт новую process/service generation.

Все capabilities/platform leases/provider bindings, которые должны существовать после restore, проходят fresh admission. Serialized handle старой generation не становится live authority.

### 12.4 Atomicity

Checkpoint должен либо:

- получить согласованный quiescent snapshot;
- либо завершиться typed failure без частично действительного image.

## 13. Направление 9 — Structured telemetry/evidence projection

### 13.1 Цель

Дать observability без `/proc`-подобной ambient host introspection.

### 13.2 Projection classes

- self operational telemetry;
- service aggregate telemetry;
- privileged system diagnostics;
- security/evidence projection;
- debug trace metadata.

### 13.3 Typed metrics/events

Примеры:

- CPU/use budget consumption;
- owned/pinned memory;
- IPC queue depth/latency;
- external operations by lifecycle state;
- restart/health state;
- deadline expirations;
- provider faults by category;
- checkpoint duration/size;
- authority pin counts without exposing restricted identities.

### 13.4 Security

- host topology unavailable by default;
- cross-tenant metrics unavailable by default;
- secure backend diagnostics require dedicated capability;
- raw evidence and telemetry remain separate types;
- evidence freshness/generation preserved;
- telemetry never mints rights.

## 14. Направление 10 — Provider conformance / fault injection

### 14.1 Цель

Сделать provider abstraction scalable для будущих GPU/NPU/storage/network/RDMA backends.

### 14.2 Conformance dimensions

Каждый effect-capable provider contract должен тестироваться на:

- exact identity/generation;
- wrong owner/domain;
- stale handle;
- malformed success receipt;
- proven pre-effect `NotAccepted`;
- ambiguous post-effect failure;
- exception after effect boundary;
- double close;
- close with stale generation;
- reset while active;
- reconfiguration while active;
- teardown during operation;
- cancellation at every lifecycle stage;
- provider recovery token exactness;
- quarantine when closure proof absent.

### 14.3 Fault injection

Fault plans должны быть deterministic и scenario-bound:

```text
FailBeforeEffect
FailAfterAcceptance
ThrowAfterAcceptance
ReturnMalformedReceipt
LoseGeneration
DelayClosure
FailClosure
ResetDuringVisible
```

Fault injector не становится production authority path.

## 15. Cross-cutting integration requirements

### 15.1 Supervisor + manifests + budgets

Manifest requests dependencies/budgets; supervisor performs admission; kernel/provider materialize exact grants. Restart obtains fresh budget/capability bindings.

### 15.2 Supervisor + deadlines

Stop/restart uses bounded drain deadlines but cannot reclaim uncontained provider effects when deadline expires.

### 15.3 Inspector + trace + telemetry

Inspector answers current authority graph; trace answers causal history; telemetry answers operational aggregate state. These surfaces must remain separate to avoid confusing evidence with authority.

### 15.4 IPC + budgets + cancellation

IPC admission reserves queue/byte budget. Cancellation releases only reservations whose semantic transfer/effect is proven not committed.

### 15.5 Checkpoint + supervisor

Supervisor may checkpoint before planned replace, but restore always creates a new generation and performs fresh capability/provider admission.

### 15.6 Provider conformance + all subsystems

Model providers should be usable to deterministically exercise supervisor crashes, IPC ambiguity, timeout after acceptance, budget pinning, checkpoint drain refusal and telemetry/trace visibility.

## 16. Public API philosophy

API должен быть semantic and provider-neutral.

Плохие API examples:

```text
RestartCxlService()
ZeroCopySendGuaranteed()
ReplayThisHardwareCommand()
RestoreDeviceLeaseFromCheckpoint()
GetAllHostTopology()
```

Предпочтительные semantic concepts:

```text
ReplaceService
TransferOwnedPayload
ReplayDiagnosticSession
RecreateExternalBinding
ReadAuthorizedTelemetryProjection
```

## 17. Error model

Новые подсистемы используют typed failures, совместимые с существующим fail-closed подходом. Требуются различимые категории:

- Unsupported;
- Denied;
- StaleGeneration;
- BudgetExceeded;
- DeadlineExpired;
- CancellationPending;
- ProviderEffectUncontained;
- DependencyUnavailable;
- HealthFailed;
- CheckpointBlocked;
- ProjectionDenied;
- MalformedProviderReceipt;
- Quarantined.

Нельзя сводить все ошибки к `Unavailable`.

## 18. Concurrency requirements

- supervisor state transitions serialized per service identity;
- dependency graph mutation atomic with generation changes;
- budget reserve/release race-safe;
- cancellation vs completion has deterministic winner/result record;
- inspector snapshot consistency class explicit;
- trace sequence monotonic per producer plus global causal correlation IDs;
- IPC MOVE has exactly one logical owner after terminal admission result;
- checkpoint blocks conflicting ownership mutations for its snapshot window;
- provider fault injection isolated per test/session.

## 19. Persistence requirements

Первый roadmap не требует general-purpose persistent service database.

Минимально persisted metadata может включать:

- declarative manifests/configuration;
- optional ordinary checkpoint images;
- bounded trace artifacts for tests/debug;
- operator policy.

Live authority ledger остаётся runtime-owned, а не reconstructable solely from persisted metadata.

## 20. Performance goals

Без фиксации конкретных microbench numbers до baseline measurements устанавливаются qualitative gates:

- supervisor не должен быть на hot path обычного IPC/effect;
- disabled tracing имеет near-zero semantic overhead и bounded implementation overhead;
- telemetry aggregation не требует global stop-the-world;
- IPC v2 должен иметь fast path не хуже существующего IPC для small messages;
- MOVE/borrow path не должен вводить лишнюю payload copy, если ownership conditions позволяют remap/transfer;
- budget checks O(1) или bounded by small hierarchy depth;
- inspector graph строится on demand и не участвует в authority hot path.

После Phase 0/1 должны быть зафиксированы измеряемые performance baselines.

## 21. Security goals

- no ambient supervisor root capability exposed to services;
- no capability serialization into manifest/checkpoint/trace;
- no host evidence leak through telemetry/inspector;
- no stale-generation restart inheritance;
- no budget bypass through child creation or IPC delegation;
- no cancellation-based reclaim before effect closure;
- no replay-based re-effect without new admission;
- no fault injector available to unprivileged production workloads.

## 22. Validation strategy

Каждая фаза должна иметь:

- contract/unit tests;
- negative stale-generation tests;
- teardown tests;
- ambiguous external-effect tests where applicable;
- process/service restart tests;
- deterministic model-provider tests;
- conformance tests;
- integration tests with existing Region/IPC/platform lifecycle;
- performance regression baseline where hot path changes.

## 23. Required end-to-end scenarios

До закрытия roadmap должны проходить минимум:

1. Service A depends on B; B crashes with no external effects; supervisor replaces B with generation N+1 and A receives only explicitly rebound authority.
2. B crashes with ambiguous provider effect; replacement/reclaim that would violate exclusivity remains blocked/quarantined.
3. Inspector explains exact object pinning a region and returns provenance without exposing reusable capability material.
4. IPC MOVE + deadline race never creates two owners or loses ownership.
5. Cancellation after provider submission reports pending/too-late state and blocks local reclaim until closure.
6. Child budget cannot exceed parent and crash does not release still-live external-operation quota.
7. Trace reconstructs capability -> IPC -> external effect -> publication causal chain without acting as permission.
8. IPC scatter-gather preserves exact ranges and ownership/borrow lifetimes.
9. Ordinary checkpoint refuses a live noncheckpointable device/CXL effect; after drain it succeeds; restore creates a fresh generation.
10. Telemetry projection exposes self metrics while denying host topology/cross-tenant facts.
11. Provider conformance catches `Unavailable`-after-effect implementation that attempts to reclaim local authority.
12. Planned service replacement with checkpoint, budget, IPC, trace and telemetry enabled completes without stale authority reuse.

## 24. Future-gated / explicitly out of scope

Этот roadmap не должен незаметно расширить scope до:

- confidential checkpoint/migration;
- cross-host live migration;
- secure writable multi-host memory;
- CXL-specific service authority;
- general distributed scheduler;
- mandatory zero-copy ABI;
- arbitrary live hardware command replay;
- application-visible raw host topology;
- production fault injection for unprivileged workloads;
- new HybridCPU ISA solely for supervisor/telemetry/budget concepts.

## 25. Definition of Done

Roadmap считается закрытым только когда:

- все 10 направлений имеют executable contracts and tests;
- service supervisor performs generation-safe replace with fail-closed external-effect handling;
- provenance inspector explains authority/reclaim state without becoming mutation authority;
- deadlines/cancellation are used by IPC and at least one external/provider operation path;
- hierarchical budgets gate admission and survive crash semantics correctly;
- deterministic tracing correlates OS lifecycle with HybridCPU/external operations while remaining non-authoritative;
- IPC v2 preserves exact ownership semantics and has measured fast paths;
- manifests are validated, versioned and drive supervisor requests without granting rights themselves;
- ordinary checkpoint/restore uses fresh authority admission and rejects unsupported state;
- telemetry/evidence projections enforce visibility policy;
- provider conformance/fault-injection suite is reusable by at least two provider families;
- full negative matrix passes;
- no earlier CXL/Virtualization/SecureCompute authority invariant is weakened.