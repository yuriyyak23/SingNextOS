using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;
using SingPlus.Sip.Native;
using SingPlus.Sip.FileSystem;
using SingPlus.Sip.Networking;
using SingPlus.Sip.Process;
using SingPlus.Sip.Virtualization;

namespace SingPlus.Tests.Virtualization;

public sealed class Phase8ResidualVirtualizationTests
{
    [Fact]
    public void NestedOriginalCleanupReleasesRegionAndConsumesAttachedChargeOnce()
    {
        var s = CreateScenario(); var parent = CreateConfiguredRoot(s);
        var admin = TestFixtures.Create(s.Kernel, 99, 999).Handle;
        var budgetCap = s.Kernel.MintCapability(new(999), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = s.Kernel.AdmitProcessBudget(admin, budgetCap, s.Owner, "nested-cleanup", [new(ServiceBudgetDimension.OwnedMemoryBytes, 512)]).Value!.ProcessBudget;
        var region = s.Kernel.AllocateBuffer<byte>(s.Owner, 512).Value!;
        var created = s.Kernel.CreateNestedVirtualDomain(s.Owner, parent.Domain, parent.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle));
        Assert.True(created.IsSuccess, created.Message);
        Assert.Equal(512UL, s.Kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Released, s.Kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(0UL, s.Kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(2, s.Provider.ChildCloseCalls);
        Assert.Equal(1, s.Provider.NestedCloseCalls);
        Assert.False(s.Kernel.Processes.Resolve(s.Owner).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).IsSuccess); // Completed owner has retired; no live teardown record.
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void NestedPendingHistoryLossOrCopyKeepsOriginalRegionPinned(bool copied)
    {
        var s = CreateScenario(); var parent = CreateConfiguredRoot(s);
        var region = s.Kernel.AllocateBuffer<byte>(s.Owner, 512).Value!;
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        var routes = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_childBindings");
        var owner = routes[local.ChildBinding!.Value.BindingId]!;
        var history = (global::System.Collections.IList)owner.GetType().GetProperty("NestedCreateInvocations")!.GetValue(owner)!;
        s.Provider.AfterNestedMaterialize = () =>
        {
            var original = Assert.Single(history.Cast<object>());
            if (copied) history[0] = typeof(object).GetMethod("MemberwiseClone", global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.Invoke(original, null)!;
            else history.Clear();
        };
        Assert.False(s.Kernel.CreateNestedVirtualDomain(s.Owner, parent.Domain, parent.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.NestedCreateCalls);
        Assert.Equal(0, s.Provider.ChildCloseCalls);
        Assert.Equal(0, owner.GetType().GetProperty("PendingChildEffects")!.GetValue(owner));
        Assert.Equal(RegionState.Owned, s.Kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
    }

    [Fact]
    public void MalformedNestedReplyCannotCloseAnotherActualKernelDomain()
    {
        var s = CreateScenario(); var parent = CreateConfiguredRoot(s);
        var foreignOwner = TestFixtures.Create(s.Kernel, 502, 5002).Handle;
        var foreignCreate = s.Kernel.MintCapability(new(5002), foreignOwner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var foreignResult = s.Kernel.CreateVirtualDomain(foreignOwner, foreignCreate, new(1, 4096));
        Assert.True(foreignResult.IsSuccess, foreignResult.Message);
        var foreign = foreignResult.Value!;
        var admin = TestFixtures.Create(s.Kernel, 99, 999).Handle;
        var budgetCap = s.Kernel.MintCapability(new(999), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = s.Kernel.AdmitProcessBudget(admin, budgetCap, s.Owner, "nested-original", [new(ServiceBudgetDimension.OwnedMemoryBytes, 512)]).Value!.ProcessBudget;
        var region = s.Kernel.AllocateBuffer<byte>(s.Owner, 512).Value!;
        var authority = (VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains");
        var foreignBinding = Assert.Single(authority.ForOwner(foreignOwner), x => x.Handle == foreign.Domain).ChildBinding!.Value;
        var routes = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_childBindings");
        var foreignLease = (PlatformProviderChildDomainLease)routes[foreignBinding.BindingId]!.GetType().GetProperty("ProviderLease")!.GetValue(routes[foreignBinding.BindingId])!;
        s.Provider.NestedReceiptMutation = reply => reply with { ChildLease = foreignLease, ImmediateParentLeaseId = new(ulong.MaxValue) };
        Assert.False(s.Kernel.CreateNestedVirtualDomain(s.Owner, parent.Domain, parent.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).IsSuccess);
        Assert.Equal(0, s.Provider.ChildCloseCalls);
        Assert.Equal(VirtualDomainState.Created, s.Kernel.QueryVirtualDomain(foreignOwner, foreign.Domain).Value);
        Assert.True(s.Kernel.DestroyVirtualDomain(foreignOwner, foreign.Domain, foreign.ConfigureCapability).IsSuccess);
        Assert.Equal(1, s.Provider.ChildCloseCalls);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.NestedCreateCalls);
        Assert.Equal(1, s.Provider.ChildCloseCalls);
        Assert.Equal(RegionState.Owned, s.Kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(512UL, s.Kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void NestedFinalCommitRejectsSameValueParentRevisionAndExhaustion(bool late, bool exhausted)
    {
        var s = CreateScenario(); var parent = CreateConfiguredRoot(s);
        var region = s.Kernel.AllocateBuffer<byte>(s.Owner, 512).Value!;
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        var routes = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_childBindings");
        var original = routes[local.ChildBinding!.Value.BindingId]!;
        void Change()
        {
            Assert.False(global::System.Threading.Monitor.IsEntered(ArtifactField(s.Kernel.PlatformAuthority, "_secureDomainLifecycleGate")));
            if (exhausted) original.GetType().GetField("_lifecycleRevisionExhausted", global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.SetValue(original, 1);
            else original.GetType().GetProperty("State")!.SetValue(original, original.GetType().GetProperty("State")!.GetValue(original));
        }
        if (late) s.Provider.AfterNestedMaterialize = Change;
        else s.Provider.BeforeIncarnationRead = () => { s.Provider.BeforeIncarnationRead = null; Change(); };
        Assert.False(s.Kernel.CreateNestedVirtualDomain(s.Owner, parent.Domain, parent.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).IsSuccess);
        Assert.Equal(late ? 1 : 0, s.Provider.NestedCreateCalls);
        Assert.Equal(0, original.GetType().GetProperty("PendingChildEffects")!.GetValue(original));
        Assert.DoesNotContain(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner), x => x.ParentDomain == parent.Domain && x.AuthorityPublished);
        Assert.Equal(RegionState.Owned, s.Kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void NestedPublicationRequiresTheCapturedLocalParentRevision(int fault)
    {
        var s = CreateScenario(); var parent = CreateConfiguredRoot(s);
        var authority = (VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains");
        var original = Assert.Single(authority.ForOwner(s.Owner));
        var state = original.State;
        var region = s.Kernel.AllocateBuffer<byte>(s.Owner, 128).Value!;
        s.Provider.AfterNestedMaterialize = () =>
        {
            if (fault == 1) original.State = VirtualDomainState.Quarantined;
            if (fault == 2) original.AuthorityPublished = true; else original.State = state;
        };
        var result = s.Kernel.CreateNestedVirtualDomain(s.Owner, parent.Domain, parent.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.GuestMemory));
        Assert.False(result.IsSuccess);
        var nested = Assert.Single(authority.ForOwner(s.Owner), x => x.ParentDomain == parent.Domain);
        Assert.False(nested.AuthorityPublished);
        Assert.Empty(nested.Capabilities);
        Assert.Equal(RegionState.Owned, s.Kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        s.Provider.AfterNestedMaterialize = null;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.NestedCloseCalls);
        Assert.Equal(RegionState.Released, s.Kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void NestedCreateCanonicalRootLossRetainsOriginalInvocationAndBacking(bool foreign)
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        Assert.True(s.Kernel.AllocateRegion(s.Owner, 4096).IsSuccess);
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        var child = local.ChildBinding!.Value;
        var roots = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_activeSubjects");
        var originalRoot = roots[child.ParentBinding.Subject]!;
        var routes = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_childBindings");
        var originalChild = routes[child.BindingId]!;
        s.Provider.AfterNestedMaterialize = () =>
        {
            if (foreign) roots[child.ParentBinding.Subject] = typeof(object).GetMethod("MemberwiseClone",
                global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.Invoke(originalRoot, null)!;
            else roots.Remove(child.ParentBinding.Subject);
        };
        Assert.False(s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).IsSuccess);
        var retained = Assert.Single(((global::System.Collections.IEnumerable)originalChild.GetType()
            .GetProperty("UnpublishedNestedCreates")!.GetValue(originalChild)!).Cast<object>());
        var receipt = (PlatformProviderNestedDomainLease)retained.GetType().GetProperty("Receipt")!.GetValue(retained)!;
        Assert.Equal(s.Provider.LastNestedLease, receipt.ChildLease);
        Assert.Single(((global::System.Collections.IEnumerable)originalChild.GetType()
            .GetProperty("NestedCreateInvocations")!.GetValue(originalChild)!).Cast<object>());
        Assert.Equal(0, originalChild.GetType().GetProperty("PendingChildEffects")!.GetValue(originalChild));
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.Equal(1, s.Provider.NestedCreateCalls);
        Assert.Equal(0, s.Provider.ChildCloseCalls);
        Assert.DoesNotContain("parent-close", s.Provider.CallLog);
    }

    [Fact]
    public async Task ConcurrentDifferentDomainInjectionCannotStealOriginalMailbox()
    {
        var (s, root, leaf) = NestedDeliveryScenario();
        var endpoint = s.Kernel.CreateKernelEventEndpoint(s.Owner).Value!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        s.Provider.AfterChildEvent = () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
        };
        var first = Task.Run(() => s.Kernel.InjectVirtualEvent(s.Owner, leaf.Domain, leaf.EventCapability, endpoint));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(KernelError.CapacityExhausted,
                s.Kernel.InjectVirtualEvent(s.Owner, root.Domain, root.EventCapability, endpoint).Error);
            Assert.Equal(1, s.Provider.EventCalls);
            Assert.False(s.Kernel.ConsumeKernelEvent(s.Owner, endpoint).IsSuccess);
        }
        finally { release.Set(); }
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(15))).IsSuccess);
        s.Provider.AfterChildEvent = null;
        Assert.Equal(VirtualizationResourceIds.Events(leaf.Domain.DomainId), s.Kernel.ConsumeKernelEvent(s.Owner, endpoint).Value!.SourceResourceId);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void EventStagedSourceLossRefusesEffectOrPublication(bool copied, bool afterEffect)
    {
        var (s, root, leaf) = NestedDeliveryScenario();
        var backing = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var endpoint = s.Kernel.CreateKernelEventEndpoint(s.Owner).Value!;
        var registry = (KernelEventRegistry)ArtifactField(s.Kernel, "_kernelEvents");
        var routes = (global::System.Collections.IDictionary)ArtifactField(registry, "_endpoints");
        var original = routes[endpoint.EndpointId]!;
        Action lose = () =>
        {
            s.Provider.BeforeIncarnationRead = null;
            Assert.NotNull(original.GetType().GetProperty("Staged")!.GetValue(original));
            if (copied) routes[endpoint.EndpointId] = typeof(object).GetMethod("MemberwiseClone",
                global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.Invoke(original, null)!;
            else routes.Remove(endpoint.EndpointId);
        };
        if (afterEffect) s.Provider.AfterChildEvent = lose;
        else s.Provider.BeforeIncarnationRead = lose;
        Assert.False(s.Kernel.InjectVirtualEvent(s.Owner, leaf.Domain, leaf.EventCapability, endpoint).IsSuccess);
        Assert.Equal(afterEffect ? 1 : 0, s.Provider.EventCalls);
        Assert.Null(original.GetType().GetProperty("Staged")!.GetValue(original));
        Assert.Null(original.GetType().GetProperty("Pending")!.GetValue(original));
        Assert.False(s.Kernel.ConsumeKernelEvent(s.Owner, endpoint).IsSuccess);
        s.Provider.AfterChildEvent = null;
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner), x => x.Handle == leaf.Domain);
        var childRoutes = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_childBindings");
        var child = childRoutes[local.ChildBinding!.Value.BindingId]!;
        Assert.Equal(0, child.GetType().GetProperty("PendingChildEffects")!.GetValue(child));
        var facts = ((global::System.Collections.IEnumerable)child.GetType().GetProperty("EventInvocations")!.GetValue(child)!).Cast<object>();
        Assert.Equal(afterEffect ? 1 : 0, facts.Count());
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot(), x => x.Handle == backing.Handle).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EventMailboxIsReservedAcrossProviderReentryOrThrow(bool throwReply)
    {
        var (s, root, leaf) = NestedDeliveryScenario();
        var endpoint = s.Kernel.CreateKernelEventEndpoint(s.Owner).Value!;
        var registry = (KernelEventRegistry)ArtifactField(s.Kernel, "_kernelEvents");
        s.Provider.AfterChildEvent = () =>
        {
            Assert.False(Monitor.IsEntered(ArtifactField(registry, "_gate")));
            Assert.Equal(KernelError.CapacityExhausted, registry.Stage(s.Owner, endpoint, KernelEventClass.ExternalSignal, "reentry").Error);
            Assert.False(s.Kernel.ConsumeKernelEvent(s.Owner, endpoint).IsSuccess);
            if (throwReply) throw new IOException("Original event outcome lost.");
        };
        Assert.Equal(!throwReply, s.Kernel.InjectVirtualEvent(s.Owner, leaf.Domain, leaf.EventCapability, endpoint).IsSuccess);
        Assert.Equal(1, s.Provider.EventCalls);
        s.Provider.AfterChildEvent = null;
        if (throwReply)
        {
            var retry = registry.Stage(s.Owner, endpoint, KernelEventClass.ExternalSignal, "local");
            Assert.True(retry.IsSuccess);
            Assert.True(registry.RollbackExact(s.Owner, retry.Value!).IsSuccess);
            Assert.False(s.Kernel.InjectVirtualEvent(s.Owner, leaf.Domain, leaf.EventCapability, endpoint).IsSuccess);
            Assert.Equal(1, s.Provider.EventCalls);
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        }
        else
        {
            Assert.True(s.Kernel.ConsumeKernelEvent(s.Owner, endpoint).IsSuccess);
            Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EventMailboxRefusalPrecedesActualProviderInjection(bool exhausted)
    {
        var (s, root, leaf) = NestedDeliveryScenario();
        var backing = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var endpoint = s.Kernel.CreateKernelEventEndpoint(s.Owner).Value!;
        var registry = (KernelEventRegistry)ArtifactField(s.Kernel, "_kernelEvents");
        var occupied = registry.Stage(s.Owner, endpoint, KernelEventClass.ExternalSignal, "original").Value!;
        if (exhausted)
        {
            Assert.True(registry.RollbackExact(s.Owner, occupied).IsSuccess);
            typeof(KernelEventRegistry).GetField("_nextEventSequence", global::System.Reflection.BindingFlags.Instance |
                global::System.Reflection.BindingFlags.NonPublic)!.SetValue(registry, 0UL);
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.Equal(KernelError.CapacityExhausted,
                s.Kernel.InjectVirtualEvent(s.Owner, leaf.Domain, leaf.EventCapability, endpoint).Error);
            Assert.Equal(0, s.Provider.EventCalls);
        }
        if (!exhausted) Assert.True(registry.RollbackExact(s.Owner, occupied).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot(), x => x.Handle == backing.Handle).State);
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)] [InlineData(3, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)] [InlineData(3, true)]
    public void GuestCleanupRetainsLocalFactAcrossLateCorrelationLoss(int fault, bool finalCommit)
    {
        var (s, domain, mapping) = ArtifactScenario();
        var original = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        var pair = original.PlatformMappings[mapping.Mapping.MappingId];
        var guests = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_guestBindings");
        var guest = guests[pair.Guest.MappingId]!;
        var source = guest.GetType().GetProperty("AdmissionSource")!.GetValue(guest)!;
        var ownerProperty = source.GetType().GetProperty("LocalMappingOwner")!;
        var mappingProperty = source.GetType().GetProperty("LocalMapping")!;
        var lost = false;
        void LoseSource()
        {
            s.Provider.BeforeIncarnationRead = null;
            lost = true;
            if (fault == 0) ownerProperty.SetValue(source, null);
            else if (fault == 1) ownerProperty.SetValue(source, typeof(object).GetMethod("MemberwiseClone",
                global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.Invoke(original, null)!);
            else mappingProperty.SetValue(source, fault == 2 ? null : mapping.Mapping with { Generation = new(2) });
        }
        s.Provider.BeforeGuestUnmap = () =>
        {
            s.Provider.BeforeGuestUnmap = null;
            if (!finalCommit) LoseSource();
            else
            {
                var reads = 0;
                s.Provider.BeforeIncarnationRead = () => { if (++reads == 2) LoseSource(); };
            }
        };
        Assert.False(s.Kernel.CloseGuestRegionMapping(s.Owner, domain.Domain, domain.MemoryCapability, mapping.Mapping).IsSuccess);
        Assert.True(lost);
        Assert.Equal(1, s.Provider.GuestUnmapCalls);
        Assert.Single(original.OriginalMappings);
        Assert.Single(original.PlatformMappings);
        Assert.Equal(finalCommit ? 1 : 0, s.Provider.CallLog.Count(x => x == "mapping-close"));
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        ownerProperty.SetValue(source, original); mappingProperty.SetValue(source, mapping.Mapping);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.GuestUnmapCalls);
        Assert.Equal(1, s.Provider.CallLog.Count(x => x == "mapping-close"));
        Assert.Empty(original.OriginalMappings);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(1, 0)] [InlineData(2, 0)] [InlineData(3, 0)]
    [InlineData(0, 1)] [InlineData(1, 1)] [InlineData(2, 1)] [InlineData(3, 1)]
    [InlineData(0, 2)] [InlineData(1, 2)] [InlineData(2, 2)] [InlineData(3, 2)]
    public void GuestLocalSourceRequiresExactOriginalOwnerAndHandle(int fault, int phase)
    {
        var (s, domain, mapping) = ArtifactScenario();
        var original = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        var pair = original.PlatformMappings[mapping.Mapping.MappingId];
        var guests = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_guestBindings");
        var guest = guests[pair.Guest.MappingId]!;
        var source = guest.GetType().GetProperty("AdmissionSource")!.GetValue(guest)!;
        var ownerProperty = source.GetType().GetProperty("LocalMappingOwner")!;
        var mappingProperty = source.GetType().GetProperty("LocalMapping")!;
        Assert.Same(original, ownerProperty.GetValue(source));
        Assert.Equal(mapping.Mapping, mappingProperty.GetValue(source));
        void LoseSource()
        {
            s.Provider.BeforeIncarnationRead = null; s.Provider.AfterArtifactBind = null;
            if (fault == 0) ownerProperty.SetValue(source, null);
            else if (fault == 1) ownerProperty.SetValue(source, typeof(object).GetMethod("MemberwiseClone",
                global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.Invoke(original, null)!);
            else mappingProperty.SetValue(source, fault == 2 ? null : mapping.Mapping with { Generation = new(2) });
        }
        if (phase == 1) s.Provider.BeforeIncarnationRead = LoseSource;
        else if (phase == 2) s.Provider.AfterArtifactBind = LoseSource;
        else LoseSource();
        Assert.False(s.Kernel.BindVirtualDomainExecutable(s.Owner, domain.Domain, domain.ExecuteCapability,
            mapping.Mapping, new byte[] { 0x56, 0x33 }, 1).IsSuccess);
        Assert.Equal(phase == 2 ? 1 : 0, s.Provider.ArtifactBindCalls);
        Assert.Equal(phase == 2, original.ExecutableArtifact is not null);
        Assert.Equal(VirtualDomainState.Configured, original.State);
        foreach (var name in new[] { "_childBindings", "_guestBindings" })
        {
            var records = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, name);
            foreach (var value in records.Values)
                Assert.Equal(0, value!.GetType().GetProperty(name == "_childBindings" ? "PendingChildEffects" : "PendingExecutableEffects")!.GetValue(value));
        }
        ownerProperty.SetValue(source, original); mappingProperty.SetValue(source, mapping.Mapping);
        if (phase == 2)
        {
            Assert.False(s.Kernel.BindVirtualDomainExecutable(s.Owner, domain.Domain, domain.ExecuteCapability,
                mapping.Mapping, new byte[] { 0x56, 0x33 }, 1).IsSuccess);
            Assert.Equal(1, s.Provider.ArtifactBindCalls);
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(0, s.Provider.GuestUnmapCalls);
            Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        }
        else
        {
            Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(1, s.Provider.GuestUnmapCalls);
            Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ArtifactBindCannotSubstituteAnotherAdmittedGuestMapping(bool cleanup)
    {
        var (s, domain, mapping) = ArtifactScenario();
        var otherRegion = s.Kernel.AllocateRegion(s.Owner, 4096).Value!.Handle;
        var cap = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(otherRegion.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Execute).Value!.CapabilityId;
        var other = s.Kernel.MapGuestRegion(s.Owner, domain.Domain, domain.MemoryCapability, cap,
            otherRegion, new(4096, 4096), GuestMemoryAccess.Read | GuestMemoryAccess.Execute);
        Assert.True(other.IsSuccess, other.Message);
        var original = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        var pair = original.PlatformMappings[mapping.Mapping.MappingId];
        var otherPair = original.PlatformMappings[other.Value!.Mapping.MappingId];
        Assert.NotEqual(pair.Guest.MappingId, otherPair.Guest.MappingId);
        original.PlatformMappings[mapping.Mapping.MappingId] = otherPair;
        var bound = cleanup ? s.Kernel.CloseGuestRegionMapping(s.Owner, domain.Domain, domain.MemoryCapability, mapping.Mapping)
            : s.Kernel.BindVirtualDomainExecutable(s.Owner, domain.Domain, domain.ExecuteCapability,
                mapping.Mapping, new byte[] { 0x56, 0x33 }, 1);
        Assert.False(bound.IsSuccess, $"Requested local {mapping.Mapping}, admitted provider guest {original.ExecutableArtifact?.GuestMapping}.");
        Assert.Equal(0, s.Provider.ArtifactBindCalls);
        Assert.Equal(0, s.Provider.GuestUnmapCalls);
        Assert.Null(original.ExecutableArtifact);
        original.PlatformMappings[mapping.Mapping.MappingId] = pair;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(2, s.Provider.GuestUnmapCalls);
        Assert.All(s.Kernel.Regions.Snapshot(), item => Assert.Equal(RegionState.Released, item.State));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ArtifactStartCannotSubstituteLocalMappingAssociation(bool absent)
    {
        var (s, domain, mapping) = ArtifactScenario();
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var cap = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Execute).Value!.CapabilityId;
        var other = s.Kernel.MapGuestRegion(s.Owner, domain.Domain, domain.MemoryCapability, cap,
            region.Handle, new(4096, 4096), GuestMemoryAccess.Read | GuestMemoryAccess.Execute);
        Assert.True(other.IsSuccess, other.Message);
        Assert.True(s.Kernel.BindVirtualDomainExecutable(s.Owner, domain.Domain, domain.ExecuteCapability,
            mapping.Mapping, new byte[] { 0x56, 0x33 }, 1).IsSuccess);
        var original = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        Assert.Equal(mapping.Mapping, original.ExecutableMapping);
        original.ExecutableMapping = absent ? null : other.Value!.Mapping;
        Assert.False(s.Kernel.StartVirtualDomain(s.Owner, domain.Domain, domain.ExecuteCapability).IsSuccess);
        Assert.Equal(0, s.Provider.ArtifactStartCalls);
        original.ExecutableMapping = mapping.Mapping;
        Assert.True(s.Kernel.StartVirtualDomain(s.Owner, domain.Domain, domain.ExecuteCapability).IsSuccess);
        Assert.Equal(1, s.Provider.ArtifactStartCalls);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(0, s.Provider.GuestUnmapCalls);
        Assert.All(s.Kernel.Regions.Snapshot(), item => Assert.Equal(RegionState.Owned, item.State));
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(1, 0)] [InlineData(2, 0)] [InlineData(3, 0)]
    [InlineData(0, 1)] [InlineData(1, 1)] [InlineData(2, 1)] [InlineData(3, 1)]
    [InlineData(0, 2)] [InlineData(1, 2)] [InlineData(2, 2)] [InlineData(3, 2)]
    public void ArtifactStartRequiresOriginalLocalMappingAndRetainsExactExecution(int fault, int phase)
    {
        var (s, domain, mapping) = ArtifactScenario();
        Assert.True(s.Kernel.BindVirtualDomainExecutable(s.Owner, domain.Domain, domain.ExecuteCapability,
            mapping.Mapping, new byte[] { 0x56, 0x33 }, 1).IsSuccess);
        var authority = (VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains");
        var original = Assert.Single(authority.ForOwner(s.Owner));
        void ChangeMapping()
        {
            s.Provider.BeforeIncarnationRead = null;
            s.Provider.AfterArtifactStart = null;
            if (fault == 3) original.OriginalMappings.Remove(mapping.Mapping.MappingId);
            else original.Mappings[mapping.Mapping.MappingId] = fault switch
            {
                0 => mapping with { GuestRange = new(4096, mapping.GuestRange.ByteLength) },
                1 => mapping with { Region = new(new(ulong.MaxValue), new(1)) },
                _ => mapping with { Access = GuestMemoryAccess.Read },
            };
        }
        if (phase == 1) s.Provider.BeforeIncarnationRead = ChangeMapping;
        else if (phase == 2) s.Provider.AfterArtifactStart = ChangeMapping;
        else ChangeMapping();
        Assert.False(s.Kernel.StartVirtualDomain(s.Owner, domain.Domain, domain.ExecuteCapability).IsSuccess);
        Assert.Equal(phase == 2 ? 1 : 0, s.Provider.ArtifactStartCalls);
        Assert.Equal(VirtualDomainState.Configured, original.State);
        var artifacts = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_executableArtifacts");
        var artifact = Assert.Single(artifacts.Values.Cast<object>());
        Assert.Equal(phase == 2, artifact.GetType().GetProperty("ExecutionReceipt")!.GetValue(artifact) is not null);
        Assert.Equal(phase == 2, (bool)artifact.GetType().GetProperty("Started")!.GetValue(artifact)!);
        Assert.False((bool)artifact.GetType().GetProperty("StartInFlight")!.GetValue(artifact)!);
        original.Mappings[mapping.Mapping.MappingId] = mapping;
        original.OriginalMappings[mapping.Mapping.MappingId] = mapping;
        Assert.Equal(phase != 2, s.Kernel.StartVirtualDomain(s.Owner, domain.Domain, domain.ExecuteCapability).IsSuccess);
        Assert.Equal(1, s.Provider.ArtifactStartCalls);
        Assert.False(s.Kernel.StartVirtualDomain(s.Owner, domain.Domain, domain.ExecuteCapability).IsSuccess);
        Assert.Equal(1, s.Provider.ArtifactStartCalls);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(0, s.Provider.GuestUnmapCalls);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot(), x => x.Handle == mapping.Region).State);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(1, 0)] [InlineData(2, 0)] [InlineData(3, 0)]
    [InlineData(0, 1)] [InlineData(1, 1)] [InlineData(2, 1)] [InlineData(3, 1)]
    [InlineData(0, 2)] [InlineData(1, 2)] [InlineData(2, 2)] [InlineData(3, 2)]
    public void ArtifactAdmissionRequiresOriginalLocalMappingBeforeProviderBind(int fault, int phase)
    {
        var (s, domain, mapping) = ArtifactScenario();
        var authority = (VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains");
        var original = Assert.Single(authority.ForOwner(s.Owner));
        void ChangeMapping()
        {
            s.Provider.BeforeIncarnationRead = null;
            s.Provider.AfterArtifactBind = null;
            if (fault == 3) original.OriginalMappings.Remove(mapping.Mapping.MappingId);
            else original.Mappings[mapping.Mapping.MappingId] = fault switch
            {
                0 => mapping with { GuestRange = new(4096, mapping.GuestRange.ByteLength) },
                1 => mapping with { Region = new(new(ulong.MaxValue), new(1)) },
                _ => mapping with { Access = GuestMemoryAccess.Read },
            };
        }
        if (phase == 1) s.Provider.BeforeIncarnationRead = ChangeMapping;
        else if (phase == 2) s.Provider.AfterArtifactBind = ChangeMapping;
        else ChangeMapping();
        var bound = s.Kernel.BindVirtualDomainExecutable(s.Owner, domain.Domain, domain.ExecuteCapability,
            mapping.Mapping, new byte[] { 0x56, 0x33 }, 1);
        Assert.False(bound.IsSuccess);
        Assert.Equal(phase == 2 ? 1 : 0, s.Provider.ArtifactBindCalls);
        Assert.Equal(phase == 2, original.ExecutableArtifact is not null);
        var artifacts = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_executableArtifacts");
        Assert.Equal(phase == 2 ? 1 : 0, artifacts.Count);
        original.Mappings[mapping.Mapping.MappingId] = mapping;
        original.OriginalMappings[mapping.Mapping.MappingId] = mapping;
        if (phase == 2)
        {
            Assert.False(s.Kernel.BindVirtualDomainExecutable(s.Owner, domain.Domain, domain.ExecuteCapability,
                mapping.Mapping, new byte[] { 0x56, 0x33 }, 1).IsSuccess);
            Assert.Equal(1, s.Provider.ArtifactBindCalls);
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(0, s.Provider.GuestUnmapCalls);
            Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
            Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot(), x => x.Handle == mapping.Region).State);
        }
        else
        {
            Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(1, s.Provider.GuestUnmapCalls);
            Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot(), x => x.Handle == mapping.Region).State);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void KnownArtifactRetainsOriginalLocalOwnerAfterRoutingLoss(bool copied)
    {
        var (s, domain, mapping) = ArtifactScenario();
        var authority = (VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains");
        var original = Assert.Single(authority.ForOwner(s.Owner));
        var routes = (global::System.Collections.IDictionary)ArtifactField(authority, "_records");
        s.Provider.AfterArtifactBind = () =>
        {
            s.Provider.AfterArtifactBind = null;
            if (copied) routes[domain.Domain.DomainId] = typeof(object).GetMethod("MemberwiseClone",
                global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.Invoke(original, null)!;
            else routes.Remove(domain.Domain.DomainId);
        };
        Assert.False(s.Kernel.BindVirtualDomainExecutable(s.Owner, domain.Domain, domain.ExecuteCapability,
            mapping.Mapping, new byte[] { 0x56, 0x33 }, 1).IsSuccess);
        Assert.NotNull(original.ExecutableArtifact);
        Assert.Equal(mapping.Mapping, original.ExecutableMapping);
        Assert.Equal(1, s.Provider.ArtifactBindCalls);
        Assert.Equal(VirtualDomainState.Configured, original.State);
        routes[domain.Domain.DomainId] = original;
        Assert.False(s.Kernel.BindVirtualDomainExecutable(s.Owner, domain.Domain, domain.ExecuteCapability,
            mapping.Mapping, new byte[] { 0x56, 0x33 }, 1).IsSuccess);
        Assert.Equal(1, s.Provider.ArtifactBindCalls);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(0, s.Provider.GuestUnmapCalls);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot(), x => x.Handle == mapping.Region).State);
    }

    [Theory]
    [InlineData(false, false, false)] [InlineData(false, true, false)]
    [InlineData(true, false, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(false, true, true)]
    [InlineData(true, false, true)] [InlineData(true, true, true)]
    public void VirtualCleanupInventoryRequiresOriginalOwnerBeforeGuestEffect(bool nested, bool copied, bool late)
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var domain = nested ? s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.GuestMemory)).Value! : root;
        if (nested) Assert.True(s.Kernel.ConfigureVirtualDomain(s.Owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var capability = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var mapped = s.Kernel.MapGuestRegion(s.Owner, domain.Domain, domain.MemoryCapability, capability,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read);
        Assert.True(mapped.IsSuccess, mapped.Message);
        var authority = (VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains");
        var original = Assert.Single(authority.ForOwner(s.Owner), x => x.Handle == domain.Domain);
        var routes = (global::System.Collections.IDictionary)ArtifactField(authority, "_records");
        void LoseRoute()
        {
            s.Provider.BeforeGuestUnmap = null;
            if (copied) routes[domain.Domain.DomainId] = typeof(object).GetMethod("MemberwiseClone",
                global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.Invoke(original, null)!;
            else routes.Remove(domain.Domain.DomainId);
        }
        if (late) s.Provider.BeforeGuestUnmap = LoseRoute;
        else LoseRoute();
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(late ? 1 : 0, s.Provider.GuestUnmapCalls);
        Assert.Equal(0, s.Provider.ChildCloseCalls);
        Assert.DoesNotContain("mapping-close", s.Provider.CallLog);
        Assert.Same(original, Assert.Single(authority.ForOwner(s.Owner), x => x.Handle == domain.Domain));
        Assert.Single(original.OriginalMappings);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
        routes[domain.Domain.DomainId] = original;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.GuestUnmapCalls);
        Assert.Equal(nested ? 2 : 1, s.Provider.ChildCloseCalls);
        Assert.Empty(authority.ForOwner(s.Owner));
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LostLocalGuestMappingRouteDoesNotAdmitOverlapWithOriginalBacking(bool replaced)
    {
        var s = CreateScenario();
        var domain = CreateConfiguredRoot(s);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var capability = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var mapped = s.Kernel.MapGuestRegion(s.Owner, domain.Domain, domain.MemoryCapability, capability,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read).Value!;
        var authority = (VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains");
        var owner = Assert.Single(authority.ForOwner(s.Owner));
        if (replaced) owner.Mappings[mapped.Mapping.MappingId] = mapped with { GuestRange = new(4096, 4096) };
        else owner.Mappings.Remove(mapped.Mapping.MappingId);
        var denied = s.Kernel.MapGuestRegion(s.Owner, domain.Domain, domain.MemoryCapability, capability,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read);
        Assert.Equal(KernelError.PlatformDenied, denied.Error);
        Assert.Equal(1, s.Provider.ParentMapCalls);
        Assert.Equal(1, s.Provider.GuestMapCalls);
        Assert.Equal(mapped, Assert.Single(owner.OriginalMappings).Value);
        owner.Mappings[mapped.Mapping.MappingId] = mapped;
        Assert.True(s.Kernel.CloseGuestRegionMapping(s.Owner, domain.Domain, domain.MemoryCapability, mapped.Mapping).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
    }

    [Fact]
    public void ReentrantOverlappingGuestMapCannotReplaceStagedOriginalOrReserveSecondBacking()
    {
        var s = CreateScenario();
        var domain = CreateConfiguredRoot(s);
        var first = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var second = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        CapabilityId Capability(RegionHandle region) => s.Kernel.MintCapability(new DomainId(5001), s.Owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var firstCapability = Capability(first.Handle);
        var secondCapability = Capability(second.Handle);
        var attempted = false;
        s.Provider.AfterParentMap = () =>
        {
            s.Provider.AfterParentMap = null;
            attempted = true;
            var overlap = s.Kernel.MapGuestRegion(s.Owner, domain.Domain, domain.MemoryCapability,
                secondCapability, second.Handle, new(0, 4096), GuestMemoryAccess.Read);
            Assert.Equal(KernelError.PlatformBindingActive, overlap.Error);
            Assert.Equal(1, s.Provider.ParentMapCalls);
            Assert.Equal(0, s.Provider.GuestMapCalls);
        };
        var mapped = s.Kernel.MapGuestRegion(s.Owner, domain.Domain, domain.MemoryCapability,
            firstCapability, first.Handle, new(0, 4096), GuestMemoryAccess.Read);
        Assert.True(mapped.IsSuccess, mapped.Message);
        Assert.True(attempted);
        var authority = (VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains");
        Assert.Equal(mapped.Value, Assert.Single(Assert.Single(authority.ForOwner(s.Owner)).OriginalMappings).Value);
        Assert.Equal(1, s.Provider.ParentMapCalls);
        Assert.Equal(1, s.Provider.GuestMapCalls);
        Assert.True(s.Kernel.CloseGuestRegionMapping(s.Owner, domain.Domain, domain.MemoryCapability, mapped.Value!.Mapping).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.All(s.Kernel.Regions.Snapshot().Where(x => x.Handle == first.Handle || x.Handle == second.Handle),
            x => Assert.Equal(RegionState.Released, x.State));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LocalGuestLastIdentityOrReservationRefusalPreservesOriginalFactsAndReclaim(bool foreignRegion)
    {
        var s = CreateScenario();
        var domain = CreateConfiguredRoot(s);
        var regionOwner = foreignRegion ? TestFixtures.Create(s.Kernel, 8002, 5002).Handle : s.Owner;
        var region = s.Kernel.AllocateRegion(regionOwner, 4096).Value!;
        var capability = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var authority = (VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains");
        var owner = Assert.Single(authority.ForOwner(s.Owner));
        typeof(VirtualDomainAuthority).GetField("_nextMappingId",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.SetValue(authority, ulong.MaxValue);
        var mapped = s.Kernel.MapGuestRegion(s.Owner, domain.Domain, domain.MemoryCapability, capability,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read);
        Assert.Equal(!foreignRegion, mapped.IsSuccess);
        Assert.Empty(owner.StagedMappings);
        if (foreignRegion)
        {
            Assert.Empty(owner.OriginalMappings);
            Assert.Equal(0, s.Provider.ParentMapCalls);
            Assert.Equal(0, s.Provider.GuestMapCalls);
        }
        else
        {
            Assert.Equal(ulong.MaxValue, mapped.Value!.Mapping.MappingId.Value);
            Assert.Equal(mapped.Value, Assert.Single(owner.OriginalMappings).Value);
            Assert.Equal(1, s.Provider.ParentMapCalls);
            Assert.Equal(1, s.Provider.GuestMapCalls);
        }
        for (var attempt = 0; attempt < 2; attempt++)
            Assert.Equal(KernelError.CapacityExhausted, s.Kernel.MapGuestRegion(s.Owner, domain.Domain,
                domain.MemoryCapability, capability, region.Handle, new(4096, 4096), GuestMemoryAccess.Read).Error);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        if (foreignRegion) Assert.True(s.Kernel.TerminateProcess(regionOwner).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
    }

    [Fact]
    public void ExhaustedLocalGuestMappingRefusesBeforeRegionReservationAndProviderMap()
    {
        var s = CreateScenario();
        var domain = CreateConfiguredRoot(s);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var capability = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var authority = (VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains");
        typeof(VirtualDomainAuthority).GetField("_nextMappingId",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.SetValue(authority, 0UL);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var mapped = s.Kernel.MapGuestRegion(s.Owner, domain.Domain, domain.MemoryCapability, capability,
                region.Handle, new(0, 4096), GuestMemoryAccess.Read);
            Assert.Equal(KernelError.CapacityExhausted, mapped.Error);
            Assert.Equal(0, s.Provider.ParentMapCalls);
            Assert.Equal(0, s.Provider.GuestMapCalls);
            Assert.Empty(Assert.Single(authority.ForOwner(s.Owner)).OriginalMappings);
        }
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
    }

    [Theory]
    [InlineData("_nextDomainId", false)] [InlineData("_nextAddressSpaceId", false)]
    [InlineData("_nextDomainId", true)] [InlineData("_nextAddressSpaceId", true)]
    [InlineData("_pendingDomainIdentities", false)] [InlineData("_pendingDomainIdentities", true)]
    public void ExhaustedLocalVirtualIdentityRefusesBeforeActualChildCreation(string allocator, bool nested)
    {
        var s = CreateScenario();
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var local = (VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains");
        var root = nested ? CreateConfiguredRoot(s) : default;
        var before = local.ForOwner(s.Owner).Length;
        var field = typeof(VirtualDomainAuthority).GetField(allocator,
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!;
        var saved = field.GetValue(local);
        field.SetValue(local, allocator == "_pendingDomainIdentities" ? (object)int.MaxValue : 0UL);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var created = nested
                ? s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
                    new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle))
                : s.Kernel.CreateVirtualDomain(s.Owner, s.CreateCapability, new(1, 8192));
            Assert.Equal(KernelError.CapacityExhausted, created.Error);
            Assert.Equal(nested ? 1 : 0, s.Provider.RootCreateCalls);
            Assert.Equal(0, s.Provider.NestedCreateCalls);
            Assert.Equal(before, local.ForOwner(s.Owner).Length);
        }
        // Synthetic pending count is not an actual outstanding invocation.
        if (allocator == "_pendingDomainIdentities") field.SetValue(local, saved);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LastLocalVirtualIdentityPublishesOriginalOwnerOnceAndAllowsExactReclaim(bool nested)
    {
        var s = CreateScenario();
        var root = nested ? CreateConfiguredRoot(s) : default;
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var local = (VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains");
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        typeof(VirtualDomainAuthority).GetField("_nextDomainId", flags)!.SetValue(local, ulong.MaxValue);
        typeof(VirtualDomainAuthority).GetField("_nextAddressSpaceId", flags)!.SetValue(local, ulong.MaxValue);
        KernelResult<VirtualDomainAuthoritySet> Create() => nested
            ? s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
                new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle))
            : s.Kernel.CreateVirtualDomain(s.Owner, s.CreateCapability, new(1, 8192));
        var created = Create();
        Assert.True(created.IsSuccess, created.Message);
        var original = Assert.Single(local.ForOwner(s.Owner), x => x.Handle == created.Value!.Domain);
        Assert.Equal(ulong.MaxValue, original.Handle.DomainId.Value);
        Assert.Equal(ulong.MaxValue, original.AddressSpace.AddressSpaceId.Value);
        Assert.Equal(0, ArtifactField(local, "_pendingDomainIdentities"));
        Assert.Equal(KernelError.CapacityExhausted, Create().Error);
        Assert.Equal(nested ? 1 : 0, s.Provider.NestedCreateCalls);
        Assert.Equal(1, s.Provider.RootCreateCalls);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void ExhaustedGuestInterlocksRefuseBeforeMutationOrProviderEffect(int consumer)
    {
        var s = CreateScenario();
        var domain = CreateConfiguredRoot(s);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var cap = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        Assert.True(s.Kernel.MapGuestRegion(s.Owner, domain.Domain, domain.MemoryCapability, cap,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read).IsSuccess);
        var bridge = s.Kernel.PlatformAuthority;
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        var mapping = Assert.Single(local.PlatformMappings).Value;
        var parents = (global::System.Collections.IDictionary)ArtifactField(bridge, "_mappings");
        var children = (global::System.Collections.IDictionary)ArtifactField(bridge, "_childBindings");
        var parent = parents[mapping.Parent.MappingId]!;
        var child = children[local.ChildBinding!.Value.BindingId]!;
        var counter = (consumer == 0 ? parent : child).GetType().GetProperty(
            consumer == 0 ? "PendingGuestMaps" : "PendingChildEffects")!;
        var owner = consumer == 0 ? parent : child;
        if (consumer >= 3)
            Assert.True(s.Kernel.StartVirtualDomain(s.Owner, domain.Domain, domain.ExecuteCapability).IsSuccess);
        counter.SetValue(owner, int.MaxValue);
        bool committed = false;
        var result = consumer switch
        {
            2 => bridge.CommitSecureGuestMapping(mapping.Guest, mapping.Parent, true,
                () => { committed = true; return KernelResult.Ok(); }, out _, out _),
            3 => ToResult(bridge.InjectChildEvent(local.ChildBinding.Value, PlatformVirtualEventClass.ExternalSignal, "capacity-source")),
            4 => ToResult(bridge.ObserveChildTrap(local.ChildBinding.Value)),
            _ => ToResult(bridge.MapChildGuestRegion(local.ChildBinding.Value, mapping.Parent,
                new(4096, 4096), PlatformGuestMemoryAccess.Read)),
        };
        Assert.Equal(KernelError.CapacityExhausted, result.Error);
        Assert.False(committed);
        Assert.Equal(int.MaxValue, counter.GetValue(owner));
        Assert.Equal(consumer == 0 ? 0 : int.MaxValue, child.GetType().GetProperty("PendingChildEffects")!.GetValue(child));
        Assert.Equal(consumer == 0 ? int.MaxValue : 0, parent.GetType().GetProperty("PendingGuestMaps")!.GetValue(parent));
        Assert.Equal(1, s.Provider.GuestMapCalls);
        Assert.Equal(0, s.Provider.EventCalls);
        Assert.Equal(0, s.Provider.TrapCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        counter.SetValue(owner, 0);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.GuestUnmapCalls);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);

        static KernelResult ToResult<T>(KernelResult<T> result) =>
            result.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(result.Error, result.Message!);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExhaustedChildCreationInterlocksRefuseWithoutIdentityOrProviderMutation(bool nested)
    {
        var provider = new NestedProvider(null, false, false, false, false, false);
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1), new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory);
        var parent = bridge.CreateChildDomain(root, subject, intent).Value!;
        var owners = (global::System.Collections.IDictionary)ArtifactField(bridge, nested ? "_childBindings" : "_domains");
        var owner = owners[nested ? (object)parent.BindingId : root.BindingId]!;
        var counter = owner.GetType().GetProperty(nested ? "PendingChildEffects" : "PendingChildCreates")!;
        counter.SetValue(owner, int.MaxValue);
        var identity = ArtifactField(bridge, "_nextChildBindingId");
        var refused = nested ? bridge.CreateNestedChildDomain(parent, subject.Process, intent)
            : bridge.CreateChildDomain(root, subject, intent);
        Assert.Equal(KernelError.CapacityExhausted, refused.Error);
        Assert.Equal(identity, ArtifactField(bridge, "_nextChildBindingId"));
        Assert.Equal(int.MaxValue, counter.GetValue(owner));
        Assert.Equal(1, provider.RootCreateCalls);
        Assert.Equal(0, provider.NestedCreateCalls);
        counter.SetValue(owner, 0);
        var admitted = nested ? bridge.CreateNestedChildDomain(parent, subject.Process, intent)
            : bridge.CreateChildDomain(root, subject, intent);
        Assert.True(admitted.IsSuccess);
        Assert.True(bridge.TransitionChildDomain(admitted.Value!, PlatformChildDomainTransition.BeginDrain).IsSuccess);
        Assert.True(bridge.CloseChildDomain(admitted.Value!).IsSuccess);
        Assert.True(bridge.TransitionChildDomain(parent, PlatformChildDomainTransition.BeginDrain).IsSuccess);
        Assert.True(bridge.CloseChildDomain(parent).IsSuccess);
        Assert.True(bridge.RevokeDomain(root, subject).IsSuccess);
        Assert.Equal(nested ? 1 : 2, provider.RootCreateCalls);
        Assert.Equal(nested ? 1 : 0, provider.NestedCreateCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void UnknownGuestMapFactSurvivesRoutingLossAndFalseLifecycleProjections(int outcome)
    {
        var s = CreateScenario();
        var domain = CreateConfiguredRoot(s);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var cap = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var bridge = s.Kernel.PlatformAuthority;
        var parents = (global::System.Collections.IDictionary)ArtifactField(bridge, "_mappings");
        var children = (global::System.Collections.IDictionary)ArtifactField(bridge, "_childBindings");
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        var child = local.ChildBinding!.Value;
        var childOwner = children[child.BindingId]!;
        object parentKey = null!, parentOwner = null!;
        s.Provider.GuestMapStatus = outcome == 1 ? PlatformAuthorityStatus.Faulted : null;
        if (outcome == 2) s.Provider.GuestLeaseMutation = lease => lease with { Generation = new(0) };
        s.Provider.AfterGuestMap = () =>
        {
            parentKey = Assert.Single(parents.Keys.Cast<object>());
            parentOwner = parents[parentKey]!;
            parents.Remove(parentKey);
            children.Remove(child.BindingId);
            if (outcome == 0) throw new IOException("Original guest-map reply lost after effect.");
        };
        Assert.False(s.Kernel.MapGuestRegion(s.Owner, domain.Domain, domain.MemoryCapability, cap,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read).IsSuccess);
        var facts = (global::System.Collections.IEnumerable)childOwner.GetType().GetProperty("GuestMapInvocations")!.GetValue(childOwner)!;
        var fact = Assert.Single(facts.Cast<object>());
        Assert.True((bool)fact.GetType().GetProperty("Unresolved")!.GetValue(fact)!);
        Assert.Equal(outcome == 0, fact.GetType().GetProperty("Observation")!.GetValue(fact) is null);
        parents[parentKey] = parentOwner;
        children[child.BindingId] = childOwner;
        var parent = (PlatformRegionMapping)parentOwner.GetType().GetProperty("Mapping")!.GetValue(parentOwner)!;
        parentOwner.GetType().GetProperty("ClosureState")!.SetValue(parentOwner, PlatformExternalClosureState.Active);
        childOwner.GetType().GetProperty("State")!.SetValue(childOwner, PlatformChildDomainState.Created);
        s.Provider.AfterGuestMap = null;
        Assert.False(bridge.MapChildGuestRegion(child, parent, new(4096, 4096), PlatformGuestMemoryAccess.Read).IsSuccess);
        Assert.Equal(1, s.Provider.GuestMapCalls);
        childOwner.GetType().GetProperty("State")!.SetValue(childOwner, PlatformChildDomainState.Draining);
        Assert.False(bridge.CloseChildDomain(child).IsSuccess);
        Assert.Equal(0, s.Provider.ChildCloseCalls);
        Assert.False(s.Kernel.ObservePlatformRegionMappingRevocation(s.Owner, parent).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        Assert.True((bool)fact.GetType().GetProperty("Unresolved")!.GetValue(fact)!);
    }

    [Fact]
    public void GuestCompletionReentryAndExceptionRetainKnownOriginalWithoutRepeatedEffect()
    {
        var s = CreateScenario();
        var domain = CreateConfiguredRoot(s);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var cap = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        Assert.True(s.Kernel.MapGuestRegion(s.Owner, domain.Domain, domain.MemoryCapability, cap,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read).IsSuccess);
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        var parent = Assert.Single(local.PlatformMappings).Value.Parent;
        var bridge = s.Kernel.PlatformAuthority;
        PlatformGuestMapping? known = null;
        Assert.Throws<IOException>(() => bridge.MapChildGuestRegion(local.ChildBinding!.Value, parent,
            new(4096, 4096), PlatformGuestMemoryAccess.Read, out known,
            completionConsumer: (mapping, commit) =>
            {
                Assert.Equal(KernelError.PlatformBindingActive, bridge.CloseChildDomain(local.ChildBinding.Value).Error);
                throw new IOException("Completion composition failed before local commit.");
            }));
        Assert.NotNull(known);
        var originals = (global::System.Collections.IDictionary)ArtifactField(bridge, "_originalGuestOwners");
        var original = originals[known!.Value.MappingId]!;
        Assert.False((bool)original.GetType().GetProperty("AdmissionPublished")!.GetValue(original)!);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.True(bridge.UnmapChildGuestRegion(known.Value).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(2, s.Provider.GuestMapCalls);
        Assert.Equal(2, s.Provider.GuestUnmapCalls);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void GuestSourceCallbackOwnerLossRefusesActualMappingPublication(bool originalIndex, bool foreign)
    {
        var s = CreateScenario();
        var domain = CreateConfiguredRoot(s);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var cap = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var bridge = s.Kernel.PlatformAuthority;
        var routes = (global::System.Collections.IDictionary)ArtifactField(bridge,
            originalIndex ? "_originalGuestOwners" : "_guestBindings");
        object key = null!, original = null!;
        s.Provider.AfterGuestMap = () => s.Provider.BeforeIncarnationRead = () =>
        {
            s.Provider.BeforeIncarnationRead = null;
            key = Assert.Single(routes.Keys.Cast<object>());
            original = routes[key]!;
            if (foreign) routes[key] = typeof(object).GetMethod("MemberwiseClone",
                global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.Invoke(original, null)!;
            else routes.Remove(key);
        };
        var result = s.Kernel.MapGuestRegion(s.Owner, domain.Domain, domain.MemoryCapability, cap,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read);
        Assert.False(result.IsSuccess);
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        Assert.Single(local.StagedMappings);
        Assert.Single(local.PlatformMappings);
        Assert.False((bool)original.GetType().GetProperty("AdmissionPublished")!.GetValue(original)!);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.Equal(0, s.Provider.GuestUnmapCalls);
        s.Provider.AfterGuestMap = null;
        routes[key] = original;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.GuestMapCalls);
        Assert.Equal(1, s.Provider.GuestUnmapCalls);
        Assert.Empty(local.StagedMappings);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void GuestFinalPublicationConsumesOnlyOriginalRetainedRecord(bool originalIndex, bool foreign)
    {
        var s = CreateScenario();
        var domain = CreateConfiguredRoot(s);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var cap = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        Assert.True(s.Kernel.MapGuestRegion(s.Owner, domain.Domain, domain.MemoryCapability, cap,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read).IsSuccess);
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        var parent = Assert.Single(local.PlatformMappings).Value.Parent;
        var bridge = s.Kernel.PlatformAuthority;
        var routes = (global::System.Collections.IDictionary)ArtifactField(bridge,
            originalIndex ? "_originalGuestOwners" : "_guestBindings");
        object original = null!;
        bool published = false;
        var result = bridge.MapChildGuestRegion(local.ChildBinding!.Value, parent,
            new(4096, 4096), PlatformGuestMemoryAccess.Read, out var known,
            completionConsumer: (mapping, commit) =>
            {
                original = routes[mapping.MappingId]!;
                if (foreign) routes[mapping.MappingId] = typeof(object).GetMethod("MemberwiseClone",
                    global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.Invoke(original, null)!;
                else routes.Remove(mapping.MappingId);
                return commit(() => { published = true; return KernelResult.Ok(); });
            });
        Assert.False(result.IsSuccess);
        Assert.False(published);
        Assert.NotNull(known);
        Assert.False((bool)original.GetType().GetProperty("AdmissionPublished")!.GetValue(original)!);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        routes[known!.Value.MappingId] = original;
        Assert.True(bridge.UnmapChildGuestRegion(known.Value).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(2, s.Provider.GuestMapCalls);
        Assert.Equal(2, s.Provider.GuestUnmapCalls);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);
    }

    [Fact]
    public async Task ConcurrentGuestRouteLossRetainsChildClosureAndRejectsOverlappingCleanup()
    {
        var s = CreateScenario();
        var domain = CreateConfiguredRoot(s);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var cap = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        Assert.True(s.Kernel.MapGuestRegion(s.Owner, domain.Domain, domain.MemoryCapability, cap,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read).IsSuccess);
        s.Provider.MappingCompletionPending = true;
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        s.Provider.MappingCompletionPending = false;
        var bridge = s.Kernel.PlatformAuthority;
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        var child = local.ChildBinding!.Value;
        Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain, true).IsSuccess);
        var routes = (global::System.Collections.IDictionary)ArtifactField(bridge, "_guestBindings");
        var key = Assert.Single(routes.Keys.Cast<object>());
        var original = routes[key]!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        s.Provider.BeforeChildClose = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); };
        var work = Task.Run(() => bridge.CloseChildDomain(child));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            lock (ArtifactField(bridge, "_secureDomainLifecycleGate")) routes.Remove(key);
            Assert.Equal(KernelError.PlatformBindingActive, bridge.CloseChildDomain(child).Error);
            Assert.False(s.Kernel.ObservePlatformRegionMappingRevocation(s.Owner,
                Assert.Single(local.PlatformMappings).Value.Parent).IsSuccess);
            Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        }
        finally { release.Set(); }
        Assert.False((await work.WaitAsync(TimeSpan.FromSeconds(15))).IsSuccess);
        Assert.Equal(1, s.Provider.ChildCloseCalls);
        s.Provider.BeforeChildClose = null;
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        lock (ArtifactField(bridge, "_secureDomainLifecycleGate")) routes[key] = original;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.ChildCloseCalls);
        Assert.Equal(1, s.Provider.GuestUnmapCalls);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)] [InlineData(3, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)] [InlineData(3, true)]
    public void ForeignCompletedGuestCannotCloseOriginalChildOrCommitLocalCleanup(int boundary, bool absent)
    {
        var scenarios = new[] { CreateScenario(), CreateScenario() };
        foreach (var s in scenarios)
        {
            var domain = CreateConfiguredRoot(s);
            var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
            var cap = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
                CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
            Assert.True(s.Kernel.MapGuestRegion(s.Owner, domain.Domain, domain.MemoryCapability, cap,
                region.Handle, new(0, 4096), GuestMemoryAccess.Read).IsSuccess);
            s.Provider.MappingCompletionPending = true;
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            s.Provider.MappingCompletionPending = false;
        }
        var first = scenarios[0];
        var bridge = first.Kernel.PlatformAuthority;
        var routes = (global::System.Collections.IDictionary)ArtifactField(bridge, "_guestBindings");
        var key = Assert.Single(routes.Keys.Cast<object>());
        var original = routes[key]!;
        var foreign = Assert.Single(((global::System.Collections.IDictionary)ArtifactField(
            scenarios[1].Kernel.PlatformAuthority, "_guestBindings")).Values.Cast<object>());
        Assert.Equal(original.GetType().GetProperty("Mapping")!.GetValue(original),
            foreign.GetType().GetProperty("Mapping")!.GetValue(foreign));
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(first.Kernel, "_virtualDomains")).ForOwner(first.Owner));
        var child = local.ChildBinding!.Value;
        Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain, true).IsSuccess);
        if (boundary == 3) Assert.True(bridge.CloseChildDomain(child).IsSuccess);
        void LoseRoute() { if (absent) routes.Remove(key); else routes[key] = foreign; }
        if (boundary is 0 or 3) LoseRoute();
        if (boundary == 1) first.Provider.BeforeIncarnationRead = () =>
        {
            first.Provider.BeforeIncarnationRead = null;
            LoseRoute();
        };
        if (boundary == 2) first.Provider.BeforeChildClose = LoseRoute;
        bool consumed = false;
        var refused = boundary == 3
            ? bridge.CloseChildDomain(child, true, commit => commit(() => { consumed = true; return KernelResult.Ok(); }))
            : bridge.CloseChildDomain(child);
        Assert.False(refused.IsSuccess);
        Assert.False(consumed);
        if (boundary == 3) Assert.False(bridge.CloseChildDomain(child, true).IsSuccess);
        Assert.Equal(boundary >= 2 ? 1 : 0, first.Provider.ChildCloseCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(first.Kernel.Regions.Snapshot()).State);
        first.Provider.BeforeChildClose = null;
        routes[key] = original;
        Assert.True(first.Kernel.TerminateProcess(first.Owner).IsSuccess);
        Assert.Equal(1, first.Provider.ChildCloseCalls);
        Assert.Equal(1, first.Provider.GuestUnmapCalls);
        Assert.Equal(RegionState.Released, Assert.Single(first.Kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MissingGuestRoutePinsActualBackingUntilSameOwnerReturns(bool closed)
    {
        var s = CreateScenario();
        var domain = CreateConfiguredRoot(s);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var cap = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        Assert.True(s.Kernel.MapGuestRegion(s.Owner, domain.Domain, domain.MemoryCapability, cap,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read).IsSuccess);
        if (closed)
        {
            s.Provider.MappingCompletionPending = true;
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            s.Provider.MappingCompletionPending = false;
        }
        var routes = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_guestBindings");
        var key = Assert.Single(routes.Keys.Cast<object>());
        var original = routes[key]!;
        routes.Remove(key);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.Equal(closed ? 1 : 0, s.Provider.GuestUnmapCalls);
        Assert.Equal(0, s.Provider.ChildCloseCalls);
        routes[key] = original;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.GuestUnmapCalls);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void ForeignClosedGuestOwnerCannotAuthorizeOriginalBackingReclaim(bool duringRelease, bool absent)
    {
        var scenarios = new[] { CreateScenario(), CreateScenario() };
        foreach (var s in scenarios)
        {
            var domain = CreateConfiguredRoot(s);
            var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
            var cap = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
                CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
            Assert.True(s.Kernel.MapGuestRegion(s.Owner, domain.Domain, domain.MemoryCapability, cap,
                region.Handle, new(0, 4096), GuestMemoryAccess.Read).IsSuccess);
            s.Provider.MappingCompletionPending = true;
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            s.Provider.MappingCompletionPending = false;
        }
        var first = scenarios[0];
        var routes = (global::System.Collections.IDictionary)ArtifactField(first.Kernel.PlatformAuthority, "_guestBindings");
        var foreignRoutes = (global::System.Collections.IDictionary)ArtifactField(scenarios[1].Kernel.PlatformAuthority, "_guestBindings");
        var key = Assert.Single(routes.Keys.Cast<object>());
        var original = routes[key]!;
        var foreign = Assert.Single(foreignRoutes.Values.Cast<object>());
        Assert.Equal(original.GetType().GetProperty("Mapping")!.GetValue(original),
            foreign.GetType().GetProperty("Mapping")!.GetValue(foreign));
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(first.Kernel, "_virtualDomains")).ForOwner(first.Owner));
        var parent = Assert.Single(local.PlatformMappings).Value.Parent;
        void LoseRoute() { if (absent) routes.Remove(key); else routes[key] = foreign; }
        if (duringRelease)
            first.Provider.BeforeIncarnationRead = () =>
            {
                var lifecycle = first.Kernel.QueryPlatformRegionMappingLifecycle(first.Owner, parent);
                if (lifecycle.IsSuccess && lifecycle.Value!.PlatformClosure == PlatformExternalClosureState.Closed)
                {
                    first.Provider.BeforeIncarnationRead = null;
                    LoseRoute();
                }
            };
        else LoseRoute();
        Assert.False(first.Kernel.ObservePlatformRegionMappingRevocation(first.Owner, parent).IsSuccess);
        first.Provider.BeforeIncarnationRead = null;
        Assert.False(first.Kernel.TerminateProcess(first.Owner).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(first.Kernel.Regions.Snapshot()).State);
        Assert.Equal(1, first.Provider.GuestUnmapCalls);
        routes[key] = original;
        Assert.True(first.Kernel.TerminateProcess(first.Owner).IsSuccess);
        Assert.Equal(1, first.Provider.GuestUnmapCalls);
    }

    [Fact]
    public void FalseClosedGuestAfterLostUnmapCannotReleaseActualBacking()
    {
        var s = CreateScenario();
        var domain = CreateConfiguredRoot(s);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var cap = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        Assert.True(s.Kernel.MapGuestRegion(s.Owner, domain.Domain, domain.MemoryCapability, cap,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read).IsSuccess);
        s.Provider.BeforeGuestUnmap = () => throw new IOException("controlled original unmap reply loss");
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        s.Provider.BeforeGuestUnmap = null;
        var guests = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_guestBindings");
        var original = Assert.Single(guests.Values.Cast<object>());
        Assert.Null(original.GetType().GetProperty("LastClosureReceipt")!.GetValue(original));
        original.GetType().GetProperty("Closure")!.SetValue(original, PlatformExternalClosureState.Closed);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.Equal(1, s.Provider.GuestUnmapCalls);
        Assert.Equal(0, s.Provider.ChildCloseCalls);
        Assert.DoesNotContain("mapping-close", s.Provider.CallLog);
    }

    [Fact]
    public void ConsumedChildClosureDoesNotBecomeReclaimPinAfterBackendEpochChanges()
    {
        var s = CreateScenario();
        var domain = CreateConfiguredRoot(s);
        _ = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        Assert.True(s.Kernel.DestroyVirtualDomain(s.Owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        Assert.Equal(1, s.Provider.ChildCloseCalls);
        Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.Equal(1, s.Provider.ChildCloseCalls);
        Assert.Equal(1, s.Provider.CallLog.Count(call => call == "parent-close"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FalseClosedChildCannotAuthorizeActualRootRevoke(bool lostCloseReply)
    {
        var s = CreateScenario();
        var domain = CreateConfiguredRoot(s);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        var children = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_childBindings");
        var original = children[local.ChildBinding!.Value.BindingId]!;
        if (lostCloseReply)
        {
            Assert.True(s.Kernel.PlatformAuthority.TransitionChildDomain(local.ChildBinding.Value, PlatformChildDomainTransition.BeginDrain).IsSuccess);
            s.Provider.BeforeChildClose = () => throw new IOException("controlled original child close reply loss");
            Assert.False(s.Kernel.PlatformAuthority.CloseChildDomain(local.ChildBinding.Value).IsSuccess);
            s.Provider.BeforeChildClose = null;
        }
        Assert.Null(original.GetType().GetProperty("LastClosureReceipt")!.GetValue(original));
        var state = original.GetType().GetProperty("State")!;
        state.SetValue(original, PlatformChildDomainState.Closed);
        Assert.False(s.Kernel.RevokePlatformDomain(s.Owner, local.ParentBinding!.Value).IsSuccess);
        Assert.DoesNotContain("parent-close", s.Provider.CallLog);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.Equal(lostCloseReply ? 1 : 0, s.Provider.ChildCloseCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FalseClosedRootCannotDischargeUnpublishedOriginalChildInvocation(bool missingRoutes)
    {
        var s = CreateScenario();
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        s.Provider.MalformedRootCreate = true;
        Assert.False(s.Kernel.CreateVirtualDomain(s.Owner, s.CreateCapability, new(1, 8192)).IsSuccess);
        var domains = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_domains");
        var original = Assert.Single(domains.Values.Cast<object>());
        var state = original.GetType().GetProperty("AuthorityState")!;
        state.SetValue(original, Enum.Parse(state.PropertyType, "Closed"));
        if (missingRoutes)
        {
            domains.Clear();
            ((global::System.Collections.IDictionary)ArtifactField(s.Kernel, "_processPlatformBindings")).Remove(s.Owner);
        }
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
        Assert.True((bool)original.GetType().GetProperty("ChildCreateMayHaveEffect")!.GetValue(original)!);
        Assert.Single(((global::System.Collections.IEnumerable)original.GetType().GetProperty("UnpublishedChildCreates")!.GetValue(original)!).Cast<object>());
        Assert.Equal(1, s.Provider.ChildCloseCalls);
    }

    [Theory]
    [InlineData(false, false, false)] [InlineData(true, false, false)]
    [InlineData(false, true, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(true, false, true)]
    [InlineData(false, true, true)] [InlineData(true, true, true)]
    public async Task ConcurrentChildDestroyAndTeardownRetainOriginalCleanup(bool nested, bool closeStage, bool lostReply)
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var target = nested ? s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.GuestMemory)).Value! : root;
        if (nested) Assert.True(s.Kernel.ConfigureVirtualDomain(s.Owner, target.Domain, target.ConfigureCapability).IsSuccess);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var cap = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        Assert.True(s.Kernel.MapGuestRegion(s.Owner, target.Domain, target.MemoryCapability, cap,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read).IsSuccess);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        void Pause()
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
            if (lostReply) throw new IOException("controlled concurrent child cleanup reply loss");
        }
        if (closeStage) s.Provider.BeforeChildClose = Pause;
        else s.Provider.BeforeGuestUnmap = Pause;
        var first = Task.Run(() => s.Kernel.DestroyVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(1, s.Provider.GuestUnmapCalls);
            Assert.Equal(closeStage ? 1 : 0, s.Provider.ChildCloseCalls);
            Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
            Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        }
        finally { release.Set(); }
        _ = await first.WaitAsync(TimeSpan.FromSeconds(15));
        s.Provider.BeforeGuestUnmap = null;
        s.Provider.BeforeChildClose = null;
        var retry = s.Kernel.TerminateProcess(s.Owner);
        Assert.Equal(!lostReply, retry.IsSuccess);
        Assert.Equal(1, s.Provider.GuestUnmapCalls);
        Assert.Equal(lostReply ? (closeStage ? 1 : 0) : (nested ? 2 : 1), s.Provider.ChildCloseCalls);
        Assert.Equal(lostReply ? RegionState.Owned : RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        if (lostReply)
        {
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
            Assert.Equal(1, s.Provider.GuestUnmapCalls);
            Assert.Equal(closeStage ? 1 : 0, s.Provider.ChildCloseCalls);
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void CleanupRetryCannotConsumeChangedLocalMappingTuple(int fault)
    {
        var s = CreateScenario();
        var domain = CreateConfiguredRoot(s);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var other = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var cap = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var mapped = s.Kernel.MapGuestRegion(s.Owner, domain.Domain, domain.MemoryCapability, cap,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read).Value!;
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        s.Provider.MappingCompletionPending = true;
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.GuestUnmapCalls);
        Assert.Single(local.PlatformMappings);
        if (fault == 3) local.Mappings.Remove(mapped.Mapping.MappingId);
        else local.Mappings[mapped.Mapping.MappingId] = fault switch
        {
            0 => mapped with { Region = other.Handle },
            1 => mapped with { GuestRange = new(4096, 4096) },
            _ => mapped with { Mapping = mapped.Mapping with { Generation = new(2) } },
        };
        s.Provider.MappingCompletionPending = false;
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        if (fault != 3) Assert.Single(local.Mappings);
        Assert.Single(local.OriginalMappings);
        Assert.Single(local.PlatformMappings);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        Assert.All(s.Kernel.Regions.Snapshot(), item => Assert.Equal(RegionState.Owned, item.State));
        local.Mappings[mapped.Mapping.MappingId] = mapped;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.GuestUnmapCalls);
        Assert.All(s.Kernel.Regions.Snapshot(), item => Assert.Equal(RegionState.Released, item.State));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void GuestPublicationAfterProviderEffectRequiresOriginalVdaOwner(bool foreign)
    {
        var s = CreateScenario();
        var domain = CreateConfiguredRoot(s);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var cap = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var authority = (VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains");
        var local = Assert.Single(authority.ForOwner(s.Owner));
        var originals = (global::System.Collections.IDictionary)ArtifactField(authority, "_originalRecords");
        s.Provider.AfterGuestMap = () =>
        {
            s.Provider.AfterGuestMap = null;
            if (foreign) originals[domain.Domain.DomainId] = typeof(object).GetMethod("MemberwiseClone",
                global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.Invoke(local, null)!;
            else originals.Remove(domain.Domain.DomainId);
        };
        var result = s.Kernel.MapGuestRegion(s.Owner, domain.Domain, domain.MemoryCapability, cap,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read);
        Assert.False(result.IsSuccess);
        Assert.Equal(1, s.Provider.GuestMapCalls);
        Assert.Single(local.Mappings);
        Assert.Single(local.StagedMappings);
        Assert.Single(local.PlatformMappings);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        originals[domain.Domain.DomainId] = local;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.GuestMapCalls);
        Assert.Equal(1, s.Provider.GuestUnmapCalls);
        Assert.Empty(local.StagedMappings);
        Assert.Empty(local.Mappings);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void PlatformMappingCleanupKeepsLocalFactUntilExactVdaOwnerCommit(int fault)
    {
        var s = CreateScenario();
        var domain = CreateConfiguredRoot(s);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var cap = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var mapped = s.Kernel.MapGuestRegion(s.Owner, domain.Domain, domain.MemoryCapability, cap,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read);
        Assert.True(mapped.IsSuccess, mapped.Message);
        var authority = (VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains");
        var local = Assert.Single(authority.ForOwner(s.Owner));
        var originals = (global::System.Collections.IDictionary)ArtifactField(authority, "_originalRecords");
        var fact = Assert.Single(local.PlatformMappings);
        var originalMapping = Assert.Single(local.Mappings).Value;
        s.Provider.BeforeGuestUnmap = () =>
        {
            s.Provider.BeforeGuestUnmap = null;
            if (fault == 1) originals[domain.Domain.DomainId] = typeof(object).GetMethod("MemberwiseClone",
                global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.Invoke(local, null)!;
            else if (fault == 0) originals.Remove(domain.Domain.DomainId);
            else if (fault == 2) local.Mappings[fact.Key] = originalMapping with { Access = GuestMemoryAccess.Read | GuestMemoryAccess.Write };
            else local.PlatformMappings[fact.Key] = (fact.Value.Guest with { Generation = new(2) }, fact.Value.Parent);
        };
        Assert.False(s.Kernel.CloseGuestRegionMapping(s.Owner, domain.Domain, domain.MemoryCapability, mapped.Value!.Mapping).IsSuccess);
        Assert.Equal(1, s.Provider.GuestUnmapCalls);
        Assert.Single(local.PlatformMappings);
        Assert.Single(local.Mappings);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.GuestUnmapCalls);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        originals[domain.Domain.DomainId] = local;
        local.Mappings[fact.Key] = originalMapping;
        local.PlatformMappings[fact.Key] = fact.Value;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.GuestUnmapCalls);
        Assert.Empty(local.PlatformMappings);
        Assert.Empty(local.Mappings);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 2)]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)]
    [InlineData(0, 3)] [InlineData(0, 4)] [InlineData(0, 5)]
    [InlineData(1, 3)] [InlineData(1, 4)] [InlineData(1, 5)]
    [InlineData(2, 3)] [InlineData(2, 4)] [InlineData(2, 5)]
    [InlineData(0, 6)] [InlineData(1, 6)] [InlineData(2, 6)]
    public void KnownChildNonDrainOutcomeAllowsCleanupAfterExactRouteRepair(int transition, int outcome)
    {
        var s = CreateScenario();
        var domain = CreateConfiguredRoot(s);
        var mapped = outcome >= 3;
        var lostUnmap = outcome == 6;
        outcome %= 3;
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        if (mapped)
        {
            var cap = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
                CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
            Assert.True(s.Kernel.MapGuestRegion(s.Owner, domain.Domain, domain.MemoryCapability, cap,
                region.Handle, new(0, 4096), GuestMemoryAccess.Read).IsSuccess);
        }
        if (transition > 0) Assert.True(s.Kernel.StartVirtualDomain(s.Owner, domain.Domain, domain.ExecuteCapability).IsSuccess);
        if (transition > 1) Assert.True(s.Kernel.ParkVirtualDomain(s.Owner, domain.Domain, domain.ExecuteCapability).IsSuccess);
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        var child = local.ChildBinding!.Value;
        var routes = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_childBindings");
        var original = routes[child.BindingId]!;
        object foreign = null!;
        var calls = 0;
        var unknown = outcome == 1;
        s.Provider.ChildTransitionStatus = outcome switch
        {
            1 => PlatformAuthorityStatus.Faulted,
            2 => PlatformAuthorityStatus.NotAccepted,
            _ => null,
        };
        s.Provider.BeforeChildTransition = () =>
        {
            calls++;
            s.Provider.BeforeChildTransition = null;
            foreign = typeof(object).GetMethod("MemberwiseClone", global::System.Reflection.BindingFlags.Instance |
                global::System.Reflection.BindingFlags.NonPublic)!.Invoke(original, null)!;
            routes[child.BindingId] = foreign;
        };
        var result = transition switch
        {
            0 => s.Kernel.StartVirtualDomain(s.Owner, domain.Domain, domain.ExecuteCapability),
            1 => s.Kernel.ParkVirtualDomain(s.Owner, domain.Domain, domain.ExecuteCapability),
            _ => s.Kernel.ResumeVirtualDomain(s.Owner, domain.Domain, domain.ExecuteCapability),
        };
        Assert.False(result.IsSuccess);
        Assert.Equal(1, calls);
        Assert.Equal(false, original.GetType().GetProperty("TransitionInFlight")!.GetValue(original));
        Assert.Equal(true, foreign.GetType().GetProperty("TransitionInFlight")!.GetValue(foreign));
        Assert.NotNull(original.GetType().GetProperty("TransitionObservation")!.GetValue(original));
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(0, s.Provider.ChildCloseCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.Equal(0, s.Provider.GuestUnmapCalls);
        if (mapped) { Assert.Single(local.Mappings); Assert.Single(local.PlatformMappings); }
        routes[child.BindingId] = original;
        s.Provider.ChildTransitionStatus = null;
        if (lostUnmap) s.Provider.BeforeGuestUnmap = () => throw new IOException("Guest unmap receipt lost");
        Assert.Equal(!unknown && !lostUnmap, s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(unknown || lostUnmap ? 0 : 1, s.Provider.ChildCloseCalls);
        Assert.Equal(unknown ? 0 : 1, s.Provider.CallLog.Count(entry => entry == "root-drain"));
        Assert.Equal(unknown || lostUnmap ? RegionState.Owned : RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.Equal(mapped && !unknown ? 1 : 0, s.Provider.GuestUnmapCalls);
        if (mapped && (unknown || lostUnmap)) { Assert.Single(local.Mappings); Assert.Single(local.PlatformMappings); }
        if (mapped && !unknown && !lostUnmap) { Assert.Empty(local.Mappings); Assert.Empty(local.PlatformMappings); }
        if (lostUnmap)
        {
            s.Provider.BeforeGuestUnmap = null;
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(1, s.Provider.GuestUnmapCalls);
            Assert.Equal(0, s.Provider.ChildCloseCalls);
            Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        }
        Assert.Equal(true, foreign.GetType().GetProperty("TransitionInFlight")!.GetValue(foreign));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void NestedClosureRequiresExactOriginalAncestorIndexBeforePublication(bool deep, bool foreign)
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var parent = s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).Value!;
        Assert.True(s.Kernel.ConfigureVirtualDomain(s.Owner, parent.Domain, parent.ConfigureCapability).IsSuccess);
        var target = deep ? s.Kernel.CreateNestedVirtualDomain(s.Owner, parent.Domain, parent.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).Value! : parent;
        _ = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var locals = ((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner);
        var rootChild = Assert.Single(locals, record => record.Handle == root.Domain).ChildBinding!.Value;
        var leaf = Assert.Single(locals, record => record.Handle == target.Domain).ChildBinding!.Value;
        var routes = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_childBindings");
        var originals = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_originalChildOwners");
        var ancestor = originals[rootChild.BindingId]!;
        var original = routes[leaf.BindingId]!;
        s.Provider.BeforeChildClose = () =>
        {
            s.Provider.BeforeChildClose = null;
            if (foreign) originals[rootChild.BindingId] = typeof(object).GetMethod("MemberwiseClone",
                global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.Invoke(ancestor, null)!;
            else originals.Remove(rootChild.BindingId);
        };
        Assert.False(s.Kernel.DestroyVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability).IsSuccess);
        Assert.Equal(1, s.Provider.ChildCloseCalls);
        Assert.NotNull(original.GetType().GetProperty("LastClosureReceipt")!.GetValue(original));
        Assert.NotEqual(PlatformChildDomainState.Closed, original.GetType().GetProperty("State")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("CloseInFlight")!.GetValue(original));
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.ChildCloseCalls);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        originals[rootChild.BindingId] = ancestor;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(deep ? 3 : 2, s.Provider.ChildCloseCalls);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false, false, 0)] [InlineData(false, true, 0)]
    [InlineData(true, false, 0)] [InlineData(true, true, 0)]
    [InlineData(false, false, 1)] [InlineData(false, true, 1)]
    [InlineData(true, false, 1)] [InlineData(true, true, 1)]
    [InlineData(false, false, 2)] [InlineData(false, true, 2)]
    [InlineData(true, false, 2)] [InlineData(true, true, 2)]
    public void OwnChildClosureRouteLossRetainsExactFactAndNeverRepeatsClose(bool nested, bool absent, int outcome)
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var target = nested ? s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).Value! : root;
        _ = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner),
            record => record.Handle == target.Domain);
        var child = local.ChildBinding!.Value;
        var children = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_childBindings");
        var originals = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_originalChildOwners");
        var original = children[child.BindingId]!;
        var unknown = outcome == 1;
        var exact = outcome == 0;
        s.Provider.MalformedChildClosure = outcome == 2;
        object foreign = null!;
        s.Provider.BeforeChildClose = () =>
        {
            s.Provider.BeforeChildClose = null;
            foreign = typeof(object).GetMethod("MemberwiseClone", global::System.Reflection.BindingFlags.Instance |
                global::System.Reflection.BindingFlags.NonPublic)!.Invoke(original, null)!;
            if (absent) children.Remove(child.BindingId);
            else children[child.BindingId] = foreign;
            if (unknown) throw new IOException("Original Close reply lost after effect-capable invocation");
        };
        Assert.False(s.Kernel.DestroyVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability).IsSuccess);
        Assert.Same(original, originals[child.BindingId]);
        Assert.Equal(1, s.Provider.ChildCloseCalls);
        Assert.Equal(!unknown, original.GetType().GetProperty("LastClosureReceipt")!.GetValue(original) is not null);
        Assert.Equal(false, original.GetType().GetProperty("CloseInFlight")!.GetValue(original));
        Assert.Equal(true, foreign.GetType().GetProperty("CloseInFlight")!.GetValue(foreign));
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.ChildCloseCalls);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        children[child.BindingId] = original;
        Assert.Equal(exact, s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(exact && nested ? 2 : 1, s.Provider.ChildCloseCalls);
        Assert.Equal(exact ? RegionState.Released : RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.Equal(true, foreign.GetType().GetProperty("CloseInFlight")!.GetValue(foreign));
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(false, 1)] [InlineData(true, 1)]
    [InlineData(false, 2)] [InlineData(true, 2)]
    [InlineData(false, 3)] [InlineData(true, 3)]
    [InlineData(false, 4)] [InlineData(true, 4)]
    [InlineData(false, 5)] [InlineData(true, 5)]
    public void OwnChildDrainRetainsOriginalOutcomeAndPinsAcrossRouteLoss(bool absent, int outcome)
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var nested = outcome >= 3;
        outcome %= 3;
        var target = nested ? s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).Value! : root;
        _ = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner),
            record => record.Handle == target.Domain);
        var child = local.ChildBinding!.Value;
        var children = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_childBindings");
        var originals = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_originalChildOwners");
        var original = children[child.BindingId]!;
        object foreign = null!;
        s.Provider.ChildTransitionStatus = outcome == 1 ? PlatformAuthorityStatus.Faulted :
            outcome == 2 ? PlatformAuthorityStatus.NotAccepted : null;
        s.Provider.BeforeChildTransition = () =>
        {
            s.Provider.BeforeChildTransition = null;
            foreign = typeof(object).GetMethod("MemberwiseClone", global::System.Reflection.BindingFlags.Instance |
                global::System.Reflection.BindingFlags.NonPublic)!.Invoke(original, null)!;
            if (absent) children.Remove(child.BindingId);
            else children[child.BindingId] = foreign;
        };
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Same(original, originals[child.BindingId]);
        Assert.Equal(PlatformChildDomainTransition.BeginDrain, original.GetType().GetProperty("TransitionIntent")!.GetValue(original));
        var observation = (PlatformAuthorityResult)original.GetType().GetProperty("TransitionObservation")!.GetValue(original)!;
        Assert.Equal(outcome == 0 ? PlatformAuthorityStatus.Success : s.Provider.ChildTransitionStatus!.Value, observation.Status);
        Assert.Equal(false, original.GetType().GetProperty("TransitionInFlight")!.GetValue(original));
        Assert.Equal(true, foreign.GetType().GetProperty("TransitionInFlight")!.GetValue(foreign));
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(0, s.Provider.ChildCloseCalls);
        Assert.False(s.Kernel.PlatformAuthority.RevokeDomain(child.ParentBinding, child.ParentBinding.Subject).IsSuccess);
        Assert.DoesNotContain("parent-close", s.Provider.CallLog);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        children[child.BindingId] = original; // exact owner reference restoration; foreign interlock is never balanced
        s.Provider.ChildTransitionStatus = null;
        if (outcome == 1)
        {
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(1, s.Provider.CallLog.Count(entry => entry == (nested ? "nested-drain" : "root-drain")));
            Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        }
        else
        {
            Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(1, s.Provider.CallLog.Count(entry => entry == "root-drain"));
            Assert.Equal(nested ? 2 : 1, s.Provider.ChildCloseCalls);
            Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ChildAuthorityMintAndFinalPublicationRequireOriginalLocalOwner(bool afterMint)
    {
        var s = CreateScenario();
        var authority = (VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains");
        var originals = (global::System.Collections.IDictionary)ArtifactField(authority, "_originalRecords");
        VirtualDomainAuthority.Record local = null!;
        s.Kernel.BeforeVirtualAuthorityPublicationQualificationHook = () =>
        {
            local = Assert.Single(authority.ForOwner(s.Owner));
            s.Provider.BeforeIncarnationRead = () =>
            {
                if (local.Capabilities.Length != (afterMint ? 5 : 0)) return;
                Assert.False(Monitor.IsEntered(ArtifactField(s.Kernel, "_platformMemoryUseGate")));
                Assert.False(Monitor.IsEntered(ArtifactField(authority, "_gate")));
                s.Provider.BeforeIncarnationRead = null;
                originals.Remove(local.Handle.DomainId);
            };
        };
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var denied = s.Kernel.CreateVirtualDomain(s.Owner, s.CreateCapability, new(1, 4096));
        Assert.False(denied.IsSuccess);
        Assert.False(local.AuthorityPublished);
        Assert.Equal(afterMint ? 5 : 0, local.Capabilities.Length);
        Assert.Equal(0, s.Provider.ChildCloseCalls);
        if (afterMint)
            Assert.False(s.Kernel.ConfigureVirtualDomain(s.Owner, local.Handle, local.Capabilities[0]).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        s.Kernel.BeforeVirtualAuthorityPublicationQualificationHook = null;
        s.Provider.BeforeIncarnationRead = null;
        originals[local.Handle.DomainId] = local; // exact cleanup owner only; publication remains refused
        if (afterMint)
            Assert.False(s.Kernel.ConfigureVirtualDomain(s.Owner, local.Handle, local.Capabilities[0]).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.ChildCloseCalls);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void ChildClosureCannotConsumeLostOrFalselyClosedLocalOwner(bool nested, bool closed)
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var domain = nested ? s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).Value! : root;
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var authority = (VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains");
        var local = Assert.Single(authority.ForOwner(s.Owner), record => record.Handle == domain.Domain);
        var routes = (global::System.Collections.IDictionary)ArtifactField(authority, "_records");
        var originals = (global::System.Collections.IDictionary)ArtifactField(authority, "_originalRecords");
        s.Provider.BeforeChildClose = () =>
        {
            s.Provider.BeforeChildClose = null;
            Assert.False(Monitor.IsEntered(ArtifactField(s.Kernel, "_platformMemoryUseGate")));
            Assert.False(Monitor.IsEntered(ArtifactField(authority, "_gate")));
            if (closed) local.State = VirtualDomainState.Closed;
            else routes.Remove(domain.Domain.DomainId);
        };
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Same(local, originals[domain.Domain.DomainId]);
        Assert.True(authority.HasUnconsumedOriginalOwner(s.Owner));
        if (closed) local.State = VirtualDomainState.Closed; // simulate skipped inventory again after the first refusal quarantined it
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        Assert.Equal(1, s.Provider.ChildCloseCalls); // unfinished original child now also blocks parent provider closure
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        if (!closed) routes[domain.Domain.DomainId] = local;
        local.State = VirtualDomainState.Quarantined; // exact controlled owner repair, no second provider closure
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(nested ? 2 : 1, s.Provider.ChildCloseCalls);
        Assert.False(authority.HasUnconsumedOriginalOwner(s.Owner));
        Assert.False(originals.Contains(domain.Domain.DomainId));
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialNestedMintKeepsExactCleanupUntilActualClosure(bool loseReply)
    {
        var s = CreateScenario();
        var subjectDomain = s.Kernel.Processes.Resolve(s.Owner).Value!.DomainId;
        var root = CreateConfiguredRoot(s);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        typeof(CapabilityAuthority).GetField("_nextId", global::System.Reflection.BindingFlags.Instance |
            global::System.Reflection.BindingFlags.NonPublic)!.SetValue(s.Kernel.CapabilityAuthority, ulong.MaxValue);
        var denied = s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.Execution));
        Assert.Equal(KernelError.CapacityExhausted, denied.Error);
        var authority = (VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains");
        var leaf = Assert.Single(authority.ForOwner(s.Owner), x => x.ParentDomain == root.Domain);
        Assert.Equal(VirtualDomainState.Quarantined, leaf.State);
        Assert.False(leaf.AuthorityPublished);
        Assert.Equal(new CapabilityId(ulong.MaxValue), Assert.Single(leaf.Capabilities));
        Assert.Equal(0, s.Provider.ChildCloseCalls);
        if (loseReply) s.Provider.BeforeChildClose = () => throw new IOException("Nested closure reply lost.");
        var stopped = s.Kernel.TerminateProcess(s.Owner);
        Assert.Equal(!loseReply, stopped.IsSuccess);
        if (loseReply)
        {
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(1, s.Provider.ChildCloseCalls);
            Assert.DoesNotContain("parent-close", s.Provider.CallLog);
            Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
            Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
        }
        else
        {
            Assert.Equal(2, s.Provider.ChildCloseCalls);
            Assert.Equal(1, s.Provider.NestedCloseCalls);
            Assert.Equal(1, s.Provider.CallLog.Count(x => x == "parent-close"));
            Assert.Equal(VirtualDomainState.Closed, leaf.State);
            Assert.Equal(KernelError.CapabilityRevoked, s.Kernel.CapabilityAuthority.Validate(
                leaf.Capabilities[0], subjectDomain, s.Owner.Generation, CapabilityRights.Configure).Error);
            Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ActualChildKnownClosureRetryUsesOriginalReceiptAtDirectAndProcessConsumers(int mode)
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        if (mode == 1)
        {
            var leaf = s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
                new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).Value!;
            Assert.True(s.Kernel.ConfigureVirtualDomain(s.Owner, leaf.Domain, leaf.ConfigureCapability).IsSuccess);
        }
        _ = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        s.Provider.BeforeChildClose = () => s.Provider.ThrowIncarnation = true;
        KernelResult Close() => mode == 2 ? s.Kernel.TerminateProcess(s.Owner) :
            s.Kernel.DestroyVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability);
        Assert.False(Close().IsSuccess);
        Assert.Equal(1, s.Provider.ChildCloseCalls);
        Assert.DoesNotContain("parent-close", s.Provider.CallLog);
        s.Provider.ThrowIncarnation = false;
        s.Provider.BeforeChildClose = null;
        Assert.True(Close().IsSuccess);
        Assert.Equal(mode == 1 ? 2 : 1, s.Provider.ChildCloseCalls);
        Assert.Equal(1, s.Provider.CallLog.Count(x => x == "parent-close"));
        if (mode != 2) Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)]
    [InlineData(false, 3)] [InlineData(false, 4)] [InlineData(false, 5)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)]
    [InlineData(true, 3)] [InlineData(true, 4)] [InlineData(true, 5)]
    public void ActualChildCloseSourceRetainsKnownReceiptAndPinsUnknownOutcome(bool nested, int fault)
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var target = root;
        if (nested)
        {
            target = s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
                new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).Value!;
            Assert.True(s.Kernel.ConfigureVirtualDomain(s.Owner, target.Domain, target.ConfigureCapability).IsSuccess);
        }
        _ = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner),
            x => x.Handle == target.Domain);
        var children = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_childBindings");
        var record = children[local.ChildBinding!.Value.BindingId]!;
        var epoch = s.Kernel.PlatformAuthority.BackendEpoch;
        var outside = true;
        s.Provider.BeforeIncarnationRead = () => outside &=
            !Monitor.IsEntered(ArtifactField(s.Kernel, "_platformMemoryUseGate")) &&
            !Monitor.IsEntered(ArtifactField(s.Kernel.PlatformAuthority, "_secureDomainLifecycleGate"));
        if (fault == 3) s.Provider.Incarnation = new(2);
        if (fault == 5) s.Provider.ThrowIncarnation = true;
        s.Provider.BeforeChildClose = () =>
        {
            if (fault == 0) s.Provider.ThrowIncarnation = true;
            if (fault == 1) s.Provider.Incarnation = new(2);
            if (fault == 2) Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault == 4) throw new IOException("Child closure reply lost");
        };
        Assert.False(s.Kernel.DestroyVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability).IsSuccess);
        Assert.True(outside);
        Assert.Equal(fault is 3 or 5 ? 0 : 1, s.Provider.ChildCloseCalls);
        Assert.NotEqual(PlatformChildDomainState.Closed, record.GetType().GetProperty("State")!.GetValue(record));
        Assert.Equal(false, record.GetType().GetProperty("CloseInFlight")!.GetValue(record));
        var fact = record.GetType().GetProperty("LastClosureReceipt")!.GetValue(record);
        if (fault is 0 or 1 or 2)
        {
            Assert.NotNull(fact);
            Assert.Equal(epoch, fact.GetType().GetProperty("AdmissionEpoch")!.GetValue(fact));
            Assert.Equal(record.GetType().GetProperty("ProviderLease")!.GetValue(record),
                fact.GetType().GetProperty("AdmittedLease")!.GetValue(fact));
        }
        else Assert.Null(fact);
        Assert.DoesNotContain("parent-close", s.Provider.CallLog);
        Assert.True(s.Kernel.Processes.Resolve(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        s.Provider.ThrowIncarnation = false;
        s.Provider.BeforeChildClose = null;
        if (fault is not (0 or 5))
            Assert.False(s.Kernel.DestroyVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability).IsSuccess);
        Assert.Equal(fault is 0 or 5, s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(fault is 0 or 5 ? (nested ? 2 : 1) : fault is 3 ? 0 : 1, s.Provider.ChildCloseCalls);
        Assert.Equal(fault is 0 or 5 ? RegionState.Released : RegionState.Owned,
            Assert.Single(s.Kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ChildCloseSourcePauseInterlocksDuplicateAndRechecksReset(bool reset)
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        s.Provider.BeforeIncarnationRead = () =>
        {
            s.Provider.BeforeIncarnationRead = null;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(6))) throw new TimeoutException("Child source pause expired");
        };
        var work = Task.Run(() => s.Kernel.DestroyVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            await Task.Run(() =>
            {
                Assert.True(s.Kernel.QueryVirtualDomain(s.Owner, root.Domain).IsSuccess);
                Assert.False(s.Kernel.PlatformAuthority.CloseChildDomain(local.ChildBinding!.Value, true).IsSuccess);
                Assert.Equal(0, s.Provider.ChildCloseCalls);
                if (reset) Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
            }).WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { release.Set(); }
        Assert.Equal(!reset, (await work).IsSuccess);
        Assert.Equal(reset ? 0 : 1, s.Provider.ChildCloseCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OverlappingNestedCreatesRetainOnlyKnownExactReceiptsAfterReset(int lostInvocation)
    {
        var provider = new NestedProvider(null, false, false, false, false, false);
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1), new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory);
        var parent = bridge.CreateChildDomain(root, subject, intent).Value!;
        var epoch = bridge.BackendEpoch;
        using var firstEntered = new ManualResetEventSlim();
        using var bothEntered = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        var expected = new global::System.Collections.Concurrent.ConcurrentBag<PlatformProviderChildDomainLease>();
        var arrivals = 0;
        provider.AfterNestedMaterialize = () =>
        {
            Assert.False(global::System.Threading.Monitor.IsEntered(ArtifactField(bridge, "_secureDomainLifecycleGate")));
            var invocation = Interlocked.Increment(ref arrivals);
            if (invocation != lostInvocation) expected.Add(provider.LastNestedLease);
            bothEntered.Signal();
            firstEntered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
            if (invocation == lostInvocation) throw new InvalidOperationException("Materialized nested reply lost");
        };
        var first = Task.Run(() => bridge.CreateNestedChildDomain(parent, subject.Process, intent));
        Task<KernelResult<PlatformChildBinding>>? second = null;
        try
        {
            Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)));
            second = Task.Run(() => bridge.CreateNestedChildDomain(parent, subject.Process, intent));
            Assert.True(bothEntered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(KernelError.PlatformBindingActive, bridge.TransitionChildDomain(parent, PlatformChildDomainTransition.BeginDrain).Error);
            Assert.True(bridge.ObserveBackendReset().IsSuccess);
        }
        finally { release.Set(); }
        Assert.Equal(KernelError.PlatformFaulted, (await first.WaitAsync(TimeSpan.FromSeconds(15))).Error);
        Assert.NotNull(second);
        Assert.Equal(KernelError.PlatformFaulted, (await second.WaitAsync(TimeSpan.FromSeconds(15))).Error);
        var children = (global::System.Collections.IDictionary)ArtifactField(bridge, "_childBindings");
        var record = Assert.Single(children.Values.Cast<object>());
        var facts = ((global::System.Collections.IEnumerable)record.GetType().GetProperty("UnpublishedNestedCreates")!.GetValue(record)!).Cast<object>().ToArray();
        Assert.Equal(lostInvocation == 0 ? 2 : 1, facts.Length);
        var receipts = facts.Select(f => ((PlatformProviderNestedDomainLease)f.GetType().GetProperty("Receipt")!.GetValue(f)!).ChildLease).ToArray();
        Assert.Equal(expected.OrderBy(l => l.LeaseId.Value), receipts.OrderBy(l => l.LeaseId.Value));
        Assert.Equal(expected.Count, receipts.Select(l => l.LeaseId).Distinct().Count());
        Assert.All(facts, f => Assert.Equal(epoch, f.GetType().GetProperty("AdmissionEpoch")!.GetValue(f)));
        Assert.Equal(0, record.GetType().GetProperty("PendingChildEffects")!.GetValue(record));
        Assert.True(bridge.TryGetQuarantinedDomainBinding(subject, out var current));
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(current, subject).Error);
        Assert.Equal(0, provider.NestedCloseCalls);
        Assert.Equal(2, provider.NestedCreateCalls);
    }

    [Fact]
    public void NestedCreationHoldsEveryAncestorUntilFinalPublication()
    {
        var provider = new NestedProvider(null, false, false, false, false, false);
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1), new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory);
        var ancestor = bridge.CreateChildDomain(root, subject, intent).Value!;
        var parent = bridge.CreateNestedChildDomain(ancestor, subject.Process, intent).Value!;
        provider.AfterNestedMaterialize = () =>
        {
            Assert.False(global::System.Threading.Monitor.IsEntered(ArtifactField(bridge, "_secureDomainLifecycleGate")));
            Assert.Equal(KernelError.PlatformBindingActive, bridge.TransitionChildDomain(ancestor, PlatformChildDomainTransition.BeginDrain).Error);
        };
        var created = bridge.CreateNestedChildDomain(parent, subject.Process, intent);
        Assert.True(created.IsSuccess, created.Message);
        var children = (global::System.Collections.IDictionary)ArtifactField(bridge, "_childBindings");
        Assert.Equal(3, children.Count);
        var record = children.Values.Cast<object>().Single(r => Equals(r.GetType().GetProperty("Binding")!.GetValue(r), parent));
        Assert.Empty(((global::System.Collections.IEnumerable)record.GetType().GetProperty("UnpublishedNestedCreates")!.GetValue(record)!).Cast<object>());
        Assert.Equal(0, record.GetType().GetProperty("PendingChildEffects")!.GetValue(record));
        Assert.Equal(KernelError.PlatformDenied, bridge.CloseChildDomain(ancestor).Error);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(root, subject).Error);
        Assert.Equal(0, provider.NestedCloseCalls);
        Assert.Equal(2, provider.NestedCreateCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NestedReceiptFactsRetainMalformedWithoutReturnedLeaseCompensation(int fault)
    {
        var provider = new NestedProvider(fault == 2 ? PlatformAuthorityStatus.NotAccepted :
            fault == 3 ? PlatformAuthorityStatus.Faulted : null, false, false, false, fault == 1, false);
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1), new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory);
        var parent = bridge.CreateChildDomain(root, subject, intent).Value!;
        var children = (global::System.Collections.IDictionary)ArtifactField(bridge, "_childBindings");
        var record = Assert.Single(children.Values.Cast<object>());
        var result = bridge.CreateNestedChildDomain(parent, subject.Process, intent);
        Assert.Equal(fault == 0, result.IsSuccess);
        Assert.Equal(fault == 0 ? 2 : 1, children.Count);
        var receipts = ((global::System.Collections.IEnumerable)record.GetType().GetProperty("UnpublishedNestedCreates")!.GetValue(record)!).Cast<object>();
        if (fault == 1) Assert.Single(receipts); else Assert.Empty(receipts);
        Assert.Equal(0, record.GetType().GetProperty("PendingChildEffects")!.GetValue(record));
        Assert.Equal(1, provider.NestedCreateCalls);
        if (fault is 1 or 2 or 3)
        {
            Assert.Equal(KernelError.PlatformFaulted, bridge.CloseChildDomain(parent).Error);
            Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(root, subject).Error);
            Assert.Equal(0, provider.NestedCloseCalls);
            Assert.DoesNotContain("parent-close", provider.CallLog);
            return;
        }
        if (fault == 0)
        {
            Assert.True(bridge.TransitionChildDomain(result.Value!, PlatformChildDomainTransition.BeginDrain).IsSuccess);
            Assert.True(bridge.CloseChildDomain(result.Value!).IsSuccess);
        }
        Assert.Equal(fault <= 1 ? 1 : 0, provider.NestedCloseCalls);
        Assert.True(bridge.TransitionChildDomain(parent, PlatformChildDomainTransition.BeginDrain).IsSuccess);
        Assert.True(bridge.CloseChildDomain(parent).IsSuccess);
        Assert.True(bridge.RevokeDomain(root, subject).IsSuccess);
        Assert.Contains("parent-close", provider.CallLog);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 0)]
    public void NestedKnownReceiptRetentionDoesNotInventReceiptOnException(int fault, int count)
    {
        var provider = new NestedProvider(null, fault == 1, false, fault == 1, false, false);
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1), new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory);
        var parent = bridge.CreateChildDomain(root, subject, intent).Value!;
        var epoch = bridge.BackendEpoch;
        if (fault == 0) provider.AfterNestedMaterialize = () => Assert.True(bridge.ObserveBackendReset().IsSuccess);
        if (fault == 2) provider.ThrowAfterNestedMaterialize = true;
        Assert.Equal(KernelError.PlatformFaulted, bridge.CreateNestedChildDomain(parent, subject.Process, intent).Error);
        var children = (global::System.Collections.IDictionary)ArtifactField(bridge, "_childBindings");
        var record = Assert.Single(children.Values.Cast<object>());
        var receipts = ((global::System.Collections.IEnumerable)record.GetType().GetProperty("UnpublishedNestedCreates")!.GetValue(record)!).Cast<object>().ToArray();
        Assert.Equal(count, receipts.Length);
        if (count == 1)
        {
            var fact = Assert.Single(receipts);
            var lease = (PlatformProviderNestedDomainLease)fact.GetType().GetProperty("Receipt")!.GetValue(fact)!;
            Assert.Equal(provider.LastNestedLease, lease.ChildLease);
            Assert.Equal(parent, fact.GetType().GetProperty("Parent")!.GetValue(fact));
            Assert.Equal(epoch, fact.GetType().GetProperty("AdmissionEpoch")!.GetValue(fact));
            var request = (PlatformNestedDomainRequest)fact.GetType().GetProperty("Request")!.GetValue(fact)!;
            var admittedParent = (PlatformProviderChildDomainLease)record.GetType().GetProperty("ProviderLease")!.GetValue(record)!;
            Assert.Equal(admittedParent, request.ParentChildLease);
            Assert.Equal(intent, request.ChildIntent);
            Assert.Equal(admittedParent.LeaseId, lease.ImmediateParentLeaseId);
            Assert.Equal(admittedParent.Generation, lease.ImmediateParentGeneration);
        }
        Assert.Equal(0, record.GetType().GetProperty("PendingChildEffects")!.GetValue(record));
        Assert.True(bridge.ObserveBackendReset().IsSuccess);
        Assert.Same(record, Assert.Single(children.Values.Cast<object>()));
        Assert.Equal(KernelError.PlatformFaulted, bridge.CloseChildDomain(parent).Error);
        Assert.True(bridge.TryGetQuarantinedDomainBinding(subject, out var current));
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(current, subject).Error);
        Assert.Equal(0, provider.NestedCloseCalls);
        Assert.DoesNotContain("parent-close", provider.CallLog);
    }

    [Fact]
    public async Task ConcurrentGuestMappingStopRefusesOverlapAndLatePublicationThenRetriesClosure()
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var cap = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        s.Provider.AfterGuestMap = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); };
        var work = Task.Run(() => s.Kernel.MapGuestRegion(s.Owner, root.Domain, root.MemoryCapability, cap,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.MapGuestRegion(s.Owner, root.Domain,
                root.MemoryCapability, cap, region.Handle, new(0, 4096), GuestMemoryAccess.Read).Error);
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(0, s.Provider.GuestUnmapCalls);
        }
        finally { release.Set(); }
        Assert.False((await work.WaitAsync(TimeSpan.FromSeconds(15))).IsSuccess);
        Assert.Equal(1, s.Provider.GuestMapCalls);
        s.Provider.AfterGuestMap = null;
        var retry = s.Kernel.TerminateProcess(s.Owner);
        Assert.True(retry.IsSuccess, retry.Message);
        Assert.Equal(1, s.Provider.GuestUnmapCalls);
    }
    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(false, 1)] [InlineData(true, 1)]
    [InlineData(false, 2)] [InlineData(true, 2)]
    public void GuestMappingRechecksBothSourcesBeforeEachEffectAndPublication(bool regionSource, int phase)
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var cap = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var source = regionSource ? cap : root.MemoryCapability;
        Action revoke = () => Assert.True(s.Kernel.RevokeCapability(source).IsSuccess);
        if (phase == 0) revoke();
        else if (phase == 1) s.Provider.AfterParentMap = revoke;
        else s.Provider.AfterGuestMap = () =>
        {
            var authority = (VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains");
            var local = Assert.Single(authority.ForOwner(s.Owner));
            var staged = Assert.Single(local.Mappings).Value;
            Assert.Equal(KernelError.PlatformBindingActive, authority.ResolveMapping(local, staged.Mapping).Error);
            revoke();
        };
        Assert.False(s.Kernel.MapGuestRegion(s.Owner, root.Domain, root.MemoryCapability, cap,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read).IsSuccess);
        Assert.Equal(phase == 0 ? 0 : 1, s.Provider.ParentMapCalls);
        Assert.Equal(phase == 2 ? 1 : 0, s.Provider.GuestMapCalls);
        s.Provider.AfterParentMap = s.Provider.AfterGuestMap = null;
        Assert.False(s.Kernel.MapGuestRegion(s.Owner, root.Domain, root.MemoryCapability, cap,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read).IsSuccess);
        Assert.Equal(phase == 0 ? 0 : 1, s.Provider.ParentMapCalls);
        // Exact known cleanup succeeds, or a revocation consequence retains the pin;
        // neither local rollback nor source refusal invents provider closure.
        var localRecord = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        if (localRecord.Mappings.Count == 0) Assert.True(s.Kernel.ReleaseRegion(s.Owner, region).IsSuccess);
        else Assert.False(s.Kernel.ReleaseRegion(s.Owner, region).IsSuccess);
    }
    [Fact]
    public void GuestClosureCommittedBeforeParentDrainCanRetryRealProcessTeardown()
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var cap = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        Assert.True(s.Kernel.MapGuestRegion(s.Owner, root.Domain, root.MemoryCapability, cap,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read).IsSuccess);
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        var guest = Assert.Single(local.PlatformMappings).Value.Guest;
        s.Provider.MappingCompletionPending = true;
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.GuestUnmapCalls);
        Assert.Single(local.PlatformMappings);
        Assert.False(s.Kernel.ReleaseRegion(s.Owner, region).IsSuccess);
        Assert.False(s.Kernel.PlatformAuthority.UnmapChildGuestRegion(guest).IsSuccess); // strict V1 caller preserved
        Assert.False(s.Kernel.PlatformAuthority.UnmapChildGuestRegion(guest with { Generation = new(2) }, true).IsSuccess);
        Assert.False(s.Kernel.PlatformAuthority.UnmapChildGuestRegion(guest with { MappingId = new(99999) }, true).IsSuccess);
        s.Provider.MappingCompletionPending = false;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.GuestUnmapCalls); // no provider unmap replay
        Assert.Empty(local.PlatformMappings);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void KernelLateGuestLineageRefusalConsumesExactCleanupFactAndReleasesReservation(bool lostClosure)
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var leaf = s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.GuestMemory)).Value!;
        Assert.True(s.Kernel.ConfigureVirtualDomain(s.Owner, leaf.Domain, leaf.ConfigureCapability).IsSuccess);
        var authority = (VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains");
        var parent = Assert.Single(authority.ForOwner(s.Owner), x => x.Handle == root.Domain);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var capability = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        s.Provider.AfterGuestMap = () =>
        {
            Assert.False(s.Kernel.PlatformAuthority.TransitionChildDomain(parent.ChildBinding!.Value, PlatformChildDomainTransition.BeginDrain).IsSuccess);
            var routes = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_childBindings");
            var original = routes[parent.ChildBinding.Value.BindingId]!;
            original.GetType().GetProperty("State")!.SetValue(original, PlatformChildDomainState.Draining);
        };
        if (lostClosure) s.Provider.BeforeGuestUnmap = () => throw new InvalidOperationException("closure receipt lost");
        Assert.False(s.Kernel.MapGuestRegion(s.Owner, leaf.Domain, leaf.MemoryCapability, capability,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read).IsSuccess);
        Assert.Equal(1, s.Provider.GuestMapCalls);
        Assert.Equal(1, s.Provider.GuestUnmapCalls);
        var local = Assert.Single(authority.ForOwner(s.Owner), x => x.Handle == leaf.Domain);
        if (lostClosure)
        {
            Assert.Single(local.Mappings);
            Assert.Single(local.PlatformMappings);
            Assert.Equal(VirtualDomainState.Quarantined, local.State);
            Assert.False(s.Kernel.ReleaseRegion(s.Owner, region).IsSuccess);
        }
        else
        {
            Assert.Empty(local.Mappings);
            Assert.Empty(local.PlatformMappings);
            Assert.True(s.Kernel.ReleaseRegion(s.Owner, region).IsSuccess);
        }
        s.Provider.AfterGuestMap = null;
        Assert.Equal(!lostClosure, s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void GuestMappingAncestorDrainRefusesAdmissionOrPublicationAndRetainsKnownLease(bool during)
    {
        var provider = new NestedProvider(null, false, false, false, false, true);
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1), new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory);
        var parent = bridge.CreateChildDomain(root, subject, intent).Value!;
        var child = bridge.CreateNestedChildDomain(parent, subject.Process, intent).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(new DomainId(1), subject.ProcessGeneration), 4096);
        var mapping = bridge.MapOwnedRegion(root, subject, new CapabilityId(8), region, PlatformMemoryAccess.Read).Value!;
        Action drain = () =>
        {
            var transition = bridge.TransitionChildDomain(parent, PlatformChildDomainTransition.BeginDrain);
            Assert.Equal(!during, transition.IsSuccess);
            if (during)
            {
                var routes = (global::System.Collections.IDictionary)ArtifactField(bridge, "_childBindings");
                routes[parent.BindingId]!.GetType().GetProperty("State")!.SetValue(routes[parent.BindingId], PlatformChildDomainState.Draining);
            }
        };
        if (during) provider.AfterGuestMap = drain; else drain();
        Assert.False(bridge.MapChildGuestRegion(child, mapping, new(0, 4096), PlatformGuestMemoryAccess.Read).IsSuccess);
        Assert.Equal(during ? 1 : 0, provider.GuestMapCalls);
        var guests = (global::System.Collections.IDictionary)ArtifactField(bridge, "_guestBindings");
        Assert.Equal(during ? 1 : 0, guests.Count);
        var children = (global::System.Collections.IDictionary)ArtifactField(bridge, "_childBindings");
        Assert.Equal(0, children[child.BindingId]!.GetType().GetProperty("PendingChildEffects")!.GetValue(children[child.BindingId]));
        provider.AfterGuestMap = null;
        Assert.False(bridge.MapChildGuestRegion(child, mapping, new(0, 4096), PlatformGuestMemoryAccess.Read).IsSuccess);
        Assert.Equal(during ? 1 : 0, provider.GuestMapCalls);
        if (during)
        {
            var known = Assert.Single(guests.Values.Cast<object>());
            var guest = (PlatformGuestMapping)known.GetType().GetProperty("Mapping")!.GetValue(known)!;
            Assert.True(bridge.UnmapChildGuestRegion(guest).IsSuccess);
            Assert.Equal(1, provider.GuestUnmapCalls);
        }
    }
    [Fact]
    public void NestedContractRejectsAnyImmediateParentAmplification()
    {
        var root = ParentLease();
        var parentIntent = Intent(PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory);
        var parent = new PlatformProviderChildDomainLease(new(10), new(23), root, parentIntent);
        var amplified = Intent(PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.Execution);

        var result = PlatformNestedDomainContract.ValidateRequest(new(parent, amplified));

        Assert.Equal(PlatformAuthorityStatus.Denied, result.Status);
    }

    [Fact]
    public void NestedIdentityAndAuthorityAreValidatedBeforeProviderCallAndEpochsStayDistinct()
    {
        var scenario = CreateScenario();
        var root = CreateConfiguredRoot(scenario);
        var requested = new NestedVirtualDomainRequestProfile(new(1, 4096),
            VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.GuestMemory);

        Assert.Equal(KernelError.CapabilityNotFound,
            scenario.Kernel.CreateNestedVirtualDomain(scenario.Owner, root.Domain, default, requested).Error);
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Kernel.CreateNestedVirtualDomain(scenario.Owner,
                root.Domain with { Generation = new(2) }, root.ConfigureCapability, requested).Error);
        Assert.Equal(0, scenario.Provider.NestedCreateCalls);

        var nested = scenario.Kernel.CreateNestedVirtualDomain(
            scenario.Owner, root.Domain, root.ConfigureCapability, requested);

        Assert.True(nested.IsSuccess, nested.Message);
        Assert.Equal(1, scenario.Provider.NestedCreateCalls);
        Assert.Equal<ulong>(1, nested.Value!.Domain.Generation.Value);
        Assert.Equal<ulong>(47, scenario.Provider.LastNestedLease.Generation.Value);
        Assert.NotEqual(nested.Value.Domain.Generation.Value, scenario.Provider.LastNestedLease.Generation.Value);
        Assert.Equal(0UL, nested.Value.ExecuteCapability.Value);
        Assert.Equal(0UL, nested.Value.EventCapability.Value);
        Assert.Equal(0UL, nested.Value.TrapCapability.Value);
    }

    [Fact]
    public void NestedCreateReceiptLossPinsImmediateParentAndRoot()
    {
        var provider = new NestedProvider(null, false, false, false, false, false)
        {
            ThrowAfterNestedMaterialize = true,
        };
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1),
            new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory);
        var parent = bridge.CreateChildDomain(root, subject, intent).Value!;

        Assert.Equal(KernelError.PlatformFaulted,
            bridge.CreateNestedChildDomain(parent, subject.Process, intent).Error);
        Assert.Equal(KernelError.PlatformFaulted,
            bridge.TransitionChildDomain(parent, PlatformChildDomainTransition.BeginDrain).Error);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(root, subject).Error);
        Assert.DoesNotContain("parent-close", provider.CallLog);
    }

    [Fact]
    public void NestedCreateCallbackBlocksImmediateParentTransition()
    {
        var provider = new NestedProvider(null, false, false, false, false, false);
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1),
            new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory);
        var parent = bridge.CreateChildDomain(root, subject, intent).Value!;
        provider.AfterNestedMaterialize = () => Assert.Equal(KernelError.PlatformBindingActive,
            bridge.TransitionChildDomain(parent, PlatformChildDomainTransition.BeginDrain).Error);

        Assert.True(bridge.CreateNestedChildDomain(parent, subject.Process, intent).IsSuccess);
        Assert.Equal(1, provider.NestedCreateCalls);
    }

    [Fact]
    public void ResetInsideNestedCreateCallbackCannotPublishLateChild()
    {
        var provider = new NestedProvider(null, false, false, false, false, false);
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1),
            new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory);
        var parent = bridge.CreateChildDomain(root, subject, intent).Value!;
        provider.AfterNestedMaterialize = () => Assert.True(bridge.ObserveBackendReset().IsSuccess);

        Assert.Equal(KernelError.PlatformFaulted,
            bridge.CreateNestedChildDomain(parent, subject.Process, intent).Error);
        Assert.Equal(1, provider.NestedCreateCalls);
        Assert.Equal(KernelError.PlatformFaulted,
            bridge.TransitionChildDomain(parent, PlatformChildDomainTransition.BeginDrain).Error);
        Assert.True(bridge.TryGetQuarantinedDomainBinding(subject, out var current));
        Assert.NotEqual(root.Generation, current.Generation);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(current, subject).Error);
    }

    [Fact]
    public void ImmediateParentTransitionCallbackRejectsNestedCreateBeforeProvider()
    {
        var provider = new NestedProvider(null, false, false, false, false, false);
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1),
            new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory);
        var parent = bridge.CreateChildDomain(root, subject, intent).Value!;
        provider.BeforeChildTransition = () => Assert.Equal(KernelError.PlatformBindingActive,
            bridge.CreateNestedChildDomain(parent, subject.Process, intent).Error);

        Assert.True(bridge.TransitionChildDomain(parent,
            PlatformChildDomainTransition.BeginDrain).IsSuccess);
        Assert.Equal(0, provider.NestedCreateCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExecutableArtifactBindOrStartReceiptLossPinsChildAndMapping(bool start)
    {
        var provider = new NestedProvider(null, false, false, false, false, true)
        {
            ThrowAfterArtifactBind = !start,
            ThrowAfterArtifactStart = start,
        };
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1),
            new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle |
            PlatformChildAuthorityClass.Execution | PlatformChildAuthorityClass.GuestMemory);
        var child = bridge.CreateChildDomain(root, subject, intent).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(new DomainId(1), subject.ProcessGeneration), 4096);
        var parentMapping = bridge.MapOwnedRegion(root, subject, new CapabilityId(8), region,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var guest = bridge.MapChildGuestRegion(child, parentMapping,
            new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;

        if (start)
        {
            var artifact = bridge.BindChildExecutableArtifact(child, guest,
                new byte[] { 0x56, 0x33 }, 1).Value!;
            Assert.Equal(KernelError.PlatformFaulted,
                bridge.StartChildExecutableArtifact(artifact).Error);
        }
        else
        {
            Assert.Equal(KernelError.PlatformFaulted,
                bridge.BindChildExecutableArtifact(child, guest,
                    new byte[] { 0x56, 0x33 }, 1).Error);
        }
        Assert.Equal(KernelError.PlatformFaulted, bridge.ValidateMapping(parentMapping, subject).Error);
        Assert.Equal(KernelError.PlatformFaulted,
            bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).Error);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(root, subject).Error);
    }

    [Fact]
    public void RevokedParentMappingCannotReachExecutableChildStartProvider()
    {
        var provider = new NestedProvider(null, false, false, false, false, true);
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1),
            new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle |
            PlatformChildAuthorityClass.Execution | PlatformChildAuthorityClass.GuestMemory);
        var child = bridge.CreateChildDomain(root, subject, intent).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(new DomainId(1), subject.ProcessGeneration), 4096);
        var parentMapping = bridge.MapOwnedRegion(root, subject, new CapabilityId(8), region,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var guest = bridge.MapChildGuestRegion(child, parentMapping,
            new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;
        var artifact = bridge.BindChildExecutableArtifact(child, guest,
            new byte[] { 0x56, 0x33 }, 1).Value!;

        Assert.Single(bridge.BeginCapabilityRevocation(new CapabilityId(8), static id => id == new CapabilityId(8)));
        Assert.False(bridge.StartChildExecutableArtifact(artifact).IsSuccess);
        Assert.Equal(0, provider.ArtifactStartCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundArtifactPinsOnlyItsExactGuestMappingAfterCallbackSettles(bool start)
    {
        var provider = new NestedProvider(null, false, false, false, false, true);
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1),
            new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle |
            PlatformChildAuthorityClass.Execution | PlatformChildAuthorityClass.GuestMemory);
        var child = bridge.CreateChildDomain(root, subject, intent).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(new DomainId(1), subject.ProcessGeneration), 8192);
        var parent = bridge.MapOwnedRegion(root, subject, new CapabilityId(8), region,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var boundGuest = bridge.MapChildGuestRegion(child, parent,
            new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;
        var otherGuest = bridge.MapChildGuestRegion(child, parent,
            new(4096, 4096), PlatformGuestMemoryAccess.Read).Value!;
        var artifact = bridge.BindChildExecutableArtifact(child, boundGuest,
            new byte[] { 0x56, 0x33 }, 1).Value!;

        Assert.Equal(KernelError.PlatformBindingActive,
            bridge.UnmapChildGuestRegion(boundGuest).Error);
        Assert.Equal(0, provider.GuestUnmapCalls);
        Assert.True(bridge.UnmapChildGuestRegion(otherGuest).IsSuccess);
        Assert.Equal(1, provider.GuestUnmapCalls);
        if (start)
            Assert.True(bridge.StartChildExecutableArtifact(artifact).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive,
            bridge.UnmapChildGuestRegion(boundGuest).Error);
        Assert.Equal(1, provider.GuestUnmapCalls);
        Assert.True(bridge.TransitionChildDomain(child,
            PlatformChildDomainTransition.BeginDrain).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.CloseChildDomain(child).Error);
        Assert.Equal(KernelError.PlatformBindingActive,
            bridge.BeginRegionMappingRevocation(parent, subject,
                PlatformRegionRevocationPolicy.DrainBeforeRevoke).Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GuestUnmapCannotOvertakeExecutableBindOrStartCallback(bool start)
    {
        var provider = new NestedProvider(null, false, false, false, false, true);
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1),
            new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle |
            PlatformChildAuthorityClass.Execution | PlatformChildAuthorityClass.GuestMemory);
        var child = bridge.CreateChildDomain(root, subject, intent).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(new DomainId(1), subject.ProcessGeneration), 4096);
        var parent = bridge.MapOwnedRegion(root, subject, new CapabilityId(8), region,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var guest = bridge.MapChildGuestRegion(child, parent,
            new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;
        Action probe = () =>
        {
            Assert.Equal(KernelError.PlatformBindingActive,
                bridge.UnmapChildGuestRegion(guest).Error);
            Assert.Equal(KernelError.PlatformBindingActive,
                bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).Error);
        };

        if (start)
        {
            var artifact = bridge.BindChildExecutableArtifact(child, guest,
                new byte[] { 0x56, 0x33 }, 1).Value!;
            provider.AfterArtifactStart = probe;
            Assert.True(bridge.StartChildExecutableArtifact(artifact).IsSuccess);
        }
        else
        {
            provider.AfterArtifactBind = probe;
            Assert.True(bridge.BindChildExecutableArtifact(child, guest,
                new byte[] { 0x56, 0x33 }, 1).IsSuccess);
        }
    }

    [Fact]
    public void ReentrantExecutableStartCannotEnterProviderTwice()
    {
        var provider = new NestedProvider(null, false, false, false, false, true);
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1),
            new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle |
            PlatformChildAuthorityClass.Execution | PlatformChildAuthorityClass.GuestMemory);
        var child = bridge.CreateChildDomain(root, subject, intent).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(new DomainId(1), subject.ProcessGeneration), 4096);
        var parent = bridge.MapOwnedRegion(root, subject, new CapabilityId(8), region,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var guest = bridge.MapChildGuestRegion(child, parent,
            new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;
        var artifact = bridge.BindChildExecutableArtifact(child, guest,
            new byte[] { 0x56, 0x33 }, 1).Value!;
        provider.AfterArtifactStart = () => Assert.False(bridge.StartChildExecutableArtifact(artifact).IsSuccess);

        Assert.True(bridge.StartChildExecutableArtifact(artifact).IsSuccess);
        Assert.Equal(1, provider.ArtifactStartCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParentAuthorizationRevokedInsideExecutableCallbackPreventsPublication(bool start)
    {
        var provider = new NestedProvider(null, false, false, false, false, true);
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1),
            new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle |
            PlatformChildAuthorityClass.Execution | PlatformChildAuthorityClass.GuestMemory);
        var child = bridge.CreateChildDomain(root, subject, intent).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(new DomainId(1), subject.ProcessGeneration), 4096);
        var parent = bridge.MapOwnedRegion(root, subject, new CapabilityId(8), region,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var guest = bridge.MapChildGuestRegion(child, parent,
            new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;
        Action revoke = () => Assert.Single(bridge.BeginCapabilityRevocation(new CapabilityId(8), static id => id == new CapabilityId(8)));

        if (start)
        {
            var artifact = bridge.BindChildExecutableArtifact(child, guest,
                new byte[] { 0x56, 0x33 }, 1).Value!;
            provider.AfterArtifactStart = revoke;
            Assert.Equal(KernelError.PlatformFaulted, bridge.StartChildExecutableArtifact(artifact).Error);
        }
        else
        {
            provider.AfterArtifactBind = revoke;
            Assert.Equal(KernelError.PlatformFaulted, bridge.BindChildExecutableArtifact(child, guest,
                new byte[] { 0x56, 0x33 }, 1).Error);
        }
        Assert.Equal(KernelError.PlatformFaulted, bridge.UnmapChildGuestRegion(guest).Error);
    }

    [Fact]
    public void WrongOwnerAndAmplifiedRuntimeRequestNeverReachNestedProvider()
    {
        var scenario = CreateScenario();
        var root = CreateConfiguredRoot(scenario);
        var other = TestFixtures.Create(scenario.Kernel, 502, 5002).Handle;
        var request = new NestedVirtualDomainRequestProfile(new(1, 4096),
            VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.Io);

        Assert.Equal(KernelError.WrongVirtualDomainOwner,
            scenario.Kernel.CreateNestedVirtualDomain(other, root.Domain, root.ConfigureCapability, request).Error);
        Assert.Equal(KernelError.PlatformDenied,
            scenario.Kernel.CreateNestedVirtualDomain(scenario.Owner, root.Domain,
                root.ConfigureCapability, request).Error);
        Assert.Equal(0, scenario.Provider.NestedCreateCalls);
    }

    [Fact]
    public async Task OrdinaryProcessFileAndNetworkSipRoutesDoNotAllocateNestedAuthority()
    {
        var scenario = CreateScenario();
        var processService = TestFixtures.Create(scenario.Kernel, 504, 5004).Handle;
        var processCapability = scenario.Kernel.MintCapability(new DomainId(5001), scenario.Owner,
            ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute).Value!.CapabilityId;
        var processProtocol = IProcessServiceProtocol.CreateDefinition();
        var processDescriptor = scenario.Kernel.RegisterService(processService, "ordinary-process",
            new(processProtocol.ContractName, "1", processProtocol.ContractDigest), processProtocol,
            IProcessServiceResponseProtocol.Definition,
            [new(ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute)]).Value!;
        var processSession = scenario.Kernel.OpenSession(scenario.Owner, processDescriptor, [processCapability]).Value;
        var processHost = RuntimeProcessServiceHost.CreateForSession(scenario.Kernel, processService, processSession).Value!;
        var processClient = IProcessServiceRuntimeClient.Create(new RuntimeSipClientTransport(scenario.Kernel, scenario.Owner, processSession));
        var creating = processClient.CreateAsync(new(processCapability, TestFixtures.Manifest(506, 5006, 1, "ordinary-child"), ChildProcessPolicy.Attached)).AsTask();
        Assert.True(processHost.ProcessNext().IsSuccess);
        Assert.NotEqual(default, (await creating).Authority.Process);

        var fileService = TestFixtures.Create(scenario.Kernel, 507, 5007).Handle;
        var fileCapability = scenario.Kernel.MintCapability(new DomainId(5001), scenario.Owner,
            ResourceKind.File, CapabilityResourceIds.FileNamespace, CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var fileProtocol = IFileServiceProtocol.CreateDefinition();
        var fileDescriptor = scenario.Kernel.RegisterService(fileService, "ordinary-file",
            new(fileProtocol.ContractName, "1", fileProtocol.ContractDigest), fileProtocol,
            IFileServiceResponseProtocol.Definition,
            [new(ResourceKind.File, CapabilityResourceIds.FileNamespace, CapabilityRights.Read | CapabilityRights.Write)]).Value!;
        var fileSession = scenario.Kernel.OpenSession(scenario.Owner, fileDescriptor, [fileCapability]).Value;
        var fileHost = RuntimeFileServiceHost.CreateForSession(scenario.Kernel, fileService, fileSession).Value!;
        var fileOpen = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(scenario.Kernel, scenario.Owner, fileSession)).OpenAsync(new(fileCapability, "/ordinary", true)).AsTask();
        Assert.True(fileHost.ProcessNext().IsSuccess);
        Assert.NotEqual(default, (await fileOpen).Authority.File);

        var networkService = TestFixtures.Create(scenario.Kernel, 508, 5008).Handle;
        var networkCapability = scenario.Kernel.MintCapability(new DomainId(5001), scenario.Owner,
            ResourceKind.Network, CapabilityResourceIds.NetworkEndpoint, CapabilityRights.Configure).Value!.CapabilityId;
        var networkProtocol = INetworkServiceProtocol.CreateDefinition();
        var networkDescriptor = scenario.Kernel.RegisterService(networkService, "ordinary-network",
            new(networkProtocol.ContractName, "1", networkProtocol.ContractDigest), networkProtocol,
            INetworkServiceResponseProtocol.Definition,
            [new(ResourceKind.Network, CapabilityResourceIds.NetworkEndpoint, CapabilityRights.Configure)]).Value!;
        var networkSession = scenario.Kernel.OpenSession(scenario.Owner, networkDescriptor, [networkCapability]).Value;
        var networkHost = RuntimeNetworkServiceHost.CreateForSession(scenario.Kernel, networkService, networkSession).Value!;
        var socket = INetworkServiceRuntimeClient.Create(new RuntimeSipClientTransport(scenario.Kernel, scenario.Owner, networkSession)).OpenAsync(new(networkCapability, "ordinary:loopback")).AsTask();
        Assert.True(networkHost.ProcessNext().IsSuccess);
        Assert.NotEqual(default, (await socket).Authority.Socket);

        Assert.Equal(0, scenario.Provider.NestedCreateCalls);
    }

    [Theory]
    [InlineData(PlatformAuthorityStatus.Denied)]
    [InlineData(PlatformAuthorityStatus.Revoked)]
    [InlineData(PlatformAuthorityStatus.Faulted)]
    [InlineData((PlatformAuthorityStatus)999)]
    public void ProviderNestedAdmissionFailuresFailClosed(PlatformAuthorityStatus status)
    {
        var scenario = CreateScenario(status);
        var root = CreateConfiguredRoot(scenario);
        var request = new NestedVirtualDomainRequestProfile(new(1, 4096),
            VirtualDomainAuthorityClass.Lifecycle);

        var nested = scenario.Kernel.CreateNestedVirtualDomain(
            scenario.Owner, root.Domain, root.ConfigureCapability, request);

        Assert.False(nested.IsSuccess);
        Assert.Equal(1, scenario.Provider.NestedCreateCalls);
    }

    [Fact]
    public void MalformedNestedLeaseQuarantinesParentAndCannotBecomeReclaimAuthority()
    {
        var scenario = CreateScenario(malformedNestedLease: true);
        var root = CreateConfiguredRoot(scenario);
        var request = new NestedVirtualDomainRequestProfile(new(1, 4096),
            VirtualDomainAuthorityClass.Lifecycle);

        var nested = scenario.Kernel.CreateNestedVirtualDomain(
            scenario.Owner, root.Domain, root.ConfigureCapability, request);

        Assert.Equal(KernelError.PlatformFaulted, nested.Error);
        Assert.Equal(KernelError.InvalidTransition,
            scenario.Kernel.StartVirtualDomain(scenario.Owner, root.Domain, root.ExecuteCapability).Error);
    }

    [Fact]
    public void WrongImmediateParentNestedReceiptQuarantinesParentImmediately()
    {
        var scenario = CreateScenario(wrongNestedImmediateParent: true);
        var root = CreateConfiguredRoot(scenario);

        var nested = scenario.Kernel.CreateNestedVirtualDomain(scenario.Owner, root.Domain,
            root.ConfigureCapability, new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle));

        Assert.Equal(KernelError.PlatformFaulted, nested.Error);
        Assert.Equal(VirtualDomainState.Quarantined,
            scenario.Kernel.QueryVirtualDomain(scenario.Owner, root.Domain).Value);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.DestroyVirtualDomain(scenario.Owner, root.Domain, root.ConfigureCapability).Error);
    }

    [Theory]
    [InlineData(PlatformAuthorityStatus.WrongDomain)]
    [InlineData((PlatformAuthorityStatus)999)]
    public void WrongDomainAndUnknownNestedProviderOutcomeQuarantineParentImmediately(PlatformAuthorityStatus status)
    {
        var scenario = CreateScenario(status);
        var root = CreateConfiguredRoot(scenario);

        Assert.False(scenario.Kernel.CreateNestedVirtualDomain(scenario.Owner, root.Domain,
            root.ConfigureCapability, new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).IsSuccess);
        Assert.Equal(VirtualDomainState.Quarantined,
            scenario.Kernel.QueryVirtualDomain(scenario.Owner, root.Domain).Value);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task GuestUnmapSourcePauseInterlocksDuplicateAndRechecksActualReset(bool reset)
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var cap = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var mapped = s.Kernel.MapGuestRegion(s.Owner, root.Domain, root.MemoryCapability,
            cap, region.Handle, new(0, 4096), GuestMemoryAccess.Read).Value!;
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        var pair = local.PlatformMappings[mapped.Mapping.MappingId];
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        s.Provider.BeforeIncarnationRead = () =>
        {
            s.Provider.BeforeIncarnationRead = null;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(6))) throw new TimeoutException("Controlled source pause expired");
        };
        var work = Task.Run(() => s.Kernel.DestroyVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            await Task.Run(() =>
            {
                Assert.True(s.Kernel.QueryVirtualDomain(s.Owner, root.Domain).IsSuccess);
                Assert.False(s.Kernel.DestroyVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability).IsSuccess);
                Assert.False(s.Kernel.PlatformAuthority.UnmapChildGuestRegion(pair.Guest, allowCommittedClosure: true).IsSuccess);
                Assert.Equal(0, s.Provider.GuestUnmapCalls);
                Assert.True(s.Kernel.Regions.HasPlatformMappingReservation(region.Handle, new RegionOwner(new(5001), s.Owner.Generation)));
                if (reset) Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
            }).WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { release.Set(); }
        Assert.Equal(!reset, (await work).IsSuccess);
        Assert.Equal(reset ? 0 : 1, s.Provider.GuestUnmapCalls);
        if (reset)
        {
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.True(s.Kernel.Regions.HasPlatformMappingReservation(region.Handle, new RegionOwner(new(5001), s.Owner.Generation)));
        }
        else Assert.True(s.Kernel.ReleaseRegion(s.Owner, region).IsSuccess);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void ActualGuestUnmapSourceRefusalRetainsOriginalReceiptOrPreEffectPin(int fault)
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var cap = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var mapped = s.Kernel.MapGuestRegion(s.Owner, root.Domain, root.MemoryCapability,
            cap, region.Handle, new(0, 4096), GuestMemoryAccess.Read).Value!;
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        var pair = local.PlatformMappings[mapped.Mapping.MappingId];
        var epoch = s.Kernel.PlatformAuthority.BackendEpoch;
        var records = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_guestBindings");
        var record = Assert.Single(records.Values.Cast<object>());
        var outside = true;
        s.Provider.BeforeIncarnationRead = () => outside &=
            !Monitor.IsEntered(ArtifactField(s.Kernel, "_platformMemoryUseGate")) &&
            !Monitor.IsEntered(ArtifactField(s.Kernel.PlatformAuthority, "_secureDomainLifecycleGate"));
        if (fault == 3) s.Provider.Incarnation = new(2);
        if (fault == 5) s.Provider.ThrowIncarnation = true;
        s.Provider.BeforeGuestUnmap = () =>
        {
            if (fault == 0) s.Provider.ThrowIncarnation = true;
            if (fault == 1) s.Provider.Incarnation = new(2);
            if (fault == 2) s.Kernel.ObservePlatformBackendReset();
            if (fault == 4) throw new IOException("Guest unmap reply lost");
        };
        Assert.False(s.Kernel.DestroyVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability).IsSuccess);
        Assert.True(outside);
        Assert.Equal(fault is 3 or 5 ? 0 : 1, s.Provider.GuestUnmapCalls);
        Assert.NotEqual(PlatformExternalClosureState.Closed, record.GetType().GetProperty("Closure")!.GetValue(record));
        Assert.True(s.Kernel.Regions.HasPlatformMappingReservation(region.Handle,
            new RegionOwner(new(5001), s.Owner.Generation)));
        Assert.DoesNotContain("mapping-close", s.Provider.CallLog);
        var fact = record.GetType().GetProperty("LastClosureReceipt")!.GetValue(record);
        if (fault is 0 or 1 or 2)
        {
            Assert.NotNull(fact);
            Assert.Equal(epoch, fact.GetType().GetProperty("AdmissionEpoch")!.GetValue(fact));
            Assert.Equal(record.GetType().GetProperty("ProviderLease")!.GetValue(record),
                fact.GetType().GetProperty("AdmittedLease")!.GetValue(fact));
        }
        else Assert.Null(fact);
        s.Provider.BeforeGuestUnmap = null;
        s.Provider.ThrowIncarnation = false;
        if (fault is 0 or 5)
        {
            Assert.True(s.Kernel.DestroyVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability).IsSuccess);
            Assert.Equal(1, s.Provider.GuestUnmapCalls);
            Assert.Equal(1, s.Provider.CallLog.Count(x => x == "mapping-close"));
            Assert.True(s.Kernel.ReleaseRegion(s.Owner, region).IsSuccess);
        }
        else
        {
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.True(s.Kernel.Regions.HasPlatformMappingReservation(region.Handle,
                new RegionOwner(new(5001), s.Owner.Generation)));
            Assert.DoesNotContain("parent-close", s.Provider.CallLog);
            Assert.Equal(fault == 3 ? 0 : 1, s.Provider.GuestUnmapCalls);
        }
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void ActualVirtualParentClosedFactRetriesWithoutDuplicateGuestOrParentEffect(int mode)
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var target = root;
        if (mode == 1)
        {
            target = s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
                new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.GuestMemory)).Value!;
            Assert.True(s.Kernel.ConfigureVirtualDomain(s.Owner, target.Domain, target.ConfigureCapability).IsSuccess);
        }
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var cap = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var mapping = s.Kernel.MapGuestRegion(s.Owner, target.Domain, target.MemoryCapability,
            cap, region.Handle, new(0, 4096), GuestMemoryAccess.Read).Value!;
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner),
            r => r.Handle == target.Domain);
        var parent = local.PlatformMappings[mapping.Mapping.MappingId].Parent;
        var subject = parent.DomainBinding.Subject;
        var reads = 0;
        var outside = true;
        s.Provider.BeforeIncarnationRead = () =>
        {
            var state = s.Kernel.PlatformAuthority.QueryRegionMappingLifecycle(parent, subject);
            if (!state.IsSuccess || state.Value.PlatformClosure != PlatformExternalClosureState.Closed) return;
            reads++;
            outside &= !Monitor.IsEntered(ArtifactField(s.Kernel, "_platformMemoryUseGate")) &&
                !Monitor.IsEntered(ArtifactField(s.Kernel.PlatformAuthority, "_secureDomainLifecycleGate"));
            s.Provider.BeforeIncarnationRead = null;
            if (mode == 4) s.Kernel.ObservePlatformBackendReset();
            else if (mode == 5) s.Provider.Incarnation = new(2);
            else s.Provider.ThrowIncarnation = true;
        };
        KernelResult Close() => mode == 2 ? s.Kernel.TerminateProcess(s.Owner) : mode == 3
            ? s.Kernel.CloseGuestRegionMapping(s.Owner, target.Domain, target.MemoryCapability, mapping.Mapping)
            : s.Kernel.DestroyVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability);
        Assert.False(Close().IsSuccess);
        Assert.Equal(1, reads);
        Assert.True(outside);
        Assert.True(s.Kernel.Processes.Resolve(s.Owner).IsSuccess);
        Assert.True(s.Kernel.Regions.HasPlatformMappingReservation(region.Handle, new RegionOwner(subject.DomainId, s.Owner.Generation)));
        Assert.Equal(1, s.Provider.GuestUnmapCalls);
        Assert.Equal(1, s.Provider.CallLog.Count(x => x == "mapping-close"));
        Assert.DoesNotContain("parent-close", s.Provider.CallLog);
        s.Provider.ThrowIncarnation = false;
        var retry = mode is 2 or 3 ? s.Kernel.TerminateProcess(s.Owner) : Close();
        Assert.Equal(mode < 4, retry.IsSuccess);
        Assert.Equal(1, s.Provider.GuestUnmapCalls);
        Assert.Equal(1, s.Provider.CallLog.Count(x => x == "mapping-close"));
        if (mode < 4)
        {
            Assert.False(s.Kernel.Regions.HasPlatformMappingReservation(region.Handle, new RegionOwner(subject.DomainId, s.Owner.Generation)));
            if (mode < 2) Assert.True(s.Kernel.ReleaseRegion(s.Owner, region).IsSuccess);
            Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        }
        else
        {
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.True(s.Kernel.Regions.HasPlatformMappingReservation(region.Handle, new RegionOwner(subject.DomainId, s.Owner.Generation)));
            Assert.DoesNotContain("parent-close", s.Provider.CallLog);
        }
    }
    [Fact]
    public void ParentDestroyClosesNestedMappingThenNestedThenRootBeforeParentReclaim()
    {
        var scenario = CreateScenario();
        var root = CreateConfiguredRoot(scenario);
        var nested = scenario.Kernel.CreateNestedVirtualDomain(scenario.Owner, root.Domain,
            root.ConfigureCapability, new(new(1, 4096),
                VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.GuestMemory)).Value!;
        Assert.True(scenario.Kernel.ConfigureVirtualDomain(scenario.Owner, nested.Domain,
            nested.ConfigureCapability).IsSuccess);
        var region = scenario.Kernel.AllocateRegion(scenario.Owner, 4096).Value!;
        var regionCapability = scenario.Kernel.MintCapability(new DomainId(5001), scenario.Owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        Assert.True(scenario.Kernel.MapGuestRegion(scenario.Owner, nested.Domain, nested.MemoryCapability,
            regionCapability, region.Handle, new(0, 4096), GuestMemoryAccess.Read).IsSuccess);
        scenario.Provider.CallLog.Clear();

        var destroyed = scenario.Kernel.DestroyVirtualDomain(
            scenario.Owner, root.Domain, root.ConfigureCapability);

        Assert.True(destroyed.IsSuccess, destroyed.Message);
        Assert.Equal(new[] { "guest-unmap", "mapping-close", "nested-drain", "nested-close", "root-drain", "root-close", "parent-close" },
            scenario.Provider.CallLog);
        Assert.True(scenario.Kernel.ReleaseRegion(scenario.Owner, region).IsSuccess);
        Assert.Equal(KernelError.VirtualDomainNotFound,
            scenario.Kernel.QueryVirtualDomain(scenario.Owner, nested.Domain).Error);
    }

    [Fact]
    public void StaleNestedMappingIsNeitherUsedNorReclaimed()
    {
        var scenario = CreateScenario();
        var root = CreateConfiguredRoot(scenario);
        var nested = scenario.Kernel.CreateNestedVirtualDomain(scenario.Owner, root.Domain,
            root.ConfigureCapability, new(new(1, 4096),
                VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.GuestMemory)).Value!;
        Assert.True(scenario.Kernel.ConfigureVirtualDomain(scenario.Owner, nested.Domain,
            nested.ConfigureCapability).IsSuccess);
        var region = scenario.Kernel.AllocateRegion(scenario.Owner, 4096).Value!;
        var regionCapability = scenario.Kernel.MintCapability(new DomainId(5001), scenario.Owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var mapping = scenario.Kernel.MapGuestRegion(scenario.Owner, nested.Domain, nested.MemoryCapability,
            regionCapability, region.Handle, new(0, 4096), GuestMemoryAccess.Read).Value!;
        var stale = mapping.Mapping with { Generation = new(mapping.Mapping.Generation.Value + 1) };

        Assert.Equal(KernelError.StaleGeneration,
            scenario.Kernel.CloseGuestRegionMapping(scenario.Owner, nested.Domain,
                nested.MemoryCapability, stale).Error);
        Assert.Equal(0, scenario.Provider.GuestUnmapCalls);
        Assert.Equal(KernelError.PlatformBindingActive,
            scenario.Kernel.ReleaseRegion(scenario.Owner, region).Error);
    }

    [Fact]
    public void AmbiguousNestedClosePinsParentAndCannotBeRetriedIntoCompletion()
    {
        var scenario = CreateScenario(malformedNestedClose: true);
        var root = CreateConfiguredRoot(scenario);
        Assert.True(scenario.Kernel.CreateNestedVirtualDomain(scenario.Owner, root.Domain,
            root.ConfigureCapability, new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).IsSuccess);

        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.DestroyVirtualDomain(scenario.Owner, root.Domain, root.ConfigureCapability).Error);
        int closes = scenario.Provider.NestedCloseCalls;
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.DestroyVirtualDomain(scenario.Owner, root.Domain, root.ConfigureCapability).Error);
        Assert.Equal(closes, scenario.Provider.NestedCloseCalls);
        Assert.Equal(VirtualDomainState.Draining,
            scenario.Kernel.QueryVirtualDomain(scenario.Owner, root.Domain).Value);
    }

    [Fact]
    public void TrapRouteRequiresExactCapabilityAndRejectsAfterDrainWithoutFakeObservation()
    {
        var scenario = CreateScenario();
        var root = CreateConfiguredRoot(scenario);
        var region = scenario.Kernel.AllocateRegion(scenario.Owner, 4096).Value!;
        var regionCapability = scenario.Kernel.MintCapability(new DomainId(5001), scenario.Owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var mapped = scenario.Kernel.MapGuestRegion(scenario.Owner, root.Domain, root.MemoryCapability,
            regionCapability, region.Handle, new(0, 4096), GuestMemoryAccess.Read);
        Assert.True(mapped.IsSuccess, mapped.Message);
        var mapping = mapped.Value!;
        Assert.True(scenario.Kernel.StartVirtualDomain(scenario.Owner, root.Domain, root.ExecuteCapability).IsSuccess);
        Assert.Equal(KernelError.CapabilityNotFound,
            scenario.Kernel.ObserveVirtualTrap(scenario.Owner, root.Domain, default).Error);
        Assert.Equal(0, scenario.Provider.TrapCalls);
        var trap = scenario.Kernel.ObserveVirtualTrap(scenario.Owner, root.Domain, root.TrapCapability);
        Assert.True(trap.IsSuccess, trap.Message);
        Assert.Equal(VirtualTrapKind.Timer, trap.Value!.Kind);
        Assert.Equal(root.Domain, trap.Value.Domain);
        Assert.Equal(1, scenario.Provider.TrapCalls);
        Assert.True(scenario.Kernel.DestroyVirtualDomain(scenario.Owner, root.Domain, root.ConfigureCapability).IsSuccess);
        Assert.Equal(KernelError.VirtualDomainNotFound,
            scenario.Kernel.ObserveVirtualTrap(scenario.Owner, root.Domain, root.TrapCapability).Error);
        Assert.Equal(1, scenario.Provider.TrapCalls);
    }

    [Fact]
    public void MalformedTrapEvidenceQuarantinesAndCannotActAsCompletion()
    {
        var scenario = CreateScenario(malformedTrap: true);
        var root = CreateConfiguredRoot(scenario);
        Assert.True(scenario.Kernel.StartVirtualDomain(scenario.Owner, root.Domain, root.ExecuteCapability).IsSuccess);

        var trap = scenario.Kernel.ObserveVirtualTrap(scenario.Owner, root.Domain, root.TrapCapability);

        Assert.Equal(KernelError.PlatformFaulted, trap.Error);
        Assert.Equal(VirtualDomainState.Quarantined,
            scenario.Kernel.QueryVirtualDomain(scenario.Owner, root.Domain).Value);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.DestroyVirtualDomain(scenario.Owner, root.Domain, root.ConfigureCapability).Error);
    }

    [Fact]
    public void FeatureFamiliesRemainIndependentAndVmxAbsenceDoesNotDisableNeutralDomains()
    {
        var host = new SingPlus.Platform.Host.HostPlatformAuthorityProvider();
        var manifest = host.QueryFeatures();

        Assert.Equal(PlatformFeatureAvailability.ModelOnly,
            manifest.Resolve(PlatformFeatureFamily.VirtualizationDomains).Availability);
        Assert.Equal(PlatformFeatureAvailability.ModelOnly,
            manifest.Resolve(PlatformFeatureFamily.GuestMemory).Availability);
        Assert.Equal(PlatformFeatureAvailability.ModelOnly,
            manifest.Resolve(PlatformFeatureFamily.VirtualEvents).Availability);
        Assert.Equal(PlatformFeatureAvailability.Unavailable,
            manifest.Resolve(PlatformFeatureFamily.NestedDomains).Availability);
        Assert.Equal(PlatformFeatureAvailability.Unavailable,
            manifest.Resolve(PlatformFeatureFamily.VirtualTraps).Availability);
        Assert.Equal(PlatformFeatureAvailability.Unavailable,
            manifest.Resolve(PlatformFeatureFamily.BoundedVirtualIo).Availability);
        Assert.Equal(PlatformFeatureAvailability.Unavailable,
            manifest.Resolve(PlatformFeatureFamily.VmxCompatibility).Availability);
    }

    [Fact]
    public async Task ExistingTypedSessionRoutesNestedAdmissionAndSemanticTrapObservation()
    {
        var scenario = CreateScenario();
        var service = TestFixtures.Create(scenario.Kernel, 503, 5003).Handle;
        var protocol = IVirtualizationServiceProtocol.CreateDefinition();
        var contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
        var descriptor = scenario.Kernel.RegisterService(service, "virtualization", contract, protocol,
            IVirtualizationServiceResponseProtocol.Definition,
            [new(ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure)]).Value!;
        var session = scenario.Kernel.OpenSession(scenario.Owner, descriptor, [scenario.CreateCapability]).Value;
        var host = RuntimeVirtualizationServiceHost.CreateForSession(scenario.Kernel, service, session).Value!;
        var client = IVirtualizationServiceRuntimeClient.Create(
            new RuntimeSipClientTransport(scenario.Kernel, scenario.Owner, session));

        var createTask = client.CreateAsync(new(scenario.CreateCapability, new(2, 8192))).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var root = (await createTask).Authority;
        await Dispatch(client.ConfigureAsync(new(root.Domain, root.ConfigureCapability)), host);
        var nestedTask = client.CreateNestedAsync(new(root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle))).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var nested = (await nestedTask).Authority;
        Assert.NotEqual(root.Domain, nested.Domain);

        await Dispatch(client.StartAsync(new(root.Domain, root.ExecuteCapability)), host);
        var trapTask = client.ObserveTrapAsync(new(root.Domain, root.TrapCapability)).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        Assert.Equal(VirtualTrapKind.Timer, (await trapTask).Trap.Kind);
        await Dispatch(client.DestroyAsync(new(root.Domain, root.ConfigureCapability)), host);
        Assert.True(scenario.Kernel.CloseSession(scenario.Owner, session).IsSuccess);
    }

    [Fact]
    public async Task TypedSipV3ArtifactContourPinsMappingAfterRetiredWorkUntilReleaseEvidence()
    {
        var scenario = CreateScenario(executableArtifact: true);
        var service = TestFixtures.Create(scenario.Kernel, 505, 5005).Handle;
        var protocol = IVirtualizationServiceProtocol.CreateDefinition();
        var contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
        var descriptor = scenario.Kernel.RegisterService(service, "virtualization.v3", contract, protocol,
            IVirtualizationServiceResponseProtocol.Definition,
            [new(ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure)]).Value!;
        var session = scenario.Kernel.OpenSession(scenario.Owner, descriptor, [scenario.CreateCapability]).Value;
        var host = RuntimeVirtualizationServiceHost.CreateForSession(scenario.Kernel, service, session).Value!;
        var client = IVirtualizationServiceRuntimeClient.Create(new RuntimeSipClientTransport(scenario.Kernel, scenario.Owner, session));

        var createTask = client.CreateAsync(new(scenario.CreateCapability, new(1, 4096))).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var domain = (await createTask).Authority;
        await Dispatch(client.ConfigureAsync(new(domain.Domain, domain.ConfigureCapability)), host);
        var region = scenario.Kernel.AllocateRegion(scenario.Owner, 4096).Value!;
        var regionCapability = scenario.Kernel.MintCapability(new DomainId(5001), scenario.Owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Execute).Value!.CapabilityId;
        var mapTask = client.MapGuestRegionAsync(new(domain.Domain, domain.MemoryCapability, regionCapability,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read | GuestMemoryAccess.Execute)).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var mapping = (await mapTask).Mapping;
        await Dispatch(client.BindExecutableArtifactAsync(new(domain.Domain, domain.ExecuteCapability,
            mapping.Mapping, new BoundedBytes([0x56, 0x33]), 1)), host);
        await Dispatch(client.StartAsync(new(domain.Domain, domain.ExecuteCapability)), host);
        Assert.NotNull(scenario.Provider.LastExecutionReceipt);
        Assert.True(scenario.Provider.LastExecutionReceipt!.Value.IsTerminal);
        Assert.True(scenario.Provider.LastExecutionReceipt!.Value.RetiredWorkUnits > 0);
        Assert.Equal(KernelError.PlatformBindingActive,
            scenario.Kernel.CloseGuestRegionMapping(scenario.Owner, domain.Domain,
                domain.MemoryCapability, mapping.Mapping).Error);
        Assert.Equal(0, scenario.Provider.GuestUnmapCalls);
        Assert.Equal(VirtualDomainState.Quarantined,
            scenario.Kernel.QueryVirtualDomain(scenario.Owner, domain.Domain).Value);
        Assert.False(scenario.Kernel.DestroyVirtualDomain(scenario.Owner, domain.Domain,
            domain.ConfigureCapability).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive,
            scenario.Kernel.ReleaseRegion(scenario.Owner, region).Error);
        Assert.True(scenario.Kernel.CloseSession(scenario.Owner, session).IsSuccess);
    }

    private static async Task Dispatch(ValueTask pending, RuntimeVirtualizationServiceHost host)
    {
        var task = pending.AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        await task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static (Scenario Scenario, VirtualDomainAuthoritySet Domain, GuestRegionMapping Mapping) ArtifactScenario()
    {
        var s = CreateScenario(executableArtifact: true);
        var domain = CreateConfiguredRoot(s);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var capability = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Execute).Value!.CapabilityId;
        var mapping = s.Kernel.MapGuestRegion(s.Owner, domain.Domain, domain.MemoryCapability, capability,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read | GuestMemoryAccess.Execute).Value!;
        return (s, domain, mapping);
    }

    private static object ArtifactField(object owner, string name) => owner.GetType().GetField(name,
        global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(owner)!;

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public void ExecutionReceiptCannotExceedAdmittedMaximumOrAuthorizeReclaim(int work)
    {
        var (s, domain, mapping) = ArtifactScenario();
        Assert.True(s.Kernel.BindVirtualDomainExecutable(s.Owner, domain.Domain, domain.ExecuteCapability,
            mapping.Mapping, new byte[] { 0x56, 0x33 }, 1).IsSuccess);
        s.Provider.ArtifactRetiredWorkUnits = work;
        var result = s.Kernel.StartVirtualDomain(s.Owner, domain.Domain, domain.ExecuteCapability);
        Assert.Equal(work == 1, result.IsSuccess);
        var record = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        Assert.Equal(work == 1, record.State == VirtualDomainState.Running);
        Assert.False(s.Kernel.StartVirtualDomain(s.Owner, domain.Domain, domain.ExecuteCapability).IsSuccess);
        Assert.Equal(1, s.Provider.ArtifactStartCalls);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        Assert.Equal(0, s.Provider.GuestUnmapCalls);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void ArtifactStartOriginalSourceRefusalRetainsExecutionAndNeverRepeatsKnownEffect(int fault)
    {
        var (s, domain, mapping) = ArtifactScenario();
        Assert.True(s.Kernel.BindVirtualDomainExecutable(s.Owner, domain.Domain,
            domain.ExecuteCapability, mapping.Mapping, new byte[] { 0x56, 0x33 }, 1).IsSuccess);
        KernelResult Start() => s.Kernel.StartVirtualDomain(s.Owner, domain.Domain, domain.ExecuteCapability);
        if (fault is 0 or 1 or 5) s.Provider.BeforeIncarnationRead = () =>
        {
            s.Provider.BeforeIncarnationRead = null;
            if (fault == 0) throw new IOException("Pre start source unavailable");
            if (fault == 1) s.Provider.Incarnation = new(2);
            if (fault == 5) Assert.True(s.Kernel.RevokeCapability(domain.ExecuteCapability).IsSuccess);
        };
        s.Provider.AfterArtifactStart = () => s.Provider.BeforeIncarnationRead = () =>
        {
            s.Provider.BeforeIncarnationRead = null;
            if (fault == 2) throw new IOException("Known execution source unavailable");
            if (fault == 3) s.Provider.Incarnation = new(2);
            if (fault == 4) Assert.True(s.Kernel.RevokeCapability(domain.ExecuteCapability).IsSuccess);
        };
        Assert.False(Start().IsSuccess);
        s.Provider.BeforeIncarnationRead = null; s.Provider.AfterArtifactStart = null;
        var invoked = fault is 2 or 3 or 4;
        Assert.Equal(invoked ? 1 : 0, s.Provider.ArtifactStartCalls);
        var artifacts = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_executableArtifacts");
        var artifact = Assert.Single(artifacts.Values.Cast<object>());
        Assert.Equal(invoked, artifact.GetType().GetProperty("ExecutionReceipt")!.GetValue(artifact) is not null);
        Assert.Equal(invoked, (bool)artifact.GetType().GetProperty("Started")!.GetValue(artifact)!);
        Assert.False((bool)artifact.GetType().GetProperty("StartInFlight")!.GetValue(artifact)!);
        var record = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        Assert.NotNull(record.ExecutableArtifact);
        Assert.NotEqual(VirtualDomainState.Running, record.State);
        foreach (var name in new[] { "_childBindings", "_guestBindings" })
        {
            var table = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, name);
            foreach (var value in table.Values)
                Assert.Equal(0, value!.GetType().GetProperty(name == "_childBindings" ? "PendingChildEffects" : "PendingExecutableEffects")!.GetValue(value));
        }
        if (fault == 0)
        {
            Assert.True(Start().IsSuccess); // no invocation occurred: fresh admission may retry
            Assert.Equal(1, s.Provider.ArtifactStartCalls);
            Assert.False(Start().IsSuccess);
        }
        else Assert.False(Start().IsSuccess);
        Assert.Equal(fault == 0 || invoked ? 1 : 0, s.Provider.ArtifactStartCalls);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        Assert.Equal(0, s.Provider.GuestUnmapCalls); // terminal execution is not artifact release
        Assert.Equal(1, s.Provider.ArtifactBindCalls);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void ArtifactBindOriginalSourceRefusalRetainsKnownCleanupAndPins(int fault)
    {
        var (s, domain, mapping) = ArtifactScenario();
        KernelResult Bind() => s.Kernel.BindVirtualDomainExecutable(s.Owner, domain.Domain,
            domain.ExecuteCapability, mapping.Mapping, new byte[] { 0x56, 0x33 }, 1);
        if (fault < 2) s.Provider.BeforeIncarnationRead = () =>
        {
            s.Provider.BeforeIncarnationRead = null;
            if (fault == 0) throw new IOException("Pre artifact source unavailable");
            s.Provider.Incarnation = new(2);
        };
        s.Provider.AfterArtifactBind = () => s.Provider.BeforeIncarnationRead = () =>
        {
            s.Provider.BeforeIncarnationRead = null;
            if (fault == 2) throw new IOException("Known artifact source unavailable");
            if (fault == 3) s.Provider.Incarnation = new(2);
            if (fault == 4) Assert.True(s.Kernel.RevokeCapability(domain.ExecuteCapability).IsSuccess);
        };
        Assert.False(Bind().IsSuccess);
        s.Provider.BeforeIncarnationRead = null; s.Provider.AfterArtifactBind = null;
        Assert.Equal(fault < 2 ? 0 : 1, s.Provider.ArtifactBindCalls);
        var artifacts = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_executableArtifacts");
        Assert.Equal(fault < 2 ? 0 : 1, artifacts.Count);
        var record = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        Assert.Equal(fault >= 2, record.ExecutableArtifact is not null);
        Assert.NotEqual(VirtualDomainState.Running, record.State);
        foreach (var name in new[] { "_childBindings", "_guestBindings" })
        {
            var table = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, name);
            foreach (var value in table.Values)
                Assert.Equal(0, value!.GetType().GetProperty(name == "_childBindings" ? "PendingChildEffects" : "PendingExecutableEffects")!.GetValue(value));
        }
        if (fault == 0)
        {
            var terminated = s.Kernel.TerminateProcess(s.Owner); Assert.True(terminated.IsSuccess, terminated.ToString());
            Assert.False(s.Kernel.Processes.Resolve(s.Owner).IsSuccess);
            Assert.All(s.Kernel.Regions.Snapshot(), region => Assert.Equal(RegionState.Released, region.State));
            Assert.Equal(1, s.Provider.GuestUnmapCalls);
        }
        else
        {
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
            Assert.Equal(0, s.Provider.GuestUnmapCalls);
        }
        Assert.Equal(fault < 2 ? 0 : 1, s.Provider.ArtifactBindCalls);
    }
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task KernelArtifactPendingRevokeOrStopRetainsReceiptWithoutLateState(bool start, bool stop)
    {
        var (s, domain, mapping) = ArtifactScenario();
        KernelResult Bind() => s.Kernel.BindVirtualDomainExecutable(s.Owner, domain.Domain,
            domain.ExecuteCapability, mapping.Mapping, new byte[] { 0x56, 0x33 }, 1);
        if (start) Assert.True(Bind().IsSuccess);
        KernelResult Run() => start ? s.Kernel.StartVirtualDomain(s.Owner, domain.Domain, domain.ExecuteCapability) : Bind();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Action hold = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); };
        if (start) s.Provider.AfterArtifactStart = hold; else s.Provider.AfterArtifactBind = hold;
        var work = Task.Run(Run);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(KernelError.PlatformBindingActive, Run().Error);
            var activeRecord = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
            Assert.False(s.Kernel.PlatformAuthority.UnmapChildGuestRegion(activeRecord.PlatformMappings[mapping.Mapping.MappingId].Guest).IsSuccess);
            if (stop) Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            else Assert.True(s.Kernel.RevokeCapability(domain.ExecuteCapability).IsSuccess);
            Assert.Equal(0, s.Provider.GuestUnmapCalls);
        }
        finally { release.Set(); }
        var result = await work.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(result.IsSuccess);
        if (!stop) Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        s.Provider.AfterArtifactStart = null;
        s.Provider.AfterArtifactBind = null;
        var record = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        if (start)
        {
            var table = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_executableArtifacts");
            var receiptOwner = Assert.Single(table.Values.Cast<object>());
            Assert.NotNull(receiptOwner.GetType().GetProperty("ExecutionReceipt")!.GetValue(receiptOwner));
        }
        Assert.NotNull(record.ExecutableArtifact); // known binding retained by real cleanup owner
        Assert.NotEqual(VirtualDomainState.Running, record.State);
        Assert.False(Run().IsSuccess);
        Assert.Equal(1, s.Provider.ArtifactBindCalls);
        Assert.Equal(start ? 1 : 0, s.Provider.ArtifactStartCalls);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        Assert.Equal(0, s.Provider.GuestUnmapCalls); // no artifact release consumer/evidence exists
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)] [InlineData(false, 3)] [InlineData(false, 4)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)] [InlineData(true, 3)] [InlineData(true, 4)]
    public void KernelArtifactFaultsPreserveExactDependencyPinsAndBalancedCounters(bool start, int fault)
    {
        var (s, domain, mapping) = ArtifactScenario();
        KernelResult Bind() => s.Kernel.BindVirtualDomainExecutable(s.Owner, domain.Domain,
            domain.ExecuteCapability, mapping.Mapping, new byte[] { 0x56, 0x33 }, 1);
        if (start) Assert.True(Bind().IsSuccess);
        if (fault == 0) { s.Provider.ThrowAfterArtifactBind = !start; s.Provider.ThrowAfterArtifactStart = start; }
        if (fault == 1)
        {
            Action reset = () => Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (start) s.Provider.AfterArtifactStart = reset; else s.Provider.AfterArtifactBind = reset;
        }
        if (fault == 2) s.Provider.ArtifactStatus = PlatformAuthorityStatus.Denied;
        if (fault == 3) s.Provider.MalformedArtifactEvidence = true;
        if (fault == 4) s.Provider.ArtifactStatus = PlatformAuthorityStatus.NotAccepted;
        Assert.False((start ? s.Kernel.StartVirtualDomain(s.Owner, domain.Domain, domain.ExecuteCapability) : Bind()).IsSuccess);
        s.Provider.ThrowAfterArtifactBind = s.Provider.ThrowAfterArtifactStart = false;
        s.Provider.AfterArtifactBind = s.Provider.AfterArtifactStart = null;
        s.Provider.ArtifactStatus = null; s.Provider.MalformedArtifactEvidence = false;
        foreach (var name in new[] { "_childBindings", "_guestBindings", "_executableArtifacts" })
        {
            var table = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, name);
            foreach (var value in table.Values)
            {
                var count = value!.GetType().GetProperty(name == "_childBindings" ? "PendingChildEffects" : "PendingExecutableEffects");
                if (count is not null) Assert.Equal(0, count.GetValue(value));
                var pending = value.GetType().GetProperty("StartInFlight");
                if (pending is not null) Assert.False((bool)pending.GetValue(value)!);
            }
        }
        if (fault == 4 && !start)
        {
            Assert.True(Bind().IsSuccess); // explicit no-admission response permits fresh admission
            return;
        }
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
    }

    [Fact]
    public void KernelArtifactCapturesCallerPackageAndRejectsDuplicateBind()
    {
        var (s, domain, mapping) = ArtifactScenario();
        byte[] package = [0x56, 0x33];
        s.Provider.AfterArtifactBind = () => package[0] = 0xff;
        Assert.True(s.Kernel.BindVirtualDomainExecutable(s.Owner, domain.Domain, domain.ExecuteCapability,
            mapping.Mapping, package, 1).IsSuccess);
        Assert.Equal(new byte[] { 0x56, 0x33 }, s.Provider.LastArtifactPackage.ToArray());
        Assert.False(s.Kernel.BindVirtualDomainExecutable(s.Owner, domain.Domain, domain.ExecuteCapability,
            mapping.Mapping, package, 1).IsSuccess);
        Assert.Equal(1, s.Provider.ArtifactBindCalls);
        Assert.True(s.Kernel.StartVirtualDomain(s.Owner, domain.Domain, domain.ExecuteCapability).IsSuccess);
        Assert.Equal(VirtualDomainState.Running, s.Kernel.QueryVirtualDomain(s.Owner, domain.Domain).Value);
        Assert.Equal(1, s.Provider.ArtifactStartCalls);
    }

    [Fact]
    public void KernelArtifactSourceAndBoundsRefuseWithoutProviderOrIdentityMutation()
    {
        var (s, domain, mapping) = ArtifactScenario();
        var bridge = s.Kernel.PlatformAuthority;
        var before = ArtifactField(bridge, "_nextExecutableArtifactBindingId");
        Assert.False(s.Kernel.BindVirtualDomainExecutable(s.Owner, domain.Domain, domain.ExecuteCapability,
            mapping.Mapping, ReadOnlyMemory<byte>.Empty, 1).IsSuccess);
        Assert.False(s.Kernel.BindVirtualDomainExecutable(s.Owner, domain.Domain, domain.ExecuteCapability,
            mapping.Mapping, new byte[] { 1 }, 0).IsSuccess);
        Assert.True(s.Kernel.RevokeCapability(domain.ExecuteCapability).IsSuccess);
        Assert.Equal(KernelError.CapabilityRevoked, s.Kernel.BindVirtualDomainExecutable(s.Owner, domain.Domain,
            domain.ExecuteCapability, mapping.Mapping, new byte[] { 1 }, 1).Error);
        Assert.Equal(before, ArtifactField(bridge, "_nextExecutableArtifactBindingId"));
        Assert.Equal(0, s.Provider.ArtifactBindCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ArtifactCompletionConsumerRemainsInsideDeliveryInterlock(bool start)
    {
        var (s, domain, mapping) = ArtifactScenario();
        var record = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        var child = record.ChildBinding!.Value;
        var guest = record.PlatformMappings[mapping.Mapping.MappingId].Guest;
        var bridge = s.Kernel.PlatformAuthority;
        KernelResult Probe(Func<KernelResult> commit)
        {
            Assert.False(bridge.UnmapChildGuestRegion(guest).IsSuccess);
            Assert.Equal(KernelError.PlatformBindingActive, bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).Error);
            return commit();
        }
        if (!start)
            Assert.True(bridge.BindChildExecutableArtifact(child, guest, new byte[] { 0x56, 0x33 }, 1,
                completionConsumer: (_, commit) => Probe(() => commit(() => KernelResult.Ok()))).IsSuccess);
        else
        {
            var bound = bridge.BindChildExecutableArtifact(child, guest, new byte[] { 0x56, 0x33 }, 1).Value!;
            Assert.True(bridge.StartChildExecutableArtifact(bound,
                completionConsumer: commit => Probe(() => commit(() => KernelResult.Ok()))).IsSuccess);
        }
    }

    [Fact]
    public void ArtifactStaleGenerationOrWrongTupleCannotConsumeStartIdentity()
    {
        var (s, domain, mapping) = ArtifactScenario();
        Assert.True(s.Kernel.BindVirtualDomainExecutable(s.Owner, domain.Domain, domain.ExecuteCapability,
            mapping.Mapping, new byte[] { 0x56, 0x33 }, 1).IsSuccess);
        var record = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        var exact = record.ExecutableArtifact!.Value;
        var before = ArtifactField(s.Kernel.PlatformAuthority, "_nextChildStartOperationId");
        Assert.Equal(KernelError.StaleGeneration, s.Kernel.PlatformAuthority.StartChildExecutableArtifact(
            exact with { Generation = new(exact.Generation.Value + 1) }).Error);
        Assert.False(s.Kernel.PlatformAuthority.StartChildExecutableArtifact(exact with
        {
            Child = exact.Child with { Owner = new(new ProcessId(9999), 2) },
        }).IsSuccess);
        Assert.Equal(before, ArtifactField(s.Kernel.PlatformAuthority, "_nextChildStartOperationId"));
        Assert.Equal(0, s.Provider.ArtifactStartCalls);
        Assert.True(s.Kernel.StartVirtualDomain(s.Owner, domain.Domain, domain.ExecuteCapability).IsSuccess);
        Assert.Equal(1, s.Provider.ArtifactStartCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void NestedSourceStopAndUnknownEffectsUseRealOwnersAndCleanup(int fault)
    {
        var s = CreateScenario(nestedFailure: fault == 6 ? PlatformAuthorityStatus.Denied : null);
        var parent = CreateConfiguredRoot(s);
        var request = new NestedVirtualDomainRequestProfile(new(1, 4096),
            VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.GuestMemory | VirtualDomainAuthorityClass.Execution);
        if (fault == 0) s.Provider.AfterNestedMaterialize = () => Assert.True(s.Kernel.RevokeCapability(parent.ConfigureCapability).IsSuccess);
        if (fault == 1) s.Provider.AfterNestedMaterialize = () => Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        if (fault == 2) s.Provider.ThrowAfterNestedMaterialize = true;
        if (fault == 3) s.Provider.AfterNestedMaterialize = () => Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
        if (fault is 4 or 5)
        {
            int nonce = 0;
            s.Kernel.CapabilityAuthority.GetType().GetField("_nonceFactory",
                global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.SetValue(
                s.Kernel.CapabilityAuthority, (Func<Guid>)(() =>
                {
                    if (++nonce == 2)
                    {
                        if (fault == 4) Assert.True(s.Kernel.RevokeCapability(parent.ConfigureCapability).IsSuccess);
                        else Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
                    }
                    return Guid.NewGuid();
                }));
        }
        Assert.False(s.Kernel.CreateNestedVirtualDomain(s.Owner, parent.Domain, parent.ConfigureCapability, request).IsSuccess);
        s.Provider.AfterNestedMaterialize = null; s.Provider.ThrowAfterNestedMaterialize = false;
        var records = ((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner);
        if (fault is 0 or 1 or 4 or 5)
        {
            var nested = Assert.Single(records, x => x.ParentDomain == parent.Domain);
            Assert.False(nested.AuthorityPublished);
            Assert.Equal(fault is 4 or 5 ? 1 : 0, nested.Capabilities.Length);
            if (nested.Capabilities.Length != 0)
                Assert.False(s.Kernel.ConfigureVirtualDomain(s.Owner, nested.Handle, nested.Capabilities[0]).IsSuccess);
            Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(1, s.Provider.NestedCloseCalls);
        }
        else
        {
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
            Assert.Equal(0, s.Provider.NestedCloseCalls);
        }
        Assert.Equal(1, s.Provider.NestedCreateCalls);
        var table = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_childBindings");
        foreach (var record in table.Values)
            Assert.Equal(0, record!.GetType().GetProperty("PendingChildEffects")!.GetValue(record));
    }

    [Fact]
    public void NestedPartialGrantIsNotPublishedAndSubsetCapabilitiesStayExact()
    {
        var s = CreateScenario();
        var parent = CreateConfiguredRoot(s);
        int nonce = 0;
        s.Kernel.CapabilityAuthority.GetType().GetField("_nonceFactory",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.SetValue(
            s.Kernel.CapabilityAuthority, (Func<Guid>)(() =>
            {
                if (++nonce == 2)
                {
                    var nested = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner), x => x.ParentDomain == parent.Domain);
                    Assert.False(nested.AuthorityPublished);
                    Assert.Equal(KernelError.PlatformBindingActive,
                        s.Kernel.ConfigureVirtualDomain(s.Owner, nested.Handle, nested.Capabilities[0]).Error);
                }
                return Guid.NewGuid();
            }));
        var result = s.Kernel.CreateNestedVirtualDomain(s.Owner, parent.Domain, parent.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.GuestMemory));
        Assert.True(result.IsSuccess, result.Message);
        Assert.NotEqual(default, result.Value!.ConfigureCapability);
        Assert.NotEqual(default, result.Value.MemoryCapability);
        Assert.Equal(default, result.Value.ExecuteCapability);
        Assert.Equal(default, result.Value.EventCapability);
        Assert.Equal(default, result.Value.TrapCapability);
        Assert.True(s.Kernel.ConfigureVirtualDomain(s.Owner, result.Value.Domain, result.Value.ConfigureCapability).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Fact]
    public async Task ConcurrentNestedCallbackStopsBeforeLatePublicationAndPreservesCleanup()
    {
        var s = CreateScenario();
        var parent = CreateConfiguredRoot(s);
        var request = new NestedVirtualDomainRequestProfile(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        s.Provider.AfterNestedMaterialize = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); };
        var work = Task.Run(() => s.Kernel.CreateNestedVirtualDomain(s.Owner, parent.Domain, parent.ConfigureCapability, request));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(KernelError.PlatformBindingActive,
                s.Kernel.CreateNestedVirtualDomain(s.Owner, parent.Domain, parent.ConfigureCapability, request).Error);
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        }
        finally { release.Set(); }
        Assert.False((await work.WaitAsync(TimeSpan.FromSeconds(15))).IsSuccess);
        s.Provider.AfterNestedMaterialize = null;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.NestedCreateCalls);
        Assert.Equal(1, s.Provider.NestedCloseCalls);
    }

    [Fact]
    public void NestedAncestorDrainDeniesNewAdmissionAndPublicationButKeepsRealCleanup()
    {
        var s = CreateScenario();
        var parent = CreateConfiguredRoot(s);
        var first = s.Kernel.CreateNestedVirtualDomain(s.Owner, parent.Domain, parent.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).Value!;
        Assert.True(s.Kernel.ConfigureVirtualDomain(s.Owner, first.Domain, first.ConfigureCapability).IsSuccess);
        var second = s.Kernel.CreateNestedVirtualDomain(s.Owner, first.Domain, first.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).Value!;
        var records = ((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner);
        var rootRecord = Assert.Single(records, x => x.Handle == parent.Domain);
        Assert.True(s.Kernel.PlatformAuthority.TransitionChildDomain(rootRecord.ChildBinding!.Value,
            PlatformChildDomainTransition.BeginDrain).IsSuccess);
        Assert.False(s.Kernel.CreateNestedVirtualDomain(s.Owner, first.Domain, first.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).IsSuccess);
        Assert.Equal(2, s.Provider.NestedCreateCalls);
        Assert.False(s.Kernel.ConfigureVirtualDomain(s.Owner, second.Domain, second.ConfigureCapability).IsSuccess);
        Assert.Equal(VirtualDomainState.Created, s.Kernel.QueryVirtualDomain(s.Owner, second.Domain).Value);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(2, s.Provider.NestedCloseCalls);
    }

    [Fact]
    public void NestedUnknownAuthorityAndRevokedSourceRefuseBeforeMutation()
    {
        var s = CreateScenario();
        var parent = CreateConfiguredRoot(s);
        int before = s.Kernel.Processes.Resolve(s.Owner).Value!.Capabilities.Items.Count;
        Assert.Equal(KernelError.PlatformDenied, s.Kernel.CreateNestedVirtualDomain(s.Owner, parent.Domain,
            parent.ConfigureCapability, new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle | (VirtualDomainAuthorityClass)0x4000)).Error);
        Assert.Equal(before, s.Kernel.Processes.Resolve(s.Owner).Value!.Capabilities.Items.Count);
        Assert.True(s.Kernel.RevokeCapability(parent.ConfigureCapability).IsSuccess);
        int afterRevoke = s.Kernel.Processes.Resolve(s.Owner).Value!.Capabilities.Items.Count;
        Assert.Equal(KernelError.CapabilityRevoked, s.Kernel.CreateNestedVirtualDomain(s.Owner, parent.Domain,
            parent.ConfigureCapability, new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).Error);
        Assert.Equal(afterRevoke, s.Kernel.Processes.Resolve(s.Owner).Value!.Capabilities.Items.Count);
        Assert.Equal(0, s.Provider.NestedCreateCalls);
        Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ActualMalformedRootChildCompensationCannotReclaimOriginalProcess(int fault)
    {
        var s = CreateScenario();
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        s.Provider.MalformedRootCreate = true;
        s.Provider.BeforeChildClose = () =>
        {
            if (fault == 1) throw new IOException("returned cleanup response lost");
            if (fault == 2) Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault == 3) s.Provider.Incarnation = new(2);
        };
        Assert.False(s.Kernel.CreateVirtualDomain(s.Owner, s.CreateCapability,
            new VirtualDomainProfile(1, 8192)).IsSuccess);
        s.Provider.BeforeChildClose = null;
        Assert.Empty(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
        Assert.Equal(1, s.Provider.ChildCloseCalls);
        var domains = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_domains");
        var original = Assert.Single(domains.Values.Cast<object>());
        Assert.True((bool)original.GetType().GetProperty("ChildCreateMayHaveEffect")!.GetValue(original)!);
        Assert.Single(((global::System.Collections.IEnumerable)original.GetType().GetProperty("UnpublishedChildCreates")!.GetValue(original)!).Cast<object>());
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)] [InlineData(false, 3)] [InlineData(false, 4)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)] [InlineData(true, 3)] [InlineData(true, 4)]
    public void ChildTransitionSourceFaultsSeparateReturnedStateAndLocalPublication(bool nested, int fault)
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var domain = nested ? s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.Execution)).Value! : root;
        if (nested) Assert.True(s.Kernel.ConfigureVirtualDomain(s.Owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner), x => x.Handle == domain.Domain);
        var effects = 0;
        s.Provider.BeforeChildTransition = () =>
        {
            effects++;
            if (fault == 2) s.Provider.ThrowIncarnation = true;
            if (fault == 3) s.Provider.Incarnation = new(2);
            if (fault == 4) throw new IOException("unknown child transition response");
        };
        if (fault == 0) s.Provider.ThrowIncarnation = true;
        if (fault == 1) s.Provider.Incarnation = new(2);
        Assert.False(s.Kernel.StartVirtualDomain(s.Owner, domain.Domain, domain.ExecuteCapability).IsSuccess);
        Assert.Equal(VirtualDomainState.Configured, local.State);
        Assert.Equal(fault < 2 ? 0 : 1, effects);
        var table = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_childBindings");
        var owner = Assert.Single(table.Values.Cast<object>(), x =>
            Equals(x.GetType().GetProperty("Binding")!.GetValue(x), local.ChildBinding!.Value));
        Assert.False((bool)owner.GetType().GetProperty("TransitionInFlight")!.GetValue(owner)!);
        if (fault == 2) Assert.Equal("Running", owner.GetType().GetProperty("State")!.GetValue(owner)!.ToString());
        s.Provider.BeforeChildTransition = null;
        s.Provider.ThrowIncarnation = false;
        if (fault is 0 or 2)
        {
            Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(nested ? 2 : 1, s.Provider.ChildCloseCalls);
            Assert.Equal(VirtualDomainState.Closed, local.State);
        }
        else
        {
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
            Assert.Equal(0, s.Provider.ChildCloseCalls);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ChildTransitionSourceCallbackRevokeOrResetWinsBeforeInvocation(bool reset)
    {
        var s = CreateScenario();
        var domain = CreateConfiguredRoot(s);
        var effects = 0;
        s.Provider.BeforeChildTransition = () => effects++;
        s.Provider.BeforeIncarnationRead = () =>
        {
            s.Provider.BeforeIncarnationRead = null;
            if (reset) Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
            else Assert.True(s.Kernel.RevokeCapability(domain.ExecuteCapability).IsSuccess);
        };
        Assert.False(s.Kernel.StartVirtualDomain(s.Owner, domain.Domain, domain.ExecuteCapability).IsSuccess);
        Assert.Equal(0, effects);
        s.Provider.BeforeChildTransition = null;
        if (reset)
        {
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
            Assert.Equal(0, s.Provider.ChildCloseCalls);
        }
        else Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Fact]
    public async Task ChildTransitionSourcePauseKeepsOwnersAvailableAndStopWinsInvocation()
    {
        var s = CreateScenario();
        var domain = CreateConfiguredRoot(s);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var effects = 0;
        s.Provider.BeforeChildTransition = () => effects++;
        s.Provider.BeforeIncarnationRead = () =>
        {
            s.Provider.BeforeIncarnationRead = null;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
        };
        var work = Task.Run(() => s.Kernel.StartVirtualDomain(s.Owner, domain.Domain, domain.ExecuteCapability));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True((await Task.Run(() => s.Kernel.Processes.Resolve(s.Owner)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.Equal(KernelError.PlatformBindingActive, (await Task.Run(() =>
                s.Kernel.StartVirtualDomain(s.Owner, domain.Domain, domain.ExecuteCapability)).WaitAsync(TimeSpan.FromSeconds(5))).Error);
            Assert.False((await Task.Run(() => s.Kernel.TerminateProcess(s.Owner)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.Equal(0, effects);
            Assert.Equal(0, s.Provider.ChildCloseCalls);
        }
        finally { release.Set(); }
        Assert.False((await work.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        Assert.Equal(0, effects);
        s.Provider.BeforeChildTransition = null;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.ChildCloseCalls);
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)]
    public void NestedLifecycleAncestorDrainRefusesAdmissionOrLatePublication(int step, bool during)
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var nested = s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.Execution)).Value!;
        Assert.True(s.Kernel.ConfigureVirtualDomain(s.Owner, nested.Domain, nested.ConfigureCapability).IsSuccess);
        if (step > 0) Assert.True(s.Kernel.StartVirtualDomain(s.Owner, nested.Domain, nested.ExecuteCapability).IsSuccess);
        if (step > 1) Assert.True(s.Kernel.ParkVirtualDomain(s.Owner, nested.Domain, nested.ExecuteCapability).IsSuccess);
        var records = ((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner);
        var parentRecord = Assert.Single(records, x => x.Handle == root.Domain);
        var local = Assert.Single(records, x => x.Handle == nested.Domain);
        var initial = local.State;
        int effects = 0;
        s.Provider.BeforeChildTransition = () =>
        {
            if (!during) { effects++; return; }
            effects++;
            s.Provider.BeforeChildTransition = null;
            Assert.True(s.Kernel.PlatformAuthority.TransitionChildDomain(parentRecord.ChildBinding!.Value,
                PlatformChildDomainTransition.BeginDrain).IsSuccess);
            Assert.False(s.Kernel.PlatformAuthority.CloseChildDomain(parentRecord.ChildBinding.Value).IsSuccess);
        };
        if (!during) Assert.True(s.Kernel.PlatformAuthority.TransitionChildDomain(parentRecord.ChildBinding!.Value,
            PlatformChildDomainTransition.BeginDrain).IsSuccess);
        effects = 0;
        KernelResult Run() => step switch
        {
            0 => s.Kernel.StartVirtualDomain(s.Owner, nested.Domain, nested.ExecuteCapability),
            1 => s.Kernel.ParkVirtualDomain(s.Owner, nested.Domain, nested.ExecuteCapability),
            _ => s.Kernel.ResumeVirtualDomain(s.Owner, nested.Domain, nested.ExecuteCapability),
        };
        Assert.False(Run().IsSuccess);
        Assert.Equal(initial, local.State);
        Assert.Equal(during ? 1 : 0, effects);
        Assert.False(Run().IsSuccess);
        Assert.Equal(during ? 1 : 0, effects);
        s.Provider.BeforeChildTransition = null;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.NestedCloseCalls);
    }

    [Fact]
    public void NestedLifecycleLocalAncestorQuarantineCannotBeBypassedByActiveBridgeIdentity()
    {
        var (s, root, mapping) = ArtifactScenario();
        var nested = s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.Execution)).Value!;

        Assert.True(s.Kernel.BindVirtualDomainExecutable(s.Owner, root.Domain, root.ExecuteCapability,
            mapping.Mapping, new byte[] { 0x56, 0x33 }, 1).IsSuccess);
        Assert.False(s.Kernel.CloseGuestRegionMapping(s.Owner, root.Domain, root.MemoryCapability, mapping.Mapping).IsSuccess);
        Assert.Equal(VirtualDomainState.Quarantined, s.Kernel.QueryVirtualDomain(s.Owner, root.Domain).Value);
        int effects = 0; s.Provider.BeforeChildTransition = () => effects++;
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner), x => x.Handle == nested.Domain);
        Assert.Equal(KernelError.PlatformFaulted,
            ((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ValidateActiveAncestors(local).Error);
        Assert.True(s.Kernel.PlatformAuthority.CommitActiveChildDomainConsumer(local.ChildBinding!.Value, () => KernelResult.Ok()).IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, s.Kernel.ConfigureVirtualDomain(s.Owner, nested.Domain, nested.ConfigureCapability).Error);
        Assert.Equal(0, effects);
        Assert.Equal(VirtualDomainState.Created, local.State);
        s.Provider.BeforeChildTransition = null;
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess); // exact root artifact release still Missing
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
    }

    [Fact]
    public async Task ConcurrentNestedLifecycleDeliveryPinsClosureAfterAncestorDrainAndStop()
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var nested = s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.Execution)).Value!;
        Assert.True(s.Kernel.ConfigureVirtualDomain(s.Owner, nested.Domain, nested.ConfigureCapability).IsSuccess);
        var records = ((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner);
        var parent = Assert.Single(records, x => x.Handle == root.Domain);
        var local = Assert.Single(records, x => x.Handle == nested.Domain);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        s.Provider.BeforeChildTransition = () =>
        {
            s.Provider.BeforeChildTransition = null;
            entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
        };
        var work = Task.Run(() => s.Kernel.StartVirtualDomain(s.Owner, nested.Domain, nested.ExecuteCapability));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(s.Kernel.PlatformAuthority.TransitionChildDomain(parent.ChildBinding!.Value,
                PlatformChildDomainTransition.BeginDrain).IsSuccess);
            Assert.False(s.Kernel.PlatformAuthority.CloseChildDomain(parent.ChildBinding.Value).IsSuccess);
            Assert.False(s.Kernel.PlatformAuthority.CloseChildDomain(local.ChildBinding!.Value).IsSuccess);
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
            Assert.Equal(0, s.Provider.NestedCloseCalls);
            Assert.DoesNotContain("root-close", s.Provider.CallLog);
        }
        finally { release.Set(); }
        Assert.False((await work.WaitAsync(TimeSpan.FromSeconds(15))).IsSuccess);
        Assert.NotEqual(VirtualDomainState.Running, local.State);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(VirtualDomainState.Closed, local.State);
        Assert.Equal(1, s.Provider.NestedCloseCalls);
    }

    [Fact]
    public void NestedSourceCommitRechecksLocalAncestorAfterEarlierAdmissionSnapshot()
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var nested = s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.Execution)).Value!;
        Assert.True(s.Kernel.ConfigureVirtualDomain(s.Owner, nested.Domain, nested.ConfigureCapability).IsSuccess);
        var authority = (VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains");
        var records = authority.ForOwner(s.Owner);
        var parent = Assert.Single(records, x => x.Handle == root.Domain);
        var leaf = Assert.Single(records, x => x.Handle == nested.Domain);
        int effects = 0; s.Provider.BeforeChildTransition = () => effects++;
        Assert.True(authority.ValidateActiveAncestors(leaf).IsSuccess);
        var result = s.Kernel.PlatformAuthority.TransitionChildDomain(leaf.ChildBinding!.Value, PlatformChildDomainTransition.Start,
            admissionConsumer: commit => { parent.State = VirtualDomainState.Quarantined; return commit(); },
            sourceCommitConsumer: commit => s.Kernel.CapabilityAuthority.CommitVirtualDomainEffect(nested.ExecuteCapability,
                new DomainId(5001), s.Owner.Generation, CapabilityRights.Execute,
                VirtualizationResourceIds.Domain(nested.Domain.DomainId),
                () => authority.CommitEffectAdmission(leaf, VirtualDomainState.Configured, commit)));
        Assert.False(result.IsSuccess);
        Assert.Equal(0, effects);
        Assert.Equal(VirtualDomainState.Configured, leaf.State);
        s.Provider.BeforeChildTransition = null;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Fact]
    public async Task VirtualStateSetterSharesActualOwnerCommitGate()
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var nested = s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).Value!;
        Assert.True(s.Kernel.ConfigureVirtualDomain(s.Owner, nested.Domain, nested.ConfigureCapability).IsSuccess);
        var authority = (VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains");
        var records = authority.ForOwner(s.Owner);
        var parent = Assert.Single(records, x => x.Handle == root.Domain);
        var leaf = Assert.Single(records, x => x.Handle == nested.Domain);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var mutationEntered = new ManualResetEventSlim();
        var work = Task.Run(() => authority.CommitEffectAdmission(leaf, VirtualDomainState.Configured, () =>
        {
            entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); return KernelResult.Ok();
        }));
        Task mutation = Task.CompletedTask;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            var gate = ArtifactField(authority, "_gate");
            bool acquired = Monitor.TryEnter(gate);
            if (acquired) Monitor.Exit(gate);
            Assert.False(acquired);
            mutation = Task.Run(() => { mutationEntered.Set(); parent.State = VirtualDomainState.Quarantined; });
            Assert.True(mutationEntered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(mutation.IsCompleted);
        }
        finally { release.Set(); }
        Assert.True((await work.WaitAsync(TimeSpan.FromSeconds(15))).IsSuccess);
        await mutation.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(VirtualDomainState.Quarantined, parent.State);
        Assert.False(authority.CommitEffectAdmission(leaf, VirtualDomainState.Configured, () => KernelResult.Ok()).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrapSourceRevokeRefusesCallbackOrLateObservation(bool during)
    {
        var (s, root, leaf) = NestedDeliveryScenario();
        Action revoke = () => Assert.True(s.Kernel.RevokeCapability(leaf.TrapCapability).IsSuccess);
        if (during) s.Provider.AfterChildTrap = revoke; else revoke();
        var result = s.Kernel.ObserveVirtualTrap(s.Owner, leaf.Domain, leaf.TrapCapability);
        Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        Assert.Equal(during ? 1 : 0, s.Provider.TrapCalls);
        Assert.Equal(KernelError.CapabilityRevoked, s.Kernel.ObserveVirtualTrap(s.Owner, leaf.Domain, leaf.TrapCapability).Error);
        Assert.Equal(during ? 1 : 0, s.Provider.TrapCalls);
        var record = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner), x => x.Handle == leaf.Domain);
        var table = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_childBindings");
        var bridgeRecord = table[record.ChildBinding!.Value.BindingId]!;
        Assert.Equal(during ? 1UL : 0UL, (ulong)bridgeRecord.GetType().GetProperty("LastTrapSequence")!.GetValue(bridgeRecord)!);
        Assert.Equal(0, (int)bridgeRecord.GetType().GetProperty("PendingChildEffects")!.GetValue(bridgeRecord)!);
        s.Provider.AfterChildTrap = null;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.NestedCloseCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EventSourceRevokeRefusesCallbackOrLatePublication(bool during)
    {
        var (s, root, leaf) = NestedDeliveryScenario();
        var endpoint = s.Kernel.CreateKernelEventEndpoint(s.Owner).Value!;
        Action revoke = () => Assert.True(s.Kernel.RevokeCapability(leaf.EventCapability).IsSuccess);
        if (during) s.Provider.AfterChildEvent = revoke; else revoke();
        Assert.Equal(KernelError.CapabilityRevoked,
            s.Kernel.InjectVirtualEvent(s.Owner, leaf.Domain, leaf.EventCapability, endpoint).Error);
        Assert.Equal(KernelError.CapabilityRevoked,
            s.Kernel.InjectVirtualEvent(s.Owner, leaf.Domain, leaf.EventCapability, endpoint).Error);
        Assert.Equal(during ? 1 : 0, s.Provider.EventCalls);
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner), x => x.Handle == leaf.Domain);
        var table = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_childBindings");
        var bridgeRecord = table[local.ChildBinding!.Value.BindingId]!;
        Assert.Equal(during ? 1UL : 0UL, (ulong)bridgeRecord.GetType().GetProperty("LastEventSequence")!.GetValue(bridgeRecord)!);
        Assert.Equal(0, (int)bridgeRecord.GetType().GetProperty("PendingChildEffects")!.GetValue(bridgeRecord)!);
        var facts = ((global::System.Collections.IEnumerable)bridgeRecord.GetType().GetProperty("EventInvocations")!.GetValue(bridgeRecord)!).Cast<object>();
        Assert.Equal(during ? 1 : 0, facts.Count());
        var registry = (KernelEventRegistry)ArtifactField(s.Kernel, "_kernelEvents");
        var staged = registry.Stage(s.Owner, endpoint, KernelEventClass.ExternalSignal, "test");
        Assert.True(staged.IsSuccess); // refused delivery leaves no staged or committed mailbox mutation
        Assert.True(registry.RollbackExact(s.Owner, staged.Value!).IsSuccess);
        s.Provider.AfterChildEvent = null;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.NestedCloseCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ConcurrentEventSourceRevokeOrStopRetainsPendingAndAllowsClosureRetry(bool stop)
    {
        var (s, root, leaf) = NestedDeliveryScenario();
        var endpoint = s.Kernel.CreateKernelEventEndpoint(s.Owner).Value!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        s.Provider.AfterChildEvent = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); };
        var work = Task.Run(() => s.Kernel.InjectVirtualEvent(s.Owner, leaf.Domain, leaf.EventCapability, endpoint));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.InjectVirtualEvent(s.Owner, leaf.Domain, leaf.EventCapability, endpoint).Error);
            if (stop) Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            else Assert.True(s.Kernel.RevokeCapability(leaf.EventCapability).IsSuccess);
            Assert.Equal(0, s.Provider.NestedCloseCalls);
        }
        finally { release.Set(); }
        Assert.False((await work.WaitAsync(TimeSpan.FromSeconds(15))).IsSuccess);
        Assert.Equal(1, s.Provider.EventCalls);
        s.Provider.AfterChildEvent = null;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.NestedCloseCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EventPublicationRefusesLocalAncestorDriftOrLostReceipt(bool lost)
    {
        var (s, root, leaf) = NestedDeliveryScenario();
        var endpoint = s.Kernel.CreateKernelEventEndpoint(s.Owner).Value!;
        var parent = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner), x => x.Handle == root.Domain);
        s.Provider.AfterChildEvent = () =>
        {
            if (lost) throw new InvalidOperationException("effect occurred; receipt lost");
            parent.State = VirtualDomainState.Quarantined;
        };
        Assert.False(s.Kernel.InjectVirtualEvent(s.Owner, leaf.Domain, leaf.EventCapability, endpoint).IsSuccess);
        Assert.False(s.Kernel.InjectVirtualEvent(s.Owner, leaf.Domain, leaf.EventCapability, endpoint).IsSuccess);
        Assert.Equal(1, s.Provider.EventCalls);
        s.Provider.AfterChildEvent = null;
        var cleanup = s.Kernel.TerminateProcess(s.Owner);
        if (lost)
        {
            Assert.False(cleanup.IsSuccess); // no exact unknown-effect reconciliation consumer: retain pin
            Assert.Equal(0, s.Provider.NestedCloseCalls);
        }
        else
        {
            Assert.True(cleanup.IsSuccess); // exact known child closure, not rollback, permits cleanup
            Assert.Equal(1, s.Provider.NestedCloseCalls);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ConcurrentTrapSourceRevokeOrStopKeepsPendingUntilLatePublication(bool stop)
    {
        var (s, root, leaf) = NestedDeliveryScenario();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        s.Provider.AfterChildTrap = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); };
        var work = Task.Run(() => s.Kernel.ObserveVirtualTrap(s.Owner, leaf.Domain, leaf.TrapCapability));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.ObserveVirtualTrap(s.Owner, leaf.Domain, leaf.TrapCapability).Error);
            if (stop) Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            else Assert.True(s.Kernel.RevokeCapability(leaf.TrapCapability).IsSuccess);
            Assert.Equal(0, s.Provider.NestedCloseCalls);
        }
        finally { release.Set(); }
        var result = await work.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(result.IsSuccess);
        if (!stop) Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        Assert.Equal(1, s.Provider.TrapCalls);
        s.Provider.AfterChildTrap = null;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.NestedCloseCalls);
    }

    [Fact]
    public void TrapPublicationRechecksActualLocalAncestorDespiteActiveBridgeIdentity()
    {
        var (s, root, leaf) = NestedDeliveryScenario();
        var parent = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner), x => x.Handle == root.Domain);
        s.Provider.AfterChildTrap = () => parent.State = VirtualDomainState.Quarantined;
        var result = s.Kernel.ObserveVirtualTrap(s.Owner, leaf.Domain, leaf.TrapCapability);
        Assert.False(result.IsSuccess);
        Assert.Equal(1, s.Provider.TrapCalls);
        Assert.False(s.Kernel.ObserveVirtualTrap(s.Owner, leaf.Domain, leaf.TrapCapability).IsSuccess);
        Assert.Equal(1, s.Provider.TrapCalls);
        s.Provider.AfterChildTrap = null;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.NestedCloseCalls);
    }

    private static (Scenario Scenario, VirtualDomainAuthoritySet Root, VirtualDomainAuthoritySet Leaf) NestedDeliveryScenario()
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var leaf = s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.Execution |
                VirtualDomainAuthorityClass.Events | VirtualDomainAuthorityClass.Traps)).Value!;
        Assert.True(s.Kernel.ConfigureVirtualDomain(s.Owner, leaf.Domain, leaf.ConfigureCapability).IsSuccess);
        Assert.True(s.Kernel.StartVirtualDomain(s.Owner, leaf.Domain, leaf.ExecuteCapability).IsSuccess);
        return (s, root, leaf);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(false, 1)] [InlineData(true, 1)]
    [InlineData(false, 2)] [InlineData(true, 2)]
    [InlineData(false, 3)] [InlineData(true, 3)]
    [InlineData(false, 4)] [InlineData(true, 4)]
    [InlineData(false, 5)] [InlineData(true, 5)]
    [InlineData(false, 6)] [InlineData(true, 6)]
    public async Task OriginalDeliveryOutcomeSurvivesActualPublicationRefusalWithoutWaiterOrReclaim(bool trap, int fault)
    {
        var (s, root, leaf) = NestedDeliveryScenario();
        var region = s.Kernel.AllocateRegion(s.Owner, 64).Value!;
        var endpoint = s.Kernel.CreateKernelEventEndpoint(s.Owner).Value!;
        using var cancellation = new CancellationTokenSource();
        var waiting = s.Kernel.WaitForKernelEventAsync(s.Owner, endpoint, cancellation.Token).AsTask();
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner), x => x.Handle == leaf.Domain);
        var children = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_childBindings");
        var binding = local.ChildBinding!.Value;
        var original = children[binding.BindingId]!;
        var roots = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_domains");
        var originalRoot = roots[binding.ParentBinding.BindingId]!;
        var epoch = s.Kernel.PlatformAuthority.BackendEpoch;
        object? foreign = null;
        void Fault()
        {
            var gate = ArtifactField(s.Kernel.PlatformAuthority, "_secureDomainLifecycleGate");
            Assert.False(Monitor.IsEntered(gate));
            if (fault == 0) Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault == 1) Assert.True(s.Kernel.RevokeCapability(trap ? leaf.TrapCapability : leaf.EventCapability).IsSuccess);
            if (fault == 2) s.Provider.ThrowIncarnation = true;
            if (fault == 5) throw new IOException("Original delivery reply lost");
            if (fault == 6)
            {
                foreign = typeof(object).GetMethod("MemberwiseClone", global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.Invoke(original, null)!;
                original.GetType().GetProperty("PendingChildEffects")!.SetValue(foreign, 7);
                children[binding.BindingId] = foreign;
            }
        }
        if (trap) s.Provider.AfterChildTrap = Fault; else s.Provider.AfterChildEvent = Fault;
        if (fault == 3)
        {
            s.Provider.EventReceiptMutation = receipt => receipt with { SourceResourceId = "foreign-source" };
            s.Provider.TrapEvidenceMutation = evidence => evidence with { Kind = (PlatformVirtualTrapKind)777 };
        }
        if (fault == 4) s.Provider.DeliveryStatus = PlatformAuthorityStatus.Denied;
        bool Run() => trap ? s.Kernel.ObserveVirtualTrap(s.Owner, leaf.Domain, leaf.TrapCapability).IsSuccess :
            s.Kernel.InjectVirtualEvent(s.Owner, leaf.Domain, leaf.EventCapability, endpoint).IsSuccess;
        Assert.False(Run());
        Assert.False(waiting.IsCompleted);
        var facts = ((global::System.Collections.IEnumerable)original.GetType().GetProperty(trap ? "TrapInvocations" : "EventInvocations")!.GetValue(original)!).Cast<object>();
        var fact = Assert.Single(facts);
        object? Property(string name) => fact.GetType().GetProperty(name)!.GetValue(fact);
        Assert.Same(originalRoot, Property("OriginalRoot"));
        Assert.Equal(epoch, Property("AdmissionEpoch"));
        if (trap) Assert.Equal(s.Provider.LastTrapRequest, Property("Request"));
        else Assert.Equal(s.Provider.LastEventRequest, Property("Request"));
        if (fault == 5) Assert.Null(Property("Observation"));
        else if (trap) Assert.Equal(s.Provider.LastTrapResponse, Property("Observation"));
        else Assert.Equal(s.Provider.LastEventResponse, Property("Observation"));
        Assert.Equal(0, original.GetType().GetProperty("PendingChildEffects")!.GetValue(original));
        if (foreign is not null) Assert.Equal(7, foreign.GetType().GetProperty("PendingChildEffects")!.GetValue(foreign));
        Assert.False(Run());
        Assert.Equal(1, trap ? s.Provider.TrapCalls : s.Provider.EventCalls);
        Assert.Same(fact, Assert.Single(facts));
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        s.Provider.ThrowIncarnation = false;
        s.Provider.AfterChildEvent = s.Provider.AfterChildTrap = null;
        var closed = s.Kernel.TerminateProcess(s.Owner);
        bool exactCleanup = fault is 1 or 2;
        Assert.Equal(exactCleanup, closed.IsSuccess);
        Assert.Equal(exactCleanup ? RegionState.Released : RegionState.Owned,
            Assert.Single(s.Kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
        if (!exactCleanup) Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DeliveryCompletionWithoutActualCommitRetainsOriginalFactAcrossLaterPublication(bool trap)
    {
        var (s, root, leaf) = NestedDeliveryScenario();
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner), x => x.Handle == leaf.Domain);
        var children = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_childBindings");
        var owner = children[local.ChildBinding!.Value.BindingId]!;
        bool Run(bool commit) => trap ? s.Kernel.PlatformAuthority.ObserveChildTrap(local.ChildBinding.Value,
            completionConsumer: publish => commit ? publish(KernelResult.Ok) : KernelResult.Ok()).IsSuccess :
            s.Kernel.PlatformAuthority.InjectChildEvent(local.ChildBinding.Value, PlatformVirtualEventClass.ExternalSignal,
                VirtualizationResourceIds.Events(leaf.Domain.DomainId), completionConsumer: publish => commit ? publish(KernelResult.Ok) : KernelResult.Ok()).IsSuccess;
        Assert.False(Run(false));
        var facts = ((global::System.Collections.IEnumerable)owner.GetType().GetProperty(trap ? "TrapInvocations" : "EventInvocations")!.GetValue(owner)!).Cast<object>();
        var original = Assert.Single(facts);
        var observation = original.GetType().GetProperty("Observation")!.GetValue(original);
        Assert.NotNull(observation);
        // A subsequent real publication cannot overwrite the earlier unpublished outcome.
        if (!trap) s.Provider.EventReceiptMutation = receipt => receipt with { Sequence = 2 };
        Assert.True(Run(true));
        Assert.Same(original, Assert.Single(facts));
        Assert.Equal(observation, original.GetType().GetProperty("Observation")!.GetValue(original));
        Assert.Equal(2, trap ? s.Provider.TrapCalls : s.Provider.EventCalls);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess); // exact child closure, not local fact removal
    }

    [Fact]
    public void EventResetRetainsKnownReceiptWithoutPublishingOrClaimingClosure()
    {
        var (s, root, leaf) = NestedDeliveryScenario();
        var endpoint = s.Kernel.CreateKernelEventEndpoint(s.Owner).Value!;
        s.Provider.AfterChildEvent = () => Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
        Assert.False(s.Kernel.InjectVirtualEvent(s.Owner, leaf.Domain, leaf.EventCapability, endpoint).IsSuccess);
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner), x => x.Handle == leaf.Domain);
        var table = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_childBindings");
        var record = table[local.ChildBinding!.Value.BindingId]!;
        Assert.Equal(1UL, (ulong)record.GetType().GetProperty("LastEventSequence")!.GetValue(record)!);
        Assert.Equal(0, (int)record.GetType().GetProperty("PendingChildEffects")!.GetValue(record)!);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(0, s.Provider.NestedCloseCalls);
    }

    [Fact]
    public async Task DeferredEventCommitKeepsExactWaiterOwnershipUntilNotification()
    {
        var (s, root, leaf) = NestedDeliveryScenario();
        var endpoint = s.Kernel.CreateKernelEventEndpoint(s.Owner).Value!;
        using var cancellation = new CancellationTokenSource();
        var waiting = s.Kernel.WaitForKernelEventAsync(s.Owner, endpoint, cancellation.Token).AsTask();
        var registry = (KernelEventRegistry)ArtifactField(s.Kernel, "_kernelEvents");
        var staged = registry.Stage(s.Owner, endpoint, KernelEventClass.ExternalSignal, "test").Value!;
        Assert.True(registry.CommitExactDeferred(s.Owner, staged, out var notify).IsSuccess);
        Assert.NotNull(notify);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel(); // cannot steal the waiter after registry commit
        Assert.False(waiting.IsCompleted);
        notify();
        Assert.Equal(staged, (await waiting).Value);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void NestedBridgeDeliveryAncestorDrainRefusesBeforeCallbackOrReceiptPublication(bool trap, bool during)
    {
        var (s, root, leaf) = NestedDeliveryScenario();
        var records = ((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner);
        var parent = Assert.Single(records, x => x.Handle == root.Domain);
        var local = Assert.Single(records, x => x.Handle == leaf.Domain);
        var bridge = s.Kernel.PlatformAuthority;
        Action drain = () => Assert.True(bridge.TransitionChildDomain(parent.ChildBinding!.Value, PlatformChildDomainTransition.BeginDrain).IsSuccess);
        if (!during) drain();
        else if (trap) s.Provider.AfterChildTrap = drain; else s.Provider.AfterChildEvent = drain;
        bool Run() => trap ? bridge.ObserveChildTrap(local.ChildBinding!.Value).IsSuccess :
            bridge.InjectChildEvent(local.ChildBinding!.Value, PlatformVirtualEventClass.ExternalSignal,
                VirtualizationResourceIds.Events(leaf.Domain.DomainId)).IsSuccess;
        Assert.False(Run());
        Assert.Equal(during ? 1 : 0, trap ? s.Provider.TrapCalls : s.Provider.EventCalls);
        Assert.False(Run());
        Assert.Equal(during ? 1 : 0, trap ? s.Provider.TrapCalls : s.Provider.EventCalls);
        var table = (global::System.Collections.IDictionary)ArtifactField(bridge, "_childBindings");
        var ownerRecord = table[local.ChildBinding!.Value.BindingId]!;
        Assert.Equal(during ? 1UL : 0UL, ownerRecord.GetType().GetProperty(trap ? "LastTrapSequence" : "LastEventSequence")!.GetValue(ownerRecord));
        Assert.Equal(0, ownerRecord.GetType().GetProperty("PendingChildEffects")!.GetValue(ownerRecord));
        s.Provider.AfterChildTrap = s.Provider.AfterChildEvent = null;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess); // exact known model effect has real cleanup
        Assert.Equal(1, s.Provider.NestedCloseCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ConcurrentNestedBridgeDeliveryKeepsClosurePinnedUntilCallbackEnds(bool trap)
    {
        var (s, root, leaf) = NestedDeliveryScenario();
        var records = ((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner);
        var parent = Assert.Single(records, x => x.Handle == root.Domain);
        var local = Assert.Single(records, x => x.Handle == leaf.Domain);
        var bridge = s.Kernel.PlatformAuthority;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        Action hold = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); };
        if (trap) s.Provider.AfterChildTrap = hold; else s.Provider.AfterChildEvent = hold;
        var work = Task.Run(() => trap ? bridge.ObserveChildTrap(local.ChildBinding!.Value).IsSuccess :
            bridge.InjectChildEvent(local.ChildBinding!.Value, PlatformVirtualEventClass.ExternalSignal,
                VirtualizationResourceIds.Events(leaf.Domain.DomainId)).IsSuccess);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(bridge.TransitionChildDomain(parent.ChildBinding!.Value, PlatformChildDomainTransition.BeginDrain).IsSuccess);
            Assert.Equal(KernelError.PlatformBindingActive, bridge.CloseChildDomain(parent.ChildBinding.Value).Error);
            Assert.Equal(KernelError.PlatformBindingActive, bridge.CloseChildDomain(local.ChildBinding!.Value).Error);
            Assert.Equal(0, s.Provider.NestedCloseCalls);
        }
        finally { release.Set(); }
        Assert.False(await work.WaitAsync(TimeSpan.FromSeconds(15)));
        s.Provider.AfterChildTrap = s.Provider.AfterChildEvent = null;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.NestedCloseCalls);
    }

    private static Scenario CreateScenario(
        PlatformAuthorityStatus? nestedFailure = null,
        bool malformedNestedLease = false,
        bool malformedTrap = false,
        bool malformedNestedClose = false,
        bool wrongNestedImmediateParent = false,
        bool executableArtifact = false)
    {
        var provider = new NestedProvider(nestedFailure, malformedNestedLease, malformedTrap, malformedNestedClose,
            wrongNestedImmediateParent, executableArtifact);
        var kernel = new RuntimeKernel(provider);
        var owner = TestFixtures.Create(kernel, 501, 5001).Handle;
        var create = kernel.MintCapability(new DomainId(5001), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        return new(kernel, provider, owner, create);
    }

    private static VirtualDomainAuthoritySet CreateConfiguredRoot(Scenario scenario)
    {
        var root = scenario.Kernel.CreateVirtualDomain(scenario.Owner, scenario.CreateCapability,
            new VirtualDomainProfile(2, 8192)).Value!;
        Assert.True(scenario.Kernel.ConfigureVirtualDomain(scenario.Owner, root.Domain,
            root.ConfigureCapability).IsSuccess);
        return root;
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void MalformedNestedCompensationKeepsOriginalParentPinBeforeCallbackAndReturnedFact(int fault)
    {
        var provider = new NestedProvider(null, false, false, fault == 4, true, false);
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1), new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory);
        var parent = bridge.CreateChildDomain(root, subject, intent).Value!;
        var epoch = bridge.BackendEpoch;
        var children = (global::System.Collections.IDictionary)ArtifactField(bridge, "_childBindings");
        var original = Assert.Single(children.Values.Cast<object>());
        var pinBeforeCallback = false;
        provider.ChildClosureStatus = fault == 1 ? PlatformAuthorityStatus.Faulted : fault == 2 ? PlatformAuthorityStatus.NotAccepted : null;
        provider.BeforeChildClose = () =>
        {
            pinBeforeCallback = (bool)original.GetType().GetProperty("NestedCreateMayHaveEffect")!.GetValue(original)!;
            if (fault == 3) throw new IOException("lost nested compensation reply");
            if (fault == 5) Assert.True(bridge.ObserveBackendReset().IsSuccess);
        };
        Assert.False(bridge.CreateNestedChildDomain(parent, subject.Process, intent).IsSuccess);
        Assert.False(pinBeforeCallback);
        Assert.Same(original, Assert.Single(children.Values.Cast<object>()));
        Assert.True((bool)original.GetType().GetProperty("NestedCreateMayHaveEffect")!.GetValue(original)!);
        Assert.Equal(0, original.GetType().GetProperty("PendingChildEffects")!.GetValue(original));
        var fact = Assert.Single(((global::System.Collections.IEnumerable)original.GetType().GetProperty("UnpublishedNestedCreates")!.GetValue(original)!).Cast<object>());
        Assert.Equal(parent, fact.GetType().GetProperty("Parent")!.GetValue(fact));
        Assert.Equal(epoch, fact.GetType().GetProperty("AdmissionEpoch")!.GetValue(fact));
        Assert.Equal(new PlatformNestedDomainRequest((PlatformProviderChildDomainLease)original.GetType().GetProperty("ProviderLease")!.GetValue(original)!, intent), fact.GetType().GetProperty("Request")!.GetValue(fact));
        Assert.Null(fact.GetType().GetProperty("ReturnedClosureReceipt")!.GetValue(fact));
        provider.BeforeChildClose = null;
        Assert.False(bridge.CloseChildDomain(parent).IsSuccess);
        var roots = (global::System.Collections.IDictionary)ArtifactField(bridge, "_domains");
        var rootOwner = Assert.Single(roots.Values.Cast<object>());
        var retained = (PlatformDomainBinding)rootOwner.GetType().GetProperty("Binding")!.GetValue(rootOwner)!;
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(retained, subject).Error);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(retained, subject).Error);
        Assert.Equal(1, provider.NestedCreateCalls);
        Assert.Equal(0, provider.ChildCloseCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ActualMalformedNestedCompensationCannotReclaimOriginalProcess(int fault)
    {
        var s = CreateScenario(wrongNestedImmediateParent: true);
        var root = CreateConfiguredRoot(s);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        s.Provider.BeforeChildClose = () =>
        {
            if (fault == 1) throw new IOException("nested compensation reply lost");
            if (fault == 2) Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault == 3) s.Provider.Incarnation = new(2);
        };
        Assert.False(s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).IsSuccess);
        s.Provider.BeforeChildClose = null;
        Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
        Assert.Equal(0, s.Provider.ChildCloseCalls);
        var children = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_childBindings");
        var original = Assert.Single(children.Values.Cast<object>());
        Assert.True((bool)original.GetType().GetProperty("NestedCreateMayHaveEffect")!.GetValue(original)!);
        Assert.Single(((global::System.Collections.IEnumerable)original.GetType().GetProperty("UnpublishedNestedCreates")!.GetValue(original)!).Cast<object>());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void ActualRootChildCreateSourceRefusalRetainsCleanupOnlyBeforeLatePublication(int fault)
    {
        var s = CreateScenario();
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        if (fault is 0 or 1 or 7)
            s.Provider.BeforeIncarnationRead = () =>
            {
                var roots = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_domains");
                if (!roots.Values.Cast<object>().Any(x => x.GetType().GetProperty("AuthorityState")!.GetValue(x)!.ToString() == "Active")) return;
                s.Provider.BeforeIncarnationRead = null;
                if (fault == 0) throw new IOException("child pre-source unavailable");
                if (fault == 1) s.Provider.Incarnation = new(2);
                if (fault == 7) Assert.True(s.Kernel.RevokeCapability(s.CreateCapability).IsSuccess);
            };
        s.Provider.BeforeRootCreateReturn = () =>
        {
            if (fault == 2) s.Provider.ThrowIncarnation = true;
            if (fault == 3) s.Provider.Incarnation = new(2);
            if (fault == 5) throw new IOException("known root-child effect response lost");
            if (fault is 4 or 6)
                s.Provider.BeforeIncarnationRead = () =>
                {
                    s.Provider.BeforeIncarnationRead = null;
                    if (fault == 4) Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
                    else Assert.True(s.Kernel.RevokeCapability(s.CreateCapability).IsSuccess);
                };
        };
        Assert.False(s.Kernel.CreateVirtualDomain(s.Owner, s.CreateCapability, new(1, 8192)).IsSuccess);
        Assert.Equal(fault is 0 or 1 or 7 ? 0 : 1, s.Provider.RootCreateCalls);
        var locals = ((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner);
        if (fault is 2 or 3 or 4 or 6)
        {
            var local = Assert.Single(locals);
            Assert.False(local.AuthorityPublished);
            Assert.Equal(VirtualDomainState.Quarantined, local.State);
            var children = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_childBindings");
            var child = Assert.Single(children.Values.Cast<object>());
            Assert.False((bool)child.GetType().GetProperty("CreationPublished")!.GetValue(child)!);
            Assert.False(s.Kernel.PlatformAuthority.TransitionChildDomain(local.ChildBinding!.Value, PlatformChildDomainTransition.Start).IsSuccess);
        }
        else Assert.Empty(locals);
        s.Provider.BeforeIncarnationRead = null;
        s.Provider.BeforeRootCreateReturn = null;
        s.Provider.ThrowIncarnation = false;
        if (fault is 0 or 2 or 6 or 7)
        {
            Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
            Assert.Equal(fault is 2 or 6 ? 1 : 0, s.Provider.ChildCloseCalls);
        }
        else
        {
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
            Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
            Assert.Equal(0, s.Provider.ChildCloseCalls);
        }
    }

    [Fact]
    public void ChildCleanupDeliveryCannotAdmitStartBeforeCreationPublication()
    {
        var s = CreateScenario();
        var bridge = s.Kernel.PlatformAuthority;
        var subject = new PlatformDomainIdentity(new DomainId(1), new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var calls = 0;
        s.Provider.BeforeChildTransition = () => calls++;
        var result = bridge.CreateChildDomain(root, subject, Intent(PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.Execution),
            cleanupConsumer: binding => Assert.False(bridge.TransitionChildDomain(binding, PlatformChildDomainTransition.Start).IsSuccess));
        Assert.True(result.IsSuccess);
        Assert.Equal(0, calls);
        Assert.True(bridge.TransitionChildDomain(result.Value!, PlatformChildDomainTransition.Start).IsSuccess);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void ActualNestedCreateSourceRefusalRetainsExactCleanupAndPinsUnknownOutcome(int fault)
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        if (fault is 0 or 1 or 7)
            s.Provider.BeforeIncarnationRead = () =>
            {
                s.Provider.BeforeIncarnationRead = null;
                if (fault == 0) throw new IOException("nested pre-source unavailable");
                if (fault == 1) s.Provider.Incarnation = new(2);
                if (fault == 7) Assert.True(s.Kernel.RevokeCapability(root.ConfigureCapability).IsSuccess);
            };
        s.Provider.AfterNestedMaterialize = () =>
        {
            if (fault == 2) s.Provider.ThrowIncarnation = true;
            if (fault == 3) s.Provider.Incarnation = new(2);
            if (fault == 5) throw new IOException("nested response lost");
            if (fault is 4 or 6)
                s.Provider.BeforeIncarnationRead = () =>
                {
                    s.Provider.BeforeIncarnationRead = null;
                    if (fault == 4) Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
                    else Assert.True(s.Kernel.RevokeCapability(root.ConfigureCapability).IsSuccess);
                };
        };
        Assert.False(s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).IsSuccess);
        Assert.Equal(fault is 0 or 1 or 7 ? 0 : 1, s.Provider.NestedCreateCalls);
        var locals = ((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner);
        if (fault is 2 or 3 or 4 or 6)
        {
            var local = Assert.Single(locals, x => x.ParentDomain == root.Domain);
            Assert.False(local.AuthorityPublished);
            Assert.Equal(VirtualDomainState.Quarantined, local.State);
            var table = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_childBindings");
            var child = Assert.Single(table.Values.Cast<object>(), x => Equals(x.GetType().GetProperty("Binding")!.GetValue(x), local.ChildBinding!.Value));
            Assert.False((bool)child.GetType().GetProperty("CreationPublished")!.GetValue(child)!);
            Assert.False(s.Kernel.PlatformAuthority.TransitionChildDomain(local.ChildBinding!.Value, PlatformChildDomainTransition.Start).IsSuccess);
        }
        else Assert.Single(locals);
        s.Provider.BeforeIncarnationRead = null;
        s.Provider.AfterNestedMaterialize = null;
        s.Provider.ThrowIncarnation = false;
        if (fault is 0 or 2 or 6 or 7)
        {
            Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
            Assert.Equal(fault is 2 or 6 ? 2 : 1, s.Provider.ChildCloseCalls);
        }
        else
        {
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
            Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
            Assert.Equal(0, s.Provider.ChildCloseCalls);
        }
    }

    [Theory]
    [InlineData(PlatformAuthorityStatus.NotAccepted, false)]
    [InlineData(PlatformAuthorityStatus.Denied, false)]
    [InlineData(PlatformAuthorityStatus.Stale, false)]
    [InlineData((PlatformAuthorityStatus)999, false)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, true)]
    public void FailedNestedInvocationRetainsOriginalRawAndBlocksActualReclaim(PlatformAuthorityStatus status, bool lostReply)
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var local = ((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner).Single();
        var children = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_childBindings");
        var original = children[local.ChildBinding!.Value.BindingId]!;
        object Fact() => Assert.Single(((global::System.Collections.IEnumerable)original.GetType().GetProperty("NestedCreateInvocations")!.GetValue(original)!).Cast<object>());
        var effectSeen = false;
        s.Provider.NestedReplyStatus = status;
        s.Provider.AfterNestedMaterialize = () =>
        {
            Assert.Same(original, Fact().GetType().GetProperty("Source")!.GetValue(Fact()));
            Assert.NotNull(Fact().GetType().GetProperty("Request")!.GetValue(Fact()));
            Assert.Null(Fact().GetType().GetProperty("Observation")!.GetValue(Fact()));
            Assert.False(Monitor.IsEntered(ArtifactField(s.Kernel.PlatformAuthority, "_secureDomainLifecycleGate")));
            effectSeen = true;
            if (lostReply) throw new IOException("Nested original reply lost.");
        };
        Assert.False(s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).IsSuccess);
        Assert.True(effectSeen);
        var raw = Fact().GetType().GetProperty("Observation")!.GetValue(Fact());
        if (lostReply) Assert.Null(raw);
        else Assert.Equal(status, Assert.IsType<PlatformAuthorityResult<PlatformProviderNestedDomainLease>>(raw).Status);
        s.Provider.AfterNestedMaterialize = null;
        s.Provider.NestedReplyStatus = null;
        Assert.False(s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.NestedCreateCalls);
        Assert.Equal(0, s.Provider.ChildCloseCalls);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
    }

    [Fact]
    public async Task RootChildCreateSourcePauseLeavesOwnersAvailableAndStopRefusesInvocation()
    {
        var s = CreateScenario();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        s.Provider.BeforeIncarnationRead = () =>
        {
            var roots = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_domains");
            if (!roots.Values.Cast<object>().Any(x => x.GetType().GetProperty("AuthorityState")!.GetValue(x)!.ToString() == "Active")) return;
            s.Provider.BeforeIncarnationRead = null;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(30)));
        };
        var work = Task.Run(() => s.Kernel.CreateVirtualDomain(s.Owner, s.CreateCapability, new(1, 8192)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True((await Task.Run(() => s.Kernel.Processes.Resolve(s.Owner)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.Equal(KernelError.PlatformBindingActive, (await Task.Run(() =>
                s.Kernel.CreateVirtualDomain(s.Owner, s.CreateCapability, new(1, 8192))).WaitAsync(TimeSpan.FromSeconds(5))).Error);
            Assert.False((await Task.Run(() => s.Kernel.TerminateProcess(s.Owner)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.Equal(0, s.Provider.RootCreateCalls);
        }
        finally { release.Set(); }
        Assert.False((await work.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        Assert.Equal(0, s.Provider.RootCreateCalls);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void NestedSourceGetterAncestorDrainRefusesWhileLineageIsHeld(bool late)
    {
        var s = CreateScenario();
        var bridge = s.Kernel.PlatformAuthority;
        var subject = new PlatformDomainIdentity(new DomainId(1), new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory);
        var ancestor = bridge.CreateChildDomain(root, subject, intent).Value!;
        var parent = bridge.CreateNestedChildDomain(ancestor, subject.Process, intent).Value!;
        Action install = () => s.Provider.BeforeIncarnationRead = () =>
        {
            s.Provider.BeforeIncarnationRead = null;
            Assert.False(global::System.Threading.Monitor.IsEntered(ArtifactField(bridge, "_secureDomainLifecycleGate")));
            Assert.Equal(KernelError.PlatformBindingActive, bridge.TransitionChildDomain(ancestor, PlatformChildDomainTransition.BeginDrain).Error);
        };
        if (late) s.Provider.AfterNestedMaterialize = install; else install();
        PlatformChildBinding? cleanup = null;
        Assert.True(bridge.CreateNestedChildDomain(parent, subject.Process, intent,
            cleanupConsumer: binding =>
            {
                cleanup = binding;
                Assert.False(bridge.TransitionChildDomain(binding, PlatformChildDomainTransition.Start).IsSuccess);
            }).IsSuccess);
        s.Provider.AfterNestedMaterialize = null;
        Assert.Equal(2, s.Provider.NestedCreateCalls);
        Assert.True(cleanup.HasValue);
        if (cleanup.HasValue)
        {
            Assert.True(bridge.TransitionChildDomain(cleanup.Value, PlatformChildDomainTransition.BeginDrain).IsSuccess);
            Assert.True(bridge.CloseChildDomain(cleanup.Value).IsSuccess);
        }
        Assert.True(bridge.TransitionChildDomain(parent, PlatformChildDomainTransition.BeginDrain).IsSuccess);
        Assert.True(bridge.CloseChildDomain(parent).IsSuccess);
        Assert.True(bridge.TransitionChildDomain(ancestor, PlatformChildDomainTransition.BeginDrain).IsSuccess);
        Assert.True(bridge.CloseChildDomain(ancestor).IsSuccess);
        Assert.True(bridge.RevokeDomain(root, subject).IsSuccess);
    }

    [Fact]
    public async Task NestedSourcePauseLeavesOwnersAvailableAndStopWinsBeforeProviderInvocation()
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var request = new NestedVirtualDomainRequestProfile(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        s.Provider.BeforeIncarnationRead = () =>
        {
            s.Provider.BeforeIncarnationRead = null;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(30)));
        };
        var work = Task.Run(() => s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability, request));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True((await Task.Run(() => s.Kernel.Processes.Resolve(s.Owner)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.Equal(KernelError.PlatformBindingActive, (await Task.Run(() =>
                s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability, request)).WaitAsync(TimeSpan.FromSeconds(5))).Error);
            Assert.False((await Task.Run(() => s.Kernel.TerminateProcess(s.Owner)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.Equal(0, s.Provider.NestedCreateCalls);
        }
        finally { release.Set(); }
        Assert.False((await work.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        Assert.Equal(0, s.Provider.NestedCreateCalls);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Fact]
    public void ChildCleanupDeliveryExceptionRetainsKnownLeaseWithoutPublishedAdmission()
    {
        var s = CreateScenario();
        var bridge = s.Kernel.PlatformAuthority;
        var subject = new PlatformDomainIdentity(new DomainId(1), new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        Assert.False(bridge.CreateChildDomain(root, subject, Intent(PlatformChildAuthorityClass.Lifecycle),
            cleanupConsumer: _ => throw new IOException("local cleanup delivery failed")).IsSuccess);
        var children = (global::System.Collections.IDictionary)ArtifactField(bridge, "_childBindings");
        var child = Assert.Single(children.Values.Cast<object>());
        Assert.False((bool)child.GetType().GetProperty("CreationPublished")!.GetValue(child)!);
        Assert.Equal(1, s.Provider.RootCreateCalls);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(root, subject).Error);
        Assert.Equal(0, s.Provider.ChildCloseCalls);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void ChildIdentityCapacityRefusalHasNoProviderEffectAndActualReclaimRemainsAvailable(bool nested, bool zero)
    {
        var s = CreateScenario();
        var root = nested ? CreateConfiguredRoot(s) : default;
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        ulong terminal = zero ? 0 : ulong.MaxValue;
        s.Kernel.PlatformAuthority.GetType().GetField("_nextChildBindingId",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.SetValue(s.Kernel.PlatformAuthority, terminal);
        var error = nested ? s.Kernel.CreateNestedVirtualDomain(s.Owner, root!.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).Error :
            s.Kernel.CreateVirtualDomain(s.Owner, s.CreateCapability, new(1, 8192)).Error;
        Assert.Equal(KernelError.CapacityExhausted, error);
        Assert.Equal(terminal, ArtifactField(s.Kernel.PlatformAuthority, "_nextChildBindingId"));
        Assert.Equal(nested ? 1 : 0, s.Provider.RootCreateCalls);
        Assert.Equal(0, s.Provider.NestedCreateCalls);
        Assert.Equal(nested ? 1 : 0, ((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner).Count());
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
        Assert.Equal(nested ? 1 : 0, s.Provider.ChildCloseCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LastChildIdentityIsReservedBeforeProviderAndKnownClosureDoesNotRecycleIt(bool nested)
    {
        var s = CreateScenario();
        var root = nested ? CreateConfiguredRoot(s) : default;
        s.Kernel.PlatformAuthority.GetType().GetField("_nextChildBindingId",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.SetValue(s.Kernel.PlatformAuthority, ulong.MaxValue - 1);
        Action observe = () => Assert.Equal(ulong.MaxValue, ArtifactField(s.Kernel.PlatformAuthority, "_nextChildBindingId"));
        if (nested) s.Provider.AfterNestedMaterialize = observe; else s.Provider.BeforeRootCreateReturn = observe;
        var created = nested ? s.Kernel.CreateNestedVirtualDomain(s.Owner, root!.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)) :
            s.Kernel.CreateVirtualDomain(s.Owner, s.CreateCapability, new(1, 8192));
        Assert.True(created.IsSuccess, created.Message);
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner), x => x.Handle == created.Value!.Domain);
        Assert.Equal(ulong.MaxValue - 1, local.ChildBinding!.Value.BindingId.Value);
        var refusal = nested ? s.Kernel.CreateNestedVirtualDomain(s.Owner, root!.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).Error :
            s.Kernel.PlatformAuthority.CreateChildDomain(local.ParentBinding!.Value, local.ParentBinding.Value.Subject,
                Intent(PlatformChildAuthorityClass.Lifecycle)).Error;
        Assert.Equal(KernelError.CapacityExhausted, refusal);
        Assert.Equal(1, s.Provider.RootCreateCalls);
        Assert.Equal(nested ? 1 : 0, s.Provider.NestedCreateCalls);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(ulong.MaxValue, ArtifactField(s.Kernel.PlatformAuthority, "_nextChildBindingId"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ConcurrentRootAndNestedCreationShareTheLastIdentityBeforeEitherReply(bool nestedWins)
    {
        var s = CreateScenario(); var bridge = s.Kernel.PlatformAuthority;
        var subject = new PlatformDomainIdentity(new DomainId(1), new ProcessHandle(new ProcessId(1), 1));
        var other = new PlatformDomainIdentity(new DomainId(2), new ProcessHandle(new ProcessId(2), 1));
        var root = bridge.BindDomain(subject).Value!; var otherRoot = bridge.BindDomain(other).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory);
        var parent = bridge.CreateChildDomain(root, subject, intent).Value!;
        bridge.GetType().GetField("_nextChildBindingId",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.SetValue(bridge, ulong.MaxValue - 1);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        Action pause = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(30))); };
        if (nestedWins) s.Provider.AfterNestedMaterialize = pause; else s.Provider.BeforeRootCreateReturn = pause;
        var work = Task.Run(() => nestedWins ? bridge.CreateNestedChildDomain(parent, subject.Process, intent) : bridge.CreateChildDomain(otherRoot, other, intent));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(ulong.MaxValue, ArtifactField(bridge, "_nextChildBindingId"));
            var loser = await Task.Run(() => nestedWins ? bridge.CreateChildDomain(otherRoot, other, intent) : bridge.CreateNestedChildDomain(parent, subject.Process, intent)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(KernelError.CapacityExhausted, loser.Error);
            Assert.Equal(nestedWins ? 1 : 2, s.Provider.RootCreateCalls);
            Assert.Equal(nestedWins ? 1 : 0, s.Provider.NestedCreateCalls);
        }
        finally { release.Set(); }
        var winner = await work.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(winner.IsSuccess, winner.Message);
        Assert.Equal(ulong.MaxValue - 1, winner.Value!.BindingId.Value);
        Assert.True(bridge.TransitionChildDomain(winner.Value, PlatformChildDomainTransition.BeginDrain).IsSuccess);
        Assert.True(bridge.CloseChildDomain(winner.Value).IsSuccess);
        Assert.True(bridge.TransitionChildDomain(parent, PlatformChildDomainTransition.BeginDrain).IsSuccess);
        Assert.True(bridge.CloseChildDomain(parent).IsSuccess);
        Assert.True(bridge.RevokeDomain(root, subject).IsSuccess);
        Assert.True(bridge.RevokeDomain(otherRoot, other).IsSuccess);
    }

    [Fact]
    public void GenericNotAcceptedNestedCreateDoesNotRecycleIdentityAndKeepsUnknownEffectPinned()
    {
        var s = CreateScenario(nestedFailure: PlatformAuthorityStatus.NotAccepted);
        var root = CreateConfiguredRoot(s);
        s.Kernel.PlatformAuthority.GetType().GetField("_nextChildBindingId",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.SetValue(s.Kernel.PlatformAuthority, ulong.MaxValue - 1);
        Assert.False(s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).IsSuccess);
        Assert.Equal(ulong.MaxValue, ArtifactField(s.Kernel.PlatformAuthority, "_nextChildBindingId"));
        Assert.Equal(KernelError.InvalidTransition, s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).Error);
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        Assert.Equal(KernelError.PlatformFaulted, s.Kernel.PlatformAuthority.CreateNestedChildDomain(local.ChildBinding!.Value, s.Owner,
            Intent(PlatformChildAuthorityClass.Lifecycle)).Error);
        Assert.Equal(1, s.Provider.NestedCreateCalls);
        Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        Assert.Equal(0, s.Provider.NestedCloseCalls);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)]
    [InlineData(false, 3)] [InlineData(false, 4)] [InlineData(false, 5)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)]
    [InlineData(true, 3)] [InlineData(true, 4)] [InlineData(true, 5)]
    public void MalformedGuestReturnedClosureNeverClosesOriginalInvocationOrReclaimsBacking(bool nested, int fault)
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var domain = nested ? s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.GuestMemory)).Value! : root;
        if (nested) Assert.True(s.Kernel.ConfigureVirtualDomain(s.Owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner), x => x.Handle == domain.Domain);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var capability = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        s.Provider.GuestLeaseMutation = lease => fault switch
        {
            1 => lease with { ChildLease = lease.ChildLease with { Generation = new(lease.ChildLease.Generation.Value + 1) } },
            2 => lease with { GuestRange = new(4096, 4096) },
            _ => lease with { ParentMappingLease = lease.ParentMappingLease with { Generation = new(lease.ParentMappingLease.Generation.Value + 1) } },
        };
        if (fault == 2) s.Provider.GuestClosureMutation = receipt => receipt with { Generation = new(receipt.Generation.Value + 1) };
        s.Provider.BeforeGuestUnmap = () =>
        {
            Assert.False(global::System.Threading.Monitor.IsEntered(ArtifactField(s.Kernel.PlatformAuthority, "_secureDomainLifecycleGate")));
            var parents = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_mappings");
            var original = Assert.Single(parents.Values.Cast<object>());
            Assert.Equal("Faulted", original.GetType().GetProperty("ClosureState")!.GetValue(original)!.ToString());
            var children = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_childBindings");
            var child = Assert.Single(children.Values.Cast<object>(), x => Equals(x.GetType().GetProperty("Binding")!.GetValue(x), local.ChildBinding!.Value));
            Assert.Equal("Faulted", child.GetType().GetProperty("State")!.GetValue(child)!.ToString());
            Assert.Single(((global::System.Collections.IEnumerable)original.GetType().GetProperty("UnpublishedGuestMaps")!.GetValue(original)!).Cast<object>());
            if (fault == 1) throw new IOException("returned unmap reply lost");
            if (fault == 3) Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault == 4) s.Provider.Incarnation = new(2);
            if (fault == 5) Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        };
        Assert.False(s.Kernel.MapGuestRegion(s.Owner, domain.Domain, domain.MemoryCapability, capability,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read).IsSuccess);
        s.Provider.BeforeGuestUnmap = null;
        Assert.Equal(1, s.Provider.GuestMapCalls);
        Assert.Equal(0, s.Provider.GuestUnmapCalls);
        var mappings = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_mappings");
        var parent = Assert.Single(mappings.Values.Cast<object>());
        var fact = Assert.Single(((global::System.Collections.IEnumerable)parent.GetType().GetProperty("UnpublishedGuestMaps")!.GetValue(parent)!).Cast<object>());
        Assert.Equal(local.ChildBinding!.Value, fact.GetType().GetProperty("Child")!.GetValue(fact));
        Assert.Equal(parent.GetType().GetProperty("Mapping")!.GetValue(parent), fact.GetType().GetProperty("ParentMapping")!.GetValue(fact));
        Assert.Equal(s.Provider.LastGuestRequest, fact.GetType().GetProperty("Request")!.GetValue(fact));
        Assert.Equal(s.Provider.LastGuestLease, fact.GetType().GetProperty("Receipt")!.GetValue(fact));
        Assert.Null(fact.GetType().GetProperty("ReturnedClosureReceipt")!.GetValue(fact));
        Assert.Equal(0, parent.GetType().GetProperty("PendingGuestMaps")!.GetValue(parent));
        Assert.Equal(VirtualDomainState.Quarantined, local.State);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
        Assert.Equal(0, s.Provider.ChildCloseCalls);
        Assert.DoesNotContain("mapping-close", s.Provider.CallLog);
        Assert.Equal(0, s.Provider.GuestUnmapCalls);
    }

    [Theory]
    [InlineData("transition", false, false, false)]
    [InlineData("transition", false, true, false)]
    [InlineData("transition", true, false, false)]
    [InlineData("transition", true, true, false)]
    [InlineData("close", false, false, false)]
    [InlineData("close", false, true, false)]
    [InlineData("close", true, false, false)]
    [InlineData("close", true, true, false)]
    [InlineData("map", false, false, false)]
    [InlineData("map", false, true, false)]
    [InlineData("map", true, false, false)]
    [InlineData("map", true, true, false)]
    [InlineData("unmap", false, false, false)]
    [InlineData("unmap", false, true, false)]
    [InlineData("unmap", true, false, false)]
    [InlineData("unmap", true, true, false)]
    [InlineData("bind", false, false, false)]
    [InlineData("bind", false, true, false)]
    [InlineData("bind", true, false, false)]
    [InlineData("bind", true, true, false)]
    [InlineData("start", false, false, false)]
    [InlineData("start", false, true, false)]
    [InlineData("start", true, false, false)]
    [InlineData("start", true, true, false)]
    [InlineData("transition", true, false, true)]
    [InlineData("transition", true, true, true)]
    [InlineData("close", true, false, true)]
    [InlineData("close", true, true, true)]
    [InlineData("map", true, false, true)]
    [InlineData("map", true, true, true)]
    [InlineData("unmap", true, false, true)]
    [InlineData("unmap", true, true, true)]
    [InlineData("bind", true, false, true)]
    [InlineData("bind", true, true, true)]
    [InlineData("start", true, false, true)]
    [InlineData("start", true, true, true)]
    public void NestedConsumersKeepOriginalAncestorSourcesAndUnpublishedGuestPins(string operation, bool ancestor, bool absent, bool late)
    {
        var provider = new NestedProvider(null, false, false, false, false, true);
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1), new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory | PlatformChildAuthorityClass.Execution);
        var first = bridge.CreateChildDomain(root, subject, intent).Value!;
        var parent = bridge.CreateNestedChildDomain(first, subject.Process, intent).Value!;
        var child = bridge.CreateNestedChildDomain(parent, subject.Process, intent).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)), new(subject.DomainId, subject.ProcessGeneration), 4096);
        var mapping = bridge.MapOwnedRegion(root, subject, new CapabilityId(8), region, PlatformMemoryAccess.Read).Value!;
        PlatformGuestMapping guest = default;
        if (operation is "unmap" or "bind" or "start")
            guest = bridge.MapChildGuestRegion(child, mapping, new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;
        PlatformExecutableArtifactBinding artifact = default;
        if (operation == "start") artifact = bridge.BindChildExecutableArtifact(child, guest, new byte[] { 1 }, 4).Value!;
        if (operation == "close") Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).IsSuccess);
        var children = (global::System.Collections.IDictionary)ArtifactField(bridge, "_childBindings");
        var leaf = children[child.BindingId]!;
        var original = children[(ancestor ? first : parent).BindingId]!;
        var clone = typeof(object).GetMethod("MemberwiseClone", global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.Invoke(original, null)!;
        var priorState = clone.GetType().GetProperty("State")!.GetValue(clone);
        var gate = ArtifactField(bridge, "_secureDomainLifecycleGate");
        void Replace()
        {
            Assert.False(Monitor.IsEntered(gate));
            if (absent) children.Remove((ancestor ? first : parent).BindingId); else children[(ancestor ? first : parent).BindingId] = clone;
        }
        int transitions = 0;
        provider.BeforeChildTransition = () => { transitions++; if (late && operation == "transition") Replace(); };
        if (late)
        {
            if (operation == "close") provider.BeforeChildClose = Replace;
            if (operation == "map") provider.AfterGuestMap = Replace;
            if (operation == "unmap") provider.BeforeGuestUnmap = Replace;
            if (operation == "bind") provider.AfterArtifactBind = Replace;
            if (operation == "start") provider.AfterArtifactStart = Replace;
        }
        else Replace();
        var mapCalls = provider.GuestMapCalls;
        var bindCalls = provider.ArtifactBindCalls;
        PlatformGuestMapping? known = null;
        bool Invoke() => operation switch
        {
            "transition" => bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).IsSuccess,
            "close" => bridge.CloseChildDomain(child).IsSuccess,
            "map" => bridge.MapChildGuestRegion(child, mapping, new(0, 4096), PlatformGuestMemoryAccess.Read, out known).IsSuccess,
            "unmap" => bridge.UnmapChildGuestRegion(guest).IsSuccess,
            "bind" => bridge.BindChildExecutableArtifact(child, guest, new byte[] { 1 }, 4).IsSuccess,
            _ => bridge.StartChildExecutableArtifact(artifact).IsSuccess,
        };
        Assert.False(Invoke());
        provider.BeforeChildClose = null; provider.AfterGuestMap = null; provider.BeforeGuestUnmap = null;
        provider.AfterArtifactBind = null; provider.AfterArtifactStart = null;
        var retained = known;
        Assert.False(Invoke());
        Assert.Equal(late && operation == "transition" ? 1 : 0, transitions);
        Assert.Equal(late && operation == "close" ? 1 : 0, provider.ChildCloseCalls);
        Assert.Equal(mapCalls + (late && operation == "map" ? 1 : 0), provider.GuestMapCalls);
        Assert.Equal(late && operation == "unmap" ? 1 : 0, provider.GuestUnmapCalls);
        Assert.Equal(bindCalls + (late && operation == "bind" ? 1 : 0), provider.ArtifactBindCalls);
        Assert.Equal(late && operation == "start" ? 1 : 0, provider.ArtifactStartCalls);
        Assert.Equal(0, leaf.GetType().GetProperty("PendingChildEffects")!.GetValue(leaf));
        Assert.Equal(false, leaf.GetType().GetProperty("TransitionInFlight")!.GetValue(leaf));
        Assert.Equal(false, leaf.GetType().GetProperty("CloseInFlight")!.GetValue(leaf));
        Assert.Equal(priorState, clone.GetType().GetProperty("State")!.GetValue(clone));
        var immediate = children[parent.BindingId];
        if (ancestor) Assert.Same(immediate, leaf.GetType().GetProperty("CreateParentChild")!.GetValue(leaf));
        if (absent) Assert.False(children.Contains((ancestor ? first : parent).BindingId)); else Assert.Same(clone, children[(ancestor ? first : parent).BindingId]);
        // Restore only as test arrangement: an unpublished receipt cannot acquire use permission.
        children[(ancestor ? first : parent).BindingId] = original;
        if (late && operation == "map")
        {
            Assert.NotNull(retained);
            var guests = (global::System.Collections.IDictionary)ArtifactField(bridge, "_guestBindings");
            var fact = guests[retained.Value.MappingId]!;
            Assert.Equal(false, fact.GetType().GetProperty("AdmissionPublished")!.GetValue(fact));
            Assert.False(bridge.BindChildExecutableArtifact(child, retained.Value, new byte[] { 1 }, 4).IsSuccess);
            Assert.Equal(0, provider.ArtifactBindCalls);
            Assert.True(bridge.UnmapChildGuestRegion(retained.Value).IsSuccess);
            Assert.Equal(1, provider.GuestUnmapCalls);
            Assert.Equal(PlatformExternalClosureState.Closed, fact.GetType().GetProperty("Closure")!.GetValue(fact));
        }
        else if (late) Assert.False(Invoke());
        else Assert.True(Invoke());
        Assert.Equal(priorState, clone.GetType().GetProperty("State")!.GetValue(clone));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void RetainedGuestReceiptWithoutPublicationCannotAdmitArtifactOrSecureUse(bool omitCommit)
    {
        var provider = new NestedProvider(null, false, false, false, false, true);
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1), new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var child = bridge.CreateChildDomain(root, subject, Intent(PlatformChildAuthorityClass.Lifecycle |
            PlatformChildAuthorityClass.GuestMemory | PlatformChildAuthorityClass.Execution)).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)), new(subject.DomainId, subject.ProcessGeneration), 4096);
        var parent = bridge.MapOwnedRegion(root, subject, new CapabilityId(8), region, PlatformMemoryAccess.Read).Value!;
        var result = bridge.MapChildGuestRegion(child, parent, new(0, 4096), PlatformGuestMemoryAccess.Read, out var known,
            completionConsumer: (_, commit) => omitCommit ? KernelResult.Ok() :
                commit(() => KernelResult.Fail(KernelError.PlatformFaulted, "local publication refused")));
        Assert.False(result.IsSuccess);
        Assert.NotNull(known);
        var guests = (global::System.Collections.IDictionary)ArtifactField(bridge, "_guestBindings");
        var fact = guests[known.Value.MappingId]!;
        Assert.Equal(false, fact.GetType().GetProperty("AdmissionPublished")!.GetValue(fact));
        Assert.Equal(PlatformExternalClosureState.Active, fact.GetType().GetProperty("Closure")!.GetValue(fact));
        Assert.Equal(1, provider.GuestMapCalls);
        Assert.False(bridge.BindChildExecutableArtifact(child, known.Value, new byte[] { 1 }, 4).IsSuccess);
        int commits = 0;
        Assert.False(bridge.CommitSecureGuestMapping(known.Value, parent, true, () => { commits++; return KernelResult.Ok(); }, out _, out _).IsSuccess);
        Assert.Equal(0, commits);
        Assert.Equal(0, provider.ArtifactBindCalls);
        Assert.Equal(0, fact.GetType().GetProperty("PendingSecureRegionEffects")!.GetValue(fact));
        Assert.True(bridge.UnmapChildGuestRegion(known.Value).IsSuccess);
        Assert.Equal(1, provider.GuestUnmapCalls);
        Assert.Equal(PlatformExternalClosureState.Closed, fact.GetType().GetProperty("Closure")!.GetValue(fact));
    }

    private static PlatformProviderDomainLease ParentLease() => new(new(1), new(7),
        new(new DomainId(1), new(new ProcessId(1), 1)));

    private static PlatformChildDomainIntent Intent(PlatformChildAuthorityClass authority) =>
        new(new(1, 8192), new(authority, authority));

    private sealed record Scenario(RuntimeKernel Kernel, NestedProvider Provider,
        ProcessHandle Owner, CapabilityId CreateCapability);

    private sealed class NestedProvider : IPlatformAuthorityProvider, IPlatformFeatureProvider,
        IPlatformChildDomainProvider, IPlatformNestedDomainProvider, IPlatformGuestMemoryProvider,
        IPlatformVirtualEventProvider, IPlatformVirtualTrapProvider,
        IPlatformRegionRevocationProvider, IPlatformCompletionProvider, IPlatformChildExecutionProvider, IPlatformProviderIncarnationSource
    {
        private readonly PlatformAuthorityStatus? _nestedFailure;
        private readonly bool _malformedNestedLease;
        private readonly bool _malformedTrap;
        private readonly bool _malformedNestedClose;
        private readonly bool _wrongNestedImmediateParent;
        private readonly bool _executableArtifact;
        private ulong _next = 10;
        private readonly Dictionary<PlatformProviderChildDomainLeaseId, bool> _nested = [];
        private readonly Dictionary<PlatformProviderChildDomainLeaseId, ulong> _trapSequences = [];

        public NestedProvider(PlatformAuthorityStatus? nestedFailure, bool malformedNestedLease,
            bool malformedTrap, bool malformedNestedClose, bool wrongNestedImmediateParent, bool executableArtifact)
        {
            _nestedFailure = nestedFailure;
            _malformedNestedLease = malformedNestedLease;
            _malformedTrap = malformedTrap;
            _malformedNestedClose = malformedNestedClose;
            _wrongNestedImmediateParent = wrongNestedImmediateParent;
            _executableArtifact = executableArtifact;
        }

        public bool MalformedRootCreate { get; set; }
        public int RootCreateCalls { get; private set; }
        public Action? BeforeRootCreateReturn { get; set; }
        public PlatformProviderIncarnation Incarnation { get; set; } = new(1);
        public bool ThrowIncarnation { get; set; }
        public Action? BeforeIncarnationRead { get; set; }
        public PlatformProviderIncarnation CurrentIncarnation
        {
            get
            {
                BeforeIncarnationRead?.Invoke();
                if (ThrowIncarnation) throw new IOException("Known parent source temporarily unavailable");
                return Incarnation;
            }
        }
        public int NestedCreateCalls { get; private set; }
        public bool ThrowAfterNestedMaterialize { get; set; }
        public Action? AfterNestedMaterialize { get; set; }
        public Func<PlatformProviderNestedDomainLease, PlatformProviderNestedDomainLease>? NestedReceiptMutation { get; set; }
        public PlatformAuthorityStatus? NestedReplyStatus { get; set; }
        public Action? BeforeChildTransition { get; set; }
        public PlatformAuthorityStatus? ChildTransitionStatus { get; set; }
        public bool ThrowAfterArtifactBind { get; set; }
        public bool ThrowAfterArtifactStart { get; set; }
        public Action? AfterArtifactBind { get; set; }
        public Action? AfterArtifactStart { get; set; }
        public int ArtifactStartCalls { get; private set; }
        public int ArtifactBindCalls { get; private set; }
        public ReadOnlyMemory<byte> LastArtifactPackage { get; private set; }
        public PlatformAuthorityStatus? ArtifactStatus { get; set; }
        public bool MalformedArtifactEvidence { get; set; }
        public int TrapCalls { get; private set; }
        public int GuestUnmapCalls { get; private set; }
        public int NestedCloseCalls { get; private set; }
        public PlatformAuthorityStatus? ChildClosureStatus { get; set; }
        public int ChildCloseCalls { get; private set; }
        public Action? BeforeChildClose { get; set; }
        public bool MalformedChildClosure { get; set; }
        public PlatformProviderChildDomainLease LastNestedLease { get; private set; }
        public int ArtifactRetiredWorkUnits { get; set; } = 1;
        public PlatformChildExecutionReceipt? LastExecutionReceipt { get; private set; }
        public List<string> CallLog { get; } = [];
        public PlatformProviderDescriptor Descriptor { get; } = new(new("phase8.nested-test"), 2,
            PlatformAuthorityFeatures.NeutralDomainBinding | PlatformAuthorityFeatures.DirectOwnedRegionMapping);

        public PlatformFeatureManifest QueryFeatures()
        {
            var features = new List<PlatformFeatureDescriptor>
            {
            new(PlatformFeatureFamily.NeutralDomains, PlatformDomainContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new(PlatformFeatureFamily.OwnedRegionMapping, PlatformOwnedRegionMappingContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new(PlatformFeatureFamily.VirtualizationDomains, PlatformChildDomainContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new(PlatformFeatureFamily.NestedDomains, PlatformNestedDomainContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new(PlatformFeatureFamily.ChildDomainLifecycle, PlatformChildDomainContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new(PlatformFeatureFamily.ChildGuestMemory, PlatformGuestMemoryContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new(PlatformFeatureFamily.GuestMemory, PlatformGuestMemoryContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new(PlatformFeatureFamily.ChildEventDelivery, PlatformVirtualEventContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new(PlatformFeatureFamily.VirtualEvents, PlatformVirtualEventContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new(PlatformFeatureFamily.ChildTrapDelivery, PlatformVirtualTrapContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new(PlatformFeatureFamily.VirtualTraps, PlatformVirtualTrapContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            };
            if (_executableArtifact)
                features.Add(new(PlatformFeatureFamily.ChildExecutableArtifact,
                    PlatformChildExecutionContract.ContractVersion, PlatformFeatureAvailability.Executable));
            return new(features);
        }

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject) =>
            PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(new(new(_next++), new(7), subject));

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease)
        {
            CallLog.Add("parent-close");
            return PlatformAuthorityResult.Ok();
        }

        public int ParentMapCalls { get; private set; }
        public Action? AfterParentMap { get; set; }
        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease, PlatformRegionIdentity region, PlatformMemoryAccess access)
        {
            ParentMapCalls++;
            var lease = new PlatformProviderRegionMappingLease(new(_next++), new(11), domainLease, region, access);
            AfterParentMap?.Invoke();
            return PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy) => PlatformAuthorityResult.Ok();

        public PlatformAuthorityResult<PlatformRegionRevocationTicket> BeginRegionMappingRevocation(
            PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy)
        {
            CallLog.Add("mapping-close");
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Ok(new(mapping.MappingId,
                mapping.Generation, new(new(_next++), new(1), mapping.DomainLease)));
        }

        public bool MappingCompletionPending { get; set; }
        public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(PlatformOperationIdentity operation) =>
            PlatformAuthorityResult<PlatformCompletionReceipt>.Ok(new(operation.OperationId,
                operation.Generation, operation.DomainLease, MappingCompletionPending ? PlatformCompletionState.Pending : PlatformCompletionState.Closed));

        public PlatformAuthorityResult<PlatformProviderChildDomainLease> CreateChildDomain(
            PlatformProviderDomainLease parentLease, PlatformChildDomainIntent intent)
        {
            RootCreateCalls++;
            var lease = new PlatformProviderChildDomainLease(new(_next++), new(23),
                MalformedRootCreate ? parentLease with { Generation = new(parentLease.Generation.Value + 1) } : parentLease, intent);
            BeforeRootCreateReturn?.Invoke();
            return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Ok(lease);
        }

        public PlatformAuthorityResult<PlatformProviderNestedDomainLease> CreateNestedChildDomain(
            PlatformNestedDomainRequest request)
        {
            NestedCreateCalls++;
            if (_nestedFailure is { } failure)
                return PlatformAuthorityResult<PlatformProviderNestedDomainLease>.Fail(failure, "Injected nested admission failure.");
            var returnedLease = new PlatformProviderChildDomainLease(new(_next++), new(47), request.ParentChildLease.ParentDomainLease,
                _malformedNestedLease
                    ? request.ChildIntent with { Authority = new(request.ChildIntent.Authority.ParentAuthority,
                        request.ChildIntent.Authority.ParentAuthority | PlatformChildAuthorityClass.Execution) }
                    : request.ChildIntent);
            LastNestedLease = returnedLease;
            _nested[returnedLease.LeaseId] = true;
            AfterNestedMaterialize?.Invoke();
            if (NestedReplyStatus is { } replyStatus)
                return PlatformAuthorityResult<PlatformProviderNestedDomainLease>.Fail(replyStatus, "Failed original nested reply after materialization.");
            if (ThrowAfterNestedMaterialize)
                throw new InvalidOperationException("Nested child materialized without receipt.");
            var receipt = new PlatformProviderNestedDomainLease(returnedLease,
                _wrongNestedImmediateParent ? new PlatformProviderChildDomainLeaseId(999) : request.ParentChildLease.LeaseId,
                request.ParentChildLease.Generation);
            return PlatformAuthorityResult<PlatformProviderNestedDomainLease>.Ok(NestedReceiptMutation is null ? receipt : NestedReceiptMutation(receipt));
        }

        public PlatformAuthorityResult TransitionChildDomain(PlatformProviderChildDomainLease lease,
            PlatformChildDomainTransition transition)
        {
            BeforeChildTransition?.Invoke();
            if (ChildTransitionStatus == PlatformAuthorityStatus.NotAccepted)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.NotAccepted, "Controlled refusal before child transition effect.");
            if (transition == PlatformChildDomainTransition.BeginDrain)
                CallLog.Add(_nested.ContainsKey(lease.LeaseId) ? "nested-drain" : "root-drain");
            return ChildTransitionStatus is { } status
                ? PlatformAuthorityResult.Fail(status, "Controlled unknown child transition outcome after effect.")
                : PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformChildDomainClosureReceipt> CloseChildDomain(
            PlatformProviderChildDomainLease lease)
        {
            ChildCloseCalls++;
            BeforeChildClose?.Invoke();
            if (ChildClosureStatus is { } status)
                return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(status, "controlled compensation failure");
            bool nested = _nested.ContainsKey(lease.LeaseId);
            if (nested) NestedCloseCalls++;
            CallLog.Add(nested ? "nested-close" : "root-close");
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Ok(new(lease.LeaseId,
                MalformedChildClosure || nested && _malformedNestedClose ? new(lease.Generation.Value + 1) : lease.Generation,
                lease.ParentDomainLease.LeaseId, lease.ParentDomainLease.Generation,
                PlatformChildDomainClosureDisposition.Closed));
        }

        public int GuestMapCalls { get; private set; }
        public Action? AfterGuestMap { get; set; }
        public PlatformAuthorityStatus? GuestMapStatus { get; set; }
        public Func<PlatformProviderGuestRegionMappingLease, PlatformProviderGuestRegionMappingLease>? GuestLeaseMutation { get; set; }
        public Func<PlatformGuestRegionMappingClosureReceipt, PlatformGuestRegionMappingClosureReceipt>? GuestClosureMutation { get; set; }
        public PlatformGuestRegionMappingRequest LastGuestRequest { get; private set; }
        public PlatformProviderGuestRegionMappingLease LastGuestLease { get; private set; }
        public PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease> MapGuestRegion(
            PlatformGuestRegionMappingRequest request)
        {
            GuestMapCalls++;
            var lease = new PlatformProviderGuestRegionMappingLease(new(_next++), new(31),
                request.ChildLease, request.ParentMapping.Lease, request.GuestRange, request.Access);
            LastGuestRequest = request;
            if (GuestLeaseMutation is not null) lease = GuestLeaseMutation(lease);
            LastGuestLease = lease;
            AfterGuestMap?.Invoke();
            if (GuestMapStatus is { } status)
                return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(status, "Controlled guest-map response after callback.");
            return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Ok(lease);
        }

        public Action? BeforeGuestUnmap { get; set; }
        public PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt> UnmapGuestRegion(
            PlatformProviderGuestRegionMappingLease lease)
        {
            GuestUnmapCalls++;
            BeforeGuestUnmap?.Invoke();
            CallLog.Add("guest-unmap");
            var receipt = new PlatformGuestRegionMappingClosureReceipt(lease.LeaseId,
                lease.Generation, lease.ChildLease.LeaseId, lease.ChildLease.Generation,
                lease.ChildLease.ParentDomainLease.LeaseId, lease.ChildLease.ParentDomainLease.Generation, true);
            return PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Ok(GuestClosureMutation is null ? receipt : GuestClosureMutation(receipt));
        }

        public int EventCalls { get; private set; }
        public PlatformAuthorityStatus? DeliveryStatus { get; set; }
        public Func<PlatformVirtualEventReceipt, PlatformVirtualEventReceipt>? EventReceiptMutation { get; set; }
        public Func<PlatformVirtualTrapEvidence, PlatformVirtualTrapEvidence>? TrapEvidenceMutation { get; set; }
        public PlatformVirtualEventRequest LastEventRequest { get; private set; }
        public PlatformProviderChildDomainLease LastTrapRequest { get; private set; }
        public PlatformAuthorityResult<PlatformVirtualEventReceipt> LastEventResponse { get; private set; }
        public PlatformAuthorityResult<PlatformVirtualTrapEvidence> LastTrapResponse { get; private set; }
        public Action? AfterChildEvent { get; set; }
        public Action? AfterChildTrap { get; set; }
        public PlatformAuthorityResult<PlatformVirtualEventReceipt> InjectVirtualEvent(PlatformVirtualEventRequest request)
        {
            EventCalls++;
            LastEventRequest = request;
            var receipt = new PlatformVirtualEventReceipt(request.ChildLease.LeaseId,
                request.ChildLease.Generation, request.ChildLease.ParentDomainLease.LeaseId,
                request.ChildLease.ParentDomainLease.Generation, 1, request.EventClass, request.SourceResourceId);
            AfterChildEvent?.Invoke();
            LastEventResponse = DeliveryStatus is { } status ?
                PlatformAuthorityResult<PlatformVirtualEventReceipt>.Fail(status, "controlled original delivery failure") :
                PlatformAuthorityResult<PlatformVirtualEventReceipt>.Ok(EventReceiptMutation is null ? receipt : EventReceiptMutation(receipt));
            return LastEventResponse;
        }

        public PlatformAuthorityResult<PlatformVirtualTrapEvidence> ObserveVirtualTrap(
            PlatformProviderChildDomainLease lease)
        {
            TrapCalls++;
            LastTrapRequest = lease;
            var sequence = _trapSequences.GetValueOrDefault(lease.LeaseId) + 1;
            _trapSequences[lease.LeaseId] = sequence;
            AfterChildTrap?.Invoke();
            var evidence = new PlatformVirtualTrapEvidence(lease.LeaseId,
                _malformedTrap ? new(lease.Generation.Value + 1) : lease.Generation,
                lease.ParentDomainLease.LeaseId, lease.ParentDomainLease.Generation,
                sequence, PlatformVirtualTrapKind.Timer);
            LastTrapResponse = DeliveryStatus is { } status ?
                PlatformAuthorityResult<PlatformVirtualTrapEvidence>.Fail(status, "controlled original trap failure") :
                PlatformAuthorityResult<PlatformVirtualTrapEvidence>.Ok(TrapEvidenceMutation is null ? evidence : TrapEvidenceMutation(evidence));
            return LastTrapResponse;
        }

        public PlatformAuthorityResult<PlatformExecutableArtifactReceipt> BindExecutableArtifact(
            PlatformExecutableArtifactRequest request)
        {
            ArtifactBindCalls++;
            if (ArtifactStatus is { } status) return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(status, "controlled admission disposition");
            LastArtifactPackage = request.ImmutablePackage;
            var receipt = new PlatformExecutableArtifactReceipt(new(1), new(1),
                request.ChildLease.LeaseId, request.ChildLease.Generation,
                request.GuestMapping.LeaseId, request.GuestMapping.Generation,
                request.ChildLease.ParentDomainLease.LeaseId, request.ChildLease.ParentDomainLease.Generation,
                MalformedArtifactEvidence ? new string('a', 64) : Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(request.ImmutablePackage.Span)), request.MaximumExecutionSteps);
            if (ThrowAfterArtifactBind)
                throw new InvalidOperationException("Artifact binding receipt lost.");
            AfterArtifactBind?.Invoke();
            return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Ok(receipt);
        }

        public PlatformAuthorityResult<PlatformChildExecutionReceipt> StartExecutableArtifact(
            PlatformChildExecutionStartRequest request)
        {
            ArtifactStartCalls++;
            if (ArtifactStatus is { } status) return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(status, "controlled start disposition");
            LastExecutionReceipt = new(request.Artifact.ArtifactId, request.Artifact.ArtifactGeneration,
                request.Artifact.ChildLeaseId, request.Artifact.ChildGeneration, request.Artifact.MappingLeaseId,
                request.Artifact.MappingGeneration, request.Artifact.ParentLeaseId, request.Artifact.ParentGeneration,
                request.Artifact.ContentDigest, request.OperationId, request.OperationGeneration, new(1), ArtifactRetiredWorkUnits, 1, 0, true);
            if (MalformedArtifactEvidence) LastExecutionReceipt = LastExecutionReceipt.Value with { IsTerminal = false };
            if (ThrowAfterArtifactStart)
                throw new InvalidOperationException("Artifact start receipt lost.");
            AfterArtifactStart?.Invoke();
            return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Ok(LastExecutionReceipt.Value);
        }
    }
}


