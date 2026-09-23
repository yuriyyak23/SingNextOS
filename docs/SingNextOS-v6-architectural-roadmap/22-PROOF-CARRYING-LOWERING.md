# Proof-Carrying Lowering — Compiler/Runtime Co-Design Plan

**Status:** cross-cutting v6 plan, separate from authority and runtime-legality owners.  
**Depends on:** P01 semantic vocabulary + P05 machine-checkable refinement.  
**Baseline:** SingNextOS `b06ec5b5980acdad3143393a72a3b69c4edb5cfb`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

## 1. Goal

Turn compiler-produced semantic facts into cryptographically/version-bound, mechanically verifiable **evidence** that can accelerate admission/refinement and strengthen conformance without becoming runtime permission.

The design rule is:

```text
compiler proof != authority
compiler proof != HybridCPU legality
validated compiler proof = reusable structural/semantic evidence
```

## 2. Why this fits the existing architecture

The live tree already has:

- `AdmissionVerifier` and `SingPlusAdmissionProofV1`;
- versioned compiler/runtime contracts;
- typed-slot facts that are explicitly non-authoritative until runtime validation;
- `OperationObligationsV1`, `ExecutionGuaranteesV1` and `SemanticExecutionBindingV1`;
- HybridCPU runtime legality and replay evidence;
- SipJob plan digests/canonical verification.

PCL therefore extends an existing proof/evidence culture rather than creating a compiler trust root.

## 3. Proposed artifact

```text
CompilerSemanticProofV1
{
    SchemaVersion
    CompilerContractVersion
    ToolchainIdentity + digest
    InputIRDigest
    LoweredImage/BundleDigest
    OperationContractDigest

    ReadFootprint[]
    WriteFootprint[]
    AliasRelations[]
    RequiredMemoryOrdering[]
    AtomicRequirements[]
    NumericSemantics
    Exception/FaultSemantics
    DeterminismClass
    ReplayClass
    ResourceEstimateEnvelope[]
    SafePointMap / PreemptionHints
    IFCTransformSummary?      // optional
    Locality/DataMovementHints? // optional evidence only

    ProofClass
    ProofDigest
}
```

The first implementation SHOULD use a deliberately small proof language with deterministic canonical serialization rather than embedding a general theorem prover.

## 4. Proof classes

Suggested classes:

```text
Structural
MemoryFootprint
AliasDisjointness
OrderingPreservation
NumericRefinement
ResourceUpperEstimate
ReplayDeterminism
SafePointMap
Composite
```

Unknown proof classes fail closed if required by the operation contract; otherwise the runtime ignores them and uses the ordinary verification path.

## 5. Validation pipeline

```text
source/IR
 -> compiler lowering
 -> CompilerSemanticProofV1
 -> static canonical verifier
 -> SingNext obligation/refinement consumer
 -> provider guarantee binding
 -> HybridCPU runtime legality
 -> execution
```

Runtime checks MUST include:

1. exact compiler/toolchain/schema identity;
2. digest binding to lowered executable/bundles;
3. proof operation-contract digest matches the live requested operation;
4. proof does not claim authority-bearing facts;
5. mandatory proof facts satisfy current `OperationObligationsV2`;
6. provider guarantees independently refine the obligations;
7. HybridCPU runtime legality independently succeeds;
8. replay/reuse revalidates all mutable generations not covered by immutable proof.

## 6. Optimization opportunities

Validated proof MAY:

- precompute read/write footprint checks;
- prove alias-disjointness required for direct coherent output;
- validate memory ordering requested by P01;
- help SipJob static graph/fusion verification;
- reduce repeated compiler/runtime structural checks;
- select candidate providers earlier;
- provide exact safe-point maps for P08;
- provide resource upper estimates as admission hints;
- strengthen differential trace diagnostics.

It MUST NOT:

- mint capabilities or RegionUse;
- create provider admission;
- bypass runtime legality;
- assert current Region ownership/generation;
- authorize DMA mapping;
- prove current device trust from compile-time facts;
- convert an estimate into a guaranteed reservation.

## 7. HybridCPU integration

Prefer extending existing compiler sideband/typed-slot contract with a versioned proof reference/digest and keeping full proof blobs out of the architectural slot payload. HybridCPU ISE validates only proof facts needed by its current legality/execution contour; it need not understand SingNext capability objects.

A validated proof can become an input to `LegalityDecision`, but `LegalityAuthoritySource` remains the runtime/GuardPlane path, not `CompilerProof`.

## 8. Security model

Threats include forged proof blobs, stale proof with changed binary, malicious/buggy compiler, proof schema confusion, incomplete footprint, alias laundering, proof replay across operation/provider versions and hash substitution.

Mitigations:

- deterministic canonical encoding;
- strong digest binding to compiler/toolchain/input/output/contract;
- independent verifier implementation where practical;
- fail-closed versioning;
- random/adversarial differential execution;
- runtime spot-check/debug mode;
- no live-generation or authority fields accepted from proof.

## 9. Qualification

Required evidence:

- mutated binary with unchanged proof rejected;
- mutated proof rejected;
- wrong operation/provider/toolchain tuple rejected;
- alias proof negative corpus;
- ordering proof litmus corpus;
- numeric edge cases;
- resource estimate underflow never treated as guarantee;
- replay with changed Region/provider generations still requires live checks;
- compiler proof accepted + runtime legality denied => no execution;
- provider admitted + proof invalid => no mandatory-proof execution.

## 10. Rollout

```text
PCL-0 schema + canonicalization + gate OFF
PCL-1 Structural proof only
PCL-2 MemoryFootprint/Alias proof
PCL-3 Ordering/Numeric proof
PCL-4 Resource/SafePoint evidence
PCL-5 SipJob/provider integration
PCL-6 conformance/performance qualification
```

Feature gate: `V6-PROOF-CARRYING-LOWERING`.
