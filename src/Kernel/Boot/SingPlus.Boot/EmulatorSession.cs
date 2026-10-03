using System.Diagnostics;
using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Kernel;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Boot;

/// <summary>
/// Live state of the emulated SingNextOS machine. Shared by boot stages, demo
/// workloads and the interactive shell; also exposes the higher-level emulator
/// operations (spawn, trace, budget, buffers, channels) on top of the raw
/// <see cref="RuntimeKernel"/> authority graph.
/// </summary>
public sealed class EmulatorSession
{
    public const ulong SystemProcessIdValue = 1;
    public const ulong SystemDomainIdValue = 1;
    public const ulong SipProcessIdBase = 1000;
    public const ulong SipDomainIdBase = 10000;
    private const ulong FirstEmulatorProcessId = 5000;
    private const ulong FirstEmulatorDomainId = 50000;

    private ulong _nextProcessId = FirstEmulatorProcessId;
    private ulong _nextDomainId = FirstEmulatorDomainId;

    public EmulatorSession(EmulatorOptions options)
    {
        Options = options;
        Uptime = Stopwatch.StartNew();
    }

    public EmulatorOptions Options { get; }
    public Stopwatch Uptime { get; }
    public RuntimeKernel? Runtime { get; set; }
    public SingProcessManifestV1? SystemManifest { get; set; }
    public ProcessHandle? SystemProcess { get; set; }

    public List<SipAdmissionRecord> Sips { get; } = [];
    public List<ChannelRecord> Channels { get; } = [];
    public Dictionary<ulong, BufferRecord> Buffers { get; } = [];
    public Dictionary<ulong, TraceSessionAdmission> TraceSessions { get; } = [];
    public Dictionary<ulong, ProcessBudgetAdmission> BudgetAccounts { get; } = [];
    public HashSet<ulong> EmulatorSpawnedPids { get; } = [];
    public List<string> EnvironmentFacts { get; } = [];

    public BudgetAccountHandle? SystemBudgetAccount { get; set; }
    public CapabilityId? BudgetAdministrationCapability { get; set; }

    public bool IsBudgetConfigured => SystemBudgetAccount is not null;

    public (ProcessId Process, DomainId Domain) AllocateIdentity() =>
        (new ProcessId(checked(_nextProcessId++)), new DomainId(checked(_nextDomainId++)));

    /// <summary>Resolves a live process by its numeric id, refreshing the current generation.</summary>
    public KernelResult<ProcessHandle> ResolvePid(ulong processIdValue)
    {
        if (Runtime is null)
            return KernelResult<ProcessHandle>.Fail(KernelError.PlatformUnavailable, "Runtime authority graph is unavailable.");

        foreach (var process in Runtime.Processes.Snapshot())
            if (process.ProcessId.Value == processIdValue)
                return KernelResult<ProcessHandle>.Ok(new ProcessHandle(process.ProcessId, process.Generation));

        return KernelResult<ProcessHandle>.Fail(
            KernelError.ProcessNotFound,
            $"Process {processIdValue} is not live (terminated generations are not addressable).");
    }

    /// <summary>Creates, admits and starts a new emulator-owned process.</summary>
    public KernelResult<ProcessHandle> SpawnProcess(string entryIdentity, ExecutionRole role, MemoryProfile memory)
    {
        if (Runtime is null)
            return KernelResult<ProcessHandle>.Fail(KernelError.PlatformUnavailable, "Runtime authority graph is unavailable.");
        if (string.IsNullOrWhiteSpace(entryIdentity))
            return KernelResult<ProcessHandle>.Fail(KernelError.InvalidManifest, "Entry identity is required.");

        var (processId, domainId) = AllocateIdentity();
        var manifest = new SingProcessManifestV1(
            processId,
            domainId,
            generation: 1,
            entryIdentity: entryIdentity,
            executionRole: role,
            memoryProfile: memory);

        var created = Runtime.CreateProcess(manifest);
        if (!created.IsSuccess) return KernelResult<ProcessHandle>.Fail(created.Error, created.Message!);

        var handle = new ProcessHandle(created.Value!.ProcessId, created.Value.Generation);
        var admitted = Runtime.AdmitProcess(handle);
        if (!admitted.IsSuccess) return KernelResult<ProcessHandle>.Fail(admitted.Error, admitted.Message!);
        var started = Runtime.StartProcess(handle);
        if (!started.IsSuccess) return KernelResult<ProcessHandle>.Fail(started.Error, started.Message!);

        EmulatorSpawnedPids.Add(processId.Value);
        return KernelResult<ProcessHandle>.Ok(handle);
    }

    /// <summary>Terminates a process and drives the teardown observation to completion.</summary>
    public KernelResult TerminateAndReclaim(ProcessHandle handle, bool fault = false)
    {
        if (Runtime is null)
            return KernelResult.Fail(KernelError.PlatformUnavailable, "Runtime authority graph is unavailable.");

        var begun = fault ? Runtime.FaultProcess(handle) : Runtime.TerminateProcess(handle);
        if (!begun.IsSuccess && begun.Error != KernelError.PlatformBindingDraining) return begun;

        for (var step = 0; step < 16; step++)
        {
            var terminal = Runtime.Processes.ResolveTerminalState(handle);
            if (terminal.IsSuccess)
                return CompleteProcessTeardown(handle, fault ? ProcessState.Faulted : ProcessState.Exited);

            var observed = Runtime.ObserveProcessTeardown(handle);
            if (!observed.IsSuccess)
            {
                terminal = Runtime.Processes.ResolveTerminalState(handle);
                if (terminal.IsSuccess)
                    return CompleteProcessTeardown(handle, fault ? ProcessState.Faulted : ProcessState.Exited);
                return KernelResult.Fail(observed.Error, observed.Message!);
            }
            if (observed.Value!.IsComplete)
                return CompleteProcessTeardown(handle, fault ? ProcessState.Faulted : ProcessState.Exited);
        }

        return KernelResult.Fail(KernelError.InvalidTransition, "Process teardown did not complete within the observation budget.");
    }

    private KernelResult CompleteProcessTeardown(ProcessHandle handle, ProcessState expectedState)
    {
        if (Runtime is null)
            return KernelResult.Fail(KernelError.PlatformUnavailable, "Runtime authority graph is unavailable.");

        var terminal = Runtime.Processes.ResolveTerminalState(handle);
        if (!terminal.IsSuccess)
            return KernelResult.Fail(terminal.Error, terminal.Message!);
        if (terminal.Value != expectedState)
            return KernelResult.Fail(KernelError.InvalidTransition, "Process reached an unexpected terminal state.");

        TraceSessions.Remove(handle.ProcessId.Value);
        foreach (var regionId in Buffers
                     .Where(pair => pair.Value.Owner == handle)
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            Buffers.Remove(regionId);
        }
        EmulatorSpawnedPids.Remove(handle.ProcessId.Value);
        return KernelResult.Ok();
    }

    /// <summary>Starts a deterministic trace session for a live process and tracks it.</summary>
    public KernelResult<TraceSessionAdmission> StartTrace(ProcessHandle owner, int producerCapacity)
    {
        if (Runtime is null)
            return KernelResult<TraceSessionAdmission>.Fail(KernelError.PlatformUnavailable, "Runtime authority graph is unavailable.");
        if (TraceSessions.ContainsKey(owner.ProcessId.Value))
            return KernelResult<TraceSessionAdmission>.Fail(KernelError.InvalidTransition, $"Process {owner.ProcessId} already owns a trace session.");

        var started = Runtime.StartTraceSession(owner, producerCapacity, TraceOverflowPolicy.DropWithMarker);
        if (!started.IsSuccess) return started;
        TraceSessions[owner.ProcessId.Value] = started.Value!;
        return started;
    }

    public KernelResult<TraceSessionAdmission> StopTrace(ulong processIdValue)
    {
        if (Runtime is null)
            return KernelResult<TraceSessionAdmission>.Fail(KernelError.PlatformUnavailable, "Runtime authority graph is unavailable.");
        if (!TraceSessions.TryGetValue(processIdValue, out var admission))
            return KernelResult<TraceSessionAdmission>.Fail(KernelError.TraceSessionNotFound, $"No tracked trace session for process {processIdValue}.");

        var owner = ResolvePid(processIdValue);
        if (!owner.IsSuccess) return KernelResult<TraceSessionAdmission>.Fail(owner.Error, owner.Message!);
        var stopped = Runtime.StopTraceSession(owner.Value, admission.Session);
        if (stopped.IsSuccess) TraceSessions.Remove(processIdValue);
        return stopped;
    }

    /// <summary>Configures the system budget and admits a process budget for one process.</summary>
    public KernelResult AdmitBudgetFor(ProcessHandle process, string correlation)
    {
        if (Runtime is null)
            return KernelResult.Fail(KernelError.PlatformUnavailable, "Runtime authority graph is unavailable.");
        if (SystemProcess is not { } system || BudgetAdministrationCapability is not { } capability)
            return KernelResult.Fail(KernelError.BudgetNotConfigured, "Budget administration is not configured (boot without --budget).");

        var admitted = Runtime.AdmitProcessBudget(system, capability, process, correlation, ProcessBudgetLimits);
        if (!admitted.IsSuccess) return KernelResult.Fail(admitted.Error, admitted.Message!);
        BudgetAccounts[process.ProcessId.Value] = admitted.Value!;
        return KernelResult.Ok();
    }

    internal KernelBootStageResult ConfigureBudgets()
    {
        if (Runtime is null || SystemProcess is not { } system)
            return KernelBootStageResult.Failure("Runtime or system process is unavailable.");

        var resolved = Runtime.Processes.Resolve(system);
        if (!resolved.IsSuccess) return KernelBootStageResult.Failure(resolved.Message ?? resolved.Error.ToString());

        var minted = Runtime.MintCapability(
            resolved.Value!.DomainId,
            system,
            ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration,
            CapabilityRights.Configure);
        if (!minted.IsSuccess) return KernelBootStageResult.Failure(minted.Message ?? minted.Error.ToString());
        BudgetAdministrationCapability = minted.Value!.CapabilityId;

        var configured = Runtime.ConfigureSystemBudget(system, BudgetAdministrationCapability.Value, SystemBudgetLimits);
        if (!configured.IsSuccess) return KernelBootStageResult.Failure(configured.Message ?? configured.Error.ToString());
        SystemBudgetAccount = configured.Value!.Account;

        var own = AdmitBudgetFor(system, "singplus.host-debug.system");
        if (!own.IsSuccess) return KernelBootStageResult.Failure(own.Message ?? own.Error.ToString());

        EmulatorConsole.StageDetail(
            $"system budget account {configured.Value.Account.AccountId.Value}, administration capability {BudgetAdministrationCapability.Value.Value}");
        return KernelBootStageResult.Success();
    }

    /// <summary>Allocates a byte buffer for a process and tracks it for later inspection/release.</summary>
    public KernelResult<BufferRecord> AllocateTrackedBuffer(ProcessHandle owner, int length)
    {
        if (Runtime is null)
            return KernelResult<BufferRecord>.Fail(KernelError.PlatformUnavailable, "Runtime authority graph is unavailable.");

        var allocated = Runtime.AllocateBuffer<byte>(owner, length);
        if (!allocated.IsSuccess) return KernelResult<BufferRecord>.Fail(allocated.Error, allocated.Message!);

        var buffer = allocated.Value!;
        var record = new BufferRecord(buffer.Handle, owner, buffer.Length, buffer);
        Buffers[buffer.Handle.RegionId.Value] = record;
        return KernelResult<BufferRecord>.Ok(record);
    }

    /// <summary>The emulator-wide request protocol used by demo workloads and shell channels.</summary>
    public static ProtocolDefinitionV1 CreatePingPongProtocol() => new(
        contractName: "singplus.emulator.pingpong",
        contractDigest: Convert.ToHexString(SHA256.HashData("singplus.emulator.pingpong"u8)).ToLowerInvariant(),
        initialState: "open",
        terminalStates: ["closed"],
        messages:
        [
            new ProtocolMessageDescriptorV1(1, "Ping"),
            new ProtocolMessageDescriptorV1(2, "Tick",
                requestPayload: new RequestPayloadDescriptorV1(
                    RequestPayloadKind.Primitive, "value", typeof(ulong).FullName!)),
            new ProtocolMessageDescriptorV1(3, "Close"),
        ],
        transitions:
        [
            new ProtocolTransitionV1(1, "open", "open"),
            new ProtocolTransitionV1(2, "open", "open"),
            new ProtocolTransitionV1(3, "open", "closed"),
        ]);

    /// <summary>Best-effort emulator shutdown: stop traces, reclaim emulator-spawned processes.</summary>
    public void Shutdown(Action<string>? report = null)
    {
        if (Runtime is null) return;

        foreach (var (pid, admission) in TraceSessions.ToArray())
        {
            var owner = ResolvePid(pid);
            if (owner.IsSuccess)
            {
                var stopped = Runtime.StopTraceSession(owner.Value, admission.Session);
                report?.Invoke(stopped.IsSuccess
                    ? $"trace session {admission.Session.SessionId.Value} for process {pid} stopped"
                    : $"trace session for process {pid} could not be stopped: {stopped.Message}");
            }
            TraceSessions.Remove(pid);
        }

        var spawned = EmulatorSpawnedPids.ToArray();
        Array.Sort(spawned);
        foreach (var pid in spawned)
        {
            var handle = ResolvePid(pid);
            if (!handle.IsSuccess) continue;
            var reclaimed = TerminateAndReclaim(handle.Value);
            report?.Invoke(reclaimed.IsSuccess
                ? $"process {pid} terminated and reclaimed"
                : $"process {pid} shutdown incomplete: {reclaimed.Message}");
        }
    }

    private static IReadOnlyList<BudgetAmount> SystemBudgetLimits { get; } =
    [
        new(ServiceBudgetDimension.CpuAllocation, 1_000_000_000UL),
        new(ServiceBudgetDimension.OwnedMemoryBytes, 512UL * 1024 * 1024),
        new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 64UL * 1024 * 1024),
        new(ServiceBudgetDimension.RegionUses, 8192),
        new(ServiceBudgetDimension.IpcMessages, 4_000_000),
        new(ServiceBudgetDimension.IpcBytes, 256UL * 1024 * 1024),
        new(ServiceBudgetDimension.ExternalOperations, 1024),
        new(ServiceBudgetDimension.CheckpointStorageBytes, 64UL * 1024 * 1024),
        new(ServiceBudgetDimension.TraceTelemetryBufferBytes, 64UL * 1024 * 1024),
        new(ServiceBudgetDimension.ComputeTimeNanoseconds, 1_000_000_000_000UL),
    ];

    private static IReadOnlyList<BudgetAmount> ProcessBudgetLimits { get; } =
    [
        new(ServiceBudgetDimension.CpuAllocation, 100_000_000UL),
        new(ServiceBudgetDimension.OwnedMemoryBytes, 64UL * 1024 * 1024),
        new(ServiceBudgetDimension.RegionUses, 1024),
        new(ServiceBudgetDimension.IpcMessages, 100_000),
        new(ServiceBudgetDimension.IpcBytes, 16UL * 1024 * 1024),
        new(ServiceBudgetDimension.TraceTelemetryBufferBytes, 4UL * 1024 * 1024),
        new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100_000_000_000UL),
    ];
}

public sealed record SipAdmissionRecord(
    string ImagePath,
    string AssemblyName,
    string AssemblyVersion,
    string ImageDigest,
    ComponentIdentity Identity);

public sealed record ChannelRecord(
    ulong ChannelId,
    ProcessHandle Left,
    ProcessHandle Right,
    ChannelEndpointHandle LeftEndpoint,
    ChannelEndpointHandle RightEndpoint,
    int Capacity)
{
    public ChannelEndpointHandle EndpointFor(ProcessHandle process) =>
        process == Left ? LeftEndpoint : RightEndpoint;

    public ProcessHandle PeerOf(ProcessHandle process) =>
        process == Left ? Right : Left;
}

public sealed record BufferRecord(RegionHandle Handle, ProcessHandle Owner, int Length, OwnedBuffer<byte> Buffer);
