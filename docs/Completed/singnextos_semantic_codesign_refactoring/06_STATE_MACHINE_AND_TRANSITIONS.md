# State machines and cross-owner transitions

Use existing authoritative machines. Do not add one global runtime state machine.

- Budget: Reserved → Bound → Consuming → Settling → Released; pre-submit cancel; Quarantined → Reconciled → settlement. 
- External operation: Prepared → Admitted → Submitted → DeviceComplete → Visible → Published → Released, with dispositions/failure/ProviderLost and effect boundary.
- Region use: exact Region generation + MutationEpoch + RegionUseState; any relevant drift invalidates binding.
- HybridCPU: provider request stages plus runtime-local legality/retire/replay state remain independent.

## Irreversible boundary

The linearization candidate is the existing SingNext submit-start owner transition immediately after final revalidation and immediately before/around the provider submit callback according to the current `ResourceAdmissionProtocol` pattern. Pre-submit resources may be compensated. Once submission may have occurred, failure is ambiguous and moves affected resources/effects to quarantine/reconciliation rather than refund-by-assumption.

## Publication

For staged output only: DeviceComplete → Visible establishes provider evidence; SingNext publication owner decides; adapter invokes provider staged action; exact publication receipt advances Published. For direct/coherent output, visibility may coincide with external observability and no fictitious withheld gate is allowed.
