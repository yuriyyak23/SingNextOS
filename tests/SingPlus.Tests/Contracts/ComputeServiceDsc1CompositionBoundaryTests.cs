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
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        DomainId serviceDomain = new DomainId(3290);
        (SingProcess _, ProcessHandle service) = TestFixtures.Create(kernel, 3209, serviceDomain.Value);
        (SingProcess _, ProcessHandle requester) = TestFixtures.Create(kernel, 3210, 3291);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(service).Value!;
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) channel = kernel.CreateChannel(
            requester,
            service,
            IComputeServiceProtocol.CreateDefinition(),
            IComputeServiceResponseProtocol.Definition,
            capacity: 1).Value;
        CapabilityId wrong = kernel.MintCapability(
            serviceDomain,
            service,
            ResourceKind.Compute,
            "compute:not-dsc1",
            CapabilityRights.Execute).Value!.CapabilityId;

        KernelResult<RuntimeComputeServiceHost> rejected = RuntimeComputeServiceHost.Create(
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
