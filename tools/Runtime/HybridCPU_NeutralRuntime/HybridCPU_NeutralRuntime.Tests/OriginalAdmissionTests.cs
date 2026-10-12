using System.Collections;
using System.Reflection;
using YAKSys_Hybrid_CPU.Core;

namespace HybridCPU_NeutralRuntime.Tests;

public sealed class OriginalAdmissionTests
{
    [Fact]
    public void OriginalRootCloseRequiresSameOwnerAndNoBackingUsersAndLookupCannotRepeatEffect()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var bind = new NeutralOriginalDomainBindRequest(Guid.NewGuid(), runtime.MappingClosureIncarnation, NeutralDomainProfile.OrdinaryService);
        var root = runtime.Bind(bind).Observation.Lease;
        var request = new NeutralOriginalDomainCloseRequest(Guid.NewGuid(), runtime.MappingClosureIncarnation, bind.InvocationId, root);
        var closer = (INeutralOriginalDomainCloseProvider)runtime;
        var mapping = runtime.MapOwnedRegion(root, new(0, 128, NeutralMemoryAccess.Read)).Lease;
        Assert.Equal(NeutralDomainCloseDecision.ActiveDependents, closer.Close(request).Observation.Decision);
        Assert.Null(runtime.ResolveDomainClose(request));
        Assert.Equal(1, runtime.ActiveBindingCount);
        Assert.Equal(NeutralOwnedRegionCloseDecision.Closed, runtime.CloseOwnedRegionMapping(mapping).Decision);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var registry = typeof(NeutralDomainRuntimeFacade).GetField("_dependencies", flags)!.GetValue(runtime)!;
        var records = (IDictionary)registry.GetType().GetField("Domains", flags)!.GetValue(registry)!;
        var original = records[root.Handle]!;
        records[root.Handle] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        Assert.Equal(NeutralDomainCloseDecision.Faulted, closer.Close(request).Observation.Decision);
        Assert.Null(runtime.ResolveDomainClose(request));
        records[root.Handle] = original;
        var closed = closer.Close(request);
        Assert.Equal(NeutralDomainCloseDecision.Closed, closed.Observation.Decision);
        Assert.Equal(closed, runtime.ResolveDomainClose(request));
        Assert.Equal(NeutralDomainCloseDecision.Faulted, closer.Close(request).Observation.Decision);
        records[root.Handle] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        Assert.Null(runtime.ResolveDomainClose(request));
        records[root.Handle] = original;
        Assert.Equal(closed, runtime.ResolveDomainClose(request));
        Assert.Null(runtime.ResolveDomainClose(request with { InvocationId = Guid.NewGuid() }));
        var foreign = new NeutralDomainRuntimeFacade();
        Assert.Equal(root, foreign.Bind(NeutralDomainProfile.OrdinaryService).Lease);
        Assert.False(((INeutralOriginalDomainCloseProvider)foreign).Close(request).SourceFenceMatched);
        Assert.Equal(1, foreign.ActiveBindingCount);
    }

    [Theory]
    [InlineData("root")] [InlineData("mapping")]
    public void ExactAdmissionCannotCloseEqualTupleOwnerCopy(string owner)
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var bindRequest = new NeutralOriginalDomainBindRequest(Guid.NewGuid(), runtime.MappingClosureIncarnation, NeutralDomainProfile.OrdinaryService);
        var root = runtime.Bind(bindRequest).Observation.Lease;
        var mapRequest = new NeutralOriginalOwnedRegionMapRequest(Guid.NewGuid(), runtime.MappingClosureIncarnation,
            bindRequest.InvocationId, root, new(0, 128, NeutralMemoryAccess.Read));
        var mapping = runtime.MapOwnedRegion(mapRequest).Observation.Lease;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var registry = typeof(NeutralDomainRuntimeFacade).GetField("_dependencies", flags)!.GetValue(runtime)!;
        var records = (IDictionary)registry.GetType().GetField(owner == "root" ? "Domains" : "Mappings", flags)!.GetValue(registry)!;
        object key = owner == "root" ? root.Handle : mapping.Handle;
        var original = records[key]!;
        records[key] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        Assert.Equal(NeutralOwnedRegionCloseDecision.Faulted, runtime.CloseOwnedRegionMapping(mapping).Decision);
        Assert.Equal(1, runtime.ActiveOwnedRegionMappingCount);
        records[key] = original;
        Assert.Equal(NeutralOwnedRegionCloseDecision.Closed, runtime.CloseOwnedRegionMapping(mapping).Decision);
        Assert.Equal(NeutralDomainCloseDecision.Closed, runtime.Close(root).Decision);
        Assert.False(runtime.Bind(bindRequest).Observation.IsBound);
        Assert.False(runtime.MapOwnedRegion(mapRequest).Observation.IsMapped);
    }
}
