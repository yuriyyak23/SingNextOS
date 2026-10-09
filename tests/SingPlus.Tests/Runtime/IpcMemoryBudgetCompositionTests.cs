using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class IpcMemoryBudgetCompositionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SessionResponseBudgetObservationSeesCommittedInvocationEvenWhenItThrows(bool throws)
    {
        var clock = new TransferClock();
        var kernel = new RuntimeKernel(null, clock);
        byte[] image = [9, 8];
        var manifest = new ServiceManifestV1(new("session-response-observe"), new("1"),
            Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(image)).ToLowerInvariant(),
            TestFixtures.Manifest(8172, 817_200),
            budgetRequests: [new(ServiceBudgetDimension.OwnedMemoryBytes, 4096), new(ServiceBudgetDimension.TraceTelemetryBufferBytes, 8192)],
            telemetryPolicy: new(ServiceTelemetryVisibility.Self, 8192));
        var caller = kernel.AdmitComponent(new(manifest, image)).Value!.Process;
        var service = TestFixtures.Create(kernel, 8171, 817_100).Handle;
        var protocol = new ProtocolDefinitionV1("session-response-observe", "1", "Ready", null,
            [new(1, "Return", returnsOwnership: true, returnOwnershipPayloadKind: OwnershipPayloadKind.OwnedBuffer)],
            [new(1, "Ready", "Ready")]);
        var responseProtocol = new ResponseProtocolDefinitionV1(protocol.ContractName, protocol.ContractDigest,
            [new(1, "Return", new(ResponsePayloadKind.Ownership, typeof(OwnedBuffer<>).Namespace + ".OwnedBuffer", ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer))]);
        var descriptor = kernel.RegisterService(service, "session-response-observe",
            new(protocol.ContractName, "1", protocol.ContractDigest), protocol, responseProtocol).Value!;
        var session = kernel.OpenSession(caller, descriptor).Value;
        var responseTask = kernel.InvokeSessionAsync(caller, session, 1, payload: null).AsTask();
        var received = kernel.ReceiveSessionRequest(service, session).Value!;
        Assert.True(kernel.AcceptSessionInvocation(service, received.Invocation, allowInFlightCancellation: false).IsSuccess);
        var payload = kernel.AllocateBuffer<byte>(service, 4).Value!;
        Assert.True(kernel.StartTraceSession(caller, 8).IsSuccess);
        var observed = false;
        ResponseEnvelope? delivered = null;
        clock.Next = () =>
        {
            var worker = Task.Run(() =>
            {
                Assert.Equal(ResponsePublicationStatus.Published,
                    kernel.SnapshotSessionInvocationConsequence(received.Invocation)!.Value.TerminalStatus);
                var response = responseTask.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                Assert.True(response.IsSuccess, response.Message);
                delivered = response.Value;
            });
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
            observed = true;
            if (throws) throw new InvalidOperationException("post-commit observation");
        };
        Assert.True(kernel.PublishSessionResponse(service, received.Invocation, payload).IsSuccess);
        Assert.True(observed);
        Assert.False(payload.IsValid);
        var returned = Assert.IsType<OwnedBuffer<byte>>(delivered!.Payload);
        Assert.True(returned.IsValid);
        Assert.Equal(KernelError.ResponseNotPending, kernel.PublishSessionResponse(service, received.Invocation, payload).Error);
        Assert.Equal(ResponsePublicationStatus.Published,
            kernel.SnapshotSessionInvocationConsequence(received.Invocation)!.Value.TerminalStatus);
        Assert.True(kernel.ReleaseRegion(caller, returned).IsSuccess);
    }

    [Fact]
    public async Task MessagePermissionOwnerCommitOrdersRevokeAfterActualOwnershipPublication()
    {
        var kernel = new RuntimeKernel();
        var sender = TestFixtures.Create(kernel, 8151, 815_100).Handle;
        var receiver = TestFixtures.Create(kernel, 8152, 815_200).Handle;
        var capability = kernel.MintCapability(new(815_100), sender, ResourceKind.Device, "message:move", CapabilityRights.Read).Value!.CapabilityId;
        var request = new RequestPayloadDescriptorV1(RequestPayloadKind.Ownership, "payload", typeof(OwnedBuffer<>).Namespace + ".OwnedBuffer", ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer);
        var protocol = new ProtocolDefinitionV1("message-commit", "1", "Ready", null,
            [new(1, "Move", requiredCapabilities: [new(ResourceKind.Device, "message:move", CapabilityRights.Read)], consumes: ["payload"], requestPayload: request)],
            [new(1, "Ready", "Ready")]);
        var endpoints = kernel.CreateChannel(sender, receiver, protocol, 4).Value;
        var payload = kernel.AllocateBuffer<byte>(sender, 4).Value!;
        Task<KernelResult>? revoke = null;
        using var started = new ManualResetEventSlim();
        var sent = kernel.Channels.Send(kernel.Processes.Resolve(sender).Value!, kernel.Processes.Resolve(receiver).Value!, endpoints.Left, 1, payload, [capability],
            transferRegion: (source, target, region, _) =>
            {
                revoke = Task.Run(() => { started.Set(); return kernel.CapabilityAuthority.Revoke(capability); });
                Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
                Assert.False(revoke.Wait(TimeSpan.FromMilliseconds(150)));
                return kernel.Regions.Transfer(region, new(source.DomainId, source.Generation), new(target.DomainId, target.Generation));
            });
        Assert.True(sent.IsSuccess);
        Assert.True((await revoke!.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.False(payload.IsValid);
        var received = kernel.Receive(receiver, endpoints.Right);
        Assert.True(received.IsSuccess);
        Assert.True(Assert.IsType<OwnedBuffer<byte>>(received.Value!.Payload).IsValid);
        Assert.Equal(sent.Value!.Sequence, received.Value.Sequence);
        var untouched = kernel.AllocateBuffer<byte>(sender, 4).Value!;
        Assert.Equal(KernelError.MissingCapability, kernel.Send(sender, receiver, endpoints.Left, 1, untouched, [capability]).Error);
        Assert.True(untouched.IsValid);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public void QueuedTransferBudgetClockCanReceiveCommittedOwnershipOutsideKernelGates(bool pair, bool response)
    {
        var clock = new TransferClock();
        var kernel = new RuntimeKernel(null, clock);
        byte[] image = [5, 6];
        var manifest = new ServiceManifestV1(new("queued-observe"), new("1"),
            Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(image)).ToLowerInvariant(),
            TestFixtures.Manifest(8162, 816_200),
            budgetRequests: [new(ServiceBudgetDimension.OwnedMemoryBytes, 4096), new(ServiceBudgetDimension.TraceTelemetryBufferBytes, 8192)],
            telemetryPolicy: new(ServiceTelemetryVisibility.Self, 8192));
        var target = kernel.AdmitComponent(new(manifest, image)).Value!.Process;
        var source = TestFixtures.Create(kernel, 8161, 816_100).Handle;
        RequestPayloadDescriptorV1 request = pair
            ? new(new OwnershipRequestDescriptorV1("borrow", OwnershipPayloadKind.OwnedBuffer, OwnershipRequestDisposition.Borrow),
                new OwnershipRequestDescriptorV1("consume", OwnershipPayloadKind.OwnedBuffer, OwnershipRequestDisposition.Consume))
            : new(RequestPayloadKind.Ownership, "consume", typeof(OwnedBuffer<>).Namespace + ".OwnedBuffer", ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer);
        var protocol = new ProtocolDefinitionV1("queued-observe", "1", "Ready", null,
            [new(1, "Move", consumes: ["consume"], borrows: pair ? ["borrow"] : [], requestPayload: request)], [new(1, "Ready", "Ready")]);
        if (response) protocol = new("queued-observe", "1", "Ready", null,
            [new(1, "Return", returnsOwnership: true, returnOwnershipPayloadKind: OwnershipPayloadKind.OwnedBuffer)], [new(1, "Ready", "Ready")]);
        var endpoints = response
            ? kernel.CreateChannel(target, source, protocol, new ResponseProtocolDefinitionV1(protocol.ContractName, protocol.ContractDigest,
                [new(1, "Return", new(ResponsePayloadKind.Ownership, typeof(OwnedBuffer<>).Namespace + ".OwnedBuffer", ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer))]), 4).Value
            : kernel.CreateChannel(source, target, protocol, 4).Value;
        ulong requestSequence = 0;
        if (response)
        {
            requestSequence = kernel.Send(target, source, endpoints.Left, 1).Value!.Sequence;
            Assert.True(kernel.Receive(source, endpoints.Right).IsSuccess);
        }
        var payload = kernel.AllocateBuffer<byte>(source, 4).Value!;
        var borrowed = pair ? kernel.AllocateBuffer<byte>(source, 4).Value! : null;
        Assert.True(kernel.StartTraceSession(target, 8).IsSuccess);
        ChannelEnvelope? received = null;
        ResponseEnvelope? responseReceived = null;
        clock.Next = () =>
        {
            var worker = Task.Run(() =>
            {
                if (response)
                {
                    var result = kernel.ReceiveResponse(target, endpoints.Left);
                    Assert.True(result.IsSuccess);
                    responseReceived = result.Value;
                    return KernelResult.Ok();
                }
                var receive = kernel.Receive(target, endpoints.Right);
                received = receive.Value;
                return receive.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(receive.Error, receive.Message!);
            });
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(worker.Result.IsSuccess);
        };
        if (response)
        {
            var published = kernel.PublishResponse(source, endpoints.Right, requestSequence, payload);
            Assert.True(published.IsSuccess);
            Assert.NotNull(responseReceived);
            Assert.Equal(requestSequence, responseReceived!.RequestSequence);
            Assert.False(payload.IsValid);
            Assert.True(Assert.IsType<OwnedBuffer<byte>>(responseReceived.Payload).IsValid);
            Assert.Equal(KernelError.ResponseNotPending, kernel.PublishResponse(source, endpoints.Right, requestSequence, payload).Error);
            return;
        }
        var sent = pair ? kernel.SendOwnershipPair(source, target, endpoints.Left, 1, borrowed!, payload)
            : kernel.Send(source, target, endpoints.Left, 1, payload);
        Assert.True(sent.IsSuccess);
        Assert.NotNull(received);
        Assert.Equal(sent.Value!.Sequence, received!.Sequence);
        Assert.False(payload.IsValid);
        var moved = Assert.IsType<OwnedBuffer<byte>>(pair ? received.SecondaryPayload : received.Payload);
        Assert.True(moved.IsValid);
        if (pair) Assert.True(kernel.ReturnBorrow(target, Assert.IsType<BorrowLease<byte>>(received.Payload).Handle).IsSuccess);
    }

    private sealed class TransferClock : TimeProvider
    {
        internal Action? Next;
        public override long GetTimestamp() { var callback = Next; Next = null; callback?.Invoke(); return base.GetTimestamp(); }
    }

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
