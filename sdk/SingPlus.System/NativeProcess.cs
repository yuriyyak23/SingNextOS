using SingPlus.Contracts;
using SingPlus.Sip.Process;

namespace SingPlus.System;

/// <summary>Source-facing process authority; the numeric process id is never sufficient authority.</summary>
public sealed class NativeProcess
{
    private readonly IProcessService _client;
    internal NativeProcess(IProcessService client, ProcessAuthority authority) { _client = client; Authority = authority; }
    public ProcessAuthority Authority { get; }
    public ValueTask StartAsync(CancellationToken cancellationToken = default) => Wait(_client.StartAsync(Command()), cancellationToken);
    public ValueTask ParkAsync(CancellationToken cancellationToken = default) => Wait(_client.ParkAsync(Command()), cancellationToken);
    public ValueTask ResumeAsync(CancellationToken cancellationToken = default) => Wait(_client.ResumeAsync(Command()), cancellationToken);
    public ValueTask TerminateAsync(CancellationToken cancellationToken = default) => Wait(_client.TerminateAsync(Command()), cancellationToken);
    public async ValueTask<ProcessState> WaitForExitAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return (await _client.WaitForExitAsync(Command()).ConfigureAwait(false)).State; }
    public ValueTask DelegateAsync(CapabilityId source, CapabilityRights subset, CancellationToken cancellationToken = default) => Wait(_client.DelegateAsync(new(Authority.Process, Authority.ControlCapability, source, subset)), cancellationToken);
    private ProcessCommand Command() => new(Authority.Process, Authority.ControlCapability);
    private static async ValueTask Wait(ValueTask operation, CancellationToken token) { token.ThrowIfCancellationRequested(); await operation.ConfigureAwait(false); }
}

public sealed class ProcessManager
{
    private readonly IProcessService _client;
    private readonly CapabilityId _createCapability;
    public ProcessManager(IProcessService generatedClient, CapabilityId createCapability) { _client = generatedClient ?? throw new ArgumentNullException(nameof(generatedClient)); _createCapability = createCapability; }
    public async ValueTask<NativeProcess> CreateAsync(
        SingProcessManifestV1 manifest,
        ChildProcessPolicy policy = ChildProcessPolicy.Attached,
        IReadOnlyList<ProcessCapabilityDelegation>? initialCapabilities = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = await _client.CreateAsync(new(_createCapability, manifest, policy, initialCapabilities ?? [])).ConfigureAwait(false);
        return new NativeProcess(_client, response.Authority);
    }
}
