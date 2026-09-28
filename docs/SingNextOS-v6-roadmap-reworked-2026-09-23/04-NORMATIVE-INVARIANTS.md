# v6 Normative Invariants

These invariants apply to specifications, runtime code, tests, adapters, compiler changes, qualification and documentation.

- **V6-001 One fact/one owner:** each mutable logical fact has one authoritative owner.
- **V6-002 Identity separation:** identity, discovery and topology never imply authority.
- **V6-003 Evidence separation:** receipts, proofs, attestation, health, telemetry and compiler metadata are evidence, not permission.
- **V6-004 Region separation:** mapping, coherence, translation and address identity never imply Region ownership/use.
- **V6-005 Effect separation:** resource permission does not imply effect permission.
- **V6-006 Provider separation:** provider admission is independent of SingNext authority.
- **V6-007 Runtime legality separation:** HybridCPU legality is independently authoritative for machine state and cannot be bypassed by SingNext or compiler evidence.
- **V6-008 Publication chain:** completion, visibility, publication, persistence, durability and release are distinct transitions.
- **V6-009 Cancellation chain:** cancel, preempt, drain, capture, suspend, resume, restart, containment and closure are distinct.
- **V6-010 Resource semantics:** reservation, upper bound, minimum service, guaranteed reservation, deadline and WCET are distinct claims.
- **V6-011 No temporal/power authority:** time/energy quantitative commitments extend existing resource ownership; scheduler/provider telemetry is not a new authority ledger.
- **V6-012 Translation separation:** VA/IOVA/PASID/IOMMU domain/mapping IDs are never authority objects.
- **V6-013 Generation completeness:** every binding depending on mutable facts carries or recomputes all relevant generations; missing mandatory generation is deny/quarantine.
- **V6-014 TOCTOU closure:** final live-owner revalidation occurs immediately before the existing commit/submit linearization point for every mutable dependency not pinned by a valid lease.
- **V6-015 No stale downgrade:** a stale strong token/proof/receipt is never reinterpreted as weaker permission.
- **V6-016 Provider callback locking:** provider callbacks do not execute while unrelated authority locks are held.
- **V6-017 Shared-mutable safety:** direct coherent/shared-mutable output remains gated until alias, atomicity, ordering, mapping generation and publication semantics are executable end-to-end.
- **V6-018 Durable freshness:** reboot/recovery never resurrects ephemeral capabilities, sessions, provider generations, mappings or RegionUse.
- **V6-019 Trust separation:** attestation/measurement is a predicate and never a capability.
- **V6-020 IFC separation:** label is not capability; declassification/endorsement requires explicit existing authority.
- **V6-021 Topology privacy:** physical/provider topology IDs never become application authority ABI.
- **V6-022 Replay separation:** replay certificate/state never grants permission and cannot revive stale mutable generations.
- **V6-023 Compiler boundary:** compiler proof is evidence only and cannot substitute for runtime legality or current authority.
- **V6-024 Exact tuple:** security/performance/production claims bind source SHA, package digest, toolchain, schema, provider/runtime and relevant hardware/firmware profile.
- **V6-025 Unknown mandatory semantics deny:** unknown mandatory enum/class/schema/version fails closed.
- **V6-026 Failure ambiguity quarantines:** if an irreversible effect may have occurred and closure is unproven, ownership/reclaim/publication paths quarantine rather than assume absence of effect.
- **V6-027 No universal GlobalState owner:** formal/global state is a specification tuple of owner states, never a runtime manager.
- **V6-028 No implicit ISA promotion:** no phase may introduce ISA changes without a separate enforcement-gap ADR and executable evidence.
