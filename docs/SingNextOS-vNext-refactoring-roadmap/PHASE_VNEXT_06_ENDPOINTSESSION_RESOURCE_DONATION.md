# P06 — EndpointSession resource donation and request-scoped delegation

## Goal

Adopt the useful seL4-MCS precedent: resource authority can follow an RPC/SIP request, preventing expensive-server-work amplification.

## Threat addressed

Without donation:

```text
cheap client request -> server spends server's scarce accelerator budget
```

An untrusted caller can amplify resource use through a privileged service.

## Model

`EndpointSession` remains the invocation authority context. Add a request-scoped non-ambient donation reference:

```text
InvocationResourceDonation
  source capability/lease lineage
  exact invocation/session correlation
  narrowed resource class/envelope
  target service/process generation
  validity until terminal invocation state
```

Donation is not copied into service-global state.

## Semantics

```text
client resource authority
 -> narrow/delegate
 -> bind to exact invocation
 -> service may consume only through sentry/runtime projection
 -> unused part returns/release per exact policy
```

The server cannot upgrade priority, assurance, amount, provider set or validity.

## Important non-equivalences

```text
donation != effect capability
donation != QoS hint
donation != provider admission
donation != Region ownership transfer
```

## Cancellation

Cancel-before-execution may release an unconsumed lease. After provider submission, P07 rules govern; cancellation cannot fabricate resource refund.

## Tests

- client has budget but lacks effect cap -> denied;
- effect cap but no required budget -> denied/fallback by contract;
- donated 500us cannot become 501us;
- service restart invalidates old donation;
- session ABA/reused numeric identity does not revive donation;
- nested service calls preserve original provenance and narrowing.

## Exit criteria

`FG-VNX-SESSION-DONATION` can be enabled for synchronous ordinary SIP host-model contour.
