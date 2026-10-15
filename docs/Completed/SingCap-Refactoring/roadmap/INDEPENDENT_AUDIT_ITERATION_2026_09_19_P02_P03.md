# Independent audit iteration — capability ledger, constraint algebra, and effect admission

Date: 2026-09-19  
Baseline HEAD: `472b7c9345605f1558958f0d00e4e2f1b4177fa4`  
Baseline worktree: clean (`git status --short` produced no output)

This is new audit evidence. It does not amend the historical P02/P03 evidence or imply that these results were obtained by an earlier qualification run.

## Disposition

| Status | Requirement | Live-code finding | Resolution / proof |
|---|---|---|---|
| ConfirmedDefect | Rejected requests must not exhaust non-reusable capability identity space. | `CapabilityAuthority.Mint` and `Delegate` allocated identity before constraint canonicalization/subset rejection. At the final ID, one rejected request denied a subsequent valid request without publishing a record. | Allocation now follows complete semantic validation. Boundary tests use `ulong.MaxValue` and prove that rejected mint/delegation preserves the final identity. |
| ConfirmedDefect | Operation authority must remain coherent with delegated rights. | Constraint dimensions were individually monotonic, but an `Execute` operation could be paired with child `Read` rights when both were subsets of a wider parent. `AcquireOperationAuthority` consumes the operation set, making this executable authority. | Canonicalization now rejects every operation lacking its corresponding right. Adversarial mint and delegation tests cover the path. |
| ConfirmedDefect | An unconstrained subject is not a subset of an exact subject, including when the parent permits retargeting. | `TargetSubjectConstraint.IsSubset` accepted a null child target when an exact parent had `AllowsRetarget`. `Delegate` had a compensating post-check, but the algebra itself was unsound. | Subset logic now requires a concrete child for an exact parent. Direct negative and positive retarget tests were added. |
| ConfirmedDefect | Failed effect admission must not consume one-shot or quota authority. | Attempt identity exhaustion was checked only after the capability lease had consumed quota and optionally transitioned the record to `Consumed`. Admission returned `CapacityExhausted` while retaining those authority mutations. | The non-reusable correlation ID is reserved before pin/lease acquisition. An exhaustion test proves no session pin, operation lease, quota charge, or one-shot transition occurs. |
| FalsePositive | Capability effects have one local authority ledger and quota lineage has one remaining counter. | `_records` remains the sole authoritative capability record store; `_opaqueIndex` is identity lookup only. Descendants retain the same `QuotaAccount` reference. | Existing architecture/concurrency tests plus the full solution test run passed. No store or counter was added. |
| FutureGated | HybridCPU refactor master-plan content. | `tools/HybridCpu_ExecutableAdapter/refctor master plan2.md` is absent. | Recorded as a limitation; no content was inferred and no HybridCPU source was changed. |

## Semantics after the fix

Capability publication remains insertion into `_records` under `CapabilityAuthority._gate`. Failed canonicalization and failed subset checks publish no record and now consume neither capability identity nor quota-account identity. Effect admission now reserves its correlation identity before the first mutable participant acquisition; the capability lease remains the exact effect-authority admission point, followed by session revalidation. Revocation policy, cancellation, provider completion, quarantine, Region reclaim, sealing, SIP surfaces, and provider mappings were not changed.

Claim level for the corrected paths: **RuntimeEnforced**, with executable focused and full-regression coverage. This is not a ProductionCandidate, hardware-capability, CHERI-equivalent, or platform-isolation claim.

## Commands actually run

```text
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter FullyQualifiedName~SingCapPhase02CapabilityLedgerTests|FullyQualifiedName~CapabilityAuthorityTests
Passed: 19, Failed: 0, Skipped: 0

dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter FullyQualifiedName~SingCapPhase02CapabilityLedgerTests|FullyQualifiedName~SingCapPhase03ConstraintAlgebraTests|FullyQualifiedName~SingCapPhase04EffectAdmissionTests|FullyQualifiedName~SingCapPhase05OpaqueV2Tests
Passed: 39, Failed: 0, Skipped: 0

dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter FullyQualifiedName~SingCapPhase04EffectAdmissionTests
Passed: 9, Failed: 0, Skipped: 0

dotnet build SingNextOS.slnx --no-restore
Build succeeded; warnings: 0; errors: 0

dotnet test SingNextOS.slnx --no-build --no-restore
HybridCPU_NeutralRuntime.Tests: passed 58
HybridCpu_ExecutableAdapter.Tests: passed 12
SingPlus.Platform.HybridCpu.Tests: passed 60
SingPlus.Tests: passed 1191, skipped 2
Total: passed 1321, failed 0, skipped 2

git diff --check
Exit code 0; only Git LF-to-CRLF working-copy notices were emitted.
```

Next audit slice: opaque handles/software sealing, followed by Region lifecycle and cross-authority composition.
