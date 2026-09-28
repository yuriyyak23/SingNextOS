using System.Diagnostics;
using SingPlus.Boot;
using SingPlus.Kernel;
using SingPlus.Kernel.Hal;

internal class Program
{
    private static int Main(string[] args)
    {
KernelConsole.Configure(new HostKernelConsole());
var exitCode = KernelEntryPoint.Run(HostDebugBootPipeline.Create(args));

        if (exitCode == 0 && Debugger.IsAttached)
        {
            KernelConsole.Write("[boot] debugger attached; press Enter to stop\r\n".AsSpan());
            Console.ReadLine();
        }

        return exitCode;
    }
}
