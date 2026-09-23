using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SingPlus.Contracts;
using SingPlus.Sip.Virtualization;

namespace SingPlus.Tests.Architecture;

public sealed class RepositoryArchitecturePolicyTests
{
    public enum Layer
    {
        Contracts,
        Sip,
        PrivilegedMechanism,
        PlatformAbstractions,
        ProviderAdapter,
        ExecutableAdapter,
        DriversServices,
        NativeSdk,
        Compatibility,
        BootContracts,
        BootCore,
        BootPlatformAdapter,
        BootCapsule,
        NeutralContracts,
        NeutralAuthority,
        NeutralModel,
        Tooling,
    }

    public enum PackageClass
    {
        TestTooling,
        CompilerTooling,
        ExternalSemanticContract,
        ExternalRuntimeImplementation,
    }

    private static readonly string Root = FindRepositoryRoot();

    [Fact]
    public void EveryProjectIsClassifiedAndEveryProjectReferenceIsAllowed()
    {
        foreach (var project in EnumerateProjects())
        {
            var sourceLayer = Classify(project);
            var document = XDocument.Load(project);
            foreach (var include in document.Descendants("ProjectReference")
                         .Select(static element => (string?)element.Attribute("Include"))
                         .Where(static include => !string.IsNullOrWhiteSpace(include)))
            {
                var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, include!));
                var targetLayer = Classify(target);
                Assert.True(
                    IsProjectReferenceAllowed(sourceLayer, targetLayer),
                    $"Forbidden architecture edge: {Path.GetRelativePath(Root, project)} [{sourceLayer}] -> {Path.GetRelativePath(Root, target)} [{targetLayer}]");
            }
        }
    }

    [Fact]
    public void EveryDirectPackageReferenceIsClassifiedAndAllowed()
    {
        foreach (var project in EnumerateProjects())
        {
            var sourceLayer = Classify(project);
            var document = XDocument.Load(project);
            foreach (var package in document.Descendants("PackageReference")
                         .Where(static element => element.Attribute("Include") is not null))
            {
                var packageId = ((string?)package.Attribute("Include"))?.Trim();
                Assert.False(
                    string.IsNullOrWhiteSpace(packageId),
                    $"PackageReference without a package id: {Path.GetRelativePath(Root, project)}");

                var packageClass = ClassifyPackage(packageId!);
                Assert.True(
                    IsPackageReferenceAllowed(sourceLayer, packageClass),
                    $"Forbidden package edge: {Path.GetRelativePath(Root, project)} [{sourceLayer}] -> {packageId} [{packageClass}]");

                if (packageClass is PackageClass.ExternalSemanticContract or PackageClass.ExternalRuntimeImplementation)
                {
                    var version = ReadPackageVersion(package);
                    Assert.True(
                        IsExactPackageVersion(version),
                        $"External semantic contract packages must use a literal exact SemVer pin: {Path.GetRelativePath(Root, project)} -> {packageId} Version='{version ?? "<missing>"}'");
                }
            }
        }
    }

    [Theory]
    [InlineData(Layer.Contracts, Layer.ProviderAdapter)]
    [InlineData(Layer.Sip, Layer.PrivilegedMechanism)]
    [InlineData(Layer.DriversServices, Layer.ProviderAdapter)]
    [InlineData(Layer.NativeSdk, Layer.PlatformAbstractions)]
    [InlineData(Layer.Compatibility, Layer.ProviderAdapter)]
    [InlineData(Layer.ExecutableAdapter, Layer.NeutralModel)]
    [InlineData(Layer.ExecutableAdapter, Layer.Compatibility)]
    [InlineData(Layer.ExecutableAdapter, Layer.ProviderAdapter)]
    [InlineData(Layer.BootCore, Layer.PrivilegedMechanism)]
    [InlineData(Layer.BootCore, Layer.ExecutableAdapter)]
    [InlineData(Layer.BootPlatformAdapter, Layer.BootCapsule)]
    [InlineData(Layer.BootCapsule, Layer.PrivilegedMechanism)]
    [InlineData(Layer.BootCapsule, Layer.ProviderAdapter)]
    [InlineData(Layer.BootCapsule, Layer.ExecutableAdapter)]
    [InlineData(Layer.BootCapsule, Layer.NeutralModel)]
    public void ProjectPolicyFixtureRejectsForbiddenEdges(Layer source, Layer target) =>
        Assert.False(IsProjectReferenceAllowed(source, target));

    [Theory]
    [InlineData(Layer.Contracts, PackageClass.TestTooling)]
    [InlineData(Layer.PrivilegedMechanism, PackageClass.ExternalRuntimeImplementation)]
    [InlineData(Layer.ProviderAdapter, PackageClass.ExternalSemanticContract)]
    [InlineData(Layer.ExecutableAdapter, PackageClass.CompilerTooling)]
    public void PackagePolicyFixtureRejectsForbiddenEdges(Layer source, PackageClass packageClass) =>
        Assert.False(IsPackageReferenceAllowed(source, packageClass));

    [Fact]
    public void ExecutableAdapterMayConsumeOnlyExactlyPinnedExternalFacadePackages()
    {
        Assert.True(IsPackageReferenceAllowed(Layer.ExecutableAdapter, PackageClass.ExternalSemanticContract));
        Assert.True(IsPackageReferenceAllowed(Layer.PrivilegedMechanism, PackageClass.ExternalSemanticContract));
        Assert.False(IsPackageReferenceAllowed(Layer.PrivilegedMechanism, PackageClass.ExternalRuntimeImplementation));
        Assert.True(IsExactPackageVersion("[1.0.0]"));
        Assert.True(IsExactPackageVersion("[2.1.3-rc.1]"));
        Assert.False(IsExactPackageVersion("1.0.0"));
        Assert.False(IsExactPackageVersion("1.*"));
        Assert.False(IsExactPackageVersion("[1.0.0,2.0.0)"));
        Assert.False(IsExactPackageVersion("$(HybridCpuExternalRuntimeContractsVersion)"));
        Assert.Equal(PackageClass.ExternalRuntimeImplementation, ClassifyPackage("HybridCPU.ExternalRuntime"));
        Assert.Throws<InvalidOperationException>(() => ClassifyPackage("HybridCPU.RuntimeKernel"));
    }

    [Fact]
    public void PublicContractAndSipSignaturesCannotReferencePlatformOrProviderAssemblies()
    {
        foreach (var assembly in new[] { typeof(ServiceManifestV1).Assembly, typeof(IVirtualizationService).Assembly })
        foreach (var type in assembly.GetExportedTypes())
        foreach (var exposed in PublicSignatureTypes(type))
            Assert.False(IsForbiddenPublicAssembly(exposed.Assembly.GetName().Name), $"{type.FullName} exposes {exposed.FullName}.");
    }

    [Fact]
    public void DirectBootRootsHaveNoForbiddenTransitiveAssemblyClosure()
    {
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SingPlus.Runtime",
            "SingPlus.Kernel.Hal.Host",
            "HybridCpu_ExecutableAdapter",
            "HybridCPU_NeutralRuntime.Model",
        };
        foreach (var root in new[]
                 {
                     typeof(SingNext.Boot.Core.BootCoreAssembly).Assembly,
                     typeof(SingNext.Boot.Capsule.BootCapsuleAssembly).Assembly,
                 })
        {
            var pending = new Queue<AssemblyName>(root.GetReferencedAssemblies());
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (pending.TryDequeue(out var reference))
            {
                if (reference.Name is null || !visited.Add(reference.Name)) continue;
                Assert.DoesNotContain(reference.Name, forbidden);
                var localPath = Path.Combine(Path.GetDirectoryName(root.Location)!, reference.Name + ".dll");
                if (!File.Exists(localPath)) continue;
                foreach (var child in Assembly.LoadFrom(localPath).GetReferencedAssemblies()) pending.Enqueue(child);
            }
        }
    }

    [Fact]
    public void ManagedGcHostDebugBootCannotBeClassifiedAsCapsule()
    {
        var project = Path.Combine(Root, "src", "Kernel", "Boot", "SingPlus.Boot", "SingPlus.Boot.csproj");
        Assert.Equal(Layer.PrivilegedMechanism, Classify(project));
        var references = XDocument.Load(project).Descendants("ProjectReference")
            .Select(static value => ((string?)value.Attribute("Include"))?.Replace('\\', '/'))
            .ToArray();
        Assert.Contains(references, static value => value?.Contains("SingPlus.Runtime", StringComparison.Ordinal) == true);
        Assert.Contains(references, static value => value?.Contains("SingPlus.Kernel.Hal.Host", StringComparison.Ordinal) == true);
    }

    private static IEnumerable<string> EnumerateProjects() =>
        Directory.EnumerateFiles(Root, "*.csproj", SearchOption.AllDirectories)
            .Where(static path => !IsBuildOutputPath(path));

    private static bool IsBuildOutputPath(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.AltDirectorySeparatorChar}bin{Path.AltDirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.AltDirectorySeparatorChar}obj{Path.AltDirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static bool IsProjectReferenceAllowed(Layer source, Layer target) => source switch
    {
        Layer.Contracts or Layer.BootContracts or Layer.NeutralContracts => false,
        Layer.BootCore => target is Layer.BootContracts,
        Layer.BootPlatformAdapter => target is Layer.BootCore or Layer.BootContracts,
        Layer.BootCapsule => target is Layer.BootCore or Layer.BootPlatformAdapter or Layer.BootContracts,
        Layer.Sip => target is Layer.Contracts or Layer.NativeSdk or Layer.Tooling,
        Layer.PlatformAbstractions => target is Layer.Contracts,
        Layer.DriversServices => target is Layer.Contracts,
        Layer.NativeSdk => target is Layer.Contracts or Layer.Sip,
        Layer.PrivilegedMechanism => target is Layer.Contracts or Layer.BootContracts or Layer.Sip or Layer.PlatformAbstractions or Layer.PrivilegedMechanism,
        Layer.ProviderAdapter => target is Layer.PlatformAbstractions or Layer.NeutralContracts or Layer.NeutralAuthority or Layer.NeutralModel,
        Layer.ExecutableAdapter => target is Layer.BootContracts or Layer.NeutralContracts or Layer.NeutralAuthority,
        Layer.Compatibility => target is Layer.NeutralContracts or Layer.NeutralModel,
        Layer.NeutralAuthority => target is Layer.NeutralContracts,
        Layer.NeutralModel => target is Layer.NeutralContracts or Layer.NeutralAuthority,
        Layer.Tooling => true,
        _ => false,
    };

    private static bool IsPackageReferenceAllowed(Layer source, PackageClass packageClass) => source switch
    {
        Layer.PrivilegedMechanism => packageClass == PackageClass.ExternalSemanticContract,
        Layer.BootCore or Layer.BootPlatformAdapter or Layer.BootCapsule => false,
        Layer.ExecutableAdapter => packageClass is PackageClass.ExternalSemanticContract or PackageClass.ExternalRuntimeImplementation,
        Layer.Tooling => packageClass is PackageClass.TestTooling or PackageClass.CompilerTooling,
        _ => false,
    };

    private static PackageClass ClassifyPackage(string packageId) => packageId.ToLowerInvariant() switch
    {
        "coverlet.collector" or
        "microsoft.net.test.sdk" or
        "xunit" or
        "xunit.runner.visualstudio" => PackageClass.TestTooling,
        "microsoft.codeanalysis.csharp" => PackageClass.CompilerTooling,
        "hybridcpu.externalruntime.contracts" => PackageClass.ExternalSemanticContract,
        "hybridcpu.externalruntime" => PackageClass.ExternalRuntimeImplementation,
        _ => throw new InvalidOperationException($"Package is not architecture-classified: {packageId}"),
    };

    private static string? ReadPackageVersion(XElement package)
    {
        var attribute = ((string?)package.Attribute("Version"))?.Trim();
        if (!string.IsNullOrWhiteSpace(attribute)) return attribute;
        return package.Elements("Version").Select(static element => element.Value.Trim()).FirstOrDefault(static value => value.Length > 0);
    }

    private static bool IsExactPackageVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        if (version.Length <= 2 || version[0] != '[' || version[^1] != ']') return false;
        var literal = version[1..^1];
        return Regex.IsMatch(
            literal,
            "^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\\+[0-9A-Za-z.-]+)?$",
            RegexOptions.CultureInvariant);
    }

    private static Layer Classify(string path)
    {
        var relative = Path.GetRelativePath(Root, path).Replace('\\', '/');
        if (relative.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".Tests.csproj", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("tools/SingPlus.", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("sdk/SingPlus.Analyzers", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("sdk/SingPlus.Generators", StringComparison.OrdinalIgnoreCase)) return Layer.Tooling;
        if (relative == "src/Kernel/Boot/SingNext.Boot.Core/SingNext.Boot.Core.csproj") return Layer.BootCore;
        if (relative == "src/Platform/SingPlus.Platform.HybridCpu.Boot/SingPlus.Platform.HybridCpu.Boot.csproj") return Layer.BootPlatformAdapter;
        if (relative == "src/Kernel/Boot/SingNext.Boot.Capsule/SingNext.Boot.Capsule.csproj") return Layer.BootCapsule;
        if (relative == "contracts/SingPlus.Contracts/SingPlus.Contracts.csproj") return Layer.Contracts;
        if (relative == "src/Sip/SingPlus.Sip/SingPlus.Sip.csproj") return Layer.Sip;
        if (relative == "src/Platform/SingPlus.Platform.Abstractions/SingPlus.Platform.Abstractions.csproj") return Layer.PlatformAbstractions;
        if (relative.StartsWith("src/Platform/SingPlus.Platform.", StringComparison.OrdinalIgnoreCase)) return Layer.ProviderAdapter;
        if (relative.StartsWith("src/Drivers/", StringComparison.OrdinalIgnoreCase)) return Layer.DriversServices;
        if (relative.StartsWith("sdk/", StringComparison.OrdinalIgnoreCase)) return Layer.NativeSdk;
        if (relative.StartsWith("src/Runtime/", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("src/Kernel/", StringComparison.OrdinalIgnoreCase)) return Layer.PrivilegedMechanism;
        if (relative.Contains("HybridCPU_NeutralRuntime.Contracts/", StringComparison.OrdinalIgnoreCase)) return Layer.NeutralContracts;
        if (relative.Contains("HybridCpu_ExecutableAdapter/Boot.Contracts/", StringComparison.OrdinalIgnoreCase)) return Layer.BootContracts;
        if (relative.Contains("HybridCPU_NeutralRuntime.AuthorityCore/", StringComparison.OrdinalIgnoreCase)) return Layer.NeutralAuthority;
        if (relative.Contains("HybridCPU_NeutralRuntime.Model/", StringComparison.OrdinalIgnoreCase)) return Layer.NeutralModel;
        if (relative.EndsWith("HybridCPU_NeutralRuntime.csproj", StringComparison.OrdinalIgnoreCase)) return Layer.Compatibility;
        if (relative.StartsWith("tools/HybridCpu_ExecutableAdapter/", StringComparison.OrdinalIgnoreCase)) return Layer.ExecutableAdapter;
        throw new InvalidOperationException($"Project is not architecture-classified: {relative}");
    }

    private static IEnumerable<Type> PublicSignatureTypes(Type type)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)) yield return Unwrap(property.PropertyType);
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            yield return Unwrap(method.ReturnType);
            foreach (var parameter in method.GetParameters()) yield return Unwrap(parameter.ParameterType);
        }
    }

    private static Type Unwrap(Type type)
    {
        while (type.HasElementType) type = type.GetElementType()!;
        if (type.IsGenericType)
            foreach (var argument in type.GetGenericArguments())
                if (IsForbiddenPublicAssembly(argument.Assembly.GetName().Name)) return argument;
        return type;
    }

    private static bool IsForbiddenPublicAssembly(string? name) =>
        name?.StartsWith("SingPlus.Platform", StringComparison.Ordinal) == true ||
        name?.StartsWith("HybridCPU", StringComparison.OrdinalIgnoreCase) == true;

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
}
