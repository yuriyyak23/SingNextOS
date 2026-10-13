# P15 evidence — observability, audit and telemetry authority boundary

## Disposition

- Normative baseline: `6227ea7cf258ef6ffce52001d4d2ffee07355b35`.
- Phase-entry/qualification HEAD: `cf66d014fd0fc19cdcabca2ef1b5b974805ecda1`.
- Claim: host/JIT `RuntimeEnforced` for the existing deterministic-trace replay and structured-telemetry projection contour only.
- `FG-VNX-AUDIT-ONLY` remains OFF. No gate is dynamically enabled by telemetry, a receipt, provider availability or configuration.
- P12-P14 dirty work and prior user/parallel work were preserved and not attributed to this phase.

## Requirement and owner chain

`DeterministicTraceAuthority` owns only bounded trace-session buffers. `RuntimeKernel.StructuredTelemetry` reads inspection snapshots from the existing process, budget, Region, cancellation, ExternalOperation, checkpoint, channel and trace owners. `TraceReplayEngine` produces diagnostic reports; it has no mint, submit, settle, publish or release path. Trace events, snapshots, replay reports and telemetry snapshots explicitly carry no effect or provider-submission authority.

The focused regression constructs dropped, duplicated and reordered trace evidence around a quarantined ComputeTime lease and proves the exact `ResourceBudgetAuthority` lease/account snapshots do not change. It also proves trace backpressure does not prevent an independent budget reservation, checks the audit-only gate is default OFF, and reflects the public observability ABI for authority handles and provider/ISA-private vocabulary. Existing focused tests cover exact projection capabilities, stale process generations, bounded subscription overflow, manifest/budget limits, replay after capability revocation, payload exclusion and separation from typed security evidence.

Applicable invariants reviewed: VNX-004, VNX-007, VNX-010, VNX-011, VNX-014 through VNX-016, VNX-022, VNX-023 and VNX-025 through VNX-028. Telemetry is evidence only; settlement, visibility, publication, Region release and provider submission remain transitions of their exact owners.

## Drift and defects

No production defect was confirmed in the enabled contour. The first focused run exposed only a test assertion that assumed a one-entry budget usage vector; the assertion was corrected to select the exact ComputeTime dimension and the rebuilt lane passed. Production code was not changed for P15.

The roadmap's desired fine-grained vNext events for grant derivation, lease bind/consume/settle/quarantine, donation, provider ambiguity and temporal-bound misses are not all represented by distinct live event kinds. Their underlying feature gates remain OFF, so inventing telemetry-only lifecycle state would create misleading evidence. These event additions remain FutureGated with their authoritative owner integrations.

## Commands and actual results

```text
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~VNextPhase15TelemetryAuthorityBoundaryTests|FullyQualifiedName~Phase06DeterministicTracingTests|FullyQualifiedName~Phase09StructuredTelemetryTests" --verbosity minimal
  Passed 19, Failed 0, Skipped 0

dotnet build SingNextOS.slnx --no-restore --verbosity minimal
  0 warnings, 0 errors

dotnet test SingNextOS.slnx --no-build --no-restore --filter "FullyQualifiedName!~SingPlus.Tests.Gui" --verbosity minimal
  Passed 1657, Failed 9, Skipped 2
```

The same unrelated nine failures remain: seven missing historical SingCap/HybridBoot artifacts, one user-owned SipJob P14 tuple/HEAD coupling failure, and one security-profile project-list drift.

## FutureGated and exclusions

Fine-grained resource lifecycle telemetry is owned by each existing authoritative transition point plus the trace subsystem. Missing prerequisite: enabled contour-specific transitions and correlation tests proving every emitted event follows, never precedes, the successful owner linearization. A telemetry-only substitute would falsely turn desired state into evidence and could obscure missing owner enforcement.

Ordinary SIP fallback remains unchanged. Plans, caches, receipts, replay reports and telemetry remain non-authoritative. No production, NativeAOT, provider, real-time, guaranteed-capacity, hardware, QEMU, firmware or CXL boot claim is made. No HybridCPU ISA or microarchitecture work was performed.
