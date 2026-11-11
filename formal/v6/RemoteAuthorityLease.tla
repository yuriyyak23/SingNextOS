-------------------------- MODULE RemoteAuthorityLease --------------------------
EXTENDS Naturals, Sequences

(***************************************************************************
Safety model for a narrow delegated remote lease.  The original logical owner
remains authoritative.  This is a specification view, never a distributed
capability database or runtime authority service.  Time is a monotonic owner
sequence, not wall-clock time.  Partition/timeout never proves remote effects
closed.  Owner or remote reboot changes an incarnation and invalidates the old
lease without making reclaim safe.
***************************************************************************)

VARIABLES phase, ownerEpoch, ownerIncarnation, remoteIncarnation,
          leaseGeneration, ownerSequence, revoked, partitioned,
          submitCount, possibleRemoteEffect, fenced, effectClosed, reclaimed

vars == << phase, ownerEpoch, ownerIncarnation, remoteIncarnation,
           leaseGeneration, ownerSequence, revoked, partitioned,
           submitCount, possibleRemoteEffect, fenced, effectClosed, reclaimed >>

ExpectedOwnerEpoch == 1
ExpectedOwnerIncarnation == 1
ExpectedRemoteIncarnation == 1
ExpectedLeaseGeneration == 1
NotAfterOwnerSequence == 3

ExactLease ==
    /\ ownerEpoch = ExpectedOwnerEpoch
    /\ ownerIncarnation = ExpectedOwnerIncarnation
    /\ remoteIncarnation = ExpectedRemoteIncarnation
    /\ leaseGeneration = ExpectedLeaseGeneration

LeaseFresh == ExactLease /\ ownerSequence <= NotAfterOwnerSequence /\ ~revoked

Init ==
    /\ phase = "Issued"
    /\ ownerEpoch = 1
    /\ ownerIncarnation = 1
    /\ remoteIncarnation = 1
    /\ leaseGeneration = 1
    /\ ownerSequence = 1
    /\ revoked = FALSE
    /\ partitioned = FALSE
    /\ submitCount = 0
    /\ possibleRemoteEffect = FALSE
    /\ fenced = FALSE
    /\ effectClosed = FALSE
    /\ reclaimed = FALSE

RemoteSubmit ==
    /\ phase = "Issued"
    /\ LeaseFresh
    /\ ~partitioned
    /\ submitCount = 0
    /\ phase' = "PossibleEffect"
    /\ submitCount' = 1
    /\ possibleRemoteEffect' = TRUE
    /\ UNCHANGED << ownerEpoch, ownerIncarnation, remoteIncarnation,
                    leaseGeneration, ownerSequence, revoked, partitioned,
                    fenced, effectClosed, reclaimed >>

Partition ==
    /\ partitioned' = TRUE
    /\ UNCHANGED << phase, ownerEpoch, ownerIncarnation, remoteIncarnation,
                    leaseGeneration, ownerSequence, revoked, submitCount,
                    possibleRemoteEffect, fenced, effectClosed, reclaimed >>

Renew ==
    /\ phase = "Issued"
    /\ ~partitioned
    /\ LeaseFresh
    /\ leaseGeneration' = leaseGeneration + 1
    /\ ownerSequence' = ownerSequence + 1
    /\ UNCHANGED << phase, ownerEpoch, ownerIncarnation, remoteIncarnation,
                    revoked, partitioned, submitCount, possibleRemoteEffect,
                    fenced, effectClosed, reclaimed >>

Revoke ==
    /\ ~revoked
    /\ revoked' = TRUE
    /\ phase' = "Revoked"
    /\ UNCHANGED << ownerEpoch, ownerIncarnation, remoteIncarnation,
                    leaseGeneration, ownerSequence, partitioned, submitCount,
                    possibleRemoteEffect, fenced, effectClosed, reclaimed >>

OwnerReboot ==
    /\ ownerIncarnation' = ownerIncarnation + 1
    /\ ownerEpoch' = ownerEpoch + 1
    /\ phase' = "Stale"
    /\ UNCHANGED << remoteIncarnation, leaseGeneration, ownerSequence,
                    revoked, partitioned, submitCount, possibleRemoteEffect,
                    fenced, effectClosed, reclaimed >>

RemoteReboot ==
    /\ remoteIncarnation' = remoteIncarnation + 1
    /\ phase' = "Stale"
    /\ UNCHANGED << ownerEpoch, ownerIncarnation, leaseGeneration,
                    ownerSequence, revoked, partitioned, submitCount,
                    possibleRemoteEffect, fenced, effectClosed, reclaimed >>

Fence ==
    /\ ~fenced
    /\ fenced' = TRUE
    /\ phase' = "Fenced"
    /\ UNCHANGED << ownerEpoch, ownerIncarnation, remoteIncarnation,
                    leaseGeneration, ownerSequence, revoked, partitioned,
                    submitCount, possibleRemoteEffect, effectClosed, reclaimed >>

ObserveEffectClosure ==
    /\ fenced
    /\ effectClosed' = TRUE
    /\ phase' = "Closed"
    /\ UNCHANGED << ownerEpoch, ownerIncarnation, remoteIncarnation,
                    leaseGeneration, ownerSequence, revoked, partitioned,
                    submitCount, possibleRemoteEffect, fenced, reclaimed >>

Reclaim ==
    /\ fenced
    /\ effectClosed
    /\ ~reclaimed
    /\ reclaimed' = TRUE
    /\ phase' = "Reclaimed"
    /\ UNCHANGED << ownerEpoch, ownerIncarnation, remoteIncarnation,
                    leaseGeneration, ownerSequence, revoked, partitioned,
                    submitCount, possibleRemoteEffect, fenced, effectClosed >>

Next == RemoteSubmit \/ Partition \/ Renew \/ Revoke \/ OwnerReboot \/
        RemoteReboot \/ Fence \/ ObserveEffectClosure \/ Reclaim

Spec == Init /\ [][Next]_vars

TypeOK ==
    /\ phase \in {"Issued", "PossibleEffect", "Revoked", "Stale", "Fenced", "Closed", "Reclaimed"}
    /\ ownerEpoch \in Nat /\ ownerIncarnation \in Nat /\ remoteIncarnation \in Nat
    /\ leaseGeneration \in Nat /\ ownerSequence \in Nat /\ submitCount \in 0..1
    /\ revoked \in BOOLEAN /\ partitioned \in BOOLEAN
    /\ possibleRemoteEffect \in BOOLEAN /\ fenced \in BOOLEAN
    /\ effectClosed \in BOOLEAN /\ reclaimed \in BOOLEAN

AtMostOneRemoteSubmit == submitCount <= 1
NoSubmitAcrossPartition == partitioned => submitCount <= 1
ReclaimRequiresFenceAndClosure == reclaimed => (fenced /\ effectClosed)
RebootNeverProvesClosure == (~ExactLease /\ possibleRemoteEffect) => ~reclaimed \/ effectClosed
NoExpiryBasedReclaim == (ownerSequence > NotAfterOwnerSequence /\ possibleRemoteEffect) => ~reclaimed \/ effectClosed

=============================================================================
