# Phase 07 Implementation Evidence

Date: 2026-09-18 (Europe/Moscow)

## Baseline and audit

- Repository `C:\Users\Yuriy Kurnosov\Desktop\SingNextOS`; baseline HEAD `d32c72b06f3ad8e755ca656d1e46c32cdc059759`; solution `SingNextOS.slnx`.
- Dirty prerequisite/Phase 00–06 work was preserved. No reset, checkout, force, commit, push or remote Git operation was performed.
- `RegionAuthority` remains the single owner of region generation, MOVE, loan, revoke/return and conflicting-use truth. `ChannelRegistry` remains protocol/queue/transfer admission truth; `ResponseRegistry` and endpoint-session invocation state remain request/reply truth.
- Phase 04 cancellation scopes and Phase 05 IPC message/byte reservations are reused. No second mutable IPC ownership registry was introduced.

## Implemented scope and guarantees

- Versioned transport-neutral v2 semantics for `Copy`, `Move`, `BorrowRead`, bounded scatter/gather copy and request/reply correlation.
- `SendCopyV2` creates value-independent bounded bytes. `SmallCopy`/`BoundedCopy` are diagnostic path evidence only, not ABI behavior.
- `SendMoveV2` requires a protocol-declared consume operation. All capacity, transition, capability and payload checks occur before the existing exact region transfer; pre-admission failure preserves sender ownership and successful admission creates the next receiver-owned region generation.
- `SendBorrowReadV2` requires protocol-declared borrow, creates an exact generation-bound read-only lease, blocks conflicting owner access, and relies on exact return/revoke/teardown invalidation. Cancellation never fabricates lease return.
- Optional generation-bound cancellation scope is bound before v2 admission. Pre-effect cancellation returns `DeadlineExpired` with sender authority unchanged; admitted transfer records completion/too-late semantics and is never reversed by timeout.
- Scatter/gather validates bounded segment count, owner generation, range arithmetic, overlap policy, aggregate bytes and segment mode. Current executable implementation safely supports `CopyRead`; mixed MOVE/borrow scatter lists fail explicitly rather than emulating unsafe partial compensation.
- Capability requirements are still protocol-declared, typed and independently validated; payload bytes cannot mint authority.
- Receiver crash after an admitted MOVE reclaims the receiver-owned generation through existing teardown; it never restores or duplicates the invalid sender token.
- `IpcV2SendReceipt` is observation only (`AuthorizesTransfer=false`, `GuaranteesZeroCopy=false`).

## Public/SIP boundary

- Contracts expose semantic transfer intent, bounded data, outcome/path evidence and causal correlation only.
- No CXL topology, BDF/HDM/DPA/route/Fabric Manager identity, raw mapping, provider lease/token, secure backend internal or HybridCPU implementation detail is present.
- Copy, page donation, remap and shared mapping remain interchangeable future implementations. No public zero-copy promise exists.

## Changed files

- `contracts/SingPlus.Contracts/IpcV2Contracts.cs`.
- `src/Runtime/SingPlus.Runtime/Channels/ChannelRegistry.cs`.
- `src/Runtime/SingPlus.Runtime/Channels/RuntimeKernel.IpcV2.cs`.
- `tests/SingPlus.Tests/Runtime/Phase07IpcV2Tests.cs`.
- Phase document, roadmap README and this evidence file.

## Qualification and measurement

1. Focused Phase 07 matrix:

   `dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~Phase07IpcV2Tests" --logger "console;verbosity=normal" --nologo`

   Passed: 7, failed: 0, skipped: 0. Covers copy equivalence/non-zero-copy claim, failed and successful MOVE, exact borrow, cancellation, scatter/gather failures and success, capability independence, receiver crash and executable measurements.

2. Focused diagnostic measurement:

   `dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~BoundedSmallCopyAndOwnershipFastPathsHaveExecutableMeasurements" --logger "console;verbosity=detailed" --nologo`

   On this developer environment (.NET runtime 11.0.0), one 100-iteration diagnostic sample reported 139293 Stopwatch ticks for 4-byte small-copy admission and 155167 ticks for allocate+MOVE of 4096 bytes. These are non-production, non-statistical diagnostic samples, not a hardware or performance guarantee.

3. Related regression matrix:

   `dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~Phase07IpcV2Tests|FullyQualifiedName~ProtocolRuntimeTests|FullyQualifiedName~OwnershipTests|FullyQualifiedName~Phase04DeadlineCancellationTests|FullyQualifiedName~Phase05ResourceBudgetTests|FullyQualifiedName~EndpointSessionCancellationTests|FullyQualifiedName~ResponseProtocolTests" --nologo`

   Passed: 71, failed: 0, skipped: 0.

4. `dotnet build SingNextOS.slnx --no-restore --nologo`: succeeded, 0 warnings, 0 errors before final naming hardening; repeated below for final closure.

5. `dotnet test SingNextOS.slnx --no-build --no-restore --nologo`: adapter 12/12, neutral runtime 58/58, platform 60/60, SingPlus 1060 passed / 0 failed / 2 opt-in skips before final naming hardening; repeated below for final closure.

6. `git diff --check` passed before documentation and is repeated after this evidence.

## Remaining gaps / FutureGated

- Mixed scatter/gather MOVE/borrow is intentionally unsupported until an all-segment compensation/quarantine protocol is designed. It fails typed and pre-effect; single-payload MOVE/borrow is fully executable.
- Existing endpoint-session request/reply is the authoritative implementation and already carries Phase 04 wait cancellation. A duplicate v2 request/reply registry was deliberately not added.
- The current runtime is an in-process deterministic transport, so it has no ambiguous external transport acceptance boundary. Any future external transport must project `Quarantined` and retain exact ownership containment rather than infer rollback.
- Production-grade benchmarking, baseline comparison, throughput distributions and storm/oversized-list abuse measurements belong to Phase 11. The sample above only proves the measurement path runs.
- No production hardware, physical CXL, QEMU, FPGA or silicon qualification was performed or claimed.

## HybridCPU boundary confirmation

HybridCPU core, ISA, compiler semantics, scheduler, runtime legality, microarchitecture and CPU architecture were unchanged. No SingNextOS-to-HybridCPU implementation dependency was introduced.
