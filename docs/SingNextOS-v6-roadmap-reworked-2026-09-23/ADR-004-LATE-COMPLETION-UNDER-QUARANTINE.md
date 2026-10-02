# ADR-004 — Late completion observation under quarantine

Status: accepted for the additive managed V1 observation contour, 2026-10-02.

The existing ExternalOperationAuthority can record exact Submitted-binding completion
after ResourceQuarantined. ResourceBudgetAuthority can subsequently settle its exact
receipt. Rejecting that owner history loses a real completion observation; dropping it
would make refinement compare an incomplete history.

Accept one RetireOrComplete observation while Quarantined without changing the
quarantined state. Reject repeated completion across the entire operation history,
including a completion observed before quarantine. Generation drift remains fail closed.
Settlement remains independent; neither completion nor settlement enables visibility,
publication or release. Exact EffectClosedWithoutPublication remains a separate event
required for the existing no-publication release path.

No public enum, signature, encoding, package or schema changes. Existing valid V1 traces
remain valid. Projection observes committed owner transitions only; no owner, provider
callback, authority decision or reclaim path consults its observation state. Full
post-quarantine visibility/publication vocabulary remains Partial and fails closed.
All v6 gates OFF. Claim ceiling RuntimeEnforced for these managed observation guards;
physical closure FutureGated. Java excluded. HybridCPU ISA/opcode/CPU impact NONE.

Required checks: actual owner quarantine/completion and exact kernel settlement,
duplicate completion, forbidden visibility/publication/release, generation drift,
independent closure ordering, focused and broad current-source regressions.

Terminal disposition extension — 2026-10-02: actual DeviceComplete:Cancelled and
DeviceComplete:Faulted also carry RetireOrComplete. The existing transition event is
bound into EvidenceDigest, preserving outcome correlation. A fresh terminal failure
adds Quarantined after completion; a previously quarantined history stays quarantined.
No terminal response implies containment/closure, and exact budget settlement remains
independent. Live sink tests cover both dispositions before/after resource quarantine,
stale binding and duplicate refusal without owner mutation, forbidden visibility and
idempotent exact settlement. Public V1 enums/APIs unchanged. Existing supported
successful traces remain compatible; cancellation traces now preserve their real
completion observation. Fault/cancel completion observation is closed for this named
managed contour; post-quarantine visibility/publication remains Partial.
