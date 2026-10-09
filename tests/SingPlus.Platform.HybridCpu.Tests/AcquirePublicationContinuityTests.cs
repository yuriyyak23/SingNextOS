using SingPlus.Contracts;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class AcquirePublicationContinuityTests
{
    [Theory]
    [InlineData("exact")][InlineData("domain")][InlineData("mapping")][InlineData("close")]
    public async Task AcquireCannotPublishAfterOriginalSourceLoss(string outcome)
    {
        var runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime, ChildClosureTupleTests.RootCloseRuntimeProxy>();
        var proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var subject = new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7));
        var domain = provider.BindDomain(subject).Value!;
        var region = new PlatformRegionIdentity(new RegionHandle(new RegionId(77), new RegionGeneration(9)),
            new RegionOwner(subject.DomainId, subject.ProcessGeneration), 4096);
        var slice = new PlatformRegionSlice(region, 0, 512, PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);
        var mapping = provider.MapOwnedRegionSlice(domain, slice).Value!;
        Assert.True(provider.RevokeRegionMapping(mapping.Lease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var records = (global::System.Collections.IDictionary)provider.GetType().GetField("_providerMappings", flags)!.GetValue(provider)!;
        var original = records[mapping.Lease.MappingId]!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        proxy.BeforeAcquire = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); };
        var invocation = Task.Run(() => provider.AcquireRegionMappingFromConsumer(new(mapping.Lease, slice,
            PlatformMemoryConsumerClass.ExternalExecutionDomain, PlatformMemoryAcquireRequirement.AcquisitionFence)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            if (outcome == "close") Assert.True((await Task.Run(() => provider.RevokeDomain(domain)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            if (outcome == "mapping") records.Remove(mapping.Lease.MappingId);
            if (outcome == "domain")
            {
                var domains = (global::System.Collections.IDictionary)provider.GetType().GetField("_domains", flags)!.GetValue(provider)!;
                domains.Remove(domain.LeaseId);
            }
        }
        finally { release.Set(); }
        var result = await invocation;
        Assert.Equal(outcome == "exact", result.IsSuccess);
        if (result.IsSuccess) Assert.True(result.Value!.IsSatisfied);
        Assert.Equal(1, proxy.AcquireCalls);
        Assert.Equal(0, proxy.Runtime.ActiveOwnedRegionMappingCount);
        Assert.NotNull(original.GetType().GetProperty("AcquireObservation")!.GetValue(original));
        Assert.Equal(outcome == "close" ? 0 : 1, proxy.Runtime.ActiveBindingCount);
    }
}
