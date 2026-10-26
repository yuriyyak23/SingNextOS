using System.Diagnostics;
using System.Text.Json;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

if (args.Length > 0 && string.Equals(args[0], "--v6-p11-remote-lease-performance", StringComparison.Ordinal))
{
    Environment.ExitCode = SingPlus.SingCapQualification.V6P11RemoteLeasePerformanceQualification.Run(args[1..]);
    return;
}

if (args.Length > 0 && string.Equals(args[0], "--v6-p09-device-trust-performance", StringComparison.Ordinal))
{
    Environment.ExitCode = SingPlus.SingCapQualification.V6P09DeviceTrustPerformanceQualification.Run(args[1..]);
    return;
}

if (args.Length > 0 && string.Equals(args[0], "--v6-p08-stateful-resume-performance", StringComparison.Ordinal))
{
    Environment.ExitCode = SingPlus.SingCapQualification.V6P08StatefulResumePerformanceQualification.Run(args[1..]);
    return;
}

if (args.Length > 0 && string.Equals(args[0], "--v6-p06-information-flow-performance", StringComparison.Ordinal))
{
    Environment.ExitCode = SingPlus.SingCapQualification.V6P06InformationFlowPerformanceQualification.Run(args[1..]);
    return;
}

if (args.Length > 0 && string.Equals(args[0], "--v6-p05-refinement-performance", StringComparison.Ordinal))
{
    Environment.ExitCode = SingPlus.SingCapQualification.V6P05RefinementPerformanceQualification.Run(args[1..]);
    return;
}

if (args.Length > 0 && string.Equals(args[0], "--v6-p04-dma-binding-performance", StringComparison.Ordinal))
{
    Environment.ExitCode = SingPlus.SingCapQualification.V6P04DmaBindingPerformanceQualification.Run(args[1..]);
    return;
}

if (args.Length > 0 && string.Equals(args[0], "--v6-p03-temporal-performance", StringComparison.Ordinal))
{
    Environment.ExitCode = SingPlus.SingCapQualification.V6P03TemporalPerformanceQualification.Run(args[1..]);
    return;
}

if (args.Length > 0 && string.Equals(args[0], "--v6-p02-durability-performance", StringComparison.Ordinal))
{
    Environment.ExitCode = SingPlus.SingCapQualification.V6P02DurabilityPerformanceQualification.Run(args[1..]);
    return;
}

if (args.Length > 0 && string.Equals(args[0], "--v6-p01-staged-memory-performance", StringComparison.Ordinal))
{
    Environment.ExitCode = SingPlus.SingCapQualification.V6P01StagedMemoryPerformanceQualification.Run(args[1..]);
    return;
}

if (args.Length > 0 && string.Equals(args[0], "--v6-p12-energy-performance", StringComparison.Ordinal))
{
    Environment.ExitCode = SingPlus.SingCapQualification.V6P12EnergyPerformanceQualification.Run(args[1..]);
    return;
}

if (args.Length > 0 && string.Equals(args[0], "--v6-p07-locality-performance", StringComparison.Ordinal))
{
    Environment.ExitCode = SingPlus.SingCapQualification.V6P07LocalityPerformanceQualification.Run(args[1..]);
    return;
}

if (args.Length > 0 && string.Equals(args[0], "--v6-pcl-performance", StringComparison.Ordinal))
{
    Environment.ExitCode = SingPlus.SingCapQualification.V6PclPerformanceQualification.Run(args[1..]);
    return;
}

if (args.Length > 0 && string.Equals(args[0], "--vnext-resource-performance", StringComparison.Ordinal))
{
    Environment.ExitCode = await SingPlus.SingCapQualification.VNextResourcePerformanceQualification.RunAsync(args[1..]);
    return;
}

if (args.Length > 0 && string.Equals(args[0], "--vnext-controlled-topology", StringComparison.Ordinal))
{
    Environment.ExitCode = SingPlus.SingCapQualification.VNextControlledTopologyQualification.Run(args[1..]);
    return;
}

const int iterationsPerWorker = 5_000;
int[] workerCounts = [1, 2, 4, 8, 16, 32];
List<Measurement> results = new List<Measurement>();

foreach (int workers in workerCounts)
{
    results.Add(await MeasureCapability(workers, shared: true));
    results.Add(await MeasureCapability(workers, shared: false));
    results.Add(await MeasureRegion(workers, shared: true));
    results.Add(await MeasureRegion(workers, shared: false));
}
IReadOnlyList<SingleThreadMeasurement> singleThreadMeasurements = MeasureSingleThreadBaselines();

RuntimeKernel spanKernel = new RuntimeKernel();
ProcessHandle spanSubject = CreateProcess(spanKernel, 900_000, 910_000);
OwnedBuffer<byte> spanBuffer = spanKernel.AllocateBuffer<byte>(spanSubject, 4096).Value
    ?? throw new InvalidOperationException("Span buffer allocation failed.");
Stopwatch spanTimer = Stopwatch.StartNew();
for (int pass = 0; pass < 10_000; pass++)
{
    Span<byte> span = spanBuffer.Span;
    for (int index = 0; index < span.Length; index++)
        span[index]++;
}
spanTimer.Stop();

var report = new
{
    schema = "SingCapPerformanceQualificationV1",
    capturedUtc = DateTimeOffset.UtcNow,
    environment = new
    {
        runtime = Environment.Version.ToString(),
        os = Environment.OSVersion.ToString(),
        processorCount = Environment.ProcessorCount,
        stopwatchFrequency = Stopwatch.Frequency,
        configuration = "Release"
    },
    methodology = new
    {
        iterationsPerWorker,
        workers = workerCounts,
        latencyUnit = "nanoseconds",
        lockWaitMetric = "maxObservedOperationLatencyNs (closest available runtime measurement; lock acquisition is not separately instrumented)",
        providerLatencyIncluded = false,
        spanRule = "Capability/Region lookup occurs outside the element loop."
    },
    measurements = results,
    singleThreadMeasurements,
    spanAfterAcquisition = new
    {
        elementsPerPass = spanBuffer.Length,
        passes = 10_000,
        elapsedNanoseconds = ToNanoseconds(spanTimer.ElapsedTicks),
        operationsPerSecond = 10_000d * spanBuffer.Length / spanTimer.Elapsed.TotalSeconds
    }
};

string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
if (args.Length == 2 && args[0] == "--output")
{
    string path = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, json + Environment.NewLine);
}
Console.WriteLine(json);

static async Task<Measurement> MeasureCapability(int workers, bool shared)
{
    RuntimeKernel kernel = new RuntimeKernel();
    ProcessHandle[] subjects = Enumerable.Range(0, shared ? 1 : workers)
        .Select(index => CreateProcess(kernel, (ulong)(1000 + index), (ulong)(2000 + index)))
        .ToArray();
    CapabilityId[] capabilities = subjects.Select((subject, index) =>
    {
        DomainId domain = kernel.Processes.Resolve(subject).Value!.DomainId;
        return kernel.MintCapability(domain, subject, ResourceKind.KernelService,
            $"p13-capability-{index}", CapabilityRights.Read).Value!.CapabilityId;
    }).ToArray();

    return await Measure("capability-lookup", workers, shared, worker =>
    {
        int slot = shared ? 0 : worker;
        SingProcess process = kernel.Processes.Resolve(subjects[slot]).Value!;
        KernelResult<CapabilityDescriptorV1> result = kernel.CapabilityAuthority.Validate(capabilities[slot], process.DomainId,
            subjects[slot].Generation, CapabilityRights.Read);
        if (!result.IsSuccess) throw new InvalidOperationException(result.Message);
    });
}

static async Task<Measurement> MeasureRegion(int workers, bool shared)
{
    RuntimeKernel kernel = new RuntimeKernel();
    ProcessHandle[] subjects = Enumerable.Range(0, shared ? 1 : workers)
        .Select(index => CreateProcess(kernel, (ulong)(10_000 + index), (ulong)(20_000 + index)))
        .ToArray();
    OwnedBuffer<byte>[] buffers = subjects.Select(subject => kernel.AllocateBuffer<byte>(subject, 64).Value
        ?? throw new InvalidOperationException("Region allocation failed.")).ToArray();
    RegionOwner[] owners = subjects.Select(subject =>
    {
        SingProcess process = kernel.Processes.Resolve(subject).Value!;
        return new RegionOwner(process.DomainId, subject.Generation);
    }).ToArray();

    return await Measure("region-validate", workers, shared, worker =>
    {
        int slot = shared ? 0 : worker;
        KernelResult<RegionDescriptor> result = kernel.Regions.Validate(buffers[slot].Handle, owners[slot]);
        if (!result.IsSuccess) throw new InvalidOperationException(result.Message);
    });
}

static IReadOnlyList<SingleThreadMeasurement> MeasureSingleThreadBaselines()
{
    const int samples = 1_000;
    List<SingleThreadMeasurement> results = new List<SingleThreadMeasurement>();

    IpcScenario ipc = CreateIpcScenario(capabilityCount: 0, capacity: samples);
    IpcCopyPayload emptyPayload = new IpcCopyPayload([], 1);
    results.Add(MeasureSamples("single-sip-empty-call", samples, () =>
    {
        RequireValue(ipc.Kernel.SendCopyV2(ipc.Left, ipc.Right, ipc.Endpoint, 1, emptyPayload));
        RequireValue(ipc.Kernel.Receive(ipc.Right, ipc.Peer));
    }));

    IpcScenario ipcWithCapabilities = CreateIpcScenario(capabilityCount: 4, capacity: samples);
    results.Add(MeasureSamples("sip-with-4-exact-capabilities", samples, () =>
    {
        RequireValue(ipcWithCapabilities.Kernel.SendCopyV2(ipcWithCapabilities.Left,
            ipcWithCapabilities.Right, ipcWithCapabilities.Endpoint, 1, emptyPayload,
            ipcWithCapabilities.Capabilities));
        RequireValue(ipcWithCapabilities.Kernel.Receive(ipcWithCapabilities.Right,
            ipcWithCapabilities.Peer));
    }));

    SealedObjectAuthority sealAuthority = new SealedObjectAuthority(new(Guid.Parse("6cd50468-9941-45ef-86e4-f928e77aa00b")));
    ProcessHandle sealService = new ProcessHandle(new(70), 1);
    ProcessHandle sealOwner = new ProcessHandle(new(71), 1);
    EndpointSessionHandle sealSession = new EndpointSessionHandle(new(72), new(1));
    ServiceEndpointDescriptor sealDescriptor = new ServiceEndpointDescriptor(new(new(73), "p13"), new(1),
        new("p13", "1", "digest"), "p13/service", ServiceAvailability.Accepting);
    SealedHandle<SocketObjectSeal> sealedHandle = sealAuthority.Seal<SocketObjectSeal>(sealDescriptor, sealService,
        sealOwner, sealSession, 1, 1, new(74)).Value;
    results.Add(MeasureSamples("seal-unseal-internal-resolution", samples, () =>
    {
        using SealedObjectPin<SocketObjectSeal> pin = sealAuthority.AcquirePin(sealedHandle, sealDescriptor, sealService,
            sealOwner, sealSession, 1, new(74)).Value
            ?? throw new InvalidOperationException("Seal acquisition failed.");
        Require(sealAuthority.Revalidate(pin, sealDescriptor, sealService));
    }));

    EndpointSessionRegistry sessionRegistry = new EndpointSessionRegistry(TimeProvider.System);
    ProcessHandle sessionCaller = new ProcessHandle(new(80), 1);
    ProcessHandle sessionService = new ProcessHandle(new(81), 1);
    EndpointSessionHandle session = sessionRegistry.Add(sessionCaller, sessionService, [], [],
        new(new(82), new(1), 1), null).Value!.Handle;
    using (EndpointSessionPin pin = sessionRegistry.AcquirePin(session, sessionCaller, sessionService).Value!)
        results.Add(MeasureSamples("async-lease-revalidation", samples,
            () => Require(sessionRegistry.RevalidatePin(pin))));

    RuntimeKernel borrowKernel = new RuntimeKernel();
    ProcessHandle borrowOwner = CreateProcess(borrowKernel, 30_000, 31_000);
    ProcessHandle borrower = CreateProcess(borrowKernel, 30_001, 31_001);
    OwnedBuffer<byte> borrowBuffer = borrowKernel.AllocateBuffer<byte>(borrowOwner, 64).Value!;
    RegionOwner ownerIdentity = new RegionOwner(new(31_000), 1);
    RegionOwner borrowerIdentity = new RegionOwner(new(31_001), 1);
    results.Add(MeasureSamples("region-lexical-borrow-acquire-return", samples, () =>
    {
        BorrowLeaseHandle lease = borrowKernel.Regions.Loan(borrowBuffer.Handle, ownerIdentity, borrowerIdentity).Value;
        Require(borrowKernel.Regions.ReturnLoan(lease, borrowerIdentity));
    }));

    RuntimeKernel moveKernel = new RuntimeKernel();
    ProcessHandle moveLeft = CreateProcess(moveKernel, 40_000, 41_000);
    ProcessHandle moveRight = CreateProcess(moveKernel, 40_001, 41_001);
    results.Add(MeasureSamples("move-64-byte-region", samples, () =>
    {
        OwnedBuffer<byte> buffer = moveKernel.AllocateBuffer<byte>(moveLeft, 64).Value!;
        OwnedBuffer<byte> moved = moveKernel.TransferRegion(moveLeft, moveRight, buffer).Value!;
        Require(moveKernel.ReleaseRegion(moveRight, moved));
    }));

    RuntimeKernel externalKernel = new RuntimeKernel();
    ProcessHandle externalOwner = CreateProcess(externalKernel, 50_000, 51_000);
    OwnedBuffer<byte> externalInput = externalKernel.AllocateBuffer<byte>(externalOwner, 8).Value!;
    OwnedBuffer<byte> externalOutput = externalKernel.AllocateBuffer<byte>(externalOwner, 8).Value!;
    OperationDependencySnapshot dependencies = new OperationDependencySnapshot(7, 11, 13, 17);
    results.Add(MeasureSamples("external-operation-prepare-admit-publish-release", samples, () =>
    {
        OperationPreparation prepared = externalKernel.PrepareExternalOperation(externalOwner,
        [
            new(externalInput.Handle, RegionUseMode.ReadOnly, new(0, 8)),
            new(externalOutput.Handle, RegionUseMode.StagedOutput, new(0, 8)),
        ], ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!;
        RequireValue(externalKernel.AdmitExternalOperation(externalOwner, prepared.Operation, dependencies));
        OperationBinding binding = externalKernel.RecordExternalOperationSubmission(externalOwner,
            prepared.Operation, dependencies).Value!;
        RequireValue(externalKernel.RecordExternalOperationCompletion(externalOwner,
            new(binding, ExternalOperationCompletionDisposition.Completed)));
        RequireValue(externalKernel.RecordExternalOperationVisibility(externalOwner,
            new(binding, ExternalVisibilityRequirement.PublicationFence, true)));
        RequireValue(externalKernel.PublishExternalOperation(externalOwner, prepared.Operation,
            dependencies, new(ExternalPublicationPolicy.Staged), static () => { }));
        RequireValue(externalKernel.ReleaseExternalOperation(externalOwner, prepared.Operation,
            new(true, false)));
    }));

    return results;
}

static IpcScenario CreateIpcScenario(int capabilityCount, int capacity)
{
    RuntimeKernel kernel = new RuntimeKernel();
    ProcessHandle left = CreateProcess(kernel, (ulong)(60_000 + capabilityCount), (ulong)(61_000 + capabilityCount));
    ProcessHandle right = CreateProcess(kernel, (ulong)(62_000 + capabilityCount), (ulong)(63_000 + capabilityCount));
    CapabilityRequirementV1[] requirements = Enumerable.Range(0, capabilityCount)
        .Select(index => new CapabilityRequirementV1(ResourceKind.File, $"p13-ipc-{index}", CapabilityRights.Read))
        .ToArray();
    CapabilityId[] capabilityIds = requirements.Select(requirement => kernel.MintCapability(new(61_000 + (ulong)capabilityCount),
        left, requirement.ResourceKind, requirement.ResourceId, requirement.Rights).Value!.CapabilityId).ToArray();
    RequestPayloadDescriptorV1 payload = new RequestPayloadDescriptorV1(RequestPayloadKind.Bounded, "payload",
        typeof(IpcCopyPayload).FullName, 1);
    ProtocolMessageDescriptorV1 message = new ProtocolMessageDescriptorV1(1, "Empty", requirements, requestPayload: payload);
    ProtocolDefinitionV1 protocol = new ProtocolDefinitionV1("P13", Guid.NewGuid().ToString("N"), "Ready", null,
        [message], [new(1, "Ready", "Ready")]);
    (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints = kernel.CreateChannel(left, right, protocol, capacity).Value;
    return new(kernel, left, right, endpoints.Left, endpoints.Right, capabilityIds);
}

static SingleThreadMeasurement MeasureSamples(string operation, int count, Action action)
{
    long[] samples = new long[count];
    Stopwatch wall = Stopwatch.StartNew();
    for (int index = 0; index < count; index++)
    {
        long started = Stopwatch.GetTimestamp();
        action();
        samples[index] = Stopwatch.GetTimestamp() - started;
    }
    wall.Stop();
    Array.Sort(samples);
    return new(operation, count, count / wall.Elapsed.TotalSeconds, Percentile(samples, 0.50),
        Percentile(samples, 0.95), Percentile(samples, 0.99), ToNanoseconds(samples[^1]));
}

static void Require(KernelResult result)
{
    if (!result.IsSuccess) throw new InvalidOperationException(result.Message);
}

static void RequireValue<T>(KernelResult<T> result)
{
    if (!result.IsSuccess) throw new InvalidOperationException(result.Message);
}

static async Task<Measurement> Measure(string operation, int workers, bool shared, Action<int> action)
{
    using Barrier barrier = new Barrier(workers + 1);
    long[][] samples = Enumerable.Range(0, workers).Select(_ => new long[iterationsPerWorker]).ToArray();
    Task[] tasks = Enumerable.Range(0, workers).Select(worker => Task.Run(() =>
    {
        barrier.SignalAndWait();
        for (int iteration = 0; iteration < iterationsPerWorker; iteration++)
        {
            long started = Stopwatch.GetTimestamp();
            action(worker);
            samples[worker][iteration] = Stopwatch.GetTimestamp() - started;
        }
    })).ToArray();

    barrier.SignalAndWait();
    Stopwatch wall = Stopwatch.StartNew();
    await Task.WhenAll(tasks);
    wall.Stop();
    long[] ordered = samples.SelectMany(static sample => sample).Order().ToArray();
    return new(operation, shared ? "shared" : "unrelated", workers, ordered.Length,
        ordered.Length / wall.Elapsed.TotalSeconds,
        Percentile(ordered, 0.50), Percentile(ordered, 0.95), Percentile(ordered, 0.99),
        ToNanoseconds(ordered[^1]));
}

static double Percentile(long[] ordered, double percentile) =>
    ToNanoseconds(ordered[(int)Math.Ceiling(percentile * ordered.Length) - 1]);

static double ToNanoseconds(long ticks) => ticks * (1_000_000_000d / Stopwatch.Frequency);

static ProcessHandle CreateProcess(RuntimeKernel kernel, ulong processId, ulong domainId)
{
    SingProcessManifestV1 manifest = new SingProcessManifestV1(new(processId), new(domainId), 1,
        $"p13-{processId}", ExecutionRole.Sip, MemoryProfile.SipRegion);
    KernelResult<SingProcess> created = kernel.CreateProcess(manifest);
    if (!created.IsSuccess) throw new InvalidOperationException(created.Message);
    return new(manifest.ProcessId, manifest.Generation);
}

internal sealed record Measurement(string Operation, string Contention, int Workers,
    int Samples, double ThroughputPerSecond, double MedianNs, double P95Ns,
    double P99Ns, double MaxObservedOperationLatencyNs);

internal sealed record SingleThreadMeasurement(string Operation, int Samples,
    double ThroughputPerSecond, double MedianNs, double P95Ns, double P99Ns,
    double MaxObservedOperationLatencyNs);

internal sealed record IpcScenario(RuntimeKernel Kernel, ProcessHandle Left, ProcessHandle Right,
    ChannelEndpointHandle Endpoint, ChannelEndpointHandle Peer, IReadOnlyCollection<CapabilityId> Capabilities);
