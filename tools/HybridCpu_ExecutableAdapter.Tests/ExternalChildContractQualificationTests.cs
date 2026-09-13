using HybridCPU.ExternalRuntime.Contracts;
using YAKSys_Hybrid_CPU.ExecutableAdapter.Backend;
using Xunit;

namespace HybridCpu_ExecutableAdapter.Tests;

public sealed class ExternalChildContractQualificationTests
{
    [Fact]
    public void RealContractPackage_IsPinnedAndAdmissionDoesNotPromoteExecutable()
    {
        HybridCpuExternalFeatureManifest manifest = Manifest(HybridCpuExternalFeatureAvailability.RuntimeAdmission);

        Assert.True(ExternalChildContractQualification.SupportsAdmission(manifest));
        Assert.False(ExternalChildContractQualification.CanAdvertiseExecutable(manifest));
        var neutral = ExternalChildContractQualification.QualifyNeutralAdmissionManifest(manifest);
        Assert.All(neutral.Features, feature =>
            Assert.Equal(YAKSys_Hybrid_CPU.Core.NeutralRuntimeFeatureAvailability.RuntimeAdmission, feature.Availability));
        Assert.Equal(YAKSys_Hybrid_CPU.Core.NeutralRuntimeFeatureAvailability.Unavailable,
            neutral.Resolve(YAKSys_Hybrid_CPU.Core.NeutralRuntimeFeatureFamily.BoundedVirtualIo).Availability);
        Assert.Equal("1.3.0", HybridCpuExternalRuntimePrerequisite.ContractsPackageVersion);
        Assert.Equal(
            "7956596E820F2536542A73171205ED0B7A3366996BBDB2B51D95C2AE1FE3FC92",
            HybridCpuExternalRuntimePrerequisite.ContractsPackageSha256);
        Assert.Equal("191A1976DECAF607425B3F93378BA11446B32AF2EBFD09DFF45A26944E7E765F",
            HybridCpuExternalRuntimePrerequisite.RuntimePackageSha256);
        Assert.Equal("fd37b00a207a162baaa860f3f8b96c0c66d7e691",
            HybridCpuExternalRuntimePrerequisite.QualifiedFacade130Commit);
    }

    [Fact]
    public void RepositoryLocalPackages_MatchQualifiedHashes()
    {
        string root = FindRepositoryRoot();
        Assert.Equal(HybridCpuExternalRuntimePrerequisite.ContractsPackageSha256,
            Hash(Path.Combine(root, ".packages", "HybridCPU.ExternalRuntime.Contracts.1.3.0.nupkg")));
        Assert.Equal(HybridCpuExternalRuntimePrerequisite.RuntimePackageSha256,
            Hash(Path.Combine(root, ".packages", "HybridCPU.ExternalRuntime.1.3.0.nupkg")));
    }

    [Fact]
    public void MissingFamilyAndWrongMajorFailClosed()
    {
        HybridCpuExternalFeatureDescriptor[] incomplete =
        [new(HybridCpuExternalFeatureFamily.ChildDomainLifecycle,
            HybridCpuExternalFeatureAvailability.RuntimeAdmission, 1)];
        Assert.False(ExternalChildContractQualification.SupportsAdmission(
            new(HybridCpuExternalContractVersion.V1_3, 1, incomplete)));
        Assert.False(ExternalChildContractQualification.SupportsAdmission(
            new(new(2, 0, 0), 1, Families(HybridCpuExternalFeatureAvailability.Executable))));
        Assert.False(ExternalChildContractQualification.CanAdvertiseExecutable(
            Manifest(HybridCpuExternalFeatureAvailability.Executable)));
    }

    [Fact]
    public void V3RequiresBothExecutableArtifactAndBoundedIoClaims()
    {
        var complete = new HybridCpuExternalFeatureManifest(HybridCpuExternalContractVersion.V1_3, 1,
            [.. Families(HybridCpuExternalFeatureAvailability.RuntimeAdmission),
             new(HybridCpuExternalFeatureFamily.ChildExecutableImage, HybridCpuExternalFeatureAvailability.Executable, 1),
             new(HybridCpuExternalFeatureFamily.ChildVirtualIo, HybridCpuExternalFeatureAvailability.Executable, 1)]);
        Assert.True(ExternalChildContractQualification.CanAdvertiseExecutable(complete));
        Assert.False(ExternalChildContractQualification.CanAdvertiseExecutable(new(
            HybridCpuExternalContractVersion.V1_3, 1,
            [.. Families(HybridCpuExternalFeatureAvailability.RuntimeAdmission),
             new(HybridCpuExternalFeatureFamily.ChildExecutableImage, HybridCpuExternalFeatureAvailability.Executable, 1)])));
    }

    [Fact]
    public void CloseReceiptMustMatchLeaseOperationVersionGenerationAndTerminalState()
    {
        HybridCpuExternalFeatureManifest manifest = Manifest(HybridCpuExternalFeatureAvailability.RuntimeAdmission);
        var parent = new ExternalDomainLease(new(Guid.NewGuid()), new(4));
        var child = new ExternalChildDomainLease(new(Guid.NewGuid()), new(8), parent);
        var operation = new ExternalOperationIdentity(new(Guid.NewGuid()), new(3));
        var receipt = new ExternalChildDomainCloseReceipt(child, operation, manifest.ContractVersion,
            manifest.Generation, ExternalChildDomainState.Closed, true, 2);

        Assert.True(ExternalChildContractQualification.IsExactTerminalClose(receipt, child, operation, manifest));
        Assert.False(ExternalChildContractQualification.IsExactTerminalClose(
            receipt with { IsTerminal = false }, child, operation, manifest));
        Assert.False(ExternalChildContractQualification.IsExactTerminalClose(
            receipt with { ManifestGeneration = manifest.Generation + 1 }, child, operation, manifest));
        Assert.False(ExternalChildContractQualification.IsExactTerminalClose(
            receipt with { Lease = child with { Epoch = new(child.Epoch.Value + 1) } }, child, operation, manifest));
    }

    private static HybridCpuExternalFeatureManifest Manifest(HybridCpuExternalFeatureAvailability availability) =>
        new(HybridCpuExternalContractVersion.V1_3, 7, Families(availability));

    private static HybridCpuExternalFeatureDescriptor[] Families(HybridCpuExternalFeatureAvailability availability) =>
    [
        new(HybridCpuExternalFeatureFamily.ChildDomainLifecycle, availability, 1),
        new(HybridCpuExternalFeatureFamily.ChildGuestMemory, availability, 1),
        new(HybridCpuExternalFeatureFamily.ChildEventDelivery, availability, 1),
        new(HybridCpuExternalFeatureFamily.ChildTrapDelivery, availability, 1),
    ];

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".packages")) &&
                File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the SingNextOS repository root.");
    }

    private static string Hash(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
}
