namespace SingPlus.Boot;

/// <summary>Built-in emulator workloads that double as runtime self-checks.</summary>
public enum EmulatorDemo
{
    Ipc,
    Regions,
    Capabilities,
    Lifecycle,
    Borrows,
    Platform,
    Compute,
    ComputeCancel,
    ComputeFault,
    PlatformRegions,
    External,
    Sessions,
    GeneratedSip,
    HostedService,
    Backing,
    VirtualDomain,
    ChildDomain,
    ChildExecute,
    ServiceRestart,
    SupervisedRestart,
}

public readonly record struct EmulatorOptionsParseResult(EmulatorOptions? Options, string? Error, bool ShowHelp)
{
    public static EmulatorOptionsParseResult Success(EmulatorOptions options) => new(options, null, false);
    public static EmulatorOptionsParseResult Failure(string error) => new(null, error, false);
    public static EmulatorOptionsParseResult Help() => new(null, null, true);
}

/// <summary>
/// Command line surface of the SingNextOS launch emulator. Parsing is strict: any
/// unknown argument aborts boot, mirroring the original host-debug profile contract.
/// </summary>
public sealed class EmulatorOptions
{
    public const int DefaultTraceCapacity = 256;
    public const int MaximumTraceCapacity = 16_384; // DeterministicTraceContract.MaximumProducerCapacity

    public IReadOnlyList<string> SipPaths { get; private init; } = [];
    public bool DeferComputeCompletion { get; private init; }
    public bool HostModelPlatform { get; private init; }
    public bool HybridCpuModelPlatform { get; private init; }
    public string? GuestArtifactPath { get; private init; }
    public bool HybridCpuChildPlatform { get; private init; }
    public string PlatformProfile => HybridCpuChildPlatform ? "hybridcpu-child" : HostModelPlatform ? "host-model" : HybridCpuModelPlatform ? "hybridcpu-model" : "none";
    public string? RecoveryJournalPath { get; private init; }
    public string? RecoveryKeyPath { get; private init; }
    public string? ReplacementSipPath { get; private init; }
    public string? SipRoot { get; private init; }
    public bool TraceEnabled { get; private init; }
    public int TraceCapacity { get; private init; } = DefaultTraceCapacity;
    public bool BudgetEnabled { get; private init; }
    public IReadOnlyList<EmulatorDemo> Demos { get; private init; } = [];
    public string? ScriptPath { get; private init; }
    public bool ShellRequested { get; private init; }
    public bool ShellSuppressed { get; private init; }
    public bool SnakeRequested { get; private init; }
    public bool PauseRequested { get; private init; }

    /// <summary>Fallback options used when argument parsing failed; every switch stays off.</summary>
    public static EmulatorOptions Empty { get; } = new();

    public bool WantsInteractiveShell(bool debuggerAttached)
    {
        if (ShellSuppressed || ScriptPath is not null || PauseRequested) return false;
        if (ShellRequested || debuggerAttached) return true;
        return Environment.UserInteractive && !Console.IsInputRedirected && !Console.IsOutputRedirected;
    }

    public static EmulatorOptionsParseResult Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        List<string> sipPaths = new List<string>();
        HashSet<string> sipPathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<EmulatorDemo> demos = new List<EmulatorDemo>();
        bool trace = false;
        int traceCapacity = DefaultTraceCapacity;
        bool budget = false;
        bool? hostModel = null;
        bool hybridCpu = false;
        bool hybridCpuChild = false;
        string? guestArtifact = null;
        bool deferCompute = false;
        string? script = null;
        string? sipRoot = null;
        string? replacementSip = null;
        string? recoveryJournal = null;
        string? recoveryKey = null;
        bool shell = false;
        bool noShell = false;
        bool pause = false;
        bool snake = false;

        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            switch (argument)
            {
                case "--sip":
                {
                    if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
                        return EmulatorOptionsParseResult.Failure("--sip requires a managed assembly path.");

                    string fullPath;
                    try { fullPath = Path.GetFullPath(arguments[index]); }
                    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                    { return EmulatorOptionsParseResult.Failure($"Invalid SIP path: {exception.Message}"); }

                    if (!sipPathSet.Add(fullPath))
                        return EmulatorOptionsParseResult.Failure($"SIP path '{fullPath}' was supplied more than once.");
                    sipPaths.Add(fullPath);
                    break;
                }

                case "--recovery-journal" or "--recovery-key":
                {
                    if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
                        return EmulatorOptionsParseResult.Failure($"{argument} requires a file path.");
                    if ((argument == "--recovery-journal" ? recoveryJournal : recoveryKey) is not null)
                        return EmulatorOptionsParseResult.Failure($"{argument} may be supplied once.");
                    string path;
                    try { path = Path.GetFullPath(arguments[index]); }
                    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                    { return EmulatorOptionsParseResult.Failure($"Invalid recovery path: {exception.Message}"); }
                    if (argument == "--recovery-journal") recoveryJournal = path;
                    else recoveryKey = path;
                    break;
                }

                case "--guest-artifact":
                    if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]) || guestArtifact is not null)
                        return EmulatorOptionsParseResult.Failure("--guest-artifact requires one unique HCEXE path.");
                    try { guestArtifact = Path.GetFullPath(arguments[index]); }
                    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                    { return EmulatorOptionsParseResult.Failure($"Invalid guest artifact path: {exception.Message}"); }
                    break;

                case "--defer-compute":
                    if (deferCompute) return EmulatorOptionsParseResult.Failure("--defer-compute may be supplied once.");
                    deferCompute = true;
                    break;

                case "--platform":
                    if (++index >= arguments.Count || hostModel is not null)
                        return EmulatorOptionsParseResult.Failure("--platform requires one profile: none, host-model or hybridcpu-model/hybridcpu-child.");
                    hybridCpuChild = string.Equals(arguments[index], "hybridcpu-child", StringComparison.OrdinalIgnoreCase);
                    hybridCpu = arguments[index].ToLowerInvariant() is "hybridcpu" or "hybridcpu-model";
                    hostModel = arguments[index].ToLowerInvariant() switch
                    {
                        "none" or "hybridcpu" or "hybridcpu-model" or "hybridcpu-child" => false,
                        "host" or "host-model" => true,
                        _ => null,
                    };
                    if (hostModel is null)
                        return EmulatorOptionsParseResult.Failure("Unsupported platform profile; use none, host-model (host alias) or hybridcpu-model (hybridcpu alias), hybridcpu-child.");
                    break;

                case "--replacement-sip":
                    if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]) || replacementSip is not null)
                        return EmulatorOptionsParseResult.Failure("--replacement-sip requires one unique assembly path.");
                    try { replacementSip = Path.GetFullPath(arguments[index]); }
                    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                    { return EmulatorOptionsParseResult.Failure($"Invalid replacement SIP path: {exception.Message}"); }
                    break;

                case "--sip-root":
                    if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]) || sipRoot is not null)
                        return EmulatorOptionsParseResult.Failure("--sip-root requires one unique Type::Method root.");
                    sipRoot = arguments[index];
                    break;

                case "--demo":
                {
                    if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
                        return EmulatorOptionsParseResult.Failure("--demo requires a workload name (ipc, regions, caps, lifecycle, all).");

                    foreach (string name in arguments[index].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (string.Equals(name, "all", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (EmulatorDemo availableDemo in Enum.GetValues<EmulatorDemo>())
                                if (availableDemo is not EmulatorDemo.VirtualDomain and not EmulatorDemo.ChildExecute and not EmulatorDemo.ChildDomain and not EmulatorDemo.PlatformRegions and not EmulatorDemo.SupervisedRestart and not EmulatorDemo.Platform and not EmulatorDemo.Compute and not EmulatorDemo.ComputeCancel and not EmulatorDemo.ComputeFault && !demos.Contains(availableDemo)) demos.Add(availableDemo);
                            continue;
                        }

                        if (!TryParseDemo(name, out EmulatorDemo demo))
                            return EmulatorOptionsParseResult.Failure($"Unknown demo workload '{name}'. Available: ipc, regions, caps, lifecycle, borrows, platform, compute, compute-cancel, compute-fault, platform-regions, external, sessions, child-execute, child-domain, virtual-domain, backing, generated-sip, hosted-service, service-restart, supervised-restart, all.");
                        if (!demos.Contains(demo)) demos.Add(demo);
                    }
                    break;
                }

                case "--trace":
                {
                    trace = true;
                    if (index + 1 < arguments.Count &&
                        int.TryParse(arguments[index + 1], System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out int capacity))
                    {
                        index++;
                        if (capacity <= 0 || capacity > MaximumTraceCapacity)
                            return EmulatorOptionsParseResult.Failure(
                                $"--trace capacity must be between 1 and {MaximumTraceCapacity}.");
                        traceCapacity = capacity;
                    }
                    break;
                }

                case "--game":
                    if (snake || ++index >= arguments.Count || !string.Equals(arguments[index], "snake", StringComparison.OrdinalIgnoreCase))
                        return EmulatorOptionsParseResult.Failure("--game requires snake and may be supplied once.");
                    snake = true;
                    break;

                case "--budget":
                    budget = true;
                    break;

                case "--script":
                {
                    if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
                        return EmulatorOptionsParseResult.Failure("--script requires a command script path.");
                    try { script = Path.GetFullPath(arguments[index]); }
                    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                    { return EmulatorOptionsParseResult.Failure($"Invalid script path: {exception.Message}"); }
                    break;
                }

                case "--shell":
                    shell = true;
                    break;

                case "--no-shell":
                    noShell = true;
                    break;

                case "--pause":
                    pause = true;
                    break;

                case "--help" or "-h" or "/?":
                    return EmulatorOptionsParseResult.Help();

                default:
                    return EmulatorOptionsParseResult.Failure($"Unknown host-debug argument '{argument}'.");
            }
        }

        if ((recoveryJournal is null) != (recoveryKey is null))
            return EmulatorOptionsParseResult.Failure("--recovery-journal and --recovery-key must be supplied together.");
        if (recoveryJournal is not null && !budget)
            return EmulatorOptionsParseResult.Failure("Recovery journal requires --budget to import conservative charges.");
        if (recoveryJournal is not null && string.Equals(recoveryJournal, recoveryKey, StringComparison.OrdinalIgnoreCase))
            return EmulatorOptionsParseResult.Failure("Recovery journal and key must be different files.");
        if (guestArtifact is not null && !hybridCpuChild)
            return EmulatorOptionsParseResult.Failure("--guest-artifact requires --platform hybridcpu-child.");
        if (deferCompute && hostModel != true)
            return EmulatorOptionsParseResult.Failure("--defer-compute requires --platform host-model.");
        if (replacementSip is not null && (sipRoot is null || sipPaths.Count != 1))
            return EmulatorOptionsParseResult.Failure("--replacement-sip requires exactly one --sip and --sip-root.");
        if (sipRoot is not null && sipPaths.Count != 1)
            return EmulatorOptionsParseResult.Failure("--sip-root requires exactly one --sip image.");
        if (snake && (script is not null || pause))
            return EmulatorOptionsParseResult.Failure("--game cannot be combined with --script or --pause.");
        if (shell && noShell)
            return EmulatorOptionsParseResult.Failure("--shell and --no-shell cannot be combined.");
        if (pause && (shell || script is not null))
            return EmulatorOptionsParseResult.Failure("--pause cannot be combined with --shell or --script.");

        return EmulatorOptionsParseResult.Success(new EmulatorOptions
        {
            SipPaths = sipPaths,
            RecoveryJournalPath = recoveryJournal,
            RecoveryKeyPath = recoveryKey,
            HostModelPlatform = hostModel ?? false,
            HybridCpuModelPlatform = hybridCpu,
            HybridCpuChildPlatform = hybridCpuChild,
            GuestArtifactPath = guestArtifact,
            DeferComputeCompletion = deferCompute,
            SipRoot = sipRoot,
            ReplacementSipPath = replacementSip,
            TraceEnabled = trace,
            TraceCapacity = traceCapacity,
            BudgetEnabled = budget,
            Demos = demos,
            ScriptPath = script,
            ShellRequested = shell,
            ShellSuppressed = noShell,
            SnakeRequested = snake,
            PauseRequested = pause,
        });
    }

    public static bool TryParseDemo(string name, out EmulatorDemo demo)
    {
        demo = default;
        if (string.Equals(name, "ipc", StringComparison.OrdinalIgnoreCase)) demo = EmulatorDemo.Ipc;
        else if (string.Equals(name, "regions", StringComparison.OrdinalIgnoreCase)) demo = EmulatorDemo.Regions;
        else if (string.Equals(name, "caps", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(name, "capabilities", StringComparison.OrdinalIgnoreCase)) demo = EmulatorDemo.Capabilities;
        else if (string.Equals(name, "lifecycle", StringComparison.OrdinalIgnoreCase)) demo = EmulatorDemo.Lifecycle;
        else if (string.Equals(name, "borrows", StringComparison.OrdinalIgnoreCase)) demo = EmulatorDemo.Borrows;
        else if (string.Equals(name, "platform", StringComparison.OrdinalIgnoreCase)) demo = EmulatorDemo.Platform;
        else if (string.Equals(name, "compute", StringComparison.OrdinalIgnoreCase)) demo = EmulatorDemo.Compute;
        else if (string.Equals(name, "compute-cancel", StringComparison.OrdinalIgnoreCase)) demo = EmulatorDemo.ComputeCancel;
        else if (string.Equals(name, "external", StringComparison.OrdinalIgnoreCase)) demo = EmulatorDemo.External;
        else if (string.Equals(name, "sessions", StringComparison.OrdinalIgnoreCase)) demo = EmulatorDemo.Sessions;
        else if (string.Equals(name, "service-restart", StringComparison.OrdinalIgnoreCase)) demo = EmulatorDemo.ServiceRestart;
        else if (string.Equals(name, "supervised-restart", StringComparison.OrdinalIgnoreCase)) demo = EmulatorDemo.SupervisedRestart;
        else if (string.Equals(name, "compute-fault", StringComparison.OrdinalIgnoreCase)) demo = EmulatorDemo.ComputeFault;
        else if (string.Equals(name, "platform-regions", StringComparison.OrdinalIgnoreCase)) demo = EmulatorDemo.PlatformRegions;
        else if (string.Equals(name, "child-execute", StringComparison.OrdinalIgnoreCase)) demo = EmulatorDemo.ChildExecute;
        else if (string.Equals(name, "virtual-domain", StringComparison.OrdinalIgnoreCase)) demo = EmulatorDemo.VirtualDomain;
        else if (string.Equals(name, "child-domain", StringComparison.OrdinalIgnoreCase)) demo = EmulatorDemo.ChildDomain;
        else if (string.Equals(name, "backing", StringComparison.OrdinalIgnoreCase)) demo = EmulatorDemo.Backing;
        else if (string.Equals(name, "hosted-service", StringComparison.OrdinalIgnoreCase)) demo = EmulatorDemo.HostedService;
        else if (string.Equals(name, "generated-sip", StringComparison.OrdinalIgnoreCase)) demo = EmulatorDemo.GeneratedSip;
        else return false;
        return true;
    }

    public static string DemoName(EmulatorDemo demo) => demo switch
    {
        EmulatorDemo.Ipc => "ipc",
        EmulatorDemo.Regions => "regions",
        EmulatorDemo.Capabilities => "caps",
        EmulatorDemo.Lifecycle => "lifecycle",
        EmulatorDemo.Borrows => "borrows",
        EmulatorDemo.VirtualDomain => "virtual-domain",
        EmulatorDemo.Platform => "platform",
        EmulatorDemo.HostedService => "hosted-service",
        EmulatorDemo.Compute => "compute",
        EmulatorDemo.ComputeCancel => "compute-cancel",
        EmulatorDemo.ComputeFault => "compute-fault",
        EmulatorDemo.PlatformRegions => "platform-regions",
        EmulatorDemo.External => "external",
        EmulatorDemo.Sessions => "sessions",
        EmulatorDemo.ChildExecute => "child-execute",
        EmulatorDemo.ChildDomain => "child-domain",
        EmulatorDemo.Backing => "backing",
        EmulatorDemo.GeneratedSip => "generated-sip",
        EmulatorDemo.ServiceRestart => "service-restart",
        EmulatorDemo.SupervisedRestart => "supervised-restart",
        _ => demo.ToString(),
    };

    public static void WriteHelp(TextWriter writer)
    {
        writer.WriteLine("SingNextOS launch emulator (SingPlus.Boot host-debug profile)");
        writer.WriteLine();
        writer.WriteLine("Usage: SingPlus.Boot [options]");
        writer.WriteLine();
        writer.WriteLine("  --sip <path>        Admit a managed assembly as a zero-authority SIP component.");
        writer.WriteLine("                      Repeatable; images are hashed (SHA-256) and admitted transactionally.");
        writer.WriteLine("  --guest-artifact <path> HCEXE package for child-execute; bounded to 64 KiB.");
        writer.WriteLine("  --platform <name>  none (default), host-model (host) or hybridcpu-model (hybridcpu); hybridcpu-child adds the executable child adapter.");
        writer.WriteLine("                      Host model is not physical CXL/HybridCPU or managed image execution.");
        writer.WriteLine("  --defer-compute    Host model debug knob: defer DSC1 completion (requires host-model).");
        writer.WriteLine("  --sip-root <root>  Require ManagedCap static verification before SIP admission.");
        writer.WriteLine("                      Root is Type::Method; requires exactly one --sip. No execution.");
        writer.WriteLine("  --replacement-sip <path>  Optional generation-2 image for supervised-restart; same static root.");
        writer.WriteLine("                            Requires one --sip and --sip-root. No CLR image execution.");
        writer.WriteLine("  --demo <name>       Run a built-in runtime workload after boot: ipc, regions, caps,");
        writer.WriteLine("                      lifecycle, borrows, platform, compute, compute-cancel, compute-fault, platform-regions, external, sessions, child-execute, child-domain, virtual-domain, backing, generated-sip, hosted-service, service-restart, supervised-restart, or 'all'. Repeatable and comma-separable.");
        writer.WriteLine("  --trace [capacity]  Start a deterministic trace session on the system process");
        writer.WriteLine($"                      (producer capacity 1..{MaximumTraceCapacity}, default {DefaultTraceCapacity}).");
        writer.WriteLine("  --budget            Configure the system resource budget and admit process budgets");
        writer.WriteLine("                      for the system process, admitted SIPs and emulator-spawned processes.");
        writer.WriteLine("  --recovery-journal <path>  Replay the authenticated runtime resource journal (--budget).");
        writer.WriteLine("  --recovery-key <path>      Existing binary 32-byte key; required with journal. No key generation.");
        writer.WriteLine("  --script <path>     Execute emulator commands from a script file after boot, then exit.");
        writer.WriteLine("  --game snake       Play interactive ASCII snake after boot; Q exits (requires terminal input).");
        writer.WriteLine("  --shell             Force the interactive kernel shell after boot.");
        writer.WriteLine("  --no-shell          Never enter the interactive shell (boot, then exit).");
        writer.WriteLine("  --pause             Legacy behavior: wait for Enter after a successful boot.");
        writer.WriteLine("  --help, -h, /?      Show this help text.");
        writer.WriteLine();
        writer.WriteLine("Exit codes: 0 success, 1 boot/script failure, 2 command-line usage error.");
        writer.WriteLine();
        writer.WriteLine("Without --script/--no-shell/--pause the emulator drops into the interactive shell");
        writer.WriteLine("whenever a debugger is attached or the console is interactive. Type 'help' inside");
        writer.WriteLine("the shell for the live command list.");
    }
}
