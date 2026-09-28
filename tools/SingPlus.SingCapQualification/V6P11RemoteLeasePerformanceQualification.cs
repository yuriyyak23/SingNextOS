using System.Diagnostics;
using System.Text.Json;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.SingCapQualification;

public static class V6P11RemoteLeasePerformanceQualification
{
    private const int DefaultIterations = 5_000;
    private const int DefaultRounds = 7;
    private const ulong ProviderGeneration = 19;
    private static readonly RemoteAuthorityLeaseV1 Lease = new(1,
        Guid.Parse("690c6048-a963-4ae0-a687-f2ee6adbd44d"),
        "host:owner", 3, "host:remote", 5, new string('c', 64),
        RemoteLeaseRightsV1.Read | RemoteLeaseRightsV1.Execute | RemoteLeaseRightsV1.StagedWrite,
        7, 11, 13, 17);
    private static readonly RemoteLeaseCurrentStateV1 Current = new(3, 5, 7, 11, 13, false, false);
    private static readonly RemoteResourceEscrowV1 Escrow = new(1, 100, 60, 0, 0);
    private static readonly RemoteEffectClosureV1 Closure = new(1, Lease.LeaseId,
        Lease.OwnerEpoch, Lease.LeaseGeneration, ProviderGeneration, 23, true, true);

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
        var samples = new List<Sample>(checked(rounds * 6));
        for (int round = 0; round < rounds; round++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            foreach (string arm in Rotate(round))
            {
                samples.Add(arm switch
                {
                    "cached-lease-admission" => MeasureArm(arm, round, iterations, CachedLeaseAdmission),
                    "lease-and-closure-predicate" => MeasureArm(arm, round, iterations, LeaseAndClosurePredicate),
                    "pilot-issue-and-submit" => MeasureArm(arm, round, iterations, PilotIssueAndSubmit),
                    "full-pilot-lifecycle" => MeasureArm(arm, round, iterations, FullPilotLifecycle),
                    "managed-transport-lifecycle" => MeasureArm(arm, round, iterations, ManagedTransportLifecycle),
                    _ => MeasureArm(arm, round, iterations, AuthenticatedRestartReplay),
                });
            }
        }

        Summary summary = Summarize(samples);
        return new("singnext.v6.p11-remote-lease-performance/1", DateTimeOffset.UtcNow,
            new(Environment.Version.ToString(), Environment.OSVersion.ToString(),
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                Environment.ProcessorCount, Stopwatch.Frequency, "Release JIT host"),
            new(iterations, rounds,
                "All immutable lease/current/escrow/closure values are prepared before timing. Arms evaluate one already-issued exact lease, evaluate lease admission plus exact reclaim predicate, create a fresh qualification-only pilot and submit a narrow staged-write allocation, run its direct full lifecycle, run the full lifecycle through the canonical bounded request/response codec and idempotent managed two-endpoint transport, or append/replay an authenticated two-transition lease chain and persist a fresh owner restart high-watermark in a memory store. Arm order rotates; forced GC occurs only between rounds.",
                "In-process qualification transport and memory-backed journal only. Serialization, owner endpoint dispatch, HMAC chaining, and replay are measured, but no network stack, remote OS/process/host, transport authentication, durable storage I/O, consensus, clock synchronization, physical packet loss, fabric/device provider, contention, CPU affinity, isolated host, hardware counter, product workload, or SLO is measured."),
            samples, summary,
            "Host-local cached predicate, pilot orchestration, managed serialized two-endpoint transport, and memory-backed authenticated restart replay overhead only. It does not measure cross-host coordination, prove an OS/network transport or ownership-transfer protocol, measure durable-media I/O, establish production latency, or promote the gate.");
    }

    private static void Warmup(int operations)
    {
        for (int index = 0; index < operations; index++)
        {
            _ = CachedLeaseAdmission();
            _ = LeaseAndClosurePredicate();
            _ = PilotIssueAndSubmit();
            _ = FullPilotLifecycle();
            _ = ManagedTransportLifecycle();
            _ = AuthenticatedRestartReplay();
        }
    }

    private static Sample MeasureArm(string arm, int round, int iterations, Func<bool> operation)
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
            if (!valid) throw new InvalidOperationException("P11 measured remote-lease invariant failed.");
        }
        wall.Stop();
        Array.Sort(elapsed);
        return new(arm, round, iterations, iterations / wall.Elapsed.TotalSeconds,
            ToNanoseconds(elapsed[(int)Math.Ceiling(iterations * 0.50) - 1]),
            ToNanoseconds(elapsed[(int)Math.Ceiling(iterations * 0.95) - 1]),
            ToNanoseconds(elapsed[(int)Math.Ceiling(iterations * 0.99) - 1]),
            ToNanoseconds(elapsed[^1]), (double)allocated / iterations);
    }

    private static bool CachedLeaseAdmission() =>
        RemoteAuthorityLeaseEvaluatorV1.Evaluate(Lease, Current) == RemoteLeaseAdmissionCodeV1.Eligible &&
        !Lease.IsParentAuthority && !Lease.TransfersOwnership && !Lease.UsesWallClockExpiry;

    private static bool LeaseAndClosurePredicate() => CachedLeaseAdmission() &&
        RemoteReclaimPredicateV1.IsSatisfied(Lease, Closure) && !Closure.AuthorizesReclaim;

    private static bool PilotIssueAndSubmit()
    {
        var created = V6RemoteDelegatedResourcePilot.CreateForQualification(
            Lease, Current, Escrow, ProviderGeneration);
        if (!created.IsSuccess) return false;
        var submitted = created.Value!.Submit(Lease, RemoteLeaseRightsV1.StagedWrite, 20);
        return submitted.IsSuccess && submitted.Value!.SubmitCount == 1 &&
            submitted.Value.HasSingleLogicalOwner && !submitted.Value.TransfersParentAuthority;
    }

    private static bool FullPilotLifecycle()
    {
        var created = V6RemoteDelegatedResourcePilot.CreateForQualification(
            Lease, Current, Escrow, ProviderGeneration);
        if (!created.IsSuccess) return false;
        V6RemoteDelegatedResourcePilot pilot = created.Value!;
        return pilot.Submit(Lease, RemoteLeaseRightsV1.StagedWrite, 20).IsSuccess &&
            pilot.Fence(Lease).IsSuccess && pilot.ObserveEffectClosure(Closure).IsSuccess &&
            pilot.Publish(Lease).IsSuccess && pilot.Reclaim(Closure).IsSuccess &&
            pilot.Query().State == V6RemoteDelegatedResourcePilotState.Reclaimed;
    }

    private static bool ManagedTransportLifecycle()
    {
        var created = V6RemoteDelegatedResourcePilot.CreateForQualification(
            Lease, Current, Escrow, ProviderGeneration);
        if (!created.IsSuccess) return false;
        var owner = new V6RemoteLeaseOwnerEndpoint(created.Value!);
        var transport = new V6ManagedRemoteLeaseTransport(owner);
        V6RemoteLeaseCommand Command(Guid request, V6RemoteLeaseCommandKind kind,
            RemoteLeaseRightsV1 rights = RemoteLeaseRightsV1.None, ulong allocation = 0,
            RemoteEffectClosureV1? closure = null) =>
            new(1, request, kind, Lease, rights, allocation, closure);
        return transport.Send(Command(new("3a534cee-818d-4184-b355-4fb769bd14dd"),
                   V6RemoteLeaseCommandKind.Submit, RemoteLeaseRightsV1.StagedWrite, 20)).IsSuccess &&
               transport.Send(Command(new("62ed6e55-c6cc-4b84-a6d7-831ff859dacf"),
                   V6RemoteLeaseCommandKind.Fence)).IsSuccess &&
               transport.Send(Command(new("3d64b273-8704-4898-8831-1270a28dba29"),
                   V6RemoteLeaseCommandKind.EffectClosure, closure: Closure)).IsSuccess &&
               transport.Send(Command(new("3c18710b-9562-4ab9-9255-f1b2aa02b9ef"),
                   V6RemoteLeaseCommandKind.Publish)).IsSuccess &&
               transport.Send(Command(new("2b7c0970-d5f0-4300-b6f5-2746474145a1"),
                   V6RemoteLeaseCommandKind.Reclaim, closure: Closure)).IsSuccess &&
               owner.Query().State == V6RemoteDelegatedResourcePilotState.Reclaimed;
    }

    private static bool AuthenticatedRestartReplay()
    {
        var store = new QualificationStore();
        byte[] key = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
        var journal = new V6RemoteLeaseRecoveryJournal(store, key,
            new("372d8545-b288-4dfe-91f6-ab67ce7291dc"));
        V6RemoteLeaseRecoveryPayload Payload(V6RemoteLeaseRecoveryTransition transition,
            RemoteResourceEscrowV1 escrow) => new(transition, Lease.OwnerHostIdentity,
            Lease.OwnerIncarnation, Lease.OwnerEpoch, Lease, escrow, ProviderGeneration);
        journal.Append(Payload(V6RemoteLeaseRecoveryTransition.Issued, Escrow));
        journal.Append(Payload(V6RemoteLeaseRecoveryTransition.Submitted,
            Escrow with { ConsumedAllocation = 20 }));
        var restarted = new V6RemoteLeaseRecoveryJournal(store, key)
            .RecordOwnerRestart(Lease.OwnerHostIdentity, 4, 8);
        var item = restarted.Items.Single();
        return item.Disposition == V6RemoteLeaseRestartDisposition.QuarantinedAwaitingClosure &&
               item.RequiresExactClosure && !item.AuthorizesExecution &&
               !item.AuthorizesPublication && !item.AuthorizesReclaim &&
               !restarted.RestoresLeaseAuthority && !restarted.AllowsOldLeasePublication;
    }

    private static Summary Summarize(IReadOnlyList<Sample> samples)
    {
        return new(Median("cached-lease-admission"), Median("lease-and-closure-predicate"),
            Median("pilot-issue-and-submit"), Median("full-pilot-lifecycle"),
            Median("managed-transport-lifecycle"), Median("authenticated-restart-replay"),
            SupportsGatePromotion: false,
            "Absolute host costs separate the cached narrow-lease predicate, direct pilot lifecycle, canonical codec plus idempotent managed endpoint dispatch, and authenticated memory-backed restart replay.");

        double Median(string arm) => Percentile(samples.Where(sample => sample.Arm == arm)
            .Select(static sample => sample.MedianNanoseconds).Order().ToArray(), 0.5);
    }

    private static string[] Rotate(int round)
    {
        string[] arms = ["cached-lease-admission", "lease-and-closure-predicate",
            "pilot-issue-and-submit", "full-pilot-lifecycle", "managed-transport-lifecycle",
            "authenticated-restart-replay"];
        int offset = round % arms.Length;
        return arms.Skip(offset).Concat(arms.Take(offset)).ToArray();
    }

    private static double Percentile(double[] ordered, double percentile) =>
        ordered[(int)Math.Ceiling(percentile * ordered.Length) - 1];
    private static double ToNanoseconds(long ticks) => ticks * (1_000_000_000d / Stopwatch.Frequency);

    private sealed record Options(string OutputPath, int Iterations, int Rounds);
    private sealed class QualificationStore : IResourceBudgetJournalStore
    {
        private readonly List<byte[]> _frames = [];
        public IReadOnlyList<byte[]> ReadFrames() => _frames.Select(static frame => frame.ToArray()).ToArray();
        public void AppendFrame(ReadOnlySpan<byte> frame) => _frames.Add(frame.ToArray());
    }
    public sealed record EnvironmentTuple(string Runtime, string OperatingSystem, string RuntimeIdentifier,
        int LogicalProcessorCount, long StopwatchFrequency, string ExecutionMode);
    public sealed record Methodology(int IterationsPerArmPerRound, int Rounds,
        string TimingBoundary, string Limitations);
    public sealed record Sample(string Arm, int Round, int Operations, double ThroughputPerSecond,
        double MedianNanoseconds, double P95Nanoseconds, double P99Nanoseconds,
        double MaximumNanoseconds, double AllocatedBytesPerOperation);
    public sealed record Summary(double CachedLeaseAdmissionMedianNanoseconds,
        double LeaseAndClosurePredicateMedianNanoseconds, double PilotIssueAndSubmitMedianNanoseconds,
        double FullPilotLifecycleMedianNanoseconds, double ManagedTransportLifecycleMedianNanoseconds,
        double AuthenticatedRestartReplayMedianNanoseconds,
        bool SupportsGatePromotion, string Interpretation);
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
        if (iterations is < 500 or > 100_000) throw new ArgumentOutOfRangeException(nameof(iterations));
        if (rounds is < 5 or > 25) throw new ArgumentOutOfRangeException(nameof(rounds));
        return new(Path.GetFullPath(output), iterations, rounds);
    }
}
