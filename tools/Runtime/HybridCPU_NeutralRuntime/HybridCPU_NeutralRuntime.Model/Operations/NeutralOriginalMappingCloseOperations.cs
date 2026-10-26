namespace YAKSys_Hybrid_CPU.Core;

public sealed partial class NeutralDomainRuntimeFacade : INeutralOriginalMappingClosureProvider
{
    // Synchronizes existing dependency-owner admissions/mutations, not another authority.
    private readonly object _lifecycleGate = new();
    public Guid MappingClosureIncarnation { get; } = Guid.NewGuid();

    public NeutralOwnedRegionCloseResult CloseOwnedRegionMapping(NeutralOriginalMappingCloseRequest request)
    {
        lock (_lifecycleGate)
        {
            if (request.InvocationId == Guid.Empty || request.RuntimeIncarnation != MappingClosureIncarnation)
                return new() { Decision = NeutralOwnedRegionCloseDecision.Faulted, Reason = "Original Close source/key is invalid." };
            NeutralLeaseValidation<NeutralMappingState> mapping = _leases.Validate(request.Mapping);
            NeutralLeaseValidation<NeutralDomainState> parent = _leases.Validate(request.Mapping.DomainLease);
            if (!mapping.IsValid || !parent.IsValid || mapping.State!.OriginalClose is not null)
                return new() { Decision = NeutralOwnedRegionCloseDecision.Faulted, Reason = "Original Close owner/admission is unavailable or already invoked." };
            NeutralOriginalMappingCloseFact fact = new NeutralOriginalMappingCloseFact(request, mapping.State, parent.State!);
            mapping.State.OriginalClose = fact;
            NeutralOwnedRegionCloseResult result = CloseOwnedRegionMappingCore(request.Mapping);
            fact.Observation = result;
            return result;
        }
    }

    public NeutralOriginalMappingCloseResolution? ResolveOwnedRegionMappingClose(NeutralOriginalMappingCloseRequest request)
    {
        lock (_lifecycleGate)
        {
            if (request.InvocationId == Guid.Empty || request.RuntimeIncarnation != MappingClosureIncarnation ||
                !_dependencies.Mappings.TryGetValue(request.Mapping.Handle, out NeutralMappingState? mapping) || mapping.Lease != request.Mapping ||
                mapping.OriginalClose is not { } fact || fact.Request != request || !ReferenceEquals(fact.Mapping, mapping) ||
                !_dependencies.Domains.TryGetValue(request.Mapping.DomainLease.Handle, out NeutralDomainState? parent) ||
                !ReferenceEquals(fact.Parent, parent) || parent.Lease != request.Mapping.DomainLease ||
                fact.Observation is not { Decision: NeutralOwnedRegionCloseDecision.Closed or NeutralOwnedRegionCloseDecision.Revoked } result ||
                mapping.IsActive || _dependencies.HasActiveDependents(request.Mapping)) return null;
            return new(request, result);
        }
    }
}
