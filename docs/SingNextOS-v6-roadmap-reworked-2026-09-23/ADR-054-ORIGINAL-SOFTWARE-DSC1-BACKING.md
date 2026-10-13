# ADR-054 — Original software DSC1 backing custody

Status: dev implementation, 2026-10-13. ISA NONE. Production gates OFF. [Queue](../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md).

## Need and decision

Actual `RuntimeComputeServiceHost` and `SubmitPlatformDsc1Copy` need a producing byte-copy consumer. DSC1 v1 ModelOnly is lifecycle-only and must not acquire byte custody; its existing admission and checks remain unchanged. Add a separate optional original software backing contract, descriptor DSC1 v2 RuntimeAdmission, for bounded disjoint UInt8 Copy up to1MiB. This is neutral managed software production, not HybridCPU-v2 instruction execution, physical DMA/coherence, ProductionSecure or ExtHcpu005 qualification. The external gate table remains unchanged.

Kernel snapshots source into a charged private input and allocates a separately charged private output before provider entry. Each array has source.Length bytes; the original payload retains one existing BudgetAuthority reservation covering both allocations. Two32-byte Regions plus two32-byte arrays require128, not96. Capability/process and mapping admission remain independent prerequisites. Private output is published to the destination only after original byte closure and fresh local authorization. Cleanup may discard Completed output without new active permission.

The existing DSC1 operation owner retains original provider, request, mapping/domain owners, raw backing observation and terminal state. No second lifecycle ledger. Provider mapping-owned facts capture original native mapping factories; actual service uses existing exact slice mapping API, since legacy whole-region Map has no original factory fact. Native request/fact are retained before byte effect under the existing native lifecycle lock; only array custody is accepted so custom MemoryManager callbacks cannot execute under that lock. Original raw completion/no-effect includes no-further-byte-use. Known original query is pure; unknown/malformed outcomes retain leases, Region dependencies and original charge, never repeat Copy. Preparation invocation is latched before entry; unknown preparation does not retry.

Bridge stores raw reply before callbacks/query, requires same original observation from pure provider query and exact request reference, checks source/owners again before final publication and local consumption. Copies/equal tuples are not continuity. Completion read is not permission, resume or independent Region closure. Native no-further-byte-use allows private custody consumption; Region/mapping/process closure remains separately owned and committed.

## Limits

Opaque native preparation reply loss has no caller-known surviving request lookup yet. Its retained preparation/mappings/private arrays remain pinned; no-effect byte absence is not no-further-custody proof. General malformed/all-history-loss/unknown preparation resolution remains open. Warm original query recovery is not B1 cold durable continuation. Neither software descriptor nor component metadata grants hardware security or physical placement. Lifecycle-only Host reference mode remains available and accurately labelled ModelOnly.

2026-10-13 implementation extension: optional original preparation provider accepts caller-retained sealed identity before custody acceptance; pure original mapping-history query recovers the same opaque request. Cleanup-only prepared custody termination under the original native lock produces exact no-effect/no-further-byte-use without Copy. Existing provider adopts it for lost preparation reply and existing kernel teardown consumes the closure. Incapable providers and unavailable/malformed histories retain prior pins. [Current preparation block](../../artifacts/v6/iteration-20261013-dsc1-preparation/report.md); preceding opaque limitation describes the original stage, not this optional surviving-history exit.

## Verification

Actual kernel Copy, capacity refusal before provider entry, Completed cancellation observation, lost native Copy reply/query with128→64→0 cleanup and independent foreign Region, and lost preparation retention/no-repeat are required. Actual generated ComputeService client/tool must consume the software profile and verify source borrow return, original destination generations, quantitative staging cleanup and native mapping/domain retirement. Applicable focused/main evidence belongs in the current implementation report; green helper tests alone do not close adoption.
