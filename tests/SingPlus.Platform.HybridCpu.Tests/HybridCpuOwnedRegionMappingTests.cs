using SingPlus.Contracts;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class HybridCpuOwnedRegionMappingTests
{
    [Fact]
    public void RedirectedCompletionCannotSubstituteAnotherOriginalClosedOperation()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var domain = provider.BindDomain(new(new DomainId(48), new ProcessHandle(new ProcessId(480), 1))).Value!;
        PlatformRegionRevocationTicket Close(ulong id)
        {
            var region = new PlatformRegionIdentity(new(new(id), new(1)), new(domain.Subject.DomainId, domain.Subject.ProcessGeneration), 4096);
            var mapped = provider.MapOwnedRegionSlice(domain, new(region, 0, 4096, PlatformMemoryAccess.Read)).Value!;
            return provider.BeginRegionMappingRevocation(mapped.Lease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).Value!;
        }
        var first = Close(1); var other = Close(2);
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var operations = (global::System.Collections.IDictionary)provider.GetType().GetField("_providerOperations", flags)!.GetValue(provider)!;
        operations[first.Operation.OperationId] = operations[other.Operation.OperationId];
        Assert.False(provider.ObserveCompletion(first.Operation).IsSuccess);
        Assert.True(provider.ObserveCompletion(other.Operation).IsSuccess);
        operations.Remove(first.Operation.OperationId);
        Assert.Equal(first.Operation, provider.ObserveCompletion(first.Operation).Value!.Operation);
        Assert.True(provider.RevokeDomain(domain).IsSuccess);
        Assert.Equal(0, runtime.ActiveOwnedRegionMappingCount);
    }

    public interface ICompletionConsumerProvider : IPlatformAuthorityProvider, IPlatformFeatureProvider,
        IPlatformOwnedRegionMappingProvider, IPlatformRegionRevocationProvider, IPlatformRegionAcquireProvider,
        IPlatformDomainExecutionProvider { }

    public class CompletionConsumerProxy : global::System.Reflection.DispatchProxy
    {
        public HybridCpuPlatformAuthorityProvider Target { get; set; } = null!;
        public Action? BeforeObserve { get; set; }
        protected override object? Invoke(global::System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IPlatformCompletionProvider.ObserveCompletion)) BeforeObserve?.Invoke();
            return method.Invoke(Target, args);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void KernelCompletionConsumesOriginalFactAcrossIndexLossWithoutReleasingForeignProjection(bool copied)
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var facade = global::System.Reflection.DispatchProxy.Create<ICompletionConsumerProvider, CompletionConsumerProxy>();
        var proxy = (CompletionConsumerProxy)(object)facade;
        proxy.Target = provider;
        var kernel = new RuntimeKernel(facade);
        var (_, owner) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 46, 460, 1);
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle, CapabilityRights.Map | CapabilityRights.Read);
        var mapped = kernel.MapPlatformOwnedRegionSlice(owner, binding, capability, region.Handle, 0, 4096, PlatformMemoryAccess.Read).Value!;
        proxy.BeforeObserve = () =>
        {
            proxy.BeforeObserve = null;
            var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
            var operations = (global::System.Collections.IDictionary)provider.GetType().GetField("_providerOperations", flags)!.GetValue(provider)!;
            var key = Assert.Single(operations.Keys.Cast<object>());
            var original = operations[key]!;
            Assert.Equal(0, runtime.ActiveOwnedRegionMappingCount);
            if (copied) operations[key] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            else operations.Remove(key);
        };
        Assert.Equal(!copied, kernel.RevokePlatformRegionMapping(owner, mapped).IsSuccess);
        Assert.Equal(!copied, kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(copied ? RegionState.Owned : RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.Equal(0, runtime.ActiveOwnedRegionMappingCount);
    }

    [Fact]
    public async Task CompletionObservationRemainsExactDuringConcurrentMappingClosePublication()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var domain = provider.BindDomain(new(new DomainId(47), new ProcessHandle(new ProcessId(470), 1))).Value!;
        var mappings = Enumerable.Range(1, 33).Select(id => provider.MapOwnedRegionSlice(domain,
            new(new(new(new((ulong)id), new(1)), new(domain.Subject.DomainId, domain.Subject.ProcessGeneration), 4096),
                0, 4096, PlatformMemoryAccess.Read)).Value!).ToArray();
        var first = provider.BeginRegionMappingRevocation(mappings[0].Lease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).Value!;
        using var start = new ManualResetEventSlim();
        var writers = mappings.Skip(1).Select(mapping => Task.Run(() =>
        {
            Assert.True(start.Wait(TimeSpan.FromSeconds(10)));
            var ticket = provider.BeginRegionMappingRevocation(mapping.Lease, PlatformRegionRevocationPolicy.DrainBeforeRevoke);
            Assert.True(ticket.IsSuccess);
            Assert.Equal(ticket.Value!.Operation, provider.ObserveCompletion(ticket.Value.Operation).Value!.Operation);
        })).ToArray();
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
                Assert.Equal(first.Operation, provider.ObserveCompletion(first.Operation).Value!.Operation);
        });
        start.Set();
        await Task.WhenAll(writers.Append(reader)).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, runtime.ActiveOwnedRegionMappingCount);
        Assert.True(provider.RevokeDomain(domain).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OriginalCompletedOperationSurvivesAbsentIndexAndRejectsCopiedOwner(bool copied)
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var domain = provider.BindDomain(new(new DomainId(45), new ProcessHandle(new ProcessId(450), 1))).Value!;
        var region = new PlatformRegionIdentity(new(new(45), new(1)), new(domain.Subject.DomainId, domain.Subject.ProcessGeneration), 4096);
        var mapped = provider.MapOwnedRegionSlice(domain, new(region, 0, 4096, PlatformMemoryAccess.Read)).Value!;
        var ticket = provider.BeginRegionMappingRevocation(mapped.Lease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var operations = (global::System.Collections.IDictionary)provider.GetType().GetField("_providerOperations", flags)!.GetValue(provider)!;
        var original = operations[ticket.Operation.OperationId]!;
        if (copied) operations[ticket.Operation.OperationId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        else operations.Remove(ticket.Operation.OperationId);
        Assert.Equal(!copied, provider.ObserveCompletion(ticket.Operation).IsSuccess);
        operations.Remove(ticket.Operation.OperationId);
        Assert.True(provider.RevokeDomain(domain).IsSuccess);
        var consumed = provider.ObserveCompletion(ticket.Operation);
        Assert.True(consumed.IsSuccess);
        Assert.True(consumed.Value!.ProvesClosure);
        Assert.Equal(0, runtime.ActiveOwnedRegionMappingCount);
        Assert.Equal(0, runtime.ActiveBindingCount);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OriginalProviderMappingInventoryPinsRootAcrossRouteLoss(bool copied)
    {
        var runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime,
            ChildClosureTupleTests.RootCloseRuntimeProxy>();
        var proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var domain = provider.BindDomain(new(new DomainId(43), new ProcessHandle(new ProcessId(430), 1))).Value!;
        var region = new PlatformRegionIdentity(new(new(43), new(1)), new(domain.Subject.DomainId, domain.Subject.ProcessGeneration), 4096);
        var mapped = provider.MapOwnedRegionSlice(domain, new(region, 0, 4096, PlatformMemoryAccess.Read)).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var records = (global::System.Collections.IDictionary)provider.GetType().GetField("_providerMappings", flags)!.GetValue(provider)!;
        var original = records[mapped.Lease.MappingId]!;
        if (copied)
        {
            var foreign = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            foreign.GetType().GetProperty("Revoked")!.SetValue(foreign, true);
            records[mapped.Lease.MappingId] = foreign;
        }
        else records.Remove(mapped.Lease.MappingId);
        Assert.False(provider.RevokeDomain(domain).IsSuccess);
        Assert.Equal(0, proxy.CloseCalls);
        Assert.Equal(1, proxy.Runtime.ActiveOwnedRegionMappingCount);
        records[mapped.Lease.MappingId] = original;
        Assert.True(provider.RevokeRegionMapping(mapped.Lease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
        Assert.True(provider.RevokeDomain(domain).IsSuccess);
        Assert.Equal(1, proxy.MappingCloseCalls);
        Assert.Equal(1, proxy.CloseCalls);
        Assert.Equal(0, proxy.Runtime.ActiveOwnedRegionMappingCount);
        Assert.Equal(0, proxy.Runtime.ActiveBindingCount);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CompletedAcquireRequiresOriginalProviderMappingWithoutNewActiveAdmission(bool copied)
    {
        var runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime,
            ChildClosureTupleTests.RootCloseRuntimeProxy>();
        var proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var domain = provider.BindDomain(new(new DomainId(44), new ProcessHandle(new ProcessId(440), 1))).Value!;
        var region = new PlatformRegionIdentity(new(new(44), new(1)), new(domain.Subject.DomainId, domain.Subject.ProcessGeneration), 4096);
        var mapped = provider.MapOwnedRegionSlice(domain, new(region, 0, 4096, PlatformMemoryAccess.Read)).Value!;
        Assert.True(provider.RevokeRegionMapping(mapped.Lease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var records = (global::System.Collections.IDictionary)provider.GetType().GetField("_providerMappings", flags)!.GetValue(provider)!;
        var original = records[mapped.Lease.MappingId]!;
        if (copied) records[mapped.Lease.MappingId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        else records.Remove(mapped.Lease.MappingId);
        var request = new PlatformRegionAcquireRequest(mapped.Lease, mapped.Slice,
            PlatformMemoryConsumerClass.ExternalExecutionDomain, PlatformMemoryAcquireRequirement.AcquisitionFence);
        Assert.False(provider.AcquireRegionMappingFromConsumer(request).IsSuccess);
        Assert.Equal(0, proxy.AcquireCalls);
        records[mapped.Lease.MappingId] = original;
        Assert.True(provider.AcquireRegionMappingFromConsumer(request).Value!.IsSatisfied);
        Assert.Equal(1, proxy.AcquireCalls);
        Assert.Equal(1, proxy.MappingCloseCalls);
        Assert.Equal(0, proxy.Runtime.ActiveOwnedRegionMappingCount);
        Assert.True(provider.RevokeDomain(domain).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ForeignMappingOwnerCannotAuthorizeOriginalKernelBackingRelease(bool completed)
    {
        var runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime,
            ChildClosureTupleTests.RootCloseRuntimeProxy>();
        var proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 43, 430, 1);
        var (_, target) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 44, 440, 1);
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle, CapabilityRights.Map | CapabilityRights.Read);
        var mapped = kernel.MapPlatformOwnedRegionSlice(owner, binding, capability, region.Handle,
            0, 4096, PlatformMemoryAccess.Read).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var records = (global::System.Collections.IDictionary)provider.GetType().GetField("_providerMappings", flags)!.GetValue(provider)!;
        var original = Assert.Single(records.Values.Cast<object>());
        var providerMapping = ((PlatformProviderOwnedRegionMapping)original.GetType().GetProperty("Mapping")!.GetValue(original)!).Lease;
        object? foreignClosure = null;
        if (completed)
        {
            var otherRegion = new PlatformRegionIdentity(new(new(999), new(1)), providerMapping.Region.Owner, 4096);
            var other = provider.MapOwnedRegionSlice(providerMapping.DomainLease,
                new(otherRegion, 0, 4096, PlatformMemoryAccess.Read)).Value!;
            Assert.True(provider.RevokeRegionMapping(other.Lease, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
            foreignClosure = records[other.Lease.MappingId]!.GetType().GetProperty("CloseObservation")!.GetValue(records[other.Lease.MappingId]);
        }
        var calls = proxy.MappingCloseCalls;
        var foreign = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        if (completed) foreign.GetType().GetProperty("CloseObservation")!.SetValue(foreign, foreignClosure);
        records[providerMapping.MappingId] = foreign;
        Assert.False(kernel.RevokePlatformRegionMapping(owner, mapped).IsSuccess);
        Assert.Equal(calls, proxy.MappingCloseCalls);
        Assert.Equal(1, proxy.Runtime.ActiveOwnedRegionMappingCount);
        Assert.Equal(KernelError.PlatformBindingActive, kernel.TransferRegion(owner, target, region).Error);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
        Assert.Equal(false, original.GetType().GetProperty("Revoked")!.GetValue(original));
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("unknown")]
    [InlineData("lease")]
    [InlineData("wrong-semantics")]
    [InlineData("null")]
    [InlineData("throw")]
    public void VisibilityFailureCannotReleaseBackingOrReplaceExactClose(string outcome)
    {
        var runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime,
            ChildClosureTupleTests.RootCloseRuntimeProxy>();
        var proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        var (_, owner) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 41, 410, 1);
        var (_, target) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 42, 420, 1);
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle, CapabilityRights.Map | CapabilityRights.Read);
        var mapped = kernel.MapPlatformOwnedRegionSlice(owner, binding, capability, region.Handle,
            0, 4096, PlatformMemoryAccess.Read).Value!;
        proxy.VisibilityOutcome = outcome;
        Assert.False(kernel.PreparePlatformRegionMappingForConsumer(owner, mapped,
            PlatformMemoryConsumerClass.ExternalExecutionDomain, PlatformMemoryVisibilityRequirement.PublicationFence).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, kernel.TransferRegion(owner, target, region).Error);
        Assert.Equal(1, proxy.Runtime.ActiveOwnedRegionMappingCount);
        Assert.False(kernel.PreparePlatformRegionMappingForConsumer(owner, mapped,
            PlatformMemoryConsumerClass.ExternalExecutionDomain, PlatformMemoryVisibilityRequirement.PublicationFence).IsSuccess);
        Assert.Equal(1, proxy.VisibilityCalls);
        Assert.True(kernel.RevokePlatformRegionMapping(owner, mapped).IsSuccess);
        Assert.Equal(1, proxy.MappingCloseCalls);
        Assert.Equal(0, proxy.Runtime.ActiveOwnedRegionMappingCount);
        Assert.True(kernel.TransferRegion(owner, target, region).IsSuccess);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("null")]
    [InlineData("throw")]
    public void OriginalCloseFailurePinsKernelBackingDespiteNeutralRemoval(string outcome)
    {
        var runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime,
            ChildClosureTupleTests.RootCloseRuntimeProxy>();
        var proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        var (_, owner) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 41, 410, 1);
        var (_, target) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 42, 420, 1);
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle, CapabilityRights.Map | CapabilityRights.Read);
        var mapped = kernel.MapPlatformOwnedRegionSlice(owner, binding, capability, region.Handle,
            0, 4096, PlatformMemoryAccess.Read).Value!;
        proxy.MappingCloseOutcome = outcome;
        Assert.False(kernel.RevokePlatformRegionMapping(owner, mapped).IsSuccess);
        Assert.Equal(0, proxy.Runtime.ActiveOwnedRegionMappingCount);
        Assert.Equal(KernelError.PlatformBindingActive, kernel.TransferRegion(owner, target, region).Error);
        Assert.False(kernel.RevokePlatformRegionMapping(owner, mapped).IsSuccess);
        Assert.Equal(1, proxy.MappingCloseCalls);
    }

    [Theory]
    [InlineData("false-range")]
    [InlineData("unknown")]
    [InlineData("null")]
    [InlineData("throw")]
    [InlineData("handle")]
    [InlineData("epoch")]
    public void OriginalMapFailureRetainsKernelBackingWithoutReturnedObjectCompensation(string outcome)
    {
        var runtime = global::System.Reflection.DispatchProxy.Create<INeutralDomainRuntime,
            ChildClosureTupleTests.RootCloseRuntimeProxy>();
        var proxy = (ChildClosureTupleTests.RootCloseRuntimeProxy)(object)runtime;
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        var (_, owner) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 41, 410, 1);
        var (_, target) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 42, 420, 1);
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle, CapabilityRights.Map | CapabilityRights.Read);
        proxy.MapOutcome = outcome;
        var mapped = kernel.MapPlatformOwnedRegionSlice(owner, binding, capability, region.Handle,
            0, 4096, PlatformMemoryAccess.Read);
        Assert.False(mapped.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, kernel.TransferRegion(owner, target, region).Error);
        Assert.Equal(1, proxy.Runtime.ActiveOwnedRegionMappingCount);
        Assert.Equal(1, proxy.MapCalls);
        Assert.Equal(0, proxy.MappingCloseCalls);
        Assert.False(kernel.MapPlatformOwnedRegionSlice(owner, binding, capability, region.Handle,
            0, 4096, PlatformMemoryAccess.Read).IsSuccess);
        Assert.Equal(1, proxy.MapCalls);
    }

    [Fact]
    public void ExactSlicePublishesFenceAndClosesBeforeOwnershipCanMove()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        var (_, owner) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 41, 410, 1);
        var (_, target) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 42, 420, 1);
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(
            kernel,
            owner,
            region.Handle,
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write);

        var mapped = kernel.MapPlatformOwnedRegionSlice(
            owner,
            binding,
            capability,
            region.Handle,
            offset: 64,
            length: 512,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);

        Assert.True(mapped.IsSuccess, mapped.Message);
        Assert.Equal(1, runtime.ActiveOwnedRegionMappingCount);
        Assert.Equal(64, mapped.Value!.Offset);
        Assert.Equal(512, mapped.Value.Length);
        Assert.NotEqual(typeof(PlatformProviderRegionMappingId), typeof(NeutralOwnedRegionMappingHandle));
        Assert.NotEqual(typeof(PlatformProviderLeaseGeneration), typeof(NeutralOwnedRegionMappingEpoch));

        var coherent = kernel.PreparePlatformRegionMappingForConsumer(
            owner,
            mapped.Value,
            PlatformMemoryConsumerClass.ExternalExecutionDomain,
            PlatformMemoryVisibilityRequirement.CoherentAccess);
        Assert.Equal(KernelError.PlatformUnsupported, coherent.Error);

        var fence = kernel.PreparePlatformRegionMappingForConsumer(
            owner,
            mapped.Value,
            PlatformMemoryConsumerClass.ExternalExecutionDomain,
            PlatformMemoryVisibilityRequirement.PublicationFence);
        Assert.True(fence.IsSuccess, fence.Message);
        Assert.True(fence.Value!.IsSatisfied);
        Assert.Equal(
            PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied,
            fence.Value.Outcome);

        Assert.Equal(
            KernelError.PlatformBindingActive,
            kernel.TransferRegion(owner, target, region).Error);

        Assert.True(kernel.RevokePlatformRegionMapping(owner, mapped.Value).IsSuccess);
        Assert.Equal(0, runtime.ActiveOwnedRegionMappingCount);

        var moved = kernel.TransferRegion(owner, target, region);
        Assert.True(moved.IsSuccess, moved.Message);
        Assert.Equal(new RegionGeneration(2), moved.Value!.Handle.Generation);
    }

    [Fact]
    public void DirectProviderRequiresExactMappingGenerationAndOwner()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var subject = new PlatformDomainIdentity(
            new DomainId(430),
            new ProcessHandle(new ProcessId(431), 3));
        var domain = provider.BindDomain(subject).Value!;
        var region = new PlatformRegionIdentity(
            new RegionHandle(new RegionId(9), new RegionGeneration(4)),
            new RegionOwner(subject.DomainId, subject.ProcessGeneration),
            2048);
        var slice = new PlatformRegionSlice(region, 128, 256, PlatformMemoryAccess.Read);
        var mapping = provider.MapOwnedRegionSlice(domain, slice);
        Assert.True(mapping.IsSuccess, mapping.Message);
        Assert.Equal(1, runtime.ActiveOwnedRegionMappingCount);

        var stale = mapping.Value!.Lease with
        {
            Generation = new PlatformProviderLeaseGeneration(
                mapping.Value.Lease.Generation.Value + 1),
        };
        var visibility = provider.PrepareRegionMappingForConsumer(
            new PlatformRegionVisibilityRequest(
                stale,
                slice,
                PlatformMemoryConsumerClass.ExternalExecutionDomain,
                PlatformMemoryVisibilityRequirement.PublicationFence));

        Assert.Equal(PlatformAuthorityStatus.Stale, visibility.Status);
        Assert.Equal(1, runtime.ActiveOwnedRegionMappingCount);
        Assert.True(provider.RevokeRegionMapping(
            mapping.Value.Lease,
            PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
        Assert.Equal(0, runtime.ActiveOwnedRegionMappingCount);
        Assert.True(provider.RevokeDomain(domain).IsSuccess);
    }

    [Fact]
    public void ProcessTeardownClosesExactMappingBeforeHybridCpuDomainAndLocalExit()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        var (process, owner) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(kernel, 44, 440, 1);
        var region = kernel.AllocateBuffer<byte>(owner, 1024).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(
            kernel,
            owner,
            region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        Assert.True(kernel.MapPlatformOwnedRegionSlice(
            owner,
            binding,
            capability,
            region.Handle,
            16,
            128,
            PlatformMemoryAccess.Read).IsSuccess);
        Assert.Equal(1, runtime.ActiveOwnedRegionMappingCount);
        Assert.Equal(1, runtime.ActiveBindingCount);

        var terminate = kernel.TerminateProcess(owner);

        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.Equal(0, runtime.ActiveOwnedRegionMappingCount);
        Assert.Equal(0, runtime.ActiveBindingCount);
        Assert.Equal(ProcessState.Exited, process.State);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(owner).Error);
    }

    private static CapabilityId MintRegionCapability(
        RuntimeKernel kernel,
        ProcessHandle subject,
        RegionHandle region,
        CapabilityRights rights)
    {
        var process = kernel.Processes.Resolve(subject);
        Assert.True(process.IsSuccess, process.Message);
        var capability = kernel.MintCapability(
            process.Value!.DomainId,
            subject,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.RegionId),
            rights);
        Assert.True(capability.IsSuccess, capability.Message);
        return capability.Value!.CapabilityId;
    }
}
