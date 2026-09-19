using System.Reflection;
using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Kernel;
using SingPlus.Runtime;

namespace SingPlus.Boot;

public static class HostDebugBootPipeline
{
    private const long MaximumSipImageBytes = 64 * 1024 * 1024;

    public static IReadOnlyList<IKernelBootStage> Create(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var context = new Context();
        var argumentResult = ParseSipArguments(arguments, context);
        List<IKernelBootStage> stages =
        [
            new Stage("host-debug arguments", () => argumentResult),
            new Stage("runtime authority graph", () =>
            {
                // No platform provider is installed in this profile. CXL and HybridCPU
                // discovery/admission therefore remain unavailable rather than mocked.
                context.Runtime = new RuntimeKernel();
                return KernelBootStageResult.Success();
            }),
            new Stage("external platform backend exclusion", () =>
            {
                if (context.Runtime is null)
                    return KernelBootStageResult.Failure("Runtime authority graph is unavailable.");

                return context.Runtime.QueryPlatformFeatures().Features.Count == 0
                    ? KernelBootStageResult.Success()
                    : KernelBootStageResult.Failure("A platform backend was unexpectedly installed in host-debug mode.");
            }),
            new Stage("system process manifest", () =>
            {
                context.Manifest = new SingProcessManifestV1(
                    new ProcessId(1),
                    new DomainId(1),
                    generation: 1,
                    entryIdentity: "singplus.host-debug.system",
                    executionRole: ExecutionRole.Kernel,
                    memoryProfile: MemoryProfile.ManagedGc);
                return KernelBootStageResult.Success();
            }),
            new Stage("system process materialization", () =>
            {
                if (context.Runtime is null || context.Manifest is null)
                    return KernelBootStageResult.Failure("Runtime or system manifest is unavailable.");

                var created = context.Runtime.CreateProcess(context.Manifest);
                if (!created.IsSuccess)
                    return KernelBootStageResult.Failure(created.Message ?? created.Error.ToString());

                context.SystemProcess = new ProcessHandle(created.Value!.ProcessId, created.Value.Generation);
                return KernelBootStageResult.Success();
            }),
            new Stage("system process admission", () =>
            {
                if (context.Runtime is null || context.SystemProcess is not { } systemProcess)
                    return KernelBootStageResult.Failure("Materialized system process is unavailable.");

                var admitted = context.Runtime.AdmitProcess(systemProcess);
                return admitted.IsSuccess
                    ? KernelBootStageResult.Success()
                    : KernelBootStageResult.Failure(admitted.Message ?? admitted.Error.ToString());
            }),
            new Stage("managed execution transition", () =>
            {
                if (context.Runtime is null || context.SystemProcess is not { } systemProcess)
                    return KernelBootStageResult.Failure("Admitted system process is unavailable.");

                var started = context.Runtime.StartProcess(systemProcess);
                return started.IsSuccess
                    ? KernelBootStageResult.Success()
                    : KernelBootStageResult.Failure(started.Message ?? started.Error.ToString());
            })
        ];

        for (var index = 0; index < context.DeferredSipStageCount; index++)
        {
            var capturedIndex = index;
            stages.Add(new Stage($"SIP component admission #{index + 1}",
                () => AdmitSip(context, capturedIndex)));
        }

        stages.Add(
            new Stage("post-start invariants", () =>
            {
                if (context.Runtime is null ||
                    context.SystemProcess is not { } systemProcess)
                {
                    return KernelBootStageResult.Failure("Boot context is incomplete.");
                }

                var resolved = context.Runtime.Processes.Resolve(systemProcess);
                if (!resolved.IsSuccess || resolved.Value!.State != ProcessState.Running)
                    return KernelBootStageResult.Failure("System process did not reach Running state.");
                var expected = checked(1 + context.SipImages.Count);
                if (context.Runtime.Processes.Snapshot().Count != expected || context.Runtime.Domains.Snapshot().Count != expected)
                    return KernelBootStageResult.Failure("Runtime process/domain generations are ambiguous.");
                if (context.SipComponents.Count != context.SipImages.Count ||
                    context.SipComponents.Any(static component => component.State != ComponentLifecycleState.Running))
                {
                    return KernelBootStageResult.Failure("One or more SIP components did not reach Running lifecycle state.");
                }

                return KernelBootStageResult.Success();
            }));

        return stages;
    }

    private static KernelBootStageResult ParseSipArguments(IReadOnlyList<string> arguments, Context context)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], "--sip", StringComparison.Ordinal))
                return KernelBootStageResult.Failure($"Unknown host-debug argument '{arguments[index]}'.");
            if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
                return KernelBootStageResult.Failure("--sip requires a managed assembly path.");

            string fullPath;
            try { fullPath = Path.GetFullPath(arguments[index]); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            { return KernelBootStageResult.Failure($"Invalid SIP path: {exception.Message}"); }

            if (!context.AddSipPath(fullPath))
                return KernelBootStageResult.Failure($"SIP path '{fullPath}' was supplied more than once.");
        }

        return KernelBootStageResult.Success();
    }

    private static KernelBootStageResult AdmitSip(Context context, int index)
    {
        if (context.Runtime is null)
            return KernelBootStageResult.Failure("Runtime authority graph is unavailable.");

        var path = context.SipPaths[index];
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
        var processId = new ProcessId(checked(1000UL + ordinal));
        var domainId = new DomainId(checked(10000UL + ordinal));
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
        var manifest = new ServiceManifestV1(
            new ComponentIdentity($"host-debug.sip.{identity}"),
            new ComponentVersion(assembly.Version?.ToString() ?? "0.0.0.0"),
            Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(),
            process);
        var admitted = context.Runtime.AdmitComponent(new ComponentAdmissionPlan(manifest, image));
        if (!admitted.IsSuccess)
            return KernelBootStageResult.Failure(admitted.Message ?? admitted.Error.ToString());

        context.SipImages.Add(path);
        context.SipComponents.Add(admitted.Value!);
        return KernelBootStageResult.Success();
    }

    private sealed class Context
    {
        internal RuntimeKernel? Runtime { get; set; }
        internal SingProcessManifestV1? Manifest { get; set; }
        internal ProcessHandle? SystemProcess { get; set; }
        private readonly HashSet<string> _sipPathSet = new(StringComparer.OrdinalIgnoreCase);
        internal List<string> SipPaths { get; } = [];
        internal List<string> SipImages { get; } = [];
        internal List<ComponentLifecycleSnapshot> SipComponents { get; } = [];
        internal int DeferredSipStageCount => SipPaths.Count;

        internal bool AddSipPath(string path)
        {
            if (!_sipPathSet.Add(path)) return false;
            SipPaths.Add(path);
            return true;
        }
    }

    private sealed class Stage(string name, Func<KernelBootStageResult> execute) : IKernelBootStage
    {
        public string Name { get; } = name;

        public KernelBootStageResult Execute() => execute();
    }
}
