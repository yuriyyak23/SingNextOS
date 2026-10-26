using SingPlus.HybridCpuQualification;
using System.Diagnostics;

namespace SingPlus.Tests.Qualification;

public sealed class SuspendedChildQualificationTests
{
    [SuspendedChildFact]
    public async Task OptInSuspendedSdkProbeHasDeterministicExitDisposition()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        bool attached = false;
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        SuspendedChildResult result = await SuspendedChildQualification.RunSdkProbeAsync(root, id =>
        {
            Assert.True(id > 0);
            attached = true;
        }, timeout.Token);
        Assert.True(result.Supported, result.Reason);
        Assert.True(attached);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("11.0.100-rc.1.26425.128", result.Output);
    }

    [SuspendedChildFact]
    public async Task AttachFailureTerminatesSuspendedChild()
    {
        int childId = 0;
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SuspendedChildQualification.RunSdkProbeAsync(root, id =>
            {
                childId = id;
                throw new InvalidOperationException("Qualification controller attach failed.");
            }, timeout.Token));
        Assert.Equal("Qualification controller attach failed.", failure.Message);
        Assert.True(childId > 0);
        try
        {
            using Process child = Process.GetProcessById(childId);
            Assert.True(child.WaitForExit(10_000), "Attach failure left the suspended qualification child running.");
        }
        catch (ArgumentException)
        {
            // The OS already removed the terminated child from its process table.
        }
    }

    public sealed class SuspendedChildFactAttribute : FactAttribute
    {
        public SuspendedChildFactAttribute()
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
                Skip = "ProcessStartInfo.StartSuspended is supported only on Windows/macOS.";
            else if (Environment.GetEnvironmentVariable("SINGPLUS_RUN_SUSPENDED_CHILD_QUALIFICATION") != "1")
                Skip = "Set SINGPLUS_RUN_SUSPENDED_CHILD_QUALIFICATION=1 to run this host-only scenario.";
        }
    }
}
