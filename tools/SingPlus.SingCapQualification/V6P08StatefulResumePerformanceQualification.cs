using System.Diagnostics;
using System.Text.Json;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.SingCapQualification;

public static class V6P08StatefulResumePerformanceQualification
{
    private const int DefaultIterations = 750;
    private const int DefaultRounds = 7;
    private const string Correlation = "p08-performance-operation";
    private static readonly string SemanticBindingDigest = new('b', 64);

    public static int Run(string[] args)
    {
        try
        {
            Options options = Parse(args);
            Report report = Measure(options.Iterations, options.Rounds);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(report,
                new JsonSerializerOptions { WriteIndented = true });
            string directory = Path.GetDirectoryName(options.OutputPath)
                ?? throw new ArgumentException("Output path must have a parent directory.");
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory,
                $".{Path.GetFileName(options.OutputPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temporary, json);
                File.Move(temporary, options.OutputPath, true);
            }
            finally { File.Delete(temporary); }
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static Report Measure(int iterations, int rounds)
    {
        var payloads = new Dictionary<int, byte[]>
        {
            [64] = Payload(64),
            [4 * 1024] = Payload(4 * 1024),
            [64 * 1024] = Payload(64 * 1024),
        };
        Warmup(Math.Min(iterations, 250), payloads);

        var samples = new List<Sample>(checked(rounds * 9));
        for (int round = 0; round < rounds; round++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            foreach (string arm in Rotate(round))
            {
                (int stateBytes, Func<bool> operation) = CreateOperation(arm, payloads, iterations);
                samples.Add(MeasureArm(arm, stateBytes, round, iterations, operation));
            }
        }

        Summary summary = Summarize(samples);
        return new("singnext.v6.p08-stateful-resume-performance/1", DateTimeOffset.UtcNow,
            new(Environment.Version.ToString(), Environment.OSVersion.ToString(),
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                Environment.ProcessorCount, Stopwatch.Frequency, "Release JIT host"),
            new(iterations, rounds, payloads.Keys.Order().ToArray(),
                "Opaque payloads and unique safe-point correlations are prepared before timing. Each round/arm receives a fresh managed provider so retained finalized records are bounded to one round. Provider arms measure safe-point request/lifecycle/digest admission with an immediate managed hook, or capture/copy/SHA-256/binding plus discard/zeroization or admit/restore/re-hash/zeroization at 64 B, 4 KiB, and 64 KiB. The full 4 KiB contour additionally measures CheckpointStorageBytes reserve/bind/release, suspension/provider correlation, three successful fresh gates, and resume settlement. Arm order rotates; forced GC occurs only between rounds.",
                "Named deterministic managed model only. The safe-point arm measures software request/evidence overhead, not interrupt delivery or time-to-next-safe-point. No physical CPU, MatrixTile, DSC, L7, external provider, device transfer, durable storage I/O, contention, CPU affinity, isolated host, hardware counter, product workload, or SLO is measured."),
            samples, summary,
            "Managed opaque-state copy/hash/lifecycle and accounting overhead only. It does not establish a safe-point latency bound, physical checkpoint cost, provider-independent result, hardware behavior, production readiness, or gate promotion.");
    }

    private static void Warmup(int operations, IReadOnlyDictionary<int, byte[]> payloads)
    {
        foreach (int size in payloads.Keys)
        {
            var provider = Provider();
            for (int index = 0; index < operations; index++)
            {
                V6ManagedCapturedStateReceipt captured = provider.Capture(Correlation, 3,
                    SemanticBindingDigest, payloads[size]).Value
                    ?? throw new InvalidOperationException("P08 warmup capture failed.");
                if (!provider.Restore(captured.Handle, captured.Binding).IsSuccess)
                    throw new InvalidOperationException("P08 warmup restore failed.");
            }
        }
        var contour = CreateContour();
        for (int index = 0; index < operations; index++)
            if (!CaptureAndResume(contour, payloads[4 * 1024]))
                throw new InvalidOperationException("P08 full-contour warmup failed.");
        var safePoint = SafePointProvider();
        for (int index = 0; index < operations; index++)
            if (!RequestSafePoint(safePoint, $"p08-safe-point-warmup:{index}"))
                throw new InvalidOperationException("P08 safe-point warmup failed.");
    }

    private static (int StateBytes, Func<bool> Operation) CreateOperation(
        string arm, IReadOnlyDictionary<int, byte[]> payloads, int iterations)
    {
        if (arm == "resume-binding-validation")
        {
            var binding = new ResumeBindingV1(1, Correlation, new string('a', 64),
                SemanticBindingDigest, 3, 5, 7, 11);
            return (0, () => binding.Validate() == binding && !binding.AuthorizesExecution &&
                !binding.AuthorizesRegionAccess && !binding.PreservesCapability);
        }
        if (arm == "managed-safe-point-request")
        {
            var provider = SafePointProvider();
            string[] correlations = Enumerable.Range(0, iterations)
                .Select(index => $"p08-safe-point-measured:{index}").ToArray();
            var next = -1;
            return (0, () => RequestSafePoint(provider, correlations[++next]));
        }

        int size = arm.EndsWith("-64b", StringComparison.Ordinal) ? 64 :
            arm.EndsWith("-4k", StringComparison.Ordinal) ? 4 * 1024 : 64 * 1024;
        byte[] payload = payloads[size];
        if (arm.StartsWith("capture-discard", StringComparison.Ordinal))
        {
            var provider = Provider();
            return (size, () => CaptureAndDiscard(provider, payload));
        }
        if (arm.StartsWith("capture-admit-restore", StringComparison.Ordinal))
        {
            var provider = Provider();
            return (size, () => CaptureAdmitAndRestore(provider, payload));
        }
        var contour = CreateContour();
        return (size, () => CaptureAndResume(contour, payload));
    }

    private static Sample MeasureArm(string arm, int stateBytes, int round, int iterations, Func<bool> operation)
    {
        var elapsed = new long[iterations];
        long allocated = 0;
        var wall = Stopwatch.StartNew();
        for (int index = 0; index < iterations; index++)
        {
            long allocationStart = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            bool valid = operation();
            elapsed[index] = Stopwatch.GetTimestamp() - started;
            allocated += GC.GetAllocatedBytesForCurrentThread() - allocationStart;
            if (!valid) throw new InvalidOperationException("P08 measured stateful-resume invariant failed.");
        }
        wall.Stop();
        Array.Sort(elapsed);
        return new(arm, stateBytes, round, iterations, iterations / wall.Elapsed.TotalSeconds,
            ToNanoseconds(elapsed[(int)Math.Ceiling(iterations * 0.50) - 1]),
            ToNanoseconds(elapsed[(int)Math.Ceiling(iterations * 0.95) - 1]),
            ToNanoseconds(elapsed[(int)Math.Ceiling(iterations * 0.99) - 1]),
            ToNanoseconds(elapsed[^1]), (double)allocated / iterations);
    }

    private static bool CaptureAndDiscard(V6ManagedStatefulResumeProvider provider, byte[] payload)
    {
        var captured = provider.Capture(Correlation, 3, SemanticBindingDigest, payload);
        return captured.IsSuccess && !captured.Value!.AuthorizesResume &&
            provider.Discard(captured.Value.Handle, captured.Value.Binding).IsSuccess;
    }

    private static bool CaptureAdmitAndRestore(V6ManagedStatefulResumeProvider provider, byte[] payload)
    {
        var captured = provider.Capture(Correlation, 3, SemanticBindingDigest, payload);
        return captured.IsSuccess &&
            provider.AdmitRestore(captured.Value!.Handle, captured.Value.Binding).IsSuccess &&
            provider.Restore(captured.Value.Handle, captured.Value.Binding).IsSuccess;
    }

    private static bool CaptureAndResume(ContourScenario scenario, byte[] payload)
    {
        var suspended = scenario.Contour.CaptureAndSuspend(scenario.Owner, Correlation, 3,
            SemanticBindingDigest, payload);
        if (!suspended.IsSuccess || suspended.Value!.AuthorizesResume || suspended.Value.PreservesCapability)
            return false;
        var resumed = scenario.Contour.Resume(scenario.Owner, suspended.Value.Handle,
            KernelResult.Ok, KernelResult.Ok);
        return resumed.IsSuccess && resumed.Value!.State == V6StatefulSuspensionState.Resumed;
    }

    private static bool RequestSafePoint(V6ManagedSafePointProvider provider, string correlation)
    {
        var result = provider.RequestSafePoint(correlation, 3,
            provider.ProviderGeneration, provider.RuntimeGeneration, KernelResult.Ok);
        return result.IsSuccess && result.Value!.ObservedLatencyNanoseconds <=
            result.Value.Guarantee.MaximumSafePointLatencyNanoseconds &&
            PreemptionLifecycleValidatorV1.IsValidSafePoint(result.Value.Lifecycle) &&
            !result.Value.AuthorizesPreemption && !result.Value.AuthorizesExecution &&
            !result.Value.ProvesPhysicalLatency;
    }

    private static V6ManagedStatefulResumeProvider Provider() =>
        new("managed-stateful-performance-v1", 7, 11);

    private static V6ManagedSafePointProvider SafePointProvider() =>
        new("managed-safe-point-performance-v1", 1_000_000_000, 7, 11);

    private static ContourScenario CreateContour()
    {
        const ulong limit = V6ManagedStatefulResumeProvider.MaximumCapturedStateBytes;
        var budgets = new ResourceBudgetAuthority();
        BudgetAmount[] amounts = [new(ServiceBudgetDimension.CheckpointStorageBytes, limit)];
        if (!budgets.ConfigureSystem(amounts).IsSuccess)
            throw new InvalidOperationException("P08 system budget configuration failed.");
        BudgetAccountHandle service = budgets.CreateChild(budgets.SystemBudget,
            BudgetAccountLevel.Service, "p08-performance", amounts).Value!.Account;
        BudgetAccountHandle account = budgets.CreateChild(service, BudgetAccountLevel.ProcessDomain,
            "p08-performance-process", amounts).Value!.Account;
        var owner = new ProcessHandle(new(930_000), 1);
        if (!budgets.AttachProcess(owner, account).IsSuccess)
            throw new InvalidOperationException("P08 process budget attachment failed.");
        var provider = Provider();
        return new(new V6ManagedStatefulResumeContour(new(budgets), provider), owner);
    }

    private static byte[] Payload(int size)
    {
        var payload = new byte[size];
        for (int index = 0; index < payload.Length; index++) payload[index] = unchecked((byte)(index * 31 + 17));
        return payload;
    }

    private static Summary Summarize(IReadOnlyList<Sample> samples)
    {
        return new(Median("resume-binding-validation"), Median("managed-safe-point-request"),
            Median("capture-discard-64b"), Median("capture-discard-4k"), Median("capture-discard-64k"),
            Median("capture-admit-restore-64b"), Median("capture-admit-restore-4k"),
            Median("capture-admit-restore-64k"), Median("full-contour-4k"),
            SupportsGatePromotion: false,
            "Absolute host costs expose managed safe-point request/evidence overhead, state-size scaling, and full 4 KiB storage-accounting/admission orchestration for the named model providers.");

        double Median(string arm) => Percentile(samples.Where(sample => sample.Arm == arm)
            .Select(static sample => sample.MedianNanoseconds).Order().ToArray(), 0.5);
    }

    private static string[] Rotate(int round)
    {
        string[] arms = ["resume-binding-validation", "managed-safe-point-request",
            "capture-discard-64b", "capture-discard-4k",
            "capture-discard-64k", "capture-admit-restore-64b", "capture-admit-restore-4k",
            "capture-admit-restore-64k", "full-contour-4k"];
        int offset = round % arms.Length;
        return arms.Skip(offset).Concat(arms.Take(offset)).ToArray();
    }

    private static double Percentile(double[] ordered, double percentile) =>
        ordered[(int)Math.Ceiling(percentile * ordered.Length) - 1];
    private static double ToNanoseconds(long ticks) => ticks * (1_000_000_000d / Stopwatch.Frequency);

    private sealed record Options(string OutputPath, int Iterations, int Rounds);
    private sealed record ContourScenario(V6ManagedStatefulResumeContour Contour, ProcessHandle Owner);
    public sealed record EnvironmentTuple(string Runtime, string OperatingSystem, string RuntimeIdentifier,
        int LogicalProcessorCount, long StopwatchFrequency, string ExecutionMode);
    public sealed record Methodology(int IterationsPerArmPerRound, int Rounds,
        IReadOnlyList<int> CapturedStateSizesBytes, string TimingBoundary, string Limitations);
    public sealed record Sample(string Arm, int CapturedStateBytes, int Round, int Operations,
        double ThroughputPerSecond, double MedianNanoseconds, double P95Nanoseconds,
        double P99Nanoseconds, double MaximumNanoseconds, double AllocatedBytesPerOperation);
    public sealed record Summary(double ResumeBindingValidationMedianNanoseconds,
        double ManagedSafePointRequestMedianNanoseconds,
        double CaptureDiscard64BMedianNanoseconds, double CaptureDiscard4KMedianNanoseconds,
        double CaptureDiscard64KMedianNanoseconds, double CaptureAdmitRestore64BMedianNanoseconds,
        double CaptureAdmitRestore4KMedianNanoseconds, double CaptureAdmitRestore64KMedianNanoseconds,
        double FullContour4KMedianNanoseconds, bool SupportsGatePromotion, string Interpretation);
    public sealed record Report(string Schema, DateTimeOffset CapturedUtc, EnvironmentTuple Environment,
        Methodology Methodology, IReadOnlyList<Sample> Samples, Summary Summary, string ClaimBoundary);

    private static Options Parse(string[] args)
    {
        string? output = null;
        int iterations = DefaultIterations;
        int rounds = DefaultRounds;
        for (int index = 0; index < args.Length; index++)
        {
            string value = index + 1 < args.Length ? args[index + 1] : string.Empty;
            switch (args[index])
            {
                case "--output": output = value; index++; break;
                case "--iterations" when int.TryParse(value, out int parsed): iterations = parsed; index++; break;
                case "--rounds" when int.TryParse(value, out int parsed): rounds = parsed; index++; break;
                default: throw new ArgumentException($"Unknown or malformed option: {args[index]}");
            }
        }
        if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("--output is required.");
        if (iterations is < 250 or > 10_000) throw new ArgumentOutOfRangeException(nameof(iterations));
        if (rounds is < 5 or > 25) throw new ArgumentOutOfRangeException(nameof(rounds));
        return new(Path.GetFullPath(output), iterations, rounds);
    }
}
