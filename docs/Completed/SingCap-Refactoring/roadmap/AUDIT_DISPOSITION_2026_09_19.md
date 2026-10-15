# External Audit Disposition — 2026-09-19

This disposition applies `../SingCap-Refactoring-audit.txt` to the normative roadmap. Recommendations were checked against live production code and executable tests; the audit text itself is advisory evidence, not implementation proof.

## Accepted and roadmap-blocking

1. **Cross-authority admission protocol.** Confirmed gap. A capability-ledger decision alone cannot linearize session, sealed-object, Region and external-operation state. `AUTHORITY_COMPOSITION_PROTOCOL.md` is now a mandatory P04–P08/P12 gate.
2. **Sealed handle role.** Confirmed ambiguity. A seal is object identity/resolution plus lifecycle binding, never standalone rights authority. P06 and P11 now require an exact live capability lineage/operation lease for effects.
3. **Region concurrency.** Confirmed from live code: `RegionAuthority` mutates its catalog and records without its own synchronization domain. P07 now requires a documented two-level catalog/record protocol or a proven equivalent, deterministic multi-region ordering, and no reliance on caller serialization.
4. **Effect revocation policy.** Confirmed underspecification. P04 now requires a closed static `EffectRevocationPolicy` classification and per-operation transition table; it is not caller-selectable.
5. **ManagedCap positive framework surface.** Accepted. P09 must combine a versioned allowlist of capability-safe framework references with explicit negative scanning. Unknown framework members fail closed.
6. **Contention and cross-authority qualification.** Accepted. P13 now includes concurrency scaling, lock-wait measurements, deadlock/lock-order stress and cross-authority generation races.
7. **P08 dependencies.** Accepted clarification. P08 is gated by both the P06 sealing pilot and P07 Region hardening, in addition to P05.

## Accepted with qualification

- The audit suggested per-region locking. The roadmap does not prescribe a lone per-record lock because the mutable `_regions` catalog and identity allocators also require synchronization. P07 requires a catalog gate plus per-record/striped/atomic transition domain, or another implementation with equivalent executable proof. Multi-record acquisition must use deterministic ordering or a pin/commit protocol.
- Correctness remains first: P04 may retain the single capability gate while semantics are proved. Sharding/lock-free work is not a P04 requirement and cannot change the formal admission semantics. P13 makes contention visible before any production claim.
- The cross-authority snapshot is not a new authority ledger. It contains attempt correlation and exact pins/leases owned by their source registries; copied fields are evidence and cannot independently authorize an effect.

## Rejected or corrected statements

- The audit's implementation-maturity statement that evidence exists only for P00/P01 became stale during this work. `PHASE_02_IMPLEMENTATION_EVIDENCE.md` now records successful P02 runtime qualification. P03 remains unclaimed until its own runnable tests, full qualification and evidence exist.
- No new numbered implementation phase is inserted: the mandated protocol is a cross-cutting ADR/gate consumed by existing P04, P06, P07, P08, P12 and P13. This preserves the required P00→P13 sequence.
- Provider admission is part of an external effect's full admission conjunction, but it remains provider-side enforcement/evidence. It never becomes SingNext local capability authority.

## Resulting claim discipline

No phase may claim cross-authority atomicity from DTO construction, sequential validation, a single registry lock, or tests against only one state machine. Evidence must identify the authoritative owners, pins, commit point, compensation order, provider-call boundary and race tests for the exact migrated operation.
