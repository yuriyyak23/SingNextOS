using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Contracts;

public sealed class ResponsePublicationRuntimeTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CohortResponseRetirementRequiresOriginalRegionAndFinalDomainSettlement(bool substitutedEvidence)
    {
        (RuntimeKernel? kernel, ProcessHandle left, ProcessHandle right, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints) = CreateChannel();
        ProcessHandle peer =TestFixtures.Create(kernel,403,410).Handle;
        CapabilityId cap =kernel.MintCapability(new(410),left,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget =kernel.AdmitProcessBudget(left,cap,left,"cohort-polling",[new(ServiceBudgetDimension.OwnedMemoryBytes,64)]).Value!.ProcessBudget;
        OwnedBuffer<int> independent =kernel.AllocateBuffer<int>(right,4).Value!;independent.Span.Fill(99);
        OwnedBuffer<int> bytes =kernel.AllocateBuffer<int>(right,4).Value!;bytes.Span.Fill(42);
        ChannelEnvelope request =kernel.Send(left,right,endpoints.Left,3).Value!;Assert.True(kernel.Receive(right,endpoints.Right).IsSuccess);
        KernelResult<ResponseEnvelope> raw =kernel.PublishResponse(right,endpoints.Right,request.Sequence,bytes);Assert.True(raw.IsSuccess);
        OwnedBuffer<int> projection =Assert.IsType<OwnedBuffer<int>>(raw.Value!.Payload);
        Assert.True(kernel.Channels.Close(endpoints.Left).IsSuccess);
        const global::System.Reflection.BindingFlags flags=global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic;
        object responses =typeof(RuntimeKernel).GetProperty("Responses",flags)!.GetValue(kernel)!;
        IDictionary retained =(global::System.Collections.IDictionary)responses.GetType().GetField("_ownedChannels",flags)!.GetValue(responses)!;
        object channel =retained[endpoints.Left.ChannelId]!;
        IDictionary completions =(global::System.Collections.IDictionary)channel.GetType().GetProperty("OwnedCompletions")!.GetValue(channel)!;
        object completion =completions[request.Sequence]!;
        PropertyInfo region =completion.GetType().GetProperty("OriginalPublishedRegion")!; PropertyInfo owner =completion.GetType().GetProperty("OriginalPublishedRegionOwner")!;
        object? originalRegion =region.GetValue(completion); object? originalOwner =owner.GetValue(completion);
        if(substitutedEvidence)
        {
            OwnedBuffer<int> released =kernel.AllocateBuffer<int>(right,4).Value!; object? evidence =kernel.Regions.CaptureOriginalAccountingOwner(released.Handle);
            Assert.True(kernel.ReleaseRegion(right,released).IsSuccess);region.SetValue(completion,released.Handle);owner.SetValue(completion,evidence);
        }
        Assert.True(kernel.TerminateProcess(left).IsSuccess);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==projection.Handle).State);
        Assert.Equal(16UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.True(retained.Contains(endpoints.Left.ChannelId));
        region.SetValue(completion,originalRegion);owner.SetValue(completion,originalOwner);
        Assert.True(kernel.TerminateProcess(peer).IsSuccess);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==projection.Handle).State);
        Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.False(retained.Contains(endpoints.Left.ChannelId));Assert.All(independent.Span.ToArray(),b=>Assert.Equal(99,b));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RequesterExitRetiresOnlyReclaimedClosedOwnershipResponse(bool pinnedUse)
    {
        (RuntimeKernel? kernel, ProcessHandle left, ProcessHandle right, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints) = CreateChannel();
        CapabilityId cap =kernel.MintCapability(new(410),left,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget =kernel.AdmitProcessBudget(left,cap,left,"exit-polling",[new(ServiceBudgetDimension.OwnedMemoryBytes,64)]).Value!.ProcessBudget;
        OwnedBuffer<int> foreign =kernel.AllocateBuffer<int>(right,4).Value!;foreign.Span.Fill(99);
        OwnedBuffer<int> bytes =kernel.AllocateBuffer<int>(right,4).Value!;bytes.Span.Fill(42);
        ChannelEnvelope request =kernel.Send(left,right,endpoints.Left,3).Value!;Assert.True(kernel.Receive(right,endpoints.Right).IsSuccess);
        KernelResult<ResponseEnvelope> published =kernel.PublishResponse(right,endpoints.Right,request.Sequence,bytes);Assert.True(published.IsSuccess);
        OwnedBuffer<int> projection =Assert.IsType<OwnedBuffer<int>>(published.Value!.Payload);
        RegionOwner principal =new RegionOwner(new(410),left.Generation);
        Assert.True(kernel.Channels.Close(endpoints.Left).IsSuccess);
        const global::System.Reflection.BindingFlags flags=global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic;
        object responses =typeof(RuntimeKernel).GetProperty("Responses",flags)!.GetValue(kernel)!;
        IDictionary retained =(global::System.Collections.IDictionary)responses.GetType().GetField("_ownedChannels",flags)!.GetValue(responses)!;
        Assert.True(retained.Contains(endpoints.Left.ChannelId));
        if(pinnedUse)
        {
            (object Fact, object? RegionOwner, ProcessHandle Owner, BudgetReservationHandle Reservation) charge =Assert.Single(kernel.Budgets.OriginalRegionCharges(projection.Handle));
            IDictionary reservations =(global::System.Collections.IDictionary)kernel.Budgets.GetType().GetField("_reservations",flags)!.GetValue(kernel.Budgets)!;
            object original =reservations[charge.Reservation.ReservationId]!;reservations.Remove(charge.Reservation.ReservationId);
            Assert.False(kernel.TerminateProcess(left).IsSuccess);Assert.True(retained.Contains(endpoints.Left.ChannelId));
            Assert.Equal(16UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            reservations[charge.Reservation.ReservationId]=original;
        }
        KernelResult exited =kernel.TerminateProcess(left);Assert.True(exited.IsSuccess,exited.Message);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==projection.Handle).State);
        Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.False(retained.Contains(endpoints.Left.ChannelId));
        Assert.False(kernel.ReceiveResponse(left,endpoints.Left).IsSuccess);Assert.All(foreign.Span.ToArray(),b=>Assert.Equal(99,b));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ForeignReleasedRegionEvidenceCannotBypassOriginalExitSettlement(bool substitute)
    {
        (RuntimeKernel? kernel, ProcessHandle left, ProcessHandle right, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints) = CreateChannel();
        CapabilityId cap =kernel.MintCapability(new(410),left,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget =kernel.AdmitProcessBudget(left,cap,left,"exit-polling",[new(ServiceBudgetDimension.OwnedMemoryBytes,64)]).Value!.ProcessBudget;
        OwnedBuffer<int> foreign =kernel.AllocateBuffer<int>(right,4).Value!;foreign.Span.Fill(99);
        OwnedBuffer<int> bytes =kernel.AllocateBuffer<int>(right,4).Value!;bytes.Span.Fill(42);
        ChannelEnvelope request =kernel.Send(left,right,endpoints.Left,3).Value!;Assert.True(kernel.Receive(right,endpoints.Right).IsSuccess);
        KernelResult<ResponseEnvelope> published =kernel.PublishResponse(right,endpoints.Right,request.Sequence,bytes);Assert.True(published.IsSuccess);
        OwnedBuffer<int> projection =Assert.IsType<OwnedBuffer<int>>(published.Value!.Payload);
        RegionOwner principal =new RegionOwner(new(410),left.Generation);
        Assert.True(kernel.Channels.Close(endpoints.Left).IsSuccess);
        const global::System.Reflection.BindingFlags flags=global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic;
        object responses =typeof(RuntimeKernel).GetProperty("Responses",flags)!.GetValue(kernel)!;
        IDictionary retained =(global::System.Collections.IDictionary)responses.GetType().GetField("_ownedChannels",flags)!.GetValue(responses)!;
        Assert.True(retained.Contains(endpoints.Left.ChannelId));
        if(true)
        {
            object record =retained[endpoints.Left.ChannelId]!;
            IDictionary completions =(global::System.Collections.IDictionary)record.GetType().GetProperty("OwnedCompletions")!.GetValue(record)!;
            object completion =completions[request.Sequence]!;
            PropertyInfo regionProperty =completion.GetType().GetProperty("OriginalPublishedRegion")!;
            PropertyInfo ownerProperty =completion.GetType().GetProperty("OriginalPublishedRegionOwner")!;
            object? originalRegion =regionProperty.GetValue(completion); object? originalOwner =ownerProperty.GetValue(completion);
            if(substitute)
            {
                OwnedBuffer<int> released =kernel.AllocateBuffer<int>(right,4).Value!;
                object? otherOwner =kernel.Regions.CaptureOriginalAccountingOwner(released.Handle);
                Assert.True(kernel.ReleaseRegion(right,released).IsSuccess);
                regionProperty.SetValue(completion,released.Handle);ownerProperty.SetValue(completion,otherOwner);
            }
            (object Fact, object? RegionOwner, ProcessHandle Owner, BudgetReservationHandle Reservation) charge =Assert.Single(kernel.Budgets.OriginalRegionCharges(projection.Handle));
            IDictionary reservations =(global::System.Collections.IDictionary)kernel.Budgets.GetType().GetField("_reservations",flags)!.GetValue(kernel.Budgets)!;
            object original =reservations[charge.Reservation.ReservationId]!;reservations.Remove(charge.Reservation.ReservationId);
            Assert.False(kernel.TerminateProcess(left).IsSuccess);Assert.True(retained.Contains(endpoints.Left.ChannelId));
            Assert.Equal(16UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            regionProperty.SetValue(completion,originalRegion);ownerProperty.SetValue(completion,originalOwner);
            reservations[charge.Reservation.ReservationId]=original;
        }
        KernelResult exited =kernel.TerminateProcess(left);Assert.True(exited.IsSuccess,exited.Message);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==projection.Handle).State);
        Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.False(retained.Contains(endpoints.Left.ChannelId));
        Assert.False(kernel.ReceiveResponse(left,endpoints.Left).IsSuccess);Assert.All(foreign.Span.ToArray(),b=>Assert.Equal(99,b));
    }
    [Fact]
    public void PollingOwnershipResponseRemainsConsumableAfterOriginalChannelClose()
    {
        (RuntimeKernel? kernel, ProcessHandle left, ProcessHandle right, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints) = CreateChannel();
        CapabilityId cap =kernel.MintCapability(new(410),left,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget =kernel.AdmitProcessBudget(left,cap,left,"closed-polling",[new(ServiceBudgetDimension.OwnedMemoryBytes,64)]).Value!.ProcessBudget;
        OwnedBuffer<int> bytes =kernel.AllocateBuffer<int>(right,4).Value!;bytes.Span.Fill(42);
        ChannelEnvelope request =kernel.Send(left,right,endpoints.Left,3).Value!;Assert.True(kernel.Receive(right,endpoints.Right).IsSuccess);
        Assert.True(kernel.PublishResponse(right,endpoints.Right,request.Sequence,bytes).IsSuccess);
        Assert.False(kernel.ReceiveResponse(left,endpoints.Left with {Generation=endpoints.Left.Generation+1}).IsSuccess);
        Assert.True(kernel.Channels.Close(endpoints.Left).IsSuccess);
        Assert.False(kernel.ReceiveResponse(left,endpoints.Left with {Generation=endpoints.Left.Generation+1}).IsSuccess);
        Assert.Equal(16UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.False(kernel.ReceiveResponse(right,endpoints.Left).IsSuccess);
        KernelResult<ResponseEnvelope> received =kernel.ReceiveResponse(left,endpoints.Left);Assert.True(received.IsSuccess,received.Message);
        OwnedBuffer<int> output =Assert.IsType<OwnedBuffer<int>>(received.Value!.Payload);Assert.All(output.Span.ToArray(),b=>Assert.Equal(42,b));
        Assert.False(kernel.ReceiveResponse(left,endpoints.Left).IsSuccess);
        Assert.True(kernel.ReleaseRegion(left,output).IsSuccess);
        Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }
    private readonly record struct Packet(int PayloadSize, int MaxPayloadSize) : IBoundedPayload;

    [Fact]
    [Trait("Category", "Runtime")]
    public void PrimitiveResponseIsValidatedBeforeClientVisiblePublication()
    {
        (RuntimeKernel? kernel, ProcessHandle left, ProcessHandle right, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints) = CreateChannel();
        KernelResult<ChannelEnvelope> sent = kernel.Send(left, right, endpoints.Left, 1);
        Assert.True(sent.IsSuccess, sent.Message);

        KernelResult<ResponseEnvelope> early = kernel.PublishResponse(right, endpoints.Right, sent.Value!.Sequence, 7);
        Assert.False(early.IsSuccess);
        Assert.Equal(KernelError.ResponseNotDelivered, early.Error);

        Assert.True(kernel.Receive(right, endpoints.Right).IsSuccess);
        KernelResult<ResponseEnvelope> wrongType = kernel.PublishResponse(right, endpoints.Right, sent.Value.Sequence, 7u);
        Assert.False(wrongType.IsSuccess);
        Assert.Equal(KernelError.UnsupportedPayload, wrongType.Error);

        KernelResult<ResponseEnvelope> invisible = kernel.ReceiveResponse(left, endpoints.Left);
        Assert.False(invisible.IsSuccess);
        Assert.Equal(KernelError.ResponseNotAvailable, invisible.Error);

        KernelResult<ResponseEnvelope> publish = kernel.PublishResponse(right, endpoints.Right, sent.Value.Sequence, 7);
        Assert.True(publish.IsSuccess, publish.Message);
        KernelResult<ResponseEnvelope> received = kernel.ReceiveResponse(left, endpoints.Left);
        Assert.True(received.IsSuccess, received.Message);
        Assert.Equal(sent.Value.Sequence, received.Value!.RequestSequence);
        Assert.Equal((uint)1, received.Value.MessageId);
        Assert.Equal(ResponsePublicationStatus.Published, received.Value.Status);
        Assert.Equal(7, Assert.IsType<int>(received.Value.Payload));
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void CancellationIsExplicitAndClientVisible()
    {
        (RuntimeKernel? kernel, ProcessHandle left, ProcessHandle right, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints) = CreateChannel();
        KernelResult<ChannelEnvelope> sent = kernel.Send(left, right, endpoints.Left, 2);
        Assert.True(sent.IsSuccess, sent.Message);
        Assert.True(kernel.Receive(right, endpoints.Right).IsSuccess);

        KernelResult<ResponseEnvelope> cancel = kernel.CancelResponse(right, endpoints.Right, sent.Value!.Sequence);
        Assert.True(cancel.IsSuccess, cancel.Message);

        KernelResult<ResponseEnvelope> received = kernel.ReceiveResponse(left, endpoints.Left);
        Assert.True(received.IsSuccess, received.Message);
        Assert.Equal(ResponsePublicationStatus.Cancelled, received.Value!.Status);
        Assert.Null(received.Value.Payload);

        KernelResult<ResponseEnvelope> latePublish = kernel.PublishResponse(right, endpoints.Right, sent.Value.Sequence);
        Assert.False(latePublish.IsSuccess);
        Assert.Equal(KernelError.ResponseNotPending, latePublish.Error);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void DeclaredCancellationTransitionRestoresProtocolStateAtomically()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle left) = TestFixtures.Create(kernel, 421, 4210);
        (SingProcess _, ProcessHandle right) = TestFixtures.Create(kernel, 422, 4220);
        ProtocolDefinitionV1 protocol = new ProtocolDefinitionV1(
            "CancellationStateProtocol", "cancel-state-digest", "Idle", null,
            [new ProtocolMessageDescriptorV1(1, "Begin")],
            [new ProtocolTransitionV1(1, "Idle", "Busy")],
            [new ProtocolCancellationTransitionV1(1, "Busy", "Idle")]);
        ResponseProtocolDefinitionV1 responses = new ResponseProtocolDefinitionV1(
            protocol.ContractName, "cancel-state-response", [new ResponseMessageDescriptorV1(1, "Begin")]);
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints = kernel.CreateChannel(left, right, protocol, responses, 8).Value;
        ChannelEnvelope sent = kernel.Send(left, right, endpoints.Left, 1).Value!;
        Assert.Equal("Busy", kernel.Channels.GetEndpoint(endpoints.Left).Value!.ProtocolState);
        Assert.True(kernel.Receive(right, endpoints.Right).IsSuccess);

        KernelResult<ResponseEnvelope> cancelled = kernel.CancelResponse(right, endpoints.Right, sent.Sequence);

        Assert.True(cancelled.IsSuccess, cancelled.Message);
        Assert.Equal("Idle", kernel.Channels.GetEndpoint(endpoints.Left).Value!.ProtocolState);
        Assert.True(kernel.Send(left, right, endpoints.Left, 1).IsSuccess);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void OwnershipResponseTransfersOnlyAtPublicationBoundary()
    {
        (RuntimeKernel? kernel, ProcessHandle left, ProcessHandle right, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints) = CreateChannel();
        OwnedBuffer<int> responseBuffer = kernel.AllocateBuffer<int>(right, 4).Value!;
        responseBuffer.Span[0] = 42;
        RegionGeneration originalGeneration = responseBuffer.Handle.Generation;

        KernelResult<ChannelEnvelope> sent = kernel.Send(left, right, endpoints.Left, 3);
        Assert.True(sent.IsSuccess, sent.Message);
        Assert.True(kernel.Receive(right, endpoints.Right).IsSuccess);

        KernelResult<ResponseEnvelope> publish = kernel.PublishResponse(right, endpoints.Right, sent.Value!.Sequence, responseBuffer);
        Assert.True(publish.IsSuccess, publish.Message);
        Assert.False(responseBuffer.IsValid);

        KernelResult<ResponseEnvelope> received = kernel.ReceiveResponse(left, endpoints.Left);
        Assert.True(received.IsSuccess, received.Message);
        OwnedBuffer<int> moved = Assert.IsType<OwnedBuffer<int>>(received.Value!.Payload);
        Assert.True(moved.IsValid);
        Assert.Equal(originalGeneration.Value + 1, moved.Handle.Generation.Value);
        Assert.Equal(42, moved.Span[0]);
        Assert.True(kernel.ReleaseRegion(left, moved).IsSuccess);
    }

    [Fact]
    public async Task OwnershipResponseAndRequesterTeardownLeaveNoRegionOwnedByExitedRequester()
    {
        for (int iteration = 0; iteration < 16; iteration++)
        {
            (RuntimeKernel? kernel, ProcessHandle left, ProcessHandle right, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints) = CreateChannel();
            OwnedBuffer<int> buffer = kernel.AllocateBuffer<int>(right, 4).Value!;
            ChannelEnvelope request = kernel.Send(left, right, endpoints.Left, 3).Value!;
            Assert.True(kernel.Receive(right, endpoints.Right).IsSuccess);
            TaskCompletionSource<bool> start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<KernelResult<ResponseEnvelope>> publish = Task.Run(async () =>
            {
                await start.Task;
                return kernel.PublishResponse(right, endpoints.Right, request.Sequence, buffer);
            });
            Task<KernelResult> teardown = Task.Run(async () =>
            {
                await start.Task;
                return kernel.TerminateProcess(left);
            });
            start.SetResult(true);
            await Task.WhenAll(publish, teardown).WaitAsync(TimeSpan.FromSeconds(5));
            KernelResult closed = await teardown;
            Assert.True(closed.IsSuccess, closed.Message);
            Assert.DoesNotContain(kernel.Regions.Snapshot(), item =>
                item.Owner.DomainId == new DomainId(410) && item.State != RegionState.Released);
        }
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void WrongOwnershipResponseKindFailsWithoutOwnershipMutation()
    {
        (RuntimeKernel? kernel, ProcessHandle left, ProcessHandle right, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints) = CreateChannel();
        OwnedRegion<int> wrongPayload = kernel.AllocateRegion(right, 99).Value!;
        RegionHandle originalHandle = wrongPayload.Handle;

        KernelResult<ChannelEnvelope> sent = kernel.Send(left, right, endpoints.Left, 3);
        Assert.True(sent.IsSuccess, sent.Message);
        Assert.True(kernel.Receive(right, endpoints.Right).IsSuccess);

        KernelResult<ResponseEnvelope> publish = kernel.PublishResponse(right, endpoints.Right, sent.Value!.Sequence, wrongPayload);
        Assert.False(publish.IsSuccess);
        Assert.Equal(KernelError.UnsupportedPayload, publish.Error);
        Assert.True(wrongPayload.IsValid);
        Assert.Equal(originalHandle, wrongPayload.Handle);

        Assert.True(kernel.CancelResponse(right, endpoints.Right, sent.Value.Sequence).IsSuccess);
        Assert.Equal(ResponsePublicationStatus.Cancelled, kernel.ReceiveResponse(left, endpoints.Left).Value!.Status);
        Assert.True(kernel.ReleaseRegion(right, wrongPayload).IsSuccess);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void BoundedResponseMustMatchContractTypeAndLimitBeforePublication()
    {
        (RuntimeKernel? kernel, ProcessHandle left, ProcessHandle right, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints) = CreateChannel();
        KernelResult<ChannelEnvelope> sent = kernel.Send(left, right, endpoints.Left, 4);
        Assert.True(sent.IsSuccess, sent.Message);
        Assert.True(kernel.Receive(right, endpoints.Right).IsSuccess);

        KernelResult<ResponseEnvelope> wrongLimit = kernel.PublishResponse(right, endpoints.Right, sent.Value!.Sequence, new Packet(8, 32));
        Assert.False(wrongLimit.IsSuccess);
        Assert.Equal(KernelError.UnsupportedPayload, wrongLimit.Error);
        Assert.Equal(KernelError.ResponseNotAvailable, kernel.ReceiveResponse(left, endpoints.Left).Error);

        KernelResult<ResponseEnvelope> oversized = kernel.PublishResponse(right, endpoints.Right, sent.Value.Sequence, new Packet(17, 16));
        Assert.False(oversized.IsSuccess);
        Assert.Equal(KernelError.UnsupportedPayload, oversized.Error);

        KernelResult<ResponseEnvelope> publish = kernel.PublishResponse(right, endpoints.Right, sent.Value.Sequence, new Packet(8, 16));
        Assert.True(publish.IsSuccess, publish.Message);
        Assert.Equal(new Packet(8, 16), Assert.IsType<Packet>(kernel.ReceiveResponse(left, endpoints.Left).Value!.Payload));
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void OutstandingResponseCapacityAppliesBackpressureAfterRequestDelivery()
    {
        (RuntimeKernel? kernel, ProcessHandle left, ProcessHandle right, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints) = CreateChannel(capacity: 1);
        KernelResult<ChannelEnvelope> first = kernel.Send(left, right, endpoints.Left, 1);
        Assert.True(first.IsSuccess, first.Message);
        Assert.True(kernel.Receive(right, endpoints.Right).IsSuccess);

        KernelResult<ChannelEnvelope> blocked = kernel.Send(left, right, endpoints.Left, 1);
        Assert.False(blocked.IsSuccess);
        Assert.Equal(KernelError.CapacityExhausted, blocked.Error);

        Assert.True(kernel.PublishResponse(right, endpoints.Right, first.Value!.Sequence, 1).IsSuccess);
        Assert.True(kernel.ReceiveResponse(left, endpoints.Left).IsSuccess);
        Assert.True(kernel.Send(left, right, endpoints.Left, 1).IsSuccess);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void ResponseProtocolMustExactlyMatchRequestMessagesAndOwnershipMetadata()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle left) = TestFixtures.Create(kernel, 411, 410);
        (SingProcess _, ProcessHandle right) = TestFixtures.Create(kernel, 412, 420);
        ProtocolDefinitionV1 protocol = RequestProtocol();
        ResponseProtocolDefinitionV1 incomplete = new ResponseProtocolDefinitionV1(
            protocol.ContractName,
            "incomplete-response-digest",
            new[] { new ResponseMessageDescriptorV1(1, "Read", new ResponsePayloadDescriptorV1(ResponsePayloadKind.Primitive, typeof(int).FullName!)) });

        KernelResult<(ChannelEndpointHandle Left, ChannelEndpointHandle Right)> create = kernel.CreateChannel(left, right, protocol, incomplete, capacity: 8);

        Assert.False(create.IsSuccess);
        Assert.Equal(KernelError.ResponseProtocolMismatch, create.Error);
        Assert.Empty(kernel.Processes.Resolve(left).Value!.Channels);
        Assert.Empty(kernel.Processes.Resolve(right).Value!.Channels);
    }

    private static (
        RuntimeKernel Kernel,
        ProcessHandle Left,
        ProcessHandle Right,
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Endpoints) CreateChannel(int capacity = 8)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle left) = TestFixtures.Create(kernel, 401, 410);
        (SingProcess _, ProcessHandle right) = TestFixtures.Create(kernel, 402, 420);
        ProtocolDefinitionV1 protocol = RequestProtocol();
        ResponseProtocolDefinitionV1 responses = new ResponseProtocolDefinitionV1(
            protocol.ContractName,
            "response-publication-digest",
            new[]
            {
                new ResponseMessageDescriptorV1(1, "Read", new ResponsePayloadDescriptorV1(ResponsePayloadKind.Primitive, typeof(int).FullName!)),
                new ResponseMessageDescriptorV1(2, "Ping"),
                new ResponseMessageDescriptorV1(3, "Acquire", new ResponsePayloadDescriptorV1(ResponsePayloadKind.Ownership, "SingPlus.Sip.OwnedBuffer", ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer)),
                new ResponseMessageDescriptorV1(4, "Packet", new ResponsePayloadDescriptorV1(ResponsePayloadKind.Bounded, typeof(Packet).FullName!, 16))
            });
        KernelResult<(ChannelEndpointHandle Left, ChannelEndpointHandle Right)> channel = kernel.CreateChannel(left, right, protocol, responses, capacity);
        Assert.True(channel.IsSuccess, channel.Message);
        return (kernel, left, right, channel.Value);
    }

    private static ProtocolDefinitionV1 RequestProtocol() =>
        new(
            "SingPlus.Tests.IResponsePublicationProtocol",
            "response-publication-contract-digest",
            "Idle",
            terminalStates: null,
            messages: new[]
            {
                new ProtocolMessageDescriptorV1(1, "Read"),
                new ProtocolMessageDescriptorV1(2, "Ping"),
                new ProtocolMessageDescriptorV1(
                    3,
                    "Acquire",
                    returnsOwnership: true,
                    returnOwnershipPayloadKind: OwnershipPayloadKind.OwnedBuffer),
                new ProtocolMessageDescriptorV1(4, "Packet")
            },
            transitions: new[]
            {
                new ProtocolTransitionV1(1, "Idle", "Idle"),
                new ProtocolTransitionV1(2, "Idle", "Idle"),
                new ProtocolTransitionV1(3, "Idle", "Idle"),
                new ProtocolTransitionV1(4, "Idle", "Idle")
            });
}



