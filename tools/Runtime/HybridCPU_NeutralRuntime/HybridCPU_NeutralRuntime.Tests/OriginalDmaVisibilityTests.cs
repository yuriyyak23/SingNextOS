using System.Collections;
using System.Reflection;
using YAKSys_Hybrid_CPU.Core;

namespace HybridCPU_NeutralRuntime.Tests;

public sealed class OriginalDmaVisibilityTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DuplicateIndependentVisibilityHistoryCannotAdmitNewCycleMutation(bool acquire)
    {
        NeutralDomainRuntimeFacade r = new NeutralDomainRuntimeFacade();
        NeutralOriginalDomainBindRequest rootRequest = new NeutralOriginalDomainBindRequest(Guid.NewGuid(),r.MappingClosureIncarnation,NeutralDomainProfile.OrdinaryService);
        NeutralDomainBindingLease root = r.Bind(rootRequest).Observation.Lease;
        NeutralOriginalOwnedRegionMapRequest map = new NeutralOriginalOwnedRegionMapRequest(Guid.NewGuid(),r.MappingClosureIncarnation,rootRequest.InvocationId,root,new(0,64,NeutralMemoryAccess.Write));
        NeutralOwnedRegionMappingLease mapping = r.MapOwnedRegion(map).Observation.Lease;
        NeutralDeviceLease device = r.BindDevice(root,new("history"),NeutralDeviceRights.Write | NeutralDeviceRights.Configure).Lease;
        NeutralOriginalDmaGrantRequest bind = new NeutralOriginalDmaGrantRequest(Guid.NewGuid(),r.MappingClosureIncarnation,rootRequest.InvocationId,map.InvocationId,device,mapping,new(0,64),NeutralDmaDirection.DeviceWritesMemory);
        NeutralDmaGrant grant = r.BindDmaGrant(bind).Observation.Grant;
        NeutralOriginalDmaVisibilityRequest prep = new NeutralOriginalDmaVisibilityRequest(Guid.NewGuid(),r.MappingClosureIncarnation,bind.InvocationId,grant,false,default);
        NeutralOriginalDmaVisibilityObservation prepared = r.PrepareDmaVisibility(prep); Assert.True(prepared.Prepare!.IsPrepared);
        NeutralOriginalDmaVisibilityRequest current = prep;
        if(!acquire)
        {
            current = new(Guid.NewGuid(),r.MappingClosureIncarnation,bind.InvocationId,grant,true,prepared.Prepare.Evidence.Cycle);
            Assert.True(r.AcquireDmaVisibility(current).Acquire!.IsAcquired);
        }
        if(acquire)
        {
            current = prep with { InvocationId = Guid.NewGuid() };
            prepared = r.PrepareDmaVisibility(current); Assert.True(prepared.Prepare!.IsPrepared);
        }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object registry = r.GetType().GetField("_dependencies",flags)!.GetValue(r)!;
        IDictionary grants = (IDictionary)registry.GetType().GetField("Dma",flags)!.GetValue(registry)!;
        object owner = grants[grant.Handle]!;
        IList history = (IList)owner.GetType().GetProperty("OriginalVisibilityHistory")!.GetValue(owner)!;
        history.Add(history[0]); owner.GetType().GetProperty("OriginalVisibilityIssued")!.SetValue(owner,(ulong)history.Count);
        object cycle = owner.GetType().GetProperty("Visibility")!.GetValue(owner)!;
        object? before = cycle.GetType().GetProperty("Revision")!.GetValue(cycle);
        int count = history.Count;
        NeutralOriginalDmaVisibilityRequest next = new NeutralOriginalDmaVisibilityRequest(Guid.NewGuid(),r.MappingClosureIncarnation,bind.InvocationId,grant,acquire,acquire ? prepared.Prepare.Evidence.Cycle : default);
        NeutralOriginalDmaVisibilityObservation raw = acquire ? r.AcquireDmaVisibility(next) : r.PrepareDmaVisibility(next);
        Assert.False(raw.NoEffectGuaranteed); Assert.False(raw.Prepare?.IsPrepared ?? raw.Acquire!.IsAcquired);
        Assert.Equal(before,cycle.GetType().GetProperty("Revision")!.GetValue(cycle)); Assert.Equal(count,history.Count);
        Assert.Null(r.ResolveDmaVisibility(current));
        Assert.False((acquire ? r.AcquireDmaVisibility(next) : r.PrepareDmaVisibility(next)).NoEffectGuaranteed);
        Assert.Equal(NeutralDmaGrantCloseDecision.Closed,r.CloseDmaGrant(new NeutralOriginalDmaGrantCloseRequest(Guid.NewGuid(),r.MappingClosureIncarnation,bind.InvocationId,grant)).Observation.Decision);
        Assert.Null(r.ResolveDmaVisibility(current));
    }

    [Theory]
    [InlineData(false,"grant")] [InlineData(true,"grant")]
    [InlineData(false,"cycle")] [InlineData(true,"cycle")]
    [InlineData(false,"history")] [InlineData(true,"history")]
    [InlineData(false,"fact")] [InlineData(true,"fact")]
    [InlineData(false,"aba")] [InlineData(true,"aba")]
    [InlineData(false,"exhaustion")] [InlineData(true,"exhaustion")]
    public void OriginalVisibilityRejectsSourceDriftAndExactGrantCloseContainsUnknownReplies(bool acquire,string fault)
    {
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade(); INeutralOriginalDmaVisibilityProvider original = (INeutralOriginalDmaVisibilityProvider)runtime;
        NeutralOriginalDomainBindRequest rootRequest = new NeutralOriginalDomainBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,NeutralDomainProfile.OrdinaryService);
        NeutralDomainBindingLease root = runtime.Bind(rootRequest).Observation.Lease;
        NeutralOriginalOwnedRegionMapRequest mappingRequest = new NeutralOriginalOwnedRegionMapRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,rootRequest.InvocationId,root,new(0,128,NeutralMemoryAccess.Write));
        NeutralOwnedRegionMappingLease mapping = runtime.MapOwnedRegion(mappingRequest).Observation.Lease;
        NeutralDeviceLease device = runtime.BindDevice(root,new("device/original-visibility"),NeutralDeviceRights.Write | NeutralDeviceRights.Configure).Lease;
        NeutralOriginalDmaGrantRequest bind = new NeutralOriginalDmaGrantRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,rootRequest.InvocationId,mappingRequest.InvocationId,device,mapping,new(0,64),NeutralDmaDirection.DeviceWritesMemory);
        NeutralDmaGrant grant = original.BindDmaGrant(bind).Observation.Grant;
        NeutralOriginalDmaVisibilityRequest prepareRequest = new NeutralOriginalDmaVisibilityRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,bind.InvocationId,grant,false,default);
        NeutralOriginalDmaVisibilityObservation prepared = original.PrepareDmaVisibility(prepareRequest); Assert.True(prepared.Prepare!.IsPrepared);
        NeutralOriginalDmaVisibilityRequest request = acquire ? new NeutralOriginalDmaVisibilityRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,bind.InvocationId,grant,true,prepared.Prepare.Evidence.Cycle) : prepareRequest;
        NeutralOriginalDmaVisibilityObservation outcome = acquire ? original.AcquireDmaVisibility(request) : prepared;
        Assert.Equal(outcome,original.ResolveDmaVisibility(request));
        NeutralOriginalDmaVisibilityObservation wrongMethod = acquire ? original.PrepareDmaVisibility(request) : original.AcquireDmaVisibility(request);
        Assert.False(wrongMethod.NoEffectGuaranteed);
        Assert.False(wrongMethod.Prepare?.IsPrepared ?? wrongMethod.Acquire!.IsAcquired);
        Assert.Equal(outcome,acquire ? original.AcquireDmaVisibility(request) : original.PrepareDmaVisibility(request));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object registry = typeof(NeutralDomainRuntimeFacade).GetField("_dependencies",flags)!.GetValue(runtime)!;
        IDictionary grants = (IDictionary)registry.GetType().GetField("Dma",flags)!.GetValue(registry)!; object owner = grants[grant.Handle]!;
        PropertyInfo visibleProperty = owner.GetType().GetProperty("Visibility")!; object cycle = visibleProperty.GetValue(owner)!;
        IList history = (IList)owner.GetType().GetProperty("OriginalVisibilityHistory")!.GetValue(owner)!;
        object fact = history[history.Count-1]!;
        object Copy(object value) => typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(value,null)!;
        if (fault == "grant") grants[grant.Handle] = Copy(owner);
        if (fault == "cycle") visibleProperty.SetValue(owner,Copy(cycle));
        if (fault == "history") history.Clear();
        if (fault == "fact") history[history.Count-1] = Copy(fact);
        if (fault == "aba") cycle.GetType().GetProperty("Acquired")!.SetValue(cycle,cycle.GetType().GetProperty("Acquired")!.GetValue(cycle));
        if (fault == "exhaustion")
        {
            cycle.GetType().GetProperty("Revision")!.SetValue(cycle,ulong.MaxValue);
            cycle.GetType().GetProperty("Acquired")!.SetValue(cycle,cycle.GetType().GetProperty("Acquired")!.GetValue(cycle));
        }
        Assert.Null(original.ResolveDmaVisibility(request));
        if (fault is "history" or "fact")
        {
            NeutralOriginalDmaVisibilityObservation refused = acquire ? original.AcquireDmaVisibility(request) : original.PrepareDmaVisibility(request);
            Assert.False(refused.NoEffectGuaranteed); // Lost original history never repeats or fabricates no-effect.
        }
        grants[grant.Handle] = owner;
        NeutralOriginalDmaGrantCloseRequest closeRequest = new NeutralOriginalDmaGrantCloseRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,bind.InvocationId,grant);
        Assert.Equal(NeutralDmaGrantCloseDecision.Closed,original.CloseDmaGrant(closeRequest).Observation.Decision);
        Assert.Equal(0,runtime.ActiveDmaGrantCount);
        Assert.Null(original.ResolveDmaVisibility(request));
        NeutralOriginalDmaVisibilityRequest fresh = request with { InvocationId = Guid.NewGuid() };
        NeutralOriginalDmaVisibilityObservation late = acquire ? original.AcquireDmaVisibility(fresh) : original.PrepareDmaVisibility(fresh);
        Assert.Equal(fault is not ("history" or "fact"),late.NoEffectGuaranteed); // Lost history is unknown even after exact containing Close.
        Assert.False(late.Prepare?.IsPrepared ?? late.Acquire!.IsAcquired);
    }
}
