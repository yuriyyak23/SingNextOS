using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class SingCapPhase13CrossAuthorityRaceTests
{
    private static readonly OperationDependencySnapshot Dependencies = new(7, 11, 13, 17);

    [Fact]
    public void ConcurrentOwnedBufferMoveHasExactlyOneWinner()
    {
        for (int iteration = 0; iteration < 250; iteration++)
        {
            RuntimeKernel kernel = new RuntimeKernel();
            ProcessHandle owner = Create(kernel, (ulong)(11000 + iteration), (ulong)(21000 + iteration));
            OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(owner, 8).Value!;
            OwnedBuffer<byte>? first = null;
            OwnedBuffer<byte>? second = null;
            Exception? firstFailure = null;
            Exception? secondFailure = null;

            Parallel.Invoke(
                () => { try { first = buffer.Move(); } catch (Exception error) { firstFailure = error; } },
                () => { try { second = buffer.Move(); } catch (Exception error) { secondFailure = error; } });

            Assert.Equal(1, new[] { first, second }.Count(static moved => moved is { IsValid: true }));
            Assert.Equal(1, new[] { firstFailure, secondFailure }.Count(static error => error is InvalidOperationException));
            Assert.False(buffer.IsValid);
            OwnedBuffer<byte> winner = first ?? second!;
            Assert.True(kernel.ReleaseRegion(owner, winner).IsSuccess);
        }
    }

    [Fact]
    public void ReleasedManagedRegionStorageIsNotReusedAcrossDomainsAndNewStorageIsZeroed()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessHandle firstOwner = Create(kernel, 10991, 20991);
        ProcessHandle secondOwner = Create(kernel, 10992, 20992);
        OwnedBuffer<byte> released = kernel.AllocateBuffer<byte>(firstOwner, 32).Value!;
        released.Span.Fill(0xa5);

        Assert.True(kernel.ReleaseRegion(firstOwner, released).IsSuccess);
        Assert.False(released.IsValid);
        Assert.Throws<InvalidOperationException>(() => { _ = released.Span[0]; });

        OwnedBuffer<byte> replacement = kernel.AllocateBuffer<byte>(secondOwner, 32).Value!;
        Assert.All(replacement.Span.ToArray(), static value => Assert.Equal(0, value));
        Assert.NotEqual(released.Handle.RegionId, replacement.Handle.RegionId);
        Assert.True(kernel.ReleaseRegion(secondOwner, replacement).IsSuccess);
    }

    [Fact]
    public async Task CapabilitySessionSealRegionAndExternalGatesCompleteDeadlockStress()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessHandle caller = Create(kernel, 12001, 22001);
        ProcessHandle service = Create(kernel, 12002, 22002);
        SingProcess callerProcess = kernel.Processes.Resolve(caller).Value!;
        CapabilityId capability = kernel.MintCapability(new(22002), caller, ResourceKind.File,
            "p13-stress", CapabilityRights.Read).Value!.CapabilityId;
        EndpointSessionHandle session = kernel.EndpointSessions.Add(caller, service, [capability], [],
            new(new(91), new(1), 1), null).Value!.Handle;
        ServiceEndpointDescriptor serviceDescriptor = new ServiceEndpointDescriptor(new(new(92), "p13"), new(1),
            new("p13", "1", "digest"), "p13/service", ServiceAvailability.Accepting);
        SealedHandle<FileObjectSeal> sealedHandle = kernel.SealedObjects.Seal<FileObjectSeal>(serviceDescriptor, service,
            caller, session, 1, 1, capability).Value;
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(caller, 8).Value!;
        OperationPreparation prepared = kernel.PrepareExternalOperation(caller,
            [new(buffer.Handle, RegionUseMode.ReadOnly, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!;

        Task[] tasks = Enumerable.Range(0, 32).Select(worker => Task.Run(() =>
        {
            for (int iteration = 0; iteration < 250; iteration++)
            {
                switch (worker % 5)
                {
                    case 0:
                        Assert.True(kernel.CapabilityAuthority.Validate(capability,
                            callerProcess.DomainId, caller.Generation, CapabilityRights.Read).IsSuccess);
                        break;
                    case 1:
                        using (EffectAdmissionLease effect = kernel.AdmitSessionCapabilityEffect(caller, service,
                                   session, capability, ResourceKind.File, "p13-stress", 1,
                                   CapabilityOperation.Read).Value!) { }
                        break;
                    case 2:
                        using (SealedObjectPin<FileObjectSeal> pin = kernel.SealedObjects.AcquirePin(sealedHandle,
                                   serviceDescriptor, service, caller, session, 1, capability).Value!)
                            Assert.True(kernel.SealedObjects.Revalidate(pin, serviceDescriptor, service).IsSuccess);
                        break;
                    case 3:
                        Assert.True(kernel.Regions.Validate(buffer.Handle,
                            new(callerProcess.DomainId, caller.Generation)).IsSuccess);
                        break;
                    default:
                        Assert.True(kernel.QueryExternalOperation(caller, prepared.Operation).IsSuccess);
                        break;
                }
            }
        })).ToArray();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(0, kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(0, kernel.EndpointSessions.ActivePinCount(session));
        using (SealedObjectPin<FileObjectSeal> closing = kernel.SealedObjects.AcquirePin(sealedHandle, serviceDescriptor,
                   service, caller, session, 1, capability).Value!)
            Assert.True(kernel.SealedObjects.BeginClose(closing).IsSuccess);
        Assert.Equal(KernelError.StaleHandle, kernel.SealedObjects.AcquirePin(sealedHandle,
            serviceDescriptor, service, caller, session, 1, capability).Error);
        Assert.True(kernel.CancelExternalOperation(caller, prepared.Operation, false).IsSuccess);
        Assert.True(kernel.ReleaseExternalOperation(caller, prepared.Operation, new(false, false)).IsSuccess);
        Assert.DoesNotContain(kernel.Regions.SnapshotUses(), use => use.State == RegionUseState.Active);
    }

    [Fact]
    public void PrepareVersusRegionMoveHasOneWinnerAndNoLeakedPins()
    {
        for (int iteration = 0; iteration < 50; iteration++)
        {
            RuntimeKernel kernel = new RuntimeKernel();
            ProcessHandle owner = Create(kernel, (ulong)(13000 + iteration * 2), (ulong)(23000 + iteration * 2));
            ProcessHandle target = Create(kernel, (ulong)(13001 + iteration * 2), (ulong)(23001 + iteration * 2));
            OwnedBuffer<byte> input = kernel.AllocateBuffer<byte>(owner, 8).Value!;
            OwnedBuffer<byte> output = kernel.AllocateBuffer<byte>(owner, 8).Value!;
            OperationPreparation prepared = kernel.PrepareExternalOperation(owner,
            [
                new(input.Handle, RegionUseMode.ReadOnly, new(0, 8)),
                new(output.Handle, RegionUseMode.StagedOutput, new(0, 8)),
            ], ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!;
            KernelResult<OperationAdmissionSnapshot> admitted = default;
            KernelResult<OwnedBuffer<byte>> moved = default;

            Parallel.Invoke(
                () => admitted = kernel.AdmitExternalOperation(owner, prepared.Operation, Dependencies),
                () => moved = kernel.TransferRegion(owner, target, input));

            Assert.True(admitted.IsSuccess || moved.IsSuccess);
            if (admitted.IsSuccess && moved.IsSuccess)
            {
                KernelResult<OperationBinding> staleSubmission = kernel.RecordExternalOperationSubmission(owner,
                    prepared.Operation, Dependencies);
                Assert.False(staleSubmission.IsSuccess);
                Assert.Equal(KernelError.StaleGeneration, staleSubmission.Error);
            }
            if (admitted.IsSuccess)
            {
                Assert.True(kernel.CancelExternalOperation(owner, prepared.Operation, false).IsSuccess);
                Assert.True(kernel.ReleaseExternalOperation(owner, prepared.Operation,
                    new(false, false)).IsSuccess);
            }
            else
            {
                Assert.True(kernel.CancelExternalOperation(owner, prepared.Operation, false).IsSuccess);
                Assert.True(kernel.ReleaseExternalOperation(owner, prepared.Operation,
                    new(false, false)).IsSuccess);
            }
            if (!moved.IsSuccess)
                moved = kernel.TransferRegion(owner, target, input);

            Assert.DoesNotContain(kernel.Regions.SnapshotUses(), use => use.State == RegionUseState.Active);
            Assert.True(kernel.ReleaseRegion(target, moved.Value!).IsSuccess);
            Assert.True(kernel.ReleaseRegion(owner, output).IsSuccess);
        }
    }

    [Fact]
    public void ProviderCompletionVersusProcessRestartCannotFakeClosureOrLeakPins()
    {
        for (int iteration = 0; iteration < 25; iteration++)
        {
            RuntimeKernel kernel = new RuntimeKernel();
            ProcessHandle owner = Create(kernel, (ulong)(15000 + iteration), (ulong)(25000 + iteration));
            OwnedBuffer<byte> input = kernel.AllocateBuffer<byte>(owner, 8).Value!;
            OwnedBuffer<byte> output = kernel.AllocateBuffer<byte>(owner, 8).Value!;
            OperationPreparation prepared = kernel.PrepareExternalOperation(owner,
            [
                new(input.Handle, RegionUseMode.ReadOnly, new(0, 8)),
                new(output.Handle, RegionUseMode.StagedOutput, new(0, 8)),
            ], ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!;
            Assert.True(kernel.AdmitExternalOperation(owner, prepared.Operation, Dependencies).IsSuccess);
            OperationBinding binding = kernel.RecordExternalOperationSubmission(owner, prepared.Operation, Dependencies).Value!;
            KernelResult<ExternalOperationSnapshot> completion = default;
            KernelResult teardown = default;

            Parallel.Invoke(
                () => completion = kernel.RecordExternalOperationCompletion(owner,
                    new(binding, ExternalOperationCompletionDisposition.Cancelled)),
                () => teardown = kernel.TerminateProcess(owner));

            Assert.True(completion.IsSuccess, completion.Message);
            Assert.False(teardown.IsSuccess);
            Assert.Equal(KernelError.PlatformBindingDraining, teardown.Error);
            Assert.Equal(ExternalOperationState.DeviceComplete,
                kernel.QueryExternalOperation(owner, prepared.Operation).Value!.State);
            Assert.True(kernel.ReleaseExternalOperation(owner, prepared.Operation, new(true, false)).IsSuccess);
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.DoesNotContain(kernel.Regions.SnapshotUses(), use => use.State == RegionUseState.Active);
            Assert.False(input.IsValid);
            Assert.False(output.IsValid);
        }
    }

    [Fact]
    public void PublicationVersusCapabilityRevokeAndRegionReclaimHonorsAdmittedPolicy()
    {
        for (int iteration = 0; iteration < 25; iteration++)
        {
            RuntimeKernel kernel = new RuntimeKernel();
            ProcessHandle owner = Create(kernel, (ulong)(17000 + iteration), (ulong)(27000 + iteration));
            SingProcess process = kernel.Processes.Resolve(owner).Value!;
            CapabilityId capability = kernel.MintCapability(process.DomainId, owner, ResourceKind.File,
                "p13-publication", CapabilityRights.Write).Value!.CapabilityId;
            using OperationAuthorityLease authority = kernel.CapabilityAuthority.AcquireOperationAuthority(capability,
                process.DomainId, owner.Generation, ResourceKind.File, "p13-publication", 1,
                CapabilityOperation.Write).Value!;
            OwnedBuffer<byte> input = kernel.AllocateBuffer<byte>(owner, 8).Value!;
            OwnedBuffer<byte> output = kernel.AllocateBuffer<byte>(owner, 8).Value!;
            OperationPreparation prepared = kernel.PrepareExternalOperation(owner,
            [
                new(input.Handle, RegionUseMode.ReadOnly, new(0, 8)),
                new(output.Handle, RegionUseMode.StagedOutput, new(0, 8)),
            ], ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!;
            Assert.True(kernel.AdmitExternalOperation(owner, prepared.Operation, Dependencies).IsSuccess);
            OperationBinding binding = kernel.RecordExternalOperationSubmission(owner, prepared.Operation, Dependencies).Value!;
            Assert.True(kernel.RecordExternalOperationCompletion(owner,
                new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
            Assert.True(kernel.RecordExternalOperationVisibility(owner,
                new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
            KernelResult<ExternalOperationSnapshot> publication = default;
            KernelResult reclaim = default;
            int publicationRan = 0;

            Parallel.Invoke(
                () => Assert.True(kernel.RevokeCapability(capability).IsSuccess),
                () => publication = kernel.PublishExternalOperation(owner, prepared.Operation,
                    Dependencies, new(ExternalPublicationPolicy.Staged),
                    () => Interlocked.Increment(ref publicationRan)),
                () => reclaim = kernel.ReleaseRegion(owner, output));

            Assert.Equal(EffectRevocationPolicy.AdmissionOnly, authority.RevocationPolicy);
            Assert.Equal(KernelError.CapabilityRevoked,
                kernel.CapabilityAuthority.AcquireOperationAuthority(capability, process.DomainId,
                    owner.Generation, ResourceKind.File, "p13-publication", 1,
                    CapabilityOperation.Write).Error);
            Assert.True(publication.IsSuccess, publication.Message);
            Assert.Equal(1, publicationRan);
            Assert.False(reclaim.IsSuccess);
            Assert.Equal(KernelError.RegionUseConflict, reclaim.Error);
            Assert.True(kernel.ReleaseExternalOperation(owner, prepared.Operation, new(true, false)).IsSuccess);
            Assert.DoesNotContain(kernel.Regions.SnapshotUses(), use => use.State == RegionUseState.Active);
            Assert.True(kernel.ReleaseRegion(owner, input).IsSuccess);
            Assert.True(kernel.ReleaseRegion(owner, output).IsSuccess);
        }
    }

    private static ProcessHandle Create(RuntimeKernel kernel, ulong processId, ulong domainId)
    {
        SingProcessManifestV1 manifest = TestFixtures.Manifest(processId, domainId);
        Assert.True(kernel.CreateProcess(manifest).IsSuccess);
        return new(manifest.ProcessId, manifest.Generation);
    }
}
