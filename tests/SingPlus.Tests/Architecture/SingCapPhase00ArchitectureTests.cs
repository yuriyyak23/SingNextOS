using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SingPlus.Tests.Architecture;

public sealed partial class SingCapPhase00ArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void CapabilityIdRecordStoreExistsOnlyInCapabilityAuthority()
    {
        var owners = ProductionSources()
            .Where(path => ContainsCapabilityRecordStore(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs"], owners);
    }

    [Theory]
    [InlineData("private readonly Dictionary<CapabilityId, Record> _records = [];")]
    [InlineData("ConcurrentDictionary < CapabilityId , Record > records = new();")]
    public void DuplicateCapabilityStoreDetectorRejectsAdversarialFixtures(string source) =>
        Assert.True(ContainsCapabilityRecordStore(source));

    [Fact]
    public void SingNextProjectsDoNotReferenceHybridCpuImplementationTrees()
    {
        var violations = Directory.EnumerateFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(static path => !IsBuildOutput(path))
            .SelectMany(project => XDocument.Load(project).Descendants("ProjectReference")
                .Select(reference => (Project: project, Include: ((string?)reference.Attribute("Include")) ?? string.Empty)))
            .Where(item => IsForbiddenHybridCpuImplementationReference(item.Include))
            .Select(item => $"{Path.GetRelativePath(RepositoryRoot, item.Project).Replace('\\', '/')}: {item.Include}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("../../../HybridCPU v2/HybridCPU_ISE/HybridCPU_ISE.csproj")]
    [InlineData("..\\HybridCPU_Compiler\\HybridCPU.Compiler.Core.csproj")]
    [InlineData("C:\\src\\HybridCPU_ExternalRuntime\\HybridCPU_ExternalRuntime.csproj")]
    public void HybridCpuImplementationDependencyDetectorRejectsAdversarialFixtures(string include) =>
        Assert.True(IsForbiddenHybridCpuImplementationReference(include));

    [Theory]
    [InlineData("../Runtime/HybridCPU_NeutralRuntime/HybridCPU_NeutralRuntime.Contracts/HybridCPU_NeutralRuntime.Contracts.csproj")]
    [InlineData("HybridCPU_ExternalRuntime.Contracts.csproj")]
    public void HybridCpuProviderNeutralContractReferencesRemainAllowed(string include) =>
        Assert.False(IsForbiddenHybridCpuImplementationReference(include));

    private static bool ContainsCapabilityRecordStore(string source) =>
        CapabilityRecordStorePattern().IsMatch(source);

    private static bool IsForbiddenHybridCpuImplementationReference(string include)
    {
        var normalized = include.Replace('\\', '/');
        return normalized.Contains("HybridCPU_ISE", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("HybridCPU v2", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("HybridCPU_Compiler", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("HybridCPU.Compiler", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("HybridCPU_ExternalRuntime/HybridCPU_ExternalRuntime.csproj", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ProductionSources()
    {
        string[] roots = ["contracts", "src", "sdk", "tools"];
        return roots.Select(root => Path.Combine(RepositoryRoot, root))
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(static path => !IsBuildOutput(path));
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("SingNextOS repository root was not found.");
    }

    [GeneratedRegex(@"(?:Concurrent)?Dictionary\s*<\s*(?:[\w.]+\.)?CapabilityId\s*,", RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityRecordStorePattern();
}
