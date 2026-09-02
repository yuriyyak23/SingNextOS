using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Ownership;

public sealed class OwnershipTests
{
    [Fact]
    public void MoveInvalidatesSourceAndPreservesDestination()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var allocation = kernel.AllocateBuffer<int>(owner, 2);
        Assert.True(allocation.IsSuccess, allocation.Message);
        allocation.Value!.Span[0] = 42;

        var moved = allocation.Value.Move();

        Assert.False(allocation.Value.IsValid);
        Assert.True(moved.IsValid);
        Assert.Equal(42, moved.Span[0]);
    }

    [Fact]
    public void UseAfterMoveThrows()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var source = kernel.AllocateBuffer<byte>(owner, 4).Value!;

        _ = source.Move();

        Assert.Throws<InvalidOperationException>(() => _ = source.Length);
    }

    [Fact]
    public void DoubleReleaseIsRejected()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var buffer = kernel.AllocateBuffer<byte>(owner, 4).Value!;

        var first = kernel.ReleaseRegion(owner, buffer);
        var second = kernel.ReleaseRegion(owner, buffer);

        Assert.True(first.IsSuccess, first.Message);
        Assert.False(second.IsSuccess);
        Assert.Equal(KernelError.InvalidRegionState, second.Error);
        Assert.Throws<InvalidOperationException>(() => _ = buffer.Length);
    }

    [Fact]
    public void TransferInvalidatesSourceAndChangesOwnerGeneration()
    {
        var kernel = new RuntimeKernel();
        var (sourceProcess, source) = TestFixtures.Create(kernel, 1, 10);
        var (targetProcess, target) = TestFixtures.Create(kernel, 2, 20);
        var sourceBuffer = kernel.AllocateBuffer<int>(source, 1).Value!;
        sourceBuffer.Span[0] = 7;
        var oldHandle = sourceBuffer.Handle;

        var transfer = kernel.TransferRegion(source, target, sourceBuffer);

        Assert.True(transfer.IsSuccess, transfer.Message);
        Assert.False(sourceBuffer.IsValid);
        Assert.True(transfer.Value!.IsValid);
        Assert.Equal(7, transfer.Value.Span[0]);
        Assert.Equal(oldHandle.RegionId, transfer.Value.Handle.RegionId);
        Assert.Equal(oldHandle.Generation.Value + 1, transfer.Value.Handle.Generation.Value);
        Assert.DoesNotContain(oldHandle, sourceProcess.Regions);
        Assert.Contains(transfer.Value.Handle, targetProcess.Regions);
    }

    [Fact]
    public void StaleRegionGenerationIsRejectedAfterTransfer()
    {
        var kernel = new RuntimeKernel();
        var (_, source) = TestFixtures.Create(kernel, 1, 10);
        var (_, target) = TestFixtures.Create(kernel, 2, 20);
        var buffer = kernel.AllocateBuffer<int>(source, 1).Value!;
        var oldHandle = buffer.Handle;

        Assert.True(kernel.TransferRegion(source, target, buffer).IsSuccess);
        var validation = kernel.Regions.Validate(oldHandle, new RegionOwner(new DomainId(10), source.Generation));

        Assert.False(validation.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, validation.Error);
    }

    [Fact]
    public void BorrowCannotBeUsedAfterOwnerMove()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var buffer = kernel.AllocateBuffer<int>(owner, 1).Value!;
        var borrowed = buffer.Borrow();

        _ = buffer.Move();

        try
        {
            _ = borrowed.Length;
            Assert.Fail("BorrowedSpan remained usable after its owner was moved.");
        }
        catch (InvalidOperationException)
        {
        }
    }

    [Fact]
    public void TerminatingLastProcessBulkReclaimsOwnedRegions()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var buffer = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var handle = buffer.Handle;

        var terminated = kernel.TerminateProcess(owner);

        Assert.True(terminated.IsSuccess, terminated.Message);
        var descriptor = Assert.Single(kernel.Regions.Snapshot(), region => region.Handle.RegionId == handle.RegionId);
        Assert.Equal(RegionState.Released, descriptor.State);
        Assert.Throws<InvalidOperationException>(() => _ = buffer.Length);
    }

    [Fact]
    public void SharedDomainIsReclaimedOnlyAfterLastProcessTerminates()
    {
        var kernel = new RuntimeKernel();
        var (_, first) = TestFixtures.Create(kernel, 1, 10);
        var (_, second) = TestFixtures.Create(kernel, 2, 10, identity: "second-entry");
        var buffer = kernel.AllocateBuffer<byte>(second, 8).Value!;

        Assert.True(kernel.TerminateProcess(first).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);

        Assert.True(kernel.TerminateProcess(second).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.Throws<InvalidOperationException>(() => _ = buffer.Length);
    }
}
