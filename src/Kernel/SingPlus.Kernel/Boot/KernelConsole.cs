using SingPlus.Kernel.Hal;

namespace SingPlus.Kernel;

public static class KernelConsole
{
    private static IKernelConsole? _console;

    public static void Configure(IKernelConsole console) => _console = console;

    public static void Write(ReadOnlySpan<char> text)
    {
        _console?.Write(text);
    }
}
