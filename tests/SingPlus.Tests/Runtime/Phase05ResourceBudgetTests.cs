using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class Phase05ResourceBudgetTests
{
    [Fact]
    public void ChildServiceBudgetCannotExceedConfiguredSystemParent()
    {
        var kernel = new RuntimeKernel();
        var admin = TestFixtures.Create(kernel, 501, 5001).Handle;
        var capability = kernel.MintCapability(new(5001), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.ConfigureSystemBudget(admin, capability,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 64)]).IsSuccess);

        var denied = Admit(kernel, "over-parent", 502, 5002,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 128)]);

        Assert.Equal(KernelError.BudgetExceeded, denied.Error);
        Assert.Equal(KernelError.ProcessNotFound, kernel.Processes.Resolve(new(new(502), 1)).Error);
    }

    [Fact]
    public async Task ConcurrentReservationsCannotOvercommitAndStaleReleaseCannotFreeCurrentCharge()
    {
        var kernel = new RuntimeKernel();
        var component = Admit(kernel, "race", 503, 5003,
            [new(ServiceBudgetDimension.ExternalOperations, 1)]).Value!;
        using var start = new ManualResetEventSlim(false);
        var attempts = Enumerable.Range(0, 2).Select(index => Task.Run(() =>
        {
            start.Wait();
            return kernel.ReserveBudget(component.Process,
                [new(ServiceBudgetDimension.ExternalOperations, 1)],
                BudgetReservationLifetime.ExternalEffect,
                index == 0 ? AdmissionQosHint.LatencySensitive : AdmissionQosHint.Background);
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(attempts);

        var winner = Assert.Single(results, result => result.IsSuccess).Value!;
        Assert.Single(results, result => result.Error == KernelError.BudgetExceeded);
        Assert.True(kernel.ReleaseBudget(component.Process, winner.Reservation).IsSuccess);
        var current = kernel.ReserveBudget(component.Process,
            [new(ServiceBudgetDimension.ExternalOperations, 1)],
            BudgetReservationLifetime.ExternalEffect).Value!;
        var stale = current.Reservation with { Generation = new(current.Reservation.Generation.Value + 1) };

        Assert.Equal(BudgetReservationState.Stale,
            kernel.ReleaseBudget(component.Process, stale).Value!.State);
        Assert.Equal(KernelError.BudgetExceeded,
            kernel.ReserveBudget(component.Process,
                [new(ServiceBudgetDimension.ExternalOperations, 1)],
                BudgetReservationLifetime.ExternalEffect,
                AdmissionQosHint.ThroughputOriented).Error);
        Assert.True(kernel.ReleaseBudget(component.Process, current.Reservation).IsSuccess);
    }

    [Fact]
    public void OwnedMemoryBudgetGatesAllocationAndRecoversOnExactRelease()
    {
        var kernel = new RuntimeKernel();
        var component = Admit(kernel, "memory", 504, 5004,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 8)]).Value!;
        var first = kernel.AllocateBuffer<byte>(component.Process, 8);
        Assert.True(first.IsSuccess, first.Message);

        Assert.Equal(KernelError.BudgetExceeded,
            kernel.AllocateBuffer<byte>(component.Process, 1).Error);
        Assert.True(kernel.ReleaseRegion(component.Process, first.Value!).IsSuccess);
        Assert.True(kernel.AllocateBuffer<byte>(component.Process, 8).IsSuccess);
    }

    [Fact]
    public void IpcMessageAndByteBudgetsReleaseWhenQueueLifetimeEnds()
    {
        var kernel = new RuntimeKernel();
        var sender = Admit(kernel, "ipc", 505, 5005,
            [new(ServiceBudgetDimension.IpcMessages, 1), new(ServiceBudgetDimension.IpcBytes, 32)]).Value!;
        var receiver = TestFixtures.Create(kernel, 506, 5006).Handle;
        var protocol = new ProtocolDefinitionV1("BudgetIpc", "budget-ipc", "Idle", null,
            [new(1, "Ping")], [new(1, "Idle", "Idle")]);
        var channel = kernel.CreateChannel(sender.Process, receiver, protocol, 4).Value;

        Assert.True(kernel.Send(sender.Process, receiver, channel.Left, 1).IsSuccess);
        Assert.Equal(KernelError.BudgetExceeded,
            kernel.Send(sender.Process, receiver, channel.Left, 1).Error);
        Assert.True(kernel.Receive(receiver, channel.Right).IsSuccess);
        Assert.True(kernel.Send(sender.Process, receiver, channel.Left, 1).IsSuccess);
    }

    [Fact]
    public void MappedMemoryBudgetIsIndependentAndReleasesOnlyAfterMappingClosure()
    {
        var kernel = new RuntimeKernel(new HostPlatformAuthorityProvider());
        var admin = TestFixtures.Create(kernel, 507, 5007).Handle;
        var owner = TestFixtures.Create(kernel, 509, 5009).Handle;
        var budgetCapability = kernel.MintCapability(new(5007), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcessBudget(admin, budgetCapability, owner, "mapping",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 256), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 64)]).IsSuccess);
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var first = kernel.AllocateBuffer<byte>(owner, 64).Value!;
        var second = kernel.AllocateBuffer<byte>(owner, 64).Value!;
        var firstCapability = kernel.MintCapability(new(5009), owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(first.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var secondCapability = kernel.MintCapability(new(5009), owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(second.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var mapped = kernel.MapPlatformOwnedRegion(owner, binding, firstCapability,
            first.Handle, PlatformMemoryAccess.Read);
        Assert.True(mapped.IsSuccess, mapped.Message);

        Assert.Equal(KernelError.BudgetExceeded,
            kernel.MapPlatformOwnedRegion(owner, binding, secondCapability,
                second.Handle, PlatformMemoryAccess.Read).Error);
        Assert.True(kernel.RevokePlatformRegionMapping(owner, mapped.Value!).IsSuccess);
        Assert.True(kernel.MapPlatformOwnedRegion(owner, binding, secondCapability,
            second.Handle, PlatformMemoryAccess.Read).IsSuccess);
    }

    [Fact]
    public void CrashWithSubmittedExternalEffectKeepsChargeUntilExactClosure()
    {
        var kernel = new RuntimeKernel();
        var component = Admit(kernel, "external", 508, 5008,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 16), new(ServiceBudgetDimension.ExternalOperations, 1)]).Value!;
        var input = kernel.AllocateBuffer<byte>(component.Process, 8).Value!;
        var output = kernel.AllocateBuffer<byte>(component.Process, 8).Value!;
        var prepared = kernel.PrepareExternalOperation(component.Process,
            [new(input.Handle, RegionUseMode.ReadOnly, new(0, 8)), new(output.Handle, RegionUseMode.StagedOutput, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!;
        var dependencies = new OperationDependencySnapshot(1, 1);
        Assert.True(kernel.AdmitExternalOperation(component.Process, prepared.Operation, dependencies).IsSuccess);
        var binding = kernel.RecordExternalOperationSubmission(component.Process, prepared.Operation, dependencies).Value!;

        var faulted = kernel.FaultComponent(component.Identity);
        Assert.True(faulted.IsSuccess, faulted.Message);
        var charged = kernel.QueryBudget(component.ProcessBudget).Value!;
        Assert.Equal(BudgetPressureState.PinnedByExternalEffect, charged.Pressure);
        Assert.Equal(1UL, Usage(charged, ServiceBudgetDimension.ExternalOperations).Used);

        Assert.True(kernel.RecordExternalOperationCompletion(component.Process,
            new(binding, ExternalOperationCompletionDisposition.Cancelled)).IsSuccess);
        Assert.True(kernel.ObserveComponentTeardown(component.Identity).IsSuccess);
        var closurePending = kernel.QueryBudget(component.ProcessBudget).Value!;
        Assert.Equal(BudgetPressureState.PinnedByExternalEffect, closurePending.Pressure);
        Assert.Equal(1UL, Usage(closurePending, ServiceBudgetDimension.ExternalOperations).Used);

        Assert.True(kernel.ReleaseExternalOperation(component.Process, prepared.Operation, new(true, false)).IsSuccess);
        Assert.True(kernel.ObserveComponentTeardown(component.Identity).IsSuccess);
        var closed = kernel.QueryBudget(component.ProcessBudget).Value!;
        Assert.Equal(0UL, Usage(closed, ServiceBudgetDimension.ExternalOperations).Used);
    }

    [Fact]
    public void BudgetDtosAndQosHintsAreNotAuthority()
    {
        var observation = new BudgetReservationSnapshot(default, default, default, [],
            BudgetReservationLifetime.LocalResource, AdmissionQosHint.LatencySensitive,
            BudgetReservationState.Active);
        Assert.False(observation.AuthorizesEffect);
        Assert.False(observation.AuthorizesReclaim);
        Assert.False(new BudgetAccountSnapshot(default, BudgetAccountLevel.System, null, "system", [],
            BudgetPressureState.Normal).MaterializesAuthority);
        var vocabulary = typeof(AdmissionQosHint).GetEnumNames();
        Assert.DoesNotContain(vocabulary, name => name.Contains("Cxl", StringComparison.OrdinalIgnoreCase) ||
                                                   name.Contains("Lane", StringComparison.OrdinalIgnoreCase) ||
                                                   name.Contains("Hybrid", StringComparison.OrdinalIgnoreCase));
    }

    private static BudgetUsage Usage(BudgetAccountSnapshot account, ServiceBudgetDimension dimension) =>
        Assert.Single(account.Usage, usage => usage.Dimension == dimension);

    private static KernelResult<ComponentLifecycleSnapshot> Admit(
        RuntimeKernel kernel, string name, ulong processId, ulong domainId,
        IReadOnlyList<ServiceBudgetRequestV1> budgets)
    {
        byte[] image = [(byte)(processId & 0xff), 0x05];
        var manifest = new ServiceManifestV1(
            new(name), new("1"), Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(),
            TestFixtures.Manifest(processId, domainId, identity: name), budgetRequests: budgets);
        return kernel.AdmitComponent(new(manifest, image));
    }
}
