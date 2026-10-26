using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Diagnostics;

public static class TransferChargeClosureOracle
{
    public static void Run(string scenario)
    {
        var kernel = new RuntimeKernel();
        ProcessHandle source = Create(kernel, 1301, 1311);
        ProcessHandle target = Create(kernel, 1302, 1312);
        ProcessHandle foreign = Create(kernel, 1303, 1313);
        var cap = kernel.MintCapability(new(1311), source, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var sourceBudget = kernel.AdmitProcessBudget(source, cap, source, "transfer-source", [new(ServiceBudgetDimension.OwnedMemoryBytes, 8)]).Value!;
        var targetBudget = kernel.AdmitProcessBudget(source, cap, target, "transfer-target", [new(ServiceBudgetDimension.OwnedMemoryBytes, 8)]).Value!;
        var peer = kernel.AllocateBuffer<byte>(foreign, 4).Value!; peer.Span[0] = 0x7D;
        bool scalar = scenario.EndsWith("scalar", StringComparison.Ordinal);
        var buffer = scalar ? null : kernel.AllocateBuffer<byte>(source, 4).Value!;
        var region = scalar ? kernel.AllocateRegion(source, 17).Value! : null;
        RegionHandle old = scalar ? region!.Handle : buffer!.Handle;
        var routes = (IDictionary)typeof(RuntimeKernel).GetField("_regionBudgetReservations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
        object sourceCharge = routes[old]!;
        RegionHandle moved = new(old.RegionId, new(old.Generation.Value + 1));
        routes[moved] = sourceCharge;
        KernelResult result;
        if (scalar)
        {
            var transferred = kernel.TransferRegion(source, target, region!);
            result = transferred.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(transferred.Error, transferred.Message!);
        }
        else
        {
            var transferred = kernel.TransferRegion(source, target, buffer!);
            result = transferred.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(transferred.Error, transferred.Message!);
        }
        Check(!result.IsSuccess && kernel.Regions.Snapshot().Single(x => x.Handle.RegionId == old.RegionId).Handle == moved,
            "original transfer effect precedes refused quantitative publication");
        Check(Used(kernel, targetBudget.ProcessBudget) == 4 && peer.IsValid && peer.Span[0] == 0x7D,
            "unknown target composition retains charge and independent backing");
        Check(!kernel.TerminateProcess(target).IsSuccess && Used(kernel, targetBudget.ProcessBudget) == 4,
            "conflicting route cannot authorize target accounting consumption");
        routes.Remove(moved); // Repair only correlation; original owners must supply closure.
        if (scenario.Contains("copy", StringComparison.Ordinal) || scenario.Contains("request-loss", StringComparison.Ordinal))
        {
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            var records = (IDictionary)typeof(ResourceBudgetAuthority).GetField("_reservations", hidden)!.GetValue(kernel.Budgets)!;
            object charge = records.Values.Cast<object>().Single(x => (ProcessHandle)x.GetType().GetProperty("Owner")!.GetValue(x)! == target);
            string name = scenario.Contains("copy", StringComparison.Ordinal) ? "OriginalFailedRegionTransfer" : "OriginalTransferPreparation";
            var property = charge.GetType().GetProperty(name)!;
            object original = property.GetValue(charge)!;
            property.SetValue(charge, name == "OriginalTransferPreparation" ? null : typeof(object).GetMethod("MemberwiseClone", hidden)!.Invoke(original, null));
            Check(!kernel.TerminateProcess(target).IsSuccess && Used(kernel, targetBudget.ProcessBudget) == 4 &&
                kernel.Processes.Resolve(target).IsSuccess && peer.IsValid && peer.Span[0] == 0x7D,
                "copied outcome or missing request cannot settle quarantined charge from Region closure alone");
            property.SetValue(charge, original);
        }
        Check(kernel.TerminateProcess(target).IsSuccess, "original target teardown continues after route repair");
        Console.WriteLine($"DIAGNOSTIC transfer-charge={scenario} targetUsed={Used(kernel, targetBudget.ProcessBudget)} region={kernel.Regions.Snapshot().Single(x => x.Handle == moved).State}");
        Check(Used(kernel, targetBudget.ProcessBudget) == 0 &&
            kernel.Regions.Snapshot().Single(x => x.Handle == moved).State == RegionState.Released &&
            peer.IsValid && peer.Span[0] == 0x7D, "exact original transfer closure must reclaim target charge without consuming foreign backing");
        Check(kernel.TerminateProcess(source).IsSuccess && Used(kernel, sourceBudget.ProcessBudget) == 0 &&
            peer.IsValid && peer.Span[0] == 0x7D && kernel.TerminateProcess(foreign).IsSuccess,
            "source and foreign resource close independently");
        Console.WriteLine($"PASS transfer-charge={scenario} targetCharge=0 sourceCharge=0 originalRegionReleased=true foreignIntact=true");
    }

    private static ProcessHandle Create(RuntimeKernel kernel, ulong id, ulong domain)
    {
        var manifest = new SingProcessManifestV1(new(id), new(domain), 1, $"transfer-charge-{id}", ExecutionRole.Sip, MemoryProfile.SipRegion);
        Check(kernel.CreateProcess(manifest).IsSuccess, "process admitted");
        return new(manifest.ProcessId, 1);
    }
    private static ulong Used(RuntimeKernel kernel, BudgetAccountHandle account) => kernel.QueryBudget(account).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
