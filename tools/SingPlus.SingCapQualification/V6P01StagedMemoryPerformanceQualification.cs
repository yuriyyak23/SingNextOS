using System.Diagnostics;
using System.Text.Json;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.SingCapQualification;

public static class V6P01StagedMemoryPerformanceQualification
{
    private static readonly int[] PayloadSizes = [64, 4096, 65536];
    private const int DefaultIterations = 200;
    private const int DefaultRounds = 7;

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
        foreach (int size in PayloadSizes)
        {
            Scenario warmup = Create(size);
            RunDirectCopy(warmup);
            RunTwoCopy(warmup);
            RunStagedLifecycle(warmup);
        }

        var samples = new List<Sample>(checked(PayloadSizes.Length * rounds * 3));
        foreach (int size in PayloadSizes)
        {
            Scenario scenario = Create(size);
            for (int round = 0; round < rounds; round++)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
                string[] order = (round % 3) switch
                {
                    0 => ["direct-one-copy", "staged-two-copy-only", "staged-owner-lifecycle"],
                    1 => ["staged-two-copy-only", "staged-owner-lifecycle", "direct-one-copy"],
                    _ => ["staged-owner-lifecycle", "direct-one-copy", "staged-two-copy-only"],
                };
                foreach (string arm in order)
                {
                    samples.Add(arm switch
                    {
                        "direct-one-copy" => MeasureArm(arm, size, round, iterations,
                            () => RunDirectCopy(scenario)),
                        "staged-two-copy-only" => MeasureArm(arm, size, round, iterations,
                            () => RunTwoCopy(scenario)),
                        _ => MeasureArm(arm, size, round, iterations,
                            () => RunStagedLifecycle(scenario)),
                    });
                }
            }
        }

        Summary[] summaries = PayloadSizes.Select(size => Summarize(size, samples)).ToArray();
        return new("singnext.v6.p01-staged-memory-performance/1", DateTimeOffset.UtcNow,
            new(Environment.Version.ToString(), Environment.OSVersion.ToString(),
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                Environment.ProcessorCount, Stopwatch.Frequency, "Release JIT host"),
            new(iterations, rounds, PayloadSizes,
                "Reusable source, staging, and destination storage is created before measurement. Direct performs one source-to-destination copy. Two-copy performs source-to-stage then stage-to-destination. The lifecycle arm performs the same two copies inside prepare, admit, submit, complete, visible, publish, and release. Each operation is timed individually; arm order rotates and forced GC occurs only between rounds.",
                "Managed single-host memory and owner lifecycle only. The campaign does not execute the v6 sidecar/refinement checker, hardware fences, DMA, device caches, direct coherence, NUMA placement, CPU affinity, isolated-host controls, or a product workload/SLO."),
            samples, summaries,
            "Host-specific elapsed-time and managed-allocation characterization for the conservative staged path. It is not physical ordering/coherence evidence, does not establish a performance benefit, and does not support enabling V6-MEMORY-SEMANTICS or direct coherent output.");
    }

    private static Sample MeasureArm(string arm, int payloadBytes, int round, int iterations,
        Func<bool> operation)
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
            if (!valid) throw new InvalidOperationException("P01 measured copy violated its data invariant.");
        }
        wall.Stop();
        Array.Sort(elapsed);
        return new(arm, payloadBytes, round, iterations, iterations / wall.Elapsed.TotalSeconds,
            ToNanoseconds(elapsed[(int)Math.Ceiling(iterations * 0.50) - 1]),
            ToNanoseconds(elapsed[(int)Math.Ceiling(iterations * 0.95) - 1]),
            ToNanoseconds(elapsed[(int)Math.Ceiling(iterations * 0.99) - 1]),
            ToNanoseconds(elapsed[^1]), (double)allocated / iterations);
    }

    private static bool RunDirectCopy(Scenario scenario)
    {
        scenario.Input.Span.CopyTo(scenario.Output.Span);
        return Valid(scenario);
    }

    private static bool RunTwoCopy(Scenario scenario)
    {
        scenario.Input.Span.CopyTo(scenario.Staging);
        scenario.Staging.AsSpan().CopyTo(scenario.Output.Span);
        return Valid(scenario);
    }

    private static bool RunStagedLifecycle(Scenario scenario)
    {
        OperationPreparation prepared = RequireValue(scenario.Kernel.PrepareExternalOperation(
            scenario.Owner,
            [
                new(scenario.Input.Handle, RegionUseMode.ReadOnly, new(0, scenario.PayloadBytes)),
                new(scenario.Output.Handle, RegionUseMode.StagedOutput, new(0, scenario.PayloadBytes)),
            ], ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged));
        RequireValue(scenario.Kernel.AdmitExternalOperation(scenario.Owner, prepared.Operation,
            scenario.Dependencies));
        OperationBinding binding = RequireValue(scenario.Kernel.RecordExternalOperationSubmission(
            scenario.Owner, prepared.Operation, scenario.Dependencies));
        scenario.Input.Span.CopyTo(scenario.Staging);
        RequireValue(scenario.Kernel.RecordExternalOperationCompletion(scenario.Owner,
            new(binding, ExternalOperationCompletionDisposition.Completed)));
        RequireValue(scenario.Kernel.RecordExternalOperationVisibility(scenario.Owner,
            new(binding, ExternalVisibilityRequirement.PublicationFence, true)));
        RequireValue(scenario.Kernel.PublishExternalOperation(scenario.Owner, prepared.Operation,
            scenario.Dependencies, new(ExternalPublicationPolicy.Staged),
            () => scenario.Staging.AsSpan().CopyTo(scenario.Output.Span)));
        RequireValue(scenario.Kernel.ReleaseExternalOperation(scenario.Owner, prepared.Operation,
            new(true, false)));
        return Valid(scenario);
    }

    private static Scenario Create(int payloadBytes)
    {
        var kernel = new RuntimeKernel();
        ProcessHandle owner = CreateProcess(kernel, 101_001, 101_002);
        OwnedBuffer<byte> input = RequireValue(kernel.AllocateBuffer<byte>(owner, payloadBytes));
        OwnedBuffer<byte> output = RequireValue(kernel.AllocateBuffer<byte>(owner, payloadBytes));
        input.Span.Fill(0xA5);
        output.Span.Clear();
        return new(kernel, owner, input, output, new byte[payloadBytes], payloadBytes,
            new(7, 11, 13, 17));
    }

    private static bool Valid(Scenario scenario) =>
        scenario.Output.Span[0] == 0xA5 && scenario.Output.Span[^1] == 0xA5;

    private static Summary Summarize(int payloadBytes, IReadOnlyList<Sample> samples)
    {
        double direct = MedianOfRoundMedians("direct-one-copy");
        double twoCopy = MedianOfRoundMedians("staged-two-copy-only");
        double lifecycle = MedianOfRoundMedians("staged-owner-lifecycle");
        return new(payloadBytes, direct, twoCopy, lifecycle,
            PercentDelta(direct, twoCopy), PercentDelta(twoCopy, lifecycle),
            SupportsDirectCoherentPromotion: false,
            "Two-copy delta isolates the managed staging copy shape; lifecycle delta adds existing owner-state transitions. Neither is device or physical-ordering evidence.");

        double MedianOfRoundMedians(string arm) => Percentile(samples
            .Where(sample => sample.PayloadBytes == payloadBytes && sample.Arm == arm)
            .Select(static sample => sample.MedianNanoseconds).Order().ToArray(), 0.5);
    }

    private static ProcessHandle CreateProcess(RuntimeKernel kernel, ulong processId, ulong domainId)
    {
        var manifest = new SingProcessManifestV1(new(processId), new(domainId), 1,
            $"p01-{processId}", ExecutionRole.Sip, MemoryProfile.SipRegion);
        RequireValue(kernel.CreateProcess(manifest));
        return new(manifest.ProcessId, manifest.Generation);
    }

    private static T RequireValue<T>(KernelResult<T> result)
    {
        if (!result.IsSuccess || result.Value is null) throw new InvalidOperationException(result.Message);
        return result.Value;
    }
    private static double PercentDelta(double baseline, double candidate) =>
        baseline == 0 ? 0 : (candidate - baseline) / baseline * 100;
    private static double Percentile(double[] ordered, double percentile) =>
        ordered[(int)Math.Ceiling(percentile * ordered.Length) - 1];
    private static double ToNanoseconds(long ticks) => ticks * (1_000_000_000d / Stopwatch.Frequency);

    private sealed record Options(string OutputPath, int Iterations, int Rounds);
    private sealed record Scenario(RuntimeKernel Kernel, ProcessHandle Owner, OwnedBuffer<byte> Input,
        OwnedBuffer<byte> Output, byte[] Staging, int PayloadBytes,
        OperationDependencySnapshot Dependencies);
    public sealed record EnvironmentTuple(string Runtime, string OperatingSystem, string RuntimeIdentifier,
        int LogicalProcessorCount, long StopwatchFrequency, string ExecutionMode);
    public sealed record Methodology(int IterationsPerArmPerRound, int Rounds,
        IReadOnlyList<int> PayloadBytes, string TimingBoundary, string Limitations);
    public sealed record Sample(string Arm, int PayloadBytes, int Round, int Operations,
        double ThroughputPerSecond, double MedianNanoseconds, double P95Nanoseconds,
        double P99Nanoseconds, double MaximumNanoseconds, double AllocatedBytesPerOperation);
    public sealed record Summary(int PayloadBytes, double DirectOneCopyMedianNanoseconds,
        double StagedTwoCopyMedianNanoseconds, double StagedOwnerLifecycleMedianNanoseconds,
        double TwoCopyOverheadPercent, double OwnerLifecycleOverTwoCopyPercent,
        bool SupportsDirectCoherentPromotion, string Interpretation);
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
        if (iterations is < 50 or > 10_000) throw new ArgumentOutOfRangeException(nameof(iterations));
        if (rounds is < 5 or > 25) throw new ArgumentOutOfRangeException(nameof(rounds));
        return new(Path.GetFullPath(output), iterations, rounds);
    }
}
