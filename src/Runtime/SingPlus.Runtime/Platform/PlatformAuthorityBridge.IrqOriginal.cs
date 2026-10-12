using SingPlus.Contracts;
using SingPlus.Platform;
namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    private sealed class OriginalIrqBindInvocation
    {
        public OriginalIrqBindInvocation(DeviceLeaseRecord device,DomainRecord parent,PlatformOriginalIrqBindRequest request,PlatformIrqBinding binding,
            CapabilityId capability,KernelEventRegistry.IrqSourceReservation endpoint)
        { OwnerSelf = this; Device = device; Parent = parent; Request = request; Binding = binding; Capability = capability; Endpoint = endpoint; DeviceRevision = device.SourceRevision; ParentRevision = parent.AuthorityRevision; DeviceFactory = device.OriginalBind; }
        public OriginalIrqBindInvocation OwnerSelf { get; }
        public DeviceLeaseRecord Device { get; }
        public DomainRecord Parent { get; }
        public PlatformOriginalIrqBindRequest Request { get; }
        public PlatformIrqBinding Binding { get; }
        public CapabilityId Capability { get; }
        public KernelEventRegistry.IrqSourceReservation Endpoint { get; }
        public ulong DeviceRevision { get; }
        public long ParentRevision { get; }
        public OriginalDeviceBindInvocation? DeviceFactory { get; }
        public bool InFlight { get; set; } = true;
        public bool Invoked { get; set; }
        public bool Published { get; set; }
        public bool NoEffectConsumed { get; set; }
        public PlatformAuthorityResult<PlatformProviderIrqBinding>? Raw { get; set; }
        public PlatformAuthorityResult<PlatformOriginalIrqBindResolution>? Resolution { get; set; }
        public IrqBindingRecord? LocalBinding { get; set; }
    }
    private bool OriginalIrqBindSources(OriginalIrqBindInvocation fact,PlatformProviderIncarnation incarnation) =>
        ReferenceEquals(fact.OwnerSelf,fact) && fact.Device.OriginalIrqBinds.Any(old => ReferenceEquals(old,fact)) &&
        (ulong)fact.Device.OriginalIrqBinds.Count == fact.Device.OriginalIrqBindsIssued &&
        TryGetOriginalDeviceRecord(fact.Binding.DeviceLease.LeaseId,out var device) && ReferenceEquals(device,fact.Device) &&
        TryGetCanonicalRootRecord(fact.Binding.DeviceLease.DomainBinding.BindingId,out var parent) && ReferenceEquals(parent,fact.Parent) && ReferenceEquals(device.BindParentSource,parent) &&
        !device.PlatformClosed && !device.FaultPinned && !device.SourceExhausted && device.SourceRevision == fact.DeviceRevision && ReferenceEquals(device.OriginalBind,fact.DeviceFactory) && !device.ClosureInFlight && device.OriginalClose is null &&
        parent.AuthorityState == DomainAuthorityState.Active && parent.AuthorityRevision == fact.ParentRevision && !parent.AuthorityRevisionExhausted && !parent.ParentRevokeInFlight && !parent.ParentRevokeMayHaveEffect &&
        incarnation.Value != 0 && incarnation == device.ProviderIncarnation && device.BackendEpoch == BackendEpoch && !_backendEpochExhausted;
    private static bool HasConsumedOriginalIrqBind(OriginalIrqBindInvocation fact) => ReferenceEquals(fact.OwnerSelf,fact) &&
        (fact.NoEffectConsumed || fact.LocalBinding is { PlatformClosed: true } child && ReferenceEquals(child.OwnerSelf,child) && ReferenceEquals(child.OriginalBind,fact) && child.ProviderClosureConfirmed);
    private void ConsumeOriginalIrqBindLocked(OriginalIrqBindInvocation fact)
    {
        if (!HasConsumedOriginalIrqBind(fact) || fact.Device.OriginalIrqBinds.Any(old => !old.Published && !HasConsumedOriginalIrqBind(old))) return;
        fact.Device.OwnIrqBindUncertain = false;
        fact.Device.UnresolvedIrqCapabilityId = null; fact.Device.IrqBindMayHaveEffect = false; fact.Device.IrqBindObservation = null;
        fact.Device.IrqBindParent = null; fact.Device.IrqBindSource = null; fact.Device.IrqBindEndpointSource = null;
        fact.Parent.IrqSources.Remove(fact.Device);
    }
    private KernelResult<PlatformIrqBinding> BindOriginalIrq(PlatformDeviceLease lease,PlatformDomainIdentity subject,CapabilityId capability,PlatformInterruptSourceIdentity source,KernelEventEndpoint endpoint,
        DeviceLeaseRecord device,DomainRecord parent,ulong id,KernelEventRegistry.IrqSourceReservation reservation,Func<KernelResult> authorize,
        Func<PlatformIrqBinding,Func<KernelResult>,KernelResult> publish,IPlatformOriginalIrqAdmissionProvider provider)
    {
        OriginalIrqBindInvocation? admittedFact = null;
        lock (_secureDomainLifecycleGate)
        {
            if ((ulong)device.OriginalIrqBinds.Count == device.OriginalIrqBindsIssued && device.OriginalIrqBinds.All(old => ReferenceEquals(old.OwnerSelf,old)) &&
                device.OriginalIrqBindsIssued != ulong.MaxValue && !device.OriginalIrqBinds.Any(old => !HasConsumedOriginalIrqBind(old) && old.Binding.Source == source))
            {
                admittedFact = new(device,parent,new(device.ProviderLease,source),new(new(id),new(1),lease,source,endpoint),capability,reservation);
                device.OriginalIrqBinds.Add(admittedFact); device.OriginalIrqBindsIssued++; parent.IrqSources.Add(device);
                device.UnresolvedIrqCapabilityId = capability; device.IrqBindMayHaveEffect = true; device.IrqBindParent = parent; device.IrqBindSource = source; device.IrqBindEndpointSource = reservation;
            }
        }
        if (admittedFact is null)
        { _ = reservation.CommitRelease(device,static () => KernelResult.Ok()); return KernelResult<PlatformIrqBinding>.Fail(KernelError.PlatformFaulted,"Original IRQ history lost/exhausted or lifetime already admitted."); }
        var fact = admittedFact;
        try
        {
            var permission = authorize(); if (!permission.IsSuccess) return Refuse(permission.Message!);
            var incarnation = CurrentProviderIncarnation();
            lock (_secureDomainLifecycleGate)
            { if (!OriginalIrqBindSources(fact,incarnation) || !ValidateDeviceLease(lease,subject).IsSuccess) return Refuse("Original IRQ source changed before Bind."); fact.Invoked = true; }
            var raw = provider.BindInterrupt(fact.Request);
            lock (_secureDomainLifecycleGate) { fact.Raw = raw; device.IrqBindObservation = raw; }
            incarnation = CurrentProviderIncarnation();
            lock (_secureDomainLifecycleGate)
            {
                if (!OriginalIrqBindSources(fact,incarnation) || !raw.IsSuccess || !PlatformIrqBindingContract.ValidateBinding(device.ProviderLease,source,raw.Value!).IsSuccess)
                    return Refuse("Original IRQ raw/source retained without publication.");
                var child = new IrqBindingRecord(fact.Binding,raw.Value!,capability) { OriginalBind = fact,EndpointSource = reservation,BindObservation = raw,BindDeviceSource = device,BindParentSource = parent };
                child.OwnerSelf = child; fact.LocalBinding = child; _originalIrqOwners.Add(child.Binding.BindingId,child); parent.IrqSources.Add(child);
            }
            reservation.TransferSource(device,fact.LocalBinding!);
            var published = publish(fact.Binding,() =>
            {
                lock (_secureDomainLifecycleGate)
                {
                    if (!OriginalIrqBindSources(fact,incarnation) || !ValidateDeviceLease(lease,subject).IsSuccess || !ReferenceEquals(fact.LocalBinding!.OwnerSelf,fact.LocalBinding))
                        return KernelResult.Fail(KernelError.PlatformFaulted,"Original IRQ source changed at final publication.");
                    _irqBindings.Add(fact.Binding.BindingId,fact.LocalBinding); fact.Published = true; TransferIrqBindObservationLocked(device,fact.LocalBinding); parent.IrqSources.Remove(device); return KernelResult.Ok();
                }
            });
            return published.IsSuccess ? KernelResult<PlatformIrqBinding>.Ok(fact.Binding) : Refuse(published.Message!);
        }
        catch (Exception exception) when (exception is not StackOverflowException) { return Refuse(exception.Message); }
        finally { lock (_secureDomainLifecycleGate) fact.InFlight = false; }
        KernelResult<PlatformIrqBinding> Refuse(string message)
        { lock (_secureDomainLifecycleGate) device.OwnIrqBindUncertain = true; return KernelResult<PlatformIrqBinding>.Fail(KernelError.PlatformFaulted,message); }
    }
    internal KernelResult ResolveOriginalIrqBindsForCleanup(PlatformDomainIdentity subject) => ResolveOriginalIrqBindsForCleanupCore(subject,null);

    internal KernelResult ResolveOriginalIrqBindsForCapabilityCleanup(Func<CapabilityId,bool> dependsOnRevokedCapability)
    {
        OriginalIrqBindInvocation[] snapshot;
        lock (_secureDomainLifecycleGate)
        {
            var devices = DeviceDependencyRecordsLocked().ToArray();
            if (devices.Any(device => (ulong)device.OriginalIrqBinds.Count != device.OriginalIrqBindsIssued || device.OriginalIrqBinds.Any(fact => !ReferenceEquals(fact.OwnerSelf,fact))))
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original IRQ history lost/copied; capability cleanup pinned.");
            snapshot = devices.SelectMany(device => device.OriginalIrqBinds)
                .Where(fact => !fact.Published && !HasConsumedOriginalIrqBind(fact)).ToArray();
        }
        // Capability ownership is inspected outside the bridge commit lock. The
        // existing original fact is revalidated again by the cleanup consumer.
        var selected = snapshot.Where(fact => dependsOnRevokedCapability(fact.Capability)).ToArray();
        foreach (var group in selected.GroupBy(fact => fact.Binding.DeviceLease.DomainBinding.Subject))
        {
            var result = ResolveOriginalIrqBindsForCleanupCore(group.Key,group.ToHashSet());
            if (!result.IsSuccess) return result;
        }
        return KernelResult.Ok();
    }

    private KernelResult ResolveOriginalIrqBindsForCleanupCore(PlatformDomainIdentity subject,IReadOnlySet<OriginalIrqBindInvocation>? selected)
    {
        OriginalIrqBindInvocation[] facts;
        lock (_secureDomainLifecycleGate)
        {
            var devices = DeviceDependencyRecordsLocked().Where(device => device.Lease.DomainBinding.Subject == subject).ToArray();
            if (devices.Any(device => (ulong)device.OriginalIrqBinds.Count != device.OriginalIrqBindsIssued || device.OriginalIrqBinds.Any(f => !ReferenceEquals(f.OwnerSelf,f))))
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original IRQ history lost/copied; cleanup pinned.");
            facts = devices.SelectMany(device => device.OriginalIrqBinds).Where(f => !f.Published && !HasConsumedOriginalIrqBind(f) && (selected is null || selected.Contains(f))).ToArray();
        }
        foreach (var fact in facts)
        {
            lock (_secureDomainLifecycleGate)
            { if (fact.InFlight || fact.Device.PendingIrqBinds != 0) return KernelResult.Fail(KernelError.PlatformBindingActive,"Original IRQ Bind in flight."); fact.InFlight = true; fact.Device.PendingIrqBinds++; }
            try
            {
                var incarnation = CurrentProviderIncarnation();
                lock (_secureDomainLifecycleGate) { if (!OriginalIrqBindSources(fact,incarnation)) return KernelResult.Fail(KernelError.PlatformFaulted,"Original IRQ cleanup source unavailable."); }
                PlatformOriginalIrqBindResolution resolution;
                if (!fact.Invoked) resolution = new(fact.Request,true,null);
                else
                {
                    if (_provider is not IPlatformOriginalIrqAdmissionProvider provider) return KernelResult.Fail(KernelError.PlatformFaulted,"Original IRQ lookup unsupported.");
                    var raw = fact.Resolution is { IsSuccess: true } known ? known : provider.ResolveIrqBind(fact.Request);
                    lock (_secureDomainLifecycleGate) fact.Resolution = raw;
                    if (!raw.IsSuccess) return FromProviderFailure(raw.Status,raw.Message);
                    resolution = raw.Value;
                }
                incarnation = CurrentProviderIncarnation();
                lock (_secureDomainLifecycleGate)
                {
                    if (!OriginalIrqBindSources(fact,incarnation) || !ReferenceEquals(resolution.Request,fact.Request) || resolution.NoEffectGuaranteed == resolution.CleanupBinding.HasValue)
                        return KernelResult.Fail(KernelError.PlatformFaulted,"Original IRQ resolution foreign/malformed; no repeat.");
                }
                if (resolution.NoEffectGuaranteed)
                {
                    var released = fact.Endpoint.CommitRelease(fact.Device,() =>
                    { lock (_secureDomainLifecycleGate) { if (!OriginalIrqBindSources(fact,incarnation)) return KernelResult.Fail(KernelError.PlatformFaulted,"Original IRQ no-effect source changed."); fact.NoEffectConsumed = true; ConsumeOriginalIrqBindLocked(fact); return KernelResult.Ok(); } });
                    if (!released.IsSuccess) return released;
                    continue;
                }
                var providerBinding = resolution.CleanupBinding!.Value;
                lock (_secureDomainLifecycleGate)
                {
                    if (!PlatformIrqBindingContract.ValidateBinding(fact.Device.ProviderLease,fact.Binding.Source,providerBinding).IsSuccess) return KernelResult.Fail(KernelError.PlatformFaulted,"Original IRQ cleanup receipt malformed.");
                    if (fact.LocalBinding is null)
                    {
                        var child = new IrqBindingRecord(fact.Binding,providerBinding,fact.Capability) { OriginalBind = fact,EndpointSource = fact.Endpoint,BindDeviceSource = fact.Device,BindParentSource = fact.Parent,LocalAuthorizationRevoked = true };
                        child.OwnerSelf = child; fact.LocalBinding = child;
                    }
                    var record = fact.LocalBinding;
                    record.LocalAuthorizationRevoked = true;
                    if (!ReferenceEquals(record.OwnerSelf,record) || record.ProviderBinding != providerBinding || _originalIrqOwners.TryGetValue(record.Binding.BindingId,out var owner) && !ReferenceEquals(owner,record) ||
                        _irqBindings.TryGetValue(record.Binding.BindingId,out var route) && !ReferenceEquals(route,record)) return KernelResult.Fail(KernelError.PlatformFaulted,"Original IRQ partial cleanup adoption conflict.");
                    _originalIrqOwners.TryAdd(record.Binding.BindingId,record); _irqBindings.TryAdd(record.Binding.BindingId,record); fact.Parent.IrqSources.Add(record);
                }
                if (ReferenceEquals(fact.Endpoint.SnapshotSource(),fact.Device)) fact.Endpoint.TransferSource(fact.Device,fact.LocalBinding!);
                var closed = RevokeIrq(fact.Binding,subject); if (!closed.IsSuccess) return closed;
                lock (_secureDomainLifecycleGate) ConsumeOriginalIrqBindLocked(fact);
            }
            catch (Exception exception) when (exception is not StackOverflowException) { return KernelResult.Fail(KernelError.PlatformFaulted,exception.Message); }
            finally { lock (_secureDomainLifecycleGate) { fact.InFlight = false; fact.Device.PendingIrqBinds--; } }
        }
        return KernelResult.Ok();
    }
}
