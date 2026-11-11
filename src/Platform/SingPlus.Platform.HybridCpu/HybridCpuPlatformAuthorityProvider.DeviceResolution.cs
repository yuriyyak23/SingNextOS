using YAKSys_Hybrid_CPU.Core;
namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider
{
    public PlatformAuthorityResult<PlatformOriginalDeviceBindResolution> ResolveDeviceBind(PlatformOriginalDeviceBindRequest request)
    {
        DeviceBindFailure fact;
        lock (_childCreateCommitGate)
        {
            if (!_originalRootBinds.TryGetValue(request.Domain.LeaseId,out RootBindRecord? root) || root.OriginalDomain is not { } parent || !HasOriginalDeviceAdmissionHistory(parent))
                return PlatformAuthorityResult<PlatformOriginalDeviceBindResolution>.Fail(PlatformAuthorityStatus.Faulted,"Original device parent unavailable.");
            DeviceBindFailure[] history = parent.OriginalDeviceAdmissions.Where(old => ReferenceEquals(old.OriginalRequest,request)).ToArray();
            if (history.Length != 1 || !ReferenceEquals(history[0].OwnerSelf,history[0]) || parent.DeviceBindInFlight)
                return PlatformAuthorityResult<PlatformOriginalDeviceBindResolution>.Fail(PlatformAuthorityStatus.Faulted,"Original device request absent/foreign/in flight.");
            fact = history[0];
        }
        PlatformAuthorityResult fresh = ValidateOriginalNativeMappingSource();
        if (!fresh.IsSuccess) return PlatformAuthorityResult<PlatformOriginalDeviceBindResolution>.Fail(fresh.Status,fresh.Message!);
        if (fact.NativeObservation is null && fact.NativeRequest is { } nativeRequest && _runtime is INeutralOriginalDeviceProvider original)
        {
            NeutralOriginalDeviceBindObservation? raw = original.ResolveDeviceBind(nativeRequest);
            lock (_childCreateCommitGate) { fact.NativeObservation = raw; if (raw is { } value) fact.Observation = value.Observation; }
        }
        PlatformAuthorityResult after = ValidateOriginalNativeMappingSource();
        if (!after.IsSuccess) return PlatformAuthorityResult<PlatformOriginalDeviceBindResolution>.Fail(after.Status,after.Message!);
        lock (_childCreateCommitGate)
        {
            if (!ReferenceEquals(fact.OwnerSelf,fact) || !ReferenceEquals(fact.OriginalRequest,request) || fact.Domain != request.Domain || fact.Device != request.Device || fact.Rights != request.Rights ||
                !_domains.TryGetValue(request.Domain.LeaseId,out DomainRecord? parent) || !ReferenceEquals(parent,fact.Parent) || !ReferenceEquals(parent.BindSource,fact.ParentFactory) ||
                !parent.OriginalDeviceAdmissions.Any(old => ReferenceEquals(old,fact)) || fact.NativeRequest is not { } expected || fact.NativeObservation is not { } raw || raw.Request != expected ||
                raw.Observation is null || !Enum.IsDefined(raw.Observation.Decision) || !raw.SourceFenceMatched && !raw.NoEffectGuaranteed)
                return PlatformAuthorityResult<PlatformOriginalDeviceBindResolution>.Fail(PlatformAuthorityStatus.Faulted,"Original device outcome/source unknown.");
            if (raw.NoEffectGuaranteed && !raw.Observation.IsBound && raw.Observation.Decision!=NeutralDeviceBindDecision.Bound && raw.Observation.Lease == default && fact.OriginalLease is null)
            { fact.NoEffectConsumed = true; if (ReferenceEquals(parent.FailedDeviceBind,fact)) parent.FailedDeviceBind = null; return PlatformAuthorityResult<PlatformOriginalDeviceBindResolution>.Ok(new(request,true,null)); }
            NeutralDeviceLease native = raw.Observation.Lease;
            if (raw.NoEffectGuaranteed || !raw.Observation.IsBound || raw.Observation.Decision != NeutralDeviceBindDecision.Bound || native.Handle.Value == 0 || native.Epoch.Value == 0 ||
                native.DomainLease != parent.HybridCpuLease || native.Device != expected.Device || native.Rights != expected.Rights)
                return PlatformAuthorityResult<PlatformOriginalDeviceBindResolution>.Fail(PlatformAuthorityStatus.Faulted,"Original device cleanup receipt malformed.");
            DeviceLeaseRecord? owner = fact.OriginalLease;
            if (owner is null)
            {
                PlatformProviderDeviceLease lease = new PlatformProviderDeviceLease(fact.InvocationId,new(1),request.Domain,request.Device,request.Rights);
                owner = new(lease,native,true) { BindParent = parent, AdmissionSource = fact, CleanupOnly = true };
                fact.OriginalLease = owner; _originalDeviceLeases.Add(lease.LeaseId,owner); _deviceLeases.Add(lease.LeaseId,owner);
            }
            if (!ReferenceEquals(owner.OwnerSelf,owner) || !ReferenceEquals(owner.AdmissionSource,fact) || !_originalDeviceLeases.TryGetValue(owner.Lease.LeaseId,out DeviceLeaseRecord? originalOwner) || !ReferenceEquals(originalOwner,owner) ||
                !_deviceLeases.TryGetValue(owner.Lease.LeaseId,out DeviceLeaseRecord? routed) || !ReferenceEquals(routed,owner))
                return PlatformAuthorityResult<PlatformOriginalDeviceBindResolution>.Fail(PlatformAuthorityStatus.Faulted,"Original device cleanup route is foreign.");
            if (ReferenceEquals(parent.FailedDeviceBind,fact)) parent.FailedDeviceBind = null;
            return PlatformAuthorityResult<PlatformOriginalDeviceBindResolution>.Ok(new(request,false,owner.Lease)); // Cleanup only, never device admission.
        }
    }

    private bool HasConsumedOriginalProviderDevice(DeviceLeaseRecord record) => ReferenceEquals(record.OwnerSelf,record) &&
        _originalDeviceLeases.TryGetValue(record.Lease.LeaseId, out DeviceLeaseRecord? original) && ReferenceEquals(original, record) &&
        _deviceLeases.TryGetValue(record.Lease.LeaseId, out DeviceLeaseRecord? routed) && ReferenceEquals(routed, record) &&
        (!record.OriginalAdmissionRequired || HasOriginalConsumedDeviceFactories(record)) &&
        (record.LifetimeRaw is { NoFurtherUse: true } lifetime && ReferenceEquals(lifetime, record.ValidatedLifetime) &&
         ReferenceEquals(lifetime.Admission, record.AdmissionSource?.NativeObservation?.Observation) ||
         record.NativeClose is { } close && ReferenceEquals(close.OwnerSelf,close) && ReferenceEquals(close.Source,record) && record.CloseHistory.Any(old => ReferenceEquals(old,close)) &&
         close.Observation is { SourceFenceMatched: true, NoEffectGuaranteed: false } raw && raw.Request == close.Request && raw.Observation.Decision == NeutralDeviceCloseDecision.Closed &&
         close.Request.Device == record.HybridCpuLease && close.Request.DeviceBindInvocationId == record.AdmissionSource?.NativeRequest?.InvocationId ||
         !record.OriginalAdmissionRequired && record.AdmissionSource?.NativeRequest is null && record.Revoked && record.CloseObservation is { Decision: NeutralDeviceCloseDecision.Closed });

    private bool HasOriginalConsumedDeviceFactories(DeviceLeaseRecord record) =>
        record.AdmissionSource is { NativeRequest: { } request, NativeObservation: { } raw } fact && ReferenceEquals(fact.OwnerSelf, fact) &&
        ReferenceEquals(fact.OriginalLease, record) && ReferenceEquals(record.BindParent, fact.Parent) &&
        fact.Domain == record.Lease.DomainLease && request.Domain == fact.Parent.HybridCpuLease && raw.Request == request &&
        raw is { SourceFenceMatched: true, NoEffectGuaranteed: false } && raw.Observation is { IsBound: true, Decision: NeutralDeviceBindDecision.Bound } &&
        raw.Observation.Lease == record.HybridCpuLease && ReferenceEquals(fact.Observation, raw.Observation) &&
        _domains.TryGetValue(fact.Domain.LeaseId, out DomainRecord? parent) && ReferenceEquals(parent, fact.Parent) &&
        ReferenceEquals(parent.BindSource, fact.ParentFactory) && fact.ParentFactory is { } factory &&
        _originalRootBinds.TryGetValue(fact.Domain.LeaseId, out RootBindRecord? root) && ReferenceEquals(root, factory) &&
        ReferenceEquals(factory.OriginalDomain, parent) &&
        HasOriginalDeviceAdmissionHistory(parent) && parent.OriginalDeviceAdmissions.Count(old => ReferenceEquals(old, fact)) == 1;

    private static bool HasOriginalDeviceAdmissionHistory(DomainRecord parent) =>
        (ulong)parent.OriginalDeviceAdmissions.Count == parent.OriginalDevicesIssued &&
        parent.OriginalDeviceAdmissions.All(fact => fact is not null && ReferenceEquals(fact.OwnerSelf, fact) && ReferenceEquals(fact.Parent, parent) && fact.Domain == parent.Lease) &&
        parent.OriginalDeviceAdmissions.Distinct(ReferenceEqualityComparer.Instance).Count() == parent.OriginalDeviceAdmissions.Count &&
        parent.OriginalDeviceAdmissions.Select(fact => fact.InvocationId).Distinct().Count() == parent.OriginalDeviceAdmissions.Count;
}
