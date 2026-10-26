using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class HybridCpuIrqBindingTests
{
    [Theory]
    [InlineData("poll")][InlineData("complete")][InlineData("close")][InlineData("foreign-closed")]
    public void ForeignIrqOwnerCannotConsumeOriginalKernelFact(string action)
    {
        INeutralDomainRuntime runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        ChildClosureTupleTests.RootCloseRuntimeProxy proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 83, 830, 1);
        PlatformDomainBinding domain = kernel.BindPlatformDomain(subject).Value!;
        PlatformDeviceLease device = kernel.BindPlatformDevice(subject, domain,
            Mint(kernel, subject, ResourceKind.Device, "device/irq0", CapabilityRights.Configure), PlatformDeviceRights.Configure).Value!;
        KernelEventEndpoint endpoint = kernel.CreateKernelEventEndpoint(subject).Value!;
        PlatformIrqBinding route = kernel.BindPlatformInterrupt(subject, device,
            Mint(kernel, subject, ResourceKind.Irq, CapabilityResourceIds.Irq("device/irq0", "notify", IrqTriggerMode.Edge), CapabilityRights.Signal), endpoint).Value!;
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary records = (global::System.Collections.IDictionary)provider.GetType().GetField("_irqBindings", flags)!.GetValue(provider)!;
        object original = records.Values.Cast<object>().Single();
        PlatformProviderIrqBinding binding = (PlatformProviderIrqBinding)original.GetType().GetProperty("Binding")!.GetValue(original)!;
        NeutralInterruptLease neutral = (NeutralInterruptLease)original.GetType().GetProperty("HybridCpuLease")!.GetValue(original)!;
        object copy = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        if (action == "foreign-closed")
        {
            PlatformProviderIrqBinding other = provider.BindInterrupt(binding.DeviceLease, new("other", PlatformInterruptTrigger.Edge)).Value!;
            Assert.True(provider.RevokeInterrupt(other).IsSuccess);
            object otherRecord = records[other.BindingId]!;
            copy.GetType().GetProperty("CloseObservation")!.SetValue(copy,
                otherRecord.GetType().GetProperty("CloseObservation")!.GetValue(otherRecord));
        }
        PlatformProviderInterruptDeliverySequence sequence = default;
        if (action == "complete")
        {
            Assert.True(proxy.Runtime.SignalInterrupt(neutral).IsSignaled);
            PlatformAuthorityResult<PlatformInterruptDeliveryObservation> observed = provider.PollInterrupt(binding);
            Assert.True(observed.IsSuccess);
            sequence = observed.Value!.Sequence;
        }
        records[binding.BindingId] = copy;
        int closeCalls = proxy.IrqCloseCalls;
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
            PlatformAuthorityResult<PlatformInterruptDeliveryObservation> observed = provider.PollInterrupt(binding);
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
        INeutralDomainRuntime runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        ChildClosureTupleTests.RootCloseRuntimeProxy proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime);
        PlatformProviderDomainLease parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(830), new ProcessHandle(new ProcessId(83), 1))).Value!;
        PlatformProviderDeviceLease device = provider.BindDevice(parent, new("device/irq0"), PlatformDeviceRights.Configure).Value!;
        PlatformProviderIrqBinding binding = provider.BindInterrupt(device, new("notify", PlatformInterruptTrigger.Edge)).Value!;
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary records = (global::System.Collections.IDictionary)provider.GetType().GetField("_irqBindings", flags)!.GetValue(provider)!;
        object original = records[binding.BindingId]!;
        if (copiedClosed)
        {
            object copy = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
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
        INeutralDomainRuntime runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        ChildClosureTupleTests.RootCloseRuntimeProxy proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(runtime);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 83, 830, 1);
        PlatformDomainBinding domain = kernel.BindPlatformDomain(subject).Value!;
        PlatformDeviceLease device = kernel.BindPlatformDevice(subject, domain,
            Mint(kernel, subject, ResourceKind.Device, "device/irq0", CapabilityRights.Configure), PlatformDeviceRights.Configure).Value!;
        KernelEventEndpoint endpoint = kernel.CreateKernelEventEndpoint(subject).Value!;
        PlatformIrqBinding route = kernel.BindPlatformInterrupt(subject, device,
            Mint(kernel, subject, ResourceKind.Irq, CapabilityResourceIds.Irq("device/irq0", "notify", IrqTriggerMode.Edge), CapabilityRights.Signal), endpoint).Value!;
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary irqs = (global::System.Collections.IDictionary)provider.GetType().GetField("_irqBindings", flags)!.GetValue(provider)!;
        object irq = irqs.Values.Cast<object>().Single();
        NeutralInterruptLease neutral = (NeutralInterruptLease)irq.GetType().GetProperty("HybridCpuLease")!.GetValue(irq)!;
        IDictionary owners = (global::System.Collections.IDictionary)provider.GetType().GetField("_domains", flags)!.GetValue(provider)!;
        object key = owners.Keys.Cast<object>().Single();
        object original = owners[key]!;
        Action replace = () => owners[key] = Activator.CreateInstance(original.GetType(),
            original.GetType().GetProperty("Lease")!.GetValue(original), original.GetType().GetProperty("HybridCpuLease")!.GetValue(original))!;
        if (outcome == "poll-owner") proxy.BeforeIrqPoll = replace;
        if (outcome == "complete-owner") proxy.BeforeIrqComplete = replace;
        Assert.True(proxy.Runtime.SignalInterrupt(neutral).IsSignaled);
        KernelResult<PlatformInterruptPollResult> delivery = kernel.PollPlatformInterrupt(subject, route);
        Assert.Equal(outcome == "exact", delivery.IsSuccess);
        KernelResult<KernelEvent> consumed = kernel.ConsumeKernelEvent(subject, endpoint);
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
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        RuntimeKernel kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        (SingProcess _, ProcessHandle subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 81, 810, 1);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId deviceCapability = Mint(
            kernel,
            subject,
            ResourceKind.Device,
            "device/net0",
            CapabilityRights.Configure);
        PlatformDeviceLease device = kernel.BindPlatformDevice(
            subject,
            binding,
            deviceCapability,
            PlatformDeviceRights.Configure).Value!;
        CapabilityId irqCapability = Mint(
            kernel,
            subject,
            ResourceKind.Irq,
            CapabilityResourceIds.Irq("device/net0", "rx-ready", IrqTriggerMode.Edge),
            CapabilityRights.Signal);
        KernelEventEndpoint endpoint = kernel.CreateKernelEventEndpoint(subject).Value!;

        KernelResult<PlatformIrqBinding> route = kernel.BindPlatformInterrupt(
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
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade());
        PlatformFeatureManifest features = provider.QueryFeatures();

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
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        RuntimeKernel kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        (SingProcess _, ProcessHandle subject) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 82, 820, 1);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        CapabilityId deviceCapability = Mint(
            kernel,
            subject,
            ResourceKind.Device,
            "device/controller0",
            CapabilityRights.Configure);
        PlatformDeviceLease device = kernel.BindPlatformDevice(
            subject,
            binding,
            deviceCapability,
            PlatformDeviceRights.Configure).Value!;
        CapabilityId irqCapability = Mint(
            kernel,
            subject,
            ResourceKind.Irq,
            CapabilityResourceIds.Irq("device/controller0", "notify", IrqTriggerMode.Level),
            CapabilityRights.Signal);
        KernelEventEndpoint endpoint = kernel.CreateKernelEventEndpoint(subject).Value!;
        Assert.True(kernel.BindPlatformInterrupt(
            subject,
            device,
            irqCapability,
            endpoint).IsSuccess);
        Assert.Equal(1, runtime.ActiveInterruptLeaseCount);

        KernelResult terminate = kernel.TerminateProcess(subject);

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
        KernelResult<SingProcess> process = kernel.Processes.Resolve(subject);
        Assert.True(process.IsSuccess, process.Message);
        KernelResult<CapabilityDescriptorV1> minted = kernel.MintCapability(
            process.Value!.DomainId,
            subject,
            kind,
            resourceId,
            rights);
        Assert.True(minted.IsSuccess, minted.Message);
        return minted.Value!.CapabilityId;
    }
}
