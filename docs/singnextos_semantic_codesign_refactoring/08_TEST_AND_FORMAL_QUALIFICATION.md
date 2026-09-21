# Test and Formal Qualification Strategy

## Executable evidence
- unit tests: typed refinement, canonicalization, reject taxonomy, state transitions;
- property tests: monotonic/narrowing algebra, no amplification, overflow/wrap boundaries;
- concurrency tests: final revalidation races, duplicate submit/settlement/publication, ABA/generation drift;
- fault injection: timeout/disconnect/reset/restart/reordered/duplicated evidence;
- differential tests: ordinary SIP versus SipJob authority-visible trace;
- provider conformance: exact guarantee/enforcement/measurement mappings;
- performance: owner-lock wait, admission latency, manycore throughput, SMT contention, provider queue pressure.

## Formal tools by proof obligation
- **TLA+/PlusCal:** global cross-owner state machine, liveness, quarantine/containment, multi-resource acquisition and duplicate/reordered event races.
- **Alloy:** compact type/refinement relation checks, identity/generation uniqueness, illegal combinations and small-scope counterexamples.
- **Ivy or protocol-focused model checker:** only if a distributed/remote provider protocol is introduced.
- **Lean/Coq:** defer unless the typed refinement algebra or provider semantic equivalence becomes stable enough to justify machine-checked theorem investment.
- **Model-specific exhaustive state exploration:** finite cancellation/publication/epoch-close state machines.

Formal models are evidence, not runtime authority.
