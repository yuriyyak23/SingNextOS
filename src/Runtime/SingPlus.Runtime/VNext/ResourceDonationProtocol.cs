using SingPlus.Contracts;
using SingPlus.Sip.Sdk;

namespace SingPlus.Runtime;

internal enum ResourceDonationState
{
    Bound = 0,
    Active,
    Returned,
    Quarantined,
    Closed,
}

internal sealed record ResourceDonationBinding(
    EndpointSessionInvocationHandle Invocation,
    ProcessHandle Caller,
    ProcessHandle Service,
    CapabilityId SourceGrant,
    CapabilityId DerivedGrant,
    ProcessHandle ChargingOwner,
    BudgetReservationHandle Lease,
    ResourceEnvelopeV1 Envelope,
    ResourceAssuranceV1 AssuranceCeiling,
    AdmissionQosHint PriorityCeiling,
    string Provenance,
    ResourceDonationState State = ResourceDonationState.Bound);

public sealed partial class RuntimeKernel
{
    internal GeneratedSipResourceAdmission EnterDonatedSipResourceAdmission(
        ProcessHandle service,
        EndpointSessionInvocationHandle invocation,
        SipResourceAdmissionBinding effectBinding,
        SipResourceRequirementV1 requirement)
    {
        var donation = SessionInvocations.ResolveResourceDonation(invocation, service);
        if (!donation.IsSuccess)
            return GeneratedSipResourceAdmission.Failure((int)donation.Error, donation.Message!);
        ResourceEnvelopeV1 requested;
        try
        {
            requested = new(ResourceEnvelopeV1.CurrentVersion, ResourceDimensionFamilyV1.Time,
                requirement.ResourceClass, requirement.Unit, requirement.MaximumAmount, 0,
                requirement.SemanticScope);
            requested = requested.Canonicalize();
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
        {
            return GeneratedSipResourceAdmission.Failure((int)KernelError.InvalidMessage, exception.Message);
        }
        if (requirement.DonationPolicy != SipResourceDonationPolicyV1.AcceptNarrowed ||
            requested != donation.Value!.Envelope ||
            !string.Equals(effectBinding.SemanticScope, requirement.SemanticScope, StringComparison.Ordinal))
            return GeneratedSipResourceAdmission.Failure((int)KernelError.DelegationDenied,
                "Generated sentry requirement does not exactly match the invocation donation.");

        var admitted = PrepareResourceExternalAdmission(service,
            effectBinding.EffectCapability, effectBinding.EffectResourceKind, effectBinding.EffectResourceId,
            effectBinding.EffectResourceGeneration, donation.Value.DerivedGrant, 1, requested,
            effectBinding.Operation, effectBinding.Dependencies, donation.Value.ChargingOwner, donation.Value.Lease);
        return admitted.IsSuccess
            ? GeneratedSipResourceAdmission.Success(admitted.Value!, providerSubmit =>
            {
                var submitted = SubmitResourceExternalAdmission(admitted.Value!, effectBinding.Dependencies, () =>
                {
                    var active = SessionInvocations.ActivateResourceDonation(invocation, service);
                    if (!active.IsSuccess)
                        return KernelResult.Fail(active.Error, active.Message!);
                    var result = providerSubmit();
                    var providerError = (KernelError)result.ErrorCode;
                    return result.IsSuccess
                        ? KernelResult.Ok()
                        : KernelResult.Fail(Enum.IsDefined(providerError) && providerError != KernelError.None
                                ? providerError
                                : KernelError.PlatformFaulted,
                            result.Message ?? "Provider submission failed.");
                });
                return submitted.IsSuccess
                    ? GeneratedSipSubmitResult.Ok()
                    : GeneratedSipSubmitResult.Failure((int)submitted.Error, submitted.Message!);
            })
            : GeneratedSipResourceAdmission.Failure((int)admitted.Error, admitted.Message!);
    }

    internal KernelResult<ResourceDonationBinding> BindResourceDonation(
        ProcessHandle caller,
        ProcessHandle service,
        EndpointSessionInvocationHandle invocation,
        CapabilityId sourceGrant,
        ulong sourceGrantGeneration,
        SipResourceRequirementV1 requirement,
        ResourceEnvelopeV1 donatedEnvelope,
        AdmissionQosHint priorityCeiling)
    {
        if (requirement.DonationPolicy != SipResourceDonationPolicyV1.AcceptNarrowed)
            return KernelResult<ResourceDonationBinding>.Fail(KernelError.DelegationDenied,
                "The generated SIP contract does not accept resource donation.");
        var session = ResolveSession(caller, invocation.Session);
        if (!session.IsSuccess)
            return KernelResult<ResourceDonationBinding>.Fail(session.Error, session.Message!);
        if (session.Value!.Service != service)
            return KernelResult<ResourceDonationBinding>.Fail(KernelError.WrongSessionOwner,
                "Donation target is not the exact invocation service generation.");
        var callerProcess = Processes.Resolve(caller);
        if (!callerProcess.IsSuccess)
            return KernelResult<ResourceDonationBinding>.Fail(callerProcess.Error, callerProcess.Message!);
        var serviceProcess = Processes.Resolve(service);
        if (!serviceProcess.IsSuccess)
            return KernelResult<ResourceDonationBinding>.Fail(serviceProcess.Error, serviceProcess.Message!);

        ResourceEnvelopeV1 envelope;
        try { envelope = donatedEnvelope.Canonicalize(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
        { return KernelResult<ResourceDonationBinding>.Fail(KernelError.InvalidMessage, exception.Message); }
        if (envelope.ResourceClass != requirement.ResourceClass || envelope.Unit != requirement.Unit ||
            envelope.Amount > requirement.MaximumAmount ||
            !string.Equals(envelope.SemanticScope, requirement.SemanticScope, StringComparison.Ordinal))
            return KernelResult<ResourceDonationBinding>.Fail(KernelError.DelegationDenied,
                "Donation exceeds or changes the generated resource requirement.");
        if (!Enum.IsDefined(priorityCeiling))
            return KernelResult<ResourceDonationBinding>.Fail(KernelError.InvalidMessage, "Donation priority ceiling is unknown.");

        var source = CapabilityAuthority.ValidateResourceUse(sourceGrant, callerProcess.Value!.DomainId,
            caller.Generation, sourceGrantGeneration, envelope);
        if (!source.IsSuccess)
            return KernelResult<ResourceDonationBinding>.Fail(source.Error, source.Message!);
        if (source.Value!.AssuranceCeiling > requirement.AssuranceCeiling)
            return KernelResult<ResourceDonationBinding>.Fail(KernelError.DelegationDenied,
                "Donation assurance exceeds the generated contract ceiling.");

        var delegated = CapabilityAuthority.Delegate(sourceGrant, callerProcess.Value.DomainId,
            serviceProcess.Value!.DomainId, CapabilityRights.Delegate, service.Generation, constraints => constraints with
            {
                ResourceUse = source.Value with
                {
                    Envelope = envelope,
                    AssuranceCeiling = requirement.AssuranceCeiling,
                    DelegationDepth = checked((ushort)(source.Value.DelegationDepth - 1))
                }
            });
        if (!delegated.IsSuccess)
            return KernelResult<ResourceDonationBinding>.Fail(delegated.Error, delegated.Message!);

        var reserved = Budgets.Reserve(caller,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, envelope.Amount)],
            BudgetReservationLifetime.IpcQueued, priorityCeiling);
        if (!reserved.IsSuccess)
        {
            _ = CapabilityAuthority.Revoke(delegated.Value!.CapabilityId);
            return KernelResult<ResourceDonationBinding>.Fail(reserved.Error, reserved.Message!);
        }

        var binding = new ResourceDonationBinding(invocation, caller, service, sourceGrant,
            delegated.Value!.CapabilityId, caller, reserved.Value!.Reservation, envelope,
            requirement.AssuranceCeiling, priorityCeiling,
            $"{caller.ProcessId.Value}:{caller.Generation}->{service.ProcessId.Value}:{service.Generation}@{invocation.Session.SessionId.Value}:{invocation.Session.Generation.Value}:{invocation.InvocationId.Value}:{invocation.Generation.Value}");
        var bound = SessionInvocations.BindResourceDonation(binding);
        if (bound.IsSuccess) return bound;
        _ = Budgets.CancelLeasePreSubmit(caller, binding.Lease);
        _ = CapabilityAuthority.Revoke(binding.DerivedGrant);
        return KernelResult<ResourceDonationBinding>.Fail(bound.Error, bound.Message!);
    }

    internal KernelResult<ResourceDonationBinding> DeriveNestedResourceDonation(
        ProcessHandle server,
        ProcessHandle downstream,
        EndpointSessionInvocationHandle parentInvocation,
        EndpointSessionInvocationHandle downstreamInvocation,
        ResourceEnvelopeV1 childEnvelope,
        ResourceAssuranceV1 assuranceCeiling,
        AdmissionQosHint priorityCeiling)
    {
        var parent = SessionInvocations.ResolveResourceDonation(parentInvocation, server);
        if (!parent.IsSuccess) return KernelResult<ResourceDonationBinding>.Fail(parent.Error, parent.Message!);
        var source = parent.Value!;
        ResourceEnvelopeV1 envelope;
        try { envelope = childEnvelope.Canonicalize(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
        { return KernelResult<ResourceDonationBinding>.Fail(KernelError.InvalidMessage, exception.Message); }
        if (!ResourceEnvelopeV1.IsSubset(envelope, source.Envelope) || assuranceCeiling > source.AssuranceCeiling ||
            PriorityRank(priorityCeiling) > PriorityRank(source.PriorityCeiling))
            return KernelResult<ResourceDonationBinding>.Fail(KernelError.DelegationDenied,
                "Nested donation may only narrow amount, assurance, priority and semantic scope.");
        var session = ResolveSession(server, downstreamInvocation.Session);
        if (!session.IsSuccess || session.Value!.Service != downstream)
            return KernelResult<ResourceDonationBinding>.Fail(session.Error == KernelError.None ? KernelError.WrongSessionOwner : session.Error,
                session.Message ?? "Downstream invocation does not match the exact service generation.");
        var serverProcess = Processes.Resolve(server);
        var downstreamProcess = Processes.Resolve(downstream);
        if (!serverProcess.IsSuccess || !downstreamProcess.IsSuccess)
            return KernelResult<ResourceDonationBinding>.Fail(KernelError.StaleGeneration, "Nested donation process generation is stale.");

        var delegated = CapabilityAuthority.Delegate(source.DerivedGrant, serverProcess.Value!.DomainId,
            downstreamProcess.Value!.DomainId, CapabilityRights.Delegate, downstream.Generation, constraints => constraints with
            {
                ResourceUse = constraints.ResourceUse!.Value with
                {
                    Envelope = envelope,
                    AssuranceCeiling = assuranceCeiling,
                    DelegationDepth = checked((ushort)(constraints.ResourceUse.Value.DelegationDepth - 1))
                }
            });
        if (!delegated.IsSuccess)
            return KernelResult<ResourceDonationBinding>.Fail(delegated.Error, delegated.Message!);
        var split = Budgets.SplitLease(source.ChargingOwner, source.Lease,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, envelope.Amount)]);
        if (!split.IsSuccess)
        {
            _ = CapabilityAuthority.Revoke(delegated.Value!.CapabilityId);
            return KernelResult<ResourceDonationBinding>.Fail(split.Error, split.Message!);
        }
        var binding = new ResourceDonationBinding(downstreamInvocation, server, downstream,
            source.DerivedGrant, delegated.Value!.CapabilityId, source.ChargingOwner, split.Value!.Reservation,
            envelope, assuranceCeiling, priorityCeiling, source.Provenance + "/" + downstream.ProcessId.Value);
        var bound = SessionInvocations.BindResourceDonation(binding);
        if (bound.IsSuccess) return bound;
        _ = Budgets.CancelLeasePreSubmit(source.ChargingOwner, binding.Lease);
        _ = CapabilityAuthority.Revoke(binding.DerivedGrant);
        return KernelResult<ResourceDonationBinding>.Fail(bound.Error, bound.Message!);
    }

    internal KernelResult CloseResourceDonation(ProcessHandle service, EndpointSessionInvocationHandle invocation, bool submitMayHaveOccurred)
    {
        var closed = SessionInvocations.CloseResourceDonation(invocation, service,
            submitMayHaveOccurred ? ResourceDonationState.Quarantined : ResourceDonationState.Returned);
        if (!closed.IsSuccess) return KernelResult.Fail(closed.Error, closed.Message!);
        var binding = closed.Value!;
        _ = CapabilityAuthority.Revoke(binding.DerivedGrant);
        var budget = submitMayHaveOccurred
            ? Budgets.QuarantineLease(binding.ChargingOwner, binding.Lease)
            : Budgets.CancelLeasePreSubmit(binding.ChargingOwner, binding.Lease);
        return budget.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(budget.Error, budget.Message!);
    }

    internal KernelResult MarkResourceDonationPossibleSubmit(
        ProcessHandle service, EndpointSessionInvocationHandle invocation)
    {
        var donation = SessionInvocations.ResolveResourceDonation(invocation, service);
        if (!donation.IsSuccess) return KernelResult.Fail(donation.Error, donation.Message!);
        var bound = Budgets.BindLease(donation.Value!.ChargingOwner, donation.Value.Lease);
        if (!bound.IsSuccess) return KernelResult.Fail(bound.Error, bound.Message!);
        var consuming = Budgets.BeginConsumption(donation.Value.ChargingOwner, donation.Value.Lease);
        if (!consuming.IsSuccess)
        {
            _ = Budgets.QuarantineLease(donation.Value.ChargingOwner, donation.Value.Lease);
            return KernelResult.Fail(consuming.Error, consuming.Message!);
        }
        var active = SessionInvocations.ActivateResourceDonation(invocation, service);
        if (active.IsSuccess) return KernelResult.Ok();
        _ = Budgets.QuarantineLease(donation.Value.ChargingOwner, donation.Value.Lease);
        return KernelResult.Fail(active.Error, active.Message!);
    }

    private void CloseSessionResourceDonations(EndpointSessionHandle session)
    {
        foreach (var donation in SessionInvocations.CloseSession(session))
        {
            _ = CapabilityAuthority.Revoke(donation.DerivedGrant);
            if (donation.State == ResourceDonationState.Active)
                _ = Budgets.QuarantineLease(donation.ChargingOwner, donation.Lease);
            else
                _ = Budgets.CancelLeasePreSubmit(donation.ChargingOwner, donation.Lease);
        }
    }

    private void CloseResourceDonationForInvocationTerminal(
        ProcessHandle service, EndpointSessionInvocationHandle invocation)
    {
        var donation = SessionInvocations.ResolveResourceDonation(invocation, service);
        if (!donation.IsSuccess) return;
        _ = CloseResourceDonation(service, invocation,
            submitMayHaveOccurred: donation.Value!.State == ResourceDonationState.Active);
    }

    private static int PriorityRank(AdmissionQosHint value) => value switch
    {
        AdmissionQosHint.None => 0,
        AdmissionQosHint.Background => 1,
        AdmissionQosHint.ThroughputOriented => 2,
        AdmissionQosHint.BoundedInteractive => 3,
        AdmissionQosHint.LatencySensitive => 4,
        _ => int.MaxValue,
    };
}
