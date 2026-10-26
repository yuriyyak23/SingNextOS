using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Ownership;

public sealed class OwnershipTests
{
    [Fact]
    public void MoveInvalidatesSourceAndPreservesDestination()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        KernelResult<OwnedBuffer<int>> allocation = kernel.AllocateBuffer<int>(owner, 2);
        Assert.True(allocation.IsSuccess, allocation.Message);
        allocation.Value!.Span[0] = 42;

        OwnedBuffer<int> moved = allocation.Value.Move();

        Assert.False(allocation.Value.IsValid);
        Assert.True(moved.IsValid);
        Assert.Equal(42, moved.Span[0]);
    }

    [Fact]
    public void UseAfterMoveThrows()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        OwnedBuffer<byte> source = kernel.AllocateBuffer<byte>(owner, 4).Value!;

        _ = source.Move();

        Assert.Throws<InvalidOperationException>(() => _ = source.Length);
    }

    [Fact]
    public void DoubleReleaseIsRejected()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(owner, 4).Value!;

        KernelResult first = kernel.ReleaseRegion(owner, buffer);
        KernelResult second = kernel.ReleaseRegion(owner, buffer);

        Assert.True(first.IsSuccess, first.Message);
        Assert.False(second.IsSuccess);
        Assert.Equal(KernelError.InvalidRegionState, second.Error);
        Assert.Throws<InvalidOperationException>(() => _ = buffer.Length);
    }

    [Fact]
    public void TransferInvalidatesSourceAndChangesOwnerGeneration()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess? sourceProcess, ProcessHandle source) = TestFixtures.Create(kernel, 1, 10);
        (SingProcess? targetProcess, ProcessHandle target) = TestFixtures.Create(kernel, 2, 20);
        OwnedBuffer<int> sourceBuffer = kernel.AllocateBuffer<int>(source, 1).Value!;
        sourceBuffer.Span[0] = 7;
        RegionHandle oldHandle = sourceBuffer.Handle;

        KernelResult<OwnedBuffer<int>> transfer = kernel.TransferRegion(source, target, sourceBuffer);

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
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle source) = TestFixtures.Create(kernel, 1, 10);
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(kernel, 2, 20);
        OwnedBuffer<int> buffer = kernel.AllocateBuffer<int>(source, 1).Value!;
        RegionHandle oldHandle = buffer.Handle;

        Assert.True(kernel.TransferRegion(source, target, buffer).IsSuccess);
        KernelResult<RegionDescriptor> validation = kernel.Regions.Validate(oldHandle, new RegionOwner(new DomainId(10), source.Generation));

        Assert.False(validation.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, validation.Error);
    }

    [Fact]
    public void BorrowCannotBeUsedAfterOwnerMove()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        OwnedBuffer<int> buffer = kernel.AllocateBuffer<int>(owner, 1).Value!;
        BorrowedSpan<int> borrowed = buffer.Borrow();

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
    public void ActiveLoanBlocksOwnerTransferAndRelease()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        (SingProcess _, ProcessHandle borrower) = TestFixtures.Create(kernel, 2, 20);
        OwnedBuffer<int> buffer = kernel.AllocateBuffer<int>(owner, 1).Value!;
        RegionOwner ownerIdentity = new RegionOwner(new DomainId(10), owner.Generation);
        RegionOwner borrowerIdentity = new RegionOwner(new DomainId(20), borrower.Generation);

        KernelResult<BorrowLeaseHandle> loan = kernel.Regions.Loan(buffer.Handle, ownerIdentity, borrowerIdentity);
        Assert.True(loan.IsSuccess, loan.Message);

        KernelResult release = kernel.ReleaseRegion(owner, buffer);
        KernelResult<OwnedBuffer<int>> transfer = kernel.TransferRegion(owner, borrower, buffer);

        Assert.False(release.IsSuccess);
        Assert.Equal(KernelError.InvalidRegionState, release.Error);
        Assert.False(transfer.IsSuccess);
        Assert.Equal(KernelError.InvalidRegionState, transfer.Error);
        Assert.True(buffer.IsValid);
        Assert.Equal(RegionState.Loaned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Fact]
    public void OwnerCanRevokeLoanAndLateReturnIsRejected()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        (SingProcess _, ProcessHandle borrower) = TestFixtures.Create(kernel, 2, 20);
        OwnedBuffer<int> buffer = kernel.AllocateBuffer<int>(owner, 1).Value!;
        RegionOwner ownerIdentity = new RegionOwner(new DomainId(10), owner.Generation);
        RegionOwner borrowerIdentity = new RegionOwner(new DomainId(20), borrower.Generation);
        KernelResult<BorrowLeaseHandle> loan = kernel.Regions.Loan(buffer.Handle, ownerIdentity, borrowerIdentity);
        Assert.True(loan.IsSuccess, loan.Message);

        KernelResult revoked = kernel.Regions.RevokeLoan(loan.Value, ownerIdentity);
        KernelResult lateReturn = kernel.Regions.ReturnLoan(loan.Value, borrowerIdentity);

        Assert.True(revoked.IsSuccess, revoked.Message);
        Assert.False(lateReturn.IsSuccess);
        Assert.Equal(KernelError.InvalidRegionState, lateReturn.Error);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.True(kernel.ReleaseRegion(owner, buffer).IsSuccess);
    }

    [Fact]
    public void WrongOwnerCannotRevokeActiveLoan()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        (SingProcess _, ProcessHandle borrower) = TestFixtures.Create(kernel, 2, 20);
        OwnedBuffer<int> buffer = kernel.AllocateBuffer<int>(owner, 1).Value!;
        RegionOwner ownerIdentity = new RegionOwner(new DomainId(10), owner.Generation);
        RegionOwner borrowerIdentity = new RegionOwner(new DomainId(20), borrower.Generation);
        KernelResult<BorrowLeaseHandle> loan = kernel.Regions.Loan(buffer.Handle, ownerIdentity, borrowerIdentity);
        Assert.True(loan.IsSuccess, loan.Message);

        KernelResult revoked = kernel.Regions.RevokeLoan(loan.Value, borrowerIdentity);

        Assert.False(revoked.IsSuccess);
        Assert.Equal(KernelError.WrongRegionOwner, revoked.Error);
        Assert.Equal(RegionState.Loaned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Fact]
    public void BorrowerDomainTerminationAutomaticallyReturnsLoan()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        (SingProcess _, ProcessHandle borrower) = TestFixtures.Create(kernel, 2, 20);
        OwnedBuffer<int> buffer = kernel.AllocateBuffer<int>(owner, 1).Value!;
        buffer.Span[0] = 77;
        RegionOwner ownerIdentity = new RegionOwner(new DomainId(10), owner.Generation);
        RegionOwner borrowerIdentity = new RegionOwner(new DomainId(20), borrower.Generation);
        KernelResult<BorrowLeaseHandle> loan = kernel.Regions.Loan(buffer.Handle, ownerIdentity, borrowerIdentity);
        Assert.True(loan.IsSuccess, loan.Message);

        KernelResult terminated = kernel.TerminateProcess(borrower);

        Assert.True(terminated.IsSuccess, terminated.Message);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.True(kernel.Regions.Validate(buffer.Handle, ownerIdentity).IsSuccess);
        Assert.True(buffer.IsValid);
        Assert.Equal(77, buffer.Span[0]);
    }

    [Fact]
    public void SharedBorrowerDomainReturnsLoansOnlyAfterLastProcessTerminates()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        (SingProcess _, ProcessHandle borrowerA) = TestFixtures.Create(kernel, 2, 20);
        (SingProcess _, ProcessHandle borrowerB) = TestFixtures.Create(kernel, 3, 20, identity: "borrower-b");
        OwnedBuffer<int> buffer = kernel.AllocateBuffer<int>(owner, 1).Value!;
        RegionOwner ownerIdentity = new RegionOwner(new DomainId(10), owner.Generation);
        RegionOwner borrowerIdentity = new RegionOwner(new DomainId(20), borrowerA.Generation);
        KernelResult<BorrowLeaseHandle> loan = kernel.Regions.Loan(buffer.Handle, ownerIdentity, borrowerIdentity);
        Assert.True(loan.IsSuccess, loan.Message);

        Assert.True(kernel.TerminateProcess(borrowerA).IsSuccess);
        Assert.Equal(RegionState.Loaned, Assert.Single(kernel.Regions.Snapshot()).State);

        Assert.True(kernel.TerminateProcess(borrowerB).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.True(kernel.Regions.Validate(buffer.Handle, ownerIdentity).IsSuccess);
    }

    [Fact]
    public void OwnerTerminationReclaimsActiveLoanAndRejectsLateReturn()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        (SingProcess _, ProcessHandle borrower) = TestFixtures.Create(kernel, 2, 20);
        OwnedBuffer<int> buffer = kernel.AllocateBuffer<int>(owner, 1).Value!;
        RegionOwner ownerIdentity = new RegionOwner(new DomainId(10), owner.Generation);
        RegionOwner borrowerIdentity = new RegionOwner(new DomainId(20), borrower.Generation);
        KernelResult<BorrowLeaseHandle> loan = kernel.Regions.Loan(buffer.Handle, ownerIdentity, borrowerIdentity);
        Assert.True(loan.IsSuccess, loan.Message);

        KernelResult terminated = kernel.TerminateProcess(owner);
        KernelResult lateReturn = kernel.Regions.ReturnLoan(loan.Value, borrowerIdentity);

        Assert.True(terminated.IsSuccess, terminated.Message);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.False(buffer.IsValid);
        Assert.False(lateReturn.IsSuccess);
        Assert.Equal(KernelError.InvalidRegionState, lateReturn.Error);
    }

    [Fact]
    public void TerminatingLastProcessBulkReclaimsOwnedRegions()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        RegionHandle handle = buffer.Handle;

        KernelResult terminated = kernel.TerminateProcess(owner);

        Assert.True(terminated.IsSuccess, terminated.Message);
        RegionDescriptor descriptor = Assert.Single(kernel.Regions.Snapshot(), region => region.Handle.RegionId == handle.RegionId);
        Assert.Equal(RegionState.Released, descriptor.State);
        Assert.Throws<InvalidOperationException>(() => _ = buffer.Length);
    }

    [Fact]
    public void SharedDomainIsReclaimedOnlyAfterLastProcessTerminates()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle first) = TestFixtures.Create(kernel, 1, 10);
        (SingProcess _, ProcessHandle second) = TestFixtures.Create(kernel, 2, 10, identity: "second-entry");
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(second, 8).Value!;

        Assert.True(kernel.TerminateProcess(first).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);

        Assert.True(kernel.TerminateProcess(second).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.Throws<InvalidOperationException>(() => _ = buffer.Length);
    }

    [Fact]
    public void TerminationInvalidatesBufferMovedLocallyWithinDomain()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        OwnedBuffer<int> buffer = kernel.AllocateBuffer<int>(owner, 1).Value!;
        buffer.Span[0] = 99;
        OwnedBuffer<int> moved = buffer.Move();

        Assert.True(moved.IsValid);
        Assert.Equal(99, moved.Span[0]);

        KernelResult terminated = kernel.TerminateProcess(owner);

        Assert.True(terminated.IsSuccess, terminated.Message);
        Assert.False(moved.IsValid);
        Assert.Throws<InvalidOperationException>(() => _ = moved.Length);
    }

    [Fact]
    public void TerminationInvalidatesRegionMovedLocallyWithinDomain()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 123).Value!;
        OwnedRegion<int> moved = region.Move();

        Assert.True(moved.IsValid);
        Assert.Equal(123, moved.Value);

        KernelResult terminated = kernel.TerminateProcess(owner);

        Assert.True(terminated.IsSuccess, terminated.Message);
        Assert.False(moved.IsValid);
        Assert.Throws<InvalidOperationException>(() => _ = moved.Value);
    }
}
