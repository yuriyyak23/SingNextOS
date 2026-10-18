using System.Collections.Concurrent;
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
public sealed class CxlType2AcceleratorService : ICxlTeardownParticipant, IComposedWorkTeardownParticipant
{
    private sealed class EffectRecord
    {
        public EffectRecord(ProcessHandle principal, OperationBinding binding, CxlAuthorityBridge.OperationPin pin)
        { OwnerSelf = this; Principal = principal; Binding = binding; Pin = pin; }
        public EffectRecord OwnerSelf { get; }
        public ProcessHandle Principal { get; }
        public OperationBinding Binding { get; }
        public CxlAuthorityBridge.OperationPin Pin { get; }
        public bool CallbackInFlight { get; set; } = true;
        // One admitted submit per existing effect record; correlation/lifecycle, never permission.
        public int SubmitAdmitted;
        public bool ClosureInFlight { get; set; }
        public bool ObservationInFlight { get; set; }
        // Adapter lifetime interlock; ExternalOperationAuthority owns publication facts.
        public bool PublicationInFlight { get; set; }
        public bool CancellationAcknowledged { get; set; }
        // Exact successful callback reply retained before later source observation.
        // It prevents duplicate release; it does not authorize local/parent reclaim.
        public CxlAcceleratorSubmission? ReleaseReceipt { get; set; }
        public PlatformAuthorityResult<CxlAcceleratorSubmission>? RawSubmitObservation { get; set; }
        public ClosureInvocation? OriginalClosure { get; set; }
        public bool ParentPinConsumed { get; set; }
        public bool ProviderClosed { get; set; }
        public OperationBinding? NotAcceptedBinding { get; set; }
    }
    private sealed class ClosureInvocation
    {
        public ClosureInvocation(EffectRecord source, CxlAcceleratorSubmission submission)
        { OwnerSelf = this; Source = source; Submission = submission; }
        public ClosureInvocation OwnerSelf { get; }
        public EffectRecord Source { get; }
        public CxlAcceleratorSubmission Submission { get; }
        public bool CancelInvoked { get; set; }
        public bool ReleaseInvoked { get; set; }
        public PlatformAuthorityResult? RawCancelObservation { get; set; }
        public PlatformAuthorityResult? RawReleaseObservation { get; set; }
    }
    private readonly ConcurrentDictionary<EffectRecord, byte> _effectOwners = new();
    private bool IsOriginalEffect(EffectRecord record)
    {
        return ReferenceEquals(record.OwnerSelf, record) && _effectOwners.ContainsKey(record) &&
            _effects.TryGetValue(record.Binding.Operation.OperationId, out var routed) && ReferenceEquals(routed, record);
    }
    private EffectRecord[] IncompleteEffectOwners(ProcessHandle process)
    { lock (_effectGate) return _effectOwners.Keys.Where(record => record.Principal == process && !record.ParentPinConsumed).ToArray(); }
    private readonly object _effectGate = new();
    private readonly ConcurrentDictionary<ExternalOperationId, EffectRecord> _effects = new();
    private readonly RuntimeKernel kernel;
    private readonly CxlAuthorityBridge authority;
    private readonly ICxlType2AcceleratorProvider provider;
    private readonly CxlFabricManagerAuthority fabricManager;
    private readonly ConcurrentDictionary<ExternalOperationId, (ProcessHandle Principal, CxlType2Execution Execution)> _live = new();
    private readonly ConcurrentDictionary<ExternalOperationId, ProcessHandle> _uncontained = new();
    private readonly ConcurrentDictionary<ExternalOperationId, VirtualComputeContext> _virtualContexts = new();

    public CxlType2AcceleratorService(RuntimeKernel kernel, CxlAuthorityBridge authority,
        ICxlType2AcceleratorProvider provider, CxlFabricManagerAuthority fabricManager)
    {
        this.kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        this.authority = authority ?? throw new ArgumentNullException(nameof(authority));
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        this.fabricManager = fabricManager ?? throw new ArgumentNullException(nameof(fabricManager));
        kernel.RegisterCxlTeardownParticipant(this);
        kernel.RegisterComposedWorkTeardownParticipant(this);
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
        if (plan.Intent.RequiresVirtualizedDomain)
            return KernelResult<CxlType2Execution>.Fail(KernelError.PlatformDenied,
                "Virtualized Type-2 work requires an exact kernel-validated virtual compute context.");
        return SubmitCore(principal, plan, currentCandidates, subject, deviceLease, endpoint, fabric,
            platformGeneration, effectPolicy, securityAuthority, securityPolicy, null, null);
    }

    internal KernelResult<CxlType2Execution> SubmitProtected(
        ProcessHandle principal,
        ComputePlan plan,
        IReadOnlyList<ComputeProviderCandidate> currentCandidates,
        PlatformDomainIdentity subject,
        PlatformDeviceLease deviceLease,
        CxlEndpointSnapshot endpoint,
        CxlFabricBinding fabric,
        ulong platformGeneration,
        Func<ExternalOperationHandle, IEnumerable<ProtectedAcceleratorLabelSidebandV1>> labelSideband,
        ExternalEffectPolicy? effectPolicy = null,
        CxlSecurityAuthority? securityAuthority = null,
        CxlSecurityPolicy? securityPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(labelSideband);
        if (provider is not ICxlType2ProtectedAcceleratorProvider)
            return KernelResult<CxlType2Execution>.Fail(KernelError.PlatformUnsupported,
                "The Type-2 provider does not expose a protected label submission boundary.");
        if (plan.Intent.RequiresVirtualizedDomain)
            return KernelResult<CxlType2Execution>.Fail(KernelError.PlatformDenied,
                "Virtualized protected Type-2 work requires an exact kernel-validated virtual compute context.");
        if (plan.Intent.RightInput is not null)
            return KernelResult<CxlType2Execution>.Fail(KernelError.PlatformUnsupported,
                "Protected Type-2 sideband currently binds one input Region only.");
        return SubmitCore(principal, plan, currentCandidates, subject, deviceLease, endpoint, fabric,
            platformGeneration, effectPolicy, securityAuthority, securityPolicy, null, labelSideband);
    }

    private KernelResult<CxlType2Execution> SubmitCore(
        ProcessHandle principal,
        ComputePlan plan,
        IReadOnlyList<ComputeProviderCandidate> currentCandidates,
        PlatformDomainIdentity subject,
        PlatformDeviceLease deviceLease,
        CxlEndpointSnapshot endpoint,
        CxlFabricBinding fabric,
        ulong platformGeneration,
        ExternalEffectPolicy? effectPolicy,
        CxlSecurityAuthority? securityAuthority,
        CxlSecurityPolicy? securityPolicy,
        VirtualComputeContext? virtualContext,
        Func<ExternalOperationHandle, IEnumerable<ProtectedAcceleratorLabelSidebandV1>>? labelSidebandFactory)
    {
        // Capture caller-owned collections once before validation, preparation or callbacks.
        // The captured plan is immutable request data, never execution permission.
        try
        {
            if (plan.RequiredRegionUses is { } uses)
                plan = plan with { RequiredRegionUses = Array.AsReadOnly(uses.ToArray()) };
            if (plan.Dependencies is { Nodes: { } nodes, Edges: { } edges })
                plan = plan with { Dependencies = new ComputeDependencyGraph(
                    Array.AsReadOnly(nodes.ToArray()), Array.AsReadOnly(edges.ToArray())) };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return KernelResult<CxlType2Execution>.Fail(KernelError.InvalidMessage,
                $"Compute request collections could not be captured before admission: {exception.GetType().Name}.");
        }
        var planned = virtualContext is { } plannedContext
            ? kernel.ValidateVirtualizedComputePlanBeforeSubmit(principal, plan, currentCandidates, plannedContext)
            : kernel.ValidateComputePlanBeforeSubmit(principal, plan, currentCandidates);
        if (!planned.IsSuccess) return KernelResult<CxlType2Execution>.Fail(planned.Error, planned.Message!);
        // This submit contract carries neither resource admission nor exact usage evidence.
        // An explicit envelope must never silently become ordinary unaccounted work.
        if (plan.Intent.ResourceRequirement is not null)
            return KernelResult<CxlType2Execution>.Fail(KernelError.PlatformUnsupported,
                "Type-2 resource-aware work requires live resource admission and exact provider usage settlement; this submit boundary does not support them.");
        if (virtualContext is { } exactContext)
        {
            var exact = kernel.RevalidateVirtualComputeContext(principal, exactContext, plan);
            if (!exact.IsSuccess) return KernelResult<CxlType2Execution>.Fail(exact.Error, exact.Message!);
        }
        if (plan.PublicationPath == ComputePublicationPath.DirectCoherent)
            return KernelResult<CxlType2Execution>.Fail(KernelError.PlatformUnsupported, "Direct coherent Type-2 output is future-gated until CPU alias exclusion and symmetric coherent binding release exist.");

        var dependencies = new OperationDependencySnapshot(platformGeneration, endpoint.DeviceGeneration.Value, fabric.Generation.Value);
        var policy = plan.PublicationPath == ComputePublicationPath.Staged ? ExternalPublicationPolicy.Staged : ExternalPublicationPolicy.DirectCoherent;
        var prepared = kernel.PrepareExternalOperationCore(principal, plan.RequiredRegionUses,
            ExternalVisibilityRequirement.PublicationFence, policy, effectPolicy, out var originalPreparation);
        if (!prepared.IsSuccess)
        {
            if (originalPreparation is not null) AbortPreSubmit(principal, originalPreparation.Operation);
            return KernelResult<CxlType2Execution>.Fail(prepared.Error, prepared.Message!);
        }
        IReadOnlyList<ProtectedAcceleratorLabelSidebandV1>? protectedSideband = null;
        if (labelSidebandFactory is not null)
        {
            ProtectedAcceleratorLabelResult verified;
            try
            {
                verified = V6ProtectedAcceleratorLabelFlow.Verify(labelSidebandFactory(prepared.Value!.Operation));
            }
            catch (Exception exception)
            {
                AbortPreSubmit(principal, prepared.Value!.Operation);
                return KernelResult<CxlType2Execution>.Fail(KernelError.PlatformDenied,
                    $"Protected Type-2 label production failed closed: {exception.Message}");
            }
            if (!verified.IsSuccess || !LabelsMatchOperation(verified.Sideband, plan, prepared.Value!.Operation) ||
                !LabelsMatchLiveRegions(principal, plan, verified.Sideband))
            {
                AbortPreSubmit(principal, prepared.Value!.Operation);
                return KernelResult<CxlType2Execution>.Fail(
                    verified.Error == ProtectedAcceleratorLabelError.OperationMismatch
                        ? KernelError.StaleGeneration : KernelError.PlatformDenied,
                    verified.Detail ?? "Protected Type-2 labels do not match the actual operation identity.");
            }
            protectedSideband = verified.Sideband;
        }
        var admitted = virtualContext is not null
            ? kernel.AdmitExternalOperationForVirtualContext(principal, prepared.Value!.Operation, dependencies,
                new ExternalServiceIdentity(plan.ProviderId.Value), ExternalCancellationSupport.BeforeSubmissionOnly)
            : kernel.AdmitExternalOperation(principal, prepared.Value!.Operation, dependencies,
                new ExternalServiceIdentity(plan.ProviderId.Value), ExternalCancellationSupport.BeforeSubmissionOnly);
        if (!admitted.IsSuccess)
        {
            AbortPreSubmit(principal, prepared.Value.Operation);
            return KernelResult<CxlType2Execution>.Fail(admitted.Error, admitted.Message!);
        }
        var device = authority.ValidateDeviceAuthority(subject, deviceLease, endpoint);
        if (!device.IsSuccess)
        {
            AbortPreSubmit(principal, prepared.Value.Operation);
            return KernelResult<CxlType2Execution>.Fail(device.Error, device.Message!);
        }
        if (virtualContext is { } currentContext)
        {
            var exact = kernel.RevalidateVirtualComputeContext(principal, currentContext, plan);
            if (!exact.IsSuccess)
            {
                AbortPreSubmit(principal, prepared.Value.Operation);
                return KernelResult<CxlType2Execution>.Fail(exact.Error, exact.Message!);
            }
        }
        var fabricAdmission = fabricManager.ValidateAdmission(fabric);
        if (!fabricAdmission.IsSuccess)
        {
            AbortPreSubmit(principal, prepared.Value.Operation);
            return KernelResult<CxlType2Execution>.Fail(fabricAdmission.Error, fabricAdmission.Message!);
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
        if (virtualContext is { } submissionContext)
        {
            var exact = kernel.RevalidateVirtualComputeContext(principal, submissionContext, plan);
            if (!exact.IsSuccess)
            {
                AbortPreSubmit(principal, prepared.Value.Operation);
                return KernelResult<CxlType2Execution>.Fail(exact.Error, exact.Message!);
            }
        }
        var binding = kernel.RecordExternalOperationSubmissionCore(principal, prepared.Value.Operation, dependencies,
            out var originalBinding);
        if (!binding.IsSuccess)
        {
            // Only this call's committed binding identifies our not-invoked submission.
            // An already-submitted foreign original has no such fact and stays pinned.
            if (originalBinding is { } ownBinding) AbortNotAccepted(principal, ownBinding);
            else AbortPreSubmit(principal, prepared.Value.Operation);
            return KernelResult<CxlType2Execution>.Fail(binding.Error, binding.Message!);
        }
        if (virtualContext is { } pinnedContext)
            kernel.PinVirtualComputeContext(principal, binding.Value.Operation, pinnedContext);
        var providerEffectStarted = false;
        var admittedEffect = fabricManager.ExecuteAdmission(fabric, () =>
        {
            providerEffectStarted = true;
            return SubmitProviderEffect(principal, plan, admitted.Value, binding.Value!, dependencies, endpoint, fabric, subject,
                deviceLease, securityAuthority, securityPolicy, virtualContext, protectedSideband);
        });
        if (!providerEffectStarted)
            AbortNotAccepted(principal, binding.Value);
        else if (!admittedEffect.IsSuccess && admittedEffect.Error == KernelError.ExternalEffectUncontained)
            _ = kernel.RecordExternalOperationProviderLoss(principal, binding.Value.Operation);
        return admittedEffect;
    }

    internal KernelResult<CxlType2Execution> SubmitVirtualized(
        ProcessHandle principal, ComputePlan plan, IReadOnlyList<ComputeProviderCandidate> currentCandidates,
        VirtualComputeContext context, PlatformDomainIdentity subject, PlatformDeviceLease deviceLease,
        CxlEndpointSnapshot endpoint, CxlFabricBinding fabric, ulong platformGeneration,
        ExternalEffectPolicy? effectPolicy = null, CxlSecurityAuthority? securityAuthority = null,
        CxlSecurityPolicy? securityPolicy = null)
    {
        if (!plan.Intent.RequiresVirtualizedDomain)
            return KernelResult<CxlType2Execution>.Fail(KernelError.PlatformDenied, "Virtualized Type-2 submit requires a virtualized compute plan.");
        return SubmitCore(principal, plan, currentCandidates, subject, deviceLease, endpoint, fabric, platformGeneration,
            effectPolicy, securityAuthority, securityPolicy, context, null);
    }

    private KernelResult<CxlType2Execution> SubmitProviderEffect(
        ProcessHandle principal, ComputePlan plan, OperationAdmissionSnapshot admission,
        OperationBinding binding, OperationDependencySnapshot dependencies, CxlEndpointSnapshot endpoint,
        CxlFabricBinding fabric, PlatformDomainIdentity subject, PlatformDeviceLease deviceLease,
        CxlSecurityAuthority? securityAuthority, CxlSecurityPolicy? securityPolicy,
        VirtualComputeContext? virtualContext,
        IReadOnlyList<ProtectedAcceleratorLabelSidebandV1>? protectedSideband)
    {
        if (plan.Intent.RequiresSecureEvidence)
        {
            if (securityAuthority is null || securityPolicy is null)
            {
                AbortNotAccepted(principal, binding);
                return KernelResult<CxlType2Execution>.Fail(KernelError.PlatformDenied,
                    "Secure-required Type-2 provider submission lost its trust predicate.");
            }
            var trust = securityAuthority.Evaluate(admission.Principal,
                admission.RegionUses[0].Handle, subject, deviceLease, endpoint, fabric, securityPolicy);
            if (!trust.IsSuccess || !trust.Value!.Ready)
            {
                AbortNotAccepted(principal, binding);
                return KernelResult<CxlType2Execution>.Fail(trust.Error,
                    trust.Message ?? "Device trust evidence became invalid before provider submit.");
            }
        }
        var pinned = authority.BeginOperation(admission.Principal, fabric, deviceLease);
        if (!pinned.IsSuccess)
        {
            AbortNotAccepted(principal, binding);
            return KernelResult<CxlType2Execution>.Fail(pinned.Error, pinned.Message!);
        }
        var record = new EffectRecord(principal, binding, pinned.Value!);
        lock (_effectGate)
        {
            if (!_effects.TryAdd(binding.Operation.OperationId, record))
            {
                _ = authority.EndOperation(pinned.Value!);
                return KernelResult<CxlType2Execution>.Fail(KernelError.PlatformBindingActive, "Type-2 effect admission is already tracked.");
            }
            _effectOwners.TryAdd(record, 0);
        }
        try { return SubmitPinnedEffect(principal, plan, admission, binding, dependencies, endpoint, fabric, subject,
            deviceLease, securityAuthority, securityPolicy, virtualContext, protectedSideband, record); }
        finally { lock (_effectGate) record.CallbackInFlight = false; }
    }

    private KernelResult<CxlType2Execution> SubmitPinnedEffect(
        ProcessHandle principal, ComputePlan plan, OperationAdmissionSnapshot admission,
        OperationBinding binding, OperationDependencySnapshot dependencies, CxlEndpointSnapshot endpoint,
        CxlFabricBinding fabric, PlatformDomainIdentity subject, PlatformDeviceLease deviceLease,
        CxlSecurityAuthority? securityAuthority, CxlSecurityPolicy? securityPolicy,
        VirtualComputeContext? virtualContext, IReadOnlyList<ProtectedAcceleratorLabelSidebandV1>? protectedSideband,
        EffectRecord effectRecord)
    {
        if (virtualContext is { } submittingContext)
        {
            var current = kernel.RevalidateVirtualComputeContext(principal, submittingContext, plan);
            if (!current.IsSuccess)
            {
                AbortNotAccepted(principal, binding);
                return KernelResult<CxlType2Execution>.Fail(current.Error, current.Message!);
            }
        }
        PlatformAuthorityResult<CxlAcceleratorSubmission> submitted;
        VirtualDomainAuthority.EffectSource? submitVirtualSource = null;
        RuntimeKernel.SecureEffectSource? submitSecureSource = null;
        RuntimeKernel.VirtualIoEffectSource? submitIoSource = null;
        KernelResult AdmitSubmit(bool begin = true)
        {
            Func<Func<KernelResult>, KernelResult>? localAdmission = null;
            PreparedSecurePublication? secureAdmission = null;
            if (virtualContext is { } context)
            {
                if (submitVirtualSource is null)
                {
                    var captured = kernel.CaptureVirtualComputeSource(principal, context.VirtualDomain);
                    if (!captured.IsSuccess) return KernelResult.Fail(captured.Error, captured.Message!);
                    submitVirtualSource = captured.Value;
                }
                if (submitIoSource is null)
                {
                    var captured = kernel.CaptureVirtualComputeIoSource(principal, context);
                    if (!captured.IsSuccess) return KernelResult.Fail(captured.Error, captured.Message!);
                    submitIoSource = captured.Value;
                }
                if (context.SecureExecution is { } secureBinding && submitSecureSource is null)
                {
                    var captured = kernel.CaptureSecureComputeSource(principal, secureBinding);
                    if (!captured.IsSuccess) return KernelResult.Fail(captured.Error, captured.Message!);
                    submitSecureSource = captured.Value;
                }
                var prepared = kernel.PrepareVirtualComputePublication(principal, context, plan, submitVirtualSource, submitSecureSource, submitIoSource);
                if (!prepared.IsSuccess) return KernelResult.Fail(prepared.Error, prepared.Message!);
                localAdmission = prepared.Value;
                if (context.SecureExecution is not null)
                {
                    var secure = kernel.PrepareSecurePublication(principal, context, submitVirtualSource, submitSecureSource);
                    if (!secure.IsSuccess) return KernelResult.Fail(secure.Error, secure.Message!);
                    secureAdmission = secure.Value;
                }
            }
            KernelResult CommitStart()
            {
                if (!begin) return KernelResult.Ok();
                if (!_effects.TryGetValue(binding.Operation.OperationId, out var effect) ||
                    !IsOriginalEffect(effect) || !ReferenceEquals(effect, effectRecord) || effect.Principal != principal || effect.Binding != binding ||
                    Interlocked.CompareExchange(ref effect.SubmitAdmitted, 1, 0) != 0)
                    return KernelResult.Fail(KernelError.InvalidTransition, "Exact Type-2 effect already started or disappeared.");
                return KernelResult.Ok();
            }
            KernelResult CommitParent() => authority.CommitOperationSource(effectRecord.Pin, CommitStart);
            KernelResult CommitLocal() => virtualContext is { } context
                ? kernel.CommitVirtualComputeIo(principal, context, CommitParent, submitIoSource) : CommitParent();
            // Existing OperationPin and CallbackInFlight retain effect/cleanup ownership
            // from this admission boundary through provider submit and receipt handling.
            return kernel.CommitCxlDeviceEffect(deviceLease, admission.Principal,
                () => secureAdmission is { } secure ? secure.Publication(CommitLocal) : CommitLocal(),
                localAdmission, secureAdmission?.Admission);
        }
        try
        {
            var request = new CxlAcceleratorRequest(plan.Intent.Operation, plan.PublicationPath, binding,
                admission.RegionUses, endpoint.EndpointId, endpoint.DeviceGeneration,
                new(fabric.BindingId, fabric.Generation));
            if (protectedSideband is not null)
            {
                var reverified = V6ProtectedAcceleratorLabelFlow.Verify(protectedSideband);
                if (!reverified.IsSuccess || !LabelsMatchOperation(reverified.Sideband, plan, binding.Operation) ||
                    !LabelsMatchLiveRegions(principal, plan, reverified.Sideband))
                {
                    AbortNotAccepted(principal, binding);
                    return KernelResult<CxlType2Execution>.Fail(KernelError.StaleGeneration,
                        reverified.Detail ?? "Protected Type-2 labels became stale before provider submit.");
                }
                if (provider is not ICxlType2ProtectedAcceleratorProvider protectedProvider)
                {
                    AbortNotAccepted(principal, binding);
                    return KernelResult<CxlType2Execution>.Fail(KernelError.PlatformUnsupported,
                        "The protected Type-2 provider boundary disappeared before submit.");
                }
                var permission = AdmitSubmit();
                if (!permission.IsSuccess)
                {
                    AbortNotAccepted(principal, binding);
                    return KernelResult<CxlType2Execution>.Fail(permission.Error, permission.Message!);
                }
                submitted = protectedProvider.SubmitProtected(
                    new(request, plan.ProviderGeneration, reverified.Sideband));
            }
            else
            {
                var permission = AdmitSubmit();
                if (!permission.IsSuccess)
                {
                    AbortNotAccepted(principal, binding);
                    return KernelResult<CxlType2Execution>.Fail(permission.Error, permission.Message!);
                }
                submitted = provider.Submit(request);
            }
        }
        catch (Exception exception)
        {
            _uncontained[binding.Operation.OperationId] = principal;
            // Preserve unknown submit effects in the existing operation/use/resource
            // owners as well as this adapter's recovery pin. This is not closure.
            _ = kernel.RecordExternalOperationProviderLoss(principal, binding.Operation);
            _ = TrackSubmittedFabricOperation(fabric, principal, binding.Operation,
                () => KernelResult.Fail(KernelError.ExternalEffectUncontained,
                    "Type-2 submission threw without a recovery token."));
            return KernelResult<CxlType2Execution>.Fail(KernelError.ExternalEffectUncontained,
                $"Type-2 submission threw after its effect boundary and remains quarantined: {exception.Message}");
        }
        lock (_effectGate) effectRecord.RawSubmitObservation = submitted;
        if (!submitted.IsSuccess)
        {
            if (submitted.Status == PlatformAuthorityStatus.NotAccepted)
            {
                // Exact invocation no-effect fact survives later source observation loss.
                lock (_effectGate) effectRecord.NotAcceptedBinding = binding;
                try
                {
                    var source = authority.ValidateDeviceAuthority(subject, deviceLease, endpoint);
                    var currentFabric = authority.QueryFabric(fabric.BindingId);
                    if (source.IsSuccess && currentFabric.IsSuccess && currentFabric.Value == fabric)
                        AbortNotAccepted(principal, binding);
                    else
                        return KernelResult<CxlType2Execution>.Fail(KernelError.ExternalEffectUncontained,
                            "Type-2 rejection lost its exact source tuple; parent remains pinned.");
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                {
                    return KernelResult<CxlType2Execution>.Fail(KernelError.ExternalEffectUncontained,
                        $"Type-2 rejection source observation failed; exact no-effect fact remains pinned: {exception.Message}");
                }
            }
            else
            {
                _uncontained[binding.Operation.OperationId] = principal;
                _ = kernel.RecordExternalOperationProviderLoss(principal, binding.Operation);
                _ = TrackSubmittedFabricOperation(fabric, principal, binding.Operation,
                    () => KernelResult.Fail(KernelError.ExternalEffectUncontained, "Submission acceptance remains ambiguous without a recovery token."));
            }
            return KernelResult<CxlType2Execution>.Fail(Map(submitted.Status), submitted.Message ?? "Type-2 submission failed.");
        }
        if (submitted.Value!.OperationBinding != binding || submitted.Value.EndpointId != endpoint.EndpointId ||
            submitted.Value.DeviceGeneration != endpoint.DeviceGeneration ||
            submitted.Value.FabricBinding != new CxlFabricBindingRef(fabric.BindingId, fabric.Generation))
        {
            _ = kernel.RecordExternalOperationProviderLoss(principal, binding.Operation);
            var compensation = ConfirmProviderClosure(principal, binding.Operation, submitted.Value, false);
            if (compensation.IsSuccess)
                CloseSubmitted(principal, binding.Operation);
            else
            {
                var recovery = new CxlType2Execution(plan, binding.Operation, binding, submitted.Value,
                    dependencies, endpoint, fabric, subject, deviceLease, securityAuthority, securityPolicy);
                _live[binding.Operation.OperationId] = (principal, recovery);
                _ = TrackSubmittedFabricOperation(fabric, principal, binding.Operation,
                    () => CloseProviderForFabricDrain(principal, recovery));
            }
            return KernelResult<CxlType2Execution>.Fail(KernelError.PlatformFaulted, "Type-2 provider returned a malformed submission identity.");
        }
        var execution = new CxlType2Execution(plan, binding.Operation, binding,
            submitted.Value, dependencies, endpoint, fabric, subject, deviceLease,
            securityAuthority, securityPolicy);
        _live.TryAdd(execution.Operation.OperationId, (principal, execution));
        if (virtualContext is { } context) _virtualContexts.TryAdd(execution.Operation.OperationId, context);
        var tracked = TrackSubmittedFabricOperation(fabric, principal, execution.Operation,
            () => CloseProviderForFabricDrain(principal, execution));
        if (!tracked.IsSuccess)
        {
            var cancellation = ConfirmProviderClosure(principal, execution.Operation, execution.Submission, true);
            if (!cancellation.IsSuccess)
            {
                // A returned submission remains an original effect even when FM tracking
                // refuses it. Failed compensation is not closure or pre-submit refusal.
                _ = kernel.RecordExternalOperationProviderLoss(principal, execution.Operation);
                return KernelResult<CxlType2Execution>.Fail(KernelError.ExternalEffectUncontained,
                    cancellation.Message ?? "Untracked Type-2 submission has no confirmed closure; exact execution remains pinned.");
            }
            if (cancellation.IsSuccess)
            {
                var settled = CloseSubmitted(principal, execution.Operation);
                if (settled.IsSuccess)
                {
                    _live.TryRemove(execution.Operation.OperationId, out _);
                    _virtualContexts.TryRemove(execution.Operation.OperationId, out _);
                }
            }
            return KernelResult<CxlType2Execution>.Fail(tracked.Error, tracked.Message!);
        }
        if (virtualContext is { } submittedContext)
        {
            var exact = kernel.RevalidateVirtualComputeContext(principal, submittedContext, plan);
            if (!exact.IsSuccess)
            {
                var failed = FailBeforePublication(principal, execution, exact.Error, exact.Message!);
                return KernelResult<CxlType2Execution>.Fail(failed.Error, failed.Message!);
            }
        }
        var finalPermission = AdmitSubmit(begin: false);
        if (!finalPermission.IsSuccess)
            return KernelResult<CxlType2Execution>.Fail(KernelError.ExternalEffectUncontained,
                "Type-2 receipt arrived after local authorization changed; actual closure consumer retains it.");
        return KernelResult<CxlType2Execution>.Ok(execution);
    }

    private KernelResult TrackSubmittedFabricOperation(CxlFabricBinding fabric, ProcessHandle principal,
        ExternalOperationHandle operation, Func<KernelResult> closure)
    {
        try { return fabricManager.TrackOperation(fabric, principal, operation, closure); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            _ = kernel.RecordExternalOperationProviderLoss(principal, operation);
            return KernelResult.Fail(KernelError.ExternalEffectUncontained,
                $"Fabric tracking observation failed after submit; original effect remains pinned: {exception.Message}");
        }
    }

    public KernelResult<ExternalOperationSnapshot> CompleteVisiblePublish(
        ProcessHandle principal,
        CxlType2Execution execution,
        IReadOnlyList<ComputeProviderCandidate> currentCandidates,
        Action publicationAction) => CompleteVisiblePublishCore(principal, execution, currentCandidates,
            publicationAction, null);

    internal KernelResult<ExternalOperationSnapshot> CompleteVisiblePublishWithGuestEvent(
        ProcessHandle principal,
        CxlType2Execution execution,
        IReadOnlyList<ComputeProviderCandidate> currentCandidates,
        Action publicationAction,
        VirtualIoBinding virtualIo,
        CapabilityId eventCapability,
        KernelEventEndpoint endpoint) => CompleteVisiblePublishCore(principal, execution, currentCandidates,
            publicationAction, () => kernel.PublishVirtualIoEvent(principal, virtualIo, execution.Operation,
                eventCapability, endpoint));

    private KernelResult<ExternalOperationSnapshot> CompleteVisiblePublishCore(
        ProcessHandle principal,
        CxlType2Execution execution,
        IReadOnlyList<ComputeProviderCandidate> currentCandidates,
        Action publicationAction,
        Func<KernelResult>? publishGuestEvent)
    {
        if (!_live.TryGetValue(execution.Operation.OperationId, out var live) ||
            live.Principal != principal || live.Execution != execution)
            return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.StaleGeneration,
                "Exact live Type-2 execution identity is required before completion or publication.");
        try
        {
            return CompleteVisiblePublishAdmitted(principal, execution, currentCandidates, publicationAction, publishGuestEvent);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // Initial source/provider queries can fail before a result-phase interlock.
            // The already-submitted effect and exact receipt are retained, not erased.
            return LostObservation(principal, execution, exception);
        }
    }

    private KernelResult<ExternalOperationSnapshot> CompleteVisiblePublishAdmitted(
        ProcessHandle principal,
        CxlType2Execution execution,
        IReadOnlyList<ComputeProviderCandidate> currentCandidates,
        Action publicationAction,
        Func<KernelResult>? publishGuestEvent)
    {
        var capturedFabricSource = fabricManager.CaptureEffectSource(execution.Fabric);
        if (!capturedFabricSource.IsSuccess)
            return FailBeforePublication(principal, execution, capturedFabricSource.Error, capturedFabricSource.Message!);
        if (_virtualContexts.TryGetValue(execution.Operation.OperationId, out var virtualContext))
        {
            var exact = kernel.RevalidateVirtualComputeContext(principal, virtualContext, execution.Plan);
            if (!exact.IsSuccess)
                return FailBeforePublication(principal, execution, exact.Error, exact.Message!);
        }
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
        var securityBeforeCompletion = RevalidateSecurity(execution, operation.Value.Admission);
        if (!securityBeforeCompletion.IsSuccess)
            return FailBeforePublication(principal, execution, securityBeforeCompletion.Error,
                securityBeforeCompletion.Message!);

        var completionAdmission = BeginResultPhase(principal, execution);
        if (!completionAdmission.IsSuccess)
            return KernelResult<ExternalOperationSnapshot>.Fail(completionAdmission.Error, completionAdmission.Message!);
        PlatformAuthorityResult<CxlAcceleratorCompletion> completion;
        var completed = KernelResult<ExternalOperationSnapshot>.Fail(KernelError.InvalidTransition, "Completion receipt was not committed.");
        try
        {
            completion = provider.ObserveCompletion(execution.Submission);
            if (completion.IsSuccess && completion.Value!.Submission == execution.Submission)
                completed = kernel.RecordExternalOperationCompletion(principal,
                    new(execution.Binding, completion.Value.Disposition));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return LostObservation(principal, execution, exception);
        }
        finally { lock (_effectGate) completionAdmission.Value!.ObservationInFlight = false; }
        if (!completion.IsSuccess || completion.Value!.Submission != execution.Submission)
            return FailBeforePublication(principal, execution, Map(completion.Status), completion.Message ?? "Type-2 completion is stale.");
        if (!completed.IsSuccess) return completed;
        if (completion.Value.Disposition != ExternalOperationCompletionDisposition.Completed)
            return CloseAfterProviderRelease(principal, execution, KernelError.PlatformFaulted,
                $"Type-2 provider completed with {completion.Value.Disposition}.");
        // Device completion is not publication authority. Security/provider generations may
        // change while completion is observed, so close that window before visibility.
        var securityAfterCompletion = RevalidateSecurity(execution, operation.Value.Admission);
        if (!securityAfterCompletion.IsSuccess)
            return FailBeforePublication(principal, execution, securityAfterCompletion.Error,
                securityAfterCompletion.Message!);
        if (_virtualContexts.TryGetValue(execution.Operation.OperationId, out var completedContext))
        {
            var current = kernel.RevalidateVirtualComputeContext(principal, completedContext, execution.Plan);
            if (!current.IsSuccess) return FailBeforePublication(principal, execution, current.Error, current.Message!);
        }
        var visibilityAdmission = BeginResultPhase(principal, execution);
        if (!visibilityAdmission.IsSuccess)
            return KernelResult<ExternalOperationSnapshot>.Fail(visibilityAdmission.Error, visibilityAdmission.Message!);
        PlatformAuthorityResult<CxlAcceleratorVisibility> visibility;
        var visible = KernelResult<ExternalOperationSnapshot>.Fail(KernelError.InvalidTransition, "Visibility receipt was not committed.");
        try
        {
            visibility = provider.AcquireVisibility(execution.Submission, ExternalVisibilityRequirement.PublicationFence);
            if (visibility.IsSuccess && visibility.Value!.Submission == execution.Submission)
                visible = kernel.RecordExternalOperationVisibility(principal,
                    new(execution.Binding, visibility.Value.Requirement, visibility.Value.Satisfied));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return LostObservation(principal, execution, exception);
        }
        finally { lock (_effectGate) visibilityAdmission.Value!.ObservationInFlight = false; }
        if (!visibility.IsSuccess || visibility.Value!.Submission != execution.Submission)
            return FailBeforePublication(principal, execution, Map(visibility.Status), visibility.Message ?? "Type-2 visibility is stale.");
        if (!visible.IsSuccess)
            return CloseAfterProviderRelease(principal, execution, visible.Error, visible.Message);
        // AcquireVisibility is a provider callback. A reset there invalidates the
        // earlier security query, even when the visibility receipt itself succeeded.
        var securityBeforePublication = RevalidateSecurity(execution, operation.Value.Admission);
        if (!securityBeforePublication.IsSuccess)
            return FailBeforePublication(principal, execution, securityBeforePublication.Error,
                securityBeforePublication.Message!);
        if (_virtualContexts.TryGetValue(execution.Operation.OperationId, out var visibleContext))
        {
            var current = kernel.RevalidateVirtualComputeContext(principal, visibleContext, execution.Plan);
            if (!current.IsSuccess) return FailBeforePublication(principal, execution, current.Error, current.Message!);
        }
        if (!_effects.TryGetValue(execution.Operation.OperationId, out var publicationSource) ||
            !IsOriginalEffect(publicationSource) || publicationSource.Principal != principal || publicationSource.Binding != execution.Binding)
            return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.StaleGeneration, "Original Type-2 publication source is absent.");
        var sourceBeforePublication = kernel.CommitCxlDeviceEffect(execution.DeviceLease,
            operation.Value.Admission.Principal, KernelResult.Ok);
        if (!sourceBeforePublication.IsSuccess)
            return FailBeforePublication(principal, execution, sourceBeforePublication.Error, sourceBeforePublication.Message!);
        VirtualDomainAuthority.EffectSource? publicationVirtualSource = null;
        RuntimeKernel.SecureEffectSource? publicationSecureSource = null;
        RuntimeKernel.VirtualIoEffectSource? publicationIoSource = null;
        if (_virtualContexts.TryGetValue(execution.Operation.OperationId, out var capturedContext))
        {
            var captured = kernel.CaptureVirtualComputeSource(principal, capturedContext.VirtualDomain);
            if (!captured.IsSuccess) return FailBeforePublication(principal, execution, captured.Error, captured.Message!);
            publicationVirtualSource = captured.Value;
            var ioCaptured = kernel.CaptureVirtualComputeIoSource(principal, capturedContext);
            if (!ioCaptured.IsSuccess) return FailBeforePublication(principal, execution, ioCaptured.Error, ioCaptured.Message!);
            publicationIoSource = ioCaptured.Value;
            if (capturedContext.SecureExecution is { } secureBinding)
            {
                var secureCaptured = kernel.CaptureSecureComputeSource(principal, secureBinding);
                if (!secureCaptured.IsSuccess) return FailBeforePublication(principal, execution, secureCaptured.Error, secureCaptured.Message!);
                publicationSecureSource = secureCaptured.Value;
            }
        }
        KernelResult<ExternalOperationSnapshot> CommitSource(Func<KernelResult<ExternalOperationSnapshot>> commit)
        {
            var result = KernelResult<ExternalOperationSnapshot>.Fail(KernelError.InvalidTransition, "Source commit did not run.");
            Func<Func<KernelResult>, KernelResult>? virtualCommit = null;
            PreparedSecurePublication? secureCommit = null;
            if (_virtualContexts.TryGetValue(execution.Operation.OperationId, out var finalContext))
            {
                var prepared = kernel.PrepareVirtualComputePublication(principal, finalContext, execution.Plan, publicationVirtualSource, publicationSecureSource, publicationIoSource);
                if (!prepared.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(prepared.Error, prepared.Message!);
                virtualCommit = prepared.Value;
                if (finalContext.SecureExecution is not null)
                {
                    var securePrepared = kernel.PrepareSecurePublication(principal, finalContext, publicationVirtualSource, publicationSecureSource);
                    if (!securePrepared.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(securePrepared.Error, securePrepared.Message!);
                    secureCommit = securePrepared.Value;
                }
            }
            KernelResult CommitOwner()
            {
                result = commit();
                return result.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(result.Error, result.Message!);
            }
            KernelResult CommitParent() => fabricManager.CommitEffectSource(capturedFabricSource.Value,
                () => authority.CommitOperationSource(publicationSource.Pin, CommitOwner));
            KernelResult CommitLocal() => virtualCommit is null ? CommitParent()
                : kernel.CommitVirtualComputeIo(principal, finalContext, CommitParent, publicationIoSource);
            var permission = kernel.CommitCxlDeviceEffect(execution.DeviceLease, operation.Value.Admission.Principal,
                () => secureCommit is { } secure ? secure.Publication(CommitLocal) : CommitLocal(),
                virtualCommit, secureCommit?.Admission);
            return permission.IsSuccess ? result : KernelResult<ExternalOperationSnapshot>.Fail(permission.Error, permission.Message!);
        }
        var publicationAdmission = BeginResultPhase(principal, execution, publication: true);
        if (!publicationAdmission.IsSuccess)
            return KernelResult<ExternalOperationSnapshot>.Fail(publicationAdmission.Error, publicationAdmission.Message!);
        KernelResult<ExternalOperationSnapshot> published;
        KernelResult? delivered = null;
        try
        {
            published = kernel.PublishExternalOperation(principal, execution.Operation, execution.Dependencies,
                new(execution.Plan.PublicationPath == ComputePublicationPath.Staged ? ExternalPublicationPolicy.Staged : ExternalPublicationPolicy.DirectCoherent),
                _ =>
                {
                    publicationAction();
                    if (_virtualContexts.TryGetValue(execution.Operation.OperationId, out var actionContext))
                    {
                        var current = kernel.RevalidateVirtualComputeContext(principal, actionContext, execution.Plan);
                        if (!current.IsSuccess)
                            throw new InvalidOperationException($"Virtual source changed during publication: {current.Error}: {current.Message}");
                    }
                    // The action may already have published output. A trust reset during
                    // it must leave the owner's publication decision ambiguous.
                    var afterAction = RevalidateSecurity(execution, operation.Value.Admission);
                    if (!afterAction.IsSuccess)
                        throw new InvalidOperationException(
                            $"Type-2 trust changed during publication action: {afterAction.Error}: {afterAction.Message}");
                }, CommitSource,
                sourceAdmission: execution.Plan.PublicationPath == ComputePublicationPath.Staged ? CommitSource : null);
            if (published.IsSuccess && publishGuestEvent is not null)
                delivered = publishGuestEvent();
        }
        finally { lock (_effectGate) publicationAdmission.Value!.PublicationInFlight = false; }
        if (!published.IsSuccess)
            return CloseAfterProviderRelease(principal, execution, published.Error, published.Message);
        if (delivered is { IsSuccess: false } deliveryFailure)
            return CloseAfterProviderRelease(principal, execution, deliveryFailure.Error, deliveryFailure.Message);
        var release = ConfirmProviderClosure(principal, execution.Operation, execution.Submission, false);
        if (!release.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.PlatformFaulted, release.Message ?? "Type-2 release is ambiguous; operation remains quarantined.");
        return CloseLocalAfterProviderClosure(principal, execution);
    }

    private static KernelResult RevalidateSecurity(CxlType2Execution execution,
        OperationAdmissionSnapshot admission)
    {
        if (!execution.Plan.Intent.RequiresSecureEvidence) return KernelResult.Ok();
        if (execution.SecurityAuthority is null || execution.SecurityPolicy is null)
            return KernelResult.Fail(KernelError.PlatformDenied,
                "Secure readiness receipt is missing before publication.");
        var readiness = execution.SecurityAuthority.Evaluate(admission.Principal,
            admission.RegionUses[0].Handle, execution.Subject, execution.DeviceLease,
            execution.Endpoint, execution.Fabric, execution.SecurityPolicy);
        if (!readiness.IsSuccess)
            return KernelResult.Fail(readiness.Error,
                readiness.Message ?? "Secure readiness became stale before publication.");
        return readiness.Value!.Ready
            ? KernelResult.Ok()
            : KernelResult.Fail(KernelError.PlatformDenied,
                "Required secure readiness was not proven before publication.");
    }

    private KernelResult<EffectRecord> BeginResultPhase(ProcessHandle principal, CxlType2Execution execution,
        bool publication = false)
    {
        lock (_effectGate)
        {
            if (!_effects.TryGetValue(execution.Operation.OperationId, out var record) ||
                !IsOriginalEffect(record) || record.Principal != principal || record.Binding != execution.Binding ||
                record.RawSubmitObservation is not { IsSuccess: true } originalSubmit || !ReferenceEquals(originalSubmit.Value, execution.Submission) ||
                execution.Submission.OperationBinding != record.Binding || record.ProviderClosed || record.ReleaseReceipt is not null)
                return KernelResult<EffectRecord>.Fail(KernelError.StaleGeneration,
                    "Exact live Type-2 submission is required before a result phase.");
            if (record.CallbackInFlight || record.ClosureInFlight || record.ObservationInFlight || record.PublicationInFlight)
                return KernelResult<EffectRecord>.Fail(KernelError.PlatformBindingDraining,
                    "Type-2 callback, observation, publication or closure is already in flight.");
            if (record.OriginalClosure is not null)
                return KernelResult<EffectRecord>.Fail(KernelError.ExternalEffectUncontained, "Original closure invocation prevents further observation/publication.");
            if (publication) record.PublicationInFlight = true;
            else record.ObservationInFlight = true;
            return KernelResult<EffectRecord>.Ok(record);
        }
    }

    private KernelResult<ExternalOperationSnapshot> LostObservation(
        ProcessHandle principal, CxlType2Execution execution, Exception exception)
    {
        // The exact submit receipt remains in _live for the existing closure consumer.
        // A lost completion/visibility reply cannot establish no effect or release.
        _ = kernel.RecordExternalOperationProviderLoss(principal, execution.Operation);
        return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.ExternalEffectUncontained,
            $"Type-2 observation response was lost; exact submission remains pinned: {exception.Message}");
    }

    private KernelResult<ExternalOperationSnapshot> FailBeforePublication(ProcessHandle principal, CxlType2Execution execution, KernelError error, string message)
    {
        var providerClosure = ConfirmProviderClosure(principal, execution.Operation, execution.Submission, false);
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
        var providerClosure = ConfirmProviderClosure(principal, execution.Operation, execution.Submission, false);
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
            var parent = ReleaseParentPin(principal, execution.Operation);
            if (!parent.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(parent.Error, parent.Message!);
            _live.TryRemove(execution.Operation.OperationId, out _);
            _virtualContexts.TryRemove(execution.Operation.OperationId, out _);
            kernel.ReleaseVirtualComputeContext(execution.Operation);
            _ = fabricManager.UntrackOperation(execution.Fabric, principal, execution.Operation);
        }
        return closed;
    }

    private void AbortPreSubmit(ProcessHandle principal, ExternalOperationHandle operation)
    {
        var cancelled = kernel.CancelExternalOperation(principal, operation, false);
        if (cancelled.IsSuccess && cancelled.Value!.State is ExternalOperationState.Prepared or ExternalOperationState.Admitted &&
            cancelled.Value.Disposition == ExternalOperationDisposition.Cancelled)
            _ = kernel.ReleaseExternalOperation(principal, operation, new(false, false));
        var original = kernel.ExternalOperations.Query(operation);
        if (!original.IsSuccess || original.Value!.State != ExternalOperationState.Released)
            _uncontained[operation.OperationId] = principal;
    }

    private void AbortNotAccepted(ProcessHandle principal, OperationBinding binding)
    {
        lock (_effectGate)
            if (_effects.TryGetValue(binding.Operation.OperationId, out var record) && IsOriginalEffect(record)) record.ProviderClosed = true;
        _ = kernel.RecordExternalOperationCompletion(principal, new(binding, ExternalOperationCompletionDisposition.Cancelled));
        var closed = kernel.ReleaseExternalOperation(principal, binding.Operation, new(true, false));
        if (!closed.IsSuccess)
        {
            // Existing effect records have their own exact retry consumer. Before
            // provider invocation there may be no record; preserve the teardown interlock.
            lock (_effectGate)
                if (!_effects.ContainsKey(binding.Operation.OperationId))
                    _uncontained[binding.Operation.OperationId] = principal;
            return;
        }
        kernel.ReleaseVirtualComputeContext(binding.Operation);
        _ = ReleaseParentPin(principal, binding.Operation);
    }

    private KernelResult CloseSubmitted(ProcessHandle principal, ExternalOperationHandle operation)
    {
        var current = kernel.QueryExternalOperation(principal, operation);
        if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
        if (current.Value!.State == ExternalOperationState.Submitted)
        {
            var lost = kernel.RecordExternalOperationProviderLoss(principal, operation);
            if (!lost.IsSuccess) return KernelResult.Fail(lost.Error, lost.Message!);
        }
        var closed = kernel.ReleaseExternalOperation(principal, operation, new(true, false));
        if (!closed.IsSuccess) return KernelResult.Fail(closed.Error, closed.Message!);
        kernel.ReleaseVirtualComputeContext(operation);
        return ReleaseParentPin(principal, operation);
    }

    private KernelResult ReleaseParentPin(ProcessHandle principal, ExternalOperationHandle operation)
    {
        lock (_effectGate)
        {
            if (!_effects.TryGetValue(operation.OperationId, out var record))
                return IncompleteEffectOwners(principal).Any(owner => owner.Binding.Operation == operation)
                    ? KernelResult.Fail(KernelError.StaleGeneration, "Original Type-2 parent pin route is absent.") : KernelResult.Ok();
            if (!IsOriginalEffect(record) || record.Principal != principal || record.Binding.Operation != operation)
                return KernelResult.Fail(KernelError.StaleGeneration, "Type-2 parent pin principal/operation is stale.");
            var ended = authority.EndOperation(record.Pin);
            if (ended.IsSuccess) { record.ParentPinConsumed = true; _effects.TryRemove(operation.OperationId, out _); }
            return ended;
        }
    }

    private KernelResult ConfirmProviderClosure(ProcessHandle principal, ExternalOperationHandle operation,
        CxlAcceleratorSubmission submission, bool requestCancellation)
    {
        EffectRecord record;
        ClosureInvocation fact;
        bool OriginalFact() => IsOriginalEffect(record) && ReferenceEquals(fact.OwnerSelf, fact) &&
            ReferenceEquals(fact.Source, record) && ReferenceEquals(record.OriginalClosure, fact) &&
            ReferenceEquals(fact.Submission, submission) && record.RawSubmitObservation is { IsSuccess: true } raw &&
            ReferenceEquals(raw.Value, submission);
        lock (_effectGate)
        {
            if (!_effects.TryGetValue(operation.OperationId, out record!) || !IsOriginalEffect(record) ||
                record.RawSubmitObservation is not { IsSuccess: true } rawSubmit || !ReferenceEquals(rawSubmit.Value, submission) ||
                record.Principal != principal ||
                record.Binding.Operation != operation || record.Binding != submission.OperationBinding ||
                submission.EndpointId != record.Pin.Fabric.EndpointId || submission.DeviceGeneration != record.Pin.Fabric.DeviceGeneration ||
                submission.FabricBinding != new CxlFabricBindingRef(record.Pin.Fabric.BindingId, record.Pin.Fabric.Generation))
                return KernelResult.Fail(KernelError.StaleGeneration, "Type-2 closure requires the exact admitted submission tuple.");

            if (record.CallbackInFlight || record.ObservationInFlight || record.PublicationInFlight || record.ClosureInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "Type-2 callback, observation, publication or closure is already in flight.");
            fact = record.OriginalClosure ??= new(record, submission);
            if (!OriginalFact()) return KernelResult.Fail(KernelError.StaleGeneration, "Original Type-2 closure fact is unavailable.");
            if (record.ProviderClosed) return fact.ReleaseInvoked && fact.RawReleaseObservation is { IsSuccess: true }
                ? KernelResult.Ok() : KernelResult.Fail(KernelError.ExternalEffectUncontained, "Original Release proof is absent.");
            record.ClosureInFlight = true;
        }
        try
        {
            var source = authority.ValidateOperationClosureSource(record.Pin);
            if (!source.IsSuccess) return source;
            if (requestCancellation && !fact.ReleaseInvoked)
            {
                bool invokeCancel;
                lock (_effectGate)
                {
                    if (!OriginalFact()) return KernelResult.Fail(KernelError.StaleGeneration, "Cancel lost its original source.");
                    invokeCancel = !fact.CancelInvoked;
                    if (invokeCancel) fact.CancelInvoked = true;
                    else if (fact.RawCancelObservation is not { IsSuccess: true })
                        return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Original Cancel is unknown; no repeat.");
                }
                if (invokeCancel)
                {
                    var cancelled = provider.Cancel(submission);
                    lock (_effectGate) fact.RawCancelObservation = cancelled;
                    if (!cancelled.IsSuccess) return KernelResult.Fail(Map(cancelled.Status), cancelled.Message ?? "Cancellation outcome remains pinned.");
                    lock (_effectGate) record.CancellationAcknowledged = true;
                }
            }
            // Cancel is a provider callback. Its acknowledgement cannot preserve
            // the earlier endpoint/fabric generation observation across a reset.
            var releaseSource = authority.ValidateOperationClosureSource(record.Pin);
            if (!releaseSource.IsSuccess) return releaseSource;
            bool invokeRelease;
            lock (_effectGate)
            {
                if (!OriginalFact()) return KernelResult.Fail(KernelError.StaleGeneration, "Release lost its original source.");
                invokeRelease = !fact.ReleaseInvoked;
                if (invokeRelease) fact.ReleaseInvoked = true;
                else if (fact.RawReleaseObservation is not { IsSuccess: true })
                    return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Original Release is unknown; no repeat.");
            }
            if (invokeRelease)
            {
                var released = provider.Release(submission);
                lock (_effectGate)
                {
                    fact.RawReleaseObservation = released;
                    if (released.IsSuccess) record.ReleaseReceipt = submission;
                }
                if (!released.IsSuccess) return KernelResult.Fail(Map(released.Status), released.Message ?? "Provider release did not prove closure.");
            }
            var finalSource = authority.ValidateOperationClosureSource(record.Pin);
            if (!finalSource.IsSuccess) return finalSource;
            lock (_effectGate)
            {
                if (!OriginalFact() || fact.RawReleaseObservation is not { IsSuccess: true })
                    return KernelResult.Fail(KernelError.StaleGeneration, "Original Release cannot be consumed after owner loss.");
                record.ProviderClosed = true;
            }
            return KernelResult.Ok();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // The existing owner linearizes repeated loss; receipt loss is not closure.
            _ = kernel.RecordExternalOperationProviderLoss(principal, operation);
            return KernelResult.Fail(KernelError.ExternalEffectUncontained, $"Type-2 closure response was lost; parent remains pinned: {exception.Message}");
        }
        finally { lock (_effectGate) record.ClosureInFlight = false; }
    }

    private KernelResult CloseProviderForFabricDrain(ProcessHandle principal, CxlType2Execution execution)
    {
        var cancellation = ConfirmProviderClosure(principal, execution.Operation, execution.Submission, true);
        if (!cancellation.IsSuccess)
        {
            // A manager drain retains its callback snapshot across concurrent local
            // cleanup. Only removal after EndOperation plus the exact terminal owner
            // lifetime makes that captured callback redundant; stale is not closure.
            if (cancellation.Error == KernelError.StaleGeneration)
            {
                bool parentReleased;
                lock (_effectGate) parentReleased = !_effects.ContainsKey(execution.Operation.OperationId);
                if (parentReleased)
                {
                    var lifetime = kernel.QueryExternalOperation(principal, execution.Operation);
                    if (lifetime.IsSuccess && lifetime.Value!.State == ExternalOperationState.Released &&
                        lifetime.Value.Binding == execution.Binding)
                        return KernelResult.Ok();
                }
            }
            return KernelResult.Fail(cancellation.Error, cancellation.Message ?? "Fabric drain could not close Type-2 provider work.");
        }
        var closed = CloseLocalAfterProviderClosure(principal, execution);
        return closed.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(closed.Error, closed.Message!);
    }

    KernelResult ICxlTeardownParticipant.CloseForProcess(ProcessHandle process, RegionOwner owner) =>
        CloseForProcess(process);

    KernelResult IComposedWorkTeardownParticipant.CloseComposedWorkForProcess(ProcessHandle process, RegionOwner owner) =>
        CloseForProcess(process);

    private KernelResult CloseForProcess(ProcessHandle process)
    {
        lock (_effectGate)
            if (IncompleteEffectOwners(process).Any(record => (record.CallbackInFlight || record.ObservationInFlight || record.PublicationInFlight || record.ClosureInFlight)))
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "Type-2 callback/closure remains in flight.");
        if (_uncontained.Values.Any(principal => principal == process))
            return KernelResult.Fail(KernelError.PlatformFaulted, "Type-2 original operation lacks exact closure/recovery; reclaim remains quarantined.");
        foreach (var live in _live.Values.Where(item => item.Principal == process).ToArray())
        {
            var cancelled = ConfirmProviderClosure(process, live.Execution.Operation, live.Execution.Submission, true);
            if (!cancelled.IsSuccess)
                return KernelResult.Fail(cancelled.Error, cancelled.Message ?? "Type-2 provider closure is ambiguous; reclaim is quarantined.");
            var closed = CloseLocalAfterProviderClosure(process, live.Execution);
            if (!closed.IsSuccess) return KernelResult.Fail(closed.Error, closed.Message!);
        }
        // Exact no-effect/closure receipts can outlive failed local settlement without a published execution.
        foreach (var record in IncompleteEffectOwners(process))
        {
            if (!IsOriginalEffect(record)) return KernelResult.Fail(KernelError.StaleGeneration, "Original Type-2 inventory lost its canonical route.");
            // The published execution cache is a route, not the original submit fact.
            // A surviving exact submission can close through its original effect owner.
            if (!record.ProviderClosed && record.RawSubmitObservation is { IsSuccess: true } submitted && submitted.Value is { } originalSubmission)
            {
                var closure = ConfirmProviderClosure(process, record.Binding.Operation, originalSubmission, true);
                if (!closure.IsSuccess) return closure;
            }
            if (!record.ProviderClosed && record.NotAcceptedBinding is not null)
            {
                var noEffect = ConfirmNotAcceptedSource(record);
                if (!noEffect.IsSuccess) return noEffect;
            }
            var exactRelease = record.OriginalClosure is { } fact && ReferenceEquals(fact.OwnerSelf, fact) &&
                ReferenceEquals(fact.Source, record) && fact.ReleaseInvoked && fact.RawReleaseObservation is { IsSuccess: true } &&
                record.RawSubmitObservation is { IsSuccess: true } raw && ReferenceEquals(raw.Value, fact.Submission);
            var exactNoEffect = record.SubmitAdmitted == 0 || record.NotAcceptedBinding == record.Binding &&
                record.RawSubmitObservation is { Status: PlatformAuthorityStatus.NotAccepted };
            if (!record.ProviderClosed || !exactRelease && !exactNoEffect)
                return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Type-2 effect has no exact original closure/no-effect fact.");
            var settled = CloseSubmitted(process, record.Binding.Operation);
            if (!settled.IsSuccess) return settled;
            _live.TryRemove(record.Binding.Operation.OperationId, out _);
            _virtualContexts.TryRemove(record.Binding.Operation.OperationId, out _);
            _ = fabricManager.UntrackOperation(record.Pin.Fabric, process, record.Binding.Operation);
        }
        return KernelResult.Ok();
    }

    private KernelResult ConfirmNotAcceptedSource(EffectRecord record)
    {
        lock (_effectGate)
        {
            if (!_effects.TryGetValue(record.Binding.Operation.OperationId, out var current) ||
                !IsOriginalEffect(record) || !ReferenceEquals(current, record) || record.NotAcceptedBinding != record.Binding)
                return KernelResult.Fail(KernelError.StaleGeneration, "No-effect cleanup requires the exact original effect record.");
            if (record.CallbackInFlight || record.ObservationInFlight || record.PublicationInFlight || record.ClosureInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "No-effect source observation is already in flight.");
            record.ClosureInFlight = true;
        }
        try
        {
            var source = authority.ValidateOperationClosureSource(record.Pin);
            if (!source.IsSuccess) return source;
            lock (_effectGate)
            {
                if (!_effects.TryGetValue(record.Binding.Operation.OperationId, out var current) ||
                    !IsOriginalEffect(record) || !ReferenceEquals(current, record) || record.NotAcceptedBinding != record.Binding)
                    return KernelResult.Fail(KernelError.StaleGeneration, "No-effect cleanup lost its original record.");
                record.ProviderClosed = true;
            }
            return KernelResult.Ok();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return KernelResult.Fail(KernelError.ExternalEffectUncontained, $"No-effect cleanup source remains unavailable: {exception.Message}");
        }
        finally { lock (_effectGate) record.ClosureInFlight = false; }
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

    private static bool LabelsMatchOperation(
        IReadOnlyList<ProtectedAcceleratorLabelSidebandV1> sideband,
        ComputePlan plan,
        ExternalOperationHandle operation)
    {
        var operationId = $"external-operation:{operation.OperationId.Value}";
        return sideband.Count >= 2 && sideband.All(value =>
            string.Equals(value.OperationId, operationId, StringComparison.Ordinal) &&
            value.ProviderGeneration == plan.ProviderGeneration &&
            value.OperationGeneration == operation.Generation.Value &&
            !value.AuthorizesAccess && !value.AuthorizesExecution);
    }

    private bool LabelsMatchLiveRegions(ProcessHandle principal, ComputePlan plan,
        IReadOnlyList<ProtectedAcceleratorLabelSidebandV1> sideband)
    {
        if (sideband.Count < 2 || plan.Intent.RightInput is not null) return false;
        var process = kernel.Processes.Resolve(principal);
        if (!process.IsSuccess) return false;
        var owner = new RegionOwner(process.Value!.DomainId, principal.Generation);
        var input = kernel.Regions.QueryProtectedLabel(plan.Intent.Input.Region, owner);
        var output = kernel.Regions.QueryProtectedLabel(plan.Intent.Output.Region, owner);
        return input.IsSuccess && output.IsSuccess &&
            sideband[0].Label == input.Value!.Label &&
            sideband[0].LabelGeneration == input.Value.LabelGeneration &&
            sideband[^1].Label == output.Value!.Label &&
            sideband[^1].LabelGeneration == output.Value.LabelGeneration;
    }
}
