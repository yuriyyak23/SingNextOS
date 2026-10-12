using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed class Phase7EvidenceSecureComputeTests
{
    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)] [InlineData(false, 3)]
    [InlineData(false, 4)] [InlineData(false, 5)] [InlineData(false, 6)] [InlineData(false, 7)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)] [InlineData(true, 3)]
    [InlineData(true, 4)] [InlineData(true, 5)] [InlineData(true, 6)] [InlineData(true, 7)]
    public void KnownOriginalSecureClosureRejectsForeignOwnerHistoryAndIndependentFault(bool region, int fault)
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 128).Value!;
        var secure = s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent, new([SecureProperty.PrivateMemory], 4096)).Value!;
        PlatformRegionMapping mapping = default;
        if (region)
        {
            var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
            var memory = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
                CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
            mapping = s.Kernel.MapPlatformOwnedRegion(s.Owner, s.Parent, memory, buffer.Handle, PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
            Assert.True(s.Kernel.BindSecureRegion(s.Owner, secure.Domain, secure.MemoryCapability, mapping, PlatformSecureRegionClass.Private).IsSuccess);
            s.Kernel.BeforeSecureRegionLocalCommit = () => throw new IOException("known Closed");
        }
        else s.Kernel.BeforeSecureDomainClosureLocalCommit = () => throw new IOException("known Closed");
        Assert.False((region ? s.Kernel.CloseSecureRegion(s.Owner, secure.Domain, secure.MemoryCapability, mapping) :
            s.Kernel.DestroySecureDomain(s.Owner, secure.Domain, secure.ConfigureCapability)).IsSuccess);
        s.Kernel.BeforeSecureRegionLocalCommit = null; s.Kernel.BeforeSecureDomainClosureLocalCommit = null;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object Clone(object value) => typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(value, null)!;
        var bridge = s.Kernel.PlatformAuthority;
        var routes = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_secureDomains", flags)!.GetValue(bridge)!;
        var canonical = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_originalSecureOwners", flags)!.GetValue(bridge)!;
        var key = routes.Keys.Cast<object>().Single(); var record = routes[key]!;
        var fact = region ? ((global::System.Collections.IEnumerable)record.GetType().GetProperty("RegionClosureInvocations")!.GetValue(record)!).Cast<object>().Single() :
            record.GetType().GetProperty("RevokeInvocation")!.GetValue(record)!;
        switch (fault)
        {
            case 0: record.GetType().GetProperty("TerminalQuarantined")!.SetValue(record, true); break;
            case 1: routes.Remove(key); break;
            case 2: routes[key] = Clone(record); break;
            case 3: routes[key] = canonical[key] = Clone(record); break;
            case 4: fact.GetType().GetProperty("Observation")!.SetValue(fact, null); break;
            case 5:
                if (region) ((global::System.Collections.IList)record.GetType().GetProperty("RegionClosureInvocations")!.GetValue(record)!)[0] = Clone(fact);
                else record.GetType().GetProperty("RevokeInvocation")!.SetValue(record, Clone(fact));
                break;
            case 6: s.Provider.ReadIncarnation = () => new(2); break;
            case 7: record.GetType().GetProperty("TerminalRevision")!.SetValue(record, long.MaxValue); record.GetType().GetProperty("TerminalQuarantined")!.SetValue(record, true); break;
        }
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(region ? 1 : 0, s.Provider.SecureUnbindCalls);
        Assert.Equal(region ? 0 : 1, s.Provider.SecureRevokeCalls);
        Assert.Equal(RegionState.Owned, s.Kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        Assert.False((bool)record.GetType().GetProperty("RevokeInFlight")!.GetValue(record)!);
        Assert.Empty(((global::System.Collections.IEnumerable)record.GetType().GetProperty("RegionMutationsInFlight")!.GetValue(record)!).Cast<object>());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    public void OriginalSecureRegionBindingFaultKeepsActualBackingPinnedWithoutClosingForeignLifetime(int fault)
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 128).Value!;
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var memory = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var mapping = s.Kernel.MapPlatformOwnedRegion(s.Owner, s.Parent, memory, buffer.Handle, PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var secure = s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent, new([SecureProperty.PrivateMemory], 4096)).Value!;
        Assert.True(s.Kernel.BindSecureRegion(s.Owner, secure.Domain, secure.MemoryCapability, mapping, PlatformSecureRegionClass.Private).IsSuccess);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object Clone(object value) => typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(value, null)!;
        var bridge = s.Kernel.PlatformAuthority;
        var record = ((global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_secureDomains", flags)!.GetValue(bridge)!).Values.Cast<object>().Single();
        var owners = (global::System.Collections.IDictionary)record.GetType().GetProperty("RegionBindingOwners")!.GetValue(record)!;
        var original = owners[mapping.MappingId]!;
        var local = ((global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureDomainRecords", flags)!.GetValue(s.Kernel)!)[secure.Domain.DomainId]!;
        var localSources = (global::System.Collections.IDictionary)local.GetType().GetProperty("RegionBindingSources")!.GetValue(local)!;
        if (fault >= 6)
        {
            s.Kernel.BeforeSecureRegionLocalCommit = () => throw new IOException("original Closed before local consumption");
            Assert.False(s.Kernel.CloseSecureRegion(s.Owner, secure.Domain, secure.MemoryCapability, mapping).IsSuccess);
            s.Kernel.BeforeSecureRegionLocalCommit = null;
        }
        switch (fault)
        {
            case 0: owners.Remove(mapping.MappingId); break;
            case 1: owners[mapping.MappingId] = Clone(original); break;
            case 2: localSources.Remove(mapping.MappingId); break;
            case 3: localSources[mapping.MappingId] = Clone(original); break;
            case 4: original.GetType().GetProperty("Observation")!.SetValue(original, null); break;
            case 5: s.Provider.ReadIncarnation = () => { owners[mapping.MappingId] = Clone(original); return new(1); }; break;
            case 6: record.GetType().GetProperty("TerminalQuarantined")!.SetValue(record, true); break;
            case 7: original.GetType().GetProperty("Observation")!.SetValue(original, null); break;
            case 8: owners[mapping.MappingId] = Clone(original); break;
            case 9: localSources[mapping.MappingId] = Clone(original); break;
            case 10:
                var facts = (global::System.Collections.IList)record.GetType().GetProperty("RegionClosureInvocations")!.GetValue(record)!;
                facts.Clear(); break;
        }
        Assert.False(s.Kernel.CloseSecureRegion(s.Owner, secure.Domain, secure.MemoryCapability, mapping).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(fault >= 6 ? 1 : 0, s.Provider.SecureUnbindCalls);
        Assert.Equal(RegionState.Owned, s.Kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        Assert.False((bool)local.GetType().GetProperty("RegionMutationInFlight")!.GetValue(local)!);
        Assert.Empty(((global::System.Collections.IEnumerable)record.GetType().GetProperty("RegionMutationsInFlight")!.GetValue(record)!).Cast<object>());
    }

    [Fact]
    public void OriginalKernelSecureRegionConsumesItsOwnClosedBindingAndLeavesANewerBindingAlive()
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 128).Value!;
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var memory = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var mapping = s.Kernel.MapPlatformOwnedRegion(s.Owner, s.Parent, memory, buffer.Handle, PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var secure = s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent, new([SecureProperty.PrivateMemory], 4096)).Value!;
        Assert.True(s.Kernel.BindSecureRegion(s.Owner, secure.Domain, secure.MemoryCapability, mapping, PlatformSecureRegionClass.Private).IsSuccess);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var bridge = s.Kernel.PlatformAuthority;
        var record = ((global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_secureDomains", flags)!.GetValue(bridge)!).Values.Cast<object>().Single();
        var binding = (PlatformAuthorityBridge.SecureDomainBinding)record.GetType().GetProperty("Binding")!.GetValue(record)!;
        var original = s.Provider.LastSecureRegionBinding;
        Assert.True(bridge.UnbindSecureRegion(binding, mapping).IsSuccess);
        Assert.True(bridge.BindSecureRegion(binding, mapping, PlatformSecureRegionClass.Private).IsSuccess);
        var newer = s.Provider.LastSecureRegionBinding;
        Assert.NotEqual(original, newer);
        Assert.True(s.Kernel.CloseSecureRegion(s.Owner, secure.Domain, secure.MemoryCapability, mapping).IsSuccess);
        Assert.Equal(1, s.Provider.SecureUnbindCalls);
        Assert.Equal(newer, ((global::System.Collections.IDictionary)record.GetType().GetProperty("Regions")!.GetValue(record)!)[mapping.MappingId]);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Owned, s.Kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        Assert.True(bridge.UnbindSecureRegion(binding, mapping).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(2, s.Provider.SecureUnbindCalls);
        Assert.Equal(RegionState.Released, s.Kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RetainedOriginalSecureClosureAfterLocalCompletionFaultMustReachActualReclaimWithoutRepeatingEffect(bool region)
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var admin = TestFixtures.Create(s.Kernel, 98, 998).Handle;
        var budgetCap = s.Kernel.MintCapability(new(998), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = s.Kernel.AdmitProcessBudget(admin, budgetCap, s.Owner, "secure-closure", [new(ServiceBudgetDimension.OwnedMemoryBytes, 128)]).Value!.ProcessBudget;
        var buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 128).Value!;
        var secure = s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent, new([SecureProperty.PrivateMemory], 4096)).Value!;
        PlatformRegionMapping mapping = default;
        if (region)
        {
            var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
            var memory = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
                CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
            mapping = s.Kernel.MapPlatformOwnedRegion(s.Owner, s.Parent, memory, buffer.Handle,
                PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
            Assert.True(s.Kernel.BindSecureRegion(s.Owner, secure.Domain, secure.MemoryCapability, mapping, PlatformSecureRegionClass.Private).IsSuccess);
            s.Kernel.BeforeSecureRegionLocalCommit = () => throw new IOException("known original region Closed, local completion lost");
        }
        else s.Kernel.BeforeSecureDomainClosureLocalCommit = () => throw new IOException("known original domain Closed, local completion lost");
        Assert.False((region ? s.Kernel.CloseSecureRegion(s.Owner, secure.Domain, secure.MemoryCapability, mapping)
            : s.Kernel.DestroySecureDomain(s.Owner, secure.Domain, secure.ConfigureCapability)).IsSuccess);
        Assert.Equal(128UL, s.Kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        s.Kernel.BeforeSecureRegionLocalCommit = null; s.Kernel.BeforeSecureDomainClosureLocalCommit = null;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(region ? 1 : 0, s.Provider.SecureUnbindCalls);
        Assert.Equal(1, s.Provider.SecureRevokeCalls);
        Assert.Equal(RegionState.Released, s.Kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        Assert.Equal(0UL, s.Kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)] [InlineData(16)]
    [InlineData(17)] [InlineData(18)]
    public void KnownDrainCleanupConsumesOnlyOriginalFactAndOwnQuarantine(int fault)
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 128).Value!;
        var secure = s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent, new([SecureProperty.PrivateMemory], 4096)).Value!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var local = ((global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureDomainRecords", flags)!.GetValue(s.Kernel)!)[secure.Domain.DomainId]!;
        var bridge = s.Kernel.PlatformAuthority;
        var routes = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_secureDomains", flags)!.GetValue(bridge)!;
        var originals = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_originalSecureOwners", flags)!.GetValue(bridge)!;
        var binding = local.GetType().GetProperty("Binding")!.GetValue(local)!;
        var key = binding.GetType().GetProperty("BindingId")!.GetValue(binding)!;
        var original = routes[key]!;
        object Clone(object value) => typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(value, null)!;
        void Set(string member, object? value) => original.GetType().GetProperty(member)!.SetValue(original, value);
        s.Kernel.BeforeSecureDomainTransitionLocalCommit = () =>
        {
            local.GetType().GetProperty("State")!.SetValue(local, local.GetType().GetProperty("State")!.GetValue(local));
            if (fault == 16) throw new InvalidOperationException("independent callback failure");
        };
        Assert.False(s.Kernel.TransitionSecureDomain(s.Owner, secure.Domain, secure.ExecuteCapability, PlatformSecureDomainTransition.BeginDrain).IsSuccess);
        s.Kernel.BeforeSecureDomainTransitionLocalCommit = null;
        var fact = original.GetType().GetProperty("LastTransitionInvocation")!.GetValue(original)!;
        if (fault is 13 or 14 or 15 or 17 or 18)
            Assert.True(bridge.ConsumeKnownSecureDrainForCleanup((PlatformAuthorityBridge.SecureDomainBinding)binding).IsSuccess);
        switch (fault)
        {
            case 0: Set("TerminalQuarantined", true); break;
            case 1: Set("TerminalQuarantined", false); Set("TerminalQuarantined", true); break;
            case 2: Set("TerminalRevision", long.MaxValue); Set("TerminalQuarantined", true); break;
            case 3: routes.Remove(key); break;
            case 4: routes[key] = Clone(original); break;
            case 5: routes[key] = originals[key] = Clone(original); break;
            case 6: Set("LastTransitionInvocation", Clone(fact)); break;
            case 7: case 13: Set("LastTransitionInvocation", null); break;
            case 8: fact.GetType().GetProperty("Observation")!.SetValue(fact, null); break;
            case 9: case 15: s.Provider.ReadIncarnation = () => new(2); break;
            case 10: Set("TransitionInFlight", true); break;
            case 11: s.Provider.ReadIncarnation = () => throw new InvalidOperationException("source unavailable"); break;
            case 12: s.Provider.BeforeSecureRevoke = () => throw new InvalidOperationException("revoke reply lost"); break;
            case 14: Set("TerminalQuarantined", false); break;
            case 17:
                s.Provider.ReadIncarnation = () => { Set("TerminalQuarantined", false); return new(1); }; break;
            case 18:
                s.Provider.BeforeSecureRevoke = () => Set("TerminalQuarantined", false); break;
        }
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.SecureTransitionCalls);
        Assert.Equal(fault is 12 or 18 ? 1 : 0, s.Provider.SecureRevokeCalls);
        Assert.Equal(RegionState.Owned, s.Kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        if (fault is 3 or 4 or 5 or 6 or 7 or 9 or 10 or 11 or 13 or 15)
        {
            routes[key] = originals[key] = original;
            Set("LastTransitionInvocation", fact); Set("TransitionInFlight", false);
            s.Provider.ReadIncarnation = null;
            Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(1, s.Provider.SecureTransitionCalls); Assert.Equal(1, s.Provider.SecureRevokeCalls);
            Assert.Equal(RegionState.Released, s.Kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        }
        else
        {
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(1, s.Provider.SecureTransitionCalls);
            Assert.Equal(fault is 12 or 18 ? 1 : 0, s.Provider.SecureRevokeCalls);
        }
    }

    [Fact]
    public void KnownOriginalDrainAfterLocalRevisionRefusalMustReachActualReclaimWithoutRepeatingTransition()
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var region = s.Kernel.AllocateBuffer<byte>(s.Owner, 128).Value!;
        var secure = s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent, new([SecureProperty.PrivateMemory], 4096)).Value!;
        var routes = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureDomainRecords", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        var original = routes[secure.Domain.DomainId]!;
        s.Kernel.BeforeSecureDomainTransitionLocalCommit = () => original.GetType().GetProperty("State")!.SetValue(original, original.GetType().GetProperty("State")!.GetValue(original));
        Assert.False(s.Kernel.TransitionSecureDomain(s.Owner, secure.Domain, secure.ExecuteCapability, PlatformSecureDomainTransition.BeginDrain).IsSuccess);
        s.Kernel.BeforeSecureDomainTransitionLocalCommit = null;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.SecureTransitionCalls); Assert.Equal(1, s.Provider.SecureRevokeCalls);
        Assert.Equal(RegionState.Released, s.Kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
    }

    [Theory]
    [InlineData(false, "State")] [InlineData(true, "State")]
    [InlineData(false, "ProtectionGeneration")] [InlineData(true, "PolicyGeneration")]
    public void ActualSecureRegionBindUsesOneLocalSourceRevisionThroughProviderAndKernelCommit(bool late, string property)
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 128).Value!;
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var memory = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var mapping = s.Kernel.MapPlatformOwnedRegion(s.Owner, s.Parent, memory, buffer.Handle, PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var secure = s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent, new([SecureProperty.PrivateMemory], 4096)).Value!;
        var routes = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureDomainRecords", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        var original = routes[secure.Domain.DomainId]!;
        void Change()
        {
            var member = original.GetType().GetProperty(property)!;
            member.SetValue(original, member.GetValue(original));
        }
        if (late) s.Kernel.BeforeSecureRegionLocalCommit = Change; else s.Kernel.BeforeSecureRegionAdmission = Change;
        Assert.False(s.Kernel.BindSecureRegion(s.Owner, secure.Domain, secure.MemoryCapability, mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(late ? 1 : 0, s.Provider.SecureBindCalls);
        Assert.Equal(false, original.GetType().GetProperty("RegionMutationInFlight")!.GetValue(original));
        s.Kernel.BeforeSecureRegionLocalCommit = null; s.Kernel.BeforeSecureRegionAdmission = null;
        if (late)
        {
            Assert.False(s.Kernel.RevokePlatformRegionMapping(s.Owner, mapping).IsSuccess);
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(1, s.Provider.SecureBindCalls);
            Assert.Equal(RegionState.Owned, s.Kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        }
        else
        {
            Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(RegionState.Released, s.Kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        }
    }

    [Theory]
    [InlineData(false, "State", false)] [InlineData(true, "State", false)]
    [InlineData(false, "State", true)] [InlineData(true, "State", true)]
    [InlineData(false, "PolicyGeneration", false)] [InlineData(true, "PolicyGeneration", false)]
    [InlineData(false, "PolicyGeneration", true)] [InlineData(true, "PolicyGeneration", true)]
    [InlineData(false, "ProtectionGeneration", false)] [InlineData(true, "ProtectionGeneration", false)]
    [InlineData(false, "ProtectionGeneration", true)] [InlineData(true, "ProtectionGeneration", true)]
    public void OriginalLocalSecureRevisionMustSurviveActualTransitionEffectAndPublication(bool late, string property, bool aba)
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var admin = TestFixtures.Create(s.Kernel, 99, 999).Handle;
        var cap = s.Kernel.MintCapability(new(999), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = s.Kernel.AdmitProcessBudget(admin, cap, s.Owner, "secure-revision", [new(ServiceBudgetDimension.OwnedMemoryBytes, 128)]).Value!.ProcessBudget;
        var buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 128).Value!;
        var secure = s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent, new([SecureProperty.PrivateMemory], 4096)).Value!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var records = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureDomainRecords", flags)!.GetValue(s.Kernel)!;
        var record = records[secure.Domain.DomainId]!;
        var state = record.GetType().GetProperty(property)!;
        var original = state.GetValue(record)!;
        var changed = false;
        void Mutate()
        {
            if (changed) return;
            changed = true;
            if (aba) state.SetValue(record, property == "State" ? (object)SecureDomainState.Parked : (ulong)original + 1);
            state.SetValue(record, original);
        }
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        if (late) Assert.Equal(1, s.Provider.SecureTransitionCalls);
        Assert.Equal(RegionState.Released, s.Kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        Assert.Equal(0UL, s.Kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Fact]
    public void SurvivingOriginalSecureSourceRefusesNewCreateThroughEqualTupleRootClone()
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        Assert.True(s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent,
            new([SecureProperty.PrivateMemory], 4096)).IsSuccess);
        Assert.True(s.Kernel.AllocateBuffer<byte>(s.Owner, 64).IsSuccess);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var routes = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_domains", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
        var original = routes[s.Parent.BindingId]!;
        var foreign = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        routes[s.Parent.BindingId] = foreign;
        ((global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_activeSubjects", flags)!.GetValue(s.Kernel.PlatformAuthority)!)[s.Parent.Subject] = foreign;
        Assert.False(s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent,
            new([SecureProperty.PrivateMemory], 4096)).IsSuccess);
        Assert.Equal(1, s.Provider.SecureCreateCalls);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.Equal(0, s.Provider.SecureRevokeCalls);
        Assert.Equal(0, s.Provider.ParentRevokeCalls);
    }

    [Fact]
    public void SurvivingSecureOriginalFactBlocksRootReplacementWhenBothRootIndexesAreLost()
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        Assert.True(s.Kernel.AllocateBuffer<byte>(s.Owner, 64).IsSuccess);
        Assert.True(s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent,
            new([SecureProperty.PrivateMemory], 4096)).IsSuccess);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var bridge = s.Kernel.PlatformAuthority;
        ((global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_activeSubjects", flags)!.GetValue(bridge)!).Remove(s.Parent.Subject);
        ((global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_domains", flags)!.GetValue(bridge)!).Remove(s.Parent.BindingId);
        Assert.False(s.Kernel.BindPlatformDomain(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.ParentBindCalls);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.Equal(0, s.Provider.SecureRevokeCalls);
        Assert.Equal(0, s.Provider.ParentRevokeCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ConcurrentCanonicalRootLossDuringSecureCreateRetainsBackingAndBalancesPending(bool foreign)
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        Assert.True(s.Kernel.AllocateBuffer<byte>(s.Owner, 64).IsSuccess);
        var bridge = s.Kernel.PlatformAuthority;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var originals = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_activeSubjects", flags)!.GetValue(bridge)!;
        var gate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(bridge)!;
        var original = originals[s.Parent.Subject]!;
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        s.Provider.AfterSecureMaterialize = () =>
        {
            Assert.False(Monitor.IsEntered(gate));
            entered.Set();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(15)));
        };
        var creation = Task.Run(() => s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent,
            new([SecureProperty.PrivateMemory], 4096)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            Assert.False(s.Kernel.RevokePlatformDomain(s.Owner, s.Parent).IsSuccess);
            lock (gate)
            {
                if (foreign) originals[s.Parent.Subject] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                else originals.Remove(s.Parent.Subject);
            }
            Assert.False(s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent,
                new([SecureProperty.PrivateMemory], 4096)).IsSuccess);
        }
        finally { resume.Set(); }
        Assert.False((await creation.WaitAsync(TimeSpan.FromSeconds(15))).IsSuccess);
        Assert.Equal(0, original.GetType().GetProperty("PendingSecureCreates")!.GetValue(original));
        Assert.Single(((global::System.Collections.IEnumerable)original.GetType()
            .GetProperty("UnpublishedSecureCreates")!.GetValue(original)!).Cast<object>());
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.Equal(1, s.Provider.SecureCreateCalls);
        Assert.Equal(0, s.Provider.SecureRevokeCalls);
        Assert.Equal(0, s.Provider.ParentRevokeCalls);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(false, 1)] [InlineData(true, 1)]
    [InlineData(false, 2)] [InlineData(true, 2)]
    public void LostCanonicalRootRefusesSecureCreationAndRetainsOriginalEffect(bool foreign, int boundary)
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        Assert.True(s.Kernel.AllocateBuffer<byte>(s.Owner, 64).IsSuccess);
        var bridge = s.Kernel.PlatformAuthority;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var originals = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_activeSubjects", flags)!.GetValue(bridge)!;
        var original = originals[s.Parent.Subject]!;
        void Lose()
        {
            if (foreign) originals[s.Parent.Subject] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            else originals.Remove(s.Parent.Subject);
        }
        if (boundary == 0)
            s.Provider.ReadIncarnation = () =>
            {
                if ((int)original.GetType().GetProperty("PendingSecureCreates")!.GetValue(original)! != 0) Lose();
                return new(1);
            };
        else if (boundary == 1) s.Provider.AfterSecureMaterialize = Lose;
        else s.Kernel.BeforeSecureDomainLocalPublication = Lose;

        Assert.False(s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent,
            new([SecureProperty.PrivateMemory], 4096)).IsSuccess);
        Assert.Equal(boundary == 0 ? 0 : 1, s.Provider.SecureCreateCalls);
        Assert.Equal(0, s.Provider.SecureRevokeCalls);
        Assert.Equal(0, original.GetType().GetProperty("PendingSecureCreates")!.GetValue(original));
        if (boundary == 1)
        {
            var retained = Assert.Single(((global::System.Collections.IEnumerable)original.GetType()
                .GetProperty("UnpublishedSecureCreates")!.GetValue(original)!).Cast<object>());
            Assert.Equal(s.Provider.LastSecureCreateLease, retained.GetType().GetProperty("Receipt")!.GetValue(retained));
            var attempt = Assert.Single(((global::System.Collections.IEnumerable)original.GetType()
                .GetProperty("SecureCreateInvocations")!.GetValue(original)!).Cast<object>());
            Assert.Same(original, attempt.GetType().GetProperty("Source")!.GetValue(attempt));
        }
        if (boundary == 2)
        {
            var secureOwners = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
                .GetField("_originalSecureOwners", flags)!.GetValue(bridge)!;
            var retained = Assert.Single(secureOwners.Values.Cast<object>());
            Assert.Same(original, retained.GetType().GetProperty("CreateSource")!.GetValue(retained));
            Assert.Equal(s.Provider.LastSecureCreateLease, retained.GetType().GetProperty("ProviderLease")!.GetValue(retained));
        }
        Assert.False(s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent,
            new([SecureProperty.PrivateMemory], 4096)).IsSuccess);
        Assert.Equal(boundary == 0 ? 0 : 1, s.Provider.SecureCreateCalls);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.Equal(0, s.Provider.ParentRevokeCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LateCanonicalRootLossCannotConsumeSecureClosure(bool foreign)
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        Assert.True(s.Kernel.AllocateBuffer<byte>(s.Owner, 64).IsSuccess);
        var secure = s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent,
            new([SecureProperty.PrivateMemory], 4096)).Value!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var originals = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_activeSubjects", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
        var original = originals[s.Parent.Subject]!;
        s.Provider.BeforeSecureRevoke = () =>
        {
            if (foreign) originals[s.Parent.Subject] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            else originals.Remove(s.Parent.Subject);
        };
        Assert.False(s.Kernel.DestroySecureDomain(s.Owner, secure.Domain, secure.ConfigureCapability).IsSuccess);
        var owners = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_originalSecureOwners", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
        var retained = Assert.Single(owners.Values.Cast<object>());
        Assert.NotNull(retained.GetType().GetProperty("LastClosureReceipt")!.GetValue(retained));
        Assert.Equal(false, retained.GetType().GetProperty("ClosureConsumed")!.GetValue(retained));
        Assert.False(s.Kernel.DestroySecureDomain(s.Owner, secure.Domain, secure.ConfigureCapability).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.SecureRevokeCalls);
        Assert.Equal(0, s.Provider.ParentRevokeCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LateSecureOriginalOwnerLossRetainsReceiptAndRootPin(bool foreign)
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        Assert.True(s.Kernel.AllocateBuffer<byte>(s.Owner, 64).IsSuccess);
        var bridge = s.Kernel.PlatformAuthority;
        var secure = bridge.CreateSecureDomain(s.Parent, s.Parent.Subject,
            new([SecureProperty.PrivateMemory], 4096)).Value!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var originals = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_originalSecureOwners", flags)!.GetValue(bridge)!;
        var original = originals[secure.BindingId]!;
        s.Provider.BeforeSecureRevoke = () =>
        {
            if (foreign) originals[secure.BindingId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            else originals.Remove(secure.BindingId);
        };
        Assert.False(bridge.RevokeSecureDomain(secure).IsSuccess);
        Assert.NotNull(original.GetType().GetProperty("LastClosureReceipt")!.GetValue(original));
        Assert.False(bridge.RevokeSecureDomain(secure).IsSuccess);
        Assert.False(bridge.RevokeDomain(s.Parent, s.Parent.Subject).IsSuccess);
        Assert.Equal(1, s.Provider.SecureRevokeCalls);
        Assert.Equal(0, s.Provider.ParentRevokeCalls);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LostSecureRouteCannotDischargeOriginalRootDependency(bool foreign)
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 64).Value!;
        var bridge = s.Kernel.PlatformAuthority;
        var secure = bridge.CreateSecureDomain(s.Parent, s.Parent.Subject,
            new([SecureProperty.PrivateMemory], 4096)).Value!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var routes = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_secureDomains", flags)!.GetValue(bridge)!;
        var original = routes[secure.BindingId]!;
        if (foreign)
        {
            routes[secure.BindingId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            Assert.False(bridge.RevokeSecureDomain(secure).IsSuccess);
            Assert.Equal(0, s.Provider.SecureRevokeCalls);
        }
        else routes.Remove(secure.BindingId);
        Assert.False(bridge.RevokeDomain(s.Parent, s.Parent.Subject).IsSuccess);
        Assert.Equal(0, s.Provider.ParentRevokeCalls);
        Assert.True(buffer.IsValid);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        routes[secure.BindingId] = original;
        Assert.True(bridge.RevokeSecureDomain(secure).IsSuccess);
        routes.Remove(secure.BindingId); // consumed exact closure cannot repin the parent
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.Equal(1, s.Provider.SecureRevokeCalls);
        Assert.Equal(1, s.Provider.ParentRevokeCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FalseClosedRootCannotDischargeUnknownSecureCreate(bool missingRoutes)
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        Assert.True(s.Kernel.AllocateBuffer<byte>(s.Owner, 64).IsSuccess);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        s.Provider.SecureCreateReplyStatus = PlatformAuthorityStatus.NotAccepted;
        Assert.False(s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent,
            new([SecureProperty.PrivateMemory], 4096)).IsSuccess);
        Assert.NotNull(s.Provider.LastSecureCreateLease);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
        var original = domains[s.Parent.BindingId]!;
        var state = original.GetType().GetProperty("AuthorityState")!;
        state.SetValue(original, Enum.Parse(state.PropertyType, "Closed"));
        if (missingRoutes)
        {
            domains.Remove(s.Parent.BindingId);
            ((global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_processPlatformBindings", flags)!.GetValue(s.Kernel)!).Remove(s.Owner);
        }
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.Single(((global::System.Collections.IEnumerable)original.GetType().GetProperty("SecureCreateInvocations")!.GetValue(original)!).Cast<object>());
        Assert.Equal(1, s.Provider.SecureCreateCalls);
        Assert.Equal(0, s.Provider.ParentRevokeCalls);
    }

    [Fact]
    public void PartialSecureMintIsRevokedAndProviderClosedBeforeObservationTeardown()
    {
        var clock = new TransitionTraceClock();
        var s = Create(PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true, clock: clock);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        Assert.True(s.Kernel.StartTraceSession(s.Owner, 32).IsSuccess);
        typeof(CapabilityAuthority).GetField("_nextId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(s.Kernel.CapabilityAuthority, ulong.MaxValue);
        var observed = false;
        clock.Next = () =>
        {
            Assert.Equal(KernelError.CapabilityRevoked, s.Kernel.CapabilityAuthority.Validate(
                new CapabilityId(ulong.MaxValue), new DomainId(701), s.Owner.Generation, CapabilityRights.Configure).Error);
            Assert.Equal(1, s.Provider.SecureRevokeCalls);
            var teardown = Task.Run(() => s.Kernel.TerminateProcess(s.Owner));
            Assert.True(teardown.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(teardown.Result.IsSuccess, teardown.Result.Message);
            observed = true;
        };
        var result = s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent,
            new([SecureProperty.PrivateMemory], 4096));
        Assert.Equal(KernelError.CapacityExhausted, result.Error);
        Assert.True(observed);
        Assert.Equal(1, s.Provider.SecureCreateCalls);
        Assert.Equal(1, s.Provider.SecureRevokeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SecureCreationObservationsAllowWorkerAndRetainPartialMintCleanup(bool exhaust)
    {
        var clock = new TransitionTraceClock();
        var s = Create(PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true, clock: clock);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        Assert.True(s.Kernel.StartTraceSession(s.Owner, 32).IsSuccess);
        if (exhaust) typeof(CapabilityAuthority).GetField("_nextId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(s.Kernel.CapabilityAuthority, ulong.MaxValue);
        var observed = false;
        clock.Next = () =>
        {
            var worker = Task.Run(() => s.Kernel.AllocateBuffer<byte>(s.Owner, 16));
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(worker.Result.IsSuccess);
            observed = true;
        };
        var created = s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent,
            new([SecureProperty.PrivateMemory], 4096));
        Assert.True(observed);
        Assert.Equal(1, s.Provider.SecureCreateCalls);
        Assert.Equal(!exhaust, created.IsSuccess);
        if (exhaust)
        {
            Assert.Equal(KernelError.CapacityExhausted, created.Error);
            Assert.Equal(1, s.Provider.SecureRevokeCalls);
            Assert.Equal(KernelError.CapabilityRevoked, s.Kernel.CapabilityAuthority.Validate(
                new CapabilityId(ulong.MaxValue), new DomainId(701), s.Owner.Generation, CapabilityRights.Configure).Error);
        }
        else Assert.True(s.Kernel.DestroySecureDomain(s.Owner, created.Value!.Domain,
            created.Value.ConfigureCapability).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SecureTransitionTraceAllowsWorkerAdmissionAfterStateCommit(bool clockThrows)
    {
        var clock = new TransitionTraceClock();
        var s = Create(PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true, clock: clock);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var secure = s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent,
            new([SecureProperty.PrivateMemory], 4096)).Value!;
        var trace = s.Kernel.StartTraceSession(s.Owner, 16);
        Assert.True(trace.IsSuccess, trace.Message);
        var observed = false;
        clock.Next = () =>
        {
            var worker = Task.Run(() => s.Kernel.MintCapability(new DomainId(701), s.Owner,
                ResourceKind.Compute, "compute/secure-trace", CapabilityRights.Execute));
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(worker.Result.IsSuccess);
            observed = true;
            if (clockThrows) throw new InvalidOperationException("Injected trace clock fault.");
        };
        Assert.True(s.Kernel.TransitionSecureDomain(s.Owner, secure.Domain, secure.ExecuteCapability,
            PlatformSecureDomainTransition.BeginDrain).IsSuccess);
        Assert.True(observed);
        Assert.Equal(1, s.Provider.SecureTransitionCalls);
        var records = (global::System.Collections.IDictionary)typeof(RuntimeKernel)
            .GetField("_secureDomainRecords", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        var record = records.Values.Cast<object>().Single();
        Assert.Equal(SecureDomainState.Draining, record.GetType().GetProperty("State")!.GetValue(record));
        Assert.False((bool)record.GetType().GetProperty("TransitionInFlight")!.GetValue(record)!);
    }

    private sealed class TransitionTraceClock : TimeProvider
    {
        public Action? Next { get; set; }
        public override long GetTimestamp() { var next = Next; Next = null; next?.Invoke(); return base.GetTimestamp(); }
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)] [InlineData(3, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)] [InlineData(3, true)]
    public void SecureRegionClosureSpansLocalCompletionAndTeardown(int fault, bool teardown)
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 128).Value!;
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var cap = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var mapping = s.Kernel.MapPlatformOwnedRegion(s.Owner, s.Parent, cap, buffer.Handle,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var secure = s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent, new([SecureProperty.PrivateMemory], 4096)).Value!;
        Assert.True(s.Kernel.BindSecureRegion(s.Owner, secure.Domain, secure.MemoryCapability, mapping, PlatformSecureRegionClass.Private).IsSuccess);
        var bridge = s.Kernel.PlatformAuthority;
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_secureDomains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
        var record = records.Values.Cast<object>().Single();
        var binding = (PlatformAuthorityBridge.SecureDomainBinding)record.GetType().GetProperty("Binding")!.GetValue(record)!;
        var locals = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureDomainRecords", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        var local = locals.Values.Cast<object>().Single();
        var generation = local.GetType().GetProperty("ProtectionGeneration")!;
        if (fault == 3) generation.SetValue(local, ulong.MaxValue);
        var before = generation.GetValue(local);
        var epoch = bridge.BackendEpoch;
        var bridgeGate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
        var kernelGate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        var calls = 0;
        s.Kernel.BeforeSecureRegionLocalCommit = () =>
        {
            calls++;
            Assert.False(Monitor.IsEntered(bridgeGate));
            Assert.False(Monitor.IsEntered(kernelGate));
            Assert.Single(((global::System.Collections.IEnumerable)record.GetType().GetProperty("RegionMutationsInFlight")!.GetValue(record)!).Cast<object>());
            Assert.Equal(KernelError.PlatformBindingActive, bridge.UnbindSecureRegion(binding, mapping).Error);
            if (fault == 1) Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault == 2) throw new IOException("local closure lost");
        };
        var result = teardown ? s.Kernel.TerminateProcess(s.Owner) : s.Kernel.CloseSecureRegion(s.Owner, secure.Domain, secure.MemoryCapability, mapping);
        Assert.Equal(fault is 0 or 3, result.IsSuccess);
        Assert.Equal(1, calls);
        Assert.Equal(1, s.Provider.SecureUnbindCalls);
        Assert.Empty(((global::System.Collections.IEnumerable)record.GetType().GetProperty("RegionMutationsInFlight")!.GetValue(record)!).Cast<object>());
        var regions = (global::System.Collections.IDictionary)record.GetType().GetProperty("Regions")!.GetValue(record)!;
        var localRegions = (global::System.Collections.IDictionary)local.GetType().GetProperty("Regions")!.GetValue(local)!;
        Assert.Equal(fault is 0 or 3 ? 0 : 1, regions.Count);
        Assert.Equal(fault is 0 or 3 ? 0 : 1, localRegions.Count);
        if (fault == 3) Assert.Equal(true, local.GetType().GetProperty("SourceRevisionExhausted")!.GetValue(local));
        if (fault is 1 or 2)
        {
            Assert.Equal(before, generation.GetValue(local));
            var facts = (global::System.Collections.IDictionary)record.GetType().GetProperty("RegionClosureReceipts")!.GetValue(record)!;
            var fact = facts.Values.Cast<object>().Single();
            Assert.Equal(epoch, fact.GetType().GetProperty("AdmissionEpoch")!.GetValue(fact));
            Assert.Equal(s.Provider.LastSecureRegionBinding, fact.GetType().GetProperty("AdmittedBinding")!.GetValue(fact));
            Assert.True(((PlatformSecureRegionClosureReceipt)fact.GetType().GetProperty("Receipt")!.GetValue(fact)!).Closed);
            Assert.False(s.Kernel.RevokePlatformRegionMapping(s.Owner, mapping).IsSuccess);
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(1, s.Provider.SecureUnbindCalls);
        }
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void SecureClosureConsumerCannotSubstituteTerminalResponseForCommit(bool refusal, bool region)
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var secure = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability,
            scenario.Parent, new([SecureProperty.PrivateMemory], 4096)).Value!;
        PlatformRegionMapping mapping = default;
        if (region)
        {
            var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Owner, 128).Value!;
            var process = scenario.Kernel.Processes.Resolve(scenario.Owner).Value!;
            var cap = scenario.Kernel.MintCapability(process.DomainId, scenario.Owner, ResourceKind.MemoryRegion,
                CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
            mapping = scenario.Kernel.MapPlatformOwnedRegion(scenario.Owner, scenario.Parent, cap, buffer.Handle,
                PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
            Assert.True(scenario.Kernel.BindSecureRegion(scenario.Owner, secure.Domain, secure.MemoryCapability, mapping, PlatformSecureRegionClass.Private).IsSuccess);
        }
        var bridge = scenario.Kernel.PlatformAuthority;
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_secureDomains",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
        var record = records.Values.Cast<object>().Single();
        var binding = (PlatformAuthorityBridge.SecureDomainBinding)record.GetType().GetProperty("Binding")!.GetValue(record)!;
        if (!region) Assert.True(bridge.TransitionSecureDomain(binding, PlatformSecureDomainTransition.BeginDrain).IsSuccess);
        KernelResult Refuse(Func<Func<KernelResult>, KernelResult> _) => refusal
            ? KernelResult.Fail(KernelError.PlatformBindingActive, "local completion refused") : KernelResult.Ok();
        var result = region ? bridge.UnbindSecureRegion(binding, mapping, Refuse) : bridge.RevokeSecureDomain(binding, Refuse);
        Assert.False(result.IsSuccess);
        Assert.Same(record, records.Values.Cast<object>().Single());
        if (region)
        {
            var facts = (global::System.Collections.IDictionary)record.GetType().GetProperty("RegionClosureReceipts")!.GetValue(record)!;
            Assert.Single(facts.Values.Cast<object>());
            Assert.Empty(((global::System.Collections.IEnumerable)record.GetType().GetProperty("RegionMutationsInFlight")!.GetValue(record)!).Cast<object>());
        }
        else Assert.NotNull(record.GetType().GetProperty("LastClosureReceipt")!.GetValue(record));
        Assert.Equal(false, record.GetType().GetProperty("RevokeInFlight")!.GetValue(record));
        Assert.True((region ? bridge.UnbindSecureRegion(binding, mapping) : bridge.RevokeSecureDomain(binding)).IsSuccess);
        Assert.Equal(region ? 1 : 0, scenario.Provider.SecureUnbindCalls);
        Assert.Equal(region ? 0 : 1, scenario.Provider.SecureRevokeCalls);
        Assert.True(scenario.Kernel.TerminateProcess(scenario.Owner).IsSuccess);
        Assert.Equal(region ? 1 : 0, scenario.Provider.SecureUnbindCalls);
        Assert.Equal(1, scenario.Provider.SecureRevokeCalls);
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)]
    public void SecureDomainExactClosureSpansLocalCompletionAndTeardown(int fault, bool teardown)
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var secure = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability,
            scenario.Parent, new([SecureProperty.PrivateMemory], 4096)).Value!;
        var bridge = scenario.Kernel.PlatformAuthority;
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_secureDomains",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
        var record = records.Values.Cast<object>().Single();
        var epoch = bridge.BackendEpoch;
        var bridgeGate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
        var kernelGate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario.Kernel)!;
        var calls = 0;
        scenario.Kernel.BeforeSecureDomainClosureLocalCommit = () =>
        {
            calls++;
            Assert.False(Monitor.IsEntered(bridgeGate));
            Assert.False(Monitor.IsEntered(kernelGate));
            Assert.Equal(true, record.GetType().GetProperty("RevokeInFlight")!.GetValue(record));
            var binding = (PlatformAuthorityBridge.SecureDomainBinding)record.GetType().GetProperty("Binding")!.GetValue(record)!;
            Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeSecureDomain(binding).Error);
            if (fault == 1) Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault == 2) throw new IOException("local completion lost after known closure");
        };
        var result = teardown ? scenario.Kernel.TerminateProcess(scenario.Owner)
            : scenario.Kernel.DestroySecureDomain(scenario.Owner, secure.Domain, secure.ConfigureCapability);
        Assert.Equal(fault == 0, result.IsSuccess);
        Assert.Equal(1, calls);
        Assert.Equal(1, scenario.Provider.SecureRevokeCalls);
        Assert.Equal(false, record.GetType().GetProperty("RevokeInFlight")!.GetValue(record));
        if (fault == 0) Assert.Empty(records.Values.Cast<object>());
        else
        {
            Assert.Same(record, records.Values.Cast<object>().Single());
            var fact = record.GetType().GetProperty("LastClosureReceipt")!.GetValue(record)!;
            Assert.Equal(epoch, fact.GetType().GetProperty("AdmissionEpoch")!.GetValue(fact));
            Assert.Equal(record.GetType().GetProperty("ProviderLease")!.GetValue(record),
                fact.GetType().GetProperty("AdmittedLease")!.GetValue(fact));
            var receipt = (PlatformSecureDomainClosureReceipt)fact.GetType().GetProperty("Receipt")!.GetValue(fact)!;
            Assert.True(receipt.Closed);
            Assert.False(scenario.Kernel.DestroySecureDomain(scenario.Owner, secure.Domain, secure.ConfigureCapability).IsSuccess);
            Assert.False(scenario.Kernel.TerminateProcess(scenario.Owner).IsSuccess);
            Assert.Equal(1, scenario.Provider.SecureRevokeCalls);
        }
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)]
    public void SecureCreateValidationLossRetainsKnownLeaseWithoutInventingClosure(int fault, bool reset)
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        var epoch = scenario.Kernel.PlatformAuthority.BackendEpoch;
        scenario.Provider.ProvenProperties = new PropertyEnumerationHook(() =>
        {
            var gate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario.Kernel.PlatformAuthority)!;
            Assert.False(Monitor.IsEntered(gate));
            if (reset) Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);
            throw fault switch
            {
                0 => new TimeoutException("returned lease validation timed out"),
                1 => new OperationCanceledException("returned lease validation cancelled"),
                _ => new IOException("returned lease validation transport loss")
            };
        });
        var result = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability, scenario.Parent,
            new([SecureProperty.PrivateMemory], 4096));
        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        var domains = typeof(PlatformAuthorityBridge).GetField("_domains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario.Kernel.PlatformAuthority)!;
        var records = ((global::System.Collections.IEnumerable)domains.GetType().GetProperty("Values")!.GetValue(domains)!).Cast<object>();
        var facts = records.SelectMany(record =>
        {
            Assert.Equal(0, record.GetType().GetProperty("PendingSecureCreates")!.GetValue(record));
            return ((global::System.Collections.IEnumerable)record.GetType().GetProperty("UnpublishedSecureCreates")!.GetValue(record)!).Cast<object>();
        }).ToArray();
        var retained = Assert.Single(facts);
        Assert.Equal(scenario.Provider.LastSecureCreateLease, retained.GetType().GetProperty("Receipt")!.GetValue(retained));
        Assert.Equal(epoch, retained.GetType().GetProperty("AdmissionEpoch")!.GetValue(retained));
        Assert.Equal(scenario.Parent, retained.GetType().GetProperty("Parent")!.GetValue(retained));
        Assert.False(scenario.Kernel.RevokePlatformDomain(scenario.Owner, scenario.Parent).IsSuccess);
        Assert.False(scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability, scenario.Parent,
            new([SecureProperty.PrivateMemory], 4096)).IsSuccess);
        Assert.Equal(1, scenario.Provider.SecureCreateCalls);
        Assert.Equal(0, scenario.Provider.SecureRevokeCalls);
        Assert.Equal(0, scenario.Provider.ParentRevokeCalls);
    }
    private sealed class PropertyEnumerationHook(Action beforeEnumeration) : IReadOnlyList<SecureProperty>
    {
        public int Count => 1;
        public SecureProperty this[int index] => index == 0 ? SecureProperty.PrivateMemory : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<SecureProperty> GetEnumerator() { beforeEnumeration(); return ((IEnumerable<SecureProperty>)new[] { SecureProperty.PrivateMemory }).GetEnumerator(); }
        global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void SecureCreateValidationDriftRetainsReturnedLeaseBeforePublication()
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        var changed = false;
        scenario.Provider.ProvenProperties = new PropertyEnumerationHook(() =>
        {
            var gate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario.Kernel.PlatformAuthority)!;
            Assert.False(Monitor.IsEntered(gate));
            if (changed) return;
            changed = true;
            Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);
        });
        Assert.False(scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability, scenario.Parent,
            new([SecureProperty.PrivateMemory], 4096)).IsSuccess);
        Assert.True(changed);
        var domains = typeof(PlatformAuthorityBridge).GetField("_domains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario.Kernel.PlatformAuthority)!;
        var records = ((global::System.Collections.IEnumerable)domains.GetType().GetProperty("Values")!.GetValue(domains)!).Cast<object>();
        var facts = records.SelectMany(record =>
        {
            Assert.Equal(0, record.GetType().GetProperty("PendingSecureCreates")!.GetValue(record));
            return ((global::System.Collections.IEnumerable)record.GetType().GetProperty("UnpublishedSecureCreates")!.GetValue(record)!).Cast<object>();
        }).ToArray();
        var retained = Assert.Single(facts);
        Assert.Equal(scenario.Provider.LastSecureCreateLease, retained.GetType().GetProperty("Receipt")!.GetValue(retained));
        Assert.False(scenario.Kernel.RevokePlatformDomain(scenario.Owner, scenario.Parent).IsSuccess);
        Assert.Equal(0, scenario.Provider.ParentRevokeCalls);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void SecureCreateResetRetainsOnlyKnownLeaseAcrossRepeatedEpochChange(int fault)
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var epoch = scenario.Kernel.PlatformAuthority.BackendEpoch;
        scenario.Provider.FailSecureCreate = fault == 1;
        scenario.Provider.BeforeSecureCreate = () =>
        {
            Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault == 2) throw new IOException("secure create response lost after reset");
        };
        Assert.False(scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability, scenario.Parent,
            new([SecureProperty.PrivateMemory], 4096)).IsSuccess);
        object[] Facts()
        {
            var domains = typeof(PlatformAuthorityBridge).GetField("_domains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario.Kernel.PlatformAuthority)!;
            return ((global::System.Collections.IEnumerable)domains.GetType().GetProperty("Values")!.GetValue(domains)!).Cast<object>().SelectMany(record =>
            {
                Assert.Equal(0, record.GetType().GetProperty("PendingSecureCreates")!.GetValue(record));
                return ((global::System.Collections.IEnumerable)record.GetType().GetProperty("UnpublishedSecureCreates")!.GetValue(record)!).Cast<object>();
            }).ToArray();
        }
        var facts = Facts();
        if (fault == 0)
        {
            var receipt = Assert.Single(facts);
            Assert.Equal(scenario.Provider.LastSecureCreateLease, receipt.GetType().GetProperty("Receipt")!.GetValue(receipt));
            Assert.Equal(scenario.Parent, receipt.GetType().GetProperty("Parent")!.GetValue(receipt));
            Assert.Equal(epoch, receipt.GetType().GetProperty("AdmissionEpoch")!.GetValue(receipt));
        }
        else Assert.Empty(facts);
        Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);
        Assert.Equal(facts, Facts()); // Existing parent owner replacement preserves the exact cleanup objects.
        Assert.False(scenario.Kernel.RevokePlatformDomain(scenario.Owner, scenario.Parent).IsSuccess);
        Assert.False(scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability, scenario.Parent,
            new([SecureProperty.PrivateMemory], 4096)).IsSuccess);
        Assert.Equal(1, scenario.Provider.SecureCreateCalls);
        Assert.Equal(0, scenario.Provider.ParentRevokeCalls);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SecureRegionPublicationTransfersReceiptButMalformedCompensationRetainsOriginalPin(bool malformed)
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Owner, 128).Value!;
        var process = scenario.Kernel.Processes.Resolve(scenario.Owner).Value!;
        var capability = scenario.Kernel.MintCapability(process.DomainId, scenario.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var mapping = scenario.Kernel.MapPlatformOwnedRegion(scenario.Owner, scenario.Parent, capability, buffer.Handle,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var secure = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability, scenario.Parent,
            new([SecureProperty.PrivateMemory], 4096)).Value!;
        scenario.Provider.MalformedSecureBind = malformed;
        var result = scenario.Kernel.BindSecureRegion(scenario.Owner, secure.Domain, secure.MemoryCapability,
            mapping, PlatformSecureRegionClass.Private);
        Assert.Equal(!malformed, result.IsSuccess);
        if (malformed) Assert.Single(UnpublishedSecureReceipts(scenario.Kernel));
        else Assert.Empty(UnpublishedSecureReceipts(scenario.Kernel));
        Assert.Equal(malformed ? 1 : 0, scenario.Provider.SecureUnbindCalls);
        if (!malformed)
            Assert.True(scenario.Kernel.CloseSecureRegion(scenario.Owner, secure.Domain, secure.MemoryCapability, mapping).IsSuccess);
        Assert.Equal(!malformed, scenario.Kernel.RevokePlatformRegionMapping(scenario.Owner, mapping).IsSuccess);
        if (malformed)
        {
            Assert.False(scenario.Kernel.TerminateProcess(scenario.Owner).IsSuccess);
            Assert.Equal(RegionState.Owned, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalSecureRegionBindRetainsRawAndBlocksActualReclaim(bool lostReply)
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 64).Value!;
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var cap = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var mapping = s.Kernel.MapPlatformOwnedRegion(s.Owner, s.Parent, cap, buffer.Handle, PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var secure = s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent, new([SecureProperty.PrivateMemory], 4096)).Value!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_secureDomains", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
        var record = Assert.Single(records.Values.Cast<object>());
        object Fact() => Assert.Single(((global::System.Collections.IDictionary)record.GetType().GetProperty("RegionBindInvocations")!.GetValue(record)!).Values.Cast<object>());
        s.Provider.SecureBindReplyStatus = PlatformAuthorityStatus.NotAccepted;
        s.Provider.AfterSecureRegionMaterialize = () =>
        {
            Assert.Same(record, Fact().GetType().GetProperty("Source")!.GetValue(Fact()));
            Assert.Null(Fact().GetType().GetProperty("Observation")!.GetValue(Fact()));
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(s.Kernel.PlatformAuthority)!));
            if (lostReply) throw new IOException("Original bind reply lost.");
        };
        Assert.False(s.Kernel.BindSecureRegion(s.Owner, secure.Domain, secure.MemoryCapability, mapping, PlatformSecureRegionClass.Private).IsSuccess);
        var raw = Fact().GetType().GetProperty("Observation")!.GetValue(Fact());
        if (lostReply) Assert.Null(raw);
        else Assert.Equal(PlatformAuthorityStatus.NotAccepted, Assert.IsType<PlatformAuthorityResult<PlatformProviderSecureRegionBinding>>(raw).Status);
        s.Provider.AfterSecureRegionMaterialize = null;
        s.Provider.SecureBindReplyStatus = null;
        Assert.False(s.Kernel.BindSecureRegion(s.Owner, secure.Domain, secure.MemoryCapability, mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.False(s.Kernel.RevokePlatformRegionMapping(s.Owner, mapping).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.Equal(1, s.Provider.SecureBindCalls);
        Assert.Equal(0, s.Provider.SecureUnbindCalls);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    public void SecureRegionSourceLossOrRevocationRefusesBeforeEffectOrPinsKnownReceipt(bool afterEffect, int fault)
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 64).Value!;
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var cap = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var mapping = s.Kernel.MapPlatformOwnedRegion(s.Owner, s.Parent, cap, buffer.Handle, PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var secure = s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent, new([SecureProperty.PrivateMemory], 4096)).Value!;
        var mappingFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        var mappings = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_mappings", mappingFlags)!.GetValue(s.Kernel.PlatformAuthority)!;
        var originalMapping = mappings[mapping.MappingId]!;
        var replaced = false;
        void LoseSource() => s.Provider.ReadIncarnation = () =>
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(s.Kernel.PlatformAuthority)!));
            Assert.False(Monitor.IsEntered(typeof(RuntimeKernel).GetField("_platformMemoryUseGate", flags)!.GetValue(s.Kernel)!));
            if (fault == 2)
            {
                _ = s.Kernel.CapabilityAuthority.Revoke(secure.MemoryCapability);
                return new(1);
            }
            if (fault == 3)
            {
                if (!replaced)
                {
                    mappings[mapping.MappingId] = typeof(object).GetMethod("MemberwiseClone", mappingFlags)!.Invoke(originalMapping, null)!;
                    replaced = true;
                }
                return new(1);
            }
            return fault == 1 ? throw new IOException("Secure bind source lost.") : new(2);
        };
        if (afterEffect) s.Provider.AfterSecureRegionMaterialize = LoseSource;
        else LoseSource();
        Assert.False(s.Kernel.BindSecureRegion(s.Owner, secure.Domain, secure.MemoryCapability, mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(afterEffect ? 1 : 0, s.Provider.SecureBindCalls);
        s.Provider.ReadIncarnation = null;
        s.Provider.AfterSecureRegionMaterialize = null;
        // Test arrangement only; reinstalling an index does not prove durable routing.
        if (fault == 3) { Assert.True(replaced); mappings[mapping.MappingId] = originalMapping; }
        if (afterEffect)
        {
            Assert.Single(UnpublishedSecureReceipts(s.Kernel));
            Assert.False(s.Kernel.RevokePlatformRegionMapping(s.Owner, mapping).IsSuccess);
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        }
        else
        {
            Assert.Empty(UnpublishedSecureReceipts(s.Kernel));
            Assert.True(s.Kernel.RevokePlatformRegionMapping(s.Owner, mapping).IsSuccess);
            Assert.True(s.Kernel.DestroySecureDomain(s.Owner, secure.Domain, secure.ConfigureCapability).IsSuccess);
        }
        Assert.Equal(0, s.Provider.SecureUnbindCalls);
    }

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(false, 1, false)]
    [InlineData(false, 2, false)]
    [InlineData(true, 0, false)]
    [InlineData(true, 1, false)]
    [InlineData(true, 2, false)]
    [InlineData(true, 0, true)] [InlineData(true, 1, true)] [InlineData(true, 2, true)]
    public void SecureRegionClosureSourceLossRefusesCallbackOrRetainsKnownClosure(bool afterEffect, int fault, bool afterLocalCallback)
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 64).Value!;
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var cap = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var mapping = s.Kernel.MapPlatformOwnedRegion(s.Owner, s.Parent, cap, buffer.Handle, PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var secure = s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent, new([SecureProperty.PrivateMemory], 4096)).Value!;
        Assert.True(s.Kernel.BindSecureRegion(s.Owner, secure.Domain, secure.MemoryCapability, mapping, PlatformSecureRegionClass.Private).IsSuccess);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var mappings = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_mappings", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
        var original = mappings[mapping.MappingId]!;
        var replaced = false;
        void LoseSource() => s.Provider.ReadIncarnation = () =>
        {
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(s.Kernel.PlatformAuthority)!));
            Assert.False(Monitor.IsEntered(typeof(RuntimeKernel).GetField("_platformMemoryUseGate", flags)!.GetValue(s.Kernel)!));
            if (fault == 2)
            {
                if (!replaced) { mappings[mapping.MappingId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!; replaced = true; }
                return new(1);
            }
            return fault == 1 ? throw new IOException("Closure source lost.") : new(2);
        };
        if (afterLocalCallback) s.Kernel.BeforeSecureRegionLocalCommit = LoseSource;
        else if (afterEffect) s.Provider.BeforeSecureUnbind = LoseSource;
        else LoseSource();
        Assert.False(s.Kernel.CloseSecureRegion(s.Owner, secure.Domain, secure.MemoryCapability, mapping).IsSuccess);
        Assert.Equal(afterEffect ? 1 : 0, s.Provider.SecureUnbindCalls);
        s.Provider.ReadIncarnation = null;
        s.Provider.BeforeSecureUnbind = null;
        s.Kernel.BeforeSecureRegionLocalCommit = null;
        if (fault == 2) { Assert.True(replaced); mappings[mapping.MappingId] = original; } // Test arrangement, not durable recovery.
        if (afterEffect)
        {
            var domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_secureDomains", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
            var record = Assert.Single(domains.Values.Cast<object>());
            Assert.Single(((global::System.Collections.IDictionary)record.GetType().GetProperty("RegionClosureReceipts")!.GetValue(record)!).Values.Cast<object>());
            Assert.Equal(afterLocalCallback, s.Kernel.CloseSecureRegion(s.Owner, secure.Domain, secure.MemoryCapability, mapping).IsSuccess);
            Assert.Equal(afterLocalCallback, s.Kernel.RevokePlatformRegionMapping(s.Owner, mapping).IsSuccess);
            Assert.Equal(afterLocalCallback, s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(afterLocalCallback ? RegionState.Released : RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        }
        else Assert.True(s.Kernel.CloseSecureRegion(s.Owner, secure.Domain, secure.MemoryCapability, mapping).IsSuccess);
        Assert.Equal(1, s.Provider.SecureUnbindCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void SecureRegionFinalPublicationRefusesLateSourceOrForeignMapping(int fault)
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 64).Value!;
        var process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        var cap = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var mapping = s.Kernel.MapPlatformOwnedRegion(s.Owner, s.Parent, cap, buffer.Handle, PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var secure = s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent, new([SecureProperty.PrivateMemory], 4096)).Value!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var mappings = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_mappings", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
        var original = mappings[mapping.MappingId]!;
        s.Kernel.BeforeSecureRegionLocalCommit = () =>
        {
            if (fault == 2) mappings[mapping.MappingId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            else s.Provider.ReadIncarnation = () =>
            {
                Assert.False(Monitor.IsEntered(typeof(RuntimeKernel).GetField("_platformMemoryUseGate", flags)!.GetValue(s.Kernel)!));
                Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(s.Kernel.PlatformAuthority)!));
                return fault == 1 ? throw new IOException("Late publication source lost.") : new(2);
            };
        };
        Assert.False(s.Kernel.BindSecureRegion(s.Owner, secure.Domain, secure.MemoryCapability, mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(1, s.Provider.SecureBindCalls);
        Assert.Equal(0, s.Provider.SecureUnbindCalls);
        s.Provider.ReadIncarnation = null;
        if (fault == 2) mappings[mapping.MappingId] = original; // Test arrangement, not durable routing.
        Assert.False(s.Kernel.RevokePlatformRegionMapping(s.Owner, mapping).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)]
    [InlineData(true, 3)] [InlineData(true, 4)]
    public void SecureDomainRevokeSourceLossRefusesCallbackOrRetainsKnownClosure(bool afterEffect, int fault)
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        Assert.True(s.Kernel.AllocateBuffer<byte>(s.Owner, 64).IsSuccess);
        var secure = s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent, new([SecureProperty.PrivateMemory], 4096)).Value!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var parents = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
        var original = parents[s.Parent.BindingId]!;
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_secureDomains", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
        var record = Assert.Single(records.Values.Cast<object>());
        var replaced = false;
        void LoseSource() => s.Provider.ReadIncarnation = () =>
        {
            Assert.False(Monitor.IsEntered(typeof(RuntimeKernel).GetField("_platformMemoryUseGate", flags)!.GetValue(s.Kernel)!));
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(s.Kernel.PlatformAuthority)!));
            // Keep the preceding drain valid; this test targets the revoke boundary.
            if ((bool)record.GetType().GetProperty("TransitionInFlight")!.GetValue(record)!) return new(1);
            Assert.Equal(true, record.GetType().GetProperty("RevokeInFlight")!.GetValue(record));
            if (fault == 2)
            {
                if (!replaced) { parents[s.Parent.BindingId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!; replaced = true; }
                return new(1);
            }
            return fault is 1 or 4 ? throw new IOException("Revoke source lost.") : new(2);
        };
        if (fault >= 3) s.Kernel.BeforeSecureDomainClosureLocalCommit = LoseSource;
        else if (afterEffect) s.Provider.BeforeSecureRevoke = LoseSource;
        else LoseSource();
        Assert.False(s.Kernel.DestroySecureDomain(s.Owner, secure.Domain, secure.ConfigureCapability).IsSuccess);
        Assert.Equal(afterEffect ? 1 : 0, s.Provider.SecureRevokeCalls);
        Assert.Equal(false, record.GetType().GetProperty("RevokeInFlight")!.GetValue(record));
        s.Provider.ReadIncarnation = null;
        s.Provider.BeforeSecureRevoke = null;
        if (fault == 2) { Assert.True(replaced); parents[s.Parent.BindingId] = original; } // Test arrangement, not durable recovery.
        if (afterEffect)
        {
            Assert.NotNull(record.GetType().GetProperty("LastClosureReceipt")!.GetValue(record));
            Assert.False(s.Kernel.DestroySecureDomain(s.Owner, secure.Domain, secure.ConfigureCapability).IsSuccess);
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        }
        else
        {
            Assert.Null(record.GetType().GetProperty("LastClosureReceipt")!.GetValue(record));
            Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
            Assert.False(s.Kernel.DestroySecureDomain(s.Owner, secure.Domain, secure.ConfigureCapability).IsSuccess);
            // Fresh process cleanup closes the known lease after a refusal before revoke effect.
            Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.NotNull(record.GetType().GetProperty("LastClosureReceipt")!.GetValue(record));
            Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        }
        Assert.Equal(1, s.Provider.SecureRevokeCalls);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 2)] [InlineData(0, 3)]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)] [InlineData(1, 3)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)] [InlineData(2, 3)]
    public void SecureTransitionRefusesStaleSourceBeforeEffectOrPublication(int stage, int fault)
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        Assert.True(s.Kernel.AllocateBuffer<byte>(s.Owner, 64).IsSuccess);
        var secure = s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent, new([SecureProperty.PrivateMemory], 4096)).Value!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var parents = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
        var original = parents[s.Parent.BindingId]!;
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_secureDomains", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
        var record = Assert.Single(records.Values.Cast<object>());
        var kernelRecords = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureDomainRecords", flags)!.GetValue(s.Kernel)!;
        var kernelRecord = kernelRecords[secure.Domain.DomainId]!;
        var changed = false;
        void LoseSource() => s.Provider.ReadIncarnation = () =>
        {
            Assert.False(Monitor.IsEntered(typeof(RuntimeKernel).GetField("_platformMemoryUseGate", flags)!.GetValue(s.Kernel)!));
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(s.Kernel.PlatformAuthority)!));
            Assert.Equal(true, kernelRecord.GetType().GetProperty("TransitionInFlight")!.GetValue(kernelRecord));
            if (!changed && fault is 2 or 3)
            {
                if (fault == 2) parents[s.Parent.BindingId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                else Assert.True(s.Kernel.RevokeCapability(secure.ExecuteCapability).IsSuccess);
                changed = true;
            }
            return fault == 1 ? throw new IOException("Transition source lost.") : new(fault == 0 ? 2UL : 1UL);
        };
        if (stage == 0) LoseSource();
        else if (stage == 1) s.Provider.BeforeSecureTransition = LoseSource;
        else s.Kernel.BeforeSecureDomainTransitionLocalCommit = LoseSource;
        Assert.False(s.Kernel.TransitionSecureDomain(s.Owner, secure.Domain, secure.ExecuteCapability,
            PlatformSecureDomainTransition.BeginDrain).IsSuccess);
        Assert.Equal(stage == 0 ? 0 : 1, s.Provider.SecureTransitionCalls);
        Assert.Equal(false, record.GetType().GetProperty("TransitionInFlight")!.GetValue(record));
        Assert.Equal(false, kernelRecord.GetType().GetProperty("TransitionInFlight")!.GetValue(kernelRecord));
        Assert.Equal(SecureDomainState.Quarantined, kernelRecord.GetType().GetProperty("State")!.GetValue(kernelRecord));
        var fact = record.GetType().GetProperty("LastTransitionInvocation")!.GetValue(record);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        s.Provider.ReadIncarnation = null;
        s.Provider.BeforeSecureTransition = null;
        s.Kernel.BeforeSecureDomainTransitionLocalCommit = null;
        if (fault == 2) { Assert.True(changed); parents[s.Parent.BindingId] = original; } // Test arrangement, not durable routing.
        if (stage == 0)
        {
            Assert.Null(fact);
            Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        }
        else
        {
            Assert.NotNull(fact);
            var observation = (PlatformAuthorityResult<PlatformSecureDomainTransitionReceipt>)fact.GetType().GetProperty("Observation")!.GetValue(fact)!;
            Assert.True(observation.IsSuccess);
            Assert.Equal(PlatformSecureDomainTransition.BeginDrain, observation.Value.Transition);
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(1, s.Provider.SecureTransitionCalls);
            Assert.Equal(0, s.Provider.SecureRevokeCalls);
            Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void SecureTransitionOriginalRawOrLostOutcomePinsActualTeardown(int fault)
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        Assert.True(s.Kernel.AllocateBuffer<byte>(s.Owner, 64).IsSuccess);
        var secure = s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent, new([SecureProperty.PrivateMemory], 4096)).Value!;
        s.Provider.SecureTransitionReplyFault = fault;
        if (fault == 4) s.Provider.BeforeSecureTransition = () => throw new IOException("Lost original transition reply.");
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_secureDomains", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
        var record = Assert.Single(records.Values.Cast<object>());
        Assert.False(s.Kernel.TransitionSecureDomain(s.Owner, secure.Domain, secure.ExecuteCapability,
            PlatformSecureDomainTransition.BeginDrain).IsSuccess);
        var fact = record.GetType().GetProperty("LastTransitionInvocation")!.GetValue(record)!;
        Assert.Same(record, fact.GetType().GetProperty("Source")!.GetValue(fact));
        Assert.Equal(record.GetType().GetProperty("ProviderLease")!.GetValue(record), fact.GetType().GetProperty("AdmittedLease")!.GetValue(fact));
        Assert.Equal(PlatformSecureDomainTransition.BeginDrain, fact.GetType().GetProperty("Transition")!.GetValue(fact));
        var raw = fact.GetType().GetProperty("Observation")!.GetValue(fact);
        if (fault == 4) Assert.Null(raw);
        else
        {
            var observation = (PlatformAuthorityResult<PlatformSecureDomainTransitionReceipt>)raw!;
            Assert.Equal(fault switch { 0 => PlatformAuthorityStatus.Faulted, 1 => PlatformAuthorityStatus.NotAccepted,
                2 => (PlatformAuthorityStatus)999, _ => PlatformAuthorityStatus.Success }, observation.Status);
            Assert.True(observation.Value.Accepted); // Even a failed status retains the original returned object.
        }
        Assert.False(s.Kernel.TransitionSecureDomain(s.Owner, secure.Domain, secure.ExecuteCapability, PlatformSecureDomainTransition.BeginDrain).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.SecureTransitionCalls);
        Assert.Equal(0, s.Provider.SecureRevokeCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
    }

    private static object[] UnpublishedSecureReceipts(RuntimeKernel kernel)
    {
        var domains = typeof(PlatformAuthorityBridge).GetField("_secureDomains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!;
        var records = ((global::System.Collections.IEnumerable)domains.GetType().GetProperty("Values")!.GetValue(domains)!).Cast<object>();
        return records.SelectMany(record =>
        {
            var pending = record.GetType().GetProperty("RegionMutationsInFlight")!.GetValue(record)!;
            Assert.Equal(0, (int)pending.GetType().GetProperty("Count")!.GetValue(pending)!);
            var receipts = record.GetType().GetProperty("UnpublishedRegionReceipts")!.GetValue(record)!;
            return ((global::System.Collections.IEnumerable)receipts.GetType().GetProperty("Values")!.GetValue(receipts)!).Cast<object>();
        }).ToArray();
    }
    [Fact]
    public void MissingEvidenceCapabilityMakesZeroProviderEvidenceCalls()
    {
        var scenario = Create();
        var result = scenario.Kernel.ReadPlatformEvidence(scenario.Owner, new CapabilityId(999), scenario.Parent,
            new("runtime.health", 1), EvidenceVisibilityClass.PublicDiagnostic, new(7, 1));
        Assert.False(result.IsSuccess);
        Assert.Equal(0, scenario.Provider.ReadCalls);
        Assert.Equal(0, scenario.Provider.CatalogCalls);
    }

    [Fact]
    public void ForgedGenerationOrRevokedEvidenceAuthorityMakesZeroProviderEvidenceCalls()
    {
        var scenario = Create(withEvidenceCapability: true);
        var forgedParent = scenario.Parent with
        {
            Generation = new PlatformDomainBindingGeneration(scenario.Parent.Generation.Value + 1)
        };

        var forged = scenario.Kernel.ReadPlatformEvidence(scenario.Owner, scenario.EvidenceCapability, forgedParent,
            new("runtime.health", 1), EvidenceVisibilityClass.PublicDiagnostic, new(7, 1));
        Assert.False(forged.IsSuccess);
        Assert.Equal(0, scenario.Provider.ReadCalls);

        Assert.True(scenario.Kernel.RevokeCapability(scenario.EvidenceCapability).IsSuccess);
        var revoked = scenario.Kernel.ReadPlatformEvidence(scenario.Owner, scenario.EvidenceCapability, scenario.Parent,
            new("runtime.health", 1), EvidenceVisibilityClass.PublicDiagnostic, new(7, 1));
        Assert.False(revoked.IsSuccess);
        Assert.Equal(0, scenario.Provider.ReadCalls);
    }

    [Fact]
    public void HostInternalIsRejectedBeforeProviderCall()
    {
        var scenario = Create(withEvidenceCapability: true);
        var result = scenario.Kernel.ReadPlatformEvidence(scenario.Owner, scenario.EvidenceCapability, scenario.Parent,
            new("runtime.health", 1), EvidenceVisibilityClass.HostInternal, new(7, 1));
        Assert.False(result.IsSuccess);
        Assert.Equal(0, scenario.Provider.ReadCalls);
    }

    [Theory]
    [InlineData(PlatformFeatureAvailability.ModelOnly)]
    [InlineData(PlatformFeatureAvailability.ProjectionOnly)]
    [InlineData(PlatformFeatureAvailability.RuntimeAdmission)]
    [InlineData(PlatformFeatureAvailability.ExecutableButNotProductionSecure)]
    [InlineData(PlatformFeatureAvailability.Executable)]
    public void WeakerReadinessCannotAdmitSecureDomain(PlatformFeatureAvailability availability)
    {
        var scenario = Create(secureAvailability: availability, withSecureCapability: true);
        var result = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability, scenario.Parent,
            new([SecureProperty.PrivateMemory], 4096));
        Assert.False(result.IsSuccess);
        Assert.Equal(0, scenario.Provider.SecureCreateCalls);
    }

    [Fact]
    public void UnsupportedSecurePropertyFailsWithoutOrdinaryFallback()
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.MeasuredLaunch];
        var result = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability, scenario.Parent,
            new([SecureProperty.PrivateMemory], 4096));
        Assert.False(result.IsSuccess);
        Assert.Equal(1, scenario.Provider.SecureCreateCalls);
        Assert.Equal(0, scenario.Provider.OrdinaryVirtualDomainCalls);
    }

    [Theory]
    [InlineData(SecureLeaseMode.ZeroIdentity)]
    [InlineData(SecureLeaseMode.WrongParent)]
    [InlineData(SecureLeaseMode.NullProperties)]
    [InlineData(SecureLeaseMode.UnknownProperty)]
    public void MalformedSecureAdmissionFailsClosedAndAttemptsExactCleanup(SecureLeaseMode mode)
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        scenario.Provider.SecureLeaseMode = mode;
        var result = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability, scenario.Parent,
            new([SecureProperty.PrivateMemory], 4096));
        Assert.False(result.IsSuccess);
        Assert.Equal(1, scenario.Provider.SecureCreateCalls);
        Assert.Equal(1, scenario.Provider.SecureRevokeCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AmbiguousSecureCreatePinsParentWithoutPublishedLease(int fault)
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure,
            withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        scenario.Provider.BeforeSecureCreate = fault switch
        {
            0 => () => throw new InvalidOperationException("after possible create effect"),
            1 => () => Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess),
            _ => null,
        };
        scenario.Provider.FailSecureCreate = fault == 2;

        var created = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability,
            scenario.Parent, new([SecureProperty.PrivateMemory], 4096));
        var revoke = scenario.Kernel.RevokePlatformDomain(scenario.Owner, scenario.Parent);

        Assert.False(created.IsSuccess);
        Assert.False(revoke.IsSuccess);
        Assert.Equal(1, scenario.Provider.SecureCreateCalls);
        Assert.Equal(0, scenario.Provider.ParentRevokeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SecureCreateFinalIdentityIsReservedBeforeReentrantCallback(bool bridgeIdentity)
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        if (bridgeIdentity) typeof(PlatformAuthorityBridge).GetField("_nextSecureDomainBindingId", flags)!.SetValue(s.Kernel.PlatformAuthority, ulong.MaxValue);
        else typeof(RuntimeKernel).GetField("_nextSecureDomainId", flags)!.SetValue(s.Kernel, ulong.MaxValue);
        var observed = false;
        s.Provider.BeforeSecureCreate = () =>
        {
            var retry = s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent, new([SecureProperty.PrivateMemory], 4096));
            Assert.Equal(KernelError.CapacityExhausted, retry.Error);
            Assert.Equal(1, s.Provider.SecureCreateCalls);
            observed = true;
        };
        var created = s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent, new([SecureProperty.PrivateMemory], 4096));
        Assert.True(created.IsSuccess, created.Message);
        Assert.True(observed);
        Assert.True(s.Kernel.DestroySecureDomain(s.Owner, created.Value!.Domain, created.Value.ConfigureCapability).IsSuccess);
        Assert.True(s.Kernel.RevokePlatformDomain(s.Owner, s.Parent).IsSuccess);
    }

    [Fact]
    public void SecureKernelIdentityExhaustionRefusesBeforeBridgeAllocation()
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(RuntimeKernel).GetField("_nextSecureDomainId", flags)!.SetValue(s.Kernel, 0UL);
        var next = typeof(PlatformAuthorityBridge).GetField("_nextSecureDomainBindingId", flags)!;
        var before = next.GetValue(s.Kernel.PlatformAuthority);
        Assert.Equal(KernelError.CapacityExhausted, s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent, new([SecureProperty.PrivateMemory], 4096)).Error);
        Assert.Equal(before, next.GetValue(s.Kernel.PlatformAuthority));
        Assert.Equal(0, s.Provider.SecureCreateCalls);
        Assert.True(s.Kernel.RevokePlatformDomain(s.Owner, s.Parent).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SecureCreateCapacityRefusesBeforeProviderOrPendingMutation(bool pendingCapacity)
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var bridge = s.Kernel.PlatformAuthority;
        var domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(bridge)!;
        var original = domains[s.Parent.BindingId]!;
        var pending = original.GetType().GetProperty("PendingSecureCreates")!;
        if (pendingCapacity) pending.SetValue(original, int.MaxValue);
        else typeof(PlatformAuthorityBridge).GetField("_nextSecureDomainBindingId", flags)!.SetValue(bridge, 0UL);
        var result = s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent, new([SecureProperty.PrivateMemory], 4096));
        Assert.Equal(KernelError.CapacityExhausted, result.Error);
        Assert.Equal(0, s.Provider.SecureCreateCalls);
        Assert.Equal(pendingCapacity ? int.MaxValue : 0, pending.GetValue(original));
        Assert.Empty(((global::System.Collections.IEnumerable)original.GetType().GetProperty("SecureCreateInvocations")!.GetValue(original)!).Cast<object>());
        Assert.Equal(false, original.GetType().GetProperty("SecureCreateMayHaveEffect")!.GetValue(original));
        if (pendingCapacity) pending.SetValue(original, 0);
        Assert.True(s.Kernel.RevokePlatformDomain(s.Owner, s.Parent).IsSuccess);
    }

    [Fact]
    public void SecureCreateSourceThrowBeforeInvocationBalancesPendingWithoutEffectPin()
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
        var original = domains[s.Parent.BindingId]!;
        var pending = original.GetType().GetProperty("PendingSecureCreates")!;
        s.Provider.ReadIncarnation = () => (int)pending.GetValue(original)! == 0 ? new(1) : throw new IOException("Source unavailable before invocation.");
        Assert.False(s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent, new([SecureProperty.PrivateMemory], 4096)).IsSuccess);
        Assert.Equal(0, s.Provider.SecureCreateCalls);
        Assert.Equal(0, pending.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("SecureCreateMayHaveEffect")!.GetValue(original));
        Assert.Empty(((global::System.Collections.IEnumerable)original.GetType().GetProperty("SecureCreateInvocations")!.GetValue(original)!).Cast<object>());
        s.Provider.ReadIncarnation = null;
        Assert.True(s.Kernel.RevokePlatformDomain(s.Owner, s.Parent).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SecureFinalPublicationSourceLossRetainsKnownLeaseAndRefusesReclaim(bool throws)
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        Assert.True(s.Kernel.AllocateBuffer<byte>(s.Owner, 64).IsSuccess);
        s.Kernel.BeforeSecureDomainLocalPublication = () => s.Provider.ReadIncarnation = () =>
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(s.Kernel.PlatformAuthority)!));
            Assert.False(Monitor.IsEntered(typeof(RuntimeKernel).GetField("_platformMemoryUseGate", flags)!.GetValue(s.Kernel)!));
            return throws ? throw new IOException("Final source lost.") : new(2);
        };
        Assert.False(s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent, new([SecureProperty.PrivateMemory], 4096)).IsSuccess);
        Assert.NotNull(s.Provider.LastSecureCreateLease);
        Assert.Equal(1, s.Provider.SecureCreateCalls);
        Assert.Equal(0, s.Provider.SecureRevokeCalls);
        s.Provider.ReadIncarnation = null;
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
    }

    [Fact]
    public void SecureFinalPublicationRejectsSameTupleForeignParent()
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
        var original = domains[s.Parent.BindingId]!;
        s.Kernel.BeforeSecureDomainLocalPublication = () => domains[s.Parent.BindingId] =
            typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        Assert.False(s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent, new([SecureProperty.PrivateMemory], 4096)).IsSuccess);
        Assert.Equal(1, s.Provider.SecureCreateCalls);
        Assert.Equal(0, s.Provider.SecureRevokeCalls);
        // Reinstalling the original index is a test arrangement, not durable routing evidence.
        domains[s.Parent.BindingId] = original;
        Assert.False(s.Kernel.RevokePlatformDomain(s.Owner, s.Parent).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SecureCreateCapabilityRevocationRefusesEffectOrPublication(bool afterEffect)
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var revoked = false;
        void Revoke()
        {
            Assert.True(s.Kernel.CapabilityAuthority.Revoke(s.SecureCapability).IsSuccess);
            revoked = true;
        }
        if (afterEffect) s.Kernel.BeforeSecureDomainLocalPublication = Revoke;
        else
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
            var original = domains[s.Parent.BindingId]!;
            s.Provider.ReadIncarnation = () =>
            {
                if (!revoked && (int)original.GetType().GetProperty("PendingSecureCreates")!.GetValue(original)! != 0) Revoke();
                return new(1);
            };
        }
        var created = s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent, new([SecureProperty.PrivateMemory], 4096));
        Assert.True(revoked);
        Assert.Equal(KernelError.CapabilityRevoked, created.Error);
        Assert.Equal(afterEffect ? 1 : 0, s.Provider.SecureCreateCalls);
        Assert.Equal(afterEffect ? 1 : 0, s.Provider.SecureRevokeCalls);
        s.Provider.ReadIncarnation = null;
        Assert.True(s.Kernel.RevokePlatformDomain(s.Owner, s.Parent).IsSuccess);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void OriginalSecureCreateRetainsRawAndBalancesCapturedParent(bool loseOwner, bool loseReply)
    {
        var s = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        Assert.True(s.Kernel.AllocateBuffer<byte>(s.Owner, 64).IsSuccess);
        s.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
        var original = domains[s.Parent.BindingId]!;
        object Fact() => Assert.Single(((global::System.Collections.IEnumerable)original.GetType().GetProperty("SecureCreateInvocations")!.GetValue(original)!).Cast<object>());
        var effectSeen = false;
        s.Provider.SecureCreateReplyStatus = PlatformAuthorityStatus.NotAccepted;
        s.Provider.AfterSecureMaterialize = () =>
        {
            Assert.Same(original, Fact().GetType().GetProperty("Source")!.GetValue(Fact()));
            Assert.Null(Fact().GetType().GetProperty("Observation")!.GetValue(Fact()));
            Assert.NotNull(s.Provider.LastSecureCreateLease);
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(s.Kernel.PlatformAuthority)!));
            effectSeen = true;
            if (loseOwner) domains.Remove(s.Parent.BindingId);
            if (loseReply) throw new IOException("Secure original create reply lost.");
        };
        Assert.False(s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent, new([SecureProperty.PrivateMemory], 4096)).IsSuccess);
        Assert.True(effectSeen);
        Assert.Equal(0, original.GetType().GetProperty("PendingSecureCreates")!.GetValue(original));
        Assert.Equal(true, original.GetType().GetProperty("SecureCreateMayHaveEffect")!.GetValue(original));
        var raw = Fact().GetType().GetProperty("Observation")!.GetValue(Fact());
        if (loseReply) Assert.Null(raw);
        else Assert.Equal(PlatformAuthorityStatus.NotAccepted, Assert.IsType<PlatformAuthorityResult<PlatformProviderSecureDomainLease>>(raw).Status);
        s.Provider.AfterSecureMaterialize = null;
        s.Provider.SecureCreateReplyStatus = null;
        Assert.False(s.Kernel.CreateSecureDomain(s.Owner, s.SecureCapability, s.Parent, new([SecureProperty.PrivateMemory], 4096)).IsSuccess);
        Assert.False(s.Kernel.RevokePlatformDomain(s.Owner, s.Parent).IsSuccess);
        if (!loseOwner)
        {
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        }
        Assert.Equal(1, s.Provider.SecureCreateCalls);
        Assert.Equal(0, s.Provider.ParentRevokeCalls);
    }

    [Fact]
    public void MalformedSecureCreateCleanupFaultPinsParent()
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure,
            withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        scenario.Provider.SecureLeaseMode = SecureLeaseMode.ZeroIdentity;
        scenario.Provider.BeforeSecureRevoke = () => throw new InvalidOperationException("cleanup may have failed");

        var created = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability,
            scenario.Parent, new([SecureProperty.PrivateMemory], 4096));
        var revoke = scenario.Kernel.RevokePlatformDomain(scenario.Owner, scenario.Parent);

        Assert.False(created.IsSuccess);
        Assert.False(revoke.IsSuccess);
        Assert.Equal(1, scenario.Provider.SecureRevokeCalls);
        Assert.Equal(0, scenario.Provider.ParentRevokeCalls);
    }

    [Fact]
    public void MalformedSecureCreateReturnedObjectCleanupRetainsOriginalPin()
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure,
            withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        scenario.Provider.SecureLeaseMode = SecureLeaseMode.ZeroIdentity;

        var created = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability,
            scenario.Parent, new([SecureProperty.PrivateMemory], 4096));

        Assert.False(created.IsSuccess);
        Assert.Equal(1, scenario.Provider.SecureRevokeCalls);
        Assert.False(scenario.Kernel.RevokePlatformDomain(scenario.Owner, scenario.Parent).IsSuccess);
        Assert.False(scenario.Kernel.TerminateProcess(scenario.Owner).IsSuccess);
        Assert.Equal(0, scenario.Provider.ParentRevokeCalls);
    }

    [Fact]
    public void ParentDomainClosesOnlyAfterSecureChildExactClosure()
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure,
            withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var secure = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability,
            scenario.Parent, new([SecureProperty.PrivateMemory], 4096)).Value!;

        Assert.Equal(KernelError.PlatformBindingActive,
            scenario.Kernel.RevokePlatformDomain(scenario.Owner, scenario.Parent).Error);
        Assert.Equal(0, scenario.Provider.ParentRevokeCalls);
        Assert.True(scenario.Kernel.DestroySecureDomain(scenario.Owner, secure.Domain,
            secure.ConfigureCapability).IsSuccess);
        Assert.True(scenario.Kernel.RevokePlatformDomain(scenario.Owner, scenario.Parent).IsSuccess);
        Assert.Equal(1, scenario.Provider.ParentRevokeCalls);
    }

    [Fact]
    public void SecureCreateCallbackCannotRevokeParentBeforeLeasePublication()
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure,
            withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        scenario.Provider.BeforeSecureCreate = () =>
        {
            Assert.Equal(KernelError.PlatformBindingActive,
                scenario.Kernel.RevokePlatformDomain(scenario.Owner, scenario.Parent).Error);
            Assert.Equal(0, scenario.Provider.ParentRevokeCalls);
        };

        var secure = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability,
            scenario.Parent, new([SecureProperty.PrivateMemory], 4096));

        Assert.True(secure.IsSuccess, secure.Message);
        Assert.True(scenario.Kernel.DestroySecureDomain(scenario.Owner, secure.Value!.Domain,
            secure.Value.ConfigureCapability).IsSuccess);
        Assert.True(scenario.Kernel.RevokePlatformDomain(scenario.Owner, scenario.Parent).IsSuccess);
        Assert.Equal(1, scenario.Provider.ParentRevokeCalls);
    }

    [Fact]
    public async Task ConcurrentParentRevokeCannotPassInFlightSecureCreate()
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure,
            withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        scenario.Provider.BeforeSecureCreate = () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        var creation = Task.Run(() => scenario.Kernel.CreateSecureDomain(scenario.Owner,
            scenario.SecureCapability, scenario.Parent, new([SecureProperty.PrivateMemory], 4096)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(KernelError.PlatformBindingActive,
                scenario.Kernel.RevokePlatformDomain(scenario.Owner, scenario.Parent).Error);
            Assert.Equal(0, scenario.Provider.ParentRevokeCalls);
        }
        finally { release.Set(); }
        var secure = await creation;
        Assert.True(secure.IsSuccess, secure.Message);
        Assert.True(scenario.Kernel.DestroySecureDomain(scenario.Owner, secure.Value!.Domain,
            secure.Value.ConfigureCapability).IsSuccess);
        Assert.True(scenario.Kernel.RevokePlatformDomain(scenario.Owner, scenario.Parent).IsSuccess);
    }

    [Fact]
    public async Task ConcurrentSecureCreateCannotPassInFlightParentRevoke()
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure,
            withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        scenario.Provider.BeforeParentRevoke = () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        var revocation = Task.Run(() => scenario.Kernel.RevokePlatformDomain(scenario.Owner, scenario.Parent));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            var created = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability,
                scenario.Parent, new([SecureProperty.PrivateMemory], 4096));
            Assert.False(created.IsSuccess);
            Assert.Equal(0, scenario.Provider.SecureCreateCalls);
        }
        finally { release.Set(); }
        Assert.True((await revocation).IsSuccess);
        Assert.Equal(1, scenario.Provider.ParentRevokeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParentRevokeResetOrThrowRetainsPossibleEffectPin(bool throws)
    {
        var scenario = Create();
        scenario.Provider.BeforeParentRevoke = throws
            ? () => throw new InvalidOperationException("after possible parent effect")
            : () => Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);

        var revoked = scenario.Kernel.RevokePlatformDomain(scenario.Owner, scenario.Parent);
        var retry = scenario.Kernel.RevokePlatformDomain(scenario.Owner, scenario.Parent);

        Assert.Equal(KernelError.PlatformFaulted, revoked.Error);
        Assert.False(retry.IsSuccess);
        Assert.Equal(1, scenario.Provider.ParentRevokeCalls);
    }

    [Fact]
    public async Task ResetBetweenBridgeAdmissionAndKernelPublicationCannotIssueSecureHandle()
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure,
            withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var capabilitiesBefore = scenario.Kernel.CapabilityAuthority.InspectionSnapshot().Length;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        scenario.Kernel.BeforeSecureDomainLocalPublication = () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        var creation = Task.Run(() => scenario.Kernel.CreateSecureDomain(scenario.Owner,
            scenario.SecureCapability, scenario.Parent, new([SecureProperty.PrivateMemory], 4096)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);
        }
        finally { release.Set(); }

        var created = await creation;
        Assert.Equal(KernelError.PlatformFaulted, created.Error);
        Assert.Equal(capabilitiesBefore, scenario.Kernel.CapabilityAuthority.InspectionSnapshot().Length);
        Assert.Equal(1, scenario.Provider.SecureCreateCalls);
        Assert.Equal(0, scenario.Provider.SecureRevokeCalls);
        Assert.False(scenario.Kernel.RevokePlatformDomain(scenario.Owner, scenario.Parent).IsSuccess);
    }

    [Fact]
    public void ProcessExitBeforeSecureHandlePublicationAttemptsExactChildClosure()
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure,
            withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        scenario.Kernel.BeforeSecureDomainLocalPublication = () =>
            Assert.False(scenario.Kernel.TerminateProcess(scenario.Owner).IsSuccess);

        var created = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability,
            scenario.Parent, new([SecureProperty.PrivateMemory], 4096));

        Assert.False(created.IsSuccess);
        Assert.Equal(1, scenario.Provider.SecureCreateCalls);
        Assert.Equal(1, scenario.Provider.SecureRevokeCalls);
        var resumed = scenario.Kernel.ObserveProcessTeardown(scenario.Owner);
        Assert.True(resumed.IsSuccess, resumed.Message);
        Assert.True(resumed.Value!.IsComplete);
    }

    [Fact]
    public void FailedLocalPublicationCleanupRetainsUnpublishedSecureChildPin()
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure,
            withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        scenario.Kernel.BeforeSecureDomainLocalPublication = () =>
            throw new InvalidOperationException("local publication failed");
        scenario.Provider.BeforeSecureRevoke = () =>
            throw new InvalidOperationException("closure may have failed");

        var created = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability,
            scenario.Parent, new([SecureProperty.PrivateMemory], 4096));

        Assert.Equal(KernelError.PlatformFaulted, created.Error);
        Assert.Equal(1, scenario.Provider.SecureRevokeCalls);
        Assert.Equal(KernelError.PlatformBindingActive,
            scenario.Kernel.RevokePlatformDomain(scenario.Owner, scenario.Parent).Error);
        Assert.Equal(0, scenario.Provider.ParentRevokeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SecureTransitionResetOrThrowQuarantinesBeforeKernelStatePublication(bool throws)
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure,
            withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var secure = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability,
            scenario.Parent, new([SecureProperty.PrivateMemory], 4096)).Value!;
        scenario.Provider.BeforeSecureTransition = throws
            ? () => throw new InvalidOperationException("after possible transition effect")
            : () => Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);

        var transitioned = scenario.Kernel.TransitionSecureDomain(scenario.Owner, secure.Domain,
            secure.ExecuteCapability, PlatformSecureDomainTransition.BeginDrain);
        var retry = scenario.Kernel.TransitionSecureDomain(scenario.Owner, secure.Domain,
            secure.ExecuteCapability, PlatformSecureDomainTransition.BeginDrain);

        Assert.Equal(KernelError.PlatformFaulted, transitioned.Error);
        Assert.False(retry.IsSuccess);
        Assert.Equal(1, scenario.Provider.SecureTransitionCalls);
        Assert.False(scenario.Kernel.RevokePlatformDomain(scenario.Owner, scenario.Parent).IsSuccess);
    }

    [Fact]
    public void ResetAfterSecureTransitionReceiptCannotPublishKernelState()
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure,
            withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var secure = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability,
            scenario.Parent, new([SecureProperty.PrivateMemory], 4096)).Value!;
        scenario.Kernel.BeforeSecureDomainTransitionLocalCommit = () =>
            Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);

        var transitioned = scenario.Kernel.TransitionSecureDomain(scenario.Owner, secure.Domain,
            secure.ExecuteCapability, PlatformSecureDomainTransition.BeginDrain);

        Assert.Equal(KernelError.PlatformFaulted, transitioned.Error);
        Assert.Equal(1, scenario.Provider.SecureTransitionCalls);
        Assert.False(scenario.Kernel.DestroySecureDomain(scenario.Owner, secure.Domain,
            secure.ConfigureCapability).IsSuccess);
    }

    [Fact]
    public async Task ConcurrentSecureTransitionHasOneProviderCallback()
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure,
            withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var secure = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability,
            scenario.Parent, new([SecureProperty.PrivateMemory], 4096)).Value!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        scenario.Provider.BeforeSecureTransition = () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        var first = Task.Run(() => scenario.Kernel.TransitionSecureDomain(scenario.Owner,
            secure.Domain, secure.ExecuteCapability, PlatformSecureDomainTransition.BeginDrain));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(scenario.Kernel.TransitionSecureDomain(scenario.Owner, secure.Domain,
                secure.ExecuteCapability, PlatformSecureDomainTransition.BeginDrain).IsSuccess);
            Assert.Equal(1, scenario.Provider.SecureTransitionCalls);
        }
        finally { release.Set(); }
        Assert.True((await first).IsSuccess);
        Assert.Equal(1, scenario.Provider.SecureTransitionCalls);
    }

    [Fact]
    public async Task SecureDestroyCannotOverlapInFlightPublicTransition()
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure,
            withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var secure = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability,
            scenario.Parent, new([SecureProperty.PrivateMemory], 4096)).Value!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        scenario.Provider.BeforeSecureTransition = () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        var transition = Task.Run(() => scenario.Kernel.TransitionSecureDomain(scenario.Owner,
            secure.Domain, secure.ExecuteCapability, PlatformSecureDomainTransition.BeginDrain));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(KernelError.PlatformBindingActive,
                scenario.Kernel.DestroySecureDomain(scenario.Owner, secure.Domain,
                    secure.ConfigureCapability).Error);
            Assert.Equal(1, scenario.Provider.SecureTransitionCalls);
        }
        finally { release.Set(); }
        Assert.True((await transition).IsSuccess);
    }

    [Fact]
    public async Task PublicTransitionCannotOverlapInFlightSecureDestroy()
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure,
            withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var secure = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability,
            scenario.Parent, new([SecureProperty.PrivateMemory], 4096)).Value!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        scenario.Provider.BeforeSecureTransition = () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        var destroy = Task.Run(() => scenario.Kernel.DestroySecureDomain(scenario.Owner,
            secure.Domain, secure.ConfigureCapability));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(KernelError.PlatformBindingActive,
                scenario.Kernel.TransitionSecureDomain(scenario.Owner, secure.Domain,
                    secure.ExecuteCapability, PlatformSecureDomainTransition.BeginDrain).Error);
            Assert.Equal(1, scenario.Provider.SecureTransitionCalls);
        }
        finally { release.Set(); }
        Assert.True((await destroy).IsSuccess);
    }

    [Fact]
    public void SecureRegionBlocksMappingReclaimUntilExactUnbind()
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Owner, 128).Value!;
        var process = scenario.Kernel.Processes.Resolve(scenario.Owner).Value!;
        var regionCapability = scenario.Kernel.MintCapability(process.DomainId, scenario.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var mapping = scenario.Kernel.MapPlatformOwnedRegion(scenario.Owner, scenario.Parent, regionCapability, buffer.Handle,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);
        Assert.True(mapping.IsSuccess, mapping.Message);
        var secure = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability, scenario.Parent,
            new([SecureProperty.PrivateMemory], 4096));
        Assert.True(secure.IsSuccess, secure.Message);
        Assert.True(scenario.Kernel.BindSecureRegion(scenario.Owner, secure.Value!.Domain, secure.Value.MemoryCapability,
            mapping.Value!, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive,
            scenario.Kernel.RevokePlatformRegionMapping(scenario.Owner, mapping.Value).Error);
        Assert.True(scenario.Kernel.CloseSecureRegion(scenario.Owner, secure.Value.Domain, secure.Value.MemoryCapability, mapping.Value).IsSuccess);
        var revoked = scenario.Kernel.RevokePlatformRegionMapping(scenario.Owner, mapping.Value);
        Assert.True(revoked.IsSuccess, revoked.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InFlightSecureRegionMutationBlocksTransitionAndMappingRevoke(bool unbind)
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Owner, 128).Value!;
        var process = scenario.Kernel.Processes.Resolve(scenario.Owner).Value!;
        var capability = scenario.Kernel.MintCapability(process.DomainId, scenario.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var mapping = scenario.Kernel.MapPlatformOwnedRegion(scenario.Owner, scenario.Parent, capability, buffer.Handle,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var secure = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability, scenario.Parent,
            new([SecureProperty.PrivateMemory], 4096)).Value!;
        if (unbind) Assert.True(scenario.Kernel.BindSecureRegion(scenario.Owner, secure.Domain,
            secure.MemoryCapability, mapping, PlatformSecureRegionClass.Private).IsSuccess);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Action pause = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        if (unbind) scenario.Provider.BeforeSecureUnbind = pause;
        else scenario.Provider.BeforeSecureBind = pause;
        var mutation = Task.Run(() => unbind
            ? scenario.Kernel.CloseSecureRegion(scenario.Owner, secure.Domain, secure.MemoryCapability, mapping)
            : scenario.Kernel.BindSecureRegion(scenario.Owner, secure.Domain, secure.MemoryCapability,
                mapping, PlatformSecureRegionClass.Private));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(KernelError.PlatformBindingActive, scenario.Kernel.TransitionSecureDomain(scenario.Owner,
                secure.Domain, secure.ExecuteCapability, PlatformSecureDomainTransition.BeginDrain).Error);
            Assert.Equal(KernelError.PlatformBindingActive,
                scenario.Kernel.RevokePlatformRegionMapping(scenario.Owner, mapping).Error);
            Assert.Equal(0, scenario.Provider.SecureTransitionCalls);
        }
        finally { release.Set(); }
        Assert.True((await mutation).IsSuccess);
        if (unbind)
            Assert.True(scenario.Kernel.RevokePlatformRegionMapping(scenario.Owner, mapping).IsSuccess);
        else
            Assert.Equal(KernelError.PlatformBindingActive,
                scenario.Kernel.RevokePlatformRegionMapping(scenario.Owner, mapping).Error);
    }

    [Fact]
    public async Task MappingRevocationDrainingRejectsLateSecureBindBeforeProvider()
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Owner, 128).Value!;
        var process = scenario.Kernel.Processes.Resolve(scenario.Owner).Value!;
        var capability = scenario.Kernel.MintCapability(process.DomainId, scenario.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var mapping = scenario.Kernel.MapPlatformOwnedRegion(scenario.Owner, scenario.Parent, capability, buffer.Handle,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var secure = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability, scenario.Parent,
            new([SecureProperty.PrivateMemory], 4096)).Value!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        scenario.Provider.BeforeMappingRevoke = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        var revoke = Task.Run(() => scenario.Kernel.RevokePlatformRegionMapping(scenario.Owner, mapping));
        Task<KernelResult>? bind = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            bind = Task.Run(() => scenario.Kernel.BindSecureRegion(scenario.Owner, secure.Domain,
                secure.MemoryCapability, mapping, PlatformSecureRegionClass.Private));
            Assert.Equal(0, scenario.Provider.SecureBindCalls);
        }
        finally { release.Set(); }
        Assert.True((await revoke).IsSuccess);
        Assert.NotNull(bind);
        Assert.False((await bind).IsSuccess);
        Assert.Equal(0, scenario.Provider.SecureBindCalls);
    }

    [Fact]
    public async Task BridgeMappingRevocationDrainingRejectsConcurrentSecureBind()
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Owner, 128).Value!;
        var process = scenario.Kernel.Processes.Resolve(scenario.Owner).Value!;
        var capability = scenario.Kernel.MintCapability(process.DomainId, scenario.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var mapping = scenario.Kernel.MapPlatformOwnedRegion(scenario.Owner, scenario.Parent, capability, buffer.Handle,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var bridge = scenario.Kernel.PlatformAuthority;
        var secure = bridge.CreateSecureDomain(scenario.Parent, scenario.Parent.Subject,
            new([SecureProperty.PrivateMemory], 4096)).Value!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        scenario.Provider.BeforeMappingRevoke = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        var revoke = Task.Run(() => bridge.BeginRegionMappingRevocation(mapping, scenario.Parent.Subject,
            PlatformRegionRevocationPolicy.DrainBeforeRevoke));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(bridge.BindSecureRegion(secure, mapping, PlatformSecureRegionClass.Private).IsSuccess);
            Assert.Equal(0, scenario.Provider.SecureBindCalls);
        }
        finally { release.Set(); }
        Assert.True((await revoke).IsSuccess);
    }

    [Fact]
    public async Task BridgeInFlightSecureBindRejectsMappingRevocation()
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Owner, 128).Value!;
        var process = scenario.Kernel.Processes.Resolve(scenario.Owner).Value!;
        var capability = scenario.Kernel.MintCapability(process.DomainId, scenario.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var mapping = scenario.Kernel.MapPlatformOwnedRegion(scenario.Owner, scenario.Parent, capability, buffer.Handle,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var bridge = scenario.Kernel.PlatformAuthority;
        var secure = bridge.CreateSecureDomain(scenario.Parent, scenario.Parent.Subject,
            new([SecureProperty.PrivateMemory], 4096)).Value!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        scenario.Provider.BeforeSecureBind = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        var bind = Task.Run(() => bridge.BindSecureRegion(secure, mapping, PlatformSecureRegionClass.Private));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(KernelError.PlatformBindingActive,
                bridge.BeginRegionMappingRevocation(mapping, scenario.Parent.Subject,
                    PlatformRegionRevocationPolicy.DrainBeforeRevoke).Error);
        }
        finally { release.Set(); }
        Assert.True((await bind).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetAfterSecureRegionProviderReceiptPreventsLocalCommit(bool unbind)
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Owner, 128).Value!;
        var process = scenario.Kernel.Processes.Resolve(scenario.Owner).Value!;
        var capability = scenario.Kernel.MintCapability(process.DomainId, scenario.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var mapping = scenario.Kernel.MapPlatformOwnedRegion(scenario.Owner, scenario.Parent, capability, buffer.Handle,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var secure = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability, scenario.Parent,
            new([SecureProperty.PrivateMemory], 4096)).Value!;
        if (unbind) Assert.True(scenario.Kernel.BindSecureRegion(scenario.Owner, secure.Domain,
            secure.MemoryCapability, mapping, PlatformSecureRegionClass.Private).IsSuccess);
        scenario.Kernel.BeforeSecureRegionLocalCommit = () =>
            Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);
        var result = unbind
            ? scenario.Kernel.CloseSecureRegion(scenario.Owner, secure.Domain, secure.MemoryCapability, mapping)
            : scenario.Kernel.BindSecureRegion(scenario.Owner, secure.Domain, secure.MemoryCapability,
                mapping, PlatformSecureRegionClass.Private);
        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.False(scenario.Kernel.RevokePlatformRegionMapping(scenario.Owner, mapping).IsSuccess);
        Assert.False(scenario.Kernel.DestroySecureDomain(scenario.Owner, secure.Domain, secure.ConfigureCapability).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SecureRegionBindResetOrThrowPinsMappingWithoutPublishedLease(bool throws)
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure,
            withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Owner, 128).Value!;
        var process = scenario.Kernel.Processes.Resolve(scenario.Owner).Value!;
        var regionCapability = scenario.Kernel.MintCapability(process.DomainId, scenario.Owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var mapping = scenario.Kernel.MapPlatformOwnedRegion(scenario.Owner, scenario.Parent,
            regionCapability, buffer.Handle, PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var secure = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability,
            scenario.Parent, new([SecureProperty.PrivateMemory], 4096)).Value!;
        scenario.Provider.BeforeSecureBind = throws
            ? () => throw new InvalidOperationException("after possible bind effect")
            : () => Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess);

        var bound = scenario.Kernel.BindSecureRegion(scenario.Owner, secure.Domain,
            secure.MemoryCapability, mapping, PlatformSecureRegionClass.Private);
        var retry = scenario.Kernel.BindSecureRegion(scenario.Owner, secure.Domain,
            secure.MemoryCapability, mapping, PlatformSecureRegionClass.Private);

        Assert.Equal(KernelError.PlatformFaulted, bound.Error);
        Assert.False(retry.IsSuccess);
        Assert.Equal(1, scenario.Provider.SecureBindCalls);
        Assert.False(scenario.Kernel.RevokePlatformRegionMapping(scenario.Owner, mapping).IsSuccess);
        var receipts = UnpublishedSecureReceipts(scenario.Kernel);
        if (throws) Assert.Empty(receipts); // Unknown outcome does not manufacture a receipt.
        else
        {
            var retained = Assert.Single(receipts);
            Assert.Equal(scenario.Provider.LastSecureRegionBinding, retained.GetType().GetProperty("Receipt")!.GetValue(retained));
            Assert.Equal(mapping, retained.GetType().GetProperty("Mapping")!.GetValue(retained));
            Assert.Equal(scenario.Provider.LastSecureRegionBinding!.Value.Mapping, retained.GetType().GetProperty("AdmittedMapping")!.GetValue(retained));
            Assert.NotEqual(scenario.Kernel.PlatformAuthority.BackendEpoch, retained.GetType().GetProperty("AdmissionEpoch")!.GetValue(retained));
        }
        Assert.False(scenario.Kernel.DestroySecureDomain(scenario.Owner, secure.Domain, secure.ConfigureCapability).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedOrMalformedSecureRegionBindPinsMappingWithoutExactClosure(bool malformed)
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure,
            withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Owner, 128).Value!;
        var process = scenario.Kernel.Processes.Resolve(scenario.Owner).Value!;
        var regionCapability = scenario.Kernel.MintCapability(process.DomainId, scenario.Owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var mapping = scenario.Kernel.MapPlatformOwnedRegion(scenario.Owner, scenario.Parent,
            regionCapability, buffer.Handle, PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var secure = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability,
            scenario.Parent, new([SecureProperty.PrivateMemory], 4096)).Value!;
        scenario.Provider.FailSecureBind = !malformed;
        scenario.Provider.MalformedSecureBind = malformed;
        scenario.Provider.BeforeSecureUnbind = () => throw new InvalidOperationException("cleanup may have failed");

        var bound = scenario.Kernel.BindSecureRegion(scenario.Owner, secure.Domain,
            secure.MemoryCapability, mapping, PlatformSecureRegionClass.Private);

        Assert.False(bound.IsSuccess);
        Assert.Equal(1, scenario.Provider.SecureBindCalls);
        Assert.Equal(malformed ? 1 : 0, scenario.Provider.SecureUnbindCalls);
        Assert.False(scenario.Kernel.RevokePlatformRegionMapping(scenario.Owner, mapping).IsSuccess);
        var receipts = UnpublishedSecureReceipts(scenario.Kernel);
        if (malformed)
        {
            var retained = Assert.Single(receipts);
            Assert.Equal(scenario.Provider.LastSecureRegionBinding, retained.GetType().GetProperty("Receipt")!.GetValue(retained));
        }
        else Assert.Empty(receipts);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void SecureRegionUnbindRefusalKeepsMappingPinned(int fault)
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure,
            withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Owner, 128).Value!;
        var process = scenario.Kernel.Processes.Resolve(scenario.Owner).Value!;
        var regionCapability = scenario.Kernel.MintCapability(process.DomainId, scenario.Owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var mapping = scenario.Kernel.MapPlatformOwnedRegion(scenario.Owner, scenario.Parent,
            regionCapability, buffer.Handle, PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var secure = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability,
            scenario.Parent, new([SecureProperty.PrivateMemory], 4096)).Value!;
        Assert.True(scenario.Kernel.BindSecureRegion(scenario.Owner, secure.Domain,
            secure.MemoryCapability, mapping, PlatformSecureRegionClass.Private).IsSuccess);
        var throws = fault == 1;
        scenario.Provider.SecureClosureReplyFault = fault >= 2 ? fault : 0;
        scenario.Provider.BeforeSecureUnbind = throws
            ? () => throw new InvalidOperationException("after possible effect")
            : () => { if (fault == 0) Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess); };

        var closed = scenario.Kernel.CloseSecureRegion(scenario.Owner, secure.Domain,
            secure.MemoryCapability, mapping);
        var retry = scenario.Kernel.CloseSecureRegion(scenario.Owner, secure.Domain,
            secure.MemoryCapability, mapping);

        Assert.Equal(KernelError.PlatformFaulted, closed.Error);
        Assert.False(retry.IsSuccess);
        Assert.Equal(1, scenario.Provider.SecureUnbindCalls);
        Assert.False(scenario.Kernel.RevokePlatformRegionMapping(scenario.Owner, mapping).IsSuccess);
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_secureDomains",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario.Kernel.PlatformAuthority)!;
        var record = records.Values.Cast<object>().Single();
        var receipts = (global::System.Collections.IDictionary)record.GetType().GetProperty("RegionClosureReceipts")!.GetValue(record)!;
        var invocations = (global::System.Collections.IList)record.GetType().GetProperty("RegionClosureInvocations")!.GetValue(record)!;
        Assert.Single(invocations.Cast<object>());
        var originalAttempt = invocations[0]!;
        Assert.Same(record, originalAttempt.GetType().GetProperty("Source")!.GetValue(originalAttempt));
        Assert.Equal(scenario.Provider.LastSecureRegionBinding, originalAttempt.GetType().GetProperty("AdmittedBinding")!.GetValue(originalAttempt));
        var originalRaw = originalAttempt.GetType().GetProperty("Observation")!.GetValue(originalAttempt);
        if (throws) Assert.Null(originalRaw);
        else Assert.Equal(fault == 2 ? PlatformAuthorityStatus.Faulted : PlatformAuthorityStatus.Success,
            ((PlatformAuthorityResult<PlatformSecureRegionClosureReceipt>)originalRaw!).Status);
        Assert.Equal(RegionState.Owned, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
        Assert.Equal(fault is 1 or 2 ? 0 : 1, receipts.Count);
        Assert.Empty(((global::System.Collections.IEnumerable)record.GetType().GetProperty("RegionMutationsInFlight")!.GetValue(record)!).Cast<object>());
        if (fault is 0 or 3)
        {
            var fact = receipts.Values.Cast<object>().Single();
            Assert.Equal(scenario.Provider.LastSecureRegionBinding, fact.GetType().GetProperty("AdmittedBinding")!.GetValue(fact));
            var receipt = (PlatformSecureRegionClosureReceipt)fact.GetType().GetProperty("Receipt")!.GetValue(fact)!;
            Assert.Equal(scenario.Provider.LastSecureRegionBinding, receipt.Binding);
            if (fault == 0) Assert.NotEqual(scenario.Kernel.PlatformAuthority.BackendEpoch, fact.GetType().GetProperty("AdmissionEpoch")!.GetValue(fact));
            else Assert.False(receipt.Closed);
        }
        if (fault >= 2)
        {
            scenario.Provider.SecureClosureReplyFault = 0;
            var recovered = scenario.Kernel.CloseSecureRegion(scenario.Owner, secure.Domain, secure.MemoryCapability, mapping);
            Assert.False(recovered.IsSuccess);
            Assert.Equal(1, scenario.Provider.SecureUnbindCalls);
            Assert.Single(invocations.Cast<object>());
            Assert.Same(originalAttempt, invocations[0]);
            Assert.Equal(originalRaw, originalAttempt.GetType().GetProperty("Observation")!.GetValue(originalAttempt));
            var regions = (global::System.Collections.IDictionary)record.GetType().GetProperty("Regions")!.GetValue(record)!;
            Assert.Single(regions.Values.Cast<object>());
            Assert.False(scenario.Kernel.RevokePlatformRegionMapping(scenario.Owner, mapping).IsSuccess);
            Assert.Equal(RegionState.Owned, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void SecureDomainRevokeRefusalKeepsDomainPinned(int fault)
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure,
            withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        Assert.True(scenario.Kernel.AllocateBuffer<byte>(scenario.Owner, 64).IsSuccess);
        var secure = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability,
            scenario.Parent, new([SecureProperty.PrivateMemory], 4096)).Value!;
        var throws = fault == 1;
        scenario.Provider.SecureClosureReplyFault = fault >= 2 ? fault : 0;
        scenario.Provider.BeforeSecureRevoke = throws
            ? () => throw new InvalidOperationException("after possible effect")
            : () => { if (fault == 0) Assert.True(scenario.Kernel.ObservePlatformBackendReset().IsSuccess); };

        var closed = scenario.Kernel.DestroySecureDomain(scenario.Owner, secure.Domain,
            secure.ConfigureCapability);
        var retry = scenario.Kernel.DestroySecureDomain(scenario.Owner, secure.Domain,
            secure.ConfigureCapability);

        Assert.Equal(KernelError.PlatformFaulted, closed.Error);
        Assert.False(retry.IsSuccess);
        Assert.Equal(1, scenario.Provider.SecureRevokeCalls);
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_secureDomains",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario.Kernel.PlatformAuthority)!;
        var record = records.Values.Cast<object>().Single();
        Assert.Equal(false, record.GetType().GetProperty("RevokeInFlight")!.GetValue(record));
        var invocation = record.GetType().GetProperty("RevokeInvocation")!.GetValue(record)!;
        Assert.Same(record, invocation.GetType().GetProperty("Source")!.GetValue(invocation));
        Assert.Equal(record.GetType().GetProperty("ProviderLease")!.GetValue(record),
            invocation.GetType().GetProperty("AdmittedLease")!.GetValue(invocation));
        var raw = invocation.GetType().GetProperty("Observation")!.GetValue(invocation);
        if (throws) Assert.Null(raw);
        else Assert.Equal(fault == 2 ? PlatformAuthorityStatus.Faulted : PlatformAuthorityStatus.Success,
            ((PlatformAuthorityResult<PlatformSecureDomainClosureReceipt>)raw!).Status);
        Assert.False(scenario.Kernel.TerminateProcess(scenario.Owner).IsSuccess);
        Assert.Equal(1, scenario.Provider.SecureRevokeCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
        var fact = record.GetType().GetProperty("LastClosureReceipt")!.GetValue(record);
        if (fault is 1 or 2) Assert.Null(fact);
        else
        {
            Assert.NotNull(fact);
            var lease = (PlatformProviderSecureDomainLease)record.GetType().GetProperty("ProviderLease")!.GetValue(record)!;
            Assert.Equal(lease, fact.GetType().GetProperty("AdmittedLease")!.GetValue(fact));
            var receipt = (PlatformSecureDomainClosureReceipt)fact.GetType().GetProperty("Receipt")!.GetValue(fact)!;
            Assert.Equal(lease, receipt.Domain);
            if (fault == 0) Assert.NotEqual(scenario.Kernel.PlatformAuthority.BackendEpoch, fact.GetType().GetProperty("AdmissionEpoch")!.GetValue(fact));
            else Assert.False(receipt.Closed);
        }
    }

    [Fact]
    public void StaleWrongSubjectAndMalformedEvidenceFailClosed()
    {
        var scenario = Create(withEvidenceCapability: true);
        foreach (var mode in new[] { EvidenceMode.Stale, EvidenceMode.WrongOwner, EvidenceMode.WrongDomain, EvidenceMode.Malformed, EvidenceMode.Revoked, EvidenceMode.Faulted, EvidenceMode.Unknown })
        {
            scenario.Provider.EvidenceMode = mode;
            var result = scenario.Kernel.ReadPlatformEvidence(scenario.Owner, scenario.EvidenceCapability, scenario.Parent,
                new("runtime.health", 1), EvidenceVisibilityClass.SecurityMeasurement, new(7, 2));
            Assert.False(result.IsSuccess);
        }
    }

    [Fact]
    public void HostInternalCatalogEntriesAreFilteredAtBridgeBoundary()
    {
        var scenario = Create(withEvidenceCapability: true);
        var catalog = scenario.Kernel.QueryPlatformEvidenceCatalog(scenario.Owner, scenario.EvidenceCapability, scenario.Parent);
        Assert.True(catalog.IsSuccess, catalog.Message);
        Assert.DoesNotContain(catalog.Value!.Entries, entry => entry.Visibility == EvidenceVisibilityClass.HostInternal);
    }

    [Fact]
    public void ModelEvidenceCannotClaimHardwareRootedAttestation()
    {
        var scenario = Create(withEvidenceCapability: true);
        scenario.Provider.EvidenceMode = EvidenceMode.Valid;
        var result = scenario.Kernel.ReadPlatformEvidence(scenario.Owner, scenario.EvidenceCapability, scenario.Parent,
            new("runtime.health", 1), EvidenceVisibilityClass.SecurityMeasurement, new(7, 1));
        Assert.True(result.IsSuccess, result.Message);
        Assert.False(result.Value!.HardwareRooted);
    }

    [Fact]
    public void ModelProviderCannotPublishHardwareRootedEvidence()
    {
        var scenario = Create(withEvidenceCapability: true);
        scenario.Provider.EvidenceMode = EvidenceMode.HardwareRooted;
        var result = scenario.Kernel.ReadPlatformEvidence(scenario.Owner, scenario.EvidenceCapability, scenario.Parent,
            new("runtime.health", 1), EvidenceVisibilityClass.SecurityMeasurement, new(7, 1));
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void UnknownVisibilityFailsBeforeProviderCall()
    {
        var scenario = Create(withEvidenceCapability: true);
        var result = scenario.Kernel.ReadPlatformEvidence(scenario.Owner, scenario.EvidenceCapability, scenario.Parent,
            new("runtime.health", 1), (EvidenceVisibilityClass)999, new(7, 1));
        Assert.False(result.IsSuccess);
        Assert.Equal(0, scenario.Provider.ReadCalls);
    }

    [Fact]
    public void ProcessTeardownDrainsAndClosesSecureDomainBeforeParent()
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var active = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability, scenario.Parent,
            new([SecureProperty.PrivateMemory], 4096));
        Assert.True(active.IsSuccess, active.Message);
        var terminated = scenario.Kernel.TerminateProcess(scenario.Owner);
        Assert.True(terminated.IsSuccess, terminated.Message);
        Assert.Equal(1, scenario.Provider.DrainCalls);
        Assert.Equal(1, scenario.Provider.SecureRevokeCalls);
        Assert.Equal(1, scenario.Provider.ParentRevokeCalls);
    }

    [Fact]
    public void BackendResetQuarantinesSecureDomainAndStaleEvidenceContext()
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var active = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability, scenario.Parent,
            new([SecureProperty.PrivateMemory], 4096));
        Assert.True(active.IsSuccess, active.Message);
        var reset = scenario.Kernel.ObservePlatformBackendReset();
        Assert.True(reset.IsSuccess, reset.Message);
        var read = scenario.Kernel.ReadSecureDomainEvidence(scenario.Owner, active.Value!.Domain,
            active.Value.EvidenceCapability, scenario.Parent, new("runtime.health", 1),
            EvidenceVisibilityClass.SecurityMeasurement, new(7, 1));
        Assert.False(read.IsSuccess);
        Assert.Equal(0, scenario.Provider.ReadCalls);
    }

    [Fact]
    public void DowngradeBlocksFutureAdmissionButActiveDomainUsesExactDrainAndClose()
    {
        var scenario = Create(secureAvailability: PlatformFeatureAvailability.ProductionSecure, withSecureCapability: true);
        scenario.Provider.ProvenProperties = [SecureProperty.PrivateMemory];
        var active = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability, scenario.Parent,
            new([SecureProperty.PrivateMemory], 4096));
        Assert.True(active.IsSuccess, active.Message);
        scenario.Provider.SecureAvailability = PlatformFeatureAvailability.Unavailable;
        scenario.Kernel.RefreshPlatformFeatures();
        var future = scenario.Kernel.CreateSecureDomain(scenario.Owner, scenario.SecureCapability, scenario.Parent,
            new([SecureProperty.PrivateMemory], 4096));
        Assert.False(future.IsSuccess);
        Assert.Equal(1, scenario.Provider.SecureCreateCalls);
        var closed = scenario.Kernel.DestroySecureDomain(scenario.Owner, active.Value!.Domain, active.Value.ConfigureCapability);
        Assert.True(closed.IsSuccess, closed.Message);
        Assert.Equal(1, scenario.Provider.DrainCalls);
        Assert.Equal(1, scenario.Provider.SecureRevokeCalls);
    }

    [Fact]
    public void EvidenceAndManifestTypesAreNotAuthorityOrReclaimProof()
    {
        Type[] evidenceTypes = [typeof(EvidenceRecord), typeof(EvidenceCatalogEntry), typeof(PlatformEvidenceCatalog), typeof(PlatformFeatureManifest)];
        foreach (var type in evidenceTypes)
        {
            Assert.False(typeof(CapabilityId).IsAssignableFrom(type));
            Assert.DoesNotContain(type.GetProperties(BindingFlags.Public | BindingFlags.Instance), property =>
                property.PropertyType == typeof(PlatformProviderDomainLease) ||
                property.PropertyType == typeof(PlatformProviderRegionMappingLease) ||
                property.PropertyType == typeof(VirtualDomainHandle) ||
                property.PropertyType == typeof(SecureDomainHandle) ||
                property.PropertyType == typeof(CapabilityId));
            Assert.DoesNotContain(type.GetMembers().Select(member => member.Name), name =>
                name.Contains("Reclaim", StringComparison.OrdinalIgnoreCase) || name.Contains("CompletionAuthority", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void ExternalGateTaxonomyHasEveryRequirementAndNoSilentUnknownState()
    {
        Assert.Equal(Enum.GetValues<PlatformExternalRequirement>().Length, PlatformExternalGateTable.Current.Count);
        Assert.All(Enum.GetValues<PlatformExternalRequirement>(), requirement =>
            Assert.Single(PlatformExternalGateTable.Current, gate => gate.Requirement == requirement));
        Assert.All(PlatformExternalGateTable.Current, gate => Assert.True(Enum.IsDefined(gate.State)));
        Assert.Contains(PlatformExternalGateTable.FeatureGates, gate => gate.Requirement == PlatformExternalRequirement.ExtHcpu006 &&
            gate.Family == PlatformFeatureFamily.PlatformEvidence && gate.State == PlatformExternalGateState.Unavailable);
        Assert.Contains(PlatformExternalGateTable.FeatureGates, gate => gate.Requirement == PlatformExternalRequirement.ExtHcpu006 &&
            gate.Family == PlatformFeatureFamily.SecureDomains && gate.State == PlatformExternalGateState.Unavailable);
        Assert.Contains(PlatformExternalGateTable.FeatureGates, gate => gate.Requirement == PlatformExternalRequirement.ExtHcpu006 &&
            gate.Family == PlatformFeatureFamily.NestedDomains && gate.State == PlatformExternalGateState.Unavailable);
    }

    private static Scenario Create(PlatformFeatureAvailability secureAvailability = PlatformFeatureAvailability.Unavailable,
        bool withEvidenceCapability = false, bool withSecureCapability = false, TimeProvider? clock = null)
    {
        var provider = new Phase7Provider { SecureAvailability = secureAvailability };
        var kernel = new RuntimeKernel(provider, clock);
        var (process, owner) = TestFixtures.Create(kernel, 700, 701);
        var parent = kernel.BindPlatformAuthorityDomain(owner);
        Assert.True(parent.IsSuccess, parent.Message);
        CapabilityId evidence = default, secure = default;
        if (withEvidenceCapability) evidence = kernel.MintCapability(process.DomainId, owner, ResourceKind.Evidence, EvidenceResourceIds.Read, CapabilityRights.Read).Value!.CapabilityId;
        if (withSecureCapability) secure = kernel.MintCapability(process.DomainId, owner, ResourceKind.SecureCompute, SecureComputeResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        return new(kernel, provider, owner, parent.Value!, evidence, secure);
    }

    private readonly record struct Scenario(RuntimeKernel Kernel, Phase7Provider Provider, ProcessHandle Owner,
        PlatformDomainBinding Parent, CapabilityId EvidenceCapability, CapabilityId SecureCapability);
    public enum SecureLeaseMode { Valid, ZeroIdentity, WrongParent, NullProperties, UnknownProperty }
    private enum EvidenceMode { Valid, Stale, WrongOwner, WrongDomain, Malformed, Revoked, Faulted, Unknown, HardwareRooted }

    private sealed class Phase7Provider : IPlatformAuthorityProvider, IPlatformFeatureProvider, IPlatformEvidenceProvider, IPlatformSecureComputeProvider, IPlatformRegionRevocationProvider, IPlatformProviderIncarnationSource
    {
        public Func<PlatformProviderIncarnation>? ReadIncarnation { get; set; }
        public PlatformProviderIncarnation CurrentIncarnation => ReadIncarnation?.Invoke() ?? new(1);
        private readonly HostPlatformAuthorityProvider _host = new();
        public PlatformProviderDescriptor Descriptor => _host.Descriptor;
        public PlatformFeatureAvailability SecureAvailability { get; set; }
        public IReadOnlyList<SecureProperty> ProvenProperties { get; set; } = [];
        public EvidenceMode EvidenceMode { get; set; }
        public SecureLeaseMode SecureLeaseMode { get; set; }
        public int ReadCalls { get; private set; }
        public int CatalogCalls { get; private set; }
        public PlatformProviderSecureDomainLease? LastSecureCreateLease { get; private set; }
        public int SecureCreateCalls { get; private set; }
        public Action? BeforeSecureCreate { get; set; }
        public Action? AfterSecureMaterialize { get; set; }
        public PlatformAuthorityStatus? SecureCreateReplyStatus { get; set; }
        public bool FailSecureCreate { get; set; }
        public int SecureRevokeCalls { get; private set; }
        public int SecureUnbindCalls { get; private set; }
        public PlatformProviderSecureRegionBinding? LastSecureRegionBinding { get; private set; }
        public int SecureBindCalls { get; private set; }
        public Action? BeforeMappingRevoke { get; set; }
        public Action? BeforeSecureBind { get; set; }
        public bool FailSecureBind { get; set; }
        public bool MalformedSecureBind { get; set; }
        public Action? AfterSecureRegionMaterialize { get; set; }
        public PlatformAuthorityStatus? SecureBindReplyStatus { get; set; }
        public Action? BeforeSecureUnbind { get; set; }
        public int SecureClosureReplyFault { get; set; }
        public Action? BeforeSecureRevoke { get; set; }
        public int DrainCalls { get; private set; }
        public int SecureTransitionCalls { get; private set; }
        public Action? BeforeSecureTransition { get; set; }
        public int? SecureTransitionReplyFault { get; set; }
        public int OrdinaryVirtualDomainCalls { get; private set; }
        public int ParentRevokeCalls { get; private set; }
        public Action? BeforeParentRevoke { get; set; }
        private ulong _nextSecure = 1;
        private ulong _nextSecureRegion = 1;

        public PlatformFeatureManifest QueryFeatures()
        {
            var entries = _host.QueryFeatures().Features.ToList();
            entries.Add(new(PlatformFeatureFamily.PlatformEvidence, 1, PlatformFeatureAvailability.RuntimeAdmission));
            if (SecureAvailability != PlatformFeatureAvailability.Unavailable) entries.Add(new(PlatformFeatureFamily.SecureDomains, 1, SecureAvailability));
            return new(entries);
        }
        public int ParentBindCalls { get; private set; }
        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject)
        {
            ParentBindCalls++;
            return _host.BindDomain(subject);
        }
        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease)
        {
            ParentRevokeCalls++;
            BeforeParentRevoke?.Invoke();
            return _host.RevokeDomain(lease);
        }
        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(PlatformProviderDomainLease domainLease, PlatformRegionIdentity region, PlatformMemoryAccess access) => _host.MapOwnedRegion(domainLease, region, access);
        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy) => _host.RevokeRegionMapping(mapping, policy);
        public PlatformAuthorityResult<PlatformRegionRevocationTicket> BeginRegionMappingRevocation(PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy)
        {
            BeforeMappingRevoke?.Invoke();
            return _host.BeginRegionMappingRevocation(mapping, policy);
        }
        public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(PlatformOperationIdentity operation) => _host.ObserveCompletion(operation);
        public PlatformAuthorityResult<PlatformEvidenceCatalog> QueryEvidenceCatalog(PlatformEvidenceCatalogRequest request) { CatalogCalls++; return PlatformAuthorityResult<PlatformEvidenceCatalog>.Ok(new([
            new(new("runtime.health", 1), EvidenceVisibilityClass.SecurityMeasurement, new("host-model"), false),
            new(new("host.secret", 1), EvidenceVisibilityClass.HostInternal, new("host-model"), false)], 7)); }
        public PlatformAuthorityResult<EvidenceRecord> ReadEvidence(PlatformEvidenceReadRequest request)
        {
            ReadCalls++;
            if (EvidenceMode == EvidenceMode.Revoked) return PlatformAuthorityResult<EvidenceRecord>.Fail(PlatformAuthorityStatus.Revoked, "revoked");
            if (EvidenceMode == EvidenceMode.Faulted) return PlatformAuthorityResult<EvidenceRecord>.Fail(PlatformAuthorityStatus.Faulted, "faulted");
            if (EvidenceMode == EvidenceMode.Unknown) return new PlatformAuthorityResult<EvidenceRecord>((PlatformAuthorityStatus)999, default, "unknown");
            var subject = request.Subject.Subject;
            var freshness = EvidenceMode == EvidenceMode.Stale ? new EvidenceFreshness(6, 1) : new EvidenceFreshness(7, 2);
            var process = EvidenceMode == EvidenceMode.WrongOwner ? new ProcessId(999) : subject.ProcessId;
            var domain = EvidenceMode == EvidenceMode.WrongDomain ? new DomainId(999) : subject.DomainId;
            var producer = EvidenceMode == EvidenceMode.Malformed ? new EvidenceProducerIdentity("") : new EvidenceProducerIdentity("host-model");
            var evidenceSubject = request.EvidenceSubject with { SubjectDomain = domain, SubjectProcess = process };
            return PlatformAuthorityResult<EvidenceRecord>.Ok(new(request.Identity, evidenceSubject, producer,
                request.Visibility, "Healthy", "model-only", freshness, EvidenceMode == EvidenceMode.HardwareRooted));
        }
        public PlatformAuthorityResult<PlatformProviderSecureDomainLease> CreateSecureDomain(PlatformSecureDomainRequest request)
        {
            SecureCreateCalls++;
            BeforeSecureCreate?.Invoke();
            if (FailSecureCreate)
                return PlatformAuthorityResult<PlatformProviderSecureDomainLease>.Fail(PlatformAuthorityStatus.Faulted,
                    "create failed after possible effect");
            var id = SecureLeaseMode == SecureLeaseMode.ZeroIdentity ? new PlatformProviderSecureDomainId(0) : new(_nextSecure++);
            var parent = SecureLeaseMode == SecureLeaseMode.WrongParent
                ? request.Parent with { Generation = new(request.Parent.Generation.Value + 1) }
                : request.Parent;
            IReadOnlyList<SecureProperty> properties = SecureLeaseMode switch
            {
                SecureLeaseMode.NullProperties => null!,
                SecureLeaseMode.UnknownProperty => [(SecureProperty)999],
                _ => ProvenProperties,
            };
            var lease = new PlatformProviderSecureDomainLease(id, new(1), parent, properties);
            LastSecureCreateLease = lease;
            AfterSecureMaterialize?.Invoke();
            if (SecureCreateReplyStatus is { } status)
                return PlatformAuthorityResult<PlatformProviderSecureDomainLease>.Fail(status, "Original secure create failed after materialization.");
            return PlatformAuthorityResult<PlatformProviderSecureDomainLease>.Ok(lease);
        }
        public PlatformAuthorityResult<PlatformProviderSecureRegionBinding> BindSecureRegion(PlatformProviderSecureDomainLease domain, PlatformProviderRegionMappingLease mapping, PlatformSecureRegionClass regionClass)
        {
            SecureBindCalls++;
            BeforeSecureBind?.Invoke();
            if (FailSecureBind)
                return PlatformAuthorityResult<PlatformProviderSecureRegionBinding>.Fail(PlatformAuthorityStatus.Faulted, "bind failed after possible effect");
            var receipt = MalformedSecureBind
                ? new PlatformProviderSecureRegionBinding(new(0), new(1), domain, mapping, regionClass)
                : new PlatformProviderSecureRegionBinding(new(_nextSecureRegion++), new(1), domain, mapping, regionClass);
            LastSecureRegionBinding = receipt;
            AfterSecureRegionMaterialize?.Invoke();
            if (SecureBindReplyStatus is { } status)
                return PlatformAuthorityResult<PlatformProviderSecureRegionBinding>.Fail(status, "Original bind reply after materialized effect.");
            return PlatformAuthorityResult<PlatformProviderSecureRegionBinding>.Ok(receipt);
        }
        public PlatformAuthorityResult<PlatformSecureRegionClosureReceipt> UnbindSecureRegion(PlatformProviderSecureRegionBinding binding)
        {
            SecureUnbindCalls++;
            BeforeSecureUnbind?.Invoke();
            return SecureClosureReplyFault == 2
                ? PlatformAuthorityResult<PlatformSecureRegionClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted, "reply failed")
                : PlatformAuthorityResult<PlatformSecureRegionClosureReceipt>.Ok(new(binding, SecureClosureReplyFault != 3));
        }
        public PlatformAuthorityResult<PlatformSecureDomainTransitionReceipt> TransitionSecureDomain(PlatformProviderSecureDomainLease domain, PlatformSecureDomainTransition transition)
        {
            SecureTransitionCalls++;
            BeforeSecureTransition?.Invoke();
            if (transition == PlatformSecureDomainTransition.BeginDrain) DrainCalls++;
            if (SecureTransitionReplyFault is { } fault)
            {
                var receipt = new PlatformSecureDomainTransitionReceipt(domain,
                    fault == 3 ? (PlatformSecureDomainTransition)999 : transition, true);
                return new(fault switch { 0 => PlatformAuthorityStatus.Faulted, 1 => PlatformAuthorityStatus.NotAccepted,
                    2 => (PlatformAuthorityStatus)999, _ => PlatformAuthorityStatus.Success }, receipt, "Original transition reply.");
            }
            return PlatformAuthorityResult<PlatformSecureDomainTransitionReceipt>.Ok(new(domain, transition, true));
        }
        public PlatformAuthorityResult<PlatformSecureDomainClosureReceipt> RevokeSecureDomain(PlatformProviderSecureDomainLease domain)
        {
            SecureRevokeCalls++;
            BeforeSecureRevoke?.Invoke();
            return SecureClosureReplyFault == 2
                ? PlatformAuthorityResult<PlatformSecureDomainClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted, "reply failed")
                : PlatformAuthorityResult<PlatformSecureDomainClosureReceipt>.Ok(new(domain, SecureClosureReplyFault != 3));
        }
    }
}
