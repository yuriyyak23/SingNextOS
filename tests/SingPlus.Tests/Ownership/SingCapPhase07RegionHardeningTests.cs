using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Ownership;

public sealed class SingCapPhase07RegionHardeningTests
{
    [Fact]
    public void TypedProjectionUsesCheckedElementArithmeticAndExactElementType()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var buffer = kernel.AllocateBuffer<int>(owner, 8).Value!;

        var exact = kernel.AcquireRegionUse<int>(owner, buffer.Handle, RegionUseMode.ReadOnly, 2, 3);
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
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var buffer = kernel.AllocateBuffer<int>(owner, 8).Value!;

        var write = buffer.BorrowWrite(2, 3);
        for (var index = 0; index < write.Length; index++) write[index] = 40 + index;
        var read = buffer.BorrowRead(2, 3);

        Assert.Equal(new[] { 40, 41, 42 }, read.Span.ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.BorrowRead(7, 2));
    }

    [Fact]
    public void ConcurrentLoanAdmissionHasExactlyOneWinner()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var (_, borrower) = TestFixtures.Create(kernel, 2, 20);
        var buffer = kernel.AllocateBuffer<byte>(owner, 32).Value!;
        var ownerIdentity = new RegionOwner(new DomainId(10), owner.Generation);
        var borrowerIdentity = new RegionOwner(new DomainId(20), borrower.Generation);

        var results = new KernelResult<BorrowLeaseHandle>[32];
        Parallel.For(0, results.Length, index =>
            results[index] = kernel.Regions.Loan(buffer.Handle, ownerIdentity, borrowerIdentity));

        Assert.Single(results, static result => result.IsSuccess);
        Assert.All(results.Where(static result => !result.IsSuccess),
            static result => Assert.Equal(KernelError.InvalidRegionState, result.Error));
    }

    [Fact]
    public void ConcurrentOverlappingWriterAdmissionHasExactlyOneWinner()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var buffer = kernel.AllocateBuffer<byte>(owner, 64).Value!;
        var results = new KernelResult<RegionUseDescriptor>[32];

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
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var (_, borrower) = TestFixtures.Create(kernel, 2, 20);
        var buffer = kernel.AllocateBuffer<int>(owner, 4).Value!;
        var ownerIdentity = new RegionOwner(new DomainId(10), owner.Generation);
        var borrowerIdentity = new RegionOwner(new DomainId(20), borrower.Generation);
        var grant = kernel.Regions.AcquireLoan(buffer.Handle, ownerIdentity, borrowerIdentity).Value!;
        var loan = (BorrowLease<int>)((ITransferableOwnedPayload)buffer)
            .CreateBorrowLeaseForRuntime(grant.Handle, grant.Lifetime);

        Assert.Equal(4, loan.Length);
        Assert.True(kernel.ReturnBorrow(borrower, loan.Handle).IsSuccess);
        Assert.Throws<InvalidOperationException>((Action)(() => { _ = loan.Span.Length; }));
    }

    [Fact]
    public void ReclaimInvalidatesOwnershipWithoutReusingIdentity()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var buffer = kernel.AllocateBuffer<int>(owner, 4).Value!;
        var oldHandle = buffer.Handle;

        Assert.True(kernel.ReleaseRegion(owner, buffer).IsSuccess);
        Assert.False(buffer.IsValid);
        var replacement = kernel.AllocateBuffer<int>(owner, 4).Value!;
        Assert.NotEqual(oldHandle.RegionId, replacement.Handle.RegionId);
    }

    [Fact]
    public void ConcurrentCatalogAllocationProducesUniqueNonzeroIdentities()
    {
        var authority = new RegionAuthority();
        var owner = new RegionOwner(new DomainId(10), 1);
        var handles = new RegionHandle[128];

        Parallel.For(0, handles.Length, index =>
            handles[index] = authority.Allocate(owner, 1, typeof(byte).FullName!).Handle);

        Assert.Equal(handles.Length, handles.Select(static handle => handle.RegionId).Distinct().Count());
        Assert.DoesNotContain(handles, static handle => handle.RegionId.Value == 0);
    }
}
