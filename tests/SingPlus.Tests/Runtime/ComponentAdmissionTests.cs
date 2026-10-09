using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Sip.Compute;

namespace SingPlus.Tests.Runtime;

public sealed class ComponentAdmissionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ControllerBindingRefusalPreservesLateReceiptForActualCleanup(bool late)
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, principal) = TestFixtures.Create(kernel, 927, 9270, identity: "binding-controller");
        Assert.True(kernel.AdmitProcess(principal).IsSuccess);
        var control = kernel.MintCapability(new DomainId(9270), principal, ResourceKind.KernelService,
            CapabilityResourceIds.ServiceSupervisor, CapabilityRights.Configure | CapabilityRights.Execute).Value!.CapabilityId;
        var image = new byte[] { 7, 27 };
        var manifest = new ServiceManifestV1(new("controlled-binding"), new("1"), Digest(image),
            TestFixtures.Manifest(928, 9280), platformRequirements:
            [new PlatformRequirementV1(ComponentPlatformFeatureFamily.NeutralDomains, PlatformDomainContract.ContractVersion,
                ComponentPlatformFeatureAvailability.RuntimeAdmission)],
            resourceRequirements: [ComponentResourceRequirementV1.ForPlatformAuthorityDomain()]);
        Action revoke = () => Assert.True(kernel.CapabilityAuthority.Revoke(control).IsSuccess);
        if (late) kernel.BeforePlatformBindingAttachmentQualificationHook = revoke;
        else kernel.BeforeComponentPlatformBindingQualificationHook = revoke;
        Assert.Equal(KernelError.SupervisorDenied, kernel.AdmitComponent(new(manifest, image), principal, control).Error);
        Assert.Equal(late ? 1 : 0, provider.BindDomainCallCount);
        var component = kernel.QueryComponent(manifest.Identity).Value!;
        Assert.Equal(ComponentLifecycleState.Reclaimable, component.State);
        Assert.True(kernel.Processes.ResolveTerminalState(component.Process).IsSuccess);
        Assert.True(kernel.RetireReclaimableComponent(manifest.Identity, component.Process).IsSuccess);
        Assert.False(kernel.Budgets.HasProcessAccount(component.Process));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LateTeardownIntentCannotRegressActualReclaimOrDrainReplacement(bool replace)
    {
        var kernel = new RuntimeKernel();
        byte[] image = [30, 31];
        var identity = new ComponentIdentity("teardown-intent");
        var admitted = kernel.AdmitComponent(new(new ServiceManifestV1(identity, new("1"), Digest(image),
            TestFixtures.Manifest(1121, 2121)), image)).Value!;
        ProcessHandle fresh = default;
        kernel.BeforeComponentTeardownIntentQualificationHook = () =>
        {
            kernel.BeforeComponentTeardownIntentQualificationHook = null;
            Assert.True(kernel.DrainComponent(identity, admitted.Process).Value!.Reclaimable);
            if (replace)
            {
                Assert.True(kernel.RetireReclaimableComponent(identity, admitted.Process).IsSuccess);
                fresh = kernel.AdmitComponent(new(new ServiceManifestV1(identity, new("2"), Digest(image),
                    TestFixtures.Manifest(1121, 2121, generation: 2)), image)).Value!.Process;
            }
        };
        var result = kernel.DrainComponent(identity, admitted.Process);
        Assert.Equal(!replace, result.IsSuccess);
        Assert.Equal(ProcessState.Exited, kernel.Processes.ResolveTerminalState(admitted.Process).Value);
        var current = kernel.QueryComponent(identity).Value!;
        if (replace)
        {
            Assert.Equal(KernelError.StaleGeneration, result.Error);
            Assert.Equal(fresh, current.Process);
            Assert.Equal(ComponentLifecycleState.Running, current.State);
            Assert.Equal(ProcessState.Running, kernel.Processes.Resolve(fresh).Value!.State);
            Assert.Null(current.Failure);
        }
        else
        {
            Assert.True(current.Reclaimable);
            Assert.True(kernel.RetireReclaimableComponent(identity, admitted.Process).IsSuccess);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OptionalEvidenceCannotPublishAfterOriginalProcessClosure(bool stop)
    {
        var kernel = new RuntimeKernel();
        byte[] image = [28, 29];
        var identity = new ComponentIdentity("optional-evidence-tracking");
        var old = new ProcessHandle(new ProcessId(1120), 1);
        ManifestAdmissionResultV1? before = null;
        kernel.BeforeComponentOptionalEvidenceQualificationHook = () =>
        {
            kernel.BeforeComponentOptionalEvidenceQualificationHook = null;
            before = kernel.QueryComponent(identity).Value!.Admission;
            if (stop) Assert.True(kernel.TerminateProcess(old).IsSuccess);
        };
        var result = kernel.AdmitComponent(new(new ServiceManifestV1(identity, new("1"), Digest(image),
            TestFixtures.Manifest(1120, 2120), dependencies: [new(new ServiceContractIdentity("OptionalAbsent", "1", "optional"), ServiceDependencyKind.Optional)]), image));
        Assert.NotNull(before);
        if (stop)
        {
            Assert.Equal(KernelError.StaleHandle, result.Error);
            var component = kernel.QueryComponent(identity).Value!;
            Assert.True(component.Reclaimable);
            Assert.Same(before, component.Admission);
            Assert.True(kernel.RetireReclaimableComponent(identity, old).IsSuccess);
        }
        else
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(ComponentLifecycleState.Running, result.Value!.State);
            Assert.Empty(result.Value.Sessions);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LateDomainBindingTrackingUsesOriginalClosureWithoutPublishingOrTouchingReplacement(bool replace)
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        byte[] image = [26, 27];
        var identity = new ComponentIdentity("binding-tracking");
        var old = new ProcessHandle(new ProcessId(1119), 1);
        ProcessHandle fresh = default;
        kernel.BeforeComponentBindingTrackingQualificationHook = () =>
        {
            kernel.BeforeComponentBindingTrackingQualificationHook = null;
            Assert.False(kernel.QueryComponent(identity).Value!.PlatformAuthorityLive);
            Assert.True(kernel.TerminateProcess(old).IsSuccess);
            if (replace)
            {
                (_, fresh) = TestFixtures.Create(kernel, 1119, 2119, generation: 2);
                Assert.True(kernel.AdmitProcess(fresh).IsSuccess);
                Assert.True(kernel.StartProcess(fresh).IsSuccess);
            }
        };
        var result = kernel.AdmitComponent(new(new ServiceManifestV1(identity, new("1"), Digest(image),
            TestFixtures.Manifest(1119, 2119), requiresPlatformDomain: true), image));
        Assert.Equal(KernelError.StaleHandle, result.Error);
        Assert.Equal(1, provider.BindDomainCallCount);
        var component = kernel.QueryComponent(identity).Value!;
        Assert.True(component.Reclaimable);
        Assert.False(component.PlatformAuthorityLive);
        Assert.Equal(ProcessState.Exited, kernel.Processes.ResolveTerminalState(old).Value);
        Assert.True(kernel.RetireReclaimableComponent(identity, old).IsSuccess);
        if (replace) Assert.Equal(ProcessState.Running, kernel.Processes.Resolve(fresh).Value!.State);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void LateServiceOrSessionTrackingRefusesTerminalOriginalAndPreservesReplacement(bool sessionStage, bool replace)
    {
        var kernel = new RuntimeKernel();
        byte[] image = [24, 25];
        var identity = new ComponentIdentity("service-session-tracking");
        var old = new ProcessHandle(new ProcessId(1117), 1);
        var dependency = new ServiceContractIdentity("TrackingDependency", "1", "dependency-tracking");
        var provider = TestFixtures.Create(kernel, 1118, 2118).Handle;
        Assert.True(kernel.AdmitProcess(provider).IsSuccess);
        Assert.True(kernel.RegisterService(provider, "tracking-dependency", dependency,
            Protocol(dependency.Name, dependency.Digest)).IsSuccess);
        var own = new ServiceContractIdentity("TrackingService", "1", "service-tracking");
        var provided = new ProvidedServiceManifestV1("tracking-service", own);
        ProcessHandle fresh = default;
        Action stop = () =>
        {
            kernel.BeforeComponentServiceTrackingQualificationHook = null;
            kernel.BeforeComponentSessionTrackingQualificationHook = null;
            Assert.True(kernel.TerminateProcess(old).IsSuccess);
            if (replace)
            {
                (_, fresh) = TestFixtures.Create(kernel, 1117, 2117, generation: 2);
                Assert.True(kernel.AdmitProcess(fresh).IsSuccess);
                Assert.True(kernel.StartProcess(fresh).IsSuccess);
            }
        };
        if (sessionStage) kernel.BeforeComponentSessionTrackingQualificationHook = stop;
        else kernel.BeforeComponentServiceTrackingQualificationHook = stop;
        var result = kernel.AdmitComponent(new(new ServiceManifestV1(identity, new("1"), Digest(image),
            TestFixtures.Manifest(1117, 2117), [provided], [dependency]), image,
            providedServices: [new ComponentProvidedServiceRegistration(provided, Protocol(own.Name, own.Digest))]));
        Assert.Equal(KernelError.StaleHandle, result.Error);
        var component = kernel.QueryComponent(identity).Value!;
        Assert.True(component.Reclaimable);
        Assert.Empty(component.ServiceInstances);
        Assert.Empty(component.Sessions);
        Assert.Empty(kernel.ResolveByContract(own).Value!);
        Assert.Single(kernel.ResolveByContract(dependency).Value!);
        Assert.Equal(ProcessState.Exited, kernel.Processes.ResolveTerminalState(old).Value);
        Assert.True(kernel.RetireReclaimableComponent(identity, old).IsSuccess);
        if (replace) Assert.Equal(ProcessState.Running, kernel.Processes.Resolve(fresh).Value!.State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MintedCapabilityTrackingRefusesTerminalOriginalAndPreservesReplacementProcess(bool replace)
    {
        var kernel = new RuntimeKernel();
        byte[] image = [22, 23];
        var identity = new ComponentIdentity("capability-tracking");
        var old = new ProcessHandle(new ProcessId(1116), 1);
        var requirement = new CapabilityRequirementV1(ResourceKind.KernelService, "tracking-data", CapabilityRights.Read);
        ProcessHandle fresh = default;
        CapabilityId freshCapability = default;
        kernel.BeforeComponentCapabilityTrackingQualificationHook = () =>
        {
            kernel.BeforeComponentCapabilityTrackingQualificationHook = null;
            Assert.Empty(kernel.QueryComponent(identity).Value!.Capabilities);
            Assert.True(kernel.TerminateProcess(old).IsSuccess);
            Assert.Equal(ProcessState.Exited, kernel.Processes.ResolveTerminalState(old).Value);
            if (replace)
            {
                (_, fresh) = TestFixtures.Create(kernel, 1116, 2116, generation: 2, capabilities: [requirement]);
                freshCapability = kernel.MintCapability(new DomainId(2116), fresh, requirement.ResourceKind,
                    requirement.ResourceId, requirement.Rights).Value!.CapabilityId;
                Assert.True(kernel.AdmitProcess(fresh).IsSuccess);
                Assert.True(kernel.StartProcess(fresh).IsSuccess);
            }
        };
        var result = kernel.AdmitComponent(new(new ServiceManifestV1(identity, new("1"), Digest(image),
            TestFixtures.Manifest(1116, 2116, capabilities: [requirement])), image,
            [new ComponentCapabilityGrant(new DomainId(2116), requirement)]));
        Assert.Equal(KernelError.StaleHandle, result.Error);
        var current = kernel.QueryComponent(identity).Value!;
        Assert.Equal(old, current.Process);
        Assert.True(current.Reclaimable);
        Assert.Empty(current.Capabilities);
        Assert.True(kernel.RetireReclaimableComponent(identity, old).IsSuccess);
        if (replace)
        {
            Assert.Equal(ProcessState.Running, kernel.Processes.Resolve(fresh).Value!.State);
            Assert.True(kernel.ValidateCapability(fresh, freshCapability, CapabilityRights.Read).IsSuccess);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CreatedPublicationRefusesChangedProcessBeforeCapabilityMint(bool replace)
    {
        var kernel = new RuntimeKernel();
        byte[] image = [20, 21];
        var identity = new ComponentIdentity("created-publication");
        var old = new ProcessHandle(new ProcessId(1115), 1);
        ProcessHandle fresh = default;
        var requirement = new CapabilityRequirementV1(ResourceKind.KernelService, "created-data", CapabilityRights.Read);
        kernel.BeforeComponentCreatedPublicationQualificationHook = () =>
        {
            kernel.BeforeComponentCreatedPublicationQualificationHook = null;
            Assert.Empty(kernel.QueryComponent(identity).Value!.Capabilities);
            if (replace)
            {
                Assert.True(kernel.TerminateProcess(old).IsSuccess);
                (_, fresh) = TestFixtures.Create(kernel, 1115, 2115, generation: 2);
                Assert.True(kernel.AdmitProcess(fresh).IsSuccess);
            }
            else
            {
                Assert.True(kernel.MintCapability(new DomainId(2115), old, requirement.ResourceKind,
                    requirement.ResourceId, requirement.Rights).IsSuccess);
                Assert.True(kernel.AdmitProcess(old).IsSuccess);
            }
        };
        var result = kernel.AdmitComponent(new(new ServiceManifestV1(identity, new("1"), Digest(image),
            TestFixtures.Manifest(1115, 2115, capabilities: [requirement])), image, [new ComponentCapabilityGrant(new DomainId(2115), requirement)]));
        Assert.Equal(replace ? KernelError.StaleHandle : KernelError.InvalidTransition, result.Error);
        var component = kernel.QueryComponent(identity).Value!;
        Assert.True(component.Reclaimable);
        Assert.Empty(component.Capabilities);
        Assert.Equal(ProcessState.Exited, kernel.Processes.ResolveTerminalState(old).Value);
        Assert.True(kernel.RetireReclaimableComponent(identity, old).IsSuccess);
        if (replace) Assert.Equal(ProcessState.Admitted, kernel.Processes.Resolve(fresh).Value!.State);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void ComponentStartupAndFinalPublicationRequireFreshMandatoryCapability(bool afterStart, bool revokeDomain)
    {
        var kernel = new RuntimeKernel();
        byte[] image = [18, 19];
        var identity = new ComponentIdentity("fresh-start-capability");
        var requirement = new CapabilityRequirementV1(ResourceKind.KernelService, "start-data", CapabilityRights.Read);
        var startup = 0;
        Action revoke = () =>
        {
            var capability = Assert.Single(kernel.QueryComponent(identity).Value!.Capabilities);
            if (revokeDomain) kernel.CapabilityAuthority.RevokeAllForDomain(new DomainId(2111));
            else Assert.True(kernel.RevokeCapability(capability).IsSuccess);
        };
        kernel.BeforeComponentStartingPublicationQualificationHook = () =>
        {
            kernel.BeforeComponentStartingPublicationQualificationHook = null;
            if (!afterStart) revoke();
        };
        kernel.BeforeComponentRunningPublicationQualificationHook = () =>
        {
            kernel.BeforeComponentRunningPublicationQualificationHook = null;
            startup++;
            Assert.Equal(ProcessState.Running, kernel.Processes.Resolve(new(new ProcessId(1111), 1)).Value!.State);
            if (afterStart) revoke();
        };
        var result = kernel.AdmitComponent(new(new ServiceManifestV1(identity, new("1"), Digest(image),
            TestFixtures.Manifest(1111, 2111, capabilities: [requirement])), image,
            [new ComponentCapabilityGrant(new DomainId(2111), requirement)]));
        Assert.Equal(KernelError.MissingCapability, result.Error);
        Assert.Equal(afterStart ? 1 : 0, startup);
        var component = kernel.QueryComponent(identity).Value!;
        Assert.True(component.Reclaimable);
        Assert.Equal(result.Error, component.Failure);
        Assert.Equal(ProcessState.Exited, kernel.Processes.ResolveTerminalState(component.Process).Value);
        Assert.True(kernel.RetireReclaimableComponent(identity, component.Process).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RollbackSourceSnapshotSurvivesActualReclaimAndCannotRevokeReplacement(bool replace)
    {
        var kernel = new RuntimeKernel();
        byte[] image = [16, 17];
        var identity = new ComponentIdentity("source-snapshot");
        var old = new ProcessHandle(new ProcessId(1110), 1);
        var requirement = new CapabilityRequirementV1(ResourceKind.KernelService, "snapshot-data", CapabilityRights.Read);
        CapabilityId oldCapability = default;
        CapabilityId freshCapability = default;
        ProcessHandle fresh = default;
        kernel.BeforeComponentRollbackSourceStopQualificationHook = () =>
        {
            kernel.BeforeComponentRollbackSourceStopQualificationHook = null;
            oldCapability = Assert.Single(kernel.QueryComponent(identity).Value!.Capabilities);
            Assert.True(kernel.TerminateProcess(old).IsSuccess);
            Assert.True(kernel.ObserveComponentTeardown(identity).Value!.Reclaimable);
            if (replace)
            {
                Assert.True(kernel.RetireReclaimableComponent(identity, old).IsSuccess);
                var next = kernel.AdmitComponent(new(new ServiceManifestV1(identity, new("2"), Digest(image),
                    TestFixtures.Manifest(1110, 2110, generation: 2, capabilities: [requirement])), image,
                    [new ComponentCapabilityGrant(new DomainId(2110), requirement)])).Value!;
                fresh = next.Process;
                freshCapability = Assert.Single(next.Capabilities);
            }
        };
        var result = kernel.AdmitComponent(new(new ServiceManifestV1(identity, new("1"), Digest(image),
            TestFixtures.Manifest(1110, 2110, capabilities: [requirement]),
            requiredContracts: [new("MissingSnapshot", "1", "missing")]), image,
            [new ComponentCapabilityGrant(new DomainId(2110), requirement)]));
        Assert.Equal(KernelError.ServiceNotFound, result.Error);
        Assert.False(kernel.ValidateCapability(old, oldCapability, CapabilityRights.Read).IsSuccess);
        var current = kernel.QueryComponent(identity).Value!;
        if (replace)
        {
            Assert.Equal(fresh, current.Process);
            Assert.Equal(ComponentLifecycleState.Running, current.State);
            Assert.True(kernel.ValidateCapability(fresh, freshCapability, CapabilityRights.Read).IsSuccess);
            Assert.Null(current.Failure);
        }
        else
        {
            Assert.True(current.Reclaimable);
            Assert.True(kernel.RetireReclaimableComponent(identity, old).IsSuccess);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RollbackFinalTerminalReceiptClosesOldComponentWithoutTouchingNewProcess(bool faulted)
    {
        var kernel = new RuntimeKernel();
        byte[] image = [14, 15];
        var identity = new ComponentIdentity("terminal-final");
        var old = new ProcessHandle(new ProcessId(1109), 1);
        ProcessHandle fresh = default;
        var calls = 0;
        kernel.RegisterComposedWorkTeardownParticipant(new ComponentClosureCallback(() =>
        {
            calls++;
            return KernelResult.Ok();
        }));
        kernel.BeforeComponentRollbackCleanupQualificationHook = () =>
        {
            kernel.BeforeComponentRollbackCleanupQualificationHook = null;
            Assert.False(kernel.Processes.ResolveTerminalState(old).IsSuccess);
            Assert.True((faulted ? kernel.FaultProcess(old) : kernel.TerminateProcess(old)).IsSuccess);
            (_, fresh) = TestFixtures.Create(kernel, 1109, 2109, generation: 2);
            Assert.True(kernel.AdmitProcess(fresh).IsSuccess);
        };
        var result = kernel.AdmitComponent(new(new ServiceManifestV1(identity, new("1"), Digest(image),
            TestFixtures.Manifest(1109, 2109), requiredContracts: [new("MissingFinal", "1", "missing")]), image));
        Assert.Equal(KernelError.StaleHandle, result.Error);
        Assert.Equal(1, calls);
        var component = kernel.QueryComponent(identity).Value!;
        Assert.True(component.Reclaimable);
        Assert.Equal(result.Error, component.Failure);
        Assert.Equal(faulted ? ProcessState.Faulted : ProcessState.Exited, kernel.Processes.ResolveTerminalState(old).Value);
        Assert.Equal(ProcessState.Admitted, kernel.Processes.Resolve(fresh).Value!.State);
        Assert.False(kernel.Processes.ResolveTerminalState(fresh).IsSuccess);
        Assert.True(kernel.RetireReclaimableComponent(identity, old).IsSuccess);
        Assert.Equal(ProcessState.Admitted, kernel.Processes.Resolve(fresh).Value!.State);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RunningPublicationRequiresCurrentProcessStateAndRollbackClosesExactGeneration(bool terminate)
    {
        var kernel = new RuntimeKernel();
        byte[] image = [12, 13];
        var identity = new ComponentIdentity("running-publication");
        var process = new ProcessHandle(new ProcessId(1108), 1);
        kernel.BeforeComponentRunningPublicationQualificationHook = () =>
        {
            kernel.BeforeComponentRunningPublicationQualificationHook = null;
            Assert.Equal(ProcessState.Running, kernel.Processes.Resolve(process).Value!.State);
            Assert.True((terminate ? kernel.TerminateProcess(process) : kernel.ParkProcess(process)).IsSuccess);
        };
        var result = kernel.AdmitComponent(new(new ServiceManifestV1(identity, new("1"), Digest(image),
            TestFixtures.Manifest(1108, 2108)), image));
        Assert.False(result.IsSuccess);
        Assert.Equal(terminate ? KernelError.StaleHandle : KernelError.InvalidTransition, result.Error);
        var component = kernel.QueryComponent(identity).Value!;
        Assert.True(component.Reclaimable);
        Assert.Equal(ProcessState.Exited, kernel.Processes.ResolveTerminalState(process).Value);
        Assert.Equal(result.Error, component.Failure);
        Assert.True(kernel.RetireReclaimableComponent(identity, process).IsSuccess);
        Assert.True(kernel.AdmitComponent(new(new ServiceManifestV1(identity, new("2"), Digest(image),
            TestFixtures.Manifest(1108, 2108, generation: 2)), image)).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RollbackFailureMetadataRequiresExactRecordAndRetainsOriginalResult(bool replace)
    {
        var kernel = new RuntimeKernel();
        byte[] image = [10, 11];
        var identity = new ComponentIdentity("rollback-publication");
        var oldProcess = new ProcessHandle(new ProcessId(1107), 1);
        var refuse = true;
        var calls = 0;
        kernel.RegisterComposedWorkTeardownParticipant(new ComponentClosureCallback(() =>
        {
            calls++;
            return refuse ? KernelResult.Fail(KernelError.PlatformFaulted, "rollback closure pending") : KernelResult.Ok();
        }));
        kernel.BeforeComponentRollbackPublicationQualificationHook = () =>
        {
            kernel.BeforeComponentRollbackPublicationQualificationHook = null;
            refuse = false;
            Assert.True(kernel.ObserveComponentTeardown(identity).Value!.Reclaimable);
            if (replace)
            {
                Assert.True(kernel.RetireReclaimableComponent(identity, oldProcess).IsSuccess);
                Assert.True(kernel.AdmitComponent(new(new ServiceManifestV1(identity, new("2"), Digest(image),
                    TestFixtures.Manifest(1107, 2107, generation: 2)), image)).IsSuccess);
            }
        };
        var result = kernel.AdmitComponent(new(new ServiceManifestV1(identity, new("1"), Digest(image),
            TestFixtures.Manifest(1107, 2107), requiredContracts: [new("MissingRollback", "1", "missing")]), image));
        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(2, calls);
        var after = kernel.QueryComponent(identity).Value!;
        if (replace)
        {
            Assert.Equal(2UL, after.Process.Generation);
            Assert.Equal(ComponentLifecycleState.Running, after.State);
            Assert.Null(after.Failure);
        }
        else
        {
            Assert.True(after.Reclaimable);
            Assert.Equal(KernelError.PlatformFaulted, after.Failure);
            Assert.True(kernel.RetireReclaimableComponent(identity, oldProcess).IsSuccess);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LateDirectFailureCannotUndoActualReclaimOrReplacement(bool replace)
    {
        var kernel = new RuntimeKernel();
        byte[] image = [8, 9];
        var identity = new ComponentIdentity("late-direct-failure");
        var old = kernel.AdmitComponent(new(new ServiceManifestV1(identity, new("1"), Digest(image),
            TestFixtures.Manifest(1106, 2106)), image)).Value!;
        var refuse = true;
        var calls = 0;
        kernel.RegisterComposedWorkTeardownParticipant(new ComponentClosureCallback(() =>
        {
            calls++;
            return refuse ? KernelResult.Fail(KernelError.PlatformFaulted, "initial failure") : KernelResult.Ok();
        }));
        kernel.BeforeComponentFailurePublicationQualificationHook = () =>
        {
            kernel.BeforeComponentFailurePublicationQualificationHook = null;
            refuse = false;
            Assert.True(kernel.ObserveComponentTeardown(identity).Value!.Reclaimable);
            if (replace)
            {
                Assert.True(kernel.RetireReclaimableComponent(identity, old.Process).IsSuccess);
                Assert.True(kernel.AdmitComponent(new(new ServiceManifestV1(identity, new("2"), Digest(image),
                    TestFixtures.Manifest(1106, 2106, generation: 2)), image)).IsSuccess);
            }
        };
        Assert.Equal(KernelError.PlatformFaulted, kernel.FaultComponent(identity).Error);
        Assert.Equal(2, calls);
        var after = kernel.QueryComponent(identity).Value!;
        if (replace)
        {
            Assert.Equal(2UL, after.Process.Generation);
            Assert.Equal(ComponentLifecycleState.Running, after.State);
            Assert.True(kernel.Budgets.HasProcessAccount(after.Process));
        }
        else
        {
            Assert.True(after.Reclaimable);
            Assert.True(kernel.RetireReclaimableComponent(identity, old.Process).IsSuccess);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LateBlockedTeardownPublicationCannotUndoReclaimOrReplacement(bool replace)
    {
        var kernel = new RuntimeKernel();
        byte[] image = [6, 7];
        var identity = new ComponentIdentity("late-teardown");
        var plan = new ComponentAdmissionPlan(new ServiceManifestV1(identity, new("1"), Digest(image),
            TestFixtures.Manifest(1105, 2105)), image);
        var old = kernel.AdmitComponent(plan).Value!;
        var refuse = true;
        var calls = 0;
        kernel.RegisterComposedWorkTeardownParticipant(new ComponentClosureCallback(() =>
        {
            calls++;
            return refuse ? KernelResult.Fail(KernelError.PlatformFaulted, "pending closure") : KernelResult.Ok();
        }));
        Assert.Equal(KernelError.PlatformFaulted, kernel.FaultComponent(identity).Error);
        kernel.BeforeComponentTeardownPublicationQualificationHook = () =>
        {
            kernel.BeforeComponentTeardownPublicationQualificationHook = null;
            refuse = false;
            Assert.True(kernel.ObserveComponentTeardown(identity).Value!.Reclaimable);
            if (replace)
            {
                Assert.True(kernel.RetireReclaimableComponent(identity, old.Process).IsSuccess);
                Assert.True(kernel.AdmitComponent(new(new ServiceManifestV1(identity, new("2"), Digest(image),
                    TestFixtures.Manifest(1105, 2105, generation: 2)), image)).IsSuccess);
            }
        };
        Assert.True(kernel.ObserveComponentTeardown(identity).IsSuccess);
        Assert.Equal(3, calls);
        var after = kernel.QueryComponent(identity).Value!;
        if (replace)
        {
            Assert.Equal(2UL, after.Process.Generation);
            Assert.Equal(ComponentLifecycleState.Running, after.State);
            Assert.True(kernel.Budgets.HasProcessAccount(after.Process));
            Assert.False(kernel.Budgets.HasProcessAccount(old.Process));
        }
        else
        {
            Assert.True(after.Reclaimable);
            Assert.True(kernel.RetireReclaimableComponent(identity, old.Process).IsSuccess);
            Assert.Equal(KernelError.ComponentNotFound, kernel.QueryComponent(identity).Error);
        }
    }

    [Fact]
    public async Task ConcurrentComponentAdmissionRefusesDuplicateBeforeHierarchyAllocation()
    {
        var kernel = new RuntimeKernel();
        byte[] image = [4, 5];
        var plans = new[] { 1103UL, 1104UL }.Select(id => new ComponentAdmissionPlan(
            new ServiceManifestV1(new("concurrent-admission"), new("1"), Digest(image),
                TestFixtures.Manifest(id, id + 1000)), image)).ToArray();
        using var barrier = new Barrier(2);
        kernel.BeforeComponentAdmissionCommitQualificationHook = () =>
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
        var results = await Task.WhenAll(plans.Select(plan => Task.Run(() => kernel.AdmitComponent(plan))))
            .WaitAsync(TimeSpan.FromSeconds(20));
        kernel.BeforeComponentAdmissionCommitQualificationHook = null;
        var winner = Assert.Single(results, result => result.IsSuccess).Value!;
        Assert.Equal(KernelError.DuplicateIdentity, Assert.Single(results, result => !result.IsSuccess).Error);
        var loser = plans.Select(plan => new ProcessHandle(plan.Manifest.Process.ProcessId, plan.Manifest.Process.Generation))
            .Single(process => process != winner.Process);
        Assert.False(kernel.Budgets.HasProcessAccount(loser));
        Assert.False(kernel.Processes.Resolve(loser).IsSuccess);
        Assert.Equal(winner.Process, kernel.QueryComponent(new("concurrent-admission")).Value!.Process);
        Assert.True(kernel.DrainComponent(new("concurrent-admission"), winner.Process).Value!.Reclaimable);
        Assert.True(kernel.RetireReclaimableComponent(new("concurrent-admission"), winner.Process).IsSuccess);
        Assert.True(kernel.AdmitComponent(plans.Single(plan => plan.Manifest.Process.ProcessId == loser.ProcessId)).IsSuccess);
    }

    [Theory]
    [InlineData(0, false)] [InlineData(0, true)]
    [InlineData(1, false)] [InlineData(1, true)]
    [InlineData(2, false)] [InlineData(2, true)]
    public void ComponentObserveRetriesExactComposedPreparationTarget(int entry, bool throws)
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        byte[] image = [2, 3];
        var manifest = new ServiceManifestV1(new("composed-retry"), new("1"), Digest(image),
            TestFixtures.Manifest(1020, 2020),
            requiredContracts: entry == 0 ? [new ServiceContractIdentity("absent", "1", "absent-digest")] : [],
            requiresPlatformDomain: true);
        var process = new ProcessHandle(manifest.Process.ProcessId, manifest.Process.Generation);
        var target = entry == 2 ? ProcessState.Faulted : ProcessState.Exited;
        var calls = 0;
        kernel.RegisterComposedWorkTeardownParticipant(new ComponentClosureCallback(() =>
        {
            Assert.Equal(target, kernel.QueryProcessTeardown(process).Value!.TargetTerminalState);
            if (++calls != 1) return KernelResult.Ok();
            var overlapping = kernel.ObserveComponentTeardown(manifest.Identity);
            Assert.True(overlapping.IsSuccess);
            Assert.False(overlapping.Value!.Reclaimable);
            Assert.True(kernel.Processes.Resolve(process).IsSuccess);
            Assert.Equal(1, calls);
            if (throws) throw new InvalidOperationException("Closure receipt unavailable.");
            return KernelResult.Fail(KernelError.PlatformBindingDraining, "Existing participant is draining.");
        }));

        var admitted = kernel.AdmitComponent(new(manifest, image));
        if (entry != 0)
        {
            Assert.True(admitted.IsSuccess);
            _ = entry == 2 ? kernel.FaultComponent(manifest.Identity) : kernel.DrainComponent(manifest.Identity);
        }
        else Assert.False(admitted.IsSuccess);
        Assert.Equal(1, calls);
        Assert.False(kernel.QueryComponent(manifest.Identity).Value!.Reclaimable);
        Assert.Equal(0, provider.RevokeDomainCallCount);
        var observed = kernel.ObserveComponentTeardown(manifest.Identity);
        Assert.True(observed.IsSuccess);
        Assert.True(observed.Value!.Reclaimable);
        Assert.Equal(2, calls);
        Assert.Equal(1, provider.RevokeDomainCallCount);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(process).Error);
        Assert.Null(observed.Value.DeviceResources);
        Assert.False(observed.Value.PlatformAuthorityLive);

        var (_, next) = TestFixtures.Create(kernel, 1020, 2020, 2);
        Assert.True(kernel.ObserveComponentTeardown(manifest.Identity).Value!.Reclaimable);
        Assert.True(kernel.Processes.Resolve(next).IsSuccess);
        Assert.Equal(2, calls);
        Assert.Equal(1, provider.RevokeDomainCallCount);
    }

    [Fact]
    public async Task ConcurrentComponentObserveCannotCloseWhileRetryCallbackIsInFlight()
    {
        var kernel = new RuntimeKernel(new HostPlatformAuthorityProvider());
        byte[] image = [4];
        var manifest = new ServiceManifestV1(new("composed-overlap"), new("1"), Digest(image),
            TestFixtures.Manifest(1021, 2021), requiresPlatformDomain: true);
        var component = kernel.AdmitComponent(new(manifest, image)).Value!;
        using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        kernel.RegisterComposedWorkTeardownParticipant(new ComponentClosureCallback(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Initial preparation refused.");
            entered.SetResult();
            return resume.Wait(TimeSpan.FromSeconds(10)) ? KernelResult.Ok() :
                KernelResult.Fail(KernelError.PlatformFaulted, "Test rendezvous expired.");
        }));
        Assert.Equal(KernelError.PlatformFaulted, kernel.FaultComponent(manifest.Identity).Error);
        var retry = Task.Run(() => kernel.ObserveComponentTeardown(manifest.Identity));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var overlapping = kernel.ObserveComponentTeardown(manifest.Identity);
            Assert.True(overlapping.IsSuccess);
            Assert.False(overlapping.Value!.Reclaimable);
            Assert.True(kernel.Processes.Resolve(component.Process).IsSuccess);
            Assert.Equal(ProcessState.Faulted, kernel.QueryProcessTeardown(component.Process).Value!.TargetTerminalState);
            Assert.Equal(2, Volatile.Read(ref calls));
        }
        finally { resume.Set(); }
        Assert.True((await retry.WaitAsync(TimeSpan.FromSeconds(10))).Value!.Reclaimable);
        Assert.Equal(2, calls);
    }

    private sealed class ComponentClosureCallback(Func<KernelResult> close) : IComposedWorkTeardownParticipant
    {
        public KernelResult CloseComposedWorkForProcess(ProcessHandle process, RegionOwner owner) => close();
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ProcessCreationPreflightRefusesBeforeComponentHierarchyAllocation(int fault)
    {
        var kernel = new RuntimeKernel();
        byte[] image = [1, 2, 3];
        var process = TestFixtures.Manifest(1010, 2010, identity: "preflight-process");
        if (fault == 0) TestFixtures.Create(kernel, 1010, 2010, identity: "different-entry");
        if (fault == 1) TestFixtures.Create(kernel, 1011, 2011, identity: "preflight-process");
        if (fault == 2)
        {
            var (_, newer) = TestFixtures.Create(kernel, 1010, 2010, 2);
            Assert.True(kernel.TerminateProcess(newer).IsSuccess);
        }
        var manifest = new ServiceManifestV1(new("pre-process-refusal"), new("1"), Digest(image), process);
        var refused = kernel.AdmitComponent(new(manifest, image));
        Assert.Equal(fault switch { 0 => KernelError.DuplicateProcessId, 1 => KernelError.DuplicateIdentity,
            _ => KernelError.StaleGeneration }, refused.Error);
        Assert.Equal(KernelError.ComponentNotFound, kernel.QueryComponent(manifest.Identity).Error);
        Assert.False(kernel.Budgets.HasProcessAccount(new(process.ProcessId, process.Generation)));
        var accounts = (global::System.Collections.IDictionary)typeof(ResourceBudgetAuthority).GetField("_accounts",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(kernel.Budgets)!;
        Assert.Single(accounts);
    }

    [Fact]
    public async Task ProcessCreationRacePreservesForeignProcessAndFaultsComponentWithoutClosureClaim()
    {
        var kernel = new RuntimeKernel();
        byte[] image = [3, 2, 1];
        var process = TestFixtures.Manifest(1012, 2012, identity: "raced-process");
        var handle = new ProcessHandle(process.ProcessId, process.Generation);
        var manifest = new ServiceManifestV1(new("raced-component"), new("1"), Digest(image), process);
        var gate = typeof(ResourceBudgetAuthority).GetField("_gate", global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(kernel.Budgets)!;
        var completion = new TaskCompletionSource<KernelResult<ComponentLifecycleSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try { completion.SetResult(kernel.AdmitComponent(new(manifest, image))); }
            catch (Exception exception) { completion.SetException(exception); }
        }) { IsBackground = true };
        lock (gate)
        {
            worker.Start();
            Assert.True(SpinWait.SpinUntil(() => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(10)));
            Assert.False(completion.Task.IsCompleted);
            Assert.True(kernel.CreateProcess(process).IsSuccess);
        }
        Assert.Equal(KernelError.DuplicateProcessId, (await completion.Task.WaitAsync(TimeSpan.FromSeconds(10))).Error);
        var component = kernel.QueryComponent(manifest.Identity).Value!;
        Assert.Equal(ComponentLifecycleState.Faulted, component.State);
        Assert.False(component.Reclaimable);
        Assert.True(kernel.Processes.Resolve(handle).IsSuccess);
        Assert.True(kernel.Budgets.HasProcessAccount(handle));
        Assert.True(kernel.AllocateBuffer<byte>(handle, 4).IsSuccess);
        Assert.Equal(KernelError.ReplacementBlocked, kernel.RetireReclaimableComponent(manifest.Identity, handle).Error);
        Assert.False(kernel.FaultComponent(manifest.Identity).IsSuccess);
        Assert.True(kernel.Processes.Resolve(handle).IsSuccess);
        Assert.True(kernel.QueryBudget(component.ProcessBudget).IsSuccess);
        Assert.Equal(4ul, Assert.Single(kernel.QueryBudget(component.ProcessBudget).Value!.Usage,
            usage => usage.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Fact]
    public void ExistingComputeServiceContractLaunchesAsDeclarativeComponent()
    {
        var kernel = new RuntimeKernel(new HostPlatformAuthorityProvider());
        var image = new byte[] { 0x53, 0x49, 0x50 };
        var protocol = IComputeServiceProtocol.CreateDefinition();
        var contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
        var provided = new ProvidedServiceManifestV1("compute", contract);
        var manifest = new ServiceManifestV1(new ComponentIdentity("compute-service"), new ComponentVersion("1"), Digest(image), TestFixtures.Manifest(11, 110), [provided], requiresPlatformDomain: true);
        var result = kernel.AdmitComponent(new ComponentAdmissionPlan(manifest, image, providedServices: [new ComponentProvidedServiceRegistration(provided, protocol, IComputeServiceResponseProtocol.Definition, [new CapabilityRequirementV1(ResourceKind.Compute, CapabilityResourceIds.Dsc1Copy, CapabilityRights.Execute)])]));

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(ComponentLifecycleState.Running, result.Value!.State);
        Assert.True(result.Value.PlatformAuthorityLive);
        Assert.Equal(contract, Assert.Single(result.Value.ProvidedContracts));
        Assert.Contains(result.Value.ResourceRequirements, requirement => requirement.Kind == ComponentResourceRequirementKind.PlatformAuthorityDomain);
        Assert.Equal(provided.ServiceName, kernel.ResolveByServiceName("compute").Value.Service.Name);
    }

    [Fact]
    public void DeclarativeComponentLaunchesAndReportsDistinctAuthorityAssociations()
    {
        var kernel = new RuntimeKernel();
        _ = TestFixtures.Create(kernel, 90, 900);
        var image = new byte[] { 1, 2, 3 };
        var requirement = new CapabilityRequirementV1(ResourceKind.KernelService, "clock", CapabilityRights.Read);
        var process = TestFixtures.Manifest(1, 10, capabilities: [requirement]);
        var serviceContract = new ServiceContractIdentity("Clock", "1", "clock-digest");
        var provided = new ProvidedServiceManifestV1("clock", serviceContract);
        var manifest = new ServiceManifestV1(new ComponentIdentity("clock-component"), new ComponentVersion("1.0.0"), Digest(image), process, [provided]);
        var protocol = Protocol("Clock", "clock-digest");
        var plan = new ComponentAdmissionPlan(manifest, image, [new ComponentCapabilityGrant(new DomainId(900), requirement)], [new ComponentProvidedServiceRegistration(provided, protocol)]);

        var admitted = kernel.AdmitComponent(plan);

        Assert.True(admitted.IsSuccess, admitted.Message);
        Assert.Equal(ComponentLifecycleState.Running, admitted.Value!.State);
        Assert.Equal(new ProcessHandle(new ProcessId(1), 1), admitted.Value.Process);
        Assert.Equal(new DomainId(10), admitted.Value.Domain);
        Assert.Single(admitted.Value.Capabilities);
        var resource = Assert.Single(admitted.Value.ResourceRequirements);
        Assert.Equal(ComponentResourceRequirementV1.ForCapability(requirement), resource);
        Assert.Single(kernel.ResolveByContract(serviceContract).Value!);
    }

    [Fact]
    public void ImageMismatchFailsBeforeProcessOrAuthorityCreation()
    {
        var kernel = new RuntimeKernel();
        var manifest = new ServiceManifestV1(new ComponentIdentity("bad-image"), new ComponentVersion("1"), Digest([1]), TestFixtures.Manifest(2, 20));
        var result = kernel.AdmitComponent(new ComponentAdmissionPlan(manifest, new byte[] { 2 }));
        Assert.Equal(KernelError.ComponentDigestMismatch, result.Error);
        Assert.Equal(KernelError.ProcessNotFound, kernel.Processes.Resolve(new ProcessHandle(new ProcessId(2), 1)).Error);
    }

    [Fact]
    public void MissingCapabilityResourceFailsBeforeProcessCreation()
    {
        var kernel = new RuntimeKernel();
        var image = new byte[] { 4 };
        var requirement = new CapabilityRequirementV1(ResourceKind.KernelService, "missing", CapabilityRights.Read);
        var process = TestFixtures.Manifest(3, 30, capabilities: [requirement]);
        var contract = new ServiceContractIdentity("Missing", "1", "missing-digest");
        var provided = new ProvidedServiceManifestV1("missing", contract);
        var manifest = new ServiceManifestV1(new ComponentIdentity("missing-cap"), new ComponentVersion("1"), Digest(image), process, [provided]);
        var result = kernel.AdmitComponent(new ComponentAdmissionPlan(manifest, image, providedServices: [new ComponentProvidedServiceRegistration(provided, Protocol("Missing", "missing-digest"))]));

        Assert.Equal(KernelError.MissingCapability, result.Error);
        Assert.Empty(kernel.ResolveByContract(contract).Value!);
        Assert.Equal(KernelError.ComponentNotFound, kernel.QueryComponent(manifest.Identity).Error);
        Assert.Equal(KernelError.ProcessNotFound, kernel.Processes.Resolve(new ProcessHandle(new ProcessId(3), 1)).Error);
    }

    [Fact]
    public void UndeclaredCapabilityGrantFailsBeforeProcessCreation()
    {
        var kernel = new RuntimeKernel();
        var image = new byte[] { 4, 1 };
        var grant = new CapabilityRequirementV1(ResourceKind.Device, "undeclared-device", CapabilityRights.Read);
        var manifest = new ServiceManifestV1(new ComponentIdentity("extra-grant"), new ComponentVersion("1"), Digest(image), TestFixtures.Manifest(31, 310));

        var result = kernel.AdmitComponent(new ComponentAdmissionPlan(
            manifest,
            image,
            [new ComponentCapabilityGrant(new DomainId(999), grant)]));

        Assert.Equal(KernelError.InvalidManifest, result.Error);
        Assert.Equal(KernelError.ComponentNotFound, kernel.QueryComponent(manifest.Identity).Error);
        Assert.Equal(KernelError.ProcessNotFound, kernel.Processes.Resolve(new ProcessHandle(new ProcessId(31), 1)).Error);
    }

    [Fact]
    public void BroaderCapabilityGrantThanManifestFailsBeforeProcessCreation()
    {
        var kernel = new RuntimeKernel();
        var image = new byte[] { 4, 2 };
        var required = new CapabilityRequirementV1(ResourceKind.Device, "sensor", CapabilityRights.Read);
        var broader = required with { Rights = CapabilityRights.Read | CapabilityRights.Configure };
        var manifest = new ServiceManifestV1(
            new ComponentIdentity("broad-grant"),
            new ComponentVersion("1"),
            Digest(image),
            TestFixtures.Manifest(32, 320, capabilities: [required]));

        var result = kernel.AdmitComponent(new ComponentAdmissionPlan(
            manifest,
            image,
            [new ComponentCapabilityGrant(new DomainId(999), broader)]));

        Assert.Equal(KernelError.InvalidManifest, result.Error);
        Assert.Equal(KernelError.ProcessNotFound, kernel.Processes.Resolve(new ProcessHandle(new ProcessId(32), 1)).Error);
    }

    [Fact]
    public void RequiredContractSessionDenialUnwindsPublishedServicesAndProcess()
    {
        var kernel = new RuntimeKernel();
        var provider = TestFixtures.Create(kernel, 80, 800).Handle;
        Assert.True(kernel.AdmitProcess(provider).IsSuccess);
        var required = new ServiceContractIdentity("Dependency", "1", "dependency-digest");
        Assert.True(kernel.RegisterService(provider, "dependency", required, Protocol("Dependency", "dependency-digest"), requiredCapabilities: [new CapabilityRequirementV1(ResourceKind.KernelService, "dependency", CapabilityRights.Read)]).IsSuccess);

        var image = new byte[] { 5 };
        var ownContract = new ServiceContractIdentity("Consumer", "1", "consumer-digest");
        var own = new ProvidedServiceManifestV1("consumer", ownContract);
        var manifest = new ServiceManifestV1(new ComponentIdentity("consumer"), new ComponentVersion("1"), Digest(image), TestFixtures.Manifest(4, 40), [own], [required]);
        var result = kernel.AdmitComponent(new ComponentAdmissionPlan(manifest, image, providedServices: [new ComponentProvidedServiceRegistration(own, Protocol("Consumer", "consumer-digest"))]));

        Assert.Equal(KernelError.MissingCapability, result.Error);
        Assert.Empty(kernel.ResolveByContract(ownContract).Value!);
        Assert.Equal(ComponentLifecycleState.Reclaimable, kernel.QueryComponent(manifest.Identity).Value!.State);
    }

    [Fact]
    public void MissingPlatformFeatureFailsBeforeProcessCreation()
    {
        var image = new byte[] { 6 };
        var manifest = new ServiceManifestV1(new ComponentIdentity("platform-required"), new ComponentVersion("1"), Digest(image), TestFixtures.Manifest(6, 60), requiresPlatformDomain: true);
        var kernel = new RuntimeKernel();
        var result = kernel.AdmitComponent(new ComponentAdmissionPlan(manifest, image));
        Assert.Equal(KernelError.PlatformUnsupported, result.Error);
        Assert.Equal(KernelError.ComponentNotFound, kernel.QueryComponent(manifest.Identity).Error);
        Assert.Equal(KernelError.ProcessNotFound, kernel.Processes.Resolve(new ProcessHandle(new ProcessId(6), 1)).Error);
    }

    [Fact]
    public void FailureAfterPlatformBindRevokesBindingBeforeLocalReclaim()
    {
        var image = new byte[] { 7 };
        var missing = new ServiceContractIdentity("Absent", "1", "absent-digest");
        var manifest = new ServiceManifestV1(new ComponentIdentity("platform-rollback"), new ComponentVersion("1"), Digest(image), TestFixtures.Manifest(7, 70), requiredContracts: [missing], requiresPlatformDomain: true);
        var kernel = new RuntimeKernel(new HostPlatformAuthorityProvider());
        var result = kernel.AdmitComponent(new ComponentAdmissionPlan(manifest, image));
        var snapshot = kernel.QueryComponent(manifest.Identity).Value!;
        Assert.Equal(KernelError.ServiceNotFound, result.Error);
        Assert.False(snapshot.PlatformAuthorityLive);
        Assert.True(snapshot.Reclaimable);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(snapshot.Process).Error);
    }

    [Fact]
    public void PlatformFeatureRequirementAloneDoesNotMaterializeOrdinaryDomain()
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        var image = new byte[] { 7, 1 };
        var manifest = new ServiceManifestV1(
            new ComponentIdentity("feature-only"),
            new ComponentVersion("1"),
            Digest(image),
            TestFixtures.Manifest(71, 710),
            platformRequirements:
            [
                new PlatformRequirementV1(
                    ComponentPlatformFeatureFamily.ExecutionPolicy,
                    PlatformExecutionPolicyContract.ContractVersion,
                    ComponentPlatformFeatureAvailability.ModelOnly)
            ]);

        var result = kernel.AdmitComponent(new ComponentAdmissionPlan(manifest, image));

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(0, provider.BindDomainCallCount);
        Assert.False(result.Value!.PlatformAuthorityLive);
        Assert.Empty(result.Value.ResourceRequirements);
    }

    [Fact]
    public void ExplicitPlatformAuthorityResourceBindsExactlyOnce()
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        var image = new byte[] { 7, 2 };
        var manifest = new ServiceManifestV1(
            new ComponentIdentity("domain-resource"),
            new ComponentVersion("1"),
            Digest(image),
            TestFixtures.Manifest(72, 720),
            platformRequirements:
            [
                new PlatformRequirementV1(
                    ComponentPlatformFeatureFamily.NeutralDomains,
                    PlatformDomainContract.ContractVersion,
                    ComponentPlatformFeatureAvailability.RuntimeAdmission)
            ],
            resourceRequirements: [ComponentResourceRequirementV1.ForPlatformAuthorityDomain()]);

        var result = kernel.AdmitComponent(new ComponentAdmissionPlan(manifest, image));

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, provider.BindDomainCallCount);
        Assert.True(result.Value!.PlatformAuthorityLive);
        Assert.Equal(ComponentResourceRequirementKind.PlatformAuthorityDomain, Assert.Single(result.Value.ResourceRequirements).Kind);
    }

    [Fact]
    public void PlatformAuthorityResourceRequiresNeutralDomainsFeatureRequirement()
    {
        var image = new byte[] { 7, 3 };
        Assert.Throws<ArgumentException>(() => new ServiceManifestV1(
            new ComponentIdentity("invalid-domain-resource"),
            new ComponentVersion("1"),
            Digest(image),
            TestFixtures.Manifest(73, 730),
            resourceRequirements: [ComponentResourceRequirementV1.ForPlatformAuthorityDomain()]));
    }

    [Fact]
    public void ExplicitLocalResourcesCannotBroadenOrDivergeFromProcessManifest()
    {
        var image = new byte[] { 7, 4 };
        var processRequirement = new CapabilityRequirementV1(ResourceKind.Device, "sensor", CapabilityRights.Read);
        var broader = ComponentResourceRequirementV1.ForCapability(processRequirement with { Rights = CapabilityRights.Read | CapabilityRights.Configure });

        Assert.Throws<ArgumentException>(() => new ServiceManifestV1(
            new ComponentIdentity("divergent-resource"),
            new ComponentVersion("1"),
            Digest(image),
            TestFixtures.Manifest(74, 740, capabilities: [processRequirement]),
            resourceRequirements: [broader]));
    }

    [Fact]
    public void UnknownResourceRequirementKindIsRejected()
    {
        var image = new byte[] { 7, 5 };
        var unknown = new ComponentResourceRequirementV1((ComponentResourceRequirementKind)99, null, null, CapabilityRights.None);

        Assert.Throws<ArgumentException>(() => new ServiceManifestV1(
            new ComponentIdentity("unknown-resource"),
            new ComponentVersion("1"),
            Digest(image),
            TestFixtures.Manifest(75, 750),
            resourceRequirements: [unknown]));
    }

    [Fact]
    public void ComponentManifestCannotCarryProviderAuthorityVocabulary()
    {
        var names = typeof(ServiceManifestV1).GetProperties().Select(x => x.Name)
            .Concat(typeof(ComponentResourceRequirementV1).GetProperties().Select(x => x.Name))
            .ToArray();
        foreach (var forbidden in new[] { "ProviderLease", "PhysicalAddress", "PageTableRoot", "Iommu", "Vmcs", "Vmx", "Lane", "Opcode" })
            Assert.DoesNotContain(names, name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TypedPlatformRequirementIsCheckedBeforeProcessOrProviderAuthorityCreation()
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        var image = new byte[] { 8 };
        var manifest = new ServiceManifestV1(
            new ComponentIdentity("needs-executable-vm"),
            new ComponentVersion("1"),
            Digest(image),
            TestFixtures.Manifest(8, 80),
            platformRequirements:
            [
                new PlatformRequirementV1(
                    ComponentPlatformFeatureFamily.VirtualizationDomains,
                    PlatformVirtualizationContract.ContractVersion,
                    ComponentPlatformFeatureAvailability.Executable)
            ]);

        var result = kernel.AdmitComponent(new ComponentAdmissionPlan(manifest, image));

        Assert.Equal(KernelError.PlatformUnsupported, result.Error);
        Assert.Equal(0, provider.BindDomainCallCount);
        Assert.Equal(KernelError.ProcessNotFound,
            kernel.Processes.Resolve(new ProcessHandle(new ProcessId(8), 1)).Error);
    }

    private static ProtocolDefinitionV1 Protocol(string name, string digest) =>
        new(name, digest, "Idle", ["Done"], [new ProtocolMessageDescriptorV1(1, "Invoke")], [new ProtocolTransitionV1(1, "Idle", "Done")]);

    private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
