using SingPlus.Contracts;
using SingPlus.Sip;

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

        KernelResult<SingProcess> process = Processes.Resolve(principal);
        if (!process.IsSuccess)
            return KernelResult<OperationObligationsV1>.Fail(process.Error, process.Message!);
        RegionOwner owner = new RegionOwner(process.Value!.DomainId, principal.Generation);

        KernelResult<ExternalOperationSnapshot> snapshot = ExternalOperations.Query(operation);
        if (!snapshot.IsSuccess)
            return KernelResult<OperationObligationsV1>.Fail(snapshot.Error, snapshot.Message!);
        if (snapshot.Value!.State is not (ExternalOperationState.Prepared or ExternalOperationState.Admitted) ||
            snapshot.Value.Principal != owner)
            return KernelResult<OperationObligationsV1>.Fail(KernelError.StaleGeneration,
                "Only an exact Prepared operation owned by the principal can produce obligations.");
        KernelResult<OperationPreparation> preparation = ExternalOperations.QueryPreparation(operation);
        if (!preparation.IsSuccess)
            return KernelResult<OperationObligationsV1>.Fail(preparation.Error, preparation.Message!);

        if (session is { } sessionHandle)
        {
            KernelResult<EndpointSessionRegistry.Record> currentSession = EndpointSessions.Resolve(sessionHandle, principal);
            if (!currentSession.IsSuccess)
                return KernelResult<OperationObligationsV1>.Fail(currentSession.Error, currentSession.Message!);
        }

        List<OperationRegionObligationV1> regions = new List<OperationRegionObligationV1>(preparation.Value!.RegionUses.Count);
        foreach (OperationRegionUseRequest request in preparation.Value.RegionUses)
        {
            KernelResult<RegionDescriptor> region = Regions.Validate(request.Region, owner);
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

        KernelResult live = RevalidateOperationObligationOwners(exact);
        if (!live.IsSuccess) return live;
        long now = _operabilityTimeProvider.GetUtcNow().UtcTicks;
        live = RevalidateOperationObligationOwners(exact);
        if (!live.IsSuccess) return live;
        if (now < exact.Temporal.NotBeforeUtcTicks || now >= exact.Temporal.ExpiresUtcTicks)
            return KernelResult.Fail(KernelError.DeadlineExpired, "Operation obligations are outside their exact temporal interval.");
        return KernelResult.Ok();
    }

    private KernelResult RevalidateOperationObligationOwners(OperationObligationsV1 exact)
    {
        KernelResult<SingProcess> process = Processes.Resolve(exact.Principal);
        if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
        RegionOwner owner = new RegionOwner(process.Value!.DomainId, exact.Principal.Generation);
        KernelResult<ExternalOperationSnapshot> snapshot = ExternalOperations.Query(exact.Operation);
        if (!snapshot.IsSuccess) return KernelResult.Fail(snapshot.Error, snapshot.Message!);
        if (snapshot.Value!.State is not (ExternalOperationState.Prepared or ExternalOperationState.Admitted) ||
            snapshot.Value.Principal != owner)
            return KernelResult.Fail(KernelError.StaleGeneration, "Operation identity or state changed after obligations were captured.");
        KernelResult<OperationPreparation> preparation = ExternalOperations.QueryPreparation(exact.Operation);
        if (!preparation.IsSuccess) return KernelResult.Fail(preparation.Error, preparation.Message!);
        if (preparation.Value!.VisibilityRequirement != exact.VisibilityRequirement ||
            preparation.Value.PublicationPolicy != exact.PublicationPolicy ||
            preparation.Value.EffectPolicy != exact.EffectPolicy ||
            preparation.Value.RegionUses.Count != exact.RegionUses.Count)
            return KernelResult.Fail(KernelError.StaleGeneration, "Operation preparation no longer matches obligations.");

        if (exact.Session is { } session)
        {
            KernelResult<EndpointSessionRegistry.Record> currentSession = EndpointSessions.Resolve(session, exact.Principal);
            if (!currentSession.IsSuccess) return KernelResult.Fail(currentSession.Error, currentSession.Message!);
        }

        for (int index = 0; index < exact.RegionUses.Count; index++)
        {
            OperationRegionObligationV1 expected = exact.RegionUses[index];
            OperationRegionUseRequest requested = preparation.Value.RegionUses[index];
            if (requested.Region != expected.Region || requested.Mode != expected.Mode || requested.Range != expected.Range)
                return KernelResult.Fail(KernelError.StaleGeneration, "Region-use preparation changed after obligations were captured.");
            KernelResult<RegionDescriptor> current = Regions.Validate(expected.Region, owner);
            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
            if (current.Value!.MutationEpoch != expected.MutationEpoch)
                return KernelResult.Fail(KernelError.StaleGeneration, "Region mutation epoch changed after obligations were captured.");
            if (snapshot.Value.Admission is { } admission)
            {
                RegionUseDescriptor admitted = admission.RegionUses[index];
                if (admitted.Region != expected.Region || admitted.Mode != expected.Mode ||
                    admitted.Range != expected.Range || admitted.MutationEpoch != expected.MutationEpoch ||
                    admitted.State != RegionUseState.Active)
                    return KernelResult.Fail(KernelError.StaleGeneration,
                        "Admitted Region use no longer matches exact obligations.");
            }
        }

        return KernelResult.Ok();
    }
}
