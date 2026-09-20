# P12 — Resource-aware SipJob composition

## Goal

Compose vNext resource authority with the completed SipJob architecture while preserving every existing SipJob normative invariant.

## Fundamental rule

SipJob remains an optimization over existing SIP transitions:

```text
SipJobPlan != authority
SipJob execution class != resource authority
SipJob cache != live budget
```

## Plan metadata

A stage may carry non-authoritative resource requirement descriptors:

```text
required semantic resource class
maximum envelope requested
whether donation may flow from upstream invocation
barrier/fallback policy
```

At runtime generated sentries resolve and revalidate real resource owners.

## Fusion legality

Fusion may remove queue/waiter/materialization work only if resource-authority transition trace remains equivalent to ordinary SIP.

Do not silently fuse across:

- resource lease ownership transfer that is externally observable;
- independent consumptive commits that cannot be safely reserved;
- provider submission barriers;
- resource settlement/publication boundaries required by ordinary path.

Insert materialization barrier instead.

## DAG/parallel branches

For parallel branches, split resource authority before branch execution. Sibling branches cannot race on a shared indivisible lease.

```text
parent lease
 -> atomic split A/B
 -> branch A consumes A
 -> branch B consumes B
```

Join does not magically recombine spent authority.

## Async

No stack-only span/resource view across suspension. Persist only opaque heap-safe lease handles/correlations; revalidate on resume.

## Tests

Differential trace ordinary SIP vs SipJob for:

- authority acquisition;
- Region transitions;
- resource reserve/bind/settle;
- cancellation;
- provider loss;
- branch failure;
- publication.

## Exit criteria

`FG-VNX-SIPJOB-RESOURCE` may enable only exact qualified linear/async/DAG contours. Existing SipJob provider gates remain independent.
