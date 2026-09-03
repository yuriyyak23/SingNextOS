using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    public RuntimeKernel()
        : this(null)
    {
    }

    public RuntimeKernel(IPlatformAuthorityProvider? platformProvider)
    {
        Processes = new ProcessRegistry();
        Domains = new DomainRegistry();
        CapabilityAuthority = new CapabilityAuthority();
        Regions = new RegionAuthority();
        Channels = new ChannelRegistry(CapabilityAuthority, Regions);
        PlatformAuthority = new PlatformAuthorityBridge(platformProvider);
    }

    public ProcessRegistry Processes { get; }
    public DomainRegistry Domains { get; }
    public CapabilityAuthority CapabilityAuthority { get; }
    public RegionAuthority Regions { get; }
    public ChannelRegistry Channels { get; }
    public PlatformAuthorityBridge PlatformAuthority { get; }

    public KernelResult<SingProcess> CreateProcess(SingProcessManifestV1 manifest)
    {
        if (manifest is null) return KernelResult<SingProcess>.Fail(KernelError.InvalidManifest, "Manifest is required.");
        var result = Processes.Create(manifest);
        if (!result.IsSuccess) return result;
        Domains.Add(result.Value!);
        return result;
    }

    public KernelResult AdmitProcess(ProcessHandle handle)
    {
        var resolved = Processes.Resolve(handle);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        var process = resolved.Value!;
        if (process.State != ProcessState.Created) return InvalidTransition(process.State, ProcessState.Admitted);
        foreach (var requirement in process.Manifest.RequiredCapabilities)
        {
            var satisfied = CapabilityAuthority.SnapshotForDomain(process.DomainId).Any(c => c.Generation == process.Generation && c.ResourceKind == requirement.ResourceKind && string.Equals(c.ResourceId, requirement.ResourceId, StringComparison.Ordinal) && (c.Rights & requirement.Rights) == requirement.Rights);
            if (!satisfied) return KernelResult.Fail(KernelError.MissingCapability, $"Missing capability {requirement.ResourceKind}:{requirement.ResourceId} ({requirement.Rights}).");
        }
        process.SetState(ProcessState.Admitted);
        return KernelResult.Ok();
    }

    public KernelResult StartProcess(ProcessHandle handle)
    {
        var resolved = Processes.Resolve(handle);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        var process = resolved.Value!;
        if (process.State != ProcessState.Admitted) return InvalidTransition(process.State, ProcessState.Runnable);

        var platform = TransitionPlatformExecutionIfBound(
            handle,
            process,
            PlatformDomainExecutionTransition.Start);
        if (!platform.IsSuccess) return platform;

        process.SetState(ProcessState.Runnable);
        process.SetState(ProcessState.Running);
        return KernelResult.Ok();
    }

    public KernelResult ParkProcess(ProcessHandle handle) =>
        TransitionProcessExecution(
            handle,
            ProcessState.Running,
            ProcessState.Parked,
            PlatformDomainExecutionTransition.Park);

    public KernelResult ResumeProcess(ProcessHandle handle) =>
        TransitionProcessExecution(
            handle,
            ProcessState.Parked,
            ProcessState.Running,
            PlatformDomainExecutionTransition.Resume);

    public KernelResult TerminateProcess(ProcessHandle handle) =>
        BeginOrAdvanceProcessTeardown(handle, ProcessState.Exited);

    public KernelResult FaultProcess(ProcessHandle handle) =>
        BeginOrAdvanceProcessTeardown(handle, ProcessState.Faulted);

    public KernelResult<CapabilityDescriptorV1> MintCapability(DomainId issuerDomain, ProcessHandle subject, ResourceKind resourceKind, string resourceId, CapabilityRights rights)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(resolved.Error, resolved.Message!);
        var effect = EnsureProcessAcceptsNewEffects(resolved.Value!);
        if (!effect.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(effect.Error, effect.Message!);
        if (!Domains.Contains(issuerDomain)) return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.DomainNotFound, $"Issuer domain {issuerDomain} is not active.");
        var descriptor = CapabilityAuthority.Mint(issuerDomain, resolved.Value!.DomainId, resourceKind, resourceId, rights, subject.Generation);
        resolved.Value.AddCapability(descriptor.CapabilityId);
        return KernelResult<CapabilityDescriptorV1>.Ok(descriptor);
    }

    public KernelResult<CapabilityDescriptorV1> DelegateCapability(ProcessHandle delegator, ProcessHandle target, CapabilityId sourceCapability, CapabilityRights rights)
    {
        var sourceProcess = Processes.Resolve(delegator);
        if (!sourceProcess.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(sourceProcess.Error, sourceProcess.Message!);
        var sourceEffect = EnsureProcessAcceptsNewEffects(sourceProcess.Value!);
        if (!sourceEffect.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(sourceEffect.Error, sourceEffect.Message!);

        var targetProcess = Processes.Resolve(target);
        if (!targetProcess.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(targetProcess.Error, targetProcess.Message!);
        var targetEffect = EnsureProcessAcceptsNewEffects(targetProcess.Value!);
        if (!targetEffect.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(targetEffect.Error, targetEffect.Message!);

        var delegated = CapabilityAuthority.Delegate(sourceCapability, sourceProcess.Value!.DomainId, targetProcess.Value!.DomainId, rights, target.Generation);
        if (delegated.IsSuccess) targetProcess.Value!.AddCapability(delegated.Value!.CapabilityId);
        return delegated;
    }

    public KernelResult<CapabilityDescriptorV1> ValidateCapability(ProcessHandle subject, CapabilityId capabilityId, CapabilityRights rights)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(resolved.Error, resolved.Message!);
        return CapabilityAuthority.Validate(capabilityId, resolved.Value!.DomainId, subject.Generation, rights);
    }

    public KernelResult RevokeCapability(CapabilityId capabilityId)
    {
        var result = CapabilityAuthority.Revoke(capabilityId);
        if (!result.IsSuccess) return result;

        foreach (var process in Processes.Snapshot())
            process.RemoveCapability(capabilityId);

        var irqCascade = CascadePlatformIrqCapabilityRevocation(capabilityId);
        var mmioCascade = CascadePlatformMmioCapabilityRevocation(capabilityId);
        var deviceCascade = CascadePlatformDeviceCapabilityRevocation(capabilityId);
        var mappingCascade = CascadePlatformCapabilityRevocation(capabilityId);
        if (!irqCascade.IsSuccess) return irqCascade;
        if (!mmioCascade.IsSuccess) return mmioCascade;
        if (!deviceCascade.IsSuccess) return deviceCascade;
        return mappingCascade.IsSuccess ? result : mappingCascade;
    }

    private KernelResult TransitionProcessExecution(
        ProcessHandle handle,
        ProcessState from,
        ProcessState to,
        PlatformDomainExecutionTransition platformTransition)
    {
        var resolved = Processes.Resolve(handle);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        var process = resolved.Value!;
        if (process.State != from) return InvalidTransition(process.State, to);

        var platform = TransitionPlatformExecutionIfBound(
            handle,
            process,
            platformTransition);
        if (!platform.IsSuccess) return platform;

        process.SetState(to);
        return KernelResult.Ok();
    }

    private KernelResult TransitionPlatformExecutionIfBound(
        ProcessHandle handle,
        SingProcess process,
        PlatformDomainExecutionTransition transition)
    {
        if (!_processPlatformBindings.TryGetValue(handle, out var binding))
            return KernelResult.Ok();

        var identity = new PlatformDomainIdentity(process.DomainId, process.Generation);
        return PlatformAuthority.TransitionDomainExecution(binding, identity, transition);
    }

    private static KernelResult EnsureProcessAcceptsNewEffects(SingProcess process)
    {
        if (process.State == ProcessState.Exiting)
        {
            return KernelResult.Fail(
                KernelError.InvalidTransition,
                "Process is Exiting and cannot authorize new effects.");
        }

        if (process.State is ProcessState.Exited or ProcessState.Faulted)
        {
            return KernelResult.Fail(
                KernelError.InvalidTransition,
                $"Terminal process state {process.State} cannot authorize new effects.");
        }

        return KernelResult.Ok();
    }

    private static KernelResult InvalidTransition(ProcessState actual, ProcessState requested) => KernelResult.Fail(KernelError.InvalidTransition, $"Cannot transition from {actual} to {requested}.");
}
