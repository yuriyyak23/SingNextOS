using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Ownership;

public sealed class SingCapPhase07RegionHardeningTests
{
    [Fact]
    public void TypedProjectionUsesCheckedElementArithmeticAndExactElementType()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        OwnedBuffer<int> buffer = kernel.AllocateBuffer<int>(owner, 8).Value!;

        KernelResult<RegionUseDescriptor> exact = kernel.AcquireRegionUse<int>(owner, buffer.Handle, RegionUseMode.ReadOnly, 2, 3);
        Assert.True(exact.IsSuccess, exact.Message);
        Assert.Equal(new RegionUseRange(8, 12), exact.Value!.Range);
        Assert.Equal(KernelError.InvalidRegionState,
            kernel.AcquireRegionUse<byte>(owner, buffer.Handle, RegionUseMode.ReadOnly, 0, 1).Error);
        Assert.Equal(KernelError.InvalidRegionState,
            kernel.AcquireRegionUse<int>(owner, buffer.Handle, RegionUseMode.ReadOnly, long.MaxValue, 2).Error);
    }

    [Fact]
    public void ExplicitLexicalViewsAreBoundedAndDoNotLookupAuthorityPerElement()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        OwnedBuffer<int> buffer = kernel.AllocateBuffer<int>(owner, 8).Value!;

        WriteBorrow<int> write = buffer.BorrowWrite(2, 3);
        for (int index = 0; index < write.Length; index++) write[index] = 40 + index;
        ReadBorrow<int> read = buffer.BorrowRead(2, 3);

        Assert.Equal(new[] { 40, 41, 42 }, read.Span.ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.BorrowRead(7, 2));
    }

    [Fact]
    public void ConcurrentLoanAdmissionHasExactlyOneWinner()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        (SingProcess _, ProcessHandle borrower) = TestFixtures.Create(kernel, 2, 20);
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(owner, 32).Value!;
        RegionOwner ownerIdentity = new RegionOwner(new DomainId(10), owner.Generation);
        RegionOwner borrowerIdentity = new RegionOwner(new DomainId(20), borrower.Generation);

        KernelResult<BorrowLeaseHandle>[] results = new KernelResult<BorrowLeaseHandle>[32];
        Parallel.For(0, results.Length, index =>
            results[index] = kernel.Regions.Loan(buffer.Handle, ownerIdentity, borrowerIdentity));

        Assert.Single(results, static result => result.IsSuccess);
        Assert.All(results.Where(static result => !result.IsSuccess),
            static result => Assert.Equal(KernelError.InvalidRegionState, result.Error));
    }

    [Fact]
    public void ConcurrentOverlappingWriterAdmissionHasExactlyOneWinner()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(owner, 64).Value!;
        KernelResult<RegionUseDescriptor>[] results = new KernelResult<RegionUseDescriptor>[32];

        Parallel.For(0, results.Length, index =>
            results[index] = kernel.AcquireRegionUse(owner, buffer.Handle,
                RegionUseMode.ExclusiveWrite, new RegionUseRange(8, 32)));

        Assert.Single(results, static result => result.IsSuccess);
        Assert.All(results.Where(static result => !result.IsSuccess),
            static result => Assert.Equal(KernelError.RegionUseConflict, result.Error));
    }

    [Fact]
    public void AsyncBorrowRematerializationFailsAfterReturn()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        (SingProcess _, ProcessHandle borrower) = TestFixtures.Create(kernel, 2, 20);
        OwnedBuffer<int> buffer = kernel.AllocateBuffer<int>(owner, 4).Value!;
        RegionOwner ownerIdentity = new RegionOwner(new DomainId(10), owner.Generation);
        RegionOwner borrowerIdentity = new RegionOwner(new DomainId(20), borrower.Generation);
        BorrowLeaseGrant grant = kernel.Regions.AcquireLoan(buffer.Handle, ownerIdentity, borrowerIdentity).Value!;
        BorrowLease<int> loan = (BorrowLease<int>)((ITransferableOwnedPayload)buffer)
            .CreateBorrowLeaseForRuntime(grant.Handle, grant.Lifetime);

        Assert.Equal(4, loan.Length);
        Assert.True(kernel.ReturnBorrow(borrower, loan.Handle).IsSuccess);
        Assert.Throws<InvalidOperationException>((Action)(() => { _ = loan.Span.Length; }));
    }

    [Fact]
    public void ReclaimInvalidatesOwnershipWithoutReusingIdentity()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        OwnedBuffer<int> buffer = kernel.AllocateBuffer<int>(owner, 4).Value!;
        RegionHandle oldHandle = buffer.Handle;

        Assert.True(kernel.ReleaseRegion(owner, buffer).IsSuccess);
        Assert.False(buffer.IsValid);
        OwnedBuffer<int> replacement = kernel.AllocateBuffer<int>(owner, 4).Value!;
        Assert.NotEqual(oldHandle.RegionId, replacement.Handle.RegionId);
    }

    [Fact]
    public void ConcurrentCatalogAllocationProducesUniqueNonzeroIdentities()
    {
        RegionAuthority authority = new RegionAuthority();
        RegionOwner owner = new RegionOwner(new DomainId(10), 1);
        RegionHandle[] handles = new RegionHandle[128];

        Parallel.For(0, handles.Length, index =>
            handles[index] = authority.Allocate(owner, 1, typeof(byte).FullName!).Handle);

        Assert.Equal(handles.Length, handles.Select(static handle => handle.RegionId).Distinct().Count());
        Assert.DoesNotContain(handles, static handle => handle.RegionId.Value == 0);
    }
}
