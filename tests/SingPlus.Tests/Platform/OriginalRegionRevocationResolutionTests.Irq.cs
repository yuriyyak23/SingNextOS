using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;
namespace SingPlus.Tests.Platform;

public sealed partial class OriginalRegionRevocationResolutionTests
{
    private static (RuntimeKernel Kernel,RuntimeSpy Spy,NeutralDomainRuntimeFacade Native,HybridCpuPlatformAuthorityProvider Provider,ProcessHandle Owner,
        PlatformDeviceLease Device,CapabilityId Capability,KernelEventEndpoint Endpoint,RegionHandle Region,BudgetAccountHandle Budget) IrqKernel()
    {
        var runtime = DispatchProxy.Create<IResolvingNeutralRuntime,RuntimeSpy>(); var spy = (RuntimeSpy)runtime;
        var native = new NeutralDomainRuntimeFacade(); spy.Inner = native;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime); var kernel = new RuntimeKernel(provider);
        var (_,owner) = TestFixtures.Create(kernel,768,1110); var admin = TestFixtures.Create(kernel,98,998).Handle;
        var budgetCap = kernel.MintCapability(new(998),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin,budgetCap,owner,"original-irq",[new(ServiceBudgetDimension.OwnedMemoryBytes,512)]).Value!.ProcessBudget;
        var root = kernel.BindPlatformDomain(owner).Value!; var region = kernel.AllocateBuffer<byte>(owner,512).Value!;
        var deviceCap = kernel.MintCapability(root.Subject.DomainId,owner,ResourceKind.Device,"device/irq",CapabilityRights.Read | CapabilityRights.Configure).Value!.CapabilityId;
        var device = kernel.BindPlatformDevice(owner,root,deviceCap,PlatformDeviceRights.Read | PlatformDeviceRights.Configure).Value!;
        var irqCap = kernel.MintCapability(root.Subject.DomainId,owner,ResourceKind.Irq,CapabilityResourceIds.Irq("device/irq","irq",IrqTriggerMode.Edge),CapabilityRights.Signal).Value!.CapabilityId;
        return (kernel,spy,native,provider,owner,device,irqCap,kernel.CreateKernelEventEndpoint(owner).Value!,region.Handle,budget);
    }
    private static NeutralInterruptLease NativeIrq(HybridCpuPlatformAuthorityProvider provider)
    {
        var records = (IDictionary)provider.GetType().GetField("_originalIrqBindings",BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(provider)!;
        return (NeutralInterruptLease)Assert.Single(records.Values.Cast<object>()).GetType().GetProperty("HybridCpuLease")!.GetValue(Assert.Single(records.Values.Cast<object>()))!;
    }
    [Theory]
    [InlineData("PollInterrupt",false)] [InlineData("CompleteInterruptDelivery",false)]
    [InlineData("PollInterrupt",true)] [InlineData("CompleteInterruptDelivery",true)]
    public void OriginalNativeIrqUnknownDeliveryContainsOnlyNativeLifetimeAndReclaims(string operation,bool loseContainment)
    {
        var x = IrqKernel(); var binding = x.Kernel.BindPlatformInterrupt(x.Owner,x.Device,x.Capability,x.Endpoint).Value!;
        var lease = NativeIrq(x.Provider); Assert.True(x.Native.SignalInterrupt(lease).IsSignaled);
        var calls = new Dictionary<string,int>();
        x.Spy.BeforeOperation = name => { calls[name] = calls.GetValueOrDefault(name) + 1; };
        var lose = true;
        x.Spy.AfterOperation = name => { if (name == operation && lose) { lose = false; throw new IOException("lost original delivery reply"); } };
        Assert.False(x.Kernel.PollPlatformInterrupt(x.Owner,binding).IsSuccess);
        Assert.False(x.Kernel.PollPlatformInterrupt(x.Owner,binding).IsSuccess);
        Assert.Equal(1,calls[operation]); Assert.False(x.Kernel.ConsumeKernelEvent(x.Owner,x.Endpoint).IsSuccess);
        Assert.Equal(RegionState.Owned,x.Kernel.Regions.Snapshot().Single(r => r.Handle == x.Region).State);
        Assert.Equal(512UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        if (loseContainment)
        {
            x.Spy.AfterOperation = name => { if (name == "ContainInterrupt") { x.Spy.AfterOperation = null; throw new IOException("lost containment receipt"); } };
            Assert.False(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
            Assert.Equal(0,x.Native.ActiveInterruptLeaseCount);
            Assert.Equal(RegionState.Owned,x.Kernel.Regions.Snapshot().Single(r => r.Handle == x.Region).State);
            Assert.Equal(512UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        }
        var closed = x.Kernel.TerminateProcess(x.Owner); Assert.True(closed.IsSuccess,closed.Message);
        Assert.Equal(1,calls["ContainInterrupt"]); Assert.Equal(1,calls[operation]);
        Assert.False(x.Native.SignalInterrupt(lease).IsSignaled);
        Assert.False(x.Native.PollInterrupt(lease).DeliveryAvailable);
        Assert.NotEqual(NeutralInterruptCompleteDecision.Completed,x.Native.CompleteInterruptDelivery(lease,new(1)).Decision);
        Assert.Equal(RegionState.Released,x.Kernel.Regions.Snapshot().Single(r => r.Handle == x.Region).State);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OriginalNativeIrqLostBindUsesPureCleanupWithoutRepeat(bool loseQuery)
    {
        var x = IrqKernel(); var calls = 0;
        x.Spy.AfterOperation = name => { if (name == "BindInterrupt") { calls++; x.Spy.AfterOperation = null; throw new IOException("lost Bind reply"); } };
        Assert.False(x.Kernel.BindPlatformInterrupt(x.Owner,x.Device,x.Capability,x.Endpoint).IsSuccess);
        Assert.Equal(1,x.Native.ActiveInterruptLeaseCount);
        if (loseQuery)
        {
            x.Spy.AfterOperation = name => { if (name == "ResolveIrqBind") { x.Spy.AfterOperation = null; throw new IOException("lost pure query"); } };
            Assert.False(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
            Assert.Equal(512UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        }
        var closed = x.Kernel.TerminateProcess(x.Owner); Assert.True(closed.IsSuccess,closed.Message);
        Assert.Equal(1,calls); Assert.Equal(0,x.Native.ActiveInterruptLeaseCount);
        Assert.Equal(RegionState.Released,x.Kernel.Regions.Snapshot().Single(r => r.Handle == x.Region).State);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }
    [Fact]
    public void OriginalNativeIrqTypedDeliveryPublishesActualLocalEventAndFreshNoDelivery()
    {
        var x = IrqKernel(); var binding = x.Kernel.BindPlatformInterrupt(x.Owner,x.Device,x.Capability,x.Endpoint).Value!;
        var noDelivery = x.Kernel.PollPlatformInterrupt(x.Owner,binding); Assert.True(noDelivery.IsSuccess,noDelivery.Message); Assert.False(noDelivery.Value!.DeliveryAvailable);
        var lease = NativeIrq(x.Provider); Assert.True(x.Native.SignalInterrupt(lease).IsSignaled);
        var delivery = x.Kernel.PollPlatformInterrupt(x.Owner,binding); Assert.True(delivery.IsSuccess,delivery.Message);
        Assert.True(x.Kernel.ConsumeKernelEvent(x.Owner,x.Endpoint).IsSuccess);
        Assert.True(x.Kernel.CloseKernelEventEndpoint(x.Owner,x.Endpoint).IsSuccess);
        Assert.True(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }
    [Theory]
    [InlineData("native-history")] [InlineData("native-owner")] [InlineData("bridge-owner")]
    [InlineData("bridge-absent")] [InlineData("endpoint-owner")] [InlineData("endpoint-source")]
    public void OriginalNativeIrqContainmentCannotReleaseIndependentHistoryOrEndpointPins(string fault)
    {
        var x = IrqKernel(); var binding = x.Kernel.BindPlatformInterrupt(x.Owner,x.Device,x.Capability,x.Endpoint).Value!;
        var lease = NativeIrq(x.Provider); Assert.True(x.Native.SignalInterrupt(lease).IsSignaled);
        x.Spy.AfterOperation = name => { if (name == "PollInterrupt") { x.Spy.AfterOperation = null; throw new IOException("lost poll"); } };
        Assert.False(x.Kernel.PollPlatformInterrupt(x.Owner,binding).IsSuccess);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object Clone(object value) => typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(value,null)!;
        if (fault.StartsWith("native-"))
        {
            var registry = x.Native.GetType().GetField("_dependencies",flags)!.GetValue(x.Native)!;
            var routes = (IDictionary)registry.GetType().GetField("Interrupts",flags)!.GetValue(registry)!;
            var original = routes[lease.Handle]!;
            if (fault == "native-history") ((IList)original.GetType().GetProperty("Polls")!.GetValue(original)!).Clear();
            else routes[lease.Handle] = Clone(original);
        }
        else if (fault.StartsWith("bridge-"))
        {
            var routes = (IDictionary)x.Kernel.PlatformAuthority.GetType().GetField("_irqBindings",flags)!.GetValue(x.Kernel.PlatformAuthority)!;
            if (fault == "bridge-absent") routes.Remove(binding.BindingId); else routes[binding.BindingId] = Clone(routes[binding.BindingId]!);
        }
        else
        {
            var events = x.Kernel.GetType().GetField("_kernelEvents",flags)!.GetValue(x.Kernel)!;
            var routes = (IDictionary)events.GetType().GetField("_endpoints",flags)!.GetValue(events)!;
            var original = routes[x.Endpoint.EndpointId]!;
            if (fault == "endpoint-owner") routes[x.Endpoint.EndpointId] = Clone(original);
            else
            {
                var reservation = Assert.Single(((IEnumerable)original.GetType().GetProperty("IrqSources")!.GetValue(original)!).Cast<object>());
                reservation.GetType().GetField("_source",flags)!.SetValue(reservation,new object());
            }
        }
        Assert.False(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
        Assert.False(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
        Assert.Equal(RegionState.Owned,x.Kernel.Regions.Snapshot().Single(r => r.Handle == x.Region).State);
        Assert.Equal(512UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(fault.StartsWith("endpoint-") ? 0 : 1,x.Native.ActiveInterruptLeaseCount);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OriginalNativeIrqPureDeliveryRetryNeverRepeatsEffect(bool completion)
    {
        var x = IrqKernel(); var local = x.Kernel.BindPlatformInterrupt(x.Owner,x.Device,x.Capability,x.Endpoint).Value!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var records = (IDictionary)x.Provider.GetType().GetField("_irqBindings",flags)!.GetValue(x.Provider)!;
        var record = Assert.Single(records.Values.Cast<object>());
        var binding = (PlatformProviderIrqBinding)record.GetType().GetProperty("Binding")!.GetValue(record)!;
        var lease = NativeIrq(x.Provider); var signal = x.Native.SignalInterrupt(lease); Assert.True(signal.IsSignaled);
        if (completion) Assert.True(x.Provider.PollInterrupt(binding).IsSuccess);
        var calls = 0;
        x.Spy.AfterOperation = name => { if (name == (completion ? "CompleteInterruptDelivery" : "PollInterrupt")) { calls++; x.Spy.AfterOperation = null; throw new IOException("lost original reply"); } };
        if (completion) Assert.ThrowsAny<Exception>(() => x.Provider.CompleteInterruptDelivery(binding,new(signal.Sequence.Value)));
        else Assert.ThrowsAny<Exception>(() => x.Provider.PollInterrupt(binding));
        x.Spy.BeforeOperation = name =>
        {
            Assert.False(Monitor.IsEntered(x.Provider.GetType().GetField("_childCreateCommitGate",flags)!.GetValue(x.Provider)!));
            if (name.StartsWith("ResolveIrq")) { Assert.False(x.Provider.PollInterrupt(binding).IsSuccess); Assert.False(x.Provider.RevokeInterrupt(binding).IsSuccess); }
        };
        if (completion) Assert.True(x.Provider.CompleteInterruptDelivery(binding,new(signal.Sequence.Value)).IsSuccess);
        else Assert.True(x.Provider.PollInterrupt(binding).IsSuccess);
        x.Spy.BeforeOperation = null;
        Assert.Equal(1,calls);
        if (!completion) Assert.True(x.Provider.CompleteInterruptDelivery(binding,new(signal.Sequence.Value)).IsSuccess);
        Assert.True(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
    }
    [Theory]
    [InlineData("poll")] [InlineData("complete")] [InlineData("containment")]
    public void OriginalNativeIrqMalformedRawNeverRepeatsInvocation(string operation)
    {
        var x = IrqKernel(); var binding = x.Kernel.BindPlatformInterrupt(x.Owner,x.Device,x.Capability,x.Endpoint).Value!;
        var lease = NativeIrq(x.Provider); Assert.True(x.Native.SignalInterrupt(lease).IsSignaled);
        var calls = new Dictionary<string,int>(); x.Spy.BeforeOperation = name => calls[name] = calls.GetValueOrDefault(name) + 1;
        x.Spy.RewriteObservation = raw => raw switch
        {
            NeutralOriginalIrqPollObservation poll when operation is "poll" or "containment" => poll with { Request = poll.Request with { InvocationId = Guid.NewGuid() } },
            NeutralOriginalIrqCompleteObservation complete when operation == "complete" => complete with { Request = complete.Request with { InvocationId = Guid.NewGuid() } },
            NeutralOriginalIrqContainObservation containment when operation == "containment" => containment with { Request = containment.Request with { InvocationId = Guid.NewGuid() } },
            _ => raw,
        };
        Assert.False(x.Kernel.PollPlatformInterrupt(x.Owner,binding).IsSuccess);
        var closed = x.Kernel.TerminateProcess(x.Owner);
        Assert.Equal(operation != "containment",closed.IsSuccess);
        Assert.Equal(1,calls[operation == "complete" ? "CompleteInterruptDelivery" : "PollInterrupt"]);
        Assert.Equal(1,calls["ContainInterrupt"]);
        Assert.Equal(operation == "containment" ? RegionState.Owned : RegionState.Released,x.Kernel.Regions.Snapshot().Single(r => r.Handle == x.Region).State);
        Assert.Equal(operation == "containment" ? 512UL : 0UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        if (operation == "containment") { x.Spy.RewriteObservation = null; Assert.False(x.Kernel.TerminateProcess(x.Owner).IsSuccess); Assert.Equal(1,calls["ContainInterrupt"]); }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OriginalNativeIrqForeignDeliveryRealmNeverTouchesEqualTuple(bool completion)
    {
        var x = IrqKernel(); var binding = x.Kernel.BindPlatformInterrupt(x.Owner,x.Device,x.Capability,x.Endpoint).Value!;
        var lease = NativeIrq(x.Provider); Assert.True(x.Native.SignalInterrupt(lease).IsSignaled);
        var foreign = new NeutralDomainRuntimeFacade(); var root = foreign.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var device = foreign.BindDevice(root,lease.DeviceLease.Device,lease.DeviceLease.Rights).Lease; var other = foreign.BindInterrupt(device,lease.Source).Lease;
        Assert.Equal(lease,other); Assert.True(foreign.SignalInterrupt(other).IsSignaled);
        x.Spy.BeforeOperation = name => { if (name == (completion ? "CompleteInterruptDelivery" : "PollInterrupt")) { x.Spy.BeforeOperation = null; x.Spy.Inner = foreign; } };
        Assert.False(x.Kernel.PollPlatformInterrupt(x.Owner,binding).IsSuccess);
        Assert.True(foreign.PollInterrupt(other).DeliveryAvailable); Assert.Equal(1,foreign.ActiveInterruptLeaseCount);
        Assert.Equal(512UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        x.Spy.Inner = x.Native;
        var closed = x.Kernel.TerminateProcess(x.Owner); Assert.True(closed.IsSuccess,closed.Message);
        Assert.Equal(1,foreign.ActiveInterruptLeaseCount); Assert.True(foreign.PollInterrupt(other).DeliveryAvailable);
        Assert.Equal(0,x.Native.ActiveInterruptLeaseCount);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OriginalIrqCapabilityCascadeConsumesLostBindWithoutClosingIndependentIrq(bool reentry)
    {
        var x = IrqKernel();
        var otherCapability = x.Kernel.MintCapability(x.Device.DomainBinding.Subject.DomainId,x.Owner,ResourceKind.Irq,
            CapabilityResourceIds.Irq("device/irq","other",IrqTriggerMode.Edge),CapabilityRights.Signal).Value!.CapabilityId;
        var otherEndpoint = x.Kernel.CreateKernelEventEndpoint(x.Owner).Value!;
        var otherBinding = x.Kernel.BindPlatformInterrupt(x.Owner,x.Device,otherCapability,otherEndpoint).Value!;
        var otherNative = NativeIrq(x.Provider);
        var calls = 0;
        x.Spy.AfterOperation = name =>
        {
            if (name != "BindInterrupt") return;
            calls++; x.Spy.AfterOperation = null;
            if (reentry) Assert.False(x.Kernel.RevokeCapability(x.Capability).IsSuccess); // Original intent is still in flight.
            throw new IOException("Lost admitted Bind reply");
        };
        Assert.False(x.Kernel.BindPlatformInterrupt(x.Owner,x.Device,x.Capability,x.Endpoint).IsSuccess);
        Assert.Equal(2,x.Native.ActiveInterruptLeaseCount);
        x.Spy.BeforeOperation = name =>
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Assert.False(Monitor.IsEntered(x.Kernel.GetType().GetField("_platformMemoryUseGate",flags)!.GetValue(x.Kernel)!));
            Assert.False(Monitor.IsEntered(x.Kernel.PlatformAuthority.GetType().GetField("_secureDomainLifecycleGate",flags)!.GetValue(x.Kernel.PlatformAuthority)!));
        };
        var revoked = x.Kernel.RevokeCapability(x.Capability); Assert.True(revoked.IsSuccess,revoked.Message);
        x.Spy.BeforeOperation = null;
        Assert.Equal(1,calls); Assert.Equal(1,x.Native.ActiveInterruptLeaseCount);
        Assert.True(x.Kernel.RevokeCapability(x.Capability).IsSuccess); // Completed consumption never re-pins/repeats.
        Assert.True(x.Kernel.CloseKernelEventEndpoint(x.Owner,x.Endpoint).IsSuccess);
        Assert.True(x.Native.SignalInterrupt(otherNative).IsSignaled);
        var delivery = x.Kernel.PollPlatformInterrupt(x.Owner,otherBinding); Assert.True(delivery.IsSuccess,delivery.Message);
        Assert.True(x.Kernel.ConsumeKernelEvent(x.Owner,otherEndpoint).IsSuccess);
        Assert.Equal(RegionState.Owned,x.Kernel.Regions.Snapshot().Single(r => r.Handle == x.Region).State);
        Assert.Equal(512UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.True(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Fact]
    public void OriginalIrqCapabilityCascadeHistoryLossCannotBecomeEmptySuccessfulCleanup()
    {
        var x = IrqKernel();
        var calls = 0;
        x.Spy.AfterOperation = name => { if (name == "BindInterrupt") { calls++; throw new IOException("Lost Bind reply"); } };
        Assert.False(x.Kernel.BindPlatformInterrupt(x.Owner,x.Device,x.Capability,x.Endpoint).IsSuccess);
        x.Spy.AfterOperation = null;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var owners = (IDictionary)x.Kernel.PlatformAuthority.GetType().GetField("_originalDeviceOwners",flags)!.GetValue(x.Kernel.PlatformAuthority)!;
        var device = Assert.Single(owners.Values.Cast<object>());
        ((IList)device.GetType().GetProperty("OriginalIrqBinds")!.GetValue(device)!).Clear();
        Assert.False(x.Kernel.RevokeCapability(x.Capability).IsSuccess);
        Assert.False(x.Kernel.RevokeCapability(x.Capability).IsSuccess);
        Assert.False(x.Kernel.CloseKernelEventEndpoint(x.Owner,x.Endpoint).IsSuccess);
        Assert.False(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
        Assert.Equal(1,calls); Assert.Equal(1,x.Native.ActiveInterruptLeaseCount);
        Assert.Equal(RegionState.Owned,x.Kernel.Regions.Snapshot().Single(r => r.Handle == x.Region).State);
        Assert.Equal(512UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }
}
