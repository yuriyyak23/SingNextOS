using System.Diagnostics;

namespace SingPlus.HybridCpuQualification;

public sealed record SuspendedChildResult(bool Supported, int? ExitCode, string Output, string Reason);

public static class SuspendedChildQualification
{
    // Host qualification only. StartSuspended has no SingNextOS process authority meaning.
    public static async Task<SuspendedChildResult> RunSdkProbeAsync(string workingDirectory, Action<int> attachController, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(attachController);
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            return new SuspendedChildResult(false, null, "", "ProcessStartInfo.StartSuspended is supported only on Windows/macOS.");

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetFullPath(workingDirectory),
            StartSuspended = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--version");
        // The child's environment is fixed at creation. Configure it before suspended start.
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Suspended SDK probe could not start.");
        try
        {
            attachController(process.Id);
            process.SafeHandle.Resume();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;
            return new SuspendedChildResult(true, process.ExitCode, output.Trim(), error.Trim());
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
