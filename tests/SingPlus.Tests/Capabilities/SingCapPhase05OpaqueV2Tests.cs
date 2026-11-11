using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.FileSystem;

namespace SingPlus.Tests.Capabilities;

public sealed class SingCapPhase05OpaqueV2Tests
{
    [Fact]
    public void CanonicalWireRoundTripIsExactAndUnknownVersionFailsClosed()
    {
        CapabilityHandleV2 handle = new CapabilityHandleV2(CapabilityHandleV2Contract.Version,
            new(new Guid("11111111-2222-3333-4444-555555555555")),
            new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

        byte[] first = handle.SerializeCanonical();
        byte[] second = handle.SerializeCanonical();

        Assert.Equal(CapabilityHandleV2Contract.SerializedSize, first.Length);
        Assert.Equal(first, second);
        Assert.True(CapabilityHandleV2.TryDeserialize(first, out CapabilityHandleV2 roundTrip));
        Assert.Equal(handle, roundTrip);
        first[0] = 99;
        Assert.False(CapabilityHandleV2.TryDeserialize(first, out _));
    }

    [Fact]
    public void ForgedTokenTamperedRealmAndUnknownVersionAreRejected()
    {
        RuntimeKernel kernel = Scenario(out ProcessHandle owner);
        CapabilityHandleV2 handle = kernel.MintCapabilityV2(new(10), owner, ResourceKind.File, "file:v2",
            CapabilityRights.Read).Value;

        Assert.Equal(KernelError.ForgedCapability, kernel.ValidateCapability(owner,
            handle with { OpaqueToken = Guid.NewGuid() }, CapabilityRights.Read).Error);
        Assert.Equal(KernelError.WrongAuthorityRealm, kernel.ValidateCapability(owner,
            handle with { RealmId = new(Guid.NewGuid()) }, CapabilityRights.Read).Error);
        Assert.Equal(KernelError.InvalidMessage, kernel.ValidateCapability(owner,
            handle with { Version = 999 }, CapabilityRights.Read).Error);
    }

    [Fact]
    public void V1AndV2ResolveSameRecordAndCrossSurfaceRevokeIsImmediate()
    {
        RuntimeKernel kernel = Scenario(out ProcessHandle owner);
        CapabilityDescriptorV1 v1 = kernel.MintCapability(new(10), owner, ResourceKind.File, "file:same",
            CapabilityRights.Read, resourceGeneration: 7).Value!;
        CapabilityHandleV2 v2 = kernel.UpgradeCapabilityV1(owner, v1.CapabilityId).Value;

        CapabilityInspectionDescriptorV2 projection = kernel.ValidateCapability(owner, v2, CapabilityRights.Read, 7).Value!;
        Assert.Equal(v1.ResourceId, projection.ResourceId);
        Assert.Equal(v1.Rights, projection.Rights);
        Assert.Equal(7UL, projection.ResourceGeneration);

        Assert.True(kernel.RevokeCapability(v2).IsSuccess);
        Assert.Equal(KernelError.CapabilityRevoked,
            kernel.ValidateCapability(owner, v1.CapabilityId, CapabilityRights.Read, 7).Error);
    }

    [Fact]
    public void CopyingV2HandleDoesNotCloneQuotaOrOneShotAuthority()
    {
        CapabilityAuthority authority = new CapabilityAuthority();
        CapabilityDescriptorV1 descriptor = authority.Mint(new(1), new(10), ResourceKind.File, "file:once",
            CapabilityRights.Execute, 1, 1, null, null, 1, 1).Value!;
        CapabilityHandleV2 handle = authority.GetHandleV2(descriptor.CapabilityId, new(10), 1).Value;
        CapabilityHandleV2[] copies = Enumerable.Repeat(handle, 32).ToArray();

        KernelResult<OperationAuthorityLease>[] results = copies.AsParallel().Select(copy => authority.AcquireOperationAuthority(copy,
            new(10), 1, ResourceKind.File, "file:once", 1, CapabilityOperation.Execute,
            quotaAmount: 1, oneShot: true)).ToArray();

        Assert.Single(results, static result => result.IsSuccess);
        Assert.All(results.Where(static result => !result.IsSuccess),
            static result => Assert.Contains(result.Error, new[] { KernelError.CapabilityRevoked, KernelError.BudgetExceeded }));
        foreach (KernelResult<OperationAuthorityLease> result in results.Where(static result => result.IsSuccess)) result.Value!.Dispose();
    }

    [Fact]
    public void CallerEditedInspectionProjectionCannotWidenLedgerAuthority()
    {
        RuntimeKernel kernel = Scenario(out ProcessHandle owner);
        CapabilityHandleV2 handle = kernel.MintCapabilityV2(new(10), owner, ResourceKind.File, "file:read-only",
            CapabilityRights.Read).Value;
        CapabilityInspectionDescriptorV2 projection = kernel.ValidateCapability(owner, handle, CapabilityRights.Read).Value!;
        CapabilityInspectionDescriptorV2 edited = projection with { Rights = CapabilityRights.Read | CapabilityRights.Write, ResourceId = "file:other" };

        Assert.Equal(KernelError.InsufficientRights,
            kernel.ValidateCapability(owner, edited.Handle, CapabilityRights.Write).Error);
        Assert.Equal("file:read-only",
            kernel.ValidateCapability(owner, edited.Handle, CapabilityRights.Read).Value!.ResourceId);
    }

    [Fact]
    public async Task GeneratedFileSipFixtureAcceptsV2WithoutNewAuthorityStore()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessHandle caller = TestFixtures.Create(kernel, 51, 510).Handle;
        ProcessHandle service = TestFixtures.Create(kernel, 52, 520).Handle;
        CapabilityDescriptorV1 v1 = kernel.MintCapability(new(510), caller, ResourceKind.File,
            CapabilityResourceIds.FileNamespace, CapabilityRights.Read | CapabilityRights.Write).Value!;
        CapabilityHandleV2 v2 = kernel.UpgradeCapabilityV1(caller, v1.CapabilityId).Value;
        ProtocolDefinitionV1 protocol = IFileServiceProtocol.CreateDefinition();
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(service, "file-v2",
            new(protocol.ContractName, "2", protocol.ContractDigest), protocol,
            IFileServiceResponseProtocol.Definition,
            [new(ResourceKind.File, CapabilityResourceIds.FileNamespace, CapabilityRights.Read | CapabilityRights.Write)]).Value!;
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [v1.CapabilityId]).Value;
        RuntimeFileServiceHost host = RuntimeFileServiceHost.CreateForSession(kernel, service, session).Value!;
        IFileService client = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));

        Task<FileObjectResponse> pending = client.OpenV2Async(new(v2, "/v2", true)).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        FileObjectResponse opened = await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(session, opened.Authority.File.Session);
        Assert.True(kernel.ValidateCapability(caller, opened.Authority.Capability, CapabilityRights.Read).IsSuccess);
    }

    private static RuntimeKernel Scenario(out ProcessHandle owner)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        owner = TestFixtures.Create(kernel, 1, 10).Handle;
        return kernel;
    }
}
