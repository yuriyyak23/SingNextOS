using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Diagnostics;

public static class DomainReclaimAdmissionOracle
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Field(object target, string name) => target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public static void Run(string scenario)
    {
        if (scenario.StartsWith("domain-rejoin-source-", StringComparison.Ordinal)) { RunMembershipSource(scenario); return; }
        if (scenario == "domain-rejoin-pending-live-peer") { RunSharedProjection(); return; }
        var kernel = new RuntimeKernel();
        var manifest = new SingProcessManifestV1(new(971), new(972), 1, "domain-reclaim-original", ExecutionRole.Sip, MemoryProfile.SipRegion);
        Check(kernel.CreateProcess(manifest).IsSuccess, "original process admitted");
        var owner = new ProcessHandle(manifest.ProcessId, manifest.Generation);
        var admin = kernel.MintCapability(manifest.DomainId, owner, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(owner, admin, owner, "domain-reclaim", [new(ServiceBudgetDimension.OwnedMemoryBytes, 8)]).Value;
        var buffer = kernel.AllocateBuffer<byte>(owner, 8).Value!; buffer.Span[0] = 0xA3;
        var use = kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.ReadOnly, new(0, 8));
        Check(use.IsSuccess && kernel.InvalidateRegionUse(owner, use.Value!.Handle).IsSuccess, "original invalidated use retained");
        var peerManifest = new SingProcessManifestV1(new(981), new(982), 1, "domain-reclaim-peer", ExecutionRole.Sip, MemoryProfile.SipRegion);
        Check(kernel.CreateProcess(peerManifest).IsSuccess, "independent peer admitted");
        var peer = new ProcessHandle(peerManifest.ProcessId, peerManifest.Generation);
        var peerBuffer = kernel.AllocateBuffer<byte>(peer, 8).Value!; peerBuffer.Span[0] = 0x7D;
        var stopped = kernel.TerminateProcess(owner);
        Check(!stopped.IsSuccess && !kernel.Domains.Contains(manifest.DomainId) &&
            kernel.Processes.Resolve(owner).Value!.State == ProcessState.Exiting,
            "last membership removed while original use still prevents local reclaim");
        var routes = (IDictionary)Field(kernel, "_processTeardowns");
        var original = routes[owner]!;
        Check((DomainId?)original.GetType().GetProperty("DomainReclaimPending")!.GetValue(original) == manifest.DomainId,
            "existing last-member cleanup owner captured");
        RuntimeKernel? other = null;
        RegionUseHandle? otherUse = null;
        bool canonicalFault = scenario.Contains("-departure-", StringComparison.Ordinal) || scenario.Contains("-outcome-", StringComparison.Ordinal);
        object? originalDeparture = null, originalOutcome = null;
        PropertyInfo? departureProperty = null;
        FieldInfo? outcomeField = null;
        if (canonicalFault)
        {
            departureProperty = original.GetType().GetProperty("DomainDeparture")!;
            originalDeparture = departureProperty.GetValue(original)!;
            outcomeField = originalDeparture.GetType().GetField("Outcome", Hidden)!;
            originalOutcome = outcomeField.GetValue(originalDeparture)!;
            if (scenario.Contains("-outcome-", StringComparison.Ordinal))
                outcomeField.SetValue(originalDeparture, scenario.EndsWith("-loss", StringComparison.Ordinal) ? null :
                    typeof(object).GetMethod("MemberwiseClone", Hidden)!.Invoke(originalOutcome, null));
            else if (scenario.EndsWith("-copy", StringComparison.Ordinal))
                departureProperty.SetValue(original, typeof(object).GetMethod("MemberwiseClone", Hidden)!.Invoke(originalDeparture, null));
            else if (scenario.EndsWith("-loss", StringComparison.Ordinal)) departureProperty.SetValue(original, null);
            else
            {
                other = new RuntimeKernel(); Check(other.CreateProcess(manifest).IsSuccess, "foreign continuation process admitted");
                var otherBuffer = other.AllocateBuffer<byte>(owner, 8).Value!;
                otherUse = other.AcquireRegionUse(owner, otherBuffer.Handle, RegionUseMode.ReadOnly, new(0, 8)).Value!.Handle;
                Check(other.InvalidateRegionUse(owner, otherUse.Value).IsSuccess && !other.TerminateProcess(owner).IsSuccess,
                    "foreign original membership departure retains independent backing");
                var otherRecord = ((IDictionary)Field(other, "_processTeardowns"))[owner]!;
                departureProperty.SetValue(original, departureProperty.GetValue(otherRecord));
            }
            var observed = kernel.ObserveProcessTeardown(owner);
            Check(observed.IsSuccess && !observed.Value.IsComplete && kernel.Processes.Resolve(owner).IsSuccess && !kernel.Domains.Contains(manifest.DomainId),
                "unknown/copied/foreign continuation or outcome cannot recreate membership or authorize original retirement");
        }
        bool projection = scenario.Contains("-pending-", StringComparison.Ordinal);
        if (projection)
        {
            original.GetType().GetProperty("DomainReclaimPending")!.SetValue(original,
                scenario.EndsWith("-missing", StringComparison.Ordinal) ? null : (object)peerManifest.DomainId);
            var observed = kernel.ObserveProcessTeardown(owner);
            Console.WriteLine($"DIAGNOSTIC projection={scenario} complete={observed.IsSuccess && observed.Value.IsComplete} processLive={kernel.Processes.Resolve(owner).IsSuccess} region={kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State} ownedCharge={kernel.QueryBudget(budget.ProcessBudget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used}");
            Check(observed.IsSuccess && !observed.Value.IsComplete && kernel.Processes.Resolve(owner).IsSuccess,
                "lost/substituted pending projection cannot retire original with invalidated RegionUse/backing/charge");
        }
        if (!canonicalFault && scenario.EndsWith("-loss", StringComparison.Ordinal)) routes.Remove(owner);
        if (!canonicalFault && scenario.EndsWith("-copy", StringComparison.Ordinal))
        {
            var copy = typeof(object).GetMethod("MemberwiseClone", Hidden)!.Invoke(original, null)!;
            copy.GetType().GetProperty("DomainReclaimPending")!.SetValue(copy, null);
            routes[owner] = copy;
        }
        if (!canonicalFault && scenario.EndsWith("-foreign", StringComparison.Ordinal))
        {
            other = new RuntimeKernel(); Check(other.CreateProcess(manifest).IsSuccess, "foreign Kernel process admitted");
            var hook = typeof(RuntimeKernel).GetProperty("AfterInitializedLoadDrainBeforeTeardownCommit", Hidden)!;
            hook.SetValue(other, (Action<ProcessHandle>)(_ => throw new IOException("Retain foreign original stop.")));
            try { other.TerminateProcess(owner); } catch (IOException) { }
            hook.SetValue(other, null);
            routes[owner] = ((IDictionary)Field(other, "_processTeardowns"))[owner]!;
        }
        var candidateManifest = new SingProcessManifestV1(new(973), manifest.DomainId, 1,
            "domain-reclaim-candidate", ExecutionRole.Sip, MemoryProfile.SipRegion);
        var candidate = new ProcessHandle(candidateManifest.ProcessId, candidateManifest.Generation);
        var joined = kernel.CreateProcess(candidateManifest);
        Check(joined.Error == KernelError.PlatformBindingDraining && !kernel.Processes.Resolve(candidate).IsSuccess &&
            !kernel.Domains.Contains(manifest.DomainId), "pending original domain reclaim must deny same-domain admission despite teardown routing loss/copy");
        Check(kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State == RegionState.Owned &&
            kernel.QueryBudget(budget.ProcessBudget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used == 8 &&
            peerBuffer.IsValid && peerBuffer.Span[0] == 0x7D, "refusal preserves original Region/charge and independent foreign backing");
        routes[owner] = original;
        if (canonicalFault)
        {
            departureProperty!.SetValue(original, originalDeparture);
            outcomeField!.SetValue(originalDeparture, originalOutcome);
        }
        Check(kernel.ReleaseRegionUse(owner, use.Value!.Handle).IsSuccess, "cleanup releases original use without permission resurrection");
        Check(kernel.TerminateProcess(owner).IsSuccess, "exact original repair completes pending domain reclaim");
        Check(kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State == RegionState.Released &&
            kernel.QueryBudget(budget.ProcessBudget).Value!.Usage.All(x => x.Used == 0), "original backing/charge independently reclaimed");
        Check(kernel.CreateProcess(candidateManifest).IsSuccess, "fresh domain admission after original reclaim");
        var fresh = kernel.AllocateBuffer<byte>(candidate, 8).Value!; fresh.Span[0] = 0xB4;
        Check(fresh.Handle != buffer.Handle && peerBuffer.IsValid && peerBuffer.Span[0] == 0x7D &&
            kernel.TerminateProcess(candidate).IsSuccess && kernel.TerminateProcess(peer).IsSuccess &&
            (other is null || (otherUse is null || other.ReleaseRegionUse(owner, otherUse.Value).IsSuccess) && other.TerminateProcess(owner).IsSuccess),
            "fresh/foreign resources close independently after original lifetime");
        if (canonicalFault)
            Check(ReferenceEquals(departureProperty!.GetValue(original), originalDeparture) &&
                ReferenceEquals(outcomeField!.GetValue(originalDeparture), originalOutcome), "retry retains same original departure/outcome without reissuing membership effect");
        Console.WriteLine($"PASS domain-reclaim={scenario} pendingAdmissionDenied=true originalRegionReleased=true originalCharge=0 foreignIntact=true");
    }

    private static void RunSharedProjection()
    {
        var kernel = new RuntimeKernel();
        var manifest = new SingProcessManifestV1(new(991), new(992), 1, "shared-original", ExecutionRole.Sip, MemoryProfile.SipRegion);
        var siblingManifest = new SingProcessManifestV1(new(993), manifest.DomainId, 2, "shared-live-peer", ExecutionRole.Sip, MemoryProfile.SipRegion);
        var foreignManifest = new SingProcessManifestV1(new(994), new(995), 1, "shared-foreign", ExecutionRole.Sip, MemoryProfile.SipRegion);
        Check(kernel.CreateProcess(manifest).IsSuccess && kernel.CreateProcess(siblingManifest).IsSuccess && kernel.CreateProcess(foreignManifest).IsSuccess,
            "original/shared/foreign processes admitted");
        var owner = new ProcessHandle(manifest.ProcessId, manifest.Generation);
        var sibling = new ProcessHandle(siblingManifest.ProcessId, siblingManifest.Generation);
        var foreign = new ProcessHandle(foreignManifest.ProcessId, foreignManifest.Generation);
        var originalBuffer = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var siblingBuffer = kernel.AllocateBuffer<byte>(sibling, 8).Value!; siblingBuffer.Span[0] = 0x6B;
        var foreignBuffer = kernel.AllocateBuffer<byte>(foreign, 8).Value!; foreignBuffer.Span[0] = 0x7D;
        var hold = new HoldFinalization(owner);
        typeof(RuntimeKernel).GetMethod("RegisterCxlTeardownParticipant", Hidden)!.Invoke(kernel, [hold]);
        Check(!kernel.TerminateProcess(owner).IsSuccess && kernel.Domains.Contains(manifest.DomainId), "public stop paused before membership effect");
        var original = ((IDictionary)Field(kernel, "_processTeardowns"))[owner]!;
        original.GetType().GetProperty("DomainReclaimPending")!.SetValue(original, manifest.DomainId);
        hold.Pending = false;
        var stopped = kernel.TerminateProcess(owner);
        var siblingState = kernel.Regions.Snapshot().Single(x => x.Handle == siblingBuffer.Handle).State;
        Console.WriteLine($"DIAGNOSTIC shared-projection stop={stopped.Error} siblingRegion={siblingState} siblingLive={kernel.Processes.Resolve(sibling).IsSuccess}");
        Check(stopped.IsSuccess && siblingState == RegionState.Owned && siblingBuffer.IsValid && siblingBuffer.Span[0] == 0x6B &&
            kernel.Domains.Contains(manifest.DomainId) && foreignBuffer.IsValid && foreignBuffer.Span[0] == 0x7D,
            "invented pending projection cannot reclaim or revoke a live shared-domain peer's backing");
        Check(kernel.Regions.Snapshot().Single(x => x.Handle == originalBuffer.Handle).State == RegionState.Owned,
            "shared domain retains exited member backing until actual last departure");
        Check(kernel.TerminateProcess(sibling).IsSuccess && !kernel.Domains.Contains(manifest.DomainId) &&
            kernel.Regions.Snapshot().Single(x => x.Handle == originalBuffer.Handle).State == RegionState.Released &&
            kernel.Regions.Snapshot().Single(x => x.Handle == siblingBuffer.Handle).State == RegionState.Released &&
            foreignBuffer.IsValid && foreignBuffer.Span[0] == 0x7D && kernel.TerminateProcess(foreign).IsSuccess,
            "actual last departure independently reclaims original cohort and preserves foreign backing");
        Console.WriteLine("PASS domain-reclaim=domain-rejoin-pending-live-peer sharedBackingPreserved=true actualLastDepartureReclaimed=true foreignIntact=true");
    }

    private static void RunMembershipSource(string scenario)
    {
        var kernel = new RuntimeKernel();
        var other = new RuntimeKernel();
        var manifest = new SingProcessManifestV1(new(1001), new(1002), 1, "source-original", ExecutionRole.Sip, MemoryProfile.SipRegion);
        var siblingManifest = new SingProcessManifestV1(new(1003), manifest.DomainId, 2, "source-sibling", ExecutionRole.Sip, MemoryProfile.SipRegion);
        Check(kernel.CreateProcess(manifest).IsSuccess && kernel.CreateProcess(siblingManifest).IsSuccess && other.CreateProcess(manifest).IsSuccess,
            "local shared and independent foreign memberships admitted");
        var owner = new ProcessHandle(manifest.ProcessId, manifest.Generation);
        var sibling = new ProcessHandle(siblingManifest.ProcessId, siblingManifest.Generation);
        var cap = kernel.MintCapability(manifest.DomainId, owner, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(owner, cap, owner, "membership-source", [new(ServiceBudgetDimension.OwnedMemoryBytes, 8)]).Value;
        var buffer = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var siblingBuffer = kernel.AllocateBuffer<byte>(sibling, 8).Value!; siblingBuffer.Span[0] = 0x6B;
        var foreignBuffer = other.AllocateBuffer<byte>(owner, 8).Value!; foreignBuffer.Span[0] = 0x7D;
        var hold = new HoldFinalization(owner);
        typeof(RuntimeKernel).GetMethod("RegisterCxlTeardownParticipant", Hidden)!.Invoke(kernel, [hold]);
        Check(!kernel.TerminateProcess(owner).IsSuccess, "actual public stop paused before source acquisition");
        var sources = (IDictionary)Field(kernel.Domains, "_ownedDomains");
        var original = sources[manifest.DomainId]!;
        var foreign = ((IDictionary)Field(other.Domains, "_ownedDomains"))[manifest.DomainId]!;
        var foreignBefore = ((IEnumerable<ProcessHandle>)foreign).ToArray();
        if (scenario.EndsWith("-loss", StringComparison.Ordinal)) sources.Remove(manifest.DomainId);
        else sources[manifest.DomainId] = scenario.EndsWith("-copy", StringComparison.Ordinal)
            ? new HashSet<ProcessHandle>((IEnumerable<ProcessHandle>)original)
            : scenario.EndsWith("-clone", StringComparison.Ordinal)
                ? typeof(object).GetMethod("MemberwiseClone", Hidden)!.Invoke(original, null)! : foreign;
        hold.Pending = false;
        var stopped = kernel.TerminateProcess(owner);
        Console.WriteLine($"DIAGNOSTIC membership-source={scenario} stop={stopped.Error} originalLive={kernel.Processes.Resolve(owner).IsSuccess} siblingRegion={kernel.Regions.Snapshot().Single(x => x.Handle == siblingBuffer.Handle).State} foreignMembers={((IEnumerable<ProcessHandle>)foreign).Count()}");
        Check(!stopped.IsSuccess && kernel.Processes.Resolve(owner).IsSuccess &&
            ((IEnumerable<ProcessHandle>)foreign).SequenceEqual(foreignBefore) &&
            ((IEnumerable<ProcessHandle>)original).Count() == 2,
            "unminted copied/lost/foreign membership source cannot issue departure or mutate foreign original membership");
        Check(kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State == RegionState.Owned &&
            kernel.Regions.Snapshot().Single(x => x.Handle == siblingBuffer.Handle).State == RegionState.Owned &&
            siblingBuffer.IsValid && siblingBuffer.Span[0] == 0x6B && foreignBuffer.IsValid && foreignBuffer.Span[0] == 0x7D &&
            kernel.QueryBudget(budget.ProcessBudget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used == 8,
            "source refusal retains local backing/charge and independent foreign backing");
        var candidate = new SingProcessManifestV1(new(1004), manifest.DomainId, 3, "source-candidate", ExecutionRole.Sip, MemoryProfile.SipRegion);
        Check(!kernel.CreateProcess(candidate).IsSuccess && ((IEnumerable<ProcessHandle>)foreign).SequenceEqual(foreignBefore),
            "membership admission also refuses unknown/copied/foreign acquisition without writing foreign source");
        sources[manifest.DomainId] = original;
        Check(kernel.TerminateProcess(owner).IsSuccess && siblingBuffer.IsValid && siblingBuffer.Span[0] == 0x6B,
            "exact source repair closes only original non-last member");
        Check(kernel.TerminateProcess(sibling).IsSuccess &&
            kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State == RegionState.Released &&
            kernel.QueryBudget(budget.ProcessBudget).Value!.Usage.All(x => x.Used == 0) &&
            foreignBuffer.IsValid && foreignBuffer.Span[0] == 0x7D && other.TerminateProcess(owner).IsSuccess,
            "actual last local departure reclaims original charge/backing; foreign source closes independently");
        Console.WriteLine($"PASS membership-source={scenario} refusalBeforeDeparture=true originalCharge=0 sharedBackingReclaimed=true foreignIntact=true");
    }

    private sealed class HoldFinalization(ProcessHandle target) : ICxlTeardownParticipant
    {
        public bool Pending = true;
        public KernelResult CloseForProcess(ProcessHandle process, RegionOwner owner) => Pending && process == target
            ? KernelResult.Fail(KernelError.PlatformBindingDraining, "Bounded pre-membership observation pause.") : KernelResult.Ok();
    }
}
