using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class HybridCpuBorrowReadGrantTests
{
    [Fact]
    public void CpuBorrowCanShareExactReadGrantWithOwnerBoundNeutralDomainUntilClosed()
    {
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        RuntimeKernel kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
        (SingProcess _, ProcessHandle owner) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(
            kernel,
            51,
            510,
            1);
        (SingProcess _, ProcessHandle borrower) = HybridCpuPlatformAuthorityProviderTests.CreateProcess(
            kernel,
            52,
            520,
            1);
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        buffer.Span[32] = 0x5a;
        BorrowLease<byte> lease = CreateCpuBorrow(kernel, owner, borrower, buffer);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;

        KernelResult<PlatformBorrowReadGrant> grant = kernel.CreatePlatformBorrowReadGrant(
            owner,
            borrower,
            binding,
            lease.Handle,
            offset: 32,
            length: 128);

        Assert.True(grant.IsSuccess, grant.Message);
        Assert.Equal(PlatformMemoryAccess.Read, grant.Value!.Access);
        Assert.Equal(1, runtime.ActiveOwnedRegionMappingCount);
        Assert.True(lease.IsValid);
        Assert.Equal((byte)0x5a, lease.Span[32]);
        Assert.Throws<InvalidOperationException>(() => buffer.Span[32] = 0x6b);

        KernelResult<PlatformBorrowReadGrantEvidence> publication = kernel.PreparePlatformBorrowReadGrantForExternalReader(
            owner,
            grant.Value);
        Assert.True(publication.IsSuccess, publication.Message);
        Assert.True(publication.Value!.IsSatisfied);
        Assert.Equal(
            PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied,
            publication.Value.Outcome);

        KernelResult complete = kernel.RequestPlatformBorrowCompletion(
            owner,
            borrower,
            lease.Handle,
            grant.Value);

        Assert.True(complete.IsSuccess, complete.Message);
        Assert.Equal(0, runtime.ActiveOwnedRegionMappingCount);
        Assert.False(lease.IsValid);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        buffer.Span[32] = 0x6b;
        Assert.Equal((byte)0x6b, buffer.Span[32]);

        KernelResult<PlatformBorrowReadGrantEvidence> lateUse = kernel.PreparePlatformBorrowReadGrantForExternalReader(
            owner,
            grant.Value);
        Assert.Equal(KernelError.PlatformBindingNotFound, lateUse.Error);
    }

    private static BorrowLease<byte> CreateCpuBorrow(
        RuntimeKernel kernel,
        ProcessHandle owner,
        ProcessHandle borrower,
        OwnedBuffer<byte> buffer)
    {
        KernelResult<(ChannelEndpointHandle Left, ChannelEndpointHandle Right)> channel = kernel.CreateChannel(owner, borrower, BorrowProtocol(), 4);
        Assert.True(channel.IsSuccess, channel.Message);
        Assert.True(kernel.Send(owner, borrower, channel.Value.Left, 1).IsSuccess);
        Assert.True(kernel.Receive(borrower, channel.Value.Right).IsSuccess);
        Assert.True(kernel.Send(owner, borrower, channel.Value.Left, 2, buffer).IsSuccess);
        KernelResult<ChannelEnvelope> receive = kernel.Receive(borrower, channel.Value.Right);
        Assert.True(receive.IsSuccess, receive.Message);
        return Assert.IsType<BorrowLease<byte>>(receive.Value!.Payload);
    }

    private static RequestPayloadDescriptorV1 OwnershipRequest(string parameterName) =>
        new(
            RequestPayloadKind.Ownership,
            parameterName,
            "SingPlus.Sip.OwnedBuffer",
            ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer);

    private static ProtocolDefinitionV1 BorrowProtocol() => new(
        "SingPlus.Platform.HybridCpu.Tests.IBorrowGrantProtocol",
        "hybridcpu-borrow-grant-contract-digest",
        "Idle",
        new[] { "Done" },
        new[]
        {
            new ProtocolMessageDescriptorV1(1, "Start"),
            new ProtocolMessageDescriptorV1(
                2,
                "Peek",
                borrows: new[] { "data" },
                requestPayload: OwnershipRequest("data")),
        },
        new[]
        {
            new ProtocolTransitionV1(1, "Idle", "Active"),
            new ProtocolTransitionV1(2, "Active", "Active"),
        });
}
