using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class CxlType2AcceleratorServiceTests
{
    [Fact]
    public void StagedOperationTraversesCommonLifecycleBeforePublication()
    {
        var s = CreateScenario();
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1);
        Assert.True(execution.IsSuccess, execution.Message);
        Assert.Equal(ExternalOperationState.Submitted,
            s.Kernel.QueryExternalOperation(s.Handle, execution.Value!.Operation).Value!.State);
        Assert.Equal(0, s.Output.Span[0]);

        var finished = s.Service.CompleteVisiblePublish(s.Handle, execution.Value, [s.Candidate],
            () => s.Input.Span.CopyTo(s.Output.Span));
        Assert.True(finished.IsSuccess, finished.Message);
        Assert.Equal(ExternalOperationState.Released, finished.Value!.State);
        Assert.Equal(s.Input.Span.ToArray(), s.Output.Span.ToArray());
    }

    [Fact]
    public void StaleRegionUseRejectsBeforeProviderSubmission()
    {
        var s = CreateScenario();
        var conflicting = s.Kernel.AcquireRegionUse(s.Handle, s.Output.Handle, RegionUseMode.ExclusiveWrite, new(0, 8));
        Assert.True(conflicting.IsSuccess);
        var result = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1);
        Assert.False(result.IsSuccess);
        Assert.Equal(0, s.Accelerator.SubmitCount);
    }

    [Fact]
    public void DeviceResetDuringStagedExecutionNeverPublishes()
    {
        var s = CreateScenario();
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1).Value!;
        s.Output.Span.Fill(0);
        Assert.True(s.Memory.Rebind(s.Endpoint.EndpointId).IsSuccess);

        var result = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => s.Output.Span.Fill(9));
        Assert.False(result.IsSuccess);
        Assert.All(s.Output.Span.ToArray(), value => Assert.Equal(0, value));
        Assert.NotEqual(ExternalOperationDisposition.Published,
            s.Kernel.QueryExternalOperation(s.Handle, execution.Operation).Value!.Disposition);
    }

    [Fact]
    public void DirectModeIsFutureGatedBeforeProviderSubmission()
    {
        var s = CreateScenario();
        var directCandidate = s.Candidate with { Capabilities = ComputeProviderCapabilities.AcceleratorExecution |
            ComputeProviderCapabilities.DirectCoherentOutput | ComputeProviderCapabilities.CoherentHostAccess };
        var gated = s.Kernel.PlanCompute(s.Handle, s.Plan.Intent with { PublicationPreference = ComputePublicationPreference.DirectRequired },
            new(true, true, true), [directCandidate]);
        Assert.False(gated.IsSuccess);
        Assert.Equal(KernelError.PlatformUnavailable, gated.Error);
        Assert.Equal(0, s.Accelerator.SubmitCount);
    }

    [Fact]
    public void AcceleratorRoleDoesNotReplaceOrdinaryDeviceAuthority()
    {
        var s = CreateScenario();
        Assert.Equal(new PlatformDeviceIdentity("device:type2-0"), s.Lease.Device);
        Assert.Contains(typeof(ICxlType2AcceleratorProvider), s.Accelerator.GetType().GetInterfaces());
        Assert.DoesNotContain(typeof(IPlatformDeviceLeaseProvider), s.Accelerator.GetType().GetInterfaces());
    }

    [Fact]
    public void PublicComputeDescriptorCannotNamePhysicalCxlTransport()
    {
        var names = typeof(CxlAcceleratorRequest).GetProperties().Select(property => property.Name).ToArray();
        foreach (var forbidden in new[] { "Decoder", "Dpa", "Hpa", "Route", "Register", "Mailbox", "Opcode", "Lane", "Pasid" })
            Assert.DoesNotContain(names, name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StaleAcceleratorCancelAndReleaseCannotDeleteCurrentSubmission()
    {
        var s = CreateScenario();
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1).Value!;
        var stale = execution.Submission with { Generation = new(execution.Submission.Generation.Value + 1) };

        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.Cancel(stale).Status);
        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.Release(stale).Status);
        Assert.True(s.Accelerator.ObserveCompletion(execution.Submission).IsSuccess);
        Assert.True(s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => { }).IsSuccess);
    }

    [Fact]
    public void ProviderSubmitFailureLeavesNoHiddenOperationOrRegionUsePinned()
    {
        var s = CreateScenario();
        var activeBefore = s.Kernel.Regions.SnapshotUses().Count(use => use.State == RegionUseState.Active);
        s.Accelerator.DeviceAvailable = false;

        var result = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1);

        Assert.False(result.IsSuccess);
        Assert.Equal(activeBefore, s.Kernel.Regions.SnapshotUses().Count(use => use.State == RegionUseState.Active));
        var operation = s.Kernel.ExternalOperations.Query(new(new(1), new(1)));
        Assert.True(operation.IsSuccess, operation.Message);
        Assert.Equal(ExternalOperationState.Released, operation.Value!.State);
    }

    [Fact]
    public void SecureRequiredType2RejectsBeforeProviderEffectWithoutCurrentReadiness()
    {
        var s = CreateScenario();
        var secureCandidate = s.Candidate with { Capabilities = s.Candidate.Capabilities | ComputeProviderCapabilities.SecureComputeEvidence };
        var securePlan = s.Kernel.PlanCompute(s.Handle, s.Plan.Intent with { RequiresSecureEvidence = true },
            new(true, true, true), [secureCandidate]).Value!;
        var securityAuthority = new CxlSecurityAuthority(s.Bridge, s.Security);

        var result = s.Service.Submit(s.Handle, securePlan, [secureCandidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1, securityAuthority: securityAuthority,
            securityPolicy: new(CxlSecurityProperties.IdeEnabled | CxlSecurityProperties.DeviceAuthenticated,
                new(1), RequiredForOperation: true));

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.PlatformUnavailable, result.Error);
        Assert.Equal(0, s.Accelerator.SubmitCount);
        Assert.Equal(ExternalOperationState.Released,
            s.Kernel.ExternalOperations.Query(new(new(1), new(1))).Value!.State);
    }

    [Fact]
    public void ProcessTeardownClosesLiveType2SubmissionBeforeRegionReclaim()
    {
        var s = CreateScenario();
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1).Value!;

        Assert.True(s.Kernel.TerminateProcess(s.Handle).IsSuccess);

        Assert.Equal(ExternalOperationState.Released,
            s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.ObserveCompletion(execution.Submission).Status);
    }

    [Fact]
    public void AmbiguousType2ClosureQuarantinesProcessAndBlocksRegionReclaim()
    {
        var s = CreateScenario();
        _ = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease,
            s.Endpoint, s.Fabric, 1).Value!;
        s.Accelerator.CancellationFails = true;

        var teardown = s.Kernel.TerminateProcess(s.Handle);

        Assert.False(teardown.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, teardown.Error);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, s.Kernel.QueryProcessTeardown(s.Handle).Value!.Phase);
        var owner = new RegionOwner(s.Subject.DomainId, s.Handle.Generation);
        Assert.True(s.Kernel.Regions.Validate(s.Input.Handle, owner).IsSuccess);
    }

    [Fact]
    public void MalformedType2Submission_ReleaseFailure_KeepsOperationAndRegionUsesPinned()
    {
        var s = CreateScenario();
        var activeBefore = s.Kernel.Regions.SnapshotUses().Count(use => use.State == RegionUseState.Active);
        s.Accelerator.ReturnMalformedSubmission = true;
        s.Accelerator.ReleaseFails = true;

        var result = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);

        Assert.False(result.IsSuccess);
        var operation = s.Kernel.ExternalOperations.Query(new(new(1), new(1))).Value!;
        Assert.Equal(ExternalOperationState.Submitted, operation.State);
        Assert.True(s.Kernel.Regions.SnapshotUses().Count(use => use.State == RegionUseState.Active) > activeBefore);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, s.Kernel.QueryProcessTeardown(s.Handle).Value!.Phase);
    }

    [Fact]
    public void FailBeforePublication_ProviderReleaseFailure_QuarantinesInsteadOfReclaim()
    {
        var s = CreateScenario();
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        s.Accelerator.ReleaseFails = true;

        var result = s.Service.CompleteVisiblePublish(s.Handle, execution, [], () => Assert.Fail("publication ran"));

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(ExternalOperationState.Submitted, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
        Assert.Contains(s.Kernel.Regions.SnapshotUses(), use => use.State == RegionUseState.Active && use.Region == s.Output.Handle);
        s.Accelerator.CancellationFails = true;
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, s.Kernel.QueryProcessTeardown(s.Handle).Value!.Phase);
        Assert.True(s.Kernel.Regions.Validate(s.Output.Handle, new(s.Subject.DomainId, s.Handle.Generation)).IsSuccess);
    }

    [Fact]
    public void SubmitFailure_AmbiguousAcceptance_DoesNotUseProviderUnavailableAsClosure()
    {
        var s = CreateScenario();
        s.Accelerator.SubmitAcceptanceAmbiguous = true;

        var result = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);

        Assert.False(result.IsSuccess);
        Assert.Equal(ExternalOperationState.Submitted,
            s.Kernel.ExternalOperations.Query(new(new(1), new(1))).Value!.State);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, s.Kernel.QueryProcessTeardown(s.Handle).Value!.Phase);
    }

    [Fact]
    public void ProviderExceptionAfterSubmitAcceptanceIsContainedAsUnrecoverableQuarantine()
    {
        var s = CreateScenario();
        s.Accelerator.SubmitThrowsAfterAcceptance = true;

        var result = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
        Assert.Equal(ExternalOperationState.Submitted,
            s.Kernel.ExternalOperations.Query(new(new(1), new(1))).Value!.State);
        Assert.False(s.Kernel.TerminateProcess(s.Handle).IsSuccess);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, s.Kernel.QueryProcessTeardown(s.Handle).Value!.Phase);
    }

    [Fact]
    public void Type2Submit_WhileFabricBindingDraining_IsRejectedBeforeProviderEffect()
    {
        var s = CreateScenario();
        Assert.True(s.FabricManager.BeginReconfiguration(s.Fabric).IsSuccess);

        var result = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1);

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, result.Error);
        Assert.Equal(0, s.Accelerator.SubmitCount);
    }

    [Fact]
    public void FabricReconfiguration_WithLiveType2Submission_ClosesProviderBeforeExternalOperationRelease()
    {
        var s = CreateScenario();
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;

        var reconfiguration = s.FabricManager.BeginReconfiguration(s.Fabric);

        Assert.True(reconfiguration.IsSuccess, reconfiguration.Message);
        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.ObserveCompletion(execution.Submission).Status);
        Assert.Equal(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
    }

    [Fact]
    public void TwoBindingsSameEndpointSameGeneration_AreDistinctInAcceleratorContract()
    {
        var first = new CxlFabricBindingRef(new(41), new(1));
        var second = new CxlFabricBindingRef(new(42), new(1));

        Assert.NotEqual(first, second);
        Assert.Equal(first.Generation, second.Generation);
        Assert.Contains(nameof(CxlFabricBindingRef.BindingId), typeof(CxlFabricBindingRef).GetProperties().Select(property => property.Name));
        Assert.DoesNotContain("FabricGeneration", typeof(CxlAcceleratorRequest).GetProperties().Select(property => property.Name));

        var accelerator = new CxlType2ModelAccelerator();
        var operation = new OperationBinding(new(new(7), new(1)), new(1), 1);
        var uses = new[]
        {
            new RegionUseDescriptor(new(new(1), 1), new(new(1), new(1)), new(new(1), 1), new(0, 8), RegionUseMode.ReadOnly, new(1), RegionUseState.Active),
            new RegionUseDescriptor(new(new(2), 1), new(new(2), new(1)), new(new(1), 1), new(0, 8), RegionUseMode.StagedOutput, new(1), RegionUseState.Active)
        };
        var firstSubmission = accelerator.Submit(new(ComputeOperationKind.Copy, ComputePublicationPath.Staged,
            operation, uses, new("shared-endpoint"), new(3), first));
        var secondSubmission = accelerator.Submit(new(ComputeOperationKind.Copy, ComputePublicationPath.Staged,
            operation, uses, new("shared-endpoint"), new(3), second));

        Assert.True(firstSubmission.IsSuccess);
        Assert.True(secondSubmission.IsSuccess);
        Assert.NotEqual(firstSubmission.Value!.FabricBinding, secondSubmission.Value!.FabricBinding);
        Assert.Equal(first, firstSubmission.Value.FabricBinding);
        Assert.Equal(second, secondSubmission.Value.FabricBinding);
    }

    [Fact]
    public void NotAcceptedStatusExtendsPlatformContractWithoutRenumberingExistingStatuses()
    {
        Assert.Equal(0, (int)PlatformAuthorityStatus.Success);
        Assert.Equal(1, (int)PlatformAuthorityStatus.Unavailable);
        Assert.Equal(7, (int)PlatformAuthorityStatus.Faulted);
        Assert.Equal(8, (int)PlatformAuthorityStatus.NotAccepted);
    }

    [Fact]
    public void FaultedCompletionClosesProviderAndReleasesRegionUses()
    {
        var s = CreateScenario();
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        s.Accelerator.DeviceAvailable = false;

        var result = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => Assert.Fail("publication ran"));

        Assert.False(result.IsSuccess);
        Assert.Equal(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.ObserveCompletion(execution.Submission).Status);
    }

    [Fact]
    public void VisibilityFailureClosesProviderAndReleasesRegionUses()
    {
        var s = CreateScenario();
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        s.Accelerator.VisibilitySatisfied = false;

        var result = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => Assert.Fail("publication ran"));

        Assert.False(result.IsSuccess);
        Assert.Equal(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.ObserveCompletion(execution.Submission).Status);
    }

    [Fact]
    public void PublicationExceptionClosesProviderAndReleasesRegionUses()
    {
        var s = CreateScenario();
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;

        var result = s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate],
            () => throw new InvalidOperationException("publication failed"));

        Assert.False(result.IsSuccess);
        Assert.Equal(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(execution.Operation).Value!.State);
        Assert.Equal(PlatformAuthorityStatus.Stale, s.Accelerator.ObserveCompletion(execution.Submission).Status);
    }

    [Fact]
    public void CompletedSubmissionIsUntrackedBeforeLaterFabricReconfiguration()
    {
        var s = CreateScenario();
        var execution = s.Service.Submit(s.Handle, s.Plan, [s.Candidate], s.Subject, s.Lease, s.Endpoint, s.Fabric, 1).Value!;
        Assert.True(s.Service.CompleteVisiblePublish(s.Handle, execution, [s.Candidate], () => { }).IsSuccess);

        var reconfiguration = s.FabricManager.BeginReconfiguration(s.Fabric);

        Assert.True(reconfiguration.IsSuccess, reconfiguration.Message);
    }

    private static Scenario CreateScenario(bool direct = false)
    {
        var platform = new AuthorityProvider();
        var kernel = new RuntimeKernel(platform);
        var (_, handle) = TestFixtures.Create(kernel, 971, 972);
        var process = kernel.Processes.Resolve(handle).Value!;
        var owner = new RegionOwner(process.DomainId, handle.Generation);
        var subject = new PlatformDomainIdentity(process.DomainId, handle);
        var domain = kernel.BindPlatformAuthorityDomain(handle).Value!;
        var deviceCap = kernel.MintCapability(process.DomainId, handle, ResourceKind.Device, "device:type2-0",
            CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!;
        var lease = kernel.BindPlatformDevice(handle, domain, deviceCap.CapabilityId,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        var memory = new CxlType3ModelProvider();
        Assert.True(memory.RegisterEndpoint(new("type2-0"), lease.Device, 1024).IsSuccess);
        var endpoint = memory.QueryEndpoint(new("type2-0")).Value!;
        var security = new CxlSecurityModelProvider();
        var bridge = new CxlAuthorityBridge(kernel, memory, memory, memory, memory,
            new CoherentStub(), security);
        var control = kernel.AllocateBuffer<byte>(handle, 16).Value!;
        var controlUse = kernel.AcquireRegionUse(handle, control.Handle, RegionUseMode.DevicePrivate, new(0, 16)).Value!;
        var fabric = bridge.BindFabric(owner, controlUse.Handle, subject, lease,
            new(endpoint.EndpointId, endpoint.DeviceGeneration, 16, CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Value!;
        var input = kernel.AllocateBuffer<byte>(handle, 8).Value!;
        var output = kernel.AllocateBuffer<byte>(handle, 8).Value!;
        for (var i = 0; i < input.Length; i++) input.Span[i] = (byte)(i + 1);
        var preference = direct ? ComputePublicationPreference.DirectRequired : ComputePublicationPreference.StagedRequired;
        var capabilities = ComputeProviderCapabilities.AcceleratorExecution |
            (direct ? ComputeProviderCapabilities.DirectCoherentOutput | ComputeProviderCapabilities.CoherentHostAccess : ComputeProviderCapabilities.StagedPublication);
        var candidate = new ComputeProviderCandidate(new("type2-model"), 1, capabilities, 1024, 1, 1, true, false);
        var plan = kernel.PlanCompute(handle, new(ComputeOperationKind.Copy,
                new(input.Handle, new(0, 8)), new(output.Handle, new(0, 8)), preference, false, false),
            new(true, true, true), [candidate]).Value!;
        var accelerator = new CxlType2ModelAccelerator();
        var manager = new CxlFabricManagerAuthority(kernel, memory);
        Assert.True(manager.Register(fabric).IsSuccess);
        var service = new CxlType2AcceleratorService(kernel, bridge, accelerator, manager);
        return new(kernel, handle, subject, lease, memory, endpoint, fabric, input, output, candidate, plan, accelerator, service, bridge, security, manager);
    }

    private sealed record Scenario(RuntimeKernel Kernel, ProcessHandle Handle, PlatformDomainIdentity Subject,
        PlatformDeviceLease Lease, CxlType3ModelProvider Memory, CxlEndpointSnapshot Endpoint, CxlFabricBinding Fabric,
        SingPlus.Sip.OwnedBuffer<byte> Input, SingPlus.Sip.OwnedBuffer<byte> Output, ComputeProviderCandidate Candidate,
        ComputePlan Plan, CxlType2ModelAccelerator Accelerator, CxlType2AcceleratorService Service,
        CxlAuthorityBridge Bridge, CxlSecurityModelProvider Security, CxlFabricManagerAuthority FabricManager);

    private sealed class CoherentStub : ICxlCoherentAccessProvider
    {
        public PlatformAuthorityResult<CxlCoherentBinding> BindCoherentAccess(CxlFabricBinding f, RegionUseDescriptor u) => Fail<CxlCoherentBinding>();
        public PlatformAuthorityResult<CxlCoherentBinding> QueryCoherentAccess(CxlCoherentBindingId id) => Fail<CxlCoherentBinding>();
        public PlatformAuthorityResult ReleaseCoherentAccess(CxlCoherentBinding b) => PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Unsupported, "unsupported");
        private static PlatformAuthorityResult<T> Fail<T>() => PlatformAuthorityResult<T>.Fail(PlatformAuthorityStatus.Unsupported, "unsupported");
    }
    private sealed class EvidenceStub : ICxlSecurityEvidenceProvider
    {
        public PlatformAuthorityResult<EvidenceRecord> QuerySecurityEvidence(CxlEndpointId id, CxlDeviceGeneration g) =>
            PlatformAuthorityResult<EvidenceRecord>.Fail(PlatformAuthorityStatus.Unsupported, "unsupported");
    }
    private sealed class AuthorityProvider : IPlatformAuthorityProvider, IPlatformDeviceLeaseProvider, IPlatformFeatureProvider
    {
        public PlatformProviderDescriptor Descriptor { get; } = new(new("type2-platform"), 1, PlatformAuthorityFeatures.NeutralDomainBinding);
        public PlatformFeatureManifest QueryFeatures() => new(new[]
        {
            new PlatformFeatureDescriptor(PlatformFeatureFamily.NeutralDomains, PlatformDomainContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.IoDomainBinding, PlatformDeviceLeaseContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission)
        });
        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity s) => PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(new(new(1), new(1), s));
        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease l) => PlatformAuthorityResult.Ok();
        public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(PlatformProviderDomainLease d, PlatformDeviceIdentity i, PlatformDeviceRights r) => PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(new(new(1), new(1), d, i, r));
        public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease l) => PlatformAuthorityResult.Ok();
        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(PlatformProviderDomainLease d, PlatformRegionIdentity r, PlatformMemoryAccess a) => PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(PlatformAuthorityStatus.Unsupported, "unused");
        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease m, PlatformRegionRevocationPolicy p) => PlatformAuthorityResult.Ok();
    }
}
