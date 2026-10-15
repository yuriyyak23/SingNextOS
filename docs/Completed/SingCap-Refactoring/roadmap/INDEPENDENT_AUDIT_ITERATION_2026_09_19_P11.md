# Independent audit iteration — native service migrations

Date: 2026-09-19  
Baseline HEAD: `472b7c9345605f1558958f0d00e4e2f1b4177fa4`  
Pre-existing worktree changes: independent P02-P10 fixes and evidence; all were preserved.

## Disposition

| Status | Requirement | Actual path / impact | Resolution |
|---|---|---|---|
| ConfirmedDefect | Every migrated effect must use one correlated admission attempt; `Validate()` is not an effect lease. | `RuntimeProcessServiceHost` Process.Create and `RuntimeNetworkServiceHost` Network.Open used `NativeServiceDispatch.ValidateExact`, then created process/socket state. Capability revoke or incompatible session transition could linearize after validation but before the effect. The P11 confused-deputy review incorrectly described both paths as participants in a capability operation lease. | Both paths now use the existing `RuntimeKernel.AdmitSessionCapabilityEffect` with exact subject, service, session, capability, resource, generation, and operation. The lease surrounds validation and state creation and is disposed before response publication. No registry or second ledger was added. |
| FalsePositive | File/object and process-control operations require both exact seal identity and exact capability authority. | File commands and Process V2 control acquire seal pins, acquire session/capability leases, revalidate the seal, and then mutate the owning lifecycle. | Existing adversarial and real generated-client tests pass. |
| FutureGated | Legacy Process messages 2-7 do not carry sealed control identity. | They remain explicitly non-ManagedCap compatibility surfaces; ManagedCap uses messages 8-13. | No silent upgrade or stronger claim. |
| FutureGated | File/network backends are in-memory pilots. | There is no external provider call or ambiguous provider result in these paths. | Provider cancellation/quarantine/publication semantics are not inferred from these pilots. |

## Semantics

Process.Create and Network.Open now linearize admission in the existing session/capability authority composition. Revocation blocks admissions not yet committed; disposal of an admitted lease does not fabricate cancellation or provider closure. Object close still linearizes in `SealedObjectAuthority.BeginClose`, then removes the service record and revokes the correlated capability. Response publication remains a distinct endpoint-session lifecycle step after the host operation returns.

The seal remains identity/lifecycle-only; rights, operations, subject/session constraints, generation, and quota remain in `CapabilityAuthority`. Service dictionaries correlate owned objects but are not capability ledgers and contain no copied remaining counter.

## Commands actually run

```text
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter FullyQualifiedName~NativeSystemServiceVerticalSliceTests
Initial compile after the Process.Create change exposed C# switch-section scoping for a using declaration; the case was explicitly scoped.
Final run: passed 18, failed 0, skipped 0.
```

Public/SIP/non-leak status: no public contract changed. Generated clients still carry explicit capabilities and bounded copied values; no ambient authority, generic resolver, sibling enumeration, or raw mutable graph was added.

Claim level: `RuntimeEnforced` for the corrected in-memory Process/File/Network composition paths only. No real-provider, hardware-isolation, ProductionCandidate, or CHERI-equivalent claim.

HybridCPU boundary status: no HybridCPU core, adapter, ISA, compiler, scheduler, or microarchitecture file was changed. `tools/HybridCpu_ExecutableAdapter/refctor master plan2.md` remains absent.

Next slice: P12 provider-neutral HybridCPU executable-adapter boundary and exact request/correlation/generation/receipt mapping.
