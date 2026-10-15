# ADR-026 — Original native DMA grant producing fence

Accepted for additive actual ModelOnly producer/consumer, 2026-10-11; gates OFF, ISA NONE.

Actual RuntimeKernel TerminateProcess repro closes a foreign realm DMA grant with equal numeric leases. Provider pre/post checks and retained legacy Closed cannot prove original effect. The producing need is DMA admission/closure identity, not ISA or physical transfer execution.

Add an optional native original DMA request carrying invocation, captured realm, original root Bind and original mapping admission identity, plus exact device/mapping/range/direction. The existing native device owner retains the request/fact before possible Bind effect. Its canonical source/self, original parent and mapping factory are checked atomically with Bind under the lifecycle gate. The admitted grant references that same fact. Close carries that Bind invocation and captured realm; original source/parent factories and the grant are checked under the same producing gate before exact revocation. Raw outcomes stay with the existing owner; pure surviving Close resolution performs no effect. Missing/history loss is unknown; never NotFound→Closed.

Optional platform DMA admission/revocation no-effect interfaces explicitly guarantee NotAccepted proves zero effect of that invocation. Generic V1 status acquires no guarantee. Exact native no-effect is consumed separately by the bridge with same original owner/raw/source checks and retained history; the grant/mapping/backing remain open after a no-effect Close. Unknown invocation never retries effect. Known completed original outcome can be consumed after late local/query loss without invoking Close again.

The additive platform original Bind protocol retains one opaque request object before effect. Pure lookup requires that same object reference, captured source and existing owner history; an equal-input copy is not invocation identity. A surviving exact native admission can produce a cleanup-only grant, retained separately from the failed raw Bind response. It cannot publish DMA admission or visibility/use. The bridge consumes exact no-effect separately, or closes that original grant once before normal mapping/device/process cleanup. Missing or malformed history stays pinned, without invoking Bind again.

No new authority, global ledger, public enum/V2, external package or machine semantics. Existing local Contracts→AuthorityCore→Model project references adopt the additive native contract. Incapable V1 retains conservative pins; legacy surface remains compatible. All-fact-loss durable recovery, general unknown/physical containment, DMA execution/coherence and production deployment remain separate. Verification must exercise actual kernel foreign realm preservation, original realm closure and Region/attached charge, original owner/factory copies, unknown/malformed/lost reply, exact no-effect, partial consumption/reentry/overlap, and terminal native/affected/main exits.


Surviving-original no-effect Close resolution: the actual provider retains native
Close request/raw before callbacks and consumes a no-effect invocation only after
fresh native source and canonical original grant/admission/factory checks. Optional
IPlatformDmaGrantRevocationResolutionNoEffectProvider extends pure resolution:
NotAccepted proves only that retained invocation had no effect, never grant
closure or independent-user containment. Base resolution providers retain the
existing rule that NotAccepted is unknown. Bridge keeps the same resolution raw,
consumes only its invocation after local final source checks, and permits a
separate freshly admitted Close; it neither repeats an unknown Close nor releases
Region/backing/charge on no-effect metadata. Actual HybridCpu producer and kernel
teardown adoption are recorded in
[Close no-effect](../../artifacts/v6/iteration-20261012-dma-close-noeffect/report.md).
No external NuGet/schema/ISA delta; local optional contract, gates OFF.
