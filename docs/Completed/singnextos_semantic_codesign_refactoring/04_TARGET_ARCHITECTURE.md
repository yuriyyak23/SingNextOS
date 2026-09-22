# Corrected target architecture

```text
App/SIP
  -> existing SingNext authority owners
  -> immutable OperationObligationsV1 (requirements, not authority)
  -> planning/candidate selection (policy only)
  -> provider admission + ExecutionGuaranteesV1 (claims)
  -> pure typed refinement
  -> immutable SemanticExecutionBinding (exact correlation/context)
  -> final SingNext revalidation
  -> HybridCPU runtime legality (independently authoritative)
  -> provider/ISE execution
  -> retire/evidence
  -> completion -> visibility
  -> SingNext publication decision
  -> staged provider publication action when physically possible
  -> settlement -> release/reclaim
```

`SemanticExecutionBinding` is not `ExternalOperationAdmissionBinding`: the latter is the existing HybridCPU CPU-guard/provider-admission combiner. The new binding is SingNext-side exact semantic correlation. It is also not `SecureExecutionBinding`.

ISE/runtime changes are allowed in existing runtime/provider seams for measurement, cancellation safe points and contour-scoped containment. No CPU-core architecture/ISA change is part of the design.
