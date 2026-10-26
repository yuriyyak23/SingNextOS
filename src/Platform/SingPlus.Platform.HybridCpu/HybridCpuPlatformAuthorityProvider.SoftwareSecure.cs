using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformSoftwareSecureAdmissionProvider
{
    private sealed class SoftwareSecureFact(PlatformSoftwareSecureAdmissionRequest request,
        ChildRecord child, INeutralSoftwareSecureAdmissionProvider provider, ArtifactRecord? artifact = null)
    {
        internal readonly PlatformSoftwareSecureAdmissionRequest Request = request;
        internal readonly ChildRecord Child = child;
        internal readonly INeutralSoftwareSecureAdmissionProvider Provider = provider;
        internal readonly NeutralSoftwareSecureAdmissionRequest Native = new(child.Neutral,
            request.Operation, request.Generation, request.Left, request.Right,
            request.ExecuteRegisterLocal ? NeutralSoftwareSecureProfile.RegisterLocalExecution : NeutralSoftwareSecureProfile.NoEffectRegisterLocal);
        internal readonly ArtifactRecord? Artifact = artifact;
        internal readonly NeutralSoftwareSecureArtifactEntryRequest? NativeArtifact = artifact is null ? null :
            new(child.Neutral, artifact.Neutral, request.Operation, request.Generation, request.Left, request.Right);
        internal bool Active = true;
        internal PlatformSoftwareSecureAdmissionObservation? Closed;
    }
    private readonly Dictionary<Guid, SoftwareSecureFact> _softwareSecureFacts = [];
    private readonly List<SoftwareSecureFact> _softwareSecureOwners = [];
    private ulong _softwareSecureIssued;
    private bool HasSoftwareSecureInventory() => _softwareSecureIssued == (ulong)_softwareSecureOwners.Count &&
        _softwareSecureFacts.Count == _softwareSecureOwners.Count &&
        _softwareSecureOwners.Distinct(ReferenceEqualityComparer.Instance).Count() == _softwareSecureOwners.Count &&
        _softwareSecureOwners.All(x => _softwareSecureFacts.TryGetValue(x.Request.Operation, out var route) && ReferenceEquals(route, x));

    private bool HasOriginalSoftwareSecureFact(SoftwareSecureFact fact) =>
        HasSoftwareSecureInventory() && _softwareSecureOwners.Count(x => ReferenceEquals(x, fact)) == 1 &&
        _softwareSecureFacts.TryGetValue(fact.Request.Operation, out var routed) && ReferenceEquals(routed, fact);

    private bool HasSoftwareSecurePins(ChildRecord child) => HasPlatformSoftwareMemoryPins(child) || !HasSoftwareSecureInventory() || _softwareSecureOwners.Any(f =>
        ReferenceEquals(f.Child, child) && (!HasOriginalSoftwareSecureFact(f) || f.Active || f.Closed is null));

    public PlatformSoftwareSecureAdmissionObservation ObserveSoftwareSecureAdmission(PlatformSoftwareSecureAdmissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        SoftwareSecureFact fact;
        lock (_childCreateCommitGate)
        {
            if (_softwareSecureFacts.TryGetValue(request.Operation, out var previous))
                return HasOriginalSoftwareSecureFact(previous) && ReferenceEquals(previous.Request, request) && !previous.Active && previous.Closed is { } closed
                    ? closed : new(request, false);
            if (request.Operation == Guid.Empty || request.Generation == 0 || _softwareSecureFacts.Count >= 4096 ||
                _childRuntime is not INeutralSoftwareSecureAdmissionProvider provider ||
                !_providerChildren.TryGetValue(request.Child.LeaseId, out var child) ||
                !IsOriginalChildOwner(child) || child.Lease != request.Child || child.CloseInFlight ||
                child.ClosureReceipt is not null || child.FailedClose is not null || child.TransitionInFlight || HasSoftwareSecurePins(child))
                return new(request, false);
            ArtifactRecord? artifact = null;
            if (request.Artifact is { } receipt && (!request.ExecuteRegisterLocal ||
                _childRuntime is not INeutralSoftwareSecureArtifactEntryProvider ||
                !_originalProviderArtifacts.TryGetValue(receipt.ArtifactId, out artifact) || artifact.Receipt != receipt ||
                artifact.BindMapping?.MapChild is not { } artifactChild || !ReferenceEquals(artifactChild, child) ||
                !HasOriginalArtifactReleaseSources(artifact) || !artifact.AdmissionPublished || artifact.ExecutionsIssued != 0 ||
                artifact.OriginalExecution is not null || artifact.ReleaseRequest is not null || artifact.ReleaseInvoked || artifact.ReleaseInFlight || artifact.Released))
                return new(request, true);
            fact = new(request, child, provider, artifact);

            _softwareSecureFacts.Add(request.Operation, fact);
            _softwareSecureOwners.Add(fact);
            _softwareSecureIssued++;
        }
        return ObserveSoftwareSecureFact(fact, false);
    }

    public PlatformSoftwareSecureAdmissionObservation ResolveSoftwareSecureAdmission(PlatformSoftwareSecureAdmissionRequest original)
    {
        SoftwareSecureFact fact;
        lock (_childCreateCommitGate)
        {
            if (!_softwareSecureFacts.TryGetValue(original.Operation, out fact!) ||
                !HasOriginalSoftwareSecureFact(fact) || !ReferenceEquals(fact.Request, original) || fact.Active) return new(original, false);
            if (fact.Closed is { } closed) return closed;
            fact.Active = true;
        }
        return ObserveSoftwareSecureFact(fact, true);
    }

    private PlatformSoftwareSecureAdmissionObservation ObserveSoftwareSecureFact(SoftwareSecureFact fact, bool resolve)
    {
        try
        {
            if (fact.Request.Artifact is not null && fact.NativeArtifact is null) return new(fact.Request, false);
            if (fact.NativeArtifact is { } originalArtifact)
            {
                var provider = (INeutralSoftwareSecureArtifactEntryProvider)fact.Provider;
                var observed = resolve ? provider.ResolveSoftwareSecureArtifactEntry(originalArtifact) : provider.ExecuteSoftwareSecureArtifactEntry(originalArtifact);
                lock (_childCreateCommitGate)
                {
                    if (HasOriginalSoftwareSecureFact(fact) && ReferenceEquals(observed.Request, originalArtifact) && observed.ResourcesClosed &&
                        observed.Status is NeutralSoftwareSecureAdmissionStatus.RegisterLocalRetired or NeutralSoftwareSecureAdmissionStatus.Denied or NeutralSoftwareSecureAdmissionStatus.Unsupported)
                        return fact.Closed = new(fact.Request, true,
                            observed.Status == NeutralSoftwareSecureAdmissionStatus.RegisterLocalRetired ? observed.MachineAttempt : 0, 0,
                            observed.Status == NeutralSoftwareSecureAdmissionStatus.RegisterLocalRetired ? observed.Value : null,
                            observed.Status == NeutralSoftwareSecureAdmissionStatus.RegisterLocalRetired ? observed.Commit : 0);
                    return new(fact.Request, false);
                }
            }
            NeutralSoftwareSecureAdmissionObservation raw;
            if (fact.Request.ExecuteRegisterLocal)
            {
                if (fact.Provider is not INeutralSoftwareSecureRegisterLocalProvider arithmetic)
                    return fact.Closed = new(fact.Request, true); // Exact never-invoked local disposition.
                raw = resolve ? arithmetic.ResolveSoftwareSecureRegisterLocal(fact.Native)
                    : arithmetic.ExecuteSoftwareSecureRegisterLocal(fact.Native);
            }
            else raw = resolve ? fact.Provider.ResolveSoftwareSecureAdmission(fact.Native)
                : fact.Provider.ObserveSoftwareSecureAdmission(fact.Native);
            lock (_childCreateCommitGate)
            {
                // Closure is independent of source permission and historical machine visibility.
                if (HasOriginalSoftwareSecureFact(fact) && ReferenceEquals(raw.Request, fact.Native) && raw.ResourcesClosed &&
                    raw.Status is NeutralSoftwareSecureAdmissionStatus.ExpectedDeny or NeutralSoftwareSecureAdmissionStatus.Denied or NeutralSoftwareSecureAdmissionStatus.Unsupported or NeutralSoftwareSecureAdmissionStatus.RegisterLocalRetired)
                    return fact.Closed = new(fact.Request, true,
                        raw.Status is NeutralSoftwareSecureAdmissionStatus.ExpectedDeny or NeutralSoftwareSecureAdmissionStatus.RegisterLocalRetired ? raw.MachineAttempt : 0,
                        raw.Status == NeutralSoftwareSecureAdmissionStatus.ExpectedDeny ? raw.NoEffectCommit : 0,
                        raw.Status == NeutralSoftwareSecureAdmissionStatus.RegisterLocalRetired && fact.Request.ExecuteRegisterLocal ? raw.RegisterLocalValue : null,
                        raw.Status == NeutralSoftwareSecureAdmissionStatus.RegisterLocalRetired && fact.Request.ExecuteRegisterLocal ? raw.RegisterLocalCommit : 0);
                return new(fact.Request, false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        { return new(fact.Request, false); }
        finally { lock (_childCreateCommitGate) fact.Active = false; }
    }
}
