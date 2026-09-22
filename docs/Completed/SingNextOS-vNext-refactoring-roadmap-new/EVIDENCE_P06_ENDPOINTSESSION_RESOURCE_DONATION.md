# P06 evidence — EndpointSession resource donation

## Disposition and live tuple

- Normative baseline: `6227ea7cf258ef6ffce52001d4d2ffee07355b35`.
- Phase-entry and qualification HEAD: `8c3f55e47555b2db99356b861ee404211d072edc`.
- Claim: `RuntimeEnforced`, limited to the internal host/JIT `ComputeTime/Nanoseconds` invocation donation contour.
- `FG-VNX-SESSION-DONATION`: **OFF**. No production service is migrated and the ordinary SIP path remains the default.
- The dirty tree recorded before qualification contained the uncommitted P05 evidence/refinements listed by `git status --short`; they were preserved. No P14 remediation or historical-roadmap deletion was modified or attributed to P06.

## Requirements and owners

| Requirement | Authoritative owner / implementation | Executed disposition |
|---|---|---|
| invocation-scoped, non-ambient donation | `EndpointSessionInvocationRegistry` exact session/invocation record | exact caller/service/session/invocation generations are checked; another invocation cannot resolve or close the donation |
| monotonic permission delegation | existing `CapabilityAuthority` via `Delegate` | child envelope, assurance and derivation depth narrow; derived grants are revoked on terminal paths |
| one quantitative charging lineage | existing `ResourceBudgetAuthority` | root reserves against the caller; nested calls use `SplitLease`; service budget is not silently charged |
| generated sentry is the consumption boundary | generated SIP sentry resolver plus `EnterDonatedSipResourceAdmission` | the exact invocation donation is revalidated and its exact lease is consumed without a second reservation |
| pre-submit versus possible-submit terminal policy | invocation owner + budget owner | pre-submit cancellation releases; possible submit, provider ambiguity, or session loss quarantines without refund |
| nested provenance and narrowing | donation binding stored on the invocation record | provenance is extended, charging owner retained, priority/assurance/envelope can only narrow |

There is no new capability, budget, resource, effect, provider, Region, publication, or visibility ledger. The donation record is a binding/provenance record inside the existing invocation owner. `CapabilityAuthority` remains permission owner and `ResourceBudgetAuthority` remains the sole quantitative owner.

## Defects remediated

1. No live invocation donation protocol existed. Added exact invocation binding, nested derivation, caller-funded lease split, provenance, and terminal states.
2. Donation cleanup was absent from invocation/session terminal paths. Terminal invocation settlement and deferred session cleanup now revoke the derived grant and either cancel pre-submit or quarantine after the possible-submit boundary.
3. The P04 admission commit assumed that effect principal and budget owner were identical. It now carries an exact `BudgetOwner`; normal P04 calls preserve the old principal-owned path, while P06 can consume one exact donated lease without double charging.
4. A capability session constraint cannot be retargeted monotonically across a nested downstream session. Exact invocation/session binding therefore remains in the invocation owner, while the resource-use capability itself narrows only resource constraints and subject. Consumption is unavailable without resolving the exact invocation binding.

## Negative and race coverage

`VNextPhase06ResourceDonationTests` proves:

- generated sentry consumption with no second charge;
- client → server → downstream provenance and monotonic envelope/assurance/priority narrowing;
- 12 concurrent child attempts against 100 units produce nine 10-unit children plus the 10-unit parent remainder, never more than 100;
- duplicate bind, stale invocation generation, amount/assurance widening and ambient reuse fail closed;
- pre-submit terminal settlement refunds exactly once;
- possible submit plus draining/session loss retains charge in `Quarantined` state;
- derived grant revocation and idempotent terminal cleanup.

Applicable invariants reviewed: VNX-001, VNX-003 through VNX-009, VNX-011 through VNX-013, VNX-018 through VNX-020, VNX-023 and VNX-024. Effect capability remains independent from resource permission; lease settlement does not publish output, release Region use, or transfer ownership. Plans, caches, receipts and telemetry were not used as authority.

## Commands and actual results

```text
git rev-parse HEAD
  8c3f55e47555b2db99356b861ee404211d072edc

git status --short
  P05 refinements/evidence plus the P06 files recorded in the qualification tuple; no destructive operation performed

dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~VNextPhase06ResourceDonationTests" --verbosity minimal
  Passed 6, Failed 0, Skipped 0

dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~VNextPhase02ResourceGrantTests|FullyQualifiedName~VNextPhase03ResourceLeaseTests|FullyQualifiedName~VNextPhase04CrossOwnerAdmissionTests|FullyQualifiedName~VNextPhase05SipResourceSentryTests|FullyQualifiedName~VNextPhase06ResourceDonationTests|FullyQualifiedName~Phase142InlineInvocationOwnerTests" --verbosity minimal
  Passed 44, Failed 0, Skipped 0

dotnet build SingNextOS.slnx --no-restore --verbosity minimal
  Build succeeded; 0 warnings, 0 errors

dotnet test SingNextOS.slnx --no-build --no-restore --filter "FullyQualifiedName!~SingPlus.Tests.Gui" --verbosity minimal
  Passed 1624, Failed 9, Skipped 2
```

The nine full-suite failures are pre-existing/unrelated: seven missing historical SingCap/HybridBoot artifacts, one stale user-owned P14 qualification tuple expectation, and one security-profile project-list drift. P06 focused and directly affected regression lanes are green; these failures were not hidden, skipped, or relabelled.

## Claim boundary and FutureGated work

The only claim is internal host/JIT `RuntimeEnforced` donation for one resource family. The gate remains OFF because no production SIP consumer and no P16 production-shaped tuple exist. P07 owns exact `ExternalOperation`↔lease terminal reconciliation and provider receipt correlation; bypassing it would make a successful/provider-lost operation indistinguishable for exact settlement. Guaranteed reservation, NativeAOT, HybridCPU/provider execution, hardware, QEMU, firmware, CXL boot, and production qualification remain excluded.

No HybridCPU ISA, VLIW, pointer/register/lane/opcode, compiler-to-ISE, pipeline, replay, memory-controller, scheduler, or microarchitecture work was performed.
