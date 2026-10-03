namespace SingPlus.Boot;

/// <summary>Built-in emulator workloads that double as runtime self-checks.</summary>
public enum EmulatorDemo
{
    Ipc,
    Regions,
    Capabilities,
    Lifecycle,
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
    public bool TraceEnabled { get; private init; }
    public int TraceCapacity { get; private init; } = DefaultTraceCapacity;
    public bool BudgetEnabled { get; private init; }
    public IReadOnlyList<EmulatorDemo> Demos { get; private init; } = [];
    public string? ScriptPath { get; private init; }
    public bool ShellRequested { get; private init; }
    public bool ShellSuppressed { get; private init; }
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

        var sipPaths = new List<string>();
        var sipPathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var demos = new List<EmulatorDemo>();
        var trace = false;
        var traceCapacity = DefaultTraceCapacity;
        var budget = false;
        string? script = null;
        var shell = false;
        var noShell = false;
        var pause = false;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
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

                case "--demo":
                {
                    if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
                        return EmulatorOptionsParseResult.Failure("--demo requires a workload name (ipc, regions, caps, lifecycle, all).");

                    foreach (var name in arguments[index].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (string.Equals(name, "all", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (var availableDemo in Enum.GetValues<EmulatorDemo>())
                                if (!demos.Contains(availableDemo)) demos.Add(availableDemo);
                            continue;
                        }

                        if (!TryParseDemo(name, out var demo))
                            return EmulatorOptionsParseResult.Failure($"Unknown demo workload '{name}'. Available: ipc, regions, caps, lifecycle, all.");
                        if (!demos.Contains(demo)) demos.Add(demo);
                    }
                    break;
                }

                case "--trace":
                {
                    trace = true;
                    if (index + 1 < arguments.Count &&
                        int.TryParse(arguments[index + 1], System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out var capacity))
                    {
                        index++;
                        if (capacity <= 0 || capacity > MaximumTraceCapacity)
                            return EmulatorOptionsParseResult.Failure(
                                $"--trace capacity must be between 1 and {MaximumTraceCapacity}.");
                        traceCapacity = capacity;
                    }
                    break;
                }

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

        if (shell && noShell)
            return EmulatorOptionsParseResult.Failure("--shell and --no-shell cannot be combined.");
        if (pause && (shell || script is not null))
            return EmulatorOptionsParseResult.Failure("--pause cannot be combined with --shell or --script.");

        return EmulatorOptionsParseResult.Success(new EmulatorOptions
        {
            SipPaths = sipPaths,
            TraceEnabled = trace,
            TraceCapacity = traceCapacity,
            BudgetEnabled = budget,
            Demos = demos,
            ScriptPath = script,
            ShellRequested = shell,
            ShellSuppressed = noShell,
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
        else return false;
        return true;
    }

    public static string DemoName(EmulatorDemo demo) => demo switch
    {
        EmulatorDemo.Ipc => "ipc",
        EmulatorDemo.Regions => "regions",
        EmulatorDemo.Capabilities => "caps",
        EmulatorDemo.Lifecycle => "lifecycle",
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
        writer.WriteLine("  --demo <name>       Run a built-in runtime workload after boot: ipc, regions, caps,");
        writer.WriteLine("                      lifecycle, or 'all'. Repeatable and comma-separable.");
        writer.WriteLine("  --trace [capacity]  Start a deterministic trace session on the system process");
        writer.WriteLine($"                      (producer capacity 1..{MaximumTraceCapacity}, default {DefaultTraceCapacity}).");
        writer.WriteLine("  --budget            Configure the system resource budget and admit process budgets");
        writer.WriteLine("                      for the system process and every admitted SIP.");
        writer.WriteLine("  --script <path>     Execute emulator commands from a script file after boot, then exit.");
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
