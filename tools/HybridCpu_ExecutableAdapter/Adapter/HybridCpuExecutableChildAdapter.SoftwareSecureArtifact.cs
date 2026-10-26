using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;
using YAKSys_Hybrid_CPU.Core;

namespace YAKSys_Hybrid_CPU.ExecutableAdapter;

public sealed partial class HybridCpuExecutableChildAdapter : INeutralSoftwareSecureArtifactEntryProvider
{
    private sealed class SoftwareArtifactEntry
    {
        internal SoftwareArtifactEntry(Child child, Artifact artifact, OriginalArtifactBindFact bind,
            NeutralSoftwareSecureArtifactEntryRequest request, HybridCpuExternalRuntime runtime)
        {
            OriginalOwner = this; Child = child; Artifact = artifact; Bind = bind; Request = request; Runtime = runtime;
            Revision = child.StateRevision; Provider = new(child.Parent, 1); Session = new(Provider);
            Create = new(child.Parent, new(Guid.NewGuid()), new(ExternalSecureAssuranceClass.Basic, false, false), ExternalSecureComputeContract.Version);
            Close = new(new(Guid.NewGuid()), new(1));
        }
        internal readonly SoftwareArtifactEntry OriginalOwner;
        internal readonly Child Child;
        internal readonly Artifact Artifact;
        internal readonly OriginalArtifactBindFact Bind;
        internal readonly NeutralSoftwareSecureArtifactEntryRequest Request;
        internal readonly HybridCpuExternalRuntime Runtime;
        internal readonly long Revision;
        internal readonly SoftwareSecureComputeProvider Provider;
        internal readonly ExternalSecureDomainAdapterSession Session;
        internal readonly ExternalSecureDomainCreateRequest Create;
        internal readonly ExternalOperationIdentity Close;
        internal ExternalSecureDomainCreateProviderResult? Creation, CanonicalCreation;
        internal ExternalSecureRegisterLocalRequest? NativeRequest;
        internal ExternalSecureExecutionReservation? Reservation;
        internal ChildSoftwareSecureEntryObservation? NativeObservation, CanonicalNative;
        internal ExternalSecureDomainCloseProviderResult? Closure, CanonicalClosure;
        internal NeutralSoftwareSecureArtifactEntryObservation? Published;
        internal bool Active = true, CreateInvoked, ReserveInvoked, NativeInvoked, CloseInvoked, ReservationReleased;
        internal volatile bool Cancelled;
    }
    private readonly Dictionary<Guid, SoftwareArtifactEntry> softwareArtifactEntries = [];
    internal Action? AfterSoftwareSecureArtifactNativeExecution { get; set; }
    internal Action? BeforeSoftwareSecureArtifactNativeExecution { get; set; }
    internal Action? BeforeSoftwareSecureArtifactPublication { get; set; }

    private bool HasSoftwareArtifactEntryHistory(Child child) =>
        child.OriginalSoftwareArtifactEntries.Count != 0 || child.SoftwareArtifactEntriesIssued != 0 ||
        softwareArtifactEntries.Values.Any(f => ReferenceEquals(f.Child, child));

    private bool IntactSoftwareArtifactEntries(Child child)
    {
        var originals = softwareArtifactEntries.Values.Where(f => ReferenceEquals(f.Child, child)).ToArray();
        return originals.Length == child.OriginalSoftwareArtifactEntries.Count && (ulong)originals.Length == child.SoftwareArtifactEntriesIssued &&
            child.OriginalSoftwareArtifactEntries.Distinct(ReferenceEqualityComparer.Instance).Count() == originals.Length &&
            originals.All(f => ReferenceEquals(f.OriginalOwner, f) && child.OriginalSoftwareArtifactEntries.Contains(f) &&
                ReferenceEquals(f.Artifact.SoftwareEntry, f));
    }
    private bool ClosedSoftwareArtifactEntry(SoftwareArtifactEntry f) =>
        f.Creation?.Receipt is { } created && ReferenceEquals(created.Request, f.Create) &&
        ReferenceEquals(f.CanonicalCreation?.Receipt, created) && created.Request.Parent == f.Child.Parent &&
        f.Closure?.Receipt is { IsTerminal: true } closed && ReferenceEquals(f.CanonicalClosure?.Receipt, closed) &&
        closed.Domain.Equals(created.Domain) && closed.Operation == f.Close &&
        (!f.ReserveInvoked || !f.NativeInvoked && f.ReservationReleased || f.NativeInvoked && f.NativeObservation is { ResourcesClosed: true } native &&
            ReferenceEquals(native, f.CanonicalNative) && ReferenceEquals(native.Request, f.NativeRequest));
    private bool HasSoftwareArtifactEntryPins(Child child) => !IntactSoftwareArtifactEntries(child) ||
        softwareArtifactEntries.Values.Any(f => ReferenceEquals(f.Child, child) && (f.Active || !ClosedSoftwareArtifactEntry(f)));

    private bool CurrentSoftwareArtifactEntry(SoftwareArtifactEntry f, bool registered = true) => !f.Cancelled && (!registered || IntactSoftwareArtifactEntries(f.Child)) &&
        !f.Child.StateRevisionExhausted && f.Child.StateRevision == f.Revision && f.Child.State == NeutralChildDomainState.Created &&
        HasOriginalBindSources(f.Bind) && HasOriginalArtifactReleaseSources(f.Artifact) &&
        ReferenceEquals(f.Artifact.BindChild, f.Child) && ReferenceEquals(f.Artifact.BindMapping, f.Bind.Mapping) &&
        ReferenceEquals(f.Bind.Mapping.ArtifactBind, f.Bind) && ReferenceEquals(f.Bind.Observation?.Receipt, f.Artifact.External) &&
        f.Artifact.Neutral == f.Request.Artifact && f.Child.Neutral == f.Request.Child && f.Artifact.AdmissionPublished &&
        f.Artifact.StartsIssued == 0 && f.Artifact.OriginalStart is null && !f.Artifact.ReleaseInvoked && !f.Artifact.ReleaseInFlight &&
        !f.Artifact.Released && f.Child.ParentSource is { } parent && HasOriginalParentClosureSources(parent) &&
        parent.CloseRequest is null && !parent.CloseInvoked && !parent.CloseInFlight;

    public NeutralSoftwareSecureArtifactEntryObservation ExecuteSoftwareSecureArtifactEntry(NeutralSoftwareSecureArtifactEntryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        SoftwareArtifactEntry f;
        lock (sync)
        {
            if (softwareArtifactEntries.TryGetValue(request.Operation, out var previous))
                return ReferenceEquals(previous.Request, request) && IntactSoftwareArtifactEntries(previous.Child) &&
                    previous.Published is { } published && ClosedSoftwareArtifactEntry(previous) ? published : new(request, NeutralSoftwareSecureAdmissionStatus.CleanupRequired);
            if (request.SchemaVersion != 1 || root is not HybridCpuExternalRuntime runtime || !ReferenceEquals(root, childRuntime))
                return new(request, NeutralSoftwareSecureAdmissionStatus.Unsupported, resourcesClosed: true);
            if (!TryChild(request.Child, out var child, out _) || child is null || child.State != NeutralChildDomainState.Created || child.NestedParent is not null ||
                HasSoftwareAdmissionPins(child) || HasSoftwareArtifactEntryHistory(child) || softwareArtifactEntries.Count >= 1024 ||
                !originalArtifacts.TryGetValue(request.Artifact.ArtifactHandle, out var artifact) || artifact.Neutral != request.Artifact ||
                !artifacts.TryGetValue(request.Artifact.ArtifactHandle, out var route) || !ReferenceEquals(route, artifact) ||
                !ReferenceEquals(artifact.BindChild, child) || artifact.BindMapping?.ArtifactBind is not { } bind ||
                pendingChildStarts.Contains(child.Neutral.Handle) || pendingChildTransitions.Contains(child.Neutral.Handle) ||
                pendingChildCloses.Contains(child.Neutral.Handle) || pendingGuestUnmaps.Contains(bind.Mapping.Neutral.Handle))
                return new(request, NeutralSoftwareSecureAdmissionStatus.Denied, resourcesClosed: true);
            f = new(child, artifact, bind, request, runtime);
            if (!CurrentSoftwareArtifactEntry(f, false)) return new(request, NeutralSoftwareSecureAdmissionStatus.Denied, resourcesClosed: true);
            softwareArtifactEntries.Add(request.Operation, f); child.OriginalSoftwareArtifactEntries.Add(f);
            child.SoftwareArtifactEntriesIssued++; artifact.SoftwareEntry = f;
            if (!CurrentSoftwareArtifactEntry(f))
            { f.Active = false; return new(request, NeutralSoftwareSecureAdmissionStatus.CleanupRequired); }
            pendingChildStarts.Add(child.Neutral.Handle);
        }
        try
        {
            f.CreateInvoked = true;
            f.Session.Create(f.Create);
            f.Creation = f.Provider.QueryCreate(f.Create); f.CanonicalCreation = f.Provider.QueryCreate(f.Create);
            if (f.Creation.Receipt is not { } created || !ReferenceEquals(f.CanonicalCreation.Receipt, created) || !ReferenceEquals(created.Request, f.Create))
                return new(request, NeutralSoftwareSecureAdmissionStatus.CleanupRequired);
            var binding = new ExternalSecureExecutionBinding(f.Child.External, created.Domain, f.Child.Parent, 1, ExternalSecureComputeContract.Version);
            if (!f.Provider.RegisterBinding(binding)) return new(request, NeutralSoftwareSecureAdmissionStatus.CleanupRequired);
            f.NativeRequest = new(binding, new(new(request.Operation), new(request.Generation)), request.Left, request.Right);
            lock (sync) if (!CurrentSoftwareArtifactEntry(f)) return new(request, NeutralSoftwareSecureAdmissionStatus.CleanupRequired);
            f.ReserveInvoked = true;
            var reserved = f.Provider.Reserve(f.NativeRequest);
            var canonical = f.Provider.QueryReservation(f.NativeRequest);
            if (reserved.Reservation is not { } reservation || !ReferenceEquals(canonical.Reservation, reservation) || !ReferenceEquals(reservation.Request, f.NativeRequest))
                return new(request, NeutralSoftwareSecureAdmissionStatus.CleanupRequired);
            f.Reservation = reservation;
            BeforeSoftwareSecureArtifactNativeExecution?.Invoke();
            lock (sync)
            {
                if (!CurrentSoftwareArtifactEntry(f)) return new(request, NeutralSoftwareSecureAdmissionStatus.CleanupRequired);
                f.NativeInvoked = true;
            }
            var native = f.Runtime.ExecuteChildSoftwareSecureArtifactEntry(f.Child.External, f.Bind.External, f.Artifact.External,
                f.NativeRequest, reservation, f.Provider);
            AfterSoftwareSecureArtifactNativeExecution?.Invoke();
            f.NativeObservation = native;
            CloseSoftwareArtifactEntry(f);
            BeforeSoftwareSecureArtifactPublication?.Invoke();
            lock (sync) return PublishSoftwareArtifactEntry(f);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { return new(request, NeutralSoftwareSecureAdmissionStatus.CleanupRequired); }
        finally
        {
            try { CloseSoftwareArtifactEntry(f); } catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { }
            lock (sync) { f.Active = false; pendingChildStarts.Remove(f.Child.Neutral.Handle); }
        }
    }

    private void CloseSoftwareArtifactEntry(SoftwareArtifactEntry f)
    {
        if (!f.CreateInvoked) return;
        f.Session.ReconcileCreation(f.Create);
        f.Creation = f.Provider.QueryCreate(f.Create); f.CanonicalCreation = f.Provider.QueryCreate(f.Create);
        if (f.Creation.Receipt is not { } created || !ReferenceEquals(created.Request, f.Create) || !ReferenceEquals(f.CanonicalCreation.Receipt, created)) return;
        if (f.NativeInvoked && f.NativeRequest is { } original)
        {
            f.NativeObservation = f.Runtime.ResolveChildSoftwareSecureEntry(f.Child.External, original);
            f.CanonicalNative = f.Runtime.ResolveChildSoftwareSecureEntry(f.Child.External, original);
            if (f.NativeObservation is not { ResourcesClosed: true } native || !ReferenceEquals(native, f.CanonicalNative) || !ReferenceEquals(native.Request, original)) return;
        }
        else if (f.ReserveInvoked)
        {
            if (f.Reservation is not { } reservation || f.NativeRequest is not { } reservedRequest ||
                !ReferenceEquals(reservation.Request, reservedRequest) ||
                !ReferenceEquals(f.Provider.QueryReservation(reservedRequest).Reservation, reservation)) return;
            f.Provider.Release(reservation);
            if (!ReferenceEquals(f.Provider.QueryRelease(reservation).Reservation, reservation) ||
                !ReferenceEquals(f.Provider.QueryRelease(reservation).Reservation, reservation)) return;
            f.ReservationReleased = true;
        }
        if (f.CloseInvoked) f.Session.ReconcileClose(created.Domain, f.Close);
        else { f.CloseInvoked = true; f.Session.Close(created.Domain, f.Close); }
        f.Closure = f.Provider.QueryClose(created.Domain, f.Close); f.CanonicalClosure = f.Provider.QueryClose(created.Domain, f.Close);
    }
    private NeutralSoftwareSecureArtifactEntryObservation PublishSoftwareArtifactEntry(SoftwareArtifactEntry f)
    {
        if (!IntactSoftwareArtifactEntries(f.Child) || !ClosedSoftwareArtifactEntry(f)) return new(f.Request, NeutralSoftwareSecureAdmissionStatus.CleanupRequired);
        if (f.Published is { } previous) return previous;
        if (!CurrentSoftwareArtifactEntry(f) || f.NativeObservation is not { ResourcesClosed: true, Result.RegisterLocalRetirement: { EffectCount: 1 } retired } native ||
            !ReferenceEquals(native.Artifact, f.Artifact.External) || native.ExecutionGeneration.Value == 0 || native.RunId == Guid.Empty ||
            !ReferenceEquals(native.Request, f.NativeRequest) || !ReferenceEquals(retired.Submission.Attempt.Request, f.NativeRequest) ||
            native.CoreValue != retired.Register3Value || native.Register3After != retired.Register3Value ||
            retired.Register3Value != unchecked(f.Request.Left + f.Request.Right))
            return f.Published = new(f.Request, NeutralSoftwareSecureAdmissionStatus.Denied, resourcesClosed: true);
        return f.Published = new(f.Request, NeutralSoftwareSecureAdmissionStatus.RegisterLocalRetired, true,
            native.RunId, native.ExecutionGeneration.Value, retired.Submission.Attempt.CpuAttempt, retired.CommitSequence, native.CoreValue);
    }
    public NeutralSoftwareSecureArtifactEntryObservation ResolveSoftwareSecureArtifactEntry(NeutralSoftwareSecureArtifactEntryRequest original)
    {
        ArgumentNullException.ThrowIfNull(original); SoftwareArtifactEntry f;
        lock (sync)
        {
            if (!softwareArtifactEntries.TryGetValue(original.Operation, out f!) || !ReferenceEquals(f.Request, original) || f.Active)
                return new(original, NeutralSoftwareSecureAdmissionStatus.CleanupRequired);
            f.Active = true;
        }
        try { CloseSoftwareArtifactEntry(f); lock (sync) return PublishSoftwareArtifactEntry(f); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { return new(original, NeutralSoftwareSecureAdmissionStatus.CleanupRequired); }
        finally { lock (sync) f.Active = false; }
    }
    public bool CancelSoftwareSecureArtifactEntry(NeutralSoftwareSecureArtifactEntryRequest original)
    {
        ArgumentNullException.ThrowIfNull(original); SoftwareArtifactEntry f;
        lock (sync) if (!softwareArtifactEntries.TryGetValue(original.Operation, out f!) || !ReferenceEquals(f.Request, original)) return false;
        RevokeSoftwareArtifactEntry(f); return true;
    }
    private static void RevokeSoftwareArtifactEntry(SoftwareArtifactEntry f)
    {
        f.Cancelled = true;
        if (f.Creation?.Receipt is { } created && ReferenceEquals(created.Request, f.Create) && ReferenceEquals(f.CanonicalCreation?.Receipt, created))
        { f.Session.RevokeLocalBinding(created.Domain); f.Provider.Revoke(created.Domain); }
        if (f.NativeInvoked && f.NativeRequest is { } original) f.Runtime.CancelChildSoftwareSecureEntry(f.Child.External, original);
    }
}
