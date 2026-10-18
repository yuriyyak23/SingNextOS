using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Kernel;
using SingPlus.Runtime;
using SingPlus.Platform.Host;

namespace SingPlus.Boot;

public static class HostDebugBootPipeline
{
    private const long MaximumSipImageBytes = 64 * 1024 * 1024;

    public static IReadOnlyList<IKernelBootStage> Create(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var parsed = EmulatorOptions.Parse(arguments);
        if (parsed.Options is null)
        {
            var error = parsed.Error ?? "Command-line help must be handled before boot.";
            return [new Stage("host-debug arguments", () => KernelBootStageResult.Failure(error))];
        }

        return Create(parsed.Options, out _);
    }

    public static IReadOnlyList<IKernelBootStage> Create(EmulatorOptions options, out EmulatorSession session)
    {
        ArgumentNullException.ThrowIfNull(options);
        var context = new Context(new EmulatorSession(options));
        session = context.Session;
        List<IKernelBootStage> stages =
        [
            new Stage("host environment capture", () =>
            {
                context.Session.EnvironmentFacts.Add($"OS: {RuntimeInformation.OSDescription}");
                context.Session.EnvironmentFacts.Add($"OS architecture: {RuntimeInformation.OSArchitecture}");
                context.Session.EnvironmentFacts.Add($"Process architecture: {RuntimeInformation.ProcessArchitecture}");
                context.Session.EnvironmentFacts.Add($"Runtime: {RuntimeInformation.FrameworkDescription}");
                context.Session.EnvironmentFacts.Add($"Working directory: {Environment.CurrentDirectory}");
                return KernelBootStageResult.Success();
            }),
            new Stage("runtime authority graph", () =>
            {
                var initialized = RecoveryBootstrap.Initialize(context.Session);
                if (!initialized.IsSuccess) return initialized;
                if (options.DeferComputeCompletion)
                    context.Session.EnvironmentFacts.Add("DSC1 completion: deferred by existing Host provider debug knob.");
                context.Session.EnvironmentFacts.Add(options.HostModelPlatform
                    ? "Platform: Host model provider (host-test); semantic model only, no physical hardware evidence."
                    : options.HybridCpuChildPlatform
                        ? "Platform: HybridCPU model parent with existing ExternalRuntime V3 executable child adapter; feature evidence remains family-specific."
                    : options.HybridCpuModelPlatform
                        ? "Platform: existing HybridCPU neutral model adapter; ModelOnly, no physical execution evidence."
                        : "Platform: none; external execution/discovery unavailable.");
                return KernelBootStageResult.Success();
            }),
            new Stage("platform profile invariants", () =>
            {
                if (context.Session.Runtime is null)
                    return KernelBootStageResult.Failure("Runtime authority graph is unavailable.");

                var hasFeatures = context.Session.Runtime.QueryPlatformFeatures().Features.Count > 0;
                if (hasFeatures != (options.HostModelPlatform || options.HybridCpuModelPlatform || options.HybridCpuChildPlatform))
                    return KernelBootStageResult.Failure("Installed platform features do not match the requested profile.");
                if (options.HostModelPlatform)
                    EmulatorConsole.StageDetail("existing Host model provider installed; model semantics are not hardware evidence");
                return KernelBootStageResult.Success();
            }),
            new Stage("system process manifest", () =>
            {
                context.Session.SystemManifest = new SingProcessManifestV1(
                    new ProcessId(EmulatorSession.SystemProcessIdValue),
                    new DomainId(EmulatorSession.SystemDomainIdValue),
                    generation: 1,
                    entryIdentity: "singplus.host-debug.system",
                    executionRole: ExecutionRole.Kernel,
                    memoryProfile: MemoryProfile.ManagedGc);
                return KernelBootStageResult.Success();
            }),
            new Stage("system process materialization", () =>
            {
                if (context.Session.Runtime is null || context.Session.SystemManifest is null)
                    return KernelBootStageResult.Failure("Runtime or system manifest is unavailable.");

                var created = context.Session.Runtime.CreateProcess(context.Session.SystemManifest);
                if (!created.IsSuccess)
                    return KernelBootStageResult.Failure(created.Message ?? created.Error.ToString());

                context.Session.SystemProcess = new ProcessHandle(created.Value!.ProcessId, created.Value.Generation);
                return KernelBootStageResult.Success();
            }),
            new Stage("system process admission", () =>
            {
                if (context.Session.Runtime is null || context.Session.SystemProcess is not { } systemProcess)
                    return KernelBootStageResult.Failure("Materialized system process is unavailable.");

                var admitted = context.Session.Runtime.AdmitProcess(systemProcess);
                return admitted.IsSuccess
                    ? KernelBootStageResult.Success()
                    : KernelBootStageResult.Failure(admitted.Message ?? admitted.Error.ToString());
            }),
            new Stage("managed execution transition", () =>
            {
                if (context.Session.Runtime is null || context.Session.SystemProcess is not { } systemProcess)
                    return KernelBootStageResult.Failure("Admitted system process is unavailable.");

                var started = context.Session.Runtime.StartProcess(systemProcess);
                return started.IsSuccess
                    ? KernelBootStageResult.Success()
                    : KernelBootStageResult.Failure(started.Message ?? started.Error.ToString());
            })
        ];

        if (options.BudgetEnabled)
        {
            stages.Add(new Stage("system resource budget", context.Session.ConfigureBudgets));
        }

        for (var index = 0; index < options.SipPaths.Count; index++)
        {
            var capturedIndex = index;
            stages.Add(new Stage($"SIP component admission #{index + 1}",
                () => AdmitSip(context, capturedIndex)));
        }

        stages.Add(
            new Stage("post-start invariants", () =>
            {
                if (context.Session.Runtime is null ||
                    context.Session.SystemProcess is not { } systemProcess)
                {
                    return KernelBootStageResult.Failure("Boot context is incomplete.");
                }

                var resolved = context.Session.Runtime.Processes.Resolve(systemProcess);
                if (!resolved.IsSuccess || resolved.Value!.State != ProcessState.Running)
                    return KernelBootStageResult.Failure("System process did not reach Running state.");
                var expected = checked(1 + context.Session.Sips.Count);
                if (context.Session.Runtime.Processes.Snapshot().Count != expected || context.Session.Runtime.Domains.Snapshot().Count != expected)
                    return KernelBootStageResult.Failure("Runtime process/domain generations are ambiguous.");
                if (context.SipComponents.Count != context.Session.Sips.Count ||
                    context.SipComponents.Any(static component => component.State != ComponentLifecycleState.Running))
                {
                    return KernelBootStageResult.Failure("One or more SIP components did not reach Running lifecycle state.");
                }

                return KernelBootStageResult.Success();
            }));

        if (options.TraceEnabled)
        {
            stages.Add(new Stage("system deterministic trace", () =>
            {
                if (context.Session.SystemProcess is not { } systemProcess)
                    return KernelBootStageResult.Failure("System process is unavailable.");

                var started = context.Session.StartTrace(systemProcess, options.TraceCapacity);
                return started.IsSuccess
                    ? KernelBootStageResult.Success()
                    : KernelBootStageResult.Failure(started.Message ?? started.Error.ToString());
            }));
        }

        return stages;
    }

    private static KernelBootStageResult AdmitSip(Context context, int index)
    {
        var session = context.Session;
        if (session.Runtime is null)
            return KernelBootStageResult.Failure("Runtime authority graph is unavailable.");

        var path = session.Options.SipPaths[index];
        if (!File.Exists(path))
            return KernelBootStageResult.Failure($"SIP image '{path}' does not exist.");

        var file = new FileInfo(path);
        if (file.Length <= 0 || file.Length > MaximumSipImageBytes)
            return KernelBootStageResult.Failure($"SIP image length must be between 1 and {MaximumSipImageBytes} bytes.");
        var expectedLength = file.Length;
        var expectedWriteTime = file.LastWriteTimeUtc;

        AssemblyName assembly;
        byte[] image;
        try
        {
            assembly = AssemblyName.GetAssemblyName(path);
            image = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return KernelBootStageResult.Failure($"SIP image is not a readable managed assembly: {exception.Message}");
        }

        file.Refresh();
        if (image.LongLength != expectedLength || file.Length != expectedLength || file.LastWriteTimeUtc != expectedWriteTime)
            return KernelBootStageResult.Failure("SIP image changed while it was being read.");

        var ordinal = checked((ulong)index + 1);
        var processId = new ProcessId(checked(EmulatorSession.SipProcessIdBase + ordinal));
        var domainId = new DomainId(checked(EmulatorSession.SipDomainIdBase + ordinal));
        var identity = assembly.Name ?? Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(identity))
            return KernelBootStageResult.Failure("SIP assembly identity is empty.");

        var process = new SingProcessManifestV1(
            processId,
            domainId,
            generation: 1,
            entryIdentity: $"host-debug.sip.{identity}",
            executionRole: ExecutionRole.Sip,
            memoryProfile: MemoryProfile.SipRegion);
        var componentIdentity = new ComponentIdentity($"host-debug.sip.{identity}");
        var digest = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
        string? staticProof = null;
        if (session.Options.SipRoot is { } root)
        {
            var verified = SipStaticVerifier.Verify(path, root, digest, out var result);
            if (!verified.IsSuccess) return verified;
            staticProof = result!.Proof.ProofDigest;
        }
        var manifest = new ServiceManifestV1(
            componentIdentity,
            new ComponentVersion(assembly.Version?.ToString() ?? "0.0.0.0"),
            digest,
            process);
        var admitted = session.Runtime.AdmitComponent(new ComponentAdmissionPlan(manifest, image));
        if (!admitted.IsSuccess)
            return KernelBootStageResult.Failure(admitted.Message ?? admitted.Error.ToString());

        context.SipComponents.Add(admitted.Value!);
        if (session.IsBudgetConfigured)
        {
            session.BudgetAccounts[admitted.Value!.Process.ProcessId.Value] = new ProcessBudgetAdmission(
                admitted.Value.Process,
                admitted.Value.ServiceBudget,
                admitted.Value.ProcessBudget);
        }
        session.Sips.Add(new SipAdmissionRecord(
            path,
            identity,
            assembly.Version?.ToString() ?? "0.0.0.0",
            digest,
            componentIdentity,
            staticProof));
        return KernelBootStageResult.Success();
    }

    private sealed class Context(EmulatorSession session)
    {
        internal EmulatorSession Session { get; } = session;
        internal List<ComponentLifecycleSnapshot> SipComponents { get; } = [];
    }

    private sealed class Stage(string name, Func<KernelBootStageResult> execute) : IKernelBootStage
    {
        public string Name { get; } = name;

        public KernelBootStageResult Execute() => execute();
    }
}
