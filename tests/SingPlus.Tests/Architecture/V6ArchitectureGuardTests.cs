using System.Reflection;
using System.Text.RegularExpressions;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Architecture;

public sealed partial class V6ArchitectureGuardTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void V6GateRegistryIsClosedCompleteAndDefaultOff()
    {
        string[] required =
        [
            "V6-DEVICE-ATTESTATION", "V6-DIRECT-COHERENT-MUTABLE-OUTPUT",
            "V6-DMA-GENERATION-BINDING", "V6-DURABLE-OUTPUT", "V6-ENERGY-BUDGETS",
            "V6-FIRST-QUALIFICATION-VERTICAL", "V6-FORMAL-REFINEMENT", "V6-GUARANTEED-DEADLINE", "V6-IFC",
            "V6-LOCALITY-PLANNING", "V6-MULTIHOST-LEASES", "V6-PREEMPTION", "V6-PROOF-CARRYING-LOWERING", "V6-RAS-PARTIAL-FAILURE",
            "V6-SHARED-ATOMIC-REGION", "V6-STAGED-EXCLUSIVE-MEMORY", "V6-STATEFUL-RESUME",
            "V6-TEMPORAL-ACCOUNTING",
        ];

        Assert.Equal(Enum.GetValues<V6FeatureGate>().Length, V6FeatureGates.Names.Count);
        Assert.Equal(required, V6FeatureGates.Names);
        Assert.All(required, name => Assert.False(V6FeatureGates.IsEnabled(name), name));
        Assert.False(V6FeatureGates.TryResolve("V6-UNKNOWN", out var unknown));
        Assert.False(Enum.IsDefined(unknown));
    }

    [Fact]
    public void ProductionSourcesDeclareNoForbiddenUniversalOrDuplicateAuthorityOwner()
    {
        string[] forbidden =
        [
            "TemporalAuthority", "PowerAuthority", "EnergyAuthority", "TranslationAuthority",
            "TopologyAuthority", "TrustAuthority", "ProofAuthority", "RasAuthority",
            "MemoryAuthority", "GlobalState", "OperationObligationsV2", "ExecutionGuaranteesV2",
            "SemanticExecutionBindingV2",
        ];
        var declarations = ProductionSources().SelectMany(path =>
            TypeDeclarationPattern().Matches(File.ReadAllText(path)).Select(match =>
                (Path: Relative(path), Name: match.Groups[1].Value))).ToArray();

        foreach (var name in forbidden)
            Assert.DoesNotContain(declarations, item => item.Name == name);

        var runtimeTypes = typeof(RuntimeKernel).Assembly.GetTypes();
        Assert.Single(runtimeTypes, type => type.Name == "CapabilityAuthority");
        Assert.Single(runtimeTypes, type => type.Name == "RegionAuthority");
        Assert.Single(runtimeTypes, type => type.Name == "ResourceBudgetAuthority");
        Assert.Single(runtimeTypes, type => type.Name == "ExternalOperationAuthority");
    }

    [Fact]
    public void NewEvidenceAndCorrelationContractsExposeNoAuthorityBearingHandle()
    {
        var guarded = typeof(SemanticExtensionClauseV1).Assembly.GetExportedTypes()
            .Where(type => type.Namespace == typeof(SemanticExtensionClauseV1).Namespace &&
                           (type.Name.Contains("SemanticExtension", StringComparison.Ordinal) ||
                            type.Name.Contains("ClaimEvidence", StringComparison.Ordinal) ||
                            type.Name.Contains("QualificationTransition", StringComparison.Ordinal) ||
                            type.Name.Contains("FirstQualificationVertical", StringComparison.Ordinal) ||
                            type.Name.Contains("FailureSemantics", StringComparison.Ordinal) ||
                            type.Name.Contains("DurabilitySemantics", StringComparison.Ordinal)))
            .ToArray();
        string[] forbidden = ["Capability", "RegionHandle", "OwnedRegion", "AuthorityToken", "Permission"];

        foreach (var member in guarded.SelectMany(PublicMemberNames))
            foreach (var fragment in forbidden)
                Assert.DoesNotContain(fragment, member, StringComparison.OrdinalIgnoreCase);

        var tuple = new V6ClaimEvidenceTuple(1, "contract-only", V6ClaimLevel.StaticAdmission,
            "singnext", "provider-inventory", "sdk", "schema", "provider", 1,
            new string('a', 64), new string('b', 64)).Validate();
        Assert.False(tuple.AuthorizesExecution);
        Assert.False(tuple.AuthorizesEffect);
        Assert.False(tuple.AuthorizesPublication);
    }

    [Fact]
    public void ClaimVocabularyIsExactAndNotAnOrdinalPromotionScale()
    {
        Assert.Equal([
            "ModelOnly", "StaticAdmission", "RuntimeEnforced", "ExecutableAdapter",
            "EnforcedUpperBound", "GuaranteedReservation", "ProductionQualified", "FutureGated"
        ], Enum.GetNames<V6ClaimLevel>());
        Assert.NotEqual((byte)V6ClaimLevel.RuntimeEnforced + 1, (byte)V6ClaimLevel.ProductionQualified);
    }

    private static IEnumerable<string> PublicMemberNames(Type type)
    {
        yield return type.Name;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            yield return property.Name;
            yield return property.PropertyType.Name;
        }
    }

    private static IEnumerable<string> ProductionSources()
    {
        string[] roots = ["contracts", "src", "sdk", "tools"];
        return roots.Select(root => Path.Combine(RepositoryRoot, root))
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
    }

    private static string Relative(string path) => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/');

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("SingNextOS repository root not found.");
    }

    [GeneratedRegex(@"\b(?:class|record|struct)\s+(\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex TypeDeclarationPattern();
}
