using SingPlus.Contracts;
using SingPlus.Sip.Gui;
using SingPlus.Sip.Process;

namespace SingPlus.Tests.Capabilities;

public sealed class SingCapPhase08GeneratedSentryTests
{
    [Fact]
    public void ProcessRequestCopiesBoundedManifestAndDelegationGraphs()
    {
        CapabilityRequirementV1[] requirements = new[]
        {
            new CapabilityRequirementV1(ResourceKind.File, "root", CapabilityRights.Read)
        };
        string[] contracts = new[] { "contract-a" };
        SingProcessManifestV1 manifest = new SingProcessManifestV1(new(1), new(2), 1, "entry", ExecutionRole.Sip,
            MemoryProfile.SipRegion, requirements, contracts);
        ProcessCapabilityDelegation[] delegations = new[]
        {
            new ProcessCapabilityDelegation(new CapabilityId(7), CapabilityRights.Read)
        };

        CreateProcessRequest request = new CreateProcessRequest(new CapabilityId(3), manifest, ChildProcessPolicy.Attached, delegations);
        requirements[0] = new(ResourceKind.Device, "changed", CapabilityRights.Write);
        contracts[0] = "changed";
        delegations[0] = new(new CapabilityId(9), CapabilityRights.Write);
        SingProcessManifestV1 materialized = request.Manifest.MaterializeForRuntime();

        Assert.Equal(ResourceKind.File, Assert.Single(materialized.RequiredCapabilities).ResourceKind);
        Assert.Equal("contract-a", Assert.Single(materialized.RequiredContracts));
        Assert.Equal(new CapabilityId(7), request.InitialCapabilities[0].SourceCapability);
    }

    [Fact]
    public void SurfacePlaneSchemaCopiesCallerArrayAndBoundsCardinality()
    {
        SurfacePlaneSlice[] planes = new[] { new SurfacePlaneSlice(0, 16, 16) };
        SurfaceMetadata metadata = new SurfaceMetadata(SurfacePixelFormat.Bgra8888, 1, 1, 4,
            SurfaceLayout.Linear, planes, 1);
        planes[0] = new(8, 8, 8);

        Assert.Equal(new SurfacePlaneSlice(0, 16, 16), metadata.Planes[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SurfacePlaneSet(
            Enumerable.Range(0, SurfacePlaneSet.MaxCount + 1).Select(static value => new SurfacePlaneSlice(value, 1, 1))));
    }

    [Fact]
    public void GeneratedMetadataBindsCanonicalDeepValueSchema()
    {
        Assert.Contains("request=value:", IProcessServiceCapabilities.CreateAsync_ValueSchema, StringComparison.Ordinal);
        Assert.Contains("ProcessManifestValue", IProcessServiceCapabilities.CreateAsync_ValueSchema, StringComparison.Ordinal);
        Assert.Contains("response=value:", IProcessServiceCapabilities.CreateAsync_ValueSchema, StringComparison.Ordinal);
    }
}
