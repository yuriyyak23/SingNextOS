using System.Collections;
using System.Reflection;
using YAKSys_Hybrid_CPU.Core;
namespace HybridCPU_NeutralRuntime.Tests;
public sealed class OriginalDeviceTests
{
    [Theory]
    [InlineData("device")] [InlineData("root")] [InlineData("admission")] [InlineData("root-factory")] [InlineData("history")]
    public void OriginalDeviceFencesFactoriesAndRetainsCompletedClose(string fault)
    {
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        NeutralOriginalDomainBindRequest rootRequest = new NeutralOriginalDomainBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,NeutralDomainProfile.OrdinaryService);
        NeutralDomainBindingLease root = runtime.Bind(rootRequest).Observation.Lease;
        NeutralOriginalDeviceBindRequest request = new NeutralOriginalDeviceBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,rootRequest.InvocationId,root,new("device/original"),NeutralDeviceRights.Configure);
        NeutralOriginalDeviceBindObservation admitted = runtime.BindDevice(request); Assert.True(admitted.Observation.IsBound);
        Assert.Equal(admitted,runtime.ResolveDeviceBind(request));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object registry = typeof(NeutralDomainRuntimeFacade).GetField("_dependencies",flags)!.GetValue(runtime)!;
        IDictionary devices = (IDictionary)registry.GetType().GetField("Devices",flags)!.GetValue(registry)!;
        IDictionary roots = (IDictionary)registry.GetType().GetField("Domains",flags)!.GetValue(registry)!;
        object device = devices[admitted.Observation.Lease.Handle]!; object parent = roots[root.Handle]!;
        object owner = fault == "root" ? parent : device;
        object Clone(object value) => typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(value,null)!;
        PropertyInfo property = (fault == "root-factory" ? parent : device).GetType().GetProperty("OriginalAdmission")!;
        object factoryOwner = fault == "root-factory" ? parent : device; object factory = property.GetValue(factoryOwner)!;
        IList history = (IList)parent.GetType().GetProperty("OriginalDeviceAdmissions")!.GetValue(parent)!; object fact = history[0]!;
        if (fault is "admission" or "root-factory") property.SetValue(factoryOwner,Clone(factory));
        else if (fault == "history") history.Clear();
        else if (fault == "root") roots[root.Handle] = Clone(parent);
        else devices[admitted.Observation.Lease.Handle] = Clone(device);
        NeutralOriginalDeviceCloseRequest close = new NeutralOriginalDeviceCloseRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,request.InvocationId,admitted.Observation.Lease);
        Assert.True(runtime.CloseDevice(close).NoEffectGuaranteed); Assert.Equal(1,runtime.ActiveDeviceLeaseCount); Assert.Null(runtime.ResolveDeviceClose(close));
        if (fault is "admission" or "root-factory") property.SetValue(factoryOwner,factory);
        else if (fault == "history") history.Add(fact);
        else if (fault == "root") roots[root.Handle] = parent;
        else devices[admitted.Observation.Lease.Handle] = device;
        NeutralOriginalDeviceCloseObservation closed = runtime.CloseDevice(close); Assert.Equal(NeutralDeviceCloseDecision.Closed,closed.Observation.Decision);
        Assert.Equal(closed,runtime.ResolveDeviceClose(close)); Assert.Equal(closed,runtime.CloseDevice(close)); Assert.Equal(0,runtime.ActiveDeviceLeaseCount);
        Assert.Null(runtime.ResolveDeviceClose(close with { InvocationId = Guid.NewGuid() }));
    }
    [Fact]
    public void OriginalDeviceCloseCannotDiscardAdmittedDmaThroughMissingRoute()
    {
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        NeutralOriginalDomainBindRequest rootRequest = new NeutralOriginalDomainBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,NeutralDomainProfile.OrdinaryService);
        NeutralDomainBindingLease root = runtime.Bind(rootRequest).Observation.Lease;
        NeutralOriginalDeviceBindRequest request = new NeutralOriginalDeviceBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,rootRequest.InvocationId,root,new("device/backing"),NeutralDeviceRights.Read | NeutralDeviceRights.Configure);
        NeutralDeviceLease device = runtime.BindDevice(request).Observation.Lease;
        NeutralOriginalOwnedRegionMapRequest mapRequest = new NeutralOriginalOwnedRegionMapRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,rootRequest.InvocationId,root,new(0,128,NeutralMemoryAccess.Read));
        NeutralOwnedRegionMappingLease mapping = runtime.MapOwnedRegion(mapRequest).Observation.Lease;
        NeutralOriginalDmaGrantRequest grantRequest = new NeutralOriginalDmaGrantRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,rootRequest.InvocationId,mapRequest.InvocationId,device,mapping,new(0,64),NeutralDmaDirection.DeviceReadsMemory);
        NeutralDmaGrant grant = runtime.BindDmaGrant(grantRequest).Observation.Grant;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object registry = typeof(NeutralDomainRuntimeFacade).GetField("_dependencies",flags)!.GetValue(runtime)!;
        IDictionary grants = (IDictionary)registry.GetType().GetField("Dma",flags)!.GetValue(registry)!; object owner = grants[grant.Handle]!; grants.Remove(grant.Handle);
        NeutralOriginalDeviceCloseRequest close = new NeutralOriginalDeviceCloseRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,request.InvocationId,device);
        NeutralOriginalDeviceCloseObservation refused = runtime.CloseDevice(close); Assert.True(refused.NoEffectGuaranteed); Assert.Equal(NeutralDeviceCloseDecision.ActiveDependents,refused.Observation.Decision);
        Assert.Equal(1,runtime.ActiveDeviceLeaseCount); Assert.Null(runtime.ResolveDeviceClose(close));
        grants[grant.Handle] = owner;
        NeutralOriginalDmaGrantCloseRequest dmaClose = new NeutralOriginalDmaGrantCloseRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,grantRequest.InvocationId,grant);
        Assert.Equal(NeutralDmaGrantCloseDecision.Closed,runtime.CloseDmaGrant(dmaClose).Observation.Decision);
        Assert.Equal(NeutralDeviceCloseDecision.Closed,runtime.CloseDevice(close).Observation.Decision);
        Assert.Equal(0,runtime.ActiveDeviceLeaseCount);
    }
}
