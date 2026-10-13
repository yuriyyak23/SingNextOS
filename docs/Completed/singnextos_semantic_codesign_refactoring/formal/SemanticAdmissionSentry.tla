------------------------- MODULE SemanticAdmissionSentry -------------------------
EXTENDS Naturals

(***************************************************************************
This is a specification view, never a runtime ledger or authority.  GlobalState
is the product of independently authoritative owner states plus immutable claims.
The model deliberately separates the one local submit winner from PossibleSubmit:
a durable/local failure before PossibleSubmit is compensable, but ambiguity after
PossibleSubmit is quarantined and must be reconciled.

Fault assumptions for this safety-only skeleton:
  * any relevant owner generation may drift before or after revalidation;
  * provider admission/refinement/runtime-legality may deny before the winner;
  * a provider call may be lost after PossibleSubmit;
  * no timeout proves that an external effect did not occur;
  * no liveness or eventual provider recovery is claimed.
***************************************************************************)

VARIABLES phase,
          capabilityGeneration, sessionGeneration, regionGeneration,
          leaseGeneration, providerGeneration,
          providerAdmitted, refinementSatisfied, runtimeLegal,
          submitWinnerCount, possibleSubmit, compensated, quarantined,
          winnerSingNextAuthorized, winnerProviderAdmitted,
          winnerRefinementSatisfied, winnerRuntimeLegal

vars == << phase,
           capabilityGeneration, sessionGeneration, regionGeneration,
           leaseGeneration, providerGeneration,
           providerAdmitted, refinementSatisfied, runtimeLegal,
           submitWinnerCount, possibleSubmit, compensated, quarantined,
           winnerSingNextAuthorized, winnerProviderAdmitted,
           winnerRefinementSatisfied, winnerRuntimeLegal >>

ExpectedGeneration == 1

ExactOwnerGenerations ==
    /\ capabilityGeneration = ExpectedGeneration
    /\ sessionGeneration = ExpectedGeneration
    /\ regionGeneration = ExpectedGeneration
    /\ leaseGeneration = ExpectedGeneration
    /\ providerGeneration = ExpectedGeneration

SingNextAuthorized == ExactOwnerGenerations

OwnerStateProduct ==
    [ capability |-> [generation |-> capabilityGeneration],
      session    |-> [generation |-> sessionGeneration],
      region     |-> [generation |-> regionGeneration],
      budget     |-> [generation |-> leaseGeneration],
      provider   |-> [generation |-> providerGeneration],
      operation  |-> [phase |-> phase] ]

GlobalState ==
    [ owners |-> OwnerStateProduct,
      claims |-> [providerAdmission |-> providerAdmitted,
                   refinement |-> refinementSatisfied,
                   runtimeLegality |-> runtimeLegal],
      submitWinnerCount |-> submitWinnerCount,
      possibleSubmit |-> possibleSubmit ]

Init ==
    /\ phase = "Prepared"
    /\ capabilityGeneration = ExpectedGeneration
    /\ sessionGeneration = ExpectedGeneration
    /\ regionGeneration = ExpectedGeneration
    /\ leaseGeneration = ExpectedGeneration
    /\ providerGeneration = ExpectedGeneration
    /\ providerAdmitted = FALSE
    /\ refinementSatisfied = FALSE
    /\ runtimeLegal = FALSE
    /\ submitWinnerCount = 0
    /\ possibleSubmit = FALSE
    /\ compensated = FALSE
    /\ quarantined = FALSE
    /\ winnerSingNextAuthorized = FALSE
    /\ winnerProviderAdmitted = FALSE
    /\ winnerRefinementSatisfied = FALSE
    /\ winnerRuntimeLegal = FALSE

ReserveLease ==
    /\ phase = "Prepared"
    /\ phase' = "Reserved"
    /\ UNCHANGED << capabilityGeneration, sessionGeneration, regionGeneration,
                    leaseGeneration, providerGeneration, providerAdmitted,
                    refinementSatisfied, runtimeLegal, submitWinnerCount,
                    possibleSubmit, compensated, quarantined,
                    winnerSingNextAuthorized, winnerProviderAdmitted,
                    winnerRefinementSatisfied, winnerRuntimeLegal >>

ProviderAdmission ==
    /\ phase \in {"Prepared", "Reserved"}
    /\ providerAdmitted' = TRUE
    /\ UNCHANGED << phase, capabilityGeneration, sessionGeneration,
                    regionGeneration, leaseGeneration, providerGeneration,
                    refinementSatisfied, runtimeLegal, submitWinnerCount,
                    possibleSubmit, compensated, quarantined,
                    winnerSingNextAuthorized, winnerProviderAdmitted,
                    winnerRefinementSatisfied, winnerRuntimeLegal >>

AcceptRefinement ==
    /\ phase \in {"Prepared", "Reserved"}
    /\ refinementSatisfied' = TRUE
    /\ UNCHANGED << phase, capabilityGeneration, sessionGeneration,
                    regionGeneration, leaseGeneration, providerGeneration,
                    providerAdmitted, runtimeLegal, submitWinnerCount,
                    possibleSubmit, compensated, quarantined,
                    winnerSingNextAuthorized, winnerProviderAdmitted,
                    winnerRefinementSatisfied, winnerRuntimeLegal >>

AcceptRuntimeLegality ==
    /\ phase \in {"Prepared", "Reserved"}
    /\ runtimeLegal' = TRUE
    /\ UNCHANGED << phase, capabilityGeneration, sessionGeneration,
                    regionGeneration, leaseGeneration, providerGeneration,
                    providerAdmitted, refinementSatisfied, submitWinnerCount,
                    possibleSubmit, compensated, quarantined,
                    winnerSingNextAuthorized, winnerProviderAdmitted,
                    winnerRefinementSatisfied, winnerRuntimeLegal >>

DriftCapability ==
    /\ capabilityGeneration = ExpectedGeneration
    /\ capabilityGeneration' = 2
    /\ UNCHANGED << phase, sessionGeneration, regionGeneration, leaseGeneration,
                    providerGeneration, providerAdmitted, refinementSatisfied,
                    runtimeLegal, submitWinnerCount, possibleSubmit, compensated,
                    quarantined, winnerSingNextAuthorized, winnerProviderAdmitted,
                    winnerRefinementSatisfied, winnerRuntimeLegal >>

DriftSession ==
    /\ sessionGeneration = ExpectedGeneration
    /\ sessionGeneration' = 2
    /\ UNCHANGED << phase, capabilityGeneration, regionGeneration, leaseGeneration,
                    providerGeneration, providerAdmitted, refinementSatisfied,
                    runtimeLegal, submitWinnerCount, possibleSubmit, compensated,
                    quarantined, winnerSingNextAuthorized, winnerProviderAdmitted,
                    winnerRefinementSatisfied, winnerRuntimeLegal >>

DriftRegion ==
    /\ regionGeneration = ExpectedGeneration
    /\ regionGeneration' = 2
    /\ UNCHANGED << phase, capabilityGeneration, sessionGeneration, leaseGeneration,
                    providerGeneration, providerAdmitted, refinementSatisfied,
                    runtimeLegal, submitWinnerCount, possibleSubmit, compensated,
                    quarantined, winnerSingNextAuthorized, winnerProviderAdmitted,
                    winnerRefinementSatisfied, winnerRuntimeLegal >>

DriftLease ==
    /\ leaseGeneration = ExpectedGeneration
    /\ leaseGeneration' = 2
    /\ UNCHANGED << phase, capabilityGeneration, sessionGeneration,
                    regionGeneration, providerGeneration, providerAdmitted,
                    refinementSatisfied, runtimeLegal, submitWinnerCount,
                    possibleSubmit, compensated, quarantined,
                    winnerSingNextAuthorized, winnerProviderAdmitted,
                    winnerRefinementSatisfied, winnerRuntimeLegal >>

DriftProvider ==
    /\ providerGeneration = ExpectedGeneration
    /\ providerGeneration' = 2
    /\ UNCHANGED << phase, capabilityGeneration, sessionGeneration,
                    regionGeneration, leaseGeneration, providerAdmitted,
                    refinementSatisfied, runtimeLegal, submitWinnerCount,
                    possibleSubmit, compensated, quarantined,
                    winnerSingNextAuthorized, winnerProviderAdmitted,
                    winnerRefinementSatisfied, winnerRuntimeLegal >>

FinalRevalidate ==
    /\ phase = "Reserved"
    /\ SingNextAuthorized
    /\ providerAdmitted
    /\ refinementSatisfied
    /\ runtimeLegal
    /\ phase' = "Revalidated"
    /\ UNCHANGED << capabilityGeneration, sessionGeneration, regionGeneration,
                    leaseGeneration, providerGeneration, providerAdmitted,
                    refinementSatisfied, runtimeLegal, submitWinnerCount,
                    possibleSubmit, compensated, quarantined,
                    winnerSingNextAuthorized, winnerProviderAdmitted,
                    winnerRefinementSatisfied, winnerRuntimeLegal >>

StartSubmitWinner ==
    /\ phase = "Revalidated"
    /\ submitWinnerCount = 0
    /\ SingNextAuthorized
    /\ providerAdmitted
    /\ refinementSatisfied
    /\ runtimeLegal
    /\ phase' = "SubmitStarted"
    /\ submitWinnerCount' = 1
    /\ winnerSingNextAuthorized' = TRUE
    /\ winnerProviderAdmitted' = providerAdmitted
    /\ winnerRefinementSatisfied' = refinementSatisfied
    /\ winnerRuntimeLegal' = runtimeLegal
    /\ UNCHANGED << capabilityGeneration, sessionGeneration, regionGeneration,
                    leaseGeneration, providerGeneration, providerAdmitted,
                    refinementSatisfied, runtimeLegal, possibleSubmit,
                    compensated, quarantined >>

RecordPossibleSubmit ==
    /\ phase = "SubmitStarted"
    /\ submitWinnerCount = 1
    /\ phase' = "PossibleSubmit"
    /\ possibleSubmit' = TRUE
    /\ UNCHANGED << capabilityGeneration, sessionGeneration, regionGeneration,
                    leaseGeneration, providerGeneration, providerAdmitted,
                    refinementSatisfied, runtimeLegal, submitWinnerCount,
                    compensated, quarantined, winnerSingNextAuthorized,
                    winnerProviderAdmitted, winnerRefinementSatisfied,
                    winnerRuntimeLegal >>

PreSubmitFailure ==
    /\ phase \in {"Prepared", "Reserved", "Revalidated", "SubmitStarted"}
    /\ ~possibleSubmit
    /\ phase' = "CancelledPreSubmit"
    /\ compensated' = TRUE
    /\ UNCHANGED << capabilityGeneration, sessionGeneration, regionGeneration,
                    leaseGeneration, providerGeneration, providerAdmitted,
                    refinementSatisfied, runtimeLegal, submitWinnerCount,
                    possibleSubmit, quarantined, winnerSingNextAuthorized,
                    winnerProviderAdmitted, winnerRefinementSatisfied,
                    winnerRuntimeLegal >>

ProviderAccepted ==
    /\ phase = "PossibleSubmit"
    /\ phase' = "Submitted"
    /\ UNCHANGED << capabilityGeneration, sessionGeneration, regionGeneration,
                    leaseGeneration, providerGeneration, providerAdmitted,
                    refinementSatisfied, runtimeLegal, submitWinnerCount,
                    possibleSubmit, compensated, quarantined,
                    winnerSingNextAuthorized, winnerProviderAdmitted,
                    winnerRefinementSatisfied, winnerRuntimeLegal >>

ProviderAmbiguousFailure ==
    /\ phase = "PossibleSubmit"
    /\ phase' = "Quarantined"
    /\ quarantined' = TRUE
    /\ UNCHANGED << capabilityGeneration, sessionGeneration, regionGeneration,
                    leaseGeneration, providerGeneration, providerAdmitted,
                    refinementSatisfied, runtimeLegal, submitWinnerCount,
                    possibleSubmit, compensated, winnerSingNextAuthorized,
                    winnerProviderAdmitted, winnerRefinementSatisfied,
                    winnerRuntimeLegal >>

Next == ReserveLease \/ ProviderAdmission \/ AcceptRefinement \/
        AcceptRuntimeLegality \/ DriftCapability \/ DriftSession \/
        DriftRegion \/ DriftLease \/ DriftProvider \/ FinalRevalidate \/
        StartSubmitWinner \/ RecordPossibleSubmit \/ PreSubmitFailure \/
        ProviderAccepted \/ ProviderAmbiguousFailure

TypeOK ==
    /\ phase \in {"Prepared", "Reserved", "Revalidated", "SubmitStarted",
                    "PossibleSubmit", "Submitted", "CancelledPreSubmit",
                    "Quarantined"}
    /\ capabilityGeneration \in 1..2
    /\ sessionGeneration \in 1..2
    /\ regionGeneration \in 1..2
    /\ leaseGeneration \in 1..2
    /\ providerGeneration \in 1..2
    /\ providerAdmitted \in BOOLEAN
    /\ refinementSatisfied \in BOOLEAN
    /\ runtimeLegal \in BOOLEAN
    /\ submitWinnerCount \in 0..1
    /\ possibleSubmit \in BOOLEAN
    /\ compensated \in BOOLEAN
    /\ quarantined \in BOOLEAN
    /\ winnerSingNextAuthorized \in BOOLEAN
    /\ winnerProviderAdmitted \in BOOLEAN
    /\ winnerRefinementSatisfied \in BOOLEAN
    /\ winnerRuntimeLegal \in BOOLEAN

AtMostOneSubmitWinner == submitWinnerCount <= 1

IrreversibleRequiresAllGates ==
    possibleSubmit =>
        /\ submitWinnerCount = 1
        /\ winnerSingNextAuthorized
        /\ winnerProviderAdmitted
        /\ winnerRefinementSatisfied
        /\ winnerRuntimeLegal

NoCompensationAfterPossibleSubmit == possibleSubmit => ~compensated

AmbiguousPossibleSubmitIsPinned == quarantined => possibleSubmit

Spec == Init /\ [][Next]_vars

=============================================================================
