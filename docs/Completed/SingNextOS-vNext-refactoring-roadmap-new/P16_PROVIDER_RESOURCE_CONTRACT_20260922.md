# P16 provider resource/reconciliation contract — additive evidence

## Disposition

The first P16 remediation prerequisite is complete at **StaticAdmission** on HEAD `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`. P16 remains **OPEN**, P17 may not start, and both `FG-VNX-HCPU-RESOURCE-CONTRACT` and `FG-VNX-HCPU-USAGE-EVIDENCE` remain OFF.

## Contract and owner boundary

`PlatformResourceContract` v1 is an additive provider-neutral seam in the existing `SingPlus.Platform.Abstractions` owner boundary. It defines:

- a semantic `ResourceEnvelopeV1` request correlated by non-zero versioned evidence identity;
- a provider reservation with exact provider-domain and reservation generations;
- one binding to an exact provider operation generation;
- `Pending`, `ExactUsage`, `ContainedWithoutConsumption` and `ConservativeWorstCase` reconciliation outcomes;
- a pre-submit cancellation operation and a separate post-submit reconciliation operation.

The contract transports no `CapabilityId`, `BudgetReservationHandle`, `RegionHandle`, publication state, lane, opcode, slot, queue, topology, physical address or other hardware-private placement field. Provider objects are evidence only. `CapabilityAuthority`, `ResourceBudgetAuthority`, `ExternalOperationAuthority`, `RegionAuthority` and publication owners retain their independent truth.

`Pending` is non-terminal and requires zero reported consumption. Exact usage cannot exceed the envelope. Containment is the only zero-consumption terminal outcome. Conservative closure must charge the full envelope. Provider loss or an unreachable provider therefore cannot be interpreted as automatic refund.

## Negative coverage

`VNextPhase16ProviderResourceContractTests` covers:

- exact v1 request/reservation/submission/evidence validation;
- unknown version, zero amount, stale correlation generation, stale provider-domain generation and cross-operation replay;
- reconciliation amount/state canonicalization;
- non-terminal pending evidence;
- absence of local authority handles and hardware-private vocabulary.

## Executed commands

```text
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~VNextPhase16ProviderResourceContractTests" --verbosity minimal
  Exit 0; passed 11, failed 0, skipped 0

dotnet build src/Platform/SingPlus.Platform.Abstractions/SingPlus.Platform.Abstractions.csproj --no-restore --verbosity minimal
  Exit 0; 0 warnings, 0 errors

git diff --check
  Exit 0; line-ending warning only
```

## Claim boundary and remaining prerequisite

This evidence proves only `StaticAdmission` of SingNext's contract surface. It does not modify or qualify the external `HybridCPU.ExternalRuntime.Contracts/1.14.0` package, does not verify the pinned HybridCPU source SHA and does not execute an external provider. The next exact prerequisite is an executable provider plus a runtime adapter that performs prepare/bind/reconcile without treating its receipts as local budget authority.

Ordinary SIP/Compute/ExternalOperation fallback remains unchanged. No HybridCPU ISA, VLIW, pointer/register, lane/opcode, pipeline, retire, scheduler-legality, memory-controller or microarchitecture work was performed. No NativeAOT, hardware, QEMU, firmware or CXL boot claim is made.
