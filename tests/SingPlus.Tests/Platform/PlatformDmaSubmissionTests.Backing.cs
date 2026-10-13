using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed partial class PlatformDmaSubmissionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ActualDmaBackingRemainsReservedThroughPendingOrUnknownAndChargeTracksRegions(bool unknown)
    {
        BudgetAccountHandle budget = default;
        var s = CreateCopyScenario(1610,2540,(kernel,subject) =>
        {
            var admin = TestFixtures.Create(kernel,98,998).Handle;
            var cap = kernel.MintCapability(new(998),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
            budget = kernel.AdmitProcessBudget(admin,cap,subject,"dma-backing",[new(ServiceBudgetDimension.OwnedMemoryBytes,128)]).Value!.ProcessBudget;
        });
        s.Input.Span.Fill(0x53);
        var sp = s.Kernel.PreparePlatformDmaForDevice(s.Subject,s.Source).Value!;
        var dp = s.Kernel.PreparePlatformDmaForDevice(s.Subject,s.Destination).Value!;
        KernelResult<V6DataMotionExecution<byte>> Move() => s.Kernel.ExecuteV6ProviderDmaDataMotion(s.Subject,s.Subject,s.Input,s.Output,s.Plan,
            s.SourceMapping,s.Source,sp,s.DestinationMapping,s.Destination,dp,() => 3,() => 5);
        s.Provider.KeepWriteCompletionPending = !unknown;
        s.Provider.CopyEffect = () =>
        {
            Assert.Equal(KernelError.PlatformBindingDraining,Move().Error);
            Assert.Throws<InvalidOperationException>(() => s.Input.Span.ToArray());
            Assert.Throws<InvalidOperationException>(() => s.Output.Span.ToArray());
            if (unknown) throw new IOException("Lost copy response after byte effect");
        };
        Assert.False(Move().IsSuccess);
        Assert.Equal(1,s.Provider.CopySubmitCalls);
        Assert.Throws<InvalidOperationException>(() => s.Input.Span.ToArray());
        Assert.Throws<InvalidOperationException>(() => s.Output.Span.ToArray());
        Assert.All(s.Provider.CopyBacking!.Destination.Span.ToArray(),value => Assert.Equal((byte)0x53,value));
        if (unknown) Assert.False(s.Kernel.TerminateProcess(s.Subject).IsSuccess);
        Assert.Equal(128UL,s.Kernel.QueryBudget(budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        s.Provider.CopyEffect = null; s.Provider.KeepWriteCompletionPending = false;
        if (unknown)
        {
            Assert.False(Move().IsSuccess); Assert.Equal(1,s.Provider.CopySubmitCalls);
            Assert.All(s.Kernel.Regions.Snapshot(),region => Assert.Equal(RegionState.Owned,region.State));
            Assert.Throws<InvalidOperationException>(() => s.Output.Span.ToArray());
        }
        else
        {
            var moved = Move(); Assert.True(moved.IsSuccess,moved.Message);
            Assert.Equal(1,s.Provider.CopySubmitCalls);
            Assert.All(moved.Value!.Moved.Span.ToArray(),value => Assert.Equal((byte)0x53,value));
            Assert.Equal(64UL,s.Kernel.QueryBudget(budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.True(s.Kernel.TerminateProcess(s.Subject).IsSuccess);
            Assert.Equal(0UL,s.Kernel.QueryBudget(budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Throws<InvalidOperationException>(() => s.Provider.CopyBacking!.Source.Span.ToArray());
            Assert.Throws<InvalidOperationException>(() => s.Provider.CopyBacking!.Destination.Span.ToArray());
        }
    }

    [Fact]
    public void ActualDmaBackingExactNoEffectReleasesReservationsAndSuccessfulRetryConsumesBytes()
    {
        var s = CreateCopyScenario(1611,2541); s.Input.Span.Fill(0x37);
        var sp = s.Kernel.PreparePlatformDmaForDevice(s.Subject,s.Source).Value!;
        var dp = s.Kernel.PreparePlatformDmaForDevice(s.Subject,s.Destination).Value!;
        KernelResult<V6DataMotionExecution<byte>> Move() => s.Kernel.ExecuteV6ProviderDmaDataMotion(s.Subject,s.Subject,s.Input,s.Output,s.Plan,
            s.SourceMapping,s.Source,sp,s.DestinationMapping,s.Destination,dp,() => 3,() => 5);
        s.Provider.CopySubmitStatus = PlatformAuthorityStatus.NotAccepted;
        Assert.False(Move().IsSuccess);
        Assert.Equal(64,s.Input.Span.Length); Assert.Equal(64,s.Output.Span.Length);
        Assert.Throws<InvalidOperationException>(() => s.Provider.CopyBacking!.Source.Span.ToArray());
        s.Provider.CopySubmitStatus = null;
        var moved = Move(); Assert.True(moved.IsSuccess,moved.Message);
        Assert.All(moved.Value!.Moved.Span.ToArray(),value => Assert.Equal((byte)0x37,value));
        Assert.Equal(2,s.Provider.CopySubmitCalls);
        Assert.Equal(RegionState.Released,s.Kernel.Regions.Snapshot().Single(r => r.Handle == s.Input.Handle).State);
        Assert.Throws<InvalidOperationException>(() => s.Provider.CopyBacking!.Destination.Span.ToArray());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ActualDmaBackingLateDestinationLossCannotPublishConsumedOriginalOutput(bool transfer)
    {
        var s = CreateCrossOwnerCopyScenario(1612,2542,1613,2543);
        var sp = s.Kernel.PreparePlatformDmaForDevice(s.SourceSubject,s.Source).Value!;
        var dp = s.Kernel.PreparePlatformDmaForDevice(s.DestinationSubject,s.Destination).Value!;
        var next = TestFixtures.Create(s.Kernel,1614,2544).Handle;
        s.Kernel.BeforeRegionReleaseQualificationHook = () =>
        {
            s.Kernel.BeforeRegionReleaseQualificationHook = null;
            if (transfer) Assert.Equal(KernelError.RegionUseConflict,s.Kernel.TransferRegion(s.DestinationSubject,next,s.Output).Error);
            else Assert.True(s.Kernel.TerminateProcess(s.DestinationSubject).IsSuccess);
        };
        var moved = s.Kernel.ExecuteV6ProviderDmaDataMotion(s.SourceSubject,s.DestinationSubject,s.Input,s.Output,s.Plan,
            s.SourceMapping,s.Source,sp,s.DestinationMapping,s.Destination,dp,() => 3,() => 5);
        Assert.Equal(transfer,moved.IsSuccess); Assert.False(s.Input.IsValid);
        if(transfer)Assert.True(s.Kernel.TransferRegion(s.DestinationSubject,next,s.Output).IsSuccess);
        Assert.False(s.Output.IsValid);
        Assert.Equal(1,s.Provider.CopySubmitCalls); Assert.Equal(2,s.Provider.DmaRevokeCalls); Assert.Equal(2,s.Provider.MappingRevokeCalls);
        var payloads = (global::System.Collections.IDictionary)s.Kernel.GetType().GetField("_dmaCopyPayloads",global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        Assert.Empty(payloads);
        Assert.Throws<InvalidOperationException>(() => s.Provider.CopyBacking!.Source.Span.ToArray());
        Assert.Throws<InvalidOperationException>(() => s.Provider.CopyBacking!.Destination.Span.ToArray());
    }
}
