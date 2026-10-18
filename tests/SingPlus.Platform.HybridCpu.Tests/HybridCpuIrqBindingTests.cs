using SingPlus.Contracts;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class HybridCpuIrqBindingTests
{
    [Theory]
    [InlineData("poll")][InlineData("complete")][InlineData("close")][InlineData("foreign-closed")]
    public void ForeignIrqOwnerCannotConsumeOriginalKernelFact(string action)
    {
        var runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        var proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 83, 830, 1);
        var domain = kernel.BindPlatformDomain(subject).Value!;
        var device = kernel.BindPlatformDevice(subject, domain,
            Mint(kernel, subject, ResourceKind.Device, "device/irq0", CapabilityRights.Configure), PlatformDeviceRights.Configure).Value!;
        var endpoint = kernel.CreateKernelEventEndpoint(subject).Value!;
        var route = kernel.BindPlatformInterrupt(subject, device,
            Mint(kernel, subject, ResourceKind.Irq, CapabilityResourceIds.Irq("device/irq0", "notify", IrqTriggerMode.Edge), CapabilityRights.Signal), endpoint).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var records = (global::System.Collections.IDictionary)provider.GetType().GetField("_irqBindings", flags)!.GetValue(provider)!;
        var original = records.Values.Cast<object>().Single();
        var binding = (PlatformProviderIrqBinding)original.GetType().GetProperty("Binding")!.GetValue(original)!;
        var neutral = (NeutralInterruptLease)original.GetType().GetProperty("HybridCpuLease")!.GetValue(original)!;
        var copy = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        if (action == "foreign-closed")
        {
            var other = provider.BindInterrupt(binding.DeviceLease, new("other", PlatformInterruptTrigger.Edge)).Value!;
            Assert.True(provider.RevokeInterrupt(other).IsSuccess);
            var otherRecord = records[other.BindingId]!;
            copy.GetType().GetProperty("CloseObservation")!.SetValue(copy,
                otherRecord.GetType().GetProperty("CloseObservation")!.GetValue(otherRecord));
        }
        PlatformProviderInterruptDeliverySequence sequence = default;
        if (action == "complete")
        {
            Assert.True(proxy.Runtime.SignalInterrupt(neutral).IsSignaled);
            var observed = provider.PollInterrupt(binding);
            Assert.True(observed.IsSuccess);
            sequence = observed.Value!.Sequence;
        }
        records[binding.BindingId] = copy;
        var closeCalls = proxy.IrqCloseCalls;
        if (action == "poll")
        {
            Assert.True(proxy.Runtime.SignalInterrupt(neutral).IsSignaled);
            Assert.False(kernel.PollPlatformInterrupt(subject, route).IsSuccess);
            Assert.False(kernel.ConsumeKernelEvent(subject, endpoint).IsSuccess);
            Assert.Equal(0, proxy.IrqPollCalls);
            Assert.Equal(0, proxy.IrqCompleteCalls);
        }
        else if (action == "complete")
        {
            Assert.False(provider.CompleteInterruptDelivery(binding, sequence).IsSuccess);
            Assert.Equal(0, proxy.IrqCompleteCalls);
        }
        else Assert.False(kernel.RevokePlatformInterrupt(subject, route).IsSuccess);
        Assert.Equal(closeCalls, proxy.IrqCloseCalls);
        Assert.Equal(1, proxy.Runtime.ActiveInterruptLeaseCount);
        Assert.False(provider.RevokeDevice(binding.DeviceLease).IsSuccess);
        Assert.Equal(0, proxy.DeviceCloseCalls);
        records[binding.BindingId] = original;
        if (action == "complete") Assert.True(provider.CompleteInterruptDelivery(binding, sequence).IsSuccess);
        if (action == "poll")
        {
            var observed = provider.PollInterrupt(binding);
            Assert.True(observed.IsSuccess);
            Assert.True(provider.CompleteInterruptDelivery(binding, observed.Value!.Sequence).IsSuccess);
        }
        Assert.True(provider.RevokeInterrupt(binding).IsSuccess);
        Assert.Equal(closeCalls + 1, proxy.IrqCloseCalls);
        Assert.Equal(0, proxy.Runtime.ActiveInterruptLeaseCount);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void LostIrqRouteCannotHideOriginalDeviceDependency(bool copiedClosed)
    {
        var runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        var proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(830), new ProcessHandle(new ProcessId(83), 1))).Value!;
        var device = provider.BindDevice(parent, new("device/irq0"), PlatformDeviceRights.Configure).Value!;
        var binding = provider.BindInterrupt(device, new("notify", PlatformInterruptTrigger.Edge)).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var records = (global::System.Collections.IDictionary)provider.GetType().GetField("_irqBindings", flags)!.GetValue(provider)!;
        var original = records[binding.BindingId]!;
        if (copiedClosed)
        {
            var copy = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            copy.GetType().GetProperty("Revoked")!.SetValue(copy, true);
            records[binding.BindingId] = copy;
        }
        else records.Remove(binding.BindingId);
        Assert.False(provider.BindInterrupt(device, binding.Source).IsSuccess);
        Assert.Equal(1, proxy.IrqBindCalls);
        Assert.False(provider.RevokeDevice(device).IsSuccess);
        Assert.Equal(0, proxy.DeviceCloseCalls);
        Assert.Equal(1, proxy.Runtime.ActiveInterruptLeaseCount);
        records[binding.BindingId] = original;
        Assert.True(provider.RevokeInterrupt(binding).IsSuccess);
        Assert.True(provider.RevokeDevice(device).IsSuccess);
        Assert.True(provider.RevokeDomain(parent).IsSuccess);
        Assert.Equal(0, proxy.Runtime.ActiveBindingCount);
    }

    [Theory]
    [InlineData("exact")][InlineData("poll-owner")][InlineData("complete-owner")]
    public void NeutralDeliveryPublishesKernelEventOnlyWithCurrentOriginalSource(string outcome)
    {
        var runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        var proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 83, 830, 1);
        var domain = kernel.BindPlatformDomain(subject).Value!;
        var device = kernel.BindPlatformDevice(subject, domain,
            Mint(kernel, subject, ResourceKind.Device, "device/irq0", CapabilityRights.Configure), PlatformDeviceRights.Configure).Value!;
        var endpoint = kernel.CreateKernelEventEndpoint(subject).Value!;
        var route = kernel.BindPlatformInterrupt(subject, device,
            Mint(kernel, subject, ResourceKind.Irq, CapabilityResourceIds.Irq("device/irq0", "notify", IrqTriggerMode.Edge), CapabilityRights.Signal), endpoint).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var irqs = (global::System.Collections.IDictionary)provider.GetType().GetField("_irqBindings", flags)!.GetValue(provider)!;
        var irq = irqs.Values.Cast<object>().Single();
        var neutral = (NeutralInterruptLease)irq.GetType().GetProperty("HybridCpuLease")!.GetValue(irq)!;
        var owners = (global::System.Collections.IDictionary)provider.GetType().GetField("_domains", flags)!.GetValue(provider)!;
        var key = owners.Keys.Cast<object>().Single();
        var original = owners[key]!;
        Action replace = () => owners[key] = Activator.CreateInstance(original.GetType(),
            original.GetType().GetProperty("Lease")!.GetValue(original), original.GetType().GetProperty("HybridCpuLease")!.GetValue(original))!;
        if (outcome == "poll-owner") proxy.BeforeIrqPoll = replace;
        if (outcome == "complete-owner") proxy.BeforeIrqComplete = replace;
        Assert.True(proxy.Runtime.SignalInterrupt(neutral).IsSignaled);
        var delivery = kernel.PollPlatformInterrupt(subject, route);
        Assert.Equal(outcome == "exact", delivery.IsSuccess);
        var consumed = kernel.ConsumeKernelEvent(subject, endpoint);
        Assert.Equal(outcome == "exact", consumed.IsSuccess);
        Assert.Equal(outcome == "poll-owner" ? 0 : 1, proxy.IrqCompleteCalls);
        Assert.Equal(1, proxy.IrqPollCalls);
        Assert.Equal(1, proxy.Runtime.ActiveInterruptLeaseCount);
        if (outcome != "exact")
        {
            Assert.False(kernel.PollPlatformInterrupt(subject, route).IsSuccess);
            Assert.Equal(1, proxy.IrqPollCalls);
            Assert.Equal(outcome == "poll-owner" ? 0 : 1, proxy.IrqCompleteCalls);
        }
        else
        {
            Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
            Assert.Equal(0, proxy.Runtime.ActiveInterruptLeaseCount);
        }
    }

    [Fact]
    public void ExactCapabilityBackedInterruptRouteMaterializesInNeutralRuntime()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        var (_, subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 81, 810, 1);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var deviceCapability = Mint(
            kernel,
            subject,
            ResourceKind.Device,
            "device/net0",
            CapabilityRights.Configure);
        var device = kernel.BindPlatformDevice(
            subject,
            binding,
            deviceCapability,
            PlatformDeviceRights.Configure).Value!;
        var irqCapability = Mint(
            kernel,
            subject,
            ResourceKind.Irq,
            CapabilityResourceIds.Irq("device/net0", "rx-ready", IrqTriggerMode.Edge),
            CapabilityRights.Signal);
        var endpoint = kernel.CreateKernelEventEndpoint(subject).Value!;

        var route = kernel.BindPlatformInterrupt(
            subject,
            device,
            irqCapability,
            endpoint);

        Assert.True(route.IsSuccess, route.Message);
        Assert.Equal(
            new PlatformInterruptSourceIdentity("rx-ready", PlatformInterruptTrigger.Edge),
            route.Value!.Source);
        Assert.Equal(endpoint, route.Value.EventEndpoint);
        Assert.Equal(1, runtime.ActiveDeviceLeaseCount);
        Assert.Equal(1, runtime.ActiveInterruptLeaseCount);

        Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.Equal(0, runtime.ActiveInterruptLeaseCount);
        Assert.Equal(0, runtime.ActiveDeviceLeaseCount);
    }

    [Fact]
    public void HybridCpuProviderAdvertisesIrqBindingWithDmaAdmissionOnly()
    {
        var provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade());
        var features = provider.QueryFeatures();

        Assert.Equal(
            PlatformIrqBindingContract.ContractVersion,
            features.Resolve(PlatformFeatureFamily.IrqBinding).ContractVersion);
        Assert.Equal(
            PlatformFeatureAvailability.RuntimeAdmission,
            features.Resolve(PlatformFeatureFamily.IrqBinding).Availability);
        Assert.Equal(
            PlatformFeatureAvailability.RuntimeAdmission,
            features.Resolve(PlatformFeatureFamily.DmaMapping).Availability);
        Assert.NotEqual(
            PlatformFeatureAvailability.Executable,
            features.Resolve(PlatformFeatureFamily.DmaMapping).Availability);
    }

    [Fact]
    public void ProcessTeardownClosesRealInterruptRouteBeforeDeviceAndDomain()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        var (_, subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 82, 820, 1);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var deviceCapability = Mint(
            kernel,
            subject,
            ResourceKind.Device,
            "device/controller0",
            CapabilityRights.Configure);
        var device = kernel.BindPlatformDevice(
            subject,
            binding,
            deviceCapability,
            PlatformDeviceRights.Configure).Value!;
        var irqCapability = Mint(
            kernel,
            subject,
            ResourceKind.Irq,
            CapabilityResourceIds.Irq("device/controller0", "notify", IrqTriggerMode.Level),
            CapabilityRights.Signal);
        var endpoint = kernel.CreateKernelEventEndpoint(subject).Value!;
        Assert.True(kernel.BindPlatformInterrupt(
            subject,
            device,
            irqCapability,
            endpoint).IsSuccess);
        Assert.Equal(1, runtime.ActiveInterruptLeaseCount);

        var terminate = kernel.TerminateProcess(subject);

        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.Equal(0, runtime.ActiveInterruptLeaseCount);
        Assert.Equal(0, runtime.ActiveDeviceLeaseCount);
        Assert.Equal(0, runtime.ActiveBindingCount);
    }

    private static CapabilityId Mint(
        RuntimeKernel kernel,
        ProcessHandle subject,
        ResourceKind kind,
        string resourceId,
        CapabilityRights rights)
    {
        var process = kernel.Processes.Resolve(subject);
        Assert.True(process.IsSuccess, process.Message);
        var minted = kernel.MintCapability(
            process.Value!.DomainId,
            subject,
            kind,
            resourceId,
            rights);
        Assert.True(minted.IsSuccess, minted.Message);
        return minted.Value!.CapabilityId;
    }
}
