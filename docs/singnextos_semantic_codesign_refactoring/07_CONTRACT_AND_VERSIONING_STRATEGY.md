# Contract and Versioning Strategy

1. Define semantics before DTO layout.
2. Additive contracts only; preserve `HybridCPU.ExternalRuntime.Contracts 1.14.0` consumers until a new package is independently qualified.
3. Every new semantic contract carries explicit version and closed enums/discriminated records. Unknown versions fail closed.
4. `OperationObligations` and `ExecutionGuarantees` must have canonical serialization only if cross-process transport requires it; canonical bytes are not authority.
5. The exact `SemanticExecutionBinding` references the external operation generation and provider generation snapshot; stale drift cannot be “upgraded” in place.
6. Do not encode provider-private topology in portable OS contracts. Use semantic locality/contention/failure/coherence domains.
7. Maintain source/package binding evidence: exact source SHA + package version + package digest + API version + runtime profile.
8. New HybridCPU fields/interfaces must be additive with a compatibility adapter that maps the old semantic request to a conservative guarantee set. Missing dimensions remain Unsupported, never inferred.
9. Feature promotion is contour-specific; one provider's executable evidence cannot promote all providers.
10. Public API baseline tests must reject accidental SingNext names/provider-private hardware identities in HybridCPU contracts.
