# Phase 7 — Safe High-performance IPC v2

Status: implemented and qualified on 2026-09-18. See
`07_PHASE_07_IMPLEMENTATION_EVIDENCE.md` for the authority mapping, executed
negative tests, diagnostic measurements, and explicit FutureGated scope.

## Goal

Reduce service decomposition overhead while making ownership transfer semantics more explicit and reusable across the OS.

## Semantic operations

Define transport-independent operations equivalent to:

```text
SendCopy<T>
SendMove<TPayload>
SendBorrowRead<TPayload>
SendScatterGather
RequestReply
BoundedBatch / Stream
```

Final API naming must match existing project conventions. The semantic distinction between copy, move and borrow is mandatory.

## MOVE semantics

A MOVE operation must have an exact terminal ownership outcome.

Pre-admission failure: sender retains ownership.

Successful transfer: sender loses ownership and receiver gains the new valid ownership identity according to existing region generation rules.

Ambiguous transport/provider failure must not create two logical owners. The implementation must track transfer state until it can prove sender retention, receiver ownership, or quarantine/containment.

## Borrow-read semantics

- read-only;
- explicit bounded lifetime;
- exact borrower identity;
- owner mutation/transfer rules respect active borrow;
- return/revoke invalidates borrower access before owner resumes conflicting mutation;
- deadline expiration alone does not fabricate borrow return.

## Scatter-gather

Represent exact segment ranges and access intent. Validate:

- range bounds;
- overlap policy;
- ownership/borrow mode per segment;
- aggregate budget;
- terminal completion for every segment.

## Request/reply

Correlation identity must be separate from authority. Reply completion does not automatically imply return of a moved payload unless the message contract explicitly transfers it back.

Integrate Phase 4 deadlines/cancellation and Phase 5 queue/in-flight budgets.

## Fast-path strategy

Implementation may select:

- inline copy for small payloads;
- page donation/remap for MOVE;
- temporary read mapping for borrow;
- scatter-gather descriptor path;
- batching;
- safe same-domain optimization.

The chosen path is not an application semantic guarantee.

## Zero-copy rule

Public API must not promise zero-copy. Runtime may expose diagnostic evidence that a fast path was used, but programs must remain correct if the runtime copies.

## Capability transfer

If IPC supports capability delegation, delegation is an explicit typed operation with its own authority checks. Arbitrary payload bytes cannot manufacture capabilities.

## Failure and teardown

Channel close/service crash must:

- resolve or quarantine in-flight MOVE state;
- revoke/close borrows according to exact lifetime rules;
- release queue budgets only after transfer state is known;
- integrate with supervisor teardown ordering.

## Performance validation

Establish baselines for:

- small copy latency;
- MOVE of page-sized/large payload;
- borrow-read setup/teardown;
- scatter-gather batch;
- request/reply throughput.

IPC v2 small-message path must not regress materially versus current IPC without documented reason.

## Required tests

- pre-admission MOVE failure retains sender ownership;
- successful MOVE leaves exactly one owner;
- timeout race does not duplicate or lose ownership;
- borrower cannot write;
- owner cannot conflictingly mutate while borrow active;
- scatter-gather rejects invalid/overflow ranges;
- queue budget enforced;
- capability delegation remains typed and independently authorized;
- service crash resolves/quarantines in-flight transfers correctly;
- implementation correctness identical across copy and optimized fast paths.

## Exit criteria

IPC v2 is ownership-correct under failures/timeouts/restart, has bounded high-performance paths, and zero-copy remains an optimization rather than ABI.
