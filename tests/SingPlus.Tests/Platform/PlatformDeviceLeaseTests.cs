using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed class PlatformDeviceLeaseTests
{
    // Existing standalone bridge fixtures model trusted caller admission; no Kernel capability claim.
    private static KernelResult<PlatformDeviceLease> BindDeviceForModel(
        PlatformAuthorityBridge bridge, PlatformDomainBinding binding, PlatformDomainIdentity owner,
        CapabilityId capability, PlatformDeviceIdentity device, PlatformDeviceRights rights) =>
        bridge.BindDevice(binding, owner, capability, device, rights, static () => KernelResult.Ok(), static (_, commit) => commit());

    [Fact]
    public void ExactDeviceCapabilityMaterializesSeparateLeaseAndBlocksEarlyDomainClose()
    {
        var provider = new DeviceProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1101, 1110);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var capability = MintDeviceCapability(
            kernel,
            subject,
            "device/net0",
            CapabilityRights.Read | CapabilityRights.Configure);

        var lease = kernel.BindPlatformDevice(
            subject,
            binding,
            capability,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure);

        Assert.True(lease.IsSuccess, lease.Message);
        Assert.Equal("device/net0", lease.Value!.Device.ResourceId);
        Assert.Equal(
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure,
            lease.Value.Rights);
        Assert.NotEqual(0UL, lease.Value.LeaseId.Value);
        Assert.NotEqual(0UL, lease.Value.Generation.Value);
        Assert.Equal(1, provider.DeviceBindCalls);
        Assert.Equal("device/net0", provider.LastDevice.ResourceId);

        var earlyDomainClose = kernel.RevokePlatformDomain(subject, binding);
        Assert.False(earlyDomainClose.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, earlyDomainClose.Error);
        Assert.Equal(0, provider.DomainRevokeCalls);

        Assert.True(kernel.RevokePlatformDevice(subject, lease.Value).IsSuccess);
        Assert.True(kernel.RevokePlatformDomain(subject, binding).IsSuccess);
        Assert.Equal(
            new[] { "bind-device:device/net0", "revoke-device:device/net0", "revoke-domain" },
            provider.Log);
    }

    [Fact]
    public void LocalAdmissionFailuresHappenBeforeProviderDeviceBinding()
    {
        var provider = new DeviceProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1102, 1120);
        var (_, other) = TestFixtures.Create(kernel, 1103, 1130);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var otherBinding = kernel.BindPlatformDomain(other).Value!;
        var readOnly = MintDeviceCapability(
            kernel,
            subject,
            "device/net0",
            CapabilityRights.Read);
        var wrongResource = kernel.MintCapability(
            new DomainId(1120),
            subject,
            ResourceKind.MemoryRegion,
            "device/net0",
            CapabilityRights.Configure).Value!.CapabilityId;

        var invalidRights = kernel.BindPlatformDevice(
            subject,
            binding,
            readOnly,
            PlatformDeviceRights.None);
        Assert.Equal(KernelError.PlatformDenied, invalidRights.Error);

        var insufficient = kernel.BindPlatformDevice(
            subject,
            binding,
            readOnly,
            PlatformDeviceRights.Configure);
        Assert.Equal(KernelError.InsufficientRights, insufficient.Error);

        var wrongKind = kernel.BindPlatformDevice(
            subject,
            binding,
            wrongResource,
            PlatformDeviceRights.Configure);
        Assert.Equal(KernelError.WrongCapabilityResource, wrongKind.Error);

        var wrongDomain = kernel.BindPlatformDevice(
            subject,
            otherBinding,
            readOnly,
            PlatformDeviceRights.Read);
        Assert.Equal(KernelError.WrongPlatformDomain, wrongDomain.Error);

        var staleSubject = subject with { Generation = subject.Generation + 1 };
        var stale = kernel.BindPlatformDevice(
            staleSubject,
            binding,
            readOnly,
            PlatformDeviceRights.Read);
        Assert.Equal(KernelError.StaleHandle, stale.Error);

        Assert.Equal(0, provider.DeviceBindCalls);
    }

    [Theory]
    [InlineData(DeviceBindFault.WrongDevice)]
    [InlineData(DeviceBindFault.WrongDomain)]
    [InlineData(DeviceBindFault.WrongRights)]
    [InlineData(DeviceBindFault.Unmaterialized)]
    public void MalformedProviderDeviceAuthorityFailsClosedAndIsRevoked(
        DeviceBindFault fault)
    {
        var provider = new DeviceProvider { BindFault = fault };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1104, 1140);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var capability = MintDeviceCapability(
            kernel,
            subject,
            "device/storage0",
            CapabilityRights.Configure);

        var result = kernel.BindPlatformDevice(
            subject,
            binding,
            capability,
            PlatformDeviceRights.Configure);

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(1, provider.DeviceBindCalls);
        Assert.Equal(1, provider.DeviceRevokeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedDeviceCleanupFailureQuarantinesExistingDeviceOwner(bool throws)
    {
        var provider = new DeviceProvider
        {
            BindFault = DeviceBindFault.WrongRights,
            RevokeStatus = throws ? null : PlatformAuthorityStatus.Faulted,
            ThrowOnRevoke = throws,
        };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1120, 1220);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var capability = MintDeviceCapability(kernel, subject, "device/ambiguous0",
            CapabilityRights.Configure);

        var bound = kernel.BindPlatformDevice(subject, binding, capability,
            PlatformDeviceRights.Configure);

        Assert.Equal(KernelError.PlatformFaulted, bound.Error);
        Assert.Equal(1, provider.DeviceRevokeCalls);
        Assert.Equal(0, provider.DomainRevokeCalls);
        Assert.False(kernel.RevokePlatformDomain(subject, binding).IsSuccess);
        Assert.Equal(0, provider.DomainRevokeCalls);
    }

    [Fact]
    public void CapabilityRevokeClosesDeviceLeaseAndOldLeaseCannotActAgain()
    {
        var provider = new DeviceProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1105, 1150);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var capability = MintDeviceCapability(
            kernel,
            subject,
            "device/audio0",
            CapabilityRights.Write | CapabilityRights.Configure);
        var lease = kernel.BindPlatformDevice(
            subject,
            binding,
            capability,
            PlatformDeviceRights.Write).Value!;

        var revokeCapability = kernel.RevokeCapability(capability);

        Assert.True(revokeCapability.IsSuccess, revokeCapability.Message);
        Assert.Equal(1, provider.DeviceRevokeCalls);
        var staleEffect = kernel.RevokePlatformDevice(subject, lease);
        Assert.False(staleEffect.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingRevoked, staleEffect.Error);
        Assert.True(kernel.RevokePlatformDomain(subject, binding).IsSuccess);
    }

    [Fact]
    public void ProcessTeardownClosesDeviceBeforeDomain()
    {
        var provider = new DeviceProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1106, 1160);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var capability = MintDeviceCapability(
            kernel,
            subject,
            "device/controller0",
            CapabilityRights.Configure);
        Assert.True(kernel.BindPlatformDevice(
            subject,
            binding,
            capability,
            PlatformDeviceRights.Configure).IsSuccess);

        var terminate = kernel.TerminateProcess(subject);

        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.Equal(
            new[] { "bind-device:device/controller0", "revoke-device:device/controller0", "revoke-domain" },
            provider.Log);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(subject).Error);
    }

    [Fact]
    public void DeviceCloseFaultPinsProcessTeardownAndPreventsDomainClose()
    {
        var provider = new DeviceProvider { RevokeStatus = PlatformAuthorityStatus.Faulted };
        var kernel = new RuntimeKernel(provider);
        var (process, subject) = TestFixtures.Create(kernel, 1107, 1170);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var capability = MintDeviceCapability(
            kernel,
            subject,
            "device/fault0",
            CapabilityRights.Configure);
        Assert.True(kernel.BindPlatformDevice(
            subject,
            binding,
            capability,
            PlatformDeviceRights.Configure).IsSuccess);

        var terminate = kernel.TerminateProcess(subject);

        Assert.False(terminate.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, terminate.Error);
        Assert.Equal(ProcessState.Exiting, process.State);
        Assert.Equal(1, provider.DeviceRevokeCalls);
        Assert.Equal(0, provider.DomainRevokeCalls);
        var snapshot = kernel.QueryProcessTeardown(subject);
        Assert.True(snapshot.IsSuccess, snapshot.Message);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, snapshot.Value!.Phase);
        Assert.False(snapshot.Value.LocalReclaimCompleted);
    }

    [Fact]
    public void ProviderRevokedStatusCannotCloseExactDeviceLease()
    {
        var provider = new DeviceProvider { RevokeStatus = PlatformAuthorityStatus.Revoked };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1130, 1230);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var capability = MintDeviceCapability(kernel, subject, "device/revoked-status",
            CapabilityRights.Configure);
        var lease = kernel.BindPlatformDevice(subject, binding, capability,
            PlatformDeviceRights.Configure).Value!;

        Assert.Equal(KernelError.PlatformFaulted, kernel.RevokePlatformDevice(subject, lease).Error);
        provider.RevokeStatus = null;
        Assert.Equal(KernelError.PlatformFaulted, kernel.RevokePlatformDevice(subject, lease).Error);
        Assert.Equal(1, provider.DeviceRevokeCalls);
        Assert.False(kernel.RevokePlatformDomain(subject, binding).IsSuccess);
        Assert.Equal(0, provider.DomainRevokeCalls);
    }

    [Fact]
    public void DeviceBindReceiptLossQuarantinesParentDomain()
    {
        var provider = new DeviceProvider { ThrowOnBindAfterMaterialize = true };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1131, 1231);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var capability = MintDeviceCapability(kernel, subject, "device/lost-receipt",
            CapabilityRights.Configure);

        var result = kernel.BindPlatformDevice(subject, binding, capability,
            PlatformDeviceRights.Configure);

        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(1, provider.DeviceBindCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(KernelError.PlatformBindingActive, kernel.RevokePlatformDomain(subject, binding).Error);
        Assert.Equal(0, provider.DomainRevokeCalls);
        Assert.Equal(KernelError.PlatformFaulted,
            kernel.BindPlatformDevice(subject, binding, capability,
                PlatformDeviceRights.Configure).Error);
        Assert.Equal(1, provider.DeviceBindCalls);
    }

    [Fact]
    public void BackendResetPreservesUnknownDeviceBindPin()
    {
        var provider = new DeviceProvider { ThrowOnBindAfterMaterialize = true };
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = new PlatformDomainIdentity(new DomainId(1233),
            new ProcessHandle(new ProcessId(1133), 1));
        var parent = bridge.BindDomain(owner).Value!;
        Assert.Equal(KernelError.PlatformFaulted,
            BindDeviceForModel(bridge, parent, owner, new CapabilityId(1), new("device/reset-pin"),
                PlatformDeviceRights.Configure).Error);

        Assert.True(bridge.ObserveBackendReset().IsSuccess);
        Assert.True(bridge.TryGetQuarantinedDomainBinding(owner, out var current));
        Assert.Equal(KernelError.StaleGeneration, bridge.RevokeDomain(parent, owner).Error);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(current, owner).Error);
        Assert.Equal(0, provider.DomainRevokeCalls);
    }

    [Fact]
    public void DeviceBindCallbackBlocksParentRevokeBeforeLeasePublication()
    {
        var provider = new DeviceProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = new PlatformDomainIdentity(new DomainId(1234),
            new ProcessHandle(new ProcessId(1134), 1));
        var parent = bridge.BindDomain(owner).Value!;
        provider.AfterBindMaterialize = () =>
        {
            Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(parent, owner).Error);
            Assert.Equal(0, provider.DomainRevokeCalls);
        };

        var lease = BindDeviceForModel(bridge, parent, owner, new CapabilityId(1),
            new("device/pending-bind"), PlatformDeviceRights.Configure).Value!;
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(parent, owner).Error);
        Assert.True(bridge.RevokeDevice(lease, owner).IsSuccess);
        Assert.True(bridge.RevokeDomain(parent, owner).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetInsideDeviceBindCallbackPinsNewParentGeneration(bool terminalEpoch)
    {
        var provider = new DeviceProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = new PlatformDomainIdentity(new DomainId(1235),
            new ProcessHandle(new ProcessId(1135), 1));
        var parent = bridge.BindDomain(owner).Value!;
        if (terminalEpoch)
            typeof(PlatformAuthorityBridge).GetField("_backendEpoch",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bridge, ulong.MaxValue);
        provider.AfterBindMaterialize = () =>
        {
            var reset = bridge.ObserveBackendReset();
            Assert.Equal(terminalEpoch ? KernelError.CapacityExhausted : KernelError.None, reset.Error);
        };

        Assert.Equal(KernelError.PlatformFaulted,
            BindDeviceForModel(bridge, parent, owner, new CapabilityId(1),
                new("device/reset-during-bind"), PlatformDeviceRights.Configure).Error);
        Assert.True(bridge.TryGetQuarantinedDomainBinding(owner, out var current));
        Assert.NotEqual(parent.Generation, current.Generation);
        Assert.Equal(KernelError.StaleGeneration, bridge.RevokeDomain(parent, owner).Error);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(current, owner).Error);
        Assert.Equal(0, provider.DomainRevokeCalls);
    }

    [Fact]
    public void ParentRevokeCallbackRejectsDeviceBindBeforeProvider()
    {
        var provider = new DeviceProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = new PlatformDomainIdentity(new DomainId(1236),
            new ProcessHandle(new ProcessId(1136), 1));
        var parent = bridge.BindDomain(owner).Value!;
        provider.BeforeDomainRevoke = () =>
        {
            Assert.Equal(KernelError.PlatformBindingActive,
                BindDeviceForModel(bridge, parent, owner, new CapabilityId(1),
                    new("device/revoke-in-flight"), PlatformDeviceRights.Configure).Error);
            Assert.Equal(0, provider.DeviceBindCalls);
        };

        Assert.True(bridge.RevokeDomain(parent, owner).IsSuccess);
        Assert.Equal(1, provider.DomainRevokeCalls);
    }

    [Theory]
    [InlineData(PlatformAuthorityStatus.Faulted)]
    [InlineData(PlatformAuthorityStatus.Denied)]
    public void DeviceBindNonAcceptanceWithoutExactNoEffectPinsParentDomain(
        PlatformAuthorityStatus status)
    {
        var provider = new DeviceProvider { BindStatusAfterMaterialize = status };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1132, 1232);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var capability = MintDeviceCapability(kernel, subject, "device/ambiguous-status",
            CapabilityRights.Configure);

        Assert.False(kernel.BindPlatformDevice(subject, binding, capability,
            PlatformDeviceRights.Configure).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive,
            kernel.RevokePlatformDomain(subject, binding).Error);
        Assert.Equal(0, provider.DomainRevokeCalls);
        Assert.Equal(KernelError.PlatformFaulted,
            kernel.BindPlatformDevice(subject, binding, capability,
                PlatformDeviceRights.Configure).Error);
        Assert.Equal(1, provider.DeviceBindCalls);
    }

    [Fact]
    public void DeviceLeaseSurfaceKeepsProviderAndHardwareAuthorityPrivate()
    {
        var surface = new[]
        {
            typeof(PlatformDeviceRights),
            typeof(PlatformDeviceIdentity),
            typeof(PlatformDeviceLeaseId),
            typeof(PlatformDeviceLeaseGeneration),
            typeof(PlatformDeviceLease),
        };
        var forbidden = new[]
        {
            "PlatformProvider",
            "HybridCPU",
            "Neutral",
            "Physical",
            "Address",
            "Pte",
            "PageTable",
            "Iommu",
            "Dma",
            "Mmio",
            "Irq",
            "InterruptVector",
            "Vmx",
            "Vmcs",
            "Lane",
            "Opcode",
        };

        foreach (var type in surface)
        foreach (var member in type.GetMembers(
                     BindingFlags.Public |
                     BindingFlags.Instance |
                     BindingFlags.Static))
        {
            var signature = member.ToString() ?? member.Name;
            foreach (var term in forbidden)
                Assert.DoesNotContain(term, signature, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static CapabilityId MintDeviceCapability(
        RuntimeKernel kernel,
        ProcessHandle subject,
        string resourceId,
        CapabilityRights rights)
    {
        var process = kernel.Processes.Resolve(subject);
        Assert.True(process.IsSuccess, process.Message);
        var minted = kernel.MintCapability(
            process.Value!.DomainId,
            subject,
            ResourceKind.Device,
            resourceId,
            rights);
        Assert.True(minted.IsSuccess, minted.Message);
        return minted.Value!.CapabilityId;
    }

    public enum DeviceBindFault
    {
        None = 0,
        WrongDevice,
        WrongDomain,
        WrongRights,
        Unmaterialized,
    }

    private sealed class DeviceProvider :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider,
        IPlatformDeviceLeaseProvider
    {
        private readonly Dictionary<PlatformProviderDomainLeaseId, PlatformProviderDomainLease> _domains = [];
        private readonly Dictionary<PlatformProviderDeviceLeaseId, PlatformProviderDeviceLease> _devices = [];
        private ulong _nextDomain = 1;
        private ulong _nextDevice = 1;

        public DeviceBindFault BindFault { get; set; }
        public PlatformAuthorityStatus? RevokeStatus { get; set; }
        public bool ThrowOnRevoke { get; set; }
        public bool ThrowOnBindAfterMaterialize { get; set; }
        public PlatformAuthorityStatus? BindStatusAfterMaterialize { get; set; }
        public Action? AfterBindMaterialize { get; set; }
        public Action? BeforeDomainRevoke { get; set; }
        public int DeviceBindCalls { get; private set; }
        public int DeviceRevokeCalls { get; private set; }
        public int DomainRevokeCalls { get; private set; }
        public PlatformDeviceIdentity LastDevice { get; private set; }
        public List<string> Log { get; } = [];

        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("device-test"),
            1,
            PlatformAuthorityFeatures.NeutralDomainBinding);

        public PlatformFeatureManifest QueryFeatures() => new(new[]
        {
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.NeutralDomains,
                PlatformDomainContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.IoDomainBinding,
                PlatformDeviceLeaseContract.ContractVersion,
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

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease)
        {
            DomainRevokeCalls++;
            Log.Add("revoke-domain");
            BeforeDomainRevoke?.Invoke();
            _domains.Remove(lease.LeaseId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(
            PlatformProviderDomainLease domainLease,
            PlatformDeviceIdentity device,
            PlatformDeviceRights rights)
        {
            DeviceBindCalls++;
            LastDevice = device;
            Log.Add($"bind-device:{device.ResourceId}");
            if (!_domains.TryGetValue(domainLease.LeaseId, out var live) || live != domainLease)
            {
                return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(
                    PlatformAuthorityStatus.WrongDomain,
                    "Unknown domain lease.");
            }

            var returnedDomain = BindFault == DeviceBindFault.WrongDomain
                ? domainLease with
                {
                    Generation = new PlatformProviderLeaseGeneration(domainLease.Generation.Value + 1),
                }
                : domainLease;
            var returnedDevice = BindFault == DeviceBindFault.WrongDevice
                ? new PlatformDeviceIdentity(device.ResourceId + "-other")
                : device;
            var returnedRights = BindFault == DeviceBindFault.WrongRights
                ? PlatformDeviceRights.Read
                : rights;
            var id = BindFault == DeviceBindFault.Unmaterialized
                ? new PlatformProviderDeviceLeaseId(0)
                : new PlatformProviderDeviceLeaseId(_nextDevice++);
            var generation = BindFault == DeviceBindFault.Unmaterialized
                ? new PlatformProviderLeaseGeneration(0)
                : new PlatformProviderLeaseGeneration(1);
            var lease = new PlatformProviderDeviceLease(
                id,
                generation,
                returnedDomain,
                returnedDevice,
                returnedRights);
            if (lease.LeaseId.Value != 0)
                _devices[lease.LeaseId] = lease;
            AfterBindMaterialize?.Invoke();
            if (ThrowOnBindAfterMaterialize)
                throw new InvalidOperationException("Injected device binding receipt loss.");
            if (BindStatusAfterMaterialize is { } status)
                return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(status,
                    "Injected ambiguous device binding response.");
            return PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease lease)
        {
            DeviceRevokeCalls++;
            Log.Add($"revoke-device:{lease.Device.ResourceId}");
            if (ThrowOnRevoke)
                throw new InvalidOperationException("Injected malformed lease cleanup receipt loss.");
            if (RevokeStatus is { } status)
            {
                return PlatformAuthorityResult.Fail(
                    status,
                    "Injected device revoke failure.");
            }

            if (lease.LeaseId.Value != 0)
                _devices.Remove(lease.LeaseId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease,
            PlatformRegionIdentity region,
            PlatformMemoryAccess access) =>
            PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                PlatformAuthorityStatus.Unsupported,
                "Not used by device lease tests.");

        public PlatformAuthorityResult RevokeRegionMapping(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy) =>
            PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Unsupported,
                "Not used by device lease tests.");
    }
}
