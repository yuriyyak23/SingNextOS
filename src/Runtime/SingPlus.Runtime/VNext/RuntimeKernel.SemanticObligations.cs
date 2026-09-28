using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    internal KernelResult<OperationObligationsV1> ConstructOperationObligationsV1(
        ProcessHandle principal,
        ExternalOperationHandle operation,
        IReadOnlyList<ResourceEnvelopeV1> resourceRequirements,
        OperationSemanticRequirementsV1 semanticRequirements,
        OperationTemporalObligationV1 temporal,
        EndpointSessionHandle? session = null)
    {
        ArgumentNullException.ThrowIfNull(resourceRequirements);
        ArgumentNullException.ThrowIfNull(semanticRequirements);

        var process = Processes.Resolve(principal);
        if (!process.IsSuccess)
            return KernelResult<OperationObligationsV1>.Fail(process.Error, process.Message!);
        var owner = new RegionOwner(process.Value!.DomainId, principal.Generation);

        var snapshot = ExternalOperations.Query(operation);
        if (!snapshot.IsSuccess)
            return KernelResult<OperationObligationsV1>.Fail(snapshot.Error, snapshot.Message!);
        if (snapshot.Value!.State is not (ExternalOperationState.Prepared or ExternalOperationState.Admitted) ||
            snapshot.Value.Principal != owner)
            return KernelResult<OperationObligationsV1>.Fail(KernelError.StaleGeneration,
                "Only an exact Prepared operation owned by the principal can produce obligations.");
        var preparation = ExternalOperations.QueryPreparation(operation);
        if (!preparation.IsSuccess)
            return KernelResult<OperationObligationsV1>.Fail(preparation.Error, preparation.Message!);

        if (session is { } sessionHandle)
        {
            var currentSession = EndpointSessions.Resolve(sessionHandle, principal);
            if (!currentSession.IsSuccess)
                return KernelResult<OperationObligationsV1>.Fail(currentSession.Error, currentSession.Message!);
        }

        var regions = new List<OperationRegionObligationV1>(preparation.Value!.RegionUses.Count);
        foreach (var request in preparation.Value.RegionUses)
        {
            var region = Regions.Validate(request.Region, owner);
            if (!region.IsSuccess)
                return KernelResult<OperationObligationsV1>.Fail(region.Error, region.Message!);
            long end;
            try { end = checked(request.Range.Offset + request.Range.Length); }
            catch (OverflowException)
            {
                return KernelResult<OperationObligationsV1>.Fail(KernelError.InvalidRegionState,
                    "Region-use range arithmetic overflowed.");
            }
            if (request.Range.Offset < 0 || request.Range.Length <= 0 || end > region.Value!.ByteLength)
                return KernelResult<OperationObligationsV1>.Fail(KernelError.InvalidRegionState,
                    "Region-use range is outside the exact live Region.");
            regions.Add(new(request.Region, request.Mode, request.Range, region.Value.MutationEpoch));
        }

        try
        {
            return KernelResult<OperationObligationsV1>.Ok(new OperationObligationsV1(
                OperationObligationsV1.CurrentVersion,
                principal,
                operation,
                session,
                regions,
                resourceRequirements,
                temporal,
                preparation.Value.VisibilityRequirement,
                preparation.Value.PublicationPolicy,
                preparation.Value.EffectPolicy,
                semanticRequirements).Canonicalize());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
        {
            return KernelResult<OperationObligationsV1>.Fail(KernelError.InvalidMessage, exception.Message);
        }
    }

    internal KernelResult RevalidateOperationObligationsV1(OperationObligationsV1 obligations)
    {
        ArgumentNullException.ThrowIfNull(obligations);
        OperationObligationsV1 exact;
        try { exact = obligations.Canonicalize(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
        {
            return KernelResult.Fail(KernelError.InvalidMessage, exception.Message);
        }

        var process = Processes.Resolve(exact.Principal);
        if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
        var owner = new RegionOwner(process.Value!.DomainId, exact.Principal.Generation);
        var snapshot = ExternalOperations.Query(exact.Operation);
        if (!snapshot.IsSuccess) return KernelResult.Fail(snapshot.Error, snapshot.Message!);
        if (snapshot.Value!.State is not (ExternalOperationState.Prepared or ExternalOperationState.Admitted) ||
            snapshot.Value.Principal != owner)
            return KernelResult.Fail(KernelError.StaleGeneration, "Operation identity or state changed after obligations were captured.");
        var preparation = ExternalOperations.QueryPreparation(exact.Operation);
        if (!preparation.IsSuccess) return KernelResult.Fail(preparation.Error, preparation.Message!);
        if (preparation.Value!.VisibilityRequirement != exact.VisibilityRequirement ||
            preparation.Value.PublicationPolicy != exact.PublicationPolicy ||
            preparation.Value.EffectPolicy != exact.EffectPolicy ||
            preparation.Value.RegionUses.Count != exact.RegionUses.Count)
            return KernelResult.Fail(KernelError.StaleGeneration, "Operation preparation no longer matches obligations.");

        if (exact.Session is { } session)
        {
            var currentSession = EndpointSessions.Resolve(session, exact.Principal);
            if (!currentSession.IsSuccess) return KernelResult.Fail(currentSession.Error, currentSession.Message!);
        }

        for (var index = 0; index < exact.RegionUses.Count; index++)
        {
            var expected = exact.RegionUses[index];
            var requested = preparation.Value.RegionUses[index];
            if (requested.Region != expected.Region || requested.Mode != expected.Mode || requested.Range != expected.Range)
                return KernelResult.Fail(KernelError.StaleGeneration, "Region-use preparation changed after obligations were captured.");
            var current = Regions.Validate(expected.Region, owner);
            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
            if (current.Value!.MutationEpoch != expected.MutationEpoch)
                return KernelResult.Fail(KernelError.StaleGeneration, "Region mutation epoch changed after obligations were captured.");
            if (snapshot.Value.Admission is { } admission)
            {
                var admitted = admission.RegionUses[index];
                if (admitted.Region != expected.Region || admitted.Mode != expected.Mode ||
                    admitted.Range != expected.Range || admitted.MutationEpoch != expected.MutationEpoch ||
                    admitted.State != RegionUseState.Active)
                    return KernelResult.Fail(KernelError.StaleGeneration,
                        "Admitted Region use no longer matches exact obligations.");
            }
        }

        var now = _operabilityTimeProvider.GetUtcNow().UtcTicks;
        if (now < exact.Temporal.NotBeforeUtcTicks || now >= exact.Temporal.ExpiresUtcTicks)
            return KernelResult.Fail(KernelError.DeadlineExpired, "Operation obligations are outside their exact temporal interval.");
        return KernelResult.Ok();
    }
}
