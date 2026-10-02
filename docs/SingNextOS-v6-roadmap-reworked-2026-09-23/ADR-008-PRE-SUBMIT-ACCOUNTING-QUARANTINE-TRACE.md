# ADR-008 — Pre-submit resource accounting quarantine observation

Status: accepted for the additive managed observation contour, 2026-10-02.

Real compensation can commit ResourceQuarantined before Submit, before/after cancellation,
or even after local Released from Prepared/Admitted. Generic effect Quarantined would
invent a submitted effect; ResourceAccountingQuarantined=12 deliberately requires the
possible-effect boundary. Dropping this existing independent fact loses the real history.

Add ResourceAccountingQuarantinedBeforeSubmit=16 to the software V1 alphabet. Existing
values, DTO/signatures/canonical encoding and kind12/post-submit rules remain unchanged.
Kind16 observes only actual ResourceQuarantined with preceding ResourceLeaseBound and
no preceding Submitted/ResourceCancelledBeforeSubmit/ResourceSettled. It preserves the
local lifecycle state and binds the existing writer transition digest.

It is valid in Initial, PreSubmitCancelled and PreSubmitReleased observations. The narrow
exception after local pre-submit release preserves late accounting facts; every other
event after that local release remains forbidden. Local operation/Region-use release is
not budget release. The independent reservation/binding quarantine persists. Kind16 can
precede a later actual Submit/EffectPossible conservative history; this is observation,
never permission, no-effect proof, minimum service or closure. Once Submitted occurred,
only kind12 represents a later resource quarantine. Kind16 counts as accounting ambiguity
and cannot be laundered into the no-resource local-release branch.

Existing FailBeforeSubmit/CompensateResourceAdmissionBeforeSubmit and the existing
memory/temporal failed-close-winner observer consume the real journal. Budget quarantine
cannot cancel as pre-submit; external resource settlement requires submitted completion.
No pre-submit clearing consumer/exact clearing evidence is implemented here: this contour
retains its quarantined quantitative reservation; accounting reservation reclaim is Missing.
Local operation/Region/process reclamation follows its independent no-submit/lifecycle
facts and need not wait for this quantitative accounting fact. Neither the new event nor
process teardown releases or settles that reservation. No synthetic settlement, zero-use
receipt or closure API.

ExternalOperationAuthority and ResourceBudgetAuthority retain separate facts; checker
state is non-authoritative. Unsupported mandatory kinds require fail-closed consumption
without discard/silent downgrade; old-binary execution compatibility is not qualified.
All gates OFF. Named managed observation RuntimeEnforced; physical and deployment
FutureGated. No V2/provider package/ISA/opcode/CPU architecture change. Java excluded.
