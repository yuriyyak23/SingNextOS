# P14-6 evidence — non-authoritative cache identity

## Repeat-audit remediation (2026-09-20)

The repeat audit re-captured baseline HEAD `52ccf45c05498a9143a599bb54499919a2cbcf8c` and preserved the complete pre-existing dirty worktree. It found that verification-cache `Put` validated the key but accepted default/empty, length-mismatched, duplicate-stage, or noncanonical stage/thunk/schema arrays. Those values are not authority, but they are not valid verified static facts and could make finite thunk metadata ambiguous. Storage now requires a nonempty one-to-one stage/thunk/schema mapping, unique canonical stage IDs, and canonical thunk/schema identities. `VerificationCacheRejectsIncompleteOrNonCanonicalStaticFacts` proves fail-closed admission and that rejected entries do not populate the cache.

A later ABA/default-identity sweep found that live-binding `Put` accepted zero runtime incarnation and zero process/service/session IDs or owner generations. Those default values cannot establish restart/ABA separation. Storage now requires every numeric owner identity/incarnation/generation represented by the key to be nonzero. `DefaultOwnerIdentitiesAndGenerationsCannotEnterLiveBindingCache` covers every such field and proves rejected values do not populate the cache. The exact-key mutation regression now also varies process/service/session IDs, seal/contract/thunk IDs, and provider identity, in addition to every generation and digest.

Post-remediation P14-6/P14-0/P14-8 tests passed 13/13. Runtime and full solution builds succeeded with 0 warnings and 0 errors. Full non-GUI results were 1341 passed, the same 8 unrelated failures, and 2 skipped in `SingPlus.Tests`; the other assemblies passed 60/60, 90/90, and 58/58. The cache remains metadata-only, every live binding still carries `RequiresLiveRevalidation=true`, and both production gates remain OFF.

Disposition: `RuntimeEnforcedMetadataOnly`; `FG-DYNAMIC-PLAN-BIND` and `FG-PLAN-CACHE` remain OFF. No production live binder is enabled.

Baseline HEAD `52ccf45c05498a9143a599bb54499919a2cbcf8c`; Windows x64 JIT; SDK `11.0.100-rc.1.26425.128`; runtime `11.0.0-rc.1.26425.128`. HEAD/status were captured before the phase; dirty state was preserved.

Implemented separate verification and live-binding metadata caches. Verification keys bind plan, admission-policy, and generator/toolchain digests. Live keys bind runtime realm/incarnation; process/service/session identities and generations; capability lineage/generation; seal identity/generation; contract and request/response schema digests; thunk identity/digest; policy/toolchain; plan digest; and provider identity/generation. Every semantic mutation tested creates a miss, including realm recreation, process/service/session generation, capability lineage, seal recreation, contract/schema/thunk/policy/toolchain/plan mutation, replay, and provider restart.

Live entries contain only finite thunk catalog index, owner lookup route ID, and the invariant `RequiresLiveRevalidation=true`. Storage rejects entries that attempt to disable revalidation. Neither cache stores authorization success, live validity, implementation references, delegates, reflection types, service locators, provider handles, or ManagedCap-visible state. Clear/remove performs metadata eviction only and invokes no callback.

Executed evidence:

- focused cache, trusted-binding, and plan-verifier regressions: 44 passed, 0 failed;
- Runtime and full solution builds: 0 warnings, 0 errors;
- full non-GUI suite: main assembly 1312 passed, 8 unchanged unrelated failures, 2 skipped (1322 total); other assemblies 90/90, 58/58, and 60/60 passed. The failures remain seven absent historical JSON artifacts and one pre-existing security-profile inventory mismatch; no P14 test failed;
- architecture/default-gate policy: 7 passed, 0 failed;
- changed JSON parsed; `git diff --check` exited 0 with only LF-to-CRLF notices.

Exact claim is runtime-enforced non-authoritative cache metadata and ABA-complete key separation. FutureGated owner/prerequisite: TCB live-binding/runtime owners. Missing evidence: production binding from these keys to finite generated thunks plus owner-by-owner live revalidation on every hit; warm-cache revoke/restart/session-close/seal-close execution tests; stale rebind/fallback; and performance counters. A cache hit is never permission.

Ordinary SIP remains fallback/oracle. No dynamic IL/runtime compilation/reflection dispatch/caller delegate/service locator was added. No HybridCPU core/ISE/ISA/compiler/scheduler-legality/microarchitecture code changed; QEMU, firmware, CXL boot, hardware, NativeAOT, acceleration, and production readiness remain unclaimed.
