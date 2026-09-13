using SingPlus.Contracts;
using SingPlus.Sip.Sdk;

namespace SingPlus.Sip.Process;

[BoundedPayload(8192)]
public readonly record struct CreateProcessRequest(
    CapabilityId CreateCapability,
    SingProcessManifestV1 Manifest,
    ChildProcessPolicy Policy,
    IReadOnlyList<ProcessCapabilityDelegation> InitialCapabilities) : IBoundedPayload
{
    public CreateProcessRequest(CapabilityId createCapability, SingProcessManifestV1 manifest, ChildProcessPolicy policy)
        : this(createCapability, manifest, policy, []) { }

    public int PayloadSize => 64 + (Manifest?.SerializeCanonical().Length ?? 0) + (InitialCapabilities?.Count ?? 0) * 24;
    public int MaxPayloadSize => 8192;
}
[BoundedPayload(48)]
public readonly record struct ProcessCapabilityDelegation(CapabilityId SourceCapability, CapabilityRights Rights) : IBoundedPayload { public int PayloadSize => 24; public int MaxPayloadSize => 48; }
[BoundedPayload(96)]
public readonly record struct ProcessCommand(ProcessHandle Process, CapabilityId ControlCapability) : IBoundedPayload { public int PayloadSize => 48; public int MaxPayloadSize => 96; }
[BoundedPayload(128)]
public readonly record struct DelegateProcessCapabilityRequest(ProcessHandle Process, CapabilityId ControlCapability, CapabilityId SourceCapability, CapabilityRights Rights) : IBoundedPayload { public int PayloadSize => 80; public int MaxPayloadSize => 128; }
[BoundedPayload(64)]
public readonly record struct ProcessAuthorityResponse(ProcessAuthority Authority) : IBoundedPayload { public int PayloadSize => 40; public int MaxPayloadSize => 64; }
[BoundedPayload(64)]
public readonly record struct ProcessStateResponse(ProcessHandle Process, ProcessState State) : IBoundedPayload { public int PayloadSize => 32; public int MaxPayloadSize => 64; }

[SipContract, InitialState("Ready")]
public interface IProcessService
{
    [Message(1), Transition("Ready", "Ready")] ValueTask<ProcessAuthorityResponse> CreateAsync(CreateProcessRequest request);
    [Message(2), Transition("Ready", "Ready")] ValueTask DelegateAsync(DelegateProcessCapabilityRequest request);
    [Message(3), Transition("Ready", "Ready")] ValueTask StartAsync(ProcessCommand command);
    [Message(4), Transition("Ready", "Ready")] ValueTask ParkAsync(ProcessCommand command);
    [Message(5), Transition("Ready", "Ready")] ValueTask ResumeAsync(ProcessCommand command);
    [Message(6), Transition("Ready", "Ready")] ValueTask TerminateAsync(ProcessCommand command);
    [Message(7), Transition("Ready", "Ready")] ValueTask<ProcessStateResponse> WaitForExitAsync(ProcessCommand command);
}
