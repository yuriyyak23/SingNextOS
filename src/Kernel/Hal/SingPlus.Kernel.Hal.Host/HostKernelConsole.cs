namespace SingPlus.Kernel.Hal;

public sealed class HostKernelConsole : IKernelConsole
{
    public void Write(ReadOnlySpan<char> text) => Console.Write(text.ToString());
}
