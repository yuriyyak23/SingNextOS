# ADR-007 — Non-resource local release observation after submit

Status: accepted for the additive managed owner projection contour, 2026-10-02.

Existing ExternalOperationAuthority permits post-submit local Release for an operation
without a resource binding through actual HybridCpuExternalOperationProvider.Release
and Type-2 exact provider-release consumers. Its journal has Released but no ResourceSettled.
P05 currently refuses the real history. Inventing Settled would claim nonexistent accounting;
inventing EffectClosedWithoutPublication would claim a receipt absent from this snapshot.
Generic Released keeps its existing stronger ordering requirements.

Here "without resource binding" means no ExternalOperationResourceBinding/ResourceLeaseBound
in this owner's full history, not absence of every resource or temporal budget association.
An independently bound temporal reservation can remain Quarantined after this local
release until its own real reconciliation consumer succeeds. The new event neither
settles nor releases that reservation and cannot erase its independent sidecar facts.

Add LocalAuthorityReleasedWithoutResourceBinding=15 to the software V1 alphabet. Existing
values/DTO/signatures/canonical format and post-submit resource-bound release rules stay
unchanged. Projection requires a complete guarded owner history with Submitted, no
ResourceLeaseBound and a compatible release writer state/disposition/policy. It emits the
new observation with the actual transition digest and never fabricates settlement/closure.

The checker accepts the local terminal observation from Published, Quarantined or exact
EffectClosed prefixes only. Observed ResourceAccountingQuarantined, any generation drift, unsettled active
effect/completion/visibility and pre-submit histories cannot use it. All later events are
refused. Ending this owner's local stream does not erase earlier effect/quarantine facts
or close an external provider. Observation is not authorization or safe reclaim evidence.

ExternalOperationAuthority and RegionAuthority retain local release decisions. ResourceBudgetAuthority
retains resource accounting. Type-2 parent/provider source continuity and exact Release are
separate facts outside the generic snapshot; its closure consumer is not replaced. Hybrid
provider Release's caller-supplied closure decision does not qualify physical containment.

Qualify actual managed provider/kernel histories and negative guards on the exact source/
dependency tuple. Unsupported mandatory kinds require fail-closed consumers without erasure
or silent downgrade; executable older binary compatibility is not claimed. No new owner,
closure API, package or V2 change. All gates OFF. Named managed observation RuntimeEnforced;
physical closure/deployment FutureGated. Java excluded; ISA/opcode/CPU architecture NONE.
