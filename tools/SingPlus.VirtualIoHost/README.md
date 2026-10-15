# Bounded Virtual-I/O software host

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

Expected output: bytes17/19/23/29, OwnedMemory charge512->516->0 and nativeDevices0.
Failed checks print and exit nonzero. The default executing path needs no reflection,
fake producer or test-only runtime callback. Gates OFF; ProductionQualified false;
Java skipped; ISA NONE.

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
