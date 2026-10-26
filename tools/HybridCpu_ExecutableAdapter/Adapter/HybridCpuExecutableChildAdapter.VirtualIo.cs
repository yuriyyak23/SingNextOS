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
        public NeutralOriginalDeviceLoanRequest? OriginalLoanRequest;
        public NeutralOriginalDeviceLoan? Loan;
        public NeutralOriginalDeviceLoanNoEffect? LoanNoEffect, ValidatedLoanNoEffect;
        public ExternalOriginalParentDeviceRequest? DeviceRequest;
        public ExternalOriginalParentDeviceReceipt? Device;
        public ExternalOriginalVirtualIoRequest? NativeRequest;
        public ExternalChildVirtualIoBindReceipt? Binding;
        public ExternalOriginalVirtualIoCloseRequest? CloseRequest;
        public NeutralOriginalVirtualIoCloseRequest? CloseAdmission;
        public ExternalOriginalVirtualIoClosure? Closure;
        public ExternalOriginalVirtualIoClosure? ValidatedClosure;
        public ExternalOriginalParentDeviceClosure? DeviceClosure;
        public ExternalOriginalParentDeviceClosure? ValidatedDeviceClosure;
        public ExternalOriginalVirtualIoContainmentRequest? ContainmentRequest;
        public ExternalOriginalVirtualIoContainmentRequest? OriginalContainmentRequest;
        public ExternalOriginalVirtualIoContainmentClosure? Containment;
        public ExternalOriginalVirtualIoContainmentClosure? OriginalContainment;
        public ExternalOriginalVirtualIoContainmentClosure? ValidatedContainment;
        public NeutralOriginalVirtualIoBindCleanupClosure? BindCleanup;
        public NeutralOriginalVirtualIoBindCleanupClosure? OriginalBindCleanup;
        public bool DeviceInvoked, ContainmentInvoked, ContainmentValidated;
        public bool LoanInvoked, DeviceNeverInvoked;
        public ExternalOriginalParentDeviceNoEffect? DeviceNoEffect;
        public ExternalOriginalParentDeviceNoEffect? ValidatedDeviceNoEffect;
        public bool DeviceNoEffectValidated;
        public bool InFlight, Published, Consumed, SourceFaulted, CloseInvoked, DeviceCloseInvoked;
        public NeutralOriginalDeviceLoanClosure? ResolveOriginalDeviceLoanClosure(NeutralOriginalDeviceLoan loan)
        {
            lock (adapter.sync)
                return adapter.OriginalIoSources(this) && ReferenceEquals(Loan, loan) && ReferenceEquals(loan.Request, LoanRequest) &&
                    (Consumed || !Child.StateRevisionExhausted && Child.StateRevision == CloseRevision) &&
                    (adapter.HasOriginalBindCleanupProof(this) ||
                    adapter.HasOriginalIoClosure(this))
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
        originalIo.TryGetValue(fact.Lease.Handle, out OriginalIoFact? original) && ReferenceEquals(original, fact) &&
        (ulong)fact.Child.OriginalIo.Count == fact.Child.OriginalIoIssued && fact.Child.OriginalIo.Contains(fact) &&
        originalChildren.TryGetValue(fact.Child.Neutral.Handle, out Child? child) && ReferenceEquals(child, fact.Child) &&
        children.TryGetValue(fact.Child.Neutral.Handle, out Child? route) && ReferenceEquals(route, child) &&
        ReferenceEquals(child.ParentSource, fact.Parent) && ReferenceEquals(fact.Parent.OriginalOwner, fact.Parent) &&
        fact.ChildAdmission is not null && ReferenceEquals(child.CreateReceipt, fact.ChildAdmission) &&
        originalParents.TryGetValue(fact.Parent.Neutral, out Parent? parent) && ReferenceEquals(parent, fact.Parent) &&
        parents.TryGetValue(fact.Parent.Neutral, out Parent? parentRoute) && ReferenceEquals(parentRoute, parent) &&
        fact.ParentAdmission is not null && ReferenceEquals(parent.Admission, fact.ParentAdmission) && ReferenceEquals(parent.BindObservation?.Receipt, fact.ParentAdmission);
    private bool HasOriginalIoPins(Child child, bool pendingOnly = false) => (ulong)child.OriginalIo.Count != child.OriginalIoIssued ||
        child.OriginalIo.Distinct(ReferenceEqualityComparer.Instance).Count() != child.OriginalIo.Count || child.OriginalIo.Any(f =>
        !ReferenceEquals(f.Self, f) || !ReferenceEquals(f.Child, child) || f.Consumed && (!OriginalIoSources(f) || !(HasOriginalIoClosure(f) || HasOriginalBindCleanupProof(f))) || !f.Consumed &&
        (!pendingOnly || !f.Published || f.InFlight || f.SourceFaulted || f.CloseAdmission is not null ||
         child.StateRevisionExhausted || child.StateRevision != f.AdmissionRevision ||
         !OriginalIoSources(f) || !HasOriginalTransferHistory(f) || f.Transfers.Any(transfer =>
             transfer.SourceChanged || transfer.Published is null || transfer.ReleaseInvoked && transfer.ReleasePublished is null)));
    private bool CurrentIoGeneration(OriginalIoFact fact, bool admission = false)
    {
        HybridCpuExternalFeatureManifest current = root.QueryFeatures();
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
            if (!TryChild(request.Request.ChildLease, out Child? child, out NeutralVirtualizationResult failure)) return Fail<NeutralVirtualIoLease>(failure);
            NeutralVirtualizationResult valid = NeutralVirtualIoContract.ValidateBind(request.Request, child!.State);
            if (!valid.IsSuccess) return Fail<NeutralVirtualIoLease>(valid);
            // A published live IO retains child closure, but does not exclude another
            // independent device. Unknown/in-flight original work still excludes admission.
            if (child.ParentSource is not { Admission: not null } parent || HasOriginalIoPins(child, pendingOnly: true) || pendingChildTransitions.Contains(child.Neutral.Handle) ||
                originalIo.Values.Any(f => f.Request.InvocationId == request.InvocationId) ||
                pendingChildStarts.Contains(child.Neutral.Handle) || nextIdentity is 0 or ulong.MaxValue || nextOperation is 0 or >= ulong.MaxValue - 2 || child.OriginalIoIssued == ulong.MaxValue)
                return Fail<NeutralVirtualIoLease>(NeutralVirtualizationStatus.NotInvoked, "Original I/O owner is blocked before effect.");
            NeutralVirtualIoLease lease = new NeutralVirtualIoLease(request.Request.ChildLease, request.Request.ParentDeviceLease, request.Request.Profile, new(nextIdentity++), new(1));
            fact = new(this, request, child, parent, lease); fact.Self = fact;
            originalIo.EnsureCapacity(originalIo.Count + 1); child.OriginalIo.EnsureCapacity(child.OriginalIo.Count + 1);
            originalIo.Add(lease.Handle, fact); child.OriginalIo.Add(fact); child.OriginalIoIssued++;
            fact.InFlight = true;
            fact.LoanRequest = fact.OriginalLoanRequest = new(request.InvocationId, request.DeviceAdmission, request.Request.ParentDeviceLease, request.Request.Profile.Rights, fact);
            fact.DeviceRequest = new(parent.Admission, request.InvocationId, request.Request.ParentDeviceLease.Device.ResourceId,
                (ExternalChildVirtualIoRights)request.Request.Profile.Rights, (int)request.Request.Profile.MaximumTransferBytes, Op());
        }
        try
        {
            if (!CurrentIoGeneration(fact, true)) return Fail<NeutralVirtualIoLease>(NeutralVirtualizationStatus.Ambiguous, "Original source lost before admission.");
            lock (sync)
            {
                if (!OriginalIoSources(fact) || !ReferenceEquals(fact.LoanRequest, fact.OriginalLoanRequest))
                    return Fail<NeutralVirtualIoLease>(NeutralVirtualizationStatus.Ambiguous, "Original loan admission custody changed.");
                fact.LoanInvoked = true; // Before acquisition callback; missing reply is never zero loans.
            }
            NeutralOriginalDeviceLoan? loan = loans.AcquireOriginalDeviceLoan(fact.LoanRequest!);
            lock (sync) fact.Loan = loan;
            if (loan is null || !loans.ValidateOriginalDeviceLoan(loan) || !CurrentIoGeneration(fact, true)) return Fail<NeutralVirtualIoLease>(NeutralVirtualizationStatus.Ambiguous, "Native device loan unavailable; retained intent.");
            ExternalOriginalParentDeviceReceipt? device = null;
            lock (sync)
            {
                if (!OriginalIoSources(fact) || !ReferenceEquals(fact.Loan, loan) ||
                    !ReferenceEquals(fact.LoanRequest, fact.OriginalLoanRequest) || !ReferenceEquals(loan.Request, fact.LoanRequest))
                    return Fail<NeutralVirtualIoLease>(NeutralVirtualizationStatus.Ambiguous, "Original loan changed before backing effect.");
                fact.DeviceInvoked = true;
            }
            try { device = native.BindOriginalParentDevice(fact.DeviceRequest!); }
            catch (Exception exception) when (exception is not StackOverflowException) { }
            finally { lock (sync) fact.Device = device; }
            device ??= native.ResolveOriginalParentDevice(fact.DeviceRequest!);
            lock (sync) fact.Device = device;
            if (device is null || !ReferenceEquals(device.Request, fact.DeviceRequest) || !ReferenceEquals(device, native.ResolveOriginalParentDevice(fact.DeviceRequest!)) ||
                !loans.ValidateOriginalDeviceLoan(loan) || !CurrentIoGeneration(fact, true))
                return Fail<NeutralVirtualIoLease>(NeutralVirtualizationStatus.Ambiguous, "Original device outcome/source unknown; loan pinned.");
            lock (sync)
            {
                if (!ReferenceEquals(fact.Device, device) || !ReferenceEquals(fact.Loan, loan) || !OriginalIoSources(fact))
                    return Fail<NeutralVirtualIoLease>(NeutralVirtualizationStatus.Ambiguous, "Original device custody changed before I/O effect.");
                fact.NativeRequest = new(fact.Child.External, device, Op());
            }
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
                if (!OriginalIoSources(fact) || fact.Child.State != NeutralChildDomainState.Created || fact.Parent.Closed || fact.Parent.CloseInvoked ||
                    !ReferenceEquals(fact.Device, device) || !ReferenceEquals(fact.Binding, binding) || !ReferenceEquals(fact.Loan, loan) ||
                    fact.NativeRequest is not { } nativeRequest || !ReferenceEquals(nativeRequest.Device, device))
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
            if(closure is null && childRuntime is IHybridCpuOriginalVirtualIoCloseContinuationRuntimeV1 continuation)
            {
                if(!CurrentIoGeneration(fact))return Fail<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,"Original source changed before retained Close continuation.");
                // Native continuation requires its retained exact intent; absence cannot create one.
                closure=continuation.ContinueOriginalVirtualIoClose(fact.CloseRequest!);
            }
            lock (sync) fact.Closure = closure;
            if (closure is not { NoFurtherUse: true } || !ReferenceEquals(closure.Request, fact.CloseRequest) ||
                closure.ContractVersion != externalFeatures.ContractVersion || closure.ManifestGeneration != externalFeatures.Generation ||
                !ReferenceEquals(closure, native.ResolveOriginalVirtualIoClosure(fact.CloseRequest!)))
                return Fail<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original I/O closure unknown; no repeat.");
            lock(sync)
            {
                if(!ReferenceEquals(fact.Closure,closure) || !ReferenceEquals(closure.Request,fact.CloseRequest) ||
                    fact.ValidatedClosure is { } prior && !ReferenceEquals(prior,closure))
                    return Fail<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,"Original IO closure custody changed.");
                fact.ValidatedClosure=closure;
            }
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
            if(deviceClosure is null && childRuntime is IHybridCpuOriginalParentDeviceCloseContinuationRuntimeV1 deviceContinuation)
            {
                if(!CurrentIoGeneration(fact))return Fail<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,"Original source changed before retained backing Close continuation.");
                deviceClosure=deviceContinuation.ContinueOriginalParentDeviceClose(fact.Device!);
            }
            lock (sync) fact.DeviceClosure = deviceClosure;
            if (deviceClosure is not { NoFurtherUse: true } || !ReferenceEquals(deviceClosure.Device, fact.Device) ||
                deviceClosure.ContractVersion != externalFeatures.ContractVersion || deviceClosure.ManifestGeneration != externalFeatures.Generation ||
                !ReferenceEquals(deviceClosure, native.ResolveOriginalParentDeviceClosure(fact.Device!)))
                return Fail<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,"Original backing closure unknown.");
            lock(sync)
            {
                if(!ReferenceEquals(fact.DeviceClosure,deviceClosure) || !ReferenceEquals(deviceClosure.Device,fact.Device) ||
                    fact.ValidatedDeviceClosure is { } prior && !ReferenceEquals(prior,deviceClosure))
                    return Fail<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,"Original backing closure custody changed.");
                fact.ValidatedDeviceClosure=deviceClosure;
            }
            if(!CurrentIoGeneration(fact) || !loans.RetireOriginalDeviceLoan(fact.Loan!))
                return Fail<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,"Original backing/loan remains pinned.");
            if (!CurrentIoGeneration(fact)) return Fail<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original source lost after loan retirement.");
            lock (sync)
            {
                if (!OriginalIoSources(fact) || fact.Child.StateRevisionExhausted || fact.Child.StateRevision != fact.CloseRevision ||
                    !ReferenceEquals(fact.CloseAdmission,admission) || !ReferenceEquals(fact.Closure,closure) || !ReferenceEquals(fact.DeviceClosure,deviceClosure) || !HasOriginalIoClosure(fact))
                    return Fail<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original local consumption source lost.");
                fact.Consumed = true; ioBindings.Remove(fact.Lease.Handle); return IoClosed(fact);
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return Fail<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original I/O cleanup retained after " + exception.GetType().Name); }
        finally { lock (sync) fact.InFlight = false; }
    }
    private bool HasOriginalIoClosure(OriginalIoFact fact) => fact.Published && fact.CloseInvoked && fact.DeviceCloseInvoked &&
        fact.NativeRequest is not null && fact.Binding is not null && fact.Device is not null && fact.CloseRequest is { } request &&
        ReferenceEquals(request.Admission,fact.NativeRequest) && ReferenceEquals(request.Binding,fact.Binding) &&
        fact.Closure is {NoFurtherUse:true} closed && ReferenceEquals(closed,fact.ValidatedClosure) && ReferenceEquals(closed.Request,request) &&
        closed.ContractVersion==externalFeatures.ContractVersion && closed.ManifestGeneration==externalFeatures.Generation &&
        fact.DeviceClosure is {NoFurtherUse:true} backing && ReferenceEquals(backing,fact.ValidatedDeviceClosure) && ReferenceEquals(backing.Device,fact.Device) &&
        backing.ContractVersion==externalFeatures.ContractVersion && backing.ManifestGeneration==externalFeatures.Generation;
    private NeutralVirtualizationResult<NeutralVirtualIoCloseReceipt> IoClosed(OriginalIoFact fact) =>
        !fact.Consumed || !HasOriginalIoClosure(fact) ? Fail<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,"Original retained IO closure unavailable.") : Ok(new NeutralVirtualIoCloseReceipt(
        fact.Lease.Handle, fact.Lease.Epoch, fact.Lease.ChildLease.Handle, fact.Lease.ChildLease.Epoch, fact.Lease.ChildLease.ParentLease.Handle, fact.Lease.ChildLease.ParentLease.Epoch, true));

    public bool SupportsOriginalVirtualIoBindCleanup => childRuntime is IHybridCpuOriginalVirtualIoContainmentRuntimeV1;
    private static bool HasOriginalContainmentIntent(OriginalIoFact fact) =>
        ReferenceEquals(fact.ContainmentRequest,fact.OriginalContainmentRequest) &&
        (fact.OriginalContainmentRequest is { } original ? ReferenceEquals(original.Admission,fact.DeviceRequest) :
         !fact.ContainmentInvoked && fact.Containment is null && fact.OriginalContainment is null &&
         fact.ValidatedContainment is null && !fact.ContainmentValidated);
    // Called under sync. Retain first raw even when a callback damaged its mutable route.
    private static bool RetainOriginalContainment(OriginalIoFact fact,ExternalOriginalVirtualIoContainmentClosure? raw)
    {
        if(raw is not null && fact.OriginalContainment is null)
        {
            fact.OriginalContainment=raw;
            fact.Containment ??=raw;
        }
        return ReferenceEquals(fact.Containment,fact.OriginalContainment) &&
            (raw is null || ReferenceEquals(raw,fact.OriginalContainment));
    }
    private static bool HasNeverInvokedDeviceProof(OriginalIoFact fact) => fact.DeviceNeverInvoked && !fact.DeviceInvoked &&
        fact.Device is null && fact.NativeRequest is null && fact.Binding is null && fact.ContainmentRequest is null &&
        HasOriginalContainmentIntent(fact) &&
        fact.OriginalLoanRequest is not null && ReferenceEquals(fact.LoanRequest, fact.OriginalLoanRequest) &&
        (!fact.LoanInvoked ? fact.Loan is null : fact.Loan is { } loan && ReferenceEquals(loan.Request, fact.LoanRequest) || HasOriginalLoanNoEffect(fact));
    private static bool HasOriginalLoanNoEffect(OriginalIoFact fact) => fact.LoanInvoked && fact.Loan is null &&
        fact.LoanNoEffect is { NoEffect: true, NoFurtherAdmission: true } raw && ReferenceEquals(raw, fact.ValidatedLoanNoEffect) && ReferenceEquals(raw.Request, fact.OriginalLoanRequest);
    private bool HasOriginalBindCleanupProof(OriginalIoFact fact) => HasNeverInvokedDeviceProof(fact) || (fact.DeviceNoEffectValidated
        ? fact.DeviceNoEffect is { NoEffect: true, NoFurtherAdmission: true } refused && ReferenceEquals(refused, fact.ValidatedDeviceNoEffect) &&
            ReferenceEquals(refused.Request, fact.DeviceRequest) && refused.ContractVersion == externalFeatures.ContractVersion && refused.ManifestGeneration == externalFeatures.Generation
        : HasOriginalContainmentIntent(fact) && fact.ContainmentValidated && fact.Containment is { NoFurtherUse: true } contained &&
            ReferenceEquals(contained,fact.OriginalContainment) && ReferenceEquals(contained, fact.ValidatedContainment) &&
            ReferenceEquals(contained.Request, fact.ContainmentRequest) && ReferenceEquals(contained.Request.Admission, fact.DeviceRequest) &&
            contained.ContractVersion == externalFeatures.ContractVersion && contained.ManifestGeneration == externalFeatures.Generation);
    private bool HasOriginalBindCleanup(OriginalIoFact fact) => fact.Consumed && fact.BindCleanup is { NoFurtherUse: true } closure &&
        ReferenceEquals(closure, fact.OriginalBindCleanup) && ReferenceEquals(closure.Admission, fact.Request) &&
        closure.NoEffect == (fact.DeviceNoEffectValidated || fact.DeviceNeverInvoked) && HasOriginalBindCleanupProof(fact);
    public NeutralOriginalVirtualIoBindCleanupClosure? ResolveOriginalVirtualIoBindCleanup(NeutralOriginalVirtualIoRequest request)
    {
        lock (sync)
        {
            OriginalIoFact? fact = originalIo.Values.SingleOrDefault(f => ReferenceEquals(f.Request, request));
            return fact is not null && ReferenceEquals(fact.Self, fact) && HasOriginalBindCleanup(fact) ? fact.BindCleanup : null;
        }
    }
    public NeutralVirtualizationResult<NeutralOriginalVirtualIoBindCleanupClosure> CleanupOriginalVirtualIoBind(NeutralOriginalVirtualIoRequest request)
    {
        if (request is null || childRuntime is not IHybridCpuOriginalVirtualIoContainmentRuntimeV1 native || deviceLoans is not { } loans)
            return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original backing containment unavailable.");
        OriginalIoFact fact;
        lock (sync)
        {
            OriginalIoFact[] originals = originalIo.Values.Where(f => ReferenceEquals(f.Request, request)).ToArray();
            if (originals.Length != 1 || !ReferenceEquals(originals[0].Self, originals[0]))
                return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original Bind intent unavailable.");
            fact = originals[0];
            if (fact.Consumed) return HasOriginalBindCleanup(fact) ? Ok(fact.BindCleanup!) :
                Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original retained Bind cleanup custody unavailable.");
            if (fact.CloseAdmission is not null || fact.CloseRequest is not null || fact.InFlight || !OriginalIoSources(fact) ||
                !HasOriginalContainmentIntent(fact) || !ReferenceEquals(fact.Containment,fact.OriginalContainment) ||
                fact.DeviceRequest is null || fact.DeviceInvoked && fact.Loan is null)
                return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original unknown Bind remains pinned.");
            fact.CloseRevision = fact.AdmissionRevision;
            fact.InFlight = true;
        }
        try
        {
            if (!CurrentIoGeneration(fact)) return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original cleanup source changed.");
            if (!fact.DeviceInvoked)
            {
                if (fact.LoanInvoked && fact.Loan is null && loans is INeutralOriginalDeviceLoanNoEffectProvider refusal)
                {
                    if (fact.ValidatedLoanNoEffect is { } retained && !ReferenceEquals(retained, fact.LoanNoEffect))
                        return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original loan no-effect custody lost.");
                    NeutralOriginalDeviceLoanNoEffect? denied = fact.LoanNoEffect ?? refusal.ResolveOriginalDeviceLoanNoEffect(fact.OriginalLoanRequest!);
                    lock (sync) fact.LoanNoEffect ??= denied;
                    if (denied is not null)
                    {
                        if (!denied.NoEffect || !denied.NoFurtherAdmission || !ReferenceEquals(denied.Request, fact.OriginalLoanRequest) ||
                            !ReferenceEquals(denied, refusal.ResolveOriginalDeviceLoanNoEffect(fact.OriginalLoanRequest!)) || !CurrentIoGeneration(fact))
                            return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original loan no-effect lookup unknown/malformed.");
                        lock (sync)
                        {
                            if (!OriginalIoSources(fact) || !ReferenceEquals(fact.LoanNoEffect, denied))
                                return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original loan no-effect source changed.");
                            fact.ValidatedLoanNoEffect = denied;
                        }
                    }
                }
                if (fact.LoanInvoked && fact.Loan is null && !HasOriginalLoanNoEffect(fact))
                {
                    if (loans is not INeutralOriginalDeviceLoanResolutionProvider resolution || fact.OriginalLoanRequest is not { } originalRequest)
                        return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original loan acquisition reply unknown; query capability unavailable.");
                    NeutralOriginalDeviceLoan? resolvedLoan = resolution.ResolveOriginalDeviceLoan(originalRequest);
                    lock (sync) fact.Loan ??= resolvedLoan; // Retain exact returned raw before another callback.
                    if (resolvedLoan is null || !ReferenceEquals(resolvedLoan.Request, originalRequest) ||
                        !ReferenceEquals(resolvedLoan, resolution.ResolveOriginalDeviceLoan(originalRequest)) || !CurrentIoGeneration(fact))
                        return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original loan lookup unknown/malformed; acquisition is never repeated.");
                }
                lock (sync)
                {
                    if (!OriginalIoSources(fact)) return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original pre-device owner changed.");
                    fact.DeviceNeverInvoked = true; // Sole SDK invocation owner fences this original; no native absence inference.
                    if (!HasNeverInvokedDeviceProof(fact)) return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original pre-device disposition unavailable.");
                }
                if (fact.Loan is { } originalLoan && !loans.RetireOriginalDeviceLoan(originalLoan))
                    return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original acquired loan retirement remains unknown.");
                if (!CurrentIoGeneration(fact)) return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original pre-device source changed after loan retirement.");
                lock (sync)
                {
                    if (!OriginalIoSources(fact) || !HasNeverInvokedDeviceProof(fact) || fact.BindCleanup is not null || fact.OriginalBindCleanup is not null)
                        return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original pre-device cleanup custody changed.");
                    fact.BindCleanup = fact.OriginalBindCleanup = new(request, true, true);
                    fact.Consumed = true;
                    return Ok(fact.BindCleanup);
                }
            }
            if (fact.Device is null && childRuntime is IHybridCpuOriginalParentDeviceNoEffectRuntimeV1 admission)
            {
                ExternalOriginalParentDeviceNoEffect? denied = fact.DeviceNoEffect ?? admission.ResolveOriginalParentDeviceNoEffect(fact.DeviceRequest!);
                lock (sync) fact.DeviceNoEffect = denied; // Before query/callback validation; malformed positive is never replaced.
                if (denied is not null)
                {
                    if (!denied.NoEffect || !denied.NoFurtherAdmission || !ReferenceEquals(denied.Request, fact.DeviceRequest) ||
                        denied.ContractVersion != externalFeatures.ContractVersion || denied.ManifestGeneration != externalFeatures.Generation ||
                        !ReferenceEquals(denied, admission.ResolveOriginalParentDeviceNoEffect(fact.DeviceRequest!)))
                        return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original no-effect fact invalid; pins retained.");
                    lock (sync)
                    {
                        if (!ReferenceEquals(fact.DeviceNoEffect, denied) || fact.ValidatedDeviceNoEffect is { } prior && !ReferenceEquals(prior, denied))
                            return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original no-effect custody changed.");
                        fact.ValidatedDeviceNoEffect = denied; fact.DeviceNoEffectValidated = true;
                    }
                    if (!CurrentIoGeneration(fact)) return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original no-effect source changed.");
                }
            }
            ExternalOriginalVirtualIoContainmentClosure? raw = fact.Containment;
            ExternalOriginalVirtualIoContainmentRequest? containmentRequest=null;
            if (!fact.DeviceNoEffectValidated)
            {
                bool invoke;
                lock (sync)
                {
                    if(!HasOriginalContainmentIntent(fact) || !ReferenceEquals(fact.Containment,fact.OriginalContainment))
                        return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous,"Original containment intent custody changed.");
                    if(fact.OriginalContainmentRequest is null)
                    {
                        if (nextOperation is 0 or ulong.MaxValue) return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Containment operation capacity exhausted.");
                        fact.OriginalContainmentRequest=fact.ContainmentRequest=new(fact.DeviceRequest!,Op());
                    }
                    containmentRequest=fact.OriginalContainmentRequest;
                    invoke=!fact.ContainmentInvoked;
                    if(invoke)fact.ContainmentInvoked=true;
                }
                if(invoke)
                {
                    ExternalOriginalVirtualIoContainmentClosure? produced=null;
                    try { produced=native.ContainOriginalVirtualIoBacking(containmentRequest); }
                    catch(Exception exception) when(exception is not StackOverflowException) { }
                    lock(sync)if(!RetainOriginalContainment(fact,produced) || !HasOriginalContainmentIntent(fact))
                        return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous,"Original containment reply custody changed.");
                }
                ExternalOriginalVirtualIoContainmentClosure? canonical=native.ResolveOriginalVirtualIoBackingContainment(containmentRequest);
                lock(sync)
                {
                    if(!RetainOriginalContainment(fact,canonical) || !HasOriginalContainmentIntent(fact))
                        return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous,"Original containment query custody changed.");
                    raw=fact.Containment;
                }
                if(raw is not {NoFurtherUse:true} || !ReferenceEquals(raw,canonical) || !ReferenceEquals(raw.Request,containmentRequest) ||
                    raw.ContractVersion!=externalFeatures.ContractVersion || raw.ManifestGeneration!=externalFeatures.Generation)
                    return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous,"Original containment unknown; no repeat.");
            }
            lock (sync) if (!fact.DeviceNoEffectValidated)
            {
                if (!HasOriginalContainmentIntent(fact) || !ReferenceEquals(fact.Containment,fact.OriginalContainment) ||
                    !ReferenceEquals(fact.Containment, raw) || fact.ValidatedContainment is { } prior && !ReferenceEquals(prior, raw))
                    return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original containment custody changed.");
                fact.ValidatedContainment = raw; fact.ContainmentValidated = true;
            }
            if (!CurrentIoGeneration(fact)) return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original cleanup source changed before loan retirement.");
            if(!fact.DeviceNoEffectValidated && !ReferenceEquals(raw,native.ResolveOriginalVirtualIoBackingContainment(containmentRequest!)))
                return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous,"Original containment changed before loan retirement.");
            lock(sync)if(!HasOriginalBindCleanupProof(fact))
                return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous,"Original cleanup custody changed before loan retirement.");
            if (!loans.RetireOriginalDeviceLoan(fact.Loan!) || !CurrentIoGeneration(fact))
                return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original native loan/source unresolved.");
            if(!fact.DeviceNoEffectValidated && !ReferenceEquals(raw,native.ResolveOriginalVirtualIoBackingContainment(containmentRequest!)))
                return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous,"Original containment changed before local publication.");
            lock (sync)
            {
                if (!OriginalIoSources(fact) || fact.Child.StateRevisionExhausted || fact.Child.StateRevision != fact.CloseRevision || !HasOriginalBindCleanupProof(fact))
                    return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original Bind cleanup commit changed.");
                fact.BindCleanup = fact.OriginalBindCleanup = new(request, true, fact.DeviceNoEffectValidated); fact.Consumed = true; ioBindings.Remove(fact.Lease.Handle); return Ok(fact.BindCleanup);
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return Fail<NeutralOriginalVirtualIoBindCleanupClosure>(NeutralVirtualizationStatus.Ambiguous, "Original cleanup retained after " + exception.GetType().Name); }
        finally { lock (sync) fact.InFlight = false; }
    }
}
