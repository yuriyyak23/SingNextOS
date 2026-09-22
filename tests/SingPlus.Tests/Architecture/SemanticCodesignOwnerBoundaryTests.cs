using System.Reflection;
using System.Text.RegularExpressions;
using Hc = HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;

namespace SingPlus.Tests.Architecture;

public sealed partial class SemanticCodesignOwnerBoundaryTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void ProductionTreeContainsNoSemanticCodesignUniversalOrDuplicateAuthorityType()
    {
        string[] forbiddenTypeNames =
        [
            "GlobalStateManager",
            "UniversalAuthority",
            "TemporalResourceAuthority",
            "SemanticExecutionBindingAuthority",
            "OperationObligationsAuthority",
            "ExecutionGuaranteesAuthority",
            "PublicationAuthorityLedger",
            "RegionAuthorityLedger",
            "ResourceBudgetAuthorityLedger",
        ];

        var declarations = ProductionSources()
            .Select(path => (Path: Relative(path), Source: File.ReadAllText(path)))
            .SelectMany(item => TypeDeclarationPattern().Matches(item.Source)
                .Select(match => (item.Path, Name: match.Groups[1].Value)))
            .Where(item => forbiddenTypeNames.Contains(item.Name, StringComparer.Ordinal))
            .ToArray();

        Assert.Empty(declarations);
    }

    [Fact]
    public void SemanticDescriptorsAndBindingsExposeNoAuthorityMutationVerb()
    {
        string[] semanticTypeFragments =
        [
            "OperationObligations",
            "ExecutionGuarantees",
            "SemanticExecutionBinding",
            "RefinementResult",
        ];
        string[] authorityVerbs =
        [
            "Authorize", "Mint", "Derive", "Revoke", "Reserve", "Charge", "Settle",
            "Publish", "Release", "Reclaim", "Submit", "Execute", "Retire",
        ];

        var contractTypes = typeof(ExternalOperationContract).Assembly.GetExportedTypes()
            .Where(type => semanticTypeFragments.Any(fragment =>
                type.Name.Contains(fragment, StringComparison.Ordinal)))
            .ToArray();

        foreach (var type in contractTypes)
        {
            var mutationMembers = type.GetMethods(BindingFlags.Public | BindingFlags.Instance |
                                                  BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(member => authorityVerbs.Any(verb =>
                    member.Name.StartsWith(verb, StringComparison.Ordinal)))
                .Select(member => $"{type.FullName}.{member.Name}")
                .ToArray();
            Assert.Empty(mutationMembers);
        }
    }

    [Fact]
    public void HybridCpuExternalRuntimePublicAbiContainsNoSingNextAuthorityIdentity()
    {
        string[] forbiddenPrivateTopology =
        [
            "PhysicalAddress", "CoreId", "LaneId", "SlotIndex",
        ];

        var contractsAssembly = typeof(Hc.ExternalOperationContract).Assembly;
        Assert.DoesNotContain(contractsAssembly.GetReferencedAssemblies(), reference =>
            reference.Name?.StartsWith("SingPlus", StringComparison.OrdinalIgnoreCase) == true);

        var publicTypes = contractsAssembly.GetExportedTypes();
        var publicNames = publicTypes
            .SelectMany(PublicSurfaceNames)
            .ToArray();

        foreach (var fragment in forbiddenPrivateTopology)
            Assert.DoesNotContain(publicNames,
                name => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(publicTypes.SelectMany(PublicSignatureTypes), type =>
            type.Assembly == typeof(CapabilityId).Assembly ||
            type.Namespace?.StartsWith("SingPlus", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void CanonicalOwnerStoresRemainInTheirExistingOwnerFiles()
    {
        Assert.Equal(
            ["src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs"],
            SourcesContaining(@"Dictionary\s*<\s*CapabilityId\s*,\s*CapabilityRecord\s*>")
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            ["src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs"],
            SourcesContaining(@"Dictionary\s*<\s*BudgetReservationId\s*,\s*ReservationRecord\s*>")
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            ["src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs"],
            SourcesContaining(@"ConcurrentDictionary\s*<\s*RegionId\s*,\s*RegionRecord\s*>")
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            ["src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs"],
            SourcesContaining(@"Dictionary\s*<\s*ExternalOperationId\s*,\s*Record\s*>")
                .Order(StringComparer.Ordinal));
    }

    private static IEnumerable<string> SourcesContaining(string pattern)
    {
        var regex = new Regex(pattern, RegexOptions.CultureInvariant);
        return ProductionSources()
            .Where(path => regex.IsMatch(File.ReadAllText(path)))
            .Select(Relative);
    }

    private static IEnumerable<string> PublicSurfaceNames(Type type)
    {
        yield return type.FullName ?? type.Name;
        foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance |
                                               BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            yield return member.Name;
            if (member is MethodInfo method)
            {
                yield return method.ReturnType.ToString();
                foreach (var parameter in method.GetParameters())
                    yield return parameter.ParameterType.ToString();
            }
            else if (member is PropertyInfo property)
                yield return property.PropertyType.ToString();
            else if (member is FieldInfo field)
                yield return field.FieldType.ToString();
        }
    }

    private static IEnumerable<Type> PublicSignatureTypes(Type type)
    {
        yield return type;
        foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance |
                                               BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (member is MethodInfo method)
            {
                yield return method.ReturnType;
                foreach (var parameter in method.GetParameters())
                    yield return parameter.ParameterType;
            }
            else if (member is PropertyInfo property)
                yield return property.PropertyType;
            else if (member is FieldInfo field)
                yield return field.FieldType;
        }
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

    [GeneratedRegex(@"\b(?:class|record|struct)\s+([A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.CultureInvariant)]
    private static partial Regex TypeDeclarationPattern();
}
