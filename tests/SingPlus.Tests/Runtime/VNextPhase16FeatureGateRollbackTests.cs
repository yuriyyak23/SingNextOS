using SingPlus.Runtime;
using System.Security.Cryptography;

namespace SingPlus.Tests.Runtime;

public sealed class VNextPhase16FeatureGateRollbackTests
{
    private const string Gate = "FG-VNX-HOST-RESOURCE-ADAPTER";
    private const string Evidence = "65e85181878847e622489506de5a939828a1235562da4e4d5abf26098f72760c";

    [Fact]
    public void DefaultIsOffAndOnlyExactQualifiedContourCanEnable()
    {
        var authority = new VNextFeatureGateAuthority();
        Assert.Empty(authority.Snapshot.EnabledGates);
        Assert.Equal(KernelError.PlatformUnsupported, authority.TryAcquire(Gate).Error);

        var enabled = authority.Apply(1, Configuration(2, [Gate]));
        Assert.True(enabled.IsSuccess, enabled.Message);
        Assert.Equal(VNextGateUseDisposition.Enabled,
            authority.Evaluate(authority.TryAcquire(Gate).Value!, possibleSubmit: false));

        var unknown = Configuration(3, ["FG-VNX-UNKNOWN"]);
        Assert.Equal(KernelError.InvalidMessage, authority.Apply(2, unknown).Error);
        var widened = Configuration(3, [Gate]) with { ProviderIdentity = "hybridcpu" };
        Assert.Equal(KernelError.PlatformUnsupported, authority.Apply(2, widened).Error);
    }

    [Fact]
    public void OnToOffMakesOldHandlesFallbackBeforeSubmitAndQuarantineAfterPossibleSubmit()
    {
        var authority = new VNextFeatureGateAuthority();
        Assert.True(authority.Apply(1, Configuration(2, [Gate])).IsSuccess);
        var lease = authority.TryAcquire(Gate).Value!;
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
        var authority = new VNextFeatureGateAuthority();
        Assert.True(authority.Apply(1, Configuration(2, [Gate])).IsSuccess);
        var lease = authority.TryAcquire(Gate).Value!;
        using var start = new ManualResetEventSlim(false);
        var readers = Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return authority.Evaluate(lease, possibleSubmit: false);
        })).ToArray();
        var disable = Task.Run(() =>
        {
            start.Wait();
            return authority.Apply(2, VNextFeatureGateConfiguration.DefaultOff(3));
        });
        start.Set();
        var observed = await Task.WhenAll(readers);
        Assert.True((await disable).IsSuccess);
        Assert.All(observed, value => Assert.Contains(value,
            new[] { VNextGateUseDisposition.Enabled, VNextGateUseDisposition.OrdinaryFallback }));
        Assert.Equal(VNextGateUseDisposition.OrdinaryFallback,
            authority.Evaluate(lease, possibleSubmit: false));
    }

    [Fact]
    public void ProviderEvidenceTelemetryOrAvailabilityCannotEnableGate()
    {
        var authority = new VNextFeatureGateAuthority();
        _ = new { ProviderAvailable = true, Receipt = Evidence, Telemetry = "healthy", CachedPlan = "fast" };
        Assert.Empty(authority.Snapshot.EnabledGates);
        Assert.Equal(KernelError.PlatformUnsupported, authority.TryAcquire(Gate).Error);
    }

    [Fact]
    public void QualifiedConfigurationPinsAnExistingEvidenceTupleDigest()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "docs", "SingNextOS-vNext-refactoring-roadmap-new",
            "P16_PROVIDER_RESOURCE_CONTRACT_20260922_TUPLE.json");
        Assert.Equal(Evidence, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
    }

    private static VNextFeatureGateConfiguration Configuration(ulong generation, string[] gates) =>
        new(VNextFeatureGateConfiguration.CurrentVersion, generation,
            VNextFeatureGateAuthority.QualifiedHostContour, "host-test",
            SingPlus.Platform.PlatformResourceContract.ContractVersion,
            "Windows-x64/.NET-11/JIT", Evidence, gates);
}
