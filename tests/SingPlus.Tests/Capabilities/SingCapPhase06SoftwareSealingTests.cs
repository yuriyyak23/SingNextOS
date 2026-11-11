using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.Networking;

namespace SingPlus.Tests.Capabilities;

public sealed class SingCapPhase06SoftwareSealingTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OriginalPinCannotConsumeCopiedRouteAndReleasesOriginalFact(bool absent)
    {
        FixtureData fixture = Fixture();
        SealedHandle<SocketObjectSeal> handle = Seal(fixture);
        SealedObjectPin<SocketObjectSeal> pin = fixture.Seals.AcquirePin(handle, fixture.Descriptor, fixture.Service,
            fixture.Owner, fixture.Session, 1, fixture.Capability).Value!;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(SealedObjectAuthority)
            .GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Seals)!;
        object original = routes[handle.OpaqueToken]!;
        object copy = original.GetType().GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(original, null)!;
        PropertyInfo state = original.GetType().GetProperty("State")!;
        PropertyInfo pins = original.GetType().GetProperty("ActivePins")!;
        if (absent) routes.Remove(handle.OpaqueToken); else routes[handle.OpaqueToken] = copy;
        Assert.Equal(KernelError.StaleHandle, fixture.Seals.Revalidate(pin, fixture.Descriptor, fixture.Service).Error);
        Assert.Equal(KernelError.StaleHandle, fixture.Seals.BeginClose(pin).Error);
        Assert.False(fixture.Seals.AcquirePin(handle, fixture.Descriptor, fixture.Service,
            fixture.Owner, fixture.Session, 1, fixture.Capability).IsSuccess);
        routes[handle.OpaqueToken] = original;
        Assert.True(fixture.Seals.BeginClose(pin).IsSuccess);
        if (absent) routes.Remove(handle.OpaqueToken); else routes[handle.OpaqueToken] = copy;
        Parallel.Invoke(pin.Dispose, pin.Dispose);
        Assert.Equal(0, (int)pins.GetValue(original)!);
        Assert.Equal(SealedObjectState.Closed, (SealedObjectState)state.GetValue(original)!);
        Assert.Equal(1, (int)pins.GetValue(copy)!);
        Assert.Equal(SealedObjectState.Active, (SealedObjectState)state.GetValue(copy)!);
        routes[handle.OpaqueToken] = original;
        Assert.False(fixture.Seals.AcquirePin(handle, fixture.Descriptor, fixture.Service,
            fixture.Owner, fixture.Session, 1, fixture.Capability).IsSuccess);
    }

    private readonly struct UnknownSeal : ISealedContractMarker;

    [Fact]
    public void HandleWireFormatIsOpaqueDeterministicAndForgeryFails()
    {
        FixtureData fixture = Fixture();
        SealedHandle<SocketObjectSeal> handle = Seal(fixture);
        byte[] bytes = handle.SerializeCanonical();

        Assert.Equal(bytes, handle.SerializeCanonical());
        Assert.True(SealedHandle<SocketObjectSeal>.TryDeserialize(bytes, out SealedHandle<SocketObjectSeal> roundTrip));
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
        FixtureData fixture = Fixture();
        SealedHandle<SocketObjectSeal> handle = Seal(fixture);
        SealedHandle<UnknownSeal> wrongType = new SealedHandle<UnknownSeal>(handle.Version, handle.RealmId, handle.OpaqueToken);

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
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        (SingProcess _, ProcessHandle service) = TestFixtures.Create(kernel, 2, 20);
        ServiceEndpointDescriptor descriptor = Descriptor(service);
        EndpointSessionHandle session = new EndpointSessionHandle(new(1), new(1));
        CapabilityId capability = kernel.MintCapability(new(20), owner, ResourceKind.Network,
            "socket:1", CapabilityRights.Write).Value!.CapabilityId;
        SealedHandle<SocketObjectSeal> handle = kernel.SealedObjects.Seal<SocketObjectSeal>(descriptor, service, owner,
            session, 1, 1, capability).Value;
        kernel.RevokeCapability(capability);

        using SealedObjectPin<SocketObjectSeal> identity = kernel.SealedObjects.AcquirePin(handle, descriptor, service, owner,
            session, 1, capability).Value!;
        KernelResult<OperationAuthorityLease> effect = kernel.CapabilityAuthority.AcquireOperationAuthority(capability, new(10), 1,
            ResourceKind.Network, "socket:1", 1, CapabilityOperation.Write, session);

        Assert.Equal(1UL, identity.ObjectKey);
        Assert.Equal(KernelError.CapabilityRevoked, effect.Error);
    }

    [Fact]
    public void SiblingCapabilityAndSealCannotBeSubstitutedOrEnumerated()
    {
        FixtureData fixture = Fixture();
        SealedHandle<SocketObjectSeal> first = Seal(fixture, objectKey: 1);
        CapabilityId secondCapability = new CapabilityId(43);
        SealedHandle<SocketObjectSeal> second = fixture.Seals.Seal<SocketObjectSeal>(fixture.Descriptor, fixture.Service,
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
        FixtureData fixture = Fixture();
        SealedHandle<SocketObjectSeal> handle = Seal(fixture);
        using SealedObjectPin<SocketObjectSeal> first = fixture.Seals.AcquirePin(handle, fixture.Descriptor, fixture.Service,
            fixture.Owner, fixture.Session, 1, fixture.Capability).Value!;
        using SealedObjectPin<SocketObjectSeal> second = fixture.Seals.AcquirePin(handle, fixture.Descriptor, fixture.Service,
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
    public void DisposedPinCannotBorrowAnotherPinsLiveness()
    {
        FixtureData fixture = Fixture();
        SealedHandle<SocketObjectSeal> handle = Seal(fixture);
        SealedObjectPin<SocketObjectSeal> disposed = fixture.Seals.AcquirePin(handle, fixture.Descriptor, fixture.Service,
            fixture.Owner, fixture.Session, 1, fixture.Capability).Value!;
        using SealedObjectPin<SocketObjectSeal> live = fixture.Seals.AcquirePin(handle, fixture.Descriptor, fixture.Service,
            fixture.Owner, fixture.Session, 1, fixture.Capability).Value!;
        disposed.Dispose();

        Assert.Equal(KernelError.StaleHandle,
            fixture.Seals.Revalidate(disposed, fixture.Descriptor, fixture.Service).Error);
        Assert.Equal(KernelError.StaleHandle, fixture.Seals.BeginClose(disposed).Error);
        Assert.True(fixture.Seals.Revalidate(live, fixture.Descriptor, fixture.Service).IsSuccess);
    }

    [Fact]
    public void PinFromAnotherAuthorityCannotUseCollidingToken()
    {
        AuthorityRealmId realm = new AuthorityRealmId(Guid.NewGuid());
        Guid token = Guid.NewGuid();
        FixtureData first = Fixture(new SealedObjectAuthority(realm, tokenFactory: () => token));
        FixtureData second = Fixture(new SealedObjectAuthority(realm, tokenFactory: () => token));
        SealedHandle<SocketObjectSeal> firstHandle = Seal(first);
        SealedHandle<SocketObjectSeal> secondHandle = Seal(second);
        using SealedObjectPin<SocketObjectSeal> firstPin = first.Seals.AcquirePin(firstHandle, first.Descriptor, first.Service,
            first.Owner, first.Session, 1, first.Capability).Value!;
        using SealedObjectPin<SocketObjectSeal> secondPin = second.Seals.AcquirePin(secondHandle, second.Descriptor, second.Service,
            second.Owner, second.Session, 1, second.Capability).Value!;

        Assert.Equal(firstHandle, secondHandle);
        Assert.Equal(KernelError.StaleHandle,
            second.Seals.Revalidate(firstPin, second.Descriptor, second.Service).Error);
        Assert.Equal(KernelError.StaleHandle, second.Seals.BeginClose(firstPin).Error);
        Assert.True(second.Seals.Revalidate(secondPin, second.Descriptor, second.Service).IsSuccess);
    }

    [Fact]
    public void ServiceRestartGenerationRejectsOldHandleEvenWithReusedObjectKey()
    {
        FixtureData fixture = Fixture();
        SealedHandle<SocketObjectSeal> old = Seal(fixture);
        ServiceEndpointDescriptor restarted = fixture.Descriptor with
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
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessHandle caller = TestFixtures.Create(kernel, 11, 110).Handle;
        ProcessHandle service = TestFixtures.Create(kernel, 12, 120).Handle;
        CapabilityId endpointCapability = kernel.MintCapability(new(110), caller, ResourceKind.Network,
            CapabilityResourceIds.NetworkEndpoint, CapabilityRights.Configure).Value!.CapabilityId;
        ProtocolDefinitionV1 protocol = INetworkServiceProtocol.CreateDefinition();
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(service, "network-sealed",
            new(protocol.ContractName, "1", protocol.ContractDigest), protocol,
            INetworkServiceResponseProtocol.Definition,
            [new(ResourceKind.Network, CapabilityResourceIds.NetworkEndpoint, CapabilityRights.Configure)]).Value!;
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [endpointCapability]).Value;
        RuntimeNetworkServiceHost host = RuntimeNetworkServiceHost.CreateForSession(kernel, service, session).Value!;
        INetworkService client = INetworkServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));
        Task<SocketObjectResponse> opening = client.OpenAsync(new(endpointCapability, "sealed")).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        SocketObjectAuthority socket = (await opening).Authority;

        SocketObjectHandle forgedHandle = socket.Socket with { Seal = default };
        Task forged = client.SendAsync(new(forgedHandle, socket.Capability, new([1]))).AsTask();
        Assert.Equal(KernelError.InvalidMessage, host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => forged);

        Assert.True(kernel.RevokeCapability(socket.Capability).IsSuccess);
        Task noRights = client.SendAsync(new(socket.Socket, socket.Capability, new([2]))).AsTask();
        Assert.Equal(KernelError.CapabilityRevoked, host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => noRights);
    }

    private static SealedHandle<SocketObjectSeal> Seal(FixtureData fixture, ulong objectKey = 1) =>
        fixture.Seals.Seal<SocketObjectSeal>(fixture.Descriptor, fixture.Service, fixture.Owner,
            fixture.Session, 1, objectKey, fixture.Capability).Value;

    private static FixtureData Fixture(SealedObjectAuthority? seals = null)
    {
        AuthorityRealmId realm = new AuthorityRealmId(Guid.NewGuid());
        ProcessHandle service = new ProcessHandle(new(2), 1);
        return new(seals ?? new SealedObjectAuthority(realm), service, new(new(1), 1),
            new(new(7), new(1)), Descriptor(service), new(42));
    }

    private static ServiceEndpointDescriptor Descriptor(ProcessHandle service) => new(
        new(new(5), "network"), new(3), new("network", "1", "digest"),
        "sing-service/5", ServiceAvailability.Accepting);

    private sealed record FixtureData(SealedObjectAuthority Seals, ProcessHandle Service,
        ProcessHandle Owner, EndpointSessionHandle Session, ServiceEndpointDescriptor Descriptor,
        CapabilityId Capability);
}
