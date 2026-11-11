Да. Здесь, на мой взгляд, возникает следующий естественный слой поверх уже сделанного `SingCap-M`: не «ускоренный RPC», а **SIP Fusion Job** — заранее квалифицированный граф нескольких SIP-вызовов, исполняемый внутри одного ManagedCap runtime без промежуточных channel/response round-trip, но с сохранением всех capability/Region/session/seal границ.

Важно: такого `SipJob` как готового subsystem в текущем репозитории я не нашёл. Ниже — архитектурное продолжение **не вопреки roadmap, а непосредственно из P04/P07/P08/P09/P13 и `AUTHORITY_COMPOSITION_PROTOCOL`**.

## 1. Что уже есть, на чём Job можно построить

Текущий обычный session-SIP идёт примерно так:

```text
generated client
    │
    ▼
RuntimeSipClientTransport
    │
    ▼
RuntimeKernel.InvokeSessionAsync
    │
    ├─ ResolveSession
    │    ├─ process/generation
    │    └─ capabilities
    │
    ├─ EndpointSession.AcquirePin
    ├─ RevalidatePin
    │
    ├─ ChannelRegistry.Send
    │    ├─ endpoint check
    │    ├─ protocol state
    │    ├─ capability checks
    │    ├─ payload schema
    │    └─ MOVE/BORROW
    │
    ├─ EndpointSessionInvocationRegistry.Register
    │
    ├─ enqueue
    │
    ▼
service
    │
    ▼
PublishSessionResponse
    │
    ├─ publication validation
    ├─ ownership return
    └─ ResponseRegistry
          │
          ▼
 TaskCompletionSource / waiter
          │
          ▼
caller
```

И это правильно как универсальный изолированный SIP transport.

Но если:

```text
SIP A
SIP B
SIP C
SIP D
```

уже находятся внутри **одного и того же квалифицированного ManagedCap runtime**, то для цепочки

```text
A -> B -> C -> D
```

большая часть транспортной механики становится не data-isolation necessity, а механизмом materialization каждого логического security transition.

И тут появляется возможность разделить:

```text
security boundary
        !=
queue boundary
        !=
scheduler boundary
        !=
serialization boundary
```

Это ключевая идея.

---

# 2. Что такое SIP Job в этой модели

Я бы дал ему такое определение:

> **SipJob — immutable, admission-qualified execution graph, связывающий несколько существующих SIP operations и их ownership/capability transitions в один runtime execution envelope.**

То есть Job не является новым SIP с объединёнными полномочиями.

Очень важно:

```text
Job authority ≠ union(SIP capabilities)
```

и:

```text
JobPlan ≠ capability
JobDigest ≠ capability
JobHandle ≠ authority by possession
```

Job должен хранить только:

```text
какие stages существуют
какие SIP methods вызываются
как связаны их inputs/outputs
какие authority requirements нужны
какие Region transitions должны произойти
где находятся effect/publication barriers
```

А authority по-прежнему остаётся в:

```text
CapabilityAuthority
EndpointSessionRegistry
RegionAuthority
SealedObjectAuthority
ExternalOperationAuthority
```

Это прямо соответствует `AUTHORITY_COMPOSITION_PROTOCOL.md`: coordinator может коррелировать leases/pins, но **не должен копировать permission truth в новый registry**.

---

# 3. Архитектурно это должно выглядеть не как batch RPC

Не так:

```text
Job {
    Call SIP1
    Call SIP2
    Call SIP3
}

foreach:
    normal SIP RPC
```

Это почти ничего не ускорит.

Нужно:

```text
                 external SIP boundary

Caller ───────► Job Entry Sentry
                       │
                       ▼
                Job admission
                       │
        ┌──────────────┴──────────────┐
        │ qualified intra-runtime     │
        │ execution segment           │
        ▼                             │
    SIP-A sentry                      │
        │ direct trusted dispatch     │
        ▼                             │
    SIP-A implementation              │
        │                             │
        │ Region handoff              │
        ▼                             │
    SIP-B sentry                      │
        │ direct trusted dispatch     │
        ▼                             │
    SIP-B implementation              │
        │                             │
        ▼                             │
    SIP-C sentry                      │
        │                             │
        └──────────────┬──────────────┘
                       │
                 publication barrier
                       │
                       ▼
                 final response
```

Главное:

> **sentry остаётся, transport materialization исчезает.**

То есть security transitions сохраняются, но между stages нет необходимости создавать:

```text
ChannelEnvelope
Queue entry
Pending response
TaskCompletionSource
ResponseEnvelope
```

если следующий consumer находится в том же qualified runtime и Job graph уже связывает producer с consumer.

---

# 4. Почему это допустимо именно для ManagedCap

P09 запрещает ManagedCap:

```text
arbitrary native code
Unsafe
dynamic IL
Reflection.Emit
runtime arbitrary assembly loading
ambient authority
unbounded mutable object graphs
```

А P08 дополнительно говорит:

> каждый cross-compartment call — security transition;

и прямо запрещает передачу mutable raw CLR object reference через ManagedCap boundary.

Это означает интересную вещь.

Разные SIP могут физически находиться:

```text
в одном CLR
в одном NativeAOT image
в одном address space
```

но логически оставаться отдельными compartments.

Изоляция строится не на:

```text
page-table switch на каждый SIP
```

а на:

```text
closed-world code
+
restricted object graph
+
generated sentry
+
explicit authority
+
Region ownership
```

Поэтому TrustedRuntime вполне может иметь прямой reference:

```text
StageAThunk -> SIP-A implementation
StageBThunk -> SIP-B implementation
```

при одном критическом условии:

```text
этот reference никогда не попадает в ManagedCap code
```

То есть:

```text
Managed SIP A

не получает:

SIPB serviceObject
delegate-to-SIPB
IServiceProvider
ServiceLocator
```

Единственный holder прямых implementation refs — trusted Job executor/generated sentry layer.

---

# 5. Я бы ввёл два разных понятия: Job Plan и Job Run

Это принципиально полезно.

`SipJobPlan` — immutable evidence:

```text
SipJobPlan
 ├─ PlanId
 ├─ PlanDigest
 ├─ StageDescriptors[]
 ├─ EdgeDescriptors[]
 ├─ input schema
 ├─ output schema
 ├─ authority requirements
 ├─ ownership graph
 ├─ cancellation graph
 ├─ effect barriers
 └─ maximum bounded resources
```

Он не даёт никакого права что-либо выполнять.

А при каждом execution появляется:

```text
SipJobRun
 ├─ JobRunId
 ├─ Caller ProcessHandle
 ├─ current generations
 ├─ acquired OperationAuthorityLeases
 ├─ EndpointSessionPins
 ├─ RegionUse handles
 ├─ seal pins
 ├─ cancellation scope
 └─ stage progress
```

Но и `SipJobRun` не становится отдельным owner authority.

Это **frame с references на authority**, а не новый ledger.

То есть:

```text
JobRunFrame
      │
      ├──ref──► CapabilityAuthority lease
      ├──ref──► EndpointSession pin
      ├──ref──► RegionUse
      └──ref──► Seal pin
```

---

# 6. Самый важный объект — `JobStageDescriptor`

Концептуально я бы делал его примерно таким:

```csharp
internal sealed record SipJobStageDescriptor(
    StageId Stage,
    ServiceContractIdentity Contract,
    uint MessageId,
    GeneratedSentryThunkId Sentry,
    StageInputShape Input,
    StageOutputShape Output,
    AuthorityRequirement[] Authorities,
    OwnershipTransition[] Ownership,
    StageEffectClass EffectClass,
    EffectRevocationPolicy RevocationPolicy,
    StageExecutionClass ExecutionClass);
```

Причём это не динамически интерпретируемая строковая RPC metadata.

Generator может превратить:

```csharp
IImageDecoder.DecodeAsync(...)
IImageFilter.FilterAsync(...)
IInference.RunAsync(...)
```

в статические IDs/thunks.

И Job plan тогда содержит:

```text
stage 0 -> decoder generated thunk #17
stage 1 -> filter generated thunk #23
stage 2 -> ML generated thunk #52
```

Не reflection.

Не `MethodInfo.Invoke`.

Не service locator.

Не dynamic code.

---

# 7. Выборочные SIP можно агрегировать динамически — но не генерировать динамический код

Это важное следствие P09.

Допустим runtime имеет 20 квалифицированных SIP:

```text
A B C D E F ...
```

И приложение хочет Job:

```text
A → C → F
```

Не обязательно заранее генерировать отдельную assembly для каждой комбинации.

Можно заранее иметь:

```text
A.StageThunk
B.StageThunk
C.StageThunk
...
```

А trusted `SipJobCompiler` только собирает immutable plan:

```text
[A, C, F]
```

из уже зарегистрированных qualified thunks.

То есть dynamic composition:

```text
yes
```

а dynamic code generation:

```text
no
```

Получаем:

```text
precompiled qualified stages
        +
runtime-selected graph
        =
qualified JobPlan
```

---

# 8. Job admission должен использовать ACP prepare → pin → commit

И здесь roadmap практически уже написал алгоритм.

Для Job segment:

```text
Resolve caller
      ↓
Resolve exact sessions
      ↓
Acquire session pins
      ↓
Acquire exact capability operation leases
      ↓
Acquire seal pins
      ↓
Acquire RegionUses / borrows
      ↓
bind generations
      ↓
final revalidate
      ↓
COMMIT segment
      ↓
release registry locks
      ↓
execute managed code
```

Крайне важно:

```text
никакого ManagedCap user code
никакого provider call
никакого await
```

под lock'ами authority registries.

Это уже прямо запрещено ACP-004.

---

# 9. Для нескольких SIP нужен детерминированный порядок pinning

Допустим Job:

```text
SIP-A
 ↓
SIP-B
 ↓
SIP-C
```

использует:

```text
3 sessions
2 capabilities
3 Regions
1 sealed object
```

Нельзя произвольно делать:

```text
A locks session
B locks Region
C locks capability
```

иначе появляется deadlock matrix.

Я бы использовал канонический prepare order:

```text
process generations
    ↓
sessions sorted by SessionId
    ↓
capability leases by CapabilityId/token index
    ↓
sealed objects by stable object identity
    ↓
Regions sorted by (RegionId, Generation)
    ↓
final cross-owner revalidation
```

Но сами locks не обязательно держать одновременно.

Лучше использовать существующую ACP reservation/revalidation модель:

```text
pin A
release A lock

pin B
release B lock

...

revalidate all exact pins

commit Job segment
```

То есть Job executor не превращается в giant lock coordinator.

---

# 10. Но нельзя заранее admit весь Job безусловно

Здесь есть тонкость.

Допустим:

```text
Stage A = decode
Stage B = filesystem write
Stage C = network send
```

Если на входе Job сразу сделать:

```text
AcquireOperationAuthority(write-file)
AcquireOperationAuthority(network-send)
```

мы можем:

```text
consume one-shot authority
consume quota
```

ещё до того, как известно, дойдёт ли execution до Stage C.

Это особенно плохо для:

```text
one-shot
quota
irreversible effects
conditional stages
```

Поэтому Job надо разбивать на **admission segments**.

Например:

```text
      SEGMENT 0
┌────────────────────┐
│ decode             │
│ transform          │
│ hash               │
└────────────────────┘
          │
          ▼
      EFFECT BARRIER
       file write
          │
          ▼
      SEGMENT 1
┌────────────────────┐
│ encode metadata    │
│ create packet      │
└────────────────────┘
          │
          ▼
      EFFECT BARRIER
       network send
```

Это очень важная архитектурная единица.

---

# 11. Какие stages реально можно fuse

Я бы делил stages на три execution class.

### `PureManaged`

Пример:

```text
parse
transform
compress
hash
image processing
crypto managed algorithm
```

Нет внешнего provider effect.

Возможно mutating private service state, поэтому «pure» здесь не означает functional purity, а означает отсутствие external effect lifecycle.

### `RegionLocal`

Например:

```text
BORROW
MOVE
subrange use
staged local buffer operation
```

Требуют RegionAuthority, но не внешнего hardware provider.

### `ExternalEffect`

Например:

```text
filesystem I/O
network send
DMA
DSC
GPU
CXL
virtualization
```

Эти stages должны оставаться explicit effect barriers.

Их нельзя «схлопнуть» так, будто completion автоматически означает publication.

---

# 12. Внутри pure fusion segment можно убрать почти весь IPC transport

Сегодня условно:

```text
A result
   ↓
ResponseRegistry
   ↓
caller receives
   ↓
another generated client
   ↓
ChannelRegistry
   ↓
B
```

Job может сделать:

```text
A
 │
 ▼
internal edge slot
 │
 ▼
B sentry
 │
 ▼
B
```

Причём `internal edge slot` не должен быть public mutable object graph.

Он должен быть TrustedRuntime-only.

Например:

```text
JobEdgeSlot =
    BoundedValue
    RegionHandle + RegionUse
    BorrowLeaseHandle
    SealedHandle
```

Не:

```text
object arbitraryClrObject
```

---

# 13. Это особенно интересно для ownership edges

Рассмотрим:

```text
Decoder SIP
   ↓ MOVE
Filter SIP
   ↓ MOVE
Encoder SIP
```

Нельзя просто сказать:

> раз они в одном runtime, owner менять не будем.

Это сломает SingCap.

MOVE всё равно должен означать:

```text
Decoder owner:
RegionGeneration = 11

        ↓ Transfer

Filter owner:
RegionGeneration = 12

        ↓ Transfer

Encoder owner:
RegionGeneration = 13
```

То есть logical authority transitions сохраняются.

Но можно убрать transport scaffolding вокруг них.

Сегодня условно:

```text
Region.Transfer
+
new ChannelEnvelope
+
enqueue
+
dequeue
+
invocation register
+
response correlation
```

Job:

```text
Region.Transfer
+
update internal Job edge slot
+
invoke next generated sentry
```

Это и есть правильная оптимизация:

> **не убрать security transition, а убрать транспорт вокруг security transition.**

---

# 14. Для BORROW ещё дешевле

Например:

```text
Source SIP
       │
       ├──── read borrow ───► Hash SIP
       │
       └──── read borrow ───► Classifier SIP
```

Job может иметь:

```text
RegionUse R/read
      │
      ├─ Stage H
      └─ Stage C
```

Если conflict matrix разрешает shared reads, оба stage могут быть scheduled параллельно.

Получается уже не просто Job chain, а DAG:

```text
                ┌─ Hash ──────┐
input Region ───┤             ├── Merge
                └─ Analyse ───┘
```

И вот здесь `SipJob` становится особенно интересным для HybridCPU/SMT.

---

# 15. Job может стать scheduler-visible DAG

Не в смысле выдавать приложению lane 0/lane 6.

Но runtime знает:

```text
Stage A:
 CPU-heavy managed

Stage B:
 read-only Region compute

Stage C:
 DSC candidate

Stage D:
 network effect
```

И Job gives scheduler больше информации, чем независимые RPC.

Отдельные RPC выглядят:

```text
request
request
request
```

Job выглядит:

```text
A
├── B
└── C
    ↓
    D
```

Это открывает:

```text
parallel read stages
SMT placement
late execution choice
DSC offload
dependency-aware scheduling
```

без публикации HybridCPU physical topology в API.

---

# 16. Вот здесь HybridCPU-v2 особенно хорошо стыкуется

Представим:

```text
SIP Decode
     ↓
SIP Normalize
     ↓
SIP MatrixInference
     ↓
SIP Aggregate
```

Job compiler знает dependency graph.

OS layer говорит:

```text
Normalize:
 managed/vector compute

MatrixInference:
 semantic Matrix capability

Aggregate:
 managed reduction
```

HybridCPU runtime уже сам определяет:

```text
ALU/LSU slots
MatrixTile
typed scheduling
SMT placement
```

То есть в перспективе:

```text
SipJob DAG
      ↓
semantic execution graph
      ↓
HybridCPU execution policy
      ↓
typed-lane materialization
```

Без:

```text
Job says "use lane6"
```

Это было бы ошибкой.

---

# 17. Очень важна операция-specific authority projection

P08 уже говорит, что `InvocationAuthorityContext` не должен быть enumerable.

Для Job это ещё важнее.

Нельзя сделать:

```csharp
class SipJobContext
{
    IEnumerable<Capability> AllCapabilities;
}
```

или:

```csharp
context.ResolveService(...)
```

Вместо этого generated thunk должен видеть что-то вроде:

```text
DecodeInvocationAuthority
  InputReadRegion
  OutputWriteRegion

FilterInvocationAuthority
  InputReadRegion
  OutputWriteRegion

NetworkInvocationAuthority
  SocketSeal
  SendCapability
  PayloadReadRegion
```

То есть Job не расширяет authority SIP.

Он просто заранее создаёт правильные **projection slots**.

---

# 18. Direct thunk может выглядеть примерно так

Концептуально:

```csharp
internal delegate ValueTask<JobStageResult>
    GeneratedJobStageThunk(
        ref JobRunFrame run,
        ref JobStageInput input);
```

Но `JobRunFrame` не передаётся service code.

Generated sentry делает:

```text
Job executor
    │
    ▼
generated Stage-B sentry
    │
    ├─ get exact Stage-B projection
    ├─ revalidate required leases
    ├─ materialize Span / handles
    │
    ▼
SIP-B implementation
```

ManagedCap implementation видит только нормальный typed method.

---

# 19. Не надо менять пользовательский SIP API

Это важная design goal.

Например сегодня:

```csharp
ValueTask<OwnedBuffer<byte>> FilterAsync(
    [Borrows] OwnedBuffer<byte> source,
    [Consumes] OwnedBuffer<byte> destination);
```

Не нужно создавать:

```csharp
FilterForJobUnsafe(...)
```

или:

```csharp
FilterDirect(...)
```

Job generator должен построить **другой trusted adapter** для того же contract.

То есть:

```text
                      normal path
IComputeService ───────────────► channel/session transport

                      job path
IComputeService ───────────────► generated intra-runtime sentry thunk
```

Security semantics одни.

Transport materialization разные.

---

# 20. Job не должен делать промежуточные `ResponseEnvelope`

Это одно из главных мест выигрыша.

Сегодня response имеет смысл, потому что:

```text
caller и callee разделены transport boundary
```

В Job:

```text
Stage A output
```

может сразу стать:

```text
Stage B input
```

Промежуточная completion запись всё равно может понадобиться для tracing/debugging:

```text
StageCompletionEvidence
```

но она не должна превращаться в:

```text
ResponseEnvelope
TaskCompletionSource
runtime waiter
```

на каждой edge.

То есть:

```text
stage completion evidence != externally published response
```

Это полностью соответствует философии SingNextOS.

---

# 21. Публиковать наружу можно один раз

Например:

```text
Caller
   │
   ▼
Job
 ├─ Stage A
 ├─ Stage B
 ├─ Stage C
 └─ Stage D
   │
   ▼
one final publication
```

Вместо:

```text
Caller -> A -> response
Caller -> B -> response
Caller -> C -> response
Caller -> D -> response
```

Для pipeline это огромная разница.

---

# 22. Но Job НЕ является транзакцией

Это надо зафиксировать жёстко.

P08 и ACP правильно говорят:

> runtime не может обещать rollback arbitrary managed side effects.

Допустим:

```text
A modified private service state
B failed
```

Нельзя делать вид:

```text
Job rollback => A never happened
```

Если service A не имеет собственного staged state.

Поэтому Job semantics должны быть:

```text
ordered execution + explicit publication boundaries
```

а не:

```text
ACID transaction
```

---

# 23. Ownership failure-path должен быть частью Job graph

Вот это потенциально сложнейшая часть.

Допустим:

```text
A --MOVE R--> B
B fails
```

Region уже принадлежит B.

Нельзя просто:

```text
exception => assign back to A
```

потому что B уже мог его изменить.

Поэтому Job compiler должен иметь для каждого ownership-bearing edge **settlement path**.

Например:

```text
              success
A ─MOVE─► B ──────────► C

          │
          │ failure
          ▼
       Recovery/Return
```

То есть Job qualification должна доказать:

```text
для любого terminal path:
каждый OwnedRegion имеет ровно одного определённого owner
каждый borrow closed/revoked
каждый RegionUse closed/quarantined
```

Это должно быть compile/admission-time property Job graph.

---

# 24. Я бы сделал ownership graph отдельной проверяемой моделью

Например:

```text
R0:
 entry owner = caller

stage A:
 BORROW R0

stage B:
 CONSUME R1
 PRODUCE R2

stage C:
 CONSUME R2

exit:
 RETURN R2 -> caller
```

Job verifier может статически проверить:

```text
no double consume
no lost ownership
no ownership duplication
no use after MOVE
all terminal paths settle ownership
```

Это почти линейная type system поверх generated SIP metadata.

Причём нет необходимости вводить новый язык — вся информация уже есть в:

```text
[Borrows]
[Consumes]
[ReturnsOwnership]
```

---

# 25. Cancellation должна быть одна на Job, но semantics остаются stage-specific

Я бы связывал Job с:

```text
CancellationScopeHandle
```

и распространял один scope по stages.

Но:

```text
Job cancellation
```

не означает:

```text
каждая уже начатая операция отменена
```

Для каждого stage действует его:

```text
EffectRevocationPolicy
```

Например:

```text
Stage A:
pure compute
→ cancellation before next stage

Stage B:
DMA submitted
→ CancelIfPossible

Stage C:
file publication
→ AdmissionOnly
```

Поэтому результат Job может быть:

```text
CancelledBeforeEffect
CancellationRequested
TooLateEffectMayExist
ProviderClosurePending
Completed
```

а не только `TaskCanceledException`.

---

# 26. Async не ломает Job, но разбивает fusion segments

Если Stage A:

```text
ValueTask<T>
```

и synchronously completes, Job продолжает без scheduler round trip.

Если реально suspends:

```text
await provider
```

Job executor сохраняет:

```text
JobRunId
stage index
Region leases
operation lease references
generation snapshot
```

но не:

```text
Span<T>
```

P07 прямо запрещает span across await.

После resume:

```text
revalidate lease
rematerialize Span
continue
```

Это хорошая естественная граница.

---

# 27. Job executor сам должен быть TrustedRuntime

Не `ManagedCap`.

Потому что ему нужно:

```text
private service implementation references
internal operation leases
session pins
RegionUse handles
sealed-object pins
```

Это часть TCB.

Но его surface должен быть очень маленьким.

Что-то вроде:

```text
SipJobRuntime
  BindPlan()
  Execute()
  Cancel()
  InspectEvidence()
```

Не:

```text
ResolveAnyService()
GetAnyCapability()
AccessAnyRegion()
```

---

# 28. Job identity сама по себе не должна давать authority

Если нужен reusable handle:

```text
SipJobHandle
```

я бы трактовал его по аналогии с P06 sealing:

```text
Job handle = which qualified plan/binding
```

а не:

```text
permission to execute it
```

Вызов всё равно требует:

```text
Job Execute capability
AND
stage-specific exact authorities
```

или stage capabilities должны быть явно derived/bound at Job creation и revalidated.

Но наличие:

```text
SipJobHandle
```

само по себе не может означать:

> можешь вызвать все SIP в графе.

---

# 29. Что можно кешировать на Job lifetime

Вот здесь есть хороший performance потенциал.

Можно один раз при `BindJob` вычислить:

```text
contract → exact generated thunk
messageId
service binding
expected service incarnation
exact capability-record reference
Region semantic requirements
seal type
schema
revocation-policy class
protocol transition template
```

Но нельзя закешировать:

```text
"authorized = true"
```

навсегда.

На каждую execution всё равно проверяются live:

```text
generation
epoch
state
lease
```

То есть cache:

```text
where to check
```

а не:

```text
result of the check
```

---

# 30. Это особенно поможет текущему `ResolveSession`

Сейчас `ResolveSession()` фактически:

```text
resolve session
resolve caller
validate every stored capability
scan every requirement
validate matching capabilities
```

При Job binding можно заранее получить:

```text
Stage B requires capability record #172
Stage C requires capability record #284
```

Тогда invocation делает:

```text
validate exact #172
validate exact #284
```

а не:

```text
scan capability list
```

Это ровно тот fast path, который разрешают `PERF-005/006`.

---

# 31. Текущий benchmark уже показывает, почему это интересно

P13 на конкретной Release/.NET 11/16-CPU Windows машине получил примерно:

| Primitive                     |          median |
| ----------------------------- | --------------: |
| capability lookup             |     ~0.4–0.5 μs |
| Region validate               |     ~0.2–0.3 μs |
| session pin revalidation      |         ~0.2 μs |
| seal resolve/pin              |         ~0.6 μs |
| lexical borrow acquire+return |         ~1.1 μs |
| MOVE 64-byte Region           |         ~3.2 μs |
| raw empty SIP send+receive    |         ~2.5 μs |
| SIP with 4 exact capabilities |         ~3.8 μs |
| external operation lifecycle  | ~40.2 μs median |

Но есть важная тонкость.

`P13_PERFORMANCE_BASELINE.json` называет тест:

```text
single-sip-empty-call
```

однако код benchmark на самом деле делает:

```csharp
SendCopyV2(...)
Receive(...)
```

То есть это **не полный `InvokeSessionAsync` request/reply** с:

```text
EndpointSession
InvocationRegistry
ResponseRegistry
TaskCompletionSource
publication
```

Следовательно, `2.5 μs` — скорее масштаб базовой channel boundary, а не полный RPC latency.

Именно поэтому Job fast path потенциально интересен ещё сильнее.

---

# 32. Представим 4-stage pipeline

Без fusion:

```text
A → B
B → C
C → D
```

Каждый hop может материализовать:

```text
session resolution
pin
capability validation
channel send
queue
receive
invocation correlation
response publication
waiter
```

При Job:

```text
external ingress
      ↓
A sentry
      ↓
B sentry
      ↓
C sentry
      ↓
D sentry
      ↓
external publication
```

То есть три внутренних transport boundaries исчезают.

Даже если security checks каждой стадии остаются.

---

# 33. Это важное различие: security overhead не равен IPC overhead

Пусть стоимость обычного hop:

```text
Tnormal =
    security
  + protocol
  + enqueue/dequeue
  + correlation
  + response
  + scheduling
```

Job stage:

```text
Tjob =
    security
  + direct dispatch
```

Поэтому:

```text
Savings ≈
    queue
  + envelope
  + invocation bookkeeping
  + response bookkeeping
  + async scheduling
```

на каждом fused edge.

---

# 34. Для длинных Job выигрыш растёт примерно с количеством внутренних boundaries

Если `N` stages:

обычный pipeline имеет примерно:

```text
N-1 internal SIP transports
```

Job сохраняет:

```text
N security stage transitions
```

но может иметь всего:

```text
1 external ingress
1 external egress
```

В упрощённой модели:

```text
Tnormal ≈ N × (S + IPC) + work

Tjob ≈ N × S + 2×IPC + work
```

где `S` — security transition.

Следовательно:

```text
Δ ≈ (N-2) × IPC
```

для длинной цепочки.

Это не benchmark result, а архитектурная модель.

---

# 35. На маленьких Job savings могут быть скромные

Например:

```text
A -> B
```

и оба метода выполняют несколько десятков наносекунд работы.

Job всё равно должен сделать:

```text
capability
session generation
Region authority
```

Поэтому direct ordinary method call останется дешевле.

Job нужен там, где хочется сохранить isolation semantics.

То есть сравнение должно быть не:

```text
Job vs function call
```

а:

```text
Job vs multiple protected SIP boundaries
```

---

# 36. У Job появляется ещё одна большая оптимизация — scheduler locality

При независимых SIP:

```text
A scheduled
A responds
B wakes
B scheduled
B responds
C wakes
```

Job executor может выполнить:

```text
A
B
C
```

на одном worker/VT, пока нет barrier.

Это улучшает:

```text
instruction cache locality
data cache locality
branch predictor locality
scheduler overhead
Task scheduling
```

Особенно если `OwnedRegion` только что обрабатывался A и сразу используется B.

---

# 37. Можно сделать two-level scheduling

Например Job:

```text
Stage 1
    │
 ┌──┴──┐
 ▼     ▼
2A     2B
 └──┬──┘
    ▼
Stage 3
```

Runtime может решить:

```text
Stage1 + Stage3 serial
2A and 2B parallel
```

При 4-way SMT HybridCPU это естественный кандидат.

Но Job говорит только:

```text
dependencies
execution class
resource/authority requirements
```

Не:

```text
VT2
lane4
lane6
```

---

# 38. Я бы ввёл понятие `FusionBarrier`

Не каждый SIP boundary можно убрать.

Пример barrier types:

```text
ExternalEffect
Publication
OwnershipSettlement
AsyncProviderWait
CrossRuntime
UnqualifiedNative
ConfidentialDomain
```

Job compiler делит граф:

```text
Segment 0
   ↓
FusionBarrier
   ↓
Segment 1
   ↓
FusionBarrier
   ↓
Segment 2
```

Это позволяет не пытаться сделать одну гигантскую транзакцию.

---

# 39. Cross-runtime fallback должен быть семантически прозрачным

Очень хороший property:

```text
тот же JobPlan
```

должен уметь работать:

```text
A,B,C same runtime
```

как fused path,

а:

```text
A,B same runtime
C remote runtime
```

как:

```text
[A,B fused]
    ↓
normal SIP transport
    ↓
C
```

То есть fusion — optimization.

Не ABI.

Очень важная архитектурная черта.

---

# 40. Тогда topology может меняться без изменения приложения

Сегодня:

```text
Runtime X:
 A B C
```

Завтра:

```text
Runtime X:
 A B

Runtime Y:
 C
```

Job compiler/runtime просто вставляет transport barrier:

```text
A -> B
   fused

B -> C
   SIP
```

Security semantics те же.

Это очень похоже на оптимизацию distributed/dataflow graph, только внутри capability ОС.

---

# 41. Это ещё и хороший путь для service aggregation

Например можно иметь обычные SIP:

```text
IFileService
ICompressionService
IEncryptionService
IStorageService
```

и не создавать вручную новый giant:

```text
ICompressedEncryptedStorageService
```

Для workload можно сформировать Job:

```text
Read
 ↓
Compress
 ↓
Encrypt
 ↓
Write
```

То есть reusable small services сохраняются, а performance получается близкой к fused implementation.

Это сильное архитектурное преимущество.

---

# 42. Но тут появляется confused-deputy риск

Очень важный.

Job executor имеет доступ ко многим stages.

Он не должен делать:

```text
SIP-B требует capability X

у Job executor самого есть X

→ разрешить B
```

Authority должен происходить из точно заявленного principal lineage:

```text
caller-provided
или
explicit delegated authority
или
manifest-bound service authority
```

и это должно быть указано per-stage.

Иначе Job превратится в huge privileged confused deputy.

---

# 43. Поэтому в StageDescriptor нужен `AuthoritySource`

Например:

```text
CallerExplicit
DelegatedFromCaller
ServiceManifestStatic
PreviousStageReturned
KernelOnly
```

И generator/admission должен запрещать implicit escalation.

Особенно:

```text
PreviousStageReturned
```

должен означать capability/handle, который по contract действительно объявлен как returned authority.

Не arbitrary DTO.

---

# 44. Ещё один нюанс: protocol state

`ChannelRegistry` сегодня проверяет:

```text
ProtocolDefinition
current state
TryTransition(...)
```

При direct Job path это нельзя просто выбросить.

Для Job можно заранее компилировать:

```text
StageProtocolTransition
```

и выполнять transition в session/protocol owner без queue.

То есть:

```text
protocol state transition remains
channel storage disappears
```

Это ещё один пример правильного separation.

---

# 45. То же самое с invocation lifecycle

Не обязательно на каждый внутренний stage создавать полноценный:

```text
EndpointSessionInvocationRegistry.Record
```

если stage synchronous/local и completion нужен только Job executor.

Можно иметь lightweight:

```text
JobStageAttempt
```

как correlation evidence.

Но он не должен стать новым source-of-truth для обычной SIP invocation authority.

Я бы ограничил это:

```text
pure intra-job stage completion
```

а если есть:

```text
cancel independently
public response
provider effect
external observer
```

использовать настоящий `EndpointSessionInvocationRegistry`.

---

# 46. Это даёт два уровня stage execution

```text
INLINE stage

no independent external lifecycle
no independent cancellation
no external publication
→ lightweight Job stage state
```

и:

```text
MATERIALIZED stage

needs observable invocation lifecycle
→ ordinary EndpointSessionInvocationRegistry
```

То есть runtime может materialize только те boundaries, которые реально нужны.

---

# 47. Это и есть настоящий смысл «selective aggregation»

Не:

```text
все SIP одного runtime всегда fuse
```

А:

```text
выбранный call graph
+
конкретный workload
+
qualification rules
=
fusion decision
```

Два одних и тех же SIP могут иногда работать fused, иногда normal.

Например:

```text
Decode → Filter
```

в media pipeline — fused.

Но user напрямую вызывает Filter отдельно — обычный SIP.

---

# 48. Как бы я вписал это в текущий roadmap

Это уже **post-SingCap-M-v1**, потому что P13 формально закрыл v1 на уровне `QualifiedManaged`, а Job меняет runtime execution path.

Я бы не вклинивал его задним числом в P08.

Я бы сделал отдельное продолжение:

| Phase | Содержание                                                |
| ----- | --------------------------------------------------------- |
| P14-0 | ADR: Job is execution plan/evidence, never authority      |
| P14-1 | generated `JobStageDescriptor` + graph verifier           |
| P14-2 | direct intra-runtime sentry thunk                         |
| P14-3 | Region MOVE/BORROW edge settlement and failure graph      |
| P14-4 | multi-session prepare/pin/commit + deterministic ordering |
| P14-5 | async/effect `FusionBarrier` support                      |
| P14-6 | dynamic selection of precompiled qualified stages         |
| P14-7 | HybridCPU scheduling hints / compute segmentation         |
| P14-8 | security + perf qualification vs normal SIP               |

Причём P14 должен использовать P04–P13 primitives, а не создавать:

```text
JobCapabilityAuthority
JobRegionAuthority
JobSessionAuthority
```

---

# 49. Самый минимальный MVP я бы делал ещё уже

Не сразу DAG.

Первый вариант:

```text
linear chain
2–4 ManagedCap SIPs
same runtime
no external providers inside chain
bounded values + BORROW/MOVE only
one final response
no branches
no independently cancellable intermediate calls
```

Например:

```text
Decode
 ↓
Transform
 ↓
Hash
```

Это позволит проверить самый важный тезис:

```text
security boundaries remain intact

while

channel/response boundaries disappear
```

---

# 50. Потом добавить read-only DAG

Следующий очень безопасный шаг:

```text
          ┌─ Hash
Input ────┤
          └─ Classify
```

Потому что read/read Region sharing проще ownership MOVE graph.

А уже затем:

```text
multi-stage MOVE
external effects
conditional flow
```

---

# 51. Какие benchmarks нужны именно для Job

Текущий P13 benchmark недостаточен для доказательства Job benefit.

Нужно сравнивать одинаковую семантику:

```text
A → B → C → D
```

в четырёх вариантах:

| Variant                | Boundary                                    |
| ---------------------- | ------------------------------------------- |
| direct unsafe baseline | plain method calls                          |
| normal SIP             | full session/channel/response               |
| Job fused              | sentry per stage, no intermediate transport |
| split-runtime Job      | fused locally + one real SIP barrier        |

И отдельно измерять:

```text
empty stages
small copied values
BORROW 4K/64K/1M
MOVE 4K/64K/1M
fan-out read DAG
mixed compute
failure settlement
cancellation
```

---

# 52. Главная ожидаемая метрика — не просто latency

Я бы измерял:

```text
ns per security transition
ns per materialized transport boundary
allocations per stage
GC bytes/op
queue operations/op
Task allocations/op
authority lookups/op
Region transitions/op
L1/L2/LLC misses
context/worker switches
```

Тогда станет видно, что Job действительно убирает transport, а не secretly ослабляет security.

---

# 53. Особенно важен allocation count

Сегодня full async path имеет:

```text
ResponseRegistry.CompletionRecord
TaskCompletionSource<ResponseEnvelope>
envelopes
invocation records
```

Для обычного coarse-grained SIP это нормально.

Для:

```text
A → B → C → D
```

в tight loop это становится GC pressure.

Job runtime может иметь:

```text
preallocated JobRunFrame
fixed EdgeSlot[]
fixed StageState[]
```

или pool.

И для synchronous fused segment вообще уйти почти в allocation-free execution.

---

# 54. Идеальный Job hot path

В крайнем оптимизированном виде я бы хотел видеть:

```text
Job entry
  │
  ├─ validate Job binding generation
  ├─ exact capability refs
  ├─ exact Region refs
  ├─ pin required sessions
  └─ commit segment
       │
       ▼
stage A generated sentry
       │
       ▼
direct call
       │
       ▼
Region ownership/use transition
       │
       ▼
stage B generated sentry
       │
       ▼
direct call
       │
       ▼
stage C generated sentry
       │
       ▼
final ownership settlement
       │
       ▼
one response publication
```

Без:

```text
service discovery
capability enumeration
reflection
serialization
channel enqueue/dequeue
TaskCompletionSource per edge
response envelope per edge
scheduler wakeup per edge
```

---

# 55. Тогда `SipJob` становится очень интересной архитектурной сущностью

Не просто:

> «пакет RPC-вызовов».

А скорее:

> **capability-safe software dataflow program**, скомпилированный из обычных SIP contracts.

То есть обычный SIP остаётся:

```text
unit of isolation/API composition
```

а SipJob становится:

```text
unit of optimized execution composition
```

Это хороший separation.

---

# 56. И здесь появляется действительно сильная связь с HybridCPU

В обычной системе OS видит:

```text
many IPC calls
```

В этой модели OS/runtime получает:

```text
bounded dependency graph
+
Region ownership graph
+
semantic compute classes
+
authority graph
```

То есть гораздо больше информации для scheduling.

Получается:

```text
SIP contracts
        ↓
SipJob graph
        ↓
SingCap admission graph
        ↓
Region dataflow
        ↓
HybridCPU semantic scheduling
        ↓
SMT / typed lanes / DSC / Matrix
```

При этом каждый слой остаётся owner своей части решения.

Это, по-моему, гораздо интереснее, чем просто «ускоренный intra-process RPC».

---

## Главная архитектурная формула

Я бы свёл `SipJob` к следующему:

```text
              обычный SIP

security boundary
+
transport boundary
+
publication boundary


             SipJob edge

security boundary
+
ownership boundary where required

BUT

transport boundary may disappear
publication boundary may be deferred
scheduler boundary may disappear
```

Именно это позволяет получить скорость **без отмены compartment model**.

Самая важная вещь, которую я бы не позволил нарушить: `SipJob` не должен становиться новым authority universe и не должен позволять raw CLR references между SIP. Если сохранить эти два инварианта, то direct generated sentry + fused ownership graph выглядит как очень естественный следующий шаг после текущего `SingCap-M` roadmap.
