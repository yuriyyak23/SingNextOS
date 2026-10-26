# SingPlus lowering-evidence emitter

This post-lowering build tool emits canonical PCL metadata for one exact compiler output.
It hashes the input IR, final binary or bundle, compiler/toolchain binary, and emitter
binary while all inputs are held open read-only. The emitted sidecar contains immutable
facts only and grants no execution, Region, capability, provider, or runtime-legality
authority.

```text
dotnet run --project tools/SingPlus.LoweringEvidenceEmitter -- \
  facts.json input.ir output.bin compiler.bin emitter.bin output.pcl
```

The fact manifest is strict JSON matching `LoweringEvidenceManifestV1`. Unknown fields,
non-canonical facts, aliased input/output paths, and oversized manifests fail closed.
The destination sidecar is written through a same-directory temporary file and atomically
replaced. Production compiler pipelines must pass their actual post-lowering artifact and
their own executable identities; this tool does not infer or grant live authority.

The HybridCPU NativeAOT single-method and adapter-presented managed-graph paths have an opt-in post-output sideband. Set
`HYBRIDCPU_SINGNEXT_PCL_EMISSION_V1` to strict JSON with `ManifestPath`, `InputIrPath`,
`ProducerPath`, `MetadataPath`, and optional `HostPath` (for example, the `dotnet` host
for a producer DLL). With `DeriveFromCanonicalIr: true`, `ManifestPath` and `InputIrPath`
are compiler-owned outputs: the restricted adapter snapshots its actual validated
canonical IR, or a deterministic method-identity-ordered graph aggregate with namespaced facts, and derives conservative exact-region/disjointness, ordering, and
integer numeric-mode facts plus managed safe-point encoded locations and live-state-shape
digests. It also emits advisory structural upper bounds for canonical instruction count,
basic-block count, and maximum operands per instruction. Graph aggregation sums instruction/block
bounds and takes the maximum operands bound across methods. Typed VectorTransfer and all four
MatrixTile lowering forms are included only when opcode/handoff/carrier identities agree, every
fallback flag is false, and the compiler-owned typed plan agrees with complete exact canonical memory
ranges or proves that compute/transpose has no external memory effect. Opaque DSC/L7 descriptors and
their runtime authority are never serialized; an authority-free compiler projection may contribute exact
DSC/L7 footprints only after descriptor validation, while missing or malformed projections fail closed.
The adapter supplies its own assembly as the toolchain identity and
the exact final output path to this emitter. If requested emission fails, the adapter
returns a nonzero status and deletes the output, its native manifest, and stale PCL
metadata. Absence of the variable preserves the existing compiler path. This integration
does not validate that caller-supplied facts were soundly derived from canonical IR and
does not affect compiler lowering, HybridCPU legality, or runtime authority.

Compiler-derived memory evidence is all-or-nothing: every declared read/write effect must
carry a non-empty, direction-consistent exact range that exactly matches the canonical IR
annotation. Missing or contradictory ranges reject the requested PCL emission and the
compiler quarantines the output; they are never silently omitted from the footprint.

For a contour that requires footprint evidence, SingNext admission also supplies the
independently expected `StaticFactSetDigest`. The verifier rejects a missing expectation
or any footprint/alias/ordering/numeric substitution before live checks. Artifact identity
changes do not change this fact-set digest, while the enclosing evidence digest remains
bound to the exact input, output, toolchain, and producer.

A safe-point map is optional evidence, not a preemption request. A contour that uses it
must independently bind `SafePointMapDigest` and invoke a live runtime/provider validator
on every admission, including static-verifier cache hits. The managed P08 reference
provider rechecks its exact provider/runtime generations and selected location/state shape;
reset makes a previously accepted map stale. No proof fact authorizes execution,
preemption, containment, or state capture.

Static resource estimates likewise carry no budget authority. A using contour must bind
the exact `StaticResourceEstimateDigest` and run a fresh generation-bound policy check,
including on verifier cache hits. The managed policy checks only compiler-structural units;
it neither maps them to time or energy nor mutates `ResourceBudgetAuthority`, reserves
provider capacity, or guarantees completion.
