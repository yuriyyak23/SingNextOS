using System.Security.Cryptography;
using YAKSys_Hybrid_CPU.ExecutableAdapter.Boot;
using Xunit;

namespace HybridCpu_ExecutableAdapter.Tests;

public sealed class AbRecoveryModelTests
{
    private static readonly Guid Domain = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void PowerLossAtEveryPersistenceBarrierPreservesConfirmedAndRecovery(int barrier)
    {
        var model = Model();
        var result = model.InstallInactive(Image(2), Guid.NewGuid(), 2, barrier);
        Assert.Equal(AbUpdateFailure.PowerLoss, result.Failure);
        Assert.True(result.ConfirmedRouteAvailable);
        Assert.True(result.RecoveryRouteAvailable);
    }

    [Fact]
    public void PayloadIsReadBackBeforeManifestAndTrialPublication()
    {
        var bad = Image(2) with { ExpectedSha384 = new byte[48] };
        var model = Model();
        var result = model.InstallInactive(bad, Guid.NewGuid(), 2);
        Assert.Equal(AbUpdateFailure.ReadBackMismatch, result.Failure);
        Assert.Null(model.Trial);
        Assert.Equal(RecoveryRoute.Confirmed, model.Select([]).Route);
    }

    [Fact]
    public void ConfirmationIsBoundAndAloneAdvancesRollbackFloor()
    {
        var domain = Domain; var nonce = Guid.NewGuid(); var image = Image(7); var model = Model();
        Assert.Equal(AbUpdateFailure.None, model.InstallInactive(image, nonce, 2).Failure);
        Assert.Equal(0UL, model.RollbackFloor(domain));
        Assert.Equal(AbUpdateFailure.WrongConfirmation, model.Confirm(domain, image.ImageId, 7, nonce));
        Assert.Equal(RecoveryRoute.Trial, model.Select([]).Route);
        Assert.Equal(AbUpdateFailure.WrongConfirmation, model.Confirm(domain, image.ImageId, 7, Guid.NewGuid()));
        Assert.Equal(0UL, model.RollbackFloor(domain));
        Assert.Equal(AbUpdateFailure.WrongConfirmation, model.Confirm(Guid.NewGuid(), image.ImageId, 7, nonce));
        Assert.Equal(AbUpdateFailure.None, model.Confirm(domain, image.ImageId, 7, nonce));
        Assert.Equal(7UL, model.RollbackFloor(domain));
        Assert.Equal(AbUpdateFailure.WrongConfirmation, model.Confirm(domain, image.ImageId, 7, nonce));
        Assert.Equal(AbUpdateFailure.RollbackRejected, model.InstallInactive(Image(6), Guid.NewGuid(), 2).Failure);
    }

    [Fact]
    public void BoundedTrialFallsBackConfirmedThenReplicaThenSignedRecovery()
    {
        var model = Model(); var image = Image(2);
        Assert.Equal(AbUpdateFailure.None, model.InstallInactive(image, Guid.NewGuid(), 1).Failure);
        Assert.Equal(RecoveryRoute.Trial, model.Select([]).Route);
        Assert.Equal(RecoveryRoute.Confirmed, model.Select([]).Route);
        model.CorruptConfirmedForTest();
        Assert.Equal(RecoveryRoute.Replica, model.Select([Image(3)]).Route);
        Assert.Equal(RecoveryRoute.SignedLocalRecovery, model.Select([]).Route);
    }

    [Fact]
    public void CorruptConfirmedAndRecoveryImagesReachDiagnosticHalt()
    {
        var model = Model();
        model.CorruptConfirmedForTest();
        model.CorruptLocalRecoveryForTest();
        var decision = model.Select([]);
        Assert.Equal(RecoveryRoute.Halt, decision.Route);
        Assert.Null(decision.Image);
    }

    [Fact]
    public void Replica_below_advanced_floor_is_never_selected()
    {
        var model = Model(); var image = Image(5); var nonce = Guid.NewGuid();
        Assert.Equal(AbUpdateFailure.None, model.InstallInactive(image, nonce, 1).Failure);
        Assert.Equal(RecoveryRoute.Trial, model.Select([]).Route);
        Assert.Equal(AbUpdateFailure.None, model.Confirm(Domain, image.ImageId, image.Generation, nonce));
        model.CorruptConfirmedForTest();
        Assert.Equal(RecoveryRoute.SignedLocalRecovery, model.Select([Image(4)]).Route);
    }

    private static AbRecoveryModel Model() => new(Domain, AbSlot.A, Image(1), Image(0));
    private static AbImage Image(ulong generation)
    {
        var payload = Enumerable.Range(0, 64).Select(i => (byte)((ulong)i + generation)).ToArray();
        return new(Guid.NewGuid(), generation, payload, SHA384.HashData(payload), true);
    }
}
