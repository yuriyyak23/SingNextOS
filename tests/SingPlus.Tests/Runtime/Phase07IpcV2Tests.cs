using System.Diagnostics;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;
using Xunit.Abstractions;

namespace SingPlus.Tests.Runtime;

public sealed class Phase07IpcV2Tests(ITestOutputHelper output)
{
    [Fact]
    public void CopyIsValueIndependentAndReceiptNeverPromisesZeroCopy()
    {
        var scenario = Create(RequestPayloadKind.Bounded, maxBytes: 64);
        var source = new byte[] { 1, 2, 3 };
        var payload = new IpcCopyPayload(source, 64);

        var sent = scenario.Kernel.SendCopyV2(scenario.Left, scenario.Right, scenario.Endpoints.Left, 1, payload);
        source[0] = 99;
        var received = scenario.Kernel.Receive(scenario.Right, scenario.Endpoints.Right).Value!;
        var copied = Assert.IsType<IpcCopyPayload>(received.Payload);

        Assert.True(sent.IsSuccess, sent.Message);
        Assert.Equal((byte)1, copied.Bytes.Span[0]);
        Assert.False(sent.Value!.GuaranteesZeroCopy);
        Assert.False(sent.Value.AuthorizesTransfer);
    }

    [Fact]
    public void PreAdmissionMoveFailureRetainsSenderAndSuccessfulMoveHasOneOwner()
    {
        var scenario = Create(RequestPayloadKind.Ownership, capacity: 1, disposition: OwnershipRequestDisposition.Consume);
        var blocker = scenario.Kernel.AllocateBuffer<byte>(scenario.Left, 1).Value!;
        Assert.True(scenario.Kernel.SendMoveV2(scenario.Left, scenario.Right, scenario.Endpoints.Left, 1, blocker).IsSuccess);
        var retained = scenario.Kernel.AllocateBuffer<byte>(scenario.Left, 4).Value!;
        var old = retained.Handle;

        var rejected = scenario.Kernel.SendMoveV2(scenario.Left, scenario.Right, scenario.Endpoints.Left, 1, retained);

        Assert.Equal(KernelError.CapacityExhausted, rejected.Error);
        Assert.True(retained.IsValid);
        Assert.Equal(RegionState.Owned, Assert.Single(scenario.Kernel.Regions.Snapshot(), item => item.Handle == old).State);
        Assert.True(scenario.Kernel.Receive(scenario.Right, scenario.Endpoints.Right).IsSuccess);

        var cancelledScope = scenario.Kernel.CreateCancellationScope(scenario.Left).Value!.Scope;
        Assert.True(scenario.Kernel.RequestCancellation(scenario.Left, cancelledScope).IsSuccess);
        Assert.Equal(KernelError.DeadlineExpired,
            scenario.Kernel.SendMoveV2(scenario.Left, scenario.Right, scenario.Endpoints.Left, 1, retained,
                cancellationScope: cancelledScope).Error);
        Assert.True(retained.IsValid);

        var moved = scenario.Kernel.SendMoveV2(scenario.Left, scenario.Right, scenario.Endpoints.Left, 1, retained).Value!;
        var receiverPayload = Assert.IsType<OwnedBuffer<byte>>(moved.Envelope.Payload);
        Assert.False(retained.IsValid);
        Assert.True(receiverPayload.IsValid);
        Assert.Equal(old.RegionId, receiverPayload.Handle.RegionId);
        Assert.Equal(old.Generation.Value + 1, receiverPayload.Handle.Generation.Value);
        Assert.Equal(IpcOwnershipDisposition.ReceiverOwned, moved.Ownership);
    }

    [Fact]
    public void BorrowIsReadOnlyAndCancellationDoesNotFabricateReturn()
    {
        var scenario = Create(RequestPayloadKind.Ownership, disposition: OwnershipRequestDisposition.Borrow);
        var owner = scenario.Kernel.AllocateBuffer<int>(scenario.Left, 1).Value!;
        owner.Span[0] = 7;
        var scope = scenario.Kernel.CreateCancellationScope(scenario.Left).Value!.Scope;
        Assert.True(scenario.Kernel.SendBorrowReadV2(scenario.Left, scenario.Right, scenario.Endpoints.Left, 1, owner,
            cancellationScope: scope).IsSuccess);
        Assert.Equal(CancellationDisposition.CompletedBeforeCancellation,
            scenario.Kernel.RequestCancellation(scenario.Left, scope).Value!.Disposition);
        var lease = Assert.IsType<BorrowLease<int>>(scenario.Kernel.Receive(scenario.Right, scenario.Endpoints.Right).Value!.Payload);

        Assert.Equal(7, lease.Span[0]);
        Assert.Throws<InvalidOperationException>(() => owner.Span[0] = 8);
        Assert.Equal(RegionState.Loaned, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
        Assert.True(scenario.Kernel.ReturnBorrow(scenario.Right, lease.Handle).IsSuccess);
        Assert.False(lease.IsValid);
        owner.Span[0] = 8;
    }

    [Fact]
    public void ScatterGatherValidatesBoundsOverlapModesAndCopiesSegments()
    {
        var scenario = Create(RequestPayloadKind.Bounded, maxBytes: IpcV2Contract.MaximumScatterGatherBytes);
        var source = scenario.Kernel.AllocateBuffer<byte>(scenario.Left, 8).Value!;
        for (var index = 0; index < source.Length; index++) source.Span[index] = (byte)index;

        Assert.Equal(KernelError.InvalidMessage,
            scenario.Kernel.SendScatterGatherCopyV2(scenario.Left, scenario.Right, scenario.Endpoints.Left, 1,
                [new(source, 7, 2)]).Error);
        Assert.Equal(KernelError.InvalidMessage,
            scenario.Kernel.SendScatterGatherCopyV2(scenario.Left, scenario.Right, scenario.Endpoints.Left, 1,
                [new(source, 0, 4), new(source, 2, 4)]).Error);
        Assert.Equal(KernelError.PlatformUnsupported,
            scenario.Kernel.SendScatterGatherCopyV2(scenario.Left, scenario.Right, scenario.Endpoints.Left, 1,
                [new(source, 0, 2, IpcSegmentTransferMode.Move)]).Error);

        var sent = scenario.Kernel.SendScatterGatherCopyV2(scenario.Left, scenario.Right, scenario.Endpoints.Left, 1,
            [new(source, 0, 2), new(source, 4, 3)]);
        Assert.True(sent.IsSuccess, sent.Message);
        source.Span[0] = 99;
        var payload = Assert.IsType<IpcScatterGatherPayload>(scenario.Kernel.Receive(scenario.Right, scenario.Endpoints.Right).Value!.Payload);
        Assert.Equal(new byte[] { 0, 1 }, payload.Segments[0].ToArray());
        Assert.Equal(new byte[] { 4, 5, 6 }, payload.Segments[1].ToArray());
    }

    [Fact]
    public void CapabilityRequirementRemainsTypedAndIndependentOfPayloadBytes()
    {
        var requirement = new CapabilityRequirementV1(ResourceKind.Device, "device:console", CapabilityRights.Write);
        var scenario = Create(RequestPayloadKind.Bounded, maxBytes: 64, requirement: requirement);
        var payload = new IpcCopyPayload([1], 64);
        Assert.Equal(KernelError.MissingCapability,
            scenario.Kernel.SendCopyV2(scenario.Left, scenario.Right, scenario.Endpoints.Left, 1, payload).Error);
        var wrong = scenario.Kernel.MintCapability(new(7001), scenario.Left, ResourceKind.Device,
            "device:other", CapabilityRights.Write).Value!.CapabilityId;
        Assert.Equal(KernelError.MissingCapability,
            scenario.Kernel.SendCopyV2(scenario.Left, scenario.Right, scenario.Endpoints.Left, 1, payload, [wrong]).Error);
        var exact = scenario.Kernel.MintCapability(new(7001), scenario.Left, ResourceKind.Device,
            "device:console", CapabilityRights.Write).Value!.CapabilityId;
        Assert.True(scenario.Kernel.SendCopyV2(scenario.Left, scenario.Right, scenario.Endpoints.Left, 1, payload, [exact]).IsSuccess);
    }

    [Fact]
    public void ReceiverCrashAfterMoveReclaimsReceiverOwnershipWithoutRestoringSender()
    {
        var scenario = Create(RequestPayloadKind.Ownership, disposition: OwnershipRequestDisposition.Consume);
        var source = scenario.Kernel.AllocateBuffer<byte>(scenario.Left, 4).Value!;
        var sent = scenario.Kernel.SendMoveV2(scenario.Left, scenario.Right, scenario.Endpoints.Left, 1, source).Value!;
        var receiverPayload = Assert.IsType<OwnedBuffer<byte>>(sent.Envelope.Payload);

        Assert.True(scenario.Kernel.FaultProcess(scenario.Right).IsSuccess);
        Assert.False(source.IsValid);
        Assert.False(receiverPayload.IsValid);
        Assert.Equal(RegionState.Released, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
    }

    [Fact]
    public void BoundedSmallCopyAndOwnershipFastPathsHaveExecutableMeasurements()
    {
        const int iterations = 100;
        var copy = Create(RequestPayloadKind.Bounded, maxBytes: 64, capacity: iterations);
        var copyPayload = new IpcCopyPayload([1, 2, 3, 4], 64);
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
            Assert.True(copy.Kernel.SendCopyV2(copy.Left, copy.Right, copy.Endpoints.Left, 1, copyPayload).IsSuccess);
        timer.Stop();
        var copyElapsed = timer.ElapsedTicks;

        var move = Create(RequestPayloadKind.Ownership, capacity: iterations, disposition: OwnershipRequestDisposition.Consume);
        timer.Restart();
        for (var i = 0; i < iterations; i++)
        {
            var buffer = move.Kernel.AllocateBuffer<byte>(move.Left, 4096).Value!;
            Assert.True(move.Kernel.SendMoveV2(move.Left, move.Right, move.Endpoints.Left, 1, buffer).IsSuccess);
        }
        timer.Stop();
        output.WriteLine("IPC v2 diagnostic measurement; environment={0}; iterations={1}; small-copy ticks={2}; allocate+MOVE(4096) ticks={3}",
            Environment.Version, iterations, copyElapsed, timer.ElapsedTicks);
    }

    private static Scenario Create(
        RequestPayloadKind kind,
        int capacity = 4,
        int maxBytes = 0,
        OwnershipRequestDisposition disposition = OwnershipRequestDisposition.None,
        CapabilityRequirementV1? requirement = null)
    {
        var kernel = new RuntimeKernel();
        var left = TestFixtures.Create(kernel, 701, 7001).Handle;
        var right = TestFixtures.Create(kernel, 702, 7002).Handle;
        RequestPayloadDescriptorV1 payload;
        string[] consumes = [];
        string[] borrows = [];
        if (kind == RequestPayloadKind.Bounded)
            payload = new(kind, "payload", kind == RequestPayloadKind.Bounded && maxBytes == IpcV2Contract.MaximumScatterGatherBytes
                ? typeof(IpcScatterGatherPayload).FullName : typeof(IpcCopyPayload).FullName, maxBytes);
        else
        {
            payload = new(kind, "payload", typeof(OwnedBuffer<>).Namespace + ".OwnedBuffer", ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer);
            if (disposition == OwnershipRequestDisposition.Consume) consumes = ["payload"];
            else borrows = ["payload"];
        }
        var message = new ProtocolMessageDescriptorV1(1, "Transfer",
            requirement is { } exact ? [exact] : null, consumes, borrows, requestPayload: payload);
        var protocol = new ProtocolDefinitionV1("IpcV2", Guid.NewGuid().ToString("N"), "Ready", null,
            [message], [new(1, "Ready", "Ready")]);
        var endpoints = kernel.CreateChannel(left, right, protocol, capacity).Value;
        return new(kernel, left, right, endpoints);
    }

    private sealed record Scenario(RuntimeKernel Kernel, ProcessHandle Left, ProcessHandle Right,
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Endpoints);
}
