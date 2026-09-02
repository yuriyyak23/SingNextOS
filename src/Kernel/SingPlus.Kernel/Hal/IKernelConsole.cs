namespace SingPlus.Kernel.Hal;

public interface IKernelConsole
{
    void Write(ReadOnlySpan<char> text);
}
