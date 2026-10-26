using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    private sealed class SoftwareArtifactLifetime(ExecutableArtifactRecord owner, PlatformSoftwareSecureAdmissionRequest request)
    {
        internal readonly ExecutableArtifactRecord Owner = owner;
        internal readonly PlatformSoftwareSecureAdmissionRequest Request = request;
    }
    private readonly Dictionary<Guid, SoftwareArtifactLifetime> _softwareArtifactLifetimes = [];
    private readonly List<SoftwareArtifactLifetime> _softwareArtifactOwners = [];
    private ulong _softwareArtifactIssued;
    private bool HasSoftwareArtifactInventory() => (ulong)_softwareArtifactOwners.Count == _softwareArtifactIssued &&
        _softwareArtifactLifetimes.Count == _softwareArtifactOwners.Count &&
        _softwareArtifactOwners.Distinct(ReferenceEqualityComparer.Instance).Count() == _softwareArtifactOwners.Count &&
        _softwareArtifactOwners.All(x => _softwareArtifactLifetimes.TryGetValue(x.Request.Operation, out var route) && ReferenceEquals(route, x));
    private bool HasSoftwareArtifactHistory(ExecutableArtifactRecord artifact) => !HasSoftwareArtifactInventory() || artifact.SoftwareEntryRequest is not null ||
        artifact.SoftwareEntryClosure is not null || artifact.OriginalSoftwareEntryClosure is not null || artifact.SoftwareEntryActive ||
        _softwareArtifactOwners.Any(x => ReferenceEquals(x.Owner, artifact));
    private bool HasOriginalSoftwareArtifactLifetime(ExecutableArtifactRecord artifact, PlatformSoftwareSecureAdmissionRequest request) =>
        HasSoftwareArtifactInventory() && _softwareArtifactLifetimes.TryGetValue(request.Operation, out var original) && ReferenceEquals(original.Owner, artifact) &&
        ReferenceEquals(original.Request, request) && _softwareArtifactLifetimes.Values.Count(x => ReferenceEquals(x.Owner, artifact)) == 1;
    internal sealed record PreparedSoftwareSecureAdmission(PlatformSoftwareSecureAdmissionRequest Request,
        Func<Func<KernelResult>, KernelResult> CommitSource);

    internal KernelResult<PreparedSoftwareSecureAdmission> PrepareSoftwareSecureAdmission(
        PlatformChildBinding binding, Guid operation, ulong generation, ulong left, ulong right, bool execute = false, PlatformExecutableArtifactBinding? artifactBinding = null)
    {
        lock (_secureDomainLifecycleGate)
        {
            if (_provider is not IPlatformSoftwareSecureAdmissionProvider)
                return KernelResult<PreparedSoftwareSecureAdmission>.Fail(KernelError.PlatformUnsupported,
                    "Limited software admission provider unavailable.");
            var child = ResolveChild(binding);
            if (!child.IsSuccess) return KernelResult<PreparedSoftwareSecureAdmission>.Fail(child.Error, child.Message!);
            if (child.Value!.TransitionInFlight || child.Value.CloseInFlight)
                return KernelResult<PreparedSoftwareSecureAdmission>.Fail(KernelError.PlatformBindingActive, "Child transition in flight.");
            var original = child.Value;
            var lineage = ValidateActiveChildLineage(original);
            if (!lineage.IsSuccess) return KernelResult<PreparedSoftwareSecureAdmission>.Fail(lineage.Error, lineage.Message!);
            if (!TryGetCanonicalRootRecord(binding.ParentBinding.BindingId, out var root) ||
                original.LifecycleRevisionExhausted || root.AuthorityRevisionExhausted || _backendEpochExhausted)
                return KernelResult<PreparedSoftwareSecureAdmission>.Fail(KernelError.PlatformFaulted, "Original software child/root source unavailable.");
            ExecutableArtifactRecord? artifact = null;
            if (artifactBinding is { } artifactIdentity && (!execute || artifactIdentity.Child != original.Binding ||
                !_executableArtifacts.TryGetValue(artifactIdentity.BindingId, out artifact) || artifact.Binding != artifactIdentity ||
                !CurrentSoftwareArtifactSource(artifact)))
                return KernelResult<PreparedSoftwareSecureAdmission>.Fail(KernelError.PlatformDenied, "Original artifact source unavailable.");
            var request = new PlatformSoftwareSecureAdmissionRequest(original.ProviderLease, operation, generation, left, right, execute)
                { Artifact = artifact?.ProviderReceipt };
            long childRevision = original.LifecycleRevision, rootRevision = root.AuthorityRevision;
            var epoch = BackendEpoch;
            KernelResult CommitSource(Func<KernelResult> commit)
            {
                lock (_secureDomainLifecycleGate)
                {
                    var current = ResolveChild(binding);
                    if (!current.IsSuccess || !ReferenceEquals(current.Value, original) ||
                        original.LifecycleRevisionExhausted || original.LifecycleRevision != childRevision ||
                        original.ProviderLease != request.Child || original.CloseInFlight ||
                        _backendEpochExhausted || BackendEpoch != epoch ||
                        !TryGetCanonicalRootRecord(binding.ParentBinding.BindingId, out var currentRoot) ||
                        !ReferenceEquals(currentRoot, root) || root.AuthorityRevisionExhausted || root.AuthorityRevision != rootRevision)
                        return KernelResult.Fail(KernelError.StaleGeneration, "Original software bridge source changed.");
                    if (artifact is not null && !CurrentSoftwareArtifactSource(artifact))
                        return KernelResult.Fail(KernelError.StaleGeneration, "Original artifact/mapping source changed.");
                    var active = ValidateActiveChildLineage(original);
                    return active.IsSuccess ? commit() : active;
                }
            }
            return KernelResult<PreparedSoftwareSecureAdmission>.Ok(new(request, CommitSource));
        }
    }

    private bool CurrentSoftwareArtifactSource(ExecutableArtifactRecord artifact) =>
        artifact.AdmissionPublished && !artifact.Started && !artifact.StartInFlight && artifact.StartInvocation is null &&
        (!HasSoftwareArtifactHistory(artifact) || !artifact.SoftwareEntryActive && HasClosedSoftwareArtifactEntry(artifact)) &&
        !artifact.Released && !artifact.ReleaseInvoked && !artifact.ReleaseInFlight && artifact.ReleaseRequest is null &&
        artifact.AdmissionSource is { } source && HasOriginalArtifactReleaseSources(artifact, source.Root.ProviderIncarnation) &&
        ValidateExecutableInputs(source.Child, source.Guest, source.Parent).IsSuccess;
    internal PlatformSoftwareSecureAdmissionObservation ObserveSoftwareSecureAdmission(PlatformSoftwareSecureAdmissionRequest request,
        bool resolve)
    {
        if (_provider is not IPlatformSoftwareSecureAdmissionProvider provider) return new(request, false);
        ExecutableArtifactRecord? artifact = null;
        lock (_secureDomainLifecycleGate)
        {
            if (request.Artifact is { } receipt)
            {
                artifact = _originalExecutableArtifactOwners.Values.SingleOrDefault(x => x.ProviderReceipt == receipt);
                if (artifact is null ||
                    !_originalChildOwners.TryGetValue(artifact.Binding.Child.BindingId, out var originalChild) || originalChild.ProviderLease != request.Child ||
                    artifact.SoftwareEntryActive || artifact.SoftwareEntryRequest is { } original && !ReferenceEquals(original, request))
                    return new(request, false);
                if (HasSoftwareArtifactHistory(artifact) && !HasOriginalSoftwareArtifactLifetime(artifact, request)) return new(request, false);
                if (artifact.SoftwareEntryClosure is { } closed && HasClosedSoftwareArtifactEntry(artifact)) return closed;
                if (!resolve && artifact.SoftwareEntryRequest is not null || resolve && artifact.SoftwareEntryRequest is null)
                    return new(request, false);
                if (!resolve)
                {
                    if (_softwareArtifactLifetimes.Count >= 4096 || _softwareArtifactLifetimes.ContainsKey(request.Operation)) return new(request, false);
                    var lifetime = new SoftwareArtifactLifetime(artifact, request);
                    _softwareArtifactLifetimes.Add(request.Operation, lifetime);
                    _softwareArtifactOwners.Add(lifetime); _softwareArtifactIssued++;
                }
                if (!HasOriginalSoftwareArtifactLifetime(artifact, request)) return new(request, false);
                artifact.SoftwareEntryRequest = request;
                artifact.SoftwareEntryActive = true;
            }
        }
        try
        {
            var observed = resolve ? provider.ResolveSoftwareSecureAdmission(request) : provider.ObserveSoftwareSecureAdmission(request);
            // An envelope alone cannot retire the original artifact custody.
            var canonical = artifact is not null && observed.ResourcesClosed ? provider.ResolveSoftwareSecureAdmission(request) : null;
            lock (_secureDomainLifecycleGate)
                if (artifact is not null && HasOriginalSoftwareArtifactLifetime(artifact, request) && ReferenceEquals(artifact.SoftwareEntryRequest, request) &&
                    ReferenceEquals(observed.Request, request) && observed.ResourcesClosed && ReferenceEquals(observed, canonical) &&
                    (artifact.OriginalSoftwareEntryClosure is null || ReferenceEquals(artifact.OriginalSoftwareEntryClosure, observed)))
                {
                    artifact.OriginalSoftwareEntryClosure = observed;
                    artifact.SoftwareEntryClosure = observed;
                }
            if (artifact is not null)
                lock (_secureDomainLifecycleGate)
                    if (!HasClosedSoftwareArtifactEntry(artifact)) return new(request, false);
            return observed;
        }
        finally { lock (_secureDomainLifecycleGate) if (artifact is not null) artifact.SoftwareEntryActive = false; }
    }

    private bool HasClosedSoftwareArtifactEntry(ExecutableArtifactRecord artifact) =>
        artifact.SoftwareEntryRequest is { ExecuteRegisterLocal: true, Artifact: { } receipt } request && receipt == artifact.ProviderReceipt &&
        HasOriginalSoftwareArtifactLifetime(artifact, request) &&
        artifact.SoftwareEntryClosure is { ResourcesClosed: true } closed && ReferenceEquals(closed.Request, request) &&
        ReferenceEquals(closed, artifact.OriginalSoftwareEntryClosure) &&
        _originalExecutableArtifactOwners.TryGetValue(artifact.Binding.BindingId, out var original) && ReferenceEquals(original, artifact);
}
