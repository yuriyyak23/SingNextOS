using System.Collections;
using SingPlus.Contracts;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed partial class ChildClosureTupleTests
{
    [Theory]
    [InlineData("exact")] [InlineData("throw")] [InlineData("not-invoked")]
    public void OriginalExecutionAttemptExistsBeforeNativeEffectAndRetainsRawOnSameOwner(string fault)
    {
        ClosureProvider runtime = new ClosureProvider { StartFault = fault };
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(),runtime);
        PlatformDomainIdentity subject = new PlatformDomainIdentity(new DomainId(10),new ProcessHandle(new ProcessId(12),7));
        PlatformProviderDomainLease parent = provider.BindDomain(subject).Value!;
        const PlatformChildAuthorityClass rights = PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory | PlatformChildAuthorityClass.Execution;
        PlatformProviderChildDomainLease child = provider.CreateChildDomain(parent,new(new(1,4096),new(rights,rights))).Value!;
        PlatformRegionIdentity region = new PlatformRegionIdentity(new RegionHandle(new RegionId(9),new RegionGeneration(4)),new RegionOwner(subject.DomainId,subject.ProcessGeneration),4096);
        PlatformProviderOwnedRegionMapping source = provider.MapOwnedRegionSlice(parent,new(region,0,4096,PlatformMemoryAccess.Read)).Value!;
        PlatformProviderGuestRegionMappingLease mapping = provider.MapGuestRegion(new(child,source,new(0,4096),PlatformGuestMemoryAccess.Read)).Value!;
        PlatformExecutableArtifactReceipt artifact = provider.BindExecutableArtifact(new(child,mapping,new byte[] {1},10)).Value!;
        PlatformChildExecutionStartRequest request = new PlatformChildExecutionStartRequest(child,artifact,new(1),new(1));
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary owners = (global::System.Collections.IDictionary)provider.GetType().GetField("_originalProviderArtifacts",flags)!.GetValue(provider)!;
        object owner = owners[artifact.ArtifactId]!; object? original = null;
        runtime.BeforeStart = () =>
        {
            Assert.False(Monitor.IsEntered(provider.GetType().GetField("_childCreateCommitGate",flags)!.GetValue(provider)!));
            original = owner.GetType().GetProperty("OriginalExecution")!.GetValue(owner);
            Assert.NotNull(original);
            Assert.Null(original.GetType().GetProperty("Observation")!.GetValue(original));
            Assert.Equal(PlatformAuthorityStatus.NotAccepted,provider.StartExecutableArtifact(request).Status);
        };
        if(fault == "throw") Assert.Throws<IOException>(() => provider.StartExecutableArtifact(request));
        else Assert.Equal(fault == "exact",provider.StartExecutableArtifact(request).IsSuccess);
        Assert.Same(original,owner.GetType().GetProperty("OriginalExecution")!.GetValue(owner));
        Assert.Equal(fault == "throw",original!.GetType().GetProperty("Observation")!.GetValue(original) is null);
        Assert.False((bool)owner.GetType().GetProperty("ExecutionInFlight")!.GetValue(owner)!);
        runtime.BeforeStart = null;
        Assert.False(provider.StartExecutableArtifact(request).IsSuccess);
        Assert.Equal(fault == "not-invoked" ? 2 : 1,runtime.StartCalls);
    }
}
