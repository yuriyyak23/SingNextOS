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
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        NeutralOriginalDomainBindRequest rootRequest = new NeutralOriginalDomainBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,NeutralDomainProfile.OrdinaryService);
        NeutralDomainBindingLease root = runtime.Bind(rootRequest).Observation.Lease;
        NeutralOriginalDeviceBindRequest deviceRequest = new NeutralOriginalDeviceBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,rootRequest.InvocationId,root,new("device"),NeutralDeviceRights.Configure);
        NeutralDeviceLease device = runtime.BindDevice(deviceRequest).Observation.Lease;
        NeutralOriginalIrqBindRequest admission = new NeutralOriginalIrqBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,deviceRequest.InvocationId,device,new("irq",NeutralInterruptTrigger.Edge));
        NeutralInterruptLease binding = runtime.BindInterrupt(admission).Observation.Lease;
        NeutralInterruptDeliverySequence sequence = runtime.SignalInterrupt(binding).Sequence;
        NeutralOriginalIrqPollRequest poll = new NeutralOriginalIrqPollRequest(Guid.NewGuid(),admission,binding); NeutralOriginalIrqPollObservation raw = runtime.PollInterrupt(poll);
        Assert.True(runtime.ValidateIrqPoll(poll)); Assert.Equal(raw,runtime.ResolveIrqPoll(poll));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object registry = runtime.GetType().GetField("_dependencies",flags)!.GetValue(runtime)!;
        IDictionary routes = (IDictionary)registry.GetType().GetField("Interrupts",flags)!.GetValue(registry)!; object source = routes[binding.Handle]!;
        IList polls = (IList)source.GetType().GetProperty("Polls")!.GetValue(source)!;
        if (fault == "aba") source.GetType().GetProperty("Sequence")!.SetValue(source,sequence); // Equal write still invalidates observed revision.
        if (fault == "history") polls.Clear();
        if (fault == "copy") polls[0] = typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(polls[0],null)!;
        if (fault == "exhausted") source.GetType().GetProperty("SourceRevisionExhausted")!.SetValue(source,true);
        Assert.False(runtime.ValidateIrqPoll(poll));
        NeutralOriginalIrqCompleteRequest completion = new NeutralOriginalIrqCompleteRequest(Guid.NewGuid(),admission,binding,sequence,poll.InvocationId);
        NeutralOriginalIrqCompleteObservation refused = runtime.CompleteInterruptDelivery(completion); Assert.True(refused.NoEffectGuaranteed);
        Assert.Equal(NeutralInterruptCloseDecision.PendingDelivery,runtime.CloseInterrupt(binding).Decision);
        Assert.Equal(1,runtime.ActiveInterruptLeaseCount);
        NeutralOriginalIrqContainRequest containment = new NeutralOriginalIrqContainRequest(Guid.NewGuid(),admission,binding);
        if (fault == "aba")
        {
            NeutralOriginalIrqContainObservation contained = runtime.ContainInterrupt(containment); Assert.True(contained.NoFurtherDeliveryGuaranteed);
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
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        NeutralOriginalDomainBindRequest rootRequest = new NeutralOriginalDomainBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,NeutralDomainProfile.OrdinaryService);
        NeutralDomainBindingLease root = runtime.Bind(rootRequest).Observation.Lease;
        NeutralOriginalDeviceBindRequest deviceRequest = new NeutralOriginalDeviceBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,rootRequest.InvocationId,root,new("device/mmio"),NeutralDeviceRights.Configure);
        NeutralDeviceLease device = runtime.BindDevice(deviceRequest).Observation.Lease;
        NeutralOriginalIrqBindRequest request = new NeutralOriginalIrqBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,deviceRequest.InvocationId,device,new("irq",NeutralInterruptTrigger.Edge));
        NeutralOriginalIrqBindObservation admitted = runtime.BindInterrupt(request); Assert.True(admitted.Observation.IsBound); Assert.Equal(admitted,runtime.ResolveIrqBind(request));
        Assert.Null(runtime.ResolveIrqBind(request with { InvocationId = Guid.NewGuid() }));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object registry = typeof(NeutralDomainRuntimeFacade).GetField("_dependencies",flags)!.GetValue(runtime)!;
        IDictionary mappings = (IDictionary)registry.GetType().GetField("Interrupts",flags)!.GetValue(registry)!; IDictionary devices = (IDictionary)registry.GetType().GetField("Devices",flags)!.GetValue(registry)!;
        object source = mappings[admitted.Observation.Lease.Handle]!; object parent = devices[device.Handle]!;
        object Clone(object value) => typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(value,null)!;
        object factoryOwner = fault == "device-factory" ? parent : source; PropertyInfo property = factoryOwner.GetType().GetProperty("OriginalAdmission")!; object factory = property.GetValue(factoryOwner)!;
        IList history = (IList)parent.GetType().GetProperty("OriginalIrqAdmissions")!.GetValue(parent)!; object fact = history[0]!;
        if (fault is "factory" or "device-factory") property.SetValue(factoryOwner,Clone(factory));
        else if (fault == "history") history.Clear();
        else if (fault == "device") devices[device.Handle] = Clone(parent);
        else mappings[admitted.Observation.Lease.Handle] = Clone(source);
        NeutralOriginalIrqCloseRequest close = new NeutralOriginalIrqCloseRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,request.InvocationId,admitted.Observation.Lease);
        Assert.True(runtime.CloseInterrupt(close).NoEffectGuaranteed); Assert.Equal(1,runtime.ActiveInterruptLeaseCount); Assert.Null(runtime.ResolveIrqClose(close));
        if (fault is "factory" or "device-factory") property.SetValue(factoryOwner,factory);
        else if (fault == "history") history.Add(fact);
        else if (fault == "device") devices[device.Handle] = parent;
        else mappings[admitted.Observation.Lease.Handle] = source;
        NeutralOriginalIrqCloseObservation closed = runtime.CloseInterrupt(close); Assert.Equal(NeutralInterruptCloseDecision.Closed,closed.Observation.Decision);
        Assert.Equal(closed,runtime.ResolveIrqClose(close)); Assert.Equal(closed,runtime.CloseInterrupt(close)); Assert.Equal(0,runtime.ActiveInterruptLeaseCount);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OriginalIrqInventoryPinsDeviceThroughRouteAndHistoryLoss(bool historyLoss)
    {
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        NeutralOriginalDomainBindRequest rootRequest = new NeutralOriginalDomainBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,NeutralDomainProfile.OrdinaryService);
        NeutralDomainBindingLease root = runtime.Bind(rootRequest).Observation.Lease;
        NeutralOriginalDeviceBindRequest deviceRequest = new NeutralOriginalDeviceBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,rootRequest.InvocationId,root,new("device/mmio"),NeutralDeviceRights.Configure);
        NeutralDeviceLease device = runtime.BindDevice(deviceRequest).Observation.Lease;
        NeutralOriginalIrqBindRequest request = new NeutralOriginalIrqBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,deviceRequest.InvocationId,device,new("irq",NeutralInterruptTrigger.Edge));
        NeutralOriginalIrqBindObservation admitted = runtime.BindInterrupt(request);
        Assert.True(runtime.ValidateIrqAdmission(request));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object registry = typeof(NeutralDomainRuntimeFacade).GetField("_dependencies",flags)!.GetValue(runtime)!;
        IDictionary mappings = (IDictionary)registry.GetType().GetField("Interrupts",flags)!.GetValue(registry)!; IDictionary devices = (IDictionary)registry.GetType().GetField("Devices",flags)!.GetValue(registry)!;
        object source = mappings[admitted.Observation.Lease.Handle]!; object parent = devices[device.Handle]!;
        IList history = (IList)parent.GetType().GetProperty("OriginalIrqAdmissions")!.GetValue(parent)!; object fact = history[0]!;
        mappings.Remove(admitted.Observation.Lease.Handle); if (historyLoss) history.Clear();
        Assert.False(runtime.ValidateIrqAdmission(request)); Assert.Null(runtime.ResolveIrqBind(request));
        if (historyLoss) Assert.False(runtime.BindInterrupt(request).NoEffectGuaranteed); // Lost history cannot reset invocation.
        NeutralOriginalDeviceCloseRequest deviceClose = new NeutralOriginalDeviceCloseRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,deviceRequest.InvocationId,device);
        Assert.Equal(NeutralDeviceCloseDecision.ActiveDependents,runtime.CloseDevice(deviceClose).Observation.Decision);
        Assert.Equal(1,runtime.ActiveDeviceLeaseCount);
        mappings[admitted.Observation.Lease.Handle] = source; if (historyLoss) history.Add(fact);
        NeutralOriginalIrqCloseRequest close = new NeutralOriginalIrqCloseRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,request.InvocationId,admitted.Observation.Lease);
        Assert.Equal(NeutralInterruptCloseDecision.Closed,runtime.CloseInterrupt(close).Observation.Decision);
        Assert.False(runtime.ValidateIrqAdmission(request)); Assert.Equal(admitted,runtime.ResolveIrqBind(request)); // Cleanup fact is not current permission.
        Assert.Equal(NeutralDeviceCloseDecision.Closed,runtime.CloseDevice(deviceClose).Observation.Decision);
    }
}
