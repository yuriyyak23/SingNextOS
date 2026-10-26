using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;
using YAKSys_Hybrid_CPU.Core;

namespace YAKSys_Hybrid_CPU.ExecutableAdapter;

public sealed partial class HybridCpuExecutableChildAdapter : INeutralSoftwareSecureAdmissionProvider, INeutralSoftwareSecureRegisterLocalProvider
{
    private sealed class SoftwareAdmission
    {
        internal SoftwareAdmission(Child child, NeutralSoftwareSecureAdmissionRequest request)
        {
            OriginalOwner = this; Child = child; Request = request; Revision = child.StateRevision;
            Provider = new(child.Parent, 1);
            if (request.Profile == NeutralSoftwareSecureProfile.RegisterLocalExecution)
            { CoreHost = new(Provider); Session = CoreHost.Session; }
            else Session = new(Provider);
            Create = new(child.Parent, new(Guid.NewGuid()), new(ExternalSecureAssuranceClass.Basic, false, false), ExternalSecureComputeContract.Version);
            CloseOperation = new(new(Guid.NewGuid()), new(1));
        }
        internal readonly SoftwareAdmission OriginalOwner;
        internal readonly Child Child;
        internal readonly NeutralSoftwareSecureAdmissionRequest Request;
        internal readonly long Revision;
        internal readonly SoftwareSecureComputeProvider Provider;
        internal readonly ExternalSecureDomainAdapterSession Session;
        internal readonly SoftwareSecureRegisterLocalCoreHost? CoreHost;
        internal SoftwareSecureRegisterLocalCoreObservation? CoreObservation;
        internal readonly ExternalSecureDomainCreateRequest Create;
        internal readonly ExternalOperationIdentity CloseOperation;
        internal ExternalSecureDomainCreateProviderResult? Creation, CanonicalCreation;
        internal ExternalSecureRegisterLocalRequest? NativeRequest;
        internal SecureComputeCanonicalAdmissionResult? Admission;
        internal ExternalSecureDomainCloseProviderResult? CloseObservation, CanonicalClose;
        internal NeutralSoftwareSecureAdmissionObservation? Published;
        internal bool Active, CreateInvoked, AdmissionInvoked, CloseInvoked;
    }

    private readonly Dictionary<Guid, SoftwareAdmission> softwareAdmissions = [];
    // Test observation seam; it supplies neither permission nor closure evidence.
    internal Action? BeforeSoftwareSecureAdmissionPublication { get; set; }

    private bool HasSoftwareAdmissionHistory(Child child) =>
        child.OriginalSoftwareAdmissions.Count == (long)child.SoftwareAdmissionsIssued &&
        child.OriginalSoftwareAdmissions.Distinct(ReferenceEqualityComparer.Instance).Count() == child.OriginalSoftwareAdmissions.Count &&
        child.OriginalSoftwareAdmissions.All(f => f is not null && ReferenceEquals(f.OriginalOwner, f) && ReferenceEquals(f.Child, child) &&
            softwareAdmissions.TryGetValue(f.Request.Operation, out var original) && ReferenceEquals(original, f));

    private bool HasOriginalSoftwareAdmission(SoftwareAdmission f) => HasSoftwareAdmissionHistory(f.Child) &&
        f.Child.OriginalSoftwareAdmissions.Contains(f);

    private bool HasClosedSoftwareAdmission(SoftwareAdmission f) => HasOriginalSoftwareAdmission(f) &&
        f.Creation?.Receipt is { } created && ReferenceEquals(created.Request, f.Create) &&
        ReferenceEquals(f.CanonicalCreation?.Receipt, created) && created.Request.Parent.Equals(f.Child.Parent) &&
        f.CloseObservation?.Receipt is { IsTerminal: true } closed &&
        ReferenceEquals(f.CanonicalClose?.Receipt, closed) && closed.Domain.Equals(created.Domain) && closed.Operation.Equals(f.CloseOperation);

    private bool HasSoftwareAdmissionPins(Child child) =>
        HasSoftwareMemoryPins(child) || HasSoftwareArtifactEntryPins(child) || !HasSoftwareAdmissionHistory(child) ||
        child.OriginalSoftwareAdmissions.Any(f => f.Active || !ReferenceEquals(f.Child, child) || !HasClosedSoftwareAdmission(f));

    private bool HasCurrentSoftwareAdmissionSource(SoftwareAdmission f) => HasOriginalSoftwareAdmission(f) &&
        originalChildren.TryGetValue(f.Request.Child.Handle, out var original) && ReferenceEquals(original, f.Child) &&
        children.TryGetValue(f.Request.Child.Handle, out var route) && ReferenceEquals(route, f.Child) &&
        f.Child.Neutral == f.Request.Child && !f.Child.StateRevisionExhausted && f.Child.StateRevision == f.Revision &&
        f.Child.State is NeutralChildDomainState.Created or NeutralChildDomainState.Running or NeutralChildDomainState.Parked &&
        f.Child.ParentSource is { } parent && HasOriginalParentClosureSources(parent) &&
        parent.CloseRequest is null && !parent.CloseInvoked && !parent.CloseInFlight && !pendingChildCloses.Contains(f.Child.Neutral.Handle);

    public NeutralSoftwareSecureAdmissionObservation ObserveSoftwareSecureAdmission(NeutralSoftwareSecureAdmissionRequest request) =>
        ObserveSoftwareSecureAdmission(request, false);

    public NeutralSoftwareSecureAdmissionObservation ExecuteSoftwareSecureRegisterLocal(NeutralSoftwareSecureAdmissionRequest request) =>
        ObserveSoftwareSecureAdmission(request, true);

    public NeutralSoftwareSecureAdmissionObservation ResolveSoftwareSecureRegisterLocal(NeutralSoftwareSecureAdmissionRequest original)
    {
        ArgumentNullException.ThrowIfNull(original);
        return original.Profile == NeutralSoftwareSecureProfile.RegisterLocalExecution ? ResolveSoftwareSecureAdmission(original)
            : new(original, NeutralSoftwareSecureAdmissionStatus.Unsupported);
    }

    private NeutralSoftwareSecureAdmissionObservation ObserveSoftwareSecureAdmission(NeutralSoftwareSecureAdmissionRequest request, bool execute)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SchemaVersion != 1 || request.Profile != (execute ? NeutralSoftwareSecureProfile.RegisterLocalExecution : NeutralSoftwareSecureProfile.NoEffectRegisterLocal))
        {
            lock (sync)
                return softwareAdmissions.ContainsKey(request.Operation)
                    ? new(request, NeutralSoftwareSecureAdmissionStatus.CleanupRequired)
                    : new(request, NeutralSoftwareSecureAdmissionStatus.Unsupported, resourcesClosed: true);
        }
        SoftwareAdmission f;
        lock (sync)
        {
            if (softwareAdmissions.TryGetValue(request.Operation, out var previous))
                return previous is not null && ReferenceEquals(previous.Request, request) && HasOriginalSoftwareAdmission(previous) && previous.Published is { } published && HasClosedSoftwareAdmission(previous)
                    ? published : new(request, NeutralSoftwareSecureAdmissionStatus.CleanupRequired);
            if (!TryChild(request.Child, out var child, out _) || child is null ||
                child.State is not (NeutralChildDomainState.Created or NeutralChildDomainState.Running or NeutralChildDomainState.Parked) ||
                child.CloseMayHaveEffect || child.ParentSource is not { } parent || !HasOriginalParentClosureSources(parent) || parent.CloseRequest is not null || parent.CloseInvoked || parent.CloseInFlight ||
                child.OriginalSoftwareAdmissions.Count >= 128 || HasSoftwareAdmissionPins(child))
                return new(request, NeutralSoftwareSecureAdmissionStatus.Denied, resourcesClosed: true);
            f = new(child, request) { Active = true };
            softwareAdmissions.Add(request.Operation, f);
            child.OriginalSoftwareAdmissions.Add(f); child.SoftwareAdmissionsIssued++;
        }
        try
        {
            f.CreateInvoked = true;
            if (f.Session.Create(f.Create) != ExternalSecureDomainAdapterStatus.Accepted)
                return new(request, NeutralSoftwareSecureAdmissionStatus.CleanupRequired);
            f.Creation = f.Provider.QueryCreate(f.Create);
            f.CanonicalCreation = f.Provider.QueryCreate(f.Create);
            var domain = f.Creation.Receipt!.Domain;
            var binding = new ExternalSecureExecutionBinding(f.Child.External, domain, f.Child.Parent, 1, ExternalSecureComputeContract.Version);
            if (!f.Provider.RegisterBinding(binding)) return new(request, NeutralSoftwareSecureAdmissionStatus.CleanupRequired);
            f.NativeRequest = new(binding, new(new(request.Operation), new(request.Generation)), request.Left, request.Right);
            lock (sync)
                if (!HasCurrentSoftwareAdmissionSource(f)) return new(request, NeutralSoftwareSecureAdmissionStatus.CleanupRequired);
            f.AdmissionInvoked = true;
            f.Admission = execute ? f.CoreHost!.ExecuteRegisterLocal(f.NativeRequest) : f.Session.AdmitRegisterLocal(f.NativeRequest);
            if (execute) f.CoreObservation = f.CoreHost!.QueryCoreObservation(f.NativeRequest, f.Admission);
            CloseSoftwareAdmission(f);
            BeforeSoftwareSecureAdmissionPublication?.Invoke();
            lock (sync) return PublishSoftwareAdmission(f);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        { return new(request, NeutralSoftwareSecureAdmissionStatus.CleanupRequired); }
        finally
        {
            // Independent original cleanup survives source/publication loss.
            try { CloseSoftwareAdmission(f); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { }
            lock (sync) f.Active = false;
        }
    }

    private void CloseSoftwareAdmission(SoftwareAdmission f)
    {
        if (!f.CreateInvoked) return;
        if (f.Creation is null)
        {
            f.Session.ReconcileCreation(f.Create);
            f.Creation = f.Provider.QueryCreate(f.Create);
            f.CanonicalCreation = f.Provider.QueryCreate(f.Create);
        }
        if (f.Creation.Receipt is not { } created || !ReferenceEquals(created.Request, f.Create) ||
            !ReferenceEquals(f.CanonicalCreation?.Receipt, created) || !created.Request.Parent.Equals(f.Child.Parent)) return;
        if (f.NativeRequest is { } native && f.Admission?.Decision == SecureComputeCanonicalAdmissionDecision.DeniedBackendCleanupRequired)
            if (f.Session.ReconcileBackendReservation(native) != ExternalSecureDomainAdapterStatus.Accepted) return;
        if (f.CloseInvoked) f.Session.ReconcileClose(created.Domain, f.CloseOperation);
        else { f.CloseInvoked = true; f.Session.Close(created.Domain, f.CloseOperation); }
        f.CloseObservation = f.Provider.QueryClose(created.Domain, f.CloseOperation);
        f.CanonicalClose = f.Provider.QueryClose(created.Domain, f.CloseOperation);
    }

    private NeutralSoftwareSecureAdmissionObservation PublishSoftwareAdmission(SoftwareAdmission f)
    {
        if (!HasClosedSoftwareAdmission(f)) return new(f.Request, NeutralSoftwareSecureAdmissionStatus.CleanupRequired);
        if (f.Published is { } previous) return previous; // historical query, no authority
        if (f.Request.Profile == NeutralSoftwareSecureProfile.RegisterLocalExecution)
        {
            if (!HasCurrentSoftwareAdmissionSource(f) || f.Admission is not { IsAllowed: true, RegisterLocalRetirement: { EffectCount: 1 } arithmetic } ||
                !ReferenceEquals(arithmetic.Submission.Attempt.Request, f.NativeRequest) ||
                f.CoreHost is null || !ReferenceEquals(f.CoreHost.Session, f.Session) ||
                f.CoreObservation is not { } core ||
                !ReferenceEquals(core, f.CoreHost.QueryCoreObservation(f.NativeRequest!, f.Admission)) ||
                !ReferenceEquals(core.Request, f.NativeRequest) || !ReferenceEquals(core.Admission, f.Admission) ||
                core.CoreValue != arithmetic.Register3Value || arithmetic.Submission.Attempt.OwnerContext != 0 ||
                arithmetic.Submission.Attempt.SourceSlot != 2 || arithmetic.Submission.Attempt.WorkingSlot != 2 ||
                arithmetic.Register3Value != unchecked(f.Request.Left + f.Request.Right))
                return new(f.Request, NeutralSoftwareSecureAdmissionStatus.Denied, resourcesClosed: true);
            return f.Published = new(f.Request, NeutralSoftwareSecureAdmissionStatus.RegisterLocalRetired,
                arithmetic.Submission.Attempt.CpuAttempt, resourcesClosed: true,
                registerLocalValue: arithmetic.Register3Value, registerLocalCommit: arithmetic.CommitSequence);
        }
        if (!HasCurrentSoftwareAdmissionSource(f) || f.Admission is not { IsAllowed: false, NoEffectRetirement: { EffectCount: 0 } retired } ||
            !ReferenceEquals(retired.Submission.Attempt.Request, f.NativeRequest))
            return new(f.Request, NeutralSoftwareSecureAdmissionStatus.Denied, resourcesClosed: true);
        return f.Published = new(f.Request, NeutralSoftwareSecureAdmissionStatus.ExpectedDeny,
            retired.Submission.Attempt.CpuAttempt, retired.CommitSequence, resourcesClosed: true);
    }

    public NeutralSoftwareSecureAdmissionObservation ResolveSoftwareSecureAdmission(NeutralSoftwareSecureAdmissionRequest originalRequest)
    {
        ArgumentNullException.ThrowIfNull(originalRequest);
        SoftwareAdmission f;
        lock (sync)
        {
            if (!softwareAdmissions.TryGetValue(originalRequest.Operation, out f!) || f is null || !ReferenceEquals(f.Request, originalRequest) ||
                !HasOriginalSoftwareAdmission(f) || f.Active)
                return new(originalRequest, NeutralSoftwareSecureAdmissionStatus.CleanupRequired);
            f.Active = true;
        }
        try { CloseSoftwareAdmission(f); lock (sync) return PublishSoftwareAdmission(f); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        { return new(originalRequest, NeutralSoftwareSecureAdmissionStatus.CleanupRequired); }
        finally { lock (sync) f.Active = false; }
    }
}
