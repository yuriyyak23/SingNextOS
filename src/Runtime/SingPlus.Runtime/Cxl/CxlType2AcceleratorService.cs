using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed record CxlType2Execution(
    ComputePlan Plan,
    ExternalOperationHandle Operation,
    OperationBinding Binding,
    CxlAcceleratorSubmission Submission,
    OperationDependencySnapshot Dependencies,
    CxlEndpointSnapshot Endpoint,
    CxlFabricBinding Fabric,
    PlatformDomainIdentity Subject,
    PlatformDeviceLease DeviceLease,
    CxlSecurityAuthority? SecurityAuthority = null,
    CxlSecurityPolicy? SecurityPolicy = null);

/// <summary>Composes Type-2 execution through the provider-neutral planner and lifecycle.</summary>
public sealed class CxlType2AcceleratorService : ICxlTeardownParticipant
{
    private readonly RuntimeKernel kernel;
    private readonly CxlAuthorityBridge authority;
    private readonly ICxlType2AcceleratorProvider provider;
    private readonly CxlFabricManagerAuthority fabricManager;
    private readonly Dictionary<ExternalOperationId, (ProcessHandle Principal, CxlType2Execution Execution)> _live = [];
    private readonly Dictionary<ExternalOperationId, ProcessHandle> _uncontained = [];

    public CxlType2AcceleratorService(RuntimeKernel kernel, CxlAuthorityBridge authority,
        ICxlType2AcceleratorProvider provider, CxlFabricManagerAuthority fabricManager)
    {
        this.kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        this.authority = authority ?? throw new ArgumentNullException(nameof(authority));
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        this.fabricManager = fabricManager ?? throw new ArgumentNullException(nameof(fabricManager));
        kernel.RegisterCxlTeardownParticipant(this);
    }
    public KernelResult<CxlType2Execution> Submit(
        ProcessHandle principal,
        ComputePlan plan,
        IReadOnlyList<ComputeProviderCandidate> currentCandidates,
        PlatformDomainIdentity subject,
        PlatformDeviceLease deviceLease,
        CxlEndpointSnapshot endpoint,
        CxlFabricBinding fabric,
        ulong platformGeneration,
        ExternalEffectPolicy? effectPolicy = null,
        CxlSecurityAuthority? securityAuthority = null,
        CxlSecurityPolicy? securityPolicy = null)
    {
        var planned = kernel.ValidateComputePlanBeforeSubmit(principal, plan, currentCandidates);
        if (!planned.IsSuccess) return KernelResult<CxlType2Execution>.Fail(planned.Error, planned.Message!);
        var device = authority.ValidateDeviceAuthority(subject, deviceLease, endpoint);
        if (!device.IsSuccess) return KernelResult<CxlType2Execution>.Fail(device.Error, device.Message!);
        var fabricAdmission = fabricManager.ValidateAdmission(fabric);
        if (!fabricAdmission.IsSuccess) return KernelResult<CxlType2Execution>.Fail(fabricAdmission.Error, fabricAdmission.Message!);
        if (plan.PublicationPath == ComputePublicationPath.DirectCoherent)
            return KernelResult<CxlType2Execution>.Fail(KernelError.PlatformUnsupported, "Direct coherent Type-2 output is future-gated until CPU alias exclusion and symmetric coherent binding release exist.");

        var dependencies = new OperationDependencySnapshot(platformGeneration, endpoint.DeviceGeneration.Value, fabric.Generation.Value);
        var policy = plan.PublicationPath == ComputePublicationPath.Staged ? ExternalPublicationPolicy.Staged : ExternalPublicationPolicy.DirectCoherent;
        var prepared = kernel.PrepareExternalOperation(principal, plan.RequiredRegionUses,
            ExternalVisibilityRequirement.PublicationFence, policy, effectPolicy);
        if (!prepared.IsSuccess) return KernelResult<CxlType2Execution>.Fail(prepared.Error, prepared.Message!);
        var admitted = kernel.AdmitExternalOperation(principal, prepared.Value!.Operation, dependencies,
            new ExternalServiceIdentity(plan.ProviderId.Value), ExternalCancellationSupport.BeforeSubmissionOnly);
        if (!admitted.IsSuccess)
        {
            AbortPreSubmit(principal, prepared.Value.Operation);
            return KernelResult<CxlType2Execution>.Fail(admitted.Error, admitted.Message!);
        }
        var preEffect = authority.RevalidateBeforeEffect(admitted.Value!.Principal, admitted.Value.RegionUses[0].Handle, endpoint, fabric);
        if (!preEffect.IsSuccess)
        {
            AbortPreSubmit(principal, prepared.Value.Operation);
            return KernelResult<CxlType2Execution>.Fail(preEffect.Error, preEffect.Message!);
        }
        if (plan.Intent.RequiresSecureEvidence)
        {
            if (securityAuthority is null || securityPolicy is null || !securityPolicy.RequiredForOperation)
            {
                AbortPreSubmit(principal, prepared.Value.Operation);
                return KernelResult<CxlType2Execution>.Fail(KernelError.PlatformDenied, "Secure-required Type-2 admission requires a generation-bound security policy and authority.");
            }
            var readiness = securityAuthority.Evaluate(admitted.Value.Principal, admitted.Value.RegionUses[0].Handle,
                subject, deviceLease, endpoint, fabric, securityPolicy);
            if (!readiness.IsSuccess || !readiness.Value!.Ready)
            {
                AbortPreSubmit(principal, prepared.Value.Operation);
                return KernelResult<CxlType2Execution>.Fail(readiness.Error, readiness.Message ?? "Secure-required Type-2 readiness failed.");
            }
        }
        var binding = kernel.RecordExternalOperationSubmission(principal, prepared.Value.Operation, dependencies);
        if (!binding.IsSuccess)
        {
            AbortPreSubmit(principal, prepared.Value.Operation);
            return KernelResult<CxlType2Execution>.Fail(binding.Error, binding.Message!);
        }
        var providerEffectStarted = false;
        var admittedEffect = fabricManager.ExecuteAdmission(fabric, () =>
        {
            providerEffectStarted = true;
            return SubmitProviderEffect(principal, plan, admitted.Value, binding.Value!, dependencies, endpoint, fabric, subject,
                deviceLease, securityAuthority, securityPolicy);
        });
        if (!providerEffectStarted)
            AbortNotAccepted(principal, binding.Value);
        return admittedEffect;
    }

    private KernelResult<CxlType2Execution> SubmitProviderEffect(
        ProcessHandle principal, ComputePlan plan, OperationAdmissionSnapshot admission,
        OperationBinding binding, OperationDependencySnapshot dependencies, CxlEndpointSnapshot endpoint,
        CxlFabricBinding fabric, PlatformDomainIdentity subject, PlatformDeviceLease deviceLease,
        CxlSecurityAuthority? securityAuthority, CxlSecurityPolicy? securityPolicy)
    {
        PlatformAuthorityResult<CxlAcceleratorSubmission> submitted;
        try
        {
            submitted = provider.Submit(new(plan.Intent.Operation, plan.PublicationPath, binding,
                admission.RegionUses, endpoint.EndpointId, endpoint.DeviceGeneration, new(fabric.BindingId, fabric.Generation)));
        }
        catch (Exception exception)
        {
            _uncontained[binding.Operation.OperationId] = principal;
            _ = fabricManager.TrackOperation(fabric, principal, binding.Operation,
                () => KernelResult.Fail(KernelError.ExternalEffectUncontained,
                    "Type-2 submission threw without a recovery token."));
            return KernelResult<CxlType2Execution>.Fail(KernelError.ExternalEffectUncontained,
                $"Type-2 submission threw after its effect boundary and remains quarantined: {exception.Message}");
        }
        if (!submitted.IsSuccess)
        {
            if (submitted.Status == PlatformAuthorityStatus.NotAccepted)
                AbortNotAccepted(principal, binding);
            else
            {
                _uncontained[binding.Operation.OperationId] = principal;
                _ = fabricManager.TrackOperation(fabric, principal, binding.Operation,
                    () => KernelResult.Fail(KernelError.ExternalEffectUncontained, "Submission acceptance remains ambiguous without a recovery token."));
            }
            return KernelResult<CxlType2Execution>.Fail(Map(submitted.Status), submitted.Message ?? "Type-2 submission failed.");
        }
        if (submitted.Value!.OperationBinding != binding || submitted.Value.EndpointId != endpoint.EndpointId ||
            submitted.Value.DeviceGeneration != endpoint.DeviceGeneration ||
            submitted.Value.FabricBinding != new CxlFabricBindingRef(fabric.BindingId, fabric.Generation))
        {
            var compensation = provider.Release(submitted.Value);
            if (compensation.IsSuccess)
                CloseSubmitted(principal, binding.Operation);
            else
            {
                var recovery = new CxlType2Execution(plan, binding.Operation, binding, submitted.Value,
                    dependencies, endpoint, fabric, subject, deviceLease, securityAuthority, securityPolicy);
                _live[binding.Operation.OperationId] = (principal, recovery);
                _ = fabricManager.TrackOperation(fabric, principal, binding.Operation,
                    () => CloseProviderForFabricDrain(principal, recovery));
            }
            return KernelResult<CxlType2Execution>.Fail(KernelError.PlatformFaulted, "Type-2 provider returned a malformed submission identity.");
        }
        var execution = new CxlType2Execution(plan, binding.Operation, binding,
            submitted.Value, dependencies, endpoint, fabric, subject, deviceLease,
            securityAuthority, securityPolicy);
        _live.Add(execution.Operation.OperationId, (principal, execution));
        var tracked = fabricManager.TrackOperation(fabric, principal, execution.Operation,
            () => CloseProviderForFabricDrain(principal, execution));
        if (!tracked.IsSuccess)
        {
            var cancellation = provider.Cancel(execution.Submission);
            if (cancellation.IsSuccess)
            {
                CloseSubmitted(principal, execution.Operation);
                _live.Remove(execution.Operation.OperationId);
            }
            return KernelResult<CxlType2Execution>.Fail(tracked.Error, tracked.Message!);
        }
        return KernelResult<CxlType2Execution>.Ok(execution);
    }

    public KernelResult<ExternalOperationSnapshot> CompleteVisiblePublish(
        ProcessHandle principal,
        CxlType2Execution execution,
        IReadOnlyList<ComputeProviderCandidate> currentCandidates,
        Action publicationAction)
    {
        var providerCandidate = currentCandidates.SingleOrDefault(candidate => candidate.ProviderId == execution.Plan.ProviderId);
        if (providerCandidate is null || !providerCandidate.Available || providerCandidate.Faulted)
            return FailBeforePublication(principal, execution, KernelError.PlatformUnavailable, "Type-2 provider disappeared before publication.");
        if (providerCandidate.Generation != execution.Plan.ProviderGeneration)
            return FailBeforePublication(principal, execution, KernelError.StaleGeneration, "Type-2 provider generation changed before publication.");
        var device = authority.ValidateDeviceAuthority(execution.Subject, execution.DeviceLease, execution.Endpoint);
        if (!device.IsSuccess) return FailBeforePublication(principal, execution, device.Error, device.Message!);
        var operation = kernel.QueryExternalOperation(principal, execution.Operation);
        if (!operation.IsSuccess || operation.Value!.Admission is null)
            return KernelResult<ExternalOperationSnapshot>.Fail(operation.Error, operation.Message ?? "Operation admission is unavailable.");
        var bindingValidation = authority.RevalidateBeforeEffect(operation.Value.Admission.Principal,
            operation.Value.Admission.RegionUses[0].Handle, execution.Endpoint, execution.Fabric);
        if (!bindingValidation.IsSuccess)
            return FailBeforePublication(principal, execution, bindingValidation.Error, bindingValidation.Message!);
        if (execution.Plan.Intent.RequiresSecureEvidence)
        {
            if (execution.SecurityAuthority is null || execution.SecurityPolicy is null)
                return FailBeforePublication(principal, execution, KernelError.PlatformDenied, "Secure readiness receipt is missing before publication.");
            var readiness = execution.SecurityAuthority.Evaluate(operation.Value.Admission.Principal,
                operation.Value.Admission.RegionUses[0].Handle, execution.Subject, execution.DeviceLease,
                execution.Endpoint, execution.Fabric, execution.SecurityPolicy);
            if (!readiness.IsSuccess || !readiness.Value!.Ready)
                return FailBeforePublication(principal, execution, readiness.Error, readiness.Message ?? "Secure readiness became stale before publication.");
        }

        var completion = provider.ObserveCompletion(execution.Submission);
        if (!completion.IsSuccess || completion.Value!.Submission != execution.Submission)
            return FailBeforePublication(principal, execution, Map(completion.Status), completion.Message ?? "Type-2 completion is stale.");
        var completed = kernel.RecordExternalOperationCompletion(principal,
            new(execution.Binding, completion.Value.Disposition));
        if (!completed.IsSuccess) return completed;
        if (completion.Value.Disposition != ExternalOperationCompletionDisposition.Completed)
            return CloseAfterProviderRelease(principal, execution, KernelError.PlatformFaulted,
                $"Type-2 provider completed with {completion.Value.Disposition}.");
        var visibility = provider.AcquireVisibility(execution.Submission, ExternalVisibilityRequirement.PublicationFence);
        if (!visibility.IsSuccess || visibility.Value!.Submission != execution.Submission)
            return FailBeforePublication(principal, execution, Map(visibility.Status), visibility.Message ?? "Type-2 visibility is stale.");
        var visible = kernel.RecordExternalOperationVisibility(principal,
            new(execution.Binding, visibility.Value.Requirement, visibility.Value.Satisfied));
        if (!visible.IsSuccess)
            return CloseAfterProviderRelease(principal, execution, visible.Error, visible.Message);
        var published = kernel.PublishExternalOperation(principal, execution.Operation, execution.Dependencies,
            new(execution.Plan.PublicationPath == ComputePublicationPath.Staged ? ExternalPublicationPolicy.Staged : ExternalPublicationPolicy.DirectCoherent),
            publicationAction);
        if (!published.IsSuccess)
            return CloseAfterProviderRelease(principal, execution, published.Error, published.Message);
        var release = provider.Release(execution.Submission);
        if (!release.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.PlatformFaulted, release.Message ?? "Type-2 release is ambiguous; operation remains quarantined.");
        return CloseLocalAfterProviderClosure(principal, execution);
    }

    private KernelResult<ExternalOperationSnapshot> FailBeforePublication(ProcessHandle principal, CxlType2Execution execution, KernelError error, string message)
    {
        var providerClosure = provider.Release(execution.Submission);
        if (!providerClosure.IsSuccess)
            return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.PlatformFaulted, $"{message} Provider release did not prove closure; operation remains quarantined.");
        var closed = CloseLocalAfterProviderClosure(principal, execution);
        if (!closed.IsSuccess)
            return KernelResult<ExternalOperationSnapshot>.Fail(closed.Error, closed.Message!);
        return KernelResult<ExternalOperationSnapshot>.Fail(error, message);
    }

    private KernelResult<ExternalOperationSnapshot> CloseAfterProviderRelease(
        ProcessHandle principal, CxlType2Execution execution, KernelError originalError, string? originalMessage)
    {
        var providerClosure = provider.Release(execution.Submission);
        if (!providerClosure.IsSuccess)
            return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.PlatformFaulted,
                $"{originalMessage ?? "Type-2 operation failed."} Provider release did not prove closure; operation remains quarantined.");
        var closed = CloseLocalAfterProviderClosure(principal, execution);
        return closed.IsSuccess
            ? KernelResult<ExternalOperationSnapshot>.Fail(originalError, originalMessage ?? "Type-2 operation failed closed.")
            : closed;
    }

    private KernelResult<ExternalOperationSnapshot> CloseLocalAfterProviderClosure(
        ProcessHandle principal, CxlType2Execution execution)
    {
        var current = kernel.QueryExternalOperation(principal, execution.Operation);
        if (!current.IsSuccess)
            return current;
        if (current.Value!.State == ExternalOperationState.Submitted)
        {
            var lost = kernel.RecordExternalOperationProviderLoss(principal, execution.Operation);
            if (!lost.IsSuccess) return lost;
        }
        else if (current.Value.State is ExternalOperationState.DeviceComplete or ExternalOperationState.Visible &&
                 current.Value.Disposition == ExternalOperationDisposition.Completed)
        {
            var cancelled = kernel.CancelExternalOperation(principal, execution.Operation, false);
            if (!cancelled.IsSuccess) return cancelled;
        }
        var closed = kernel.ReleaseExternalOperation(principal, execution.Operation, new(true, false));
        if (closed.IsSuccess)
        {
            _live.Remove(execution.Operation.OperationId);
            _ = fabricManager.UntrackOperation(execution.Fabric, principal, execution.Operation);
        }
        return closed;
    }

    private void AbortPreSubmit(ProcessHandle principal, ExternalOperationHandle operation)
    {
        _ = kernel.CancelExternalOperation(principal, operation, false);
        _ = kernel.ReleaseExternalOperation(principal, operation, new(true, false));
    }

    private void AbortNotAccepted(ProcessHandle principal, OperationBinding binding)
    {
        _ = kernel.RecordExternalOperationCompletion(principal, new(binding, ExternalOperationCompletionDisposition.Cancelled));
        _ = kernel.ReleaseExternalOperation(principal, binding.Operation, new(true, false));
    }

    private void CloseSubmitted(ProcessHandle principal, ExternalOperationHandle operation)
    {
        _ = kernel.RecordExternalOperationProviderLoss(principal, operation);
        _ = kernel.ReleaseExternalOperation(principal, operation, new(true, false));
    }

    private KernelResult CloseProviderForFabricDrain(ProcessHandle principal, CxlType2Execution execution)
    {
        var cancellation = provider.Cancel(execution.Submission);
        if (!cancellation.IsSuccess)
            return KernelResult.Fail(Map(cancellation.Status), cancellation.Message ?? "Fabric drain could not close Type-2 provider work.");
        var closed = CloseLocalAfterProviderClosure(principal, execution);
        return closed.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(closed.Error, closed.Message!);
    }

    KernelResult ICxlTeardownParticipant.CloseForProcess(ProcessHandle process, RegionOwner owner)
    {
        if (_uncontained.Values.Any(principal => principal == process))
            return KernelResult.Fail(KernelError.PlatformFaulted, "Type-2 submission acceptance is ambiguous and has no recovery token; reclaim remains quarantined.");
        foreach (var live in _live.Values.Where(item => item.Principal == process).ToArray())
        {
            var cancelled = provider.Cancel(live.Execution.Submission);
            if (!cancelled.IsSuccess)
                return KernelResult.Fail(Map(cancelled.Status), cancelled.Message ?? "Type-2 provider closure is ambiguous; reclaim is quarantined.");
            var closed = CloseLocalAfterProviderClosure(process, live.Execution);
            if (!closed.IsSuccess) return KernelResult.Fail(closed.Error, closed.Message!);
        }
        return KernelResult.Ok();
    }

    private static KernelError Map(PlatformAuthorityStatus status) => status switch
    {
        PlatformAuthorityStatus.NotAccepted => KernelError.PlatformUnavailable,
        PlatformAuthorityStatus.Unavailable => KernelError.PlatformUnavailable,
        PlatformAuthorityStatus.Unsupported => KernelError.PlatformUnsupported,
        PlatformAuthorityStatus.Stale => KernelError.StaleGeneration,
        PlatformAuthorityStatus.Revoked => KernelError.PlatformBindingRevoked,
        PlatformAuthorityStatus.Denied => KernelError.PlatformDenied,
        _ => KernelError.PlatformFaulted
    };
}
