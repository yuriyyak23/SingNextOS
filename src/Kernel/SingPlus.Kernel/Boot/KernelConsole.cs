namespace SingPlus.Kernel;

public static class KernelConsole
{
    public static void Write(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Console.Write(message);
    }
}
