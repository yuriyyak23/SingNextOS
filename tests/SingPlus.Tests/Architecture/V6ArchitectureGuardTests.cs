using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Architecture;

public sealed partial class V6ArchitectureGuardTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Theory]
    [InlineData("HybridCPU.ExternalRuntime")]
    [InlineData("HybridCPU.ExternalRuntime.Contracts")]
    public void CurrentCrossProjectPackageVocabularyMatchesAdapterProjectAndLock(string packageId)
    {
        string adapter = Path.Combine(RepositoryRoot, "tools", "HybridCpu_ExecutableAdapter");
        var project = XDocument.Load(Path.Combine(adapter, "HybridCpu_ExecutableAdapter.csproj"));
        var reference = Assert.Single(project.Descendants("PackageReference"),
            element => (string?)element.Attribute("Include") == packageId);
        string pin = (string)reference.Attribute("Version")!;
        Assert.StartsWith("[", pin);
        Assert.EndsWith("]", pin);
        string version = pin[1..^1];
        using var locked = JsonDocument.Parse(File.ReadAllText(Path.Combine(adapter, "packages.lock.json")));
        var dependency = locked.RootElement.GetProperty("dependencies").GetProperty("net11.0").GetProperty(packageId);
        Assert.Equal(version, dependency.GetProperty("resolved").GetString());
        Assert.Equal($"[{version}, {version}]", dependency.GetProperty("requested").GetString());
        string document = File.ReadAllText(Path.Combine(RepositoryRoot, "docs",
            "SingNextOS-v6-roadmap-reworked-2026-09-23", "30-CROSS-PROJECT-CONTRACTS.md"));
        Assert.Contains($"{packageId} {version}", document, StringComparison.Ordinal);
    }

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
        var guarded = GuardedEvidenceTypes();
        string[] forbidden = ["Capability", "RegionHandle", "OwnedRegion", "AuthorityToken", "Permission"];

        Assert.Contains(typeof(DmaGrantClosureObservationV1), guarded);
        var closureObservation = default(DmaGrantClosureObservationV1);
        Assert.False(closureObservation.AuthorizesEffect);
        Assert.False(closureObservation.AuthorizesReclaim);
        Assert.False(closureObservation.ProvesPublication);
        Assert.False(closureObservation.ProvesSettlement);

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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClaimTupleRejectsNoncanonicalDigestCase(bool gateDigest)
    {
        var tuple = new V6ClaimEvidenceTuple(1, "contract-only", V6ClaimLevel.StaticAdmission,
            "singnext", "provider", "sdk", "schema", "provider", 1,
            new string('a', 64), new string('b', 64));
        Assert.Same(tuple, tuple.Validate());
        var noncanonical = gateDigest
            ? tuple with { FeatureGateSetDigest = new string('A', 64) }
            : tuple with { QualificationArtifactDigest = new string('B', 64) };
        Assert.Throws<ArgumentException>(() => noncanonical.Validate());
        Assert.False(noncanonical.AuthorizesExecution);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClaimTupleRejectsInvalidUnicodeIdentity(bool contour)
    {
        var tuple = new V6ClaimEvidenceTuple(1, "contour-😀", V6ClaimLevel.StaticAdmission,
            "singnext", "provider", "sdk", "schema", "provider", 1,
            new string('a', 64), new string('b', 64));
        Assert.Same(tuple, tuple.Validate());
        var malformed = contour ? tuple with { Contour = "contour-" + (char)0xD800 }
            : tuple with { ProviderSourceIdentity = "provider-" + (char)0xDC00 };
        Assert.Throws<ArgumentException>(() => malformed.Validate());
        Assert.False(malformed.AuthorizesExecution);
    }
    [Fact]
    public void DmaClosureObservationHasNoRuntimeAdmissionConsumer()
    {
        var runtimeInputs = typeof(RuntimeKernel).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                               BindingFlags.Instance | BindingFlags.Static).Cast<MethodBase>()
                .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic |
                                            BindingFlags.Instance | BindingFlags.Static)))
            .SelectMany(method => method.GetParameters().Where(parameter => !parameter.IsOut)
                .Select(parameter => (Method: method, Parameter: parameter)))
            .Where(item => ContainsObservation(item.Parameter.ParameterType)).ToArray();
        Assert.Empty(runtimeInputs);
        var runtimeSources = ProductionSources().Where(path =>
            Relative(path).StartsWith("src/Runtime/", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(runtimeSources);
        foreach (var path in runtimeSources)
            Assert.DoesNotMatch(@"\bDmaGrantClosureProjectionV1\s*\.\s*Validate\s*\(", File.ReadAllText(path));

        static bool ContainsObservation(Type type) =>
            type == typeof(DmaGrantClosureObservationV1) ||
            (type.HasElementType && ContainsObservation(type.GetElementType()!)) ||
            (type.IsGenericType && type.GetGenericArguments().Any(ContainsObservation));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EvidenceMemberInspectionCannotHideCapabilityInsideContainerOrField(int shape)
    {
        var fixture = shape switch
        {
            0 => typeof(GenericEvidenceFixture),
            1 => typeof(ArrayEvidenceFixture),
            _ => typeof(FieldEvidenceFixture),
        };
        Assert.Contains(PublicMemberNames(fixture), name => name.Contains("Capability", StringComparison.Ordinal));
    }

    private sealed class GenericEvidenceFixture
    {
        public IReadOnlyList<CapabilityId> Values { get; } = [];
    }

    private sealed class ArrayEvidenceFixture
    {
        public CapabilityId[] Values { get; } = [];
    }

    private sealed class FieldEvidenceFixture
    {
        public readonly CapabilityId? Value = null;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EvidenceInspectionCannotHideCapabilityInsideUserDefinedCarrier(bool callable)
    {
        var fixture = callable ? typeof(CallableCarrierFixture) : typeof(PropertyCarrierFixture);
        Assert.Contains(PublicMemberNames(fixture), name => name.Contains("Capability", StringComparison.Ordinal));
    }

    private sealed class PropertyCarrierFixture
    {
        public HiddenCarrier? Value { get; }
    }

    private sealed class CallableCarrierFixture
    {
        public HiddenCarrier? Read() => null;
    }

    private sealed class HiddenCarrier
    {
        public HiddenCarrier? Next { get; }
        public CapabilityId Value { get; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EvidenceInspectionCannotHideCapabilityInsideImportedCarrier(bool callable)
    {
        var importedAssembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("ImportedCarrierFixture"), AssemblyBuilderAccess.RunAndCollect);
        var carrier = importedAssembly.DefineDynamicModule("carrier").DefineType("NeutralCarrier", TypeAttributes.Public);
        carrier.DefineField("Value", typeof(CapabilityId), FieldAttributes.Public);
        carrier.DefineField("Next", carrier, FieldAttributes.Public);
        var imported = carrier.CreateType()!;
        var rootAssembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("EvidenceRootFixture"), AssemblyBuilderAccess.RunAndCollect);
        var root = rootAssembly.DefineDynamicModule("root").DefineType("EvidenceRoot", TypeAttributes.Public);
        if (callable)
        {
            var method = root.DefineMethod("Read", MethodAttributes.Public, imported, Type.EmptyTypes);
            var body = method.GetILGenerator();
            body.Emit(OpCodes.Ldnull);
            body.Emit(OpCodes.Ret);
        }
        else root.DefineField("Value", imported, FieldAttributes.Public);
        var fixture = root.CreateType()!;
        Assert.NotEqual(fixture.Assembly, imported.Assembly);
        Assert.Contains(PublicMemberNames(fixture), name => name.Contains("Capability", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EvidenceSignatureInspectionCannotHideCapabilityInPublicCallableShape(int shape)
    {
        var fixture = shape switch
        {
            0 => typeof(ResultEvidenceFixture),
            1 => typeof(InputEvidenceFixture),
            2 => typeof(ConstructorEvidenceFixture),
            3 => typeof(OutputEvidenceFixture),
            _ => typeof(ConstraintEvidenceFixture),
        };
        Assert.Contains(PublicMemberNames(fixture), name => name.Contains("Capability", StringComparison.Ordinal));
    }

    private sealed class ResultEvidenceFixture
    {
        public Func<IReadOnlyList<CapabilityId>> Read() => () => [];
    }
    private sealed class InputEvidenceFixture
    {
        public void Read(Func<CapabilityId> value) { }
    }
    private sealed class ConstructorEvidenceFixture
    {
        public ConstructorEvidenceFixture(IReadOnlyList<CapabilityId> value) { }
    }
    private sealed class OutputEvidenceFixture
    {
        public void Read(out CapabilityId value) => value = default;
    }
    private interface IShape<T> { }
    [Fact]
    public void EvidenceSignatureInspectionTerminatesForRecursiveNonAuthorityConstraint()
    {
        var names = PublicMemberNames(typeof(RecursiveEvidenceFixture)).ToArray();
        Assert.Contains("IComparable`1", names);
        Assert.DoesNotContain(names, name => name.Contains("Capability", StringComparison.Ordinal));
    }
    private sealed class RecursiveEvidenceFixture
    {
        public void Read<T>() where T : IComparable<T> { }
    }
    private sealed class ConstraintEvidenceFixture
    {
        public void Read<T>() where T : IShape<CapabilityId> { }
    }

    [Fact]
    public void AdditivePreemptionTraceDmaAndLoweringEvidenceAreInGuardedInventory()
    {
        Type[] required = [typeof(PreemptionGuaranteeV1), typeof(ResumeBindingV1),
            typeof(PreemptionLifecycleEventV1), typeof(RestartAdmissionReceiptV1),
            typeof(SemanticTraceEventV1), typeof(ProviderTraceEventV1),
            typeof(SemanticTraceCounterexampleV1), typeof(SemanticTraceDifferenceV1),
            typeof(DmaExecutionBindingV1), typeof(CompilerLoweringEvidenceV1),
            typeof(CompilerLoweringEvidenceEnvelopeV1), typeof(LoweringSafePointFactV1),
            typeof(LoweringFootprintFactV1), typeof(LoweringAliasFactV1),
            typeof(LoweringOrderingFactV1), typeof(LoweringNumericModeFactV1),
            typeof(LoweringStaticResourceEstimateFactV1)];
        var guarded = GuardedEvidenceTypes();
        Assert.All(required, type => Assert.Contains(type, guarded));
    }

    private static Type[] GuardedEvidenceTypes()
    {
        return typeof(SemanticExtensionClauseV1).Assembly.GetExportedTypes()
            .Where(type => type.Namespace == typeof(SemanticExtensionClauseV1).Namespace &&
                           (type.Name.Contains("SemanticExtension", StringComparison.Ordinal) ||
                            type.Name.Contains("ClaimEvidence", StringComparison.Ordinal) ||
                            type.Name.Contains("QualificationTransition", StringComparison.Ordinal) ||
                            type.Name.Contains("FirstQualificationVertical", StringComparison.Ordinal) ||
                            type.Name.Contains("FailureSemantics", StringComparison.Ordinal) ||
                            type.Name.Contains("DurabilitySemantics", StringComparison.Ordinal) ||
                            type.Name.Contains("SemanticTrace", StringComparison.Ordinal) ||
                            type.Name.Contains("Preemption", StringComparison.Ordinal) ||
                            type.Name.Contains("CompilerLoweringEvidence", StringComparison.Ordinal) ||
                            (type.Name.StartsWith("Lowering", StringComparison.Ordinal) && type.Name.EndsWith("FactV1", StringComparison.Ordinal)) ||
                            type.Name is nameof(ProviderTraceEventV1) or nameof(ResumeBindingV1) or
                                nameof(RestartAdmissionReceiptV1) or nameof(DmaExecutionBindingV1) or
                                nameof(DmaGrantClosureObservationV1)))
            .ToArray();
    }

    private static IEnumerable<string> PublicMemberNames(Type type)
    {
        yield return type.Name;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            if (property != NonAuthorityResumeDiagnostic()) yield return property.Name;
            foreach (var name in TypeNames(property.PropertyType)) yield return name;
        }
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            yield return field.Name;
            foreach (var name in TypeNames(field.FieldType)) yield return name;
        }

        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
        foreach (var method in methods)
        {
            if (method != NonAuthorityResumeDiagnostic().GetMethod) yield return method.Name;
            foreach (var name in TypeNames(method.ReturnType)) yield return name;
            foreach (var argument in method.GetGenericArguments())
                foreach (var name in TypeNames(argument)) yield return name;
        }
        foreach (var callable in methods.Cast<MethodBase>().Concat(
                     type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)))
            foreach (var parameter in callable.GetParameters())
                foreach (var name in TypeNames(parameter.ParameterType)) yield return name;

        static IEnumerable<string> TypeNames(Type memberType) => Walk(memberType, []);

        static IEnumerable<string> Walk(Type memberType, HashSet<Type> visited)
        {
            if (!visited.Add(memberType)) yield break;
            yield return memberType.Name;
            if (memberType.HasElementType)
                foreach (var name in Walk(memberType.GetElementType()!, visited)) yield return name;
            if (memberType.IsGenericType)
                foreach (var argument in memberType.GetGenericArguments())
                    foreach (var name in Walk(argument, visited)) yield return name;
            if (memberType.IsGenericParameter)
                foreach (var constraint in memberType.GetGenericParameterConstraints())
                    foreach (var name in Walk(constraint, visited)) yield return name;
            // Enum constants are observation vocabulary, not carrier members.
            // Follow imported carrier definitions too. Core-library type shapes are
            // leaves after their element/generic/constraint types have been checked.
            if (memberType.Assembly == typeof(object).Assembly || memberType.IsGenericParameter || memberType.IsEnum) yield break;
            foreach (var property in memberType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                foreach (var name in Walk(property.PropertyType, visited)) yield return name;
            }
            foreach (var field in memberType.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                foreach (var name in Walk(field.FieldType, visited)) yield return name;
            }
            var methods = memberType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            foreach (var method in methods)
            {
                foreach (var name in Walk(method.ReturnType, visited)) yield return name;
                foreach (var argument in method.GetGenericArguments())
                    foreach (var name in Walk(argument, visited)) yield return name;
            }
            foreach (var callable in methods.Cast<MethodBase>().Concat(
                         memberType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)))
                foreach (var parameter in callable.GetParameters())
                    foreach (var name in Walk(parameter.ParameterType, visited)) yield return name;
        }
    }

    private static PropertyInfo NonAuthorityResumeDiagnostic() =>
        typeof(ResumeBindingV1).GetProperty(nameof(ResumeBindingV1.PreservesCapability))!;

    [Fact]
    public void CapabilityDiagnosticExceptionIsExactReadOnlyFalseResumeFlag()
    {
        var property = NonAuthorityResumeDiagnostic();
        Assert.Equal(typeof(bool), property.PropertyType);
        Assert.False(property.CanWrite);
        Assert.False((bool)property.GetValue(default(ResumeBindingV1))!);
        Assert.Contains(PublicMemberNames(typeof(DiagnosticEvidenceFixture)),
            name => name.Contains("Capability", StringComparison.Ordinal));
    }
    private sealed class DiagnosticEvidenceFixture
    {
        public bool PreservesCapability => true;
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
