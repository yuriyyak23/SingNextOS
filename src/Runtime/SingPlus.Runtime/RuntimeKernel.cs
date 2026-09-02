using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    public RuntimeKernel()
    {
        Processes = new ProcessRegistry();
        Domains = new DomainRegistry();
        CapabilityAuthority = new CapabilityAuthority();
        Regions = new RegionAuthority();
    }

    public ProcessRegistry Processes { get; }
    public DomainRegistry Domains { get; }
    public CapabilityAuthority CapabilityAuthority { get; }
    public RegionAuthority Regions { get; }

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
            var satisfied = CapabilityAuthority.SnapshotForDomain(process.DomainId).Any(c =>
                c.Generation == process.Generation && c.ResourceKind == requirement.ResourceKind &&
                string.Equals(c.ResourceId, requirement.ResourceId, StringComparison.Ordinal) &&
                (c.Rights & requirement.Rights) == requirement.Rights);
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
        process.SetState(ProcessState.Runnable);
        process.SetState(ProcessState.Running);
        return KernelResult.Ok();
    }

    public KernelResult ParkProcess(ProcessHandle handle) => Transition(handle, ProcessState.Running, ProcessState.Parked);
    public KernelResult ResumeProcess(ProcessHandle handle) => Transition(handle, ProcessState.Parked, ProcessState.Running);

    public KernelResult TerminateProcess(ProcessHandle handle)
    {
        var resolved = Processes.Resolve(handle);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        var process = resolved.Value!;
        if (process.State is ProcessState.Exited or ProcessState.Faulted) return InvalidTransition(process.State, ProcessState.Exiting);
        process.SetState(ProcessState.Exiting);
        CleanupProcess(process);
        process.SetState(ProcessState.Exited);
        Processes.Retire(process);
        return KernelResult.Ok();
    }

    public KernelResult FaultProcess(ProcessHandle handle)
    {
        var resolved = Processes.Resolve(handle);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        var process = resolved.Value!;
        if (process.State is ProcessState.Exited or ProcessState.Faulted) return InvalidTransition(process.State, ProcessState.Faulted);
        CleanupProcess(process);
        process.SetState(ProcessState.Faulted);
        Processes.Retire(process);
        return KernelResult.Ok();
    }

    public KernelResult<CapabilityDescriptorV1> MintCapability(DomainId issuerDomain, ProcessHandle subject, ResourceKind resourceKind, string resourceId, CapabilityRights rights)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(resolved.Error, resolved.Message!);
        if (!Domains.Contains(issuerDomain)) return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.DomainNotFound, $"Issuer domain {issuerDomain} is not active.");
        var descriptor = CapabilityAuthority.Mint(issuerDomain, resolved.Value!.DomainId, resourceKind, resourceId, rights, subject.Generation);
        resolved.Value.AddCapability(descriptor.CapabilityId);
        return KernelResult<CapabilityDescriptorV1>.Ok(descriptor);
    }

    public KernelResult<CapabilityDescriptorV1> DelegateCapability(ProcessHandle delegator, ProcessHandle target, CapabilityId sourceCapability, CapabilityRights rights)
    {
        var sourceProcess = Processes.Resolve(delegator);
        if (!sourceProcess.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(sourceProcess.Error, sourceProcess.Message!);
        var targetProcess = Processes.Resolve(target);
        if (!targetProcess.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(targetProcess.Error, targetProcess.Message!);
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
        if (result.IsSuccess) foreach (var process in Processes.Snapshot()) process.RemoveCapability(capabilityId);
        return result;
    }

    private KernelResult Transition(ProcessHandle handle, ProcessState from, ProcessState to)
    {
        var resolved = Processes.Resolve(handle);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        var process = resolved.Value!;
        if (process.State != from) return InvalidTransition(process.State, to);
        process.SetState(to);
        return KernelResult.Ok();
    }

    private void CleanupProcess(SingProcess process)
    {
        process.ClearCapabilities();
        var domainEnded = Domains.Remove(process);
        if (domainEnded)
        {
            CapabilityAuthority.RevokeAllForDomain(process.DomainId);
            Regions.ReclaimAllForDomain(process.DomainId);
        }
        process.ClearRuntimeResources();
    }

    private static KernelResult InvalidTransition(ProcessState actual, ProcessState requested) => KernelResult.Fail(KernelError.InvalidTransition, $"Cannot transition from {actual} to {requested}.");
}
