using HybridCPU.ExternalRuntime.Contracts;
using YAKSys_Hybrid_CPU.Core;
namespace YAKSys_Hybrid_CPU.ExecutableAdapter;

public sealed partial class HybridCpuExecutableChildAdapter : INeutralOriginalVirtualIoProvider, INeutralOriginalVirtualIoCloseProvider, INeutralOriginalVirtualIoBindCleanupProvider
{
    private sealed class OriginalIoFact(HybridCpuExecutableChildAdapter adapter, NeutralOriginalVirtualIoRequest request, Child child, Parent parent, NeutralVirtualIoLease lease) : INeutralOriginalDeviceLoanLifetime
    {
        public OriginalIoFact? Self;
        public List<IoTransferFact> Transfers { get; } = [];
        public ulong TransfersIssued;
        public readonly NeutralOriginalVirtualIoRequest Request = request;
        public readonly Child Child = child;
        public readonly Parent Parent = parent;
        public readonly ExternalChildDomainCreateReceipt? ChildAdmission = child.CreateReceipt;
        public readonly ExternalDomainBindReceipt? ParentAdmission = parent.Admission;
        public readonly NeutralVirtualIoLease Lease = lease;
        public readonly long AdmissionRevision = child.StateRevision;
        public long CloseRevision;
        public NeutralOriginalDeviceLoanRequest? LoanRequest;
        public NeutralOriginalDeviceLoan? Loan;
        public ExternalOriginalParentDeviceRequest? DeviceRequest;
        public ExternalOriginalParentDeviceReceipt? Device;
        public ExternalOriginalVirtualIoRequest? NativeRequest;
        public ExternalChildVirtualIoBindReceipt? Binding;
        public ExternalOriginalVirtualIoCloseRequest? CloseRequest;
        public NeutralOriginalVirtualIoCloseRequest? CloseAdmission;
        public ExternalOriginalVirtualIoClosure? Closure;
        public ExternalOriginalParentDeviceClosure? DeviceClosure;
        public ExternalOriginalVirtualIoContainmentRequest? ContainmentRequest;
        public ExternalOriginalVirtualIoContainmentClosure? Containment;
        public NeutralOriginalVirtualIoBindCleanupClosure? BindCleanup;
        public bool DeviceInvoked, ContainmentInvoked, ContainmentValidated;
        public ExternalOriginalParentDeviceNoEffect? DeviceNoEffect;
        public bool DeviceNoEffectValidated;
        public bool InFlight, Published, Consumed, SourceFaulted, CloseInvoked, DeviceCloseInvoked;
        public NeutralOriginalDeviceLoanClosure? ResolveOriginalDeviceLoanClosure(NeutralOriginalDeviceLoan loan)
        {
            lock (adapter.sync)
                return adapter.OriginalIoSources(this) && ReferenceEquals(Loan, loan) && ReferenceEquals(loan.Request, LoanRequest) &&
                    (Consumed || !Child.StateRevisionExhausted && Child.StateRevision == CloseRevision) &&
                    (DeviceNoEffectValidated && DeviceNoEffect is { NoEffect: true, NoFurtherAdmission: true } refused && ReferenceEquals(refused.Request, DeviceRequest) ||
                    ContainmentValidated && Containment is { NoFurtherUse: true } contained && ReferenceEquals(contained.Request, ContainmentRequest) &&
                     ReferenceEquals(contained.Request.Admission, DeviceRequest) ||
                    Closure is { NoFurtherUse: true } closure && ReferenceEquals(closure.Request, CloseRequest) &&
                    DeviceClosure is { NoFurtherUse: true } closed && ReferenceEquals(closed.Device, Device) &&
                    closed.ContractVersion == adapter.externalFeatures.ContractVersion && closed.ManifestGeneration == adapter.externalFeatures.Generation)
                    ? new(loan, true) : null;
        }
    }
    private readonly Dictionary<NeutralVirtualIoHandle, OriginalIoFact> originalIo = [];
    private INeutralOriginalDeviceLoanProvider? deviceLoans;
    public NeutralVirtualizationResult<ulong> DescribeOriginalVirtualIoPrivateBackingBytes(NeutralVirtualIoProfile profile)
    {
        lock (sync) return deviceLoans is not null && childRuntime is IHybridCpuOriginalVirtualIoRuntimeV1 && profile.MaximumTransferBytes is > 0 and <= 1048576
            ? Ok((ulong)profile.MaximumTransferBytes) : Fail<ulong>(NeutralVirtualizationStatus.NotInvoked, "Original private backing producer/bound unavailable.");
    }
    public void AttachOriginalDeviceLoanProvider(INeutralOriginalDeviceLoanProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (sync)
        {
            if (deviceLoans is not null && !ReferenceEquals(deviceLoans, provider) || originalIo.Count != 0)
                throw new InvalidOperationException("Device lifetime source cannot change after attachment/admission.");
            deviceLoans = provider;
        }
    }
    private bool OriginalIoSources(OriginalIoFact fact) => !fact.SourceFaulted && ReferenceEquals(fact.Self, fact) &&
        originalIo.TryGetValue(fact.Lease.Handle, out var original) && ReferenceEquals(original, fact) &&
        (ulong)fact.Child.OriginalIo.Count == fact.Child.OriginalIoIssued && fact.Child.OriginalIo.Contains(fact) &&
        originalChildren.TryGetValue(fact.Child.Neutral.Handle, out var child) && ReferenceEquals(child, fact.Child) &&
        children.TryGetValue(fact.Child.Neutral.Handle, out var route) && ReferenceEquals(route, child) &&
        ReferenceEquals(child.ParentSource, fact.Parent) && ReferenceEquals(fact.Parent.OriginalOwner, fact.Parent) &&
        fact.ChildAdmission is not null && ReferenceEquals(child.CreateReceipt, fact.ChildAdmission) &&
        originalParents.TryGetValue(fact.Parent.Neutral, out var parent) && ReferenceEquals(parent, fact.Parent) &&
        parents.TryGetValue(fact.Parent.Neutral, out var parentRoute) && ReferenceEquals(parentRoute, parent) &&
        fact.ParentAdmission is not null && ReferenceEquals(parent.Admission, fact.ParentAdmission) && ReferenceEquals(parent.BindObservation?.Receipt, fact.ParentAdmission);
    private bool HasOriginalIoPins(Child child) => (ulong)child.OriginalIo.Count != child.OriginalIoIssued || child.OriginalIo.Any(f =>
        !ReferenceEquals(f.Self, f) || !ReferenceEquals(f.Child, child) || !f.Consumed);
    private bool CurrentIoGeneration(OriginalIoFact fact, bool admission = false)
    {
        var current = root.QueryFeatures();
        lock (sync)
        {
            if (current.Generation != externalFeatures.Generation || current.ContractVersion != externalFeatures.ContractVersion) fact.SourceFaulted = true;
            if (fact.Child.StateRevisionExhausted || fact.Child.StateRevision != (admission ? fact.AdmissionRevision : fact.CloseRevision)) fact.SourceFaulted = true;
            if (admission && (fact.Parent.Closed || fact.Parent.CloseInvoked || fact.Child.State != NeutralChildDomainState.Created)) fact.SourceFaulted = true;
            return OriginalIoSources(fact);
        }
    }
    public NeutralVirtualizationResult<NeutralVirtualIoLease> BindOriginalVirtualIo(NeutralOriginalVirtualIoRequest request)
    {
        if (request is null || request.InvocationId == Guid.Empty || request.Request.Profile.MaximumTransferBytes > 1048576 ||
            childRuntime is not IHybridCpuOriginalVirtualIoRuntimeV1 native || deviceLoans is not { } loans)
            return Fail<NeutralVirtualIoLease>(NeutralVirtualizationStatus.NotInvoked, "Original bounded device producer unavailable.");
        OriginalIoFact fact;
        lock (sync)
        {
            if (!TryChild(request.Request.ChildLease, out var child, out var failure)) return Fail<NeutralVirtualIoLease>(failure);
            var valid = NeutralVirtualIoContract.ValidateBind(request.Request, child!.State);
            if (!valid.IsSuccess) return Fail<NeutralVirtualIoLease>(valid);
            if (child.ParentSource is not { Admission: not null } parent || HasOriginalIoPins(child) || pendingChildTransitions.Contains(child.Neutral.Handle) ||
                originalIo.Values.Any(f => f.Request.InvocationId == request.InvocationId) ||
                pendingChildStarts.Contains(child.Neutral.Handle) || nextIdentity is 0 or ulong.MaxValue || nextOperation is 0 or >= ulong.MaxValue - 2 || child.OriginalIoIssued == ulong.MaxValue)
                return Fail<NeutralVirtualIoLease>(NeutralVirtualizationStatus.NotInvoked, "Original I/O owner is blocked before effect.");
            var lease = new NeutralVirtualIoLease(request.Request.ChildLease, request.Request.ParentDeviceLease, request.Request.Profile, new(nextIdentity++), new(1));
            fact = new(this, request, child, parent, lease); fact.Self = fact;
            originalIo.EnsureCapacity(originalIo.Count + 1); child.OriginalIo.EnsureCapacity(child.OriginalIo.Count + 1);
            originalIo.Add(lease.Handle, fact); child.OriginalIo.Add(fact); child.OriginalIoIssued++;
            fact.InFlight = true;
            fact.LoanRequest = new(request.InvocationId, request.DeviceAdmission, request.Request.ParentDeviceLease, request.Request.Profile.Rights, fact);
            fact.DeviceRequest = new(parent.Admission, request.InvocationId, request.Request.ParentDeviceLease.Device.ResourceId,
                (ExternalChildVirtualIoRights)request.Request.Profile.Rights, (int)request.Request.Profile.MaximumTransferBytes, Op());
        }
        try
        {
            if (!CurrentIoGeneration(fact, true)) return Fail<NeutralVirtualIoLease>(NeutralVirtualizationStatus.Ambiguous, "Original source lost before admission.");
            var loan = loans.AcquireOriginalDeviceLoan(fact.LoanRequest!);
            lock (sync) fact.Loan = loan;
            if (loan is null || !loans.ValidateOriginalDeviceLoan(loan) || !CurrentIoGeneration(fact, true)) return Fail<NeutralVirtualIoLease>(NeutralVirtualizationStatus.Ambiguous, "Native device loan unavailable; retained intent.");
            ExternalOriginalParentDeviceReceipt? device = null;
            lock (sync) fact.DeviceInvoked = true;
            try { device = native.BindOriginalParentDevice(fact.DeviceRequest!); }
            catch (Exception exception) when (exception is not StackOverflowException) { }
            finally { lock (sync) fact.Device = device; }
            device ??= native.ResolveOriginalParentDevice(fact.DeviceRequest!);
            lock (sync) fact.Device = device;
            if (device is null || !ReferenceEquals(device.Request, fact.DeviceRequest) || !ReferenceEquals(device, native.ResolveOriginalParentDevice(fact.DeviceRequest!)) ||
                !loans.ValidateOriginalDeviceLoan(loan) || !CurrentIoGeneration(fact, true))
                return Fail<NeutralVirtualIoLease>(NeutralVirtualizationStatus.Ambiguous, "Original device outcome/source unknown; loan pinned.");
            lock (sync) fact.NativeRequest = new(fact.Child.External, device, Op());
            ExternalChildVirtualIoBindReceipt? binding = null;
            try { binding = native.BindOriginalVirtualIo(fact.NativeRequest); }
            catch (Exception exception) when (exception is not StackOverflowException) { }
            finally { lock (sync) fact.Binding = binding; }
            binding ??= native.ResolveOriginalVirtualIo(fact.NativeRequest);
            lock (sync) fact.Binding = binding;
            if (binding is null || !ReferenceEquals(binding, native.ResolveOriginalVirtualIo(fact.NativeRequest)) ||
                binding.ChildHandle != fact.Child.External.Handle || binding.ChildEpoch != fact.Child.External.Epoch || binding.Parent != fact.Parent.External ||
                binding.ParentDeviceHandle != device.Handle || binding.ParentDeviceEpoch != device.Epoch || binding.Operation != fact.NativeRequest.Operation ||
                binding.ContractVersion != externalFeatures.ContractVersion || binding.ManifestGeneration != externalFeatures.Generation ||
                !loans.ValidateOriginalDeviceLoan(loan) || !CurrentIoGeneration(fact, true))
                return Fail<NeutralVirtualIoLease>(NeutralVirtualizationStatus.Ambiguous, "Original backing binding outcome/source unknown; loan pinned.");
            lock (sync)
            {
                if (!OriginalIoSources(fact) || fact.Child.State != NeutralChildDomainState.Created || fact.Parent.Closed || fact.Parent.CloseInvoked)
                    return Fail<NeutralVirtualIoLease>(NeutralVirtualizationStatus.Ambiguous, "Original owner changed before publication.");
                ioBindings.Add(fact.Lease.Handle, new(fact.Lease, binding)); fact.Published = true;
                return Ok(fact.Lease);
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return Fail<NeutralVirtualIoLease>(NeutralVirtualizationStatus.Ambiguous, "Original I/O retained after " + exception.GetType().Name); }
        finally { lock (sync) fact.InFlight = false; }
    }
    public NeutralVirtualizationResult<NeutralVirtualIoCloseReceipt> CloseOriginalVirtualIo(NeutralOriginalVirtualIoCloseRequest request)
        => CloseAdmittedOriginalIo(request, false);
    public NeutralVirtualizationResult<NeutralVirtualIoCloseReceipt> ContinueOriginalVirtualIoClose(NeutralOriginalVirtualIoCloseRequest request)
        => CloseAdmittedOriginalIo(request, true);
    private NeutralVirtualizationResult<NeutralVirtualIoCloseReceipt> CloseAdmittedOriginalIo(NeutralOriginalVirtualIoCloseRequest request, bool continuation)
    {
        OriginalIoFact fact;
        lock (sync)
        {
            if (request is null || request.InvocationId == Guid.Empty ||
                !originalIo.TryGetValue(request.Lease.Handle, out fact!) || !ReferenceEquals(fact.Request, request.Admission) ||
                fact.Lease != request.Lease || !ReferenceEquals(fact.Self, fact) || fact.InFlight ||
                (fact.CloseAdmission is null ? continuation || fact.CloseRequest is not null : !ReferenceEquals(fact.CloseAdmission, request)))
                return Fail<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original Close admission unavailable; no new effect.");
            fact.CloseAdmission ??= request; // Before any effect; continuation cannot create this admission.
        }
        return CloseOriginalIo(fact, request.Lease, request);
    }
    private NeutralVirtualizationResult<NeutralVirtualIoCloseReceipt> CloseOriginalIo(OriginalIoFact fact, NeutralVirtualIoLease lease, NeutralOriginalVirtualIoCloseRequest? admission = null)
    {
        if (childRuntime is not IHybridCpuOriginalVirtualIoRuntimeV1 native || deviceLoans is not { } loans)
            return Fail<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original closure producer unavailable.");
        lock (sync)
        {
            if (admission is null && fact.Consumed && lease == fact.Lease && ReferenceEquals(fact.Self, fact)) return IoClosed(fact); // V1 retained completion.
            if (lease != fact.Lease || !ReferenceEquals(fact.Self, fact) || !ReferenceEquals(fact.CloseAdmission, admission)) return Fail<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Stale, "Original I/O identity differs.");
            if (fact.Consumed) return IoClosed(fact);
            if (fact.InFlight || !OriginalIoSources(fact) || fact.Binding is null || fact.NativeRequest is null || fact.Loan is null || !fact.Published ||
                (fact.CloseRequest is null && nextOperation is 0 or ulong.MaxValue)) return Fail<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original I/O cleanup pinned.");
            if (fact.CloseRequest is null) { fact.CloseRevision = fact.Child.StateRevision; fact.CloseRequest = new(fact.NativeRequest, fact.Binding, Op()); }
            fact.InFlight = true;
        }
        try
        {
            if (!CurrentIoGeneration(fact)) return Fail<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original closure source changed before effect.");
            ExternalOriginalVirtualIoClosure? closure = fact.Closure;
            if (!fact.CloseInvoked)
            {
                lock (sync) fact.CloseInvoked = true;
                try { closure = native.CloseOriginalVirtualIo(fact.CloseRequest!); }
                catch (Exception exception) when (exception is not StackOverflowException) { }
                finally { lock (sync) fact.Closure = closure; }
            }
            closure ??= native.ResolveOriginalVirtualIoClosure(fact.CloseRequest!);
            lock (sync) fact.Closure = closure;
            if (closure is not { NoFurtherUse: true } || !ReferenceEquals(closure.Request, fact.CloseRequest) ||
                closure.ContractVersion != externalFeatures.ContractVersion || closure.ManifestGeneration != externalFeatures.Generation ||
                !ReferenceEquals(closure, native.ResolveOriginalVirtualIoClosure(fact.CloseRequest!)))
                return Fail<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original I/O closure unknown; no repeat.");
            if (!CurrentIoGeneration(fact)) return Fail<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original source changed before backing cleanup.");
            ExternalOriginalParentDeviceClosure? deviceClosure = fact.DeviceClosure;
            if (!fact.DeviceCloseInvoked)
            {
                lock (sync) fact.DeviceCloseInvoked = true;
                try { deviceClosure = native.CloseOriginalParentDevice(fact.Device!); }
                catch (Exception exception) when (exception is not StackOverflowException) { }
                finally { lock (sync) fact.DeviceClosure = deviceClosure; }
            }
            deviceClosure ??= native.ResolveOriginalParentDeviceClosure(fact.Device!);
            lock (sync) fact.DeviceClosure = deviceClosure;
            if (deviceClosure is not { NoFurtherUse: true } || !ReferenceEquals(deviceClosure.Device, fact.Device) ||
                deviceClosure.ContractVersion != externalFeatures.ContractVersion || deviceClosure.ManifestGeneration != externalFeatures.Generation ||
                !ReferenceEquals(deviceClosure, native.ResolveOriginalParentDeviceClosure(fact.Device!)) || !CurrentIoGeneration(fact) ||
                !loans.RetireOriginalDeviceLoan(fact.Loan!)) return Fail<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original backing/loan remains pinned.");
            if (!CurrentIoGeneration(fact)) return Fail<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original source lost after loan retirement.");
            lock (sync)
            {
                if (!OriginalIoSources(fact) || fact.Child.StateRevisionExhausted || fact.Child.StateRevision != fact.CloseRevision)
                    return Fail<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original local consumption source lost.");
                fact.Consumed = true; ioBindings.Remove(fact.Lease.Handle); return IoClosed(fact);
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return Fail<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original I/O cleanup retained after " + exception.GetType().Name); }
        finally { lock (sync) fact.InFlight = false; }
    }
    private static NeutralVirtualizationResult<NeutralVirtualIoCloseReceipt> IoClosed(OriginalIoFact fact) => Ok(new NeutralVirtualIoCloseReceipt(
        fact.Lease.Handle, fact.Lease.Epoch, fact.Lease.ChildLease.Handle, fact.Lease.ChildLease.Epoch, fact.Lease.ChildLease.ParentLease.Handle, fact.Lease.ChildLease.ParentLease.Epoch, true));

    public bool SupportsOriginalVirtualIoBindCleanup => childRuntime is IHybridCpuOriginalVirtualIoContainmentRuntimeV1;
    public NeutralOriginalVirtualIoBindCleanupClosure? ResolveOriginalVirtualIoBindCleanup(NeutralOriginalVirtualIoRequest request)
    {
        lock (sync)
        {
            var fact = originalIo.Values.SingleOrDefault(f => ReferenceEquals(f.Request, request));
            return fact is { Consumed: true, BindCleanup: { NoFurtherUse: true } closure } && ReferenceEquals(fact.Self, fact) &&
                ReferenceEquals(closure.Admission, request) ? closure : null;
        }
    }
    public NeutralVirtualizationResult<NeutralOriginalVirtualIoBindCleanupClosure> CleanupOriginalVirtualIoBind(NeutralOriginalVirtualIoRequest request)
    {
        if (request is null || childRuntime is not IHybridCpuOriginalVirtualIoContainmentRuntimeV1 native || deviceLoans is not { } loans)
            return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original backing containment unavailable.");
        OriginalIoFact fact;
        lock (sync)
        {
            var originals = originalIo.Values.Where(f => ReferenceEquals(f.Request, request)).ToArray();
            if (originals.Length != 1 || !ReferenceEquals(originals[0].Self, originals[0]))
                return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original Bind intent unavailable.");
            fact = originals[0];
            if (fact.Consumed && fact.BindCleanup is not null) return Ok(fact.BindCleanup);
            if (fact.CloseAdmission is not null || fact.CloseRequest is not null || fact.InFlight || !OriginalIoSources(fact) || !fact.DeviceInvoked || fact.DeviceRequest is null || fact.Loan is null)
                return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original unknown Bind remains pinned.");
            fact.CloseRevision = fact.AdmissionRevision;
            fact.InFlight = true;
        }
        try
        {
            if (!CurrentIoGeneration(fact)) return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original cleanup source changed.");
            if (fact.Device is null && childRuntime is IHybridCpuOriginalParentDeviceNoEffectRuntimeV1 admission)
            {
                var denied = fact.DeviceNoEffect ?? admission.ResolveOriginalParentDeviceNoEffect(fact.DeviceRequest!);
                lock (sync) fact.DeviceNoEffect = denied; // Before query/callback validation; malformed positive is never replaced.
                if (denied is not null)
                {
                    if (!denied.NoEffect || !denied.NoFurtherAdmission || !ReferenceEquals(denied.Request, fact.DeviceRequest) ||
                        denied.ContractVersion != externalFeatures.ContractVersion || denied.ManifestGeneration != externalFeatures.Generation ||
                        !ReferenceEquals(denied, admission.ResolveOriginalParentDeviceNoEffect(fact.DeviceRequest!)) || !CurrentIoGeneration(fact))
                        return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original no-effect fact invalid; pins retained.");
                    lock (sync) fact.DeviceNoEffectValidated = true;
                }
            }
            var raw = fact.Containment;
            if (!fact.DeviceNoEffectValidated && fact.ContainmentRequest is null)
            {
                lock (sync)
                {
                    if (nextOperation is 0 or ulong.MaxValue) return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Containment operation capacity exhausted.");
                    fact.ContainmentRequest = new(fact.DeviceRequest!, Op());
                }
            }
            if (!fact.DeviceNoEffectValidated && !fact.ContainmentInvoked)
            {
                lock (sync) fact.ContainmentInvoked = true;
                try { raw = native.ContainOriginalVirtualIoBacking(fact.ContainmentRequest!); }
                catch (Exception exception) when (exception is not StackOverflowException) { }
                finally { lock (sync) fact.Containment = raw; }
            }
            if (!fact.DeviceNoEffectValidated) raw ??= native.ResolveOriginalVirtualIoBackingContainment(fact.ContainmentRequest!);
            lock (sync) fact.Containment = raw;
            if (!fact.DeviceNoEffectValidated && (raw is not { NoFurtherUse: true } || !ReferenceEquals(raw.Request, fact.ContainmentRequest) ||
                raw.ContractVersion != externalFeatures.ContractVersion || raw.ManifestGeneration != externalFeatures.Generation ||
                !ReferenceEquals(raw, native.ResolveOriginalVirtualIoBackingContainment(fact.ContainmentRequest!)) || !CurrentIoGeneration(fact)))
                return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original containment unknown; no repeat.");
            lock (sync) if (!fact.DeviceNoEffectValidated) fact.ContainmentValidated = true;
            if (!loans.RetireOriginalDeviceLoan(fact.Loan!) || !CurrentIoGeneration(fact))
                return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original native loan/source unresolved.");
            lock (sync)
            {
                if (!OriginalIoSources(fact) || fact.Child.StateRevisionExhausted || fact.Child.StateRevision != fact.CloseRevision)
                    return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original Bind cleanup commit changed.");
                fact.BindCleanup = new(request, true, fact.DeviceNoEffectValidated); fact.Consumed = true; ioBindings.Remove(fact.Lease.Handle); return Ok(fact.BindCleanup);
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original cleanup retained after " + exception.GetType().Name); }
        finally { lock (sync) fact.InFlight = false; }
    }
}
