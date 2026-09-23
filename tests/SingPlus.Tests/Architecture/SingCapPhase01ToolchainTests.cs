using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

namespace SingPlus.Tests.Architecture;

public sealed class SingCapPhase01ToolchainTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void ExactToolchainGoldenMatchesBuildConfigurationAndAdmissionPolicy()
    {
        using var golden = ReadJson("eng/singcap-toolchain-v1.json");
        var root = golden.RootElement;
        using var global = ReadJson("global.json");
        Assert.Equal(global.RootElement.GetProperty("sdk").GetProperty("version").GetString(), root.GetProperty("sdkVersion").GetString());
        Assert.Equal(root.GetProperty("sdkVersion").GetString(), RunDotNet("--version"));

        var props = XDocument.Load(Path.Combine(RepositoryRoot, "Directory.Build.props"));
        Assert.Equal(root.GetProperty("defaultLanguageVersion").GetString(), Property(props, "LangVersion", element => element.Attribute("Condition") is null));
        Assert.Equal("preview", Property(props, "LangVersion", element => ((string?)element.Attribute("Condition"))?.Contains("SingPlusPreviewLanguage", StringComparison.Ordinal) == true));

        var verifier = Path.Combine(RepositoryRoot, "tools", "SingPlus.Admission", "AdmissionVerifier.cs");
        var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(verifier)));
        Assert.Equal(root.GetProperty("admissionVerifierSourceDigest").GetString(), digest);

    }

    [Fact]
    public void EveryProjectHasExactlyOneOwnedSecurityProfile()
    {
        using var inventory = ReadJson("eng/singcap-security-profiles-v1.json");
        var entries = inventory.RootElement.GetProperty("profiles").EnumerateArray().ToArray();
        var declared = entries.Select(entry => entry.GetProperty("project").GetString()!).ToArray();
        Assert.Equal(declared.Length, declared.Distinct(StringComparer.Ordinal).Count());

        var projects = Directory.EnumerateFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(static path => !IsBuildOutput(path))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(projects, declared.Order(StringComparer.Ordinal).ToArray());

        string[] profiles =
        [
            "ManagedCap", "TrustedRuntime", "NativeIsolated", "PlatformExternal", "BuildTool/TestOnly",
            "BootCore", "BootPlatformAdapter", "BootCapsule"
        ];
        foreach (var entry in entries)
        {
            Assert.Contains(entry.GetProperty("profile").GetString(), profiles);
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("owner").GetString()));
        }
    }

    [Fact]
    public void ManagedCapClaimRequiresTheExistingAdmissionGate()
    {
        using var inventory = ReadJson("eng/singcap-security-profiles-v1.json");
        var fixture = inventory.RootElement.GetProperty("profiles").EnumerateArray().Single(entry =>
            entry.GetProperty("profile").GetString() == "ManagedCap");
        Assert.Equal("RuntimeEnforced", fixture.GetProperty("claimLevel").GetString());

        var project = XDocument.Load(Path.Combine(RepositoryRoot, fixture.GetProperty("project").GetString()!.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Equal("ManagedCap", Property(project, "SingCapSecurityProfile", _ => true));
        Assert.Equal("true", Property(project, "SingPlusEnableAdmission", _ => true));
        Assert.Equal("ManagedCap", Property(project, "SingPlusMemoryProfile", _ => true));
        Assert.Equal("SingCap.ManagedCap.NativeAotFixture.Program::Main", Property(project, "SingPlusAdmissionRoot", _ => true));
    }

    [Fact]
    public void NativeIsolatedRequiresAnExplicitIndependentBoundary()
    {
        using var inventory = ReadJson("eng/singcap-security-profiles-v1.json");
        foreach (var entry in inventory.RootElement.GetProperty("profiles").EnumerateArray()
                     .Where(entry => entry.GetProperty("profile").GetString() == "NativeIsolated"))
        {
            Assert.True(entry.TryGetProperty("isolationBoundary", out var boundary));
            Assert.Contains(boundary.GetProperty("kind").GetString(), new[] { "Process", "VM", "PlatformProtectionDomain" });
            Assert.False(string.IsNullOrWhiteSpace(boundary.GetProperty("evidence").GetString()));
        }
    }

    [Fact]
    public void NativeAotFixtureIsExplicitAndUsesManagedCapAdmission()
    {
        var project = XDocument.Load(Path.Combine(RepositoryRoot, "tests", "fixtures", "SingCap.ManagedCap.NativeAotFixture", "SingCap.ManagedCap.NativeAotFixture.csproj"));
        Assert.Equal("true", Property(project, "PublishAot", _ => true));
        Assert.Equal("ManagedCap", Property(project, "SingCapSecurityProfile", _ => true));
        Assert.Empty(project.Descendants("ProjectReference"));
        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Equal("ManagedCap", Property(project, "SingPlusMemoryProfile", _ => true));
        Assert.Equal("SingCap.ManagedCap.NativeAotFixture.Program::Main", Property(project, "SingPlusAdmissionRoot", _ => true));
    }

    private static JsonDocument ReadJson(string relative) =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(RepositoryRoot, relative.Replace('/', Path.DirectorySeparatorChar))));

    private static string? Property(XDocument document, string name, Func<XElement, bool> predicate) =>
        document.Descendants(name).Single(predicate).Value.Trim();

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static string RunDotNet(string argument)
    {
        using var process = Process.Start(new ProcessStartInfo("dotnet", argument)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("dotnet process did not start.");
        var output = process.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("SingNextOS repository root was not found.");
    }
}
