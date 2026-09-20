# P14-2 evidence — Generated SIP Operation Sentry Foundation

## Disposition

The external-audit recommendation was accepted with one qualification: the generated sentry foundation and an exact two-stage closed-value contour are executable only as JIT qualification tests under an explicit test-local gate set. Production Job direct execution remains `FutureGatedRequiresCore`; the production claim is still `StaticAdmission` for generated operation identity/linkage, while the exact test contour is `QualifiedManagedTestOnly`. `FG-DIRECT-SENTRY` remains OFF in production.

Baseline HEAD is `52ccf45c05498a9143a599bb54499919a2cbcf8c`. All pre-existing P14-0/P14-1 dirty files were preserved. Runtime mode is Windows x64 JIT on SDK/runtime `11.0.100-rc.1.26425.128` / `11.0.0-rc.1.26425.128`. Latest generated artifact SHA-256 is `CDE44E219D9EE3DEA144E10117FD8B8CA277A8C5A79924E7435528D2467B37C5`.

## Audit verification and remediation

The audit was correct that `_implementation.Method(...)` is permitted only inside an operation-specific generated sentry, and that the normal dispatcher must converge on that sentry before a Job adapter exists. It was also correct to reject reflection, generic object invocation, delegates and a Job-only security pipeline.

`SingPlusGenerator` now emits a fifth deterministic artifact, `*.Sentries.g.cs`. For every operation it contains a concrete typed static `Invoke_<Operation>` entrypoint; stable `Thunk_<Operation>` identity bound to contract, message ID and operation name; SHA-256 `Thunk_<Operation>_Digest` bound to contract digest, thunk identity, request schema and response schema; and the only generated implementation call.

The existing generated dispatcher retains its TCB-private typed implementation field but now calls `GeneratedOperationSentries.Invoke_<Operation>`. It no longer contains `_implementation.@Method(...)`. No reflection, dynamic IL, arbitrary delegate, `object` service parameter or `IServiceProvider` was introduced.

The representative production `IFileService.OpenAsync` ordinary-SIP operation is now migrated to the generated sentry. `NativeServiceDispatch` creates a TCB-only exact invocation context after request acceptance and exact session resolution. Its envelope projection calls `IFileServiceGeneratedOperationSentries.InvokeRuntime_OpenAsync`, whose operation-specific typed target performs live `AdmitSessionCapabilityEffect` and then enters the File implementation core. Other native operations remain hand-written, so this is deliberately not represented as universal runtime sentry enforcement.

The generated runtime ABI contains no `object`, arbitrary delegate, reflection, service locator, or implementation value in ManagedCap-visible state. `TrustedSipInvocationContext` and `GeneratedSipSentryResult<T>` are public only so generated code in non-friend consumer assemblies can name their signatures; they expose no public constructor, property or instance field. Their state/factories are internal to the friend TCB assemblies. A default/forged context is not authority and is rejected by live owners.

## Ordinary-SIP completed-event baseline

The representative production `RuntimeFileServiceHost` now accepts an internal qualification-only `INativeSipCompletedEventSink`. `NativeServiceDispatch` and the File host emit only after the corresponding existing owner operation has succeeded: request receive, invocation acceptance, exact session resolution, capability-effect admission, implementation entry/exit, response publication/cancellation, and cancellation commit. The event has no capability, service implementation, handle, generation token, permission boolean, or mutable payload. The sink is internal, is not stored in any plan/frame/cache, and is not consulted by admission or execution.

Executable ordinary-SIP tests establish the baseline for later differential qualification. The success trace is ordered through response publication. A capability revoked after request enqueue produces receive/accept/session-resolution and owner cancellation only; it produces neither `CapabilityAdmitted` nor `ImplementationEntered`. Direct possession of the generated File sentry plus a stale caller generation fails before admission and implementation entry. Reflection checks prove the public-nameable ABI exposes no state or construction surface outside friend TCB assemblies.

Internal deterministic qualification hooks now exist after request receive, invocation acceptance and session resolution, immediately before/after capability commit, immediately before implementation entry, after implementation exit, and immediately before/after publication. They are not delegates stored in a plan/frame/cache and are unavailable through public construction. Hooks run only between completed owner calls, never while an authority-owner lock is held. They do not decide admission or alter production behavior when absent.

Race evidence proves: session close after exact resolution blocks capability admission and implementation; service fault at the same point has the same fail-closed result; revoke immediately before capability commit denies entry; revoke immediately after successful commit preserves the already-admitted ordinary outcome and releases the operation lease; close immediately before publication prevents publication and drains the service-created object. Cancellation before invocation commit never reaches the sentry target.

## P14-2B exact trusted binding foundation

`SipJobFileOpenBindingTable` is an operation-specific TCB-private lookup table for the representative generated File Open sentry. Its identity binds the capability-authority realm, caller and service process generations, service ID/generation, endpoint-session ID/generation, contract name/version/digest, and generated thunk ID/digest. The stored target is the exact generated typed target interface; no `object`, delegate, reflection or service locator is used.

The table intentionally has no invoke method. Validation returns only route metadata and never the target/implementation reference, authority result, capability or historical `authorized=true`. Every validation re-runs `ProcessRegistry`, `ServiceRegistry` and `EndpointSessionRegistry` resolution against the exact tuple. Consequently the table is lookup acceleration scaffolding, not authority and not yet a Job execution path.

Focused tests cover the exact route, mutation of every bound identity, unknown/empty handles, session close, service fault, explicit retirement/replay, and replacement ABA where the service identity is reused with fresh process/service/session generations. The old binding cannot validate while a freshly registered binding can. Route validation produces no implementation trace or service effect.

## Baseline invocation-owner remediation

The audit also identified a live baseline defect: `EndpointSessionInvocationRegistry.Publish` and `Cancel` invoked their transport settlement callbacks while holding the invocation authority-owner lock. The baseline owner now performs a two-step owner-controlled transition: reserve exactly one terminal settlement under its lock, execute response publication/cancellation outside that lock, then commit the successful terminal status under the lock. A failed or throwing action releases the reservation and does not manufacture a terminal result. Competing publish/cancel attempts fail closed as `ResponseNotPending` while settlement is reserved.

Session close now serializes invocation removal and channel close with the existing request/response correlation gate. This preserves the ordinary SIP close-versus-publication ordering while the invocation owner lock is no longer held across transport publication. A deterministic hook after reservation proves that cancellation-owner inspection can complete while publication is paused and that a concurrent second publication cannot win. This is baseline SIP hardening only: it does not create an inline Job completion primitive, a Job executor, publication authority, or a new transaction model.

## Owner-owned inline invocation foundation

`ChannelRegistry.BeginInlineCopiedInvocation` now supplies a narrow owner-side transport-elision primitive. It repeats exact endpoint/process ownership, message identity, copied-value schema and authoritative protocol-transition validation, increments the owner sequence, and deliberately creates no queue entry or envelope. It rejects ownership/BORROW/MOVE payloads, message-attached capability requirements, malformed values, stale owners/sessions and illegal protocol transitions. It does not invoke service code or carry a result.

`RuntimeKernel.BeginInlineSessionInvocation` composes this with the existing `EndpointSessionRegistry` and `EndpointSessionInvocationRegistry`: it resolves exact live parties, acquires and revalidates an owner-issued session pin, commits the channel protocol transition, then registers/delivers/accepts the exact invocation. The returned `InlineSipInvocationLease` is TCB-private and contains only trusted correlation plus the existing owner pin. `SettleInlineSessionInvocation` uses the existing invocation terminal state machine and releases that pin. Neither primitive stores a stage value or grants publication authority.

Focused tests prove protocol state/sequence equivalence, zero queued messages, exactly-once settlement, pin release, malformed/stale/closed rejection, fail-closed message-attached authority handling and illegal-transition rejection. This is prerequisite infrastructure only. No production or test Job executor calls it yet, all gates remain OFF, and ordinary SIP remains the sole executable service path.

The representative File Open binding is intentionally not connected to this primitive: `FileObjectResponse` contains capability-bearing `FileObjectAuthority`, so it is not a valid closed-value P14-2 edge. Treating it as the first fused contour would violate the roadmap rather than qualify it.

## P14-2C test-only two-stage closed-value contour

A qualification-only generated SIP contract now uses the same bounded immutable value type for request and response. Its two-stage plan is canonical and verified against a closed catalog containing the exact generated thunk ID/digest. Stage 1 is `HiddenIntermediate`; stage 2 is `FinalPublication`; the edge is `ClosedCopiedValue` with no Region, external effect, async, independent cancellation or ownership semantics.

Under the exact test gate set `FG-JOB-LINEAR` + `FG-DIRECT-SENTRY`, stage 1 enters through an operation-specific TCB binding. The binding privately holds only the generated typed target interface, performs the owner-owned inline session/protocol/invocation transition, revalidates the session pin, calls the generated sentry and settles through `EndpointSessionInvocationRegistry`. The executor has no implementation/target/delegate/object-container field and cannot call an implementation method. Stage 2 is deliberately executed through ordinary `RuntimeSipClientTransport` and `NativeServiceDispatch`, so final completion/publication remains owned by the existing response/invocation owners.

The ordinary two-stage run materializes two requests; the direct contour materializes only the final request. After removing that transport-only difference, completed semantic traces are identical: exact session validation, generated implementation entry/exit and invocation settlement for each stage. Returned closed values and final publication counts are also identical. Tests cover default-OFF gate rejection, malformed first projection, stale service incarnation, tampered plan digest, first-stage service fault/cleanup, close immediately after inline begin, cancellation before first-stage admission, cancellation after the ordinary non-cancellable acceptance point, service fault/replay after inline begin, zero queued intermediate messages, and structural absence of executor implementation references. In the close race, session revalidation denies sentry entry, the existing invocation owner settles cancellation, the last owner pin triggers exactly-once deferred invocation/channel cleanup, and no service or final-stage code runs. A pre-admission cancellation produces no owner transition; a later cancellation loses with the same `InvalidTransition` class as ordinary non-in-flight acceptance. A service fault after begin prevents sentry entry and makes the old route non-replayable. This is JIT test-only evidence: production `SipJobFeatureGates.IsEnabled` remains false for every gate, and no NativeAOT/general service contour claim is made.

## Requirement disposition

- SJOB-002: generated sentry signatures remain strongly typed; existing generator rejection of object/mutable graphs remains intact.
- SJOB-003: the exact ordinary `IFileService.OpenAsync` operation has runtime-enforced generated-sentry entry, and the bounded qualification contract has test-only direct execution; universal and production direct Job execution remain FutureGated. The exact test contour is `QualifiedManagedTestOnly`; the production aggregate remains `StaticAdmission` because `FG-DIRECT-SENTRY` is OFF.
- SJOB-004/005: no owner logic was copied into generated code and no metadata was treated as permission.
- SJOB-013: precompiled thunk identity/digest and a finite exact non-executing binding route exist; executable binding remains absent.
- SJOB-016: JIT generator evidence only; no NativeAOT or fused contour claim.

## Executed evidence

- generator focused tests: 33 passed, 0 failed;
- ordinary File SIP completed-event focused tests: 2 passed, 0 failed;
- generated File sentry tests cover success, revoke on both sides of commit, stale context, malformed projection, cancellation, session close, service fault, publication close/drain and opaque ABI;
- endpoint-session cancellation/publication focused tests: 7 passed, 0 failed, including deterministic callback-outside-owner-lock and exactly-one-settlement evidence;
- inline invocation-owner focused tests: 3 passed, 0 failed; no executor or service call is present;
- P14-2C closed-value direct-contour tests: 7 passed, 0 failed; exact test gates only, with ordinary final publication and deterministic close/cancel/restart cleanup;
- P14-2B exact trusted binding focused tests: 6 passed, 0 failed;
- native-service/generator/SipJob regression set after representative host migration, deterministic races and binding tests: 96 passed, 0 failed;
- solution build: 0 warnings, 0 errors;
- latest directly relevant generator/SipJob/runtime/native/effect-admission regression set: 139 passed, 0 failed;
- full non-GUI suite after the close/cancel/restart race additions: 1249 passed, 8 failed, 2 skipped, total 1259. The failures are the same unrelated missing historical P11/P12/P13 and Hybrid Boot evidence paths plus the pre-existing project-profile inventory mismatch recorded in P14-0;
- `git diff --check`: exit 0, with line-ending notices only.

Structural tests prove the dispatcher references the generated sentry, contains no direct implementation method entry, and the sentry contains no reflection/service-locator/generic-object dispatch. Determinism tests compare every generated artifact across two runs.

## Remaining prerequisite before direct Job execution

Owner: SIP generator plus Runtime native/generated service-host owners. The narrow two-stage qualification contour is now executable and semantically differential-tested under explicit test-only gates. Remaining before any production/runtime gate can be enabled: move the qualification executor shape into a production TCB component without widening its contract set; add deterministic close/cancel/restart hooks at every direct-stage linearization; qualify a real authority-free production operation or keep the contour qualification-only; preserve ordinary fallback for every mismatch; run separate NativeAOT evidence if claimed. File Open remains excluded because its response carries authority.

Until those steps are executable in production, no production Job executor exists and no production direct-sentry runtime claim is made. The executor present in the test assembly is qualification-only and cannot enable a product gate. Ordinary SIP remains the executable oracle/fallback. No plan, thunk ID/digest or dispatcher possession grants authority. No HybridCPU/QEMU/hardware/NativeAOT claim is made.
