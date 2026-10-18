using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Tests.Platform;

public sealed class PlatformDmaCopySubmissionContractTests
{
    [Fact]
    public void ExactReadWritePairAndAtomicReceiptValidateWithoutAuthority()
    {
        var request = Request();
        var submission = Submission(request);

        Assert.True(PlatformDmaCopySubmissionContract.ValidateRequest(request).IsSuccess);
        Assert.True(PlatformDmaCopySubmissionContract.ValidateSubmission(request, submission).IsSuccess);
        Assert.False(submission.AuthorizesDma);
        Assert.False(submission.AuthorizesRegionAccess);
        Assert.False(submission.ProvesCompletion);
    }

    [Fact]
    public void SameGrantDirectionOrLengthMismatchFailsBeforeProviderAcceptance()
    {
        var request = Request();
        Assert.Equal(PlatformAuthorityStatus.Denied,
            PlatformDmaCopySubmissionContract.ValidateRequest(request with
            {
                Destination = request.Destination with { Grant = request.Source.Grant },
                DestinationBinding = request.SourceBinding,
            }).Status);
        Assert.Equal(PlatformAuthorityStatus.Denied,
            PlatformDmaCopySubmissionContract.ValidateRequest(request with
            {
                Destination = request.Destination with
                {
                    Grant = request.Destination.Grant with { Direction = PlatformDmaDirection.DeviceReadsMemory },
                },
            }).Status);
        Assert.Equal(PlatformAuthorityStatus.Denied,
            PlatformDmaCopySubmissionContract.ValidateRequest(request with
            {
                Destination = request.Destination with
                {
                    Grant = request.Destination.Grant with { Range = new(0, 32) },
                },
            }).Status);
    }

    [Fact]
    public void StaleBindingGenerationFailsClosed()
    {
        var request = Request();
        var stale = request with
        {
            DestinationBinding = request.DestinationBinding with
            {
                TranslationGeneration = request.DestinationBinding.TranslationGeneration + 1,
            },
        };

        Assert.Equal(PlatformAuthorityStatus.Stale,
            PlatformDmaCopySubmissionContract.ValidateRequest(stale).Status);
    }

    [Fact]
    public void MalformedOrAliasedProviderReceiptFailsClosed()
    {
        var request = Request();
        var exact = Submission(request);
        Assert.Equal(PlatformAuthorityStatus.Faulted,
            PlatformDmaCopySubmissionContract.ValidateSubmission(request,
                exact with { CopySubmissionId = default }).Status);
        Assert.Equal(PlatformAuthorityStatus.Faulted,
            PlatformDmaCopySubmissionContract.ValidateSubmission(request,
                exact with { DestinationSubmission = exact.DestinationSubmission with
                    { SubmissionId = exact.SourceSubmission.SubmissionId } }).Status);
    }

    private static PlatformProviderDmaCopySubmitRequest Request()
    {
        var subject = new PlatformDomainIdentity(new(10), new(new(20), 30));
        var domain = new PlatformProviderDomainLease(new(1), new(2), subject);
        var device = new PlatformProviderDeviceLease(new(3), new(4), domain,
            new("device:copy"), PlatformDeviceRights.Read | PlatformDeviceRights.Write |
            PlatformDeviceRights.Configure);
        var sourceRegion = new PlatformRegionIdentity(new(new(5), new(6)), new(new(10), 30), 64);
        var destinationRegion = new PlatformRegionIdentity(new(new(7), new(8)), new(new(10), 30), 64);
        var sourceMapping = new PlatformProviderRegionMappingLease(new(9), new(10), domain,
            sourceRegion, PlatformMemoryAccess.Read);
        var destinationMapping = new PlatformProviderRegionMappingLease(new(11), new(12), domain,
            destinationRegion, PlatformMemoryAccess.Write);
        var sourceGrant = new PlatformProviderDmaGrant(new(13), new(14), device, sourceMapping,
            new(0, 64), PlatformDmaDirection.DeviceReadsMemory);
        var destinationGrant = new PlatformProviderDmaGrant(new(15), new(16), device, destinationMapping,
            new(0, 64), PlatformDmaDirection.DeviceWritesMemory);
        var source = new PlatformProviderDmaSubmitRequest(sourceGrant, new(17));
        var destination = new PlatformProviderDmaSubmitRequest(destinationGrant, new(18));
        return new(source, destination, Binding(sourceGrant, 19), Binding(destinationGrant, 20));
    }

    private static DmaExecutionBindingV1 Binding(PlatformProviderDmaGrant grant, ulong mutation) =>
        new(1, new string('a', 64), grant.MappingLease.Region.Handle.Generation.Value, mutation,
            grant.MappingLease.DomainLease.Subject.ProcessGeneration,
            grant.MappingLease.DomainLease.Generation.Value, grant.MappingLease.Generation.Value,
            grant.DeviceLease.Generation.Value, 21, grant.Generation.Value, 1,
            DmaEffectStateV1.Admitted, new string('b', 64));

    private static PlatformProviderDmaCopySubmission Submission(
        PlatformProviderDmaCopySubmitRequest request) => new(new(1), new(1),
            Leg(new(2), request.Source), Leg(new(3), request.Destination));

    private static PlatformProviderDmaSubmission Leg(
        PlatformProviderDmaSubmissionId id, PlatformProviderDmaSubmitRequest request) => new(
            id, new(1), request.Grant.GrantId, request.Grant.Generation,
            request.PreparedCycle, request.Grant.Range, request.Grant.Direction);
}
