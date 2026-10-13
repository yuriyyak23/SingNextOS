# Baseline and live-code audit

## Frozen source

- SingNextOS `master`: `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e` / tree `b0a9e343df10a99e26adddbe57669ed2533d1b8e`.
- HybridCPU-v2 `master`: `794c4a53494f503855ac8cf209efab23fde083b2` / tree `189f59a4b95fb822618117c60c8224bed701f9a5`.
- Audit timestamp: `2026-09-22T01:23:00+03:00`.

## Verified SingNextOS anchors

- `ResourceBudgetAuthority`: single accounting ledger; `Reserve` accepts a vector of `BudgetAmount`; states include Reserved/Bound/Consuming/Settling/Released/CancelledPreSubmit/Quarantined/Reconciled; `SettleLease` enforces actual <= reserved.
- `ResourceEnvelopeV1`/`ResourceUseConstraintV1`: immutable semantic resource descriptors with canonicalization and subset checks; descriptor is explicitly not reservation/authority.
- `RegionAuthority`: per-Region generation/owner/state/MutationEpoch and RegionUse state; extend here rather than create second Region ledger.
- `ExternalOperationAuthority` + `ExternalOperations.cs`: Prepared→Admitted→Submitted→DeviceComplete→Visible→Published→Released, explicit effect/publication/replay/boundary semantics.
- `ResourceAdmissionProtocol`/`ExternalOperationResourceBinding`: existing prepare/revalidate/commit and exact lease/provider binding seams.
- `ComputePlanning`: current `ComputeOperationKind` = Copy/Transform/Reduce only; staged/direct publication planning exists.
- `ResourceDonationProtocol`: nested donation narrows resource, assurance and priority ceilings; uses `SplitLease` conservation.
- `Checkpointing`: restore requires a fresh process generation and fresh admission; this is the durable-authority precedent.
- SipJob admission/barriers name existing authority owners explicitly and reject unsafe multiple consumptive commits.

## Verified HybridCPU anchors

- Contracts project is 1.14.0/net11.0; ExternalOperation schema 1.4.0.
- `ExternalOperationAdmissionBinding.Evaluate` combines independent CPU guard and provider admission; result is structural submit eligibility, not OS permission.
- `ExternalOperationPublicationGate` only validates staged publication preconditions and current generations; it is not SingNext publication authority.
- `ExternalOperationAdapterSessionTests` statically show single-submit rejection, stale invalidation, exact cancellation acknowledgement and fresh admission after reconnect.
- `LegalityDecision`/`LegalityAuthoritySource` and `IRuntimeLegalityService` keep runtime legality inside ISE.
- `TypedSlotFactStaging.CurrentMode == ValidationOnly`; compiler facts are diagnostic and absence does not bypass/replace runtime legality.
- MatrixTile has real instruction/pipeline/retire code and tests; Lane6 DmaStreamCompute has runtime/backend/telemetry/external-operation bridge; Lane7 L7-SDC has queue/backend/staging/commit/fence and MatMul capability/backend tests. Executable substrate exists, but semantic co-design guarantee claims remain contour-specific.

## Evidence level

No local test execution was possible/performed through the GitHub connector. Therefore this package uses `test exists / statically inspected` and never says `passed` unless future P18 evidence records an actual run.
