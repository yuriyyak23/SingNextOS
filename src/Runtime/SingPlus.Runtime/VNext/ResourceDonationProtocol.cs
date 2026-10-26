using SingPlus.Contracts;
using SingPlus.Sip;
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
    ResourceDonationState State = ResourceDonationState.Bound)
{
    // Immutable source correlation, never permission. Zero remains unknown/deny.
    internal ulong ResourceGrantGeneration { get; init; }
}

public sealed partial class RuntimeKernel
{
    internal GeneratedSipResourceAdmission EnterDonatedSipResourceAdmission(
        ProcessHandle service,
        EndpointSessionInvocationHandle invocation,
        SipResourceAdmissionBinding effectBinding,
        SipResourceRequirementV1 requirement)
    {
        KernelResult<ResourceDonationBinding> donation = SessionInvocations.ResolveResourceDonation(invocation, service, requireAdmission: true);
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

        KernelResult<ResourceAdmissionCommit> admitted = PrepareResourceExternalAdmission(service,
            effectBinding.EffectCapability, effectBinding.EffectResourceKind, effectBinding.EffectResourceId,
            effectBinding.EffectResourceGeneration, donation.Value.DerivedGrant, donation.Value.ResourceGrantGeneration, requested,
            effectBinding.Operation, effectBinding.Dependencies, donation.Value.ChargingOwner, donation.Value.Lease);
        return admitted.IsSuccess
            ? GeneratedSipResourceAdmission.Success(admitted.Value!, providerSubmit =>
            {
                KernelResult<OperationBinding> submitted = SubmitResourceExternalAdmission(admitted.Value!, effectBinding.Dependencies, () =>
                {
                    KernelResult permission = RevalidateResourceAdmissionCommit(admitted.Value!, afterSubmit: true);
                    if (!permission.IsSuccess) return permission;
                    KernelResult<ResourceDonationBinding> active = SessionInvocations.ActivateResourceDonation(invocation, service);
                    if (!active.IsSuccess)
                        return KernelResult.Fail(active.Error, active.Message!);
                    GeneratedSipSubmitResult result = providerSubmit();
                    KernelError providerError = (KernelError)result.ErrorCode;
                    return result.IsSuccess
                        ? KernelResult.Ok()
                        : KernelResult.Fail(Enum.IsDefined(providerError) && providerError != KernelError.None
                                ? providerError
                                : KernelError.PlatformFaulted,
                            result.Message ?? "Provider submission failed.");
                }, () => RevalidateResourceAdmissionCommit(admitted.Value!));
                return submitted.IsSuccess
                    ? GeneratedSipSubmitResult.Ok()
                    : GeneratedSipSubmitResult.Failure((int)submitted.Error, submitted.Message!);
            })
            : GeneratedSipResourceAdmission.Failure((int)admitted.Error, admitted.Message!);
    }

    internal Action<ResourceDonationBinding>? BeforeDonationPublicationQualificationHook { get; set; }

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
        Action release;
        KernelResult pending;
        lock (_requestResponseCorrelationGate)
            pending = SessionInvocations.BeginResourceDonationBinding(invocation, caller, service, out release);
        if (!pending.IsSuccess) return KernelResult<ResourceDonationBinding>.Fail(pending.Error, pending.Message!);
        try
        {
        if (requirement.DonationPolicy != SipResourceDonationPolicyV1.AcceptNarrowed)
            return KernelResult<ResourceDonationBinding>.Fail(KernelError.DelegationDenied,
                "The generated SIP contract does not accept resource donation.");
            KernelResult<EndpointSessionRegistry.Record> session = ResolveSession(caller, invocation.Session);
        if (!session.IsSuccess)
            return KernelResult<ResourceDonationBinding>.Fail(session.Error, session.Message!);
        if (session.Value!.Service != service)
            return KernelResult<ResourceDonationBinding>.Fail(KernelError.WrongSessionOwner,
                "Donation target is not the exact invocation service generation.");
            KernelResult<SingProcess> callerProcess = Processes.Resolve(caller);
        if (!callerProcess.IsSuccess)
            return KernelResult<ResourceDonationBinding>.Fail(callerProcess.Error, callerProcess.Message!);
            KernelResult<SingProcess> serviceProcess = Processes.Resolve(service);
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

            KernelResult<ResourceUseConstraintV1> source = CapabilityAuthority.ValidateResourceUse(sourceGrant, callerProcess.Value!.DomainId,
            caller.Generation, sourceGrantGeneration, envelope);
        if (!source.IsSuccess)
            return KernelResult<ResourceDonationBinding>.Fail(source.Error, source.Message!);
        if (source.Value!.AssuranceCeiling > requirement.AssuranceCeiling)
            return KernelResult<ResourceDonationBinding>.Fail(KernelError.DelegationDenied,
                "Donation assurance exceeds the generated contract ceiling.");
            KernelResult<CapabilityDescriptorV1> delegated = CapabilityAuthority.Delegate(sourceGrant, callerProcess.Value.DomainId,
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

            KernelResult<IReadOnlyList<BudgetAmount>> budgetAmounts = ResourceEnvelopeBudgetMapping.Map([envelope]);
        if (!budgetAmounts.IsSuccess)
        {
            _ = CapabilityAuthority.Revoke(delegated.Value!.CapabilityId);
            return KernelResult<ResourceDonationBinding>.Fail(budgetAmounts.Error, budgetAmounts.Message!);
        }
            KernelResult<BudgetReservationSnapshot> reserved = Budgets.Reserve(caller, budgetAmounts.Value!,
            BudgetReservationLifetime.IpcQueued, priorityCeiling);
        if (!reserved.IsSuccess)
        {
            _ = CapabilityAuthority.Revoke(delegated.Value!.CapabilityId);
            return KernelResult<ResourceDonationBinding>.Fail(reserved.Error, reserved.Message!);
        }

            ResourceDonationBinding binding = new ResourceDonationBinding(invocation, caller, service, sourceGrant,
            delegated.Value!.CapabilityId, caller, reserved.Value!.Reservation, envelope,
            requirement.AssuranceCeiling, priorityCeiling,
            $"{caller.ProcessId.Value}:{caller.Generation}->{service.ProcessId.Value}:{service.Generation}@{invocation.Session.SessionId.Value}:{invocation.Session.Generation.Value}:{invocation.InvocationId.Value}:{invocation.Generation.Value}")
            { ResourceGrantGeneration = sourceGrantGeneration };
            bool published = false;
        try
        {
            BeforeDonationPublicationQualificationHook?.Invoke(binding);
                DateTimeOffset sessionTime = EndpointSessions.ReadDonationPublicationTime();
                KernelResult commit = CapabilityAuthority.CommitResourceDonationPublication(sourceGrant, callerProcess.Value.DomainId,
                caller.Generation, sourceGrantGeneration, binding.DerivedGrant, serviceProcess.Value.DomainId,
                service.Generation, sourceGrantGeneration, envelope, ownerCommit =>
                {
                    lock (_platformMemoryUseGate)
                    {
                        KernelResult<SingProcess> currentCaller = Processes.Resolve(caller);
                        KernelResult<SingProcess> currentService = Processes.Resolve(service);
                        if (!currentCaller.IsSuccess || !currentService.IsSuccess ||
                            !ReferenceEquals(currentCaller.Value, callerProcess.Value) ||
                            !ReferenceEquals(currentService.Value, serviceProcess.Value))
                            return KernelResult.Fail(KernelError.StaleGeneration, "Donation process incarnation changed.");
                        KernelResult callerEffect = EnsureProcessAcceptsNewEffects(currentCaller.Value!);
                        if (!callerEffect.IsSuccess) return callerEffect;
                        KernelResult serviceEffect = EnsureProcessAcceptsNewEffects(currentService.Value!);
                        return serviceEffect.IsSuccess ? ownerCommit() : serviceEffect;
                    }
                }, () => EndpointSessions.CommitDonationPublication(session.Value!, sessionTime,
                    () => Budgets.CommitReservedDonation(caller, binding.Lease, () =>
                {
                    KernelResult<ResourceDonationBinding> bound = SessionInvocations.BindResourceDonation(binding);
                    return bound.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(bound.Error, bound.Message!);
                })));
            if (!commit.IsSuccess) return KernelResult<ResourceDonationBinding>.Fail(commit.Error, commit.Message!);
            published = true;
            return KernelResult<ResourceDonationBinding>.Ok(binding);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        { return KernelResult<ResourceDonationBinding>.Fail(KernelError.PlatformFaulted, $"Donation publication interrupted: {exception.Message}"); }
        finally
        {
            if (!published)
            {
                _ = Budgets.CancelLeasePreSubmit(caller, binding.Lease);
                _ = CapabilityAuthority.Revoke(binding.DerivedGrant);
            }
        }
        }
        finally { release(); }
    }

    internal Action<CapabilityId>? BeforeNestedDonationPublicationQualificationHook { get; set; }

    internal KernelResult<ResourceDonationBinding> DeriveNestedResourceDonation(
        ProcessHandle server,
        ProcessHandle downstream,
        EndpointSessionInvocationHandle parentInvocation,
        EndpointSessionInvocationHandle downstreamInvocation,
        ResourceEnvelopeV1 childEnvelope,
        ResourceAssuranceV1 assuranceCeiling,
        AdmissionQosHint priorityCeiling)
    {
        Action release;
        KernelResult pending;
        KernelResult<ResourceDonationBinding> parent = SessionInvocations.ResolveResourceDonation(parentInvocation, server, requireAdmission: true);
        if (!parent.IsSuccess) return KernelResult<ResourceDonationBinding>.Fail(parent.Error, parent.Message!);
        ResourceDonationBinding source = parent.Value!;
        ResourceEnvelopeV1 envelope;
        try { envelope = childEnvelope.Canonicalize(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
        { return KernelResult<ResourceDonationBinding>.Fail(KernelError.InvalidMessage, exception.Message); }
        if (!ResourceEnvelopeV1.IsSubset(envelope, source.Envelope) || assuranceCeiling > source.AssuranceCeiling ||
            PriorityRank(priorityCeiling) > PriorityRank(source.PriorityCeiling))
            return KernelResult<ResourceDonationBinding>.Fail(KernelError.DelegationDenied,
                "Nested donation may only narrow amount, assurance, priority and semantic scope.");
        lock (_requestResponseCorrelationGate)
            pending = SessionInvocations.BeginResourceDonationBinding(downstreamInvocation, server, downstream, out release);
        if (!pending.IsSuccess) return KernelResult<ResourceDonationBinding>.Fail(pending.Error, pending.Message!);
        try
        {
            KernelResult<EndpointSessionRegistry.Record> session = ResolveSession(server, downstreamInvocation.Session);
        if (!session.IsSuccess || session.Value!.Service != downstream)
            return KernelResult<ResourceDonationBinding>.Fail(session.Error == KernelError.None ? KernelError.WrongSessionOwner : session.Error,
                session.Message ?? "Downstream invocation does not match the exact service generation.");
            KernelResult<SingProcess> serverProcess = Processes.Resolve(server);
            KernelResult<SingProcess> downstreamProcess = Processes.Resolve(downstream);
        if (!serverProcess.IsSuccess || !downstreamProcess.IsSuccess)
            return KernelResult<ResourceDonationBinding>.Fail(KernelError.StaleGeneration, "Nested donation process generation is stale.");

            KernelResult<CapabilityHandleV2> sourceHandle = CapabilityAuthority.GetHandleV2(source.DerivedGrant, serverProcess.Value!.DomainId, server.Generation);
        if (!sourceHandle.IsSuccess) return KernelResult<ResourceDonationBinding>.Fail(sourceHandle.Error, sourceHandle.Message!);
            KernelResult<CapabilityInspectionDescriptorV2> sourceIdentity = CapabilityAuthority.Validate(sourceHandle.Value!, serverProcess.Value.DomainId,
            server.Generation, CapabilityRights.Delegate);
        if (!sourceIdentity.IsSuccess) return KernelResult<ResourceDonationBinding>.Fail(sourceIdentity.Error, sourceIdentity.Message!);
            ulong resourceGeneration = sourceIdentity.Value!.ResourceGeneration;
        if (resourceGeneration != source.ResourceGrantGeneration)
            return KernelResult<ResourceDonationBinding>.Fail(KernelError.StaleGeneration, "Donation source resource generation does not match its original binding.");
            KernelResult<CapabilityDescriptorV1> delegated = CapabilityAuthority.Delegate(source.DerivedGrant, serverProcess.Value!.DomainId,
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
            KernelResult<IReadOnlyList<BudgetAmount>> budgetAmounts = ResourceEnvelopeBudgetMapping.Map([envelope]);
        if (!budgetAmounts.IsSuccess)
        {
            _ = CapabilityAuthority.Revoke(delegated.Value!.CapabilityId);
            return KernelResult<ResourceDonationBinding>.Fail(budgetAmounts.Error, budgetAmounts.Message!);
        }
        ResourceDonationBinding? published = null;
        try
        {
            BeforeNestedDonationPublicationQualificationHook?.Invoke(delegated.Value!.CapabilityId);
                DateTimeOffset sessionTime = EndpointSessions.ReadDonationPublicationTime();
                KernelResult commit = CapabilityAuthority.CommitResourceDonationPublication(source.DerivedGrant,
                serverProcess.Value.DomainId, server.Generation, resourceGeneration, delegated.Value!.CapabilityId,
                downstreamProcess.Value!.DomainId, downstream.Generation, resourceGeneration, envelope, ownerCommit =>
                {
                    lock (_platformMemoryUseGate)
                    {
                        KernelResult<SingProcess> currentServer = Processes.Resolve(server);
                        KernelResult<SingProcess> currentDownstream = Processes.Resolve(downstream);
                        if (!currentServer.IsSuccess || !currentDownstream.IsSuccess ||
                            !ReferenceEquals(currentServer.Value, serverProcess.Value) ||
                            !ReferenceEquals(currentDownstream.Value, downstreamProcess.Value))
                            return KernelResult.Fail(KernelError.StaleGeneration, "Nested donation process incarnation changed.");
                        KernelResult serverEffect = EnsureProcessAcceptsNewEffects(currentServer.Value!);
                        if (!serverEffect.IsSuccess) return serverEffect;
                        KernelResult downstreamEffect = EnsureProcessAcceptsNewEffects(currentDownstream.Value!);
                        return downstreamEffect.IsSuccess ? ownerCommit() : downstreamEffect;
                    }
                }, () => EndpointSessions.CommitDonationPublication(session.Value!, sessionTime, () =>
                {
                    KernelResult<ResourceDonationBinding> result = Budgets.SplitDonationAndPublish(source.ChargingOwner, source.Lease, budgetAmounts.Value!, lease =>
                        SessionInvocations.BindNestedResourceDonation(source, new ResourceDonationBinding(
                            downstreamInvocation, server, downstream, source.DerivedGrant, delegated.Value!.CapabilityId,
                            source.ChargingOwner, lease, envelope, assuranceCeiling, priorityCeiling,
                            $"{source.Provenance}/{server.ProcessId.Value}:{server.Generation}->{downstream.ProcessId.Value}:{downstream.Generation}@{downstreamInvocation.Session.SessionId.Value}:{downstreamInvocation.Session.Generation.Value}:{downstreamInvocation.InvocationId.Value}:{downstreamInvocation.Generation.Value}")
                            { ResourceGrantGeneration = resourceGeneration }));
                    if (result.IsSuccess) published = result.Value!;
                    return result.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(result.Error, result.Message!);
                }));
            return commit.IsSuccess ? KernelResult<ResourceDonationBinding>.Ok(published!)
                : KernelResult<ResourceDonationBinding>.Fail(commit.Error, commit.Message!);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        { return KernelResult<ResourceDonationBinding>.Fail(KernelError.PlatformFaulted, $"Nested donation publication interrupted: {exception.Message}"); }
        finally { if (published is null) _ = CapabilityAuthority.Revoke(delegated.Value!.CapabilityId); }
        }
        finally { release(); }
    }

    internal KernelResult CloseResourceDonation(ProcessHandle service, EndpointSessionInvocationHandle invocation, bool submitMayHaveOccurred)
    {
        lock (_requestResponseCorrelationGate)
        {
            KernelResult<ResourceDonationBinding> closed = submitMayHaveOccurred
            ? SessionInvocations.CloseResourceDonation(invocation, service, ResourceDonationState.Quarantined)
            : SessionInvocations.ReturnResourceDonationPreSubmit(invocation, service, current =>
            {
                KernelResult<BudgetReservationSnapshot> cancelled = Budgets.CancelLeasePreSubmit(current.ChargingOwner, current.Lease);
                return cancelled.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(cancelled.Error, cancelled.Message!);
            });
        if (!closed.IsSuccess) return KernelResult.Fail(closed.Error, closed.Message!);
            ResourceDonationBinding binding = closed.Value!;
        _ = CapabilityAuthority.Revoke(binding.DerivedGrant);
        if (binding.State != ResourceDonationState.Quarantined) return KernelResult.Ok();
            KernelResult<BudgetReservationSnapshot> budget = Budgets.QuarantineLease(binding.ChargingOwner, binding.Lease);
        return budget.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(budget.Error, budget.Message!);
        }
    }

    internal KernelResult MarkResourceDonationPossibleSubmit(
        ProcessHandle service, EndpointSessionInvocationHandle invocation)
    {
        KernelResult<ResourceDonationBinding> donation = SessionInvocations.ResolveResourceDonation(invocation, service, requireAdmission: true);
        if (!donation.IsSuccess) return KernelResult.Fail(donation.Error, donation.Message!);
        KernelResult<SingProcess> process = Processes.Resolve(service);
        if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
        KernelResult<EndpointSessionRegistry.Record> session = EndpointSessions.ResolveForService(invocation.Session, service);
        if (!session.IsSuccess) return KernelResult.Fail(session.Error, session.Message!);
        try
        {
            ResourceDonationBinding exact = donation.Value!;
            DateTimeOffset sessionTime = default(DateTimeOffset);
            return CapabilityAuthority.CommitDonationConsumption(exact.DerivedGrant, process.Value!.DomainId,
                service.Generation, exact.ResourceGrantGeneration, exact.Envelope, ownerCommit =>
                {
                    sessionTime = EndpointSessions.ReadDonationPublicationTime();
                    lock (_platformMemoryUseGate)
                    {
                        KernelResult<SingProcess> current = Processes.Resolve(service);
                        if (!current.IsSuccess || !ReferenceEquals(current.Value, process.Value))
                            return KernelResult.Fail(KernelError.StaleGeneration, "Donation consumer incarnation changed.");
                        KernelResult accepts = EnsureProcessAcceptsNewEffects(current.Value!);
                        return accepts.IsSuccess ? ownerCommit() : accepts;
                    }
                }, () => EndpointSessions.CommitDonationPublication(session.Value!, sessionTime,
                    () => Budgets.CommitDonationConsumption(exact.ChargingOwner, exact.Lease, () =>
                    {
                        KernelResult<ResourceDonationBinding> active = SessionInvocations.ActivateResourceDonation(invocation, service, exact);
                        return active.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(active.Error, active.Message!);
                    })));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        { return KernelResult.Fail(KernelError.PlatformFaulted, $"Donation consumption interrupted: {exception.Message}"); }
    }

    private void CloseSessionResourceDonations(EndpointSessionHandle session)
    {
        foreach (ResourceDonationBinding donation in SessionInvocations.CloseSession(session))
        {
            _ = CapabilityAuthority.Revoke(donation.DerivedGrant);
            // A Bound donation may already have an external owner submit in flight.
            // Session loss cannot establish pre-submit closure across those owners.
            _ = Budgets.QuarantineLease(donation.ChargingOwner, donation.Lease);
        }
    }

    private void CloseResourceDonationForInvocationTerminal(
        ProcessHandle service, EndpointSessionInvocationHandle invocation)
    {
        KernelResult<ResourceDonationBinding> donation = SessionInvocations.ResolveResourceDonation(invocation, service);
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
