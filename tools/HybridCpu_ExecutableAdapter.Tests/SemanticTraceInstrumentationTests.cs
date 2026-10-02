using SingPlus.Contracts;
using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;
using Xunit;

namespace HybridCpu_ExecutableAdapter.Tests;

public sealed class SemanticTraceInstrumentationTests
{
    [Fact]
    public void DistinctChildOwnersWithSameEpochsHaveDistinctGenerationTraceDigests()
    {
        var sink = new CollectingSink();
        var adapter = new HybridCpuExecutableChildAdapter(sink);
        var first = PrepareStart(adapter);
        var second = PrepareStart(adapter);
        Assert.Equal(first.ChildLease.Epoch, second.ChildLease.Epoch);
        Assert.NotEqual(first.ChildLease.Handle, second.ChildLease.Handle);
        _ = adapter.StartExecutableArtifact(first);
        var firstDigest = sink.Events[0].GenerationVectorDigest;
        var count = sink.Events.Count;
        _ = adapter.StartExecutableArtifact(second);
        Assert.NotEqual(firstDigest, sink.Events[count].GenerationVectorDigest);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("lost")]
    [InlineData("throw")]
    public void ChildCreateReplyRevalidatesParentUncertaintyFromNestedCreate(string fault)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory;
        var intent = new NeutralChildDomainIntent(new(1, 8192), new(authority, authority));
        NeutralChildDomainLease nested = default;
        bool checkedNested = false;
        injector.BeforeCreateForTest = () =>
        {
            injector.BeforeCreateForTest = null;
            injector.LoseCreateReceipt = fault == "lost";
            injector.ThrowAfterCreate = fault == "throw";
            var inner = adapter.CreateChildDomain(parent, intent);
            injector.LoseCreateReceipt = false;
            injector.ThrowAfterCreate = false;
            Assert.Equal(fault == "none" ? NeutralVirtualizationStatus.Success : NeutralVirtualizationStatus.Ambiguous,
                inner.Status);
            nested = inner.Value;
            checkedNested = true;
        };
        var outer = adapter.CreateChildDomain(parent, intent);
        Assert.True(checkedNested);
        Assert.Equal(2, injector.CreateCalls);
        if (fault == "none")
        {
            Assert.True(outer.IsSuccess, outer.Reason);
            Assert.True(adapter.CloseChildDomain(nested).IsSuccess);
            Assert.True(adapter.CloseChildDomain(outer.Value).IsSuccess);
        }
        else
        {
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous, outer.Status);
            Assert.Contains("continuity", outer.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.CreateChildDomain(parent, intent).Status);
            Assert.Equal(2, injector.CreateCalls);
            Assert.Equal(0, injector.CloseCalls);
        }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("lost")]
    [InlineData("throw")]
    public void ParentBindCallbackCannotReenterCreateAcrossParentEpochs(string fault)
    {
        var runtime = new HybridCpuExternalRuntime();
        var root = DispatchProxy.Create<IHybridCpuExternalRuntime, AdapterBoundaryTests.FaultingRootRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingRootRuntimeProxy)root;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(root, runtime);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory;
        var intent = new NeutralChildDomainIntent(new(1, 8192), new(authority, authority));
        bool checkedAdmission = false;
        injector.BeforeBindForTest = () =>
        {
            injector.BeforeBindForTest = null;
            Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.CreateChildDomain(parent, intent).Status);
            Assert.Equal(NeutralVirtualizationStatus.Denied,
                adapter.CreateChildDomain(parent with { Epoch = new(2) }, intent).Status);
            Assert.Equal(1, injector.BindCalls);
            checkedAdmission = true;
        };
        injector.LoseBindReceipt = fault == "lost";
        injector.ThrowAfterBind = fault == "throw";
        var result = adapter.CreateChildDomain(parent, intent);
        Assert.True(checkedAdmission);
        Assert.Equal(fault == "success" ? NeutralVirtualizationStatus.Success : NeutralVirtualizationStatus.Ambiguous,
            result.Status);
        Assert.Equal(1, injector.BindCalls);
        var pending = (System.Collections.IEnumerable)typeof(HybridCpuExecutableChildAdapter)
            .GetField("pendingParentBinds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        Assert.Empty(pending.Cast<object>());
        if (fault != "success")
        {
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.CreateChildDomain(parent, intent).Status);
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
                adapter.CreateChildDomain(parent with { Epoch = new(2) }, intent).Status);
            Assert.Equal(1, injector.BindCalls);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void ChildStartAndProviderTransitionCannotOverlapByReentry(bool startFirst, bool throwAfter)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var request = PrepareStart(adapter);
        bool checkedAdmission = false;
        if (startFirst)
        {
            injector.AfterStartForTest = () =>
            {
                Assert.Equal(NeutralVirtualizationStatus.Denied,
                    adapter.TransitionChildDomain(request.ChildLease, NeutralChildDomainTransition.Park).Status);
                checkedAdmission = true;
            };
            injector.ThrowAfterStart = throwAfter;
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.StartExecutableArtifact(request).Status);
            Assert.Equal(1, injector.StartCalls);
            Assert.Equal(0, injector.TransitionCalls);
        }
        else
        {
            injector.BeforeTransitionForTest = () =>
            {
                Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.StartExecutableArtifact(request).Status);
                checkedAdmission = true;
            };
            injector.ThrowAfterTransition = throwAfter;
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
                adapter.TransitionChildDomain(request.ChildLease, NeutralChildDomainTransition.Park).Status);
            Assert.Equal(0, injector.StartCalls);
            Assert.Equal(1, injector.TransitionCalls);
        }
        Assert.True(checkedAdmission);
        foreach (string field in new[] { "pendingChildStarts", "pendingChildTransitions" })
        {
            var pending = (System.Collections.IEnumerable)typeof(HybridCpuExecutableChildAdapter)
                .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
            Assert.Empty(pending.Cast<object>());
        }
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.StartExecutableArtifact(request).Status);
        Assert.Equal(startFirst ? 1 : 0, injector.StartCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExecutableStartCallbackCannotAdmitSecondProviderStart(bool throwAfter)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var request = PrepareStart(adapter);
        bool checkedAdmission = false;
        injector.AfterStartForTest = () =>
        {
            injector.AfterStartForTest = null;
            Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.StartExecutableArtifact(request).Status);
            Assert.Equal(NeutralVirtualizationStatus.Denied,
                adapter.StartExecutableArtifact(request with { OperationId = new(18), OperationGeneration = new(4) }).Status);
            Assert.Equal(1, injector.StartCalls);
            checkedAdmission = true;
        };
        injector.ThrowAfterStart = throwAfter;
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.StartExecutableArtifact(request).Status);
        Assert.True(checkedAdmission);
        Assert.Equal(1, injector.StartCalls);
        var pending = (System.Collections.IEnumerable)typeof(HybridCpuExecutableChildAdapter)
            .GetField("pendingChildStarts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        Assert.Empty(pending.Cast<object>());
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.StartExecutableArtifact(request).Status);
        Assert.Equal(1, injector.StartCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InFlightChildTransitionRejectsCloseAndDuplicateProviderTransition(bool throwAfter)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory;
        var child = adapter.CreateChildDomain(parent, new(new(1, 8192), new(authority, authority))).Value;
        bool checkedAdmissions = false;
        injector.BeforeTransitionForTest = () =>
        {
            injector.BeforeTransitionForTest = null;
            Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.CloseChildDomain(child).Status);
            Assert.Equal(NeutralVirtualizationStatus.Denied,
                adapter.TransitionChildDomain(child, NeutralChildDomainTransition.Park).Status);
            Assert.Equal(0, injector.CloseCalls);
            Assert.Equal(1, injector.TransitionCalls);
            checkedAdmissions = true;
        };
        injector.ThrowAfterTransition = throwAfter;
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.TransitionChildDomain(child, NeutralChildDomainTransition.Park).Status);
        Assert.True(checkedAdmissions);
        var pending = (System.Collections.IEnumerable)typeof(HybridCpuExecutableChildAdapter)
            .GetField("pendingChildTransitions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        Assert.Empty(pending.Cast<object>());
        Assert.Equal(0, injector.CloseCalls);
        Assert.Equal(1, injector.TransitionCalls);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.TransitionChildDomain(child, NeutralChildDomainTransition.Resume).Status);
        Assert.Equal(1, injector.TransitionCalls);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("lost")]
    [InlineData("throw")]
    public void InFlightGuestMapRejectsChildCloseBeforeLeasePublication(string fault)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory;
        var child = adapter.CreateChildDomain(parent, new(new(1, 8192), new(authority, authority))).Value;
        var parentMapping = new NeutralOwnedRegionMappingLease(parent,
            new(0, 8192, NeutralMemoryAccess.Read), NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        int refused = 0;
        void CheckClose()
        {
            Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.CloseChildDomain(child).Status);
            Assert.Equal(0, injector.CloseCalls);
            refused++;
        }
        injector.BeforeMapForTest = CheckClose;
        injector.AfterMapForTest = CheckClose;
        injector.LoseMapReceipt = fault == "lost";
        injector.ThrowAfterMap = fault == "throw";
        var result = adapter.MapGuestRegion(new(child, parentMapping, new(0, 8192), NeutralGuestMemoryAccess.Read));
        Assert.Equal(2, refused);
        Assert.Equal(fault == "success" ? NeutralVirtualizationStatus.Success : NeutralVirtualizationStatus.Ambiguous,
            result.Status);
        var pending = (System.Collections.IDictionary)typeof(HybridCpuExecutableChildAdapter)
            .GetField("pendingGuestMaps", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        Assert.Empty(pending);
        if (fault == "success")
        {
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.CloseChildDomain(child).Status);
            Assert.True(adapter.UnmapGuestRegion(result.Value).IsSuccess);
            Assert.True(adapter.CloseChildDomain(child).IsSuccess);
            Assert.Equal(1, injector.CloseCalls);
        }
        else
        {
            Assert.Contains("Guest mapping may exist", adapter.CloseChildDomain(child).Reason);
            Assert.Equal(0, injector.CloseCalls);
        }
    }

    [Fact]
    public void NestedGuestMapCompletionCannotClearOuterAdmissionInterlock()
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory;
        var child = adapter.CreateChildDomain(parent, new(new(1, 8192), new(authority, authority))).Value;
        var parentMapping = new NeutralOwnedRegionMappingLease(parent,
            new(0, 8192, NeutralMemoryAccess.Read), NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        var request = new NeutralGuestMappingRequest(child, parentMapping, new(0, 8192), NeutralGuestMemoryAccess.Read);
        NeutralGuestMappingLease nested = default;
        bool checkedOuter = false;
        injector.BeforeMapForTest = () =>
        {
            injector.BeforeMapForTest = null;
            var inner = adapter.MapGuestRegion(request);
            Assert.True(inner.IsSuccess, inner.Reason);
            nested = inner.Value;
            Assert.True(adapter.UnmapGuestRegion(nested).IsSuccess);
            Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.CloseChildDomain(child).Status);
            Assert.Equal(0, injector.CloseCalls);
            checkedOuter = true;
        };
        var outer = adapter.MapGuestRegion(request);
        Assert.True(checkedOuter);
        Assert.True(outer.IsSuccess, outer.Reason);
        Assert.True(adapter.UnmapGuestRegion(outer.Value).IsSuccess);
        Assert.True(adapter.CloseChildDomain(child).IsSuccess);
        Assert.Equal(2, injector.MapCalls);
        Assert.Equal(2, injector.UnmapCalls);
        Assert.Equal(1, injector.CloseCalls);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("lost")]
    [InlineData("throw")]
    public void InFlightChildCloseRejectsReentrantEffectsAndDuplicateClose(string fault)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory;
        var child = adapter.CreateChildDomain(parent, new(new(1, 8192), new(authority, authority))).Value;
        var parentMapping = new NeutralOwnedRegionMappingLease(parent,
            new(0, 8192, NeutralMemoryAccess.Read), NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        var map = new NeutralGuestMappingRequest(child, parentMapping, new(0, 8192), NeutralGuestMemoryAccess.Read);
        bool checkedAdmissions = false;
        injector.BeforeCloseForTest = () =>
        {
            injector.BeforeCloseForTest = null;
            Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.MapGuestRegion(map).Status);
            Assert.Equal(NeutralVirtualizationStatus.Denied,
                adapter.TransitionChildDomain(child, NeutralChildDomainTransition.BeginDrain).Status);
            Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.CloseChildDomain(child).Status);
            Assert.Equal(0, injector.MapCalls);
            Assert.Equal(1, injector.CloseCalls);
            checkedAdmissions = true;
        };
        injector.LoseCloseReceipt = fault == "lost";
        injector.ThrowAfterClose = fault == "throw";
        var result = adapter.CloseChildDomain(child);
        Assert.True(checkedAdmissions);
        Assert.Equal(fault == "success" ? NeutralVirtualizationStatus.Success : NeutralVirtualizationStatus.Ambiguous,
            result.Status);
        var pending = (System.Collections.IEnumerable)typeof(HybridCpuExecutableChildAdapter)
            .GetField("pendingChildCloses", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        Assert.Empty(pending.Cast<object>());
        Assert.Equal(fault == "success" ? NeutralVirtualizationStatus.Stale : NeutralVirtualizationStatus.Ambiguous,
            adapter.CloseChildDomain(child).Status);
        Assert.False(adapter.MapGuestRegion(map).IsSuccess);
        Assert.Equal(0, injector.MapCalls);
        Assert.Equal(1, injector.CloseCalls);
    }

    [Theory]
    [InlineData("map-return")]
    [InlineData("map-throw")]
    [InlineData("drain-return")]
    public void ChildTransitionReplyCannotOverwriteCallbackOwnerChanges(string fault)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory;
        var child = adapter.CreateChildDomain(parent, new(new(1, 8192), new(authority, authority))).Value;
        bool changed = false;
        injector.BeforeTransitionForTest = () =>
        {
            injector.BeforeTransitionForTest = null;
            if (fault == "drain-return")
                Assert.True(adapter.TransitionChildDomain(child, NeutralChildDomainTransition.BeginDrain).IsSuccess);
            else
            {
                injector.LoseMapReceipt = true;
                var parentMapping = new NeutralOwnedRegionMappingLease(parent,
                    new(0, 8192, NeutralMemoryAccess.Read), NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
                Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.MapGuestRegion(new(child,
                    parentMapping, new(0, 8192), NeutralGuestMemoryAccess.Read)).Status);
            }
            changed = true;
        };
        injector.ThrowAfterTransition = fault == "map-throw";
        var result = adapter.TransitionChildDomain(child, NeutralChildDomainTransition.Park);
        Assert.True(changed);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, result.Status);
        if (fault == "drain-return") Assert.Contains("continuity", result.Reason, StringComparison.OrdinalIgnoreCase);
        else
        {
            Assert.Contains("Guest mapping may exist", adapter.CloseChildDomain(child).Reason);
            Assert.Equal(0, injector.CloseCalls);
        }
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.TransitionChildDomain(child, NeutralChildDomainTransition.Resume).Status);
        Assert.Equal(1, injector.TransitionCalls);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("lost")]
    [InlineData("throw")]
    public void InFlightGuestUnmapRejectsReentrantBindAndDuplicateUnmap(string fault)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory;
        var child = adapter.CreateChildDomain(parent, new(new(1, 8192), new(authority, authority))).Value;
        var parentMapping = new NeutralOwnedRegionMappingLease(parent,
            new(0, 8192, NeutralMemoryAccess.Read), NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        var mapping = adapter.MapGuestRegion(new(child, parentMapping, new(0, 8192),
            NeutralGuestMemoryAccess.Read | NeutralGuestMemoryAccess.Execute)).Value;
        var request = new NeutralExecutableArtifactRequest(child, mapping, CreateExecutablePackage(), 3);
        bool checkedAdmissions = false;
        injector.BeforeUnmapForTest = () =>
        {
            injector.BeforeUnmapForTest = null;
            Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.BindExecutableArtifact(request).Status);
            Assert.Equal(0, injector.ArtifactBindCalls);
            Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.UnmapGuestRegion(mapping).Status);
            Assert.Equal(1, injector.UnmapCalls);
            checkedAdmissions = true;
        };
        injector.LoseUnmapReceipt = fault == "lost";
        injector.ThrowAfterUnmap = fault == "throw";
        var result = adapter.UnmapGuestRegion(mapping);
        Assert.True(checkedAdmissions);
        Assert.Equal(fault == "success" ? NeutralVirtualizationStatus.Success : NeutralVirtualizationStatus.Ambiguous,
            result.Status);
        Assert.Equal(0, injector.ArtifactBindCalls);
        Assert.Equal(1, injector.UnmapCalls);
        var pending = (System.Collections.IEnumerable)typeof(HybridCpuExecutableChildAdapter)
            .GetField("pendingGuestUnmaps", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        Assert.Empty(pending.Cast<object>());
        Assert.False(adapter.BindExecutableArtifact(request).IsSuccess);
        Assert.Equal(0, injector.ArtifactBindCalls);
        Assert.Equal(fault == "success" ? NeutralVirtualizationStatus.Stale : NeutralVirtualizationStatus.Ambiguous,
            adapter.UnmapGuestRegion(mapping).Status);
        Assert.Equal(1, injector.UnmapCalls);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("lost")]
    [InlineData("throw")]
    public void InFlightArtifactBindRejectsReentrantUnmapAndDuplicateBind(string fault)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory;
        var child = adapter.CreateChildDomain(parent, new(new(1, 8192), new(authority, authority))).Value;
        var parentMapping = new NeutralOwnedRegionMappingLease(parent,
            new(0, 8192, NeutralMemoryAccess.Read), NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        var mapping = adapter.MapGuestRegion(new(child, parentMapping, new(0, 8192),
            NeutralGuestMemoryAccess.Read | NeutralGuestMemoryAccess.Execute)).Value;
        var request = new NeutralExecutableArtifactRequest(child, mapping, CreateExecutablePackage(), 3);
        bool checkedBothAdmissions = false;
        injector.BeforeArtifactBindForTest = () =>
        {
            injector.BeforeArtifactBindForTest = null;
            Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.UnmapGuestRegion(mapping).Status);
            Assert.Equal(0, injector.UnmapCalls);
            Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.BindExecutableArtifact(request).Status);
            Assert.Equal(1, injector.ArtifactBindCalls);
            checkedBothAdmissions = true;
        };
        injector.LoseBindReceipt = fault == "lost";
        injector.ThrowAfterBind = fault == "throw";
        var result = adapter.BindExecutableArtifact(request);
        Assert.Equal(fault == "success" ? NeutralVirtualizationStatus.Success : NeutralVirtualizationStatus.Ambiguous,
            result.Status);
        Assert.Equal(0, injector.UnmapCalls);
        Assert.Equal(1, injector.ArtifactBindCalls);
        Assert.True(checkedBothAdmissions);
        var pending = (System.Collections.IEnumerable)typeof(HybridCpuExecutableChildAdapter)
            .GetField("pendingArtifactBinds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        Assert.Empty(pending.Cast<object>());
        Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.UnmapGuestRegion(mapping).Status);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("lost")]
    [InlineData("throw")]
    [InlineData("nested-map-throw")]
    public void ArtifactBindReplyAfterReentrantDrainCannotPublishReceipt(string fault)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory;
        var child = adapter.CreateChildDomain(parent, new(new(1, 8192), new(authority, authority))).Value;
        var parentMapping = new NeutralOwnedRegionMappingLease(parent,
            new(0, 8192, NeutralMemoryAccess.Read), NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        var mapping = adapter.MapGuestRegion(new(child, parentMapping, new(0, 8192),
            NeutralGuestMemoryAccess.Read | NeutralGuestMemoryAccess.Execute)).Value;
        injector.AfterArtifactBindForTest = () =>
        {
            if (fault == "nested-map-throw")
            {
                injector.LoseMapReceipt = true;
                Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.MapGuestRegion(new(child,
                    parentMapping, new(0, 8192), NeutralGuestMemoryAccess.Read)).Status);
            }
            else Assert.True(adapter.TransitionChildDomain(child, NeutralChildDomainTransition.BeginDrain).IsSuccess);
        };
        injector.LoseBindReceipt = fault == "lost";
        injector.ThrowAfterBind = fault is "throw" or "nested-map-throw";
        var result = adapter.BindExecutableArtifact(new(child, mapping, CreateExecutablePackage(), 3));
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, result.Status);
        if (fault is "success" or "lost") Assert.Contains("continuity", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.UnmapGuestRegion(mapping).Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.CloseChildDomain(child).Status);
        Assert.Equal(0, injector.CloseCalls);
        if (fault == "nested-map-throw") Assert.Contains("Guest mapping may exist", adapter.CloseChildDomain(child).Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MappingReplyAfterReentrantDrainCannotPublishLease(bool lostReceipt)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory;
        var child = adapter.CreateChildDomain(parent, new(new(1, 8192), new(authority, authority))).Value;
        injector.AfterMapForTest = () => Assert.True(adapter.TransitionChildDomain(child,
            NeutralChildDomainTransition.BeginDrain).IsSuccess);
        injector.LoseMapReceipt = lostReceipt;
        var parentMapping = new NeutralOwnedRegionMappingLease(parent,
            new(0, 8192, NeutralMemoryAccess.Read), NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        var result = adapter.MapGuestRegion(new(child, parentMapping, new(0, 8192), NeutralGuestMemoryAccess.Read));
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, result.Status);
        Assert.Contains("continuity", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Guest mapping may exist", adapter.CloseChildDomain(child).Reason);
        Assert.Equal(0, injector.CloseCalls);
    }

    [Fact]
    public void StartExceptionPreservesGuestMapUncertaintyCreatedDuringCallback()
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        injector.ThrowAfterStart = true;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var request = PrepareStart(adapter);
        injector.AfterStartForTest = () =>
        {
            injector.LoseMapReceipt = true;
            var parentMapping = new NeutralOwnedRegionMappingLease(request.ChildLease.ParentLease,
                new(0, 8192, NeutralMemoryAccess.Read), NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
            Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
                adapter.MapGuestRegion(new(request.ChildLease, parentMapping,
                    new(0, 8192), NeutralGuestMemoryAccess.Read)).Status);
        };
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.StartExecutableArtifact(request).Status);
        var records = (System.Collections.IDictionary)typeof(HybridCpuExecutableChildAdapter)
            .GetField("children", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        var child = records[request.ChildLease.Handle]!;
        Assert.True((bool)child.GetType().GetProperty("GuestMapMayHaveEffect")!.GetValue(child)!);
        Assert.Contains("Guest mapping may exist", adapter.CloseChildDomain(request.ChildLease).Reason);
    }

    [Fact]
    public void ProviderStartReentryCannotPublishAgainstChangedChildLifecycle()
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        NeutralGuestMappingLease mapping = default;
        var request = PrepareStart(adapter, lease => mapping = lease);
        injector.AfterStartForTest = () => Assert.True(adapter.TransitionChildDomain(request.ChildLease,
            NeutralChildDomainTransition.BeginDrain).IsSuccess);
        var result = adapter.StartExecutableArtifact(request);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, result.Status);
        Assert.Contains("continuity", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, injector.StartCalls);
        Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.UnmapGuestRegion(mapping).Status);
    }

    [Fact]
    public void TraceCallbackRunsAfterStartDecisionOutsideOwnerLock()
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3, AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var sink = new ReentrantSink();
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy, sink);
        var request = PrepareStart(adapter);
        var completedOutsideLock = false;
        var callsAtObservation = 0;
        sink.Callback = () =>
        {
            callsAtObservation = injector.StartCalls;
            var otherThread = Task.Run(() => adapter.TransitionChildDomain(request.ChildLease,
                NeutralChildDomainTransition.BeginDrain));
            completedOutsideLock = otherThread.Wait(TimeSpan.FromSeconds(2));
        };
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, adapter.StartExecutableArtifact(request).Status);
        Assert.True(completedOutsideLock);
        Assert.Equal(1, callsAtObservation);
        Assert.Equal(1, injector.StartCalls);
    }

    private sealed class ReentrantSink : ISemanticTraceSinkV1
    {
        internal Action? Callback;
        public bool TryRecord(SemanticTraceEventV1 item)
        {
            var callback = Callback;
            Callback = null;
            callback?.Invoke();
            return true;
        }
    }

    [Fact]
    public void BoundArtifactKeepsExactGuestMappingUntilReleaseEvidenceExists()
    {
        var adapter = new HybridCpuExecutableChildAdapter();
        NeutralGuestMappingLease mapping = default;
        var request = PrepareStart(adapter, lease => mapping = lease);

        Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.UnmapGuestRegion(mapping).Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.StartExecutableArtifact(request).Status);
        Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.UnmapGuestRegion(mapping).Status);
    }

    [Fact]
    public void DrainingChildCannotBindOrStartExecutableArtifact()
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3,
            AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        NeutralGuestMappingLease mapping = default;
        var request = PrepareStart(adapter, lease => mapping = lease);
        Assert.Equal(1, injector.ArtifactBindCalls);
        Assert.True(adapter.TransitionChildDomain(request.ChildLease,
            NeutralChildDomainTransition.BeginDrain).IsSuccess);

        Assert.Equal(NeutralVirtualizationStatus.Denied,
            adapter.BindExecutableArtifact(new(request.ChildLease, mapping,
                CreateExecutablePackage(), 3)).Status);
        Assert.Equal(NeutralVirtualizationStatus.Denied,
            adapter.StartExecutableArtifact(request).Status);
        Assert.Equal(1, injector.ArtifactBindCalls);
        Assert.Equal(0, injector.StartCalls);
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("forged")]
    [InlineData("digest")]
    [InlineData("throw")]
    public void UnreceiptedExternalArtifactBindPinsExactGuestMapping(string fault)
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3,
            AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        injector.LoseBindReceipt = fault == "lost";
        injector.ForgeBind = fault == "forged";
        injector.ForgeBindDigest = fault == "digest";
        injector.ThrowAfterBind = fault == "throw";
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy);
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory;
        var child = adapter.CreateChildDomain(parent,
            new(new(1, 8192), new(authority, authority))).Value;
        var parentMapping = new NeutralOwnedRegionMappingLease(parent,
            new(0, 8192, NeutralMemoryAccess.Read), NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        var mapping = adapter.MapGuestRegion(new(child, parentMapping,
            new(0, 8192), NeutralGuestMemoryAccess.Read | NeutralGuestMemoryAccess.Execute)).Value;

        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.BindExecutableArtifact(new(child, mapping, CreateExecutablePackage(), 3)).Status);
        Assert.Equal(NeutralVirtualizationStatus.Denied, adapter.UnmapGuestRegion(mapping).Status);
        Assert.Equal(0, injector.UnmapCalls);
    }

    [Fact]
    public void ExecutableStartEmitsCanonicalNonAuthoritativeLifecycleDirectly()
    {
        var sink = new CollectingSink();
        var adapter = new HybridCpuExecutableChildAdapter(sink);
        var request = PrepareStart(adapter);

        var result = adapter.StartExecutableArtifact(request);

        Assert.False(result.IsSuccess);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, result.Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.StartExecutableArtifact(request).Status);
        Assert.Equal(
            [SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
             SemanticTraceEventKindV1.Quarantined],
            sink.Events.Select(static item => item.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.All(sink.Events, item =>
        {
            Assert.False(item.AuthorizesExecution);
            Assert.False(item.AuthorizesEffect);
            Assert.False(item.AuthorizesPublication);
            Assert.Equal("HybridCPU.ExternalRuntime.V3", item.Source);
        });
    }

    [Fact]
    public void ObservationFailureCannotChangeExecutableOutcome()
    {
        var baselineAdapter = new HybridCpuExecutableChildAdapter();
        var baseline = baselineAdapter.StartExecutableArtifact(PrepareStart(baselineAdapter));
        var observedAdapter = new HybridCpuExecutableChildAdapter(new ThrowingSink());
        var observed = observedAdapter.StartExecutableArtifact(PrepareStart(observedAdapter));

        Assert.Equal(baseline.Status, observed.Status);
        Assert.Equal(baseline.Reason, observed.Reason);
    }

    [Fact]
    public void ForgedSuccessfulStartReceiptIsQuarantinedAndCannotBeRetried()
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3,
            AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        ((AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy).Inner = runtime;
        ((AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy).ForgeStart = true;
        var sink = new CollectingSink();
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy, sink);
        var request = PrepareStart(adapter);

        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.StartExecutableArtifact(request).Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.StartExecutableArtifact(request).Status);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined], sink.Events.Select(static item => item.Kind));
    }

    [Fact]
    public void ThrowingExternalStartPinsChildAndEmitsQuarantineWithoutRetry()
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3,
            AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        var injector = (AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy;
        injector.Inner = runtime;
        injector.ThrowAfterStart = true;
        var sink = new CollectingSink();
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy, sink);
        var request = PrepareStart(adapter);

        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.StartExecutableArtifact(request).Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.StartExecutableArtifact(request).Status);
        Assert.Equal(1, injector.StartCalls);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined], sink.Events.Select(static item => item.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
    }

    [Fact]
    public void ExecutableStartMatchesReferenceQuarantineOwnerProjection()
    {
        var sink = new CollectingSink();
        var adapter = new HybridCpuExecutableChildAdapter(sink);

        var result = adapter.StartExecutableArtifact(PrepareStart(adapter));

        Assert.False(result.IsSuccess);
        SemanticTraceEventKindV1[] referenceKinds =
        [
            SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined,
        ];
        var reference = referenceKinds.Select((kind, index) => new SemanticTraceEventV1(
            SemanticTraceEventV1.CurrentVersion, "reference-child-start:17:3", checked((ulong)index + 1),
            kind, "SingNext.Reference.V1", Digest("reference-admitted-generation"),
            Digest($"reference-evidence:{kind}"))).ToArray();
        var comparison = SemanticTraceDifferentialV1.CompareAllowedProjection(reference, sink.Events,
            Digest("singnext-reference-source-tuple"), Digest("hybridcpu-executable-source-tuple"));

        Assert.True(comparison.IsEquivalent);
        Assert.Null(comparison.Difference);
    }

    private static NeutralChildExecutionStartRequest PrepareStart(HybridCpuExecutableChildAdapter adapter,
        Action<NeutralGuestMappingLease>? onBound = null)
    {
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory;
        var childResult = adapter.CreateChildDomain(parent,
            new(new(1, 8192), new(authority, authority)));
        Assert.True(childResult.IsSuccess, childResult.Reason);
        var parentMapping = new NeutralOwnedRegionMappingLease(parent,
            new(0, 8192, NeutralMemoryAccess.Read), NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        var mapResult = adapter.MapGuestRegion(new(childResult.Value, parentMapping,
            new(0, 8192), NeutralGuestMemoryAccess.Read | NeutralGuestMemoryAccess.Execute));
        Assert.True(mapResult.IsSuccess, mapResult.Reason);
        var artifactResult = adapter.BindExecutableArtifact(new(childResult.Value, mapResult.Value,
            CreateExecutablePackage(), 3));
        Assert.True(artifactResult.IsSuccess, artifactResult.Reason);
        onBound?.Invoke(mapResult.Value);
        return new(childResult.Value, artifactResult.Value, new(17), new(3));
    }

    private static byte[] CreateExecutablePackage()
    {
        Assembly compiler = Assembly.Load("HybridCPU.Compiler.Core");
        Type optionsType = Required("HybridCPU.Compiler.Core.Target.Runtime.HybridCpuRestrictedStartupOptionsV1");
        object options = optionsType.GetProperty("Production", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        object linkOptions = compiler.GetType("HybridCPU.Compiler.Core.Target.Link.HybridCpuStaticLinkOptionsV1")!
            .GetProperty("Production", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        ulong imageBase = (ulong)linkOptions.GetType().GetProperty("ImageBase")!.GetValue(linkOptions)!;
        string optionsDigest = (string)linkOptions.GetType().GetProperty("OptionsDigest")!.GetValue(linkOptions)!;
        byte[] image = new byte[1024];
        string imageDigest = Convert.ToHexStringLower(SHA256.HashData(image));
        string mapDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("p05-hybridcpu-trace")));

        Type sectionType = Required("HybridCPU.Compiler.Core.Target.Link.HybridCpuLinkedSectionV1");
        Type sectionKind = Required("HybridCPU.Compiler.Core.Target.HybridCpuObjectSectionKind");
        object section = Activator.CreateInstance(sectionType, "p05", ".text", Enum.Parse(sectionKind, "Code"),
            imageBase, (ulong)image.Length, 256)!;
        Type symbolType = Required("HybridCPU.Compiler.Core.Target.Link.HybridCpuLinkedSymbolV1");
        Type bindingType = Required("HybridCPU.Compiler.Core.Target.HybridCpuSymbolBinding");
        Type visibilityType = Required("HybridCPU.Compiler.Core.Target.HybridCpuSymbolVisibility");
        object symbol = Activator.CreateInstance(symbolType, "main", "p05", Enum.Parse(bindingType, "Global"),
            Enum.Parse(visibilityType, "Default"), imageBase, 256UL)!;

        Type artifactType = Required("HybridCPU.Compiler.Core.Target.Link.HybridCpuStaticLinkArtifactV1");
        Type statusType = Required("HybridCPU.Compiler.Core.Target.Link.HybridCpuLinkStatusV1");
        object artifact = Activator.CreateInstance(artifactType, Enum.Parse(statusType, "Success"), imageBase, image,
            imageDigest, mapDigest, optionsDigest, ArrayOf(sectionType, section), ArrayOf(symbolType, symbol),
            Empty("HybridCPU.Compiler.Core.Target.Link.HybridCpuAppliedRelocationV1"),
            Empty("HybridCPU.Compiler.Core.Target.Link.HybridCpuLinkDiagnosticV1"))!;
        Type requestType = Required("HybridCPU.Compiler.Core.Target.Runtime.HybridCpuRestrictedStartupRequestV1");
        object request = Activator.CreateInstance(requestType, artifact, "main", 0, null, null)!;
        Type builderType = Required("HybridCPU.Compiler.Core.Target.Runtime.HybridCpuRestrictedImageBuilderV1");
        object built = builderType.GetMethod("Build")!.Invoke(Activator.CreateInstance(builderType), [request, options])!;
        return (byte[])built.GetType().GetProperty("PackageBytes")!.GetValue(built)!;

        Type Required(string name) => compiler.GetType(name) ?? throw new TypeLoadException(name);
        Array Empty(string name) => Array.CreateInstance(Required(name), 0);
        static Array ArrayOf(Type type, object value)
        {
            Array array = Array.CreateInstance(type, 1);
            array.SetValue(value, 0);
            return array;
        }
    }

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class CollectingSink : ISemanticTraceSinkV1
    {
        public List<SemanticTraceEventV1> Events { get; } = [];
        public bool TryRecord(SemanticTraceEventV1 traceEvent)
        {
            Events.Add(traceEvent);
            return true;
        }
    }

    private sealed class ThrowingSink : ISemanticTraceSinkV1
    {
        public bool TryRecord(SemanticTraceEventV1 traceEvent) =>
            throw new InvalidOperationException("observation channel unavailable");
    }
}
