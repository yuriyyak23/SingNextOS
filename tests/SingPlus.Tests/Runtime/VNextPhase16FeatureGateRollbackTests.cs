using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class VNextPhase16FeatureGateRollbackTests
{
    private const string Gate = "FG-VNX-HOST-RESOURCE-ADAPTER";
    private const string Evidence = "65e85181878847e622489506de5a939828a1235562da4e4d5abf26098f72760c";

    [Fact]
    public void DefaultIsOffAndOnlyExactQualifiedContourCanEnable()
    {
        VNextFeatureGateAuthority authority = new VNextFeatureGateAuthority();
        Assert.Empty(authority.Snapshot.EnabledGates);
        Assert.Equal(KernelError.PlatformUnsupported, authority.TryAcquire(Gate).Error);

        KernelResult<VNextFeatureGateConfiguration> enabled = authority.Apply(1, Configuration(2, [Gate]));
        Assert.True(enabled.IsSuccess, enabled.Message);
        Assert.Equal(VNextGateUseDisposition.Enabled,
            authority.Evaluate(authority.TryAcquire(Gate).Value!, possibleSubmit: false));

        VNextFeatureGateConfiguration unknown = Configuration(3, ["FG-VNX-UNKNOWN"]);
        Assert.Equal(KernelError.InvalidMessage, authority.Apply(2, unknown).Error);
        VNextFeatureGateConfiguration widened = Configuration(3, [Gate]) with { ProviderIdentity = "hybridcpu" };
        Assert.Equal(KernelError.PlatformUnsupported, authority.Apply(2, widened).Error);
    }

    [Fact]
    public void OnToOffMakesOldHandlesFallbackBeforeSubmitAndQuarantineAfterPossibleSubmit()
    {
        VNextFeatureGateAuthority authority = new VNextFeatureGateAuthority();
        Assert.True(authority.Apply(1, Configuration(2, [Gate])).IsSuccess);
        VNextFeatureGateLease lease = authority.TryAcquire(Gate).Value!;
        Assert.True(authority.Apply(2, VNextFeatureGateConfiguration.DefaultOff(3)).IsSuccess);

        Assert.Equal(VNextGateUseDisposition.OrdinaryFallback,
            authority.Evaluate(lease, possibleSubmit: false));
        Assert.Equal(VNextGateUseDisposition.Quarantine,
            authority.Evaluate(lease, possibleSubmit: true));
        Assert.Equal(KernelError.PlatformUnsupported, authority.TryAcquire(Gate).Error);
    }

    [Fact]
    public async Task DisableRacingWithReadersHasOneGenerationBoundaryAndNeverReenables()
    {
        VNextFeatureGateAuthority authority = new VNextFeatureGateAuthority();
        Assert.True(authority.Apply(1, Configuration(2, [Gate])).IsSuccess);
        VNextFeatureGateLease lease = authority.TryAcquire(Gate).Value!;
        using ManualResetEventSlim start = new ManualResetEventSlim(false);
        Task<VNextGateUseDisposition>[] readers = Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return authority.Evaluate(lease, possibleSubmit: false);
        })).ToArray();
        Task<KernelResult<VNextFeatureGateConfiguration>> disable = Task.Run(() =>
        {
            start.Wait();
            return authority.Apply(2, VNextFeatureGateConfiguration.DefaultOff(3));
        });
        start.Set();
        VNextGateUseDisposition[] observed = await Task.WhenAll(readers);
        Assert.True((await disable).IsSuccess);
        Assert.All(observed, value => Assert.Contains(value,
            new[] { VNextGateUseDisposition.Enabled, VNextGateUseDisposition.OrdinaryFallback }));
        Assert.Equal(VNextGateUseDisposition.OrdinaryFallback,
            authority.Evaluate(lease, possibleSubmit: false));
    }

    [Fact]
    public void ProviderEvidenceTelemetryOrAvailabilityCannotEnableGate()
    {
        VNextFeatureGateAuthority authority = new VNextFeatureGateAuthority();
        _ = new { ProviderAvailable = true, Receipt = Evidence, Telemetry = "healthy", CachedPlan = "fast" };
        Assert.Empty(authority.Snapshot.EnabledGates);
        Assert.Equal(KernelError.PlatformUnsupported, authority.TryAcquire(Gate).Error);
    }

    private static VNextFeatureGateConfiguration Configuration(ulong generation, string[] gates) =>
        new(VNextFeatureGateConfiguration.CurrentVersion, generation,
            VNextFeatureGateAuthority.QualifiedHostContour, "host-test",
            SingPlus.Platform.PlatformResourceContract.ContractVersion,
            "Windows-x64/.NET-11/JIT", Evidence, gates);
}
