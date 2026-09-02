namespace SingPlus.Drivers;

public interface IConsoleDriver
{
    void Write(ReadOnlySpan<char> text);
}
