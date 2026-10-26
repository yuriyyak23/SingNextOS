using SingPlus.Platform;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformSoftwareSecureMemoryReadProvider
{
    private sealed class SoftwareMemoryFact(PlatformSoftwareSecureMemoryReadRequest request, GuestRecord mapping,
        ChildRecord child, INeutralSoftwareSecureMemoryReadProvider provider)
    {
        internal SoftwareMemoryFact? OriginalSelf;
        internal readonly PlatformSoftwareSecureMemoryReadRequest Request = request;
        internal readonly GuestRecord Mapping = mapping;
        internal readonly ChildRecord Child = child;
        internal readonly INeutralSoftwareSecureMemoryReadProvider Provider = provider;
        internal readonly NeutralSoftwareSecureMemorySourceRequest SourceRequest = new(mapping.Neutral, request.Operation,
            request.Generation, request.Address, request.InitialBytes);
        internal NeutralSoftwareSecureMemorySourceLease? Source;
        internal NeutralSoftwareSecureMemoryReadRequest? Read;
        internal NeutralSoftwareSecureMemoryReadObservation? Native;
        internal PlatformSoftwareSecureMemoryReadObservation? Observation;
        internal bool Active = true, CreateInvoked, ReadInvoked;
    }
    private readonly Dictionary<Guid, SoftwareMemoryFact> _softwareMemoryFacts = [];
    private readonly List<SoftwareMemoryFact> _softwareMemoryOwners = [];
    private ulong _softwareMemoryIssued;
    private sealed class SoftwareMemoryRefusal(PlatformSoftwareSecureMemoryReadRequest request)
    {
        internal SoftwareMemoryRefusal? OriginalSelf;
        internal readonly PlatformSoftwareSecureMemoryReadRequest Request = request;
        internal readonly PlatformSoftwareSecureMemoryReadObservation Observation = new(request, true);
    }
    private readonly Dictionary<Guid, SoftwareMemoryRefusal> _softwareMemoryRefusals = [];
    private readonly List<SoftwareMemoryRefusal> _originalSoftwareMemoryRefusals = [];
    private ulong _softwareMemoryRefusalsIssued;
    private bool IntactSoftwareMemoryRefusals() => _softwareMemoryRefusalsIssued == (ulong)_originalSoftwareMemoryRefusals.Count &&
        _softwareMemoryRefusals.Count == _originalSoftwareMemoryRefusals.Count &&
        _originalSoftwareMemoryRefusals.Distinct(ReferenceEqualityComparer.Instance).Count() == _originalSoftwareMemoryRefusals.Count &&
        _originalSoftwareMemoryRefusals.All(f => ReferenceEquals(f.OriginalSelf, f) && _softwareMemoryRefusals.TryGetValue(f.Request.Operation, out var route) && ReferenceEquals(f, route));
    private PlatformSoftwareSecureMemoryReadObservation RefuseSoftwareMemory(PlatformSoftwareSecureMemoryReadRequest request)
    {
        if (!IntactSoftwareMemoryFacts() || _softwareMemoryRefusals.Count >= 1024) return new(request, false);
        var refusal = new SoftwareMemoryRefusal(request); refusal.OriginalSelf = refusal;
        _softwareMemoryRefusals.Add(request.Operation, refusal); _originalSoftwareMemoryRefusals.Add(refusal); _softwareMemoryRefusalsIssued++;
        return refusal.Observation;
    }
    private bool IntactSoftwareMemoryFacts() => IntactSoftwareMemoryRefusals() && _softwareMemoryIssued == (ulong)_softwareMemoryOwners.Count &&
        _softwareMemoryFacts.Count == _softwareMemoryOwners.Count &&
        _softwareMemoryOwners.Distinct(ReferenceEqualityComparer.Instance).Count() == _softwareMemoryOwners.Count &&
        _softwareMemoryOwners.All(f => ReferenceEquals(f.OriginalSelf, f) && _softwareMemoryFacts.TryGetValue(f.Request.Operation, out var route) && ReferenceEquals(route, f));
    private bool HasPlatformSoftwareMemoryPins(ChildRecord child) => !IntactSoftwareMemoryFacts() ||
        _softwareMemoryOwners.Any(f => ReferenceEquals(f.Child, child) && (f.Active || f.Observation?.ResourcesClosed != true));
    private bool CurrentSoftwareMemoryFact(SoftwareMemoryFact f) => IntactSoftwareMemoryFacts() && IsOriginalChildOwner(f.Child) &&
        f.Child.Lease == f.Request.Mapping.ChildLease && !f.Child.CloseInFlight && !f.Child.TransitionInFlight &&
        f.Child.ClosureReceipt is null && f.Child.FailedClose is null &&
        _providerGuestMappings.TryGetValue(f.Request.Mapping.LeaseId, out var route) && ReferenceEquals(route, f.Mapping) &&
        _originalProviderGuestMappings.TryGetValue(f.Request.Mapping.LeaseId, out var original) && ReferenceEquals(original, f.Mapping) &&
        f.Mapping.Lease == f.Request.Mapping && f.Mapping.AdmissionPublished && !f.Mapping.UnmapInFlight &&
        f.Mapping.FailedUnmap is null && f.Mapping.ClosureReceipt is null && f.Mapping.OriginalUnmapInvocation is null;

    public PlatformSoftwareSecureMemoryReadObservation ExecuteSoftwareSecureMemoryRead(PlatformSoftwareSecureMemoryReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        SoftwareMemoryFact fact;
        lock (_childCreateCommitGate)
        {
            if (!IntactSoftwareMemoryFacts()) return new(request, false);
            if (_softwareMemoryRefusals.TryGetValue(request.Operation, out var refusal))
                return ReferenceEquals(refusal.Request, request) ? refusal.Observation : new(request, false);
            if (_softwareMemoryFacts.TryGetValue(request.Operation, out var previous))
                return IntactSoftwareMemoryFacts() && ReferenceEquals(previous.Request, request) && !previous.Active && previous.Observation is not null
                    ? previous.Observation : new(request, false);
            if (_softwareMemoryFacts.Count >= 1024 ||
                _childRuntime is not INeutralSoftwareSecureMemoryReadProvider provider ||
                !_providerGuestMappings.TryGetValue(request.Mapping.LeaseId, out var mapping) || mapping.Lease != request.Mapping ||
                mapping.MapChild is not { } child || !IsOriginalChildOwner(child) || HasSoftwareSecurePins(child)) return RefuseSoftwareMemory(request);
            fact = new(request, mapping, child, provider); fact.OriginalSelf = fact;
            _softwareMemoryFacts.Add(request.Operation, fact); _softwareMemoryOwners.Add(fact); _softwareMemoryIssued++;
        }
        return RunSoftwareMemoryFact(fact, false);
    }
    public PlatformSoftwareSecureMemoryReadObservation ResolveSoftwareSecureMemoryRead(PlatformSoftwareSecureMemoryReadRequest original)
    {
        ArgumentNullException.ThrowIfNull(original);
        SoftwareMemoryFact fact;
        lock (_childCreateCommitGate)
        {
            if (IntactSoftwareMemoryFacts() && _softwareMemoryRefusals.TryGetValue(original.Operation, out var refusal))
                return ReferenceEquals(refusal.Request, original) ? refusal.Observation : new(original, false);
            if (!IntactSoftwareMemoryFacts() || !_softwareMemoryFacts.TryGetValue(original.Operation, out fact!) ||
                !ReferenceEquals(fact.Request, original) || fact.Active) return new(original, false);
            if (fact.Observation?.ResourcesClosed == true) return fact.Observation;
            fact.Active = true;
        }
        return RunSoftwareMemoryFact(fact, true);
    }
    private PlatformSoftwareSecureMemoryReadObservation RunSoftwareMemoryFact(SoftwareMemoryFact fact, bool resolve)
    {
        try
        {
            if (!resolve)
            {
                lock (_childCreateCommitGate)
                    if (!CurrentSoftwareMemoryFact(fact)) return fact.Observation = new(fact.Request, true);
                fact.CreateInvoked = true;
                fact.Source = fact.Provider.CreateSoftwareSecureMemorySource(fact.SourceRequest);
                if (fact.Source is not null && ReferenceEquals(fact.Provider.ResolveSoftwareSecureMemorySource(fact.SourceRequest), fact.Source))
                {
                    fact.Read = new(fact.Source, Guid.NewGuid(), fact.Request.Generation, fact.Request.Address);
                    lock (_childCreateCommitGate)
                        if (!CurrentSoftwareMemoryFact(fact)) return new(fact.Request, false);
                    fact.ReadInvoked = true;
                    fact.Native = fact.Provider.ExecuteSoftwareSecureMemoryRead(fact.Read);
                }
            }
            // Exact surviving original lookup never invokes Create/Execute again.
            if (fact.ReadInvoked && fact.Read is { } read)
            {
                var original = fact.Provider.ResolveSoftwareSecureMemoryRead(read);
                if (!ReferenceEquals(original.Request, read) || !original.ResourcesClosed) return new(fact.Request, false);
                if (fact.Native is null) fact.Native = original;
                else if (!ReferenceEquals(fact.Native, original)) return new(fact.Request, false);
            }
            if (!fact.CreateInvoked || !fact.Provider.CloseOriginalSoftwareSecureMemorySource(fact.SourceRequest) ||
                !fact.Provider.IsOriginalSoftwareSecureMemorySourceClosed(fact.SourceRequest)) return new(fact.Request, false);
            lock (_childCreateCommitGate)
            {
                if (!IntactSoftwareMemoryFacts()) return new(fact.Request, false);
                var retired = !resolve && CurrentSoftwareMemoryFact(fact) && fact.Native?.Status == NeutralSoftwareSecureMemoryReadStatus.LoadRetired ? fact.Native : null;
                return fact.Observation = new(fact.Request, true, retired?.MachineAttempt ?? 0, retired?.Commit ?? 0, retired?.Value);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { return new(fact.Request, false); }
        finally { lock (_childCreateCommitGate) fact.Active = false; }
    }
}
