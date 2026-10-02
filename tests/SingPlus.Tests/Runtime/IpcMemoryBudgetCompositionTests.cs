using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class IpcMemoryBudgetCompositionTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 0)]
    [InlineData(4, 1)]
    [InlineData(4, 2)]
    public void RealIpcOwnershipConsumersComposeExactSourceAndTargetMemoryBudgets(int path, int targetMode)
    {
        var s = Create(path, targetMode);
        var old = s.Payload.Handle;
        var sourceCharge = Charges(s.Kernel)[old];
        var before = s.Kernel.Regions.Snapshot();
        var result = Transfer(s);
        if (targetMode != 0)
        {
            Assert.Equal(targetMode == 1 ? KernelError.BudgetExceeded : KernelError.BudgetNotConfigured, result.Error);
            Assert.True(s.Payload.IsValid);
            Assert.Equal(sourceCharge, Charges(s.Kernel)[old]);
            foreach (var original in before)
            {
                var after = s.Kernel.Regions.Snapshot().Single(r => r.Handle == original.Handle);
                Assert.Equal(original.State, after.State);
                Assert.Equal(original.Owner, after.Owner);
                Assert.Equal(original.MutationEpoch, after.MutationEpoch);
            }
            Assert.Equal(path == 2 ? 8UL : 4UL, Used(s.Kernel, s.SourceBudget));
            if (s.TargetBudget is { } targetBudget) Assert.Equal(0UL, Used(s.Kernel, targetBudget));
            return;
        }
        Assert.True(result.IsSuccess, result.Message);
        var moved = result.Value!;
        Assert.False(s.Payload.IsValid);
        Assert.Equal(old.RegionId, moved.Handle.RegionId);
        Assert.NotEqual(old.Generation, moved.Handle.Generation);
        Assert.False(Charges(s.Kernel).ContainsKey(old));
        Assert.Equal(s.Target, Charges(s.Kernel)[moved.Handle].Owner);
        Assert.Equal(BudgetReservationState.Released, s.Kernel.QueryBudget(sourceCharge.Reservation).Value!.State);
        Assert.Equal(path == 2 ? 4UL : 0UL, Used(s.Kernel, s.SourceBudget));
        Assert.Equal(4UL, Used(s.Kernel, s.TargetBudget!.Value));
        Assert.False(Transfer(s).IsSuccess);
        Assert.Equal(4UL, Used(s.Kernel, s.TargetBudget.Value));
        Assert.True(s.Kernel.ReleaseRegion(s.Target, moved).IsSuccess);
        Assert.Equal(0UL, Used(s.Kernel, s.TargetBudget.Value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void SuccessfulIpcTransferCannotEraseQuarantinedSourceCharge(int path)
    {
        var s = Create(path, 0);
        var old = s.Payload.Handle;
        var charge = Charges(s.Kernel)[old];
        Assert.True(s.Kernel.Budgets.QuarantineLease(s.Source, charge.Reservation).IsSuccess);
        var moved = Transfer(s);
        Assert.True(moved.IsSuccess, moved.Message);
        Assert.Equal(charge, Charges(s.Kernel)[old]);
        Assert.Equal(s.Target, Charges(s.Kernel)[moved.Value!.Handle].Owner);
        Assert.True(s.Kernel.ReleaseRegion(s.Target, moved.Value).IsSuccess);
        Assert.Equal(charge, Charges(s.Kernel)[old]);
        Assert.Equal(BudgetReservationState.Quarantined, s.Kernel.QueryBudget(charge.Reservation).Value!.State);
        Assert.Equal(path == 2 ? 8UL : 4UL, Used(s.Kernel, s.SourceBudget));
        Assert.Equal(0UL, Used(s.Kernel, s.TargetBudget!.Value));
    }

    [Fact]
    public void ExceptionAfterTargetReservationRetainsBudgetOwnerQuarantineWithoutInventedSettlement()
    {
        var s = Create(0, 0);
        var old = s.Payload.Handle;
        var call = typeof(RuntimeKernel).GetMethod("TransferRegionForIpc", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var result = (KernelResult<RegionHandle>)call.Invoke(s.Kernel,
            [s.SourceProcess, s.TargetProcess, old, (Func<KernelResult>)(() => throw new InvalidOperationException("injected-local-preparation-loss"))])!;
        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.True(s.Payload.IsValid);
        Assert.Equal(4UL, Used(s.Kernel, s.TargetBudget!.Value));
        var records = (IDictionary)typeof(ResourceBudgetAuthority).GetField("_reservations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel.Budgets)!;
        var targetRecord = Assert.Single(records.Values.Cast<object>(), record =>
            (ProcessHandle)record.GetType().GetProperty("Owner")!.GetValue(record)! == s.Target);
        var reservation = (BudgetReservationHandle)targetRecord.GetType().GetProperty("Handle")!.GetValue(targetRecord)!;
        Assert.Equal(BudgetReservationState.Quarantined, s.Kernel.QueryBudget(reservation).Value!.State);
        Assert.False(s.Kernel.ReleaseBudget(s.Target, reservation).IsSuccess);
        Assert.Equal(RegionState.Owned, s.Kernel.Regions.Snapshot().Single(r => r.Handle == old).State);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void CohortPeerTransferUsesActualQuantitativePayerWithoutChangingV1Authorization(int path, bool quarantine)
    {
        var s = Create(0, 0);
        var (peerProcess, peer) = TestFixtures.Create(s.Kernel, 604, 6001);
        var admin = TestFixtures.Create(s.Kernel, 605, 6005).Handle;
        var cap = s.Kernel.MintCapability(new(6005), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var peerBudget = s.Kernel.AdmitProcessBudget(admin, cap, peer, "cohort-peer",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4), new(ServiceBudgetDimension.IpcMessages, 4), new(ServiceBudgetDimension.IpcBytes, 1024)]).Value!.ProcessBudget;
        var old = s.Payload.Handle;
        var payerCharge = Charges(s.Kernel)[old];
        if (quarantine) Assert.True(s.Kernel.Budgets.QuarantineLease(s.Source, payerCharge.Reservation).IsSuccess);
        var protocol = new ProtocolDefinitionV1("CohortMove", "cohort-move", "Ready", null,
            [new(1, "Move", consumes: ["payload"], requestPayload: new(RequestPayloadKind.Ownership, "payload",
                typeof(OwnedBuffer<>).Namespace + ".OwnedBuffer", ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer))], [new(1, "Ready", "Ready")]);
        var endpoint = s.Kernel.CreateChannel(peer, s.Target, protocol, 4).Value;
        OwnedBuffer<byte> moved;
        if (path == 0)
        {
            var sent = s.Kernel.Send(peer, s.Target, endpoint.Left, 1, s.Payload);
            Assert.True(sent.IsSuccess, sent.Message);
            moved = (OwnedBuffer<byte>)sent.Value!.Payload!;
        }
        else if (path == 1)
        {
            var inline = s.Kernel.Channels.BeginInlineMoveInvocation(peerProcess, s.TargetProcess, endpoint.Left, 1, s.Payload);
            Assert.True(inline.IsSuccess, inline.Message);
            moved = inline.Value.Payload;
        }
        else
        {
            var direct = s.Kernel.TransferRegion(peer, s.Target, s.Payload);
            Assert.True(direct.IsSuccess, direct.Message);
            moved = direct.Value!;
        }
        Assert.Equal(0UL, Used(s.Kernel, peerBudget));
        Assert.Equal(quarantine ? 4UL : 0UL, Used(s.Kernel, s.SourceBudget));
        Assert.Equal(4UL, Used(s.Kernel, s.TargetBudget!.Value));
        if (quarantine) Assert.Equal(payerCharge, Charges(s.Kernel)[old]);
        else Assert.False(Charges(s.Kernel).ContainsKey(old));
        Assert.True(s.Kernel.ReleaseRegion(s.Target, moved).IsSuccess);
        Assert.Equal(0UL, Used(s.Kernel, s.TargetBudget.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PairOwnerRefusalRefundsProvisionalTargetAndKeepsSourceOwnership(bool blockBorrow)
    {
        var s = Create(2, 0);
        var blocked = blockBorrow ? s.Borrowed!.Handle : s.Payload.Handle;
        var use = s.Kernel.AcquireRegionUse(s.Source, blocked, RegionUseMode.ExclusiveWrite, new(0, 4)).Value!;
        var sourceCharge = Charges(s.Kernel)[s.Payload.Handle];
        var result = Transfer(s);
        Assert.Equal(KernelError.RegionUseConflict, result.Error);
        Assert.True(s.Payload.IsValid);
        Assert.True(s.Borrowed!.IsValid);
        Assert.Equal(RegionState.Owned, s.Kernel.Regions.Snapshot().Single(r => r.Handle == s.Borrowed.Handle).State);
        Assert.Equal(sourceCharge, Charges(s.Kernel)[s.Payload.Handle]);
        Assert.Equal(8UL, Used(s.Kernel, s.SourceBudget));
        Assert.Equal(0UL, Used(s.Kernel, s.TargetBudget!.Value));
        Assert.True(s.Kernel.ReleaseRegionUse(s.Source, use.Handle).IsSuccess);
    }

    [Fact]
    public async Task OverlappingQueuedMovesCannotOvercommitTargetMemoryBudget()
    {
        var s = Create(0, 0);
        var second = s.Kernel.AllocateBuffer<byte>(s.Source, 4).Value!;
        using var start = new ManualResetEventSlim(false);
        var calls = new[] { s.Payload, second }.Select(payload => Task.Run(() =>
        {
            start.Wait();
            return s.Kernel.Send(s.Source, s.Target, s.Endpoints.Left, 1, payload);
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(calls);
        Assert.Single(results, r => r.IsSuccess);
        Assert.Single(results, r => r.Error == KernelError.BudgetExceeded);
        Assert.Equal(4UL, Used(s.Kernel, s.SourceBudget));
        Assert.Equal(4UL, Used(s.Kernel, s.TargetBudget!.Value));
        Assert.Single(new[] { s.Payload, second }, payload => payload.IsValid);
        Assert.Equal(1, s.Kernel.Channels.InspectionSummary(s.Source).QueuedMessages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReentrantPreparationCannotTransferAfterProcessExit(bool exitSource)
    {
        var s = Create(0, 0);
        var old = s.Payload.Handle;
        var call = typeof(RuntimeKernel).GetMethod("TransferRegionForIpc", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var result = (KernelResult<RegionHandle>)call.Invoke(s.Kernel,
            [s.SourceProcess, s.TargetProcess, old, (Func<KernelResult>)(() =>
            {
                Assert.True(s.Kernel.TerminateProcess(exitSource ? s.Source : s.Target).IsSuccess);
                return KernelResult.Ok();
            })])!;
        Assert.Equal(KernelError.StaleHandle, result.Error);
        Assert.Equal(0UL, Used(s.Kernel, s.TargetBudget!.Value));
        Assert.DoesNotContain(Charges(s.Kernel).Keys, r => r.RegionId == old.RegionId && r != old);
        if (!exitSource) Assert.True(s.Payload.IsValid);
    }

    private static Scenario Create(int path, int targetMode)
    {
        var kernel = new RuntimeKernel();
        var (sourceProcess, source) = TestFixtures.Create(kernel, 601, 6001);
        var (targetProcess, target) = TestFixtures.Create(kernel, 602, 6002);
        var admin = TestFixtures.Create(kernel, 603, 6003).Handle;
        var cap = kernel.MintCapability(new(6003), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var sourceBudget = kernel.AdmitProcessBudget(admin, cap, source, "ipc-source",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 8), new(ServiceBudgetDimension.IpcMessages, 4), new(ServiceBudgetDimension.IpcBytes, 1024)]).Value!.ProcessBudget;
        BudgetAccountHandle? targetBudget = targetMode == 2 ? null : kernel.AdmitProcessBudget(admin, cap, target, "ipc-target",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, targetMode == 1 ? 1UL : 4UL), new(ServiceBudgetDimension.IpcMessages, 4), new(ServiceBudgetDimension.IpcBytes, 1024)]).Value!.ProcessBudget;
        var payload = kernel.AllocateBuffer<byte>(source, 4).Value!;
        var borrowed = path == 2 ? kernel.AllocateBuffer<byte>(source, 4).Value! : null;
        ProtocolDefinitionV1 protocol;
        if (path is 3 or 4)
            protocol = new("MemoryReturn", "memory-return", "Ready", null,
                [new(1, "Return", returnsOwnership: true, returnOwnershipPayloadKind: OwnershipPayloadKind.OwnedBuffer)], [new(1, "Ready", "Ready")]);
        else
        {
            RequestPayloadDescriptorV1 request = path == 2
                ? new(new OwnershipRequestDescriptorV1("borrow", OwnershipPayloadKind.OwnedBuffer, OwnershipRequestDisposition.Borrow),
                    new OwnershipRequestDescriptorV1("consume", OwnershipPayloadKind.OwnedBuffer, OwnershipRequestDisposition.Consume))
                : new(RequestPayloadKind.Ownership, "consume", typeof(OwnedBuffer<>).Namespace + ".OwnedBuffer", ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer);
            protocol = new("MemoryTransfer", "memory-transfer", "Ready", null,
                [new(1, "Transfer", consumes: ["consume"], borrows: path == 2 ? ["borrow"] : [], requestPayload: request)], [new(1, "Ready", "Ready")]);
        }
        var endpoints = path is 3 or 4
            ? kernel.CreateChannel(target, source, protocol, new ResponseProtocolDefinitionV1(protocol.ContractName, protocol.ContractDigest,
                [new(1, "Return", new(ResponsePayloadKind.Ownership, typeof(OwnedBuffer<>).Namespace + ".OwnedBuffer", ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer))]), 4).Value
            : kernel.CreateChannel(source, target, protocol, 4).Value;
        ulong sequence = 0;
        if (path == 3)
        {
            sequence = kernel.Send(target, source, endpoints.Left, 1).Value!.Sequence;
            Assert.True(kernel.Receive(source, endpoints.Right).IsSuccess);
        }
        return new(kernel, sourceProcess, targetProcess, source, target, sourceBudget, targetBudget, payload, borrowed, endpoints, path, sequence);
    }

    private static KernelResult<OwnedBuffer<byte>> Transfer(Scenario s)
    {
        if (s.Path == 1)
        {
            var inline = s.Kernel.Channels.BeginInlineMoveInvocation(s.SourceProcess, s.TargetProcess, s.Endpoints.Left, 1, s.Payload);
            return inline.IsSuccess ? KernelResult<OwnedBuffer<byte>>.Ok(inline.Value.Payload) : KernelResult<OwnedBuffer<byte>>.Fail(inline.Error, inline.Message!);
        }
        if (s.Path == 4)
        {
            var responses = (ResponseRegistry)typeof(RuntimeKernel).GetProperty("Responses", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
            return responses.TransferInlineOwnership(s.SourceProcess, s.TargetProcess, s.Endpoints.Right, 1, s.Payload);
        }
        if (s.Path == 3)
        {
            var response = s.Kernel.PublishResponse(s.Source, s.Endpoints.Right, s.Sequence, s.Payload);
            return response.IsSuccess ? KernelResult<OwnedBuffer<byte>>.Ok((OwnedBuffer<byte>)response.Value!.Payload!) : KernelResult<OwnedBuffer<byte>>.Fail(response.Error, response.Message!);
        }
        var send = s.Path == 2
            ? s.Kernel.SendOwnershipPair(s.Source, s.Target, s.Endpoints.Left, 1, s.Borrowed!, s.Payload)
            : s.Kernel.Send(s.Source, s.Target, s.Endpoints.Left, 1, s.Payload);
        return send.IsSuccess ? KernelResult<OwnedBuffer<byte>>.Ok((OwnedBuffer<byte>)(s.Path == 2 ? send.Value!.SecondaryPayload! : send.Value!.Payload!))
            : KernelResult<OwnedBuffer<byte>>.Fail(send.Error, send.Message!);
    }

    private static ulong Used(RuntimeKernel kernel, BudgetAccountHandle account) => kernel.QueryBudget(account).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used;
    private static Dictionary<RegionHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)> Charges(RuntimeKernel kernel) =>
        (Dictionary<RegionHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)>)typeof(RuntimeKernel).GetField("_regionBudgetReservations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
    private sealed record Scenario(RuntimeKernel Kernel, SingProcess SourceProcess, SingProcess TargetProcess, ProcessHandle Source, ProcessHandle Target,
        BudgetAccountHandle SourceBudget, BudgetAccountHandle? TargetBudget, OwnedBuffer<byte> Payload, OwnedBuffer<byte>? Borrowed,
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Endpoints, int Path, ulong Sequence);
}
