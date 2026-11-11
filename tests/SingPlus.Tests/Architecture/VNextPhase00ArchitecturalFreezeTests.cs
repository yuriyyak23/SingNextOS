using System.Reflection;
using System.Text.RegularExpressions;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip.Virtualization;

namespace SingPlus.Tests.Architecture;

public sealed partial class VNextPhase00ArchitecturalFreezeTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("FG-VNX-UNKNOWN")]
    [InlineData("fg-vnx-resource-grant")]
    [InlineData("FG-VNX-RESOURCE-GRANT ")]
    public void UnknownOrNonCanonicalGateFailsClosed(string? name)
    {
        Assert.False(VNextFeatureGates.TryResolve(name, out VNextFeatureGate resolved));
        Assert.False(Enum.IsDefined(resolved));
        Assert.NotEqual(VNextFeatureGate.ResourceGrant, resolved);
        Assert.False(VNextFeatureGates.IsEnabled(name));
    }

    [Fact]
    public void ProductionTreeHasNoTemporalResourceAuthorityOrDuplicateOwnerStore()
    {
        (string Path, string Source)[] sources = ProductionSources()
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
        Assembly[] assemblies = new[] { typeof(ServiceManifestV1).Assembly, typeof(IVirtualizationService).Assembly };
        string[] exposedNames = assemblies.SelectMany(assembly => assembly.GetExportedTypes())
            .SelectMany(PublicSurfaceNames)
            .ToArray();

        foreach (string fragment in forbidden)
            Assert.DoesNotContain(exposedNames, name => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));

        Assert.False(new BudgetReservationSnapshot(default, default, default, [],
            BudgetReservationLifetime.LocalResource, AdmissionQosHint.LatencySensitive,
            BudgetReservationState.Active).AuthorizesEffect);
        Assert.False(new BudgetAccountSnapshot(default, BudgetAccountLevel.System, null, "system", [],
            BudgetPressureState.Normal).MaterializesAuthority);
    }

    private static IEnumerable<string> PublicSurfaceNames(Type type)
    {
        yield return type.FullName ?? type.Name;
        foreach (MemberInfo member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
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
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("SingNextOS repository root was not found.");
    }

    [GeneratedRegex(@"\b(?:class|record|struct)\s+TemporalResourceAuthority\b", RegexOptions.CultureInvariant)]
    private static partial Regex TemporalResourceAuthorityPattern();

    [GeneratedRegex(@"Dictionary\s*<\s*(?:[\w.]+\.)?CapabilityId\s*,", RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityStorePattern();

    [GeneratedRegex(@"Dictionary\s*<\s*(?:[\w.]+\.)?BudgetReservationId\s*,", RegexOptions.CultureInvariant)]
    private static partial Regex BudgetStorePattern();
}
