# Phase 4 — Lane7 L7-SDC External Operation Bridge

## Goal

Refactor lane7 L7-SDC so its external accelerator commands are executed through a provider-neutral SingNextOS ExternalRuntime bridge while preserving existing lane7 `SystemSingleton` placement and L7 token/commit semantics.

## Preserve architectural placement

Do not create a CXL lane or change lane7 slot classification solely for CXL. Existing L7-SDC remains a `SystemSingleton` carrier on lane7.

## Backend refactoring

Replace device-specific/fake execution assumptions with a narrow backend contract that can:

- translate validated L7 descriptors into `ExternalOperationRequest`;
- obtain SingNextOS admission;
- submit exactly once;
- consume completion/visibility/publication receipts;
- surface cancellation/reset/stale outcomes;
- expose only semantic capability/effect data to L7 runtime.

The backend must not expose CXL HDM/DPA/route/port identities to the descriptor or ISA layer.

## Token lifecycle

Extend existing accelerator token state so the runtime can distinguish:

```text
Created
Validated
Admitted
Submitted
DeviceComplete
Visible
CommitPending
Published
Released
Faulted/Stale/Cancelled
```

Exact enum names may differ, but the distinctions must be representable.

`DeviceComplete` must not implicitly advance to `CommitPending` unless visibility requirements are already satisfied by a proven provider contract.

## Descriptor semantics

Add only provider-neutral semantic fields where needed, for example:

- input/output region role;
- staged-output requirement;
- coherent-access requirement;
- external-effect constraints;
- operation class/capability requirement;
- cancellation/idempotence flags.

Avoid versioning the instruction encoding around CXL 3.x vs 4.x. Capability negotiation belongs in runtime admission.

## Admission and submit guards

Before submit require:

- current CPU owner/domain GuardPlane success;
- exact descriptor/token identity;
- SingNextOS admission receipt valid for current generation snapshot;
- no incompatible active token/footprint conflict;
- replay policy permits first submission.

## Staging

Keep staged writes as default L7 publication mode. The external provider may compute through CXL Type-2 or another accelerator, but architectural writes are published only through the existing HybridCPU commit contour after visibility and revalidation.

## Direct coherent output

Do not enable by default. A future opt-in mode requires:

- SingNextOS reports coherent direct output capability;
- exact write authority and exclusivity are proven;
- replay policy accepts the stronger effect class;
- fence/visibility semantics are explicit;
- stale/reset behavior cannot publish ambiguous data.

## Tests

Add L7 tests with a fake SingNextOS adapter for:

- admission reject;
- submit success + delayed DeviceComplete;
- DeviceComplete before visibility;
- stale generation before publication;
- device reset after submit;
- duplicate completion;
- staged commit success;
- direct-output path disabled without capability proof.

## Exit criteria

L7-SDC can run an end-to-end simulated operation through the SingNextOS adapter without any CXL-specific identity or transport rule in lane7 decode/scheduling.