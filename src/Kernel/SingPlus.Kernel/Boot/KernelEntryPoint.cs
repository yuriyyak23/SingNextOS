namespace SingPlus.Kernel;

public static class KernelEntryPoint
{
    public static int Run()
    {
        KernelConsole.Write("Sing+\r\n".AsSpan());
        return 0;
    }
}
