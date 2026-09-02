namespace SingPlus.Kernel;

public interface IKernelConsole
{
    void Write(ReadOnlySpan<char> text);
}
