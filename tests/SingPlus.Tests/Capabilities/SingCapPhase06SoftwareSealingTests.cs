using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.Networking;

namespace SingPlus.Tests.Capabilities;

public sealed class SingCapPhase06SoftwareSealingTests
{
    private readonly struct UnknownSeal : ISealedContractMarker;

    [Fact]
    public void HandleWireFormatIsOpaqueDeterministicAndForgeryFails()
    {
        var fixture = Fixture();
        var handle = Seal(fixture);
        var bytes = handle.SerializeCanonical();

        Assert.Equal(bytes, handle.SerializeCanonical());
        Assert.True(SealedHandle<SocketObjectSeal>.TryDeserialize(bytes, out var roundTrip));
        Assert.Equal(handle, roundTrip);
        Assert.Equal(KernelError.ForgedCapability, fixture.Seals.AcquirePin(
            handle with { OpaqueToken = Guid.NewGuid() }, fixture.Descriptor, fixture.Service,
            fixture.Owner, fixture.Session, 1, fixture.Capability).Error);
        Assert.Equal(KernelError.WrongAuthorityRealm, fixture.Seals.AcquirePin(
            handle with { RealmId = new(Guid.NewGuid()) }, fixture.Descriptor, fixture.Service,
            fixture.Owner, fixture.Session, 1, fixture.Capability).Error);
    }

    [Fact]
    public void WrongTypeServiceIncarnationSessionOwnerGenerationAndCapabilityFailClosed()
    {
        var fixture = Fixture();
        var handle = Seal(fixture);
        var wrongType = new SealedHandle<UnknownSeal>(handle.Version, handle.RealmId, handle.OpaqueToken);

        Assert.Equal(KernelError.InvalidMessage, fixture.Seals.AcquirePin(wrongType,
            fixture.Descriptor, fixture.Service, fixture.Owner, fixture.Session, 1, fixture.Capability).Error);
        Assert.Equal(KernelError.StaleGeneration, fixture.Seals.AcquirePin(handle,
            fixture.Descriptor with { Generation = new(fixture.Descriptor.Generation.Value + 1) },
            fixture.Service, fixture.Owner, fixture.Session, 1, fixture.Capability).Error);
        Assert.Equal(KernelError.StaleGeneration, fixture.Seals.AcquirePin(handle,
            fixture.Descriptor, fixture.Service with { Generation = fixture.Service.Generation + 1 },
            fixture.Owner, fixture.Session, 1, fixture.Capability).Error);
        Assert.Equal(KernelError.WrongCapabilitySubject, fixture.Seals.AcquirePin(handle,
            fixture.Descriptor, fixture.Service, fixture.Owner with { Generation = fixture.Owner.Generation + 1 },
            fixture.Session, 1, fixture.Capability).Error);
        Assert.Equal(KernelError.WrongSessionOwner, fixture.Seals.AcquirePin(handle,
            fixture.Descriptor, fixture.Service, fixture.Owner,
            fixture.Session with { Generation = new(fixture.Session.Generation.Value + 1) }, 1, fixture.Capability).Error);
        Assert.Equal(KernelError.StaleGeneration, fixture.Seals.AcquirePin(handle,
            fixture.Descriptor, fixture.Service, fixture.Owner, fixture.Session, 2, fixture.Capability).Error);
        Assert.Equal(KernelError.WrongCapabilityResource, fixture.Seals.AcquirePin(handle,
            fixture.Descriptor, fixture.Service, fixture.Owner, fixture.Session, 1, new(999)).Error);
    }

    [Fact]
    public void SealIdentityAloneCannotAuthorizeEffectAfterCapabilityRevoke()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var (_, service) = TestFixtures.Create(kernel, 2, 20);
        var descriptor = Descriptor(service);
        var session = new EndpointSessionHandle(new(1), new(1));
        var capability = kernel.MintCapability(new(20), owner, ResourceKind.Network,
            "socket:1", CapabilityRights.Write).Value!.CapabilityId;
        var handle = kernel.SealedObjects.Seal<SocketObjectSeal>(descriptor, service, owner,
            session, 1, 1, capability).Value;
        kernel.RevokeCapability(capability);

        using var identity = kernel.SealedObjects.AcquirePin(handle, descriptor, service, owner,
            session, 1, capability).Value!;
        var effect = kernel.CapabilityAuthority.AcquireOperationAuthority(capability, new(10), 1,
            ResourceKind.Network, "socket:1", 1, CapabilityOperation.Write, session);

        Assert.Equal(1UL, identity.ObjectKey);
        Assert.Equal(KernelError.CapabilityRevoked, effect.Error);
    }

    [Fact]
    public void SiblingCapabilityAndSealCannotBeSubstitutedOrEnumerated()
    {
        var fixture = Fixture();
        var first = Seal(fixture, objectKey: 1);
        var secondCapability = new CapabilityId(43);
        var second = fixture.Seals.Seal<SocketObjectSeal>(fixture.Descriptor, fixture.Service,
            fixture.Owner, fixture.Session, 1, 2, secondCapability).Value;

        Assert.NotEqual(first, second);
        Assert.Equal(KernelError.WrongCapabilityResource, fixture.Seals.AcquirePin(first,
            fixture.Descriptor, fixture.Service, fixture.Owner, fixture.Session, 1, secondCapability).Error);
        Assert.DoesNotContain(typeof(SealedHandle<SocketObjectSeal>).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            method => method.Name.Contains("Resolve", StringComparison.Ordinal) || method.Name.Contains("Enumerate", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(SealedHandle<SocketObjectSeal>).GetProperties(), property =>
            property.Name.Contains("Rights", StringComparison.Ordinal) || property.Name.Contains("Resource", StringComparison.Ordinal));
    }

    [Fact]
    public void CloseRaceHasPinnedWinnerThenDeterministicClosure()
    {
        var fixture = Fixture();
        var handle = Seal(fixture);
        using var first = fixture.Seals.AcquirePin(handle, fixture.Descriptor, fixture.Service,
            fixture.Owner, fixture.Session, 1, fixture.Capability).Value!;
        using var second = fixture.Seals.AcquirePin(handle, fixture.Descriptor, fixture.Service,
            fixture.Owner, fixture.Session, 1, fixture.Capability).Value!;

        Assert.True(fixture.Seals.BeginClose(first).IsSuccess);
        Assert.Equal(KernelError.StaleHandle,
            fixture.Seals.Revalidate(second, fixture.Descriptor, fixture.Service).Error);
        Assert.Equal(KernelError.StaleHandle, fixture.Seals.AcquirePin(handle,
            fixture.Descriptor, fixture.Service, fixture.Owner, fixture.Session, 1, fixture.Capability).Error);

        second.Dispose();
        first.Dispose();
        Assert.Equal(SealedObjectState.Closed, fixture.Seals.InspectState(handle));
    }

    [Fact]
    public void ServiceRestartGenerationRejectsOldHandleEvenWithReusedObjectKey()
    {
        var fixture = Fixture();
        var old = Seal(fixture);
        var restarted = fixture.Descriptor with
        {
            Generation = new(fixture.Descriptor.Generation.Value + 1),
            Availability = ServiceAvailability.Accepting
        };

        Assert.Equal(KernelError.StaleGeneration, fixture.Seals.AcquirePin(old, restarted,
            fixture.Service, fixture.Owner, fixture.Session, 1, fixture.Capability).Error);
    }

    [Fact]
    public async Task RealSocketSentryRequiresBothSealAndLiveCapability()
    {
        var kernel = new RuntimeKernel();
        var caller = TestFixtures.Create(kernel, 11, 110).Handle;
        var service = TestFixtures.Create(kernel, 12, 120).Handle;
        var endpointCapability = kernel.MintCapability(new(110), caller, ResourceKind.Network,
            CapabilityResourceIds.NetworkEndpoint, CapabilityRights.Configure).Value!.CapabilityId;
        var protocol = INetworkServiceProtocol.CreateDefinition();
        var descriptor = kernel.RegisterService(service, "network-sealed",
            new(protocol.ContractName, "1", protocol.ContractDigest), protocol,
            INetworkServiceResponseProtocol.Definition,
            [new(ResourceKind.Network, CapabilityResourceIds.NetworkEndpoint, CapabilityRights.Configure)]).Value!;
        var session = kernel.OpenSession(caller, descriptor, [endpointCapability]).Value;
        var host = RuntimeNetworkServiceHost.CreateForSession(kernel, service, session).Value!;
        var client = INetworkServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));
        var opening = client.OpenAsync(new(endpointCapability, "sealed")).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var socket = (await opening).Authority;

        var forgedHandle = socket.Socket with { Seal = default };
        var forged = client.SendAsync(new(forgedHandle, socket.Capability, new([1]))).AsTask();
        Assert.Equal(KernelError.InvalidMessage, host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => forged);

        Assert.True(kernel.RevokeCapability(socket.Capability).IsSuccess);
        var noRights = client.SendAsync(new(socket.Socket, socket.Capability, new([2]))).AsTask();
        Assert.Equal(KernelError.CapabilityRevoked, host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => noRights);
    }

    private static SealedHandle<SocketObjectSeal> Seal(FixtureData fixture, ulong objectKey = 1) =>
        fixture.Seals.Seal<SocketObjectSeal>(fixture.Descriptor, fixture.Service, fixture.Owner,
            fixture.Session, 1, objectKey, fixture.Capability).Value;

    private static FixtureData Fixture()
    {
        var realm = new AuthorityRealmId(Guid.NewGuid());
        var service = new ProcessHandle(new(2), 1);
        return new(new SealedObjectAuthority(realm), service, new(new(1), 1),
            new(new(7), new(1)), Descriptor(service), new(42));
    }

    private static ServiceEndpointDescriptor Descriptor(ProcessHandle service) => new(
        new(new(5), "network"), new(3), new("network", "1", "digest"),
        "sing-service/5", ServiceAvailability.Accepting);

    private sealed record FixtureData(SealedObjectAuthority Seals, ProcessHandle Service,
        ProcessHandle Owner, EndpointSessionHandle Session, ServiceEndpointDescriptor Descriptor,
        CapabilityId Capability);
}
