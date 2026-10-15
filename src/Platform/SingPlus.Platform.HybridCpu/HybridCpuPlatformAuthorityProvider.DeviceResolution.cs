using YAKSys_Hybrid_CPU.Core;
namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider
{
    public PlatformAuthorityResult<PlatformOriginalDeviceBindResolution> ResolveDeviceBind(PlatformOriginalDeviceBindRequest request)
    {
        DeviceBindFailure fact;
        lock (_childCreateCommitGate)
        {
            if (!_originalRootBinds.TryGetValue(request.Domain.LeaseId,out var root) || root.OriginalDomain is not { } parent)
                return PlatformAuthorityResult<PlatformOriginalDeviceBindResolution>.Fail(PlatformAuthorityStatus.Faulted,"Original device parent unavailable.");
            var history = parent.OriginalDeviceAdmissions.Where(old => ReferenceEquals(old.OriginalRequest,request)).ToArray();
            if (history.Length != 1 || !ReferenceEquals(history[0].OwnerSelf,history[0]) || parent.DeviceBindInFlight)
                return PlatformAuthorityResult<PlatformOriginalDeviceBindResolution>.Fail(PlatformAuthorityStatus.Faulted,"Original device request absent/foreign/in flight.");
            fact = history[0];
        }
        var fresh = ValidateOriginalNativeMappingSource();
        if (!fresh.IsSuccess) return PlatformAuthorityResult<PlatformOriginalDeviceBindResolution>.Fail(fresh.Status,fresh.Message!);
        if (fact.NativeObservation is null && fact.NativeRequest is { } nativeRequest && _runtime is INeutralOriginalDeviceProvider original)
        {
            var raw = original.ResolveDeviceBind(nativeRequest);
            lock (_childCreateCommitGate) { fact.NativeObservation = raw; if (raw is { } value) fact.Observation = value.Observation; }
        }
        var after = ValidateOriginalNativeMappingSource();
        if (!after.IsSuccess) return PlatformAuthorityResult<PlatformOriginalDeviceBindResolution>.Fail(after.Status,after.Message!);
        lock (_childCreateCommitGate)
        {
            if (!ReferenceEquals(fact.OwnerSelf,fact) || !ReferenceEquals(fact.OriginalRequest,request) || fact.Domain != request.Domain || fact.Device != request.Device || fact.Rights != request.Rights ||
                !_domains.TryGetValue(request.Domain.LeaseId,out var parent) || !ReferenceEquals(parent,fact.Parent) || !ReferenceEquals(parent.BindSource,fact.ParentFactory) ||
                !parent.OriginalDeviceAdmissions.Any(old => ReferenceEquals(old,fact)) || fact.NativeRequest is not { } expected || fact.NativeObservation is not { } raw || raw.Request != expected ||
                raw.Observation is null || !Enum.IsDefined(raw.Observation.Decision) || !raw.SourceFenceMatched && !raw.NoEffectGuaranteed)
                return PlatformAuthorityResult<PlatformOriginalDeviceBindResolution>.Fail(PlatformAuthorityStatus.Faulted,"Original device outcome/source unknown.");
            if (raw.NoEffectGuaranteed && !raw.Observation.IsBound && raw.Observation.Decision!=NeutralDeviceBindDecision.Bound && raw.Observation.Lease == default && fact.OriginalLease is null)
            { fact.NoEffectConsumed = true; if (ReferenceEquals(parent.FailedDeviceBind,fact)) parent.FailedDeviceBind = null; return PlatformAuthorityResult<PlatformOriginalDeviceBindResolution>.Ok(new(request,true,null)); }
            var native = raw.Observation.Lease;
            if (raw.NoEffectGuaranteed || !raw.Observation.IsBound || raw.Observation.Decision != NeutralDeviceBindDecision.Bound || native.Handle.Value == 0 || native.Epoch.Value == 0 ||
                native.DomainLease != parent.HybridCpuLease || native.Device != expected.Device || native.Rights != expected.Rights)
                return PlatformAuthorityResult<PlatformOriginalDeviceBindResolution>.Fail(PlatformAuthorityStatus.Faulted,"Original device cleanup receipt malformed.");
            var owner = fact.OriginalLease;
            if (owner is null)
            {
                var lease = new PlatformProviderDeviceLease(fact.InvocationId,new(1),request.Domain,request.Device,request.Rights);
                owner = new(lease,native) { BindParent = parent, AdmissionSource = fact, CleanupOnly = true };
                fact.OriginalLease = owner; _originalDeviceLeases.Add(lease.LeaseId,owner); _deviceLeases.Add(lease.LeaseId,owner);
            }
            if (!ReferenceEquals(owner.OwnerSelf,owner) || !ReferenceEquals(owner.AdmissionSource,fact) || !_originalDeviceLeases.TryGetValue(owner.Lease.LeaseId,out var originalOwner) || !ReferenceEquals(originalOwner,owner) ||
                !_deviceLeases.TryGetValue(owner.Lease.LeaseId,out var routed) || !ReferenceEquals(routed,owner))
                return PlatformAuthorityResult<PlatformOriginalDeviceBindResolution>.Fail(PlatformAuthorityStatus.Faulted,"Original device cleanup route is foreign.");
            if (ReferenceEquals(parent.FailedDeviceBind,fact)) parent.FailedDeviceBind = null;
            return PlatformAuthorityResult<PlatformOriginalDeviceBindResolution>.Ok(new(request,false,owner.Lease)); // Cleanup only, never device admission.
        }
    }

    private static bool HasConsumedOriginalProviderDevice(DeviceLeaseRecord record) => ReferenceEquals(record.OwnerSelf,record) &&
        (record.NativeClose is { } close && ReferenceEquals(close.OwnerSelf,close) && ReferenceEquals(close.Source,record) && record.CloseHistory.Any(old => ReferenceEquals(old,close)) &&
         close.Observation is { SourceFenceMatched: true, NoEffectGuaranteed: false } raw && raw.Request == close.Request && raw.Observation.Decision == NeutralDeviceCloseDecision.Closed &&
         close.Request.Device == record.HybridCpuLease && close.Request.DeviceBindInvocationId == record.AdmissionSource?.NativeRequest?.InvocationId ||
         record.AdmissionSource?.NativeRequest is null && record.Revoked && record.CloseObservation is { Decision: NeutralDeviceCloseDecision.Closed });
}
