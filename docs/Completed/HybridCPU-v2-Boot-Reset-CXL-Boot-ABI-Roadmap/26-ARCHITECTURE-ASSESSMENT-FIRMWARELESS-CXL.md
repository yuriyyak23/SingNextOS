# HybridCPU-v2 × SingNextOS: оценка CXL и boot architecture

**Тип документа:** principal-level architecture assessment / design review  
**Дата:** 2026-09-18  
**Оцениваемая база:** `HybridCPU-v2`, `SingNextOS`, ранее подготовленный `HybridCPU-v2 Boot/Reset + CXL Boot ABI Roadmap`  
**Фокус:** современность, соответствие позиционированию CPU/OS, перспективность, простота, минимизация firmware, CXL Type-3 persistent memory, путь simulator → QEMU → hardware.

---

## 0. Краткий вывод

Архитектурная идея **HybridCPU-v2 + SingNextOS + CXL как OS-managed external memory fabric** сильная и современная. Особенно удачно совпадают три свойства проектов:

1. HybridCPU-v2 уже позиционируется не как commodity x86-like platform, а как исследовательская CPU/runtime architecture с явными state/evidence/replay boundaries.
2. SingNextOS строит authority вокруг capability/`OwnedRegion`/generation, а не вокруг физических device identifiers.
3. CXL сам по себе развивается именно в сторону memory expansion, pooling, switching, multi-host/fabric management, dynamic capacity и более богатой RAS/security модели.

Поэтому **CXL не следует встраивать в ISA и не следует отдавать firmware долгосрочное владение CXL topology**. Лучший архитектурный слой для CXL — platform/provider layer SingNextOS.

При этом первоначальная boot-схема из roadmap технически корректна, но для целевого позиционирования проектов **немного переусложнена на pre-OS стороне**. Связка:

```text
ROM
 -> PCIe enumeration
 -> CXL capability parsing
 -> mailbox/LSA
 -> HDM programming
 -> manifest
 -> Stage1
```

рискует со временем превратить Boot ROM в небольшой firmware stack, хотя вся философия SingNextOS говорит, что CXL должен принадлежать OS/provider layer.

### Рекомендуемый конечный профиль

Для собственного HybridCPU platform profile я рекомендую **не “firmware”, а Direct SingNext Boot**:

```text
Reset
  -> immutable Boot ROM
       - reset state
       - root of trust
       - protected boot state
       - verify local SingNext Boot Capsule
       - load capsule into boot SRAM/RAM
  -> SingNext Stage-1 Boot Capsule
       - PCIe/CXL discovery
       - BootVolumeId discovery
       - temporary HDM mapping
       - manifest verification
       - load kernel/services from CXL Type-3 PMEM
  -> SingNextOS kernel
       - fresh/revalidated provider admission
       - new runtime generations
       - replace boot mapping
       - normal OwnedRegion/provider authority
```

В этой модели **общего firmware layer нет вообще**. Есть:

- immutable hardware trust/reset substrate;
- небольшой локальный **OS-owned** Stage-1 capsule;
- SingNextOS runtime.

Это существенно лучше соответствует совместному позиционированию HybridCPU-v2 и SingNextOS, уменьшает immutable TCB, устраняет дублирование CXL stack и упрощает развитие CXL вместе с OS.

Если требуется абсолютный режим “Stage-1 тоже лежит только на CXL”, тогда CXL bootstrap неизбежно должен существовать **либо в ROM, либо в hardware sequencer**. Логически убрать его невозможно — его можно только перенести.

---

# 1. Итоговая оценка

| Критерий | Текущая идея CXL boot | После рекомендуемого упрощения |
|---|---:|---:|
| Архитектурная современность | **9/10** | **9.5/10** |
| Соответствие модели SingNextOS | **9.5/10** | **10/10** |
| Соответствие модели HybridCPU-v2 | **8.5/10** | **9.5/10** |
| Чистота authority model | **10/10** | **10/10** |
| Минимальность immutable TCB | **6.5/10** | **9/10** |
| Простота boot path | **6/10** | **8.5/10** |
| Simulator-first реализуемость | **9/10** | **9.5/10** |
| QEMU validation potential | **8.5/10** | **9/10** |
| Переносимость на generic real CXL servers | **6.5/10** | **6.5/10** |
| Переносимость на native HybridCPU SoC/platform | **9/10** | **9.5/10** |
| Перспективность на 5+ лет | **9/10** | **9.5/10** |
| Mainstream-совместимость boot model | **4.5/10** | **4/10** |
| Исследовательская ценность | **9.5/10** | **10/10** |

Главная оговорка: **boot напрямую с CXL persistent memory — не mainstream server boot model**. Это research/product differentiation для memory-centric системы. Зато сама CXL provider architecture и identity/authority separation очень хорошо совпадают с направлением CXL 3.x/4.0.

---

# 2. Что в существующей схеме уже сделано правильно

## 2.1. `deviceId = 0` отвергнут правильно

Это одно из самых сильных решений всего roadmap.

Нельзя делать долговременной OS identity:

- enumeration order;
- PCI BDF;
- device index;
- port ID;
- decoder ID;
- HPA;
- DPA;
- fabric route.

Все эти сущности описывают **текущую материализацию topology**, а не логическую установку OS.

SingNextOS уже формулирует ровно такой принцип для runtime: HDM decoder, HPA↔DPA, DPA, switch/port routes, MLD/LD binding и mailbox details должны оставаться provider-private. Discovery evidence не является authority.

Это хорошо масштабируется на:

- replacement;
- BDF reorder;
- switches;
- MLD;
- memory pooling;
- multi-path;
- fabric manager rebind;
- future CXL generations.

**Оценка: 10/10. Это необходимо сохранить без изменений.**

---

## 2.2. `BootVolumeId` как logical identity — удачный уровень абстракции

Разделение:

```text
physical device identity
logical boot-volume identity
boot-image identity
runtime OS authority
```

архитектурно зрелое.

Рекомендуемая трактовка остаётся такой:

```text
DSN / serial / BDF
    = physical evidence

BootVolumeId
    = stable semantic identity installation / replica set

ImageId + ImageGeneration
    = signed software-release identity

OwnedRegion / RegionAuthority + runtime generations
    = live SingNextOS authority
```

`BootVolumeId` при этом не capability. Знание UUID не даёт права обращаться к памяти.

**Оценка: 10/10.**

---

## 2.3. Fresh authority после handoff — очень современное решение

Существующий SingNextOS contract требует:

- generation-bound authority;
- stale state fail-closed;
- provider-private physical binding;
- явного re-admission после reset/reconfiguration;
- separation evidence ≠ authority.

Поэтому переход:

```text
boot discovery evidence
        ->
SingNextOS provider admission
        ->
new Device/Fabric/Mapping generations
        ->
OwnedRegion / RegionUse
```

правильнее, чем попытка “усыновить” firmware CXL state.

Особенно важно, что kernel уже скопирован в local/system RAM: CXL topology можно переинициализировать без потери instruction fetch.

**Оценка: 10/10.**

---

## 2.4. Отказ от CXL-specific ISA — правильный

CXL для CPU — прежде всего:

- PCIe-compatible discovery/configuration;
- MMIO/component registers;
- coherent memory addressability;
- memory attributes/faults;
- platform topology.

CPU не должен получать инструкции вроде:

```text
CXL_ENUM
CXL_MAP
CXL_BIND
CXL_GET_DPA
```

Это создало бы неправильную связь ISA с конкретным interconnect generation.

HybridCPU-v2 достаточно иметь:

- нормальную physical address space;
- MMIO/config access abstraction;
- privileged platform control;
- memory fault model;
- reset semantics.

**Оценка: 10/10.**

---

# 3. Насколько это современно относительно CXL 2026

На сентябрь 2026 актуальной публичной спецификацией является **CXL 4.0**. Она сохраняет backward compatibility и продолжает линию CXL 3.x: Type-3 memory, pooling, fabrics и security остаются фундаментальными, а 4.0 добавляет 128 GT/s, bundled ports и RAS enhancements.

Это важно для оценки roadmap: архитектура HybridCPU/SingNext не привязана к CXL 2.0/3.0 register identity как к semantic contract. Значит переход к CXL 4.x не должен ломать:

- `BootVolumeId`;
- `OwnedRegion`;
- authority model;
- generation model;
- Boot Manifest;
- OS/provider boundary.

Меняется backend, а не верхний ABI.

### Что особенно хорошо совпадает с современным CXL

CXL 3.x/4.0 усиливает именно те направления, для которых SingNextOS уже имеет подходящую архитектуру:

```text
CXL fabrics / switching
    -> ICxlFabricProvider + FabricBindingGeneration

memory pooling / dynamic capacity
    -> semantic placement + provider-private backing

Type-3 persistent/volatile memory
    -> normal OwnedRegion above provider

RAS / reset / replacement
    -> generation invalidation + fail-closed lifecycle

security / IDE / TSP evidence
    -> security evidence provider, not capability minting
```

То есть SingNextOS CXL model выглядит **более future-oriented, чем обычная модель “device driver exposes memN”**.

### Но есть важное различие с mainstream ecosystem

Linux сегодня во многом получает CXL root-window/topology description через platform firmware/ACPI, например CEDT/CFMWS. QEMU также задаёт CXL Fixed Memory Windows как machine configuration.

HybridCPU-native platform может отказаться от ACPI/UEFI, но тогда он обязан иметь **свой эквивалент platform description**.

Это не обязательно firmware API. Достаточно immutable/static structure:

```c
struct HybridPlatformDescriptor {
    uint32_t version;
    Guid     platformId;

    PhysicalRange bootRom;
    PhysicalRange bootSram;
    PhysicalRange localRam;

    PciEcamSegment ecam[N];
    CxlRootDescriptor cxlRoots[M];

    PhysicalRange cxlAssignableHpaWindow;
    PhysicalRange cxlBootAperture;

    PlatformResetDescriptor reset;
    ProtectedStateDescriptor protectedState;
};
```

Это **platform contract**, не firmware service table.

---

# 4. Главная архитектурная проблема исходной boot-схемы

Первоначальный roadmap старается минимизировать ROM, но всё ещё помещает туда значительную часть CXL boot logic:

```text
PCI enumeration
CXL capability parsing
optional mailbox / LSA
capacity discovery
HDM programming
manifest parsing
crypto
anti-rollback
copy Stage1
```

Функционально это допустимо, но архитектурно возникает давление:

```text
“раз ROM уже умеет PCI/CXL...
 давайте добавим switch discovery...
 потом diagnostics...
 потом recovery shell...
 потом more mailbox commands...
 потом update support...”
```

Через несколько итераций получается именно тот firmware layer, от которого хотелось уйти.

### Проблема не в количестве строк кода

Проблема в **ownership**.

CXL развивается быстро. Если immutable ROM содержит CXL parsing/quirks, то новые hardware generations требуют:

- ROM update mechanism;
- backward compatibility;
- device workaround database;
- расширение immutable TCB.

Это противоположно сильной стороне SingNextOS, где provider backend можно обновлять как software.

---

# 5. Можно ли убрать firmware вообще?

## Короткий ответ

**Можно убрать general-purpose firmware layer. Нельзя убрать bootstrapping function как таковую.**

Всегда кто-то должен выполнить:

```text
reset -> найти исполняемый код -> проверить -> сделать доступным -> передать управление
```

Если весь Stage-1 лежит на CXL, то до Stage-1 кто-то обязан:

- обнаружить CXL path;
- настроить хотя бы минимальный decoder/window;
- прочитать bytes.

Этим “кем-то” может быть только:

1. Boot ROM software;
2. hardware state machine/sequencer;
3. отдельный firmware processor;
4. заранее настроенный immutable hardware mapping.

То есть **“firmwareless CXL-only boot” физически означает перенос firmware logic в silicon**.

Для research CPU это обычно хуже, а не лучше.

---

# 6. Рекомендуемая архитектура: Direct SingNext Boot

## 6.1. Основная идея

Stage-1 следует считать **частью SingNextOS**, а не firmware.

Небольшой signed `SingNextBootCapsule` хранится локально:

- маленький NOR/SPI region;
- protected local NVRAM;
- simulator image;
- будущая SoC boot flash.

Он содержит только код, необходимый для запуска SingNextOS с external media, включая CXL.

```text
                 immutable
+----------------------------------+
| HybridCPU Boot ROM               |
| reset + root key + capsule verify|
+-----------------+----------------+
                  |
                  v
          signed / updateable
+----------------------------------+
| SingNext Boot Capsule (Stage-1)  |
| PCIe/CXL + boot-volume loader    |
+-----------------+----------------+
                  |
                  v
             CXL Type-3
+----------------------------------+
| BootVolume                       |
| manifests + kernel + services    |
+-----------------+----------------+
                  |
                  v
+----------------------------------+
| SingNextOS runtime providers     |
+----------------------------------+
```

## 6.2. Что остаётся в ROM

ROM должен делать только:

```text
ArchitecturalReset()
ReadProtectedBootState()
SelectLocalCapsule(A/B/recovery)
VerifyCapsuleSignatureAndGeneration()
CopyCapsuleToBootRam()
JumpToCapsule()
```

Опционально:

- minimal serial/debug output;
- watchdog/reset dispatch;
- immutable recovery capsule selector.

ROM **не должен** знать:

- CXL DVSEC;
- LSA;
- mailbox opcodes;
- HDM decoder layout;
- BDF semantics beyond possibly local recovery controller;
- fabric topology;
- MLD;
- pooling.

Это резко уменьшает immutable TCB.

## 6.3. Что делает SingNext Boot Capsule

Stage-1 получает `HybridPlatformDescriptor` и сам реализует:

```text
PCIe enumeration
CXL capability discovery
Type-3 classification
BootVolumeId discovery
HDM boot mapping
manifest verification
kernel/services loading
BootInfo construction
```

Это уже **OS code** и может обновляться вместе с SingNextOS.

---

# 7. Почему эта схема лучше именно для HybridCPU-v2 / SingNextOS

## 7.1. Она соответствует исследовательскому характеру CPU

HybridCPU-v2 не позиционируется как PC-compatible CPU, которому нужен UEFI ecosystem.

Его сильные стороны:

- explicit machine state;
- replay/evidence separation;
- typed legality;
- explicit retire publication;
- experimental ISA/runtime co-design.

Следовательно, boot architecture тоже может быть **co-designed**, а не наследовать PC firmware model.

UEFI здесь дал бы совместимость, которая пока не является целью, но добавил бы:

- DXE-like driver model;
- device path model;
- firmware service tables;
- runtime services;
- сложный ownership transition.

Это архитектурно чуждо проекту.

## 7.2. Она соответствует capability-native SingNextOS

Если Stage-1 является частью SingNextOS, то CXL parsing/provider code можно делить между:

```text
SingNext.Boot.Cxl
SingNext.Platform.Cxl
```

при строгом разделении authority.

Например:

```text
CxlProtocolCore
  - config parser
  - capability parser
  - mailbox codec
  - HDM register codec
  - topology walker

Boot context
  - temporary mapping only
  - no OwnedRegion minting

Runtime provider context
  - generation-bound admission
  - RegionAuthority / OwnedRegion integration
```

Это намного лучше, чем две независимые CXL реализации:

```text
firmware CXL stack
+
SingNextOS CXL stack
```

---

# 8. Fresh discovery можно сделать проще

Первый roadmap правильно требует fresh OS admission, но это не обязано означать **полный повтор всех PCI config reads с нуля**.

Нужно различать:

```text
fresh authority
!=
blind full physical rediscovery
```

Stage-1 уже является trusted SingNext code. Он может передать kernel:

```text
BootCxlEvidenceSnapshot
  topologyDigest
  endpoints[]
  decoderSnapshot[]
  observedGenerationHints[]
  bootMapping
```

Kernel/provider затем обязан revalidate минимум:

- endpoint still present;
- link/current device generation;
- decoder state still matches or is intentionally replaced;
- fabric assignment still valid;
- no reset/rebind happened;
- security state satisfies policy.

После этого он создаёт **новую runtime authority generation**.

Таким образом:

```text
re-use discovery evidence
+
mandatory liveness / generation revalidation
+
new runtime authority
```

дает ту же correctness модель при меньшем boot latency и без лишнего повторения.

---

# 9. Нужно ли оставлять CXL LSA

## Рекомендация

**Не делать LSA частью baseline architecture.**

LSA — полезный optimization path, но неудачная фундаментальная зависимость.

Плюсы LSA:

- locator можно прочитать до большого persistent mapping;
- удобно хранить маленькую boot hint запись;
- QEMU умеет отдельно моделировать LSA backing;
- CXL software ecosystem имеет mailbox operations для label access.

Минусы:

- требует mailbox path;
- не на каждой platform комбинации одинаково удобно доступен до boot mapping;
- LSA имеет собственное namespace/device-management назначение;
- появляется дополнительный parser в early boot;
- возникает риск сделать locator metadata “магической” boot authority.

### Более простой baseline

Stage-1:

1. enumerates candidate Type-3 devices;
2. maps небольшой canonical boot-anchor window;
3. читает `BootVolumeHeader`;
4. сравнивает `BootVolumeId`;
5. только после match расширяет mapping.

LSA может позже ускорять candidate ordering:

```text
LSA hint -> map likely candidate first
```

но отсутствие/повреждение LSA не должно влиять на boot correctness.

---

# 10. Нужен ли DSN

DSN полезен, но его роль надо даже уменьшить относительно первого roadmap.

Рекомендуемая модель:

```text
DSN = diagnostics + optional provisioning pin
```

Не использовать DSN в normal boot policy, если нет конкретного security/asset-management требования.

Почему:

- device replacement должен быть нормальной операцией;
- logical volume может реплицироваться;
- MLD/fabric assignment может менять observed physical endpoint;
- serial number не должен определять OS instance.

Лучший default policy:

```text
BootVolumeId + signature + generation
```

DSN включается только флагом вроде:

```text
RequireExpectedPhysicalDevice
```

для appliance/security use-case.

---

# 11. Temporary HDM aperture — остаётся обязательным понятием

Даже при устранении firmware CXL boot требует способа читать persistent capacity.

CXL Type-3 — memory-semantic device. Полный kernel image нельзя практически загрузить через LSA mailbox. Нужен CXL.mem path.

Поэтому концепция:

```text
DPA / device persistent capacity
     -> temporary HPA window
```

остаётся.

Но ownership меняется:

### Было

```text
firmware-owned temporary mapping
```

### Рекомендуется

```text
SingNext Stage-1-owned temporary boot mapping
```

Это важное упрощение.

Stage-1 использует mapping как boot transport. Kernel/runtime provider после revalidation:

- либо уничтожает его;
- либо программирует новый runtime mapping;
- либо в будущем explicit-adopts его через validated transition.

В v1 всё ещё лучше уничтожать/rebuild.

---

# 12. Где брать HPA window без ACPI/firmware

Для native HybridCPU platform нужно заранее определить platform-owned assignable CXL window.

Например:

```text
Hybrid physical address space

local RAM
ROM
MMIO
PCI ECAM
CXL MMIO
CXL Assignable HPA Window
    + temporary boot aperture
    + runtime provider windows
```

Это не означает, что конкретное устройство навсегда привязано к HPA.

Контракт фиксирует только:

```text
“этот HPA range разрешено использовать CXL provider”
```

а не:

```text
“HPA X = BootVolume Y”
```

Такой model близок к идее CXL Fixed Memory Windows, но задаётся native HybridCPU platform profile, а не ACPI CEDT.

Для real x86/ARM compatibility backend SingNextOS может отдельно читать ACPI/firmware tables. Этот adapter не должен влиять на native ABI.

---

# 13. Boot Manifest остаётся нужен

Устранение firmware **не означает устранение manifest**.

Наоборот, manifest становится главной границей между persistent media и trusted OS boot code.

Минимальный manifest должен сохранять:

```text
FormatVersion
BootVolumeId
ImageId
ImageGeneration
RequiredCpuAbi
RequiredPlatformAbi
Payload descriptors
Entry points
Hashes
Signature algorithm ID
Signature
```

Можно убрать из него всё, что описывает runtime CXL topology.

Manifest не должен содержать authoritative:

- BDF;
- decoder ID;
- HPA;
- DPA;
- port route.

---

# 14. Secure boot в firmwareless модели становится чище

Цепочка:

```text
Immutable ROM
  -> RootKeyHash / platform trust anchor
  -> local SingNext Boot Capsule A/B
  -> CXL Boot Manifest
  -> kernel/services
```

Это лучше, чем:

```text
ROM -> firmware -> firmware drivers -> OS loader -> OS
```

потому что security boundary короткая.

### Protected state

Вне CXL writable media должны оставаться:

- root key / root-key hash;
- minimum boot-capsule generation;
- minimum accepted OS generation;
- trial/confirmed capsule state;
- trial/confirmed OS image state;
- production/debug lock.

### Рекомендуемое упрощение

Использовать один концептуальный `ProtectedBootState`, но два monotonic domains:

```text
BootCapsuleGeneration
OsImageGeneration
```

Не смешивать их с:

- CXL provider generation;
- metadata sequence;
- fabric binding generation.

---

# 15. A/B boot: локальный capsule + CXL image

Если Stage-1 вынесен из ROM, A/B нужен на двух уровнях.

```text
Local boot storage:
  Capsule A
  Capsule B
  Recovery Capsule

CXL BootVolume:
  Image A
  Image B
  Recovery image (optional)
```

Это кажется сложнее, но на практике даёт важное свойство:

```text
сломанный CXL driver в новом Stage-1
не уничтожает возможность загрузить старый Stage-1
```

ROM знает только локальные capsules и не знает CXL.

### Минимальная policy

- capsule обновляется редко;
- OS image обновляется часто;
- capsule может быть backward-compatible с несколькими manifest ABI;
- local recovery capsule всегда способен дать diagnostic/reflash path.

---

# 16. Recovery становится заметно сильнее

В CXL-only ROM design существует риск:

```text
CXL stack bug in immutable ROM
 -> boot impossible
```

В Direct SingNext Boot:

```text
ROM
 -> local Recovery Capsule
```

работает даже если:

- CXL root отсутствует;
- endpoint завис;
- mailbox broken;
- HDM programming failed;
- BootVolume metadata damaged;
- new Stage-1 incompatible.

Recovery capsule может иметь:

- serial console;
- basic diagnostics;
- local image restore;
- network recovery later;
- CXL diagnostic driver if needed.

Но это signed OS payload, а не ROM functionality.

---

# 17. Оценка четырёх boot variants

## Variant A — UEFI/BIOS-like firmware

```text
Reset -> firmware -> PCI/CXL drivers -> boot manager -> SingNextOS
```

### Плюсы

- близко к industry ecosystem;
- легче использовать generic hardware;
- можно reuse ACPI/UEFI descriptions.

### Минусы

- большой TCB;
- дублирование CXL stack;
- чужой ownership model;
- слабое соответствие experimental CPU/OS co-design;
- firmware becomes long-lived architectural dependency.

**Итог:** не рекомендован как native HybridCPU profile. Может существовать только как compatibility port.

---

## Variant B — текущий roadmap: CXL-aware Stage-0 ROM

```text
ROM -> minimal PCI/CXL -> temporary HDM -> Stage1 from CXL
```

### Плюсы

- настоящий CXL-only boot;
- self-contained platform;
- нет локальной mutable boot media dependency.

### Минусы

- ROM знает CXL protocol;
- большой immutable attack/maintenance surface;
- mailbox/decoder quirks становятся boot-critical;
- дублируется OS CXL parser/backend.

**Итог:** хороший optional research mode, но не лучший default product architecture.

---

## Variant C — рекомендуемый: Direct SingNext Boot Capsule

```text
ROM -> local signed SingNext Stage1 -> CXL -> SingNext kernel
```

### Плюсы

- самый маленький ROM;
- CXL stack находится в OS project;
- общий код boot/runtime;
- лучший update path;
- сильный recovery;
- нет general firmware runtime;
- хорошо соответствует capability/provider model.

### Минусы

- нужен небольшой local boot medium;
- формально “вся OS не находится только на CXL”;
- real hardware всё равно требует minimal silicon bring-up below this model.

**Итог:** рекомендуемый baseline.

---

## Variant D — hardware CXL boot sequencer

```text
Reset hardware -> locate/map CXL BootVolume -> ROM/kernel
```

### Плюсы

- минимальный software boot path;
- очень быстрый старт потенциально.

### Минусы

- firmware complexity превращается в silicon complexity;
- почти нулевая гибкость;
- CXL generation changes требуют hardware redesign;
- плохо для исследовательского CPU.

**Итог:** не рекомендован, кроме очень позднего appliance silicon optimization.

---

# 18. Сравнение вариантов

| Свойство | UEFI-like | CXL in ROM | Local SingNext capsule | HW sequencer |
|---|---:|---:|---:|---:|
| Native project fit | 4 | 8 | **10** | 7 |
| Software simplicity | 3 | 6 | **9** | 10 |
| Hardware simplicity | 9 | 9 | **9** | 3 |
| Immutable TCB | 3 | 6 | **9** | 8 |
| Updatability | 7 | 4 | **10** | 2 |
| Recovery | 8 | 6 | **10** | 4 |
| CXL-only purity | 5 | **10** | 7 | **10** |
| Generic server compatibility | **10** | 6 | 6 | 2 |
| Native HybridCPU compatibility | 6 | 9 | **10** | 7 |
| Research value | 5 | 9 | **10** | 8 |

---

# 19. CXL в HybridCPU: что должно быть архитектурой CPU, а что нет

## В CPU/platform model должно быть

```text
ResetVector
PhysicalAddressSpace
MMIO routing
PCIe ECAM/config access
memory attributes
external-memory access faults
platform reset/status
interrupt delivery
reserved CXL HPA window
```

## Не должно быть в ISA

```text
CXL device index
CXL decoder registers
DPA
fabric route
BootVolumeId opcode
mailbox instruction
pooling instruction
```

## Возможно понадобится позже

Не CXL-specific, а generic architectural facilities:

- machine-check / external-memory fault classification;
- poison/error reporting;
- physical memory attributes;
- persistence fence semantics if ISA memory model requires explicit architectural support;
- privileged cache/persistence primitives.

Но каждое такое изменение должно быть мотивировано **общей memory architecture**, а не брендом CXL.

---

# 20. CXL в SingNextOS: оценка provider architecture

Сильнейшая часть текущего проекта — то, что CXL не создаёт отдельную параллельную authority universe.

Правильная цепочка:

```text
semantic placement request
  -> CXL provider
  -> provider-private HPA/DPA/HDM/fabric binding
  -> normal OwnedRegion
  -> normal RegionUse / capability rules
```

Это особенно перспективно для:

- volatile Type-3 memory;
- persistent Type-3 memory;
- memory tiering;
- pooling;
- dynamic capacity;
- replacement/rebind;
- future multi-host experiments.

### Что важно не переусложнить

SingNextOS должен продолжать избегать:

- глобального `CxlFabricEpoch`, если достаточно узких generations;
- публичных DPA/decoder handles;
- отдельного `CxlOwnedRegion`;
- автоматически “coherent = safe to share”;
- implicit authority from Fabric Manager.

Текущие roadmap уже идут в правильную сторону.

---

# 21. Насколько перспективен boot именно с CXL persistent memory

## Как исследовательская идея — очень перспективен

Это позволяет исследовать system model, где:

```text
compute node is relatively stateless
OS/system image resides in composable memory
physical memory attachment may change
logical system identity survives device replacement/topology movement
```

Такой boot path естественно заставляет правильно решить:

- semantic identity;
- replicas;
- generation;
- recovery;
- trust;
- fabric rebind;
- persistent metadata.

Это хороший stress test для архитектуры SingNextOS.

## Как ближайший mainstream product requirement — умеренно перспективен

CXL industry focus в первую очередь связан с memory expansion, pooling, fabrics, AI/HPC memory capacity и RAS. Сам boot from CXL PMEM пока не является типовой платформенной моделью.

Поэтому CXL boot лучше позиционировать как:

```text
native HybridCPU/SingNext memory-centric boot mode
```

а не как попытку заменить стандартный server boot ecosystem.

---

# 22. QEMU: насколько полезен после упрощения

Очень полезен, но с чёткой границей.

Актуальная QEMU CXL модель умеет:

- CXL host bridges/root ports;
- switches;
- Type-3 volatile/persistent memory;
- separate LSA backing;
- serial number;
- fixed memory windows;
- HDM decoder topology;
- multi-device/interleave fixtures.

При этом документация QEMU прямо ограничивает scope single-host/static configuration и не моделирует весь fabric-management world.

### Лучший способ использовать QEMU

Не пытаться запускать HybridCPU ISA в QEMU сразу.

Разделить:

```text
HybridCPU simulator
  -> validates reset/ABI/Stage1 execution

QEMU x86/arm companion harness
  -> validates CXL protocol assumptions
     PCI config
     Type-3
     HDM
     LSA optional path
     multiple endpoints
```

Общий тестовый слой:

```text
CxlProtocolCore conformance vectors
BootVolume layout vectors
Manifest vectors
HDM planning vectors
failure scenarios
```

Это намного реалистичнее, чем ждать полноценный HybridCPU QEMU target.

---

# 23. Real hardware path без UEFI architecture dependency

Нужно различать два режима.

## Native HybridCPU hardware

Можно иметь:

```text
ROM + HybridPlatformDescriptor + SingNext Boot Capsule
```

без UEFI/ACPI вообще.

## Port на существующий x86/ARM CXL server

Практически придётся принимать platform data через:

- ACPI CEDT/CFMWS;
- EFI/E820 memory reservation;
- vendor/platform ownership conventions.

Но это может быть только adapter:

```text
AcpiCxlPlatformAdapter
        ->
normal SingNext CXL provider contracts
```

То есть real-hardware compatibility не должна заставлять native HybridCPU platform принять UEFI как архитектурную основу.

---

# 24. Что я бы убрал из baseline roadmap

Для **baseline native profile**:

### Убрать из ROM

- PCI enumeration;
- CXL DVSEC parsing;
- CXL mailbox;
- LSA parsing;
- HDM decoder programming;
- CXL candidate selection;
- CXL-specific recovery logic.

### Убрать как обязательные baseline concepts

- LSA boot locator;
- DSN preference;
- platform boot directory;
- fabric-aware boot selection;
- interleaved boot mapping;
- XIP.

### Перенести в SingNext Stage-1

- PCI/CXL discovery;
- boot-volume discovery;
- temporary HDM;
- CXL read fault handling;
- topology snapshot;
- manifest loading.

---

# 25. Что обязательно сохранить

Даже после сильного упрощения нельзя убирать:

- immutable local reset target;
- root of trust;
- anti-rollback;
- protected boot state;
- `BootVolumeId`;
- signed manifest;
- A/B logic;
- local recovery;
- temporary mapping lifecycle;
- separation boot evidence vs runtime authority;
- generation invalidation;
- copy-to-RAM baseline;
- no CXL-specific ISA.

Это фундаментальные решения, а не лишняя сложность.

---

# 26. Рекомендованный конечный boot flow

```text
+---------------------------------------------------------------+
| HybridCPU reset hardware                                      |
| deterministic reset state + ROM vector                        |
+------------------------------+--------------------------------+
                               |
                               v
+---------------------------------------------------------------+
| Immutable Boot ROM                                             |
|                                                               |
| - verify PlatformDescriptor                                   |
| - read protected BootState                                    |
| - select local Capsule A/B/recovery                            |
| - verify capsule signature / generation                        |
| - copy to Boot SRAM / RAM                                     |
+------------------------------+--------------------------------+
                               |
                               v
+---------------------------------------------------------------+
| SingNext Boot Capsule / Stage-1                               |
|                                                               |
| shared SingNext CXL protocol library                           |
| - enumerate PCIe/CXL roots                                    |
| - discover Type-3 candidates                                  |
| - read BootVolumeHeader                                       |
| - match BootVolumeId                                          |
| - establish temporary non-interleaved HDM mapping              |
| - verify signed image manifest                                 |
| - load kernel/services into normal RAM                         |
| - produce BootEvidenceSnapshot                                 |
+------------------------------+--------------------------------+
                               |
                               v
+---------------------------------------------------------------+
| SingNextOS early kernel                                       |
|                                                               |
| - validate handoff                                             |
| - revalidate live CXL topology                                 |
| - create fresh Device/Fabric/Mapping generations               |
| - destroy/replace boot mapping                                 |
| - establish normal provider authority                          |
+------------------------------+--------------------------------+
                               |
                               v
+---------------------------------------------------------------+
| Normal SingNextOS                                              |
| OwnedRegion / RegionUse / Fabric Manager / persistence         |
+---------------------------------------------------------------+
```

---

# 27. Рекомендуемая component ownership model

| Component | Владеет | Не владеет |
|---|---|---|
| HybridCPU reset logic | reset state, reset vector | CXL discovery, boot policy |
| Boot ROM | root of trust, local capsule verification | PCIe/CXL stack |
| Platform descriptor | physical map and discoverability roots | OS authority |
| SingNext Stage-1 | boot-time CXL transport and temporary mapping | runtime `OwnedRegion` authority |
| SingNext CXL provider | runtime CXL discovery/bindings/generations | application capability policy |
| Region layer | `OwnedRegion`, `RegionUse`, lifetime | decoder/DPA topology |
| Fabric manager/provider | fabric assignment and reconfiguration | implicit region ownership |

---

# 28. Product positioning

Я бы формулировал связку так:

> **HybridCPU-v2 + SingNextOS — vertically co-designed capability-native compute platform, где CPU предоставляет минимальный architectural execution/reset substrate, а OS владеет внешней памятью и fabric semantics. CXL является сменным physical memory fabric backend, а не частью ISA и не источником authority.**

В таком позиционировании boot с CXL PMEM — не “BIOS feature”, а следствие более общей идеи:

```text
logical machine state
must not depend on one physical memory device identity
```

Это гораздо сильнее и интереснее, чем просто “OS умеет грузиться с CXL”.

---

# 29. Основные риски

## R1. Реальное platform bring-up сложнее simulator model

На настоящем silicon могут понадобиться:

- clock/PHY initialization;
- link training policy;
- DRAM training;
- host bridge initialization;
- vendor security controller interaction.

Часть этого неизбежно будет ROM/microcode/platform-specific.

**Митигирование:** не включать это в Boot ABI. Считать pre-architectural silicon initialization реализационной деталью platform profile.

## R2. Generic real hardware ожидает firmware-described windows

Linux ecosystem часто использует ACPI/EFI descriptions.

**Митигирование:** separate compatibility adapter, не менять native architecture.

## R3. CXL boot становится слишком важной частью продукта

Если вся работа начинает оптимизироваться вокруг CXL boot, можно исказить OS design.

**Митигирование:** `BootVolume` — один из boot media providers. Архитектура reset/manifest должна быть media-neutral.

## R4. Дублирование Stage-1 и runtime CXL code

**Митигирование:** общий `CxlProtocolCore`, разные authority contexts.

## R5. Сложность persistent update/recovery

**Митигирование:** local capsule A/B + CXL image A/B, но независимые generation domains.

---

# 30. Что делать практически

## Шаг 1 — зафиксировать firmwareless native profile

Новый ADR:

```text
BR-019 Native HybridCPU profile has no general-purpose firmware runtime.
BR-020 Boot ROM does not implement PCIe/CXL in the default profile.
BR-021 SingNext Boot Capsule is OS-owned and updateable.
BR-022 CXL boot discovery belongs to the SingNext Stage-1 codebase.
BR-023 LSA is optional optimization only.
```

## Шаг 2 — определить `HybridPlatformDescriptor`

Он должен заменить необходимость ACPI/UEFI для native simulator/platform.

## Шаг 3 — выделить shared CXL protocol core

Из SingNextOS provider architecture выделить reuse-safe низкий слой:

```text
PCI config parser
CXL capability parser
mailbox codec
HDM codec
Type-3 descriptors
```

без authority objects.

## Шаг 4 — сделать local Boot Capsule A/B

Simulator сначала моделирует capsule как read-only local boot media.

## Шаг 5 — загрузить kernel с ModelCxlType3

Только после этого добавлять:

- multiple devices;
- replacement;
- duplicate BootVolume;
- optional LSA;
- switch topology.

## Шаг 6 — QEMU companion validation

Проверить protocol assumptions независимо от HybridCPU ISA.

## Шаг 7 — real hardware adapter

Только здесь принимать решение, нужны ли:

- ACPI CEDT;
- UEFI memory map;
- host firmware coordination;
- vendor HDM ownership.

---

# 31. Финальный verdict

## По современности

**Высоко.** Identity/authority/provider separation лучше соответствует направлению CXL fabrics/pooling, чем device-index-centric модель. CXL 4.0 не делает эту архитектуру устаревшей — наоборот, повышает ценность version-neutral provider boundary.

## По соответствию HybridCPU-v2

**Очень высоко**, если CXL остаётся platform/memory subsystem, а не ISA extension. Reset/Boot ROM нужно добавить как архитектурный substrate, но не смешивать с pipeline/replay semantics.

## По соответствию SingNextOS

**Практически идеально.** `OwnedRegion`, generation invalidation, evidence ≠ authority и provider-private topology дают правильную основу для CXL.

## По простоте

Первый roadmap — **слишком сложный в ROM**, хотя логически корректный. Лучшее упрощение — local signed SingNext Boot Capsule и полное удаление general-purpose firmware layer.

## По перспективности

**Высоко как research/product architecture**, особенно для memory-centric / composable systems. Сам CXL boot следует считать native feature, а не industry compatibility requirement.

## Главное решение

```text
НЕ:
CPU -> firmware -> CXL -> OS

И НЕ:
CPU ISA knows CXL

А:
HybridCPU reset/trust substrate
      -> SingNext boot code
      -> CXL as OS-managed physical fabric
      -> SingNext runtime authority
```

Именно этот вариант наиболее последователен для двух проектов.

---

# 32. Источники и evidence basis

## Проекты

- HybridCPU-v2 repository: https://github.com/yuriyyak23/HybridCPU-v2
- SingNextOS CXL authority/provider decomposition: https://github.com/yuriyyak23/SingNextOS/blob/master/docs/cxl-refactoring-roadmap/04-cxl-authority-and-provider-decomposition.md
- SingNextOS Type-3 memory provider: https://github.com/yuriyyak23/SingNextOS/blob/master/docs/cxl-refactoring-roadmap/05-cxl-type3-memory-provider.md
- SingNextOS real-hardware/QEMU backend: https://github.com/yuriyyak23/SingNextOS/blob/master/docs/cxl-refactoring-roadmap/08-real-hardware-and-qemu-backend.md
- SingNextOS fabric/pooling/reconfiguration: https://github.com/yuriyyak23/SingNextOS/blob/master/docs/cxl-refactoring-roadmap/09-fabric-manager-pooling-and-reconfiguration.md
- SingNextOS post-CXL operability roadmap: https://github.com/yuriyyak23/SingNextOS/blob/master/docs/post-cxl-operability-refactoring-roadmap/README.md

## CXL / system software

- CXL Consortium, CXL 4.0 Specification page: https://computeexpresslink.org/cxl-specification/
- CXL Consortium, CXL 4.0 release: https://computeexpresslink.org/wp-content/uploads/2025/11/CXL_4.0-Specification-Release_FINAL_Website-Copy.pdf
- CXL Consortium, CXL 4.0 feature overview: https://computeexpresslink.org/wp-content/uploads/2025/12/CXL_4.0-Webinar_December-2025_FINAL.pdf
- Linux CXL driver operation: https://cdn.kernel.org/doc/html/latest/driver-api/cxl/linux/cxl-driver.html
- Linux CXL device types: https://cdn.kernel.org/doc/html/latest/driver-api/cxl/devices/device-types.html
- Linux CXL theory of operation: https://cdn.kernel.org/doc/html/latest/driver-api/cxl/theory-of-operation.html
- QEMU CXL documentation: https://www.qemu.org/docs/master/system/devices/cxl.html

## Firmware-minimization reference

- LinuxBoot Book: https://book.linuxboot.org/
- LinuxBoot repository: https://github.com/linuxboot/linuxboot
- UEFI PI 1.10: https://uefi.org/specs/PI/1.10/V1_Introduction.html

---

# 33. Связь с предыдущим roadmap

Этот отчёт **не отменяет** `HybridCPU-v2 Boot/Reset + CXL Boot ABI Roadmap`. Он предлагает более сильный product profile:

```text
предыдущий roadmap
  = полный CXL-only boot capability

этот assessment
  = рекомендуемый default architecture
    с OS-owned local Stage-1 и без general firmware
```

Имеет смысл сохранить CXL-aware ROM flow как **optional `PureCxlBootProfile`**, но не делать его default requirement первой реализации.
