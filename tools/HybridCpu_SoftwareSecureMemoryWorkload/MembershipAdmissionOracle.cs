using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Diagnostics;

public static class MembershipAdmissionOracle
{
    public static void RunStale(string scenario)
    {
        var kernel = new RuntimeKernel();
        var previous = new SingProcessManifestV1(new(1201), new(1202), 1, "stale-old", ExecutionRole.Sip, MemoryProfile.SipRegion);
        var current = new SingProcessManifestV1(previous.ProcessId, previous.DomainId, 2, "stale-current", ExecutionRole.Sip, MemoryProfile.SipRegion);
        var siblingManifest = new SingProcessManifestV1(new(1203), current.DomainId, 3, "stale-sibling", ExecutionRole.Sip, MemoryProfile.SipRegion);
        var candidateManifest = new SingProcessManifestV1(new(1204), current.DomainId, 4, "stale-candidate", ExecutionRole.Sip, MemoryProfile.SipRegion);
        var oldOwner = new ProcessHandle(previous.ProcessId, previous.Generation);
        var owner = new ProcessHandle(current.ProcessId, current.Generation);
        var sibling = new ProcessHandle(siblingManifest.ProcessId, siblingManifest.Generation);
        var candidate = new ProcessHandle(candidateManifest.ProcessId, candidateManifest.Generation);
        Check(kernel.CreateProcess(previous).IsSuccess, "previous cohort admitted");
        var sources = (IDictionary)typeof(DomainRegistry).GetField("_ownedDomains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.Domains)!;
        var oldSource = (HashSet<ProcessHandle>)sources[previous.DomainId]!;
        Check(kernel.TerminateProcess(oldOwner).IsSuccess && oldSource.Count == 0, "previous original cohort completed");
        Check(kernel.CreateProcess(current).IsSuccess && kernel.CreateProcess(siblingManifest).IsSuccess, "new original cohort admitted");
        var originalSource = sources[current.DomainId]!;
        var cap = kernel.MintCapability(current.DomainId, owner, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(owner, cap, owner, "stale-cohort", [new(ServiceBudgetDimension.OwnedMemoryBytes, 8)]).Value;
        var buffer = kernel.AllocateBuffer<byte>(owner, 8).Value!; buffer.Span[0] = 0x6B;
        var siblingBuffer = kernel.AllocateBuffer<byte>(sibling, 8).Value!; siblingBuffer.Span[0] = 0x5A;
        var foreign = new RuntimeKernel(); Check(foreign.CreateProcess(current).IsSuccess, "foreign cohort admitted");
        var foreignBuffer = foreign.AllocateBuffer<byte>(owner, 8).Value!; foreignBuffer.Span[0] = 0x7D;
        bool departure = scenario.EndsWith("departure", StringComparison.Ordinal);
        if (departure || scenario == "domain-stale-reseeded-admission") oldSource.Add(owner);
        if (scenario.Contains("full-reseeded", StringComparison.Ordinal))
        {
            oldSource.Add(owner);
            oldSource.Add(sibling);
        }
        var oldContents = oldSource.ToArray();
        sources[current.DomainId] = oldSource;
        KernelResult refusal;
        if (departure) refusal = kernel.TerminateProcess(owner);
        else
        {
            var admitted = kernel.CreateProcess(candidateManifest);
            if (admitted.IsSuccess)
            {
                var stopped = kernel.TerminateProcess(candidate);
                Console.WriteLine($"DIAGNOSTIC stale-cohort admitted=true candidateStop={stopped.Error} liveOriginalRegion={kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State}");
            }
            refusal = admitted.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(admitted.Error, admitted.Message!);
        }
        Check(!refusal.IsSuccess && kernel.Processes.Resolve(owner).IsSuccess && !kernel.Processes.Resolve(candidate).IsSuccess && oldSource.SequenceEqual(oldContents),
            "genuine retired same-registry source cannot admit or depart a new cohort, even with equal current handle reseeded");
        Check(buffer.IsValid == !departure && siblingBuffer.IsValid && siblingBuffer.Span[0] == 0x5A &&
            kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State == RegionState.Owned &&
            kernel.QueryBudget(budget.ProcessBudget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used == 8 &&
            foreignBuffer.IsValid && foreignBuffer.Span[0] == 0x7D, "refusal preserves original backing/charge, live sibling and foreign cohort");
        sources[current.DomainId] = originalSource;
        if (!departure) Check(kernel.CreateProcess(candidateManifest).IsSuccess && kernel.TerminateProcess(candidate).IsSuccess,
            "exact current source repair admits actual non-last candidate");
        Check(kernel.TerminateProcess(owner).IsSuccess && siblingBuffer.IsValid && siblingBuffer.Span[0] == 0x5A,
            "current original non-last stop preserves live sibling");
        Check(kernel.TerminateProcess(sibling).IsSuccess &&
            kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State == RegionState.Released &&
            kernel.QueryBudget(budget.ProcessBudget).Value!.Usage.All(x => x.Used == 0) &&
            foreignBuffer.IsValid && foreignBuffer.Span[0] == 0x7D && foreign.TerminateProcess(owner).IsSuccess,
            "actual last current departure reclaims original backing/charge; foreign remains independent");
        Console.WriteLine($"PASS membership={scenario} retiredSourceRefused=true exactCurrentRepair=true originalCharge=0 foreignIntact=true");
    }

    public static void Run()
    {
        var kernel = new RuntimeKernel();
        var first = new SingProcessManifestV1(new(1101), new(1102), 1, "membership-live", ExecutionRole.Sip, MemoryProfile.SipRegion);
        var next = new SingProcessManifestV1(new(1103), first.DomainId, 2, "membership-new", ExecutionRole.Sip, MemoryProfile.SipRegion);
        var owner = new ProcessHandle(first.ProcessId, first.Generation);
        var candidate = new ProcessHandle(next.ProcessId, next.Generation);
        Check(kernel.CreateProcess(first).IsSuccess, "original live membership");
        var cap = kernel.MintCapability(first.DomainId, owner, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(owner, cap, owner, "live-membership", [new(ServiceBudgetDimension.OwnedMemoryBytes, 8)]).Value;
        var buffer = kernel.AllocateBuffer<byte>(owner, 8).Value!; buffer.Span[0] = 0x6B;
        var foreign = new RuntimeKernel();
        Check(foreign.CreateProcess(first).IsSuccess, "same-handle foreign process");
        var peerBuffer = foreign.AllocateBuffer<byte>(owner, 8).Value!; peerBuffer.Span[0] = 0x7D;
        var sources = (IDictionary)typeof(DomainRegistry).GetField("_ownedDomains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.Domains)!;
        var original = sources[first.DomainId]!;
        sources.Remove(first.DomainId);
        var admitted = kernel.CreateProcess(next);
        Console.WriteLine($"DIAGNOSTIC live-membership-loss admitted={admitted.IsSuccess} originalLive={kernel.Processes.Resolve(owner).IsSuccess}");
        if (admitted.IsSuccess)
        {
            var stop = kernel.TerminateProcess(candidate);
            Console.WriteLine($"DIAGNOSTIC substituted-cohort stop={stop.Error} originalRegion={kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State} originalLive={kernel.Processes.Resolve(owner).IsSuccess}");
        }
        Check(!admitted.IsSuccess && !kernel.Processes.Resolve(candidate).IsSuccess && !sources.Contains(first.DomainId),
            "missing live source must refuse before process registration and membership mint");
        Check(buffer.IsValid && buffer.Span[0] == 0x6B && kernel.Processes.Resolve(owner).IsSuccess &&
            kernel.QueryBudget(budget.ProcessBudget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used == 8 &&
            peerBuffer.IsValid && peerBuffer.Span[0] == 0x7D, "refusal preserves original backing/charge and independent foreign owner");
        sources[first.DomainId] = original;
        Check(kernel.CreateProcess(next).IsSuccess && kernel.TerminateProcess(candidate).IsSuccess &&
            buffer.IsValid && buffer.Span[0] == 0x6B, "exact repair admits fresh member; non-last departure preserves original backing");
        Check(kernel.TerminateProcess(owner).IsSuccess &&
            kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State == RegionState.Released &&
            kernel.QueryBudget(budget.ProcessBudget).Value!.Usage.All(x => x.Used == 0) &&
            peerBuffer.IsValid && peerBuffer.Span[0] == 0x7D && foreign.TerminateProcess(owner).IsSuccess,
            "actual last departure independently reclaims original backing");
        Console.WriteLine("PASS live-membership-loss refusalBeforeRegistration=true exactRepair=true originalRegionReleased=true");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
