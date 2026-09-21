# Normative Co-design Invariants

The existing VNX-001..VNX-028 invariants remain normative. The following co-design invariants are additive.

1. **CD-001 Independent authorization.** Provider or CPU guarantees MUST NOT authorize an operation that SingNext has not independently authorized.
2. **CD-002 Typed guarantee satisfaction.** The irreversible boundary MUST NOT be crossed unless a versioned typed refinement relation proves all mandatory obligations satisfied. String/enum coincidence is insufficient.
3. **CD-003 Resource bound.** `actual provider consumption <= bound execution envelope <= exact live SingNext lease` for every enforced resource dimension.
4. **CD-004 Resource is not effect authority.** A resource envelope/lease never grants the semantic effect.
5. **CD-005 Staged publication gate.** For staged contours, external publication occurs only after exact SingNext publication decision and only through a provider contour that can actually withhold publication.
6. **CD-006 Lifecycle separation.** `execute != retire != provider complete != visible != published != released/reclaimed/settled`.
7. **CD-007 Generation drift.** Operation, provider, binding, contract, resource, measurement, visibility and containment generation drift fails closed.
8. **CD-008 Evidence non-authority.** Measurement, completion, certificate, replay, topology and guarantee evidence never mint capability, resource, ownership, invocation, publication or reclaim authority.
9. **CD-009 Replay admission.** Prior execution evidence never authorizes resubmission; fresh current authority/provider/runtime admission is mandatory.
10. **CD-010 Containment closure.** Successful close of an effect epoch means no member operation can later produce a new external effect.
11. **CD-011 Explicit charging semantics.** Admitted, issued, executed, replayed, squashed, retired, provider-overhead, residency and blocked/stalled work are distinct; each resource class declares chargeability.
12. **CD-012 Scheduler separation.** Planner/scheduler may select candidates but cannot make obligation/guarantee mismatch valid.
13. **CD-013 Cancellation evidence.** Cancellation request is not cancellation proof; after possible submit, reclaim waits for exact terminal evidence, containment, or declared conservative settlement.
14. **CD-014 Coherence separation.** Coherence cannot create Region ownership, visibility policy or publication authority.
15. **CD-015 Provider semantic refinement.** Precision, rounding, atomicity, ordering, nondeterminism, partial progress, failure, visibility, cancellation and measurement must refine the semantic operation contract.
16. **CD-016 No guarantee laundering.** Feature bits, plan/cache metadata or a weaker provider claim cannot be promoted to a stronger guarantee.
17. **CD-017 No donation/priority laundering.** Donation may only narrow amount, lifetime, assurance, scope, priority ceiling and delegation lineage.
18. **CD-018 Logical owner != global lock.** Scalability work may shard/escrow implementation while preserving one logical owner for each fact.
19. **CD-019 SipJob observational refinement.** Fused execution preserves authority-visible transitions of ordinary SIP; transport/serialization may disappear, owner commits may not.
20. **CD-020 ISA independence.** Unsupported Level-3 enforcement downgrades a contour; it does not justify SingNext-specific ISA coupling by default.
21. **CD-021 Obligation descriptor non-authority.** `OperationObligations` is a semantic snapshot/requirement set, not an authority handle or a signature substitute.
22. **CD-022 Guarantee descriptor non-authority.** `ExecutionGuarantees` is an exact provider/runtime claim; only the underlying runtime/provider mechanisms enforce it.
23. **CD-023 Binding non-authority.** `SemanticExecutionBinding` correlates exact identities/generations/contracts; it cannot grant permission by possession.
24. **CD-024 Publication permit negative space.** A publication decision cannot retroactively authorize execution, resource use, Region use, or provider admission.
