using SingPlus.Contracts;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class HybridCpuDeviceLeaseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopiedDeviceOwnerCannotAdmitDependentNeutralEffect(bool interrupt)
    {
        var runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        var proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var parent = provider.BindDomain(new(new DomainId(610), new ProcessHandle(new ProcessId(61), 1))).Value!;
        var device = provider.BindDevice(parent, new("device/net0"), PlatformDeviceRights.Read | PlatformDeviceRights.Configure).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var records = (global::System.Collections.IDictionary)provider.GetType().GetField("_deviceLeases", flags)!.GetValue(provider)!;
        var original = records[device.LeaseId]!;
        records[device.LeaseId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        if (interrupt)
            Assert.False(provider.BindInterrupt(device, new("rx-ready", PlatformInterruptTrigger.Edge)).IsSuccess);
        else
            Assert.False(provider.MapMmio(device, new("registers", 4096), new(0, 64), PlatformMmioAccess.Read).IsSuccess);
        Assert.Equal(0, proxy.IrqBindCalls);
        Assert.Equal(0, proxy.MmioMapCalls);
        Assert.Equal(1, proxy.Runtime.ActiveDeviceLeaseCount);
        records[device.LeaseId] = original;
        if (interrupt)
        {
            var binding = provider.BindInterrupt(device, new("rx-ready", PlatformInterruptTrigger.Edge));
            Assert.True(binding.IsSuccess);
            Assert.True(provider.RevokeInterrupt(binding.Value!).IsSuccess);
        }
        else
        {
            var mapping = provider.MapMmio(device, new("registers", 4096), new(0, 64), PlatformMmioAccess.Read);
            Assert.True(mapping.IsSuccess);
            Assert.True(provider.RevokeMmio(mapping.Value!).IsSuccess);
        }
        Assert.True(provider.RevokeDevice(device).IsSuccess);
        Assert.True(provider.RevokeDomain(parent).IsSuccess);
        Assert.Equal(0, proxy.Runtime.ActiveBindingCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignDeviceOwnerCannotConsumeOriginalKernelClosure(bool completed)
    {
        var runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        var proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 61, 610, 1);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var capability = kernel.MintCapability(new DomainId(610), subject, ResourceKind.Device,
            "device/net0", CapabilityRights.Read | CapabilityRights.Configure).Value!.CapabilityId;
        var device = kernel.BindPlatformDevice(subject, binding, capability,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var records = (global::System.Collections.IDictionary)provider.GetType().GetField("_deviceLeases", flags)!.GetValue(provider)!;
        var original = records.Values.Cast<object>().Single();
        var copy = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        if (completed)
        {
            var parent = (PlatformProviderDeviceLease)original.GetType().GetProperty("Lease")!.GetValue(original)!;
            var other = provider.BindDevice(parent.DomainLease, new("device/other"), PlatformDeviceRights.Read).Value!;
            Assert.True(provider.RevokeDevice(other).IsSuccess);
            copy.GetType().GetProperty("CloseObservation")!.SetValue(copy,
                records[other.LeaseId]!.GetType().GetProperty("CloseObservation")!.GetValue(records[other.LeaseId]));
        }
        var key = original.GetType().GetProperty("Lease")!.GetValue(original)!;
        var lease = (PlatformProviderDeviceLease)key;
        records[lease.LeaseId] = copy;
        var calls = proxy.DeviceCloseCalls;
        Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.Equal(calls, proxy.DeviceCloseCalls);
        Assert.Equal(1, proxy.Runtime.ActiveDeviceLeaseCount);
        records[lease.LeaseId] = original;
        Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.Equal(calls, proxy.DeviceCloseCalls);
        Assert.True(provider.RevokeDevice(lease).IsSuccess);
        Assert.Equal(calls + 1, proxy.DeviceCloseCalls);
        Assert.False(kernel.RevokePlatformDomain(subject, binding).IsSuccess);
        Assert.Equal(1, proxy.Runtime.ActiveBindingCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LostDeviceRouteCannotHideOriginalRootDependency(bool copiedClosed)
    {
        var runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        var proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var parent = provider.BindDomain(new(new DomainId(610), new ProcessHandle(new ProcessId(61), 1))).Value!;
        var device = provider.BindDevice(parent, new("device/net0"), PlatformDeviceRights.Read).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var records = (global::System.Collections.IDictionary)provider.GetType().GetField("_deviceLeases", flags)!.GetValue(provider)!;
        var original = records[device.LeaseId]!;
        if (copiedClosed)
        {
            var copy = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            copy.GetType().GetProperty("Revoked")!.SetValue(copy, true);
            records[device.LeaseId] = copy;
        }
        else records.Remove(device.LeaseId);
        Assert.False(provider.RevokeDomain(parent).IsSuccess);
        Assert.Equal(0, proxy.CloseCalls);
        Assert.Equal(1, proxy.Runtime.ActiveDeviceLeaseCount);
        records[device.LeaseId] = original;
        Assert.True(provider.RevokeDevice(device).IsSuccess);
        Assert.True(provider.RevokeDomain(parent).IsSuccess);
        Assert.Equal(0, proxy.Runtime.ActiveBindingCount);
    }

    [Fact]
    public void CapabilityBackedSingDeviceLeaseOwnsExactNeutralHybridCpuLifetime()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(
            kernel,
            61,
            610,
            1);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var capability = kernel.MintCapability(
            new DomainId(610),
            subject,
            ResourceKind.Device,
            "device/net0",
            CapabilityRights.Read | CapabilityRights.Configure).Value!.CapabilityId;

        var features = kernel.QueryPlatformFeatures();
        Assert.Equal(PlatformDeviceLeaseContract.ContractVersion,
            features.Resolve(PlatformFeatureFamily.IoDomainBinding).ContractVersion);
        Assert.Equal(PlatformFeatureAvailability.RuntimeAdmission,
            features.Resolve(PlatformFeatureFamily.IoDomainBinding).Availability);
        Assert.Equal(PlatformFeatureAvailability.RuntimeAdmission,
            features.Resolve(PlatformFeatureFamily.DmaMapping).Availability);
        Assert.NotEqual(PlatformFeatureAvailability.Executable,
            features.Resolve(PlatformFeatureFamily.DmaMapping).Availability);

        var lease = kernel.BindPlatformDevice(
            subject,
            binding,
            capability,
            PlatformDeviceRights.Read | PlatformDeviceRights.Configure);

        Assert.True(lease.IsSuccess, lease.Message);
        Assert.Equal(1, runtime.ActiveDeviceLeaseCount);
        Assert.Equal("device/net0", lease.Value!.Device.ResourceId);
        Assert.NotEqual(typeof(PlatformDeviceLeaseId), typeof(NeutralDeviceLeaseHandle));
        Assert.NotEqual(typeof(PlatformDeviceLeaseGeneration), typeof(NeutralDeviceLeaseEpoch));
        Assert.NotEqual(typeof(CapabilityId), typeof(NeutralDeviceLeaseHandle));

        var earlyDomainClose = kernel.RevokePlatformDomain(subject, binding);
        Assert.False(earlyDomainClose.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, earlyDomainClose.Error);
        Assert.Equal(1, runtime.ActiveBindingCount);
        Assert.Equal(1, runtime.ActiveDeviceLeaseCount);

        var closeDevice = kernel.RevokePlatformDevice(subject, lease.Value);
        Assert.True(closeDevice.IsSuccess, closeDevice.Message);
        Assert.Equal(0, runtime.ActiveDeviceLeaseCount);
        Assert.Equal(1, runtime.ActiveBindingCount);

        var closeDomain = kernel.RevokePlatformDomain(subject, binding);
        Assert.True(closeDomain.IsSuccess, closeDomain.Message);
        Assert.Equal(0, runtime.ActiveBindingCount);

        var staleDevice = kernel.RevokePlatformDevice(subject, lease.Value);
        Assert.False(staleDevice.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingRevoked, staleDevice.Error);
    }

    [Fact]
    public void CapabilityRevokeClosesRealNeutralDeviceLeaseBeforeDomainClosure()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        var (_, subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(
            kernel,
            62,
            620,
            1);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var capability = kernel.MintCapability(
            new DomainId(620),
            subject,
            ResourceKind.Device,
            "device/storage0",
            CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.BindPlatformDevice(
            subject,
            binding,
            capability,
            PlatformDeviceRights.Configure).IsSuccess);
        Assert.Equal(1, runtime.ActiveDeviceLeaseCount);

        var revoke = kernel.RevokeCapability(capability);

        Assert.True(revoke.IsSuccess, revoke.Message);
        Assert.Equal(0, runtime.ActiveDeviceLeaseCount);
        Assert.Equal(1, runtime.ActiveBindingCount);
        Assert.True(kernel.RevokePlatformDomain(subject, binding).IsSuccess);
        Assert.Equal(0, runtime.ActiveBindingCount);
    }

    [Fact]
    public void ProcessTeardownClosesModelOnlyNeutralDeviceAuthorityBeforeDomainAndExit()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        var (process, subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(
            kernel,
            63,
            630,
            1);
        Assert.True(kernel.AdmitProcess(subject).IsSuccess);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var capability = kernel.MintCapability(
            new DomainId(630),
            subject,
            ResourceKind.Device,
            "device/controller0",
            CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.BindPlatformDevice(
            subject,
            binding,
            capability,
            PlatformDeviceRights.Configure).IsSuccess);
        Assert.Equal(1, runtime.ActiveDeviceLeaseCount);
        Assert.Equal(1, runtime.ActiveBindingCount);

        var terminate = kernel.TerminateProcess(subject);

        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.Equal(0, runtime.ActiveDeviceLeaseCount);
        Assert.Equal(0, runtime.ActiveBindingCount);
        Assert.Equal(ProcessState.Exited, process.State);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(subject).Error);
    }
}
