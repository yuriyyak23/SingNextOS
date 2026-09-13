# 12. Native Sing+ API And UI Contract Architecture

**Status:** normative native-API/UI architecture with explicit current/target/future boundaries  
**SingNextOS baseline:** `af791aba4e25615cef09b3933f34efca62296304`

## Purpose

Этот документ фиксирует нативную модель application API Singularity Plus (Sing+) и место UI/GUI в системной архитектуре. Он не добавляет WinAPI/POSIX compatibility substrate и не объявляет отсутствующие GUI services реализованными.

Главный принцип:

> **source familiarity, not binary compatibility.**

Sing+ должен быть удобен разработчику, привыкшему к современному C#/.NET API, но семантика authority, IPC, ownership и service decomposition является нативной для Sing+.

## Current repository evidence

На указанном baseline уже реализованы фундаментальные механизмы, на которых должен строиться native API:

- `[SipContract]` source generation создаёт детерминированные protocol definitions, dispatcher, typed client transport и contract/capability metadata;
- `CapabilityDescriptorV1` связывает authority с issuer/subject domain, resource kind/id, rights, generation и revocation epoch;
- `CapabilityAuthority` проверяет subject, generation, revocation и rights, а delegation ограничена подмножеством исходных rights;
- `OwnedRegion<T>` / `OwnedBuffer<T>` и `RegionAuthority` поддерживают generation-bound ownership transfer и revocable borrow;
- `ChannelRegistry` проверяет contract state, capability requirements и payload shape до mutation, а `[Consumes]` переводит ownership region к receiver;
- local `PlatformAuthorityBridge` и `IPlatformAuthorityProvider` уже имеют host-backed v1 contours `NeutralDomainBinding` и `DirectOwnedRegionMapping`;
- active platform mapping резервирует region и блокирует несовместимые loan/transfer/release/termination до revoke.

На этом baseline **не реализованы** готовые `IFileSystem`, `INetwork`, `IWindowService`, compositor/window-manager/input contracts, `SingPlus.UI`, surface presentation runtime, GPU/display backend или HybridCPU-backed platform provider. Эти части ниже имеют статус **target architecture**, если явно не сказано иначе.

## Native Sing+ application API

Целевая модель:

```text
.NET-like Sing+ public API
 File / Stream / Socket / Process / UI
 Window / Display / Input / Clipboard / ...
                ↓
generated typed SIP contracts
 IFileSystem / INetwork / IProcessManager
 IWindowService / ICompositorService
 IInputService / IClipboardService
 IDisplayService / ...
                ↓
capability + ownership IPC runtime
 Channel / Endpoint / OwnedRegion
 MOVE / Borrow / controlled sharing
                ↓
privileged minimal kernel authority
                ↓
Platform Authority Bridge
                ↓
HybridCPU domain/device mechanisms
```

Названия высокоуровневых interfaces в этой схеме являются **target API vocabulary**, а не утверждением о наличии этих типов в текущем исходном коде.

### .NET-like means ergonomics, not compatibility

Верхний API может использовать знакомые формы:

```csharp
await file.ReadAsync(...);
await socket.ReceiveAsync(...);
await process.WaitForExitAsync(...);
await window.PresentAsync(...);
```

Но из этого не следует:

- Win32 handle model;
- POSIX fd/syscall model;
- CoreCLR/Windows/Linux binary compatibility;
- автоматическая совместимость произвольных NuGet packages с OS-specific assumptions;
- скрытый переход к `CreateFileW`, `read(2)`, X11/Wayland или другому legacy ABI.

Native Sing+ API должен быть **source-familiar**: типы и async patterns знакомы, но authority и transport остаются typed SIP semantics.

## Typed SIP service architecture

Высокоуровневые системные функции должны предоставляться через typed services, а не через огромный syscall ABI.

Целевые примеры:

```text
File.OpenAsync
  -> generated IFileSystem client
  -> FileSystem SIP
  -> capability-backed file/session reference

Socket.ReceiveAsync
  -> generated INetwork client
  -> Network SIP
  -> owned/bounded packet payload

Process.StartAsync
  -> generated IProcessManager client
  -> Process Manager SIP
  -> kernel lifecycle authority where required
```

Текущий generator уже доказывает базовый механизм `typed interface -> generated protocol/client transport`, но перечисленные high-level services ещё не являются current implementation.

## Where service API ends and kernel authority begins

Kernel не является обычным заменяемым service SIP.

Kernel остаётся privileged authority layer для:

- process/domain lifecycle;
- capability mint/delegate/revoke/validation;
- region ownership and generation;
- channel lifecycle and trusted protocol admission;
- projection of local authority into platform bindings;
- fail-closed cleanup/reclamation.

Filesystem, network stack, compositor, window manager, shell и другие high-level subsystems должны быть service SIPs, где это архитектурно возможно. Kernel предоставляет минимальные authoritative primitives, но не превращает каждую функцию ОС в syscall.

## Capability-oriented public API

Sing+ не должен полагаться на ambient authority.

Предпочтительная модель:

```text
typed reference / handle
≈ explicit capability-bearing authority
```

Само знание:

```text
PID
path
window id
device name
display id
clipboard name
```

не должно автоматически разрешать операцию.

Current capability model уже включает:

```text
resource kind + resource identity
subject domain
rights
generation
revocation epoch
delegation constraints
```

и именно её следует расширять для новых services. Нельзя создавать отдельную независимую GUI security-token system.

Текущий `ResourceKind` содержит только `KernelService`, `MemoryRegion`, `ChannelEndpoint`, `Device`, `MmioRegion`, `Irq`, `Dma`; текущие rights — `Read`, `Write`, `Map`, `Signal`, `Configure`, `Transfer`, `Delegate`. Поэтому GUI capability names ниже являются **semantic target concepts**, а точное расширение `ResourceKind`/resource identity должно быть определено отдельной реализационной итерацией.

## IPC payload classes and zero-copy direction

Sing+ различает по крайней мере три semantic classes.

### 1. Small value messages

Для небольших immutable/value payload копирование допустимо и часто оптимально. Zero-copy не является догмой для десятков байт metadata.

### 2. Large mutable payload: ownership transfer

Основная модель:

```text
Domain A owns region
        ↓ MOVE
runtime validates capability + generation
        ↓
Domain B owns same logical backing region
```

После successful MOVE sender теряет mutable authority на прежнюю generation.

Current runtime уже реализует эту семантику для `OwnedRegion<T>`/`OwnedBuffer<T>` в channel `Consumes` path.

### 3. Controlled borrow/shared grant

Borrow нужен для bounded temporary access. Current runtime имеет generation-bound borrow lease.

Более общий `SharedGrant` — **target**, а не текущий primitive. Если он будет добавлен, он должен иметь explicit rights, owner, borrower/domain, lifetime, revocation и synchronization/coherence contract.

Базовое правило:

```text
ownership transfer > shared mutable memory
message passing     > global coherent state
```

Shared writable memory может существовать как явная оптимизация, но не как ambient global heap между SIPs.

## Coherence-independent software model

Корректность native Sing+ software не должна фундаментально зависеть от наличия global cache coherence между всеми CPU/device/accelerator actors.

Это **не** утверждение, что HybridCPU сегодня является некогерентной системой или что конкретная cache-maintenance операция уже существует. Это программная архитектурная цель:

- один writable owner там, где возможно;
- explicit transition before another domain/device becomes writer;
- explicit completion/publication fence before ownership is reused;
- copying remains valid fallback when safe rebinding/sharing is unavailable.

Current local Platform Authority Bridge может резервировать owned region для direct mapping, но реальная HybridCPU mapping/IOMMU/DMA/coherence semantics остаётся external-blocked.

## DMA and accelerator ownership direction

Будущий DMA/accelerator path должен концептуально продолжать ту же model:

```text
SIP owns region
  -> local capability + generation validation
  -> platform mapping/grant
  -> device/accelerator access window
  -> completion/publication
  -> revoke/unmap or ownership return
```

Local `CapabilityId` не является HybridCPU native token, а external token не становится process-visible Sing+ capability. Hardware effect требует пересечения local OS authority и live platform authority.

## Async service model

Меж-SIP operation следует считать потенциально asynchronous.

Public façade должен позволять идиоматические `Task`/`ValueTask` APIs и cancellation/completion patterns. Source generator уже допускает `Task<T>`/`ValueTask<T>` shapes при анализе ownership-return signatures, но это не означает, что каждый `Task` соответствует kernel thread или что конкретный hardware provider выполняет operation с физическим overlap.

Целевая ABI/runtime direction:

```text
request
  -> generated protocol state machine
  -> SIP scheduling
  -> completion token / typed response
  -> publication
```

`ValueTask` и completion tokens предпочтительны там, где это уменьшает allocation/dispatch overhead и не ухудшает contract clarity.

# UI/GUI as a standard Sing+ contract subsystem

UI/GUI является **стандартной системной contract-подсистемой Sing+**, а не ABI конкретного desktop environment или toolkit.

Целевая схема:

```text
.NET-like Sing+ UI façade
 Window / Surface / Display / Input / Clipboard / ...
                ↓
typed UI SIP contracts
 Display / Compositor / Window Manager / Input
 Clipboard / Font-Text / Accessibility / Notification / Shell
                ↓
capability + ownership IPC
 OwnedRegion / future Surface resource
 MOVE / Borrow / controlled grant
                ↓
kernel authority + platform bridge
                ↓
HybridCPU/device/display mechanisms when available
```

## Required role separation

Совместимая desktop environment должна быть собрана из implementations стандартных contracts. Не требуется, чтобы все roles были отдельными processes, но ABI и authority boundaries не должны склеивать их в один обязательный монолит.

### Display SIP

Target responsibilities:

- outputs and modes;
- display enumeration;
- display configuration;
- mode/state change authorization.

### Compositor SIP

Target responsibilities:

- surfaces;
- composition;
- presentation queue;
- damage/presentation metadata;
- completion/fence handoff.

### Window Manager SIP

Target responsibilities:

- window lifecycle;
- placement;
- focus;
- z-order;
- policy for overlays/top-most placement.

Window manager и compositor могут быть одним product implementation, но это не должно превращать их в один неделимый system ABI.

### Input SIP

Target responsibilities:

- keyboard;
- mouse;
- touch;
- pen;
- focus-scoped delivery;
- privileged global input/shortcut mediation.

### Clipboard SIP

Target responsibilities:

- controlled read/write exchange;
- format negotiation;
- ownership/lifetime of large clipboard payloads.

### Font/Text SIP

Target responsibilities:

- font discovery;
- shaping services;
- rasterization or provider-neutral text rendering services.

### Accessibility SIP

Target responsibilities:

- semantic/accessibility tree;
- capability-bounded cross-window inspection;
- assistive-technology event routing.

### Notification SIP

Target responsibilities:

- application notification publication;
- policy/permission mediation;
- presentation handoff to shell/notification presenter.

### Shell SIP

Target responsibilities:

- desktop/panel/launcher/session UX;
- user-facing policy surfaces;
- session orchestration that does not become the universal GUI ABI.

Drag/drop and lifecycle protocols are OS-standard interaction contracts and may compose Window/Input/Clipboard/ownership services; exact service boundaries remain target design.

## Desktop environment does not define the ABI

KDE/GNOME/Plasma-like environment или собственный Sing+ Shell может реализовывать один или несколько стандартных service contracts, но приложения не должны зависеть от его private implementation API.

Correct dependency:

```text
Application
  -> Sing+ UI façade/contracts
  -> selected service implementations
```

Not:

```text
Application
  -> KDE ABI / GNOME ABI / shell-private protocol
  -> OS
```

Compatible desktop environments can replace contract implementations independently where policy and session composition allow it.

## OS GUI contracts versus toolkit

Sing+ OS standardizes system-level interaction and authority, not a widget catalog.

### OS-standard target contracts

- Window;
- Surface/presentation;
- Display;
- Input;
- Clipboard;
- drag/drop;
- notifications;
- accessibility;
- font/text services;
- lifecycle;
- capabilities;
- ownership/presentation semantics.

### Toolkit layer, not OS ABI

Sing+ does not need to standardize:

- Button;
- CheckBox;
- TreeView;
- Ribbon;
- Material Design;
- GNOME HIG;
- KDE widgets.

Target layering:

```text
SingPlus.UI.Contracts    <- stable OS contracts
SingPlus.UI              <- .NET-like façade

Avalonia-like toolkit    <- on top
KDE-like environment     <- on top
GNOME-like environment   <- on top
Custom/game UI           <- on top
```

These namespace/product names are architectural direction, not current assemblies at the repository baseline.

# GUI capability and security model

Ordinary application authority must be scoped to its own UI resources and explicitly granted cross-session/global operations.

It must not automatically be able to:

- intercept global keyboard input;
- read input delivered to other windows;
- capture the screen or arbitrary windows;
- inject global input;
- read clipboard contents;
- inspect foreign windows;
- draw over arbitrary foreign windows;
- change display configuration;
- register global shortcuts.

Conceptual policy capabilities include:

```text
WindowCapability
SurfaceCapability
InputCapability
ClipboardReadCapability
ClipboardWriteCapability
ScreenCaptureCapability
NotificationCapability
DisplayConfigurationCapability
GlobalShortcutCapability
```

These are **not current enum/type names**. Implementation must map equivalent authority into the existing capability ledger using semantic resource identities and rights. If new `ResourceKind` values are required, they extend the existing taxonomy rather than forming a parallel authority system.

Useful least-authority split:

```text
ordinary app:
  own-window
  own-surface
  focused-input
  explicitly granted clipboard operations

privileged/session service only when granted:
  global-input
  screen-capture
  input-injection
  foreign-window-inspection
  display-configuration
  global-shortcuts
```

Clipboard read/write should be separable. Accessibility and automation may legitimately need broader inspection/input authority, but only through explicit delegated capabilities with revocation and scope.

# Zero-copy surface and presentation model

Surface presentation should be a specialization of Sing+ ownership architecture, not a parallel unrestricted shared framebuffer model.

Avoid making this the base path:

```text
application framebuffer
  -> copy
window server
  -> copy
compositor
  -> copy
GPU
```

Target model:

```text
Application SIP
  owns SurfaceBuffer / owned region
        ↓ Present
ownership transition or controlled read grant
        ↓
Compositor SIP
        ↓ submit
GPU / Display device access
        ↓ completion/fence
buffer returned / application reacquires write authority
```

`SurfaceBuffer` here is a target semantic resource; current channel ownership payload types remain `OwnedBuffer<T>` and `OwnedRegion<T>`.

## Present invariant

После successful `Present` application не должно продолжать mutable access к переданному buffer, пока authority не возвращена или новый writable buffer не выдан.

Double/triple buffering естественно выражается так:

```text
Buffer A: compositor/device reads or presents
Buffer B: application exclusive-write renders
Buffer C: queued/staged for a later present
```

После completion/fence buffer может вернуться application в writable state, при необходимости с новой generation/lease epoch.

## Controlled shared surface grant

Если physical sharing выгоднее ownership move, sharing должен быть bounded and directional:

```text
APP exclusive-write
    ↓ Present
COMPOSITOR read-only
    ↓ submit
GPU/device read grant
    ↓ completion/fence
APP exclusive-write
```

Не допускается считать `Application <-> Compositor unrestricted shared writable framebuffer` фундаментальным IPC primitive.

Exact GPU DMA-read, page remap, cache maintenance, fence and coherence behavior является **future platform integration detail**. Current repository доказывает только local ownership/generation/capability mechanics и generic direct-owned-region mapping reservation, но не конкретный display/GPU transport.

# Compatibility personalities

Win32/POSIX/Wine support может быть добавлен позже как отдельный compatibility/personality SIP or service stack.

Correct layering:

```text
legacy application/API
  -> compatibility personality SIP
  -> native Sing+ typed services/capabilities
```

Compatibility layer может переводить legacy identifiers, handles and calls в bounded native requests, но не должен определять:

- kernel object model;
- native filesystem/network/process ABI;
- native GUI ABI;
- capability representation;
- ownership semantics;
- HybridCPU platform authority.

Native applications should target Sing+ contracts directly.

# Current / target / future matrix

| Area | Status at baseline | Rule |
|---|---|---|
| generated SIP protocol/client metadata | **Current** | foundation for typed service APIs |
| capability subject/rights/generation/revocation | **Current** | reuse for every subsystem |
| owned region/buffer MOVE + borrow | **Current** | preferred large-payload IPC |
| local platform domain/direct-region bridge | **Current, host-backed** | not proof of HybridCPU hardware mapping |
| `.NET-like` File/Network/Process façade | **Target** | source familiarity, native SIP semantics |
| filesystem/network/process-manager service contracts | **Target** | high-level services, not giant syscall surface |
| UI contract family | **Target** | standard OS subsystem |
| Display/Compositor/WM/Input/etc. implementations | **Target** | replaceable service roles |
| GUI-specific capability resource kinds | **Target** | extend existing capability taxonomy |
| surface ownership/presentation protocol | **Target** | reuse region/ownership model |
| controlled general `SharedGrant` primitive | **Target** | explicit, bounded, revocable if introduced |
| GPU/display HybridCPU backend | **Future / external-blocked until proven** | no remap/DMA/coherence overclaim |
| Win32/POSIX/Wine personality | **Future compatibility** | downstream of native architecture |

# Architecture answers

1. **Native application API:** .NET-like façade over generated typed SIP contracts, capabilities and ownership-aware IPC.
2. **Why not WinAPI/POSIX clone:** native semantics are capability/ownership/service-first; compatibility is downstream.
3. **How façade reaches SIP:** source-generated typed client/protocol metadata transports semantic requests to service SIPs.
4. **Kernel boundary:** kernel owns privileged lifecycle/capability/region/channel/platform-projection authority; high-level services stay SIPs.
5. **Resource access:** live capability with correct subject, rights, generation, revocation state and resource identity is required; identifier knowledge alone is insufficient.
6. **Large data:** prefer ownership MOVE or bounded borrow/grant; copy small values and use copy fallback when direct transfer is unsafe/unavailable.
7. **Ownership and domains:** region owner includes `DomainId` and process generation; transfer changes authoritative owner/generation rather than relying on global mutable sharing.
8. **Standard GUI:** typed Window/Surface/Display/Input/etc. contracts over the same capability/ownership runtime.
9. **Desktop environment obligation:** implement compatible standard service contracts for the roles it provides.
10. **Why KDE/GNOME do not define ABI:** they are implementations/toolkits/shells above stable Sing+ contracts.
11. **OS contracts vs widgets:** OS standardizes system resources, lifecycle, authority and presentation; widget design belongs to toolkits.
12. **GUI protection:** global input, clipboard, capture, injection, foreign-window inspection, display changes and global shortcuts require explicit scoped authority.
13. **Surface without mandatory copies:** application transfers or grants read access to an owned surface region, compositor/device consume it, completion returns/re-enables write authority.
14. **HybridCPU readiness:** software semantics are domain/ownership/capability based today; concrete remap/IOMMU/DMA/coherence/display behavior remains behind the Platform Authority Bridge and is only claimed when an external provider proves it.

## Decision

The native Sing+ contract is:

```text
.NET-like ergonomics
+ generated typed SIP services
+ capability-oriented authority
+ ownership-first IPC
+ minimal privileged kernel
+ platform/domain bridge
```

UI/GUI is a first-class member of this model. It does not create a second security system, a second memory model, or a desktop-environment-specific ABI.