# Phase 06 Implementation Evidence — Identity-Only Software Sealing

**Date:** 2026-09-19  
**Claim:** `RuntimeEnforced` identity/lifecycle sealing for the socket pilot, composed with capability/session authority.

## 1. Baseline and preservation

- HEAD before P06: `a67eea1aafc72054d22f1586b62c6883cdc71681`.
- Existing P00-P05 and unrelated dirty-worktree changes were preserved.
- No reset, checkout, clean, force, commit, push or destructive operation was performed.

## 2. Audited dependencies and reused owners

Audited native file/socket contracts and hosts, `ServiceRegistry`, endpoint-session lifecycle, P04 operation admission, P05 opaque handles, generated network SIP client and native-service tests.

Reused authoritative owners:

- `CapabilityAuthority`: sole rights, constraints, lineage, quota and exact operation authority.
- `EndpointSessionRegistry`: caller/service/session lifecycle and pins.
- `ServiceRegistry`: stable service identity and incarnation/generation.
- `SealedObjectAuthority`: new orthogonal socket object identity/lifecycle owner only.
- `RuntimeNetworkServiceHost`: service-private socket state.

## 3. Design decisions

- Public `SealedHandle<TSeal>` contains only version, runtime realm and opaque token. `TResource` is never public.
- Stable registered `SealTypeId` constants are used; `Type.GetHashCode()` and inferred type names are not identities. Unknown markers fail closed.
- Socket seal records bind type, realm, service identity/incarnation, service process, caller, session, object generation/key and associated exact capability identity.
- Seal records contain no rights or arbitrary operation policy.
- Compatibility `SocketObjectHandle` retains session/object/generation fields, but runtime object lookup uses only the trusted key returned by the live seal pin. Caller fields cannot select a sibling.
- Sealed resolution is internal and operation-specific. There is no public `Resolve<TResource>()` or enumeration API.
- Socket send/receive/close acquire: seal pin, P04 session+capability effect admission, final seal revalidation, then service-local effect after all registry locks are released.

## 4. Authority / identity / evidence / provider split

- Seal: object identity and lifecycle only.
- Capability operation lease: effect rights and exact network resource/operation authority.
- Session pin: live caller/service session authority.
- Compatibility object ID/generation fields: evidence checked against the seal record, not lookup authority.
- Provider: socket pilot is local/model service state and invokes no external provider.

## 5. Non-duplication proof

The seal table cannot grant Read/Write/Configure and cannot resolve application resources publicly. Its associated capability field is an identity reference used to reject sibling substitution; liveness and rights are always re-evaluated by the existing capability ledger. No sealing rights counter, capability graph, quota store, completion registry or generic object heap was added.

## 6. Linearization, stale, revoke and closure semantics

- Seal mint inserts the full binding under the seal gate.
- Pin acquisition validates every binding and increments the exact record pin count under that gate.
- Capability/session admission then follows P04 reservation/revalidation; final seal revalidation rejects close/restart races.
- Close changes the exact seal from Active to Draining. New pins fail; release of the last active pin linearizes Closed.
- Service generation/process mismatch rejects old handles, including object-key reuse after restart.
- Revoking the associated capability leaves identity resolvable but prevents effect admission; seal possession alone cannot authorize.
- Drain revokes socket seals and their capability records before clearing service-private objects.
- No cancellation, publication, quarantine or reclaim outcome is fabricated by sealing.

## 7. Public/SIP and non-leak status

The generic handle is identity-only and constrained to registered marker types. There is no public resolver, sibling enumeration, rights field or resource implementation type. Existing generated network messages carry the compatibility wrapper containing the seal; the runtime sentry ignores caller object IDs for lookup and composes seal/session/capability authority.

## 8. Changed files/projects

- `contracts/SingPlus.Contracts/SealedHandles.cs`
- `contracts/SingPlus.Contracts/NativeServiceContracts.cs`
- `src/Runtime/SingPlus.Runtime/Capabilities/SealedObjectAuthority.cs`
- `src/Runtime/SingPlus.Runtime/RuntimeKernel.cs`
- `src/Runtime/SingPlus.Runtime/Services/ServiceRegistry.cs`
- `src/Runtime/SingPlus.Runtime/NativeServices/RuntimeNativeServiceHosts.cs`
- `tests/SingPlus.Tests/Capabilities/SingCapPhase06SoftwareSealingTests.cs`
- this evidence file.

## 9. Commands and actual results

```text
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore --filter "FullyQualifiedName~SingCapPhase06SoftwareSealingTests|FullyQualifiedName~NativeSystemServiceVerticalSliceTests|FullyQualifiedName~SingCapPhase04EffectAdmissionTests"
Passed: 31, Failed: 0, Skipped: 0

dotnet build "SingNextOS.slnx"
Build succeeded. Warnings: 0, Errors: 0

dotnet test "SingNextOS.slnx" --no-build
HybridCpu_ExecutableAdapter.Tests: 12 passed
HybridCPU_NeutralRuntime.Tests: 58 passed
SingPlus.Platform.HybridCpu.Tests: 60 passed
SingPlus.Tests: 1139 passed, 2 skipped
Aggregate: 1269 passed, 2 skipped, 0 failed

git diff --check
Exit 0; no whitespace errors. Git emitted only existing LF-to-CRLF working-copy notices.
```

Tests cover stable serialization, forged token/realm, unknown marker/type, wrong service incarnation/process, caller, session, object generation and capability identity, identity without live rights, sibling substitution/non-enumerability, restart reuse, close race and a real generated socket sentry with independently missing seal/rights.

## 10. Claim and limitations

Claim is `RuntimeEnforced` for socket identity/lifecycle sealing and its composed local effects. File/process/GUI object migration remains P11 scope. P07 Region hardening and P08 generated generalized sentries remain required. The compatibility socket wrapper still exposes legacy diagnostic fields; they are not used as object lookup authority.

The optional `tools/HybridCpu_ExecutableAdapter/refctor master plan2.md` is absent; no content was inferred.

## 11. HybridCPU boundary

HybridCPU core, ISE, ISA/opcodes, registers, compiler, scheduler, load/store, retire and architecture were not modified. Seal tokens never become provider authority.
