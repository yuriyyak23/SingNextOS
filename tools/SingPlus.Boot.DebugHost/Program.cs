using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using SingPlus.Boot;
using SingPlus.Kernel;
using SingPlus.Kernel.Hal;

var options = DebugHostOptions.Parse(args);
if (!options.IsSuccess)
{
    Console.Error.WriteLine(options.Error);
    return 2;
}

KernelConsole.Configure(new HostKernelConsole());
var bootArguments = options.SipAssemblies
    .SelectMany(static path => new[] { "--sip", path })
    .ToArray();
var bootResult = KernelEntryPoint.Run(HostDebugBootPipeline.Create(bootArguments));
if (bootResult != 0) return bootResult;

var runtimeExercise = await DebugRuntimeExercise.RunAsync();
if (!runtimeExercise.IsSuccess)
{
    Console.Error.WriteLine($"[debug-host] runtime exercise failed: {runtimeExercise.Error}");
    return 1;
}

if (options.SipAssemblies.Count == 0) return 0;

foreach (var assembly in options.SipAssemblies)
{
    var execution = options.Mode == DebugExecutionMode.InProcess
        ? await DebugSipExecutor.InvokeInProcessAsync(assembly, options.SipArguments)
        : await DebugSipExecutor.InvokeChildProcessAsync(assembly, options.SipArguments);
    if (!execution.IsSuccess)
    {
        Console.Error.WriteLine($"[debug-host] execution failed: {execution.Error}");
        return 1;
    }
}

Console.WriteLine("[debug-host] all requested SIP debug executions completed.");
return 0;

internal enum DebugExecutionMode
{
    InProcess,
    ChildProcess,
}

internal sealed record DebugHostOptions(
    bool IsSuccess,
    string? Error,
    DebugExecutionMode Mode,
    IReadOnlyList<string> SipAssemblies,
    IReadOnlyList<string> SipArguments)
{
    internal static DebugHostOptions Parse(IReadOnlyList<string> arguments)
    {
        var mode = DebugExecutionMode.InProcess;
        var assemblies = new List<string>();
        var sipArguments = new List<string>();
        var forwarding = false;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (forwarding)
            {
                sipArguments.Add(argument);
                continue;
            }
            if (argument == "--")
            {
                forwarding = true;
                continue;
            }
            if (argument == "--sip")
            {
                if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
                    return Failure("--sip requires an assembly path.");
                assemblies.Add(arguments[index]);
                continue;
            }
            if (argument == "--mode")
            {
                if (++index >= arguments.Count)
                    return Failure("--mode requires 'in-process' or 'child-process'.");
                if (arguments[index] == "in-process") mode = DebugExecutionMode.InProcess;
                else if (arguments[index] == "child-process") mode = DebugExecutionMode.ChildProcess;
                else return Failure("--mode accepts only 'in-process' or 'child-process'.");
                continue;
            }
            return Failure($"Unknown argument '{argument}'.");
        }

        return new(true, null, mode, assemblies, sipArguments);
    }

    private static DebugHostOptions Failure(string error) =>
        new(false, error, DebugExecutionMode.InProcess, [], []);
}

internal readonly record struct DebugExecutionResult(bool IsSuccess, string? Error)
{
    internal static DebugExecutionResult Success() => new(true, null);
    internal static DebugExecutionResult Failure(string error) => new(false, error);
}

internal static class DebugSipExecutor
{
    internal static async Task<DebugExecutionResult> InvokeInProcessAsync(string path, IReadOnlyList<string> arguments)
    {
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { return DebugExecutionResult.Failure($"Invalid assembly path: {exception.Message}"); }
        if (!File.Exists(fullPath)) return DebugExecutionResult.Failure($"Assembly '{fullPath}' does not exist.");

        var resolver = new AssemblyDependencyResolver(fullPath);
        var context = new AssemblyLoadContext($"singplus-debug-sip-{Guid.NewGuid():N}", isCollectible: true);
        context.Resolving += (_, name) =>
        {
            var dependency = resolver.ResolveAssemblyToPath(name);
            return dependency is null ? null : context.LoadFromAssemblyPath(dependency);
        };

        try
        {
            var assembly = context.LoadFromAssemblyPath(fullPath);
            var entry = assembly.EntryPoint;
            if (entry is null)
                return DebugExecutionResult.Failure("Managed SIP assembly has no executable entry point; use child-process only for an executable artifact.");

            Console.WriteLine($"[debug-host] in-process invocation: {assembly.GetName().Name}");
            var returned = InvokeEntryPoint(entry, arguments.ToArray());
            var exitCode = await AwaitExitCodeAsync(returned);
            return exitCode == 0
                ? DebugExecutionResult.Success()
                : DebugExecutionResult.Failure($"SIP entry point returned exit code {exitCode}.");
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            return DebugExecutionResult.Failure($"SIP entry point threw {exception.InnerException.GetType().Name}: {exception.InnerException.Message}");
        }
        catch (Exception exception) when (exception is BadImageFormatException or FileLoadException or FileNotFoundException or InvalidOperationException)
        {
            return DebugExecutionResult.Failure($"Unable to invoke SIP assembly: {exception.Message}");
        }
        finally
        {
            context.Unload();
        }
    }

    internal static async Task<DebugExecutionResult> InvokeChildProcessAsync(string path, IReadOnlyList<string> arguments)
    {
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { return DebugExecutionResult.Failure($"Invalid assembly path: {exception.Message}"); }
        if (!File.Exists(fullPath)) return DebugExecutionResult.Failure($"Assembly '{fullPath}' does not exist.");

        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            FileName = string.Equals(Path.GetExtension(fullPath), ".dll", StringComparison.OrdinalIgnoreCase)
                ? "dotnet"
                : fullPath,
        };
        if (string.Equals(Path.GetExtension(fullPath), ".dll", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(fullPath);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(start);
            if (process is null) return DebugExecutionResult.Failure("Child process could not be created.");
            Console.WriteLine($"[debug-host] child-process invocation: {fullPath}");
            await process.WaitForExitAsync();
            return process.ExitCode == 0
                ? DebugExecutionResult.Success()
                : DebugExecutionResult.Failure($"Child process returned exit code {process.ExitCode}.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return DebugExecutionResult.Failure($"Unable to start child process: {exception.Message}");
        }
    }

    private static object? InvokeEntryPoint(MethodInfo entry, string[] arguments)
    {
        var parameters = entry.GetParameters();
        return parameters.Length switch
        {
            0 => entry.Invoke(null, null),
            1 when parameters[0].ParameterType == typeof(string[]) => entry.Invoke(null, [arguments]),
            _ => throw new InvalidOperationException("SIP entry point must have no parameters or exactly one string[] parameter."),
        };
    }

    private static async Task<int> AwaitExitCodeAsync(object? returned) => returned switch
    {
        null => 0,
        int code => code,
        Task task => await AwaitTaskAsync(task),
        _ => throw new InvalidOperationException($"Unsupported SIP entry-point return type '{returned.GetType().FullName}'."),
    };

    private static async Task<int> AwaitTaskAsync(Task task)
    {
        await task;
        var type = task.GetType();
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>) &&
               type.GetGenericArguments()[0] == typeof(int)
            ? (int)(type.GetProperty("Result")!.GetValue(task)!)
            : 0;
    }
}
