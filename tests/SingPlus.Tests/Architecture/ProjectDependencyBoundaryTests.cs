using System.Reflection;
using System.Xml.Linq;
using SingPlus.Platform;
using YAKSys_Hybrid_CPU.ExecutableAdapter;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Tests.Architecture;

public sealed class ProjectDependencyBoundaryTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void ActiveProjectsDoNotReferenceCompatibilityAggregation()
    {
        var aggregationReferences = Directory.EnumerateFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(RepositoryRoot, "*.yml", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(RepositoryRoot, "*.yaml", SearchOption.AllDirectories))
            .Select(path => (path, text: File.ReadAllText(path)))
            .Where(item => !item.path.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase)
                        && !item.path.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase)
                        && !item.path.Contains("\\TempEnv\\", StringComparison.OrdinalIgnoreCase))
            .Where(item => item.text.Contains("HybridCPU_NeutralRuntime/HybridCPU_NeutralRuntime.csproj", StringComparison.OrdinalIgnoreCase)
                        || item.text.Contains("HybridCPU_NeutralRuntime\\HybridCPU_NeutralRuntime.csproj", StringComparison.OrdinalIgnoreCase))
            .Select(item => Path.GetRelativePath(RepositoryRoot, item.path))
            .ToArray();

        Assert.Empty(aggregationReferences);
    }

    [Fact]
    public void ExecutableAdapterAssemblyCannotReferenceModelOrCompatibilityAssembly()
    {
        var references = typeof(HybridCpuExecutableAdapterProject).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, x => x.Name is "HybridCPU_NeutralRuntime.Model" or "HybridCPU_NeutralRuntime");
    }

    [Fact]
    public void AdapterPublicSurfaceContainsNoProviderAuthorityVocabulary()
    {
        var forbidden = new[] { "PhysicalAddress", "PageTableRoot", "ProviderLease", "Iommu", "Vmcs", "Vmx", "HybridCpuLane", "Opcode" };
        var names = typeof(HybridCpuExecutableAdapterProject).Assembly.GetExportedTypes()
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Select(member => member.Name);

        foreach (var fragment in forbidden)
            Assert.DoesNotContain(names, name => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExternalRuntimeContractsPackageIsReferencedOnlyByExecutableAdapter()
    {
        var consumers = Directory.EnumerateFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) &&
                           !path.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase))
            .Where(path => XDocument.Load(path).Descendants("PackageReference")
                .Any(element => string.Equals((string?)element.Attribute("Include"),
                    "HybridCPU.ExternalRuntime.Contracts", StringComparison.OrdinalIgnoreCase)))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'))
            .ToArray();

        Assert.Equal(["tools/HybridCpu_ExecutableAdapter/HybridCpu_ExecutableAdapter.csproj"], consumers);
    }

    [Fact]
    public void ExecutableAdapterPublicSurfaceContainsNoLegacyFacadeOrScaffoldType()
    {
        string[] forbidden = ["Scaffold", "Facade110", "RuntimeV1", "RuntimeV2"];
        var types = typeof(HybridCpuExecutableAdapterProject).Assembly.GetExportedTypes();

        foreach (var fragment in forbidden)
            Assert.DoesNotContain(types, type => type.FullName?.Contains(fragment, StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void ActiveProjectMetadataDoesNotReferenceTheFutureArchiveRoot()
    {
        string[] forbidden = ["HybridCPU ISE\\Legacy", "HybridCPU ISE/Legacy"];
        var violations = Directory.EnumerateFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(RepositoryRoot, "*.props", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(RepositoryRoot, "*.targets", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(RepositoryRoot, "*.slnx", SearchOption.AllDirectories))
            .Where(path => !path.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase) &&
                           !path.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) &&
                           !path.Contains("\\TempEnv\\", StringComparison.OrdinalIgnoreCase))
            .Where(path => forbidden.Any(fragment => File.ReadAllText(path).Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void ConcreteExternalRuntimePackageIsReferencedOnlyByExecutableAdapter()
    {
        var consumers = Directory.EnumerateFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) &&
                           !path.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase))
            .Where(path => XDocument.Load(path).Descendants("PackageReference")
                .Any(element => string.Equals((string?)element.Attribute("Include"),
                    "HybridCPU.ExternalRuntime", StringComparison.OrdinalIgnoreCase)))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'))
            .ToArray();

        Assert.Equal(["tools/HybridCpu_ExecutableAdapter/HybridCpu_ExecutableAdapter.csproj"], consumers);
    }

    [Fact]
    public void SingNextOsHasNoDirectCompilerOrIseExecutionDependency()
    {
        string[] forbidden = ["HybridCPU_Compiler", "HybridCPU.Compiler.Core", "HybridCPU_ISE"];
        var violations = Directory.EnumerateFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) &&
                           !path.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => XDocument.Load(path).Descendants()
                .Where(element => element.Name.LocalName is "ProjectReference" or "PackageReference")
                .Select(element => ((string?)element.Attribute("Include")) ?? string.Empty)
                .Where(include => forbidden.Any(fragment => include.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
                .Select(include => $"{Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/')}: {include}"))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void SingExecutionBoundaryDoesNotImportHcexeOrIseRunnerTypes()
    {
        string[] roots =
        [
            Path.Combine(RepositoryRoot, "contracts"),
            Path.Combine(RepositoryRoot, "src", "Runtime"),
            Path.Combine(RepositoryRoot, "src", "Platform"),
            Path.Combine(RepositoryRoot, "tools", "HybridCpu_ExecutableAdapter"),
        ];
        string[] forbidden =
        [
            "HybridCpuRestrictedImageBuilderV1",
            "HybridCpuRestrictedImageV1",
            "HybridCpuIseManagedGuestExecutionRequestV1",
            "HybridCpuIseManagedGuestExecutionRunnerV1",
        ];

        var violations = roots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) &&
                           !path.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => forbidden
                .Where(fragment => File.ReadAllText(path).Contains(fragment, StringComparison.Ordinal))
                .Select(fragment => $"{Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/')}: {fragment}"))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Phase501NeutralAndPlatformSurfaceHasNoBackendLeakage()
    {
        var roots = new[]
        {
            typeof(INeutralChildDomainProvider), typeof(INeutralGuestMemoryProvider),
            typeof(INeutralVirtualEventProvider), typeof(INeutralVirtualTrapProvider), typeof(INeutralVirtualIoProvider),
            typeof(IPlatformChildDomainProvider), typeof(IPlatformNestedDomainProvider), typeof(IPlatformGuestMemoryProvider),
            typeof(IPlatformVirtualEventProvider), typeof(IPlatformVirtualTrapProvider), typeof(IPlatformVirtualIoProvider),
        };
        string[] forbidden = ["VmExitReason", "Vmcs", "Vmx", "ExitQualification", "SecondStage", "PageTableRoot", "PhysicalAddress", "Iommu", "HybridCpu", "DomainTag", "AddressSpaceTag"];

        foreach (var root in roots)
        {
            var types = root.GetMethods()
                .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType))
                .Append(root);
            foreach (var type in types)
            foreach (var fragment in forbidden)
                Assert.DoesNotContain(fragment, type.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Phase501ContractSourcesContainNoBackendSpecificVocabulary()
    {
        var paths = new[]
        {
            Path.Combine(RepositoryRoot, "src", "Platform", "SingPlus.Platform.Abstractions", "PlatformChildDomainContracts.cs"),
            Path.Combine(RepositoryRoot, "src", "Platform", "SingPlus.Platform.Abstractions", "PlatformNestedDomainContracts.cs"),
            Path.Combine(RepositoryRoot, "src", "Platform", "SingPlus.Platform.Abstractions", "PlatformVirtualTrapContracts.cs"),
            Path.Combine(RepositoryRoot, "tools", "Runtime", "HybridCPU_NeutralRuntime", "HybridCPU_NeutralRuntime.Contracts", "Contracts", "NeutralChildDomainContracts.cs"),
        };
        string[] forbidden = ["VmExitReason", "Vmcs", "Vmx", "ExitQualification", "SecondStage", "Ept", "PhysicalAddress", "Iommu", "DomainTag", "AddressSpaceTag"];
        foreach (var path in paths)
        {
            var source = File.ReadAllText(path);
            foreach (var fragment in forbidden)
                Assert.DoesNotContain(fragment, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("SingNextOS repository root was not found.");
    }
}
