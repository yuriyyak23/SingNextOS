using System.Collections;
using System.Reflection;
using YAKSys_Hybrid_CPU.Core;

namespace HybridCPU_NeutralRuntime.Tests;

public sealed class OriginalAdmissionTests
{
    [Fact]
    public void OriginalRootCloseRequiresSameOwnerAndNoBackingUsersAndLookupCannotRepeatEffect()
    {
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        NeutralOriginalDomainBindRequest bind = new NeutralOriginalDomainBindRequest(Guid.NewGuid(), runtime.MappingClosureIncarnation, NeutralDomainProfile.OrdinaryService);
        NeutralDomainBindingLease root = runtime.Bind(bind).Observation.Lease;
        NeutralOriginalDomainCloseRequest request = new NeutralOriginalDomainCloseRequest(Guid.NewGuid(), runtime.MappingClosureIncarnation, bind.InvocationId, root);
        INeutralOriginalDomainCloseProvider closer = (INeutralOriginalDomainCloseProvider)runtime;
        NeutralOwnedRegionMappingLease mapping = runtime.MapOwnedRegion(root, new(0, 128, NeutralMemoryAccess.Read)).Lease;
        Assert.Equal(NeutralDomainCloseDecision.ActiveDependents, closer.Close(request).Observation.Decision);
        Assert.Null(runtime.ResolveDomainClose(request));
        Assert.Equal(1, runtime.ActiveBindingCount);
        Assert.Equal(NeutralOwnedRegionCloseDecision.Closed, runtime.CloseOwnedRegionMapping(mapping).Decision);
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object registry = typeof(NeutralDomainRuntimeFacade).GetField("_dependencies", flags)!.GetValue(runtime)!;
        IDictionary records = (IDictionary)registry.GetType().GetField("Domains", flags)!.GetValue(registry)!;
        object original = records[root.Handle]!;
        records[root.Handle] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        Assert.Equal(NeutralDomainCloseDecision.Faulted, closer.Close(request).Observation.Decision);
        Assert.Equal(NeutralDomainCloseDecision.Faulted, runtime.ContinueOriginalDomainClose(request).Observation.Decision);
        Assert.Null(runtime.ResolveDomainClose(request));
        records[root.Handle] = original;
        // Close admitted the original intent even while backing users existed.
        // A second Close must refuse; only its continuation may finish that intent.
        Assert.Equal(NeutralDomainCloseDecision.Faulted, closer.Close(request).Observation.Decision);
        NeutralOriginalDomainCloseObservation closed = runtime.ContinueOriginalDomainClose(request);
        Assert.Equal(NeutralDomainCloseDecision.Closed, closed.Observation.Decision);
        Assert.Equal(closed, runtime.ResolveDomainClose(request));
        Assert.Equal(NeutralDomainCloseDecision.Faulted, closer.Close(request).Observation.Decision);
        records[root.Handle] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        Assert.Null(runtime.ResolveDomainClose(request));
        records[root.Handle] = original;
        Assert.Equal(closed, runtime.ResolveDomainClose(request));
        Assert.Null(runtime.ResolveDomainClose(request with { InvocationId = Guid.NewGuid() }));
        NeutralDomainRuntimeFacade foreign = new NeutralDomainRuntimeFacade();
        Assert.Equal(root, foreign.Bind(NeutralDomainProfile.OrdinaryService).Lease);
        Assert.False(((INeutralOriginalDomainCloseProvider)foreign).Close(request).SourceFenceMatched);
        Assert.Equal(1, foreign.ActiveBindingCount);
    }

    [Theory]
    [InlineData("root")] [InlineData("mapping")]
    public void ExactAdmissionCannotCloseEqualTupleOwnerCopy(string owner)
    {
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        NeutralOriginalDomainBindRequest bindRequest = new NeutralOriginalDomainBindRequest(Guid.NewGuid(), runtime.MappingClosureIncarnation, NeutralDomainProfile.OrdinaryService);
        NeutralDomainBindingLease root = runtime.Bind(bindRequest).Observation.Lease;
        NeutralOriginalOwnedRegionMapRequest mapRequest = new NeutralOriginalOwnedRegionMapRequest(Guid.NewGuid(), runtime.MappingClosureIncarnation,
            bindRequest.InvocationId, root, new(0, 128, NeutralMemoryAccess.Read));
        NeutralOwnedRegionMappingLease mapping = runtime.MapOwnedRegion(mapRequest).Observation.Lease;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object registry = typeof(NeutralDomainRuntimeFacade).GetField("_dependencies", flags)!.GetValue(runtime)!;
        IDictionary records = (IDictionary)registry.GetType().GetField(owner == "root" ? "Domains" : "Mappings", flags)!.GetValue(registry)!;
        object key = owner == "root" ? root.Handle : mapping.Handle;
        object original = records[key]!;
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
