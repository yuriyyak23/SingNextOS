namespace SingPlus.Kernel;

public static class KernelEntryPoint
{
    public static int Run()
    {
        KernelConsole.Write("Sing+\r\n".AsSpan());
        return 0;
    }

    public static int Run(IReadOnlyList<IKernelBootStage> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);

        KernelConsole.Write("Sing+\r\n".AsSpan());

        foreach (var stage in stages)
        {
            if (stage is null || string.IsNullOrWhiteSpace(stage.Name))
            {
                KernelConsole.Write("[boot] invalid stage descriptor\r\n".AsSpan());
                return 1;
            }

            KernelConsole.Write("[boot] ".AsSpan());
            KernelConsole.Write(stage.Name.AsSpan());
            KernelConsole.Write(" ... ".AsSpan());

            var result = stage.Execute();
            if (!result.IsSuccess)
            {
                KernelConsole.Write("failed: ".AsSpan());
                KernelConsole.Write((result.Error ?? "unspecified failure").AsSpan());
                KernelConsole.Write("\r\n".AsSpan());
                return 1;
            }

            KernelConsole.Write("ok\r\n".AsSpan());
        }

        KernelConsole.Write("[boot] host-debug runtime ready\r\n".AsSpan());
        return 0;
    }
}
