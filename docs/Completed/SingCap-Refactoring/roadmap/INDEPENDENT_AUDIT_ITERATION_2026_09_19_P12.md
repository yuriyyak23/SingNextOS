# Independent audit iteration — HybridCPU provider boundary

Date: 2026-09-19  
Baseline HEAD: `472b7c9345605f1558958f0d00e4e2f1b4177fa4`  
Pre-existing worktree changes: independent P02-P11 fixes and evidence; all were preserved.

## Disposition

| Status | Requirement | Actual path / impact | Resolution |
|---|---|---|---|
| ConfirmedDefect | Provider callbacks/completion must correlate by exact operation, correlation, and generation. | Submit, poll, and cancellation accepted the full `ExternalOperationRequest`, but `RecordDeviceCompletion`, `RecordVisibility`, `Publish`, and `Release` accepted only `ExternalRequestCorrelation`. Their check against the adapter's current generation did not prove the callback supplied the exact request/operation identity. | All four transitions now require the full request and pass through `TryExact`, which compares the complete immutable request including operation id/generation, scope, contract version, generation set, correlation, effect, visibility, and cancellation mode. Cross-operation requests with a reused correlation fail closed before local lifecycle mutation. |
| FalsePositive | Package/source/schema evidence identifies the exact local dependency. | Both repository-local and restored `HybridCPU.ExternalRuntime.Contracts/1.14.0` nupkgs hash to `B96E99BDA066EE585B26A11CBFA7B68CE6BF44FC0006679483CCC1A4EEB678C2`; lock files pin `[1.14.0,1.14.0]`. The available local HybridCPU checkout is commit `794c4a53494f503855ac8cf209efab23fde083b2`. | Reverified locally without remote access. |
| FalsePositive | Completion, visibility, publication, and release remain distinct. | Adapter methods call the existing `ExternalOperationAuthority` transitions. Poll emits each reached stage in order; completion alone cannot publish, and provider loss without closure/containment cannot release Region uses. | Focused lifecycle/conformance tests pass. |
| FutureGated | Physical isolation and hardware capability enforcement. | The integration is an in-process provider-neutral model/runtime boundary. | No ProductionCandidate, NativeIsolated, hardware capability, or CHERI-equivalent claim. |
| EvidenceDrift | Mapping described exact callbacks but only correlation was required by live code. | `P12_HYBRIDCPU_PROVIDER_MAPPING.json` overstated the old callback surface. | Mapping now states the executable full-request rule and records this independent-audit baseline. |

## Authority and lifecycle split

Provider requests, generation sets, guards, receipts, and package identities remain correlation/evidence. They never mint or resolve a SingNext capability. Capability authority remains in `CapabilityAuthority`; Region ownership/use/reclaim remains in `RegionAuthority`; external completion/publication truth remains in the existing `ExternalOperationAuthority` lifecycle.

Adapter linearization remains: reserve the exact entry under the adapter gate, release the gate, invoke the runtime transition, then reacquire and clear the reservation. Provider callbacks and publication delegates execute outside the adapter gate and outside authority-registry locks. Reconfiguration is deferred while a reserved transition is in flight. Ambiguous cancellation or provider loss does not fabricate closure, publication, release, or reclaim.

## Commands actually run

```text
git -C "C:\Users\Yuriy Kurnosov\Desktop\HybridCPU v2" rev-parse HEAD
794c4a53494f503855ac8cf209efab23fde083b2

Get-FileHash SHA256 for repository and restored 1.14.0 nupkgs
B96E99BDA066EE585B26A11CBFA7B68CE6BF44FC0006679483CCC1A4EEB678C2 (both)

dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~HybridCpuExternalOperationProviderTests|FullyQualifiedName~HybridCpuCxlDeploymentConformanceTests|FullyQualifiedName~Phase10ProviderConformanceTests|FullyQualifiedName~SingCapPhase12ProviderMappingTests"
Passed: 14, failed: 0, skipped: 0.
```

Public/SIP/non-leak status: no public SingNext capability or SIP API contains a HybridCPU token, receipt, provider generation, or generic authority resolver. The corrected provider adapter surface carries evidence only.

Claim level: `RuntimeEnforced` for the tested local adapter lifecycle and exact-request mapping; provider/hardware enforcement remains `ModelOnly`/`FutureGated`.

HybridCPU boundary status: no HybridCPU core, ISE, ISA/opcode, compiler, register, load/store, scheduler, retire, runtime-legality, microarchitecture, or architecture file was modified. `tools/HybridCpu_ExecutableAdapter/refctor master plan2.md` is absent; its contents were not inferred.

Next slice: P13 cross-authority races, performance-claim accuracy, evidence/claim closure, and Technical Specification section 22 item-by-item mapping.
