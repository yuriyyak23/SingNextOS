using SingPlus.Contracts;

namespace SingPlus.Runtime;

public interface ICxlTeardownParticipant
{
    KernelResult CloseForProcess(ProcessHandle process, RegionOwner owner);
}

internal interface IComposedWorkTeardownParticipant
{
    KernelResult CloseComposedWorkForProcess(ProcessHandle process, RegionOwner owner);
}

// Kernel-private: Type-3 retains authority over whether its current backing
// generations can support a new secure guest effect. It deliberately exposes
// neither endpoint identity nor provider handles to the secure-compute model.
internal interface ISecureGuestBackingAuthority
{
    KernelResult RevalidateForSecureGuest(RegionOwner owner, RegionHandle region);
    KernelResult<Func<Func<KernelResult>, KernelResult>> PrepareForSecureGuest(RegionOwner owner, RegionHandle region,
        out RegionBackingLeaseHandle? backing);
}

public sealed partial class RuntimeKernel
{
    internal KernelResult CommitCxlDeviceEffect(PlatformDeviceLease lease, RegionOwner owner, Func<KernelResult> commit)
        => CommitCxlDeviceEffect(lease, owner, commit, null);

    internal KernelResult CommitCxlDeviceEffect(PlatformDeviceLease lease, RegionOwner owner, Func<KernelResult> commit,
        Func<Func<KernelResult>, KernelResult>? localAdmission)
        => CommitCxlDeviceEffect(lease, owner, commit, localAdmission, null);

    internal KernelResult CommitCxlDeviceEffect(PlatformDeviceLease lease, RegionOwner owner, Func<KernelResult> commit,
        Func<Func<KernelResult>, KernelResult>? localAdmission, Func<Func<KernelResult>, KernelResult>? compositionAdmission)
    {
        var subject = lease.DomainBinding.Subject;
        var source = PlatformAuthority.CxlDeviceSource(lease, subject);
        if (!source.IsSuccess) return KernelResult.Fail(source.Error, source.Message!);
        KernelResult CommitSources() =>
            CapabilityAuthority.CommitDeviceLeaseAdmission(source.Value, subject.DomainId, subject.Process.Generation,
                ToDeviceCapabilityRights(lease.Rights), lease.Device.ResourceId,
                () => localAdmission is null ? PlatformAuthority.CommitCxlDeviceAdmission(lease, subject, commit)
                    : localAdmission(() => PlatformAuthority.CommitCxlDeviceAdmission(lease, subject, commit)));
        return CommitCxlBackingPublication(subject.Process, owner, () =>
            compositionAdmission is null ? CommitSources() : compositionAdmission(CommitSources));
    }

    // Callback is local admission/publication only, never provider code.
    internal KernelResult<Func<Func<KernelResult>, KernelResult>> PrepareCxlDeviceSource(
        PlatformDeviceLease lease, RegionOwner owner)
    {
        var subject = lease.DomainBinding.Subject;
        var source = PlatformAuthority.CxlDeviceSource(lease, subject); // Provider incarnation query outside all commit locks.
        if (!source.IsSuccess) return KernelResult<Func<Func<KernelResult>, KernelResult>>.Fail(source.Error, source.Message!);
        KernelResult Commit(Func<KernelResult> commit) => CommitCxlBackingPublication(subject.Process, owner, () =>
            CapabilityAuthority.CommitDeviceLeaseAdmission(source.Value, subject.DomainId, subject.Process.Generation,
                ToDeviceCapabilityRights(lease.Rights), lease.Device.ResourceId,
                () => PlatformAuthority.CommitCxlDeviceAdmission(lease, subject, commit)));
        return KernelResult<Func<Func<KernelResult>, KernelResult>>.Ok(Commit);
    }

    // Callback is local admission/publication only, never provider code.
    internal KernelResult CommitCxlBackingPublication(ProcessHandle process, RegionOwner owner, Func<KernelResult> commit)
    {
        lock (_platformMemoryUseGate)
        {
            var resolved = Processes.Resolve(process);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            if (owner != new RegionOwner(resolved.Value!.DomainId, process.Generation))
                return KernelResult.Fail(KernelError.WrongRegionOwner, "CXL backing principal generation is not exact.");
            var active = EnsureProcessAcceptsNewEffects(resolved.Value);
            return active.IsSuccess ? commit() : active;
        }
    }
    private readonly List<ICxlTeardownParticipant> _cxlTeardownParticipants = [];
    private readonly List<IComposedWorkTeardownParticipant> _composedWorkTeardownParticipants = [];
    private readonly List<ISecureGuestBackingAuthority> _secureGuestBackingAuthorities = [];

    internal void RegisterCxlTeardownParticipant(ICxlTeardownParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        lock (_platformMemoryUseGate)
            if (!_cxlTeardownParticipants.Any(current => ReferenceEquals(current, participant)))
                _cxlTeardownParticipants.Add(participant);
    }

    internal void RegisterComposedWorkTeardownParticipant(IComposedWorkTeardownParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        if (!_composedWorkTeardownParticipants.Contains(participant))
            _composedWorkTeardownParticipants.Add(participant);
    }

    private KernelResult CloseComposedWorkForProcess(ProcessHandle process, RegionOwner owner)
    {
        foreach (var participant in _composedWorkTeardownParticipants.AsEnumerable().Reverse().ToArray())
        {
            var closure = participant.CloseComposedWorkForProcess(process, owner);
            if (!closure.IsSuccess) return closure;
        }
        return KernelResult.Ok();
    }

    internal void RegisterSecureGuestBackingAuthority(ISecureGuestBackingAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (!_secureGuestBackingAuthorities.Contains(authority))
            _secureGuestBackingAuthorities.Add(authority);
    }

    private KernelResult RevalidateSecureGuestBackings(RegionOwner owner, RegionHandle region)
    {
        foreach (var authority in _secureGuestBackingAuthorities.ToArray())
        {
            var validation = authority.RevalidateForSecureGuest(owner, region);
            if (!validation.IsSuccess) return validation;
        }
        return KernelResult.Ok();
    }

    // Prepare all backing owners before taking any Region gate. Both single-map and
    // multi-operand consumers use the same owner-major, then RegionId lock order.
    private KernelResult<Func<Func<KernelResult>, KernelResult>> PrepareSecureGuestBackings(
        RegionOwner owner, RegionHandle region, RegionUseRange range)
        => PrepareSecureGuestBackings(owner, [(region, range)]);

    private KernelResult<Func<Func<KernelResult>, KernelResult>> PrepareSecureGuestBackings(
        RegionOwner owner, IReadOnlyList<(RegionHandle Region, RegionUseRange Range)> operands)
    {
        var authorities = _secureGuestBackingAuthorities.ToArray();
        var ordered = operands.OrderBy(x => x.Region.RegionId.Value).ToArray();
        var commits = new List<Func<Func<KernelResult>, KernelResult>>();
        var backings = ordered.Select(_ => new List<RegionBackingLeaseHandle>()).ToArray();
        foreach (var authority in authorities)
        {
            for (var index = 0; index < ordered.Length; index++)
            {
                var prepared = authority.PrepareForSecureGuest(owner, ordered[index].Region, out var backing);
                if (!prepared.IsSuccess) return KernelResult<Func<Func<KernelResult>, KernelResult>>.Fail(prepared.Error, prepared.Message!);
                commits.Add(prepared.Value!);
                if (backing is { } exact) backings[index].Add(exact); // Correlation only.
            }
        }
        for (var index = 0; index < ordered.Length; index++)
        {
            var regionCommit = Regions.PrepareSecureGuestAdmission(ordered[index].Region, owner, ordered[index].Range, backings[index]);
            if (!regionCommit.IsSuccess) return regionCommit;
            commits.Add(regionCommit.Value!);
        }
        KernelResult Commit(Func<KernelResult> commit)
        {
            if (!_secureGuestBackingAuthorities.SequenceEqual(authorities))
                return KernelResult.Fail(KernelError.StaleGeneration, "Secure guest backing owners changed; fresh admission required.");
            KernelResult Next(int index) => index == commits.Count ? commit() : commits[index](() => Next(index + 1));
            return Next(0);
        }
        return KernelResult<Func<Func<KernelResult>, KernelResult>>.Ok(Commit);
    }
    // Called only under the kernel memory-use gate; inventory correlation, never permission.
    private bool HasCurrentCxlTeardownParticipants(ICxlTeardownParticipant[] snapshot)
    {
        var current = _cxlTeardownParticipants.AsEnumerable().Reverse().ToArray();
        return current.Length == snapshot.Length && current.Where((participant, index) =>
            !ReferenceEquals(participant, snapshot[index])).Any() == false;
    }
    private KernelResult CloseCxlResourcesForProcess(ProcessHandle process, RegionOwner owner,
        ICxlTeardownParticipant[] participants)
    {
        foreach (var participant in participants)
        {
            var closure = participant.CloseForProcess(process, owner);
            if (!closure.IsSuccess) return closure;
        }
        return KernelResult.Ok();
    }
}
