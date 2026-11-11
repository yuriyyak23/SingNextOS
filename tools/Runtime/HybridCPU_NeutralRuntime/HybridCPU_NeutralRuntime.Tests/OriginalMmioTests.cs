using System.Collections;
using System.Reflection;
using YAKSys_Hybrid_CPU.Core;
namespace HybridCPU_NeutralRuntime.Tests;
public sealed class OriginalMmioTests
{
    [Theory]
    [InlineData("mapping")] [InlineData("device")] [InlineData("factory")] [InlineData("device-factory")] [InlineData("history")]
    public void OriginalMmioCloseFencesOriginalSourcesAndRetainsCompletedOutcome(string fault)
    {
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        NeutralOriginalDomainBindRequest rootRequest = new NeutralOriginalDomainBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,NeutralDomainProfile.OrdinaryService);
        NeutralDomainBindingLease root = runtime.Bind(rootRequest).Observation.Lease;
        NeutralOriginalDeviceBindRequest deviceRequest = new NeutralOriginalDeviceBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,rootRequest.InvocationId,root,new("device/mmio"),NeutralDeviceRights.Read);
        NeutralDeviceLease device = runtime.BindDevice(deviceRequest).Observation.Lease;
        NeutralOriginalMmioMapRequest request = new NeutralOriginalMmioMapRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,deviceRequest.InvocationId,device,new("registers",128),new(0,64),NeutralMmioAccess.Read);
        NeutralOriginalMmioMapObservation admitted = runtime.MapMmio(request); Assert.True(admitted.Observation.IsMapped); Assert.Equal(admitted,runtime.ResolveMmioMap(request));
        Assert.Null(runtime.ResolveMmioMap(request with { InvocationId = Guid.NewGuid() }));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object registry = typeof(NeutralDomainRuntimeFacade).GetField("_dependencies",flags)!.GetValue(runtime)!;
        IDictionary mappings = (IDictionary)registry.GetType().GetField("Mmio",flags)!.GetValue(registry)!; IDictionary devices = (IDictionary)registry.GetType().GetField("Devices",flags)!.GetValue(registry)!;
        object source = mappings[admitted.Observation.Lease.Handle]!; object parent = devices[device.Handle]!;
        object Clone(object value) => typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(value,null)!;
        object factoryOwner = fault == "device-factory" ? parent : source; PropertyInfo property = factoryOwner.GetType().GetProperty("OriginalAdmission")!; object factory = property.GetValue(factoryOwner)!;
        IList history = (IList)parent.GetType().GetProperty("OriginalMmioAdmissions")!.GetValue(parent)!; object fact = history[0]!;
        if (fault is "factory" or "device-factory") property.SetValue(factoryOwner,Clone(factory));
        else if (fault == "history") history.Clear();
        else if (fault == "device") devices[device.Handle] = Clone(parent);
        else mappings[admitted.Observation.Lease.Handle] = Clone(source);
        NeutralOriginalMmioCloseRequest close = new NeutralOriginalMmioCloseRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,request.InvocationId,admitted.Observation.Lease);
        Assert.True(runtime.CloseMmio(close).NoEffectGuaranteed); Assert.Equal(1,runtime.ActiveMmioLeaseCount); Assert.Null(runtime.ResolveMmioClose(close));
        if (fault is "factory" or "device-factory") property.SetValue(factoryOwner,factory);
        else if (fault == "history") history.Add(fact);
        else if (fault == "device") devices[device.Handle] = parent;
        else mappings[admitted.Observation.Lease.Handle] = source;
        NeutralOriginalMmioCloseObservation closed = runtime.CloseMmio(close); Assert.Equal(NeutralMmioCloseDecision.Closed,closed.Observation.Decision);
        Assert.Equal(closed,runtime.ResolveMmioClose(close)); Assert.Equal(closed,runtime.CloseMmio(close)); Assert.Equal(0,runtime.ActiveMmioLeaseCount);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OriginalMmioInventoryPinsDeviceThroughRouteAndHistoryLoss(bool historyLoss)
    {
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        NeutralOriginalDomainBindRequest rootRequest = new NeutralOriginalDomainBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,NeutralDomainProfile.OrdinaryService);
        NeutralDomainBindingLease root = runtime.Bind(rootRequest).Observation.Lease;
        NeutralOriginalDeviceBindRequest deviceRequest = new NeutralOriginalDeviceBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,rootRequest.InvocationId,root,new("device/mmio"),NeutralDeviceRights.Read);
        NeutralDeviceLease device = runtime.BindDevice(deviceRequest).Observation.Lease;
        NeutralOriginalMmioMapRequest request = new NeutralOriginalMmioMapRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,deviceRequest.InvocationId,device,new("registers",128),new(0,64),NeutralMmioAccess.Read);
        NeutralOriginalMmioMapObservation admitted = runtime.MapMmio(request);
        Assert.True(runtime.ValidateMmioAdmission(request));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object registry = typeof(NeutralDomainRuntimeFacade).GetField("_dependencies",flags)!.GetValue(runtime)!;
        IDictionary mappings = (IDictionary)registry.GetType().GetField("Mmio",flags)!.GetValue(registry)!; IDictionary devices = (IDictionary)registry.GetType().GetField("Devices",flags)!.GetValue(registry)!;
        object source = mappings[admitted.Observation.Lease.Handle]!; object parent = devices[device.Handle]!;
        IList history = (IList)parent.GetType().GetProperty("OriginalMmioAdmissions")!.GetValue(parent)!; object fact = history[0]!;
        mappings.Remove(admitted.Observation.Lease.Handle); if (historyLoss) history.Clear();
        Assert.False(runtime.ValidateMmioAdmission(request)); Assert.Null(runtime.ResolveMmioMap(request));
        if (historyLoss) Assert.False(runtime.MapMmio(request).NoEffectGuaranteed); // Lost history cannot reset invocation.
        NeutralOriginalDeviceCloseRequest deviceClose = new NeutralOriginalDeviceCloseRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,deviceRequest.InvocationId,device);
        Assert.Equal(NeutralDeviceCloseDecision.ActiveDependents,runtime.CloseDevice(deviceClose).Observation.Decision);
        Assert.Equal(1,runtime.ActiveDeviceLeaseCount);
        mappings[admitted.Observation.Lease.Handle] = source; if (historyLoss) history.Add(fact);
        NeutralOriginalMmioCloseRequest close = new NeutralOriginalMmioCloseRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,request.InvocationId,admitted.Observation.Lease);
        Assert.Equal(NeutralMmioCloseDecision.Closed,runtime.CloseMmio(close).Observation.Decision);
        Assert.False(runtime.ValidateMmioAdmission(request)); Assert.Equal(admitted,runtime.ResolveMmioMap(request)); // Cleanup fact is not current permission.
        Assert.Equal(NeutralDeviceCloseDecision.Closed,runtime.CloseDevice(deviceClose).Observation.Decision);
    }
}
