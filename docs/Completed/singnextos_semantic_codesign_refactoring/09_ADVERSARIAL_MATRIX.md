# Adversarial matrix

| Scenario | Current prevention/evidence | Test status/action | Protocol action | Provider limitation |
|---|---|---|---|---|
| revoke during admission | existing generation checks + new composed race test | requires new integration race test | no new protocol beyond final sentry | — |
| revoke after submit | quarantine/authority revoke prevents new OS work; cannot undo provider effect | requires fault test | existing lifecycle + reconciliation | provider may be unable to cancel |
| session close during bind | session/invocation owner + final revalidation | requires composed race test | existing owner protocol | — |
| process/runtime/provider restart | fresh generations/admission; stale binding invalid | requires restart integration | P13 reconciliation | provider closure may be unknown |
| provider generation drift | ExternalGenerationSet/current dependencies fail closed | existing Hybrid adapter tests + new SingNext binding test | binding revalidation | — |
| duplicate submit | Hybrid adapter rejects second submit; SingNext must have one winner | existing Hybrid test + new end-to-end concurrency | P06 single winner | — |
| duplicate completion / reordered evidence | ExternalOperation stage validation plus exact binding | new cross-owner tests | no new owner | — |
| cross-operation receipt replay | exact request/correlation checks | existing Hybrid negatives + new usage/publication binding tests | P05/P09 binding | — |
| resource double spend/refund | single budget ledger + terminal settlement | existing budget tests + property tests | existing ledger | — |
| nested donation / priority laundering | ResourceDonationProtocol narrowing/priority ceiling | existing donation tests + guarantee anti-laundering | existing donation protocol | — |
| guarantee laundering | not currently end-to-end | new refinement tests | P02/P05 refinement | — |
| Region reuse/ABA or mutation during execution | Region generation/MutationEpoch | new binding race tests | P04/P08 revalidation | — |
| Complete but not Visible | existing separate lifecycle states | lifecycle/integration test | existing state machine | provider must report visibility |
| Visible without publication decision | staged contour must not advance Published | new integration test | P07 exact publication action | blocked if provider cannot withhold |
| publication failure after consumption | budget remains settlement/quarantine problem, not rollback | new fault test | P07/P13 reconciliation | provider-specific |
| cancel before/after possible submit; cancel/retire race | exact cancellation ack exists; generic request is insufficient | existing Hybrid cancellation test + new ISE/SingNext races | P11 mapping/closure | stronger class provider-dependent |
| closure racing retire / late effect after close | no generic proof at baseline | new ISE/provider tests + TLA | P11 contour closure hook | unsupported if runtime cannot prove |
| replay after revocation | fresh authorization required | new end-to-end test | P12 policy | — |
| stale scheduler topology | scheduler non-authoritative | new test | final sentry/refinement | — |
| provider changes execution class after planning | binding/generation exactness | new test | P05/P12 | — |
| integer overflow/time wrap | checked arithmetic/canonical validation required | property tests | no new owner | — |
| partial multi-resource acquisition/deadlock | OS budget vector already atomic; provider-local all-or-none required | new fault tests | P10 corrected strategy | provider-local limitation possible |
