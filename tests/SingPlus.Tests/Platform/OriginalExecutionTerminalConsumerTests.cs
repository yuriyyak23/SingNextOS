using System.Collections;
using System.Reflection;
using HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Tests.Platform;

public sealed class OriginalExecutionTerminalConsumerTests
{
    public class TerminalSpy : ExactNestedGuestConsumerTests.GuestSpy
    {
        public string TerminalFault = "exact";
        public ExternalOriginalChildExecutionStartObservation? StartRaw;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            object? raw = base.Invoke(method, args);
            if (method!.Name == "StartOriginalChildExecution") StartRaw = raw as ExternalOriginalChildExecutionStartObservation;
            if (method!.Name != "ResolveOriginalExecutionTerminal") return raw;
            if (TerminalFault == "lost-reply") throw new IOException("Typed terminal reply lost after pure lookup.");
            if (raw is ExternalOriginalExecutionTerminalObservation terminal)
                return TerminalFault switch
                {
                    "copy-request" => terminal with { Request = terminal.Request with { } },
                    "copy-receipt" => terminal with { Receipt = terminal.Receipt with { } },
                    "unknown-kind" => terminal with { Kind = (ExternalExecutionTerminalKind)255 },
                    "missing-result" => terminal with { ProcessExitCode = null },
                    "changed-result" => terminal with { ProcessExitCode = 0 },
                    _ => raw,
                };
            return raw;
        }
    }

    [Theory]
    [InlineData("materialized-entry")]
    [InlineData("finite-branch")]
    [InlineData("linked-call")]
    [InlineData("eh")]
    [InlineData("eh-normal")]
    [InlineData("eh-rethrow")]
    [InlineData("eh-callee")]
    [InlineData("eh-callee-finally")]
    [InlineData("eh-ancestor")]
    [InlineData("eh-nested")]
    [InlineData("eh-replacement")]
    [InlineData("object-field")]
    [InlineData("instance-call")]
    [InlineData("slot-inherited")]
    [InlineData("slot-deep")]
    [InlineData("slot-distinct")]
    [InlineData("nominal")]
    [InlineData("nominal-overload")]
    [InlineData("nominal-result-lost-reply")]
    [InlineData("nominal-result-native-history")]
    [InlineData("reference")]
    [InlineData("reference-null")]
    [InlineData("reference-result-lost-reply")]
    [InlineData("reference-result-native-history")]
    [InlineData("cctor-method")]
    [InlineData("cctor-direct")]
    [InlineData("cctor-result-lost-reply")]
    [InlineData("cctor-result-native-history")]
    [InlineData("failed-cctor")]
    [InlineData("failed-cctor-read-lost-reply")]
    [InlineData("failed-cctor-native-history")]
    [InlineData("failed-cctor-read-local-late-aba")]
    [InlineData("multi-cctor")]
    [InlineData("multi-cctor-result-lost-reply")]
    [InlineData("multi-cctor-result-native-history")]
    [InlineData("multi-cctor-failed")]
    [InlineData("multi-cctor-failed-read-lost-reply")]
    [InlineData("multi-cctor-failed-native-history")]
    [InlineData("slot-deep-result-lost-reply")]
    [InlineData("slot-deep-result-native-history")]
    [InlineData("slot-hidden")]
    [InlineData("slot-null")]
    [InlineData("slot-null-result-lost-reply")]
    [InlineData("slot-null-result-native-history")]
    [InlineData("instance-call-result-lost-reply")]
    [InlineData("instance-call-result-native-history")]
    [InlineData("virtual-dispatch")]
    [InlineData("virtual-dispatch-result-lost-reply")]
    [InlineData("virtual-dispatch-result-native-history")]
    [InlineData("object-field-result-lost-reply")]
    [InlineData("object-field-result-native-history")]
    [InlineData("eh-replacement-result-lost-reply")]
    [InlineData("eh-replacement-result-native-history")]
    [InlineData("eh-nested-foreign")]
    [InlineData("eh-nested-result-lost-reply")]
    [InlineData("eh-nested-result-copy-receipt")]
    [InlineData("eh-nested-result-native-history")]
    [InlineData("eh-nested-result-capability")]
    [InlineData("eh-nested-result-local-late-aba")]
    [InlineData("eh-ancestor-result-lost-reply")]
    [InlineData("eh-ancestor-result-copy-receipt")]
    [InlineData("eh-ancestor-result-native-history")]
    [InlineData("eh-ancestor-result-capability")]
    [InlineData("eh-ancestor-result-local-late-aba")]
    [InlineData("eh-callee-result-lost-reply")]
    [InlineData("eh-callee-result-copy-receipt")]
    [InlineData("eh-callee-result-native-history")]
    [InlineData("eh-callee-result-capability")]
    [InlineData("eh-callee-result-local-late-aba")]
    [InlineData("eh-result-lost-reply")]
    [InlineData("eh-result-copy-receipt")]
    [InlineData("eh-result-native-history")]
    [InlineData("eh-result-capability")]
    [InlineData("eh-result-local-late-aba")]
    [InlineData("managed-array")]
    [InlineData("managed-array-bounds")]
    [InlineData("result-lost-reply")]
    [InlineData("result-copy-receipt")]
    [InlineData("result-missing-result")]
    [InlineData("result-changed-result")]
    [InlineData("result-local-late-aba")]
    [InlineData("result-capability")]
    [InlineData("result-native-history")]
    [InlineData("exact")] [InlineData("reentry")] [InlineData("copy-request")]
    [InlineData("copy-receipt")] [InlineData("unknown-kind")] [InlineData("lost-reply")]
    [InlineData("native-history")]
    [InlineData("sdk-aba")] [InlineData("bridge-aba")] [InlineData("independent-user")]
    [InlineData("read-local-aba")] [InlineData("read-local-marker")] [InlineData("read-local-copy")]
    [InlineData("read-capability")] [InlineData("read-native-history")] [InlineData("read-bridge-aba")]
    [InlineData("read-lost-reply")] [InlineData("read-copy-receipt")]
    [InlineData("read-local-late-aba")]
    public void ActualCpuFailureOutcomePublishesExactTerminalTrapAndIndependentBackingClosure(string mode)
    {
        bool multiCctor = mode == "multi-cctor" || mode.StartsWith("multi-cctor-", StringComparison.Ordinal);
        bool multiFailed = mode == "multi-cctor-failed" || mode.StartsWith("multi-cctor-failed-", StringComparison.Ordinal);
        if (multiCctor) mode = multiFailed ? "failed-cctor" + mode[18..] :
            mode == "multi-cctor" ? "cctor-direct" : "cctor-" + mode[12..];
        bool failedCctor = mode == "failed-cctor" || mode.StartsWith("failed-cctor-", StringComparison.Ordinal);
        if (failedCctor) mode = mode == "failed-cctor" ? "exact" : mode[13..];
        bool normalFinally = mode == "eh-normal";
        string? slotFixture = multiCctor ? multiFailed ? "managed-cctor-multi-failed.hcexe" : "managed-cctor-multi.hcexe" :
            failedCctor ? "managed-cctor-failed.hcexe" : mode switch
        {
            "cctor-method" => "managed-cctor-method.hcexe",
            _ when mode == "cctor-direct" || mode.StartsWith("cctor-result-", StringComparison.Ordinal) => "managed-cctor-direct.hcexe",
            "reference-null" => "managed-slot-reference-null.hcexe",
            _ when mode == "reference" || mode.StartsWith("reference-result-", StringComparison.Ordinal) => "managed-slot-reference.hcexe",
            "nominal-overload" => "managed-slot-nominal-overload.hcexe",
            _ when mode == "nominal" || mode.StartsWith("nominal-result-", StringComparison.Ordinal) => "managed-slot-nominal.hcexe",
            "slot-inherited" => "managed-slot-inherited.hcexe",
            "slot-hidden" => "managed-slot-hidden.hcexe",
            _ when mode == "slot-deep" || mode.StartsWith("slot-deep-", StringComparison.Ordinal) => "managed-slot-deep.hcexe",
            "slot-distinct" => "managed-slot-distinct.hcexe",
            _ when mode == "slot-null" || mode.StartsWith("slot-null-", StringComparison.Ordinal) => "managed-slot-null.hcexe",
            _ => null
        };
        if (slotFixture is not null && !failedCctor) mode = mode.StartsWith("cctor-result-", StringComparison.Ordinal) ? mode[6..] : mode.StartsWith("reference-result-", StringComparison.Ordinal) ? mode[10..] : mode.StartsWith("nominal-result-", StringComparison.Ordinal) ? mode[8..] : mode.StartsWith("slot-null-result-", StringComparison.Ordinal) ||
            mode.StartsWith("slot-deep-result-", StringComparison.Ordinal) ? mode[10..] : "finite-branch";
        bool instanceCall = mode == "instance-call" || mode.StartsWith("instance-call-", StringComparison.Ordinal);
        bool virtualDispatch = mode == "virtual-dispatch" || mode.StartsWith("virtual-dispatch-", StringComparison.Ordinal);
        if (instanceCall) mode = mode == "instance-call" ? "finite-branch" : mode[14..];
        if (virtualDispatch) mode = mode == "virtual-dispatch" ? "finite-branch" : mode[17..];
        bool objectField = mode == "object-field" || mode.StartsWith("object-field-", StringComparison.Ordinal);
        if (objectField) mode = mode == "object-field" ? "finite-branch" : mode[13..];
        bool rethrow = mode == "eh-rethrow";
        bool calleeFinally = mode == "eh-callee-finally";
        bool calleeEh = mode.StartsWith("eh-callee", StringComparison.Ordinal);
        bool ancestorEh = mode == "eh-ancestor" || mode.StartsWith("eh-ancestor-", StringComparison.Ordinal);
        bool nestedForeign = mode == "eh-nested-foreign";
        bool nestedEh = mode == "eh-nested" || mode.StartsWith("eh-nested-", StringComparison.Ordinal);
        bool replacementEh = mode == "eh-replacement" || mode.StartsWith("eh-replacement-", StringComparison.Ordinal);
        bool managedEh = mode == "eh" || mode.StartsWith("eh-", StringComparison.Ordinal);
        if (managedEh) mode = rethrow ? "managed-array-bounds" :
            replacementEh ? mode == "eh-replacement" ? "finite-branch" : mode[15..] :
            nestedEh ? mode.StartsWith("eh-nested-result-", StringComparison.Ordinal) ? mode[10..] : "finite-branch" :
            ancestorEh ? mode == "eh-ancestor" ? "finite-branch" : mode[12..] :
            calleeEh ? mode.StartsWith("eh-callee-result-", StringComparison.Ordinal) ? mode[10..] : "finite-branch" :
            normalFinally || mode == "eh" ? "finite-branch" : mode[3..];
        ulong bytes = slotFixture is not null || instanceCall || virtualDispatch || objectField || managedEh || mode.StartsWith("managed-array") ? 4UL * 1024 * 1024 : 1024 * 1024;
        ExactNestedArtifactConsumerTests.IOriginalNestedStartRuntime native = DispatchProxy.Create<ExactNestedArtifactConsumerTests.IOriginalNestedStartRuntime, TerminalSpy>();
        TerminalSpy spy = (TerminalSpy)(object)native; spy.TerminalFault = mode;
        HybridCpuExecutableChildAdapter sdk = new HybridCpuExecutableChildAdapter(spy.Inner, native);
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), sdk);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 790, 1134);
        ProcessHandle admin = TestFixtures.Create(kernel, 99, 999).Handle;
        CapabilityId budgetCapability = kernel.MintCapability(new(999), admin, ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(admin, budgetCapability, owner, "terminal-cpu",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, bytes * 2), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, bytes),
             new(ServiceBudgetDimension.GuestMemoryBytes, bytes), new(ServiceBudgetDimension.RegionUses, 8)]).Value!.ProcessBudget;
        OwnedBuffer<byte> region = kernel.AllocateBuffer<byte>(owner, (int)bytes).Value!;
        CapabilityId create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet root = kernel.CreateVirtualDomain(owner, create, new(1, (long)bytes * 2)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, root.Domain, root.ConfigureCapability).IsSuccess);
        VirtualDomainAuthoritySet child = kernel.CreateNestedVirtualDomain(owner, root.Domain, root.ConfigureCapability,
            new(new(1, (long)bytes), VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.Execution | VirtualDomainAuthorityClass.GuestMemory | VirtualDomainAuthorityClass.Traps | VirtualDomainAuthorityClass.Events)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, child.Domain, child.ConfigureCapability).IsSuccess);
        CapabilityId memory = kernel.MintCapability(process.DomainId, owner, ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Execute).Value!.CapabilityId;
        KernelResult<GuestRegionMapping> mapped = kernel.MapGuestRegion(owner, child.Domain, child.MemoryCapability, memory, region.Handle, new(0, (long)bytes), GuestMemoryAccess.Read | GuestMemoryAccess.Execute, bytes);
        Assert.True(mapped.IsSuccess, mapped.Message);
        KernelResult bound = kernel.BindVirtualDomainExecutable(owner, child.Domain, child.ExecuteCapability, mapped.Value!.Mapping,
            slotFixture is not null ? ReadPackage(slotFixture) :
            instanceCall ? ReadPackage("managed-instance-call.hcexe") :
            virtualDispatch ? ReadPackage("managed-virtual-dispatch.hcexe") :
            objectField ? ReadPackage("managed-object-field.hcexe") :
            replacementEh ? ReadPackage("managed-finally-replacement.hcexe") :
            nestedEh ? ReadPackage(nestedForeign ? "managed-nested-foreign-rethrow.hcexe" : "managed-nested-rethrow.hcexe") :
            ancestorEh ? ReadPackage("managed-ancestor-rethrow.hcexe") :
            calleeEh ? ReadPackage(calleeFinally ? "managed-callee-finally.hcexe" : "managed-callee-catch-finally.hcexe") :
            managedEh ? ReadPackage(rethrow ? "managed-rethrow.hcexe" : normalFinally ? "managed-finally.hcexe" : "managed-catch-finally.hcexe") :
            mode == "managed-array" ? ReadPackage("managed-array.hcexe") :
            mode == "managed-array-bounds" ? ReadPackage("managed-array-bounds.hcexe") :
            mode == "linked-call" ? ReadPackage("call-tree-materialized.hcexe") :
            mode == "finite-branch" || mode.StartsWith("result-") ? ReadPackage("finite-branch-materialized.hcexe") :
            mode == "materialized-entry" ? ReadPackage("longrun-materialized.hcexe") : ReadPackage(),
            slotFixture is not null || instanceCall || virtualDispatch || objectField || replacementEh || nestedEh ? 80000 : ancestorEh ? 60000 : calleeEh ? 40000 : managedEh ? 20000 : mode == "managed-array-bounds" ? 10000 : mode == "managed-array" ? 1000 : mode == "linked-call" ? 256 : 64);
        Assert.True(bound.IsSuccess, bound.Message);
        object leaf = ((IDictionary)Field(spy.Inner, "childRegistry")).Values.Cast<object>().Single(record => Property(record, "ImmediateParent") is not null);
        ExternalOriginalChildEventRequest? independent = null;
        spy.After = name =>
        {
            if (name != "ResolveOriginalExecutionTerminal") return;
            Assert.False(Monitor.IsEntered(Field(sdk, "sync")));
            Assert.False(Monitor.IsEntered(Field(provider, "_childCreateCommitGate")));
            Assert.False(Monitor.IsEntered(Field(kernel.PlatformAuthority, "_secureDomainLifecycleGate")));
            if (mode == "reentry")
            {
                object original = ((IDictionary)Field(kernel.PlatformAuthority, "_originalChildOwners")).Values.Cast<object>().Single(record => Property(record, "ParentChild") is null);
                Assert.False(kernel.PlatformAuthority.TransitionChildDomain((PlatformChildBinding)Property(original, "Binding")!, global::SingPlus.Platform.PlatformChildDomainTransition.BeginDrain).IsSuccess);
            }
            if (mode == "native-history") ((IList)Property(spy.NativeParent, "OriginalNestedChildren")!).Clear();
            if (mode == "sdk-aba")
            {
                object original = ((IDictionary)Field(sdk, "originalChildren")).Values.Cast<object>().Single(record => Property(record, "NestedParent") is not null);
                original.GetType().GetProperty("State")!.SetValue(original, NeutralChildDomainState.Parked);
                original.GetType().GetProperty("State")!.SetValue(original, NeutralChildDomainState.Created);
            }
            if (mode == "bridge-aba")
            {
                object original = ((IDictionary)Field(kernel.PlatformAuthority, "_originalChildOwners")).Values.Cast<object>().Single(record => Property(record, "ParentChild") is null);
                original.GetType().GetProperty("State")!.SetValue(original, global::SingPlus.Platform.PlatformChildDomainState.Draining);
                original.GetType().GetProperty("State")!.SetValue(original, global::SingPlus.Platform.PlatformChildDomainState.Created);
            }
        };
        KernelResult started = kernel.StartVirtualDomain(owner, child.Domain, child.ExecuteCapability);
        bool readFault = mode.StartsWith("read-");
        bool expectedStart = readFault || mode.StartsWith("result-") || mode is "exact" or "reentry" or "independent-user" or "materialized-entry" or "finite-branch" or "linked-call" or "managed-array" or "managed-array-bounds";
        object? failedResult = null;
        if (expectedStart && !started.IsSuccess)
        {
            object task = Field(Property(leaf, "Execution")!, "run");
            failedResult = Property(task, "Result");
        }
        Assert.True(expectedStart == started.IsSuccess, $"{mode}: {started.Message}; native={spy.StartRaw?.Raw.Outcome}: {spy.StartRaw?.Raw.Reason}; CPU={failedResult}");
        object start = Property(leaf, "OriginalExecutionStart")!;
        ExternalOriginalExecutionTerminalObservation produced = Assert.IsType<ExternalOriginalExecutionTerminalObservation>(Property(start, "TerminalObservation"));
        if (mode.StartsWith("result-") || mode is "finite-branch" or "linked-call" or "managed-array" or "managed-array-bounds")
        {
            Assert.Equal(ExternalExecutionTerminalKind.Completed, produced.Kind);
            Assert.True(produced.Receipt.LastRetireSequence > 5);
            Assert.Equal(VirtualDomainState.Completed, kernel.QueryVirtualDomain(owner, child.Domain).Value);
            Assert.False(kernel.StartVirtualDomain(owner, child.Domain, child.ExecuteCapability).IsSuccess);
            Assert.False(kernel.ParkVirtualDomain(owner, child.Domain, child.ExecuteCapability).IsSuccess);
            Assert.False(kernel.ResumeVirtualDomain(owner, child.Domain, child.ExecuteCapability).IsSuccess);
            Assert.Equal(0, spy.Calls.GetValueOrDefault("TransitionChildDomain"));
            Assert.False(kernel.ObserveVirtualTrap(owner, child.Domain, child.TrapCapability).IsSuccess);
            Assert.Equal(1UL, (ulong)Property(leaf, "ExecutionStartsIssued")!);
            object completedRun = Field(Property(leaf, "Execution")!, "run");
            Assert.Equal(mode == "managed-array-bounds" ? 255 : 42, Property(Property(completedRun, "Result")!, "ProcessExitCode"));
            object completedMemory = Field(Property(leaf, "Execution")!, "memory");
            Assert.True((ulong)Property(completedMemory, "AllocatedDataBytes")! > 0);
            spy.After = null;
            ulong pagesBefore = (ulong)Property(completedMemory, "AllocatedDataBytes")!;
            ulong[] usageBefore = kernel.QueryBudget(budget).Value!.Usage.Select(item => item.Used).ToArray();
            if (slotFixture is not null || instanceCall || virtualDispatch || objectField || calleeEh || ancestorEh || nestedEh || replacementEh)
            {
                Assert.Equal(0UL, pagesBefore % 4096);
                Assert.InRange(pagesBefore, 4096UL, bytes);
                IReadOnlyList<BudgetUsage> quantitative = kernel.QueryBudget(budget).Value!.Usage;
                Assert.Equal(bytes * 2, quantitative.Single(item => item.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
                Assert.Equal(bytes, quantitative.Single(item => item.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
                Assert.Equal(bytes, quantitative.Single(item => item.Dimension == ServiceBudgetDimension.GuestMemoryBytes).Used);
            }
            if (mode.StartsWith("result-"))
            {
                spy.TerminalFault = mode[7..];
                if (mode == "result-capability") Assert.True(kernel.RevokeCapability(child.TrapCapability).IsSuccess);
                if (mode == "result-native-history") spy.After = name =>
                {
                    if (name == "ResolveOriginalExecutionTerminal") ((IList)Property(spy.NativeParent, "OriginalNestedChildren")!).Clear();
                };
                if (mode == "result-local-late-aba") spy.After = name =>
                {
                    if (name != "ResolveOriginalExecutionTerminal") return;
                    object local = ((IDictionary)Field(Field(kernel, "_virtualDomains"), "_records"))[child.Domain.DomainId]!;
                    local.GetType().GetProperty("State")!.SetValue(local, VirtualDomainState.Parked);
                    local.GetType().GetProperty("State")!.SetValue(local, VirtualDomainState.Completed);
                };
                Assert.False(kernel.ObserveVirtualExecutionResult(owner, child.Domain, child.TrapCapability).IsSuccess);
                spy.TerminalFault = "exact"; spy.After = null;
            }
            if (mode is not "result-capability" and not "result-local-late-aba" and not "result-native-history")
            {
                KernelResult<VirtualExecutionResult> result = kernel.ObserveVirtualExecutionResult(owner, child.Domain, child.TrapCapability);
                Assert.True(result.IsSuccess, result.Message);
                Assert.Equal(mode == "managed-array-bounds" ? 255 : 42, result.Value.ProcessExitCode);
                Assert.Equal(result.Value, kernel.ObserveVirtualExecutionResult(owner, child.Domain, child.TrapCapability).Value);
            }
            Assert.Equal(pagesBefore, (ulong)Property(completedMemory, "AllocatedDataBytes")!);
            Assert.Equal(usageBefore, kernel.QueryBudget(budget).Value!.Usage.Select(item => item.Used).ToArray());
            Assert.Equal(1UL, (ulong)Property(leaf, "ExecutionStartsIssued")!);
            spy.After = null;
            if (mode == "result-native-history")
            {
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(record => record.Handle == region.Handle).State);
                Assert.Equal(pagesBefore, (ulong)Property(completedMemory, "AllocatedDataBytes")!);
                Assert.Equal(usageBefore, kernel.QueryBudget(budget).Value!.Usage.Select(item => item.Used).ToArray());
                return;
            }
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(record => record.Handle == region.Handle).State);
            Assert.All(kernel.QueryBudget(budget).Value!.Usage, usage => Assert.Equal(0UL, usage.Used));
            Assert.Equal(0UL, (ulong)Property(completedMemory, "AllocatedDataBytes")!);
            return;
        }
        Assert.Equal(mode == "materialized-entry" ? ExternalExecutionTerminalKind.BudgetExhausted : ExternalExecutionTerminalKind.ExecutionFailure, produced.Kind);
        if (mode == "materialized-entry") Assert.True(produced.Receipt.LastRetireSequence > 5);
        if (started.IsSuccess) Assert.Equal(VirtualDomainState.Faulted, kernel.QueryVirtualDomain(owner, child.Domain).Value);
        Assert.False(kernel.StartVirtualDomain(owner, child.Domain, child.ExecuteCapability).IsSuccess);
        if (mode == "independent-user")
        {
            independent = new((ExternalChildDomainCreateReceipt)Property(leaf, "OriginalAdmission")!, "independent-native-terminal-mailbox", 1,
                new(new(Guid.NewGuid()), new(900)));
            ExternalOriginalChildEventObservation accepted = spy.Inner.InjectOriginalChildEvent(independent)!;
            Assert.False(accepted.NoEffect); Assert.False(accepted.NoFurtherDelivery);
        }
        object privateData = Field(Property(leaf, "Execution")!, "memory");
        Assert.True((ulong)Property(privateData, "AllocatedDataBytes")! > 0);
        ulong beforePages = (ulong)Property(privateData, "AllocatedDataBytes")!;
        ulong[] beforeUsage = kernel.QueryBudget(budget).Value!.Usage.Select(item => item.Used).ToArray();
        if (readFault)
        {
            spy.After = null;
            object localAuthority = Field(kernel, "_virtualDomains");
            IDictionary localRoutes = (IDictionary)Field(localAuthority, "_records");
            object localOwner = localRoutes[child.Domain.DomainId]!;
            if (mode == "read-local-aba")
            {
                localOwner.GetType().GetProperty("State")!.SetValue(localOwner, VirtualDomainState.Parked);
                localOwner.GetType().GetProperty("State")!.SetValue(localOwner, VirtualDomainState.Faulted);
            }
            if (mode == "read-local-marker")
                localOwner.GetType().GetProperty("TerminalPublicationRevision", Private)!.SetValue(localOwner, null);
            if (mode == "read-local-copy")
                localRoutes[child.Domain.DomainId] = localOwner.GetType().GetMethod("MemberwiseClone", Private)!.Invoke(localOwner, null)!;
            if (mode == "read-capability") Assert.True(kernel.RevokeCapability(child.TrapCapability).IsSuccess);
            if (mode == "read-native-history") spy.After = name =>
            {
                if (name == "ResolveOriginalExecutionTerminal") ((IList)Property(spy.NativeParent, "OriginalNestedChildren")!).Clear();
            };
            if (mode == "read-local-late-aba") spy.After = name =>
            {
                if (name != "ResolveOriginalExecutionTerminal") return;
                localOwner.GetType().GetProperty("State")!.SetValue(localOwner, VirtualDomainState.Parked);
                localOwner.GetType().GetProperty("State")!.SetValue(localOwner, VirtualDomainState.Faulted);
            };
            if (mode == "read-bridge-aba")
            {
                object ancestor = ((IDictionary)Field(kernel.PlatformAuthority, "_originalChildOwners")).Values.Cast<object>().Single(record => Property(record, "ParentChild") is null);
                ancestor.GetType().GetProperty("State")!.SetValue(ancestor, global::SingPlus.Platform.PlatformChildDomainState.Draining);
                ancestor.GetType().GetProperty("State")!.SetValue(ancestor, global::SingPlus.Platform.PlatformChildDomainState.Created);
            }
            if (mode == "read-lost-reply") spy.TerminalFault = "lost-reply";
            if (mode == "read-copy-receipt") spy.TerminalFault = "copy-receipt";
        }
        Assert.False(kernel.ResumeVirtualDomain(owner, child.Domain, child.ExecuteCapability).IsSuccess);
        object providerArtifact = ((IDictionary)Field(provider, "_originalProviderArtifacts")).Values.Cast<object>().Single();
        object providerStart = Property(providerArtifact, "OriginalExecution")!;
        object bridgeArtifact = ((IDictionary)Field(kernel.PlatformAuthority, "_originalExecutableArtifactOwners")).Values.Cast<object>().Single();
        object? providerTerminalBefore = Property(providerStart, "TerminalObservation");
        object? bridgeTerminalBefore = Property(bridgeArtifact, "TerminalObservation");
        KernelResult<VirtualTrapObservation> terminalRead = kernel.ObserveVirtualTrap(owner, child.Domain, child.TrapCapability);
        Assert.Equal(started.IsSuccess && !readFault, terminalRead.IsSuccess);
        if (mode is "read-copy-receipt" or "read-lost-reply")
        {
            Assert.Equal(providerTerminalBefore, Property(providerStart, "TerminalObservation"));
            Assert.Equal(bridgeTerminalBefore, Property(bridgeArtifact, "TerminalObservation"));
            Assert.Equal(VirtualDomainState.Faulted, kernel.QueryVirtualDomain(owner, child.Domain).Value);
            spy.TerminalFault = "exact";
            KernelResult<VirtualTrapObservation> recovered = kernel.ObserveVirtualTrap(owner, child.Domain, child.TrapCapability);
            Assert.True(recovered.IsSuccess, recovered.Message);
            Assert.Equal(VirtualTrapKind.ExecutionFailure, recovered.Value!.Kind);
        }
        if (terminalRead.IsSuccess)
        {
            Assert.Equal(mode == "materialized-entry" ? VirtualTrapKind.ExecutionBudgetExhausted : VirtualTrapKind.ExecutionFailure, terminalRead.Value!.Kind);
            Assert.Equal(VirtualDomainState.Faulted, kernel.QueryVirtualDomain(owner, child.Domain).Value);
            KernelResult<VirtualTrapObservation> repeat = kernel.ObserveVirtualTrap(owner, child.Domain, child.TrapCapability);
            Assert.True(repeat.IsSuccess, repeat.Message);
            Assert.Equal(terminalRead.Value, repeat.Value);
        }
        int queries = mode is "read-copy-receipt" or "read-lost-reply" ? 3 : mode is "read-native-history" or "read-local-late-aba" ? 2 :
            started.IsSuccess && !readFault ? 3 : 1;
        Assert.Equal(queries, spy.Calls.GetValueOrDefault("ResolveOriginalExecutionTerminal"));
        Assert.Equal(1UL, (ulong)Property(leaf, "ExecutionStartsIssued")!);
        Assert.Equal(beforePages, (ulong)Property(privateData, "AllocatedDataBytes")!);
        Assert.Equal(beforeUsage, kernel.QueryBudget(budget).Value!.Usage.Select(item => item.Used).ToArray());
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(record => record.Handle == region.Handle).State);
        spy.After = null;
        KernelResult cleanup = kernel.TerminateProcess(owner);
        Assert.Equal(mode is not ("native-history" or "sdk-aba" or "bridge-aba" or "independent-user" or "read-native-history" or "read-bridge-aba" or "read-local-copy"), cleanup.IsSuccess);
        if (cleanup.IsSuccess)
        {
            Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(record => record.Handle == region.Handle).State);
            Assert.All(kernel.QueryBudget(budget).Value!.Usage, usage => Assert.Equal(0UL, usage.Used));
            Assert.Equal(0UL, (ulong)Property(privateData, "AllocatedDataBytes")!);
        }
        else
        {
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(record => record.Handle == region.Handle).State);
            if (mode == "independent-user")
            {
                // Exact artifact/guest closure does not contain this independent mailbox.
                Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(usage => usage.Dimension == ServiceBudgetDimension.GuestMemoryBytes).Used);
                Assert.Equal(bytes, kernel.QueryBudget(budget).Value!.Usage.Single(usage => usage.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
                Assert.Equal(0UL, (ulong)Property(privateData, "AllocatedDataBytes")!);
                Assert.False(spy.Inner.ResolveOriginalChildEvent(independent!)!.NoFurtherDelivery);
                Assert.True(spy.Inner.ContainOriginalChildEvent(independent!)!.NoFurtherDelivery);
                KernelResult retry = kernel.TerminateProcess(owner);
                Assert.True(retry.IsSuccess, retry.Message);
                Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(record => record.Handle == region.Handle).State);
                Assert.All(kernel.QueryBudget(budget).Value!.Usage, usage => Assert.Equal(0UL, usage.Used));
                Assert.Equal(1UL, (ulong)Property(leaf, "ExecutionStartsIssued")!);
            }
            else
            {
                Assert.Equal(bytes, kernel.QueryBudget(budget).Value!.Usage.Single(usage => usage.Dimension == ServiceBudgetDimension.GuestMemoryBytes).Used);
                Assert.True((ulong)Property(privateData, "AllocatedDataBytes")! > 0);
            }
        }
    }

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Field(object owner, string name) => owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static object? Property(object owner, string name) => owner.GetType().GetProperty(name, Private | BindingFlags.Public)!.GetValue(owner);
    // Explicit decoder-negative guest keeps failure publication distinct from the materialized budget-exhaustion guest.
    private static byte[] ReadPackage(string fileName = "decoder-failure.hcexe")
    {
        for (DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "tests", "SingPlus.Tests", "Fixtures", "OriginalExecutionTerminal", "CurrentStartupProfile", fileName);
            if (File.Exists(path)) return File.ReadAllBytes(path);
        }
        throw new FileNotFoundException("Actual compiler-produced terminal execution fixture is missing.");
    }
}


