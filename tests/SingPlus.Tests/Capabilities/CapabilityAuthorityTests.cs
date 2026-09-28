using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Capabilities;

public sealed class CapabilityAuthorityTests
{
    [Fact]
    public void SipProcessCannotMintOrGrantCapabilities()
    {
        var publicMethods = typeof(SingProcess).GetMethods(BindingFlags.Public | BindingFlags.Instance).Select(static method => method.Name).ToArray();
        Assert.DoesNotContain("Grant", publicMethods);
        Assert.DoesNotContain("Mint", publicMethods);

        var publicAuthorityMethods = typeof(CapabilityAuthority).GetMethods(BindingFlags.Public | BindingFlags.Instance).Select(static method => method.Name).ToArray();
        Assert.DoesNotContain("Mint", publicAuthorityMethods);
        Assert.DoesNotContain("Delegate", publicAuthorityMethods);
    }

    [Fact]
    public void WrongSubjectIsRejected()
    {
        var kernel = new RuntimeKernel();
        var (_, issuer) = TestFixtures.Create(kernel, 1, 10);
        var (_, subject) = TestFixtures.Create(kernel, 2, 20);
        var (_, other) = TestFixtures.Create(kernel, 3, 30);

        var minted = kernel.MintCapability(new DomainId(10), subject, ResourceKind.Device, "console0", CapabilityRights.Read);
        Assert.True(minted.IsSuccess, minted.Message);

        var validation = kernel.ValidateCapability(other, minted.Value!.CapabilityId, CapabilityRights.Read);
        Assert.False(validation.IsSuccess);
        Assert.Equal(KernelError.WrongCapabilitySubject, validation.Error);
    }

    [Fact]
    public void InsufficientRightIsRejected()
    {
        var kernel = new RuntimeKernel();
        TestFixtures.Create(kernel, 1, 10);
        var (_, subject) = TestFixtures.Create(kernel, 2, 20);
        var minted = kernel.MintCapability(new DomainId(10), subject, ResourceKind.Device, "timer0", CapabilityRights.Read);

        var validation = kernel.ValidateCapability(subject, minted.Value!.CapabilityId, CapabilityRights.Write);

        Assert.False(validation.IsSuccess);
        Assert.Equal(KernelError.InsufficientRights, validation.Error);
    }

    [Fact]
    public void RevokedCapabilityCannotBeValidated()
    {
        var kernel = new RuntimeKernel();
        TestFixtures.Create(kernel, 1, 10);
        var (_, subject) = TestFixtures.Create(kernel, 2, 20);
        var minted = kernel.MintCapability(new DomainId(10), subject, ResourceKind.KernelService, "clock", CapabilityRights.Read);

        Assert.True(kernel.RevokeCapability(minted.Value!.CapabilityId).IsSuccess);
        var validation = kernel.ValidateCapability(subject, minted.Value.CapabilityId, CapabilityRights.Read);

        Assert.False(validation.IsSuccess);
        Assert.Equal(KernelError.CapabilityRevoked, validation.Error);
    }

    [Fact]
    public void DelegationCreatesRestrictedCapabilityForTarget()
    {
        var kernel = new RuntimeKernel();
        TestFixtures.Create(kernel, 1, 10);
        var (_, source) = TestFixtures.Create(kernel, 2, 20);
        var (_, target) = TestFixtures.Create(kernel, 3, 30);
        var minted = kernel.MintCapability(new DomainId(10), source, ResourceKind.ChannelEndpoint, "control", CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Delegate);

        var delegated = kernel.DelegateCapability(source, target, minted.Value!.CapabilityId, CapabilityRights.Read);

        Assert.True(delegated.IsSuccess, delegated.Message);
        Assert.Equal(new DomainId(30), delegated.Value!.SubjectDomainId);
        Assert.Equal(CapabilityRights.Read, delegated.Value.Rights);
        Assert.True(kernel.ValidateCapability(target, delegated.Value.CapabilityId, CapabilityRights.Read).IsSuccess);
        Assert.Equal(KernelError.InsufficientRights, kernel.ValidateCapability(target, delegated.Value.CapabilityId, CapabilityRights.Write).Error);
    }

    [Fact]
    public void DelegationWithoutDelegateRightIsRejected()
    {
        var kernel = new RuntimeKernel();
        TestFixtures.Create(kernel, 1, 10);
        var (_, source) = TestFixtures.Create(kernel, 2, 20);
        var (_, target) = TestFixtures.Create(kernel, 3, 30);
        var minted = kernel.MintCapability(new DomainId(10), source, ResourceKind.Device, "device0", CapabilityRights.Read);

        var delegated = kernel.DelegateCapability(source, target, minted.Value!.CapabilityId, CapabilityRights.Read);

        Assert.False(delegated.IsSuccess);
        Assert.Equal(KernelError.InsufficientRights, delegated.Error);
    }

    [Fact]
    public void StaleCapabilityGenerationIsRejected()
    {
        var kernel = new RuntimeKernel();
        TestFixtures.Create(kernel, 1, 10);
        var (_, subject) = TestFixtures.Create(kernel, 2, 20, generation: 1);
        var minted = kernel.MintCapability(new DomainId(10), subject, ResourceKind.MemoryRegion, "region-x", CapabilityRights.Map);

        var validation = kernel.CapabilityAuthority.Validate(minted.Value!.CapabilityId, new DomainId(20), generation: 2, CapabilityRights.Map);

        Assert.False(validation.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, validation.Error);
    }

    [Fact]
    public void BulkRevokeInvalidatesAllCapabilitiesForDomain()
    {
        var kernel = new RuntimeKernel();
        TestFixtures.Create(kernel, 1, 10);
        var (_, subject) = TestFixtures.Create(kernel, 2, 20);
        var first = kernel.MintCapability(new DomainId(10), subject, ResourceKind.Device, "console0", CapabilityRights.Read);
        var second = kernel.MintCapability(new DomainId(10), subject, ResourceKind.Device, "timer0", CapabilityRights.Read);

        kernel.CapabilityAuthority.RevokeAllForDomain(new DomainId(20));

        Assert.Equal(KernelError.CapabilityRevoked, kernel.ValidateCapability(subject, first.Value!.CapabilityId, CapabilityRights.Read).Error);
        Assert.Equal(KernelError.CapabilityRevoked, kernel.ValidateCapability(subject, second.Value!.CapabilityId, CapabilityRights.Read).Error);
        Assert.Empty(kernel.CapabilityAuthority.SnapshotForDomain(new DomainId(20)));
    }
}
