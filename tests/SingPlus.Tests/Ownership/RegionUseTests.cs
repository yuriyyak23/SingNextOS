using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Ownership;

public sealed class RegionUseTests
{
    [Fact]
    public void ManagedSpanWritesDoNotClaimAnAuthorityObservedMutation()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var buffer = kernel.AllocateBuffer<int>(owner, 2).Value!;
        var before = Assert.Single(kernel.Regions.Snapshot()).MutationEpoch;

        buffer.Span[0] = 42;

        Assert.Equal(before, Assert.Single(kernel.Regions.Snapshot()).MutationEpoch);
    }

    [Fact]
    public void ReadUsesShareButAnyWriterIsWholeRegionExclusive()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var buffer = kernel.AllocateBuffer<byte>(owner, 16).Value!;

        var firstRead = kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.ReadOnly, new(0, 4));
        var secondRead = kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.ReadOnly, new(4, 4));
        var writer = kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.ExclusiveWrite, new(8, 4));

        Assert.True(firstRead.IsSuccess, firstRead.Message);
        Assert.True(secondRead.IsSuccess, secondRead.Message);
        Assert.False(writer.IsSuccess);
        Assert.Equal(KernelError.RegionUseConflict, writer.Error);

        Assert.True(kernel.ReleaseRegionUse(owner, firstRead.Value!.Handle).IsSuccess);
        Assert.True(kernel.ReleaseRegionUse(owner, secondRead.Value!.Handle).IsSuccess);

        var firstWriter = kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.ExclusiveWrite, new(0, 4));
        var disjointWriter = kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.StagedOutput, new(8, 4));
        Assert.True(firstWriter.IsSuccess, firstWriter.Message);
        Assert.False(disjointWriter.IsSuccess);
        Assert.Equal(KernelError.RegionUseConflict, disjointWriter.Error);
    }

    [Fact]
    public void WritableUseActivationAndReleaseAdvanceEpochExactlyOnce()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var buffer = kernel.AllocateBuffer<byte>(owner, 8).Value!;

        var acquired = kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.ExclusiveWrite, new(0, 8));
        Assert.True(acquired.IsSuccess, acquired.Message);
        Assert.Equal<ulong>(2, acquired.Value!.MutationEpoch.Value);

        Assert.True(kernel.ReleaseRegionUse(owner, acquired.Value.Handle).IsSuccess);
        Assert.Equal<ulong>(3, Assert.Single(kernel.Regions.Snapshot()).MutationEpoch.Value);
        Assert.True(kernel.ReleaseRegionUse(owner, acquired.Value.Handle).IsSuccess);
        Assert.Equal<ulong>(3, Assert.Single(kernel.Regions.Snapshot()).MutationEpoch.Value);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Fact]
    public void StaleUseFailsBeforeSubmitAndBetweenSubmitAndPublish()
    {
        var kernel = new RuntimeKernel();
        var (_, source) = TestFixtures.Create(kernel, 1, 10);
        var (_, target) = TestFixtures.Create(kernel, 2, 20);
        var provider = new FakeExternalProvider(kernel);

        var beforeSubmitBuffer = kernel.AllocateBuffer<byte>(source, 8).Value!;
        var beforeSubmitUse = kernel.AcquireRegionUse(source, beforeSubmitBuffer.Handle, RegionUseMode.ReadOnly, new(0, 8)).Value!;
        Assert.True(kernel.TransferRegion(source, target, beforeSubmitBuffer).IsSuccess);

        var rejectedSubmit = provider.Submit(source, beforeSubmitUse.Handle);
        Assert.False(rejectedSubmit.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, rejectedSubmit.Error);
        Assert.Equal(0, provider.AcceptedSubmissions);

        var beforePublishBuffer = kernel.AllocateBuffer<byte>(source, 8).Value!;
        var beforePublishUse = kernel.AcquireRegionUse(source, beforePublishBuffer.Handle, RegionUseMode.ReadOnly, new(0, 8)).Value!;
        Assert.True(provider.Submit(source, beforePublishUse.Handle).IsSuccess);
        Assert.True(kernel.TransferRegion(source, target, beforePublishBuffer).IsSuccess);

        var rejectedPublish = provider.Publish(source, beforePublishUse.Handle);
        Assert.False(rejectedPublish.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, rejectedPublish.Error);
        Assert.Equal(0, provider.Publications);
    }

    [Fact]
    public void ReadOnlyBorrowCannotAuthorizeWriterAndReturnInvalidatesReadUse()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var (_, borrower) = TestFixtures.Create(kernel, 2, 20);
        var buffer = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var ownerIdentity = new RegionOwner(new DomainId(10), owner.Generation);
        var borrowerIdentity = new RegionOwner(new DomainId(20), borrower.Generation);
        var loan = kernel.Regions.Loan(buffer.Handle, ownerIdentity, borrowerIdentity).Value;

        var writer = kernel.AcquireBorrowRegionUse(owner, borrower, loan, RegionUseMode.ExclusiveWrite, new(0, 8));
        Assert.False(writer.IsSuccess);
        Assert.Equal(KernelError.InsufficientRights, writer.Error);

        var reader = kernel.AcquireBorrowRegionUse(owner, borrower, loan, RegionUseMode.ReadOnly, new(0, 8));
        Assert.True(reader.IsSuccess, reader.Message);
        Assert.True(kernel.ReturnBorrow(borrower, loan).IsSuccess);
        var stale = kernel.ValidateRegionUse(borrower, reader.Value!.Handle);
        Assert.False(stale.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, stale.Error);
    }

    [Fact]
    public void WritableUseExcludesBorrowAndSeparatePlatformMappingAdmission()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var (_, borrower) = TestFixtures.Create(kernel, 2, 20);
        var buffer = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var ownerIdentity = new RegionOwner(new DomainId(10), owner.Generation);
        var borrowerIdentity = new RegionOwner(new DomainId(20), borrower.Generation);
        var writer = kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.ExclusiveWrite, new(0, 8));
        Assert.True(writer.IsSuccess, writer.Message);

        var borrow = kernel.Regions.Loan(buffer.Handle, ownerIdentity, borrowerIdentity);
        var mapping = kernel.Regions.ReservePlatformMapping(buffer.Handle, ownerIdentity);

        Assert.False(borrow.IsSuccess);
        Assert.Equal(KernelError.RegionUseConflict, borrow.Error);
        Assert.False(mapping.IsSuccess);
        Assert.Equal(KernelError.RegionUseConflict, mapping.Error);
    }

    [Fact]
    public void BorrowGrantAdvancesEpochAndInvalidatesPriorOwnerReadUse()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var (_, borrower) = TestFixtures.Create(kernel, 2, 20);
        var buffer = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var ownerIdentity = new RegionOwner(new DomainId(10), owner.Generation);
        var borrowerIdentity = new RegionOwner(new DomainId(20), borrower.Generation);
        var use = kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.ReadOnly, new(0, 8)).Value!;

        var loan = kernel.Regions.Loan(buffer.Handle, ownerIdentity, borrowerIdentity);

        Assert.True(loan.IsSuccess, loan.Message);
        Assert.Equal<ulong>(use.MutationEpoch.Value + 1, Assert.Single(kernel.Regions.Snapshot()).MutationEpoch.Value);
        Assert.Equal(KernelError.StaleGeneration, kernel.Regions.ValidateUse(use.Handle, ownerIdentity).Error);
    }

    [Fact]
    public void MoveBorrowReclaimAndBackendResetCannotResurrectAUse()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var (_, target) = TestFixtures.Create(kernel, 2, 20);
        var buffer = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var principal = new RegionOwner(new DomainId(10), owner.Generation);
        var use = kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.ReadOnly, new(0, 8)).Value!;

        Assert.True(kernel.TransferRegion(owner, target, buffer).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, kernel.Regions.ValidateUse(use.Handle, principal).Error);
        _ = kernel.ObservePlatformBackendReset();
        Assert.Equal(KernelError.StaleGeneration, kernel.Regions.ValidateUse(use.Handle, principal).Error);
        Assert.True(kernel.Regions.ReleaseUse(use.Handle, principal).IsSuccess);
        Assert.True(kernel.Regions.ReleaseUse(use.Handle, principal).IsSuccess);

        var reclaimed = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var reclaimedUse = kernel.AcquireRegionUse(owner, reclaimed.Handle, RegionUseMode.ReadOnly, new(0, 8)).Value!;
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, kernel.Regions.ValidateUse(reclaimedUse.Handle, principal).Error);
        Assert.True(kernel.Regions.ReleaseUse(reclaimedUse.Handle, principal).IsSuccess);
        Assert.True(kernel.Regions.ReleaseUse(reclaimedUse.Handle, principal).IsSuccess);
    }

    [Fact]
    public void InvalidationIsIdempotentAndDoesNotReturnOwnership()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var buffer = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var use = kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.DevicePrivate, new(0, 8)).Value!;

        Assert.True(kernel.InvalidateRegionUse(owner, use.Handle).IsSuccess);
        Assert.True(kernel.InvalidateRegionUse(owner, use.Handle).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, kernel.ValidateRegionUse(owner, use.Handle).Error);
        Assert.True(kernel.ReleaseRegionUse(owner, use.Handle).IsSuccess);
        Assert.True(kernel.ReleaseRegionUse(owner, use.Handle).IsSuccess);
        Assert.True(kernel.Regions.Validate(buffer.Handle, new(new DomainId(10), owner.Generation)).IsSuccess);
    }

    [Fact]
    public void InvalidRangesAndFutureGatedSharingFailClosed()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var buffer = kernel.AllocateBuffer<byte>(owner, 8).Value!;

        Assert.Equal(KernelError.InvalidRegionState, kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.ReadOnly, new(-1, 1)).Error);
        Assert.Equal(KernelError.InvalidRegionState, kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.ReadOnly, new(0, 0)).Error);
        Assert.Equal(KernelError.InvalidRegionState, kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.ReadOnly, new(7, 2)).Error);
        Assert.Equal(KernelError.PlatformUnsupported, kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.SharedReadMostly, new(0, 8)).Error);
    }

    [Fact]
    public void RegionUsePublicSurfaceContainsOnlyLogicalProviderNeutralIdentity()
    {
        var forbidden = new[] { "Cxl", "Hdm", "Dpa", "Hpa", "Pasid", "Requester", "PhysicalAddress", "Route", "Port", "ProviderToken" };
        var surface = typeof(RegionUseDescriptor).Assembly.GetExportedTypes()
            .Where(type => type.Name.Contains("RegionUse", StringComparison.Ordinal) || type == typeof(MutationEpoch))
            .SelectMany(type => new[] { type.Name }
                .Concat(type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Select(property => property.Name))
                .Concat(type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Select(field => field.Name)))
            .ToArray();

        Assert.NotEmpty(surface);
        Assert.DoesNotContain(surface, name => forbidden.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class FakeExternalProvider(RuntimeKernel kernel)
    {
        public int AcceptedSubmissions { get; private set; }
        public int Publications { get; private set; }

        public KernelResult Submit(ProcessHandle principal, RegionUseHandle use)
        {
            var validation = kernel.ValidateRegionUse(principal, use);
            if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
            AcceptedSubmissions++;
            return KernelResult.Ok();
        }

        public KernelResult Publish(ProcessHandle principal, RegionUseHandle use)
        {
            var validation = kernel.ValidateRegionUse(principal, use);
            if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
            Publications++;
            return KernelResult.Ok();
        }
    }
}
