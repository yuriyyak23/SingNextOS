using System.Text.Json;

namespace SingPlus.Tests.Architecture;

public sealed class SingCapPhase12ProviderMappingTests
{
    [Fact]
    public void ProviderMappingPinsExactArtifactAndKeepsEveryReceiptNonAuthoritative()
    {
        var path = Path.Combine(RepositoryRoot(), "docs", "Completed", "SingCap-Refactoring", "roadmap",
            "P12_HYBRIDCPU_PROVIDER_MAPPING.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;

        Assert.Equal("SingCapHybridCpuProviderMappingV1", root.GetProperty("schema").GetString());
        Assert.Equal("794c4a53494f503855ac8cf209efab23fde083b2",
            root.GetProperty("hybridCpuSourceCommit").GetString());
        var package = root.GetProperty("contractsPackage");
        Assert.Equal("1.14.0", package.GetProperty("version").GetString());
        Assert.Equal("B96E99BDA066EE585B26A11CBFA7B68CE6BF44FC0006679483CCC1A4EEB678C2",
            package.GetProperty("sha256").GetString());
        Assert.True(package.GetProperty("repositoryArtifactEqualsRestoredArtifact").GetBoolean());

        var mappings = root.GetProperty("mappings").EnumerateArray().ToArray();
        Assert.Equal(7, mappings.Length);
        Assert.All(mappings, mapping =>
            Assert.NotEqual("authority", mapping.GetProperty("classification").GetString()));
        Assert.False(root.GetProperty("hybridCpuCoreModified").GetBoolean());
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
