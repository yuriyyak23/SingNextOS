# Proof-Carrying Lowering — Corrected Plan

## Structured verdict

**PHASE:** PCL cross-cutting compiler/runtime optimization  
**BASELINE:** SingPlus admission proof culture, SipJob digests/barriers, HybridCPU compiler/runtime contract v6, typed-slot compatibility validation, independent HybridCPU runtime legality.  
**VERDICT:** `CONDITIONAL GO` as an optimization/evidence track only. The original broad "semantic proof" concept is reduced to a small proof/evidence language for immutable lowering facts. PCL is not required for baseline v6 correctness.

### VERIFIED_EXISTING
- versioned compiler/runtime contract and load-time mismatch detection;
- compiler-emitted typed-slot/admissibility facts with compatibility-validation posture;
- `SingPlusAdmissionProofV1`/AdmissionVerifier evidence culture;
- SipJob canonical plan/digest/barrier machinery;
- runtime legality remains authoritative through `IRuntimeLegalityService`/GuardPlane.

### PARTIAL
- compiler facts can be carried and validated, but no end-to-end PCL proof language is currently required for admission;
- typed-slot facts are not a proof of SingNext authority or whole-program semantics.

### GAPS
- minimal proof language and canonical serialization;
- binary/bundle digest binding;
- verifier TCB and mutation tests;
- exact fallback semantics when proof is absent/invalid;
- proof-cache invalidation rules.

### REMOVE_OR_MERGE
- remove claims that compiler proves general determinism, runtime legality, current ownership or provider permission;
- remove live Region/provider generation claims from proof payloads;
- do not put full proof blobs into ISA/architectural bundle slots.

### NEW_REQUIRED

```text
CompilerLoweringEvidenceV1
  SchemaId + SchemaVersion
  CompilerContractVersion
  ToolchainDigest
  InputIrDigest
  OutputBinaryOrBundleDigest
  FootprintSummary
  AliasFacts
  OrderingPreservationFacts
  NumericModeFacts
  SafePointMap?            # optional
  StaticResourceEstimate?  # advisory only
  ProducerSignature/Digest # integrity, not authority
```

## Proof classes

1. **Footprint facts** — conservative read/write Region-relative ranges or symbolic sets.
2. **Alias/disjointness facts** — only properties mechanically checked against lowering artifacts or independently revalidated.
3. **Ordering preservation** — which source ordering constraints are preserved/emitted.
4. **Numeric semantics** — rounding/overflow/vector width/contract version where relevant.
5. **Safe-point map** — optional, used by P08 after runtime validation.
6. **Static resource estimates** — advisory/upper-bound inputs only when independently validated; not reservations.

Determinism, current authority, provider availability, live generations, attestation state and runtime legality are outside the proof language.

## Validation pipeline

```text
source/SIP intent
 -> compiler lowering
 -> evidence generation
 -> canonical digest binding to exact output
 -> verifier validates schema/toolchain/output binding
 -> optional fact-specific checks
 -> SingNext live authority still revalidated
 -> provider admission still required
 -> refinement still required
 -> HybridCPU runtime legality still required
```

If evidence is absent or invalid, the system either rejects a feature that explicitly requires it or falls back to existing runtime checks. It never grants permission because a proof object exists.

## TCB policy

The verifier must be substantially smaller than the compiler. Proof classes are accepted only if verification is cheaper and simpler than trusting the compiler. Any class whose verifier effectively reimplements the compiler is removed from v6 PCL.

## HybridCPU integration

Prefer a versioned proof digest/reference in compiler sideband or compiled-program metadata. HybridCPU consumes only the facts relevant to its own legality/execution contour. `LegalityAuthoritySource` remains runtime/GuardPlane, never `CompilerProof`.

## Security risks

- forged proof or digest substitution;
- stale proof for changed binary;
- alias laundering through incomplete footprint;
- schema confusion/downgrade;
- proof cache reused across changed compiler/runtime contract;
- proof accepted while live Region/provider generations changed.

## Required tests

- mutate output binary after proof generation -> reject;
- mutate proof schema/version -> reject;
- omit mandatory footprint fact -> reject when feature requires it;
- forge alias disjointness -> verifier rejects or runtime check catches;
- valid proof + runtime legality deny -> no execution;
- valid proof + capability revoke -> no execution;
- proof replay with changed Region/provider generation still performs live checks;
- proof absent -> reference runtime path remains correct;
- differential performance measurement demonstrates actual saved validation cost.

## Formal work

Property-test the proof verifier and canonicalization. A theorem prover is unnecessary until the proof language has stable semantics and a demonstrated optimization benefit.

## Dependencies

Stable P05 refinement dimensions and HybridCPU compiler contract v6. PCL is optional and not on the correctness critical path.

## ISA impact

`COMPILER_CONTRACT`; ISA extension = `NONE`.

## Feature gate / claim ceiling

Gate `V6-PROOF-CARRYING-LOWERING`, default OFF. Maximum initial claim: `StaticAdmission` plus measured optimization. It never earns `RuntimeEnforced` authority semantics by itself.

## Exit criteria

- proof language is minimal and mechanically verified;
- proof does not contain authority-bearing live facts;
- disabling PCL preserves correctness and only loses optimization/evidence;
- verifier and cache invalidation are mutation-tested;
- performance data shows enough repeated-check savings to justify TCB cost.
