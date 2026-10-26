using System.Collections;
using System.Reflection;
using YAKSys_Hybrid_CPU.Core;

namespace HybridCPU_NeutralRuntime.Tests;

public sealed class OriginalDmaTests
{
    [Theory]
    [InlineData("grant")] [InlineData("device")] [InlineData("mapping")] [InlineData("root")]
    [InlineData("admission")] [InlineData("root-factory")] [InlineData("mapping-factory")]
    [InlineData("history")] [InlineData("duplicate")]
    public void OriginalDmaCloseRejectsOwnerAndFactoryCopiesAndRetainsExactCompletedOutcome(string fault)
    {
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        INeutralOriginalDmaGrantProvider original = (INeutralOriginalDmaGrantProvider)runtime;
        NeutralOriginalDomainBindRequest bind = new NeutralOriginalDomainBindRequest(Guid.NewGuid(), runtime.MappingClosureIncarnation, NeutralDomainProfile.OrdinaryService);
        NeutralDomainBindingLease root = runtime.Bind(bind).Observation.Lease;
        NeutralOriginalOwnedRegionMapRequest map = new NeutralOriginalOwnedRegionMapRequest(Guid.NewGuid(), runtime.MappingClosureIncarnation, bind.InvocationId, root, new(0,128,NeutralMemoryAccess.Read));
        NeutralOwnedRegionMappingLease mapping = runtime.MapOwnedRegion(map).Observation.Lease;
        NeutralDeviceLease device = runtime.BindDevice(root, new("device/original-dma"), NeutralDeviceRights.Read | NeutralDeviceRights.Configure).Lease;
        NeutralOriginalDmaGrantRequest request = new NeutralOriginalDmaGrantRequest(Guid.NewGuid(), runtime.MappingClosureIncarnation, bind.InvocationId, map.InvocationId, device, mapping, new(0,64), NeutralDmaDirection.DeviceReadsMemory);
        NeutralOriginalDmaGrantObservation admitted = original.BindDmaGrant(request);
        Assert.True(admitted.Observation.IsGranted);
        Assert.Equal(admitted, original.ResolveDmaGrantBind(request));
        Assert.Null(original.ResolveDmaGrantBind(request with { InvocationId = Guid.NewGuid() }));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object registry = typeof(NeutralDomainRuntimeFacade).GetField("_dependencies", flags)!.GetValue(runtime)!;
        IDictionary Records(string name) => (IDictionary)registry.GetType().GetField(name, flags)!.GetValue(registry)!;
        IDictionary grants = Records("Dma"); IDictionary devices = Records("Devices"); IDictionary mappings = Records("Mappings"); IDictionary roots = Records("Domains");
        object grantState = grants[admitted.Observation.Grant.Handle]!; object deviceState = devices[device.Handle]!;
        object mappingState = mappings[mapping.Handle]!; object rootState = roots[root.Handle]!;
        object Clone(object value) => typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(value,null)!;
        IDictionary records = fault == "device" ? devices : fault == "mapping" ? mappings : fault == "root" ? roots : grants;
        object key = fault == "device" ? device.Handle : fault == "mapping" ? mapping.Handle : fault == "root" ? root.Handle : admitted.Observation.Grant.Handle;
        object owner = records[key]!;
        object? factoryOwner = fault == "root-factory" ? rootState : fault == "mapping-factory" ? mappingState : fault == "admission" ? grantState : null;
        PropertyInfo? factoryProperty = factoryOwner?.GetType().GetProperty("OriginalAdmission");
        object? factory = factoryProperty?.GetValue(factoryOwner);
        IList history = (IList)deviceState.GetType().GetProperty("OriginalDmaAdmissions")!.GetValue(deviceState)!;
        object historyFact = history[0]!;
        if (factoryProperty is not null) factoryProperty.SetValue(factoryOwner, Clone(factory!));
        else if (fault == "history") history.Clear();
        else if (fault == "duplicate")
        {
            history.Add(historyFact);
            deviceState.GetType().GetProperty("OriginalDmaIssued")?.SetValue(deviceState,(ulong)history.Count);
            Assert.Null(original.ResolveDmaGrantBind(request));
        }
        else records[key] = Clone(owner);
        NeutralOriginalDmaGrantCloseRequest close = new NeutralOriginalDmaGrantCloseRequest(Guid.NewGuid(), runtime.MappingClosureIncarnation, request.InvocationId, admitted.Observation.Grant);
        Assert.True(original.CloseDmaGrant(close).NoEffectGuaranteed);
        Assert.Equal(1,runtime.ActiveDmaGrantCount);
        Assert.Null(original.ResolveDmaGrantClose(close));
        if (factoryProperty is not null) factoryProperty.SetValue(factoryOwner,factory);
        else if (fault == "history") history.Add(historyFact);
        else if (fault == "duplicate")
        {
            history.RemoveAt(history.Count-1);
            deviceState.GetType().GetProperty("OriginalDmaIssued")?.SetValue(deviceState,(ulong)history.Count);
        }
        else records[key] = owner;
        NeutralOriginalDmaGrantCloseObservation closed = original.CloseDmaGrant(close);
        Assert.Equal(NeutralDmaGrantCloseDecision.Closed,closed.Observation.Decision);
        Assert.Equal(0,runtime.ActiveDmaGrantCount);
        Assert.Equal(closed,original.ResolveDmaGrantClose(close));
        Assert.Equal(closed,original.CloseDmaGrant(close));
        Assert.Null(original.ResolveDmaGrantClose(close with { InvocationId = Guid.NewGuid() }));
        Assert.Equal(0,runtime.ActiveDmaGrantCount);
        if(fault == "duplicate")
        {
            history.Clear(); // Exact child closure does not make lost parent history safe.
            Assert.NotEqual(NeutralDeviceCloseDecision.Closed,runtime.CloseDevice(device).Decision);
            Assert.Equal(1,runtime.ActiveDeviceLeaseCount);
            Assert.Null(original.ResolveDmaGrantClose(close));
            Assert.False(original.CloseDmaGrant(close).NoEffectGuaranteed);
        }
    }
}
