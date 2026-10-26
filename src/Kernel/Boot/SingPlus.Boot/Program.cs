using System.Diagnostics;
using SingPlus.Boot;
using SingPlus.Boot.Demos;
using SingPlus.Kernel;
using SingPlus.Kernel.Hal;

internal class Program
{
    private static int Main(string[] args)
    {
        KernelConsole.Configure(new HostKernelConsole());
        EmulatorOptionsParseResult parsed = EmulatorOptions.Parse(args);
        if (parsed.ShowHelp)
        {
            EmulatorOptions.WriteHelp(Console.Out);
            return 0;
        }

        if (parsed.Options is null)
        {
            Console.Error.WriteLine($"[emulator] {parsed.Error}");
            EmulatorOptions.WriteHelp(Console.Error);
            return 2;
        }

        IReadOnlyList<IKernelBootStage> stages = HostDebugBootPipeline.Create(parsed.Options, out EmulatorSession? session);
        try
        {
            int exitCode = KernelEntryPoint.Run(stages);
            if (exitCode != 0) return exitCode;

            foreach (EmulatorDemo demo in session.Options.Demos)
            {
                string name = EmulatorOptions.DemoName(demo);
                EmulatorConsole.Info($"[demo] running '{name}'\r\n");
                KernelBootStageResult result = DemoWorkloads.Run(demo, session);
                if (!result.IsSuccess)
                {
                    EmulatorConsole.Error($"[demo] '{name}' failed: {result.Error}\r\n");
                    return 1;
                }
                EmulatorConsole.Ok($"[demo] '{name}' passed\r\n");
            }

            if (session.Options.SnakeRequested)
                return SnakeGame.Run(session, null) ? 0 : 1;

            if (session.Options.ScriptPath is { } scriptPath)
                return EmulatorShell.RunScript(session, scriptPath);

            if (session.Options.WantsInteractiveShell(Debugger.IsAttached))
            {
                EmulatorShell.RunInteractive(session);
                return 0;
            }

            if (session.Options.PauseRequested)
            {
                KernelConsole.Write("[boot] press Enter to stop\r\n".AsSpan());
                Console.ReadLine();
            }

            return 0;
        }
        finally
        {
            session.Shutdown(message => EmulatorConsole.Detail($"[emulator] shutdown: {message}\r\n"));
        }
    }
}
