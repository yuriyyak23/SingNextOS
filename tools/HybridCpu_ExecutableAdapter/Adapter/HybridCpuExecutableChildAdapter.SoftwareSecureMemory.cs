using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;
using YAKSys_Hybrid_CPU.Core;

namespace YAKSys_Hybrid_CPU.ExecutableAdapter;

public sealed partial class HybridCpuExecutableChildAdapter : INeutralSoftwareSecureMemoryReadProvider
{
    private sealed class SoftwareMemorySource(Child child, Mapping mapping, NeutralSoftwareSecureMemorySourceRequest request)
    {
        internal SoftwareMemorySource? OriginalSelf;
        internal readonly Child Child = child;
        internal readonly Mapping Mapping = mapping;
        internal readonly NeutralSoftwareSecureMemorySourceRequest Request = request;
        internal readonly long Revision = child.StateRevision;
        internal readonly SoftwareSecureComputeProvider Provider = new(child.Parent, 1);
        internal readonly ExternalSecureDomainCreateRequest Create = new(child.Parent, new(Guid.NewGuid()),
            new(ExternalSecureAssuranceClass.Basic, false, false), ExternalSecureComputeContract.Version);
        internal readonly ExternalOperationIdentity CloseOperation = new(new(Guid.NewGuid()), new(1));
        internal ExternalSecureDomainCreateReceipt? Domain;
        internal SoftwareSecureReadOnlyMemorySource? Source;
        internal SoftwareSecureReadOnlyMemorySource? OriginalSource;
        internal ExternalSecureMemoryReadSession? Session;
        internal NeutralSoftwareSecureMemorySourceLease? Lease;
        internal NeutralSoftwareSecureMemorySourceClosure? Closure;
        internal readonly Dictionary<Guid, SoftwareMemoryRead> Reads = [];
        internal readonly List<SoftwareMemoryRead> OriginalReads = [];
        internal ulong ReadsIssued;
        internal bool Cancelled, Closed, CreateInvoked;
    }
    private sealed class SoftwareMemoryRead(NeutralSoftwareSecureMemoryReadRequest request, ExternalSecureMemoryReadRequest native)
    {
        internal SoftwareMemoryRead? OriginalSelf;
        internal readonly NeutralSoftwareSecureMemoryReadRequest Request = request;
        internal readonly ExternalSecureMemoryReadRequest Native = native;
        internal ExternalSecureMemoryReadExecutionObservation? Execution;
        internal NeutralSoftwareSecureMemoryReadObservation? Observation;
    }
    private readonly Dictionary<Guid, SoftwareMemorySource> softwareMemorySources = [];
    private readonly List<SoftwareMemorySource> originalSoftwareMemorySources = [];
    private ulong softwareMemorySourcesIssued;
    private readonly Dictionary<Guid, NeutralSoftwareSecureMemorySourceRequest> softwareMemoryRefusals = [];
    private readonly List<NeutralSoftwareSecureMemorySourceRequest> originalSoftwareMemoryRefusals = [];
    private ulong softwareMemoryRefusalsIssued;

    private bool IntactSoftwareMemoryRefusals() => softwareMemoryRefusalsIssued == (ulong)originalSoftwareMemoryRefusals.Count &&
        softwareMemoryRefusals.Count == originalSoftwareMemoryRefusals.Count &&
        originalSoftwareMemoryRefusals.Distinct(ReferenceEqualityComparer.Instance).Count() == originalSoftwareMemoryRefusals.Count &&
        originalSoftwareMemoryRefusals.All(request => softwareMemoryRefusals.TryGetValue(request.Operation, out var original) && ReferenceEquals(request, original));
    private bool OriginalSoftwareMemoryRefusal(NeutralSoftwareSecureMemorySourceRequest request) =>
        IntactSoftwareMemoryRefusals() && softwareMemoryRefusals.TryGetValue(request.Operation, out var original) && ReferenceEquals(request, original);
    private NeutralSoftwareSecureMemorySourceLease? RefuseSoftwareMemorySource(NeutralSoftwareSecureMemorySourceRequest request)
    {
        // Called under sync before native Create/allocation. Unknown/full history
        // is not converted into a no-effect receipt or permission to retry.
        if (!IntactSoftwareMemoryRefusals() || softwareMemoryRefusals.Count >= 1024) return null;
        softwareMemoryRefusals.Add(request.Operation, request); originalSoftwareMemoryRefusals.Add(request); softwareMemoryRefusalsIssued++;
        return null;
    }

    private bool IntactSoftwareMemorySources() => IntactSoftwareMemoryRefusals() && softwareMemorySourcesIssued == (ulong)originalSoftwareMemorySources.Count &&
        softwareMemorySources.Count == originalSoftwareMemorySources.Count &&
        originalSoftwareMemorySources.Distinct(ReferenceEqualityComparer.Instance).Count() == originalSoftwareMemorySources.Count &&
        originalSoftwareMemorySources.All(f => ReferenceEquals(f.OriginalSelf, f) && softwareMemorySources.TryGetValue(f.Request.Operation, out var route) && ReferenceEquals(route, f));
    private bool HasSoftwareMemoryPins(Child child) => !IntactSoftwareMemorySources() ||
        originalSoftwareMemorySources.Any(f => ReferenceEquals(f.Child, child) && !f.Closed);
    private bool IntactSoftwareMemoryReads(SoftwareMemorySource f) => f.ReadsIssued == (ulong)f.OriginalReads.Count && f.Reads.Count == f.OriginalReads.Count &&
        f.OriginalReads.Distinct(ReferenceEqualityComparer.Instance).Count() == f.OriginalReads.Count &&
        f.OriginalReads.All(r => ReferenceEquals(r.OriginalSelf, r) && f.Reads.TryGetValue(r.Request.Operation, out var route) && ReferenceEquals(route, r));
    private bool CurrentSoftwareMemorySource(SoftwareMemorySource f) => !f.Cancelled && !f.Closed &&
        IntactSoftwareMemorySources() && IntactSoftwareMemoryReads(f) &&
        f.Child.State == NeutralChildDomainState.Created && !f.Child.StateRevisionExhausted && f.Child.StateRevision == f.Revision &&
        children.TryGetValue(f.Child.Neutral.Handle, out var child) && ReferenceEquals(child, f.Child) &&
        originalChildren.TryGetValue(f.Child.Neutral.Handle, out var original) && ReferenceEquals(original, f.Child) &&
        originalMappings.TryGetValue(f.Mapping.Neutral.Handle, out var mapping) && ReferenceEquals(mapping, f.Mapping) &&
        mappings.TryGetValue(f.Mapping.Neutral.Handle, out var mappingRoute) && ReferenceEquals(mappingRoute, f.Mapping) &&
        f.Mapping.AdmissionPublished && !f.Mapping.ClosureMayHaveEffect && f.Mapping.ClosureReceipt is null && f.Mapping.OriginalUnmap is null &&
        !pendingGuestUnmaps.Contains(f.Mapping.Neutral.Handle) && !pendingChildStarts.Contains(f.Child.Neutral.Handle) &&
        !pendingChildTransitions.Contains(f.Child.Neutral.Handle) && !pendingChildCloses.Contains(f.Child.Neutral.Handle) &&
        f.Child.ParentSource is { } parent && HasOriginalParentClosureSources(parent) &&
        parent.CloseRequest is null && !parent.CloseInvoked && !parent.CloseInFlight &&
        ReferenceEquals(f.Source, f.OriginalSource);

    public NeutralSoftwareSecureMemorySourceLease? CreateSoftwareSecureMemorySource(NeutralSoftwareSecureMemorySourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (sync)
        {
            if (!IntactSoftwareMemorySources() || softwareMemoryRefusals.ContainsKey(request.Operation)) return null;
            if (softwareMemorySources.TryGetValue(request.Operation, out var previous))
                return ReferenceEquals(previous.Request, request) && IntactSoftwareMemorySources() ? previous.Lease : null;
            var lease = request.Mapping;
            if (softwareMemorySources.Count >= 128 || request.SchemaVersion != 1 ||
                request.InitialBytes.Length is < 8 or > 4096 || (request.Address & 7) != 0 || lease.GuestRange.Length < request.InitialBytes.Length ||
                request.Address < lease.GuestRange.Offset || request.Address - lease.GuestRange.Offset > (ulong)(lease.GuestRange.Length - request.InitialBytes.Length) ||
                lease.Access != NeutralGuestMemoryAccess.Read || !TryChild(lease.ChildLease, out var child, out _) || child is null ||
                child.State != NeutralChildDomainState.Created || child.NestedParent is not null || HasSoftwareAdmissionPins(child) ||
                !originalMappings.TryGetValue(lease.Handle, out var mapping) || mapping.Neutral != lease ||
                !mappings.TryGetValue(lease.Handle, out var mapped) || !ReferenceEquals(mapped, mapping)) return RefuseSoftwareMemorySource(request);
            var f = new SoftwareMemorySource(child, mapping, request);
            f.OriginalSelf = f;
            softwareMemorySources.Add(request.Operation, f); originalSoftwareMemorySources.Add(f); softwareMemorySourcesIssued++;
            if (!CurrentSoftwareMemorySource(f)) return null;
            try
            {
            f.CreateInvoked = true;
            var created = f.Provider.Create(f.Create);
            if (!ReferenceEquals(created, f.Provider.QueryCreate(f.Create)) || created.Receipt is not { } domain) return null;
            f.Domain = domain;
            var binding = new ExternalSecureExecutionBinding(child.External, domain.Domain, child.Parent, 1, ExternalSecureComputeContract.Version);
            if (!f.Provider.RegisterBinding(binding)) return null;
            f.Source = f.OriginalSource = new(binding, request.Address, request.InitialBytes.Span);
            if (!f.Provider.RegisterMemoryReadSource(f.Source)) return null;
            f.Session = new(f.Provider, f.Source);
            return f.Lease = new(request, f.Source.Region.Region, f.Source.Region.Generation);
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { return null; }
        }
    }
    private SoftwareMemorySource? FindSoftwareMemorySource(NeutralSoftwareSecureMemorySourceLease lease) =>
        lease is not null && lease.Request is not null && IntactSoftwareMemorySources() && softwareMemorySources.TryGetValue(lease.Request.Operation, out var f) &&
        ReferenceEquals(f.Lease, lease) && ReferenceEquals(f.Request, lease.Request) ? f : null;
    public NeutralSoftwareSecureMemorySourceLease? ResolveSoftwareSecureMemorySource(NeutralSoftwareSecureMemorySourceRequest original)
    {
        lock (sync) return IntactSoftwareMemorySources() && softwareMemorySources.TryGetValue(original.Operation, out var f) && ReferenceEquals(f.Request, original) ? f.Lease : null;
    }
    public NeutralSoftwareSecureMemoryReadObservation ExecuteSoftwareSecureMemoryRead(NeutralSoftwareSecureMemoryReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (sync)
        {
            var f = FindSoftwareMemorySource(request.Source);
            if (f is null) return new(request, NeutralSoftwareSecureMemoryReadStatus.Denied);
            if (f.Reads.TryGetValue(request.Operation, out var prior))
                return ReferenceEquals(prior.Request, request) && IntactSoftwareMemoryReads(f) && prior.Observation is not null ? prior.Observation : new(request, NeutralSoftwareSecureMemoryReadStatus.CleanupRequired);
            if (request.SchemaVersion != 1) return new(request, NeutralSoftwareSecureMemoryReadStatus.Unsupported, true);
            if (request.Operation == Guid.Empty || request.Generation == 0 || f.Reads.Count >= 128 || !CurrentSoftwareMemorySource(f) || f.Source is null || f.Session is null)
                return new(request, NeutralSoftwareSecureMemoryReadStatus.Denied, true);
            var read = new SoftwareMemoryRead(request, new(f.Source.Region, new(new(request.Operation), new(request.Generation)), request.Address));
            read.OriginalSelf = read;
            f.Reads.Add(request.Operation, read); f.OriginalReads.Add(read); f.ReadsIssued++;
            // Adapter source gate stays held through the sealed native composition.
            // No user/backend delegate acquires this gate from inside machine locks.
            read.Execution = f.Session.Execute(read.Native);
            return read.Observation = ProjectSoftwareMemoryRead(f, read);
        }
    }
    private NeutralSoftwareSecureMemoryReadObservation ProjectSoftwareMemoryRead(SoftwareMemorySource f, SoftwareMemoryRead read)
    {
        var observation = read.Execution;
        if (observation is null || !ReferenceEquals(observation.Request, read.Native) || !observation.ResourcesClosed)
            return new(read.Request, NeutralSoftwareSecureMemoryReadStatus.CleanupRequired);
        if (!CurrentSoftwareMemorySource(f) || observation.Machine?.Retirement is not { } retired || !observation.IsAllowed)
            return new(read.Request, NeutralSoftwareSecureMemoryReadStatus.Denied, true);
        return new(read.Request, NeutralSoftwareSecureMemoryReadStatus.LoadRetired, true,
            observation.Machine.Attempt!.CpuAttempt, retired.CommitSequence, retired.Register3Value);
    }
    public NeutralSoftwareSecureMemoryReadObservation ResolveSoftwareSecureMemoryRead(NeutralSoftwareSecureMemoryReadRequest original)
    {
        lock (sync)
        {
            var f = FindSoftwareMemorySource(original.Source);
            if (f is null || !IntactSoftwareMemoryReads(f) || !f.Reads.TryGetValue(original.Operation, out var read) || !ReferenceEquals(read.Request, original))
                return new(original, NeutralSoftwareSecureMemoryReadStatus.CleanupRequired);
            if (read.Observation?.ResourcesClosed == true) return read.Observation;
            read.Execution = f.Session?.Reconcile(read.Native);
            // Reconciliation may release custody but never creates late positive projection.
            return read.Observation = new(original, NeutralSoftwareSecureMemoryReadStatus.Denied, read.Execution?.ResourcesClosed == true);
        }
    }
    public bool CancelSoftwareSecureMemorySource(NeutralSoftwareSecureMemorySourceLease original)
    {
        lock (sync)
        {
            var f = FindSoftwareMemorySource(original); if (f is null) return false;
            f.Cancelled = true; f.Session?.Cancel(); f.Source?.Revoke(); return true;
        }
    }
    public NeutralSoftwareSecureMemorySourceClosure? CloseSoftwareSecureMemorySource(NeutralSoftwareSecureMemorySourceLease original)
    {
        lock (sync)
        {
            var f = FindSoftwareMemorySource(original); if (f is null || !IntactSoftwareMemoryReads(f)) return null;
            return CloseSoftwareMemoryFact(f) ? f.Closure ??= new(original, true) : null;
        }
    }
    private bool CloseSoftwareMemoryFact(SoftwareMemorySource f)
    {
        if (!IntactSoftwareMemorySources() || !IntactSoftwareMemoryReads(f)) return false;
        if (f.Closed) return true;
        f.Cancelled = true; f.Session?.Cancel();
        if (!f.CreateInvoked && f.Source is null && f.Domain is null && f.OriginalReads.Count == 0) { f.Closed = true; return true; }
        foreach (var read in f.OriginalReads)
            if (f.Session?.Reconcile(read.Native) is not { ResourcesClosed: true }) return false;
        if (!ReferenceEquals(f.Source, f.OriginalSource)) return false;
        if (f.Source is not null && (f.Source.Close() is not { NoFurtherUse: true } closed || !ReferenceEquals(f.Source.QueryClosure(), closed))) return false;
        var created = f.Provider.QueryCreate(f.Create);
        if (created.Receipt is not { } domain || !ReferenceEquals(domain.Request, f.Create) ||
            f.Domain is not null && !ReferenceEquals(f.Domain, domain)) return false;
        var close = f.Provider.Close(domain.Domain, f.CloseOperation);
        if (close.Receipt is null || !ReferenceEquals(close.Receipt, f.Provider.QueryClose(domain.Domain, f.CloseOperation).Receipt)) return false;
        f.Closed = true; return true;
    }
    public bool CloseOriginalSoftwareSecureMemorySource(NeutralSoftwareSecureMemorySourceRequest original)
    {
        lock (sync) return OriginalSoftwareMemoryRefusal(original) || IntactSoftwareMemorySources() && softwareMemorySources.TryGetValue(original.Operation, out var f) &&
            ReferenceEquals(f.Request, original) && CloseSoftwareMemoryFact(f);
    }
    public bool IsOriginalSoftwareSecureMemorySourceClosed(NeutralSoftwareSecureMemorySourceRequest original)
    {
        lock (sync) return OriginalSoftwareMemoryRefusal(original) || IntactSoftwareMemorySources() && softwareMemorySources.TryGetValue(original.Operation, out var f) &&
            ReferenceEquals(f.Request, original) && f.Closed;
    }
    public NeutralSoftwareSecureMemorySourceClosure? ResolveSoftwareSecureMemorySourceClosure(NeutralSoftwareSecureMemorySourceLease original)
    { lock (sync) return FindSoftwareMemorySource(original)?.Closure; }
}
