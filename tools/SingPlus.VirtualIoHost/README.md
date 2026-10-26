# Bounded Virtual-I/O software host

`--nested` admits an original nested child with Lifecycle/Io authority and uses
the generated Bind/Transfer/Close client on that child. Normal execution opens
two independent devices/backings: Bind2/Transfer4/Close2, charges512→1024→512→0.
After the first Close, the second IO still reads byte6C. Both native devices/root
eventually close while a foreign32-byte Region retains byte7E. Add
`--lost-bind-reply --lost-close-reply`, `--lost-transfer-reply --lost-close-reply`
or `--transfer-capacity` to exercise their original cleanup paths on the nested
factory. Device/MMIO prerequisite modes reject `--nested`.

`--transfer-capacity` executes256 generated writes through the adopted native
producer, then refuses one write and one read at the kernel's original history
bound. No257th native invocation or read snapshot reservation occurs. Original
refusal custody drains, IO Close runs once, charge512→0, native loan/root close,
and the independent service buffer0x6D survives. This mode observes native counts
through the existing forwarding wrapper; it does not infer native no-effect from
absence or replace original history to reclaim capacity.

Runs the actual versioned HybridCPU external runtime through the executable adapter,
SingNextOS provider, kernel admission and generated virtualization session client.
The host creates admitted process/device/domain owners, executes bounded write/read,
closes the original IO and verifies native device retirement and quantitative reclaim.
It does not execute a whole OS/guest syscall data plane or physical device DMA.

From the repository root, restore this project when dependencies change, then build:

```powershell
dotnet build tools/SingPlus.VirtualIoHost/SingPlus.VirtualIoHost.csproj --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
dotnet tools/SingPlus.VirtualIoHost/bin/Debug/net11.0/SingPlus.VirtualIoHost.dll
```

Expected output: bytes17/19/23/29, native snapshot released independently after the
bounded response copy, backing charge512->512->0 and nativeDevices0. The optional
capable loss wrapper retains snapshot charge516 while query is unknown, then uses original query-only cleanup to release it to512 before whole-IO closure. Transfer is never repeated.
Failed checks print and exit nonzero. The default executing path needs no reflection,
fake producer or test-only runtime callback. Gates OFF; ProductionQualified false;
Java skipped; ISA NONE.

`--lost-snapshot-release-reply` hides the native snapshot Release reply and query
after its real effect. The generated read fails publication and retains charge516;
same-original pending cleanup later consumes only the snapshot charge to512.
Independent IO closure then reclaims0. Transfer2/Release1/Close1, native devices/root0,
and foreign Region byte7E survives. It supports `--nested`, `--lost-close-reply`,
and `--lost-transfer-reply`. Combined transfer loss first retains the unknown read
with Release0; exact transfer query advances to Release1, whose separate lost query
still retains516 until original cleanup succeeds. Neither effect repeats.

`--device-capacity-refusal` creates one independent native device, places the actual
allocator at its exhausted bound, and attempts another original device Bind.
`--lost-device-reply` also hides its original reply/query. Caller Region/charge and
independent device stay retained while outcome is unknown; same-original lookup
permits exact caller cleanup, with Bind2 (independent1/refusal1), no repeat,
charges64→0 and foreign service Region Owned. This is the device prerequisite path;
the failed device emits no IO lease. Normal and transfer/Close-loss modes execute
the generated service IO/backing producer separately.

`--mmio-capacity-refusal` exercises the existing original Map/Close consumer with
an independent native MMIO lease, a second exhausted Map, and exact caller teardown.
`--lost-mmio-map-reply` hides reply/query until same-original cleanup. Map2 never
repeats; unknown outcome retains caller Region/charge, final cleanup reclaims64
bytes while the foreign Region survives. Allocator setup uses reflection only in
this diagnostic mode. MMIO byte Read/Write/backing is not implemented by this
lease-only path; Virtual-I/O transfer modes remain the executing byte consumers.
Failed assertions print and exit1.

`--mmio-map-effect-loss` uses the actual native Map producer without exhausting its
allocator, loses the Map reply/query and reconciles only the surviving original.
`--lost-mmio-close-reply` also loses Close reply/query after native retirement:
Region/charge stay pinned until exact lookup. Map2/Close2 do not repeat. This mode
needs no allocator reflection and still makes no MMIO byte-access claim.

`--mmio-registers` imports the caller's original64-byte Region buffer into the
native software MMIO owner without a byte copy. Exact RegionUse and runtime storage
reservation exclude owner access and public use release until original closure.
The host writes command17/status42 and reads status42 through kernel/provider/native
32-bit little-endian access. `--lost-mmio-write-reply` and `--lost-mmio-read-reply`
hide the corresponding effect reply/query; the same caller-retained invocation
reconciles without another write/read. `--lost-mmio-close-reply` holds original
use/Region/charge after native retirement until exact Close lookup. Final
Map1/Write2/Read1/Close1, charges64→0 and foreign Region Owned. Software ModelOnly;
gatesOFF, ISA NONE, Java skipped, ProductionQualified=false.

