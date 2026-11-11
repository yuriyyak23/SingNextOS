using System.Collections;
using System.Reflection;
using YAKSys_Hybrid_CPU.Core;

namespace HybridCPU_NeutralRuntime.Tests;

public sealed class OriginalMappingCloseTests
{
    [Fact]
    public async Task ConcurrentDmaAdmissionAndOriginalCloseCannotBothCommit()
    {
        for (int attempt = 0; attempt < 16; attempt++)
        {
            NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
            NeutralDomainBindingLease root = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
            NeutralDeviceLease device = runtime.BindDevice(root, new("dma-original-close"), NeutralDeviceRights.Configure | NeutralDeviceRights.Read).Lease;
            NeutralOwnedRegionMappingLease mapping = runtime.MapOwnedRegion(root, new(0, 1024, NeutralMemoryAccess.Read)).Lease;
            NeutralOriginalMappingCloseRequest request = new NeutralOriginalMappingCloseRequest(Guid.NewGuid(), runtime.MappingClosureIncarnation, mapping);
            using Barrier start = new Barrier(2);
            Task<NeutralOwnedRegionCloseResult> close = Task.Run(() => { Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5))); return runtime.CloseOwnedRegionMapping(request); });
            Task<NeutralDmaGrantResult> bind = Task.Run(() => { Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5))); return runtime.BindDmaGrant(device, mapping, new(0, 128), NeutralDmaDirection.DeviceReadsMemory); });
            await Task.WhenAll(close, bind).WaitAsync(TimeSpan.FromSeconds(6));
            NeutralOwnedRegionCloseResult closed = await close;
            NeutralDmaGrantResult granted = await bind;
            Assert.False(closed.Decision == NeutralOwnedRegionCloseDecision.Closed && granted.IsGranted);
            if (closed.Decision == NeutralOwnedRegionCloseDecision.Closed)
            {
                Assert.NotNull(runtime.ResolveOwnedRegionMappingClose(request));
                Assert.Equal(0, runtime.ActiveDmaGrantCount);
            }
            else
            {
                Assert.True(granted.IsGranted);
                Assert.Equal(NeutralOwnedRegionCloseDecision.ActiveDependents, closed.Decision);
                Assert.Null(runtime.ResolveOwnedRegionMappingClose(request));
                Assert.Equal(NeutralDmaGrantCloseDecision.Closed, runtime.CloseDmaGrant(granted.Grant).Decision);
                Assert.Equal(NeutralOwnedRegionCloseDecision.Closed, runtime.CloseOwnedRegionMapping(mapping).Decision);
                Assert.Null(runtime.ResolveOwnedRegionMappingClose(request)); // Another Close is not this original invocation.
            }
        }
    }

    [Theory]
    [InlineData("exact")] [InlineData("key")] [InlineData("source")]
    [InlineData("lease")] [InlineData("foreign")] [InlineData("unknown")]
    public void OriginalCloseQueryRequiresRetainedSameOwnerFact(string fault)
    {
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        NeutralDomainBindingLease root = runtime.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        NeutralOwnedRegionMappingLease mapping = runtime.MapOwnedRegion(root, new(0, 1024, NeutralMemoryAccess.Read, NeutralMemoryCoherenceModel.NonCoherent)).Lease;
        NeutralOriginalMappingCloseRequest request = new NeutralOriginalMappingCloseRequest(Guid.NewGuid(), runtime.MappingClosureIncarnation, mapping);
        Assert.Equal(NeutralOwnedRegionCloseDecision.Closed, runtime.CloseOwnedRegionMapping(request).Decision);
        Assert.Equal(NeutralOwnedRegionCloseDecision.Faulted, runtime.CloseOwnedRegionMapping(request).Decision);
        Assert.Equal(NeutralDomainCloseDecision.Closed, runtime.Close(root).Decision);
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object registry = typeof(NeutralDomainRuntimeFacade).GetField("_dependencies", flags)!.GetValue(runtime)!;
        IDictionary mappings = (IDictionary)registry.GetType().GetField("Mappings", flags)!.GetValue(registry)!;
        object original = mappings[mapping.Handle]!;
        if (fault == "key") request = request with { InvocationId = Guid.NewGuid() };
        if (fault == "source") request = request with { RuntimeIncarnation = Guid.NewGuid() };
        if (fault == "lease") request = request with { Mapping = mapping with { Epoch = new(2) } };
        if (fault == "foreign") mappings[mapping.Handle] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        if (fault == "unknown")
        {
            object fact = original.GetType().GetProperty("OriginalClose")!.GetValue(original)!;
            fact.GetType().GetProperty("Observation")!.SetValue(fact, null);
        }
        Assert.Equal(fault == "exact", runtime.ResolveOwnedRegionMappingClose(request) is not null);
    }
}
