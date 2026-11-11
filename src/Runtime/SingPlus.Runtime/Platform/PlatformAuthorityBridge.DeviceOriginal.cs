using SingPlus.Contracts;
using SingPlus.Platform;
namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    private sealed class OriginalDeviceBindInvocation
    {
        public OriginalDeviceBindInvocation(DomainRecord source,PlatformOriginalDeviceBindRequest request,PlatformDeviceLease lease,CapabilityId capability)
        { OwnerSelf = this; Source = source; Request = request; Lease = lease; Capability = capability; Revision = source.AuthorityRevision; }
        public OriginalDeviceBindInvocation OwnerSelf { get; }
        public DomainRecord Source { get; }
        public PlatformOriginalDeviceBindRequest Request { get; }
        public PlatformDeviceLease Lease { get; }
        public CapabilityId Capability { get; }
        public long Revision { get; }
        public bool InFlight { get; set; } = true;
        public bool Invoked { get; set; }
        public bool Published { get; set; }
        public bool NoEffectConsumed { get; set; }
        public bool NeverInvokedConsumed { get; set; }
        public bool OwnFault { get; set; }
        public long FaultRevision { get; set; }
        public PlatformAuthorityResult<PlatformProviderDeviceLease>? RawObservation { get; set; }
        public PlatformAuthorityResult<PlatformOriginalDeviceBindResolution>? RawResolution { get; set; }
        public DeviceLeaseRecord? LocalDevice { get; set; }
    }
    private sealed class OriginalDeviceCloseInvocation
    {
        public OriginalDeviceCloseInvocation(DeviceLeaseRecord source,DomainRecord parent)
        { OwnerSelf = this; Source = source; Parent = parent; Lease = source.ProviderLease; Revision = source.SourceRevision; Epoch = source.BackendEpoch; }
        public OriginalDeviceCloseInvocation OwnerSelf { get; }
        public DeviceLeaseRecord Source { get; }
        public DomainRecord Parent { get; }
        public PlatformProviderDeviceLease Lease { get; }
        public ulong Revision { get; }
        public PlatformBackendEpoch Epoch { get; }
        public bool Invoked { get; set; }
        public bool SourceChanged { get; set; }
        public bool Consumed { get; set; }
        public bool NoEffectConsumed { get; set; }
        public PlatformAuthorityResult? RawObservation { get; set; }
        public PlatformAuthorityResult? RawResolution { get; set; }
    }
    private bool OriginalDeviceBindSources(OriginalDeviceBindInvocation fact,PlatformProviderIncarnation incarnation) =>
        ReferenceEquals(fact.OwnerSelf,fact) && fact.Source.OriginalDeviceBinds.Any(old => ReferenceEquals(old,fact)) &&
        TryGetCanonicalRootRecord(fact.Source.Binding.BindingId,out DomainRecord? root) && ReferenceEquals(root,fact.Source) && fact.Lease.DomainBinding == root.Binding &&
        fact.Request.Domain == root.ProviderLease && fact.Request.Device == fact.Lease.Device && fact.Request.Rights == fact.Lease.Rights &&
        incarnation.Value != 0 && incarnation == root.ProviderIncarnation && root.BackendEpoch == BackendEpoch && !_backendEpochExhausted &&
        !root.ParentRevokeInFlight && !root.ParentRevokeMayHaveEffect && !root.AuthorityRevisionExhausted &&
        (root.AuthorityState == DomainAuthorityState.Active && root.AuthorityRevision == fact.Revision || fact.OwnFault && root.AuthorityState == DomainAuthorityState.Quarantined && root.AuthorityRevision == fact.FaultRevision);
    private void QuarantineOriginalDeviceBindLocked(DomainRecord root,OriginalDeviceBindInvocation? fact)
    {
        bool own = fact is not null && ReferenceEquals(fact.Source,root) && ReferenceEquals(fact.OwnerSelf,fact) &&
            root.OriginalDeviceBinds.Any(old => ReferenceEquals(old,fact)) && root.AuthorityState == DomainAuthorityState.Active && root.AuthorityRevision == fact.Revision;
        if (fact is { OwnFault: true } && root.AuthorityState == DomainAuthorityState.Quarantined && root.AuthorityRevision == fact.FaultRevision) return;
        QuarantineDomain(root);
        if (own) { fact!.OwnFault = true; fact.FaultRevision = root.AuthorityRevision; }
    }
    private static bool HasConsumedOriginalDeviceClosure(DeviceLeaseRecord record) => ReferenceEquals(record.OwnerSelf,record) &&
        record.OriginalClose is { Invoked: true, Consumed: true } fact && ReferenceEquals(fact.OwnerSelf,fact) && ReferenceEquals(fact.Source,record) &&
        !fact.SourceChanged && !fact.NoEffectConsumed && fact.Lease == record.ProviderLease && ReferenceEquals(record.BindParentSource,fact.Parent) &&
        PlatformDeviceLeaseContract.ValidateLease(fact.Parent.ProviderLease,record.Lease.Device,record.Lease.Rights,fact.Lease).IsSuccess &&
        record.CloseHistory.Any(old => ReferenceEquals(old,fact)) && (fact.RawObservation is { IsSuccess: true } || fact.RawResolution is { IsSuccess: true });
    private static bool HasConsumedOriginalDeviceBind(OriginalDeviceBindInvocation fact) => ReferenceEquals(fact.OwnerSelf,fact) &&
        (fact.NeverInvokedConsumed && !fact.Invoked || fact.NoEffectConsumed &&
            (fact.RawObservation is { Status: PlatformAuthorityStatus.NotAccepted } || fact.RawResolution is { IsSuccess: true } raw && ReferenceEquals(raw.Value.Request,fact.Request) && raw.Value.NoEffectGuaranteed && raw.Value.CleanupLease is null) ||
         fact.LocalDevice is { } device && ReferenceEquals(device.OriginalBind,fact) && HasConsumedOriginalDeviceClosure(device));
    private void ConsumeOriginalDeviceBindLocked(OriginalDeviceBindInvocation fact)
    {
        DomainRecord root = fact.Source;
        if (!HasConsumedOriginalDeviceBind(fact)) return;
        if (fact.OwnFault && root.AuthorityState == DomainAuthorityState.Quarantined && root.AuthorityRevision == fact.FaultRevision) root.AuthorityState = DomainAuthorityState.Active;
        root.DeviceBindMayHaveEffect = root.OriginalDeviceBinds.Any(old => !old.Published && !HasConsumedOriginalDeviceBind(old));
        if (!root.DeviceBindMayHaveEffect)
        { root.DeviceBindObservation = null; root.DeviceBindCapabilityId = null; root.DeviceBindDevice = null; root.DeviceBindRights = null; root.DeviceBindIncarnation = null; root.DeviceBindEpoch = null; root.DeviceBindSource = null; }
    }
    internal KernelResult ResolveOriginalDeviceBindsForCleanup(PlatformDomainIdentity subject)
    {
        OriginalDeviceBindInvocation[] facts;
        lock (_secureDomainLifecycleGate) facts = _domains.Values.Where(root => root.Binding.Subject == subject).SelectMany(root => root.OriginalDeviceBinds).Where(fact => !fact.Published && !HasConsumedOriginalDeviceBind(fact)).ToArray();
        foreach (OriginalDeviceBindInvocation fact in facts)
        {
            lock (_secureDomainLifecycleGate)
            {
                if (fact.InFlight || fact.Source.PendingDeviceBinds != 0) return KernelResult.Fail(KernelError.PlatformBindingActive,"Original device Bind is still in flight.");
                fact.InFlight = true; fact.Source.PendingDeviceBinds++;
            }
            try
            {
                PlatformProviderIncarnation source = CurrentProviderIncarnation();
                lock (_secureDomainLifecycleGate)
                {
                    if (!OriginalDeviceBindSources(fact,source)) return KernelResult.Fail(KernelError.PlatformFaulted,"Original device Bind cleanup source unavailable.");
                    if (!fact.Invoked) { fact.NeverInvokedConsumed = true; ConsumeOriginalDeviceBindLocked(fact); continue; }
                }
                if (_provider is not IPlatformOriginalDeviceProvider original) return KernelResult.Fail(KernelError.PlatformFaulted,"Original device Bind lookup unavailable.");
                PlatformAuthorityResult<PlatformOriginalDeviceBindResolution> raw = fact.RawResolution is { IsSuccess: true } retained ? retained : original.ResolveDeviceBind(fact.Request);
                lock (_secureDomainLifecycleGate) fact.RawResolution = raw; // Before source callbacks.
                source = CurrentProviderIncarnation();
                lock (_secureDomainLifecycleGate)
                {
                    if (!OriginalDeviceBindSources(fact,source) || !raw.IsSuccess || !ReferenceEquals(raw.Value.Request,fact.Request) || raw.Value.NoEffectGuaranteed == raw.Value.CleanupLease.HasValue)
                        return KernelResult.Fail(KernelError.PlatformFaulted,"Original device Bind resolution missing/foreign/malformed; no repeat.");
                    if (raw.Value.NoEffectGuaranteed) { fact.NoEffectConsumed = true; ConsumeOriginalDeviceBindLocked(fact); continue; }
                    PlatformProviderDeviceLease lease = raw.Value.CleanupLease!.Value;
                    if (!PlatformDeviceLeaseContract.ValidateLease(fact.Request.Domain,fact.Request.Device,fact.Request.Rights,lease).IsSuccess)
                        return KernelResult.Fail(KernelError.PlatformFaulted,"Original device cleanup receipt does not refine request.");
                    if (fact.LocalDevice is null)
                    {
                        DeviceLeaseRecord device = new DeviceLeaseRecord(fact.Lease,lease,fact.Capability,fact.Source.ProviderIncarnation,fact.Source.BackendEpoch)
                            { OriginalBind = fact, BindParentSource = fact.Source, CleanupOnly = true, LocalAuthorizationRevoked = true };
                        fact.LocalDevice = device; _originalDeviceOwners.Add(device.Lease.LeaseId,device); _deviceLeases.Add(device.Lease.LeaseId,device);
                    }
                    DeviceLeaseRecord adopted = fact.LocalDevice!;
                    if (!ReferenceEquals(adopted.OwnerSelf,adopted) || !ReferenceEquals(adopted.OriginalBind,fact) || adopted.ProviderLease != lease ||
                        _originalDeviceOwners.TryGetValue(adopted.Lease.LeaseId,out DeviceLeaseRecord? owner) && !ReferenceEquals(owner,adopted) ||
                        _deviceLeases.TryGetValue(adopted.Lease.LeaseId,out DeviceLeaseRecord? route) && !ReferenceEquals(route,adopted))
                        return KernelResult.Fail(KernelError.PlatformFaulted,"Original device partial adoption owner conflict.");
                    _originalDeviceOwners.TryAdd(adopted.Lease.LeaseId,adopted); _deviceLeases.TryAdd(adopted.Lease.LeaseId,adopted);
                }
                KernelResult closed = RevokeDevice(fact.LocalDevice.Lease,subject);
                if (!closed.IsSuccess) return closed;
                lock (_secureDomainLifecycleGate) ConsumeOriginalDeviceBindLocked(fact);
            }
            catch (Exception exception) when (exception is not StackOverflowException) { return KernelResult.Fail(KernelError.PlatformFaulted,exception.Message); }
            finally { lock (_secureDomainLifecycleGate) { fact.InFlight = false; fact.Source.PendingDeviceBinds--; } }
        }
        return KernelResult.Ok();
    }
    internal bool IsOriginalDeviceClosureConsumed(PlatformDeviceLease lease,PlatformDomainIdentity subject)
    { lock (_secureDomainLifecycleGate) return lease.DomainBinding.Subject == subject && _originalDeviceOwners.Values.Count(owner => owner.Lease == lease && HasConsumedOriginalDeviceClosure(owner)) == 1; }
}
