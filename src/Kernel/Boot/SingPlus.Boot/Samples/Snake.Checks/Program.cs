using System.Diagnostics;
using SingPlus.Apps.Snake;
using SingPlus.Apps.Snake.Host;
using SingPlus.Boot;
using SingPlus.Contracts;
using SingPlus.Kernel;
using SingPlus.Runtime;
using SingPlus.Sip;

EmulatorOptions options = EmulatorOptions.Parse(["--budget", "--trace", "--no-shell"]).Options!;
IReadOnlyList<IKernelBootStage> stages = HostDebugBootPipeline.Create(options, out EmulatorSession? session);
Require(KernelEntryPoint.Run(stages) == 0, "boot");
try
{
    for (int iteration = 0; iteration < 4; iteration++) await CheckService(iteration);
    Console.WriteLine("Snake checks passed: separate child, dispatch, snapshot budget, growth, revocation, child exit/refusal, stop/death/release, restart and reclaim.");
}
finally { session.Shutdown(Console.WriteLine); }

async Task CheckService(int iteration)
{
    RuntimeKernel runtime = session.Runtime!;
    ProcessHandle caller = session.SpawnProcess("snake.check.client", ExecutionRole.User, MemoryProfile.ManagedGc).Value;
    ProcessHandle server = session.SpawnProcess("snake.check.service", ExecutionRole.Sip, MemoryProfile.SipRegion).Value;
    Require(session.StartTrace(caller, 256).IsSuccess, "client trace");
    Require(session.StartTrace(server, 256).IsSuccess, "server trace");
    ProtocolDefinitionV1 protocol = SnakeBindings.Protocol;
    string resource = $"snake.check:{iteration}";
    KernelResult<ServiceEndpointDescriptor> registered = runtime.RegisterService(server, resource,
        new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest), protocol, SnakeBindings.Responses,
        [new CapabilityRequirementV1(ResourceKind.KernelService, resource, CapabilityRights.Read)]);
    Require(registered.IsSuccess, "register");
    SingProcess process = runtime.Processes.Resolve(caller).Value!;
    CapabilityDescriptorV1 cap = runtime.MintCapability(process.DomainId, caller, ResourceKind.KernelService, resource, CapabilityRights.Read).Value!;
    KernelResult<EndpointSessionHandle> opened = runtime.OpenSession(caller, registered.Value, [cap.CapabilityId]);
    Require(opened.IsSuccess, "open");
    KernelResult<SnakeServiceHost> started = SnakeServiceHost.Start(runtime, server, opened.Value);
    Require(started.IsSuccess, $"worker start: {started.Error}: {started.Message}");
    SnakeServiceHost host = started.Value!;
    using Process child = System.Diagnostics.Process.GetProcessById(host.ChildProcessId);
    Require(child.Id != Environment.ProcessId && !child.HasExited, "separate live child process");
    try
    {
        BudgetAccountHandle account = session.BudgetAccounts[server.ProcessId.Value].ProcessBudget;
        ulong MemoryUsed() => runtime.QueryBudget(account).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used;
        Require(MemoryUsed() == 640, "runtime storage charged 640");
        Require(runtime.Processes.Resolve(server).Value!.Regions.Contains(host.Storage), "region belongs to server");
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        ISnakeService client = SnakeBindings.CreateClient(new RuntimeSipClientTransport(runtime, caller, opened.Value, timeout.Token));
        Require(client.Step(new SnakeMove(0)).Score == 0, "initial snapshot");
        client.Step(new SnakeMove(2));
        client.Step(new SnakeMove(2));
        Require(client.Step(new SnakeMove(2)).Score == 1, "worker growth");
        using (CancellationTokenSource cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try
            {
                await new RuntimeSipClientTransport(runtime, caller, opened.Value, cancelled.Token)
                    .InvokeAsync(SnakeBindings.StepMessageId, new SnakeMove(2), cancelled.Token);
                throw new Exception("Cancelled client call unexpectedly completed.");
            }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
        }
        Require(runtime.RevokeCapability(cap.CapabilityId).IsSuccess, "revoke");
        // Wait until successful publication accounting completes on the worker.
        Require(SpinWait.SpinUntil(() => host.DispatchCount == 4, TimeSpan.FromSeconds(1)), "dispatch count");
        try
        {
            client.Step(new SnakeMove(2));
            throw new Exception("Revoked capability invoked game.");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("CapabilityRevoked", StringComparison.Ordinal)) { }
        Require(host.DispatchCount == 4, "revoked call did not dispatch");
        if (iteration == 2)
        {
            child.Kill();
            Require(child.WaitForExit(1000), "forced child exit");
            try
            {
                await host.Completion.WaitAsync(TimeSpan.FromSeconds(2));
                throw new Exception("Exited child was not reported.");
            }
            catch (InvalidOperationException exception) when (exception.Message.Contains("child exited", StringComparison.Ordinal)) { }
        }
        Require(host.Stop().IsSuccess == (iteration != 2), "join/release and retained child failure");
        Require(host.Completion.IsCompletedSuccessfully == (iteration != 2), "worker completion state");
        Require(child.WaitForExit(1000), "child death confirmed before storage release");
        Require(MemoryUsed() == 0, "storage budget refunded");
        Require(!runtime.Processes.Resolve(server).Value!.Regions.Contains(host.Storage), "region removed");
        await Task.Delay(20);
        Require(host.DispatchCount == 4, "stopped worker stays stopped");
        Require(host.Stop().IsSuccess == (iteration != 2), "idempotent host stop/failure");
    }
    finally
    {
        KernelResult stop = host.Stop();
        Require(stop.IsSuccess == (iteration != 2), "final stop");
        Require(runtime.CloseSession(caller, opened.Value).IsSuccess, "session close");
        Require(session.TerminateAndReclaim(caller).IsSuccess, "client reclaim");
        Require(session.TerminateAndReclaim(server).IsSuccess, "service reclaim");
        Require(session.CompletedTraces.ContainsKey(caller.ProcessId.Value), "client trace retained");
        Require(session.CompletedTraces.ContainsKey(server.ProcessId.Value), "service trace retained");
    }
}
static void Require(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException($"Check failed: {label}");
}