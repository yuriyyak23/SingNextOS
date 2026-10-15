# ADR-035 — Original Virtual-I/O backing and native device loan

Accepted 2026-10-11 for the actual external bounded buffer consumer. Legacy
Virtual-I/O handle metadata and IsTerminal alone cannot release a native device
borrow or establish no further use of backing. V1 entry points remain additive;
unqualified parent-device tuples remain unsupported by the executable adapter.

INeutralOriginalDeviceLoanProvider admits an original borrower into the existing
native device history under the lifecycle gate. Realm, canonical device, original
device/root admission and granted rights are checked before adding the borrower.
An issued marker detects lost history. Existing device dependency checks retain
device/root lifetime until exact borrower retirement. The loan is neither new
permission nor closure. The captured lifetime provider's query runs outside the
native owner gate; retirement consumes only a same-loan scoped NoFurtherUse fact.
Unknown, copied, absent and independent facts remain pinned. Completed exact
retirement does not require renewed active authorization.

IHybridCpuOriginalVirtualIoRuntimeV1 owns a software parent-device buffer (at most
1 MiB) under the admitted external root and actual original child factory. Intent,
self identity and issued history are retained before allocation/binding effects.
Original requests/receipts use reference continuity; equal copies cannot adopt a
fact. Existing root→child gates serialize transfer, IO Close and backing Close.
Read/write checks original factories, rights, bounds and current lifetime. IO
Close prevents subsequent transfer. Parent-device Close requires every original
user's exact closure, clears the private buffer and retains scoped NoFurtherUse.
Pure original queries never invoke Bind/Close and never infer closure from absence.
History/routing/factory loss preserves pins in both legacy and original teardown.

The SDK captures the native device-loan provider before admission, retains requests
before effects and raw responses before callbacks. Native pure queries reconcile
lost replies without another Bind/Close. Fresh generation, canonical factories and
local state revision precede publication/consumption. The actual platform provider
passes its original device factory to this SDK path. A native loan is retired only
after exact external IO and parent-device backing closure. Unknown/malformed or
lost-query composite lifetimes remain pinned, without repeating native effects.
RuntimeAdmission enables bounded binding; it does not promote executable compute,
physical device access, general unknown recovery or production qualification.

RuntimeKernel retains the original IO intent and charges the optional producer's
private-buffer bytes as OwnedMemoryBytes in ResourceBudgetAuthority before provider
invocation. This producer allocates MaximumDmaTransferBytes; V1 transfer bounds
alone do not imply new private allocation or change legacy accounting. The
same accounting record is bound to the original IO owner; no second ledger exists.
Unknown admission retains the reservation and blocks another reservation/effect
for that unresolved local scope. Quantum release participates in the existing
bridge→kernel local closure commit, after exact backing closure and independent
event/compute checks. Missing/copied quantum routing/history cannot consume charge.
The bridge's own never-invoked boundary can consume pre-effect capacity outside
owner gates; provider failure/absence is not this fact. Local consumption retries
retain original receipts and reservations and do not repeat provider effects.

Claim ceiling: in-process bounded software backing and exact lifetime cleanup.
No durable all-fact-loss/reboot continuation, authenticated cold freshness,
general composite cleanup continuation, DMA/coherence/hardware equivalence or
independent event backing containment. Gates OFF; ProductionQualified false;
Java-only skipped; ISA NONE.
