using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed class PlatformAuthorityBridgeChildDomainTests
{
    [Fact]
    public void RootClosureOutcomeCannotTransferToEqualTupleForeignRoute()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 995, 9950);
        Assert.True(kernel.AllocateRegion(owner, 4096).IsSuccess);
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var routes = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        var original = routes[binding.BindingId]!;
        object? foreign = null;
        provider.BeforeDomainRevoke = () =>
        {
            foreign = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            routes[binding.BindingId] = foreign;
        };
        Assert.False(kernel.RevokePlatformDomain(owner, binding).IsSuccess);
        Assert.NotNull(foreign);
        Assert.Equal(true, original.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(original));
        Assert.NotNull(original.GetType().GetProperty("RootRevokeObservation")!.GetValue(original));
        Assert.Equal(false, foreign.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(foreign));
        Assert.Null(foreign.GetType().GetProperty("RootRevokeObservation")!.GetValue(foreign));
        Assert.Equal(true, foreign.GetType().GetProperty("ParentRevokeInFlight")!.GetValue(foreign));
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        routes[binding.BindingId] = original;
        provider.BeforeDomainRevoke = null;
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.Equal(1, provider.RevokeDomainCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void GuestUnmapCannotUseForeignCanonicalRootOrReleaseBacking(bool foreign)
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 994, 9940);
        var capability = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var domain = kernel.CreateVirtualDomain(owner, capability, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        var region = kernel.AllocateRegion(owner, 4096).Value!;
        var memory = kernel.MintCapability(process.DomainId, owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        Assert.True(kernel.MapGuestRegion(owner, domain.Domain, domain.MemoryCapability, memory, region.Handle,
            new(0, 4096), GuestMemoryAccess.Read | GuestMemoryAccess.Write).IsSuccess);
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var roots = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_activeSubjects", flags)!.GetValue(kernel.PlatformAuthority)!;
        var original = Assert.Single(roots.Values.Cast<object>());
        var root = (PlatformDomainBinding)original.GetType().GetProperty("Binding")!.GetValue(original)!;
        if (foreign) roots[root.Subject] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        else roots.Remove(root.Subject);
        Assert.False(kernel.DestroyVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(0, provider.GuestUnmapCalls);
        Assert.Equal(0, provider.RegionRevocationBeginCalls);
        Assert.Equal(0, provider.CloseCalls);
        Assert.Equal(0, provider.RevokeDomainCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MalformedChildCannotCompensateAfterCanonicalRootSourceLoss(bool foreign)
    {
        var provider = new ChildProvider { MalformedCreateParent = true };
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 993, 9930);
        Assert.True(kernel.AllocateRegion(owner, 4096).IsSuccess);
        var capability = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var originals = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_activeSubjects", flags)!.GetValue(kernel.PlatformAuthority)!;
        object? original = null;
        provider.BeforeIncarnationRead = () =>
        {
            var candidate = originals.Values.Cast<object>().FirstOrDefault(record =>
                (bool)record.GetType().GetProperty("ChildCreateMayHaveEffect")!.GetValue(record)!);
            if (candidate is null) return;
            original ??= candidate;
            var root = (PlatformDomainBinding)original.GetType().GetProperty("Binding")!.GetValue(original)!;
            if (foreign) originals[root.Subject] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            else originals.Remove(root.Subject);
        };
        Assert.False(kernel.CreateVirtualDomain(owner, capability, new(1, 4096)).IsSuccess);
        Assert.NotNull(original);
        var retained = Assert.Single(((global::System.Collections.IEnumerable)original.GetType()
            .GetProperty("UnpublishedChildCreates")!.GetValue(original)!).Cast<object>());
        Assert.Equal(provider.LastChildLease, retained.GetType().GetProperty("Receipt")!.GetValue(retained));
        Assert.Null(retained.GetType().GetProperty("ReturnedClosureReceipt")!.GetValue(retained));
        Assert.Equal(1, provider.ChildCreateCalls);
        Assert.Equal(0, provider.CloseCalls);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.Equal(0, provider.RevokeDomainCalls);
    }

    [Fact]
    public void ConsumedChildClosureDoesNotRenewRootAdmissionAfterEpochChange()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 992, 9920);
        Assert.True(kernel.AllocateRegion(owner, 4096).IsSuccess);
        var capability = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var domain = kernel.CreateVirtualDomain(owner, capability, new(1, 4096)).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var virtuals = (VirtualDomainAuthority)typeof(RuntimeKernel).GetField("_virtualDomains", flags)!.GetValue(kernel)!;
        var child = Assert.Single(virtuals.ForOwner(owner)).ChildBinding!.Value;
        Assert.True(kernel.DestroyVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        Assert.True(kernel.ObservePlatformBackendReset().IsSuccess);
        // This observes already committed original lifetime truth; it cannot reclaim by itself.
        Assert.True(kernel.PlatformAuthority.CloseChildDomain(child, allowCommittedClosure: true).IsSuccess);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.Equal(1, provider.CloseCalls);
        Assert.Equal(1, provider.RevokeDomainCalls);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void LostCanonicalRootRefusesChildEffectAndRetainsOriginalReceipt(bool foreign, bool afterEffect)
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 991, 9910);
        Assert.True(kernel.AllocateRegion(owner, 4096).IsSuccess);
        var capability = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var originals = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_activeSubjects", flags)!.GetValue(kernel.PlatformAuthority)!;
        object? original = null;
        void Lose()
        {
            original ??= Assert.Single(originals.Values.Cast<object>());
            var root = (PlatformDomainBinding)original.GetType().GetProperty("Binding")!.GetValue(original)!;
            if (foreign) originals[root.Subject] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            else originals.Remove(root.Subject);
        }
        if (afterEffect) provider.BeforeChildCreateReturn = Lose;
        else provider.BeforeIncarnationRead = () =>
        {
            var pending = originals.Values.Cast<object>().FirstOrDefault(candidate =>
                (int)candidate.GetType().GetProperty("PendingChildCreates")!.GetValue(candidate)! != 0);
            if (pending is not null) { original ??= pending; Lose(); }
        };
        Assert.False(kernel.CreateVirtualDomain(owner, capability, new(1, 4096)).IsSuccess);
        Assert.Equal(afterEffect ? 1 : 0, provider.ChildCreateCalls);
        Assert.Equal(0, provider.CloseCalls);
        Assert.NotNull(original);
        Assert.Equal(0, original.GetType().GetProperty("PendingChildCreates")!.GetValue(original));
        if (afterEffect)
        {
            var receipt = Assert.Single(((global::System.Collections.IEnumerable)original.GetType()
                .GetProperty("UnpublishedChildCreates")!.GetValue(original)!).Cast<object>());
            Assert.Equal(provider.LastChildLease, receipt.GetType().GetProperty("Receipt")!.GetValue(receipt));
            var invocation = Assert.Single(((global::System.Collections.IEnumerable)original.GetType()
                .GetProperty("ChildCreateInvocations")!.GetValue(original)!).Cast<object>());
            Assert.Same(original, invocation.GetType().GetProperty("Source")!.GetValue(invocation));
        }
        Assert.False(kernel.CreateVirtualDomain(owner, capability, new(1, 4096)).IsSuccess);
        Assert.Equal(afterEffect ? 1 : 0, provider.ChildCreateCalls);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.Equal(0, provider.RevokeDomainCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RootClosedProjectionRequiresActualProviderClosure(bool knownClosure)
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 990, 9900);
        var region = kernel.AllocateRegion(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        var original = domains[binding.BindingId]!;
        if (knownClosure) Assert.True(kernel.RevokePlatformDomain(owner, binding).IsSuccess);
        else
        {
            var state = original.GetType().GetProperty("AuthorityState")!;
            state.SetValue(original, Enum.Parse(state.PropertyType, "Closed"));
        }
        Assert.Equal(knownClosure, kernel.RevokePlatformDomain(owner, binding).IsSuccess);
        Assert.Equal(knownClosure, kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(knownClosure ? RegionState.Released : RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.Equal(knownClosure ? 1 : 0, provider.RevokeDomainCalls);
        if (!knownClosure)
        {
            ((global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_processPlatformBindings", flags)!.GetValue(kernel)!).Remove(owner);
            domains.Remove(binding.BindingId);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FalseClosedRootCannotBypassOriginalRevokeInterlockOrUnknownOutcome(bool lostReply)
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 989, 9890);
        var region = kernel.AllocateRegion(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        var original = domains[binding.BindingId]!;
        var state = original.GetType().GetProperty("AuthorityState")!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        provider.BeforeDomainRevoke = () =>
        {
            state.SetValue(original, Enum.Parse(state.PropertyType, "Closed"));
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
            if (lostReply) throw new IOException("controlled original root revoke reply loss");
        };
        var first = Task.Run(() => kernel.RevokePlatformDomain(owner, binding));
        KernelResult overlap;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            overlap = kernel.RevokePlatformDomain(owner, binding);
            Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        }
        finally { release.Set(); }
        var completed = await first.WaitAsync(TimeSpan.FromSeconds(15));
        provider.BeforeDomainRevoke = null;
        Assert.False(overlap.IsSuccess);
        Assert.Equal(!lostReply, completed.IsSuccess);
        if (lostReply)
        {
            Assert.False(kernel.RevokePlatformDomain(owner, binding).IsSuccess);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
            Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        }
        else
        {
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
        }
        Assert.Equal(1, provider.RevokeDomainCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void InitialTeardownCapturesOriginalCleanupOwnerInsteadOfForeignKernelRoute(bool otherProcess)
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 107, 1070);
        var region = kernel.AllocateRegion(owner, 4096).Value!;
        var original = kernel.BindPlatformAuthorityDomain(owner).Value!;
        var (_, foreignOwner) = TestFixtures.Create(kernel, 108, 1080);
        var foreignBinding = kernel.BindPlatformAuthorityDomain(foreignOwner).Value!;
        var replacement = otherProcess ? foreignBinding : original with { Generation = new(original.Generation.Value + 1) };
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var routes = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_processPlatformBindings", flags)!.GetValue(kernel)!;
        routes[owner] = replacement;
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        var teardowns = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_processTeardowns", flags)!.GetValue(kernel)!;
        Assert.Equal(original, teardowns[owner]!.GetType().GetProperty("DomainBinding")!.GetValue(teardowns[owner]));
        Assert.False(kernel.QueryProcessTeardown(owner).Value!.PlatformDomainClosed);
        Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(0, provider.RevokeDomainCalls);
        Assert.Equal(replacement, routes[owner]);
        Assert.True(kernel.PlatformAuthority.HasActiveAuthority(original.Subject));
        Assert.True(kernel.PlatformAuthority.HasActiveAuthority(foreignBinding.Subject));
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(item => item.Handle == region.Handle).State);
        routes[owner] = original; // controlled repair of correlation, not durable recovery
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.RevokeDomainCalls);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(item => item.Handle == region.Handle).State);
        Assert.True(kernel.Processes.Resolve(foreignOwner).IsSuccess);
        Assert.True(kernel.TerminateProcess(foreignOwner).IsSuccess);
        Assert.Equal(2, provider.RevokeDomainCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExactDetachCannotDischargeForeignKernelRouteBeforeOrAfterClosure(bool late)
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 106, 1060);
        var binding = kernel.BindPlatformAuthorityDomain(owner).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var routes = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_processPlatformBindings", flags)!.GetValue(kernel)!;
        var replacement = binding with { Generation = new(binding.Generation.Value + 1) };
        if (late) provider.BeforeDomainRevoke = () => routes[owner] = replacement;
        else routes[owner] = replacement;
        Assert.False(kernel.RevokePlatformDomain(owner, binding).IsSuccess);
        Assert.False(kernel.RevokePlatformDomain(owner, binding).IsSuccess);
        Assert.Equal(late ? 1 : 0, provider.RevokeDomainCalls);
        Assert.Equal(replacement, routes[owner]);
        var pending = (HashSet<ProcessHandle>)typeof(RuntimeKernel).GetField("_executionTransactions", flags)!.GetValue(kernel)!;
        Assert.DoesNotContain(owner, pending);
        provider.BeforeDomainRevoke = null;
        routes[owner] = binding; // controlled correlation repair, not durable recovery
        Assert.True(kernel.RevokePlatformDomain(owner, binding).IsSuccess);
        Assert.Equal(1, provider.RevokeDomainCalls);
        Assert.False(routes.Contains(owner));
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.RevokeDomainCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void AcceptedRootClosureSurvivesLateKernelRoutingLossWithoutForeignReclaim(int fault)
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 105, 1050);
        var region = kernel.AllocateRegion(owner, 4096).Value!;
        var binding = kernel.BindPlatformAuthorityDomain(owner).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var routes = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_processPlatformBindings", flags)!.GetValue(kernel)!;
        var teardowns = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_processTeardowns", flags)!.GetValue(kernel)!;
        var replacement = binding with { Generation = new(binding.Generation.Value + 1) };
        provider.BeforeDomainRevoke = () =>
        {
            if (fault == 0) routes.Remove(owner);
            if (fault == 1) routes[owner] = replacement;
            if (fault == 2) teardowns[owner]!.GetType().GetProperty("DomainBinding")!.SetValue(teardowns[owner], replacement);
        };
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        var deferred = kernel.QueryProcessTeardown(owner).Value!;
        Assert.Equal(fault != 2, deferred.PlatformDomainClosed);
        Assert.False(deferred.LocalReclaimCompleted);
        Assert.Equal(1, provider.RevokeDomainCalls);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(item => item.Handle == region.Handle).State);
        provider.BeforeDomainRevoke = null;
        if (fault != 0)
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(fault == 1 ? replacement : binding, routes[owner]);
            Assert.Equal(fault != 2, kernel.QueryProcessTeardown(owner).Value!.PlatformDomainClosed);
            Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(item => item.Handle == region.Handle).State);
            // Model fault repair of the exact correlation is not durable recovery.
            if (fault == 1) routes[owner] = binding;
            if (fault == 2) teardowns[owner]!.GetType().GetProperty("DomainBinding")!.SetValue(teardowns[owner], binding);
        }
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.RevokeDomainCalls);
        Assert.False(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(item => item.Handle == region.Handle).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AuthorityOnlyStartRechecksLocalRoutingAndStateAfterSourceCallback(bool reenter)
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 104, 1040);
        Assert.True(kernel.BindPlatformAuthorityDomain(owner).IsSuccess);
        Assert.True(kernel.AdmitProcess(owner).IsSuccess);
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var callbackRan = false;
        var outsideLocks = false;
        KernelResult? nested = null;
        provider.BeforeIncarnationRead = () =>
        {
            provider.BeforeIncarnationRead = null;
            callbackRan = true;
            outsideLocks = !Monitor.IsEntered(typeof(RuntimeKernel).GetField("_platformMemoryUseGate", flags)!.GetValue(kernel)!) &&
                !Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!);
            if (reenter) nested = kernel.StartProcess(owner);
            else ((global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_processPlatformBindings", flags)!.GetValue(kernel)!).Remove(owner);
        };
        var outer = kernel.StartProcess(owner);
        Assert.True(callbackRan);
        Assert.True(outsideLocks);
        Assert.False(outer.IsSuccess);
        Assert.Equal(reenter ? ProcessState.Running : ProcessState.Admitted, process.State);
        if (reenter) Assert.True(nested!.Value.IsSuccess);
        Assert.Equal(0, provider.TransitionCalls);
        Assert.Equal(0, provider.RevokeDomainCalls);
        if (!reenter)
        {
            // The original owner still exists: teardown recovers its cleanup route.
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1, provider.RevokeDomainCalls);
            Assert.False(kernel.Processes.Resolve(owner).IsSuccess);
        }
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 2)]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)]
    public void AuthorityOnlyProcessTransitionRefusesChangedOrUnavailableRootSource(int transition, int fault)
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 103, 1030);
        var binding = kernel.BindPlatformAuthorityDomain(owner).Value!;
        Assert.True(kernel.AdmitProcess(owner).IsSuccess);
        var region = kernel.AllocateRegion(owner, 4096).Value!;
        if (transition != 0) Assert.True(kernel.StartProcess(owner).IsSuccess);
        if (transition == 2) Assert.True(kernel.ParkProcess(owner).IsSuccess);
        var before = process.State;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var reads = 0;
        provider.BeforeIncarnationRead = () =>
        {
            reads++;
            Assert.False(Monitor.IsEntered(typeof(RuntimeKernel).GetField("_platformMemoryUseGate", flags)!.GetValue(kernel)!));
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!));
            if (fault == 2) throw new IOException("Transition root source unavailable.");
        };
        if (fault != 2) provider.CurrentIncarnation = new(fault == 0 ? 2UL : 0UL);
        KernelResult Attempt() => transition switch
        {
            0 => kernel.StartProcess(owner),
            1 => kernel.ParkProcess(owner),
            _ => kernel.ResumeProcess(owner)
        };
        Assert.Equal(KernelError.PlatformFaulted, Attempt().Error);
        Assert.True(reads > 0);
        Assert.Equal(before, process.State);
        Assert.False(Attempt().IsSuccess);
        Assert.Equal(before, process.State);
        Assert.True(kernel.PlatformAuthority.HasActiveAuthority(binding.Subject));
        Assert.Equal(0, provider.TransitionCalls);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(0, provider.RevokeDomainCalls);
        Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(item => item.Handle == region.Handle).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FinalRootPublicationRefusalRetainsMintedCleanupButCannotConfigure(bool unavailable)
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 102, 1020);
        var source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var region = kernel.AllocateRegion(owner, 4096).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var domains = (VirtualDomainAuthority)typeof(RuntimeKernel).GetField("_virtualDomains", flags)!.GetValue(kernel)!;
        var reads = 0;
        kernel.BeforeVirtualAuthorityPublicationQualificationHook = () => provider.BeforeIncarnationRead = () =>
        {
            if (++reads != 2) return;
            Assert.False(Monitor.IsEntered(typeof(RuntimeKernel).GetField("_platformMemoryUseGate", flags)!.GetValue(kernel)!));
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!));
            var staged = Assert.Single(domains.ForOwner(owner));
            Assert.Equal(5, staged.Capabilities.Length);
            Assert.False(staged.AuthorityPublished);
            Assert.False(kernel.ConfigureVirtualDomain(owner, staged.Handle, staged.Capabilities[0]).IsSuccess);
            Assert.Equal(0, provider.TransitionCalls);
            if (unavailable) throw new IOException("Final publication source unavailable.");
            provider.CurrentIncarnation = new(2);
        };
        var created = kernel.CreateVirtualDomain(owner, source, new(1, 4096));
        kernel.BeforeVirtualAuthorityPublicationQualificationHook = null;
        provider.BeforeIncarnationRead = null;
        Assert.False(created.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, created.Error);
        var retained = Assert.Single(domains.ForOwner(owner));
        Assert.False(retained.AuthorityPublished);
        Assert.Equal(VirtualDomainState.Quarantined, retained.State);
        Assert.Equal(5, retained.Capabilities.Length);
        Assert.All(retained.Capabilities, capability => Assert.Contains(capability, process.Capabilities.Items));
        Assert.False(kernel.ConfigureVirtualDomain(owner, retained.Handle, retained.Capabilities[0]).IsSuccess);
        Assert.Equal(0, provider.TransitionCalls);
        // Keep the original provider source unavailable: no closure or reclaim can be inferred.
        provider.CurrentIncarnation = new(2);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(0, provider.RevokeDomainCalls);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(item => item.Handle == region.Handle).State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task ConcurrentRootSourceLossBeforeVirtualPublicationRetainsCleanupWithoutDerivedPermission(int fault)
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 101, 1010);
        var source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var region = kernel.AllocateRegion(owner, 4096).Value!;
        var capabilities = process.Capabilities.Items.ToArray();
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var kernelGate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate", flags)!.GetValue(kernel)!;
        var bridgeGate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        kernel.BeforeVirtualAuthorityPublicationQualificationHook = () =>
        {
            Assert.False(Monitor.IsEntered(kernelGate));
            Assert.False(Monitor.IsEntered(bridgeGate));
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        var pending = Task.Run(() => kernel.CreateVirtualDomain(owner, source, new(1, 4096)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            lock (bridgeGate)
            {
                var routes = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
                var root = Assert.Single(routes.Values.Cast<object>());
                var binding = (PlatformDomainBinding)root.GetType().GetProperty("Binding")!.GetValue(root)!;
                if (fault == 0) provider.CurrentIncarnation = new(2);
                if (fault == 1) ((global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_activeSubjects", flags)!.GetValue(kernel.PlatformAuthority)!).Remove(binding.Subject);
                if (fault == 2) routes[binding.BindingId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(root, null)!;
            }
        }
        finally { release.Set(); }
        var created = await pending;
        kernel.BeforeVirtualAuthorityPublicationQualificationHook = null;
        Assert.False(created.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, created.Error);
        Assert.Equal(capabilities, process.Capabilities.Items.ToArray());
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(0, provider.RevokeDomainCalls);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(item => item.Handle == region.Handle).State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ActualRootAttachmentRefusesChangedOrUnavailableProviderSourceAfterCallback(int fault)
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, process) = TestFixtures.Create(kernel, 100, 1000);
        var region = kernel.AllocateRegion(process, 4096).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var kernelGate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate", flags)!.GetValue(kernel)!;
        var bridgeGate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!;
        var reads = 0;
        kernel.BeforePlatformBindingAttachmentQualificationHook = () =>
        {
            Assert.False(Monitor.IsEntered(kernelGate));
            Assert.False(Monitor.IsEntered(bridgeGate));
            if (fault == 0) provider.CurrentIncarnation = new(2);
            if (fault == 1) provider.CurrentIncarnation = new(0);
            provider.BeforeIncarnationRead = () =>
            {
                reads++;
                Assert.False(Monitor.IsEntered(kernelGate));
                Assert.False(Monitor.IsEntered(bridgeGate));
                if (fault == 2) throw new IOException("Final attachment source unavailable.");
            };
        };
        var bound = kernel.BindPlatformAuthorityDomain(process);
        Assert.False(bound.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, bound.Error);
        Assert.True(reads > 0);
        kernel.BeforePlatformBindingAttachmentQualificationHook = null;
        Assert.True(kernel.AdmitProcess(process).IsSuccess);
        Assert.False(kernel.StartProcess(process).IsSuccess);
        Assert.False(kernel.TerminateProcess(process).IsSuccess);
        Assert.Equal(0, provider.RevokeDomainCalls);
        Assert.True(kernel.Processes.Resolve(process).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(process).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(item => item.Handle == region.Handle).State);
        Assert.False(kernel.PlatformAuthority.HasUnresolvedDomainBindEffect(new(new DomainId(1000), process)));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ActualFallbackStartRequiresCanonicalOriginalRootOwnerAndKernelRoute(int fault)
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (owner, process) = TestFixtures.Create(kernel, 99, 990);
        var binding = kernel.BindPlatformAuthorityDomain(process).Value!;
        Assert.True(kernel.AdmitProcess(process).IsSuccess);
        var before = owner.State;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        if (fault == 0)
            ((global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_activeSubjects", flags)!.GetValue(kernel.PlatformAuthority)!).Remove(binding.Subject);
        if (fault == 1)
        {
            var routes = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
            routes[binding.BindingId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(routes[binding.BindingId], null)!;
        }
        if (fault == 2)
            ((global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_processPlatformBindings", flags)!.GetValue(kernel)!).Remove(process);
        var started = kernel.StartProcess(process);
        Assert.False(started.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, started.Error);
        Assert.Equal(before, owner.State);
        Assert.False(kernel.StartProcess(process).IsSuccess);
        Assert.True(kernel.Processes.Resolve(process).IsSuccess);
        Assert.Equal(0, provider.RevokeDomainCalls);
        Assert.True(kernel.PlatformAuthority.HasActiveAuthority(binding.Subject));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void RecoveredKernelRootCleanupRouteRetainsFailureOrLostResponseAndNeverRepeatsEffect(int outcome)
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, process) = TestFixtures.Create(kernel, 98, 980);
        var region = kernel.AllocateRegion(process, 4096).Value!;
        var binding = kernel.BindPlatformAuthorityDomain(process).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        ((global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_processPlatformBindings", flags)!.GetValue(kernel)!).Remove(process);
        if (outcome == 3) provider.BeforeDomainRevoke = () => throw new IOException("Original root closure response lost.");
        else provider.RootRevokeStatus = outcome switch
        {
            0 => PlatformAuthorityStatus.Denied,
            1 => PlatformAuthorityStatus.Faulted,
            _ => PlatformAuthorityStatus.NotAccepted,
        };
        Assert.False(kernel.TerminateProcess(process).IsSuccess);
        provider.BeforeDomainRevoke = null;
        provider.RootRevokeStatus = null;
        Assert.False(kernel.TerminateProcess(process).IsSuccess);
        Assert.Equal(1, provider.RevokeDomainCalls);
        Assert.True(kernel.PlatformAuthority.HasActiveAuthority(binding.Subject));
        Assert.True(kernel.Processes.Resolve(process).IsSuccess);
        var teardown = kernel.QueryProcessTeardown(process).Value!;
        Assert.False(teardown.PlatformDomainClosed);
        Assert.False(teardown.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(item => item.Handle == region.Handle).State);
    }

    [Fact]
    public void ForeignRootPublicationRouteCannotReplaceOriginalKernelCleanupBinding()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, foreignProcess) = TestFixtures.Create(kernel, 96, 960);
        var foreignBinding = kernel.BindPlatformAuthorityDomain(foreignProcess).Value!;
        var (_, process) = TestFixtures.Create(kernel, 97, 970);
        var region = kernel.AllocateRegion(process, 4096).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var routes = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        var gate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!;
        PlatformDomainBinding originalBinding = default;
        kernel.BeforePlatformBindingAttachmentQualificationHook = () =>
        {
            Assert.False(Monitor.IsEntered(gate));
            lock (gate)
            {
                var original = routes.Values.Cast<object>().Single(item =>
                    ((PlatformDomainBinding)item.GetType().GetProperty("Binding")!.GetValue(item)!).Subject.ProcessId == process.ProcessId);
                originalBinding = (PlatformDomainBinding)original.GetType().GetProperty("Binding")!.GetValue(original)!;
                var foreign = routes[foreignBinding.BindingId]!;
                var state = foreign.GetType().GetProperty("AuthorityState")!;
                state.SetValue(foreign, Enum.Parse(state.PropertyType, "Quarantined"));
                routes[originalBinding.BindingId] = foreign;
            }
        };
        Assert.False(kernel.BindPlatformAuthorityDomain(process).IsSuccess);
        kernel.BeforePlatformBindingAttachmentQualificationHook = null;
        var kernelRoutes = (global::System.Collections.IDictionary)typeof(RuntimeKernel)
            .GetField("_processPlatformBindings", flags)!.GetValue(kernel)!;
        Assert.Equal(originalBinding, (PlatformDomainBinding)kernelRoutes[process]!);
        Assert.True(kernel.PlatformAuthority.TryGetQuarantinedDomainBinding(originalBinding.Subject, out var retained));
        Assert.Equal(originalBinding, retained);
        Assert.False(kernel.TerminateProcess(process).IsSuccess);
        Assert.Equal(0, provider.RevokeDomainCalls);
        Assert.True(kernel.Processes.Resolve(process).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(process).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(item => item.Handle == region.Handle).State);
        Assert.False(kernel.PlatformAuthority.HasUnresolvedDomainBindEffect(originalBinding.Subject));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void OriginalRootCleanupRoutingControlsActualProcessReclaim(int routeLoss)
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, process) = TestFixtures.Create(kernel, 95, 950);
        var region = kernel.AllocateRegion(process, 4096).Value!;
        var binding = kernel.BindPlatformAuthorityDomain(process).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var kernelRoutes = (global::System.Collections.IDictionary)typeof(RuntimeKernel)
            .GetField("_processPlatformBindings", flags)!.GetValue(kernel)!;
        if (routeLoss == 3) kernelRoutes[process] = binding with { Generation = new PlatformDomainBindingGeneration(binding.Generation.Value + 1) };
        else kernelRoutes.Remove(process);
        if (routeLoss == 1)
            ((global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
                .GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!).Remove(binding.BindingId);
        if (routeLoss == 2)
            ((global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
                .GetField("_activeSubjects", flags)!.GetValue(kernel.PlatformAuthority)!).Remove(binding.Subject);

        if (routeLoss == 0)
        {
            // Recover only the teardown consumer route. Exact provider closure still precedes reclaim.
            var terminated = kernel.TerminateProcess(process);
            Assert.True(terminated.IsSuccess, terminated.Message);
            Assert.Equal(1, provider.RevokeDomainCalls);
            Assert.False(kernel.TerminateProcess(process).IsSuccess);
            Assert.Equal(1, provider.RevokeDomainCalls);
            Assert.False(kernel.Processes.Resolve(process).IsSuccess);
            Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(item => item.Handle == region.Handle).State);
            return;
        }
        Assert.False(kernel.TerminateProcess(process).IsSuccess);
        Assert.False(kernel.TerminateProcess(process).IsSuccess);
        Assert.Equal(0, provider.RevokeDomainCalls);
        Assert.True(kernel.PlatformAuthority.HasActiveAuthority(binding.Subject));
        Assert.True(kernel.Processes.Resolve(process).IsSuccess);
        var teardown = kernel.QueryProcessTeardown(process).Value!;
        Assert.False(teardown.PlatformDomainClosed);
        Assert.False(teardown.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(item => item.Handle == region.Handle).State);
        Assert.False(kernel.StartProcess(process).IsSuccess);
    }

    [Theory]
    [InlineData(false, true, 0)] [InlineData(true, true, 0)]
    [InlineData(false, false, 0)] [InlineData(true, false, 0)]
    [InlineData(false, false, 1)] [InlineData(true, false, 1)]
    [InlineData(false, false, 2)] [InlineData(true, false, 2)]
    public void OriginalRootSubjectOwnerSurvivesLostDomainRouting(bool foreign, bool beforeRevoke, int outcome)
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, process) = TestFixtures.Create(kernel, 94, 940);
        var region = kernel.AllocateRegion(process, 4096).Value!;
        var binding = kernel.BindPlatformAuthorityDomain(process).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var routes = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        var subjects = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_activeSubjects", flags)!.GetValue(kernel.PlatformAuthority)!;
        var gate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!;
        var original = routes[binding.BindingId]!;
        void Lose()
        {
            Assert.False(Monitor.IsEntered(gate));
            lock (gate)
            {
                if (foreign) routes[binding.BindingId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                else routes.Remove(binding.BindingId);
            }
        }
        if (beforeRevoke) Lose();
        else provider.BeforeDomainRevoke = () =>
        {
            Lose();
            if (outcome == 1) provider.RootRevokeStatus = PlatformAuthorityStatus.Denied;
            if (outcome == 2) throw new IOException("Root response lost after domain route loss.");
        };
        Assert.False(kernel.TerminateProcess(process).IsSuccess);
        Assert.Same(original, subjects[binding.Subject]);
        Assert.Equal(!beforeRevoke && outcome == 0, original.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(original));
        Assert.Equal(!beforeRevoke, original.GetType().GetProperty("ParentRevokeMayHaveEffect")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("ParentRevokeInFlight")!.GetValue(original));
        Assert.True(kernel.PlatformAuthority.HasActiveAuthority(binding.Subject));
        if (!beforeRevoke)
        {
            Assert.True(kernel.PlatformAuthority.TryGetQuarantinedDomainBinding(binding.Subject, out var retained));
            Assert.Equal(binding, retained);
            var raw = original.GetType().GetProperty("RootRevokeObservation")!.GetValue(original);
            if (outcome == 2) Assert.Null(raw);
            else Assert.Equal(outcome == 0 ? PlatformAuthorityStatus.Success : PlatformAuthorityStatus.Denied,
                ((PlatformAuthorityResult)raw!).Status);
        }
        provider.BeforeDomainRevoke = null;
        provider.RootRevokeStatus = null;
        Assert.False(kernel.TerminateProcess(process).IsSuccess);
        Assert.Equal(beforeRevoke ? 0 : 1, provider.RevokeDomainCalls);
        Assert.True(kernel.Processes.Resolve(process).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(process).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(item => item.Handle == region.Handle).State);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    public async Task ConcurrentResetAroundRootRevokePreservesIntentAndPinsActualReclaim(bool beforeEffect, int outcome)
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 93, 930);
        _ = kernel.AllocateRegion(owner, 4096).Value!;
        Assert.True(kernel.BindPlatformAuthorityDomain(owner).IsSuccess);
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var gate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!;
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        var original = Assert.Single(records.Values.Cast<object>());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        void WaitForReset()
        {
            Assert.False(Monitor.IsEntered(gate));
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
        }
        if (beforeEffect)
            provider.BeforeIncarnationRead = () => { provider.BeforeIncarnationRead = null; WaitForReset(); };
        else
            provider.BeforeDomainRevoke = () =>
            {
                WaitForReset();
                if (outcome == 1) provider.RootRevokeStatus = PlatformAuthorityStatus.Denied;
                if (outcome == 2) throw new IOException("Original root response lost after reset");
            };
        var work = Task.Run(() => kernel.TerminateProcess(owner));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            Assert.True(kernel.ObservePlatformBackendReset().IsSuccess);
            var replacement = Assert.Single(records.Values.Cast<object>());
            Assert.NotSame(original, replacement);
            Assert.Equal(!beforeEffect, replacement.GetType().GetProperty("ParentRevokeMayHaveEffect")!.GetValue(replacement));
            Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        }
        finally { release.Set(); }
        Assert.False((await work.WaitAsync(TimeSpan.FromSeconds(15))).IsSuccess);
        provider.BeforeDomainRevoke = null;
        provider.BeforeIncarnationRead = null;
        provider.RootRevokeStatus = null;
        var retained = Assert.Single(records.Values.Cast<object>());
        Assert.Equal(false, original.GetType().GetProperty("ParentRevokeInFlight")!.GetValue(original));
        Assert.Equal(!beforeEffect && outcome == 0, original.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(original));
        Assert.Equal(true, retained.GetType().GetProperty("ParentRevokeInFlight")!.GetValue(retained));
        Assert.Equal(false, retained.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(retained));
        Assert.Null(retained.GetType().GetProperty("RootRevokeObservation")!.GetValue(retained));
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(beforeEffect ? 0 : 1, provider.RevokeDomainCalls);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(PlatformAuthorityStatus.Denied)]
    [InlineData(PlatformAuthorityStatus.Stale)]
    [InlineData(PlatformAuthorityStatus.NotAccepted)]
    public void FailedOriginalRootRevokeRetainsResponseAndPinsActualTeardown(PlatformAuthorityStatus status)
    {
        var provider = new ChildProvider { RootRevokeStatus = status };
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 92, 920);
        _ = kernel.AllocateRegion(owner, 4096).Value!;
        Assert.True(kernel.BindPlatformAuthorityDomain(owner).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_domains", global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!
            .GetValue(kernel.PlatformAuthority)!;
        var record = Assert.Single(records.Values.Cast<object>());
        var raw = record.GetType().GetProperty("RootRevokeObservation")!.GetValue(record)!;
        Assert.Equal(status, raw.GetType().GetProperty("Status")!.GetValue(raw));
        Assert.Equal(true, record.GetType().GetProperty("ParentRevokeMayHaveEffect")!.GetValue(record));
        Assert.Equal(false, record.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(record));
        Assert.Equal(false, record.GetType().GetProperty("ParentRevokeInFlight")!.GetValue(record));
        provider.RootRevokeStatus = null;
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.RevokeDomainCalls);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void RefusedGuestUnmapSourceRecoveryNeverRepeatsEffectOrLicensesReclaim(int fault)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(984, 9840);
        var root = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(root, owner, Intent()).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(owner.DomainId, owner.ProcessGeneration), 4096);
        var parent = bridge.MapOwnedRegion(root, owner, new CapabilityId(8), region, PlatformMemoryAccess.Read).Value!;
        var guest = bridge.MapChildGuestRegion(child, parent, new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;
        var records = (global::System.Collections.IDictionary)IoField(bridge, "_guestBindings");
        var record = records[guest.MappingId]!;
        object? Fact(string name) => record.GetType().GetProperty(name)!.GetValue(record);
        var gate = IoField(bridge, "_secureDomainLifecycleGate");
        provider.GuestUnmapStatus = PlatformAuthorityStatus.NotAccepted;
        provider.BeforeGuestUnmap = () => provider.BeforeIncarnationRead = () => throw new IOException("Late source loss");
        Assert.False(bridge.UnmapChildGuestRegion(guest).IsSuccess);
        var refusal = Fact("LastUnmapRefusal");
        Assert.NotNull(refusal);
        Assert.Null(Fact("LastClosureReceipt"));
        Assert.Equal(PlatformExternalClosureState.Draining, Fact("Closure"));
        Assert.False(bridge.UnmapChildGuestRegion(guest).IsSuccess);
        Assert.Same(refusal, Fact("LastUnmapRefusal"));
        Assert.Equal(1, provider.GuestUnmapCalls);
        provider.BeforeGuestUnmap = null;
        provider.GuestUnmapStatus = null;
        provider.BeforeIncarnationRead = () =>
        {
            Assert.False(Monitor.IsEntered(gate));
            provider.BeforeIncarnationRead = null;
            if (fault == 1) throw new IOException("Recovery source loss");
            if (fault == 2) provider.CurrentIncarnation = new(2);
            if (fault == 3) Assert.True(bridge.ObserveBackendReset().IsSuccess);
            if (fault == 4)
            {
                Assert.False(bridge.UnmapChildGuestRegion(guest).IsSuccess);
                Assert.False(bridge.CloseChildDomain(child).IsSuccess);
                Assert.False(bridge.RevokeDomain(root, owner).IsSuccess);
            }
        };
        Assert.False(bridge.UnmapChildGuestRegion(guest, allowCommittedClosure: true).IsSuccess);
        Assert.Equal(1, provider.GuestUnmapCalls);
        Assert.Null(Fact("LastClosureReceipt"));
        Assert.Equal(false, Fact("ClosureInFlight"));
        Assert.False(bridge.RevokeDomain(root, owner).IsSuccess);
        if (fault is 0 or 4)
        {
            Assert.Null(Fact("LastUnmapRefusal"));
            Assert.Equal(PlatformExternalClosureState.Active, Fact("Closure"));
            Assert.True(bridge.CommitSecureGuestMapping(guest, parent, true, KernelResult.Ok, out var endPending, out _).IsSuccess);
            endPending!();
            Assert.True(bridge.UnmapChildGuestRegion(guest).IsSuccess);
            Assert.NotNull(Fact("LastClosureReceipt"));
            Assert.Equal(2, provider.GuestUnmapCalls);
            Assert.True(bridge.UnmapChildGuestRegion(guest, allowCommittedClosure: true).IsSuccess);
            Assert.Equal(2, provider.GuestUnmapCalls);
        }
        else
        {
            Assert.Same(refusal, Fact("LastUnmapRefusal"));
            Assert.Equal(fault == 1 ? PlatformExternalClosureState.Draining : PlatformExternalClosureState.Faulted, Fact("Closure"));
            Assert.False(bridge.CommitSecureGuestMapping(guest, parent, true, KernelResult.Ok, out _, out _).IsSuccess);
        }
    }

    [Theory]
    [InlineData(PlatformAuthorityStatus.Denied)] [InlineData(PlatformAuthorityStatus.Unsupported)]
    [InlineData((PlatformAuthorityStatus)777)] [InlineData(PlatformAuthorityStatus.Faulted)]
    public void UnknownGuestUnmapNeverAcquiresRefusalRecoveryFact(PlatformAuthorityStatus status)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(985, 9850);
        var root = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(root, owner, Intent()).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(owner.DomainId, owner.ProcessGeneration), 4096);
        var parent = bridge.MapOwnedRegion(root, owner, new CapabilityId(8), region, PlatformMemoryAccess.Read).Value!;
        var guest = bridge.MapChildGuestRegion(child, parent, new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;
        provider.GuestUnmapStatus = status;
        if (status == PlatformAuthorityStatus.Faulted) provider.BeforeGuestUnmap = () => throw new IOException("Unknown original outcome");
        Assert.False(bridge.UnmapChildGuestRegion(guest).IsSuccess);
        var records = (global::System.Collections.IDictionary)IoField(bridge, "_guestBindings");
        var record = records[guest.MappingId]!;
        Assert.Null(record.GetType().GetProperty("LastUnmapRefusal")!.GetValue(record));
        provider.BeforeGuestUnmap = null; provider.GuestUnmapStatus = null;
        Assert.False(bridge.UnmapChildGuestRegion(guest).IsSuccess);
        Assert.Equal(1, provider.GuestUnmapCalls);
        Assert.False(bridge.CommitSecureGuestMapping(guest, parent, true, KernelResult.Ok, out _, out _).IsSuccess);
        Assert.False(bridge.RevokeDomain(root, owner).IsSuccess);
    }

    [Fact]
    public void GuestUnmapFinalAdmissionSeesSecureConsumerAdmittedBySourceQuery()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(983, 9830);
        var root = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(root, owner, Intent()).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(owner.DomainId, owner.ProcessGeneration), 4096);
        var parent = bridge.MapOwnedRegion(root, owner, new CapabilityId(8), region, PlatformMemoryAccess.Read).Value!;
        var guest = bridge.MapChildGuestRegion(child, parent, new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;
        Action? endPending = null;
        provider.BeforeIncarnationRead = () =>
        {
            provider.BeforeIncarnationRead = null;
            Assert.True(bridge.CommitSecureGuestMapping(guest, parent, true, KernelResult.Ok, out endPending, out _).IsSuccess);
        };
        Assert.Equal(KernelError.PlatformBindingActive, bridge.UnmapChildGuestRegion(guest).Error);
        Assert.Equal(0, provider.GuestUnmapCalls);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.UnmapChildGuestRegion(guest).Error);
        Assert.False(bridge.RevokeDomain(root, owner).IsSuccess);
        endPending!();
        endPending(); // Completing the same captured interlock twice cannot debit another attempt.
        Assert.True(bridge.UnmapChildGuestRegion(guest).IsSuccess);
        Assert.Equal(1, provider.GuestUnmapCalls);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)] [InlineData(false, 3)] [InlineData(false, 4)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)] [InlineData(true, 3)] [InlineData(true, 4)]
    public void RefusedTransitionOrUnmapRechecksLiveSourceOutsideLockBeforeRetainingState(bool unmap, int fault)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(981, 9810);
        var root = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(root, owner, Intent()).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(owner.DomainId, owner.ProcessGeneration), 4096);
        var parent = bridge.MapOwnedRegion(root, owner, new CapabilityId(8), region, PlatformMemoryAccess.Read).Value!;
        var guest = bridge.MapChildGuestRegion(child, parent, new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;
        if (!unmap) Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.Start).IsSuccess);
        var records = (global::System.Collections.IDictionary)IoField(bridge, unmap ? "_guestBindings" : "_childBindings");
        var record = records[unmap ? (object)guest.MappingId : child.BindingId]!;
        object? State() => record.GetType().GetProperty(unmap ? "Closure" : "State")!.GetValue(record);
        var original = State();
        var gate = IoField(bridge, "_secureDomainLifecycleGate");
        int reads = 0, before = unmap ? provider.GuestUnmapCalls : provider.TransitionCalls;
        bool publication = false;
        KernelResult Run() => unmap ? bridge.UnmapChildGuestRegion(guest) :
            bridge.TransitionChildDomain(child, PlatformChildDomainTransition.Park,
                completionConsumer: commit => { publication = true; return commit(KernelResult.Ok); });
        void InstallSourceHook() => provider.BeforeIncarnationRead = () =>
        {
            Assert.False(Monitor.IsEntered(gate));
            reads++;
            provider.BeforeIncarnationRead = null;
            if (fault == 1) throw new IOException("Refused invocation source unavailable");
            if (fault == 2) provider.CurrentIncarnation = new(2);
            if (fault == 3) Assert.True(bridge.ObserveBackendReset().IsSuccess);
            if (fault == 4)
            {
                Assert.False(Run().IsSuccess);
                Assert.False(bridge.CloseChildDomain(child).IsSuccess);
                Assert.False(bridge.RevokeDomain(root, owner).IsSuccess);
            }
        };
        if (unmap)
        {
            provider.GuestUnmapStatus = PlatformAuthorityStatus.NotAccepted;
            provider.BeforeGuestUnmap = InstallSourceHook;
        }
        else
        {
            provider.ChildTransitionStatus = PlatformAuthorityStatus.NotAccepted;
            provider.BeforeChildTransition = InstallSourceHook;
        }
        Assert.False(Run().IsSuccess);
        Assert.False(publication);
        Assert.Equal(1, reads);
        Assert.Equal(before + 1, unmap ? provider.GuestUnmapCalls : provider.TransitionCalls);
        Assert.Same(record, records[unmap ? (object)guest.MappingId : child.BindingId]);
        Assert.Equal(false, record.GetType().GetProperty(unmap ? "ClosureInFlight" : "TransitionInFlight")!.GetValue(record));
        if (unmap) Assert.Null(record.GetType().GetProperty("LastClosureReceipt")!.GetValue(record));
        Assert.False(bridge.RevokeDomain(root, owner).IsSuccess);
        if (fault is 0 or 4)
        {
            Assert.Equal(original, State());
            provider.GuestUnmapStatus = null; provider.ChildTransitionStatus = null;
            provider.BeforeGuestUnmap = null; provider.BeforeChildTransition = null;
            Assert.True(Run().IsSuccess);
            Assert.Equal(before + 2, unmap ? provider.GuestUnmapCalls : provider.TransitionCalls);
            Assert.Equal(!unmap, publication);
        }
        else
        {
            // Unavailable source leaves a staged pin, never an Active mapping.
            var expected = unmap ? fault == 1 ? PlatformExternalClosureState.Draining : PlatformExternalClosureState.Faulted : (object)
                (fault == 1 ? PlatformChildDomainState.Running : PlatformChildDomainState.Faulted);
            Assert.Equal(expected, State());
            if (unmap) Assert.False(bridge.CommitSecureGuestMapping(guest, parent, true, KernelResult.Ok, out _, out _).IsSuccess);
        }
    }

    [Theory]
    [InlineData(PlatformAuthorityStatus.Denied)]
    [InlineData(PlatformAuthorityStatus.Unsupported)]
    [InlineData((PlatformAuthorityStatus)777)]
    public void GenericTransitionFailureNeverRetainsUsableChild(PlatformAuthorityStatus status)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(982, 9820);
        var root = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(root, owner, Intent()).Value!;
        Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.Start).IsSuccess);
        provider.ChildTransitionStatus = status;
        Assert.False(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.Park).IsSuccess);
        var records = (global::System.Collections.IDictionary)IoField(bridge, "_childBindings");
        var record = records[child.BindingId]!;
        Assert.Equal(PlatformChildDomainState.Faulted, record.GetType().GetProperty("State")!.GetValue(record));
        Assert.Equal(false, record.GetType().GetProperty("TransitionInFlight")!.GetValue(record));
        Assert.False(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.Park).IsSuccess);
        Assert.Equal(2, provider.TransitionCalls);
        Assert.False(bridge.RevokeDomain(root, owner).IsSuccess);
    }

    private static object IoField(object owner, string name) => owner.GetType().GetField(name,
        global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(owner)!;

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void VirtualIoCapacityRefusalPreservesOriginalInterlocksAndActualBacking(bool deviceCapacity)
    {
        var s = CreateIoClosureScenario();
        var bridge = s.Kernel.PlatformAuthority;
        var records = (global::System.Collections.IDictionary)IoField(bridge, "_virtualIoBindings");
        var record = Assert.Single(records.Values.Cast<object>());
        var binding = (PlatformVirtualIoBinding)record.GetType().GetProperty("Binding")!.GetValue(record)!;
        var owners = (global::System.Collections.IDictionary)IoField(bridge, deviceCapacity ? "_deviceLeases" : "_childBindings");
        var owner = owners[deviceCapacity ? (object)s.Device.LeaseId : binding.Child.BindingId]!;
        var counter = owner.GetType().GetProperty(deviceCapacity ? "PendingVirtualIoBinds" : "PendingChildEffects")!;
        counter.SetValue(owner, int.MaxValue);
        var identity = IoField(bridge, "_nextVirtualIoBindingId");
        Assert.Equal(KernelError.CapacityExhausted, bridge.BindChildVirtualIo(binding.Child, s.Device,
            new(PlatformDeviceRights.Read, 512)).Error);
        Assert.Equal(int.MaxValue, counter.GetValue(owner));
        Assert.Equal(identity, IoField(bridge, "_nextVirtualIoBindingId"));
        Assert.Single(records.Values.Cast<object>());
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        counter.SetValue(owner, 0);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.VirtualIoRevokeCalls);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);
    }

    private static (RuntimeKernel Kernel, ChildProvider Provider, ProcessHandle Owner,
        VirtualDomainAuthoritySet Domain, PlatformDeviceLease Device,
        VirtualIoBinding Io) CreateIoClosureScenario()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 10, 100);
        var create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var domain = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        var parent = new PlatformDomainBinding(new(1), new(1), new(process.DomainId, owner));
        var source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Device,
            "device:virtual-io-closure", CapabilityRights.Read | CapabilityRights.Configure).Value!.CapabilityId;
        var device = kernel.BindPlatformDevice(owner, parent, source, PlatformDeviceRights.Read).Value!;
        var io = kernel.BindVirtualIo(owner, domain.Domain, device, new(PlatformDeviceRights.Read, 512)).Value!;
        _ = kernel.AllocateRegion(owner, 4096).Value!;
        return (kernel, provider, owner, domain, device, io);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void ActualVirtualIoCloseSourceRefusesEffectOrRetainsOriginalKnownReceipt(int fault)
    {
        var s = CreateIoClosureScenario();
        var records = (global::System.Collections.IDictionary)IoField(s.Kernel.PlatformAuthority, "_virtualIoBindings");
        var record = Assert.Single(records.Values.Cast<object>());
        var epoch = s.Kernel.PlatformAuthority.BackendEpoch;
        var outside = true;
        s.Provider.BeforeIncarnationRead = () =>
        {
            outside &= !Monitor.IsEntered(IoField(s.Kernel, "_platformMemoryUseGate")) &&
                !Monitor.IsEntered(IoField(s.Kernel, "_virtualIoGate")) &&
                !Monitor.IsEntered(IoField(s.Kernel.PlatformAuthority, "_secureDomainLifecycleGate"));
            if (fault == 5) throw new IOException("Pre-effect I/O source unavailable");
        };
        if (fault == 3) s.Provider.CurrentIncarnation = new(2);
        s.Provider.BeforeVirtualIoRevoke = () =>
        {
            if (fault == 0) s.Provider.BeforeIncarnationRead = () => throw new IOException("Known I/O source unavailable");
            if (fault == 1) s.Provider.CurrentIncarnation = new(2);
            if (fault == 2) Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault == 4) throw new IOException("I/O revoke reply lost");
        };
        Assert.False(s.Kernel.CloseVirtualIo(s.Io).IsSuccess);
        Assert.True(outside);
        Assert.Equal(fault is 3 or 5 ? 0 : 1, s.Provider.VirtualIoRevokeCalls);
        Assert.NotEqual(PlatformExternalClosureState.Closed, record.GetType().GetProperty("Closure")!.GetValue(record));
        var fact = record.GetType().GetProperty("LastClosureReceipt")!.GetValue(record);
        if (fault is 0 or 1 or 2)
        {
            Assert.NotNull(fact);
            Assert.Equal(epoch, fact.GetType().GetProperty("AdmissionEpoch")!.GetValue(fact));
            Assert.Equal(record.GetType().GetProperty("ProviderLease")!.GetValue(record),
                fact.GetType().GetProperty("AdmittedLease")!.GetValue(fact));
        }
        else Assert.Null(fact);
        Assert.Equal(0, s.Provider.DeviceRevokeCalls);
        Assert.Equal(0, s.Provider.RevokeDomainCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        s.Provider.BeforeIncarnationRead = null;
        s.Provider.BeforeVirtualIoRevoke = null;
        Assert.Equal(fault is 0 or 5, s.Kernel.CloseVirtualIo(s.Io).IsSuccess);
        Assert.Equal(fault is 0 or 5, s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(fault is 3 ? 0 : 1, s.Provider.VirtualIoRevokeCalls);
        Assert.Equal(fault is 0 or 5 ? RegionState.Released : RegionState.Owned,
            Assert.Single(s.Kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ActualVirtualIoKnownClosureRetryPreservesOneEffectAcrossConsumers(int mode)
    {
        var s = CreateIoClosureScenario();
        s.Provider.BeforeVirtualIoRevoke = () =>
            s.Provider.BeforeIncarnationRead = () => throw new IOException("Known I/O source unavailable");
        KernelResult Close() => mode switch
        {
            0 => s.Kernel.CloseVirtualIo(s.Io),
            1 => s.Kernel.RevokePlatformDevice(s.Owner, s.Device),
            2 => s.Kernel.TerminateProcess(s.Owner),
            _ => s.Kernel.DestroyVirtualDomain(s.Owner, s.Domain.Domain, s.Domain.ConfigureCapability)
        };
        Assert.False(Close().IsSuccess);
        Assert.Equal(1, s.Provider.VirtualIoRevokeCalls);
        Assert.Equal(0, s.Provider.DeviceRevokeCalls);
        Assert.Equal(0, s.Provider.RevokeDomainCalls);
        s.Provider.BeforeVirtualIoRevoke = null;
        s.Provider.BeforeIncarnationRead = null;
        if (mode == 3) Assert.True(s.Kernel.RevokePlatformDevice(s.Owner, s.Device).IsSuccess);
        Assert.True(Close().IsSuccess);
        Assert.Equal(1, s.Provider.VirtualIoRevokeCalls);
        if (mode != 2) Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.DeviceRevokeCalls);
        Assert.Equal(1, s.Provider.RevokeDomainCalls);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task VirtualIoSourcePauseInterlocksActualDeviceDrainAndRechecksReset(bool reset)
    {
        var s = CreateIoClosureScenario();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        s.Provider.BeforeIncarnationRead = () =>
        {
            s.Provider.BeforeIncarnationRead = null;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(6))) throw new TimeoutException("I/O source pause expired");
        };
        var work = Task.Run(() => s.Kernel.RevokePlatformDevice(s.Owner, s.Device));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            await Task.Run(() =>
            {
                Assert.True(s.Kernel.QueryVirtualDomain(s.Owner, s.Domain.Domain).IsSuccess);
                Assert.False(s.Kernel.CloseVirtualIo(s.Io).IsSuccess);
                Assert.False(s.Kernel.RevokePlatformDevice(s.Owner, s.Device).IsSuccess);
                Assert.Equal(0, s.Provider.VirtualIoRevokeCalls);
                Assert.Equal(0, s.Provider.DeviceRevokeCalls);
                if (reset) Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
            }).WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { release.Set(); }
        Assert.Equal(!reset, (await work).IsSuccess);
        Assert.Equal(reset ? 0 : 1, s.Provider.VirtualIoRevokeCalls);
        Assert.Equal(reset ? 0 : 1, s.Provider.DeviceRevokeCalls);
        Assert.Equal(!reset, s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(reset ? RegionState.Owned : RegionState.Released,
            Assert.Single(s.Kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void VirtualIoClosureReceiptRetentionNeverSubstitutesForExactContinuousClosure(int fault)
    {
        var provider = new ChildProvider { VirtualIoRevokeStatus = fault == 3 ? PlatformAuthorityStatus.Faulted : null };
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        var device = BindDeviceForModel(bridge, parent, owner, new CapabilityId(9),
            new("device:test"), PlatformDeviceRights.Read).Value!;
        var io = bridge.BindChildVirtualIo(child, device, new(PlatformDeviceRights.Read, 512)).Value!;
        var epoch = bridge.BackendEpoch;
        var gate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(bridge)!;
        provider.BeforeVirtualIoRevoke = () =>
        {
            Assert.False(global::System.Threading.Monitor.IsEntered(gate));
            if (fault == 1) Assert.True(bridge.ObserveBackendReset().IsSuccess);
            if (fault == 2) throw new InvalidOperationException("reply lost");
        };
        if (fault == 4) provider.VirtualIoClosureTransform = receipt => receipt with { Generation = new(receipt.Generation.Value + 1) };
        Assert.Equal(fault == 0, bridge.RevokeChildVirtualIo(io).IsSuccess);
        var bindings = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_virtualIoBindings",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(bridge)!;
        var record = bindings.Values.Cast<object>().Single();
        var fact = record.GetType().GetProperty("LastClosureReceipt")!.GetValue(record);
        if (fault is 2 or 3) Assert.Null(fact);
        else
        {
            Assert.NotNull(fact);
            var lease = (PlatformProviderVirtualIoLease)record.GetType().GetProperty("ProviderLease")!.GetValue(record)!;
            Assert.Equal(lease, fact.GetType().GetProperty("AdmittedLease")!.GetValue(fact));
            Assert.Equal(epoch, fact.GetType().GetProperty("AdmissionEpoch")!.GetValue(fact));
            var receipt = (PlatformVirtualIoClosureReceipt)fact.GetType().GetProperty("Receipt")!.GetValue(fact)!;
            Assert.Equal(lease.LeaseId, receipt.LeaseId);
            Assert.Equal(lease.Generation.Value + (fault == 4 ? 1UL : 0UL), receipt.Generation.Value);
        }
        Assert.Equal(fault == 0, bridge.RevokeChildVirtualIo(io).IsSuccess);
        Assert.Equal(1, provider.VirtualIoRevokeCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void ChildClosureKnownReceiptIsRetainedWithoutGrantingClosureOnContinuityLoss(int fault)
    {
        var provider = new ChildProvider { MalformedClose = fault == 4 };
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).IsSuccess);
        var epoch = bridge.BackendEpoch;
        var gate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(bridge)!;
        provider.BeforeChildClose = () =>
        {
            Assert.False(global::System.Threading.Monitor.IsEntered(gate));
            if (fault == 1) Assert.True(bridge.ObserveBackendReset().IsSuccess);
            if (fault == 2) throw new InvalidOperationException("Closure reply lost");
        };
        if (fault == 3) provider.ChildCreateCleanupStatus = PlatformAuthorityStatus.Faulted;
        Assert.Equal(fault == 0, bridge.CloseChildDomain(child).IsSuccess);
        var children = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_childBindings",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(bridge)!;
        var record = Assert.Single(children.Values.Cast<object>());
        var fact = record.GetType().GetProperty("LastClosureReceipt")!.GetValue(record);
        if (fault is 2 or 3) Assert.Null(fact);
        else
        {
            Assert.NotNull(fact);
            Assert.Equal(provider.LastChildLease, fact.GetType().GetProperty("AdmittedLease")!.GetValue(fact));
            Assert.Equal(epoch, fact.GetType().GetProperty("AdmissionEpoch")!.GetValue(fact));
            var receipt = (PlatformChildDomainClosureReceipt)fact.GetType().GetProperty("Receipt")!.GetValue(fact)!;
            Assert.Equal(provider.LastChildLease.LeaseId, receipt.LeaseId);
            Assert.Equal(provider.LastChildLease.Generation.Value + (fault == 4 ? 1UL : 0UL), receipt.Generation.Value);
        }
        Assert.Equal(false, record.GetType().GetProperty("CloseInFlight")!.GetValue(record));
        Assert.Equal(fault == 0, bridge.CloseChildDomain(child, allowCommittedClosure: true).IsSuccess);
        if (fault == 1) Assert.True(bridge.TryGetQuarantinedDomainBinding(owner, out parent));
        Assert.Equal(fault == 0, bridge.RevokeDomain(parent, owner).IsSuccess);
        Assert.Equal(1, provider.CloseCalls);
        Assert.Equal(fault == 0 ? 1 : 0, provider.RevokeDomainCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void KernelTrapResetRejectsStaleReplayWithoutReplacingKnownSequence(int fault)
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 10, 100);
        var create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var domain = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        Assert.True(kernel.StartVirtualDomain(owner, domain.Domain, domain.ExecuteCapability).IsSuccess);
        Assert.True(kernel.ObserveVirtualTrap(owner, domain.Domain, domain.TrapCapability).IsSuccess);
        var bridge = kernel.PlatformAuthority;
        var children = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_childBindings",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(bridge)!;
        var record = Assert.Single(children.Values.Cast<object>());
        var admittedLease = (PlatformProviderChildDomainLease)record.GetType().GetProperty("ProviderLease")!.GetValue(record)!;
        var observations = new List<ProviderTraceEventV1>();
        // Exact evidence correlation only, never a semantic-extension admission digest.
        string Hash(string text) => Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(
            global::System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        string Digest(ulong epoch) => Hash(FormattableString.Invariant(
            $"trap-qualification/1:{admittedLease.LeaseId.Value}:{admittedLease.Generation.Value}:{admittedLease.ParentDomainLease.LeaseId.Value}:{admittedLease.ParentDomainLease.Generation.Value}:{owner.ProcessId.Value}:{owner.Generation}:{domain.TrapCapability.Value}:{epoch}"));
        void Observe(SemanticTraceEventKindV1 kind) => observations.Add(new(1, "trap-invocation-2", (ulong)observations.Count + 1,
            ProviderTraceDispositionV1.SecurityRelevant, kind, "test.child.trap.qualifier",
            Digest(bridge.BackendEpoch.Value), Hash(FormattableString.Invariant(
                $"trap-observation/1:{(byte)kind}:{provider.TrapCalls}:{record.GetType().GetProperty("LastTrapSequence")!.GetValue(record)}:{record.GetType().GetProperty("PendingChildEffects")!.GetValue(record)}:{record.GetType().GetProperty("State")!.GetValue(record)}"))));
        provider.TrapTransform = evidence => fault switch
        {
            0 => evidence with { ChildGeneration = new(evidence.ChildGeneration.Value + 1) },
            1 => evidence with { ParentGeneration = new(evidence.ParentGeneration.Value + 1) },
            2 => evidence with { Sequence = 1 },
            _ => evidence with { Sequence = 0 },
        };
        provider.BeforeChildTrap = () =>
        {
            Assert.Equal(1, record.GetType().GetProperty("PendingChildEffects")!.GetValue(record));
            Observe(SemanticTraceEventKindV1.Submit);
            Observe(SemanticTraceEventKindV1.EffectPossible);
            Assert.True(kernel.ObservePlatformBackendReset().IsSuccess);
            Observe(SemanticTraceEventKindV1.GenerationChanged);
        };
        Assert.Equal(KernelError.PlatformFaulted,
            kernel.ObserveVirtualTrap(owner, domain.Domain, domain.TrapCapability).Error);
        Assert.Equal(PlatformChildDomainState.Faulted, record.GetType().GetProperty("State")!.GetValue(record));
        Observe(SemanticTraceEventKindV1.Quarantined);
        var trace = SemanticTraceProjectionV1.Project(observations);
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.All(trace, e => { Assert.False(e.AuthorizesExecution); Assert.False(e.AuthorizesEffect); Assert.False(e.AuthorizesPublication); });
        foreach (var invented in new[] { SemanticTraceEventKindV1.Published, SemanticTraceEventKindV1.Released })
            Assert.False(SemanticTraceValidatorV1.Validate(trace.Concat([trace[^1] with { Sequence = 5, Kind = invented }])).IsValid);
        var containment = SemanticRefinementEvaluatorV1.Evaluate(
            new SemanticRequirementV1<ContainmentClassV1>(1, SemanticRequirementStrengthV1.Mandatory, ContainmentClassV1.ExactOperationProviderGenerationClosure),
            new SemanticGuaranteeV1<ContainmentClassV1>(1, SemanticGuaranteeSupportV1.Unsupported, ContainmentClassV1.None),
            SemanticPartialOrdersV1.ContainmentRefines);
        Assert.Equal(SemanticRefinementStatusV1.MandatoryUnsupported, containment.Status);
        Assert.False(containment.AuthorizesEffect);
        Assert.Equal(1UL, record.GetType().GetProperty("LastTrapSequence")!.GetValue(record));
        Assert.Equal(0, record.GetType().GetProperty("PendingChildEffects")!.GetValue(record));
        Assert.False(kernel.ObserveVirtualTrap(owner, domain.Domain, domain.TrapCapability).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(2, provider.TrapCalls);
        Assert.Equal(0, provider.CloseCalls);
        Assert.Equal(0, provider.RevokeDomainCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void TrapResetKeepsValidKnownSequenceWithoutInventingLostReceipt(int fault)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        if (fault == 2) provider.TrapStatus = PlatformAuthorityStatus.Denied;
        Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.Start).IsSuccess);
        var gate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(bridge)!;
        provider.BeforeChildTrap = () =>
        {
            Assert.False(global::System.Threading.Monitor.IsEntered(gate));
            Assert.True(bridge.ObserveBackendReset().IsSuccess);
            if (fault == 1) throw new InvalidOperationException("Trap reply lost after reset");
        };
        Assert.Equal(KernelError.PlatformFaulted, bridge.ObserveChildTrap(child).Error);
        var children = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_childBindings",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(bridge)!;
        var record = Assert.Single(children.Values.Cast<object>());
        Assert.Equal(fault == 0 ? 1UL : 0UL, record.GetType().GetProperty("LastTrapSequence")!.GetValue(record));
        Assert.Equal(0, record.GetType().GetProperty("PendingChildEffects")!.GetValue(record));
        Assert.Equal(KernelError.PlatformFaulted, bridge.ObserveChildTrap(child).Error);
        Assert.Equal(KernelError.PlatformFaulted, bridge.CloseChildDomain(child).Error);
        Assert.True(bridge.TryGetQuarantinedDomainBinding(owner, out var current));
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(current, owner).Error);
        Assert.Equal(1, provider.TrapCalls);
        Assert.Equal(0, provider.CloseCalls);
        Assert.Equal(0, provider.RevokeDomainCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OverlappingRootChildCreatesRetainOnlyKnownReceiptsAcrossParentReplacement(int lostInvocation)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var epoch = bridge.BackendEpoch;
        var domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(bridge)!;
        var original = Assert.Single(domains.Values.Cast<object>());
        var gate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(bridge)!;
        using var firstEntered = new ManualResetEventSlim();
        using var bothEntered = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        var expected = new global::System.Collections.Concurrent.ConcurrentBag<PlatformProviderChildDomainLease>();
        var arrivals = 0;
        provider.BeforeChildCreateReturn = () =>
        {
            Assert.False(global::System.Threading.Monitor.IsEntered(gate));
            var invocation = Interlocked.Increment(ref arrivals);
            if (invocation != lostInvocation) expected.Add(provider.LastChildLease);
            bothEntered.Signal();
            firstEntered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
            if (invocation == lostInvocation) throw new InvalidOperationException("Materialized child reply lost");
        };
        var first = Task.Run(() => bridge.CreateChildDomain(parent, owner, Intent()));
        Task<KernelResult<PlatformChildBinding>>? second = null;
        try
        {
            Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)));
            second = Task.Run(() => bridge.CreateChildDomain(parent, owner, Intent()));
            Assert.True(bothEntered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(2, original.GetType().GetProperty("PendingChildCreates")!.GetValue(original));
            Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(parent, owner).Error);
            Assert.True(bridge.ObserveBackendReset().IsSuccess);
        }
        finally { release.Set(); }
        Assert.Equal(KernelError.PlatformFaulted, (await first.WaitAsync(TimeSpan.FromSeconds(15))).Error);
        Assert.NotNull(second);
        Assert.Equal(KernelError.PlatformFaulted, (await second.WaitAsync(TimeSpan.FromSeconds(15))).Error);
        var replacement = Assert.Single(domains.Values.Cast<object>());
        Assert.NotSame(original, replacement);
        var list = original.GetType().GetProperty("UnpublishedChildCreates")!.GetValue(original)!;
        Assert.Same(list, replacement.GetType().GetProperty("UnpublishedChildCreates")!.GetValue(replacement));
        var facts = ((global::System.Collections.IEnumerable)list).Cast<object>().ToArray();
        Assert.Equal(lostInvocation == 0 ? 2 : 1, facts.Length);
        var receipts = facts.Select(f => (PlatformProviderChildDomainLease)f.GetType().GetProperty("Receipt")!.GetValue(f)!).ToArray();
        Assert.Equal(expected.OrderBy(l => l.LeaseId.Value), receipts.OrderBy(l => l.LeaseId.Value));
        Assert.Equal(expected.Count, receipts.Select(l => l.LeaseId).Distinct().Count());
        Assert.All(facts, f => Assert.Equal(epoch, f.GetType().GetProperty("AdmissionEpoch")!.GetValue(f)));
        Assert.Equal(0, original.GetType().GetProperty("PendingChildCreates")!.GetValue(original));
        Assert.Equal(0, replacement.GetType().GetProperty("PendingChildCreates")!.GetValue(replacement));
        var children = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_childBindings",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(bridge)!;
        Assert.Empty(children);
        Assert.True(bridge.TryGetQuarantinedDomainBinding(owner, out var current));
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(current, owner).Error);
        Assert.Equal(0, provider.RevokeDomainCalls);
        Assert.Equal(0, provider.CloseCalls);
        Assert.Equal(2, provider.ChildCreateCalls);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void RootChildUnknownOutcomeNeverInventsReceiptAndMalformedCompensationKeepsPin(int fault, bool pinned)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        if (fault == 0) provider.BeforeChildCreateReturn = () => throw new InvalidOperationException("Receipt lost after effect");
        if (fault == 1) provider.ChildCreateStatus = PlatformAuthorityStatus.Faulted;
        if (fault == 2) provider.ChildCreateStatus = PlatformAuthorityStatus.NotAccepted;
        if (fault == 3) provider.MalformedCreateParent = true;
        Assert.False(bridge.CreateChildDomain(parent, owner, Intent()).IsSuccess);
        var domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(bridge)!;
        var record = Assert.Single(domains.Values.Cast<object>());
        var receipts = ((global::System.Collections.IEnumerable)record.GetType().GetProperty("UnpublishedChildCreates")!.GetValue(record)!).Cast<object>();
        if (fault == 3) Assert.Single(receipts); else Assert.Empty(receipts);
        Assert.Equal(0, record.GetType().GetProperty("PendingChildCreates")!.GetValue(record));
        var children = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_childBindings",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(bridge)!;
        Assert.Empty(children);
        var close = bridge.RevokeDomain(parent, owner);
        Assert.Equal(!pinned, close.IsSuccess);
        Assert.Equal(pinned ? 0 : 1, provider.RevokeDomainCalls);
        Assert.Equal(1, provider.ChildCreateCalls);
    }

    [Theory]
    [InlineData(PlatformAuthorityStatus.NotAccepted, false, false)]
    [InlineData(PlatformAuthorityStatus.Denied, false, false)]
    [InlineData(PlatformAuthorityStatus.Stale, false, false)]
    [InlineData((PlatformAuthorityStatus)999, false, false)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, true, false)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, false, true)]
    public void OriginalChildCreateFailureRetainsIntentRawAndRefusesRepeatedEffect(
        PlatformAuthorityStatus status, bool reset, bool lostReply)
    {
        var provider = new ChildProvider { ChildCreateStatus = status };
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(bridge)!;
        var original = domains[parent.BindingId]!;
        object? Read(string name) => original.GetType().GetProperty(name)!.GetValue(original);
        object Fact() => Assert.Single(((global::System.Collections.IEnumerable)Read("ChildCreateInvocations")!).Cast<object>());
        object? FactRead(string name) => Fact().GetType().GetProperty(name)!.GetValue(Fact());
        var callbackSeen = false;
        provider.BeforeChildCreateReturn = () =>
        {
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(bridge)!));
            Assert.NotNull(FactRead("Intent"));
            Assert.Same(original, FactRead("Source"));
            Assert.Null(FactRead("Observation"));
            callbackSeen = true;
            if (reset) Assert.True(bridge.ObserveBackendReset().IsSuccess);
            if (lostReply) throw new IOException("Child create reply lost after effect.");
        };
        Assert.False(bridge.CreateChildDomain(parent, owner, Intent()).IsSuccess);
        Assert.True(callbackSeen);
        Assert.Equal(0, Read("PendingChildCreates"));
        Assert.Equal(true, Read("ChildCreateMayHaveEffect"));
        if (lostReply) Assert.Null(FactRead("Observation"));
        else Assert.Equal(status, Assert.IsType<PlatformAuthorityResult<PlatformProviderChildDomainLease>>(FactRead("Observation")).Status);
        provider.BeforeChildCreateReturn = null;
        provider.ChildCreateStatus = null;
        var current = parent;
        if (reset)
        {
            Assert.True(bridge.TryGetQuarantinedDomainBinding(owner, out current));
            var retained = domains[parent.BindingId]!;
            Assert.Same(Read("ChildCreateInvocations"), retained.GetType().GetProperty("ChildCreateInvocations")!.GetValue(retained));
        }
        Assert.False(bridge.CreateChildDomain(current, owner, Intent()).IsSuccess);
        Assert.False(bridge.RevokeDomain(current, owner).IsSuccess);
        Assert.Equal(1, provider.ChildCreateCalls);
        Assert.Equal(0, provider.CloseCalls);
        Assert.Equal(0, provider.RevokeDomainCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RootChildCreateRetainsExactKnownReceiptAcrossReset(bool malformed)
    {
        var provider = new ChildProvider { MalformedCreateParent = malformed,
            ChildCreateCleanupStatus = malformed ? PlatformAuthorityStatus.Faulted : null };
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var epoch = bridge.BackendEpoch;
        if (!malformed) provider.BeforeChildCreateReturn = () => Assert.True(bridge.ObserveBackendReset().IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, bridge.CreateChildDomain(parent, owner, Intent()).Error);
        var domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(bridge)!;
        var record = Assert.Single(domains.Values.Cast<object>());
        var list = record.GetType().GetProperty("UnpublishedChildCreates")!.GetValue(record)!;
        var receipt = Assert.Single(((global::System.Collections.IEnumerable)list).Cast<object>());
        Assert.Equal(provider.LastChildLease, receipt.GetType().GetProperty("Receipt")!.GetValue(receipt));
        Assert.Equal(parent, receipt.GetType().GetProperty("Parent")!.GetValue(receipt));
        Assert.Equal(epoch, receipt.GetType().GetProperty("AdmissionEpoch")!.GetValue(receipt));
        Assert.Equal(0, record.GetType().GetProperty("PendingChildCreates")!.GetValue(record));
        Assert.True(bridge.ObserveBackendReset().IsSuccess);
        var replacement = Assert.Single(domains.Values.Cast<object>());
        Assert.Same(list, replacement.GetType().GetProperty("UnpublishedChildCreates")!.GetValue(replacement));
        Assert.True(bridge.TryGetQuarantinedDomainBinding(owner, out var current));
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(current, owner).Error);
        Assert.Equal(0, provider.RevokeDomainCalls);
        Assert.Equal(1, provider.ChildCreateCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void VirtualIoRevalidationUsesFreshSourceAndKeepsExactCleanup(int fault)
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 10, 100);
        var create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var domain = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        var parent = new PlatformDomainBinding(new(1), new(1), new(process.DomainId, owner));
        var source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Device,
            "device:virtual-io-revalidate", CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!.CapabilityId;
        var device = kernel.BindPlatformDevice(owner, parent, source,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write).Value!;
        var io = kernel.BindVirtualIo(owner, domain.Domain, device, new(PlatformDeviceRights.Read, 512)).Value;
        Assert.True(kernel.RevalidateVirtualIo(owner, io).IsSuccess);
        void Revoke() => Assert.True(kernel.CapabilityAuthority.Revoke(source).IsSuccess);
        if (fault == 2) provider.BeforeIncarnationRead = () =>
        {
            provider.BeforeIncarnationRead = null;
            Assert.True(kernel.StartVirtualDomain(owner, domain.Domain, domain.ExecuteCapability).IsSuccess);
        };
        else if (fault == 1) provider.BeforeIncarnationRead = () => { provider.BeforeIncarnationRead = null; Revoke(); };
        else Revoke();
        Assert.Equal(fault == 2 ? KernelError.InvalidTransition : KernelError.CapabilityRevoked,
            kernel.RevalidateVirtualIo(owner, io).Error);
        if (fault == 2) Assert.True(kernel.RevalidateVirtualIo(owner, io).IsSuccess);
        Assert.True(kernel.PlatformAuthority.ValidateDeviceLease(device, parent.Subject).IsSuccess);
        Assert.True(kernel.CloseVirtualIo(io).IsSuccess);
        Assert.True(kernel.CloseVirtualIo(io).IsSuccess);
        Assert.Equal(1, provider.VirtualIoRevokeCalls);
        Assert.True(kernel.RevokePlatformDevice(owner, device).IsSuccess);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void KernelVirtualIoFreshSourceRefusesAdmissionOrLatePublication(int fault)
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 10, 100);
        var create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var authority = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).IsSuccess);
        var parent = new PlatformDomainBinding(new(1), new(1), new(process.DomainId, owner));
        var source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Device,
            "device:virtual-io-source", CapabilityRights.Read | CapabilityRights.Configure).Value!.CapabilityId;
        var device = kernel.BindPlatformDevice(owner, parent, source, PlatformDeviceRights.Read).Value!;
        var callbacks = 0;
        void RevokeSource() => Assert.True(kernel.CapabilityAuthority.Revoke(source).IsSuccess);
        if (fault == 0)
            provider.BeforeIncarnationRead = () => { provider.BeforeIncarnationRead = null; RevokeSource(); };
        provider.AfterVirtualIoBind = () =>
        {
            callbacks++;
            if (fault == 2)
                Assert.Equal(KernelError.PlatformBindingDraining, kernel.TerminateProcess(owner).Error);
            else if (fault == 3)
                Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
            else if (fault == 4)
                Assert.Equal(KernelError.PlatformBindingActive, kernel.RevokeCapability(source).Error);
            else
            {
                Assert.Equal(KernelError.PlatformBindingActive,
                    kernel.BindVirtualIo(owner, authority.Domain, device, new(PlatformDeviceRights.Read, 512)).Error);
                RevokeSource();
            }
        };
        var result = kernel.BindVirtualIo(owner, authority.Domain, device, new(PlatformDeviceRights.Read, 512));
        Assert.False(result.IsSuccess);
        Assert.Equal(fault == 0 ? 0 : 1, callbacks);
        Assert.Equal(fault is 1 or 4 ? 1 : 0, provider.VirtualIoRevokeCalls);
        if (fault == 4) Assert.True(kernel.RevokeCapability(source).IsSuccess);
        if (fault == 2) Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.False(kernel.BindVirtualIo(owner, authority.Domain, device, new(PlatformDeviceRights.Read, 512)).IsSuccess);
        Assert.Equal(fault == 0 ? 0 : 1, callbacks);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void VirtualIoSourceExceptionSeparatesUninvokedAndKnownCleanupAtActualConsumer(bool late)
    {
        var provider = new ChildProvider(); var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 982, 9820);
        var create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var domain = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        var root = new PlatformDomainBinding(new(1), new(1), new(process.DomainId, owner));
        var cap = kernel.MintCapability(process.DomainId, owner, ResourceKind.Device,
            "device:source-exception", CapabilityRights.Read | CapabilityRights.Configure).Value!.CapabilityId;
        var device = kernel.BindPlatformDevice(owner, root, cap, PlatformDeviceRights.Read).Value!;
        var region = kernel.AllocateRegion(owner, 4096).Value!;
        var invocations = 0;
        var sourceReads = 0;
        void Install() => provider.BeforeIncarnationRead = () =>
        {
            if (!late && ++sourceReads == 1) return; // Kernel DeviceEffectSource precedes bridge admission.
            provider.BeforeIncarnationRead = null;
            if (late)
            {
                var known = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_virtualIoBindings",
                    global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!;
                Assert.Single(known.Values.Cast<object>());
            }
            throw new IOException("virtual-I/O source unavailable");
        };
        if (!late) Install();
        provider.AfterVirtualIoBind = () => { invocations++; if (late) Install(); };
        Assert.False(kernel.BindVirtualIo(owner, domain.Domain, device, new(PlatformDeviceRights.Read, 512)).IsSuccess);
        provider.BeforeIncarnationRead = null; provider.AfterVirtualIoBind = null;
        Assert.Equal(late ? 1 : 0, invocations);
        Assert.Equal(late ? 1 : 0, provider.VirtualIoRevokeCalls);
        Assert.True(kernel.RevokePlatformDevice(owner, device).IsSuccess);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
    }

    [Fact]
    public async Task VirtualIoSourcePauseAllowsStopAndRefusesInvocationAfterFreshAdmission()
    {
        var provider = new ChildProvider(); var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 983, 9830);
        var create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var domain = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        var root = new PlatformDomainBinding(new(1), new(1), new(process.DomainId, owner));
        var cap = kernel.MintCapability(process.DomainId, owner, ResourceKind.Device,
            "device:source-pause", CapabilityRights.Read | CapabilityRights.Configure).Value!.CapabilityId;
        var device = kernel.BindPlatformDevice(owner, root, cap, PlatformDeviceRights.Read).Value!;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var invocations = 0; provider.AfterVirtualIoBind = () => invocations++;
        var sourceReads = 0;
        provider.BeforeIncarnationRead = () =>
        {
            if (++sourceReads == 1) return; // Pause actual bridge source after its pending admission.
            provider.BeforeIncarnationRead = null; entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(30)));
        };
        var work = Task.Run(() => kernel.BindVirtualIo(owner, domain.Domain, device, new(PlatformDeviceRights.Read, 512)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True((await Task.Run(() => kernel.Processes.Resolve(owner)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.False((await Task.Run(() => kernel.TerminateProcess(owner)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.Equal(0, invocations);
        }
        finally { release.Set(); }
        Assert.False((await work.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        Assert.Equal(0, invocations);
        provider.AfterVirtualIoBind = null;
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void VirtualIoGenerationQueriesRunOutsideOwnerLockAndDriftRefusesPublication(bool drift)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        var device = BindDeviceForModel(bridge, parent, owner, new CapabilityId(9),
            new("device:test"), PlatformDeviceRights.Read).Value!;
        var gate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(bridge)!;
        var reads = 0;
        provider.BeforeIncarnationRead = () => { Assert.False(Monitor.IsEntered(gate)); reads++; };
        provider.AfterVirtualIoBind = () =>
        {
            Assert.False(Monitor.IsEntered(gate));
            if (drift) provider.CurrentIncarnation = new(2);
        };
        var result = bridge.BindChildVirtualIo(child, device, new(PlatformDeviceRights.Read, 512), out var known);
        Assert.Equal(!drift, result.IsSuccess);
        Assert.NotNull(known);
        Assert.True(reads >= 2);
        provider.BeforeIncarnationRead = null;
        if (!drift) Assert.True(bridge.RevokeChildVirtualIo(result.Value!).IsSuccess);
        else
        {
            Assert.Equal(KernelError.PlatformFaulted, bridge.RevokeChildVirtualIo(known!.Value).Error);
            Assert.False(bridge.BindChildVirtualIo(child, device, new(PlatformDeviceRights.Read, 512)).IsSuccess);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void VirtualIoTerminalCapacityRefusesProviderAndPreservesActualDeviceCleanup(bool zero)
    {
        var provider = new ChildProvider(); var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 984, 9840);
        var create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var domain = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        var root = new PlatformDomainBinding(new(1), new(1), new(process.DomainId, owner));
        var cap = kernel.MintCapability(process.DomainId, owner, ResourceKind.Device,
            "device:capacity", CapabilityRights.Read | CapabilityRights.Configure).Value!.CapabilityId;
        var device = kernel.BindPlatformDevice(owner, root, cap, PlatformDeviceRights.Read).Value!;
        var region = kernel.AllocateRegion(owner, 4096).Value!;
        var counter = typeof(PlatformAuthorityBridge).GetField("_nextVirtualIoBindingId",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!;
        ulong terminal = zero ? 0 : ulong.MaxValue; counter.SetValue(kernel.PlatformAuthority, terminal);
        provider.AfterVirtualIoBind = () => Assert.Fail("Capacity refusal must precede provider invocation.");
        Assert.Equal(KernelError.CapacityExhausted, kernel.BindVirtualIo(owner, domain.Domain, device, new(PlatformDeviceRights.Read, 512)).Error);
        Assert.Equal(terminal, counter.GetValue(kernel.PlatformAuthority));
        Assert.Equal(0, provider.VirtualIoRevokeCalls);
        Assert.True(kernel.RevokePlatformDevice(owner, device).IsSuccess);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void ChildTrapProviderSourceRefusalPreservesKnownSequenceAndActualCleanup(int fault)
    {
        var provider = new ChildProvider(); var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 986, 9860);
        var create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var domain = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        Assert.True(kernel.StartVirtualDomain(owner, domain.Domain, domain.ExecuteCapability).IsSuccess);
        var region = kernel.AllocateRegion(owner, 4096).Value!;
        if (fault is 0 or 1)
            provider.BeforeIncarnationRead = () =>
            {
                provider.BeforeIncarnationRead = null;
                if (fault == 0) throw new IOException("event pre-source unavailable");
                provider.CurrentIncarnation = new(2);
            };
        provider.BeforeChildTrap = () => provider.BeforeIncarnationRead = () =>
        {
            provider.BeforeIncarnationRead = null;
            if (fault == 2) throw new IOException("known event source unavailable");
            if (fault == 3) provider.CurrentIncarnation = new(2);
            if (fault == 4) Assert.True(kernel.RevokeCapability(domain.TrapCapability).IsSuccess);
        };
        Assert.False(kernel.ObserveVirtualTrap(owner, domain.Domain, domain.TrapCapability).IsSuccess);
        provider.BeforeIncarnationRead = null; provider.BeforeChildTrap = null;
        Assert.Equal(fault < 2 ? 0 : 1, provider.TrapCalls);
        var children = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_childBindings",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!;
        var child = Assert.Single(children.Values.Cast<object>());
        Assert.Equal(fault < 2 ? 0UL : 1UL, child.GetType().GetProperty("LastTrapSequence")!.GetValue(child));
        Assert.Equal(0, child.GetType().GetProperty("PendingChildEffects")!.GetValue(child));
        if (fault is 1 or 3)
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
            Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
            Assert.Equal(0, provider.CloseCalls);
        }
        else
        {
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
            Assert.Equal(1, provider.CloseCalls);
        }
        Assert.Equal(fault < 2 ? 0 : 1, provider.TrapCalls);
    }


    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task ChildEventProviderSourceRefusalPreservesKnownSequenceAndActualCleanup(int fault)
    {
        var provider = new ChildProvider(); var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 985, 9850);
        var create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var domain = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        Assert.True(kernel.StartVirtualDomain(owner, domain.Domain, domain.ExecuteCapability).IsSuccess);
        var endpoint = kernel.CreateKernelEventEndpoint(owner).Value!;
        var region = kernel.AllocateRegion(owner, 4096).Value!;
        using var cancellation = new CancellationTokenSource();
        var waiting = kernel.WaitForKernelEventAsync(owner, endpoint, cancellation.Token).AsTask();
        if (fault is 0 or 1)
            provider.BeforeIncarnationRead = () =>
            {
                provider.BeforeIncarnationRead = null;
                if (fault == 0) throw new IOException("event pre-source unavailable");
                provider.CurrentIncarnation = new(2);
            };
        provider.BeforeChildEvent = () => provider.BeforeIncarnationRead = () =>
        {
            provider.BeforeIncarnationRead = null;
            if (fault == 2) throw new IOException("known event source unavailable");
            if (fault == 3) provider.CurrentIncarnation = new(2);
            if (fault == 4) Assert.True(kernel.RevokeCapability(domain.EventCapability).IsSuccess);
        };
        Assert.False(kernel.InjectVirtualEvent(owner, domain.Domain, domain.EventCapability, endpoint).IsSuccess);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        provider.BeforeIncarnationRead = null; provider.BeforeChildEvent = null;
        Assert.Equal(fault < 2 ? 0 : 1, provider.EventCalls);
        var children = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_childBindings",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!;
        var child = Assert.Single(children.Values.Cast<object>());
        Assert.Equal(fault < 2 ? 0UL : 1UL, child.GetType().GetProperty("LastEventSequence")!.GetValue(child));
        Assert.Equal(0, child.GetType().GetProperty("PendingChildEffects")!.GetValue(child));
        if (fault is 1 or 3)
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
            Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
            Assert.Equal(0, provider.CloseCalls);
        }
        else
        {
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
            Assert.Equal(1, provider.CloseCalls);
        }
        Assert.Equal(fault < 2 ? 0 : 1, provider.EventCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LastVirtualIoIdentityIsReservedBeforeReplyAndClosureOrNotAcceptedNeverRecycle(bool notAccepted)
    {
        var provider = new ChildProvider { VirtualIoBindStatus = notAccepted ? PlatformAuthorityStatus.NotAccepted : null };
        var bridge = new PlatformAuthorityBridge(provider); var subject = Subject(1, 10);
        var root = bridge.BindDomain(subject).Value!;
        var child = bridge.CreateChildDomain(root, subject, Intent()).Value!;
        var device = BindDeviceForModel(bridge, root, subject, new CapabilityId(9), new("device:capacity-race"), PlatformDeviceRights.Read).Value!;
        var counter = typeof(PlatformAuthorityBridge).GetField("_nextVirtualIoBindingId",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!;
        counter.SetValue(bridge, ulong.MaxValue - 1);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var calls = 0;
        provider.AfterVirtualIoBind = () => { calls++; entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(30))); };
        var work = Task.Run(() => bridge.BindChildVirtualIo(child, device, new(PlatformDeviceRights.Read, 512)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(ulong.MaxValue, counter.GetValue(bridge));
            var loser = await Task.Run(() => bridge.BindChildVirtualIo(child, device, new(PlatformDeviceRights.Read, 512))).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(KernelError.CapacityExhausted, loser.Error);
            Assert.Equal(1, calls);
        }
        finally { release.Set(); }
        var result = await work.WaitAsync(TimeSpan.FromSeconds(10)); provider.AfterVirtualIoBind = null;
        Assert.Equal(!notAccepted, result.IsSuccess);
        if (!notAccepted)
        {
            Assert.Equal(ulong.MaxValue - 1, result.Value!.BindingId.Value);
            Assert.True(bridge.RevokeChildVirtualIo(result.Value).IsSuccess);
        }
        Assert.Equal(notAccepted ? KernelError.PlatformFaulted : KernelError.CapacityExhausted, bridge.BindChildVirtualIo(child, device, new(PlatformDeviceRights.Read, 512)).Error);
        Assert.Equal(ulong.MaxValue, counter.GetValue(bridge));
        Assert.Equal(notAccepted ? 0 : 1, provider.VirtualIoRevokeCalls);
        if (notAccepted)
        {
            Assert.False(bridge.RevokeDevice(device, subject).IsSuccess);
            Assert.False(bridge.CloseChildDomain(child).IsSuccess);
            Assert.False(bridge.RevokeDomain(root, subject).IsSuccess);
            return; // V1 NotAccepted has no exact no-effect guarantee; original intent/parents stay pinned.
        }
        Assert.True(bridge.RevokeDevice(device, subject).IsSuccess);
        Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).IsSuccess);
        Assert.True(bridge.CloseChildDomain(child).IsSuccess);
        Assert.True(bridge.RevokeDomain(root, subject).IsSuccess);
    }
    // Existing standalone bridge fixtures model trusted caller admission; no Kernel capability claim.
    private static KernelResult<PlatformDeviceLease> BindDeviceForModel(
        PlatformAuthorityBridge bridge, PlatformDomainBinding binding, PlatformDomainIdentity owner,
        CapabilityId capability, PlatformDeviceIdentity device, PlatformDeviceRights rights) =>
        bridge.BindDevice(binding, owner, capability, device, rights, static () => KernelResult.Ok(), static (_, commit) => commit());

    [Fact]
    public void DamageInsideMappingCallbackPreservesReceiptButRevokesLocalAuthorization()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 2, 20);
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var capability = kernel.MintCapability(process.DomainId, owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var regionOwner = new RegionOwner(process.DomainId, owner.Generation);
        provider.BeforeMapOwnedRegionReturn = () => Assert.True(kernel.Regions.QuarantineSubrange(
            region.Handle, regionOwner, new(1, new("test-provider", "bank-0", 1, 1), 1,
                ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(8, 8))).IsSuccess);

        var mapped = kernel.MapPlatformOwnedRegion(owner, binding, capability, region.Handle, PlatformMemoryAccess.Read);
        Assert.True(mapped.IsSuccess, mapped.Message);
        Assert.Equal(KernelError.PlatformBindingRevoked,
            kernel.PlatformAuthority.ValidateMapping(mapped.Value!, binding.Subject).Error);
        Assert.True(kernel.Regions.HasPlatformMappingReservation(region.Handle, regionOwner));
        Assert.Equal(KernelError.Quarantined, kernel.ReleaseRegion(owner, region).Error);
        Assert.True(kernel.RevokePlatformRegionMapping(owner, mapped.Value!).IsSuccess);
        Assert.False(kernel.Regions.HasPlatformMappingReservation(region.Handle, regionOwner));
        Assert.Single(kernel.Regions.SnapshotDamage());
    }

    [Fact]
    public void ProcessExitInsideMappingCallbackTracksLateMappingForExactTeardown()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 2, 20);
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var capability = kernel.MintCapability(process.DomainId, owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        provider.BeforeMapOwnedRegionReturn = () =>
            Assert.Equal(KernelError.PlatformBindingDraining, kernel.TerminateProcess(owner).Error);

        var mapped = kernel.MapPlatformOwnedRegion(owner, binding, capability, region.Handle,
            PlatformMemoryAccess.Read);

        Assert.True(mapped.IsSuccess, mapped.Message);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.False(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.Equal(1, provider.RegionRevocationBeginCalls);
        Assert.Equal(1, provider.RevokeDomainCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LostMappingReceiptBlocksProcessReclaim(bool missingRoutes)
    {
        var provider = new ChildProvider
        {
            BeforeMapOwnedRegionReturn = () => throw new InvalidOperationException("Mapping receipt lost."),
        };
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 3, 30);
        var admin = TestFixtures.Create(kernel, 93, 930).Handle;
        var administration = kernel.MintCapability(new DomainId(930), admin,
            ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
            CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, owner, "lost-map",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4096),
                new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 4096)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var capability = kernel.MintCapability(process.DomainId, owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var binding = kernel.BindPlatformDomain(owner).Value!;

        Assert.Equal(KernelError.PlatformFaulted,
            kernel.MapPlatformOwnedRegion(owner, binding, capability, region.Handle,
                PlatformMemoryAccess.Read).Error);
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        var original = domains[binding.BindingId]!;
        Assert.Equal(true, original.GetType().GetProperty("MappingMayHaveEffect")!.GetValue(original));
        var state = original.GetType().GetProperty("AuthorityState")!;
        state.SetValue(original, Enum.Parse(state.PropertyType, "Closed"));
        if (missingRoutes)
        {
            domains.Remove(binding.BindingId);
            ((global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_processPlatformBindings", flags)!.GetValue(kernel)!).Remove(owner);
        }
        Assert.Equal(KernelError.PlatformFaulted, kernel.TerminateProcess(owner).Error);
        Assert.Equal(KernelError.PlatformFaulted, kernel.TerminateProcess(owner).Error);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.True(kernel.Regions.HasPlatformMappingReservation(region.Handle,
            new RegionOwner(process.DomainId, owner.Generation)));
        Assert.Equal(0, provider.RevokeDomainCalls);
        Assert.Equal(1, provider.MapCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        Assert.Equal(4096UL, kernel.QueryBudget(budget).Value!.Usage.Single(usage =>
            usage.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
    }

    [Fact]
    public void ResetFaultedMappingKeepsBudgetAndRegionPinnedAcrossTeardownRetries()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 4, 40);
        var admin = TestFixtures.Create(kernel, 94, 940).Handle;
        var administration = kernel.MintCapability(new DomainId(940), admin,
            ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
            CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, owner, "reset-map",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4096),
                new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 4096)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var capability = kernel.MintCapability(process.DomainId, owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        provider.BeforeMapOwnedRegionReturn = () =>
            Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);

        var mapped = kernel.MapPlatformOwnedRegion(owner, binding, capability, region.Handle,
            PlatformMemoryAccess.Read);

        Assert.True(mapped.IsSuccess, mapped.Message);
        Assert.Equal(KernelError.StaleGeneration,
            kernel.QueryPlatformRegionMappingLifecycle(owner, mapped.Value!).Error);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.True(kernel.Regions.HasPlatformMappingReservation(region.Handle,
            new RegionOwner(process.DomainId, owner.Generation)));
        Assert.Equal(4096UL, kernel.QueryBudget(budget).Value!.Usage.Single(usage =>
            usage.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.Equal(0, provider.RevokeDomainCalls);
    }

    [Fact]
    public void ResetBeforeNotAcceptedMappingReplyDoesNotReleaseRegion()
    {
        var provider = new ChildProvider { MapStatus = PlatformAuthorityStatus.NotAccepted };
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 5, 50);
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var capability = kernel.MintCapability(process.DomainId, owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        provider.BeforeMapOwnedRegionReturn = () =>
            Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);

        Assert.Equal(KernelError.PlatformFaulted,
            kernel.MapPlatformOwnedRegion(owner, binding, capability, region.Handle,
                PlatformMemoryAccess.Read).Error);
        Assert.True(kernel.Regions.HasPlatformMappingReservation(region.Handle,
            new RegionOwner(process.DomainId, owner.Generation)));
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
    }

    [Fact]
    public void StableNotAcceptedMappingReplyReleasesLocalReservation()
    {
        var provider = new ChildProvider { MapStatus = PlatformAuthorityStatus.NotAccepted };
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 6, 60);
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var capability = kernel.MintCapability(process.DomainId, owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var binding = kernel.BindPlatformDomain(owner).Value!;

        Assert.False(kernel.MapPlatformOwnedRegion(owner, binding, capability, region.Handle,
            PlatformMemoryAccess.Read).IsSuccess);
        Assert.False(kernel.Regions.HasPlatformMappingReservation(region.Handle,
            new RegionOwner(process.DomainId, owner.Generation)));
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
    }

    [Fact]
    public void MappingCallbackCannotRevokeParentBeforePublication()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var binding = bridge.BindDomain(owner).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(1), new RegionGeneration(1)),
            new(new DomainId(1), owner.ProcessGeneration), 4096);
        provider.BeforeMapOwnedRegionReturn = () =>
            Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(binding, owner).Error);

        var mapping = bridge.MapOwnedRegion(binding, owner, new CapabilityId(8), region,
            PlatformMemoryAccess.Read);

        Assert.True(mapping.IsSuccess, mapping.Message);
        Assert.Equal(1, provider.MapCalls);
        Assert.Equal(0, provider.RevokeDomainCalls);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(binding, owner).Error);
    }

    [Fact]
    public void MappingReceiptLossPinsParentAndLocalReservation()
    {
        var provider = new ChildProvider
        {
            BeforeMapOwnedRegionReturn = () => throw new InvalidOperationException("Mapping receipt lost."),
        };
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var binding = bridge.BindDomain(owner).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(1), new RegionGeneration(1)),
            new(new DomainId(1), owner.ProcessGeneration), 4096);

        var mapping = bridge.MapOwnedRegion(binding, owner, new CapabilityId(8), region,
            PlatformMemoryAccess.Read, out var retainReservation);

        Assert.Equal(KernelError.PlatformFaulted, mapping.Error);
        Assert.True(retainReservation);
        Assert.Equal(KernelError.PlatformFaulted, bridge.RevokeDomain(binding, owner).Error);
        Assert.Equal(0, provider.RevokeDomainCalls);
    }

    [Fact]
    public void BackendResetInsideMappingCallbackFaultPinsLateLease()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var binding = bridge.BindDomain(owner).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(1), new RegionGeneration(1)),
            new(new DomainId(1), owner.ProcessGeneration), 4096);
        var epoch = bridge.BackendEpoch;
        var domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_domains", global::System.Reflection.BindingFlags.Instance |
                global::System.Reflection.BindingFlags.NonPublic)!.GetValue(bridge)!;
        var originalDomain = domains[binding.BindingId];
        provider.BeforeMapOwnedRegionReturn = () =>
        {
            Assert.True(bridge.ObserveBackendReset().IsSuccess);
            Assert.NotEqual(epoch, bridge.BackendEpoch);
        };

        var mapped = bridge.MapOwnedRegion(binding, owner, new CapabilityId(8), region,
            PlatformMemoryAccess.Read, out var retainReservation);

        Assert.True(mapped.IsSuccess, mapped.Message);
        Assert.False(retainReservation);
        Assert.NotEqual(epoch, bridge.BackendEpoch);
        Assert.NotSame(originalDomain, domains[binding.BindingId]);
        Assert.Equal(true, domains[binding.BindingId]!.GetType()
            .GetProperty("MappingMayHaveEffect")!.GetValue(domains[binding.BindingId]));
        var mappings = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_mappings", global::System.Reflection.BindingFlags.Instance |
                global::System.Reflection.BindingFlags.NonPublic)!.GetValue(bridge)!;
        var record = mappings[mapped.Value!.MappingId]!;
        Assert.Equal(epoch, record.GetType().GetProperty("BackendEpoch")!.GetValue(record));
        Assert.Equal(PlatformExternalClosureState.Faulted,
            record.GetType().GetProperty("ClosureState")!.GetValue(record));
        Assert.Equal(KernelError.StaleGeneration,
            bridge.QueryRegionMappingLifecycle(mapped.Value!, owner).Error);
        Assert.Equal(KernelError.StaleGeneration, bridge.RevokeDomain(binding, owner).Error);
    }

    [Fact]
    public void ParentRevokeCallbackRejectsNewMappingBeforeProvider()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var binding = bridge.BindDomain(owner).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(1), new RegionGeneration(1)),
            new(new DomainId(1), owner.ProcessGeneration), 4096);
        provider.BeforeDomainRevoke = () =>
            Assert.Equal(KernelError.PlatformBindingActive,
                bridge.MapOwnedRegion(binding, owner, new CapabilityId(8), region,
                    PlatformMemoryAccess.Read).Error);

        Assert.True(bridge.RevokeDomain(binding, owner).IsSuccess);
        Assert.Equal(0, provider.MapCalls);
        Assert.Equal(1, provider.RevokeDomainCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ProcessExitInsideRootBindCallbackRetainsCleanupWithoutPublishingAttachment(bool executionAttached)
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 7, 70);
        provider.BeforeDomainBindReturn = () =>
            Assert.Equal(KernelError.PlatformFaulted, kernel.TerminateProcess(owner).Error);

        var bind = executionAttached ? kernel.BindPlatformDomain(owner) : kernel.BindPlatformAuthorityDomain(owner);
        Assert.Equal(KernelError.InvalidTransition, bind.Error);
        Assert.Equal(ProcessState.Exiting, process.State);
        Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        Assert.True(kernel.PlatformAuthority.TryGetQuarantinedDomainBinding(new(process.DomainId, owner), out _));
        var attachments = (HashSet<ProcessHandle>)typeof(RuntimeKernel).GetField("_platformExecutionAttachments",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(kernel)!;
        Assert.DoesNotContain(owner, attachments);
        Assert.Equal(0, provider.RevokeDomainCalls);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.RevokeDomainCalls);
        Assert.False(kernel.Processes.Resolve(owner).IsSuccess);
        provider.BeforeDomainBindReturn = null;
        var replacement = TestFixtures.Create(kernel, 7, 70, generation: 2).Handle;
        Assert.True(kernel.BindPlatformDomain(replacement).IsSuccess);
        Assert.Equal(KernelError.StaleHandle, kernel.BindPlatformDomain(owner).Error);
    }

    [Fact]
    public async Task RootBindResolvesProcessUnderLifecycleGateBeforePendingAdmission()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 81, 810);
        var gate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(kernel)!;
        Task<KernelResult<PlatformDomainBinding>> work;
        using var entered = new ManualResetEventSlim();
        lock (gate)
        {
            work = Task.Run(() => { entered.Set(); return kernel.BindPlatformDomain(owner); });
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            TestFixtures.Create(kernel, 81, 810, generation: 2);
        }
        Assert.Equal(KernelError.StaleHandle, (await work.WaitAsync(TimeSpan.FromSeconds(15))).Error);
        Assert.Equal(ProcessState.Exited, process.State);
        Assert.Equal(0, provider.DomainBindCalls);
        Assert.Equal(0, provider.RevokeDomainCalls);
    }

    [Fact]
    public void RootBindFreshAdmissionRefusesProcessStoppedDuringProviderPreflight()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 82, 820);
        provider.BeforeDescriptorRead = () =>
        {
            provider.BeforeDescriptorRead = null;
            Assert.Equal(KernelError.PlatformFaulted, kernel.TerminateProcess(owner).Error);
        };
        Assert.Equal(KernelError.InvalidTransition, kernel.BindPlatformDomain(owner).Error);
        Assert.Equal(ProcessState.Exiting, process.State);
        Assert.Equal(0, provider.DomainBindCalls);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(0, provider.RevokeDomainCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void RootBindCompletionRefusalKeepsExactCleanupOrResetPin(int fault)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(83, 830);
        var committed = false;
        var bind = bridge.BindDomain(owner, completionConsumer: (published, commit) =>
        {
            Assert.True(bridge.HasUnresolvedDomainBindEffect(owner));
            if (fault == 0) return KernelResult.Fail(KernelError.InvalidTransition, "local subject stopped");
            if (fault == 1) throw new InvalidOperationException("controlled local attachment failure");
            Assert.True(bridge.ObserveBackendReset().IsSuccess);
            return commit(() => { committed = true; return KernelResult.Ok(); });
        });
        Assert.Equal(fault == 0 ? KernelError.InvalidTransition : KernelError.PlatformFaulted, bind.Error);
        Assert.False(committed);
        Assert.Equal(1, provider.DomainBindCalls);
        Assert.Equal(0, provider.RevokeDomainCalls);
        Assert.Equal(fault == 2, bridge.HasUnresolvedDomainBindEffect(owner));
        Assert.True(bridge.TryGetQuarantinedDomainBinding(owner, out var cleanup));
        Assert.False(bridge.BindDomain(owner).IsSuccess);
        if (fault < 2) Assert.True(bridge.RevokeDomain(cleanup, owner).IsSuccess);
        // Reset tuple is quarantined; standalone current binding lookup is not a
        // demonstrated real kernel reconciliation consumer for its old attachment.
    }

    [Fact]
    public void RootBindAdmissionRefusalLeavesNoProviderEffectOrPendingPin()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(84, 840);
        Assert.Equal(KernelError.InvalidTransition,
            bridge.BindDomain(owner, admissionConsumer: _ =>
                KernelResult.Fail(KernelError.InvalidTransition, "fresh owner refuses")).Error);
        Assert.Equal(0, provider.DomainBindCalls);
        Assert.False(bridge.HasUnresolvedDomainBindEffect(owner));
        Assert.True(bridge.BindDomain(owner).IsSuccess);
    }

    [Fact]
    public void PendingAuthorityBindCannotBypassAdmissionButActiveRootKeepsV1LocalFallback()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 85, 850);
        Assert.True(kernel.AdmitProcess(owner).IsSuccess);
        provider.BeforeDomainBindReturn = () =>
        {
            Assert.Equal(KernelError.PlatformBindingActive, kernel.StartProcess(owner).Error);
            Assert.Equal(ProcessState.Admitted, process.State);
        };
        Assert.True(kernel.BindPlatformAuthorityDomain(owner).IsSuccess);
        Assert.True(kernel.StartProcess(owner).IsSuccess);
        Assert.True(kernel.ParkProcess(owner).IsSuccess);
        Assert.True(kernel.ResumeProcess(owner).IsSuccess);
        Assert.Equal(ProcessState.Running, process.State);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.RevokeDomainCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void VirtualCreationRetainsActualCleanupAcrossSourceStopAndFaults(int fault)
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 86, 860);
        _ = kernel.AllocateRegion(owner, 4096).Value!;
        var source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        void Revoke() => Assert.True(kernel.RevokeCapability(source).IsSuccess);
        if (fault == 0) provider.BeforeDescriptorRead = () => { provider.BeforeDescriptorRead = null; Revoke(); };
        if (fault == 1) provider.BeforeDomainBindReturn = Revoke;
        if (fault == 2) provider.BeforeChildCreateReturn = Revoke;
        if (fault == 3) provider.BeforeChildCreateReturn = () =>
        {
            Assert.Equal(KernelError.PlatformFaulted, kernel.TerminateProcess(owner).Error);
            Assert.Equal(ProcessState.Exiting, process.State);
            Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
            Assert.Equal(0, provider.CloseCalls);
            Assert.Equal(0, provider.RevokeDomainCalls);
        };
        if (fault == 4) { provider.ChildCreateStatus = PlatformAuthorityStatus.NotAccepted; provider.RootRevokeStatus = PlatformAuthorityStatus.Faulted; }
        if (fault == 5) provider.ChildCreateStatus = PlatformAuthorityStatus.Faulted;
        if (fault is 7 or 8)
            typeof(CapabilityAuthority).GetField("_nonceFactory", global::System.Reflection.BindingFlags.Instance |
                global::System.Reflection.BindingFlags.NonPublic)!.SetValue(kernel.CapabilityAuthority, (Func<Guid>)(() =>
                {
                    if (fault == 8) throw new InvalidOperationException("controlled derived nonce loss");
                    Revoke();
                    return Guid.NewGuid();
                }));
        var created = kernel.CreateVirtualDomain(owner, source, new(1, 4096));
        if (fault == 6)
        {
            Assert.True(created.IsSuccess, created.Message);
            provider.RootRevokeStatus = PlatformAuthorityStatus.Faulted;
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1, provider.CloseCalls);
            Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        }
        else Assert.False(created.IsSuccess);
        Assert.Equal(fault == 0 ? 0 : 1, provider.DomainBindCalls);
        Assert.Equal(fault < 2 ? 0 : 1, provider.ChildCreateCalls);
        if (fault != 6) Assert.Equal(0, provider.CloseCalls);
        if (fault is 0 or 1 or 2 or 7)
            Assert.Equal(KernelError.CapabilityRevoked, created.Error);
        if (fault is 2 or 7) Assert.Empty(process.Capabilities.Items);
        if (fault == 8) Assert.Single(process.Capabilities.Items);
        provider.BeforeDescriptorRead = null;
        provider.BeforeDomainBindReturn = null;
        provider.BeforeChildCreateReturn = null;
        provider.RootRevokeStatus = null;
        if (fault == 5)
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
            Assert.Equal(0, provider.RevokeDomainCalls);
            Assert.Equal(0, provider.CloseCalls);
        }
        else if (fault is 4 or 6)
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
            Assert.NotEqual(ProcessState.Exited, process.State);
            Assert.Equal(fault == 6 ? 1 : 0, provider.CloseCalls);
            Assert.Equal(fault == 4 ? 0 : 1, provider.RevokeDomainCalls);
            Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        }
        else
        {
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(ProcessState.Exited, process.State);
            Assert.Equal(KernelError.StaleHandle, kernel.QueryProcessTeardown(owner).Error);
            Assert.Equal(fault >= 2 && fault != 4 ? 1 : 0, provider.CloseCalls);
            Assert.Equal(fault == 0 ? 0 : 1, provider.RevokeDomainCalls);
            Assert.Equal(KernelError.StaleHandle, kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Error);
        }
    }

    [Theory]
    [InlineData(PlatformAuthorityStatus.NotAccepted, false)]
    [InlineData(PlatformAuthorityStatus.Denied, false)]
    [InlineData((PlatformAuthorityStatus)999, false)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, true)]
    public void FailedCreatePreventsActualProcessRegionReclaimAndRepeatedInvocation(PlatformAuthorityStatus status, bool lostReply)
    {
        var provider = new ChildProvider { ChildCreateStatus = status };
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 89, 890);
        _ = kernel.AllocateRegion(owner, 4096).Value!;
        var source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var effectSeen = false;
        provider.BeforeChildCreateReturn = () =>
        {
            Assert.NotEqual(0UL, provider.LastChildLease.LeaseId.Value);
            effectSeen = true;
            if (lostReply) throw new IOException("Create receipt lost after materialization.");
        };
        Assert.False(kernel.CreateVirtualDomain(owner, source, new(1, 4096)).IsSuccess);
        Assert.True(effectSeen);
        provider.BeforeChildCreateReturn = null;
        provider.ChildCreateStatus = null;
        Assert.False(kernel.CreateVirtualDomain(owner, source, new(1, 4096)).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.ChildCreateCalls);
        Assert.Equal(0, provider.CloseCalls);
        Assert.Equal(0, provider.RevokeDomainCalls);
        Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Fact]
    public void VirtualCreationRejectsOverlapWithoutSecondRootEffect()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 87, 870);
        var source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        provider.BeforeDomainBindReturn = () =>
            Assert.Equal(KernelError.PlatformBindingActive, kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Error);
        var created = kernel.CreateVirtualDomain(owner, source, new(1, 4096));
        Assert.True(created.IsSuccess, created.Message);
        Assert.Equal(1, provider.DomainBindCalls);
        Assert.Equal(1, provider.ChildCreateCalls);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.CloseCalls);
        Assert.Equal(1, provider.RevokeDomainCalls);
    }

    [Fact]
    public void VirtualCreationCannotReuseRetiredSourceGenerationForReplacement()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 88, 880);
        var source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        var (_, replacement) = TestFixtures.Create(kernel, 88, 880, generation: 2);
        Assert.False(kernel.CreateVirtualDomain(replacement, source, new(1, 4096)).IsSuccess);
        Assert.Equal(0, provider.DomainBindCalls);
        Assert.Equal(0, provider.ChildCreateCalls);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void ActualRootRevokeKeepsOriginalSuccessWithoutCurrentOwnerAndPinsProcess(bool foreignOwner)
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 91, 910);
        _ = kernel.AllocateRegion(owner, 4096).Value!;
        Assert.True(kernel.BindPlatformAuthorityDomain(owner).IsSuccess);
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        var key = Assert.Single(records.Keys.Cast<object>());
        var original = records[key]!;
        object? replacement = null;
        provider.BeforeDomainRevoke = () =>
        {
            if (foreignOwner)
            {
                replacement = original.GetType().GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                original.GetType().GetProperty("ProviderIncarnation")!.SetValue(replacement, new PlatformProviderIncarnation(2));
                records[key] = replacement;
            }
            else records.Remove(key);
        };
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(true, original.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("ParentRevokeInFlight")!.GetValue(original));
        if (replacement is not null)
        {
            Assert.Same(replacement, records[key]);
            Assert.Equal(false, replacement.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(replacement));
        }
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.RevokeDomainCalls);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        // Model-only restoration checks retained continuation, not durable owner recovery.
        provider.BeforeDomainRevoke = null;
        records[key] = original;
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.RevokeDomainCalls);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ActualRootRevokeRetainsKnownSuccessAndNeverRepeatsUnknownEffect(int fault)
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 89, 890);
        _ = kernel.AllocateRegion(owner, 4096).Value!;
        Assert.True(kernel.BindPlatformAuthorityDomain(owner).IsSuccess);
        provider.BeforeDomainRevoke = () =>
        {
            if (fault == 0) provider.BeforeIncarnationRead = () => throw new IOException("Root closure source unavailable");
            if (fault == 1) provider.CurrentIncarnation = new(2);
            if (fault == 2) kernel.ObservePlatformBackendReset();
            if (fault == 3) throw new IOException("Root closure reply lost");
        };
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_activeSubjects",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!;
        var record = Assert.Single(records.Values.Cast<object>());
        Assert.Equal(fault != 3, record.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(record));
        Assert.Equal(false, record.GetType().GetProperty("ParentRevokeInFlight")!.GetValue(record));
        provider.BeforeDomainRevoke = null;
        provider.BeforeIncarnationRead = null;
        Assert.Equal(fault == 0, kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.RevokeDomainCalls);
        Assert.Equal(fault == 0 ? RegionState.Released : RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void ActualRootBindRetainsOriginalSourceAndKnownLeaseBeforeLateRefusal(int fault)
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 89, 890);
        var region = kernel.AllocateRegion(owner, 4096).Value!;
        var source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var epoch = kernel.PlatformAuthority.BackendEpoch;
        var outside = true;
        provider.BeforeIncarnationRead = () =>
        {
            var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
            outside &= !Monitor.IsEntered(typeof(RuntimeKernel).GetField("_platformMemoryUseGate", flags)!.GetValue(kernel)!) &&
                !Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!);
            if (fault == 3) throw new IOException("Root source unavailable before invocation");
        };
        provider.BeforeDomainBindReturn = () =>
        {
            if (fault == 0) provider.BeforeIncarnationRead = () => throw new IOException("Known root source unavailable");
            if (fault == 1) provider.CurrentIncarnation = new(2);
            if (fault == 2) kernel.ObservePlatformBackendReset();
            if (fault == 4) throw new IOException("Root lease reply lost");
        };
        var bound = kernel.CreateVirtualDomain(owner, source, new(1, 4096));
        Assert.Equal(fault == 5, bound.IsSuccess);
        Assert.True(outside);
        Assert.Equal(fault == 3 ? 0 : 1, provider.DomainBindCalls);
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!;
        if (fault is 3 or 4) Assert.Empty(records.Values.Cast<object>());
        else
        {
            var record = Assert.Single(records.Values.Cast<object>());
            var incarnationProperty = record.GetType().GetProperty("ProviderIncarnation");
            Assert.NotNull(incarnationProperty);
            Assert.Equal(new PlatformProviderIncarnation(1), incarnationProperty.GetValue(record));
            Assert.Equal(epoch, record.GetType().GetProperty("BackendEpoch")!.GetValue(record));
            Assert.Equal(7UL, ((PlatformProviderDomainLease)record.GetType().GetProperty("ProviderLease")!.GetValue(record)!).Generation.Value);
        }
        if (fault != 5) Assert.Equal(0, provider.ChildCreateCalls);
        provider.BeforeIncarnationRead = null;
        provider.BeforeDomainBindReturn = null;
        var terminated = kernel.TerminateProcess(owner);
        Assert.Equal(fault is 0 or 3 or 5, terminated.IsSuccess);
        if (fault is 0 or 3 or 5) Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
        else
        {
            Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
            Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
            Assert.Equal(0, provider.RevokeDomainCalls);
        }
        if (fault == 0) Assert.Equal(1, provider.RevokeDomainCalls);
    }
    [Fact]
    public async Task ConcurrentStopDuringRootCallbackPinsUntilKnownReceiptAndNeverCreatesChild()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 89, 890);
        var source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        provider.BeforeDomainBindReturn = () =>
        {
            foreach (var pair in new (object Owner, string Gate)[]
            {
                (kernel, "_platformMemoryUseGate"), (kernel.PlatformAuthority, "_secureDomainLifecycleGate"),
                (kernel.CapabilityAuthority, "_gate")
            })
            {
                var gate = pair.Owner.GetType().GetField(pair.Gate,
                    global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(pair.Owner)!;
                Assert.False(Monitor.IsEntered(gate));
            }
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
        };
        var work = Task.Run(() => kernel.CreateVirtualDomain(owner, source, new(1, 4096)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            Assert.True(kernel.RevokeCapability(source).IsSuccess);
            Assert.Equal(KernelError.PlatformFaulted, kernel.TerminateProcess(owner).Error);
            Assert.Equal(ProcessState.Exiting, process.State);
            Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
            Assert.Equal(0, provider.RevokeDomainCalls);
        }
        finally { release.Set(); }
        Assert.False((await work.WaitAsync(TimeSpan.FromSeconds(15))).IsSuccess);
        Assert.Equal(0, provider.ChildCreateCalls);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.RevokeDomainCalls);
        Assert.Equal(0, provider.CloseCalls);
    }

    [Fact]
    public void PartialDerivedIssuanceRemainsOwnedAndCannotPublishCompleteAuthoritySet()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 90, 900);
        var source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var nonceCalls = 0;
        typeof(CapabilityAuthority).GetField("_nonceFactory", global::System.Reflection.BindingFlags.Instance |
            global::System.Reflection.BindingFlags.NonPublic)!.SetValue(kernel.CapabilityAuthority, (Func<Guid>)(() =>
            {
                if (++nonceCalls == 3) Assert.True(kernel.RevokeCapability(source).IsSuccess);
                return Guid.NewGuid();
            }));
        Assert.Equal(KernelError.CapabilityRevoked, kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Error);
        var domains = (VirtualDomainAuthority)typeof(RuntimeKernel).GetField("_virtualDomains",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(kernel)!;
        var cleanup = Assert.Single(domains.ForOwner(owner));
        Assert.Equal(VirtualDomainState.Quarantined, cleanup.State);
        Assert.Equal(2, cleanup.Capabilities.Length);
        Assert.Equal(2, process.Capabilities.Items.Count);
        Assert.False(kernel.ConfigureVirtualDomain(owner, cleanup.Handle, cleanup.Capabilities[0]).IsSuccess);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.CloseCalls);
        Assert.Equal(1, provider.RevokeDomainCalls);
        foreach (var id in cleanup.Capabilities)
            Assert.False(kernel.CapabilityAuthority.Validate(id, process.DomainId, owner.Generation, CapabilityRights.None).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetInsideRootBindCallbackRetainsKnownLeaseWithoutPublishingAuthority(bool terminalEpoch)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        if (terminalEpoch)
            typeof(PlatformAuthorityBridge).GetField("_backendEpoch",
                global::System.Reflection.BindingFlags.Instance |
                global::System.Reflection.BindingFlags.NonPublic)!.SetValue(bridge, ulong.MaxValue);
        provider.BeforeDomainBindReturn = () =>
        {
            var reset = bridge.ObserveBackendReset();
            Assert.Equal(terminalEpoch ? KernelError.CapacityExhausted : KernelError.None, reset.Error);
        };

        Assert.Equal(KernelError.PlatformFaulted, bridge.BindDomain(owner).Error);
        Assert.True(bridge.HasUnresolvedDomainBindEffect(owner));
        Assert.True(bridge.TryGetQuarantinedDomainBinding(owner, out var retained));
        Assert.False(bridge.ValidateDomain(retained, owner).IsSuccess);
        Assert.False(bridge.RevokeDomain(retained, owner).IsSuccess);
        Assert.Equal(0, provider.RevokeDomainCalls);
        Assert.False(bridge.BindDomain(owner).IsSuccess);
        Assert.Equal(1, provider.DomainBindCalls);
    }

    [Fact]
    public void LostRootBindReceiptPinsProcessReclaimWithoutProviderLease()
    {
        var provider = new ChildProvider
        {
            BeforeDomainBindReturn = () => throw new InvalidOperationException("Domain materialized without lease receipt."),
        };
        var kernel = new RuntimeKernel(provider);
        var owner = TestFixtures.Create(kernel, 6, 60).Handle;

        Assert.Equal(KernelError.PlatformFaulted, kernel.BindPlatformDomain(owner).Error);
        Assert.Equal(KernelError.PlatformFaulted, kernel.TerminateProcess(owner).Error);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.Equal(1, provider.DomainBindCalls);
    }

    [Fact]
    public void RootBindCallbackRejectsDuplicateAdmissionBeforeProvider()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        provider.BeforeDomainBindReturn = () => Assert.Equal(KernelError.PlatformBindingActive,
            bridge.BindDomain(owner).Error);

        Assert.True(bridge.BindDomain(owner).IsSuccess);
        Assert.Equal(1, provider.DomainBindCalls);
    }

    [Fact]
    public void RuntimeKernelUsesChildLedgerWithoutPublishingProviderLeaseAuthority()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var owner = TestFixtures.Create(kernel, 4, 40).Handle;
        CapabilityId create = kernel.MintCapability(new DomainId(40), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;

        VirtualDomainAuthoritySet authority = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).IsSuccess);
        Assert.True(kernel.StartVirtualDomain(owner, authority.Domain, authority.ExecuteCapability).IsSuccess);
        KernelEventEndpoint endpoint = kernel.CreateKernelEventEndpoint(owner).Value!;
        Assert.True(kernel.InjectVirtualEvent(owner, authority.Domain, authority.EventCapability, endpoint).IsSuccess);
        Assert.True(kernel.ParkVirtualDomain(owner, authority.Domain, authority.ExecuteCapability).IsSuccess);
        Assert.True(kernel.DestroyVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).IsSuccess);

        Assert.Equal(3, provider.TransitionCalls);
        Assert.Equal(1, provider.CloseCalls);
        Assert.Equal(KernelError.VirtualDomainNotFound, kernel.QueryVirtualDomain(owner, authority.Domain).Error);
    }

    [Fact]
    public void RuntimeKernelPinsVirtualAuthorityAfterAmbiguousChildClose()
    {
        var provider = new ChildProvider { MalformedClose = true };
        var kernel = new RuntimeKernel(provider);
        var owner = TestFixtures.Create(kernel, 5, 50).Handle;
        CapabilityId create = kernel.MintCapability(new DomainId(50), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet authority = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).IsSuccess);

        Assert.Equal(KernelError.PlatformFaulted,
            kernel.DestroyVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).Error);
        Assert.Equal(VirtualDomainState.Quarantined, kernel.QueryVirtualDomain(owner, authority.Domain).Value);
        Assert.Equal(KernelError.PlatformFaulted, kernel.TerminateProcess(owner).Error);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
    }

    [Fact]
    public void ChildLedgerKeepsLocalGenerationDistinctAndRejectsWrongOwnerBeforeProvider()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = Subject(1, 10);
        PlatformDomainBinding parent = bridge.BindDomain(owner).Value!;
        PlatformChildBinding child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;

        var stale = child with { Generation = new(child.Generation.Value + 1) };
        var wrongOwner = child with { Owner = new ProcessHandle(new ProcessId(99), 1) };

        Assert.NotEqual(provider.LastChildLease.Generation.Value, child.Generation.Value);
        Assert.Equal(KernelError.StaleGeneration,
            bridge.TransitionChildDomain(stale, PlatformChildDomainTransition.Start).Error);
        Assert.Equal(KernelError.WrongPlatformDomain,
            bridge.TransitionChildDomain(wrongOwner, PlatformChildDomainTransition.Start).Error);
        Assert.Equal(0, provider.TransitionCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChildTransitionCallbackFaultOrResetCannotPublishLateState(bool reset)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        provider.BeforeChildTransition = reset
            ? () => Assert.True(bridge.ObserveBackendReset().IsSuccess)
            : () => throw new InvalidOperationException("Transition receipt lost after callback.");

        Assert.Equal(KernelError.PlatformFaulted,
            bridge.TransitionChildDomain(child, PlatformChildDomainTransition.Start).Error);
        Assert.Equal(KernelError.PlatformFaulted,
            bridge.TransitionChildDomain(child, PlatformChildDomainTransition.Start).Error);
        Assert.Equal(1, provider.TransitionCalls);
        Assert.False(bridge.CloseChildDomain(child).IsSuccess);
    }

    [Fact]
    public async Task ConcurrentChildTransitionHasOneProviderCallback()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        provider.BeforeChildTransition = () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };
        var first = Task.Run(() => bridge.TransitionChildDomain(child, PlatformChildDomainTransition.Start));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.PlatformBindingActive,
                bridge.TransitionChildDomain(child, PlatformChildDomainTransition.Start).Error);
            Assert.Equal(KernelError.PlatformBindingActive, bridge.CloseChildDomain(child).Error);
        }
        finally { release.Set(); }
        Assert.True((await first).IsSuccess);
        Assert.Equal(1, provider.TransitionCalls);
    }

    [Fact]
    public void TransitionCallbackRejectsGuestMapEventAndTrapBeforeProvider()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(new DomainId(1), owner.ProcessGeneration), 4096);
        var parentMapping = bridge.MapOwnedRegion(parent, owner, new CapabilityId(8), region,
            PlatformMemoryAccess.Read).Value!;
        provider.BeforeChildTransition = () =>
        {
            Assert.Equal(KernelError.PlatformBindingActive,
                bridge.MapChildGuestRegion(child, parentMapping, new(0, 4096),
                    PlatformGuestMemoryAccess.Read).Error);
            Assert.Equal(KernelError.PlatformBindingActive,
                bridge.InjectChildEvent(child, PlatformVirtualEventClass.Timer, "timer:0").Error);
            Assert.Equal(KernelError.PlatformBindingActive, bridge.ObserveChildTrap(child).Error);
        };

        Assert.True(bridge.TransitionChildDomain(child,
            PlatformChildDomainTransition.BeginDrain).IsSuccess);
        Assert.Equal(0, provider.GuestMapCalls);
        Assert.Equal(0, provider.EventCalls);
        Assert.Equal(0, provider.TrapCalls);
    }

    [Fact]
    public void VirtualIoBindCallbackRejectsOverlappingChildTransition()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        var device = BindDeviceForModel(bridge, parent, owner, new CapabilityId(9),
            new("device:test"), PlatformDeviceRights.Read).Value!;
        provider.AfterVirtualIoBind = () =>
        {
            Assert.Equal(KernelError.PlatformBindingActive,
                bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).Error);
            Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDevice(device, owner).Error);
        };

        var bound = bridge.BindChildVirtualIo(child, device, new(PlatformDeviceRights.Read, 512));
        Assert.True(bound.IsSuccess);
        Assert.Equal(0, provider.TransitionCalls);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDevice(device, owner).Error);
        Assert.True(bridge.RevokeChildVirtualIo(bound.Value!).IsSuccess);
        Assert.True(bridge.RevokeDevice(device, owner).IsSuccess);
        Assert.Equal(1, provider.VirtualIoRevokeCalls);
    }

    [Fact]
    public void ChildTransitionCallbackRejectsVirtualIoBindBeforeProvider()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        var device = BindDeviceForModel(bridge, parent, owner, new CapabilityId(9),
            new("device:test"), PlatformDeviceRights.Read).Value!;
        provider.AfterVirtualIoBind = () => Assert.Fail("Virtual-I/O provider callback must not run.");
        provider.BeforeChildTransition = () => Assert.Equal(KernelError.PlatformBindingActive,
            bridge.BindChildVirtualIo(child, device,
                new(PlatformDeviceRights.Read, 512)).Error);

        Assert.True(bridge.TransitionChildDomain(child,
            PlatformChildDomainTransition.BeginDrain).IsSuccess);
    }

    [Theory]
    [InlineData("guest-map")]
    [InlineData("event")]
    [InlineData("trap")]
    public void ChildEffectCallbackRejectsOverlappingTransition(string effect)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        Action probe = () => Assert.Equal(KernelError.PlatformBindingActive,
            bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).Error);
        if (effect == "guest-map")
        {
            var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
                new(new DomainId(1), owner.ProcessGeneration), 4096);
            var parentMapping = bridge.MapOwnedRegion(parent, owner, new CapabilityId(8), region,
                PlatformMemoryAccess.Read).Value!;
            provider.AfterGuestMap = probe;
            Assert.True(bridge.MapChildGuestRegion(child, parentMapping, new(0, 4096),
                PlatformGuestMemoryAccess.Read).IsSuccess);
        }
        else if (effect == "event")
        {
            provider.BeforeChildEvent = probe;
            Assert.True(bridge.InjectChildEvent(child, PlatformVirtualEventClass.Timer, "timer:0").IsSuccess);
        }
        else
        {
            provider.BeforeChildTrap = probe;
            Assert.True(bridge.ObserveChildTrap(child).IsSuccess);
        }
        Assert.Equal(0, provider.TransitionCalls);
        Assert.True(bridge.TransitionChildDomain(child,
            PlatformChildDomainTransition.BeginDrain).IsSuccess);
    }

    [Theory]
    [InlineData("event", false)]
    [InlineData("event", true)]
    [InlineData("trap", false)]
    [InlineData("trap", true)]
    public void VirtualEffectCallbackFaultOrResetCannotPublishLateEvidence(string effect, bool reset)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        Action fault = reset
            ? () => Assert.True(bridge.ObserveBackendReset().IsSuccess)
            : () => throw new InvalidOperationException("Virtual effect receipt lost.");
        if (effect == "event")
        {
            provider.BeforeChildEvent = fault;
            Assert.Equal(KernelError.PlatformFaulted,
                bridge.InjectChildEvent(child, PlatformVirtualEventClass.Timer, "timer:0").Error);
            Assert.Equal(KernelError.PlatformFaulted,
                bridge.InjectChildEvent(child, PlatformVirtualEventClass.Timer, "timer:0").Error);
            Assert.Equal(1, provider.EventCalls);
        }
        else
        {
            provider.BeforeChildTrap = fault;
            Assert.Equal(KernelError.PlatformFaulted, bridge.ObserveChildTrap(child).Error);
            Assert.Equal(KernelError.PlatformFaulted, bridge.ObserveChildTrap(child).Error);
            Assert.Equal(1, provider.TrapCalls);
        }
    }

    [Fact]
    public void EventAndTrapEvidenceAreSequencedAndNeverCloseChildAuthority()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = Subject(1, 10);
        PlatformDomainBinding parent = bridge.BindDomain(owner).Value!;
        PlatformChildBinding child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.Start).IsSuccess);

        Assert.True(bridge.InjectChildEvent(child, PlatformVirtualEventClass.Timer, "timer:0").IsSuccess);
        Assert.True(bridge.ObserveChildTrap(child).IsSuccess);
        Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.Park).IsSuccess);
    }

    [Fact]
    public void AmbiguousChildCloseQuarantinesAndDoesNotPermitRetryOrReclaim()
    {
        var provider = new ChildProvider { MalformedClose = true };
        var bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = Subject(1, 10);
        PlatformDomainBinding parent = bridge.BindDomain(owner).Value!;
        PlatformChildBinding child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).IsSuccess);

        KernelResult close = bridge.CloseChildDomain(child);
        KernelResult retry = bridge.CloseChildDomain(child);

        Assert.Equal(KernelError.PlatformFaulted, close.Error);
        Assert.Equal(KernelError.PlatformFaulted, retry.Error);
        Assert.Equal(1, provider.CloseCalls);
    }

    [Fact]
    public void ReentrantChildCloseCannotInvokeProviderTwice()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        Assert.True(bridge.TransitionChildDomain(child,
            PlatformChildDomainTransition.BeginDrain).IsSuccess);
        provider.BeforeChildClose = () =>
        {
            Assert.Equal(KernelError.PlatformBindingActive, bridge.CloseChildDomain(child).Error);
            Assert.Equal(1, provider.CloseCalls);
            Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(parent, owner).Error);
        };

        Assert.True(bridge.CloseChildDomain(child).IsSuccess);
        Assert.Equal(1, provider.CloseCalls);
        Assert.Equal(KernelError.PlatformBindingRevoked, bridge.CloseChildDomain(child).Error);
    }

    [Fact]
    public void ParentDomainWaitsForExactPublishedChildClosure()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;

        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(parent, owner).Error);
        Assert.Equal(0, provider.RevokeDomainCalls);
        Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).IsSuccess);
        Assert.True(bridge.CloseChildDomain(child).IsSuccess);
        Assert.True(bridge.RevokeDomain(parent, owner).IsSuccess);
    }

    [Fact]
    public void ChildCreateCallbackPinsParentBeforeChildBindingPublication()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        provider.BeforeChildCreateReturn = () =>
        {
            Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(parent, owner).Error);
            Assert.Equal(0, provider.RevokeDomainCalls);
        };

        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        Assert.Equal(1, provider.ChildCreateCalls);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(parent, owner).Error);
        Assert.True(bridge.TransitionChildDomain(child,
            PlatformChildDomainTransition.BeginDrain).IsSuccess);
        Assert.True(bridge.CloseChildDomain(child).IsSuccess);
        Assert.True(bridge.RevokeDomain(parent, owner).IsSuccess);
    }

    [Fact]
    public void ParentRevokeCallbackRejectsChildCreateBeforeProvider()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        provider.BeforeDomainRevoke = () =>
        {
            Assert.Equal(KernelError.PlatformBindingActive,
                bridge.CreateChildDomain(parent, owner, Intent()).Error);
            Assert.Equal(0, provider.ChildCreateCalls);
        };

        Assert.True(bridge.RevokeDomain(parent, owner).IsSuccess);
        Assert.Equal(1, provider.RevokeDomainCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChildCreateReceiptLossOrAmbiguousStatusPinsParent(bool throws)
    {
        var provider = new ChildProvider
        {
            BeforeChildCreateReturn = throws
                ? () => throw new InvalidOperationException("Child created without receipt.")
                : null,
            ChildCreateStatus = throws ? null : PlatformAuthorityStatus.Faulted,
        };
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;

        Assert.False(bridge.CreateChildDomain(parent, owner, Intent()).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(parent, owner).Error);
        Assert.Equal(0, provider.RevokeDomainCalls);
        Assert.Equal(KernelError.PlatformFaulted,
            bridge.CreateChildDomain(parent, owner, Intent()).Error);
    }

    [Fact]
    public void BackendResetPreservesUnknownChildCreatePin()
    {
        var provider = new ChildProvider
        {
            BeforeChildCreateReturn = () => throw new InvalidOperationException("Child created without receipt."),
        };
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        Assert.Equal(KernelError.PlatformFaulted,
            bridge.CreateChildDomain(parent, owner, Intent()).Error);

        Assert.True(bridge.ObserveBackendReset().IsSuccess);
        Assert.True(bridge.TryGetQuarantinedDomainBinding(owner, out var current));
        Assert.Equal(KernelError.StaleGeneration, bridge.RevokeDomain(parent, owner).Error);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(current, owner).Error);
        Assert.Equal(0, provider.RevokeDomainCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetInsideChildCreateCallbackPinsNewParentGeneration(bool terminalEpoch)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        if (terminalEpoch)
            typeof(PlatformAuthorityBridge).GetField("_backendEpoch",
                global::System.Reflection.BindingFlags.Instance |
                global::System.Reflection.BindingFlags.NonPublic)!.SetValue(bridge, ulong.MaxValue);
        var parent = bridge.BindDomain(owner).Value!;
        provider.BeforeChildCreateReturn = () =>
        {
            var reset = bridge.ObserveBackendReset();
            Assert.Equal(terminalEpoch ? KernelError.CapacityExhausted : KernelError.None, reset.Error);
        };

        Assert.Equal(KernelError.PlatformFaulted,
            bridge.CreateChildDomain(parent, owner, Intent()).Error);
        Assert.True(bridge.TryGetQuarantinedDomainBinding(owner, out var current));
        Assert.NotEqual(parent.Generation, current.Generation);
        Assert.Equal(KernelError.StaleGeneration, bridge.RevokeDomain(parent, owner).Error);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(current, owner).Error);
        Assert.Equal(0, provider.RevokeDomainCalls);
        Assert.Equal(1, provider.ChildCreateCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedChildCreateRequiresExactCleanupBeforeParentClose(bool cleanupFails)
    {
        var provider = new ChildProvider
        {
            MalformedCreateParent = true,
            ChildCreateCleanupStatus = cleanupFails ? PlatformAuthorityStatus.Faulted : null,
        };
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;

        Assert.Equal(KernelError.PlatformFaulted,
            bridge.CreateChildDomain(parent, owner, Intent()).Error);
        Assert.Equal(1, provider.CloseCalls);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(parent, owner).Error);
        Assert.Equal(0, provider.RevokeDomainCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void MalformedChildCompensationPinsOriginalBeforeCallbackAndRetainsOnlyReturnedClosureFact(int fault)
    {
        var provider = new ChildProvider { MalformedCreateParent = true, MalformedClose = fault == 4 };
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = Subject(1, 10);
        var parent = bridge.BindDomain(subject).Value!;
        var epoch = bridge.BackendEpoch;
        var pinnedBeforeCallback = false;
        var domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(bridge)!;
        provider.ChildCreateCleanupStatus = fault == 1 ? PlatformAuthorityStatus.Faulted :
            fault == 2 ? PlatformAuthorityStatus.NotAccepted : null;
        provider.BeforeChildClose = () =>
        {
            var original = Assert.Single(domains.Values.Cast<object>());
            pinnedBeforeCallback = (bool)original.GetType().GetProperty("ChildCreateMayHaveEffect")!.GetValue(original)!;
            if (fault == 3) throw new IOException("lost compensation response");
            if (fault == 5) Assert.True(bridge.ObserveBackendReset().IsSuccess);
        };
        Assert.False(bridge.CreateChildDomain(parent, subject, Intent()).IsSuccess);
        Assert.True(pinnedBeforeCallback);
        var current = Assert.Single(domains.Values.Cast<object>());
        Assert.True((bool)current.GetType().GetProperty("ChildCreateMayHaveEffect")!.GetValue(current)!);
        Assert.Equal(0, current.GetType().GetProperty("PendingChildCreates")!.GetValue(current));
        var receipt = Assert.Single(((global::System.Collections.IEnumerable)current.GetType().GetProperty("UnpublishedChildCreates")!.GetValue(current)!).Cast<object>());
        Assert.Equal(parent, receipt.GetType().GetProperty("Parent")!.GetValue(receipt));
        Assert.Equal(epoch, receipt.GetType().GetProperty("AdmissionEpoch")!.GetValue(receipt));
        Assert.Equal(provider.LastChildLease, receipt.GetType().GetProperty("Receipt")!.GetValue(receipt));
        Assert.Equal(fault is 0 or 5, receipt.GetType().GetProperty("ReturnedClosureReceipt")!.GetValue(receipt) is not null);
        provider.BeforeChildClose = null;
        Assert.True(bridge.TryGetQuarantinedDomainBinding(subject, out var retained));
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(retained, subject).Error);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(retained, subject).Error);
        Assert.Equal(0, provider.RevokeDomainCalls);
        Assert.Equal(1, provider.ChildCreateCalls);
        Assert.Equal(1, provider.CloseCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChildCloseCallbackResetOrThrowCannotCloseOrRetry(bool throws)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).IsSuccess);
        provider.BeforeChildClose = throws
            ? () => throw new InvalidOperationException("after possible effect")
            : () => Assert.True(bridge.ObserveBackendReset().IsSuccess);

        var closed = bridge.CloseChildDomain(child);
        var retry = bridge.CloseChildDomain(child);

        Assert.Equal(KernelError.PlatformFaulted, closed.Error);
        Assert.Equal(KernelError.PlatformFaulted, retry.Error);
        Assert.Equal(1, provider.CloseCalls);
    }

    [Fact]
    public void ChildClosureRequiresTerminalGuestAndIoReceipts()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = Subject(1, 10);
        PlatformDomainBinding parent = bridge.BindDomain(owner).Value!;
        PlatformChildBinding child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(new DomainId(1), owner.ProcessGeneration), 4096);
        PlatformRegionMapping parentMapping = bridge.MapOwnedRegion(parent, owner, new CapabilityId(8), region,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        PlatformGuestMapping guest = bridge.MapChildGuestRegion(child, parentMapping,
            new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;
        PlatformDeviceLease device = BindDeviceForModel(bridge, parent, owner, new CapabilityId(9),
            new("device:test"), PlatformDeviceRights.Read).Value!;
        PlatformVirtualIoBinding io = bridge.BindChildVirtualIo(child, device,
            new(PlatformDeviceRights.Read, 512)).Value!;
        Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).IsSuccess);

        Assert.Equal(KernelError.PlatformBindingActive, bridge.CloseChildDomain(child).Error);
        Assert.True(bridge.RevokeChildVirtualIo(io).IsSuccess);
        Assert.True(bridge.UnmapChildGuestRegion(guest).IsSuccess);
        Assert.True(bridge.CloseChildDomain(child).IsSuccess);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GuestMapReceiptLossOrAmbiguousStatusPinsChildAndParentMapping(bool throws)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var root = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(root, owner, Intent()).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(new DomainId(1), owner.ProcessGeneration), 4096);
        var mapping = bridge.MapOwnedRegion(root, owner, new CapabilityId(8), region,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        provider.ThrowAfterGuestMap = throws;
        provider.GuestMapStatus = throws ? null : PlatformAuthorityStatus.Denied;

        Assert.False(bridge.MapChildGuestRegion(child, mapping,
            new(0, 4096), PlatformGuestMemoryAccess.Read).IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted,
            bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).Error);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(root, owner).Error);
        Assert.Equal(0, provider.RevokeDomainCalls);
    }

    [Fact]
    public void ParentMappingRevocationWaitsForExactGuestClosure()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var root = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(root, owner, Intent()).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(new DomainId(1), owner.ProcessGeneration), 4096);
        var parent = bridge.MapOwnedRegion(root, owner, new CapabilityId(8), region,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var guest = bridge.MapChildGuestRegion(child, parent,
            new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;

        Assert.Equal(KernelError.PlatformBindingActive,
            bridge.BeginRegionMappingRevocation(parent, owner, PlatformRegionRevocationPolicy.DrainBeforeRevoke).Error);
        Assert.True(bridge.UnmapChildGuestRegion(guest).IsSuccess);
        Assert.True(bridge.BeginRegionMappingRevocation(parent, owner,
            PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
    }

    [Fact]
    public async Task ParentMappingRevocationWaitsForInFlightGuestAdmission()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var root = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(root, owner, Intent()).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(new DomainId(1), owner.ProcessGeneration), 4096);
        var parent = bridge.MapOwnedRegion(root, owner, new CapabilityId(8), region,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        provider.AfterGuestMap = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        var admission = Task.Run(() => bridge.MapChildGuestRegion(child, parent,
            new(0, 4096), PlatformGuestMemoryAccess.Read));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(KernelError.PlatformBindingActive,
                bridge.BeginRegionMappingRevocation(parent, owner,
                    PlatformRegionRevocationPolicy.DrainBeforeRevoke).Error);
        }
        finally { release.Set(); }
        Assert.True((await admission).IsSuccess);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VirtualIoBindReceiptLossOrAmbiguousStatusPinsChildAndDevice(bool throws)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var root = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(root, owner, Intent()).Value!;
        var device = BindDeviceForModel(bridge, root, owner, new CapabilityId(9),
            new("device:test"), PlatformDeviceRights.Read).Value!;
        provider.ThrowAfterVirtualIoBind = throws;
        provider.VirtualIoBindStatus = throws ? null : PlatformAuthorityStatus.Denied;

        Assert.False(bridge.BindChildVirtualIo(child, device,
            new(PlatformDeviceRights.Read, 512)).IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, bridge.RevokeDevice(device, owner).Error);
        Assert.Equal(KernelError.PlatformFaulted,
            bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).Error);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(root, owner).Error);
        Assert.Equal(0, provider.RevokeDomainCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BackendResetDuringGuestOrVirtualIoAdmissionCannotPublishChild(bool virtualIo)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var root = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(root, owner, Intent()).Value!;
        provider.AfterGuestMap = virtualIo ? null : () => Assert.True(bridge.ObserveBackendReset().IsSuccess);
        provider.AfterVirtualIoBind = virtualIo ? () => Assert.True(bridge.ObserveBackendReset().IsSuccess) : null;

        if (virtualIo)
        {
            var device = BindDeviceForModel(bridge, root, owner, new CapabilityId(9),
                new("device:test"), PlatformDeviceRights.Read).Value!;
            Assert.Equal(KernelError.PlatformFaulted,
                bridge.BindChildVirtualIo(child, device,
                    new(PlatformDeviceRights.Read, 512), out var known).Error);
            Assert.NotNull(known);
            Assert.Equal(KernelError.PlatformFaulted, bridge.RevokeChildVirtualIo(known!.Value).Error);
            Assert.Equal(KernelError.StaleGeneration, bridge.RevokeDevice(device, owner).Error);
        }
        else
        {
            var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
                new(new DomainId(1), owner.ProcessGeneration), 4096);
            var mapping = bridge.MapOwnedRegion(root, owner, new CapabilityId(8), region,
                PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
            Assert.Equal(KernelError.PlatformFaulted,
                bridge.MapChildGuestRegion(child, mapping,
                    new(0, 4096), PlatformGuestMemoryAccess.Read).Error);
        }
        Assert.Equal(KernelError.PlatformFaulted,
            bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).Error);
        Assert.Equal(0, provider.RevokeDomainCalls);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void MalformedGuestOrVirtualIoAdmissionNeedsExactCompensation(
        bool virtualIo, bool cleanupFails)
    {
        var provider = new ChildProvider
        {
            MalformedGuestMap = !virtualIo,
            MalformedVirtualIoBind = virtualIo,
            GuestUnmapStatus = !virtualIo && cleanupFails ? PlatformAuthorityStatus.Faulted : null,
            VirtualIoRevokeStatus = virtualIo && cleanupFails ? PlatformAuthorityStatus.Faulted : null,
        };
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var root = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(root, owner, Intent()).Value!;

        if (virtualIo)
        {
            var device = BindDeviceForModel(bridge, root, owner, new CapabilityId(9),
                new("device:test"), PlatformDeviceRights.Read).Value!;
            Assert.Equal(KernelError.PlatformFaulted,
                bridge.BindChildVirtualIo(child, device,
                    new(PlatformDeviceRights.Read, 512)).Error);
            Assert.Equal(1, provider.VirtualIoRevokeCalls);
            Assert.Equal(KernelError.PlatformFaulted, bridge.RevokeDevice(device, owner).Error);
        }
        else
        {
            var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
                new(new DomainId(1), owner.ProcessGeneration), 4096);
            var mapping = bridge.MapOwnedRegion(root, owner, new CapabilityId(8), region,
                PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
            Assert.Equal(KernelError.PlatformFaulted,
                bridge.MapChildGuestRegion(child, mapping,
                    new(0, 4096), PlatformGuestMemoryAccess.Read).Error);
            Assert.Equal(0, provider.GuestUnmapCalls); // A malformed returned lease is not the original cleanup target.
            Assert.Equal(KernelError.PlatformFaulted, bridge.ValidateMapping(mapping, owner).Error);
        }

        Assert.Equal(KernelError.PlatformFaulted,
            bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ParentAuthorizationRevokedInsideChildAdmissionPinsPossibleEffect(bool virtualIo)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var root = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(root, owner, Intent()).Value!;

        if (virtualIo)
        {
            var device = BindDeviceForModel(bridge, root, owner, new CapabilityId(9),
                new("device:test"), PlatformDeviceRights.Read).Value!;
            provider.AfterVirtualIoBind = () =>
                Assert.Single(bridge.BeginDeviceCapabilityRevocation(new CapabilityId(9), static id => id == new CapabilityId(9)));

            Assert.Equal(KernelError.PlatformFaulted,
                bridge.BindChildVirtualIo(child, device,
                    new(PlatformDeviceRights.Read, 512)).Error);
            Assert.Equal(KernelError.PlatformFaulted, bridge.RevokeDevice(device, owner).Error);
        }
        else
        {
            var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
                new(new DomainId(1), owner.ProcessGeneration), 4096);
            var mapping = bridge.MapOwnedRegion(root, owner, new CapabilityId(8), region,
                PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
            provider.AfterGuestMap = () =>
                Assert.Single(bridge.BeginCapabilityRevocation(new CapabilityId(8), static id => id == new CapabilityId(8)));

            Assert.Equal(KernelError.PlatformFaulted,
                bridge.MapChildGuestRegion(child, mapping,
                    new(0, 4096), PlatformGuestMemoryAccess.Read).Error);
            Assert.Equal(KernelError.PlatformBindingRevoked, bridge.ValidateMapping(mapping, owner).Error);
        }

        Assert.Equal(KernelError.PlatformFaulted,
            bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).Error);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(root, owner).Error);
        Assert.Equal(0, provider.RevokeDomainCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProviderIncarnationDriftInsideChildAdmissionPinsParent(bool virtualIo)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var root = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(root, owner, Intent()).Value!;

        if (virtualIo)
        {
            var device = BindDeviceForModel(bridge, root, owner, new CapabilityId(9),
                new("device:test"), PlatformDeviceRights.Read).Value!;
            provider.AfterVirtualIoBind = () => provider.CurrentIncarnation = new(2);
            Assert.Equal(KernelError.PlatformFaulted,
                bridge.BindChildVirtualIo(child, device,
                    new(PlatformDeviceRights.Read, 512)).Error);
            Assert.Equal(KernelError.PlatformFaulted, bridge.RevokeDevice(device, owner).Error);
        }
        else
        {
            var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
                new(new DomainId(1), owner.ProcessGeneration), 4096);
            var mapping = bridge.MapOwnedRegion(root, owner, new CapabilityId(8), region,
                PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
            provider.AfterGuestMap = () => provider.CurrentIncarnation = new(2);
            Assert.Equal(KernelError.PlatformFaulted,
                bridge.MapChildGuestRegion(child, mapping,
                    new(0, 4096), PlatformGuestMemoryAccess.Read).Error);
            Assert.Equal(KernelError.PlatformFaulted, bridge.ValidateMapping(mapping, owner).Error);
        }

        Assert.Equal(KernelError.PlatformFaulted,
            bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).Error);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(root, owner).Error);
        Assert.Equal(0, provider.RevokeDomainCalls);
    }

    [Fact]
    public void BackendResetQuarantinedVirtualIoCannotInvokeLateProviderRevoke()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        var device = BindDeviceForModel(bridge, parent, owner, new CapabilityId(9),
            new("device:test"), PlatformDeviceRights.Read).Value!;
        var io = bridge.BindChildVirtualIo(child, device,
            new(PlatformDeviceRights.Read, 512)).Value!;

        Assert.True(bridge.ObserveBackendReset().IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, bridge.RevokeChildVirtualIo(io).Error);
        Assert.Equal(0, provider.VirtualIoRevokeCalls);
        Assert.Equal(KernelError.PlatformFaulted, bridge.RevokeChildVirtualIo(io).Error);
        Assert.Equal(0, provider.VirtualIoRevokeCalls);
    }

    [Fact]
    public void ReentrantVirtualIoRevokeCannotEnterProviderTwice()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        var device = BindDeviceForModel(bridge, parent, owner, new CapabilityId(9),
            new("device:test"), PlatformDeviceRights.Read).Value!;
        var io = bridge.BindChildVirtualIo(child, device,
            new(PlatformDeviceRights.Read, 512)).Value!;
        provider.BeforeVirtualIoRevoke = () =>
        {
            Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeChildVirtualIo(io).Error);
            Assert.Equal(1, provider.VirtualIoRevokeCalls);
        };

        Assert.True(bridge.RevokeChildVirtualIo(io).IsSuccess);
        Assert.Equal(1, provider.VirtualIoRevokeCalls);
        Assert.True(bridge.RevokeChildVirtualIo(io).IsSuccess);
        Assert.Equal(1, provider.VirtualIoRevokeCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BackendResetInsideClosureCallbackCannotTurnLateReceiptIntoClosure(bool virtualIo)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(new DomainId(1), owner.ProcessGeneration), 4096);
        var parentMapping = bridge.MapOwnedRegion(parent, owner, new CapabilityId(8), region,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var guest = bridge.MapChildGuestRegion(child, parentMapping,
            new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;
        var device = BindDeviceForModel(bridge, parent, owner, new CapabilityId(9),
            new("device:test"), PlatformDeviceRights.Read).Value!;
        var io = bridge.BindChildVirtualIo(child, device,
            new(PlatformDeviceRights.Read, 512)).Value!;
        if (virtualIo) provider.BeforeVirtualIoRevoke = () => Assert.True(bridge.ObserveBackendReset().IsSuccess);
        else provider.BeforeGuestUnmap = () => Assert.True(bridge.ObserveBackendReset().IsSuccess);

        var close = virtualIo ? bridge.RevokeChildVirtualIo(io) : bridge.UnmapChildGuestRegion(guest);

        Assert.Equal(KernelError.PlatformFaulted, close.Error);
        Assert.Equal(KernelError.PlatformFaulted, bridge.RevokeChildVirtualIo(io).Error);
        Assert.Equal(KernelError.PlatformFaulted, bridge.UnmapChildGuestRegion(guest).Error);
        Assert.Equal(virtualIo ? 1 : 0, provider.VirtualIoRevokeCalls);
        Assert.Equal(virtualIo ? 0 : 1, provider.GuestUnmapCalls);
        if (!virtualIo)
        {
            var guests = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_guestBindings",
                global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(bridge)!;
            var record = guests.Values.Cast<object>().Single();
            var fact = record.GetType().GetProperty("LastClosureReceipt")!.GetValue(record);
            Assert.NotNull(fact);
            Assert.Equal(record.GetType().GetProperty("ProviderLease")!.GetValue(record),
                fact.GetType().GetProperty("AdmittedLease")!.GetValue(fact));
            Assert.NotEqual(bridge.BackendEpoch, fact.GetType().GetProperty("AdmissionEpoch")!.GetValue(fact));
            Assert.Equal(KernelError.PlatformFaulted, bridge.UnmapChildGuestRegion(guest, allowCommittedClosure: true).Error);
        }
        else
        {
            var bindings = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_virtualIoBindings",
                global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(bridge)!;
            var record = bindings.Values.Cast<object>().Single();
            var fact = record.GetType().GetProperty("LastClosureReceipt")!.GetValue(record);
            Assert.NotNull(fact);
            Assert.Equal(record.GetType().GetProperty("ProviderLease")!.GetValue(record),
                fact.GetType().GetProperty("AdmittedLease")!.GetValue(fact));
            Assert.NotEqual(bridge.BackendEpoch, fact.GetType().GetProperty("AdmissionEpoch")!.GetValue(fact));
            Assert.NotNull(fact.GetType().GetProperty("Receipt")!.GetValue(fact));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClosureCallbackExceptionPinsGuestOrVirtualIoWithoutRetry(bool virtualIo)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(new DomainId(1), owner.ProcessGeneration), 4096);
        var mapping = bridge.MapOwnedRegion(parent, owner, new CapabilityId(8), region,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var guest = bridge.MapChildGuestRegion(child, mapping,
            new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;
        var device = BindDeviceForModel(bridge, parent, owner, new CapabilityId(9),
            new("device:test"), PlatformDeviceRights.Read).Value!;
        var io = bridge.BindChildVirtualIo(child, device,
            new(PlatformDeviceRights.Read, 512)).Value!;
        if (virtualIo) provider.BeforeVirtualIoRevoke = () => throw new InvalidOperationException("after effect");
        else provider.BeforeGuestUnmap = () => throw new InvalidOperationException("after effect");

        var first = virtualIo ? bridge.RevokeChildVirtualIo(io) : bridge.UnmapChildGuestRegion(guest);
        var retry = virtualIo ? bridge.RevokeChildVirtualIo(io) : bridge.UnmapChildGuestRegion(guest);

        Assert.Equal(KernelError.PlatformFaulted, first.Error);
        Assert.Equal(KernelError.PlatformFaulted, retry.Error);
        Assert.Equal(virtualIo ? 1 : 0, provider.VirtualIoRevokeCalls);
        Assert.Equal(virtualIo ? 0 : 1, provider.GuestUnmapCalls);
        if (!virtualIo)
        {
            var guests = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_guestBindings",
                global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(bridge)!;
            var record = guests.Values.Cast<object>().Single();
            Assert.Null(record.GetType().GetProperty("LastClosureReceipt")!.GetValue(record));
        }
        else
        {
            var bindings = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_virtualIoBindings",
                global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(bridge)!;
            var record = bindings.Values.Cast<object>().Single();
            Assert.Null(record.GetType().GetProperty("LastClosureReceipt")!.GetValue(record));
        }
    }

    [Fact]
    public void StaleRuntimeVmGenerationMakesZeroChildProviderCalls()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var owner = TestFixtures.Create(kernel, 6, 60).Handle;
        CapabilityId create = kernel.MintCapability(new DomainId(60), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet authority = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).IsSuccess);
        var stale = authority.Domain with { Generation = new(authority.Domain.Generation.Value + 1) };

        KernelResult start = kernel.StartVirtualDomain(owner, stale, authority.ExecuteCapability);

        Assert.Equal(KernelError.StaleGeneration, start.Error);
        Assert.Equal(0, provider.TransitionCalls);
    }

    [Fact]
    public void RuntimeKernelClosesChildAndParentGuestMappingBeforeLocalReclaim()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 7, 70);
        CapabilityId create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet authority = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).IsSuccess);
        var region = kernel.AllocateRegion(owner, 4096).Value!;
        CapabilityId regionCapability = kernel.MintCapability(process.DomainId, owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        GuestRegionMapping mapping = kernel.MapGuestRegion(owner, authority.Domain, authority.MemoryCapability,
            regionCapability, region.Handle, new(0, 4096), GuestMemoryAccess.Read | GuestMemoryAccess.Write).Value!;

        Assert.True(kernel.DestroyVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).IsSuccess);
        Assert.True(kernel.ReleaseRegion(owner, region).IsSuccess);
        Assert.Equal(1, provider.GuestUnmapCalls);
        Assert.Equal(1, provider.RegionRevocationBeginCalls);
        Assert.Equal(1, provider.CompletionCalls);
        Assert.Equal(1, provider.RevokeDomainCalls);
    }

    [Fact]
    public void ProcessTeardownUsesDeterministicChildGuestParentClosureOrder()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 8, 80);
        CapabilityId create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet authority = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).IsSuccess);
        var region = kernel.AllocateRegion(owner, 4096).Value!;
        CapabilityId regionCapability = kernel.MintCapability(process.DomainId, owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        Assert.True(kernel.MapGuestRegion(owner, authority.Domain, authority.MemoryCapability, regionCapability,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read).IsSuccess);

        provider.CallLog.Clear();
        KernelResult terminate = kernel.TerminateProcess(owner);

        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.Equal(new[] { "guest-unmap", "parent-map-begin", "parent-map-observe", "child-drain", "child-close", "parent-close" },
            provider.CallLog);
    }

    [Fact]
    public void BackendResetFaultPinsChildAndGuestLedgersBeforeAnyFurtherProviderCall()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = Subject(9, 90);
        PlatformDomainBinding parent = bridge.BindDomain(owner).Value!;
        PlatformChildBinding child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(9), new RegionGeneration(1)),
            new(new DomainId(9), owner.ProcessGeneration), 4096);
        PlatformRegionMapping parentMapping = bridge.MapOwnedRegion(parent, owner, new CapabilityId(99), region,
            PlatformMemoryAccess.Read).Value!;
        PlatformGuestMapping guest = bridge.MapChildGuestRegion(child, parentMapping,
            new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;
        int transitions = provider.TransitionCalls;
        int unmaps = provider.GuestUnmapCalls;

        var reset = bridge.ObserveBackendReset();
        KernelResult transition = bridge.TransitionChildDomain(child, PlatformChildDomainTransition.Start);
        KernelResult unmap = bridge.UnmapChildGuestRegion(guest);

        Assert.True(reset.IsSuccess, reset.Message);
        Assert.Equal(1, reset.Value!.InvalidatedVirtualDomains);
        Assert.Equal(KernelError.PlatformFaulted, transition.Error);
        Assert.Equal(KernelError.PlatformFaulted, unmap.Error);
        Assert.Equal(transitions, provider.TransitionCalls);
        Assert.Equal(unmaps, provider.GuestUnmapCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public async Task VirtualIoGuestEventRequiresExactPublishedOperation(int fault)
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 10, 100);
        var create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var authority = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).IsSuccess);

        var parent = new PlatformDomainBinding(new(1), new(1),
            new(process.DomainId, owner));
        var deviceCapability = kernel.MintCapability(process.DomainId, owner, ResourceKind.Device,
            "device:virtual-io-event", CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!;
        var device = kernel.BindPlatformDevice(owner, parent, deviceCapability.CapabilityId,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write).Value!;
        var virtualIoResult = kernel.BindVirtualIo(owner, authority.Domain, device,
            new(PlatformDeviceRights.Read | PlatformDeviceRights.Write, 4096));
        Assert.True(virtualIoResult.IsSuccess, virtualIoResult.Message);
        var virtualIo = virtualIoResult.Value;
        var currentIo = kernel.RevalidateVirtualIo(owner, virtualIo);
        Assert.True(currentIo.IsSuccess, currentIo.Message);

        var input = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var output = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var dependencies = new OperationDependencySnapshot(1, 1, 1, 1);
        var prepared = kernel.PrepareExternalOperation(owner,
            [new(input.Handle, RegionUseMode.ReadOnly, new(0, 8)),
             new(output.Handle, RegionUseMode.StagedOutput, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!;
        Assert.True(kernel.AdmitExternalOperation(owner, prepared.Operation, dependencies).IsSuccess);
        var submitted = kernel.RecordExternalOperationSubmission(owner, prepared.Operation, dependencies).Value!;
        var endpoint = kernel.CreateKernelEventEndpoint(owner).Value!;

        Assert.Equal(KernelError.InvalidTransition,
            kernel.PublishVirtualIoEvent(owner, virtualIo, prepared.Operation,
                authority.EventCapability, endpoint).Error);
        Assert.Equal(0, provider.EventCalls);

        Assert.True(kernel.RecordExternalOperationCompletion(owner,
            new(submitted, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        Assert.True(kernel.RecordExternalOperationVisibility(owner,
            new(submitted, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        Assert.True(kernel.PublishExternalOperation(owner, prepared.Operation, dependencies,
            new(ExternalPublicationPolicy.Staged), () => input.Span.CopyTo(output.Span)).IsSuccess);
        using var cancellation = new CancellationTokenSource();
        var waiting = kernel.WaitForKernelEventAsync(owner, endpoint, cancellation.Token).AsTask();
        if (fault == 1) Assert.True(kernel.CapabilityAuthority.Revoke(deviceCapability.CapabilityId).IsSuccess);
        provider.BeforeChildEvent = () =>
        {
            Assert.Equal(KernelError.PlatformBindingActive, kernel.CloseVirtualIo(virtualIo).Error);
            if (fault == 2) Assert.True(kernel.CapabilityAuthority.Revoke(deviceCapability.CapabilityId).IsSuccess);
            if (fault == 3) Assert.Equal(KernelError.PlatformBindingActive,
                kernel.RevokeCapability(deviceCapability.CapabilityId).Error);
        };
        object? ioOwner = null;
        if (fault == 5)
        {
            var records = (global::System.Collections.IDictionary)IoField(kernel, "_virtualIo");
            ioOwner = records[virtualIo.Id]!;
            ioOwner.GetType().GetProperty("PendingEvents", global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.SetValue(ioOwner, int.MaxValue);
        }
        var published = kernel.PublishVirtualIoEvent(owner, virtualIo, prepared.Operation,
            authority.EventCapability, endpoint);
        if (fault is 1 or 2 or 3 or 5)
        {
            Assert.False(published.IsSuccess);
            Assert.False(waiting.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        else
        {
            Assert.True(published.IsSuccess, published.Message);
            Assert.True((await waiting).IsSuccess);
        }
        Assert.Equal(fault is 1 or 5 ? 0 : 1, provider.EventCalls);
        if (fault == 5)
        {
            Assert.Equal(KernelError.CapacityExhausted, published.Error);
            var pending = ioOwner!.GetType().GetProperty("PendingEvents", global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!;
            Assert.Equal(int.MaxValue, pending.GetValue(ioOwner));
            pending.SetValue(ioOwner, 0);
        }
        if (fault is 1 or 2 or 3 or 5)
        {
            // Independent Events authority may use the endpoint after the refused I/O event.
            provider.BeforeChildEvent = null;
            Assert.True(kernel.InjectVirtualEvent(owner, authority.Domain, authority.EventCapability, endpoint).IsSuccess);
            Assert.True((await kernel.WaitForKernelEventAsync(owner, endpoint)).IsSuccess);
        }
        if (fault == 3) Assert.True(kernel.RevokeCapability(deviceCapability.CapabilityId).IsSuccess);
        else Assert.True(kernel.CloseVirtualIo(virtualIo).IsSuccess);
        Assert.Equal(1, provider.VirtualIoRevokeCalls);
        Assert.Equal(input.Span.ToArray(), output.Span.ToArray());
    }

    private static PlatformDomainIdentity Subject(ulong domain, ulong process) =>
        new(new DomainId(domain), new(new ProcessId(process), 1));

    private static PlatformChildDomainIntent Intent() => new(
        new(1, 4096),
        new(PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.Execution |
                PlatformChildAuthorityClass.GuestMemory | PlatformChildAuthorityClass.Events |
                PlatformChildAuthorityClass.Traps | PlatformChildAuthorityClass.Io,
            PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.Execution |
                PlatformChildAuthorityClass.GuestMemory | PlatformChildAuthorityClass.Events |
                PlatformChildAuthorityClass.Traps | PlatformChildAuthorityClass.Io));

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)]
    public async Task KernelChildTransitionRetainsPendingThroughFreshCommit(int step, bool stop)
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 975, 9750);
        var source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var created = kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, created.Domain, created.ConfigureCapability).IsSuccess);
        if (step > 0) Assert.True(kernel.StartVirtualDomain(owner, created.Domain, created.ExecuteCapability).IsSuccess);
        if (step > 1) Assert.True(kernel.ParkVirtualDomain(owner, created.Domain, created.ExecuteCapability).IsSuccess);
        KernelResult Run() => step switch
        {
            0 => kernel.StartVirtualDomain(owner, created.Domain, created.ExecuteCapability),
            1 => kernel.ParkVirtualDomain(owner, created.Domain, created.ExecuteCapability),
            _ => kernel.ResumeVirtualDomain(owner, created.Domain, created.ExecuteCapability),
        };
        var local = (VirtualDomainAuthority)typeof(RuntimeKernel).GetField("_virtualDomains",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(kernel)!;
        var record = Assert.Single(local.ForOwner(owner));
        var child = record.ChildBinding!.Value;
        int before = provider.TransitionCalls;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        provider.BeforeChildTransition = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); };
        var work = Task.Run(Run);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(KernelError.PlatformBindingActive, Run().Error);
            Assert.Equal(KernelError.PlatformBindingActive, kernel.PlatformAuthority.CloseChildDomain(child).Error);
            Assert.Equal(KernelError.PlatformBindingActive,
                kernel.PlatformAuthority.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).Error);
            if (stop)
            {
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
            }
            else Assert.True(kernel.RevokeCapability(created.ExecuteCapability).IsSuccess);
            Assert.Equal(0, provider.CloseCalls);
        }
        finally { release.Set(); }
        var result = await work.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(result.IsSuccess);
        if (!stop) Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        Assert.NotEqual(step == 1 ? VirtualDomainState.Parked : VirtualDomainState.Running, record.State);
        Assert.False(Run().IsSuccess);
        Assert.Equal(before + 1, provider.TransitionCalls);
        provider.BeforeChildTransition = null;
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(VirtualDomainState.Closed, record.State);
        Assert.Equal(1, provider.CloseCalls);
        Assert.Equal(before + 2, provider.TransitionCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void KernelChildUnknownTransitionPinsExistingOwnerAndBalancesPending(int fault)
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 976, 9760);
        var source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var created = kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, created.Domain, created.ConfigureCapability).IsSuccess);
        if (fault == 0) provider.BeforeChildTransition = () => throw new InvalidOperationException("lost child transition result");
        if (fault == 1) provider.BeforeChildTransition = () => Assert.True(kernel.ObservePlatformBackendReset().IsSuccess);
        if (fault == 2) provider.ChildTransitionStatus = PlatformAuthorityStatus.Denied;
        Assert.False(kernel.StartVirtualDomain(owner, created.Domain, created.ExecuteCapability).IsSuccess);
        provider.BeforeChildTransition = null;
        provider.ChildTransitionStatus = null;
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_childBindings",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!;
        foreach (var value in records.Values)
        {
            Assert.False((bool)value!.GetType().GetProperty("TransitionInFlight")!.GetValue(value)!);
            Assert.Equal(PlatformChildDomainState.Faulted, value.GetType().GetProperty("State")!.GetValue(value));
        }
        Assert.False(kernel.StartVirtualDomain(owner, created.Domain, created.ExecuteCapability).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        Assert.Equal(1, provider.TransitionCalls);
        Assert.Equal(0, provider.CloseCalls);
    }

    [Fact]
    public void ChildConfigureAndStartRequireFreshSourceAndActiveParent()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 977, 9770);
        var source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var created = kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Value!;
        Assert.True(kernel.RevokeCapability(created.ConfigureCapability).IsSuccess);
        Assert.Equal(KernelError.CapabilityRevoked,
            kernel.ConfigureVirtualDomain(owner, created.Domain, created.ConfigureCapability).Error);
        Assert.Equal(VirtualDomainState.Created, kernel.QueryVirtualDomain(owner, created.Domain).Value);
        Assert.Equal(0, provider.TransitionCalls);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);

        var bridge = new PlatformAuthorityBridge(provider);
        var subject = Subject(978, 9780);
        var parent = bridge.BindDomain(subject).Value!;
        var child = bridge.CreateChildDomain(parent, subject, Intent()).Value!;
        int before = provider.TransitionCalls;
        Assert.False(bridge.TransitionChildDomain(child, (PlatformChildDomainTransition)999).IsSuccess);
        Assert.Equal(before, provider.TransitionCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void MalformedVirtualIoReturnedClosureCannotReclaimOriginalDeviceOrProcess(int fault)
    {
        var provider = new ChildProvider { MalformedVirtualIoBind = true };
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 978, 9780);
        var source = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var created = kernel.CreateVirtualDomain(owner, source, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, created.Domain, created.ConfigureCapability).IsSuccess);
        var root = new PlatformDomainBinding(new(1), new(1), new(process.DomainId, owner));
        var cap = kernel.MintCapability(process.DomainId, owner, ResourceKind.Device,
            "device:malformed-original", CapabilityRights.Read | CapabilityRights.Configure).Value!.CapabilityId;
        var device = kernel.BindPlatformDevice(owner, root, cap, PlatformDeviceRights.Read).Value!;
        var region = kernel.AllocateRegion(owner, 4096).Value!;
        object Field(object obj, string name) => obj.GetType().GetField(name,
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(obj)!;
        var devices = (global::System.Collections.IDictionary)Field(kernel.PlatformAuthority, "_deviceLeases");
        var original = Assert.Single(devices.Values.Cast<object>());
        if (fault == 2) provider.VirtualIoClosureTransform = receipt => receipt with { Generation = new(receipt.Generation.Value + 1) };
        provider.BeforeVirtualIoRevoke = () =>
        {
            Assert.False(global::System.Threading.Monitor.IsEntered(Field(kernel.PlatformAuthority, "_secureDomainLifecycleGate")));
            Assert.True((bool)original.GetType().GetProperty("FaultPinned")!.GetValue(original)!);
            Assert.Single(((global::System.Collections.IEnumerable)original.GetType().GetProperty("UnpublishedVirtualIoBinds")!.GetValue(original)!).Cast<object>());
            Assert.False(kernel.RevokePlatformDevice(owner, device).IsSuccess);
            if (fault == 1) throw new IOException("returned closure lost");
            if (fault == 3) Assert.True(kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault == 4) provider.CurrentIncarnation = new(2);
            if (fault == 5) Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        };
        Assert.False(kernel.BindVirtualIo(owner, created.Domain, device, new(PlatformDeviceRights.Read, 512)).IsSuccess);
        provider.BeforeVirtualIoRevoke = null;
        var fact = Assert.Single(((global::System.Collections.IEnumerable)original.GetType().GetProperty("UnpublishedVirtualIoBinds")!.GetValue(original)!).Cast<object>());
        Assert.Equal(device, fact.GetType().GetProperty("ParentDevice")!.GetValue(fact));
        Assert.Equal(fault is not (1 or 2), fact.GetType().GetProperty("ReturnedClosureReceipt")!.GetValue(fact) is not null);
        Assert.Equal(0, original.GetType().GetProperty("PendingVirtualIoBinds")!.GetValue(original));
        Assert.False(kernel.RevokePlatformDevice(owner, device).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
        Assert.Equal(1, provider.VirtualIoRevokeCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(0, provider.CloseCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void GuestMapSourceCallbacksRecheckPermissionAndRetainActualKnownCleanup(int fault)
    {
        var provider = new ChildProvider(); var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 979, 9790);
        var create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var domain = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        var region = kernel.AllocateRegion(owner, 4096).Value!;
        var cap = kernel.MintCapability(process.DomainId, owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        object Field(string name) => typeof(PlatformAuthorityBridge).GetField(name,
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!;
        if (fault is 0 or 1)
            provider.BeforeIncarnationRead = () =>
            {
                if (((global::System.Collections.IDictionary)Field("_mappings")).Count == 0) return;
                provider.BeforeIncarnationRead = null;
                Assert.False(Monitor.IsEntered(Field("_secureDomainLifecycleGate")));
                if (fault == 0) Assert.True(kernel.RevokeCapability(domain.MemoryCapability).IsSuccess);
                else throw new IOException("pre guest source unavailable");
            };
        provider.AfterGuestMap = () => provider.BeforeIncarnationRead = () =>
        {
            provider.BeforeIncarnationRead = null;
            Assert.Single(((global::System.Collections.IDictionary)Field("_guestBindings")).Values.Cast<object>());
            if (fault == 2) throw new IOException("known guest source unavailable");
            if (fault == 3) provider.CurrentIncarnation = new(2);
            if (fault == 4) Assert.True(kernel.RevokeCapability(domain.MemoryCapability).IsSuccess);
        };
        Assert.False(kernel.MapGuestRegion(owner, domain.Domain, domain.MemoryCapability, cap,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read).IsSuccess);
        provider.BeforeIncarnationRead = null; provider.AfterGuestMap = null;
        Assert.Equal(fault < 2 ? 0 : 1, provider.GuestMapCalls);
        if (fault == 3)
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
            Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
            Assert.Equal(0, provider.GuestUnmapCalls);
        }
        else
        {
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
            Assert.Equal(fault < 2 ? 0 : 1, provider.GuestUnmapCalls);
        }
    }

    [Fact]
    public async Task GuestMapSourcePauseKeepsOwnersAvailableAndStopWinsBeforeInvocation()
    {
        var provider = new ChildProvider(); var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 980, 9800);
        var create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var domain = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        var region = kernel.AllocateRegion(owner, 4096).Value!;
        var cap = kernel.MintCapability(process.DomainId, owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        provider.BeforeIncarnationRead = () =>
        {
            var maps = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_mappings",
                global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!;
            if (maps.Count == 0) return;
            provider.BeforeIncarnationRead = null;
            entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(30)));
        };
        var work = Task.Run(() => kernel.MapGuestRegion(owner, domain.Domain, domain.MemoryCapability, cap,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True((await Task.Run(() => kernel.Processes.Resolve(owner)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.False((await Task.Run(() => kernel.TerminateProcess(owner)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.Equal(0, provider.GuestMapCalls);
        }
        finally { release.Set(); }
        Assert.False((await work.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        Assert.Equal(0, provider.GuestMapCalls);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void GuestIdentityTerminalRefusalHasNoProviderEffectAndReclaimsActualBacking(bool zero)
    {
        var provider = new ChildProvider(); var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 981, 9810);
        var create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var domain = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        var region = kernel.AllocateRegion(owner, 4096).Value!;
        var cap = kernel.MintCapability(process.DomainId, owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        ulong terminal = zero ? 0 : ulong.MaxValue;
        var counter = typeof(PlatformAuthorityBridge).GetField("_nextGuestBindingId",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!;
        counter.SetValue(kernel.PlatformAuthority, terminal);
        Assert.Equal(KernelError.CapacityExhausted, kernel.MapGuestRegion(owner, domain.Domain, domain.MemoryCapability,
            cap, region.Handle, new(0, 4096), GuestMemoryAccess.Read).Error);
        Assert.Equal(terminal, counter.GetValue(kernel.PlatformAuthority));
        Assert.Equal(0, provider.GuestMapCalls);
        Assert.Equal(0, provider.GuestUnmapCalls);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LastGuestIdentityIsReservedBeforeReplyAndNeverRecycled(bool notAccepted)
    {
        var provider = new ChildProvider { GuestMapStatus = notAccepted ? PlatformAuthorityStatus.NotAccepted : null };
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = Subject(1, 10); var root = bridge.BindDomain(subject).Value!;
        var child = bridge.CreateChildDomain(root, subject, Intent()).Value!;
        PlatformRegionMapping Parent(ulong id) => bridge.MapOwnedRegion(root, subject, new CapabilityId(id),
            new(new(new RegionId(id), new RegionGeneration(1)), new(subject.DomainId, subject.ProcessGeneration), 4096),
            PlatformMemoryAccess.Read).Value!;
        var firstParent = Parent(700); var secondParent = Parent(701);
        var counter = typeof(PlatformAuthorityBridge).GetField("_nextGuestBindingId",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!;
        counter.SetValue(bridge, ulong.MaxValue - 1);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        provider.AfterGuestMap = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(30))); };
        var work = Task.Run(() => bridge.MapChildGuestRegion(child, firstParent, new(0, 4096), PlatformGuestMemoryAccess.Read));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(ulong.MaxValue, counter.GetValue(bridge));
            var loser = await Task.Run(() => bridge.MapChildGuestRegion(child, secondParent, new(0, 4096), PlatformGuestMemoryAccess.Read)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(KernelError.CapacityExhausted, loser.Error);
            Assert.Equal(1, provider.GuestMapCalls);
        }
        finally { release.Set(); }
        var result = await work.WaitAsync(TimeSpan.FromSeconds(10));
        provider.AfterGuestMap = null;
        Assert.Equal(!notAccepted, result.IsSuccess);
        if (!notAccepted)
        {
            Assert.Equal(ulong.MaxValue - 1, result.Value!.MappingId.Value);
            Assert.True(bridge.UnmapChildGuestRegion(result.Value).IsSuccess);
        }
        Assert.Equal(ulong.MaxValue, counter.GetValue(bridge));
        Assert.Equal(KernelError.CapacityExhausted, bridge.MapChildGuestRegion(child, secondParent, new(0, 4096), PlatformGuestMemoryAccess.Read).Error);
        Assert.Equal(1, provider.GuestMapCalls);
        Assert.Equal(notAccepted ? 0 : 1, provider.GuestUnmapCalls);
        Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).IsSuccess);
        Assert.True(bridge.CloseChildDomain(child).IsSuccess);
    }

    [Fact]
    public void ChildCompletionInterlockCannotCloseBetweenProviderAndLocalCommit()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = Subject(979, 9790);
        var parent = bridge.BindDomain(subject).Value!;
        var child = bridge.CreateChildDomain(parent, subject, Intent()).Value!;
        Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.Start,
            completionConsumer: commit =>
            {
                Assert.Equal(KernelError.PlatformBindingActive, bridge.CloseChildDomain(child).Error);
                Assert.Equal(KernelError.PlatformBindingActive,
                    bridge.TransitionChildDomain(child, PlatformChildDomainTransition.Park).Error);
                return commit(() => KernelResult.Ok());
            }).IsSuccess);
        Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).IsSuccess);
        Assert.True(bridge.CloseChildDomain(child).IsSuccess);
        Assert.Equal(1, provider.CloseCalls);
    }

    private sealed class ChildProvider : IPlatformAuthorityProvider, IPlatformFeatureProvider,
        IPlatformChildDomainProvider, IPlatformGuestMemoryProvider, IPlatformVirtualEventProvider,
        IPlatformVirtualTrapProvider, IPlatformVirtualIoProvider, IPlatformDeviceLeaseProvider,
        IPlatformRegionRevocationProvider, IPlatformCompletionProvider,
        IPlatformProviderIncarnationSource
    {
        private ulong next = 20;
        public bool MalformedClose { get; init; }
        public int TransitionCalls { get; private set; }
        public int CloseCalls { get; private set; }
        public int GuestUnmapCalls { get; private set; }
        public int RegionRevocationBeginCalls { get; private set; }
        public int CompletionCalls { get; private set; }
        public int RevokeDomainCalls { get; private set; }
        public int DomainBindCalls { get; private set; }
        public int MapCalls { get; private set; }
        public Action? BeforeMapOwnedRegionReturn { get; set; }
        public PlatformAuthorityStatus? MapStatus { get; set; }
        public Action? BeforeDomainBindReturn { get; set; }
        public int ChildCreateCalls { get; private set; }
        public Action? BeforeDomainRevoke { get; set; }
        public PlatformAuthorityStatus? RootRevokeStatus { get; set; }
        public int EventCalls { get; private set; }
        public int GuestMapCalls { get; private set; }
        public int TrapCalls { get; private set; }
        public Action? BeforeChildEvent { get; set; }
        public Action? BeforeChildTrap { get; set; }
        public PlatformAuthorityStatus? TrapStatus { get; set; }
        public Func<PlatformVirtualTrapEvidence, PlatformVirtualTrapEvidence>? TrapTransform { get; set; }
        public int VirtualIoRevokeCalls { get; private set; }
        public int DeviceRevokeCalls { get; private set; }
        public Action? BeforeVirtualIoRevoke { get; set; }
        public Func<PlatformVirtualIoClosureReceipt, PlatformVirtualIoClosureReceipt>? VirtualIoClosureTransform { get; set; }
        public Action? BeforeGuestUnmap { get; set; }
        public Action? BeforeChildClose { get; set; }
        public Action? BeforeChildCreateReturn { get; set; }
        public Action? BeforeChildTransition { get; set; }
        public PlatformAuthorityStatus? ChildTransitionStatus { get; set; }
        public PlatformAuthorityStatus? ChildCreateStatus { get; set; }
        public bool MalformedCreateParent { get; set; }
        public PlatformAuthorityStatus? ChildCreateCleanupStatus { get; set; }
        public bool ThrowAfterGuestMap { get; set; }
        public PlatformAuthorityStatus? GuestMapStatus { get; set; }
        public bool ThrowAfterVirtualIoBind { get; set; }
        public PlatformAuthorityStatus? VirtualIoBindStatus { get; set; }
        public Action? AfterGuestMap { get; set; }
        public Action? AfterVirtualIoBind { get; set; }
        public bool MalformedGuestMap { get; set; }
        public bool MalformedVirtualIoBind { get; set; }
        public PlatformAuthorityStatus? GuestUnmapStatus { get; set; }
        public PlatformAuthorityStatus? VirtualIoRevokeStatus { get; set; }
        public List<string> CallLog { get; } = [];
        public PlatformProviderChildDomainLease LastChildLease { get; private set; }
        private PlatformProviderIncarnation _currentIncarnation = new(1);
        public Action? BeforeIncarnationRead { get; set; }
        public PlatformProviderIncarnation CurrentIncarnation
        {
            get { BeforeIncarnationRead?.Invoke(); return _currentIncarnation; }
            set { _currentIncarnation = value; }
        }
        public Action? BeforeDescriptorRead { get; set; }
        private readonly PlatformProviderDescriptor _descriptor = new(new("test.child"), 2,
            PlatformAuthorityFeatures.NeutralDomainBinding | PlatformAuthorityFeatures.DirectOwnedRegionMapping);
        public PlatformProviderDescriptor Descriptor
        {
            get { BeforeDescriptorRead?.Invoke(); return _descriptor; }
        }
        public PlatformFeatureManifest QueryFeatures() => new(Enum.GetValues<PlatformFeatureFamily>()
            .Where(f => f is PlatformFeatureFamily.NeutralDomains or PlatformFeatureFamily.OwnedRegionMapping or
                PlatformFeatureFamily.ChildDomainLifecycle or
                PlatformFeatureFamily.ChildGuestMemory or PlatformFeatureFamily.ChildEventDelivery or
                PlatformFeatureFamily.ChildTrapDelivery or PlatformFeatureFamily.BoundedVirtualIo)
            .Select(f => new PlatformFeatureDescriptor(f,
                f is PlatformFeatureFamily.NeutralDomains or PlatformFeatureFamily.ChildDomainLifecycle or
                    PlatformFeatureFamily.OwnedRegionMapping ? 2u : 1u,
                f == PlatformFeatureFamily.BoundedVirtualIo
                    ? PlatformFeatureAvailability.Executable
                    : PlatformFeatureAvailability.RuntimeAdmission)));
        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject)
        {
            DomainBindCalls++;
            var lease = new PlatformProviderDomainLease(new(next++), new(7), subject);
            BeforeDomainBindReturn?.Invoke();
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(lease);
        }
        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease)
        {
            RevokeDomainCalls++;
            CallLog.Add("parent-close");
            BeforeDomainRevoke?.Invoke();
            if (RootRevokeStatus is { } status) return PlatformAuthorityResult.Fail(status, "controlled root closure refusal");
            return PlatformAuthorityResult.Ok();
        }
        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease, PlatformRegionIdentity region, PlatformMemoryAccess access)
        {
            MapCalls++;
            BeforeMapOwnedRegionReturn?.Invoke();
            if (MapStatus is { } status)
                return PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                    status, "Mapping rejected after callback.");
            return PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Ok(
                new(new(next++), new(11), domainLease, region, access));
        }
        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy) => PlatformAuthorityResult.Ok();
        public PlatformAuthorityResult<PlatformRegionRevocationTicket> BeginRegionMappingRevocation(
            PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy)
        {
            RegionRevocationBeginCalls++;
            CallLog.Add("parent-map-begin");
            var operation = new PlatformOperationIdentity(new(next++), new(1), mapping.DomainLease);
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Ok(new(mapping.MappingId, mapping.Generation, operation));
        }
        public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(PlatformOperationIdentity operation)
        {
            CompletionCalls++;
            CallLog.Add("parent-map-observe");
            return PlatformAuthorityResult<PlatformCompletionReceipt>.Ok(new(operation.OperationId, operation.Generation,
                operation.DomainLease, PlatformCompletionState.Closed));
        }
        public PlatformAuthorityResult<PlatformProviderChildDomainLease> CreateChildDomain(
            PlatformProviderDomainLease parentLease, PlatformChildDomainIntent intent)
        {
            ChildCreateCalls++;
            var returnedParent = MalformedCreateParent
                ? parentLease with { Generation = new(parentLease.Generation.Value + 1) }
                : parentLease;
            var returnedLease = new PlatformProviderChildDomainLease(new(next++), new(23), returnedParent, intent);
            LastChildLease = returnedLease;
            BeforeChildCreateReturn?.Invoke();
            if (ChildCreateStatus is { } status)
                return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Fail(status,
                    "Injected ambiguous child creation response.");
            return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Ok(returnedLease);
        }
        public PlatformAuthorityResult TransitionChildDomain(PlatformProviderChildDomainLease lease,
            PlatformChildDomainTransition transition)
        {
            TransitionCalls++;
            BeforeChildTransition?.Invoke();
            if (transition == PlatformChildDomainTransition.BeginDrain) CallLog.Add("child-drain");
            if (ChildTransitionStatus is { } status) return PlatformAuthorityResult.Fail(status, "controlled transition outcome");
            return PlatformAuthorityResult.Ok();
        }
        public PlatformAuthorityResult<PlatformChildDomainClosureReceipt> CloseChildDomain(PlatformProviderChildDomainLease lease)
        {
            CloseCalls++;
            CallLog.Add("child-close");
            BeforeChildClose?.Invoke();
            if (ChildCreateCleanupStatus is { } status)
                return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(status,
                    "Injected child creation cleanup failure.");
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Ok(new(lease.LeaseId,
                MalformedClose ? new(lease.Generation.Value + 1) : lease.Generation,
                lease.ParentDomainLease.LeaseId, lease.ParentDomainLease.Generation,
                PlatformChildDomainClosureDisposition.Closed));
        }
        public PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease> MapGuestRegion(PlatformGuestRegionMappingRequest request)
        {
            GuestMapCalls++;
            var range = MalformedGuestMap
                ? request.GuestRange with { ByteLength = request.GuestRange.ByteLength - 1 }
                : request.GuestRange;
            var lease = new PlatformProviderGuestRegionMappingLease(new(next++), new(31), request.ChildLease,
                request.ParentMapping.Lease, range, request.Access);
            AfterGuestMap?.Invoke();
            if (ThrowAfterGuestMap) throw new InvalidOperationException("Guest map receipt lost.");
            if (GuestMapStatus is { } status)
                return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(status,
                    "Injected ambiguous guest mapping status.");
            return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Ok(lease);
        }
        public PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt> UnmapGuestRegion(PlatformProviderGuestRegionMappingLease lease)
        {
            GuestUnmapCalls++;
            CallLog.Add("guest-unmap");
            BeforeGuestUnmap?.Invoke();
            if (GuestUnmapStatus is { } status)
                return PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Fail(status,
                    "Injected guest cleanup failure.");
            return PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Ok(new(lease.LeaseId, lease.Generation,
                lease.ChildLease.LeaseId, lease.ChildLease.Generation, lease.ChildLease.ParentDomainLease.LeaseId,
                lease.ChildLease.ParentDomainLease.Generation, true));
        }
        public PlatformAuthorityResult<PlatformVirtualEventReceipt> InjectVirtualEvent(PlatformVirtualEventRequest request)
        {
            EventCalls++;
            BeforeChildEvent?.Invoke();
            return PlatformAuthorityResult<PlatformVirtualEventReceipt>.Ok(new(request.ChildLease.LeaseId,
                request.ChildLease.Generation, request.ChildLease.ParentDomainLease.LeaseId,
                request.ChildLease.ParentDomainLease.Generation, (ulong)EventCalls, request.EventClass, request.SourceResourceId));
        }
        public PlatformAuthorityResult<PlatformVirtualTrapEvidence> ObserveVirtualTrap(PlatformProviderChildDomainLease lease)
        {
            TrapCalls++;
            BeforeChildTrap?.Invoke();
            if (TrapStatus is { } status)
                return PlatformAuthorityResult<PlatformVirtualTrapEvidence>.Fail(status, "Controlled trap reply failure");
            var evidence = new PlatformVirtualTrapEvidence(lease.LeaseId, lease.Generation,
                lease.ParentDomainLease.LeaseId, lease.ParentDomainLease.Generation, (ulong)TrapCalls,
                PlatformVirtualTrapKind.Timer);
            return PlatformAuthorityResult<PlatformVirtualTrapEvidence>.Ok(TrapTransform is null ? evidence : TrapTransform(evidence));
        }
        public PlatformAuthorityResult<PlatformProviderVirtualIoLease> BindVirtualIo(PlatformVirtualIoRequest request)
        {
            var profile = MalformedVirtualIoBind
                ? request.Profile with { MaximumDmaTransferBytes = request.Profile.MaximumDmaTransferBytes - 1 }
                : request.Profile;
            var lease = new PlatformProviderVirtualIoLease(new(next++), new(41), request.ChildLease,
                request.ParentDeviceLease, profile);
            AfterVirtualIoBind?.Invoke();
            if (ThrowAfterVirtualIoBind) throw new InvalidOperationException("Virtual-I/O bind receipt lost.");
            if (VirtualIoBindStatus is { } status)
                return PlatformAuthorityResult<PlatformProviderVirtualIoLease>.Fail(status,
                    "Injected ambiguous virtual-I/O status.");
            return PlatformAuthorityResult<PlatformProviderVirtualIoLease>.Ok(lease);
        }
        public PlatformAuthorityResult<PlatformVirtualIoClosureReceipt> RevokeVirtualIo(PlatformProviderVirtualIoLease lease)
        {
            VirtualIoRevokeCalls++;
            BeforeVirtualIoRevoke?.Invoke();
            if (VirtualIoRevokeStatus is { } status)
                return PlatformAuthorityResult<PlatformVirtualIoClosureReceipt>.Fail(status,
                    "Injected virtual-I/O cleanup failure.");
            var receipt = new PlatformVirtualIoClosureReceipt(lease.LeaseId, lease.Generation,
                lease.ChildLease.LeaseId, lease.ChildLease.Generation, lease.ChildLease.ParentDomainLease.LeaseId,
                lease.ChildLease.ParentDomainLease.Generation, true);
            return PlatformAuthorityResult<PlatformVirtualIoClosureReceipt>.Ok(
                VirtualIoClosureTransform is null ? receipt : VirtualIoClosureTransform(receipt));
        }
        public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(PlatformProviderDomainLease domainLease,
            PlatformDeviceIdentity device, PlatformDeviceRights rights) =>
            PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(new(new(next++), new(37), domainLease, device, rights));
        public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease lease)
        {
            DeviceRevokeCalls++;
            return PlatformAuthorityResult.Ok();
        }
    }
}
