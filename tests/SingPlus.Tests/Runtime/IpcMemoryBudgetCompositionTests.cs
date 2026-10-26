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
        TransferClock clock = new TransferClock();
        RuntimeKernel kernel = new RuntimeKernel(null, clock);
        byte[] image = [9, 8];
        ServiceManifestV1 manifest = new ServiceManifestV1(new("session-response-observe"), new("1"),
            Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(image)).ToLowerInvariant(),
            TestFixtures.Manifest(8172, 817_200),
            budgetRequests: [new(ServiceBudgetDimension.OwnedMemoryBytes, 4096), new(ServiceBudgetDimension.TraceTelemetryBufferBytes, 8192)],
            telemetryPolicy: new(ServiceTelemetryVisibility.Self, 8192));
        ProcessHandle caller = kernel.AdmitComponent(new(manifest, image)).Value!.Process;
        ProcessHandle service = TestFixtures.Create(kernel, 8171, 817_100).Handle;
        ProtocolDefinitionV1 protocol = new ProtocolDefinitionV1("session-response-observe", "1", "Ready", null,
            [new(1, "Return", returnsOwnership: true, returnOwnershipPayloadKind: OwnershipPayloadKind.OwnedBuffer)],
            [new(1, "Ready", "Ready")]);
        ResponseProtocolDefinitionV1 responseProtocol = new ResponseProtocolDefinitionV1(protocol.ContractName, protocol.ContractDigest,
            [new(1, "Return", new(ResponsePayloadKind.Ownership, typeof(OwnedBuffer<>).Namespace + ".OwnedBuffer", ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer))]);
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(service, "session-response-observe",
            new(protocol.ContractName, "1", protocol.ContractDigest), protocol, responseProtocol).Value!;
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor).Value;
        Task<KernelResult<ResponseEnvelope>> responseTask = kernel.InvokeSessionAsync(caller, session, 1, payload: null).AsTask();
        EndpointSessionRequestEnvelope received = kernel.ReceiveSessionRequest(service, session).Value!;
        Assert.True(kernel.AcceptSessionInvocation(service, received.Invocation, allowInFlightCancellation: false).IsSuccess);
        OwnedBuffer<byte> payload = kernel.AllocateBuffer<byte>(service, 4).Value!;
        Assert.True(kernel.StartTraceSession(caller, 8).IsSuccess);
        bool observed = false;
        ResponseEnvelope? delivered = null;
        clock.Next = () =>
        {
            Task worker = Task.Run(() =>
            {
                Assert.Equal(ResponsePublicationStatus.Published,
                    kernel.SnapshotSessionInvocationConsequence(received.Invocation)!.Value.TerminalStatus);
                KernelResult<ResponseEnvelope> response = responseTask.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
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
        OwnedBuffer<byte> returned = Assert.IsType<OwnedBuffer<byte>>(delivered!.Payload);
        Assert.True(returned.IsValid);
        Assert.Equal(KernelError.ResponseNotPending, kernel.PublishSessionResponse(service, received.Invocation, payload).Error);
        Assert.Equal(ResponsePublicationStatus.Published,
            kernel.SnapshotSessionInvocationConsequence(received.Invocation)!.Value.TerminalStatus);
        Assert.True(kernel.ReleaseRegion(caller, returned).IsSuccess);
    }

    [Fact]
    public async Task MessagePermissionOwnerCommitOrdersRevokeAfterActualOwnershipPublication()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessHandle sender = TestFixtures.Create(kernel, 8151, 815_100).Handle;
        ProcessHandle receiver = TestFixtures.Create(kernel, 8152, 815_200).Handle;
        CapabilityId capability = kernel.MintCapability(new(815_100), sender, ResourceKind.Device, "message:move", CapabilityRights.Read).Value!.CapabilityId;
        RequestPayloadDescriptorV1 request = new RequestPayloadDescriptorV1(RequestPayloadKind.Ownership, "payload", typeof(OwnedBuffer<>).Namespace + ".OwnedBuffer", ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer);
        ProtocolDefinitionV1 protocol = new ProtocolDefinitionV1("message-commit", "1", "Ready", null,
            [new(1, "Move", requiredCapabilities: [new(ResourceKind.Device, "message:move", CapabilityRights.Read)], consumes: ["payload"], requestPayload: request)],
            [new(1, "Ready", "Ready")]);
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints = kernel.CreateChannel(sender, receiver, protocol, 4).Value;
        OwnedBuffer<byte> payload = kernel.AllocateBuffer<byte>(sender, 4).Value!;
        Task<KernelResult>? revoke = null;
        using ManualResetEventSlim started = new ManualResetEventSlim();
        KernelResult<ChannelEnvelope> sent = kernel.Channels.Send(kernel.Processes.Resolve(sender).Value!, kernel.Processes.Resolve(receiver).Value!, endpoints.Left, 1, payload, [capability],
            transferRegion: (source, target, region, _, noEffect, prepared) =>
            {
                revoke = Task.Run(() => { started.Set(); return kernel.CapabilityAuthority.Revoke(capability); });
                Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
                Assert.False(revoke.Wait(TimeSpan.FromMilliseconds(150)));
                return kernel.Regions.Transfer(region, new(source.DomainId, source.Generation), new(target.DomainId, target.Generation),prepared.Source,prepared);
            });
        Assert.True(sent.IsSuccess);
        Assert.True((await revoke!.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.False(payload.IsValid);
        KernelResult<ChannelEnvelope> received = kernel.Receive(receiver, endpoints.Right);
        Assert.True(received.IsSuccess);
        Assert.True(Assert.IsType<OwnedBuffer<byte>>(received.Value!.Payload).IsValid);
        Assert.Equal(sent.Value!.Sequence, received.Value.Sequence);
        OwnedBuffer<byte> untouched = kernel.AllocateBuffer<byte>(sender, 4).Value!;
        Assert.Equal(KernelError.MissingCapability, kernel.Send(sender, receiver, endpoints.Left, 1, untouched, [capability]).Error);
        Assert.True(untouched.IsValid);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public void QueuedTransferBudgetClockCanReceiveCommittedOwnershipOutsideKernelGates(bool pair, bool response)
    {
        TransferClock clock = new TransferClock();
        RuntimeKernel kernel = new RuntimeKernel(null, clock);
        byte[] image = [5, 6];
        ServiceManifestV1 manifest = new ServiceManifestV1(new("queued-observe"), new("1"),
            Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(image)).ToLowerInvariant(),
            TestFixtures.Manifest(8162, 816_200),
            budgetRequests: [new(ServiceBudgetDimension.OwnedMemoryBytes, 4096), new(ServiceBudgetDimension.TraceTelemetryBufferBytes, 8192)],
            telemetryPolicy: new(ServiceTelemetryVisibility.Self, 8192));
        ProcessHandle target = kernel.AdmitComponent(new(manifest, image)).Value!.Process;
        ProcessHandle source = TestFixtures.Create(kernel, 8161, 816_100).Handle;
        RequestPayloadDescriptorV1 request = pair
            ? new(new OwnershipRequestDescriptorV1("borrow", OwnershipPayloadKind.OwnedBuffer, OwnershipRequestDisposition.Borrow),
                new OwnershipRequestDescriptorV1("consume", OwnershipPayloadKind.OwnedBuffer, OwnershipRequestDisposition.Consume))
            : new(RequestPayloadKind.Ownership, "consume", typeof(OwnedBuffer<>).Namespace + ".OwnedBuffer", ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer);
        ProtocolDefinitionV1 protocol = new ProtocolDefinitionV1("queued-observe", "1", "Ready", null,
            [new(1, "Move", consumes: ["consume"], borrows: pair ? ["borrow"] : [], requestPayload: request)], [new(1, "Ready", "Ready")]);
        if (response) protocol = new("queued-observe", "1", "Ready", null,
            [new(1, "Return", returnsOwnership: true, returnOwnershipPayloadKind: OwnershipPayloadKind.OwnedBuffer)], [new(1, "Ready", "Ready")]);
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints = response
            ? kernel.CreateChannel(target, source, protocol, new ResponseProtocolDefinitionV1(protocol.ContractName, protocol.ContractDigest,
                [new(1, "Return", new(ResponsePayloadKind.Ownership, typeof(OwnedBuffer<>).Namespace + ".OwnedBuffer", ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer))]), 4).Value
            : kernel.CreateChannel(source, target, protocol, 4).Value;
        ulong requestSequence = 0;
        if (response)
        {
            requestSequence = kernel.Send(target, source, endpoints.Left, 1).Value!.Sequence;
            Assert.True(kernel.Receive(source, endpoints.Right).IsSuccess);
        }
        OwnedBuffer<byte> payload = kernel.AllocateBuffer<byte>(source, 4).Value!;
        OwnedBuffer<byte>? borrowed = pair ? kernel.AllocateBuffer<byte>(source, 4).Value! : null;
        Assert.True(kernel.StartTraceSession(target, 8).IsSuccess);
        ChannelEnvelope? received = null;
        ResponseEnvelope? responseReceived = null;
        clock.Next = () =>
        {
            Task<KernelResult> worker = Task.Run(() =>
            {
                if (response)
                {
                    KernelResult<ResponseEnvelope> result = kernel.ReceiveResponse(target, endpoints.Left);
                    Assert.True(result.IsSuccess);
                    responseReceived = result.Value;
                    return KernelResult.Ok();
                }
                KernelResult<ChannelEnvelope> receive = kernel.Receive(target, endpoints.Right);
                received = receive.Value;
                return receive.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(receive.Error, receive.Message!);
            });
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(worker.Result.IsSuccess);
        };
        if (response)
        {
            KernelResult<ResponseEnvelope> published = kernel.PublishResponse(source, endpoints.Right, requestSequence, payload);
            Assert.True(published.IsSuccess);
            Assert.NotNull(responseReceived);
            Assert.Equal(requestSequence, responseReceived!.RequestSequence);
            Assert.False(payload.IsValid);
            Assert.True(Assert.IsType<OwnedBuffer<byte>>(responseReceived.Payload).IsValid);
            Assert.Equal(KernelError.ResponseNotPending, kernel.PublishResponse(source, endpoints.Right, requestSequence, payload).Error);
            return;
        }
        KernelResult<ChannelEnvelope> sent = pair ? kernel.SendOwnershipPair(source, target, endpoints.Left, 1, borrowed!, payload)
            : kernel.Send(source, target, endpoints.Left, 1, payload);
        Assert.True(sent.IsSuccess);
        Assert.NotNull(received);
        Assert.Equal(sent.Value!.Sequence, received!.Sequence);
        Assert.False(payload.IsValid);
        OwnedBuffer<byte> moved = Assert.IsType<OwnedBuffer<byte>>(pair ? received.SecondaryPayload : received.Payload);
        Assert.True(moved.IsValid);
        if (pair) Assert.True(kernel.ReturnBorrow(target, Assert.IsType<BorrowLease<byte>>(received.Payload).Handle).IsSuccess);
    }

    private sealed class TransferClock : TimeProvider
    {
        internal Action? Next;
        public override long GetTimestamp() { Action? callback = Next; Next = null; callback?.Invoke(); return base.GetTimestamp(); }
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
        Scenario s = Create(path, targetMode);
        RegionHandle old = s.Payload.Handle;
        (ProcessHandle Owner, BudgetReservationHandle Reservation) sourceCharge = Charges(s.Kernel)[old];
        IReadOnlyList<RegionDescriptor> before = s.Kernel.Regions.Snapshot();
        KernelResult<OwnedBuffer<byte>> result = Transfer(s);
        if (targetMode != 0)
        {
            Assert.Equal(targetMode == 1 ? KernelError.BudgetExceeded : KernelError.BudgetNotConfigured, result.Error);
            Assert.True(s.Payload.IsValid);
            Assert.Equal(sourceCharge, Charges(s.Kernel)[old]);
            foreach (RegionDescriptor original in before)
            {
                RegionDescriptor after = s.Kernel.Regions.Snapshot().Single(r => r.Handle == original.Handle);
                Assert.Equal(original.State, after.State);
                Assert.Equal(original.Owner, after.Owner);
                Assert.Equal(original.MutationEpoch, after.MutationEpoch);
            }
            Assert.Equal(path == 2 ? 8UL : 4UL, Used(s.Kernel, s.SourceBudget));
            if (s.TargetBudget is { } targetBudget) Assert.Equal(0UL, Used(s.Kernel, targetBudget));
            return;
        }
        Assert.True(result.IsSuccess, result.Message);
        OwnedBuffer<byte> moved = result.Value!;
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
        Scenario s = Create(path, 0);
        RegionHandle old = s.Payload.Handle;
        (ProcessHandle Owner, BudgetReservationHandle Reservation) charge = Charges(s.Kernel)[old];
        Assert.True(s.Kernel.Budgets.QuarantineLease(s.Source, charge.Reservation).IsSuccess);
        KernelResult<OwnedBuffer<byte>> moved = Transfer(s);
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
        Scenario s = Create(0, 0);
        RegionHandle old = s.Payload.Handle;
        MethodInfo call = typeof(RuntimeKernel).GetMethod("TransferRegionForIpc", BindingFlags.Instance | BindingFlags.NonPublic)!;
        KernelResult<RegionHandle> result = (KernelResult<RegionHandle>)call.Invoke(s.Kernel,
            [s.SourceProcess, s.TargetProcess, old, (Func<KernelResult>)(() => throw new InvalidOperationException("injected-local-preparation-loss"))])!;
        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.True(s.Payload.IsValid);
        Assert.Equal(4UL, Used(s.Kernel, s.TargetBudget!.Value));
        IDictionary records = (IDictionary)typeof(ResourceBudgetAuthority).GetField("_reservations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel.Budgets)!;
        object targetRecord = Assert.Single(records.Values.Cast<object>(), record =>
            (ProcessHandle)record.GetType().GetProperty("Owner")!.GetValue(record)! == s.Target);
        BudgetReservationHandle reservation = (BudgetReservationHandle)targetRecord.GetType().GetProperty("Handle")!.GetValue(targetRecord)!;
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
        Scenario s = Create(0, 0);
        (SingProcess? peerProcess, ProcessHandle peer) = TestFixtures.Create(s.Kernel, 604, 6001);
        ProcessHandle admin = TestFixtures.Create(s.Kernel, 605, 6005).Handle;
        CapabilityId cap = s.Kernel.MintCapability(new(6005), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle peerBudget = s.Kernel.AdmitProcessBudget(admin, cap, peer, "cohort-peer",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4), new(ServiceBudgetDimension.IpcMessages, 4), new(ServiceBudgetDimension.IpcBytes, 1024)]).Value!.ProcessBudget;
        RegionHandle old = s.Payload.Handle;
        (ProcessHandle Owner, BudgetReservationHandle Reservation) payerCharge = Charges(s.Kernel)[old];
        if (quarantine) Assert.True(s.Kernel.Budgets.QuarantineLease(s.Source, payerCharge.Reservation).IsSuccess);
        ProtocolDefinitionV1 protocol = new ProtocolDefinitionV1("CohortMove", "cohort-move", "Ready", null,
            [new(1, "Move", consumes: ["payload"], requestPayload: new(RequestPayloadKind.Ownership, "payload",
                typeof(OwnedBuffer<>).Namespace + ".OwnedBuffer", ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer))], [new(1, "Ready", "Ready")]);
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoint = s.Kernel.CreateChannel(peer, s.Target, protocol, 4).Value;
        OwnedBuffer<byte> moved;
        if (path == 0)
        {
            KernelResult<ChannelEnvelope> sent = s.Kernel.Send(peer, s.Target, endpoint.Left, 1, s.Payload);
            Assert.True(sent.IsSuccess, sent.Message);
            moved = (OwnedBuffer<byte>)sent.Value!.Payload!;
        }
        else if (path == 1)
        {
            KernelResult<ChannelRegistry.InlineMoveProjection<byte>> inline = s.Kernel.Channels.BeginInlineMoveInvocation(peerProcess, s.TargetProcess, endpoint.Left, 1, s.Payload);
            Assert.True(inline.IsSuccess, inline.Message);
            moved = inline.Value.Payload;
        }
        else
        {
            KernelResult<OwnedBuffer<byte>> direct = s.Kernel.TransferRegion(peer, s.Target, s.Payload);
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
        Scenario s = Create(2, 0);
        RegionHandle blocked = blockBorrow ? s.Borrowed!.Handle : s.Payload.Handle;
        RegionUseDescriptor use = s.Kernel.AcquireRegionUse(s.Source, blocked, RegionUseMode.ExclusiveWrite, new(0, 4)).Value!;
        (ProcessHandle Owner, BudgetReservationHandle Reservation) sourceCharge = Charges(s.Kernel)[s.Payload.Handle];
        KernelResult<OwnedBuffer<byte>> result = Transfer(s);
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
        Scenario s = Create(0, 0);
        OwnedBuffer<byte> second = s.Kernel.AllocateBuffer<byte>(s.Source, 4).Value!;
        using ManualResetEventSlim start = new ManualResetEventSlim(false);
        Task<KernelResult<ChannelEnvelope>>[] calls = new[] { s.Payload, second }.Select(payload => Task.Run(() =>
        {
            start.Wait();
            return s.Kernel.Send(s.Source, s.Target, s.Endpoints.Left, 1, payload);
        })).ToArray();
        start.Set();
        KernelResult<ChannelEnvelope>[] results = await Task.WhenAll(calls);
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
        Scenario s = Create(0, 0);
        RegionHandle old = s.Payload.Handle;
        MethodInfo call = typeof(RuntimeKernel).GetMethod("TransferRegionForIpc", BindingFlags.Instance | BindingFlags.NonPublic)!;
        KernelResult<RegionHandle> result = (KernelResult<RegionHandle>)call.Invoke(s.Kernel,
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

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void LostSendTransferReplyRetainsOriginalProjectionAndIndependentCharges(int path)
    {
        Scenario s =Create(path,0);
        OwnedBuffer<byte> independent =path==2 ? s.Borrowed! : s.Kernel.AllocateBuffer<byte>(s.Source,4).Value!;
        independent.Span.Fill(99);
        s.Payload.Span.Fill(42);
        PreparedOwnedTransfer? original=null;
        int effects =0;
        MethodInfo producer =typeof(RuntimeKernel).GetMethod("TransferPreparedRegionForIpc",BindingFlags.Instance|BindingFlags.NonPublic)!;
        KernelResult<RegionHandle> LostReply(SingProcess source,SingProcess target,RegionHandle region,Func<KernelResult>? admission,Action noEffect,PreparedOwnedTransfer prepared)
        {
            original=prepared;
            effects++;
            KernelResult<RegionHandle> moved =(KernelResult<RegionHandle>)producer.Invoke(s.Kernel,[source,target,region,admission,noEffect,prepared])!;
            Assert.True(moved.IsSuccess,moved.Message);
            throw new IOException("lost-original-transfer-reply");
        }
        Assert.Throws<IOException>(() =>
        {
            if(path==1) s.Kernel.Channels.BeginInlineMoveInvocation(s.SourceProcess,s.TargetProcess,s.Endpoints.Left,1,s.Payload,LostReply);
            else if(path==2) s.Kernel.Channels.SendOwnershipPair(s.SourceProcess,s.TargetProcess,s.Endpoints.Left,1,s.Borrowed!,s.Payload,null,LostReply);
            else s.Kernel.Channels.Send(s.SourceProcess,s.TargetProcess,s.Endpoints.Left,1,s.Payload,null,transferRegion:LostReply);
        });
        Assert.Same(s.Payload,original!.Source);
        Assert.False(s.Payload.IsValid);
        OwnedBuffer<byte> moved =Assert.IsType<OwnedBuffer<byte>>(original.Projection);
        Assert.Equal((byte)42,moved.Span[0]);
        Assert.Equal(KernelError.RegionUseConflict,Transfer(s).Error);
        Assert.Equal(1,effects);
        Assert.Equal(4UL,Used(s.Kernel,s.SourceBudget));
        Assert.Equal(4UL,Used(s.Kernel,s.TargetBudget!.Value));
        Assert.True(s.Kernel.ReleaseRegion(s.Target,moved).IsSuccess);
        Assert.Equal(0UL,Used(s.Kernel,s.TargetBudget.Value));
        Assert.Equal(4UL,Used(s.Kernel,s.SourceBudget));
        Assert.True(independent.IsValid);
        if(path!=2)
        {
            Assert.Equal((byte)99,independent.Span[0]);
            Assert.True(s.Kernel.ReleaseRegion(s.Source,independent).IsSuccess);
            Assert.Equal(0UL,Used(s.Kernel,s.SourceBudget));
        }
        else Assert.Equal(RegionState.Loaned,s.Kernel.Regions.Snapshot().Single(r=>r.Handle==independent.Handle).State);
        Assert.Equal(KernelError.RegionUseConflict,Transfer(s).Error); // Result/release is not pending publication closure.
    }
    [Fact]
    public void ActualSendLateCorrelationFailureRetainsOriginalIpcChargeAndForeignQueue()
    {
        Scenario s =Create(0,0);
        ProtocolDefinitionV1 foreignProtocol =new ProtocolDefinitionV1("foreign-queue","1","Ready",null,[new(1,"Ping")],[new(1,"Ready","Ready")]);
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) foreign =s.Kernel.CreateChannel(s.Source,s.Target,foreignProtocol,4).Value;
        Assert.True(s.Kernel.Send(s.Source,s.Target,foreign.Left,1).IsSuccess);
        IDictionary routes =(IDictionary)typeof(RuntimeKernel).GetField("_ipcBudgetReservations",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        (ChannelId ChannelId, ulong) foreignKey =(foreign.Left.ChannelId,1UL);
        (ChannelId ChannelId, ulong) collidedKey =(s.Endpoints.Left.ChannelId,1UL);
        object foreignFact =routes[foreignKey]!;
        routes[collidedKey]=foreignFact; // Existing foreign routing cannot justify compensation.
        Assert.Throws<ArgumentException>(()=>s.Kernel.Send(s.Source,s.Target,s.Endpoints.Left,1,s.Payload));
        Assert.Equal(foreignFact,routes[foreignKey]);
        Assert.Equal(foreignFact,routes[collidedKey]);
        Assert.False(s.Payload.IsValid);
        IDictionary records =(IDictionary)typeof(ChannelRegistry).GetField("_ownedChannels",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(s.Kernel.Channels)!;
        object original =records[s.Endpoints.Left.ChannelId]!;
        (ProcessHandle Owner, BudgetReservationHandle Reservation) pending =((ProcessHandle Owner,BudgetReservationHandle Reservation))original.GetType().GetProperty("PendingSendBudget")!.GetValue(original)!;
        Assert.Equal(s.Source,pending.Owner);
        Assert.Equal(BudgetReservationState.Quarantined,s.Kernel.QueryBudget(pending.Reservation).Value!.State);
        Assert.Equal(KernelError.RegionUseConflict,s.Kernel.Send(s.Source,s.Target,s.Endpoints.Left,1,s.Payload).Error);
        Assert.Equal(4UL,Used(s.Kernel,s.TargetBudget!.Value));
        RegionDescriptor region =s.Kernel.Regions.Snapshot().Single(r=>r.Owner==new RegionOwner(s.TargetProcess.DomainId,s.Target.Generation));
        OwnedBuffer<byte> moved =Assert.IsType<OwnedBuffer<byte>>(s.Kernel.Regions.CaptureTransferPayload(region.Handle,region.Owner).Value);
        Assert.True(s.Kernel.ReleaseRegion(s.Target,moved).IsSuccess);
        Assert.Equal(0UL,Used(s.Kernel,s.TargetBudget.Value));
        Assert.True(s.Kernel.Receive(s.Target,foreign.Right).IsSuccess);
        Assert.Equal(BudgetReservationState.Quarantined,s.Kernel.QueryBudget(pending.Reservation).Value!.State);
        Assert.NotNull(original.GetType().GetProperty("PendingSendBudget")!.GetValue(original));
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void TargetQuantitativeRouteCollisionCannotPublishTransferOrEraseSourceCharge(int path)
    {
        Scenario s =Create(path,0);
        RegionHandle old =s.Payload.Handle;
        (ProcessHandle Owner, BudgetReservationHandle Reservation) sourceCharge =Charges(s.Kernel)[old];
        RegionHandle next =new RegionHandle(old.RegionId,new RegionGeneration(old.Generation.Value+1));
        Charges(s.Kernel)[next]=sourceCharge;
        ProcessHandle foreign =TestFixtures.Create(s.Kernel,617,6170).Handle;
        OwnedBuffer<byte> independent =s.Kernel.AllocateBuffer<byte>(foreign,4).Value!;
        independent.Span.Fill(99);
        KernelResult<OwnedBuffer<byte>> result =Transfer(s);
        Assert.Equal(KernelError.PlatformFaulted,result.Error);
        Assert.False(s.Payload.IsValid); // Region effect happened; failed composition is not no-effect.
        Assert.Equal(sourceCharge,Charges(s.Kernel)[old]);
        Assert.Equal(sourceCharge,Charges(s.Kernel)[next]);
        Assert.Equal(BudgetReservationState.Active,s.Kernel.QueryBudget(sourceCharge.Reservation).Value!.State);
        Assert.Equal(path==2 ? 8UL : 4UL,Used(s.Kernel,s.SourceBudget));
        Assert.Equal(4UL,Used(s.Kernel,s.TargetBudget!.Value));
        (object Fact, object? RegionOwner, ProcessHandle Owner, BudgetReservationHandle Reservation) target =Assert.Single(s.Kernel.Budgets.OriginalRegionCharges(next));
        Assert.Equal(s.Target,target.Owner);
        Assert.Equal(BudgetReservationState.Quarantined,s.Kernel.QueryBudget(target.Reservation).Value!.State);
        RegionDescriptor actual =s.Kernel.Regions.Snapshot().Single(r=>r.Handle.RegionId==old.RegionId);
        Assert.Equal(next,actual.Handle);
        Assert.False(Transfer(s).IsSuccess);
        Assert.Equal(actual,s.Kernel.Regions.Snapshot().Single(r=>r.Handle.RegionId==old.RegionId));
        Assert.Equal((byte)99,independent.Span[0]);
        Assert.True(s.Kernel.ReleaseRegion(foreign,independent).IsSuccess);
        Assert.Equal(4UL,Used(s.Kernel,s.TargetBudget.Value));
        Assert.Equal(sourceCharge,Charges(s.Kernel)[next]);
    }
    private static Scenario Create(int path, int targetMode)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess? sourceProcess, ProcessHandle source) = TestFixtures.Create(kernel, 601, 6001);
        (SingProcess? targetProcess, ProcessHandle target) = TestFixtures.Create(kernel, 602, 6002);
        ProcessHandle admin = TestFixtures.Create(kernel, 603, 6003).Handle;
        CapabilityId cap = kernel.MintCapability(new(6003), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle sourceBudget = kernel.AdmitProcessBudget(admin, cap, source, "ipc-source",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 8), new(ServiceBudgetDimension.IpcMessages, 4), new(ServiceBudgetDimension.IpcBytes, 1024)]).Value!.ProcessBudget;
        BudgetAccountHandle? targetBudget = targetMode == 2 ? null : kernel.AdmitProcessBudget(admin, cap, target, "ipc-target",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, targetMode == 1 ? 1UL : 4UL), new(ServiceBudgetDimension.IpcMessages, 4), new(ServiceBudgetDimension.IpcBytes, 1024)]).Value!.ProcessBudget;
        OwnedBuffer<byte> payload = kernel.AllocateBuffer<byte>(source, 4).Value!;
        OwnedBuffer<byte>? borrowed = path == 2 ? kernel.AllocateBuffer<byte>(source, 4).Value! : null;
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
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints = path is 3 or 4
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
            KernelResult<ChannelRegistry.InlineMoveProjection<byte>> inline = s.Kernel.Channels.BeginInlineMoveInvocation(s.SourceProcess, s.TargetProcess, s.Endpoints.Left, 1, s.Payload);
            return inline.IsSuccess ? KernelResult<OwnedBuffer<byte>>.Ok(inline.Value.Payload) : KernelResult<OwnedBuffer<byte>>.Fail(inline.Error, inline.Message!);
        }
        if (s.Path == 4)
        {
            ResponseRegistry responses = (ResponseRegistry)typeof(RuntimeKernel).GetProperty("Responses", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
            return responses.TransferInlineOwnership(s.SourceProcess, s.TargetProcess, s.Endpoints.Right, 1, s.Payload);
        }
        if (s.Path == 3)
        {
            KernelResult<ResponseEnvelope> response = s.Kernel.PublishResponse(s.Source, s.Endpoints.Right, s.Sequence, s.Payload);
            return response.IsSuccess ? KernelResult<OwnedBuffer<byte>>.Ok((OwnedBuffer<byte>)response.Value!.Payload!) : KernelResult<OwnedBuffer<byte>>.Fail(response.Error, response.Message!);
        }
        KernelResult<ChannelEnvelope> send = s.Path == 2
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





