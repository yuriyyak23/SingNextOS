# P07 evidence — ExternalOperation resource binding

## Disposition and exact contour

- Normative baseline: `6227ea7cf258ef6ffce52001d4d2ffee07355b35`.
- Phase-entry and qualification HEAD: `8c3f55e47555b2db99356b861ee404211d072edc`.
- Claim: internal host/JIT `RuntimeEnforced` exact `ComputeTime/Nanoseconds` ExternalOperation↔lease binding and settlement.
- `FG-VNX-EXTOP-RESOURCE-BIND`: **OFF**. No production provider path is migrated and no provider package/artifact was qualified.
- The pre-existing dirty P05/P06 work and user-owned historical/P14 state were preserved; no reset, checkout, clean, commit, push, deletion, or overwrite was performed.

## Requirement → owner → behavior → evidence

| Requirement | Authoritative owner | Live behavior and evidence |
|---|---|---|
| exact opaque operation↔lease correlation | `ExternalOperationAuthority` | stores operation generation, budget owner/lease generation, envelope and host-provider identity/generation; public receipts/DTOs are not authority |
| quantitative bind/consume/reconcile/settle | `ResourceBudgetAuthority` only | P04 binds and consumes the exact lease; P07 validates evidence then invokes the budget owner outside the ExternalOperation lock |
| one settlement winner | ExternalOperation transition plus budget owner | `Consuming/Quarantined -> Settling -> Settled/Quarantined`; concurrent duplicate receipt evidence cannot double settle/refund |
| provider ambiguity | both existing owners, independently | possible submit/provider loss quarantines the lease; no automatic refund |
| completion/visibility/publication separation | `ExternalOperationAuthority` and publication path | completion permits quantitative settlement but does not make output visible or published |
| Region independence | `RegionAuthority` | budget settlement never releases RegionUse; reclaim is blocked while any exact active use exists |

No second resource/budget/capability/publication ledger was introduced. The resource binding is correlation state owned by the existing ExternalOperation record; budget liveness and amounts remain exclusively in `ResourceBudgetAuthority`. Provider usage evidence is versioned internal evidence and has no submit, mint, release, publication, or authorization method.

## Defects found and remediated

1. P04 had procedural correlation but `ExternalOperationAuthority` did not own an exact opaque operation↔lease binding. Added exact generation-bound binding and resource terminal state on the existing operation record.
2. No single-winner usage-evidence settlement existed. Added begin/complete settlement reservation so budget work occurs outside the operation lock; exact duplicate terminal evidence is idempotent, conflicting/reordered/cross-operation evidence fails closed.
3. Generic provider loss did not quarantine a P07-bound lease. The existing provider-loss transition now also asks `ResourceBudgetAuthority` to quarantine the exact bound lease and records correlation state, without treating the receipt as authority.
4. `RegionAuthority.Release` blocked only writable active uses. A read-only provider use could therefore be reclaimed after budget settlement. Release now rejects every active exact-generation RegionUse; borrow/transfer policies were not broadened.
5. P04 pre-submit compensation now terminates the exact resource binding as `CancelledPreSubmit`, preserving idempotent refund semantics.

## Negative, race and independence coverage

`VNextPhase07ExternalOperationResourceBindingTests` (7 tests) covers:

- completion before visibility: settlement succeeds while publication remains forbidden;
- publication callback failure after settlement: charged usage remains and RegionUse still blocks reclaim;
- provider loss after possible submit: exact lease remains charged and quarantined;
- cross-operation binding replay and provider-generation drift rejection;
- unknown/dimension-changing and reordered/pre-completion evidence rejection;
- exact duplicate receipt idempotency;
- 32 concurrent duplicate settlement attempts with one quantitative result and no double refund;
- double pre-submit disposal with one cancellation/refund.

The directly affected regression lane also executes P03–P06, ordinary ExternalOperation lifecycle, and the existing HybridCPU provider model tests. The latter remains model coverage only and does not qualify a HybridCPU artifact/provider.

Applicable invariants reviewed: VNX-001, VNX-002, VNX-004, VNX-007, VNX-008, VNX-010 through VNX-015, VNX-018 through VNX-024, VNX-026 through VNX-028.

## Commands and actual results

```text
git rev-parse HEAD
  8c3f55e47555b2db99356b861ee404211d072edc

dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~VNextPhase07ExternalOperationResourceBindingTests" --verbosity minimal
  Passed 7, Failed 0, Skipped 0

dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~VNextPhase03ResourceLeaseTests|FullyQualifiedName~VNextPhase04CrossOwnerAdmissionTests|FullyQualifiedName~VNextPhase05SipResourceSentryTests|FullyQualifiedName~VNextPhase06ResourceDonationTests|FullyQualifiedName~VNextPhase07ExternalOperationResourceBindingTests|FullyQualifiedName~ExternalOperationLifecycleTests|FullyQualifiedName~HybridCpuExternalOperationProviderTests" --verbosity minimal
  Passed 61, Failed 0, Skipped 0

dotnet build SingNextOS.slnx --no-restore --verbosity minimal
  Build succeeded; 0 warnings, 0 errors

dotnet test SingNextOS.slnx --no-build --no-restore --filter "FullyQualifiedName!~SingPlus.Tests.Gui" --verbosity minimal
  Passed 1631, Failed 9, Skipped 2
```

The nine failures are the same unrelated baseline: seven missing historical SingCap/HybridBoot artifacts, one stale user-owned P14 tuple expectation, and one project/security-profile list drift. They were not hidden, skipped, or relabelled.

## Gate, fallback, FutureGated and exclusions

The gate remains OFF because P07 executed only the internal host-model contour. Ordinary ExternalOperation/SIP paths remain available and unchanged by default. P08 owns independently revalidated ComputePlan policy; a plan cannot obtain or cache this binding. P09 owns any provider-neutral contract decision and exact locally available provider artifact qualification. Bypassing those phases would let planner/provider evidence substitute for live owner validation.

No ProductionQualified, ExecutableAdapter, EnforcedUpperBound, GuaranteedReservation, NativeAOT, real provider, hardware, QEMU, firmware, CXL boot, or HybridCPU execution claim is made. No HybridCPU ISA, VLIW, pointer/register/lane/opcode, compiler-to-ISE, pipeline, replay, memory-controller, scheduler, or microarchitecture work was performed.
