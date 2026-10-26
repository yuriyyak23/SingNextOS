using YAKSys_Hybrid_CPU.ExecutableAdapter.Boot;
using Xunit;

namespace HybridCpu_ExecutableAdapter.Tests;

public sealed class CxlBootSelectionModelTests
{
    private readonly Guid _volume = Guid.NewGuid(); private readonly Guid _image = Guid.NewGuid();

    [Fact]
    public void Every_enumeration_permutation_selects_same_logical_replica()
    {
        CxlBootCandidate[] candidates = new[] { Candidate(Guid.Parse("20000000-0000-0000-0000-000000000000"), 2, 0x11), Candidate(Guid.Parse("10000000-0000-0000-0000-000000000000"), 2, 0x22) };
        BootSelectionPolicy policy = Policy(preferred: 0x22);
        foreach (CxlBootCandidate[]? order in new[] { candidates, candidates.Reverse().ToArray() })
        {
            BootSelectionResult result = new SemanticCxlBootSelector().Select(policy, order); Assert.Equal(BootSelectionFailure.None, result.Failure);
            Assert.Equal(0x22UL, result.Candidate!.Physical.Dsn);
        }
    }

    [Fact]
    public void Bdf_route_and_dsn_replacement_do_not_change_semantic_volume_selection()
    {
        CxlBootCandidate first = Candidate(Guid.NewGuid(), 2, 1); CxlBootCandidate moved = first with { Physical = new("replacement", 3, 200, 1, 7, null, "new-route") };
        SemanticCxlBootSelector selector = new SemanticCxlBootSelector();
        Assert.Equal(first.ImageId, selector.Select(Policy(), [first]).Candidate!.ImageId);
        Assert.Equal(first.ImageId, selector.Select(Policy(), [moved]).Candidate!.ImageId);
    }

    [Fact]
    public void Invalid_stale_and_property_mismatch_candidates_are_filtered()
    {
        CxlBootCandidate invalid = Candidate(Guid.NewGuid(), 4, 1) with { SignatureValid = false };
        CxlBootCandidate stale = Candidate(Guid.NewGuid(), 1, 2); CxlBootCandidate wrongProperty = Candidate(Guid.NewGuid(), 4, 3) with { Properties = 0 };
        CxlBootCandidate valid = Candidate(Guid.NewGuid(), 4, 4);
        Assert.Equal(valid.ReplicaId, new SemanticCxlBootSelector().Select(Policy() with { RollbackFloor = 3 }, [invalid, stale, wrongProperty, valid]).Candidate!.ReplicaId);
    }

    [Fact]
    public void Same_generation_conflicting_image_or_digest_is_split_brain()
    {
        CxlBootCandidate a = Candidate(Guid.NewGuid(), 5, 1); CxlBootCandidate b = Candidate(Guid.NewGuid(), 5, 2) with { ImageId = Guid.NewGuid() };
        Assert.Equal(BootSelectionFailure.SplitBrain, new SemanticCxlBootSelector().Select(Policy(), [a, b]).Failure);
        b = Candidate(Guid.NewGuid(), 5, 2) with { SignedDigest = [9, 9] };
        Assert.Equal(BootSelectionFailure.SplitBrain, new SemanticCxlBootSelector().Select(Policy(), [a, b]).Failure);
    }

    [Fact]
    public void Required_physical_selector_differs_from_preference()
    {
        CxlBootCandidate candidate = Candidate(Guid.NewGuid(), 2, null); SemanticCxlBootSelector selector = new SemanticCxlBootSelector();
        Assert.NotNull(selector.Select(Policy(preferred: 99), [candidate]).Candidate);
        Assert.Equal(BootSelectionFailure.RequiredPhysicalSelectorMissing, selector.Select(Policy(preferred: 99) with { RequirePhysicalMatch = true }, [candidate]).Failure);
        Assert.Equal(BootSelectionFailure.RequiredPhysicalSelectorMissing, selector.Select(Policy() with { RequirePhysicalMatch = true }, [candidate]).Failure);
    }

    [Fact]
    public void Exact_duplicate_replica_is_idempotent_and_order_independent()
    {
        CxlBootCandidate replica = Candidate(Guid.Parse("30000000-0000-0000-0000-000000000000"), 9, 17);
        SemanticCxlBootSelector selector = new SemanticCxlBootSelector();
        BootSelectionResult once = selector.Select(Policy(), [replica]);
        BootSelectionResult duplicated = selector.Select(Policy(), [replica, replica]);
        Assert.Equal(BootSelectionFailure.None, duplicated.Failure);
        Assert.Equal(once.Candidate, duplicated.Candidate);
        Assert.Equal(BootSelectionFailure.None, selector.Select(Policy() with { AllowReplicaFailover = false }, [replica, replica]).Failure);
    }

    [Fact]
    public void Selected_image_with_conflicting_generation_fails_closed_under_every_permutation()
    {
        Guid selected = Guid.NewGuid();
        CxlBootCandidate old = Candidate(Guid.NewGuid(), 4, 1) with { ImageId = selected };
        CxlBootCandidate current = Candidate(Guid.NewGuid(), 5, 2) with { ImageId = selected };
        BootSelectionPolicy policy = Policy() with { SelectedImageId = selected };
        SemanticCxlBootSelector selector = new SemanticCxlBootSelector();
        Assert.Equal(BootSelectionFailure.SplitBrain, selector.Select(policy, [old, current]).Failure);
        Assert.Equal(BootSelectionFailure.SplitBrain, selector.Select(policy, [current, old]).Failure);
    }

    [Fact]
    public void Highest_eligible_generation_wins_independent_of_device_and_fabric_path()
    {
        CxlBootCandidate old = Candidate(Guid.NewGuid(), 4, 10);
        CxlBootCandidate current = Candidate(Guid.NewGuid(), 7, 20) with
        {
            Physical = new("mld-renumbered", 11, 99, 2, 1, null, "switch-path-after-replacement")
        };
        SemanticCxlBootSelector selector = new SemanticCxlBootSelector();
        Assert.Equal(7UL, selector.Select(Policy(), [old, current]).Candidate!.ImageGeneration);
        Assert.Equal(7UL, selector.Select(Policy(), [current, old]).Candidate!.ImageGeneration);
    }

    [Fact]
    public void Same_volume_same_generation_conflict_remains_ambiguous_after_route_change()
    {
        CxlBootCandidate a = Candidate(Guid.NewGuid(), 12, 1);
        CxlBootCandidate b = Candidate(Guid.NewGuid(), 12, 2) with
        {
            ImageId = Guid.NewGuid(),
            Physical = new("other-fabric", 4, 5, 6, 7, 999, "route-b")
        };
        Assert.Equal(BootSelectionFailure.SplitBrain, new SemanticCxlBootSelector().Select(Policy(), [b, a]).Failure);
    }

    private BootSelectionPolicy Policy(ulong? preferred = null) => new(_volume, 3, null, 0, preferred, false, true);
    private CxlBootCandidate Candidate(Guid replica, ulong generation, ulong? dsn) => new(_volume, replica, _image, generation, [1, 2, 3], 3, true, true, new("endpoint-observation", 0, 1, 2, 3, dsn, "route"));
}
