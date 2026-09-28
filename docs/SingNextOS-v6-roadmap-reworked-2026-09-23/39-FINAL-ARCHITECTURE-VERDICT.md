# Final Architecture Verdict

## Executive verdict

The v6 direction is architecturally viable **only after correction**. The strongest parts are the existing authority separation, semantic obligations/guarantees/refinement seam, staged external-operation lifecycle, independent HybridCPU legality and provider-neutral intent. The original plan becomes unrealistic when it treats all semantic dimensions as one ABI migration, puts optional research contours on the main path, or allows evidence/telemetry/provider concepts to look like new authority owners.

The corrected roadmap therefore keeps existing owners, uses V1 + additive sidecars, closes memory/DMA/refinement first, proves one staged heterogeneous vertical, and keeps persistence, IFC, trust, energy and multi-host independently gated.

## Answers to the 20 mandatory questions

### 1. Is v6 architecturally complete?
**Not as originally proposed.** It has the right architectural spine but was incomplete around generation-bound DMA invalidation, cross-provider visibility, explicit failure ambiguity/quarantine, contract canonicalization/downgrade, hardware qualification boundaries and narrow first-vertical sequencing. The corrected package closes the plan-level gaps; implementation evidence is still future work.

### 2. What directions were missing?
Explicit address-space/translation generation closure; page-fault/revoke semantics; provider-loss/effect-ambiguity quarantine; exact recovery freshness; per-contour trace projection; hardware qualification boundaries; small canonical extension/versioning rules; production artifact/supply-chain tuple discipline.

### 3. What directions were excessive?
Blanket V2 ABI replacement; generic whole-system IFC; hard-real-time/WCET ambitions without enforcement; early multi-host; broad proof-carrying semantics; any suggestion of universal temporal/energy/topology/trust authorities.

### 4. Which phases risk duplicating owners?
P03 if it creates TemporalAuthority; P12 if it creates PowerAuthority; P06 if labels become permission; P09 if trust evidence becomes authority; P04 if a TranslationAuthority duplicates platform/IOMMU state; P11 if remote leases become a second capability database. The corrected plans forbid these.

### 5. Which proposed types are unnecessary?
A blanket `OperationObligationsV2`/`ExecutionGuaranteesV2`/`SemanticExecutionBindingV2` is not justified yet. TemporalAuthority, PowerAuthority, ProofAuthority, TopologyAuthority and TranslationAuthority are unnecessary. V1 additive sidecars and narrow correlation records are sufficient until a breaking need is proven.

### 6. Where were claims too strong?
Shared coherent/atomic memory, hard deadlines/WCET, generic durability, production attestation/RAS, stateful resume, whole-system IFC, multi-host correctness and hardware security. All now have explicit lower claim ceilings.

### 7. Which contracts are under-expressive?
V1 lacks explicit memory ordering/atomicity/coherence clauses, DMA/address-space/translation generation binding, temporal guarantee classes, failure/containment clauses, persistence-domain semantics, preemption/resume classes and trust freshness predicates. Additive sidecars address this without changing V1 meaning.

### 8. Where can authority inversion occur?
Compiler proof accepted as permission; provider admission treated as SingNext authority; PASID/IOVA/mapping treated as ownership; attestation treated as capability; scheduler/topology/telemetry treated as resource authority; label treated as capability; captured state/checkpoint treated as persisted authority; remote lease treated as parent authority.

### 9. Where are stale-generation / ABA / TOCTOU bugs likely?
Region mutation between planning and submit; process/address-space reincarnation; PASID/IOMMU domain reuse; translation invalidation vs DMA; device reset/provider restart; trust evidence across reset; capture/resume across provider/Region change; remote lease epoch reuse; reboot/recovery using stale sessions; publication racing completion/visibility.

### 10. Which phases require HybridCPU changes?
P01/P04/P05 need at most runtime/sideband observability and adapter work for selected contours; P08 may require runtime safe-point/capture hooks; P02/P09/P10/P12 mainly need provider/ExternalRuntime evidence if HybridCPU is the provider. P06/P07 are usually optional sideband only. None requires ownership changes in ISE.

### 11. Which require compiler changes?
Only PCL definitely proposes compiler work. P08 may optionally consume compiler-generated safe-point maps. P01 may optionally use footprint/alias/order evidence. Core v6 correctness and QV1 do not require compiler changes.

### 12. Is a real ISA extension required anywhere?
**No demonstrated requirement.** Current roadmap impact is NONE/RUNTIME_ONLY/SIDEBAND/COMPILER_CONTRACT. Any ISA extension needs a separate enforcement-gap ADR.

### 13. Which features should remain FutureGated?
Shared atomic/direct coherent output until qualified; hard deadline/WCET; generic stateful resume; generic IFC; production device attestation; hardware RAS promotion; multi-host leases; provider-independent enforced energy guarantees; any ISA extension.

### 14. Which phases can be merged?
No owner-sensitive phases should be merged. P05 is cross-cutting and may share infrastructure with every phase. P03 and P12 share the resource algebra but remain separate semantics. P01/P04 and P07/P08 should **not** be merged.

### 15. Which phases should be split?
P08 basic preemption vs stateful resume must be separately gated. P01 staged/exclusive memory vs shared atomic/direct coherent memory must be separate subcontours. P03 accounting/upper bounds vs guaranteed deadline must be separate. P02 software/provider durability vs physical persistence-domain qualification must be separate.

### 16. Is numeric P01–P12 order correct?
**No.** Correct execution order is dependency-driven: P00/C0 -> P01 -> P04 with P05 starting early; QV1 closes the core. P03 can run in parallel. P02/P10/P08 follow their prerequisites. P06/P07/P09/P12 are optional contours. P11 is last-wave.

### 17. Is Proof-Carrying Lowering design correct?
The idea is useful only after scope reduction. Compiler evidence should cover small immutable lowering facts and be optional optimization evidence. It must not claim live authority, provider permission, current generations or HybridCPU legality. A large semantic proof language is NO-GO until a small verifier demonstrates real runtime savings.

### 18. Is the first qualification vertical correct?
A narrow heterogeneous staged-output vertical is the right first target. It should exercise authority/session/Region/budget/provider/refinement/HybridCPU legality/visibility/publication/settlement with no shared mutable memory, persistence, hard RT, multi-host or production trust dependencies.

### 19. What claims are realistic on current emulator/runtime?
Existing authority/resource/Region local enforcement; executable V1 semantic refinement; an `ExecutableAdapter` staged heterogeneous contour after QV1 work; generation/race/fault behavior in software adapters; model-checked cross-owner properties; PCL static verification if implemented. Not realistic: physical DMA isolation, persistence-domain durability, hardware attestation/RAS, hard RT/WCET, multi-host production correctness or full production security.

### 20. What is required for hardware/production qualification?
Named hardware/firmware/IOMMU/CXL/device tuples; physical memory-order and DMA invalidation tests; persistence crash/power-loss campaigns; real trust/attestation freshness/reset validation; ECC/poison/RAS fault campaigns; contention and timing characterization; energy-counter/enforcement validation where claimed; supply-chain/SBOM/reproducible artifact evidence; operational recovery/rollback drills; security review and negative/fuzz/race testing on the final tuple.

## GO / CONDITIONAL GO / NO-GO matrix

| Area | Verdict | Reason |
|---|---|---|
| architecture | CONDITIONAL GO | corrected owner/contract/DAG model is coherent; QV1 must validate it |
| SingNextOS implementation | CONDITIONAL GO | requires additive contracts, P01/P04/P05 and QV1 runtime work |
| HybridCPU runtime/ISE | CONDITIONAL GO | current legality model fits; selected contours need observability/adapter qualification |
| compiler | GO for core / CONDITIONAL GO for PCL | core needs no compiler change; PCL optional |
| emulator qualification | CONDITIONAL GO | realistic after QV1 + race/fault/differential evidence |
| real hardware | NO-GO for new generic v6 claims today | physical evidence not yet closed |
| production security | NO-GO as a blanket v6 claim; contour-specific CONDITIONAL GO later | needs hardware, ops and supply-chain evidence |
| multi-host | NO-GO / FutureGated | protocol and executable environment not closed |

## Final architectural criterion

v6 succeeds only if new semantics increase expressiveness without creating a hidden authority root in CPU, compiler, provider, topology, evidence, scheduler, mapping or replay machinery.
