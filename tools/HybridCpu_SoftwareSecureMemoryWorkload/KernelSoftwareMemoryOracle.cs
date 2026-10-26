using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using HybridCPU.ExternalRuntime;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Diagnostics;

public static class KernelSoftwareMemoryOracle
{
    public static readonly string[] Scenarios = ["positive", "lost-public-reply", "wrong-capability", "storage-budget",
        "revoke-before-publication", "revoke-after-staging", "region-revoke", "cancel-after-staging", "pending-provider-reply",
        "lost-provider-reply", "unknown-provider-query", "kernel-history-loss", "budget-route-loss", "root-aba",
        "cancel-pending-reply", "revoke-pending-reply", "region-snapshot", "region-snapshot-pending", "region-snapshot-lost-reply",
        "region-snapshot-lease-loss", "region-snapshot-storage-route-loss", "region-snapshot-foreign", "region-snapshot-moved",
        "region-snapshot-offset", "region-snapshot-last-word", "region-snapshot-unaligned", "region-snapshot-outside", "region-snapshot-relocated-offset",
        "late-root-copy", "late-root-loss", "late-root-foreign", "late-mapping-copy", "late-mapping-loss", "late-mapping-foreign",
        "domain-rejoin-intact", "domain-rejoin-copy", "domain-rejoin-loss", "domain-rejoin-foreign",
        "domain-rejoin-pending-missing", "domain-rejoin-pending-substitution",
        "domain-rejoin-pending-live-peer", "domain-live-source-loss",
        "domain-rejoin-source-copy", "domain-rejoin-source-clone", "domain-rejoin-source-foreign", "domain-rejoin-source-loss",
        "domain-stale-admission", "domain-stale-reseeded-admission", "domain-stale-departure",
        "domain-stale-full-reseeded-admission", "domain-stale-full-reseeded-departure",
        "transfer-charge-close-buffer", "transfer-charge-close-scalar",
        "session-send-closure-flag",
        "transfer-charge-copy-buffer", "transfer-charge-copy-scalar", "transfer-charge-request-loss-buffer", "transfer-charge-request-loss-scalar",
        "domain-rejoin-departure-copy", "domain-rejoin-departure-loss", "domain-rejoin-departure-foreign",
        "domain-rejoin-outcome-copy", "domain-rejoin-outcome-loss",
        "region-snapshot-teardown-pending", "region-snapshot-teardown-lost", "region-snapshot-teardown-late-drain", "region-snapshot-teardown-history-loss", "region-snapshot-teardown-late-drain-foreign", "region-snapshot-teardown-completion-flag", "region-snapshot-teardown-preparation-foreign", "region-snapshot-teardown-foreign-kernel"];
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private const ulong Word = 0xE102030405060708;
    private static object Field(object owner, string name) => owner.GetType().GetField(name, Hidden)!.GetValue(owner)!;
    private static void Check(bool condition, string? message) { if (!condition) throw new InvalidOperationException(message); }

    public static void Run(string scenario)
    {
        if (scenario.StartsWith("session-send-closure-", StringComparison.Ordinal)) { SessionSendClosureOracle.Run(scenario); return; }
        if (scenario.StartsWith("transfer-charge-", StringComparison.Ordinal)) { TransferChargeClosureOracle.Run(scenario); return; }
        if (scenario.StartsWith("domain-stale-", StringComparison.Ordinal)) { MembershipAdmissionOracle.RunStale(scenario); return; }
        if (scenario == "domain-live-source-loss") { MembershipAdmissionOracle.Run(); return; }
        if (scenario.StartsWith("late-", StringComparison.Ordinal)) { LatePlatformAdmissionOracle.Run(scenario); return; }
        if (scenario.StartsWith("domain-rejoin-", StringComparison.Ordinal)) { DomainReclaimAdmissionOracle.Run(scenario); return; }
        var adapter = new HybridCpuExecutableChildAdapter();
        var provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), adapter);
        var kernel = new RuntimeKernel(provider);
        var manifest = new SingProcessManifestV1(new(931), new(932), 1, "kernel-memory-oracle", ExecutionRole.Sip, MemoryProfile.SipRegion);
        var process = kernel.CreateProcess(manifest).Value!;
        var owner = new ProcessHandle(manifest.ProcessId, manifest.Generation);
        var budgetCap = kernel.MintCapability(process.DomainId, owner, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(owner, budgetCap, owner, "kernel-memory", [
            new(ServiceBudgetDimension.ExternalOperations, 1), new(ServiceBudgetDimension.OwnedMemoryBytes, scenario == "storage-budget" ? 4096UL : 8192UL),
            new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 4096), new(ServiceBudgetDimension.GuestMemoryBytes, 4096), new(ServiceBudgetDimension.RegionUses, 8)]);
        Check(budget.IsSuccess, budget.Message);
        var buffer = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        bool teardownScenario = scenario.StartsWith("region-snapshot-teardown-", StringComparison.Ordinal);
        SingPlus.Sip.OwnedBuffer<byte>? foreign = null;
        ProcessHandle foreignOwner = default;
        if (teardownScenario)
        {
            var foreignManifest = new SingProcessManifestV1(new(941), new(942), 1, "foreign-teardown", ExecutionRole.Sip, MemoryProfile.SipRegion);
            Check(kernel.CreateProcess(foreignManifest).IsSuccess, "foreign process admitted");
            foreignOwner = new(foreignManifest.ProcessId, foreignManifest.Generation);
            foreign = kernel.AllocateBuffer<byte>(foreignOwner, 16).Value!;
            foreign.Span[0] = 0x7D;
        }
        var create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var vm = kernel.CreateVirtualDomain(owner, create, new(1, scenario == "region-snapshot-relocated-offset" ? 12288L : 4096L)).Value;
        Check(kernel.ConfigureVirtualDomain(owner, vm.Domain, vm.ConfigureCapability).IsSuccess, "configured original child");
        var region = kernel.MintCapability(process.DomainId, owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        ulong guestBase = scenario == "region-snapshot-relocated-offset" ? 8192UL : 0;
        var mapping = kernel.MapGuestRegion(owner, vm.Domain, vm.MemoryCapability, region, buffer.Handle, new(guestBase, 4096), GuestMemoryAccess.Read);
        Check(mapping.IsSuccess, mapping.Message);
        var execute = kernel.MintCapability(process.DomainId, owner, ResourceKind.SecureCompute,
            scenario == "wrong-capability" ? RuntimeKernel.SoftwareSecureRegisterLocalResource(vm.Domain) : RuntimeKernel.SoftwareSecureInitializedLoadResource(vm.Domain),
            CapabilityRights.Execute).Value!.CapabilityId;
        byte[] bytes = new byte[4096]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, Word);
        bool regionSnapshot = scenario.StartsWith("region-snapshot", StringComparison.Ordinal);
        if (regionSnapshot) { bytes.CopyTo(buffer.Span); Array.Fill(bytes, (byte)0x55); }
        int snapshotOffset = scenario switch { "region-snapshot-offset" or "region-snapshot-relocated-offset" => 8, "region-snapshot-last-word" => 4088,
            "region-snapshot-unaligned" => 1, "region-snapshot-outside" => 4096, _ => 0 };
        int snapshotLength = snapshotOffset == 0 ? 4096 : 8;
        if (scenario is "region-snapshot-offset" or "region-snapshot-last-word" or "region-snapshot-relocated-offset")
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.Span, ~Word);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.Span[snapshotOffset..], Word);
        }
        if (scenario is "region-snapshot-unaligned" or "region-snapshot-outside")
        {
            var denied = kernel.ExecuteSoftwareSecureRegionSnapshotLoad(owner, vm.Domain, vm.ConfigureCapability, execute,
                mapping.Value.Mapping, buffer, snapshotLength, snapshotOffset);
            Check(!denied.IsSuccess && ((IDictionary)Field(adapter, "softwareMemorySources")).Count == 0 &&
                ((IList)Field(kernel, "_originalKernelInitializedLoads")).Count == 0,
                "invalid subrange fails before local reservation or native mint");
            Check(BinaryPrimitives.ReadUInt64LittleEndian(buffer.Span) == Word, "range refusal preserves original buffer access/content");
            Check(kernel.CloseGuestRegionMapping(owner, vm.Domain, vm.MemoryCapability, mapping.Value.Mapping).IsSuccess &&
                kernel.DestroyVirtualDomain(owner, vm.Domain, vm.ConfigureCapability).IsSuccess && kernel.TerminateProcess(owner).IsSuccess,
                "invalid subrange independent cleanup");
            Console.WriteLine($"PASS kernel-memory={scenario} nativeCreate=0 originalBytesPreserved=true"); return;
        }
        if (scenario is "region-snapshot-foreign" or "region-snapshot-moved")
        {
            var supplied = scenario == "region-snapshot-moved" ? buffer.Move() :
                (SingPlus.Sip.OwnedBuffer<byte>)typeof(object).GetMethod("MemberwiseClone", Hidden)!.Invoke(buffer, null)!;
            var denied = kernel.ExecuteSoftwareSecureRegionSnapshotLoad(owner, vm.Domain, vm.ConfigureCapability, execute,
                mapping.Value.Mapping, supplied, 4096);
            Check(!denied.IsSuccess && ((IDictionary)Field(adapter, "softwareMemorySources")).Count == 0 &&
                ((IList)Field(kernel, "_originalKernelInitializedLoads")).Count == 0,
                "foreign or unadopted moved buffer cannot create native source or Kernel operation");
            var live = scenario == "region-snapshot-moved" ? supplied : buffer;
            Check(BinaryPrimitives.ReadUInt64LittleEndian(live.Span) == Word, "refusal preserves actual original storage and leaves it unreserved");
            Check(kernel.CloseGuestRegionMapping(owner, vm.Domain, vm.MemoryCapability, mapping.Value.Mapping).IsSuccess,
                "foreign/moved refusal does not pin mapping");
            Check(kernel.DestroyVirtualDomain(owner, vm.Domain, vm.ConfigureCapability).IsSuccess && kernel.TerminateProcess(owner).IsSuccess,
                "foreign/moved refusal independent original cleanup");
            Console.WriteLine($"PASS kernel-memory={scenario} nativeCreate=0 originalBufferPreserved=true"); return;
        }
        var history = (IList)Field(kernel, "_originalKernelInitializedLoads");
        var routes = (IDictionary)Field(kernel, "_kernelInitializedLoads");
        ExternalOperationHandle OriginalOperation() => (ExternalOperationHandle)Field(history[0]!, "Operation");
        ulong Used(ServiceBudgetDimension dimension) => kernel.QueryBudget(budget.Value.ProcessBudget).Value!.Usage.Single(x => x.Dimension == dimension).Used;
        void Held()
        {
            Check(Used(ServiceBudgetDimension.OwnedMemoryBytes) == 8192 && Used(ServiceBudgetDimension.ExternalOperations) == 1,
                "original private storage and external-operation charges held");
            var external = kernel.QueryExternalOperation(owner, OriginalOperation());
            Check(external.IsSuccess && external.Value!.Admission!.RegionUses.Count == 1 &&
                external.Value.Admission.RegionUses[0].Region == buffer.Handle && external.Value.Admission.RegionUses[0].Mode == RegionUseMode.ReadOnly,
                "actual original Region read-only use acquired");
        }
        Action? repair = null;
        if (scenario == "positive") Hook("BeforeSoftwareSecureInitializedLoadPublication", () =>
        {
            Console.WriteLine("DIAGNOSTIC before-publication hook entered");
            var source = (KernelResult)kernel.GetType().GetMethod("CommitKernelInitializedLoadSource", Hidden)!
                .Invoke(kernel, [history[0], (Func<KernelResult>)KernelResult.Ok, true, true])!;
            Console.WriteLine($"DIAGNOSTIC publicationSource={source.Error} {source.Message} owned={Used(ServiceBudgetDimension.OwnedMemoryBytes)} external={Used(ServiceBudgetDimension.ExternalOperations)}");
            Check(source.IsSuccess, $"original publication source: {source.Error} {source.Message}");
            Held();
        });
        if (scenario == "revoke-before-publication")
            Hook("BeforeSoftwareSecureInitializedLoadPublication", () => { Held(); Check(kernel.RevokeCapability(execute).IsSuccess, "execute revoke"); });
        if (scenario is "revoke-after-staging" or "region-revoke" or "cancel-after-staging" or "kernel-history-loss" or "budget-route-loss" or "root-aba" or "region-snapshot-lease-loss" or "region-snapshot-storage-route-loss")
            Hook("AfterSoftwareSecureInitializedLoadStaging", () =>
            {
                Held();
                var fact = history[0]!;
                Check(Field(fact, "Published") is null, "private candidate is not OS result");
                if (scenario == "revoke-after-staging") Check(kernel.RevokeCapability(execute).IsSuccess, "execute revoke after staging");
                if (scenario == "region-revoke") Check(kernel.RevokeCapability(region).IsSuccess, "Region capability revoke");
                if (scenario == "cancel-after-staging") Check(kernel.CancelSoftwareSecureInitializedLoad(owner, OriginalOperation()).IsSuccess, "original cancellation withdraws result");
                if (scenario == "kernel-history-loss") { history.Clear(); repair = () => history.Add(fact); }
                if (scenario == "region-snapshot-lease-loss")
                {
                    var leaseField = fact.GetType().GetField("OriginalBufferLease", Hidden)!;
                    var original = leaseField.GetValue(fact); leaseField.SetValue(fact, null);
                    repair = () => leaseField.SetValue(fact, original);
                }
                if (scenario == "region-snapshot-storage-route-loss")
                {
                    var storage = Field(buffer, "_storage");
                    var slot = storage.GetType().GetField("_runtimeReservation", Hidden)!;
                    var original = slot.GetValue(storage); slot.SetValue(storage, null);
                    repair = () => slot.SetValue(storage, original);
                }
                if (scenario == "budget-route-loss")
                {
                    var charge = (BudgetReservationHandle)Field(fact, "Charge");
                    var reservations = (IDictionary)Field(kernel.Budgets, "_reservations");
                    var original = reservations[charge.ReservationId]!; reservations.Remove(charge.ReservationId);
                    repair = () => reservations.Add(charge.ReservationId, original);
                }
                if (scenario == "root-aba")
                {
                    var guest = ((IDictionary)Field(kernel.PlatformAuthority, "_guestBindings")).Values.Cast<object>().Single();
                    var admission = guest.GetType().GetProperty("AdmissionSource")!.GetValue(guest)!;
                    var root = admission.GetType().GetProperty("Root")!.GetValue(admission)!;
                    var state = root.GetType().GetProperty("AuthorityState")!;
                    var original = state.GetValue(root); state.SetValue(root, original); state.SetValue(root, original);
                }
            });

        var proxyRuntime = DispatchProxy.Create<BridgeSoftwareMemoryOracle.IBridgeProvider, BridgeSoftwareMemoryOracle.BridgeProviderProxy>();
        var proxy = (BridgeSoftwareMemoryOracle.BridgeProviderProxy)(object)proxyRuntime; proxy.Target = provider;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var providerField = kernel.PlatformAuthority.GetType().GetField("_provider", Hidden)!;
        bool teardown = teardownScenario;
        bool pendingReply = scenario is "pending-provider-reply" or "cancel-pending-reply" or "revoke-pending-reply" or "region-snapshot-pending" or "region-snapshot-teardown-pending";
        if (scenario is "lost-provider-reply" or "unknown-provider-query" or "region-snapshot-lost-reply" || pendingReply || teardown)
        {
            proxy.After = name =>
            {
                if (name == nameof(IPlatformSoftwareSecureMemoryReadProvider.ResolveSoftwareSecureMemoryRead) && (scenario == "unknown-provider-query" || teardown && !pendingReply))
                    throw new IOException("Original provider query unavailable.");
                if (name != nameof(IPlatformSoftwareSecureMemoryReadProvider.ExecuteSoftwareSecureMemoryRead)) return;
                Held();
                if (regionSnapshot)
                {
                    bool blocked = false; try { _ = buffer.Span.Length; } catch (InvalidOperationException) { blocked = true; }
                    Check(blocked, "original Region buffer remains runtime-reserved while provider reply is pending/unknown");
                }
                if (scenario is "lost-provider-reply" or "region-snapshot-lost-reply" || teardown && !pendingReply) throw new IOException("Lost reply after actual native retire and source closure.");
                if (pendingReply) { entered.Set(); Check(release.Wait(TimeSpan.FromSeconds(10)), "bounded original provider reply gate"); }
            };
            providerField.SetValue(kernel.PlatformAuthority, proxyRuntime);
        }
        KernelResult<SoftwareSecureInitializedLoadResult> observed;
        if (pendingReply)
        {
            var executing = Task.Run(Execute);
            try
            {
                Check(entered.Wait(TimeSpan.FromSeconds(10)), "actual provider load reached pending reply");
                var operation = OriginalOperation(); Held();
                Check(!kernel.ResolveSoftwareSecureInitializedLoad(owner, operation).IsSuccess, "active resolve cannot reuse original invocation");
                Check(!kernel.ReleaseExternalOperation(owner, operation, new(true, false)).IsSuccess, "caller closed flag cannot release pending original custody");
                Check(!kernel.CloseGuestRegionMapping(owner, vm.Domain, vm.MemoryCapability, mapping.Value.Mapping).IsSuccess, "pending source blocks actual mapping close");
                if (scenario == "cancel-pending-reply")
                {
                    Check(!kernel.CancelSoftwareSecureInitializedLoad(new(owner.ProcessId, owner.Generation + 1), operation).IsSuccess,
                        "foreign process cannot cancel original load");
                    Check(kernel.CancelSoftwareSecureInitializedLoad(owner, operation).IsSuccess, "pending original cancellation accepted");
                    Held();
                    Check(!kernel.ReleaseExternalOperation(owner, operation, new(true, false)).IsSuccess,
                        "cancellation does not certify source closure or release custody");
                }
                if (scenario == "revoke-pending-reply")
                {
                    Check(kernel.RevokeCapability(execute).IsSuccess, "revoke original pending execution capability");
                    Held();
                }
                if (scenario == "region-snapshot-teardown-pending")
                {
                    Check(!kernel.TerminateProcess(owner).IsSuccess, "teardown cannot reclaim during original callback");
                    var stop = kernel.QueryProcessTeardown(owner).Value;
                    Check(stop.Phase == ProcessTeardownPhase.PlatformDraining && stop.LocalAuthorizationRevoked && !stop.LocalReclaimCompleted,
                        "pending original snapshot drains after local revoke without permanent fault/reclaim");
                    Held();
                }
            }
            finally { release.Set(); }
            Check(executing.Wait(TimeSpan.FromSeconds(10)), "bounded original Kernel execution completion");
            observed = executing.GetAwaiter().GetResult();
        }
        else observed = Execute();
        Check(observed.IsSuccess, observed.Message);
        var result = observed.Value!;
        if (teardown)
        {
            if (scenario == "region-snapshot-teardown-foreign-kernel")
            {
                Check(!result.ResourcesClosed && !kernel.TerminateProcess(owner).IsSuccess, "original unknown load retains stop custody");
                var routesForStop = (IDictionary)Field(kernel, "_processTeardowns");
                var originalStop = routesForStop[owner]!;
                var otherKernel = new RuntimeKernel();
                Check(otherKernel.CreateProcess(manifest).IsSuccess, "independent Kernel admits same numeric process identity");
                var otherHook = otherKernel.GetType().GetProperty("AfterInitializedLoadDrainBeforeTeardownCommit", Hidden)!;
                otherHook.SetValue(otherKernel, (Action<ProcessHandle>)(_ => throw new IOException("Retain genuinely minted foreign stop before preparation.")));
                try { otherKernel.TerminateProcess(owner); } catch (IOException) { }
                otherHook.SetValue(otherKernel, null);
                var foreignStop = ((IDictionary)Field(otherKernel, "_processTeardowns"))[owner]!;
                var foreignBefore = otherKernel.QueryProcessTeardown(owner).Value;
                Check((bool)foreignStop.GetType().GetProperty("IsOriginal")!.GetValue(foreignStop)!, "foreign record is genuinely minted, not a clone");
                routesForStop[owner] = foreignStop;
                Check(kernel.QueryProcessTeardown(owner).Error == KernelError.StaleGeneration &&
                    kernel.ObserveProcessTeardown(owner).Error == KernelError.StaleGeneration &&
                    kernel.TerminateProcess(owner).Error == KernelError.StaleGeneration,
                    "same-handle record from a foreign Kernel cannot enter Query/Observe/Begin");
                Check(otherKernel.QueryProcessTeardown(owner).Value == foreignBefore, "refusal does not mutate the foreign original owner");
                Held(); routesForStop[owner] = originalStop;
                Check(otherKernel.TerminateProcess(owner).IsSuccess, "independent foreign Kernel cleanup remains usable");
                proxy.After = null;
            }
            if (scenario == "region-snapshot-teardown-preparation-foreign")
            {
                proxy.After = null; providerField.SetValue(kernel.PlatformAuthority, provider);
                var stopRoutes = (IDictionary)Field(kernel, "_processTeardowns");
                object? originalStop = null, copiedStop = null;
                var preparationHook = kernel.GetType().GetProperty("AfterComposedPreparationBeforeTeardownCommit", Hidden)!;
                preparationHook.SetValue(kernel, (Action<ProcessHandle>)(_ =>
                {
                    originalStop = stopRoutes[owner]!;
                    copiedStop = typeof(object).GetMethod("MemberwiseClone", Hidden)!.Invoke(originalStop, null)!;
                    stopRoutes[owner] = copiedStop;
                }));
                var denied = kernel.TerminateProcess(owner);
                preparationHook.SetValue(kernel, null);
                Check(denied.Error == KernelError.StaleGeneration && originalStop is not null && copiedStop is not null,
                    "composed callback outcome cannot adopt foreign teardown route");
                Check(!(bool)originalStop!.GetType().GetProperty("ComposedAuthorityPrepared")!.GetValue(originalStop)! &&
                    !(bool)originalStop.GetType().GetProperty("ComposedPreparationInFlight")!.GetValue(originalStop)! &&
                    !(bool)copiedStop!.GetType().GetProperty("ComposedAuthorityPrepared")!.GetValue(copiedStop)!,
                    "foreign outcome commits neither preparation nor foreign interlock release");
                var retainedUsage = kernel.QueryBudget(budget.Value.ProcessBudget).Value!.Usage.ToArray();
                Check(kernel.TerminateProcess(owner).Error == KernelError.StaleGeneration &&
                    kernel.ObserveProcessTeardown(owner).Error == KernelError.StaleGeneration &&
                    kernel.QueryProcessTeardown(owner).Error == KernelError.StaleGeneration,
                    "fresh public calls cannot adopt copied original teardown owner");
                stopRoutes.Remove(owner);
                Check(!kernel.TerminateProcess(owner).IsSuccess && !kernel.ObserveProcessTeardown(owner).IsSuccess &&
                    !kernel.QueryProcessTeardown(owner).IsSuccess,
                    "lost original teardown route cannot recreate cleanup authority");
                Check(kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State == RegionState.Owned &&
                    kernel.QueryBudget(budget.Value.ProcessBudget).Value!.Usage.SequenceEqual(retainedUsage) &&
                    foreign!.IsValid && foreign.Span[0] == 0x7D && proxy.Executes == 1,
                    "copy/loss refusal preserves original Region/charges and independent foreign backing without reissue");
                stopRoutes[owner] = originalStop;
            }
            if (scenario.StartsWith("region-snapshot-teardown-late-drain", StringComparison.Ordinal))
            {
                bool foreignRoute = scenario.EndsWith("-foreign", StringComparison.Ordinal);
                var teardownRoutes = (IDictionary)Field(kernel, "_processTeardowns");
                object? copiedStop = null;
                using var drainEntered = new ManualResetEventSlim(); using var resumeDrain = new ManualResetEventSlim();
                int drains = 0;
                var hook = kernel.GetType().GetProperty("AfterInitializedLoadDrainBeforeTeardownCommit", Hidden)!;
                hook.SetValue(kernel, (Action<ProcessHandle>)(_ =>
                {
                    if (Interlocked.Increment(ref drains) != 1) return;
                    drainEntered.Set(); Check(resumeDrain.Wait(TimeSpan.FromSeconds(10)), "bounded late original drain outcome gate");
                }));
                var late = Task.Run(() => kernel.TerminateProcess(owner));
                object originalStop;
                try
                {
                    Check(drainEntered.Wait(TimeSpan.FromSeconds(10)), "original unknown drain outcome before commit");
                    originalStop = ((IDictionary)Field(kernel, "_processTeardowns"))[owner]!;
                    Held(); proxy.After = null; providerField.SetValue(kernel.PlatformAuthority, provider);
                    if (foreignRoute)
                    {
                        copiedStop = typeof(object).GetMethod("MemberwiseClone", Hidden)!.Invoke(originalStop, null)!;
                        teardownRoutes[owner] = copiedStop;
                    }
                    else Check(kernel.TerminateProcess(owner).IsSuccess, "second actual stop completes original cleanup/reclaim while first outcome paused");
                }
                finally { resumeDrain.Set(); }
                Check(late.Wait(TimeSpan.FromSeconds(10)), "bounded late original stop completion");
                hook.SetValue(kernel, null);
                if (foreignRoute)
                {
                    var copiedSnapshot = (ProcessTeardownSnapshot)copiedStop!.GetType().GetProperty("Snapshot")!.GetValue(copiedStop)!;
                    Check(late.GetAwaiter().GetResult().Error == KernelError.StaleGeneration && copiedSnapshot.Phase == ProcessTeardownPhase.LocalExitStarted && !copiedSnapshot.IsComplete,
                        "late original outcome cannot mutate or adopt foreign teardown route");
                    teardownRoutes[owner] = originalStop;
                    Check(kernel.TerminateProcess(owner).IsSuccess, "exact original owner repair independently completes cleanup");
                }
                var terminal = (ProcessTeardownSnapshot)originalStop.GetType().GetProperty("Snapshot")!.GetValue(originalStop)!;
                Check((foreignRoute || late.GetAwaiter().GetResult().IsSuccess) && terminal.IsComplete && terminal.Phase == ProcessTeardownPhase.PlatformClosed,
                    "late draining cannot regress completed original teardown or replace terminal result");
                var load = history[0]!;
                Check((bool)Field(load, "Closed") && (bool)Field(load, "BufferLeaseReleased") && Field(load, "Published") is null && proxy.Executes == 1,
                    "overlapping stops close original source/lease once without publication or reissue");
                Check(foreign!.IsValid && foreign.Span[0] == 0x7D && kernel.TerminateProcess(foreignOwner).IsSuccess,
                    "late original stop preserves independent foreign storage/process cleanup");
                Console.WriteLine($"PASS kernel-memory={scenario} originalPhase={terminal.Phase} processReclaimed=true providerExecute=1"); return;
            }
            object? completionOwner = null;
            if (scenario is "region-snapshot-teardown-lost" or "region-snapshot-teardown-history-loss" or "region-snapshot-teardown-completion-flag")
            {
                object? originalLoad = null;
                if (scenario == "region-snapshot-teardown-history-loss") { originalLoad = history[0]; history.Clear(); }
                Check(!result.ResourcesClosed && !kernel.TerminateProcess(owner).IsSuccess, "unknown reply retains process cleanup custody");
                var stop = kernel.QueryProcessTeardown(owner).Value;
                Check(stop.Phase == ProcessTeardownPhase.PlatformDraining && !stop.LocalReclaimCompleted, "unknown query is retryable drain, not closure");
                if (scenario == "region-snapshot-teardown-completion-flag")
                {
                    completionOwner = ((IDictionary)Field(kernel, "_processTeardowns"))[owner]!;
                    completionOwner.GetType().GetProperty("LocalReclaimCompleted")!.SetValue(completionOwner, true);
                    Check(!kernel.TerminateProcess(owner).IsSuccess && !kernel.ObserveProcessTeardown(owner).Value.IsComplete,
                        "mutable completion projection cannot certify original snapshot/process closure");
                    Held();
                }
                if (originalLoad is not null)
                {
                    Check(!kernel.ObserveProcessTeardown(owner).Value.IsComplete, "unknown original history cannot advance local reclaim");
                    history.Add(originalLoad);
                }
                Held(); proxy.After = null;
            }
            // The fault proxy only implements the memory seam. Original child/root cleanup
            // must use the actual provider's complete supported interface set.
            providerField.SetValue(kernel.PlatformAuthority, provider);
            var stopped = kernel.ObserveProcessTeardown(owner);
            for (int retry = 0; retry < 8 && stopped.IsSuccess && !stopped.Value.IsComplete && stopped.Value.Phase != ProcessTeardownPhase.PlatformFaulted; retry++)
                stopped = kernel.ObserveProcessTeardown(owner);
            Check(stopped.IsSuccess && stopped.Value.IsComplete, $"actual Observe teardown progresses original cleanup: {stopped.Error} {stopped.Message} state={stopped.Value}");
            var original = history[0]!;
            Check((bool)Field(original, "Closed") && (bool)Field(original, "BufferLeaseReleased") && Field(original, "Published") is null,
                "original operation/lease closed without OS publication after stop");
            var native = ((IDictionary)Field(adapter, "softwareMemorySources")).Values.Cast<object>().Single();
            var source = (SoftwareSecureReadOnlyMemorySource)Field(native, "Source");
            var backend = (SoftwareSecureComputeProvider)Field(native, "Provider");
            Check(proxy.Executes == 1 && source.ReadCount == 1 && source.AllocatedBytes == 0 && source.OutstandingPins == 0 && backend.OutstandingReservations == 0,
                "stop queries close original source without repeated provider execution");
            Check(!buffer.IsValid, "process stop revokes original local buffer access");
            if (completionOwner is not null)
            {
                completionOwner.GetType().GetProperty("LocalReclaimCompleted")!.SetValue(completionOwner, false);
                var completed = (ProcessTeardownSnapshot)completionOwner.GetType().GetProperty("Snapshot")!.GetValue(completionOwner)!;
                Check(completed.IsComplete && completed.Phase == ProcessTeardownPhase.PlatformClosed,
                    "original committed completion survives mutable projection loss");
            }
            foreach (var dimension in new[] { ServiceBudgetDimension.OwnedMemoryBytes, ServiceBudgetDimension.ExternalOperations,
                ServiceBudgetDimension.PinnedMappedMemoryBytes, ServiceBudgetDimension.GuestMemoryBytes, ServiceBudgetDimension.RegionUses })
                Check(Used(dimension) == 0, $"original teardown fully reclaims {dimension}");
            Check(kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State == RegionState.Released,
                "original Region record released after original provider closure");
            Check(foreign!.IsValid && foreign.Span[0] == 0x7D &&
                kernel.Regions.Snapshot().Single(x => x.Handle == foreign.Handle).State == RegionState.Owned,
                "foreign Region/backing remains owned and intact");
            Check(kernel.TerminateProcess(foreignOwner).IsSuccess, "independent foreign process cleanup");
            Console.WriteLine($"PASS kernel-memory={scenario} processReclaimed=true read=1 published=false sourceBytes=0 pins=0"); return;
        }
        if (pendingReply) Check(proxy.Executes == 1, "pending/cancel/revoke query never repeats actual provider execution");
        bool deniedBeforeNative = scenario is "wrong-capability" or "storage-budget";
        bool positive = scenario is "positive" or "lost-public-reply" or "pending-provider-reply" or "region-snapshot" or "region-snapshot-pending" or "region-snapshot-offset" or "region-snapshot-last-word" or "region-snapshot-relocated-offset";
        if (positive && !result.WasPublished)
        {
            var fact = history[0]!;
            var proof = (SoftwareSecureInitializedLoadObservation?)Field(fact, "Observation");
            Console.WriteLine($"DIAGNOSTIC original invoked={Field(fact, "Invoked")} attempted={Field(fact, "PublicationAttempted")} candidate={Field(fact, "Candidate") is not null} nativeClosed={proof?.ResourcesClosed} nativeValue={proof?.Value} machine={proof?.MachineAttempt} retire={proof?.Commit}");
            Console.WriteLine(string.Join(",", kernel.QueryExternalOperation(owner, result.Operation).Value!.Transitions.Select(x => x.Event)));
        }
        if (scenario is "lost-provider-reply" or "unknown-provider-query" or "kernel-history-loss" or "budget-route-loss" or "region-snapshot-lost-reply" or "region-snapshot-lease-loss" or "region-snapshot-storage-route-loss")
        {
            Check(!result.ResourcesClosed && !result.WasPublished && result.Value is null, "unknown original custody retained with no OS result");
            Check(!kernel.CloseGuestRegionMapping(owner, vm.Domain, vm.MemoryCapability, mapping.Value.Mapping).IsSuccess, "unknown original semantic owner pins mapping");
            if (regionSnapshot)
            {
                bool blocked = false; try { _ = buffer.Span.Length; } catch (InvalidOperationException) { blocked = true; }
                Check(blocked, "lost reply retains actual original buffer reservation independently of native pages");
            }
            proxy.After = null; repair?.Invoke(); repair = null;
            result = kernel.ResolveSoftwareSecureInitializedLoad(owner, result.Operation).Value!;
        }
        if (scenario == "lost-public-reply") result = kernel.ResolveSoftwareSecureInitializedLoad(owner, result.Operation).Value!;
        Check(result.ResourcesClosed && result.WasPublished == positive && result.Value == (positive ? Word : null),
            $"separate post-retire OS result: {scenario} closed={result.ResourcesClosed} published={result.WasPublished} value={result.Value}");
        if (positive) Check(result.MachineAttempt > 0 && result.Commit > 0, "original machine retire facts accompany OS result");
        Check(kernel.ResolveSoftwareSecureInitializedLoad(owner, result.Operation).Value == result, "original resolve is historical and does not republish");
        Check(!kernel.ResolveSoftwareSecureInitializedLoad(new(owner.ProcessId, owner.Generation + 1), result.Operation).IsSuccess,
            "foreign owner generation cannot query original result");
        Check(kernel.QueryExternalOperation(owner, result.Operation).Value!.State == ExternalOperationState.Released,
            "original external operation released independently");
        Check(Used(ServiceBudgetDimension.OwnedMemoryBytes) == 4096 && Used(ServiceBudgetDimension.ExternalOperations) == 0,
            "private storage and original operation budget reclaimed");
        if (regionSnapshot) Check(BinaryPrimitives.ReadUInt64LittleEndian(buffer.Span[snapshotOffset..]) == Word,
            "known original cleanup restores actual OS buffer access without changing its bytes");
        var nativeSources = (IDictionary)Field(adapter, "softwareMemorySources");
        Check(nativeSources.Count == (deniedBeforeNative ? 0 : 1), "actual native source mint count");
        if (!deniedBeforeNative)
        {
            var native = nativeSources.Values.Cast<object>().Single();
            var storage = (SoftwareSecureReadOnlyMemorySource)Field(native, "Source");
            var backend = (SoftwareSecureComputeProvider)Field(native, "Provider");
            if (snapshotOffset != 0)
            {
                var sourceRequest = (NeutralSoftwareSecureMemorySourceRequest)Field(native, "Request");
                var use = kernel.QueryExternalOperation(owner, result.Operation).Value!.Admission!.RegionUses.Single();
                Check(sourceRequest.Address == guestBase + (ulong)snapshotOffset && sourceRequest.InitialBytes.Length == snapshotLength &&
                    use.Range == new RegionUseRange(snapshotOffset, snapshotLength), "actual native address and original Region use match exact snapshot subrange");
            }
            Check(storage.ReadCount == 1 && storage.OutstandingPins == 0 && storage.AllocatedBytes == 0 && backend.OutstandingReservations == 0,
                "actual original source read/page/pin/provider closure");
        }
        providerField.SetValue(kernel.PlatformAuthority, provider);
        Check(kernel.CloseGuestRegionMapping(owner, vm.Domain, vm.MemoryCapability, mapping.Value.Mapping).IsSuccess, "independent original mapping cleanup");
        Check(kernel.DestroyVirtualDomain(owner, vm.Domain, vm.ConfigureCapability).IsSuccess, "independent original child cleanup");
        Check(kernel.TerminateProcess(owner).IsSuccess, "independent original process cleanup");
        Console.WriteLine($"PASS kernel-memory={scenario} read={(deniedBeforeNative ? 0 : 1)} published={positive} privateBytes=0 operationBudget=0");

        KernelResult<SoftwareSecureInitializedLoadResult> Execute() => regionSnapshot
            ? kernel.ExecuteSoftwareSecureRegionSnapshotLoad(owner, vm.Domain, vm.ConfigureCapability, execute, mapping.Value.Mapping, buffer, snapshotLength, snapshotOffset)
            : kernel.ExecuteSoftwareSecureInitializedLoad(owner, vm.Domain, vm.ConfigureCapability, execute, mapping.Value.Mapping, bytes);
        void Hook(string name, Action action) => kernel.GetType().GetProperty(name, Hidden)!.SetValue(kernel, action);
    }
}
