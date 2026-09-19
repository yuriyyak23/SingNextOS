using SingPlus.Kernel;
using SingPlus.Kernel.Hal;

namespace SingPlus.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    [Trait("Category", "Runtime")]
    public void KernelBusinessLogicUsesHalContract()
    {
        var console = new RecordingConsole();
        KernelConsole.Configure(console);
        Assert.Equal(0, KernelEntryPoint.Run());
        Assert.Equal("Sing+\r\n", console.Text);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void KernelBootStagesExecuteInOrderAndPublishReadyOnlyAfterSuccess()
    {
        var console = new RecordingConsole();
        KernelConsole.Configure(console);
        var order = new List<string>();

        var result = KernelEntryPoint.Run(
        [
            new Stage("first", () => { order.Add("first"); return KernelBootStageResult.Success(); }),
            new Stage("second", () => { order.Add("second"); return KernelBootStageResult.Success(); })
        ]);

        Assert.Equal(0, result);
        Assert.Equal(["first", "second"], order);
        Assert.Contains("[boot] host-debug runtime ready\r\n", console.Text);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void KernelBootStopsAtFirstFailedStage()
    {
        var console = new RecordingConsole();
        KernelConsole.Configure(console);
        var laterExecuted = false;

        var result = KernelEntryPoint.Run(
        [
            new Stage("failure", () => KernelBootStageResult.Failure("denied")),
            new Stage("later", () => { laterExecuted = true; return KernelBootStageResult.Success(); })
        ]);

        Assert.Equal(1, result);
        Assert.False(laterExecuted);
        Assert.Contains("[boot] failure ... failed: denied\r\n", console.Text);
        Assert.DoesNotContain("runtime ready", console.Text);
    }

    private sealed class RecordingConsole : IKernelConsole
    {
        public string Text { get; private set; } = string.Empty;
        public void Write(ReadOnlySpan<char> text) => Text += text.ToString();
    }

    private sealed class Stage(string name, Func<KernelBootStageResult> execute) : IKernelBootStage
    {
        public string Name { get; } = name;
        public KernelBootStageResult Execute() => execute();
    }
}
