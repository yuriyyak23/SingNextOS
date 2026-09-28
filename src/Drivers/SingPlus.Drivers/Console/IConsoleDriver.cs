namespace SingPlus.Drivers;

public interface IConsoleDriver
{
    void Write(ReadOnlySpan<char> text);
}

public sealed class HostConsoleDriver : IConsoleDriver
{
    public void Write(ReadOnlySpan<char> text) => Console.Write(text.ToString());
}
