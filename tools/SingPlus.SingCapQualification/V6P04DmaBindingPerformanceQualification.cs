using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SingPlus.Contracts;

namespace SingPlus.SingCapQualification;

public static class V6P04DmaBindingPerformanceQualification
{
    private const int DefaultIterations = 5_000;
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
        Warmup(Math.Min(iterations, 1_000));
        DmaExecutionBindingV1 binding = Binding(17);
        byte[] canonical = binding.SerializeCanonical();
        string[] regionFacts = Enumerable.Range(0, iterations)
            .Select(index => $"region=41/3;mapping=71/5;range={index % 64}/64;direction=2")
            .ToArray();
        string[] domainFacts = Enumerable.Range(0, iterations)
            .Select(index => $"domain=101;process=103/7;binding=107/11;device=device/p04/{index % 8}")
            .ToArray();

        var samples = new List<Sample>(checked(rounds * 4));
        for (int round = 0; round < rounds; round++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            string[] order = Rotate(round);
            foreach (string arm in order)
            {
                samples.Add(arm switch
                {
                    "record-equality" => MeasureArm(arm, round, iterations,
                        _ => binding.Equals(binding)),
                    "validate-and-match-current" => MeasureArm(arm, round, iterations,
                        _ => binding.MatchesCurrent(binding)),
                    "construct-digests-and-validate" => MeasureArm(arm, round, iterations,
                        index => Construct(regionFacts[index], domainFacts[index], (ulong)index + 1)),
                    _ => MeasureArm(arm, round, iterations,
                        _ => RoundTrip(binding, canonical)),
                });
            }
        }

        Summary summary = Summarize(samples);
        return new("singnext.v6.p04-dma-binding-performance/1", DateTimeOffset.UtcNow,
            new(Environment.Version.ToString(), Environment.OSVersion.ToString(),
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                Environment.ProcessorCount, Stopwatch.Frequency, "Release JIT host"),
            new(iterations, rounds, DmaExecutionBindingV1.CanonicalSize,
                "Realistic region/security fact strings are prepared before timing. Equality compares an immutable exact tuple. Match validates both supplied/current bindings and compares every field. Construction hashes both fact strings and validates all generations. Round-trip serializes the validated binding, parses canonical bytes, validates, and matches the result. Arm order rotates; forced GC occurs only between rounds.",
                "Contract/sideband CPU cost only. No RuntimeKernel owner lookup, provider callback, mapping invalidation, page fault, device submit/completion, DMA byte movement, IOMMU, hardware counter, contention, CPU affinity, isolated host, or product workload/SLO."),
            samples, summary,
            "Generation-binding construction/check/codec overhead only. Functional submit/complete/race behavior is qualified separately; these timings cannot prove invalidation closure, physical DMA/IOMMU isolation, hardware performance, production readiness, or gate promotion.");
    }

    private static void Warmup(int operations)
    {
        DmaExecutionBindingV1 binding = Binding(17);
        byte[] canonical = binding.SerializeCanonical();
        for (int index = 0; index < operations; index++)
        {
            _ = binding.Equals(binding);
            _ = binding.MatchesCurrent(binding);
            _ = Construct("region=41/3;mapping=71/5;range=0/64;direction=2",
                "domain=101;process=103/7;binding=107/11;device=device/p04/0", (ulong)index + 1);
            _ = RoundTrip(binding, canonical);
        }
    }

    private static Sample MeasureArm(string arm, int round, int iterations, Func<int, bool> operation)
    {
        var elapsed = new long[iterations];
        long allocated = 0;
        var wall = Stopwatch.StartNew();
        for (int index = 0; index < iterations; index++)
        {
            long allocationStart = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            bool valid = operation(index);
            elapsed[index] = Stopwatch.GetTimestamp() - started;
            allocated += GC.GetAllocatedBytesForCurrentThread() - allocationStart;
            if (!valid) throw new InvalidOperationException("P04 measured binding operation violated its invariant.");
        }
        wall.Stop();
        Array.Sort(elapsed);
        return new(arm, round, iterations, iterations / wall.Elapsed.TotalSeconds,
            ToNanoseconds(elapsed[(int)Math.Ceiling(iterations * 0.50) - 1]),
            ToNanoseconds(elapsed[(int)Math.Ceiling(iterations * 0.95) - 1]),
            ToNanoseconds(elapsed[(int)Math.Ceiling(iterations * 0.99) - 1]),
            ToNanoseconds(elapsed[^1]), (double)allocated / iterations);
    }

    private static bool Construct(string regionFacts, string domainFacts, ulong mutationGeneration)
    {
        DmaExecutionBindingV1 binding = new(1, Digest(regionFacts), 3, mutationGeneration,
            7, 11, 5, 13, 17, 19, 23, DmaEffectStateV1.Admitted, Digest(domainFacts));
        return binding.Validate().MutationGeneration == mutationGeneration && !binding.AuthorizesDma;
    }

    private static bool RoundTrip(DmaExecutionBindingV1 binding, byte[] canonical)
    {
        byte[] serialized = binding.SerializeCanonical();
        DmaExecutionBindingV1 parsed = DmaExecutionBindingV1.ParseCanonical(canonical);
        return serialized.AsSpan().SequenceEqual(canonical) && binding.MatchesCurrent(parsed);
    }

    private static DmaExecutionBindingV1 Binding(ulong mutationGeneration) => new DmaExecutionBindingV1(1,
        new string('a', 64), 3, mutationGeneration, 7, 11, 5, 13, 17, 19, 23,
        DmaEffectStateV1.Admitted, new string('b', 64)).Validate();
    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static Summary Summarize(IReadOnlyList<Sample> samples)
    {
        double equality = Median("record-equality");
        double match = Median("validate-and-match-current");
        double construct = Median("construct-digests-and-validate");
        double roundTrip = Median("canonical-roundtrip-and-match");
        return new(equality, match, construct, roundTrip,
            PercentDelta(equality, match), PercentDelta(match, roundTrip),
            SupportsGatePromotion: false,
            "Absolute host costs separate immutable tuple equality, generation-aware validation/match, admission-side digest construction, and wire-side canonical codec work.");

        double Median(string arm) => Percentile(samples.Where(sample => sample.Arm == arm)
            .Select(static sample => sample.MedianNanoseconds).Order().ToArray(), 0.5);
    }

    private static string[] Rotate(int round)
    {
        string[] arms = ["record-equality", "validate-and-match-current",
            "construct-digests-and-validate", "canonical-roundtrip-and-match"];
        int offset = round % arms.Length;
        return arms.Skip(offset).Concat(arms.Take(offset)).ToArray();
    }

    private static double PercentDelta(double baseline, double candidate) =>
        baseline == 0 ? 0 : (candidate - baseline) / baseline * 100;
    private static double Percentile(double[] ordered, double percentile) =>
        ordered[(int)Math.Ceiling(percentile * ordered.Length) - 1];
    private static double ToNanoseconds(long ticks) => ticks * (1_000_000_000d / Stopwatch.Frequency);

    private sealed record Options(string OutputPath, int Iterations, int Rounds);
    public sealed record EnvironmentTuple(string Runtime, string OperatingSystem, string RuntimeIdentifier,
        int LogicalProcessorCount, long StopwatchFrequency, string ExecutionMode);
    public sealed record Methodology(int IterationsPerArmPerRound, int Rounds,
        int CanonicalBindingBytes, string TimingBoundary, string Limitations);
    public sealed record Sample(string Arm, int Round, int Operations, double ThroughputPerSecond,
        double MedianNanoseconds, double P95Nanoseconds, double P99Nanoseconds,
        double MaximumNanoseconds, double AllocatedBytesPerOperation);
    public sealed record Summary(double RecordEqualityMedianNanoseconds,
        double ValidateAndMatchMedianNanoseconds, double ConstructDigestsMedianNanoseconds,
        double CanonicalRoundTripMedianNanoseconds, double MatchOverEqualityPercent,
        double RoundTripOverMatchPercent, bool SupportsGatePromotion, string Interpretation);
    public sealed record Report(string Schema, DateTimeOffset CapturedUtc, EnvironmentTuple Environment,
        Methodology Methodology, IReadOnlyList<Sample> Samples, Summary Summary,
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
        if (iterations is < 500 or > 100_000) throw new ArgumentOutOfRangeException(nameof(iterations));
        if (rounds is < 5 or > 25) throw new ArgumentOutOfRangeException(nameof(rounds));
        return new(Path.GetFullPath(output), iterations, rounds);
    }
}
