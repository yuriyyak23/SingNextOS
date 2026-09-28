using System.Diagnostics;
using System.Text.Json;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.SingCapQualification;

public static class VNextResourcePerformanceQualification
{
    private static readonly int[] DefaultWorkerCounts = [1, 2, 4, 8, 16];

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = Parse(args);
            var report = await MeasureAsync(options);
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            if (options.OutputPath is { } outputPath)
            {
                var fullPath = Path.GetFullPath(outputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                await JsonSerializer.SerializeAsync(stream, report, new JsonSerializerOptions { WriteIndented = true });
            }
            Console.WriteLine(json);
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    public static async Task<VNextResourcePerformanceReport> MeasureAsync(VNextResourcePerformanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        var measurements = new List<VNextResourceMeasurement>();
        foreach (var workers in options.WorkerCounts)
        {
            measurements.Add(await MeasureReserveReleaseAsync(workers, options.IterationsPerWorker, sharedProcessDomain: true));
            measurements.Add(await MeasureReserveReleaseAsync(workers, options.IterationsPerWorker, sharedProcessDomain: false));
            measurements.Add(await MeasureQuarantineLifecycleAsync(workers, options.IterationsPerWorker));
            measurements.Add(await MeasureQuarantinePressureDenialAsync(workers, options.IterationsPerWorker));
        }

        return new(
            Schema: "SingNextOS.VNext.ResourceBudgetPerformance.v1",
            CapturedUtc: DateTimeOffset.UtcNow,
            Environment: new(Environment.Version.ToString(), Environment.OSVersion.ToString(),
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                Environment.ProcessorCount, Stopwatch.Frequency, "JIT-host"),
            Methodology: new(options.IterationsPerWorker, options.WorkerCounts.ToArray(),
                "Each sample is complete owner-operation latency and therefore includes lock acquisition; lock wait is not separately instrumented.",
                "One ResourceBudgetAuthority per measurement; no replicated ledger.",
                "Warm-up is excluded. Timed workers use LongRunning tasks and a ready/start rendezvous."),
            Measurements: measurements,
            Claim: "AccountingOnly performance evidence for the host/JIT ComputeTime resource-budget owner contour.",
            Exclusions: [
                "No latency threshold, deadline, realtime, EnforcedUpperBound, GuaranteedReservation or ProductionQualified claim.",
                "No SMT-topology control or measurement.",
                "No provider, HybridCPU, NativeAOT, hardware, QEMU, firmware or CXL execution.",
                "Quarantine pressure is induced through the live owner state machine; it is not provider-loss evidence."
            ]);
    }

    private static async Task<VNextResourceMeasurement> MeasureReserveReleaseAsync(int workers, int iterations, bool sharedProcessDomain)
    {
        var setup = CreateAuthority(workers, sharedProcessDomain, checked((ulong)workers + 1));
        WarmUpReserveRelease(setup.Authority, setup.Processes[0]);
        return await MeasureAsync("reserve-release", sharedProcessDomain ? "shared-process-domain" : "per-worker-process-domain",
            setup, workers, iterations, expectedError: null, static (authority, process) =>
            {
                var reserved = authority.Reserve(process, [Amount(1)], BudgetReservationLifetime.LocalResource, AdmissionQosHint.None);
                if (!reserved.IsSuccess) return reserved.Error;
                var released = authority.Release(process, reserved.Value!.Reservation);
                return released.IsSuccess ? KernelError.None : released.Error;
            });
    }

    private static async Task<VNextResourceMeasurement> MeasureQuarantineLifecycleAsync(int workers, int iterations)
    {
        var setup = CreateAuthority(workers, sharedProcessDomain: false, (ulong)workers + 1);
        return await MeasureAsync("reserve-bind-consume-quarantine-reconcile-settle-zero", "per-worker-process-domain",
            setup, workers, iterations, expectedError: null, static (authority, process) =>
            {
                var reserved = authority.Reserve(process, [Amount(1)], BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None);
                if (!reserved.IsSuccess) return reserved.Error;
                var lease = reserved.Value!.Reservation;
                if (!authority.BindLease(process, lease).IsSuccess) return KernelError.InvalidTransition;
                if (!authority.BeginConsumption(process, lease).IsSuccess) return KernelError.InvalidTransition;
                if (!authority.QuarantineLease(process, lease).IsSuccess) return KernelError.InvalidTransition;
                if (!authority.ReconcileLease(process, lease).IsSuccess) return KernelError.InvalidTransition;
                var settled = authority.SettleLease(process, lease, []);
                return settled.IsSuccess ? KernelError.None : settled.Error;
            });
    }

    private static async Task<VNextResourceMeasurement> MeasureQuarantinePressureDenialAsync(int workers, int iterations)
    {
        var setup = CreateAuthority(workers, sharedProcessDomain: true, 1);
        var process = setup.Processes[0];
        var reserved = Require(setup.Authority.Reserve(process, [Amount(1)], BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None));
        Require(setup.Authority.BindLease(process, reserved.Reservation));
        Require(setup.Authority.BeginConsumption(process, reserved.Reservation));
        Require(setup.Authority.QuarantineLease(process, reserved.Reservation));
        var before = Require(setup.Authority.Query(setup.RootProcessAccount));
        if (before.Pressure != BudgetPressureState.PinnedByExternalEffect)
            throw new InvalidOperationException("Quarantined ExternalEffect must report PinnedByExternalEffect pressure.");

        return await MeasureAsync("reserve-denied-by-quarantined-external-effect", "shared-process-domain",
            setup, workers, iterations, KernelError.BudgetExceeded, static (authority, owner) =>
                authority.Reserve(owner, [Amount(1)], BudgetReservationLifetime.LocalResource, AdmissionQosHint.None).Error);
    }

    private static async Task<VNextResourceMeasurement> MeasureAsync(
        string operation, string topology, Setup setup, int workers, int iterations,
        KernelError? expectedError, Func<ResourceBudgetAuthority, ProcessHandle, KernelError> operationBody)
    {
        using var ready = new CountdownEvent(workers);
        using var start = new ManualResetEventSlim(false);
        var samples = Enumerable.Range(0, workers).Select(_ => new long[iterations]).ToArray();
        var outcomes = new KernelError[workers][];
        var tasks = Enumerable.Range(0, workers).Select(worker => Task.Factory.StartNew(() =>
        {
            outcomes[worker] = new KernelError[iterations];
            ready.Signal();
            start.Wait();
            var process = setup.Processes[worker % setup.Processes.Count];
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                var started = Stopwatch.GetTimestamp();
                outcomes[worker][iteration] = operationBody(setup.Authority, process);
                samples[worker][iteration] = Stopwatch.GetTimestamp() - started;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        ready.Wait();
        var wall = Stopwatch.StartNew();
        start.Set();
        await Task.WhenAll(tasks);
        wall.Stop();

        var ordered = samples.SelectMany(static values => values).Order().ToArray();
        var flattened = outcomes.SelectMany(static values => values).ToArray();
        var expectedSuccess = expectedError is null;
        var successes = flattened.Count(static error => error == KernelError.None);
        var expectedFailures = expectedError is { } error ? flattened.Count(item => item == error) : 0;
        var unexpected = flattened.Length - (expectedSuccess ? successes : expectedFailures);
        if (unexpected != 0)
            throw new InvalidOperationException($"{operation}/{topology} observed {unexpected} unexpected owner outcomes.");

        var account = Require(setup.Authority.Query(setup.RootProcessAccount));
        var used = account.Usage.Single(item => item.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Used;
        var live = setup.Authority.InspectionSnapshot().Length;
        var expectedUsed = expectedError == KernelError.BudgetExceeded ? 1UL : 0UL;
        var expectedLive = expectedError == KernelError.BudgetExceeded ? 1 : 0;
        if (used != expectedUsed || live != expectedLive)
            throw new InvalidOperationException($"{operation}/{topology} violated conservation: used={used}, live={live}.");

        return new(operation, topology, workers, setup.Processes.Count, flattened.Length, successes,
            expectedFailures, unexpected, flattened.Length / wall.Elapsed.TotalSeconds,
            Percentile(ordered, .50), Percentile(ordered, .95), Percentile(ordered, .99), ToNanoseconds(ordered[^1]),
            used, live, account.Pressure.ToString());
    }

    private static Setup CreateAuthority(int workers, bool sharedProcessDomain, ulong limit)
    {
        var authority = new ResourceBudgetAuthority();
        Require(authority.ConfigureSystem([Amount(limit)]));
        var processes = new List<ProcessHandle>();
        BudgetAccountHandle firstAccount = default;
        var count = sharedProcessDomain ? 1 : workers;
        for (var index = 0; index < count; index++)
        {
            var service = Require(authority.CreateChild(authority.SystemBudget, BudgetAccountLevel.Service,
                $"qualification-service-{index}", [Amount(limit)]));
            var account = Require(authority.CreateChild(service.Account, BudgetAccountLevel.ProcessDomain,
                $"qualification-domain-{index}", [Amount(limit)]));
            var process = new ProcessHandle(new((ulong)index + 10_000), 1);
            Require(authority.AttachProcess(process, account.Account));
            processes.Add(process);
            if (index == 0) firstAccount = account.Account;
        }
        return new(authority, processes, firstAccount);
    }

    private static void WarmUpReserveRelease(ResourceBudgetAuthority authority, ProcessHandle process)
    {
        var reservation = Require(authority.Reserve(process, [Amount(1)], BudgetReservationLifetime.LocalResource, AdmissionQosHint.None));
        Require(authority.Release(process, reservation.Reservation));
    }

    private static T Require<T>(KernelResult<T> result) => result.IsSuccess
        ? result.Value!
        : throw new InvalidOperationException(result.Message ?? result.Error.ToString());

    private static void Require(KernelResult result)
    {
        if (!result.IsSuccess) throw new InvalidOperationException(result.Message ?? result.Error.ToString());
    }

    private static BudgetAmount Amount(ulong amount) => new(ServiceBudgetDimension.ComputeTimeNanoseconds, amount);
    private static double Percentile(long[] ordered, double percentile) =>
        ToNanoseconds(ordered[(int)Math.Ceiling(percentile * ordered.Length) - 1]);
    private static double ToNanoseconds(long ticks) => ticks * (1_000_000_000d / Stopwatch.Frequency);

    private static VNextResourcePerformanceOptions Parse(string[] args)
    {
        string? output = null;
        var iterations = 250;
        var workers = DefaultWorkerCounts;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--output" when index + 1 < args.Length:
                    output = args[++index];
                    break;
                case "--iterations" when index + 1 < args.Length && int.TryParse(args[++index], out var parsedIterations):
                    iterations = parsedIterations;
                    break;
                case "--workers" when index + 1 < args.Length:
                    workers = args[++index].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(value => int.TryParse(value, out var parsed) ? parsed : -1).ToArray();
                    break;
                default:
                    throw new ArgumentException($"Unknown or incomplete vNext performance argument: {args[index]}");
            }
        }
        return new(iterations, workers, output);
    }

    private static void Validate(VNextResourcePerformanceOptions options)
    {
        if (options.IterationsPerWorker is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(options), "Iterations must be between 1 and 100000.");
        if (options.WorkerCounts.Count == 0 || options.WorkerCounts.Any(static value => value is < 1 or > 64) ||
            options.WorkerCounts.Distinct().Count() != options.WorkerCounts.Count)
            throw new ArgumentException("Worker counts must be unique values between 1 and 64.", nameof(options));
        if (options.OutputPath is { Length: 0 })
            throw new ArgumentException("Output path cannot be empty.", nameof(options));
    }

    private sealed record Setup(ResourceBudgetAuthority Authority, IReadOnlyList<ProcessHandle> Processes,
        BudgetAccountHandle RootProcessAccount);
}

public sealed record VNextResourcePerformanceOptions(int IterationsPerWorker, IReadOnlyList<int> WorkerCounts, string? OutputPath = null);
public sealed record VNextResourcePerformanceEnvironment(string Runtime, string OperatingSystem, string RuntimeIdentifier,
    int LogicalProcessorCount, long StopwatchFrequency, string ExecutionMode);
public sealed record VNextResourcePerformanceMethodology(int IterationsPerWorker, IReadOnlyList<int> WorkerCounts,
    string LatencyMetric, string AuthorityTopology, string Synchronization);
public sealed record VNextResourceMeasurement(string Operation, string Topology, int Workers, int ProcessDomains,
    int Attempts, int Successes, int ExpectedFailures, int UnexpectedOutcomes, double ThroughputPerSecond,
    double MedianNanoseconds, double P95Nanoseconds, double P99Nanoseconds, double MaxObservedNanoseconds,
    ulong FinalUsedAmount, int FinalLiveLeases, string FinalPressure);
public sealed record VNextResourcePerformanceReport(string Schema, DateTimeOffset CapturedUtc,
    VNextResourcePerformanceEnvironment Environment, VNextResourcePerformanceMethodology Methodology,
    IReadOnlyList<VNextResourceMeasurement> Measurements, string Claim, IReadOnlyList<string> Exclusions);
