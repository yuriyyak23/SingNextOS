# Contract and versioning strategy

1. Preserve current `HybridCPU.ExternalRuntime.Contracts 1.14.0` consumers; new semantic surfaces are additive and versioned.
2. Keep HybridCPU `ExternalOperationContract.Version 1.4.0` distinct from SingNext `ExternalOperationContract.Version 1`.
3. Unknown semantic contract version, enum/class or guarantee dimension fails closed for Mandatory obligations.
4. `OperationObligationsV1` lives in SingNext provider-neutral contracts and reuses existing value types rather than copying authority records.
5. `ExecutionGuaranteesV1` belongs in provider-neutral ExternalRuntime contracts only for claims the provider/runtime can express and evidence. Unsupported is explicit.
6. `SemanticExecutionBinding` is SingNext-side immutable context, references exact operation/provider/budget/Region/contract identities and generations, and is never upgraded in place.
7. Public HybridCPU ABI must not contain SingNext CapabilityId/RegionHandle/ResourceLeaseHandle, physical addresses, core/lane IDs or private topology.
8. Package version alone is insufficient: qualification binds package digest + source SHA + API baseline + runtime/test tuple.
9. Conservative 1.14 adapter may map only semantics proved by current contracts/tests; missing preemption/resource enforcement/containment is Unsupported.
