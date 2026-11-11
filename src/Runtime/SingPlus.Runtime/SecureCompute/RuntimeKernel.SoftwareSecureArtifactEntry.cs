using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    /// <summary>Explicit one-shot entry of an already admitted artifact. Publication remains separately committed.</summary>
    public KernelResult<SoftwareSecureRegisterLocalResult> ExecuteSoftwareSecureArtifactEntry(ProcessHandle owner,
        VirtualDomainHandle domain, CapabilityId virtualCapability, CapabilityId artifactExecuteCapability,
        CapabilityId softwareExecuteCapability, ulong left, ulong right)
    {
        using ComposedUse composed = PinComposedDomain(owner, virtualDomain: domain);
        if (!composed.Result.IsSuccess) return KernelResult<SoftwareSecureRegisterLocalResult>.Fail(composed.Result.Error, composed.Result.Message!);
        var resolved = ResolveVirtualCapability(owner, domain, artifactExecuteCapability,
            VirtualizationResourceIds.Domain(domain.DomainId), CapabilityRights.Execute);
        if (!resolved.IsSuccess) return KernelResult<SoftwareSecureRegisterLocalResult>.Fail(resolved.Error, resolved.Message!);
        var source = resolved.Value!;
        if (source.State != VirtualDomainState.Configured || source.ParentDomain is not null ||
            source.ChildBinding is not { } child || source.ExecutableArtifact is not { } artifact || source.ExecutableMapping is not { } mapping)
            return KernelResult<SoftwareSecureRegisterLocalResult>.Fail(KernelError.PlatformDenied, "Original configured artifact required.");
        var local = _virtualDomains.ResolveMapping(source, mapping);
        if (!local.IsSuccess) return KernelResult<SoftwareSecureRegisterLocalResult>.Fail(local.Error, local.Message!);
        var mappingSource = _virtualDomains.ResolveMappingSource(source, mapping);
        if (!mappingSource.IsSuccess) return KernelResult<SoftwareSecureRegisterLocalResult>.Fail(mappingSource.Error, mappingSource.Message!);
        var process = Processes.Resolve(owner);
        if (!process.IsSuccess) return KernelResult<SoftwareSecureRegisterLocalResult>.Fail(process.Error, process.Message!);
        var quota = PlatformAuthority.QueryOriginalMappingBudgetOwner(artifact.GuestMapping.ParentMapping);
        if (!quota.IsSuccess || !quota.Value.Known)
            return KernelResult<SoftwareSecureRegisterLocalResult>.Fail(KernelError.PlatformFaulted, "Original executable budget source required.");
        KernelResult Quota(Func<KernelResult> commit)
        {
            var current = PlatformAuthority.QueryOriginalMappingBudgetOwner(artifact.GuestMapping.ParentMapping);
            if (!current.IsSuccess || !current.Value.Known || current.Value.Expected != quota.Value.Expected ||
                current.Value.PrivateDataQuota != quota.Value.PrivateDataQuota || !ReferenceEquals(current.Value.Owner, quota.Value.Owner) ||
                !ReferenceEquals(current.Value.MappingOwner, quota.Value.MappingOwner))
                return KernelResult.Fail(KernelError.StaleGeneration, "Original executable budget source changed.");
            var valid = Budgets.ValidateOriginalPrivateDataQuota(owner, current.Value.Owner, current.Value.MappingOwner,
                current.Value.Expected, current.Value.PrivateDataQuota);
            return valid.IsSuccess ? commit() : valid;
        }
        KernelResult<SoftwareSecureAdmissionResult> observation = default;
        var executed = UseVirtualExecutableSource(owner, domain, artifactExecuteCapability, source, child, (fresh, sourceCommit) =>
        {
            KernelResult ArtifactSource(Func<KernelResult> commit) => fresh(() => Quota(() =>
                CapabilityAuthority.CommitGuestRegionSource(mappingSource.Value.Region, process.Value!.DomainId, owner.Generation,
                    CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Execute,
                    CapabilityResourceIds.MemoryRegion(local.Value!.Region.RegionId), () =>
                CapabilityAuthority.CommitVirtualDomainEffect(mappingSource.Value.Memory, process.Value.DomainId, owner.Generation,
                    CapabilityRights.Map, VirtualizationResourceIds.Memory(domain.DomainId), () =>
                PlatformAuthority.CommitGuestLocalMappingSource(artifact.GuestMapping, source, local.Value!.Mapping,
                    () => sourceCommit(() => _virtualDomains.CommitExecutableArtifactAdmission(source, artifact, local.Value!, commit)))))));
            var current = ArtifactSource(KernelResult.Ok);
            if (!current.IsSuccess) return current;
            observation = ObserveSoftwareSecureAdmissionCore(owner, domain, virtualCapability, softwareExecuteCapability,
                left, right, true, artifact, ArtifactSource);
            return observation.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(observation.Error, observation.Message!);
        });
        return executed.IsSuccess && observation.IsSuccess
            ? ReadSoftwareSecureRegisterLocal(owner, observation.Value!.Operation)
            : KernelResult<SoftwareSecureRegisterLocalResult>.Fail(executed.Error, executed.Message!);
    }
}
