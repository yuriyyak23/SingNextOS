using SingPlus.Contracts;
using SingPlus.Sip.Sdk;

namespace SingPlus.Sip.Process;

[BoundedPayload(7168)]
public readonly struct ProcessManifestValue : IBoundedPayload
{
    private const int MaxCapabilities = 64;
    private const int MaxContracts = 64;
    private readonly SingProcessManifestV1 _manifest;
    private readonly int _payloadSize;

    public ProcessManifestValue(SingProcessManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.RequiredCapabilities.Count > MaxCapabilities || manifest.RequiredContracts.Count > MaxContracts)
            throw new ArgumentOutOfRangeException(nameof(manifest), "SIP process manifest cardinality exceeds the closed value schema.");
        var canonical = manifest.SerializeCanonical();
        if (canonical.Length > MaxPayloadSizeValue)
            throw new ArgumentOutOfRangeException(nameof(manifest), "SIP process manifest exceeds its canonical byte bound.");
        _manifest = new SingProcessManifestV1(
            manifest.ProcessId, manifest.DomainId, manifest.Generation, manifest.EntryIdentity,
            manifest.ExecutionRole, manifest.MemoryProfile, manifest.RequiredCapabilities.ToArray(),
            manifest.RequiredContracts.ToArray(), manifest.ResourceLimits, manifest.SchemaId, manifest.SchemaVersion);
        _payloadSize = canonical.Length;
    }

    private const int MaxPayloadSizeValue = 7168;
    public int PayloadSize => _payloadSize;
    public int MaxPayloadSize => MaxPayloadSizeValue;
    public SingProcessManifestV1 MaterializeForRuntime() => _manifest is null
        ? throw new InvalidOperationException("Process manifest value is uninitialized.")
        : new SingProcessManifestV1(
            _manifest.ProcessId, _manifest.DomainId, _manifest.Generation, _manifest.EntryIdentity,
            _manifest.ExecutionRole, _manifest.MemoryProfile, _manifest.RequiredCapabilities.ToArray(),
            _manifest.RequiredContracts.ToArray(), _manifest.ResourceLimits, _manifest.SchemaId, _manifest.SchemaVersion);
}

[BoundedPayload(512)]
public readonly struct InitialCapabilitySet : IBoundedPayload, IReadOnlyList<ProcessCapabilityDelegation>
{
    public const int MaxCount = 16;
    private readonly ProcessCapabilityDelegation[] _items;

    public InitialCapabilitySet(IEnumerable<ProcessCapabilityDelegation>? items)
    {
        _items = (items ?? []).ToArray();
        if (_items.Length > MaxCount) throw new ArgumentOutOfRangeException(nameof(items));
    }

    public int Count => _items?.Length ?? 0;
    public ProcessCapabilityDelegation this[int index] => _items[index];
    public int PayloadSize => checked(Count * 24);
    public int MaxPayloadSize => 512;
    public IEnumerator<ProcessCapabilityDelegation> GetEnumerator() =>
        ((IEnumerable<ProcessCapabilityDelegation>)(_items ?? [])).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

[BoundedPayload(8192)]
public readonly record struct CreateProcessRequest(
    CapabilityId CreateCapability,
    ProcessManifestValue Manifest,
    ChildProcessPolicy Policy,
    InitialCapabilitySet InitialCapabilities) : IBoundedPayload
{
    public CreateProcessRequest(CapabilityId createCapability, SingProcessManifestV1 manifest, ChildProcessPolicy policy)
        : this(createCapability, new ProcessManifestValue(manifest), policy, new InitialCapabilitySet()) { }

    public CreateProcessRequest(CapabilityId createCapability, SingProcessManifestV1 manifest, ChildProcessPolicy policy,
        IEnumerable<ProcessCapabilityDelegation> initialCapabilities)
        : this(createCapability, new ProcessManifestValue(manifest), policy, new InitialCapabilitySet(initialCapabilities)) { }

    public int PayloadSize => checked(64 + Manifest.PayloadSize + InitialCapabilities.PayloadSize);
    public int MaxPayloadSize => 8192;
}
[BoundedPayload(48)]
public readonly record struct ProcessCapabilityDelegation(CapabilityId SourceCapability, CapabilityRights Rights) : IBoundedPayload { public int PayloadSize => 24; public int MaxPayloadSize => 48; }
[BoundedPayload(96)]
public readonly record struct ProcessCommand(ProcessHandle Process, CapabilityId ControlCapability) : IBoundedPayload { public int PayloadSize => 48; public int MaxPayloadSize => 96; }
[BoundedPayload(128)]
public readonly record struct ProcessCommandV2(ProcessControlObjectHandle Control, CapabilityId ControlCapability) : IBoundedPayload { public int PayloadSize => 80; public int MaxPayloadSize => 128; }
[BoundedPayload(128)]
public readonly record struct DelegateProcessCapabilityRequest(ProcessHandle Process, CapabilityId ControlCapability, CapabilityId SourceCapability, CapabilityRights Rights) : IBoundedPayload { public int PayloadSize => 80; public int MaxPayloadSize => 128; }
[BoundedPayload(160)]
public readonly record struct DelegateProcessCapabilityRequestV2(ProcessControlObjectHandle Control, CapabilityId ControlCapability, CapabilityId SourceCapability, CapabilityRights Rights) : IBoundedPayload { public int PayloadSize => 112; public int MaxPayloadSize => 160; }
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
    [Message(8), Transition("Ready", "Ready")] ValueTask DelegateV2Async(DelegateProcessCapabilityRequestV2 request);
    [Message(9), Transition("Ready", "Ready")] ValueTask StartV2Async(ProcessCommandV2 command);
    [Message(10), Transition("Ready", "Ready")] ValueTask ParkV2Async(ProcessCommandV2 command);
    [Message(11), Transition("Ready", "Ready")] ValueTask ResumeV2Async(ProcessCommandV2 command);
    [Message(12), Transition("Ready", "Ready")] ValueTask TerminateV2Async(ProcessCommandV2 command);
    [Message(13), Transition("Ready", "Ready")] ValueTask<ProcessStateResponse> WaitForExitV2Async(ProcessCommandV2 command);
}
