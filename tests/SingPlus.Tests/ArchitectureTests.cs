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

    private sealed class RecordingConsole : IKernelConsole
    {
        public string Text { get; private set; } = string.Empty;
        public void Write(ReadOnlySpan<char> text) => Text += text.ToString();
    }
}
