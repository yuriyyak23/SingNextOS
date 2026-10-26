Да. Если смотреть именно на `SingCap-M` как на механизм **ускорения SIP**, главный тезис такой:

> `SingCap-M` способен дать выигрыш не потому, что «capability lookup быстрее syscall», а потому что позволяет сделать дорогие security-проверки **один раз на границе операции**, после чего передавать данные через ownership/borrow без копирования и работать с ними обычными `Span<T>`/memory operations.

В правильной реализации получается разделение:

```text
CONTROL PATH                     DATA PATH

SIP sentry
  ↓
session check
  ↓
capability/effect lease          OwnedRegion / OwnedBuffer
  ↓                                      │
Region authority check                  │
  ↓                                      │
admit operation                         │
  └─────────────────────────────────────┤
                                        ↓
                              ordinary memory access
                              no capability lookup
                              per load/store
```

Именно правая сторона потенциально даёт основной performance gain.

---

# 1. Самый большой выигрыш: SIP перестаёт копировать большие payload

Это уже видно непосредственно в текущем коде.

При `[Consumes]` текущий `ChannelRegistry.SendCore()` делает примерно:

```text
validate region
       ↓
RegionAuthority.Transfer(...)
       ↓
new RegionGeneration / owner
       ↓
OwnedBuffer.TransferForRuntime(...)
       ↓
receiver gets new wrapper
```

Сам массив данных при этом **не переписывается**.

`OwnedBuffer<T>` хранит:

```csharp
Storage {
    T[] Data;
}
```

При transfer создаётся новый `OwnedBuffer<T>` вокруг того же `Storage`, а старый owner становится invalid.

Поэтому логически:

```text
обычный IPC с копированием:

A memory
  ↓ memcpy N bytes
kernel/buffer
  ↓ memcpy N bytes
B memory
```

против:

```text
SingCap MOVE:

Storage = [ N bytes ]

A owns Storage
        ↓
Region owner/generation update
        ↓
B owns same Storage
```

Performance-модель принципиально разная:

```text
Tcopy(N) ≈ Tipc + C × N

Tmove(N) ≈ T_sentry
         + T_cap
         + T_region
         + T_metadata
```

То есть для MOVE стоимость относительно размера сообщения становится почти константной:

```text
copy: O(N)
move: O(1) metadata
```

Конечно, реальные page mappings, cache migration и TLB не бесплатны. Но **байты payload не надо прогонять через CPU memcpy**.

### Где это особенно важно

Например pipeline:

```text
Camera
  ↓
Decoder SIP
  ↓
Filter SIP
  ↓
ML SIP
  ↓
Display SIP
```

Пусть один frame имеет размер `N`.

Классический design легко превращается в:

```text
Camera -> Decoder : copy N
Decoder -> Filter : copy N
Filter -> ML      : copy N
ML -> Display     : copy N

≈ 4N bytes дополнительного traffic
```

Ownership pipeline:

```text
Region R
 ↓ MOVE
Decoder
 ↓ MOVE
Filter
 ↓ MOVE
ML
 ↓ MOVE
Display
```

Сами `N` байт вообще не обязаны перемещаться между стадиями.

Получается:

```text
data movement: O(N × stages)
                     ↓
authority movement: O(stages)
```

Для крупных payload это потенциально **самый большой выигрыш всей архитектуры SIP**.

---

# 2. `BORROW` ещё интереснее для read-only processing

Для `[Borrows]` происходит не transfer ownership, а:

```text
Region owner A
      ↓
AcquireLoan()
      ↓
BorrowLease<T>
      ↓
service B reads same Storage
```

То есть вместо:

```text
A
 ↓ copy
B reads private copy
```

имеем:

```text
                Region R
                 /   \
               A       B
             owner   readonly borrow
```

При этом SingCap обеспечивает temporal boundary:

```text
BorrowGeneration
BorrowLeaseLifetime
RegionGeneration
```

То есть можно безопаснее делать то, ради чего в обычных системах часто используют shared memory.

А shared memory обычно приводит к другой проблеме:

```text
shared mutable mapping
        ↓
locks
atomics
cache-line bouncing
lifetime coordination
```

SingCap-BORROW говорит:

```text
reader may read

owner cannot perform incompatible mutation
until lease closes
```

Это позволяет заменить некоторую runtime synchronization на **authority state transition**.

---

# 3. Второй очень большой выигрыш: capability не проверяется на каждый load/store

Это, на мой взгляд, критически важное performance-решение SingCap-M.

В спецификации это прямо закреплено как `PERF-004`:

```text
No capability-table access occurs
per ordinary scalar/array/Span element access
after successful borrow/view acquisition.
```

То есть SingCap не пытается делать software-CHERI:

```text
for every load:
    lookup capability table
    validate bounds
    validate owner
    validate generation
    load
```

Это было бы очень дорого.

Вместо этого:

```text
Acquire borrow
      ↓
validate authority once
      ↓
produce bounded Span<T>
      ↓
for (...) {
    x = span[i];
}
```

В цикле остаются практически обычные CLR/JIT/AOT accesses.

---

## Почему это фундаментально

Предположим сервис делает обработку массива:

```csharp
var span = lease.Span;

for (var i = 0; i < span.Length; i++)
    sum += span[i];
```

Security cost:

```text
before loop:
  session check
  capability check
  Region check
  borrow check

inside loop:
  normal array/span bounds + memory access
```

а не:

```text
inside loop:
  CapabilityAuthority lookup
  RegionAuthority lookup
  generation check
  revocation check
  bounds
  load
```

Поэтому чем больше данных сервис обрабатывает за один SIP вызов, тем сильнее security overhead амортизируется.

Можно представить:

```text
              security checks
                  │
                  ▼
SIP ──────── [ O(1) ] ────────────────────
                  |
                  + 1 byte
                  + 1 KB
                  + 1 MB
                  + 1 GB
```

Стоимость capability admission остаётся примерно одной и той же, а полезная работа растёт.

---

# 4. Отсюда важный вывод: SingCap-M особенно любит coarse-grained SIP

Лучший workload:

```text
one SIP request
        ↓
large amount of useful work
```

Например:

```text
Decode(buffer)
Compress(buffer)
Hash(buffer)
Encrypt(buffer)
MatrixMultiply(buffer)
ParseDatabaseBlock(buffer)
ProcessFrame(buffer)
RunInference(buffer)
```

Хуже:

```text
GetByte()
GetByte()
GetByte()
GetByte()
...
```

Потому что тогда security/control overhead приходится платить на каждый крошечный SIP.

В терминах useful work:

```text
                 useful compute
efficiency ≈ ---------------------------
              SIP overhead + useful compute
```

Для большого operation:

```text
SIP overhead << compute
```

Для tiny RPC:

```text
SIP overhead ≈ или > compute
```

---

# 5. Generated SIP может быть значительно дешевле generic RPC

Текущая архитектура уже использует source generation.

Например `ClientRuntimeAdapterGenerator` генерирует конкретные вызовы:

```text
Send_Foo(...)
   ↓
_runtime.Invoke(...)
```

или:

```text
_runtime.InvokeOwnershipPair(...)
```

То есть runtime не обязан на каждый call делать:

```text
reflection
 ↓
method lookup
 ↓
schema discovery
 ↓
JSON serialization
 ↓
object graph serialization
 ↓
dynamic dispatch
```

Схема известна compile-time.

Генератор уже ограничивает request shape:

```text
primitive
enum
bounded immutable payload
OwnedBuffer / OwnedRegion
Borrow + Consume pair
```

Это очень хороший performance property.

---

## Сравните generic RPC

Условный RPC:

```text
object
 ↓
serializer
 ↓
wire representation
 ↓
allocate buffer
 ↓
copy
 ↓
deserialize
 ↓
allocate object graph
 ↓
dispatch
```

SIP целится в:

```text
generated typed stub
 ↓
known message id
 ↓
known payload shape
 ↓
known capability requirement
 ↓
known ownership operation
 ↓
dispatch
```

При NativeAOT/JIT это ещё и потенциально гораздо лучше inline/optimize.

---

# 6. SingCap-M позволяет превратить sentry в очень маленькую машину

Особенно интересная возможность появляется из-за того, что authorization semantics известна заранее.

Например contract:

```csharp
[RequiresCapability(
    ResourceKind.Network,
    "socket",
    CapabilityRights.Write)]
ValueTask SendAsync(
    [Borrows] OwnedBuffer<byte> buffer);
```

Generator знает:

```text
message = Send
required operation = Write
resource class = Network
memory disposition = Borrow
response semantics = ...
```

Поэтому можно сгенерировать sentry вида:

```text
check session generation
check exact capability token
acquire operation authority
acquire Region read-use
dispatch
```

без generic policy interpreter.

То есть вместо:

```text
generic ACL engine
 -> enumerate rules
 -> names/groups/SIDs
 -> dynamic decision
```

получается почти:

```text
dictionary lookup
generation compare
rights bit test
epoch compare
region generation compare
```

Это очень CPU-friendly code.

---

# 7. `EndpointSession` позволяет амортизировать service discovery

До session:

```text
find service
verify contract
resolve provider
validate admission capabilities
create channel
bind caller/service identities
```

После:

```text
EndpointSessionHandle
```

То есть это аналог:

```text
connect once
call many times
```

а не:

```text
resolve service
authenticate
authorize
connect

для каждого RPC
```

В идеальном SingCap fast path session должна превращаться почти в:

```text
SessionId
SessionGeneration
Caller
Service
compiled SentryPlan
```

После чего вызов:

```text
session lookup
+
epoch/generation validation
+
message-specific effect admission
```

---

# 8. Но текущий код здесь ещё оставляет много performance на столе

Это важный момент.

В текущем `RuntimeKernel.ResolveSession()` на **каждом invocation** выполняется:

```text
for every capability stored in session:
    ValidateCapability(...)

for every requirement:
    scan session capabilities
        Validate(...)
```

А затем `ChannelRegistry.SendCore()` делает ещё:

```text
ValidateCapabilities(...)
```

То есть capability checking сейчас местами повторяется.

Грубо может получиться:

```text
InvokeSession
 │
 ├─ ResolveSession
 │   ├─ validate cap1
 │   ├─ validate cap2
 │   ├─ ...
 │   └─ scan requirements
 │
 ├─ Acquire session pin
 ├─ Revalidate pin
 │
 └─ Send
     └─ validate message capability again
```

С точки зрения security это консервативно.

С точки зрения performance — **это одно из первых мест для оптимизации**.

---

# 9. SingCap-M даёт архитектурно безопасный способ эту работу убрать

Спецификация уже допускает reuse validation только в пределах точной authority lifetime:

```text
PERF-005:
Sentry may reuse validation results
only for exact invocation/effect lease

PERF-006:
Caches must include revocation/resource/session epochs
```

Именно здесь можно сделать мощный fast path.

При `OpenSession`:

```text
Capability token
   ↓
resolve once

Requirement:
    Compute / dsc1 / Execute

   ↓

SessionSentryPlan:
    CapRef = #1234
    RequiredEpoch = E
    ResourceGeneration = G
    Operation = Execute
```

При вызове:

```text
check session generation
check cap epoch/generation
AcquireOperationAuthority()
```

Вместо:

```text
enumerate capability collection
search correct capability
compare string resource
repeat validation
```

То есть:

```text
generic O(requirements × caps)
          ↓
compiled O(requirements)
```

а для типичного SIP method с одной capability:

```text
≈ O(1)
```

---

# 10. Ещё один потенциальный выигрыш: operation lease вместо постоянной revalidation

`SingCap-M` вводит:

```text
AcquireOperationAuthority(...)
       ↓
OperationAuthorityLease
```

И это очень важный performance primitive.

Идея:

```text
security decision

        t0
        │
        ▼
AcquireOperationAuthority
        │
        ├───────────────────────────────┐
        │ admitted effect lifetime      │
        └───────────────────────────────┘
```

Не нужно внутри длинной операции многократно спрашивать:

```text
capability still valid?
capability still valid?
capability still valid?
...
```

Для уже admitted effect используется явно заданная revocation policy:

```text
GrandfatherAdmitted
AdmissionOnly
CancelIfPossible
```

То есть expensive authority resolution можно отделить от hot execution.

---

# 11. Особенно большой выигрыш на chains из SIP сервисов

Допустим:

```text
Client
 ↓
Filesystem
 ↓
Compression
 ↓
Encryption
 ↓
Storage driver
```

При традиционном RPC дизайн легко получает:

```text
copy
serialize
copy
deserialize

× 4 boundaries
```

SingCap может передавать:

```text
OwnedRegion R
   ↓ MOVE/BORROW
Filesystem
   ↓
Compression
   ↓
Encryption
   ↓
Storage
```

В каждом hop меняется:

```text
authority
generation
owner/use state
```

а не содержимое region.

Поэтому cost pipeline становится примерно:

```text
S × authority transitions
+
actual useful compute
```

вместо:

```text
S × payload copying
+
S × serialization
+
useful compute
```

Где `S` — число service boundaries.

Чем больше pipeline — тем интереснее SingCap.

---

# 12. Ownership может уменьшить cache-coherence traffic

Это менее очевидный, но потенциально большой плюс.

Shared mutable memory:

```text
Core 0       Core 1
  │            │
  └── cache line ──┘
       ↕
    ownership bouncing
```

Если оба SIP мутируют один buffer:

```text
locks
atomics
MESI transfers
cache invalidations
false sharing
```

SingCap MOVE делает:

```text
time t0:
Core A owns R

time t1:
A cannot mutate
B owns R
```

То есть одновременно имеется один mutable owner.

Это не отменяет перенос cache lines между cores после MOVE, но сильно уменьшает возможность продолжительного конкурентного write-sharing.

Для streaming workloads это очень полезно:

```text
producer -> consumer -> producer -> consumer
```

может быть значительно дешевле, чем хаотическое shared writable state.

---

# 13. BORROW позволяет дешёвый fan-out для read-heavy workloads

Например:

```text
             buffer
               │
     ┌─────────┼──────────┐
     ↓         ↓          ↓
   Hash      Scan       Inspect
```

Три reader'а могут потенциально работать с одним backing region.

Без этого:

```text
copy N × 3
```

С borrow:

```text
3 × metadata/lease
+
read same memory
```

Опять же это требует правильной cache/topology реализации, но architecture не заставляет копировать payload.

---

# 14. На HybridCPU выигрыш может усилиться ещё сильнее

Здесь начинается интересный co-design.

SIP может передавать не данные в command message, а:

```text
intent
+
Region authority
```

Например:

```text
Compute SIP

CopyAsync(
    source BORROW,
    destination MOVE)
```

После SingCap admission:

```text
source Region
destination Region
        ↓
HybridCPU platform bridge
        ↓
DSC1
```

То есть CPU control path занимается только:

```text
capability
region handles
operation descriptor
```

а bulk data остаётся data plane.

При реальном DSC/Matrix/L7 backend:

```text
ALU/CPU
   ↓
tiny control message

lane6 / accelerator
   ↓
large data
```

Это может убрать:

```text
CPU memcpy
intermediate driver copy
SIP copy
accelerator staging copy
```

Но здесь важно: **это architectural target**, а не доказанная сегодня производительность HybridCPU hardware backend.

---

# 15. Где SingCap-M почти наверняка НЕ даст выигрыша

На tiny RPC всё может быть наоборот.

Например:

```text
int GetCounter()
```

Полезной работы почти нет.

А SingCap должен сделать:

```text
process lookup
session lookup
session generation
capability lookup
lineage/revocation
pin
protocol transition
queue
response correlation
TaskCompletionSource
publication
```

Здесь обычный:

```text
direct call
```

или чрезвычайно оптимизированный syscall вполне может быть быстрее.

Поэтому я бы ожидал такую картину:

| Workload                  |                         Вероятный эффект SingCap/SIP |
| ------------------------- | ---------------------------------------------------: |
| 8-byte RPC                |                                потенциально проигрыш |
| tiny control message      | примерно паритет / проигрыш до fast-path оптимизации |
| 4–64 KB immutable payload |                       начинает становиться интересно |
| MB-sized MOVE/BORROW      |                         потенциально большой выигрыш |
| multimedia pipeline       |                              очень большой потенциал |
| storage/network packets   |          большой потенциал при batching/region pools |
| ML/matrix buffers         |                              очень большой потенциал |
| DMA/GPU/CXL buffers       |                              очень большой потенциал |
| millions tiny RPC/s       |           требует серьёзной оптимизации control path |

Размеры здесь не benchmark thresholds, а качественная зависимость: crossover нужно измерять.

---

# 16. Сейчас главный performance bottleneck SingCap — `CapabilityAuthority._gate`

В текущем коде:

```csharp
private readonly object _gate = new();
```

и практически:

```text
Validate
Delegate
Revoke
AcquireOperationAuthority
ResolveCapabilityId
...
```

входят под глобальный lock.

Для одного core это нормально.

Для:

```text
32 cores
+
1000 SIP sessions
+
millions calls/sec
```

это станет точкой сериализации.

Особенно опасна схема:

```text
Core0 ─┐
Core1 ─┤
Core2 ─┼── CapabilityAuthority._gate
Core3 ─┤
...    │
CoreN ─┘
```

То есть архитектурно SingCap допускает очень быстрый path, но текущая reference implementation пока не является highly scalable capability engine.

---

# 17. Второй bottleneck — capability lineage

Сейчас `ValidateLineage()` идёт вверх по delegation chain:

```text
child
 ↓
parent
 ↓
parent
 ↓
...
```

до bounded depth примерно 16.

Это безопасно, но SIP hot path может получить:

```text
O(delegation depth)
```

dictionary accesses под capability lock.

Для frequent SIP я бы заменил это на revocation tree/node epoch:

```text
Capability
   │
   └── RevocationNode
          Epoch
```

При delegation:

```text
child references revocation lineage node
```

Validation:

```text
record epoch == node live epoch
```

тогда типичный path приблизится к O(1).

SingCap specification это архитектурно позволяет.

---

# 18. Третий bottleneck — string `ResourceId`

Сейчас операции включают сравнение:

```csharp
record.ResourceKind != resourceKind
string.Equals(record.ResourceId, resourceId, ...)
```

Для control plane это нормально.

Но для миллиона SIP calls/second лучше:

```text
ResourceId = 64/128-bit stable interned identity
```

а не:

```text
"compute:dsc1-copy:v1"
```

Строка хороша для manifest/debug/evidence.

Hot path лучше:

```text
ResourceKey
```

с заранее resolved value.

---

# 19. Четвёртый bottleneck — текущий async response transport

`ResponseRegistry` создаёт на request:

```csharp
TaskCompletionSource<ResponseEnvelope>
```

с:

```text
RunContinuationsAsynchronously
```

Это даёт хорошую семантику, но для high-frequency SIP означает:

```text
allocation
Task object/state
scheduler continuation
GC pressure
```

Для bulk operation это ничто.

Для `5–10 млн tiny messages/s` — огромная проблема.

Production fast path я бы строил на:

```text
pooled IValueTaskSource
или
fixed completion slots
или
ring-buffer descriptors
```

То есть:

```text
Session
  └── preallocated invocation slots
```

вместо allocation на каждый call.

---

# 20. Возможен ещё более быстрый intra-runtime SIP path

Если два ManagedCap SIP находятся внутри одного квалифицированного managed runtime, можно теоретически сделать:

```text
caller
 ↓
generated sentry
 ↓
callee
```

без:

```text
kernel mode transition
page table switch
scheduler context switch
serialization
```

При этом security transition остаётся:

```text
caller identity
session
capability lease
region lease
```

То есть что-то концептуально похожее на:

```text
protected method call
```

а не Unix IPC.

Это потенциально один из самых интересных результатов ManagedCap.

Но текущий `RuntimeSipClientTransport` всё ещё идёт через:

```text
RuntimeKernel
Channel
Queue
ResponseRegistry
```

Поэтому такой «near function-call SIP» пока надо рассматривать как **очень логичный optimization target**, а не текущую измеренную характеристику.

---

# 21. Как я бы построил SingCap/SIP fast path

На практике я бы сделал его так:

```text
                     OPEN SESSION

contract metadata
      +
capabilities
      +
caller/service identities
      ↓
compile once
      ↓
┌───────────────────────────────────┐
│ SessionSentryPlan                 │
│                                   │
│ caller generation                 │
│ service generation                │
│ session generation                │
│ revocation epoch refs             │
│ message → capability reference    │
│ message → Region disposition      │
│ message → response shape          │
└───────────────────────────────────┘


                     INVOKE

messageId
    ↓
array/table index
    ↓
check session epoch
    ↓
AcquireOperationAuthority(exact ref)
    ↓
Region MOVE/BORROW
    ↓
direct generated dispatch / queue
```

Никаких:

```text
service lookup
capability enumeration
string lookup
reflection
schema parser
ACL walk
object serialization
```

в основном hot path.

---

# 22. Тогда стоимость SIP можно приблизить к нескольким O(1) operations

Идеальная steady-state модель:

```text
1 session lookup
1 generation comparison
1 capability lookup
1 revocation epoch comparison
1 operation-right test
1 Region lookup
1 Region generation test
1 queue/direct dispatch
```

А данные вообще не проходят через control path.

То есть архитектурно:

```text
SIP control cost ≈ metadata cost

data cost ≈ actual application algorithm
```

Это именно то, чего хочется добиться.

---

# 23. Очень важная оптимизация: `Span` брать один раз

Есть маленькая деталь текущего `BorrowLease<T>`.

Индексатор:

```csharp
public ref readonly T this[int index] => ref Span[index];
```

а `Span` вызывает:

```text
EnsureValid()
```

Поэтому писать:

```csharp
for (...)
    x += lease[i];
```

хуже, чем:

```csharp
var span = lease.Span;

for (...)
    x += span[i];
```

Во втором случае validity boundary проверяется перед получением span, а loop работает на локальном `ReadOnlySpan<T>`.

Это очень хорошо соответствует философии `PERF-004`.

Для SingCap SDK/analyzer я бы даже добавил рекомендацию/diagnostic:

```text
SIPPERF001:
Do not repeatedly access BorrowLease<T>.Span/indexer
inside a hot loop; materialize bounded Span once.
```

---

# 24. Где я ожидаю максимальный реальный эффект

Если ранжировать именно по **потенциалу ускорения SIP**, то картина примерно такая:

1. **Большие buffers через MOVE/BORROW** — самый сильный эффект. Убирается O(N) copy на IPC boundary.

2. **Многоступенчатые pipelines** — эффект суммируется с числом SIP boundaries.

3. **Device/DMA/DSC/GPU/CXL data paths** — SingCap позволяет оставить data в Region и передавать authority, потенциально убирая bounce buffers.

4. **Compute services** — один authority check перед миллионами обычных memory operations даёт почти нулевой относительный security overhead.

5. **Read-only fan-out** — BORROW вместо создания нескольких copies.

6. **High-frequency small RPC** — потенциал есть только после session/sentry fast-path, lock sharding и allocation removal. Текущая reference implementation здесь, скорее всего, не покажет чудес.

---

# 25. Поэтому ключевая performance формула SingCap-M такая

Я бы формализовал преимущество вот так:

### Conventional protected IPC

```text
T =
    protection transition
  + authorization
  + serialization
  + payload copy
  + scheduling
  + deserialization
  + work
  + reverse copy
```

### SingCap/SIP target

```text
T =
    sentry
  + authority lease
  + Region ownership/use transition
  + dispatch
  + work over original Region
  + ownership/publication transition
```

Для большого `N`:

```text
Conventional:
T ≈ K + αN

SingCap:
T ≈ K'
```

где речь именно об **IPC-specific overhead**; сама полезная обработка `N` байт, конечно, остаётся.

---

## И самая интересная мысль

В обычной системе stronger isolation часто означает:

```text
больше boundaries
→ больше IPC
→ больше copies/context switches
→ медленнее
```

`SingCap-M` пытается изменить именно эту зависимость:

```text
больше compartments
       ↓
больше authority transitions

НО НЕ ОБЯЗАТЕЛЬНО
       ↓
больше data movement
```

Если это довести до production fast path, то можно получить архитектуру, где **мелкая декомпозиция на SIP практически не штрафует bulk-data path**.

И вот это, на мой взгляд, является главным performance research case для SingCap-M.

При этом текущему коду я бы в первую очередь оптимизировал **четыре вещи**: предварительно компилировал message-specific `SessionSentryPlan`, убрал глобальный `_gate` из read-side capability validation, сделал O(1) revocation-lineage validation и заменил per-call `TaskCompletionSource` на pooled/fixed invocation slots. После этих изменений уже имеет смысл честно benchmark'ать `direct call → SIP copy → SIP MOVE/BORROW → Unix-domain-like IPC → shared-memory RPC` на payload'ах от tiny control messages до многомегабайтных Region.
