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
        var handle = new CapabilityHandleV2(CapabilityHandleV2Contract.Version,
            new(new Guid("11111111-2222-3333-4444-555555555555")),
            new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

        var first = handle.SerializeCanonical();
        var second = handle.SerializeCanonical();

        Assert.Equal(CapabilityHandleV2Contract.SerializedSize, first.Length);
        Assert.Equal(first, second);
        Assert.True(CapabilityHandleV2.TryDeserialize(first, out var roundTrip));
        Assert.Equal(handle, roundTrip);
        first[0] = 99;
        Assert.False(CapabilityHandleV2.TryDeserialize(first, out _));
    }

    [Fact]
    public void ForgedTokenTamperedRealmAndUnknownVersionAreRejected()
    {
        var kernel = Scenario(out var owner);
        var handle = kernel.MintCapabilityV2(new(10), owner, ResourceKind.File, "file:v2",
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
        var kernel = Scenario(out var owner);
        var v1 = kernel.MintCapability(new(10), owner, ResourceKind.File, "file:same",
            CapabilityRights.Read, resourceGeneration: 7).Value!;
        var v2 = kernel.UpgradeCapabilityV1(owner, v1.CapabilityId).Value;

        var projection = kernel.ValidateCapability(owner, v2, CapabilityRights.Read, 7).Value!;
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
        var authority = new CapabilityAuthority();
        var descriptor = authority.Mint(new(1), new(10), ResourceKind.File, "file:once",
            CapabilityRights.Execute, 1, 1, null, null, 1, 1).Value!;
        var handle = authority.GetHandleV2(descriptor.CapabilityId, new(10), 1).Value;
        var copies = Enumerable.Repeat(handle, 32).ToArray();

        var results = copies.AsParallel().Select(copy => authority.AcquireOperationAuthority(copy,
            new(10), 1, ResourceKind.File, "file:once", 1, CapabilityOperation.Execute,
            quotaAmount: 1, oneShot: true)).ToArray();

        Assert.Single(results, static result => result.IsSuccess);
        Assert.All(results.Where(static result => !result.IsSuccess),
            static result => Assert.Contains(result.Error, new[] { KernelError.CapabilityRevoked, KernelError.BudgetExceeded }));
        foreach (var result in results.Where(static result => result.IsSuccess)) result.Value!.Dispose();
    }

    [Fact]
    public void CallerEditedInspectionProjectionCannotWidenLedgerAuthority()
    {
        var kernel = Scenario(out var owner);
        var handle = kernel.MintCapabilityV2(new(10), owner, ResourceKind.File, "file:read-only",
            CapabilityRights.Read).Value;
        var projection = kernel.ValidateCapability(owner, handle, CapabilityRights.Read).Value!;
        var edited = projection with { Rights = CapabilityRights.Read | CapabilityRights.Write, ResourceId = "file:other" };

        Assert.Equal(KernelError.InsufficientRights,
            kernel.ValidateCapability(owner, edited.Handle, CapabilityRights.Write).Error);
        Assert.Equal("file:read-only",
            kernel.ValidateCapability(owner, edited.Handle, CapabilityRights.Read).Value!.ResourceId);
    }

    [Fact]
    public async Task GeneratedFileSipFixtureAcceptsV2WithoutNewAuthorityStore()
    {
        var kernel = new RuntimeKernel();
        var caller = TestFixtures.Create(kernel, 51, 510).Handle;
        var service = TestFixtures.Create(kernel, 52, 520).Handle;
        var v1 = kernel.MintCapability(new(510), caller, ResourceKind.File,
            CapabilityResourceIds.FileNamespace, CapabilityRights.Read | CapabilityRights.Write).Value!;
        var v2 = kernel.UpgradeCapabilityV1(caller, v1.CapabilityId).Value;
        var protocol = IFileServiceProtocol.CreateDefinition();
        var descriptor = kernel.RegisterService(service, "file-v2",
            new(protocol.ContractName, "2", protocol.ContractDigest), protocol,
            IFileServiceResponseProtocol.Definition,
            [new(ResourceKind.File, CapabilityResourceIds.FileNamespace, CapabilityRights.Read | CapabilityRights.Write)]).Value!;
        var session = kernel.OpenSession(caller, descriptor, [v1.CapabilityId]).Value;
        var host = RuntimeFileServiceHost.CreateForSession(kernel, service, session).Value!;
        var client = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));

        var pending = client.OpenV2Async(new(v2, "/v2", true)).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var opened = await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(session, opened.Authority.File.Session);
        Assert.True(kernel.ValidateCapability(caller, opened.Authority.Capability, CapabilityRights.Read).IsSuccess);
    }

    private static RuntimeKernel Scenario(out ProcessHandle owner)
    {
        var kernel = new RuntimeKernel();
        owner = TestFixtures.Create(kernel, 1, 10).Handle;
        return kernel;
    }
}
