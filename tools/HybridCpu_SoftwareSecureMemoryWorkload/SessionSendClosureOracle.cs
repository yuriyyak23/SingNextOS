using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.Compute;

namespace SingPlus.Diagnostics;

public static class SessionSendClosureOracle
{
    public static void Run(string scenario)
    {
        var kernel = new RuntimeKernel();
        ProcessHandle caller = Create(kernel, 1401, 1411);
        ProcessHandle foreign = Create(kernel, 1403, 1413);
        ProtocolDefinitionV1 protocol = ComputeServiceClient.CreateProtocol();
        var contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
        var registration = new ProvidedServiceManifestV1("session-closure", contract);
        var requirement = new CapabilityRequirementV1(ResourceKind.Compute, CapabilityResourceIds.Dsc1Copy, CapabilityRights.Execute);
        byte[] image = [1, 4, 2];
        var manifest = new ServiceManifestV1(new("session-closure"), new("1"), Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(),
            new SingProcessManifestV1(new(1402), new(1412), 1, "session-closure-service", ExecutionRole.Sip, MemoryProfile.SipRegion, requiredCapabilities: [requirement]),
            [registration], requiresPlatformDomain: false,
            budgetRequests: [new(ServiceBudgetDimension.OwnedMemoryBytes, 64), new(ServiceBudgetDimension.IpcMessages, 4), new(ServiceBudgetDimension.IpcBytes, 4096)]);
        var admitted = kernel.AdmitComponent(new ComponentAdmissionPlan(manifest, image, [new ComponentCapabilityGrant(new(1411), requirement)],
            [new ComponentProvidedServiceRegistration(registration, protocol, ComputeServiceClient.ResponseProtocol, [requirement])]));
        Check(admitted.IsSuccess, admitted.Message ?? "component admitted");
        var admin = kernel.MintCapability(new(1411), caller, ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(caller, admin, caller, "session-closure-caller",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 64), new(ServiceBudgetDimension.IpcMessages, 4), new(ServiceBudgetDimension.IpcBytes, 4096)]).Value!;
        var capability = kernel.MintCapability(new(1411), caller, ResourceKind.Compute, CapabilityResourceIds.Dsc1Copy, CapabilityRights.Execute).Value!.CapabilityId;
        var session = kernel.OpenSession(caller, kernel.ResolveByServiceName("session-closure").Value, [capability]).Value;
        var source = kernel.AllocateBuffer<byte>(caller, 16).Value!; source.Span[0] = 0x6B;
        var destination = kernel.AllocateBuffer<byte>(caller, 16).Value!;
        var peer = kernel.AllocateBuffer<byte>(foreign, 4).Value!; peer.Span[0] = 0x7D;
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var routes = (IDictionary)typeof(RuntimeKernel).GetField("_regionBudgetReservations", hidden)!.GetValue(kernel)!;
        RegionHandle old = destination.Handle;
        RegionHandle moved = new(old.RegionId, new(old.Generation.Value + 1));
        routes[moved] = routes[old]!;
        var transport = new RuntimeSipClientTransport(kernel, caller, session);
        var client = ComputeServiceClient.Create(transport);
        bool refused = false;
        try { _ = client.CopyAsync(source, destination).AsTask().GetAwaiter().GetResult(); }
        catch (InvalidOperationException) { refused = true; }
        Check(refused && transport.HasPendingOwnershipPair, "actual generated failed send retains original custody");
        var registry = typeof(RuntimeKernel).GetProperty("SessionInvocations", hidden)!.GetValue(kernel)!;
        var records = (IDictionary)registry.GetType().GetField("_ownedRecords", hidden)!.GetValue(registry)!;
        object record = records.Values.Cast<object>().Single();
        var pendingFlag = record.GetType().GetProperty("OriginalSendPending")!;
        var noEffectFlag = record.GetType().GetProperty("OriginalSendNoEffect")!;
        pendingFlag.SetValue(record, false);
        noEffectFlag.SetValue(record, true);
        Check(transport.HasPendingOwnershipPair, "pending/no-effect projections cannot erase original send obligations");
        pendingFlag.SetValue(record, true);
        noEffectFlag.SetValue(record, false);
        PropertyInfo flag = record.GetType().GetProperty("OriginalSendCleanupCompleted")!;
        flag.SetValue(record, true);
        Console.WriteLine($"DIAGNOSTIC session-send={scenario} pendingAfterFlag={transport.HasPendingOwnershipPair}");
        Check(transport.HasPendingOwnershipPair, "terminal projection flag cannot replace original cleanup outcome");
        Check(transport.CleanupPendingOwnershipPair().IsSuccess, "actual original cleanup ignores invented terminal projection");
        Check(!transport.HasPendingOwnershipPair && source.IsValid && source.Span[0] == 0x6B && !destination.IsValid &&
            kernel.Regions.Snapshot().Single(x => x.Handle == moved).State == RegionState.Released &&
            Used(kernel, admitted.Value!.ProcessBudget) == 0 && Used(kernel, budget.ProcessBudget) == 16 &&
            peer.IsValid && peer.Span[0] == 0x7D, "cleanup independently closes original resources/target charges and restores source borrow");
        flag.SetValue(record, false);
        Check(!transport.HasPendingOwnershipPair && transport.CleanupPendingOwnershipPair().IsSuccess,
            "lost terminal projection cannot erase canonical same-original completion");
        var outcomeProperty = record.GetType().GetProperty("OriginalSendCleanupOutcome")!;
        object outcome = outcomeProperty.GetValue(record)!;
        foreach (bool copy in new[] { true, false })
        {
            outcomeProperty.SetValue(record, copy ? typeof(object).GetMethod("MemberwiseClone", hidden)!.Invoke(outcome, null) : null);
            Check(transport.HasPendingOwnershipPair && !transport.CleanupPendingOwnershipPair().IsSuccess &&
                Used(kernel, admitted.Value!.ProcessBudget) == 0 && source.IsValid && peer.IsValid && peer.Span[0] == 0x7D,
                "copied/lost canonical outcome cannot report completion or repeat independent resource cleanup");
            outcomeProperty.SetValue(record, outcome);
            Check(!transport.HasPendingOwnershipPair && transport.CleanupPendingOwnershipPair().IsSuccess,
                "exact original outcome repair resolves without repeating send");
        }
        Check(kernel.CloseSession(caller, session).IsSuccess && kernel.TerminateProcess(caller).IsSuccess &&
            Used(kernel, budget.ProcessBudget) == 0 && peer.IsValid && peer.Span[0] == 0x7D && kernel.TerminateProcess(foreign).IsSuccess,
            "original session pin closes independently; foreign backing survives");
        Check(kernel.FaultComponent(manifest.Identity).IsSuccess, "service closure independent");
        Console.WriteLine($"PASS session-send={scenario} targetCharge=0 sourceCharge=0 sourceBorrowRestored=true foreignIntact=true");
    }
    private static ProcessHandle Create(RuntimeKernel kernel, ulong process, ulong domain)
    {
        Check(kernel.CreateProcess(new(new(process), new(domain), 1, $"session-closure-{process}", ExecutionRole.Sip, MemoryProfile.SipRegion)).IsSuccess, "process admitted");
        return new(new(process), 1);
    }
    private static ulong Used(RuntimeKernel kernel, BudgetAccountHandle budget) => kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
