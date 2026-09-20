using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip.Virtualization;

namespace SingPlus.Tests.Architecture;

public sealed partial class VNextPhase00ArchitecturalFreezeTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string RoadmapRoot = Path.Combine(
        RepositoryRoot, "docs", "SingNextOS-vNext-refactoring-roadmap-new");

    [Fact]
    public void EveryDeclaredVNextGateExistsExactlyOnceAndIsOff()
    {
        var documented = File.ReadLines(Path.Combine(RoadmapRoot, "VNEXT_FEATURE_GATES.md"))
            .Select(line => GateNamePattern().Match(line))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.Equal(Enum.GetValues<VNextFeatureGate>().Length, VNextFeatureGates.Names.Count);
        Assert.Equal(documented.Order(StringComparer.Ordinal), VNextFeatureGates.Names);
        Assert.Equal(documented.Length, documented.Distinct(StringComparer.Ordinal).Count());
        Assert.All(VNextFeatureGates.Names, gate => Assert.False(VNextFeatureGates.IsEnabled(gate)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("FG-VNX-UNKNOWN")]
    [InlineData("fg-vnx-resource-grant")]
    [InlineData("FG-VNX-RESOURCE-GRANT ")]
    public void UnknownOrNonCanonicalGateFailsClosed(string? name)
    {
        Assert.False(VNextFeatureGates.TryResolve(name, out var resolved));
        Assert.False(Enum.IsDefined(resolved));
        Assert.NotEqual(VNextFeatureGate.ResourceGrant, resolved);
        Assert.False(VNextFeatureGates.IsEnabled(name));
    }

    [Fact]
    public void ProductionTreeHasNoTemporalResourceAuthorityOrDuplicateOwnerStore()
    {
        var sources = ProductionSources()
            .Select(path => (Path: path, Source: File.ReadAllText(path)))
            .ToArray();

        Assert.DoesNotContain(sources, item => TemporalResourceAuthorityPattern().IsMatch(item.Source));
        Assert.Equal(
            ["src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs"],
            sources.Where(item => CapabilityStorePattern().IsMatch(item.Source))
                .Select(item => Relative(item.Path)).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs"],
            sources.Where(item => BudgetStorePattern().IsMatch(item.Source))
                .Select(item => Relative(item.Path)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void PublicSipManagedCapAndBudgetAbiContainsNoProviderPrivateAuthorityVocabulary()
    {
        string[] forbidden =
        [
            "LaneId", "RawOpcode", "SlotIndex", "DscToken", "L7Token", "Vmcs", "Iommu",
            "CxlHdm", "CxlDpa", "PhysicalAddress", "ProviderPrivateHandle",
        ];
        var assemblies = new[] { typeof(ServiceManifestV1).Assembly, typeof(IVirtualizationService).Assembly };
        var exposedNames = assemblies.SelectMany(assembly => assembly.GetExportedTypes())
            .SelectMany(PublicSurfaceNames)
            .ToArray();

        foreach (var fragment in forbidden)
            Assert.DoesNotContain(exposedNames, name => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));

        Assert.False(new BudgetReservationSnapshot(default, default, default, [],
            BudgetReservationLifetime.LocalResource, AdmissionQosHint.LatencySensitive,
            BudgetReservationState.Active).AuthorizesEffect);
        Assert.False(new BudgetAccountSnapshot(default, BudgetAccountLevel.System, null, "system", [],
            BudgetPressureState.Normal).MaterializesAuthority);
    }

    [Fact]
    public void BaselineAndManifestAreMachineReadableAndPinnedToNormativeSha()
    {
        using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(RoadmapRoot, "BASELINE.json")));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(RoadmapRoot, "MANIFEST.json")));
        const string expected = "6227ea7cf258ef6ffce52001d4d2ffee07355b35";

        Assert.Equal(expected, baseline.RootElement.GetProperty("SingNextOS").GetProperty("sha").GetString());
        Assert.Equal(expected, manifest.RootElement.GetProperty("baseline").GetProperty("SingNextOS").GetString());
        Assert.Equal(18, manifest.RootElement.GetProperty("phase_count").GetInt32());

        foreach (var entry in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            var path = Path.Combine(RoadmapRoot, entry.GetProperty("file").GetString()!);
            Assert.True(File.Exists(path), $"Manifest path does not exist: {path}");
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(entry.GetProperty("bytes").GetInt64(), bytes.LongLength);
            Assert.Equal(entry.GetProperty("sha256").GetString(),
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
    }

    private static IEnumerable<string> PublicSurfaceNames(Type type)
    {
        yield return type.FullName ?? type.Name;
        foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            yield return member.Name;
    }

    private static IEnumerable<string> ProductionSources()
    {
        string[] roots = ["contracts", "src", "sdk", "tools"];
        return roots.Select(root => Path.Combine(RepositoryRoot, root))
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
    }

    private static string Relative(string path) =>
        Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/');

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("SingNextOS repository root was not found.");
    }

    [GeneratedRegex(@"`(FG-VNX-[A-Z0-9-]+)`\s*\|\s*OFF\s*\|", RegexOptions.CultureInvariant)]
    private static partial Regex GateNamePattern();

    [GeneratedRegex(@"\b(?:class|record|struct)\s+TemporalResourceAuthority\b", RegexOptions.CultureInvariant)]
    private static partial Regex TemporalResourceAuthorityPattern();

    [GeneratedRegex(@"Dictionary\s*<\s*(?:[\w.]+\.)?CapabilityId\s*,", RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityStorePattern();

    [GeneratedRegex(@"Dictionary\s*<\s*(?:[\w.]+\.)?BudgetReservationId\s*,", RegexOptions.CultureInvariant)]
    private static partial Regex BudgetStorePattern();
}
