# ADR-006 — Pre-submit cancellation and local authority release observations

Status: accepted for the additive managed owner projection contour, 2026-10-02.

ExternalOperationAuthority commits CancelledBeforeSubmit/TeardownCancelledBeforeSubmit
and Released from Prepared/Admitted through real kernel cancellation and process teardown.
P05 currently validates these facts but erases the whole history as an empty prefix.
Submit, Settled, EffectClosedWithoutPublication or generic Released cannot represent this
history: no operation submit occurred, no consumption settlement is implied, and local
Region-use release is not provider/mapping closure. This is the demonstrated necessity
for the narrow additive software enum extension.

Add CancelledBeforeSubmit=13 and LocalAuthorityReleasedBeforeSubmit=14 to the V1
observation alphabet. Existing numeric values, DTO version, signatures and canonical
encoding remain unchanged. Existing Submit-starting traces remain valid. A new branch
starts only with actual pre-submit cancellation, permits repeated committed teardown
cancellation observations, and ends only with the distinct local release observation.
It rejects Submit/effect/completion/publication/accounting settlement/closure/drift and
all events after local release. The existing post-submit branch cannot enter this branch.

Projection uses the existing contiguous exact owner history and transition digest,
requires Cancelled disposition, NotCrossed boundary and absent submission binding.
ResourceCancelledBeforeSubmit remains separately checked but does not fabricate a
budget settlement event. ExternalOperationAuthority and ResourceBudgetAuthority retain
their separate facts; the checker is observational, not another owner.

Actual kernel cancellation/release and process teardown are the consumers producing
the snapshots checked offline. Normal semantic live registration occurs only after
RecordSubmission; pre-submit live delivery is not qualified by this decision and its
registration timing is unchanged. No fake registration/closure API is introduced.

Unsupported older consumers must fail closed on the mandatory kinds, never erase them
or silently downgrade. Executable old-binary compatibility is not claimed. Qualification
binds the exact source/checker/dependency tuple. All gates remain OFF. Claim ceiling is
RuntimeEnforced for the named managed observation guards; physical closure and broader
pre-submit live instrumentation remain FutureGated/Partial. Java excluded; ISA/opcode/
CPU architecture impact NONE. No provider package, machine legality or V2 change.
