using System.Collections;
using System.Reflection;
using YAKSys_Hybrid_CPU.Core;
namespace HybridCPU_NeutralRuntime.Tests;
public sealed class OriginalIrqTests
{
    [Theory]
    [InlineData("aba")] [InlineData("history")] [InlineData("copy")] [InlineData("exhausted")]
    public void OriginalDeliveryRevisionAndHistoryFenceCompletion(string fault)
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var rootRequest = new NeutralOriginalDomainBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,NeutralDomainProfile.OrdinaryService);
        var root = runtime.Bind(rootRequest).Observation.Lease;
        var deviceRequest = new NeutralOriginalDeviceBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,rootRequest.InvocationId,root,new("device"),NeutralDeviceRights.Configure);
        var device = runtime.BindDevice(deviceRequest).Observation.Lease;
        var admission = new NeutralOriginalIrqBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,deviceRequest.InvocationId,device,new("irq",NeutralInterruptTrigger.Edge));
        var binding = runtime.BindInterrupt(admission).Observation.Lease;
        var sequence = runtime.SignalInterrupt(binding).Sequence;
        var poll = new NeutralOriginalIrqPollRequest(Guid.NewGuid(),admission,binding); var raw = runtime.PollInterrupt(poll);
        Assert.True(runtime.ValidateIrqPoll(poll)); Assert.Equal(raw,runtime.ResolveIrqPoll(poll));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var registry = runtime.GetType().GetField("_dependencies",flags)!.GetValue(runtime)!;
        var routes = (IDictionary)registry.GetType().GetField("Interrupts",flags)!.GetValue(registry)!; var source = routes[binding.Handle]!;
        var polls = (IList)source.GetType().GetProperty("Polls")!.GetValue(source)!;
        if (fault == "aba") source.GetType().GetProperty("Sequence")!.SetValue(source,sequence); // Equal write still invalidates observed revision.
        if (fault == "history") polls.Clear();
        if (fault == "copy") polls[0] = typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(polls[0],null)!;
        if (fault == "exhausted") source.GetType().GetProperty("SourceRevisionExhausted")!.SetValue(source,true);
        Assert.False(runtime.ValidateIrqPoll(poll));
        var completion = new NeutralOriginalIrqCompleteRequest(Guid.NewGuid(),admission,binding,sequence,poll.InvocationId);
        var refused = runtime.CompleteInterruptDelivery(completion); Assert.True(refused.NoEffectGuaranteed);
        Assert.Equal(NeutralInterruptCloseDecision.PendingDelivery,runtime.CloseInterrupt(binding).Decision);
        Assert.Equal(1,runtime.ActiveInterruptLeaseCount);
        var containment = new NeutralOriginalIrqContainRequest(Guid.NewGuid(),admission,binding);
        if (fault == "aba")
        {
            var contained = runtime.ContainInterrupt(containment); Assert.True(contained.NoFurtherDeliveryGuaranteed);
            Assert.Equal(contained,runtime.ResolveIrqContainment(containment)); Assert.Equal(contained,runtime.ContainInterrupt(containment));
            Assert.False(runtime.SignalInterrupt(binding).IsSignaled); Assert.False(runtime.PollInterrupt(binding).DeliveryAvailable);
            Assert.Equal(NeutralDeviceCloseDecision.Closed,runtime.CloseDevice(new NeutralOriginalDeviceCloseRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,deviceRequest.InvocationId,device)).Observation.Decision);
        }
        else { Assert.False(runtime.ContainInterrupt(containment).NoFurtherDeliveryGuaranteed); Assert.Null(runtime.ResolveIrqContainment(containment)); }
    }
    [Theory]
    [InlineData("mapping")] [InlineData("device")] [InlineData("factory")] [InlineData("device-factory")] [InlineData("history")]
    public void OriginalIrqCloseFencesOriginalSourcesAndRetainsCompletedOutcome(string fault)
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var rootRequest = new NeutralOriginalDomainBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,NeutralDomainProfile.OrdinaryService);
        var root = runtime.Bind(rootRequest).Observation.Lease;
        var deviceRequest = new NeutralOriginalDeviceBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,rootRequest.InvocationId,root,new("device/mmio"),NeutralDeviceRights.Configure);
        var device = runtime.BindDevice(deviceRequest).Observation.Lease;
        var request = new NeutralOriginalIrqBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,deviceRequest.InvocationId,device,new("irq",NeutralInterruptTrigger.Edge));
        var admitted = runtime.BindInterrupt(request); Assert.True(admitted.Observation.IsBound); Assert.Equal(admitted,runtime.ResolveIrqBind(request));
        Assert.Null(runtime.ResolveIrqBind(request with { InvocationId = Guid.NewGuid() }));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var registry = typeof(NeutralDomainRuntimeFacade).GetField("_dependencies",flags)!.GetValue(runtime)!;
        var mappings = (IDictionary)registry.GetType().GetField("Interrupts",flags)!.GetValue(registry)!; var devices = (IDictionary)registry.GetType().GetField("Devices",flags)!.GetValue(registry)!;
        var source = mappings[admitted.Observation.Lease.Handle]!; var parent = devices[device.Handle]!;
        object Clone(object value) => typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(value,null)!;
        var factoryOwner = fault == "device-factory" ? parent : source; var property = factoryOwner.GetType().GetProperty("OriginalAdmission")!; var factory = property.GetValue(factoryOwner)!;
        var history = (IList)parent.GetType().GetProperty("OriginalIrqAdmissions")!.GetValue(parent)!; var fact = history[0]!;
        if (fault is "factory" or "device-factory") property.SetValue(factoryOwner,Clone(factory));
        else if (fault == "history") history.Clear();
        else if (fault == "device") devices[device.Handle] = Clone(parent);
        else mappings[admitted.Observation.Lease.Handle] = Clone(source);
        var close = new NeutralOriginalIrqCloseRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,request.InvocationId,admitted.Observation.Lease);
        Assert.True(runtime.CloseInterrupt(close).NoEffectGuaranteed); Assert.Equal(1,runtime.ActiveInterruptLeaseCount); Assert.Null(runtime.ResolveIrqClose(close));
        if (fault is "factory" or "device-factory") property.SetValue(factoryOwner,factory);
        else if (fault == "history") history.Add(fact);
        else if (fault == "device") devices[device.Handle] = parent;
        else mappings[admitted.Observation.Lease.Handle] = source;
        var closed = runtime.CloseInterrupt(close); Assert.Equal(NeutralInterruptCloseDecision.Closed,closed.Observation.Decision);
        Assert.Equal(closed,runtime.ResolveIrqClose(close)); Assert.Equal(closed,runtime.CloseInterrupt(close)); Assert.Equal(0,runtime.ActiveInterruptLeaseCount);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OriginalIrqInventoryPinsDeviceThroughRouteAndHistoryLoss(bool historyLoss)
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var rootRequest = new NeutralOriginalDomainBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,NeutralDomainProfile.OrdinaryService);
        var root = runtime.Bind(rootRequest).Observation.Lease;
        var deviceRequest = new NeutralOriginalDeviceBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,rootRequest.InvocationId,root,new("device/mmio"),NeutralDeviceRights.Configure);
        var device = runtime.BindDevice(deviceRequest).Observation.Lease;
        var request = new NeutralOriginalIrqBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,deviceRequest.InvocationId,device,new("irq",NeutralInterruptTrigger.Edge));
        var admitted = runtime.BindInterrupt(request);
        Assert.True(runtime.ValidateIrqAdmission(request));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var registry = typeof(NeutralDomainRuntimeFacade).GetField("_dependencies",flags)!.GetValue(runtime)!;
        var mappings = (IDictionary)registry.GetType().GetField("Interrupts",flags)!.GetValue(registry)!; var devices = (IDictionary)registry.GetType().GetField("Devices",flags)!.GetValue(registry)!;
        var source = mappings[admitted.Observation.Lease.Handle]!; var parent = devices[device.Handle]!;
        var history = (IList)parent.GetType().GetProperty("OriginalIrqAdmissions")!.GetValue(parent)!; var fact = history[0]!;
        mappings.Remove(admitted.Observation.Lease.Handle); if (historyLoss) history.Clear();
        Assert.False(runtime.ValidateIrqAdmission(request)); Assert.Null(runtime.ResolveIrqBind(request));
        if (historyLoss) Assert.False(runtime.BindInterrupt(request).NoEffectGuaranteed); // Lost history cannot reset invocation.
        var deviceClose = new NeutralOriginalDeviceCloseRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,deviceRequest.InvocationId,device);
        Assert.Equal(NeutralDeviceCloseDecision.ActiveDependents,runtime.CloseDevice(deviceClose).Observation.Decision);
        Assert.Equal(1,runtime.ActiveDeviceLeaseCount);
        mappings[admitted.Observation.Lease.Handle] = source; if (historyLoss) history.Add(fact);
        var close = new NeutralOriginalIrqCloseRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,request.InvocationId,admitted.Observation.Lease);
        Assert.Equal(NeutralInterruptCloseDecision.Closed,runtime.CloseInterrupt(close).Observation.Decision);
        Assert.False(runtime.ValidateIrqAdmission(request)); Assert.Equal(admitted,runtime.ResolveIrqBind(request)); // Cleanup fact is not current permission.
        Assert.Equal(NeutralDeviceCloseDecision.Closed,runtime.CloseDevice(deviceClose).Observation.Decision);
    }
}
