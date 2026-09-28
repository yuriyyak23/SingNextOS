using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.Compute;

namespace SingPlus.Tests.Contracts;

public sealed class ComputeServiceDsc1CompositionBoundaryTests
{
    [Fact]
    [Trait("Category", "Runtime")]
    public void HostFactoryRejectsWrongInternalComputeAuthorityBeforeReceivingRequests()
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        var serviceDomain = new DomainId(3290);
        var (_, service) = TestFixtures.Create(kernel, 3209, serviceDomain.Value);
        var (_, requester) = TestFixtures.Create(kernel, 3210, 3291);
        var binding = kernel.BindPlatformDomain(service).Value!;
        var channel = kernel.CreateChannel(
            requester,
            service,
            IComputeServiceProtocol.CreateDefinition(),
            IComputeServiceResponseProtocol.Definition,
            capacity: 1).Value;
        var wrong = kernel.MintCapability(
            serviceDomain,
            service,
            ResourceKind.Compute,
            "compute:not-dsc1",
            CapabilityRights.Execute).Value!.CapabilityId;

        var rejected = RuntimeComputeServiceHost.Create(
            kernel,
            service,
            channel.Right,
            binding,
            new Dsc1ComputeCapability(wrong));

        Assert.False(rejected.IsSuccess);
        Assert.Equal(KernelError.WrongCapabilityResource, rejected.Error);
        Assert.Equal(0, provider.SubmitDsc1CopyCallCount);
    }
}
