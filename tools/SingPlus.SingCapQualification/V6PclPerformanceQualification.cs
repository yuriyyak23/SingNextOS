using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.SingCapQualification;

public static class V6PclPerformanceQualification
{
    private const int DefaultIterations = 10_000;
    private const int DefaultRounds = 15;
    private static readonly byte[] Output = "v6-pcl-performance-output"u8.ToArray();

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
        CompilerLoweringEvidenceEnvelopeV1 envelope = Envelope();
        var expected = new V6LoweringEvidenceExpectation(
            "compiler-contract-v6", D('a'), D('d'), 1, true,
            envelope.Evidence.StaticFactSetDigest);
        for (int warmup = 0; warmup < 2_000; warmup++)
        {
            var verifier = new V6CompilerLoweringEvidenceVerifier();
            Require(verifier.VerifyForAdmission(envelope, Output, expected,
                KernelResult.Ok, KernelResult.Ok), expectedCacheHit: false);
        }

        var samples = new List<Sample>(checked(rounds * 2));
        for (int round = 0; round < rounds; round++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            if ((round & 1) == 0)
            {
                samples.Add(MeasureMisses(round, iterations, envelope, expected));
                samples.Add(MeasureHits(round, iterations, envelope, expected));
            }
            else
            {
                samples.Add(MeasureHits(round, iterations, envelope, expected));
                samples.Add(MeasureMisses(round, iterations, envelope, expected));
            }
        }

        double[] miss = samples.Where(static row => row.Mode == "cache-miss")
            .Select(static row => row.NanosecondsPerOperation).Order().ToArray();
        double[] hit = samples.Where(static row => row.Mode == "cache-hit")
            .Select(static row => row.NanosecondsPerOperation).Order().ToArray();
        double missMedian = Percentile(miss, 0.5);
        double hitMedian = Percentile(hit, 0.5);
        double savings = missMedian == 0 ? 0 : (missMedian - hitMedian) / missMedian * 100;
        bool observed = savings >= 10;
        return new(
            "singnext.v6.pcl-performance/1",
            DateTimeOffset.UtcNow,
            new(Environment.Version.ToString(), Environment.OSVersion.ToString(),
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                Environment.ProcessorCount, Stopwatch.Frequency, "Release JIT host"),
            new(iterations, rounds,
                "Alternating batch measurements compare a fresh policy-generation cache miss on every admission with a prewarmed identical-tuple cache hit. Both paths hash the exact output and run two no-op live checks per admission.",
                "No CPU affinity, isolated host, hardware counter, GC pause subtraction, or product workload/SLO."),
            samples,
            new(missMedian, hitMedian, savings,
                Percentile(miss, 0.95), Percentile(hit, 0.95), observed,
                observed
                    ? "A repeated-check saving was observed on this named host tuple; product threshold and broader workload qualification remain open."
                    : "This host run did not demonstrate a 10% median repeated-check saving; PCL TCB inclusion is not performance-justified."),
            "Measurement evidence only; V6-PROOF-CARRYING-LOWERING remains OFF and live authority/runtime legality are never cached.");
    }

    private static Sample MeasureMisses(int round, int iterations,
        CompilerLoweringEvidenceEnvelopeV1 envelope, V6LoweringEvidenceExpectation expected)
    {
        var verifier = new V6CompilerLoweringEvidenceVerifier();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (int index = 0; index < iterations; index++)
            Require(verifier.VerifyForAdmission(envelope, Output,
                expected with { VerifierPolicyGeneration = checked((ulong)index + 1) },
                KernelResult.Ok, KernelResult.Ok), expectedCacheHit: false);
        long elapsed = Stopwatch.GetTimestamp() - started;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        if (verifier.StaticVerificationCount != iterations)
            throw new InvalidOperationException("Cache-miss campaign did not execute one static verification per admission.");
        return SampleOf("cache-miss", round, iterations, elapsed, allocated, verifier.StaticVerificationCount);
    }

    private static Sample MeasureHits(int round, int iterations,
        CompilerLoweringEvidenceEnvelopeV1 envelope, V6LoweringEvidenceExpectation expected)
    {
        var verifier = new V6CompilerLoweringEvidenceVerifier();
        Require(verifier.VerifyForAdmission(envelope, Output, expected,
            KernelResult.Ok, KernelResult.Ok), expectedCacheHit: false);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (int index = 0; index < iterations; index++)
            Require(verifier.VerifyForAdmission(envelope, Output, expected,
                KernelResult.Ok, KernelResult.Ok), expectedCacheHit: true);
        long elapsed = Stopwatch.GetTimestamp() - started;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        if (verifier.StaticVerificationCount != 1)
            throw new InvalidOperationException("Cache-hit campaign repeated static verification.");
        return SampleOf("cache-hit", round, iterations, elapsed, allocated, verifier.StaticVerificationCount);
    }

    private static Sample SampleOf(string mode, int round, int iterations, long elapsed, long allocated,
        int staticVerifications) => new(mode, round, iterations, staticVerifications,
            elapsed * (1_000_000_000d / Stopwatch.Frequency) / iterations,
            (double)allocated / iterations);

    private static void Require(KernelResult<V6LoweringAdmissionReceipt> result, bool expectedCacheHit)
    {
        if (!result.IsSuccess || result.Value is null || !result.Value.EvidenceAccepted ||
            result.Value.CacheHit != expectedCacheHit || result.Value.AuthorizesExecution)
            throw new InvalidOperationException("PCL performance operation violated its admission/cache invariant.");
    }

    private static CompilerLoweringEvidenceEnvelopeV1 Envelope()
    {
        CompilerLoweringEvidenceV1 evidence = CompilerLoweringEvidenceV1.Create(
            "compiler-contract-v6", D('a'), D('b'), Sha(Output), D('d'),
            [new("input", 0, 4096, LoweringFootprintAccessV1.Read),
             new("output", 0, 4096, LoweringFootprintAccessV1.Write)],
            [new("input", "output", true)],
            [new("release-before-publish", true, true)],
            [new("vector-add", "nearest-even", "wrap", 128)]);
        return CompilerLoweringEvidenceEnvelopeV1.Create(evidence);
    }

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
        if (iterations is < 1_000 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(iterations));
        if (rounds is < 5 or > 50) throw new ArgumentOutOfRangeException(nameof(rounds));
        return new(Path.GetFullPath(output), iterations, rounds);
    }

    private static double Percentile(double[] ordered, double percentile) =>
        ordered[(int)Math.Ceiling(percentile * ordered.Length) - 1];
    private static string Sha(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static string D(char value) => new(value, 64);

    private sealed record Options(string OutputPath, int Iterations, int Rounds);
    public sealed record EnvironmentTuple(string Runtime, string OperatingSystem, string RuntimeIdentifier,
        int LogicalProcessorCount, long StopwatchFrequency, string ExecutionMode);
    public sealed record Methodology(int IterationsPerModePerRound, int Rounds,
        string Comparison, string Limitations);
    public sealed record Sample(string Mode, int Round, int Operations, int StaticVerifications,
        double NanosecondsPerOperation, double AllocatedBytesPerOperation);
    public sealed record Summary(double CacheMissMedianNanoseconds, double CacheHitMedianNanoseconds,
        double MedianSavingsPercent, double CacheMissP95Nanoseconds, double CacheHitP95Nanoseconds,
        bool TenPercentRepeatedCheckSavingObserved, string Interpretation);
    public sealed record Report(string Schema, DateTimeOffset CapturedUtc, EnvironmentTuple Environment,
        Methodology Methodology, IReadOnlyList<Sample> Samples, Summary Summary, string ClaimBoundary);
}
