using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class ProcessManifestCollectionFreezeTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PublicViewsRejectTypedAndUntypedMutationWithoutCanonicalOrDigestDrift(bool contracts)
    {
        var manifest = TestFixtures.Manifest(1, 10, capabilities: [new(ResourceKind.Device, "required", CapabilityRights.Read)],
            contracts: ["Z", "A"]);
        var bytes = manifest.SerializeCanonical();
        var digest = manifest.ComputeDigest();
        var contractDigest = manifest.ContractDigest;
        if (contracts)
        {
            Assert.False(manifest.RequiredContracts is string[]);
            var list = Assert.IsAssignableFrom<IList<string>>(manifest.RequiredContracts);
            Assert.True(list.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => list[0] = "changed");
            Assert.Throws<NotSupportedException>(() => list.Add("extra"));
            Assert.Throws<NotSupportedException>(() => list.RemoveAt(0));
            Assert.Throws<NotSupportedException>(() => list.Clear());
            var untyped = Assert.IsAssignableFrom<global::System.Collections.IList>(list);
            Assert.Throws<NotSupportedException>(() => untyped[0] = "changed");
            Assert.Throws<NotSupportedException>(() => untyped.Add("extra"));
        }
        else
        {
            Assert.False(manifest.RequiredCapabilities is CapabilityRequirementV1[]);
            var list = Assert.IsAssignableFrom<IList<CapabilityRequirementV1>>(manifest.RequiredCapabilities);
            Assert.True(list.IsReadOnly);
            var changed = new CapabilityRequirementV1(ResourceKind.Device, "changed", CapabilityRights.Configure);
            Assert.Throws<NotSupportedException>(() => list[0] = changed);
            Assert.Throws<NotSupportedException>(() => list.Add(changed));
            Assert.Throws<NotSupportedException>(() => list.RemoveAt(0));
            Assert.Throws<NotSupportedException>(() => list.Clear());
            var untyped = Assert.IsAssignableFrom<global::System.Collections.IList>(list);
            Assert.Throws<NotSupportedException>(() => untyped[0] = changed);
            Assert.Throws<NotSupportedException>(() => untyped.Add(changed));
        }
        Assert.Equal(bytes, manifest.SerializeCanonical());
        Assert.Equal(digest, manifest.ComputeDigest());
        Assert.Equal(contractDigest, manifest.ContractDigest);
        Assert.Same(manifest.RequiredCapabilities, manifest.RequiredCapabilities);
        Assert.Same(manifest.RequiredContracts, manifest.RequiredContracts);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EmptyViewsCannotBecomeNewMutableInputs(bool contracts)
    {
        var manifest = TestFixtures.Manifest(1, 10);
        var bytes = manifest.SerializeCanonical();
        if (contracts)
        {
            var list = Assert.IsAssignableFrom<IList<string>>(manifest.RequiredContracts);
            Assert.Throws<NotSupportedException>(() => list.Add("new"));
            Assert.Throws<NotSupportedException>(() => list.Clear());
        }
        else
        {
            var list = Assert.IsAssignableFrom<IList<CapabilityRequirementV1>>(manifest.RequiredCapabilities);
            Assert.Throws<NotSupportedException>(() => list.Add(new(ResourceKind.Device, "new", CapabilityRights.Read)));
            Assert.Throws<NotSupportedException>(() => list.Clear());
        }
        Assert.Empty(manifest.RequiredContracts);
        Assert.Empty(manifest.RequiredCapabilities);
        Assert.Equal(bytes, manifest.SerializeCanonical());
    }

    [Fact]
    public void CallerArrayAndCopiedViewMutationCannotChangePrivateCanonicalOwner()
    {
        CapabilityRequirementV1[] capabilities = [new(ResourceKind.Device, "b", CapabilityRights.Read), new(ResourceKind.Device, "a", CapabilityRights.Write)];
        string[] contracts = ["Z", "A"];
        var manifest = TestFixtures.Manifest(1, 10, capabilities: capabilities, contracts: contracts);
        var reference = TestFixtures.Manifest(1, 10, capabilities: capabilities.Reverse(), contracts: contracts.Reverse());
        var bytes = manifest.SerializeCanonical();
        var contractDigest = manifest.ContractDigest;
        capabilities[0] = new(ResourceKind.Device, "changed", CapabilityRights.Configure);
        contracts[0] = "changed";
        var capCopy = manifest.RequiredCapabilities.ToArray();
        var contractCopy = manifest.RequiredContracts.ToArray();
        capCopy[0] = new(ResourceKind.Device, "copy", CapabilityRights.Execute);
        contractCopy[0] = "copy";
        Assert.Equal(bytes, manifest.SerializeCanonical());
        Assert.Equal(reference.SerializeCanonical(), manifest.SerializeCanonical());
        Assert.Equal(reference.ComputeDigest(), manifest.ComputeDigest());
        Assert.Equal(contractDigest, manifest.ContractDigest);
        Assert.Equal(new[] { "A", "Z" }, manifest.RequiredContracts);
    }

    [Fact]
    public void PublicViewCannotWeakenRealKernelAdmissionRequirement()
    {
        var kernel = new RuntimeKernel();
        var (process, handle) = TestFixtures.Create(kernel, 1, 10,
            capabilities: [new(ResourceKind.Device, "required", CapabilityRights.Read)]);
        var requirements = Assert.IsAssignableFrom<IList<CapabilityRequirementV1>>(process.Manifest.RequiredCapabilities);
        Assert.Throws<NotSupportedException>(() => requirements[0] = new(ResourceKind.Device, "required", CapabilityRights.None));
        Assert.Equal(KernelError.MissingCapability, kernel.AdmitProcess(handle).Error);
        Assert.Equal(ProcessState.Created, process.State);
        Assert.True(kernel.MintCapability(new(10), handle, ResourceKind.Device, "required", CapabilityRights.Read).IsSuccess);
        Assert.True(kernel.AdmitProcess(handle).IsSuccess);
        Assert.Equal(ProcessState.Admitted, process.State);
    }

    [Fact]
    public void ConcurrentPublicMutationAttemptsLeaveCanonicalInputsAndDigestsStable()
    {
        var manifest = TestFixtures.Manifest(1, 10, capabilities: [new(ResourceKind.Device, "required", CapabilityRights.Read)],
            contracts: ["Z", "A"]);
        var bytes = manifest.SerializeCanonical();
        var digest = manifest.ComputeDigest();
        var contractDigest = manifest.ContractDigest;
        var caps = Assert.IsAssignableFrom<IList<CapabilityRequirementV1>>(manifest.RequiredCapabilities);
        var contracts = Assert.IsAssignableFrom<IList<string>>(manifest.RequiredContracts);
        Parallel.For(0, 64, i =>
        {
            Assert.Throws<NotSupportedException>(() => caps[0] = new(ResourceKind.Device, $"changed-{i}", CapabilityRights.Write));
            Assert.Throws<NotSupportedException>(() => contracts[0] = $"changed-{i}");
            Assert.Equal(bytes, manifest.SerializeCanonical());
            Assert.Equal(digest, manifest.ComputeDigest());
            Assert.Equal(contractDigest, manifest.ContractDigest);
        });
    }
}
