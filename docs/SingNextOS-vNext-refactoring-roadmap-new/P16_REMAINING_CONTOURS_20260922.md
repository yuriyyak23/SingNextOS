# P16 remaining-contour prerequisite audit

## Disposition

P16 remains **OPEN** and P17 may not start. This audit converts the remaining broad gaps into six exact, machine-validated FutureGated contours in `P16_REMAINING_CONTOURS_20260922.json`. It does not implement a missing provider contract, durable journal, gate enablement path or hardware qualification environment.

Normative baseline is `6227ea7cf258ef6ffce52001d4d2ffee07355b35`; reviewed HEAD is `1890a8e921cfe903b5b44857e4661168bf7bbceb`. Existing dirty work and completed-roadmap moves were preserved.

## Live-source findings

- Provider resource contour: local Contracts 1.14.0 is exactly present at 74,244 bytes with SHA-256 `b96e99bda066ee585b26a11cbfa7b68ce6bf44fc0006679483ccc1a4eeb678c2`. Its ordinary ExternalOperation API separates admission, submit, completion, visibility, publication and release. It has no `ExternalResourceEnvelope`, `ExternalResourceUsageEvidence`, `ExternalBudgetLease` or `ExternalResourceAmount` type. Its README explicitly assigns ambiguous reconciliation/closure to future provider/adapter work. The HybridCPU source SHA remains locally unverified.
- Durable restart: `ResourceBudgetAuthority` is an in-memory owner. `RuntimeKernel.Checkpointing` rejects live resource leases and restores through fresh admission; it has no authenticated journal/import or cold-process reconciliation protocol. Persisting current handles would violate VNX-025.
- Controlled SMT: the executed performance artifact controls worker and process-domain counts, not physical-core/SMT sibling placement. Logical concurrency cannot be relabelled as SMT evidence.
- Live rollback: `VNextFeatureGates` has a closed vocabulary and hard-OFF result with no enablement path. Creating a test-only ON path would bypass the evidence policy.
- SipJob differential: only the resource barrier classifier exists. There is no executable resource-aware fused path to compare against ordinary authoritative traces.
- Temporal claims: current measurements are AccountingOnly and have no enforced threshold, preemption owner or provider minimum-capacity reservation.

Each matrix row names its exact owner, missing prerequisite, current evidence, unsafe-bypass reason, gate set and claim ceiling. Regression coverage checks row completeness, rejects any enabled listed gate, hashes the local package, verifies the package README boundary and absence of the expected resource/usage types, and validates the byte-pinned tuple.

## Executed commands

```text
dotnet build tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --verbosity minimal
  Exit 0; 0 warnings, 0 errors

dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~VNextPhase16RemainingContourTests" --verbosity minimal
  Exit 0; passed 3, failed 0, skipped 0
```

The initial focused run found only a test-literal defect: the exact README sentence wraps across a newline inside the nupkg. The assertion was narrowed to the stable phrase `reconciliation/closure belongs to the provider`; the package and prerequisite conclusion were unchanged.

Final canonical `.\eng\qualify-vnext.ps1` result after adding the matrix regression:

```text
solution build: Exit 0; 0 warnings, 0 errors
focused vNext lane: passed 155, failed 0, skipped 0
full non-GUI SingPlus.Tests: passed 1498, failed 0, skipped 2
other projects: passed 90 + 58 + 60, failed 0, skipped 0
aggregate: passed 1706, failed 0, skipped 2
git diff --check: Exit 0; line-ending warnings only
```

## Claim boundary

This audit supports `StaticAdmission` of the remaining-contour classification only. It does not raise any earlier implementation claim. All `FG-VNX-*` gates remain OFF, `ProductionQualified` remains false and `p17MayStart` remains false.

Ordinary SIP/Compute/ExternalOperation behavior is unchanged. Package DTOs, receipts, plans, cache entries, telemetry and this matrix remain evidence, not authority. No HybridCPU ISA, VLIW, lane/opcode, pointer/register, pipeline, replay, scheduler-legality, memory-controller or microarchitecture work was performed. No NativeAOT, provider, real hardware, QEMU, firmware or CXL boot execution is claimed.
