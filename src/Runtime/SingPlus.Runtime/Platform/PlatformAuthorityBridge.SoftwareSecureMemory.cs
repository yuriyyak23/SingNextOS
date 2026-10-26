using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

/// <summary>Trusted host bridge intent, not Kernel capability permission or existing guest-memory contents.</summary>
public sealed class SoftwareSecureInitializedLoadIntent
{
    public SoftwareSecureInitializedLoadIntent(PlatformGuestMapping mapping, Guid operation, ulong generation,
        ulong address, ReadOnlyMemory<byte> initialBytes)
    {
        if (operation == Guid.Empty || generation == 0 || initialBytes.Length is < 8 or > 4096)
            throw new ArgumentException("Original bounded initialized load required.");
        Mapping = mapping; Operation = operation; Generation = generation; Address = address; _initialBytes = initialBytes.ToArray();
    }
    public PlatformGuestMapping Mapping { get; }
    public Guid Operation { get; }
    public ulong Generation { get; }
    public ulong Address { get; }
    private readonly byte[] _initialBytes;
    // A read-only view of an array still permits TryGetArray to recover its mutable owner.
    // Export a bounded snapshot so the original initializer remains privately owned.
    public ReadOnlyMemory<byte> InitialBytes => _initialBytes.ToArray();
}
public sealed record SoftwareSecureInitializedLoadObservation(SoftwareSecureInitializedLoadIntent Intent,
    bool ResourcesClosed, ulong MachineAttempt = 0, ulong Commit = 0, ulong? Value = null)
{
    public bool IsPublished => false;
}

public sealed partial class PlatformAuthorityBridge
{
    private sealed class InitializedLoadLifetime(SoftwareSecureInitializedLoadIntent intent, GuestBindingRecord guest,
        PlatformSoftwareSecureMemoryReadRequest request)
    {
        internal InitializedLoadLifetime? OriginalSelf;
        internal readonly SoftwareSecureInitializedLoadIntent Intent = intent;
        internal readonly GuestBindingRecord Guest = guest;
        internal readonly GuestAdmissionSource? Admission = guest.AdmissionSource;
        internal readonly long GuestRevision = guest.LifecycleRevision;
        internal readonly long ChildRevision = guest.AdmissionSource?.Child.LifecycleRevision ?? -1;
        internal readonly long ParentRevision = guest.AdmissionSource?.Parent.LifecycleRevision ?? -1;
        internal readonly long RootRevision = guest.AdmissionSource?.Root.AuthorityRevision ?? -1;
        internal readonly PlatformSoftwareSecureMemoryReadRequest Request = request;
        internal Action? EndPending;
        internal Func<Func<KernelResult>, KernelResult>? CurrentSource;
        internal PlatformSoftwareSecureMemoryReadObservation? OriginalClosure;
        internal SoftwareSecureInitializedLoadObservation? Observation;
        internal bool Active = true, Invoked, Cancelled;
    }
    private readonly Dictionary<Guid, InitializedLoadLifetime> _initializedLoads = [];
    private readonly List<InitializedLoadLifetime> _originalInitializedLoads = [];
    private ulong _initializedLoadsIssued;
    private sealed class InitializedLoadRefusal(SoftwareSecureInitializedLoadIntent intent)
    {
        internal InitializedLoadRefusal? OriginalSelf;
        internal readonly SoftwareSecureInitializedLoadIntent Intent = intent;
        internal readonly SoftwareSecureInitializedLoadObservation Observation = new(intent, true);
    }
    private readonly Dictionary<Guid, InitializedLoadRefusal> _initializedLoadRefusals = [];
    private readonly List<InitializedLoadRefusal> _originalInitializedLoadRefusals = [];
    private ulong _initializedLoadRefusalsIssued;
    private bool IntactInitializedLoadRefusals() => _initializedLoadRefusalsIssued == (ulong)_originalInitializedLoadRefusals.Count &&
        _initializedLoadRefusals.Count == _originalInitializedLoadRefusals.Count &&
        _originalInitializedLoadRefusals.Distinct(ReferenceEqualityComparer.Instance).Count() == _originalInitializedLoadRefusals.Count &&
        _originalInitializedLoadRefusals.All(f => ReferenceEquals(f.OriginalSelf, f) && _initializedLoadRefusals.TryGetValue(f.Intent.Operation, out var route) && ReferenceEquals(route, f));
    private SoftwareSecureInitializedLoadObservation RefuseInitializedLoad(SoftwareSecureInitializedLoadIntent intent)
    {
        if (!IntactInitializedLoads() || _initializedLoadRefusals.Count >= 1024) return new(intent, false);
        var refusal = new InitializedLoadRefusal(intent); refusal.OriginalSelf = refusal;
        _initializedLoadRefusals.Add(intent.Operation, refusal); _originalInitializedLoadRefusals.Add(refusal); _initializedLoadRefusalsIssued++;
        return refusal.Observation;
    }
    private bool IntactInitializedLoads() => IntactInitializedLoadRefusals() && _initializedLoadsIssued == (ulong)_originalInitializedLoads.Count &&
        _initializedLoads.Count == _originalInitializedLoads.Count &&
        _originalInitializedLoads.Distinct(ReferenceEqualityComparer.Instance).Count() == _originalInitializedLoads.Count &&
        _originalInitializedLoads.All(f => ReferenceEquals(f.OriginalSelf, f) && _initializedLoads.TryGetValue(f.Intent.Operation, out var route) && ReferenceEquals(route, f));
    private bool HasInitializedLoadPins(GuestBindingRecord guest) => !IntactInitializedLoads() ||
        _originalInitializedLoads.Any(f => ReferenceEquals(f.Guest, guest) &&
            (f.Active || f.Observation?.ResourcesClosed != true || f.Invoked &&
                (f.OriginalClosure is not { ResourcesClosed: true } closed || !ReferenceEquals(closed.Request, f.Request))));
    private bool InitializedLoadRevisionsCurrent(InitializedLoadLifetime fact) => !fact.Cancelled && IntactInitializedLoads() &&
        fact.Admission is { } source && ReferenceEquals(fact.Guest.AdmissionSource, source) &&
        !fact.Guest.LifecycleRevisionExhausted && fact.Guest.LifecycleRevision == fact.GuestRevision &&
        !source.Child.LifecycleRevisionExhausted && source.Child.LifecycleRevision == fact.ChildRevision &&
        !source.Parent.LifecycleRevisionExhausted && source.Parent.LifecycleRevision == fact.ParentRevision &&
        !source.Root.AuthorityRevisionExhausted && source.Root.AuthorityRevision == fact.RootRevision;
    private bool CurrentInitializedLoad(InitializedLoadLifetime fact) => InitializedLoadRevisionsCurrent(fact) &&
        fact.CurrentSource?.Invoke(KernelResult.Ok).IsSuccess == true;
    internal KernelResult ValidateInitializedLoadCleanup(PlatformGuestMapping mapping)
    {
        lock (_secureDomainLifecycleGate)
            return !IntactInitializedLoads() || !_guestBindings.TryGetValue(mapping.MappingId, out var guest) ||
                guest.Mapping != mapping || HasInitializedLoadPins(guest)
                ? KernelResult.Fail(KernelError.PlatformBindingActive, "Original initialized-load custody remains pending.")
                : KernelResult.Ok();
    }

    internal KernelResult CommitInitializedLoadSource(SoftwareSecureInitializedLoadIntent intent, Func<KernelResult> commit)
    {
        lock (_secureDomainLifecycleGate)
        {
            if (!IntactInitializedLoads() || !_initializedLoads.TryGetValue(intent.Operation, out var fact) ||
                !ReferenceEquals(fact.Intent, intent) || !InitializedLoadRevisionsCurrent(fact))
                return KernelResult.Fail(KernelError.StaleGeneration, "Original initialized-load source revisions changed.");
            var child = ResolveChild(intent.Mapping.Child);
            if (!child.IsSuccess) return KernelResult.Fail(child.Error, child.Message!);
            var lineage = ValidateActiveChildLineage(child.Value!);
            if (!lineage.IsSuccess) return lineage;
            if (fact.Observation is not { ResourcesClosed: true } || fact.Active || fact.Admission is not { } source ||
                !_guestBindings.TryGetValue(intent.Mapping.MappingId, out var guest) || !ReferenceEquals(guest, fact.Guest) ||
                guest.Mapping != intent.Mapping || !guest.AdmissionPublished || guest.ClosureInFlight ||
                guest.Closure != PlatformExternalClosureState.Active || !HasCurrentGuestAdmissionSources(guest) ||
                guest.PendingSecureRegionEffects != 0 || guest.PendingExecutableEffects != 0 ||
                !ReferenceEquals(child.Value, source.Child) || child.Value!.TransitionInFlight || child.Value.CloseInFlight ||
                !_mappings.TryGetValue(intent.Mapping.ParentMapping.MappingId, out var parent) ||
                !ReferenceEquals(parent, source.Parent) || parent.LocalAuthorizationRevoked || parent.ClosureState != PlatformExternalClosureState.Active)
                return KernelResult.Fail(KernelError.StaleGeneration, "Original post-cleanup initialized-load mapping is not current.");
            return commit(); // Original closure/current owner check, never a reopened pending lifetime.
        }
    }

    /// <summary>Low-level trusted bridge composition; Kernel semantic admission and publication are separate.</summary>
    public SoftwareSecureInitializedLoadObservation ExecuteSoftwareSecureInitializedLoad(SoftwareSecureInitializedLoadIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        InitializedLoadLifetime fact;
        lock (_secureDomainLifecycleGate)
        {
            if (!IntactInitializedLoads()) return new(intent, false);
            if (_initializedLoadRefusals.TryGetValue(intent.Operation, out var refusal))
                return ReferenceEquals(refusal.Intent, intent) ? refusal.Observation : new(intent, false);
            if (_initializedLoads.TryGetValue(intent.Operation, out var prior))
                return ReferenceEquals(prior.Intent, intent) && !prior.Active && prior.Observation is not null ? prior.Observation : new(intent, false);
            if (_provider is not IPlatformSoftwareSecureMemoryReadProvider || _initializedLoads.Count >= 1024 ||
                !_guestBindings.TryGetValue(intent.Mapping.MappingId, out var guest) || guest.Mapping != intent.Mapping)
                return RefuseInitializedLoad(intent);
            PlatformSoftwareSecureMemoryReadRequest request;
            try { request = new(guest.ProviderLease, intent.Operation, intent.Generation, intent.Address, intent.InitialBytes); }
            catch (ArgumentException) { return RefuseInitializedLoad(intent); }
            fact = new(intent, guest, request); fact.OriginalSelf = fact;
            var begin = CommitSecureGuestMapping(intent.Mapping, intent.Mapping.ParentMapping, true, () =>
            {
                _initializedLoads.Add(intent.Operation, fact); _originalInitializedLoads.Add(fact); _initializedLoadsIssued++;
                return KernelResult.Ok();
            }, out var end, out var current);
            if (!begin.IsSuccess) return RefuseInitializedLoad(intent);
            fact.EndPending = end; fact.CurrentSource = current;
        }
        return RunInitializedLoad(fact, false);
    }
    public SoftwareSecureInitializedLoadObservation ResolveSoftwareSecureInitializedLoad(SoftwareSecureInitializedLoadIntent original)
    {
        ArgumentNullException.ThrowIfNull(original);
        InitializedLoadLifetime fact;
        lock (_secureDomainLifecycleGate)
        {
            if (IntactInitializedLoads() && _initializedLoadRefusals.TryGetValue(original.Operation, out var refusal))
                return ReferenceEquals(refusal.Intent, original) ? refusal.Observation : new(original, false);
            if (!IntactInitializedLoads() || !_initializedLoads.TryGetValue(original.Operation, out fact!) ||
                !ReferenceEquals(fact.Intent, original) || fact.Active) return new(original, false);
            if (fact.Observation?.ResourcesClosed == true) return fact.Observation;
            fact.Active = true;
        }
        return RunInitializedLoad(fact, true);
    }
    /// <summary>Withdraws a pending bridge result. It certifies neither native quiescence nor cleanup.</summary>
    public bool CancelSoftwareSecureInitializedLoad(SoftwareSecureInitializedLoadIntent original)
    {
        ArgumentNullException.ThrowIfNull(original);
        lock (_secureDomainLifecycleGate)
        {
            if (!IntactInitializedLoads() || !_initializedLoads.TryGetValue(original.Operation, out var fact) ||
                !ReferenceEquals(fact.Intent, original) || fact.Observation?.ResourcesClosed == true) return false;
            fact.Cancelled = true; return true;
        }
    }
    private SoftwareSecureInitializedLoadObservation RunInitializedLoad(InitializedLoadLifetime fact, bool resolve)
    {
        try
        {
            if (_provider is not IPlatformSoftwareSecureMemoryReadProvider provider) return new(fact.Intent, false);
            if (!resolve)
            {
                lock (_secureDomainLifecycleGate)
                {
                    if (!CurrentInitializedLoad(fact)) return new(fact.Intent, false);
                    fact.Invoked = true;
                }
            }
            if (!fact.Invoked)
            {
                lock (_secureDomainLifecycleGate)
                {
                    if (!resolve || !IntactInitializedLoads()) return new(fact.Intent, false);
                    fact.EndPending?.Invoke(); fact.EndPending = null;
                    return fact.Observation = new(fact.Intent, true);
                }
            }
            var observed = resolve ? provider.ResolveSoftwareSecureMemoryRead(fact.Request) : provider.ExecuteSoftwareSecureMemoryRead(fact.Request);
            var canonical = provider.ResolveSoftwareSecureMemoryRead(fact.Request);
            lock (_secureDomainLifecycleGate)
            {
                if (!IntactInitializedLoads() || !ReferenceEquals(observed, canonical) || !ReferenceEquals(observed.Request, fact.Request) ||
                    !observed.ResourcesClosed || fact.OriginalClosure is not null && !ReferenceEquals(fact.OriginalClosure, observed)) return new(fact.Intent, false);
                fact.OriginalClosure = observed;
                bool current = !resolve && CurrentInitializedLoad(fact);
                var result = new SoftwareSecureInitializedLoadObservation(fact.Intent, true,
                    current ? observed.MachineAttempt : 0, current ? observed.Commit : 0, current ? observed.Value : null);
                fact.EndPending?.Invoke(); fact.EndPending = null;
                return fact.Observation = result;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { return new(fact.Intent, false); }
        finally { lock (_secureDomainLifecycleGate) fact.Active = false; }
    }
}
