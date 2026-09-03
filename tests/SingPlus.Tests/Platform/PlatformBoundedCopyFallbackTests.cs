using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed class PlatformBoundedCopyFallbackTests
{
    [Fact]
    public void CopyFallbackLimitIsRejectedBeforeSourceDrain()
    {
        var scenario = CreateScenario(bufferLength: 256, rejectTargetMapping: true);

        var result = scenario.Kernel.MovePlatformOwnedBuffer(
            scenario.Source,
            scenario.Target,
            scenario.Buffer,
            scenario.SourceMapping,
            scenario.TargetRequest,
            new PlatformBoundedCopyFallbackPolicy(255));

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.CapacityExhausted, result.Error);
        Assert.Equal(0, scenario.Provider.BeginRevokeCalls);
        Assert.True(scenario.Buffer.IsValid);
        Assert.Equal(new RegionGeneration(1), scenario.Buffer.Handle.Generation);
    }

    [Fact]
    public void CopyFallbackRequiresExplicitTargetMappingRequest()
    {
        var scenario = CreateScenario(bufferLength: 128, rejectTargetMapping: false);

        var result = scenario.Kernel.MovePlatformOwnedBuffer(
            scenario.Source,
            scenario.Target,
            scenario.Buffer,
            scenario.SourceMapping,
            targetMapping: null,
            copyFallback: new PlatformBoundedCopyFallbackPolicy(64));

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, result.Error);
        Assert.Equal(0, scenario.Provider.BeginRevokeCalls);
        Assert.True(scenario.Buffer.IsValid);
    }

    [Fact]
    public void UnsupportedTargetMappingRematerializesFullBufferThroughBoundedCopy()
    {
        var scenario = CreateScenario(bufferLength: 256, rejectTargetMapping: true);
        for (var i = 0; i < scenario.Buffer.Length; i++)
            scenario.Buffer.Span[i] = unchecked((byte)(i * 17 + 3));
        var expected = scenario.Buffer.Span.ToArray();
        var sourceStorage = StorageIdentity(scenario.Buffer);

        var result = scenario.Kernel.MovePlatformOwnedBuffer(
            scenario.Source,
            scenario.Target,
            scenario.Buffer,
            scenario.SourceMapping,
            scenario.TargetRequest,
            new PlatformBoundedCopyFallbackPolicy(256));

        Assert.True(result.IsSuccess, result.Message);
        Assert.False(scenario.Buffer.IsValid);
        Assert.True(result.Value!.Buffer.IsValid);
        Assert.Equal(new RegionGeneration(2), result.Value.Buffer.Handle.Generation);
        Assert.Equal(
            PlatformMoveTargetExposureState.BoundedCopyFallback,
            result.Value.TargetExposure);
        Assert.Null(result.Value.TargetMapping);
        Assert.Null(result.Value.TargetPublication);
        Assert.Equal(KernelError.PlatformUnsupported, result.Value.TargetExposureError);

        var copy = Assert.NotNull(result.Value.BoundedCopy);
        Assert.True(copy.IsExactAndBounded);
        Assert.Equal(result.Value.Buffer.Handle, copy.Region);
        Assert.Equal(256, copy.ByteLength);
        Assert.Equal(256, copy.MaxBytes);
        Assert.Equal(expected, result.Value.Buffer.Span.ToArray());
        Assert.NotSame(sourceStorage, StorageIdentity(result.Value.Buffer));

        var targetOwner = new RegionOwner(new DomainId(1020), scenario.Target.Generation);
        Assert.True(scenario.Kernel.Regions.Validate(result.Value.Buffer.Handle, targetOwner).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Kernel.Regions.Validate(
                scenario.SourceMapping.Region,
                new RegionOwner(new DomainId(1010), scenario.Source.Generation)).Error);
        Assert.Equal(1, scenario.Provider.AcquireCalls);
        Assert.Equal(1, scenario.Provider.TargetMapAttempts);
    }

    [Fact]
    public void CopyFallbackCopiesWholeOwnedBufferNotOnlyMappedSlice()
    {
        var scenario = CreateScenario(bufferLength: 512, rejectTargetMapping: true);
        scenario.Buffer.Span[0] = 11;
        scenario.Buffer.Span[31] = 22;
        scenario.Buffer.Span[32] = 33;
        scenario.Buffer.Span[95] = 44;
        scenario.Buffer.Span[96] = 55;
        scenario.Buffer.Span[511] = 66;

        var result = scenario.Kernel.MovePlatformOwnedBuffer(
            scenario.Source,
            scenario.Target,
            scenario.Buffer,
            scenario.SourceMapping,
            scenario.TargetRequest,
            new PlatformBoundedCopyFallbackPolicy(512));

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(11, result.Value!.Buffer.Span[0]);
        Assert.Equal(22, result.Value.Buffer.Span[31]);
        Assert.Equal(33, result.Value.Buffer.Span[32]);
        Assert.Equal(44, result.Value.Buffer.Span[95]);
        Assert.Equal(55, result.Value.Buffer.Span[96]);
        Assert.Equal(66, result.Value.Buffer.Span[511]);
        Assert.Equal(512, result.Value.BoundedCopy!.Value.ByteLength);
    }

    [Fact]
    public void MissingCopyPolicyPreservesExistingLocalOwnershipFallbackWithoutClaimingCopy()
    {
        var scenario = CreateScenario(bufferLength: 128, rejectTargetMapping: true);
        var sourceStorage = StorageIdentity(scenario.Buffer);

        var result = scenario.Kernel.MovePlatformOwnedBuffer(
            scenario.Source,
            scenario.Target,
            scenario.Buffer,
            scenario.SourceMapping,
            scenario.TargetRequest);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(
            PlatformMoveTargetExposureState.LocalOwnershipFallback,
            result.Value!.TargetExposure);
        Assert.Null(result.Value.BoundedCopy);
        Assert.Same(sourceStorage, StorageIdentity(result.Value.Buffer));
    }

    [Fact]
    public void TargetPublicationFailureDoesNotRematerializeWhileTargetMappingLifecycleExisted()
    {
        var scenario = CreateScenario(
            bufferLength: 128,
            rejectTargetMapping: false,
            rejectTargetPublication: true);
        var sourceStorage = StorageIdentity(scenario.Buffer);

        var result = scenario.Kernel.MovePlatformOwnedBuffer(
            scenario.Source,
            scenario.Target,
            scenario.Buffer,
            scenario.SourceMapping,
            scenario.TargetRequest,
            new PlatformBoundedCopyFallbackPolicy(128));

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(
            PlatformMoveTargetExposureState.LocalOwnershipFallback,
            result.Value!.TargetExposure);
        Assert.Equal(KernelError.PlatformUnsupported, result.Value.TargetExposureError);
        Assert.Null(result.Value.BoundedCopy);
        Assert.Same(sourceStorage, StorageIdentity(result.Value.Buffer));
        Assert.Equal(1, scenario.Provider.TargetMapAttempts);
        Assert.True(scenario.Provider.TargetRevokeCalls > 0);
    }

    [Fact]
    public void BoundedCopyFallbackSurfaceCarriesNoProviderHybridCpuOrHardwareAuthorityIdentity()
    {
        var forbidden = new[]
        {
            "PlatformProvider",
            "PlatformOperation",
            "Neutral",
            "HybridCPU",
            "Physical",
            "Pte",
            "PageTable",
            "CacheLine",
            "Dma",
            "Iommu",
            "Vmcs",
            "Vmx",
            "Lane",
            "Opcode",
        };
        var surface = new[]
        {
            typeof(PlatformBoundedCopyFallbackPolicy),
            typeof(PlatformBoundedCopyEvidence),
            typeof(PlatformOwnedBufferMoveResult<byte>),
        };

        foreach (var type in surface)
        foreach (var member in type.GetMembers(
                     BindingFlags.Public |
                     BindingFlags.Instance |
                     BindingFlags.Static))
        {
            var signature = member.ToString() ?? member.Name;
            foreach (var name in forbidden)
                Assert.DoesNotContain(name, signature, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static object StorageIdentity<T>(OwnedBuffer<T> buffer)
        where T : unmanaged
    {
        var field = typeof(OwnedBuffer<T>).GetField(
            "_storage",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return Assert.IsType<OwnedBuffer<T>.Storage>(field!.GetValue(buffer));
    }

    private static Scenario CreateScenario(
        int bufferLength,
        bool rejectTargetMapping,
        bool rejectTargetPublication = false)
    {
        var provider = new CopyFallbackProvider
        {
            RejectTargetDomain = rejectTargetMapping ? new DomainId(1020) : null,
            RejectPublicationDomain = rejectTargetPublication ? new DomainId(1020) : null,
        };
        var kernel = new RuntimeKernel(provider);
        var (_, source) = TestFixtures.Create(kernel, 1001, 1010);
        var (_, target) = TestFixtures.Create(kernel, 1002, 1020);
        var buffer = kernel.AllocateBuffer<byte>(source, bufferLength).Value!;
        var sourceBinding = kernel.BindPlatformDomain(source).Value!;
        var targetBinding = kernel.BindPlatformDomain(target).Value!;
        var sourceCapability = MintRegionCapability(
            kernel,
            source,
            buffer.Handle,
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write);
        var targetCapability = MintRegionCapability(
            kernel,
            target,
            buffer.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        var sourceMapping = kernel.MapPlatformOwnedRegionSlice(
            source,
            sourceBinding,
            sourceCapability,
            buffer.Handle,
            32,
            64,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var targetRequest = new PlatformMoveTargetMappingRequest(
            targetBinding,
            targetCapability,
            PlatformMemoryAccess.Read);
        return new Scenario(
            kernel,
            provider,
            source,
            target,
            buffer,
            sourceMapping,
            targetRequest);
    }

    private static CapabilityId MintRegionCapability(
        RuntimeKernel kernel,
        ProcessHandle subject,
        RegionHandle region,
        CapabilityRights rights)
    {
        var process = kernel.Processes.Resolve(subject);
        Assert.True(process.IsSuccess, process.Message);
        var capability = kernel.MintCapability(
            process.Value!.DomainId,
            subject,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.RegionId),
            rights);
        Assert.True(capability.IsSuccess, capability.Message);
        return capability.Value!.CapabilityId;
    }

    private sealed record Scenario(
        RuntimeKernel Kernel,
        CopyFallbackProvider Provider,
        ProcessHandle Source,
        ProcessHandle Target,
        OwnedBuffer<byte> Buffer,
        PlatformOwnedRegionSliceMapping SourceMapping,
        PlatformMoveTargetMappingRequest TargetRequest);

    private sealed class CopyFallbackProvider :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider,
        IPlatformOwnedRegionMappingProvider,
        IPlatformRegionVisibilityProvider,
        IPlatformRegionRevocationProvider,
        IPlatformRegionAcquireProvider
    {
        private sealed class MappingRecord(PlatformProviderOwnedRegionMapping mapping)
        {
            public PlatformProviderOwnedRegionMapping Mapping { get; } = mapping;
            public bool RevocationStarted { get; set; }
            public bool Revoked { get; set; }
        }

        private readonly Dictionary<PlatformProviderDomainLeaseId, PlatformProviderDomainLease> _domains = [];
        private readonly Dictionary<PlatformProviderRegionMappingId, MappingRecord> _mappings = [];
        private readonly Dictionary<PlatformOperationId, (PlatformOperationIdentity Identity, PlatformProviderRegionMappingId MappingId)> _operations = [];
        private ulong _nextDomain = 1;
        private ulong _nextMapping = 1;
        private ulong _nextOperation = 1;

        public DomainId? RejectTargetDomain { get; set; }
        public DomainId? RejectPublicationDomain { get; set; }
        public int BeginRevokeCalls { get; private set; }
        public int AcquireCalls { get; private set; }
        public int TargetMapAttempts { get; private set; }
        public int TargetRevokeCalls { get; private set; }

        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("bounded-copy-fallback-test"),
            5,
            PlatformAuthorityFeatures.NeutralDomainBinding |
            PlatformAuthorityFeatures.DirectOwnedRegionMapping);

        public PlatformFeatureManifest QueryFeatures() => new(new[]
        {
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.NeutralDomains,
                1,
                PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.OwnedRegionMapping,
                PlatformOwnedRegionMappingContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.ExplicitMemoryVisibility,
                PlatformRegionAcquireContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
        });

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(
            PlatformDomainIdentity subject)
        {
            var lease = new PlatformProviderDomainLease(
                new PlatformProviderDomainLeaseId(_nextDomain++),
                new PlatformProviderLeaseGeneration(1),
                subject);
            _domains.Add(lease.LeaseId, lease);
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease) =>
            PlatformAuthorityResult.Ok();

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease,
            PlatformRegionIdentity region,
            PlatformMemoryAccess access)
        {
            var mapped = MapOwnedRegionSlice(
                domainLease,
                new PlatformRegionSlice(region, 0, region.ByteLength, access));
            return mapped.IsSuccess
                ? PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Ok(mapped.Value!.Lease)
                : PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                    mapped.Status,
                    mapped.Message!);
        }

        public PlatformAuthorityResult<PlatformProviderOwnedRegionMapping> MapOwnedRegionSlice(
            PlatformProviderDomainLease domainLease,
            PlatformRegionSlice slice)
        {
            if (!_domains.TryGetValue(domainLease.LeaseId, out var live) || live != domainLease)
            {
                return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                    PlatformAuthorityStatus.WrongDomain,
                    "Unknown domain lease.");
            }

            if (RejectTargetDomain is { } targetDomain &&
                domainLease.Subject.DomainId == targetDomain)
            {
                TargetMapAttempts++;
                return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                    PlatformAuthorityStatus.Unsupported,
                    "Target exact mapping is unavailable.");
            }

            if (domainLease.Subject.DomainId == new DomainId(1020))
                TargetMapAttempts++;

            var lease = new PlatformProviderRegionMappingLease(
                new PlatformProviderRegionMappingId(_nextMapping++),
                new PlatformProviderLeaseGeneration(1),
                domainLease,
                slice.Region,
                slice.Access);
            var mapped = new PlatformProviderOwnedRegionMapping(lease, slice);
            _mappings.Add(lease.MappingId, new MappingRecord(mapped));
            return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Ok(mapped);
        }

        public PlatformAuthorityResult<PlatformRegionVisibilityResult> PrepareRegionMappingForConsumer(
            PlatformRegionVisibilityRequest request)
        {
            if (!_mappings.TryGetValue(request.Mapping.MappingId, out var record) ||
                record.Mapping.Lease != request.Mapping ||
                record.Mapping.Slice != request.Slice ||
                record.RevocationStarted)
            {
                return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Fail(
                    PlatformAuthorityStatus.Revoked,
                    "Mapping is not live.");
            }

            var supported = RejectPublicationDomain is not { } rejected ||
                            request.Mapping.DomainLease.Subject.DomainId != rejected;
            var outcome = supported &&
                          request.Consumer == PlatformMemoryConsumerClass.ExternalExecutionDomain &&
                          request.Requirement == PlatformMemoryVisibilityRequirement.PublicationFence
                ? PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied
                : PlatformMemoryVisibilityOutcome.Unsupported;
            return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Ok(
                new PlatformRegionVisibilityResult(
                    request.Mapping.MappingId,
                    request.Mapping.Generation,
                    request.Slice,
                    request.Consumer,
                    request.Requirement,
                    outcome));
        }

        public PlatformAuthorityResult RevokeRegionMapping(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy)
        {
            if (_mappings.TryGetValue(mapping.MappingId, out var record))
                record.Revoked = true;
            if (mapping.DomainLease.Subject.DomainId == new DomainId(1020))
                TargetRevokeCalls++;
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformRegionRevocationTicket> BeginRegionMappingRevocation(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy)
        {
            BeginRevokeCalls++;
            if (!_mappings.TryGetValue(mapping.MappingId, out var record) ||
                record.Mapping.Lease != mapping || record.Revoked)
            {
                return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Unknown mapping.");
            }

            record.RevocationStarted = true;
            var operation = new PlatformOperationIdentity(
                new PlatformOperationId(_nextOperation++),
                new PlatformOperationGeneration(1),
                mapping.DomainLease);
            _operations.Add(operation.OperationId, (operation, mapping.MappingId));
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Ok(
                new PlatformRegionRevocationTicket(
                    mapping.MappingId,
                    mapping.Generation,
                    operation));
        }

        public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(
            PlatformOperationIdentity operation)
        {
            if (!_operations.TryGetValue(operation.OperationId, out var entry) ||
                entry.Identity != operation)
            {
                return PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Unknown operation.");
            }

            _mappings[entry.MappingId].Revoked = true;
            if (operation.DomainLease.Subject.DomainId == new DomainId(1020))
                TargetRevokeCalls++;
            return PlatformAuthorityResult<PlatformCompletionReceipt>.Ok(
                new PlatformCompletionReceipt(
                    operation.OperationId,
                    operation.Generation,
                    operation.DomainLease,
                    PlatformCompletionState.Closed));
        }

        public PlatformAuthorityResult<PlatformRegionAcquireResult> AcquireRegionMappingFromConsumer(
            PlatformRegionAcquireRequest request)
        {
            AcquireCalls++;
            if (!_mappings.TryGetValue(request.Mapping.MappingId, out var record) ||
                record.Mapping.Lease != request.Mapping ||
                record.Mapping.Slice != request.Slice ||
                !record.Revoked)
            {
                return PlatformAuthorityResult<PlatformRegionAcquireResult>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Mapping is not closed.");
            }

            return PlatformAuthorityResult<PlatformRegionAcquireResult>.Ok(
                new PlatformRegionAcquireResult(
                    request.Mapping.MappingId,
                    request.Mapping.Generation,
                    request.Slice,
                    request.Producer,
                    request.Requirement,
                    PlatformMemoryAcquireOutcome.AcquisitionFenceSatisfied));
        }
    }
}
