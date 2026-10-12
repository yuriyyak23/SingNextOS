using SingPlus.Contracts;
using SingPlus.Platform;
namespace SingPlus.Runtime;
public sealed partial class PlatformAuthorityBridge
{
    private sealed class OriginalMmioMapInvocation
    {
        public OriginalMmioMapInvocation(DeviceLeaseRecord source,DomainRecord parent,PlatformOriginalMmioMapRequest request,PlatformMmioLease lease,CapabilityId capability)
        { OwnerSelf = this; Source = source; Parent = parent; Request = request; Lease = lease; Capability = capability; DeviceFactory = source.OriginalBind; Revision = source.SourceRevision; ParentRevision = parent.AuthorityRevision; }
        public OriginalMmioMapInvocation OwnerSelf { get; }
        public DeviceLeaseRecord Source { get; }
        public DomainRecord Parent { get; }
        public OriginalDeviceBindInvocation? DeviceFactory { get; }
        public PlatformOriginalMmioMapRequest Request { get; }
        public PlatformMmioLease Lease { get; }
        public CapabilityId Capability { get; }
        public ulong Revision { get; }
        public long ParentRevision { get; }
        public bool Invoked { get; set; }
        public bool InFlight { get; set; } = true;
        public bool Published { get; set; }
        public bool NoEffectConsumed { get; set; }
        public bool NeverInvokedConsumed { get; set; }
        public PlatformAuthorityResult<PlatformProviderMmioLease>? RawObservation { get; set; }
        public PlatformAuthorityResult<PlatformOriginalMmioMapResolution>? RawResolution { get; set; }
        public MmioLeaseRecord? LocalMapping { get; set; }
    }
    private sealed class OriginalMmioCloseInvocation
    {
        public OriginalMmioCloseInvocation(MmioLeaseRecord source,DeviceLeaseRecord device,DomainRecord parent)
        { OwnerSelf = this; Source = source; Device = device; Parent = parent; Lease = source.ProviderLease; Revision = source.SourceRevision; DeviceRevision = device.SourceRevision; Epoch = device.BackendEpoch; Factory = source.OriginalMap; }
        public OriginalMmioCloseInvocation OwnerSelf { get; }
        public MmioLeaseRecord Source { get; }
        public DeviceLeaseRecord Device { get; }
        public DomainRecord Parent { get; }
        public OriginalMmioMapInvocation? Factory { get; }
        public PlatformProviderMmioLease Lease { get; }
        public ulong Revision { get; }
        public ulong DeviceRevision { get; }
        public PlatformBackendEpoch Epoch { get; }
        public bool Invoked { get; set; }
        public bool SourceChanged { get; set; }
        public bool Consumed { get; set; }
        public bool NoEffectConsumed { get; set; }
        public PlatformAuthorityResult? RawObservation { get; set; }
        public PlatformAuthorityResult? RawResolution { get; set; }
    }
    private bool OriginalMmioMapSources(OriginalMmioMapInvocation fact,PlatformProviderIncarnation incarnation) => ReferenceEquals(fact.OwnerSelf,fact) &&
        ReferenceEquals(fact.Source.OwnerSelf,fact.Source) && fact.Source.OriginalMmioMaps.Any(old => ReferenceEquals(old,fact)) && ReferenceEquals(fact.Source.OriginalBind,fact.DeviceFactory) &&
        TryGetOriginalDeviceRecord(fact.Lease.DeviceLease.LeaseId,out var device) && ReferenceEquals(device,fact.Source) && device.ProviderLease == fact.Request.Device &&
        device.SourceRevision == fact.Revision && !device.SourceExhausted && !device.PlatformClosed && !device.FaultPinned && !device.ClosureInFlight && device.OriginalClose is null &&
        TryGetCanonicalRootRecord(fact.Lease.DeviceLease.DomainBinding.BindingId,out var parent) && ReferenceEquals(parent,fact.Parent) && ReferenceEquals(device.BindParentSource,parent) &&
        parent.AuthorityState == DomainAuthorityState.Active && parent.AuthorityRevision == fact.ParentRevision && !parent.AuthorityRevisionExhausted && !parent.ParentRevokeInFlight && !parent.ParentRevokeMayHaveEffect &&
        incarnation.Value != 0 && incarnation == device.ProviderIncarnation && device.BackendEpoch == BackendEpoch && !_backendEpochExhausted;
    private static bool HasConsumedOriginalMmioClosure(MmioLeaseRecord record) => ReferenceEquals(record.OwnerSelf,record) && record.OriginalClose is { Invoked: true,Consumed: true,SourceChanged: false,NoEffectConsumed: false } fact &&
        ReferenceEquals(fact.OwnerSelf,fact) && ReferenceEquals(fact.Source,record) && ReferenceEquals(record.OriginalMap,fact.Factory) && ReferenceEquals(record.MapDeviceSource,fact.Device) && ReferenceEquals(record.MapParent,fact.Parent) &&
        fact.Lease == record.ProviderLease && record.CloseHistory.Any(old => ReferenceEquals(old,fact)) &&
        PlatformMmioLeaseContract.ValidateLease(fact.Device.ProviderLease,record.Lease.Region,record.Lease.Range,record.Lease.Access,fact.Lease).IsSuccess &&
        (fact.RawObservation is { IsSuccess: true } || fact.RawResolution is { IsSuccess: true });
    private static bool HasConsumedOriginalMmioMap(OriginalMmioMapInvocation fact) => ReferenceEquals(fact.OwnerSelf,fact) &&
        (fact.NeverInvokedConsumed && !fact.Invoked || fact.NoEffectConsumed && fact.RawResolution is { IsSuccess: true } raw && ReferenceEquals(raw.Value.Request,fact.Request) && raw.Value.NoEffectGuaranteed && raw.Value.CleanupLease is null ||
         fact.LocalMapping is { } mapping && ReferenceEquals(mapping.OriginalMap,fact) && HasConsumedOriginalMmioClosure(mapping));
    private IEnumerable<MmioLeaseRecord> MmioDependencyRecordsLocked() => _originalMmioOwners.Values.Concat(_mmioLeases.Values).Distinct();
    private void ConsumeOriginalMmioMapLocked(OriginalMmioMapInvocation fact)
    {
        if (!HasConsumedOriginalMmioMap(fact)) return;
        if (fact.Source.OriginalMmioMaps.Any(old => !old.Published && !HasConsumedOriginalMmioMap(old))) return;
        fact.Source.OwnMmioMapUncertain = false;
        fact.Source.MmioMapMayHaveEffect = false; fact.Source.UnresolvedMmioCapabilityId = null; fact.Source.MmioMapObservation = null;
        fact.Source.MmioMapParent = null; fact.Source.MmioMapRegion = null; fact.Source.MmioMapRange = null; fact.Source.MmioMapAccess = null;
        fact.Parent.MmioSources.Remove(fact.Source);
    }
    private KernelResult<PlatformMmioLease> BindOriginalMmio(PlatformDeviceLease lease,PlatformDomainIdentity subject,CapabilityId capability,PlatformMmioRegionIdentity region,PlatformMmioRange range,PlatformMmioAccess access,
        DeviceLeaseRecord device,DomainRecord parent,ulong localId,Func<Func<KernelResult>,KernelResult> authorize,Func<PlatformMmioLease,Func<KernelResult>,KernelResult> publish,IPlatformOriginalMmioProvider provider)
    {
        OriginalMmioMapInvocation fact = null!;
        var admission = authorize(() =>
        {
            lock (_secureDomainLifecycleGate)
            {
                var valid = ValidateDeviceLease(lease,subject); if (!valid.IsSuccess) return valid;
                if ((ulong)device.OriginalMmioMaps.Count != device.OriginalMmioMapsIssued || device.OriginalMmioMaps.Any(old => !ReferenceEquals(old.OwnerSelf,old))) return KernelResult.Fail(KernelError.PlatformFaulted,"Original MMIO history lost/copied; no repeat.");
                if (device.OriginalMmioMapsIssued == ulong.MaxValue) return KernelResult.Fail(KernelError.CapacityExhausted,"Original MMIO history exhausted before effect.");
                if (device.OriginalMmioMaps.Any(old => !HasConsumedOriginalMmioMap(old) && old.Lease.Region.ResourceId == region.ResourceId)) return KernelResult.Fail(KernelError.PlatformBindingActive,"Original MMIO lifetime already admitted/pending.");
                fact = new(device,parent,new(device.ProviderLease,region,range,access),new(new(localId),new(1),lease,region,range,access),capability);
                device.OriginalMmioMaps.Add(fact); device.OriginalMmioMapsIssued++; parent.MmioSources.Add(device); device.MmioMapMayHaveEffect = true; device.UnresolvedMmioCapabilityId = capability; device.MmioMapParent = parent;
                device.MmioMapRegion = region; device.MmioMapRange = range; device.MmioMapAccess = access; return KernelResult.Ok();
            }
        });
        if (!admission.IsSuccess) return KernelResult<PlatformMmioLease>.Fail(admission.Error,admission.Message!);
        try
        {
            var source = CurrentProviderIncarnation();
            lock (_secureDomainLifecycleGate) { if (!OriginalMmioMapSources(fact,source)) return Refuse("Original MMIO source changed before effect."); fact.Invoked = true; }
            var raw = provider.MapMmio(fact.Request);
            lock (_secureDomainLifecycleGate) { fact.RawObservation = raw; device.MmioMapObservation = raw; }
            source = CurrentProviderIncarnation();
            lock (_secureDomainLifecycleGate)
            {
                if (!OriginalMmioMapSources(fact,source) || !raw.IsSuccess || !PlatformMmioLeaseContract.ValidateLease(fact.Request.Device,region,range,access,raw.Value!).IsSuccess)
                    return Refuse("Original MMIO raw/source retained without publication.");
                var record = new MmioLeaseRecord(fact.Lease,raw.Value!,capability) { OriginalMap = fact,MapDeviceSource = device,MapParent = parent,MapObservation = raw };
                fact.LocalMapping = record; _originalMmioOwners.Add(record.Lease.LeaseId,record); // Original owner before publication callback.
            }
            var publication = publish(fact.Lease,() =>
            {
                lock (_secureDomainLifecycleGate)
                {
                    if (!OriginalMmioMapSources(fact,source) || !ReferenceEquals(fact.LocalMapping!.OwnerSelf,fact.LocalMapping)) return KernelResult.Fail(KernelError.PlatformFaulted,"Original MMIO source changed at publication.");
                    var valid = ValidateDeviceLease(lease,subject); if (!valid.IsSuccess) return valid;
                    _mmioLeases.Add(fact.Lease.LeaseId,fact.LocalMapping); TransferMmioMapObservationLocked(device,parent,fact.LocalMapping); fact.Published = true; return KernelResult.Ok();
                }
            });
            if (publication.IsSuccess) return KernelResult<PlatformMmioLease>.Ok(fact.Lease);
            return Refuse(publication.Message!);
        }
        catch (Exception exception) when (exception is not StackOverflowException) { return Refuse("Original MMIO Map retained without repeat: "+exception.Message); }
        finally { lock (_secureDomainLifecycleGate) fact.InFlight = false; }
        KernelResult<PlatformMmioLease> Refuse(string message)
        { lock (_secureDomainLifecycleGate) device.OwnMmioMapUncertain = true; return KernelResult<PlatformMmioLease>.Fail(KernelError.PlatformFaulted,message); }
    }
    internal KernelResult ResolveOriginalMmioMapsForCleanup(PlatformDomainIdentity subject)
    {
        OriginalMmioMapInvocation[] facts;
        lock (_secureDomainLifecycleGate)
        {
            var devices = DeviceDependencyRecordsLocked().Where(device => device.Lease.DomainBinding.Subject == subject).ToArray();
            if (devices.Any(device => (ulong)device.OriginalMmioMaps.Count != device.OriginalMmioMapsIssued || device.OriginalMmioMaps.Any(fact => !ReferenceEquals(fact.OwnerSelf,fact)))) return KernelResult.Fail(KernelError.PlatformFaulted,"Original MMIO history missing/copied; cleanup pinned.");
            facts = devices.SelectMany(device => device.OriginalMmioMaps).Where(fact => !fact.Published && !HasConsumedOriginalMmioMap(fact)).ToArray();
        }
        foreach (var fact in facts)
        {
            lock (_secureDomainLifecycleGate)
            {
                if (fact.InFlight || fact.Source.PendingMmioBinds != 0) return KernelResult.Fail(KernelError.PlatformBindingActive,"Original MMIO Map still in flight.");
                fact.InFlight = true; fact.Source.PendingMmioBinds++;
            }
            try
            {
                var source = CurrentProviderIncarnation();
                lock (_secureDomainLifecycleGate)
                {
                    if (!OriginalMmioMapSources(fact,source)) return KernelResult.Fail(KernelError.PlatformFaulted,"Original MMIO Map cleanup source unavailable.");
                    if (!fact.Invoked) { fact.NeverInvokedConsumed = true; ConsumeOriginalMmioMapLocked(fact); continue; }
                }
                if (_provider is not IPlatformOriginalMmioProvider provider) return KernelResult.Fail(KernelError.PlatformFaulted,"Original MMIO lookup unavailable.");
                var raw = fact.RawResolution is { IsSuccess: true } known ? known : provider.ResolveMmioMap(fact.Request);
                lock (_secureDomainLifecycleGate) fact.RawResolution = raw;
                source = CurrentProviderIncarnation();
                lock (_secureDomainLifecycleGate)
                {
                    if (!OriginalMmioMapSources(fact,source) || !raw.IsSuccess || !ReferenceEquals(raw.Value.Request,fact.Request) || raw.Value.NoEffectGuaranteed == raw.Value.CleanupLease.HasValue)
                        return KernelResult.Fail(KernelError.PlatformFaulted,"Original MMIO lookup missing/foreign/malformed; no repeat.");
                    if (raw.Value.NoEffectGuaranteed) { fact.NoEffectConsumed = true; ConsumeOriginalMmioMapLocked(fact); continue; }
                    var providerLease = raw.Value.CleanupLease!.Value;
                    if (!PlatformMmioLeaseContract.ValidateLease(fact.Request.Device,fact.Lease.Region,fact.Lease.Range,fact.Lease.Access,providerLease).IsSuccess) return KernelResult.Fail(KernelError.PlatformFaulted,"Original MMIO cleanup receipt malformed.");
                    if (fact.LocalMapping is null) fact.LocalMapping = new(fact.Lease,providerLease,fact.Capability) { OriginalMap = fact,MapDeviceSource = fact.Source,MapParent = fact.Parent,CleanupOnly = true,LocalAuthorizationRevoked = true };
                    var record = fact.LocalMapping;
                    if (!ReferenceEquals(record.OwnerSelf,record) || !ReferenceEquals(record.OriginalMap,fact) || record.ProviderLease != providerLease ||
                        _originalMmioOwners.TryGetValue(record.Lease.LeaseId,out var owner) && !ReferenceEquals(owner,record) || _mmioLeases.TryGetValue(record.Lease.LeaseId,out var route) && !ReferenceEquals(route,record)) return KernelResult.Fail(KernelError.PlatformFaulted,"Original MMIO partial adoption conflict.");
                    _originalMmioOwners.TryAdd(record.Lease.LeaseId,record); _mmioLeases.TryAdd(record.Lease.LeaseId,record); fact.Parent.MmioSources.Add(record);
                }
                var closed = RevokeMmio(fact.Lease,subject); if (!closed.IsSuccess) return closed;
                lock (_secureDomainLifecycleGate) ConsumeOriginalMmioMapLocked(fact);
            }
            catch (Exception exception) when (exception is not StackOverflowException) { return KernelResult.Fail(KernelError.PlatformFaulted,exception.Message); }
            finally { lock (_secureDomainLifecycleGate) { fact.InFlight = false; fact.Source.PendingMmioBinds--; } }
        }
        return KernelResult.Ok();
    }
    private KernelResult RevokeOriginalMmio(PlatformMmioLease lease,PlatformDomainIdentity subject,MmioLeaseRecord record)
    {
        OriginalMmioCloseInvocation fact;
        lock (_secureDomainLifecycleGate)
        {
            var valid = ValidateMmioLeaseIdentity(lease,subject); if (!valid.IsSuccess) return valid;
            if (!record.ClosureInFlight || record.FaultPinned || record.PlatformClosed || record.MapDeviceSource is not { } device || record.MapParent is not { } parent) return KernelResult.Fail(KernelError.PlatformFaulted,"Original MMIO source unavailable.");
            fact = record.OriginalClose ?? new(record,device,parent); if (record.OriginalClose is null) { record.OriginalClose = fact; record.CloseHistory.Add(fact); }
            if (!Exact() || fact.Invoked && fact.RawObservation is not { IsSuccess: true } && _provider is not IPlatformOriginalMmioProvider && !(_provider is IPlatformMmioRevocationNoEffectProvider && fact.RawObservation is { Status: PlatformAuthorityStatus.NotAccepted })) return KernelResult.Fail(KernelError.PlatformFaulted,"Original MMIO Close unknown; no repeat.");
        }
        if (_provider is not IPlatformMmioLeaseProvider provider) return KernelResult.Fail(KernelError.PlatformFaulted,"Original MMIO provider unavailable.");
        try
        {
            var source = CurrentProviderIncarnation(); bool invoke;
            lock (_secureDomainLifecycleGate) { if (!Current(source)) return Fail("Original MMIO source changed before Close."); invoke = !fact.Invoked; if (invoke) { fact.Invoked = true; record.RevokeMayHaveEffect = true; record.RevokeParent = fact.Parent; } }
            if (invoke)
            {
                var raw = provider.RevokeMmio(fact.Lease);
                lock (_secureDomainLifecycleGate) { fact.RawObservation = raw; record.RevokeObservation = raw; if (raw.IsSuccess) record.ProviderClosureConfirmed = true; }
            }
            else if (fact.RawObservation is not { IsSuccess: true } && !(provider is IPlatformMmioRevocationNoEffectProvider && fact.RawObservation is { Status: PlatformAuthorityStatus.NotAccepted }))
            {
                var raw = ((IPlatformOriginalMmioProvider)provider).ResolveMmioRevocation(fact.Lease); lock (_secureDomainLifecycleGate) fact.RawResolution = raw;
            }
            source = CurrentProviderIncarnation();
            lock (_secureDomainLifecycleGate)
            {
                if (!Current(source)) return Fail("Original MMIO outcome retained without final source.");
                if (provider is IPlatformMmioRevocationNoEffectProvider && fact.RawObservation is { Status: PlatformAuthorityStatus.NotAccepted } noEffect)
                { fact.NoEffectConsumed = true; record.OriginalClose = null; record.RevokeMayHaveEffect = false; record.OwnCloseUncertain = false; return FromProviderFailure(noEffect.Status,noEffect.Message); }
                if (fact.RawObservation is not { IsSuccess: true } && fact.RawResolution is not { IsSuccess: true }) return Fail("Original MMIO Close reply unknown; no repeat.");
                fact.Consumed = true; record.ProviderClosureConfirmed = true; record.OwnCloseUncertain = false; record.LocalAuthorizationRevoked = true; record.PlatformClosed = true; fact.Parent.MmioSources.Remove(record);
                return KernelResult.Ok();
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_secureDomainLifecycleGate) if (fact.RawObservation is not { IsSuccess: true } || !Owners() || BackendEpoch != fact.Epoch || _backendEpochExhausted) return Fail(exception.Message);
            return KernelResult.Fail(KernelError.PlatformFaulted,"Known original MMIO closure retained while source read failed: "+exception.Message);
        }
        bool Exact() => ReferenceEquals(record.OwnerSelf,record) && ReferenceEquals(record.OriginalClose,fact) && ReferenceEquals(fact.OwnerSelf,fact) && ReferenceEquals(fact.Source,record) && ReferenceEquals(record.OriginalMap,fact.Factory) &&
            record.CloseHistory.Any(old => ReferenceEquals(old,fact)) && fact.Lease == record.ProviderLease;
        bool Owners() => Exact() && _mmioLeases.TryGetValue(lease.LeaseId,out var route) && ReferenceEquals(route,record) && _originalMmioOwners.TryGetValue(lease.LeaseId,out var owner) && ReferenceEquals(owner,record) &&
            TryGetOriginalDeviceRecord(lease.DeviceLease.LeaseId,out var device) && ReferenceEquals(device,fact.Device) && HasExactChildCleanupDomainLocked(device,fact.Parent) && ReferenceEquals(record.MapDeviceSource,device) && ReferenceEquals(record.MapParent,fact.Parent) && fact.Parent.MmioSources.Contains(record);
        bool Current(PlatformProviderIncarnation source)
        {
            if (source.Value != 0 && source != fact.Device.ProviderIncarnation || record.SourceRevision != fact.Revision || fact.Device.SourceRevision != fact.DeviceRevision || BackendEpoch != fact.Epoch || record.SourceExhausted || fact.Device.SourceExhausted) fact.SourceChanged = true;
            return !fact.SourceChanged && source.Value != 0 && source == fact.Device.ProviderIncarnation && !_backendEpochExhausted && Owners() && !fact.Device.FaultPinned && !fact.Device.PlatformClosed && !fact.Device.ClosureInFlight &&
                record.ClosureInFlight && !record.FaultPinned && !record.PlatformClosed && PlatformMmioLeaseContract.ValidateLease(fact.Device.ProviderLease,lease.Region,lease.Range,lease.Access,fact.Lease).IsSuccess &&
                (record.MapObservation is { IsSuccess: true } raw && raw.Value == fact.Lease || record.OriginalMap?.RawResolution is { IsSuccess: true } resolution && ReferenceEquals(resolution.Value.Request,record.OriginalMap.Request) && !resolution.Value.NoEffectGuaranteed && resolution.Value.CleanupLease == fact.Lease);
        }
        KernelResult Fail(string message)
        {
            if (provider is IPlatformOriginalMmioProvider) record.OwnCloseUncertain = true;
            else { record.FaultPinned = true; fact.Device.FaultPinned = true; }
            return KernelResult.Fail(KernelError.PlatformFaulted,message);
        }
    }
    internal bool IsOriginalMmioClosureConsumed(PlatformMmioLease lease,PlatformDomainIdentity subject)
    { lock (_secureDomainLifecycleGate) return lease.DeviceLease.DomainBinding.Subject == subject && _originalMmioOwners.Values.Count(owner => owner.Lease == lease && HasConsumedOriginalMmioClosure(owner)) == 1; }
}
