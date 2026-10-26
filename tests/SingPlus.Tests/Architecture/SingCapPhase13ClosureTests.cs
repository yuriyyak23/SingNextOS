using System.Text.Json;

namespace SingPlus.Tests.Architecture;

public sealed class SingCapPhase13ClosureTests
{
    [Fact]
    public void InventoryMakesNoNativeIsolatedClaim()
    {
        string path = Path.Combine(RepositoryRoot(), "eng", "singcap-security-profiles-v1.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.DoesNotContain(document.RootElement.GetProperty("profiles").EnumerateArray(),
            profile => profile.GetProperty("profile").GetString() == "NativeIsolated");
    }

    [Fact]
    public void ReleaseGateContainsLockedRestoreNativeAotStressAndPerformanceStages()
    {
        string script = File.ReadAllText(Path.Combine(RepositoryRoot(), "eng", "qualify-singcap-p13.ps1"));
        Assert.Contains("--locked-mode", script, StringComparison.Ordinal);
        Assert.Contains("dotnet test $solution", script, StringComparison.Ordinal);
        Assert.Contains("qualify-singcap-p01.ps1", script, StringComparison.Ordinal);
        Assert.Contains("SingPlus.SingCapQualification", script, StringComparison.Ordinal);
        Assert.Contains("@(1, 2, 4, 8, 16, 32)", script, StringComparison.Ordinal);
        Assert.Contains("git diff --check", script, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
