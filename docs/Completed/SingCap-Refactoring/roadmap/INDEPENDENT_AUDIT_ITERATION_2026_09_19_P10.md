# Independent audit iteration — manifest, audit, and supply-chain binding

Date: 2026-09-19  
Baseline HEAD: `472b7c9345605f1558958f0d00e4e2f1b4177fa4`  
Pre-existing worktree changes: independent P02-P09 fixes and evidence; all were preserved.

## Disposition

| Status | Requirement | Actual path / impact | Resolution |
|---|---|---|---|
| ConfirmedDefect | Unknown policy schemas and versions fail closed. | `ServiceManifestV2.ValidatePolicy` accepted every non-empty schema and every positive version. The existing negative test used both an unknown name and malformed version/digest, so it did not prove unknown-but-well-formed rejection. | Each of the five policy slots now requires its exact existing schema name and version 1. Added unknown-name and future-version negative tests. This is contract validation, not a second policy or authority registry. |
| ConfirmedDefect | MAN-003 audit authority intent must be directly reviewable and deterministic. | `SingCapAuditV1` serialized only `ManifestDigest` and selected summary fields. Component identity, contracts, static imports, sealed types, quota/policy intent, and dependency entries were not present in the audit JSON despite the P10 evidence claiming a complete deterministic artifact. | Audit serialization now embeds the already-authoritative canonical V1 and V2 manifest JSON values and retains their digests. A focused test proves component identity and dependency content are visible and digest-bound. No values are reinterpreted or copied into runtime authority. |
| MissingCoverage / FutureGated | MAN-003 asks for a reviewable native-asset inventory, detailed unsafe/reflection/dynamic results, and an explicit AOT compiler version tuple. | `SingPlusAdmissionProofV1` currently retains only dependency/native closure digest and aggregate violation count; successful audit construction has no inventory/result list or separate compiler identity to serialize. | Not fabricated. The digest is enforced by P09, but claim-level closure for those itemized MAN-003 fields remains FutureGated until the existing proof contract is versioned to retain them. |
| FalsePositive | Same provider identity/version with changed bytes must require requalification; evidence is not authority. | `EnsureNoSameVersionArtifactMutation` rejects digest drift; runtime startup authority still comes only from `RuntimeKernel`/`CapabilityAuthority`. | Existing adversarial test passes. |

## Semantics and claims

The P10 runtime gate enforces exact equality between V2 static imports and supplied startup grants before process/capability creation. Audit possession remains unusable as a runtime handle. Manifest/audit/provider data do not express completion, visibility, publication, cancellation, release, quarantine, or reclaim.

Claim level: `RuntimeEnforced` only for exact startup static-grant equality and schema rejection; `StaticAdmission` for deterministic audit binding. The itemized native/toolchain portions of MAN-003 are `FutureGated`, so P10 is not claimed complete at Definition-of-Done level.

## Commands actually run

```text
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter FullyQualifiedName~SingCapPhase10ManifestAuditTests
First run: compile failed because the test's `System` resolved to `SingPlus.System`; corrected to `global::System`.
Final run: passed 8, failed 0, skipped 0.
```

Public/SIP/non-leak status: the audit adds canonical evidence JSON only; no handle, live reference, resolver, quota counter, CLR object graph, or capability-bearing SIP surface was added.

HybridCPU boundary status: no HybridCPU core, adapter, ISA, compiler, scheduler, or microarchitecture source was modified. Provider package/source/schema values remain evidence only. `tools/HybridCpu_ExecutableAdapter/refctor master plan2.md` remains absent.

Next slice: P11 real network/file/process service migrations and confused-deputy/lifecycle behavior.
