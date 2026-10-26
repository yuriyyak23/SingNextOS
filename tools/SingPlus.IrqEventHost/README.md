# Software IRQ/event host

Runs original native software signals through existing provider, kernel event delivery
and exact cleanup, followed by component-scoped generated driver-session delivery.
The executable checks original replay after acknowledgement produces no second event,
Region release, independent direct/component charge512->0 and zero native IRQ/device/root
users. The default producer has no reflection, fake producer or test-only callback.

After restore when dependencies change:

```powershell
 dotnet build tools/SingPlus.IrqEventHost/SingPlus.IrqEventHost.csproj --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
 dotnet tools/SingPlus.IrqEventHost/bin/Debug/net11.0/SingPlus.IrqEventHost.dll
 dotnet tools/SingPlus.IrqEventHost/bin/Debug/net11.0/SingPlus.IrqEventHost.dll --irq-capacity-refusal --lost-irq-bind-reply
```

The optional signal API is a trusted software input-source producer. Semantic names
select an unambiguous already admitted original IRQ; they confer no Sing authority.
The component session transports only committed kernel event evidence. The admission
image is metadata, not machine-executed driver code. Physical IRQ requires independent
producer/qualification. Gates OFF; ISA NONE; Java skipped; no ProductionQualified.

Capacity mode produces one independent signal/event, then exhausts the actual native
allocator before another IRQ Bind. Optional reply/query loss retains original endpoint
admission/device/Region/charge. Waiter cancellation is local notification, not containment.
Existing CloseKernelEventEndpoint consumes exact original no-effect before caller teardown:
Bind2 (independent1/refusal1), no repeat, charges512→0, native users0, foreign Region Owned.
Failed assertions print their reason and exit1 without an interactive crash reporter.

`--driver-mmio-registers` opts the component DriverResourceSet into original64-byte
software register backing, alongside its IRQ route and512-byte driver Region.
The host writes command17/status42, receives a generated typed IRQ event, then
reads status42 with a caller-retained component invocation. The event session
still carries only committed event evidence; register access uses kernel component
and capability admission. `--lost-driver-status-reply` reconciles the same read;
`--lost-driver-mmio-close-reply` retains original use/charge576 until exact Fault
teardown lookup. Final Write2/Read1/Close1, charge576→0; ModelOnly, gatesOFF, ISA NONE.

`--supervisor-mmio` runs a managed MMIO driver through actual supervisor admission,
health failure, predecessor closure and fresh generation restart. Optional
`--lost-supervisor-mmio-close-reply` loses the native Close response and query:
original RegionUse/64-byte charge remain held, Observe reconciles the same Close,
and generation2 gets a new zeroed backing only after generation1 reclaim.
Write1/Read1/Close2, both budgets64→0. Ordinary fresh restart; no P08 staged
ExternalOperation predecessor, stateful resume or cold recovery claim.

`--supervisor-mmio --lost-supervisor-mmio-map-reply` loses original Map response
and query during Start. No service Instance is published; supervisor Observe
consumes rollback through the original admission plan/component owner. Unknown
holds original use/64-byte charge. Same-original repair yields Map1/Close1,
Released use and charge64→0, retaining Failed metadata and no implicit restart.

`--supervisor-mmio --restore-mmio` checkpoints an ordinary SIP logical state,
publicly drains its original generation, and executes authorized fresh driver
admission with new MMIO backing. This is an explicit fresh role/admission, not
restoration of driver/native state. `--lost-restore-mmio-map-reply` retains the
unpublished original target/use/charge64 until original teardown: Map1/Close1,
charge64→0. Live platform/MMIO source remains NonCheckpointable.

Restore mode supports `--revoke-restore-mmio-source` or
`--delete-restore-mmio-source`: after native Map effect, invalidate checkpoint
source and lose original Close reply/query. No restore receipt is published;
target64 stays charged until permission-independent original teardown lookup.
An independent foreign OwnedBuffer retains bytes99/charge32 through target
cleanup. It is not a foreign MMIO mapping. Map1/Close1, no repeat.

`--driver-mmio-dma` adopts the existing component DMA policy/map/grant owner:
command17 precedes an original64-byte cross-owner copy, exact DMA grant/map
closure precedes status42/typed IRQ/read42. MMIO backing, source input and
separate destination backing are distinct Regions. Source charge640→576;
destination64 stays live through event/status consumption, then→0; component
576→0. Original request/result survives to cleanup; metadata read cannot repin.
Optional lost driver status/MMIO Close flags remain applicable. This is software
DMA adoption, not a staged ExternalOperation restart prerequisite.

`--driver-mmio-dma --lost-driver-dma-reply` retains the caller's original request
through reply/query loss. Unknown holds source640/destination64 and permits no
Copy repeat or success result. Same-original cleanup closes grants/custody;
caller closes both original mappings separately before source release. Only
then failure status255/typed IRQ is published. Destination remains charged64
until independent teardown; source/component640→576→0. This is application
failure status, not a machine fault-kind claim.

`--lost-driver-dma-mapping-close-reply` together with lost DMA reply progresses
partial source mapping retirement through existing original Close query. Unknown
holds source Region/charge640 and destination64, denying source release and
status publication. Same-original Observe consumes proof without repeat Copy
or source Close, then failure255/IRQ proceeds. Raw retention is warm only.

`--malformed-driver-dma-reply` replaces observed Copy/query shape with a
contradictory Completed+NoEffect object. Actual native Copy still executes once.
Provider raw-reference continuity denies a different later raw object; caller
retains original continuation/pins, publishes no status/IRQ/result. This mode
verifies retained in-memory model resources before host exit, not containment.
`--foreign-driver-dma-mapping` adds an independent native mapping/buffer32 with
bytes99: it stays charged and mapped through own cleanup/refusal, then its own
process teardown reclaims it. Foreign MMIO/device lifetime is not claimed.

The DMA caller reads an original admitted component command before creating
source/destination buffers, mappings or grants. Command17 selects Copy;
`--driver-dma-unknown-command` supplies command99 and produces failure255/IRQ
with Copy0 and no DMA acquisition. Command read is application selection,
not permission or a staged operation predecessor. Read counts include command
and status; no effect replay on lost status.

`--lost-driver-command-reply` loses the original command read/query before any
DMA acquisition. Same-original Resolve reconciles without another Read; unknown
holds component576, Copy0, no status write. Result is application data; current
component/process checks and ordinary map/grant/provider admission still apply
before dispatch. Combine with unsupported command99 or DMA/status reply loss.

`--cancel-driver-dma-before` and `--cancel-driver-dma-after` forward a token on
one original DMA request. Before Copy: Copy0; after native effect: Copy1 and
necessary original completion/visibility observation, then no result publication.
Caller separately cleans grants/custody/mappings and Regions before failure255.
Cancellation alone is neither no-effect nor containment; foreign mapping32
survives until independent teardown. No locality evidence cancellation shim.

Cancellation can now compose with lost Copy reply/query without disabling the
loss mode. The caller preserves actual unknown error, retains original custody
and charges, and reconciles before cancellation/failure status. Optional
`--lost-driver-dma-grant-close-reply` retains partial original grant retirement;
each of the two grants closes once, retries query existing originals. Combine
with mapping Close loss and foreign mapping survival; cancellation is no proof.

`--lost-irq-publication-ack` discards the outer ProcessNext acknowledgement and
reconciles the captured original publication without another Poll. The client
receives the original typed event once. ReconcileLastPublication also reads
metadata after session close removes routing; this never grants IRQ permission,
contains independent users or repins backing.
