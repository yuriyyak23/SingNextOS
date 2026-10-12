using SingPlus.Apps.Snake;
using SingPlus.Apps.Snake.Host;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Boot;

internal static class SnakeGame
{
    public static bool Run(EmulatorSession session, string? moves)
    {
        if (session.Runtime is not { } runtime) return Error("Runtime is unavailable.");
        if (moves is null && Console.IsInputRedirected)
            return Error("Interactive snake requires terminal input; use snake <WASDRQ moves> for scripts.");
        if (moves is not null && (moves.Length > 4096 || moves.Any(ch => !"wasdrqWASDRQ".Contains(ch))))
            return Error("Expected at most 4096 WASD/R/Q characters.");
        var caller = session.SpawnProcess("singplus.game.snake.client", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!caller.IsSuccess) return Error($"{caller.Error}: {caller.Message}");
        ProcessHandle? serverHandle = null;
        EndpointSessionHandle? openedHandle = null;
        var success = false;
        SnakeServiceHost? executor = null;
        try
        {
            KernelResult<ProcessHandle> server = session.SpawnProcess("singplus.game.snake.service", ExecutionRole.Sip, MemoryProfile.SipRegion);
            if (!server.IsSuccess) return Error($"{server.Error}: {server.Message}");
            serverHandle = server.Value;
            if (session.Options.TraceEnabled)
            {
                foreach (var owner in new[] { caller.Value, server.Value })
                {
                    var trace = session.StartTrace(owner, session.Options.TraceCapacity);
                    if (!trace.IsSuccess) return Error($"{trace.Error}: {trace.Message}");
                }
            }
            ProtocolDefinitionV1 protocol = SnakeBindings.Protocol;
            var resource = $"singplus.game.snake:{server.Value.ProcessId.Value}";
            var registered = runtime.RegisterService(server.Value, resource,
                new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest), protocol,
                SnakeBindings.Responses, [new CapabilityRequirementV1(ResourceKind.KernelService, resource, CapabilityRights.Read)]);
            if (!registered.IsSuccess) return Error($"{registered.Error}: {registered.Message}");
            var principal = runtime.Processes.Resolve(caller.Value);
            if (!principal.IsSuccess) return Error($"{principal.Error}: {principal.Message}");
            var authority = runtime.MintCapability(principal.Value!.DomainId, caller.Value,
                ResourceKind.KernelService, resource, CapabilityRights.Read);
            if (!authority.IsSuccess) return Error($"{authority.Error}: {authority.Message}");
            var opened = runtime.OpenSession(caller.Value, registered.Value, [authority.Value!.CapabilityId]);
            if (!opened.IsSuccess) return Error($"{opened.Error}: {opened.Message}");
            openedHandle = opened.Value;
            var hosted = SnakeServiceHost.Start(runtime, server.Value, opened.Value);
            if (!hosted.IsSuccess) return Error($"{hosted.Error}: {hosted.Message}");
            executor = hosted.Value!;
            using var timeout = new CancellationTokenSource();
            var transport = new SnakeClientTransport(runtime, caller.Value, opened.Value, executor, timeout);
            var client = SnakeBindings.CreateClient(transport);
            EmulatorConsole.Info($"[snake] storage Region={executor.Storage.RegionId.Value}: 160 Int32 cells, 640 runtime-accounted bytes; SIP role/SipRegion profile\r\n");
            EmulatorConsole.Info($"[snake] caller={caller.Value.ProcessId.Value} server={server.Value.ProcessId.Value} session={opened.Value.SessionId.Value}; generated SIP, restricted child execution, runtime-owned snapshot Region\r\n");
            EmulatorConsole.Info($"[snake] Windows child PID={executor.ChildProcessId}; low integrity, disabled privileges; Job: 1 process, 512 MiB committed memory, CPU cap 20%, kill-on-close; reads/network are not fully sandboxed\r\n");
            if (moves is not null)
            {
                EmulatorConsole.Info("ASCII SNAKE | scripted steps; R = restart; Q = quit\r\n");
                Render(client.Step(new SnakeMove(0)));
                Play(moves);
            }
            else RunInteractive(client);
            EmulatorConsole.Ok($"[snake] finished; generated runtime calls={transport.DispatchCount}\r\n");
            success = true;

            bool Play(string input)
            {
                foreach (var key in input)
                {
                    var ch = char.ToUpperInvariant(key);
                    if (ch == 'Q') return false;
                    var direction = ch switch { 'W' => 1, 'D' => 2, 'S' => 3, 'A' => 4, 'R' => 5, _ => 0 };
                    Render(client.Step(new SnakeMove(direction)));
                }
                return true;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException)
        { Error($"Invocation failed: {exception.Message}"); }
        finally
        {
            if (executor is not null)
            {
                try
                {
                    var stopped = executor.Stop();
                    if (!stopped.IsSuccess) { Error($"Storage cleanup: {stopped.Error}: {stopped.Message}"); success = false; }
                    else EmulatorConsole.Info("[snake] worker joined; runtime Region released before process reclaim; restricted child termination confirmed\r\n");
                }
                catch (InvalidOperationException exception)
                {
                    Error($"Worker stopped with failure: {exception.Message}; owner teardown retains cleanup responsibility.");
                    success = false;
                }
            }
            if (openedHandle is { } handle)
            {
                var closed = runtime.CloseSession(caller.Value, handle);
                if (!closed.IsSuccess) { Error($"Session cleanup: {closed.Error}: {closed.Message}"); success = false; }
            }
            foreach (var owner in serverHandle is { } server ? new[] { caller.Value, server } : new[] { caller.Value })
            {
                var reclaimed = session.TerminateAndReclaim(owner);
                if (!reclaimed.IsSuccess) { Error($"Process cleanup: {reclaimed.Error}: {reclaimed.Message}"); success = false; }
            }
        }
        return success;
    }

    private static void RunInteractive(ISnakeService client)
    {
        if (Console.IsOutputRedirected)
            throw new InvalidOperationException("Interactive snake requires terminal output; use scripted moves.");
        int direction = 2;
        var pending = direction;
        var frame = client.Step(new SnakeMove(0));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var nextStep = clock.ElapsedMilliseconds + 500;
        var cursorVisible = OperatingSystem.IsWindows() ? Console.CursorVisible : true;
        try
        {
            Console.CursorVisible = false;
            Draw();
            while (true)
            {
                while (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key is ConsoleKey.Q or ConsoleKey.Escape) return;
                    if (key.Key == ConsoleKey.R)
                    {
                        frame = client.Step(new SnakeMove(5));
                        direction = pending = 2;
                        nextStep = clock.ElapsedMilliseconds + 500;
                        Draw();
                        continue;
                    }
                    var candidate = key.Key switch
                    {
                        ConsoleKey.W or ConsoleKey.UpArrow => 1,
                        ConsoleKey.D or ConsoleKey.RightArrow => 2,
                        ConsoleKey.S or ConsoleKey.DownArrow => 3,
                        ConsoleKey.A or ConsoleKey.LeftArrow => 4,
                        _ => 0,
                    };
                    if (candidate != 0 && candidate != ((direction + 1) % 4) + 1)
                        pending = candidate;
                }
                if (frame.Status == 0 && clock.ElapsedMilliseconds >= nextStep)
                {
                    direction = pending;
                    frame = client.Step(new SnakeMove(direction));
                    nextStep = clock.ElapsedMilliseconds + 500;
                    Draw();
                }
                Thread.Sleep(15);
            }
        }
        finally { Console.CursorVisible = cursorVisible; }

        void Draw()
        {
            Console.Clear();
            Console.WriteLine("SingNextOS ASCII SNAKE | WASD / arrows (no Enter)");
            Console.WriteLine("R restart | Q / Esc quit | @ head, o body, * food");
            Render(frame);
        }
    }
    private static void Render(SnakeFrame frame)
    {
        Console.WriteLine($"+{new string('-', SnakeBindings.Width)}+  Score: {frame.Score}");
        for (var y = 0; y < SnakeBindings.Height; y++)
        {
            Console.Write('|');
            for (var x = 0; x < SnakeBindings.Width; x++)
            {
                var cell = y * SnakeBindings.Width + x;
                var bits = cell < 64 ? frame.Cells0 : cell < 128 ? frame.Cells1 : frame.Cells2;
                var occupied = (bits & (1UL << (cell % 64))) != 0;
                Console.Write(cell == frame.Head ? '@' : occupied ? 'o' : cell == frame.Food ? '*' : ' ');
            }
            Console.WriteLine('|');
        }
        Console.WriteLine($"+{new string('-', SnakeBindings.Width)}+");
        Console.WriteLine(frame.Status switch { 1 => "GAME OVER - R to restart, Q to quit", 2 => "YOU WIN - R to restart, Q to quit", _ => "Ready" });
    }

    private static bool Error(string message)
    {
        EmulatorConsole.Error($"[snake] {message}\r\n");
        return false;
    }

    private sealed class SnakeClientTransport(RuntimeKernel runtime, ProcessHandle caller,
        EndpointSessionHandle session, SnakeServiceHost executor, CancellationTokenSource timeout) : ISipClientRuntimeTransport
    {
        private readonly RuntimeSipClientTransport _transport = new(runtime, caller, session, timeout.Token);
        public int DispatchCount => executor.DispatchCount;
        public ResponseEnvelope Invoke(uint id, object? payload = null) => InvokeAsync(id, payload).AsTask().GetAwaiter().GetResult();
        public ValueTask<ResponseEnvelope> InvokeAsync(uint id, object? payload = null) => InvokeAsync(id, payload, timeout.Token);
        public async ValueTask<ResponseEnvelope> InvokeAsync(uint id, object? payload, CancellationToken token)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var response = _transport.InvokeAsync(id, payload, token).AsTask();
                if (await Task.WhenAny(response, executor.Completion).ConfigureAwait(false) == executor.Completion)
                {
                    await executor.Completion.ConfigureAwait(false);
                    throw new InvalidOperationException("Snake service stopped before response.");
                }
                return await response.ConfigureAwait(false);
            }
            finally { timeout.CancelAfter(Timeout.InfiniteTimeSpan); }
        }
        public ResponseEnvelope InvokeOwnershipPair(uint id, object first, object second) => throw new NotSupportedException();
        public ValueTask<ResponseEnvelope> InvokeOwnershipPairAsync(uint id, object first, object second) => throw new NotSupportedException();
        public ValueTask<ResponseEnvelope> InvokeOwnershipPairAsync(uint id, object first, object second, CancellationToken token) => throw new NotSupportedException();
    }
}