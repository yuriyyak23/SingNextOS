using System.Collections.Immutable;

namespace SingPlus.Runtime;

internal sealed record SipJobWorkerItemDescriptor(
    uint Version,
    string StageId,
    string InvocationCorrelationId,
    string ClosedStateSchemaId);

internal sealed record SipJobParallelDagEligibilityDescriptor(
    uint Version,
    int BranchCount,
    int WorkerCount,
    int AvailableProcessorCount,
    ImmutableArray<SipJobWorkerItemDescriptor> WorkItems,
    ImmutableArray<string> DeclaredGateSet);

internal enum SipJobParallelDagEligibilityError
{
    None = 0,
    UnknownVersion,
    UnsupportedGate,
    UnsupportedTopology,
    MalformedWorkItem,
}

internal readonly record struct SipJobParallelDagEligibilityResult(
    SipJobParallelDagEligibilityError Error,
    ImmutableArray<string> OrderedStageIds,
    string? Detail)
{
    internal bool IsSuccess => Error == SipJobParallelDagEligibilityError.None;
}

internal static class SipJobParallelDagEligibilityVerifier
{
    internal const uint Version = 1;
    internal static readonly ImmutableArray<string> ExactGates =
        ["FG-JOB-LINEAR", "FG-READONLY-DAG", "FG-DAG-PARALLEL"];
    private static readonly int[] BranchCounts = [2, 4, 8];
    private static readonly int[] WorkerCounts = [1, 2, 4, 8, 16, 32];

    internal static SipJobParallelDagEligibilityResult Verify(SipJobParallelDagEligibilityDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.WorkItems.IsDefault || descriptor.WorkItems.Any(static item => item is null))
            return Fail(SipJobParallelDagEligibilityError.MalformedWorkItem,
                "Parallel DAG work items cannot be a default collection or contain null entries.");
        if (descriptor.DeclaredGateSet.IsDefault)
            return Fail(SipJobParallelDagEligibilityError.UnsupportedGate,
                "Parallel DAG requires an explicit exact prerequisite gate set.");
        if (descriptor.Version != Version || descriptor.WorkItems.Any(static item => item.Version != Version))
            return Fail(SipJobParallelDagEligibilityError.UnknownVersion, "Unknown parallel DAG eligibility version.");
        if (!descriptor.DeclaredGateSet.SequenceEqual(ExactGates))
            return Fail(SipJobParallelDagEligibilityError.UnsupportedGate, "Parallel DAG requires the exact prerequisite gate set.");
        if (!BranchCounts.Contains(descriptor.BranchCount) || !WorkerCounts.Contains(descriptor.WorkerCount) ||
            descriptor.AvailableProcessorCount != Environment.ProcessorCount ||
            descriptor.WorkerCount > descriptor.AvailableProcessorCount ||
            descriptor.WorkItems.Length != descriptor.BranchCount)
            return Fail(SipJobParallelDagEligibilityError.UnsupportedTopology,
                "Requested branch/worker topology must match the current recorded runtime environment.");
        if (descriptor.WorkItems.Any(static item => !Canonical(item.StageId) || !Canonical(item.InvocationCorrelationId) ||
                !Canonical(item.ClosedStateSchemaId) ||
                !item.ClosedStateSchemaId.StartsWith("schema:", StringComparison.Ordinal) ||
                item.ClosedStateSchemaId.Length == "schema:".Length) ||
            descriptor.WorkItems.Select(static item => item.StageId).Distinct(StringComparer.Ordinal).Count() != descriptor.WorkItems.Length ||
            descriptor.WorkItems.Select(static item => item.InvocationCorrelationId).Distinct(StringComparer.Ordinal).Count() != descriptor.WorkItems.Length)
            return Fail(SipJobParallelDagEligibilityError.MalformedWorkItem,
                "Worker items contain only unique stage/correlation IDs and a closed-state schema.");

        return new(SipJobParallelDagEligibilityError.None,
            descriptor.WorkItems.Select(static item => item.StageId).Order(StringComparer.Ordinal).ToImmutableArray(), null);
    }

    private static bool Canonical(string value) => !string.IsNullOrWhiteSpace(value) && value == value.Trim();
    private static SipJobParallelDagEligibilityResult Fail(SipJobParallelDagEligibilityError error, string detail) => new(error, [], detail);
}
