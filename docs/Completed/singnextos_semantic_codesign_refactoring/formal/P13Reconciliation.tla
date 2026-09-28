------------------------ MODULE P13Reconciliation ------------------------
EXTENDS Naturals, Sequences

CONSTANT MaxAttempts

VARIABLES state, attempts

States == {"Prepared", "PossibleSubmit", "Quarantined", "SettledExact",
           "SettledContained", "SettledConservative", "CancelledPreSubmit"}

Init == state = "Prepared" /\ attempts = 0

MarkPossibleSubmit ==
    /\ state = "Prepared"
    /\ state' = "PossibleSubmit"
    /\ UNCHANGED attempts

CancelPreSubmit ==
    /\ state = "Prepared"
    /\ state' = "CancelledPreSubmit"
    /\ UNCHANGED attempts

LoseProvider ==
    /\ state = "PossibleSubmit"
    /\ state' = "Quarantined"
    /\ UNCHANGED attempts

RetryEvidence ==
    /\ state \in {"PossibleSubmit", "Quarantined"}
    /\ attempts < MaxAttempts
    /\ attempts' = attempts + 1
    /\ UNCHANGED state

Settle(kind) ==
    /\ state \in {"PossibleSubmit", "Quarantined"}
    /\ kind \in {"SettledExact", "SettledContained", "SettledConservative"}
    /\ state' = kind
    /\ UNCHANGED attempts

Next == MarkPossibleSubmit \/ CancelPreSubmit \/ LoseProvider \/ RetryEvidence \/
        (\E kind \in {"SettledExact", "SettledContained", "SettledConservative"}: Settle(kind))

TypeInvariant == state \in States /\ attempts \in 0..MaxAttempts

NoRefundAfterPossibleSubmit ==
    state = "CancelledPreSubmit" => attempts = 0

\* Conditional liveness is intentionally not unconditional: only an environment
\* that eventually supplies exact closure/settlement evidence can leave quarantine.
=============================================================================
