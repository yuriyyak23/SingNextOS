using System.Diagnostics;
using System.Text.Json;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.SingCapQualification;

public static class V6P07LocalityPerformanceQualification
{
    private static readonly int[] PayloadSizes = [64, 4096, 65536];
    private const int DefaultIterations = 100;
    private const int DefaultRounds = 7;

    public static int Run(string[] args)
    {
        try
        {
            Options options = Parse(args);
            Report report = Measure(options.Iterations, options.Rounds);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true });
            string directory = Path.GetDirectoryName(options.OutputPath)
                ?? throw new ArgumentException("Output path must have a parent directory.");
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, $".{Path.GetFileName(options.OutputPath)}.{Guid.NewGuid():N}.tmp");
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
        foreach (int size in PayloadSizes)
        {
            _ = RunOperation(size, plannerObserved: false);
            _ = RunOperation(size, plannerObserved: true);
        }

        var samples = new List<Sample>(checked(PayloadSizes.Length * rounds * 2));
        foreach (int size in PayloadSizes)
        for (int round = 0; round < rounds; round++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            if ((round & 1) == 0)
            {
                samples.Add(MeasureArm("preselected-same-destination", size, round, iterations, false));
                samples.Add(MeasureArm("provider-observed-planner", size, round, iterations, true));
            }
            else
            {
                samples.Add(MeasureArm("provider-observed-planner", size, round, iterations, true));
                samples.Add(MeasureArm("preselected-same-destination", size, round, iterations, false));
            }
        }

        Summary[] summaries = PayloadSizes.Select(size => Summarize(size, samples)).ToArray();
        return new(
            "singnext.v6.p07-locality-performance/1",
            DateTimeOffset.UtcNow,
            new(Environment.Version.ToString(), Environment.OSVersion.ToString(),
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                Environment.ProcessorCount, Stopwatch.Frequency, "Release JIT host"),
            new(iterations, rounds, PayloadSizes,
                "Each operation starts from a fresh RuntimeKernel with source/destination processes and a filled OwnedBuffer. Timing begins immediately before movement and ends at its result; setup and post-result validation are excluded. Alternating rounds compare direct movement to the same preselected destination with provider-observed query, ranking, and revalidation plus the identical host-copy path.",
                "Managed provider classes do not alter physical placement or copy latency. No CPU affinity, isolated host, hardware counter, GC-pause subtraction, DMA, NUMA/topology control, or product workload/SLO."),
            samples, summaries,
            "Elapsed-time overhead characterization only. Because both arms execute the same managed host-copy destination, this campaign cannot demonstrate locality benefit or authorize policy promotion; V6-LOCALITY-PLANNING remains OFF.");
    }

    private static Sample MeasureArm(string arm, int payloadBytes, int round, int iterations,
        bool plannerObserved)
    {
        var elapsed = new long[iterations];
        long allocated = 0;
        for (int index = 0; index < iterations; index++)
        {
            OperationContext context = Create(payloadBytes);
            long allocationStart = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            KernelResult<V6DataMotionExecution<byte>> result = plannerObserved
                ? context.Kernel.ExecuteV6ProviderObservedHostDataMotion(
                    context.Source, context.Destination, context.Input, "source",
                    ["reference", "selected"], 1500, (ulong)payloadBytes, context.Provider)
                : context.Kernel.ExecuteV6HostDataMotion(
                    context.Source, context.Destination, context.Input, context.Plan,
                    static () => 3, static () => 5);
            elapsed[index] = Stopwatch.GetTimestamp() - started;
            allocated += GC.GetAllocatedBytesForCurrentThread() - allocationStart;
            Validate(result, payloadBytes);
        }
        Array.Sort(elapsed);
        return new(arm, payloadBytes, round, iterations,
            ToNanoseconds(elapsed[(int)Math.Ceiling(iterations * 0.50) - 1]),
            ToNanoseconds(elapsed[(int)Math.Ceiling(iterations * 0.95) - 1]),
            ToNanoseconds(elapsed[^1]), (double)allocated / iterations);
    }

    private static KernelResult<V6DataMotionExecution<byte>> RunOperation(int payloadBytes, bool plannerObserved)
    {
        OperationContext context = Create(payloadBytes);
        KernelResult<V6DataMotionExecution<byte>> result = plannerObserved
            ? context.Kernel.ExecuteV6ProviderObservedHostDataMotion(
                context.Source, context.Destination, context.Input, "source",
                ["reference", "selected"], 1500, (ulong)payloadBytes, context.Provider)
            : context.Kernel.ExecuteV6HostDataMotion(
                context.Source, context.Destination, context.Input, context.Plan,
                static () => 3, static () => 5);
        Validate(result, payloadBytes);
        return result;
    }

    private static OperationContext Create(int payloadBytes)
    {
        var kernel = new RuntimeKernel();
        ProcessHandle source = CreateProcess(kernel, 70_001, 71_001);
        ProcessHandle destination = CreateProcess(kernel, 70_002, 71_002);
        OwnedBuffer<byte> input = kernel.AllocateBuffer<byte>(source, payloadBytes).Value
            ?? throw new InvalidOperationException("P07 source allocation failed.");
        input.Span.Fill(0x5A);
        RegionDescriptor descriptor = kernel.Regions.Validate(input.Handle,
            new(new(71_001), source.Generation)).Value
            ?? throw new InvalidOperationException("P07 source Region validation failed.");
        LocalityCostEstimateV1 sourceEstimate = Estimate("source", 3, payloadBytes, 500, 100);
        LocalityCostEstimateV1 reference = Estimate("reference", 4, payloadBytes, 900, 300);
        LocalityCostEstimateV1 selected = Estimate("selected", 5, payloadBytes, 100, 0);
        DataMotionPlanV1 plan = LocalityPlanningPolicyV1.PlanHostMove(sourceEstimate,
            [reference, selected], 1500, input.Handle.Generation.Value,
            descriptor.MutationEpoch.Value, (ulong)payloadBytes);
        var provider = new V6ManagedLocalityCostProvider();
        Require(provider.Publish(sourceEstimate));
        Require(provider.Publish(reference));
        Require(provider.Publish(selected));
        return new(kernel, source, destination, input, plan, provider);
    }

    private static void Validate(KernelResult<V6DataMotionExecution<byte>> result, int payloadBytes)
    {
        if (!result.IsSuccess || result.Value is null || result.Value.Moved.Length != payloadBytes ||
            result.Value.Receipt.Plan.DestinationProviderClass != "selected" ||
            result.Value.Receipt.AuthorizesMovement || result.Value.Moved.Span[0] != 0x5A ||
            result.Value.Moved.Span[^1] != 0x5A)
            throw new InvalidOperationException($"P07 measured operation violated its movement invariant: {result.Error} {result.Message}");
    }

    private static Summary Summarize(int payloadBytes, IReadOnlyList<Sample> samples)
    {
        double[] reference = samples.Where(row => row.PayloadBytes == payloadBytes &&
            row.Arm == "preselected-same-destination").Select(static row => row.MedianNanoseconds).Order().ToArray();
        double[] planner = samples.Where(row => row.PayloadBytes == payloadBytes &&
            row.Arm == "provider-observed-planner").Select(static row => row.MedianNanoseconds).Order().ToArray();
        double referenceMedian = Percentile(reference, 0.5);
        double plannerMedian = Percentile(planner, 0.5);
        double overhead = referenceMedian == 0 ? 0 : (plannerMedian - referenceMedian) / referenceMedian * 100;
        return new(payloadBytes, referenceMedian, plannerMedian, overhead,
            ElapsedTimeLocalityBenefitDemonstrated: false,
            SupportsPolicyPromotion: false,
            "The arms share one managed destination and copy mechanism; the delta characterizes planner-side overhead, not placement-sensitive locality benefit.");
    }

    private static ProcessHandle CreateProcess(RuntimeKernel kernel, ulong processId, ulong domainId)
    {
        var manifest = new SingProcessManifestV1(new(processId), new(domainId), 1,
            $"p07-{processId}", ExecutionRole.Sip, MemoryProfile.SipRegion);
        KernelResult<SingProcess> created = kernel.CreateProcess(manifest);
        if (!created.IsSuccess) throw new InvalidOperationException(created.Message);
        return new(manifest.ProcessId, manifest.Generation);
    }

    private static LocalityCostEstimateV1 Estimate(string provider, ulong generation, int bytes,
        ulong latency, ushort contention) =>
        new(1, provider, generation, 1000, 2000, checked((ulong)bytes), latency, contention, 900);
    private static void Require(KernelResult result)
    {
        if (!result.IsSuccess) throw new InvalidOperationException(result.Message);
    }
    private static double Percentile(double[] ordered, double percentile) =>
        ordered[(int)Math.Ceiling(percentile * ordered.Length) - 1];
    private static double ToNanoseconds(long ticks) => ticks * (1_000_000_000d / Stopwatch.Frequency);

    private sealed record Options(string OutputPath, int Iterations, int Rounds);
    private sealed record OperationContext(RuntimeKernel Kernel, ProcessHandle Source,
        ProcessHandle Destination, OwnedBuffer<byte> Input, DataMotionPlanV1 Plan,
        V6ManagedLocalityCostProvider Provider);
    public sealed record EnvironmentTuple(string Runtime, string OperatingSystem, string RuntimeIdentifier,
        int LogicalProcessorCount, long StopwatchFrequency, string ExecutionMode);
    public sealed record Methodology(int IterationsPerArmPerRound, int Rounds,
        IReadOnlyList<int> PayloadBytes, string TimingBoundary, string Limitations);
    public sealed record Sample(string Arm, int PayloadBytes, int Round, int Operations,
        double MedianNanoseconds, double P95Nanoseconds, double MaximumNanoseconds,
        double AllocatedBytesPerOperation);
    public sealed record Summary(int PayloadBytes, double PreselectedMedianNanoseconds,
        double PlannerObservedMedianNanoseconds, double PlannerOverheadPercent,
        bool ElapsedTimeLocalityBenefitDemonstrated, bool SupportsPolicyPromotion, string Interpretation);
    public sealed record Report(string Schema, DateTimeOffset CapturedUtc, EnvironmentTuple Environment,
        Methodology Methodology, IReadOnlyList<Sample> Samples, IReadOnlyList<Summary> Summaries,
        string ClaimBoundary);

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
        if (iterations is < 20 or > 10_000) throw new ArgumentOutOfRangeException(nameof(iterations));
        if (rounds is < 5 or > 25) throw new ArgumentOutOfRangeException(nameof(rounds));
        return new(Path.GetFullPath(output), iterations, rounds);
    }
}
