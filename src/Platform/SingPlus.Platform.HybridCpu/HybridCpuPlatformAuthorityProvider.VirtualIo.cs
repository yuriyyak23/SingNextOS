using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformVirtualIoPrivateBackingProvider, IPlatformOriginalVirtualIoCloseProvider, IPlatformOriginalVirtualIoBindProvider
{
    public bool SupportsOriginalVirtualIoClose => _childRuntime is INeutralOriginalVirtualIoCloseProvider;
    public PlatformAuthorityResult<ulong> DescribeVirtualIoPrivateBackingBytes(PlatformVirtualIoProfile profile)
    {
        if (_childRuntime is not INeutralOriginalVirtualIoProvider original) return PlatformAuthorityResult<ulong>.Ok(0); // No additive private-buffer producing path.
        NeutralVirtualizationResult<ulong> raw = original.DescribeOriginalVirtualIoPrivateBackingBytes(new(ToNeutralDeviceRights(profile.Rights), profile.MaximumDmaTransferBytes));
        return raw.IsSuccess ? PlatformAuthorityResult<ulong>.Ok(raw.Value) : PlatformAuthorityResult<ulong>.Fail(PlatformAuthorityStatus.NotAccepted, raw.Reason);
    }
    private bool HasOriginalIoSources(IoRecord fact) => fact.IsOriginal && !fact.SourceFaulted &&
        _originalProviderVirtualIo.TryGetValue(fact.Lease.LeaseId, out IoRecord? original) && ReferenceEquals(original, fact) &&
        IsOriginalChildOwner(fact.Child) && _providerChildren.TryGetValue(fact.Child.Lease.LeaseId, out ChildRecord? child) && ReferenceEquals(child, fact.Child) &&
        _originalDeviceLeases.TryGetValue(fact.Device.Lease.LeaseId, out DeviceLeaseRecord? device) && ReferenceEquals(device, fact.Device) &&
        _deviceLeases.TryGetValue(device.Lease.LeaseId, out DeviceLeaseRecord? deviceRoute) && ReferenceEquals(deviceRoute, device) && ReferenceEquals(device.OwnerSelf, device) &&
        ReferenceEquals(device.AdmissionSource, fact.DeviceFactory) && fact.DeviceFactory is { } factory && ReferenceEquals(factory.OwnerSelf, factory) &&
        ReferenceEquals(factory.OriginalLease, device) && ReferenceEquals(device.BindParent, fact.Parent) && ReferenceEquals(fact.Child.CreateParent, fact.Parent) &&
        fact.Parent is { } parent && _domains.TryGetValue(parent.Lease.LeaseId, out DomainRecord? parentRoute) && ReferenceEquals(parentRoute, parent) &&
        ReferenceEquals(parent.BindSource, fact.ParentFactory) && fact.ParentFactory is { } root &&
        _originalRootBinds.TryGetValue(parent.Lease.LeaseId, out RootBindRecord? rootOriginal) && ReferenceEquals(rootOriginal, root) && ReferenceEquals(root.OriginalDomain, parent);

    private bool HasProviderIoPins(PlatformProviderChildDomainLease child, bool pendingOnly = false)
    {
        if (_originalProviderChildren.TryGetValue(child.LeaseId, out ChildRecord? owner) &&
            (owner.IoAdmissionsConsumed > owner.IoAdmissionsIssued || owner.IoAdmissionsIssued - owner.IoAdmissionsConsumed !=
                (ulong)_originalProviderVirtualIo.Values.Count(fact => ReferenceEquals(fact.Child, owner) && !fact.Consumed))) return true;
        return _originalProviderVirtualIo.Values.Any(fact => fact.Lease.ChildLease == child && !fact.Consumed &&
            (!pendingOnly || fact.BindInFlight || fact.CloseInFlight || fact.TransferInFlight || !fact.Published));
    }

    private bool HasProviderIoPins(PlatformProviderDeviceLease device) =>
        _originalDeviceLeases.TryGetValue(device.LeaseId, out DeviceLeaseRecord? owner) && owner.IoAdmissionsIssued != owner.IoAdmissionsConsumed ||
        _originalProviderVirtualIo.Values.Any(fact => fact.Lease.ParentDeviceLease == device && !fact.Consumed);

    private static void ConsumeOriginalIo(IoRecord fact)
    {
        if (fact.Consumed) return;
        fact.Child.IoAdmissionsConsumed++;
        fact.Device.IoAdmissionsConsumed++;
        fact.Consumed = true;
    }

    public bool SupportsOriginalVirtualIoBindCleanup => _childRuntime is INeutralOriginalVirtualIoBindCleanupProvider { SupportsOriginalVirtualIoBindCleanup: true };
    public PlatformAuthorityResult<PlatformProviderVirtualIoLease> BindOriginalVirtualIo(PlatformOriginalVirtualIoBindRequest request)
        => request is null || request.InvocationId == Guid.Empty
            ? PlatformAuthorityResult<PlatformProviderVirtualIoLease>.Fail(PlatformAuthorityStatus.NotAccepted, "Original invocation required.")
            : BindOriginalProviderVirtualIo(request.Request, request);
    private PlatformAuthorityResult<PlatformProviderVirtualIoLease> BindOriginalProviderVirtualIo(PlatformVirtualIoRequest request, PlatformOriginalVirtualIoBindRequest? admission = null)
    {
        PlatformAuthorityResult validation = PlatformVirtualIoContract.ValidateRequest(request);
        if (!validation.IsSuccess) return PlatformAuthorityResult<PlatformProviderVirtualIoLease>.Fail(validation.Status, validation.Message!);
        if (_childRuntime is not INeutralVirtualIoProvider io) return ChildContourBlocked<PlatformProviderVirtualIoLease>("bind bounded virtual I/O");
        bool supportsCleanup = admission is null || SupportsOriginalVirtualIoBindCleanup; // Optional callback outside owner gate.
        IoRecord fact;
        lock (_childCreateCommitGate)
        {
            if (admission is not null && (!supportsCleanup || _originalProviderVirtualIo.Values.Any(f => f.BindAdmission?.InvocationId == admission.InvocationId)))
                return PlatformAuthorityResult<PlatformProviderVirtualIoLease>.Fail(PlatformAuthorityStatus.NotAccepted, "Original invocation cannot repeat.");
            if (!_providerChildren.TryGetValue(request.ChildLease.LeaseId, out ChildRecord? child) || !IsOriginalChildOwner(child) || child.Lease != request.ChildLease ||
                !_deviceLeases.TryGetValue(request.ParentDeviceLease.LeaseId, out DeviceLeaseRecord? device) || device.Lease != request.ParentDeviceLease ||
                device.Revoked || device.CleanupOnly || device.CloseInFlight || device.CloseUncertain || child.CreateParent is not { } parent ||
                child.UnclosedGuestMap is not null || child.GuestMapInFlight || child.CloseInFlight || child.TransitionInFlight || child.FailedTransition is not null ||
                child.FailedClose is not null || child.ClosureReceipt is not null || HasProviderIoPins(child.Lease, true) ||
                HasGuestUnmapInterlock(child.Lease) || HasArtifactBindInterlock(child.Lease) || !ValidateDomain(parent.Lease).IsSuccess)
                return PlatformAuthorityResult<PlatformProviderVirtualIoLease>.Fail(PlatformAuthorityStatus.Faulted, "Original child/device is absent, changed or in flight.");
            if (_nextProviderIoId == 0 || _nextProviderIoId == ulong.MaxValue || child.IoAdmissionsIssued == ulong.MaxValue || device.IoAdmissionsIssued == ulong.MaxValue)
                return PlatformAuthorityResult<PlatformProviderVirtualIoLease>.Fail(PlatformAuthorityStatus.NotAccepted, "Virtual-I/O identity capacity exhausted before effect.");
            PlatformProviderVirtualIoLease lease = new PlatformProviderVirtualIoLease(new(_nextProviderIoId), new(1), request.ChildLease, request.ParentDeviceLease, request.Profile);
            fact = new(lease, request, child, device);
            fact.BindAdmission = admission;
            fact.EstablishSelf();
            _originalProviderVirtualIo.EnsureCapacity(_originalProviderVirtualIo.Count + 1);
            _providerVirtualIo.EnsureCapacity(_providerVirtualIo.Count + 1);
            _originalProviderVirtualIo.Add(lease.LeaseId, fact);
            if (!HasOriginalIoSources(fact))
            {
                _originalProviderVirtualIo.Remove(lease.LeaseId); // No external effect yet.
                return PlatformAuthorityResult<PlatformProviderVirtualIoLease>.Fail(PlatformAuthorityStatus.Faulted, "Original factories are not canonical.");
            }
            _nextProviderIoId++;
            child.IoAdmissionsIssued++;
            device.IoAdmissionsIssued++;
            fact.BindInFlight = true;
        }
        try
        {
            PlatformAuthorityResult source = ValidateOriginalNativeMappingSource();
            lock (_childCreateCommitGate) if (!source.IsSuccess || !HasOriginalIoSources(fact))
            {
                ConsumeOriginalIo(fact); // This fact has not invoked the producer.
                return PlatformAuthorityResult<PlatformProviderVirtualIoLease>.Fail(PlatformAuthorityStatus.Faulted, "Source changed before invocation.");
            }
            NeutralVirtualIoRequest nativeRequest = new NeutralVirtualIoRequest(fact.Child.Neutral, fact.Device.HybridCpuLease,
                new(ToNeutralDeviceRights(request.Profile.Rights), request.Profile.MaximumDmaTransferBytes));
            NeutralVirtualizationResult<NeutralVirtualIoLease> raw;
            NeutralOriginalVirtualIoRequest? originalRequest = null;
            if (io is INeutralOriginalVirtualIoProvider original && fact.DeviceFactory?.NativeRequest is { } deviceAdmission)
            {
                lock (_childCreateCommitGate) fact.NativeOriginalRequest = originalRequest = new(admission?.InvocationId ?? Guid.NewGuid(), deviceAdmission, nativeRequest);
                raw = original.BindOriginalVirtualIo(originalRequest);
            }
            else raw = io.BindVirtualIo(nativeRequest);
            lock (_childCreateCommitGate) fact.Admission = raw; // Before provider/source callback.
            source = ValidateOriginalNativeMappingSource();
            lock (_childCreateCommitGate)
            {
                if (!source.IsSuccess) fact.SourceFaulted = true;
                if (!source.IsSuccess || !HasOriginalIoSources(fact) || fact.Device.Revoked || fact.Device.CloseUncertain || fact.Child.ClosureReceipt is not null ||
                    fact.Admission is not { } retained || retained != raw || !ReferenceEquals(fact.NativeOriginalRequest, originalRequest) ||
                    !ReferenceEquals(fact.BindAdmission, admission) || originalRequest is not null && originalRequest.Request != nativeRequest ||
                    !raw.IsSuccess || !NeutralVirtualIoContract.ValidateLease(nativeRequest, raw.Value).IsSuccess)
                    return PlatformAuthorityResult<PlatformProviderVirtualIoLease>.Fail(PlatformAuthorityStatus.Faulted, "Original I/O outcome/source unknown or malformed; dependencies pinned.");
                fact.Neutral = raw.Value;
                fact.Published = true;
                _providerVirtualIo.Add(fact.Lease.LeaseId, fact);
                return PlatformAuthorityResult<PlatformProviderVirtualIoLease>.Ok(fact.Lease);
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return PlatformAuthorityResult<PlatformProviderVirtualIoLease>.Fail(PlatformAuthorityStatus.Faulted, "Original I/O Bind retained/unknown: " + exception.GetType().Name); }
        finally { lock (_childCreateCommitGate) fact.BindInFlight = false; }
    }

    public PlatformAuthorityResult<PlatformVirtualIoClosureReceipt> CloseOriginalVirtualIo(PlatformOriginalVirtualIoCloseRequest request)
        => CloseOriginalProviderVirtualIo(request.Lease, request);
    public PlatformAuthorityResult<PlatformVirtualIoClosureReceipt> ContinueOriginalVirtualIoClose(PlatformOriginalVirtualIoCloseRequest request)
        => CloseOriginalProviderVirtualIo(request.Lease, request, true);
    private PlatformAuthorityResult<PlatformVirtualIoClosureReceipt> CloseOriginalProviderVirtualIo(PlatformProviderVirtualIoLease lease,
        PlatformOriginalVirtualIoCloseRequest? admission = null, bool continuation = false)
    {
        if (_childRuntime is not INeutralVirtualIoProvider io) return ChildContourBlocked<PlatformVirtualIoClosureReceipt>("close bounded virtual I/O");
        IoRecord fact;
        IoCloseObservation? observation;
        NeutralOriginalVirtualIoCloseRequest? nativeAdmission;
        NeutralVirtualIoLease nativeLease;
        lock (_childCreateCommitGate)
        {
            if (!_originalProviderVirtualIo.TryGetValue(lease.LeaseId, out fact!) || !fact.IsOriginal || fact.Lease != lease)
                return PlatformAuthorityResult<PlatformVirtualIoClosureReceipt>.Fail(PlatformAuthorityStatus.Stale, "Original I/O lease unavailable.");
            if ((fact.Completion is not null || fact.OriginalCompletion is not null) && !HasOriginalIoCloseObservation(fact))
                return PlatformAuthorityResult<PlatformVirtualIoClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Original Close observation custody unavailable; pinned.");
            if (admission is null && fact.Consumed && fact.Published) return OriginalIoClosure(fact); // V1 retained completion, no effect.
            if (admission is not null && (admission.InvocationId == Guid.Empty || admission.Lease != lease) ||
                (fact.CloseAdmission is null ? continuation || admission is not null && fact.CloseInvoked : !ReferenceEquals(fact.CloseAdmission, admission)))
                return PlatformAuthorityResult<PlatformVirtualIoClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Original Close admission differs; no repeat.");
            if (fact.Consumed && fact.Published) return OriginalIoClosure(fact);
            if (!fact.Published || fact.BindInFlight || fact.CloseInFlight || fact.TransferInFlight || !HasOriginalIoSources(fact) ||
                !_providerVirtualIo.TryGetValue(lease.LeaseId, out IoRecord? route) || !ReferenceEquals(route, fact))
                return PlatformAuthorityResult<PlatformVirtualIoClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Original I/O source unavailable or in flight.");
            if (fact.CloseInvoked && fact.Completion is null && admission is null)
                return PlatformAuthorityResult<PlatformVirtualIoClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Unknown original I/O Close cannot repeat.");
            if (admission is not null && fact.CloseAdmission is null)
            {
                if (_childRuntime is not INeutralOriginalVirtualIoCloseProvider || fact.NativeOriginalRequest is null)
                    return PlatformAuthorityResult<PlatformVirtualIoClosureReceipt>.Fail(PlatformAuthorityStatus.Unsupported, "Original cleanup continuation unavailable.");
                fact.CloseAdmission = admission;
                fact.NativeCloseAdmission = new(admission.InvocationId, fact.NativeOriginalRequest, fact.Neutral);
            }
            observation = fact.Completion;
            nativeAdmission = fact.NativeCloseAdmission;
            nativeLease = fact.Neutral;
            fact.CloseInFlight = true;
        }
        try
        {
            if (observation is null || admission is not null && !observation.Result.IsSuccess)
            {
                PlatformAuthorityResult source = ValidateOriginalNativeMappingSource();
                bool wasInvoked;
                lock (_childCreateCommitGate)
                {
                    if (!source.IsSuccess) fact.SourceFaulted = true;
                    if (!source.IsSuccess || !HasOriginalIoSources(fact) || !ReferenceEquals(fact.Completion, observation) ||
                        !ReferenceEquals(fact.CloseAdmission, admission) || !ReferenceEquals(fact.NativeCloseAdmission, nativeAdmission) || fact.Neutral != nativeLease ||
                        observation is not null && !HasOriginalIoCloseObservation(fact))
                        return PlatformAuthorityResult<PlatformVirtualIoClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Original Close source changed before effect.");
                    wasInvoked = fact.CloseInvoked;
                    fact.CloseInvoked = true;
                }
                NeutralVirtualizationResult<NeutralVirtualIoCloseReceipt> raw = admission is not null && io is INeutralOriginalVirtualIoCloseProvider original
                    ? wasInvoked ? original.ContinueOriginalVirtualIoClose(nativeAdmission!) : original.CloseOriginalVirtualIo(nativeAdmission!)
                    : io.CloseVirtualIo(nativeLease);
                observation = new(fact, admission, nativeAdmission, nativeLease, raw);
                lock (_childCreateCommitGate) fact.Completion = fact.OriginalCompletion = observation;
            }
            PlatformAuthorityResult finalSource = ValidateOriginalNativeMappingSource(); // Raw before fresh source callback.
            lock (_childCreateCommitGate)
            {
                if (!finalSource.IsSuccess) fact.SourceFaulted = true;
                if (!finalSource.IsSuccess || !HasOriginalIoSources(fact) || !ReferenceEquals(fact.Completion, observation) ||
                    !HasOriginalIoCloseObservation(fact) || !NeutralVirtualIoContract.ValidateClose(nativeLease, observation.Result).IsSuccess)
                    return PlatformAuthorityResult<PlatformVirtualIoClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Exact original Close/source unavailable; pinned.");
                ConsumeOriginalIo(fact);
                _providerVirtualIo.Remove(lease.LeaseId);
                return OriginalIoClosure(fact);
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return PlatformAuthorityResult<PlatformVirtualIoClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Original I/O Close retained/unknown: " + exception.GetType().Name); }
        finally { lock (_childCreateCommitGate) fact.CloseInFlight = false; }
    }

    private static bool HasOriginalIoCloseObservation(IoRecord fact) => fact.Completion is { IsOriginal: true } observation &&
        ReferenceEquals(observation, fact.OriginalCompletion) && ReferenceEquals(observation.Source, fact) &&
        ReferenceEquals(observation.Admission, fact.CloseAdmission) && ReferenceEquals(observation.NativeAdmission, fact.NativeCloseAdmission) &&
        observation.Lease == fact.Neutral && (observation.NativeAdmission is not { } request ||
            ReferenceEquals(request.Admission, fact.NativeOriginalRequest) && request.Lease == observation.Lease &&
            observation.Admission is { } admission && request.InvocationId == admission.InvocationId && admission.Lease == fact.Lease);

    private static PlatformAuthorityResult<PlatformVirtualIoClosureReceipt> OriginalIoClosure(IoRecord fact) =>
        !fact.Consumed || !HasOriginalIoCloseObservation(fact) || !NeutralVirtualIoContract.ValidateClose(fact.Neutral, fact.Completion!.Result).IsSuccess
        ? PlatformAuthorityResult<PlatformVirtualIoClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Original retained Close observation unavailable.")
        : PlatformAuthorityResult<PlatformVirtualIoClosureReceipt>.Ok(new(fact.Lease.LeaseId, fact.Lease.Generation,
            fact.Lease.ChildLease.LeaseId, fact.Lease.ChildLease.Generation, fact.Lease.ChildLease.ParentDomainLease.LeaseId,
            fact.Lease.ChildLease.ParentDomainLease.Generation, true));

    public PlatformOriginalVirtualIoBindCleanupClosure? ResolveOriginalVirtualIoBindCleanup(PlatformOriginalVirtualIoBindRequest request)
    {
        if (request is null) return null;
        lock (_childCreateCommitGate)
        {
            IoRecord? fact = _originalProviderVirtualIo.Values.SingleOrDefault(f => ReferenceEquals(f.BindAdmission, request));
            return fact is not null && fact.IsOriginal && HasOriginalProviderBindCleanup(fact) ? fact.BindCleanup : null;
        }
    }
    private static bool HasOriginalProviderBindCleanup(IoRecord fact) => fact.Consumed &&
        fact.BindCleanup is { NoFurtherUse: true } closure && ReferenceEquals(closure, fact.OriginalBindCleanup) && ReferenceEquals(closure.Admission, fact.BindAdmission) &&
        fact.BindCleanupRaw is { IsSuccess: true, Value: { NoFurtherUse: true } raw } && ReferenceEquals(raw, fact.ValidatedBindCleanup) &&
        ReferenceEquals(raw.Admission, fact.NativeOriginalRequest) && closure.NoEffect == raw.NoEffect;
    public PlatformAuthorityResult<PlatformOriginalVirtualIoBindCleanupClosure> CleanupOriginalVirtualIoBind(PlatformOriginalVirtualIoBindRequest request)
    {
        if (request is null || _childRuntime is not INeutralOriginalVirtualIoBindCleanupProvider cleanup)
            return PlatformAuthorityResult<PlatformOriginalVirtualIoBindCleanupClosure>.Fail(PlatformAuthorityStatus.Unsupported, "Original Bind cleanup unavailable.");
        IoRecord fact;
        lock (_childCreateCommitGate)
        {
            IoRecord[] originals = _originalProviderVirtualIo.Values.Where(f => ReferenceEquals(f.BindAdmission, request)).ToArray();
            if (originals.Length != 1 || !originals[0].IsOriginal)
                return PlatformAuthorityResult<PlatformOriginalVirtualIoBindCleanupClosure>.Fail(PlatformAuthorityStatus.Faulted, "Original Bind intent unavailable.");
            fact = originals[0];
            if (fact.Consumed) return HasOriginalProviderBindCleanup(fact) ? PlatformAuthorityResult<PlatformOriginalVirtualIoBindCleanupClosure>.Ok(fact.BindCleanup!) :
                PlatformAuthorityResult<PlatformOriginalVirtualIoBindCleanupClosure>.Fail(PlatformAuthorityStatus.Faulted, "Original retained Bind cleanup custody unavailable.");
            if (fact.BindInFlight || fact.CloseInFlight || fact.TransferInFlight || fact.NativeOriginalRequest is null || !HasOriginalIoSources(fact))
                return PlatformAuthorityResult<PlatformOriginalVirtualIoBindCleanupClosure>.Fail(PlatformAuthorityStatus.Faulted, "Original Bind source/phase unavailable.");
            if (fact.CloseAdmission is not null || fact.CloseInvoked) return PlatformAuthorityResult<PlatformOriginalVirtualIoBindCleanupClosure>.Fail(PlatformAuthorityStatus.Faulted, "Another original Close scope is admitted.");
            fact.CloseInFlight = true;
        }
        try
        {
            PlatformAuthorityResult source = ValidateOriginalNativeMappingSource();
            lock (_childCreateCommitGate) if (!source.IsSuccess || !HasOriginalIoSources(fact) ||
                fact.ValidatedBindCleanup is { } prior && (fact.BindCleanupRaw is not { IsSuccess: true } old || !ReferenceEquals(old.Value, prior)))
                return PlatformAuthorityResult<PlatformOriginalVirtualIoBindCleanupClosure>.Fail(PlatformAuthorityStatus.Faulted, "Original cleanup source changed.");
            NeutralVirtualizationResult<NeutralOriginalVirtualIoBindCleanupClosure> raw = cleanup.CleanupOriginalVirtualIoBind(fact.NativeOriginalRequest!);
            lock (_childCreateCommitGate) fact.BindCleanupRaw = raw;
            NeutralOriginalVirtualIoBindCleanupClosure? resolved = cleanup.ResolveOriginalVirtualIoBindCleanup(fact.NativeOriginalRequest!); // Pure, raw retained first.
            lock (_childCreateCommitGate)
            {
                if (!raw.IsSuccess || raw.Value is not { NoFurtherUse: true } produced || !ReferenceEquals(produced, resolved) ||
                    !ReferenceEquals(produced.Admission, fact.NativeOriginalRequest) ||
                    fact.BindCleanupRaw is not { IsSuccess: true } retained || !ReferenceEquals(retained.Value, produced) ||
                    fact.ValidatedBindCleanup is { } prior && !ReferenceEquals(prior, produced))
                    return PlatformAuthorityResult<PlatformOriginalVirtualIoBindCleanupClosure>.Fail(PlatformAuthorityStatus.Faulted, "Original Bind cleanup raw custody unavailable.");
                fact.ValidatedBindCleanup = produced;
            }
            source = ValidateOriginalNativeMappingSource();
            // Source validation is an external callback. Re-establish the same
            // lower-owner proof after it, before consuming this obligation.
            NeutralOriginalVirtualIoBindCleanupClosure? finalResolved = cleanup.ResolveOriginalVirtualIoBindCleanup(fact.NativeOriginalRequest!);
            lock (_childCreateCommitGate)
            {
                if (!source.IsSuccess || !HasOriginalIoSources(fact) || !raw.IsSuccess || raw.Value is not { NoFurtherUse: true } closure ||
                    !ReferenceEquals(closure, fact.ValidatedBindCleanup) || fact.BindCleanupRaw is not { IsSuccess: true } current || !ReferenceEquals(current.Value, closure) ||
                    !ReferenceEquals(closure.Admission, fact.NativeOriginalRequest) || !ReferenceEquals(closure, resolved) || !ReferenceEquals(closure, finalResolved))
                    return PlatformAuthorityResult<PlatformOriginalVirtualIoBindCleanupClosure>.Fail(PlatformAuthorityStatus.Faulted, "Exact original backing closure unavailable.");
                fact.BindCleanup = fact.OriginalBindCleanup = new(request, true, closure.NoEffect); ConsumeOriginalIo(fact);
                _providerVirtualIo.Remove(fact.Lease.LeaseId);
                return PlatformAuthorityResult<PlatformOriginalVirtualIoBindCleanupClosure>.Ok(fact.BindCleanup);
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return PlatformAuthorityResult<PlatformOriginalVirtualIoBindCleanupClosure>.Fail(PlatformAuthorityStatus.Faulted, "Original Bind cleanup retained after " + exception.GetType().Name); }
        finally { lock (_childCreateCommitGate) fact.CloseInFlight = false; }
    }
}

