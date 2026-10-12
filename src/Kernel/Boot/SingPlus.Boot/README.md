# SingPlus.Boot host-debug profile

Set `SingPlus.Boot` as the Visual Studio startup project and start it with F5. The process builds a local `RuntimeKernel`, verifies that no external platform backend is installed, starts the managed system-process lifecycle, and stops on `Console.ReadLine` while a debugger is attached.

An additional managed artifact can be admitted as a zero-authority SIP component:

```text
SingPlus.Boot --sip <absolute-or-relative-managed-assembly-path>
```

`--sip` may be repeated. Each artifact is size-bounded, checked as a managed assembly, hashed with SHA-256, assigned a fresh process/domain identity, and passed through `RuntimeKernel.AdmitComponent`. The admitted component reaches the runtime `Running` lifecycle state only after manifest/image validation and transactional admission.

This host-debug profile does **not** load or invoke the assembly entry point. The repository currently has no SIP binary execution/isolation contract, so reflection loading or launching the assembly as an ordinary child process would bypass SingPlus authority and isolation. CXL discovery, CXL Boot, HybridCPU execution, firmware evidence, and platform-provider admission are intentionally absent.

Trace diagnostics are available through `--trace [capacity]` or `trace-start <pid> [capacity]`. Use `trace-show <pid> [count]` to inspect events and `trace-stop <pid>` to stop recording and retain a runtime-authorized snapshot. `trace` lists both tracked sessions and retained snapshots; `status` reports their counts.

The emulator keeps only the last completed snapshot per PID in memory for the current run. A successful `trace-start` replaces that snapshot; a refused start preserves it. `stop` and `fault` stop and capture the trace before process teardown, so the snapshot remains readable after process exit but excludes teardown events. Dropped events and the runtime completeness flag remain visible. Missing traces or refused inspection fail scripts with exit code 1. This is diagnostic capture, not replay or execution of an OS image.

Read-only region borrowing (roadmap H/Q/R) uses real runtime IPC and lease lifetimes:

```text
borrow-channel <leftPid> <rightPid> [capacity]
borrow <ownerPid> <channelId> <regionId>
recv <borrowerPid> <channelId>
borrows
dump <pid> <regionId> [offset] [count]
borrow-return <borrowerPid> <regionId>
borrow-revoke <ownerPid> <regionId>
```

`borrow` queues a loan; `recv` records the delivered lease. While the loan is active, owner `fill`, `dump`, `transfer` and `free` are refused. The borrower gets read-only access through its live lease. Return/revoke invalidates that token and restores owner access. `borrows` keeps the last delivered lease per region, including invalid leases, and `status` counts valid delivered leases. Queued loans are not included in that count; `regions` shows the runtime `Loaned` state. Return/revoke commands require a delivered lease. Shutdown revokes still-valid delivered leases and reports runtime refusals.

`dump` reads only through a tracked owner or borrower token, validates the PID generation, prints at most 256 bytes (default 64), and truncates at the end of the buffer. It does not expose untracked region payloads or fabricate borrow-write support.

Run `--demo borrows --no-shell` to demonstrate read/checksum preservation, refused MOVE/release, wrong borrower, stale lease, owner revocation and subsequent MOVE/release. It also participates in `--demo all` and shell `demo borrows`. For a reproducible console showcase from the repository root:

```powershell
dotnet run --project .\src\Kernel\Boot\SingPlus.Boot -- --no-shell --script .\src\Kernel\Boot\SingPlus.Boot\Scripts\borrow-showcase.txt
```

The script expects a fresh boot without prior demos (deterministic process/channel/region IDs). This remains a host-debug authority demo; it adds no managed binary executor or platform backend.

With `--budget`, emulator-spawned processes (including demos) receive real process budget admission before starting. Admission errors are reported, and partially admitted processes remain tracked for shutdown. This preserves the runtime requirement that budgeted ownership cannot MOVE into an unbudgeted generation.

ManagedCap static verification (roadmap B):

```text
SingPlus.Boot --sip component.dll --sip-root Namespace.Type::Method --no-shell
verify-sip "component.dll" Namespace.Type::Method
```

`--sip-root` requires exactly one `--sip`. It calls the existing ManagedCap AdmissionVerifier, binds the proof digest to the exact image bytes passed to AdmitComponent, and refuses boot before runtime admission on violations. The root file is held with read-only sharing during verification. Dependency, framework and native inventory checks follow the existing verifier policy. Output includes image/dependency/ruleset/proof digests and up to 20 violations with an explicit omitted count.

The shell command verifies without admitting or executing code; refusals fail scripts. `components` distinguishes static pass from not verified. Plain `--sip` preserves unqualified zero-authority admission. Static proof is evidence only: neither route grants a qualified ManagedCap execution environment, loads an entry point, nor guarantees dependency stability across future execution/reload transactions.

Roslyn sample (roadmap C) is in `Samples/ManagedCap`. Build its project normally to generate a bounded counter protocol, client, dispatcher and sentry under `obj/generated`. Build with `-p:IncludeForbiddenApi=true` for the deliberate SING5001 refusal on `Guid.NewGuid`; return to a normal build to restore the positive sample. The sample explicitly references the existing analyzer/generator projects and exposes their profile settings to Roslyn. Sample sources are excluded from Boot compilation. This library is a build/IDE demo, not an executable admitted SIP.

Current SDK constraints: the source analyzer rejects async ValueTask contract return types despite generator support, so the sample uses a synchronous bounded-value contract. The existing FileService generated client/protocol and trusted dispatch binding APIs are internal to Runtime/SIP; Boot does not use reflection or duplicate internal authority types to bypass that boundary.

`authority <pid> [inspectionCapabilityId]` invokes the real public AuthorityInspector. Default scope is the principal's allowed projection; system scope requires an existing Read capability for the exact AuthorityInspector resource, validated by runtime. Capture reports consistency, redaction, nodes and dependency edges, capped at 100 per table with omitted counts. It is read-only diagnostic evidence and never authorizes mutations.

Explicit budget reservations (roadmap I): `budget-reserve <pid> <dimension> <positive amount>` calls RuntimeKernel.ReserveBudget with LocalResource lifetime. `budget-reservations` queries tracked reservation handles from runtime; `budget-release <pid> <reservationId>` releases only explicit emulator reservations, preserving runtime ownership checks. A reservation is accounting, not capability/effect permission, and does not authorize external compute. The shell cannot release runtime-owned buffer/IPC/platform reservations through this command. Shutdown releases explicit local reservations and reports refused/non-Released outcomes.

Run `--budget --no-shell --script src/Kernel/Boot/SingPlus.Boot/Scripts/budget-showcase.txt` from a fresh boot without --trace, SIPs or prior demos: the script uses deterministic first reservation id 1. The showcase exposes hierarchical usage, Active/Released state and exact owner generation. Quarantine, donation and external settlement remain separate unfinished runtime integrations; they are not represented by local reservations.

Platform profiles (roadmap L): `--platform none` is the default and preserves the empty platform manifest. `--platform host-model` (alias `host`) installs the repository's existing HostPlatformAuthorityProvider with its standard feature configuration. Boot verifies that installed features match the requested profile. `platform`, `env` and `status` report the profile and provider manifest without upgrading model availability to physical evidence. Repeated or unknown profile options fail CLI validation with exit code 2.

The Host provider exposes its existing semantic model capabilities; selecting it alone does not bind a running process to a platform domain, execute an admitted assembly, or provide physical CXL/HybridCPU. Platform execution attachment requires a runtime transaction before execution starts; this iteration does not fabricate that transaction. Run the profile showcase with `--platform host-model --no-shell --script src/Kernel/Boot/SingPlus.Boot/Scripts/platform-showcase.txt`.

Platform domain admission: `spawn <identity> --admitted` creates/admit/budgets a process without starting it; `platform-bind <pid>` binds that exact admitted generation; `platform-policy <pid>` configures the explicit background 20ms/100ms policy. `start <pid>` calls the normal runtime start path. Runtime rejects attachment/policy changes after start. Current Host model allows domain/policy admission but refuses start with PlatformUnsupported, leaving state Admitted. `stop` still completes provider closure and teardown.

`--platform host-model --demo platform --no-shell` verifies binding, policy, the exact start refusal and preservation of Admitted state, then reclaims the subject. Shell `demo platform` is identical. This profile-specific demo is deliberately explicit and excluded from `demo all`, which retains generic workloads. `Scripts/platform-binding-showcase.txt` shows the admitted subject and actual authority graph before teardown and runs the negative execution proof. This is a model admission demo, not managed or physical execution.

DSC1 model copy showcase: `--platform host-model --demo compute --no-shell` (or shell `demo compute`) admits a subject without CPU start, binds its platform domain, mints exact read/write mapping capabilities and DSC1 Execute capability, and submits a bounded copy through RuntimeKernel.SubmitPlatformDsc1Copy. Boot never copies the destination itself. The demo verifies ModelOnly evidence, unchanged destination before observation, blocked local buffer access while reserved, exact publication after closed completion, unchanged surrounding bytes and rejection of the completed continuation. It then revokes mappings, releases buffers and completes process teardown. Budget and trace options can be combined. Compute is explicit and excluded from generic demo all; without host-model it refuses. This is provider model compute, not admitted managed image execution or physical acceleration.

Host debug delay/cancellation (roadmap K): `--platform host-model --defer-compute --demo compute-cancel --no-shell` enables the existing provider's deferred DSC1 completion knob. The demo observes PlatformBindingDraining, verifies that both buffers remain reserved and domain revocation returns PlatformBindingActive, then calls the actual CancelPlatformDsc1Copy API. It requires Cancelled outcome, no output publication, unchanged destination, restored access, refused stale continuation and complete mapping/buffer/process teardown. This is deterministic model delay and cancellation, not an emulated provider-loss/quarantine outcome.

Immediate `demo compute` refuses deferred mode; `demo compute-cancel` requires it. Both are explicit profile-specific scenarios, excluded from demo all. `--defer-compute` requires the Host profile and rejects duplicates. Environment diagnostics record the active knob. No runtime correctness checks or provider internals are bypassed.

### External operation admission showcase

Run `--demo external --budget --no-shell`, or use `--budget --script Scripts/external-showcase.txt` from the Boot project directory. Shell `demo external` invokes the same real runtime path. The demo prepares and admits an exclusive Region use, checks premature operation release and Region reclaim refusals, cancels before submission, then explicitly releases the use and its ExternalOperations budget reservation. Runtime transition history is printed; unchanged bytes and stale use rejection are checked before process teardown. This scenario requires no platform provider and is included in `all`.

Generic Region uses protect runtime mutation/reclaim operations but do not revoke a CLR OwnedBuffer Span. This demo does not claim span isolation, provider execution, completion, visibility, publication, or resource consumption evidence. Submitted effects remain covered separately by the DSC1 model demo.

### Step-by-step external admission controls

`external-prepare <pid> <regionId>` creates a staged, exclusive-write operation over a tracked buffer. `external-admit`, `external-cancel`, `external-release` take `<pid> <operationId>` and call the real runtime owners. Cancellation retains the Region use and budget until explicit release. `external <pid> <operationId>` queries state, disposition, effect boundary and exact transition history. `region-uses [regionId]` reads runtime descriptors rather than a local copy. Host-debug inspection is best effort, not a SIP authority grant.

Run `--budget --script Scripts/external-control-showcase.txt` from the Boot project directory in a fresh session. IDs in the script assume no preceding demos. These controls never submit to a provider or manufacture completion/closure evidence. Shutdown cancels and releases unfinished shell-created operations, including system-owned operations; successful process teardown removes their local handles.

### Authenticated resource-budget recovery boot

Supply `--budget --recovery-journal <path> --recovery-key <path>` to pass real RuntimeKernelRecoveryOptions to the runtime. The key must be an existing 32-byte binary file; Boot does not generate, display or replace it. The temporary key array is cleared after runtime construction. Retain the exact key across restarts. Journal and key paths must differ. Use only one host process per journal.

Runtime performs authenticated replay and conservative charge staging; system budget configuration imports those charges, visible via `budget`. `Scripts/recovery-inspect.txt` prints environment and budget state. Missing journals mean empty history, not recovered operations. Corrupt/torn journals and invalid keys fail boot without reset or repair. Local reservations, generic pre-submit operations and DSC1 copies are not claimed to produce durable resource-admission records. Non-empty crash/reconciliation showcase and detailed backlog inspection remain unfinished: replay snapshot/backlog APIs are internal. Power-loss durability is not qualified by this host-debug integration.

### Cancellation coordination

`cancel-scope <pid> [parentScopeId]`, `cancel-request <pid> <scopeId>` and `cancel-show <pid> <scopeId>` use runtime-owned cancellation scopes. Child scopes inherit requests; runtime enforces exact process-generation ownership. `external-admit <pid> <operationId> [scopeId]` binds a consumer. `external-cancel <pid> <operationId> [scopeId]` with a scope invokes RequestExternalOperationCancellation and checks the exact admission binding. Scope requests alone do not cancel effects or release regions/budgets. Stale observations fail the command. Use `cancel-scope <pid> [parentScopeId] --after-ms <1..86400000>` for a relative monotonic deadline. Boot uses the exact TimeProvider instance supplied to runtime, rounds upward to clock ticks and prints the effective timestamp. Children inherit parent deadlines and cannot extend them. Expiry is a runtime observation, not provider cancellation acknowledgement or resource release.

`--budget --trace --script Scripts/cancellation-showcase.txt` shows parent propagation, explicit cancellation disposition, held Region use and separate release in a fresh session. Runtime owns scope invalidation during process teardown; local scope handles remain available for stale-owner checks, never reused as authority.

### Service/session lifecycle showcase

`--demo sessions --budget --trace --no-shell`, shell `demo sessions`, or `Scripts/sessions-showcase.txt` uses real RegisterService, discovery, OpenSession and CloseSession. It checks exact admission capability requirements, revocation refusal, foreign-owner close refusal, Draining refusal for new sessions, closure of an existing session and rejection of its closed handle. Both processes are reclaimed by runtime teardown. It is included in `all`. The protocol reuses Boot ping-pong descriptors; this is service/session lifecycle evidence, not generated SIP dispatch, a managed-image executor or hot reload.

### Reproducible implemented-path showcase

From any working directory, run `Scripts/Run-HostedShowcase.ps1` using its full path. The runner resolves artifacts relative to itself, builds Boot and the real analyzer/generator sample, verifies SING5001 on the opt-in forbidden API, restores the positive sample, then builds the minimal VerifiedRoot assembly. It checks ordinary boot/help, the current generated-contract static-verifier closure refusal, positive minimal-root static admission, and the combined Host/model ownership/capability/session/external/DSC1 script. Deferred DSC1 cancellation runs in a separate process. Every gate checks exit status and required evidence; unexpected results stop the runner.

The minimal VerifiedRoot sample is separate because the generated-contract sample currently fails verifier resolution of a signature-qualified local constructor. Neither sample is executed as an admitted managed image. Lifecycle is deliberately omitted from hosted-showcase.txt because its parked-allocation assertion currently fails in runtime; the assertion and `all` behavior are preserved. This runner reproduces implemented paths, not the full canonical external plan. It does not qualify generated SIP invocation, crash recovery, provider loss/reconciliation, service generation replacement, HybridCPU or physical CXL. Normal bin/obj build outputs remain; no temporary source/key/journal files are created by the runner.

### Service generation restart

`--demo service-restart --budget --trace --no-shell` and shell `demo service-restart` register a real service/session, terminate and reclaim its provider, then register a replacement under the same service identity. Runtime teardown closes the old session and retires the service generation. The demo verifies a gap in discovery, an exact generation increment, StaleGeneration for the old descriptor, SessionClosed for the old session, publication of the replacement descriptor, a fresh session and final cleanup. Scripts/service-restart-showcase.txt and the hosted showcase include this path.

The test service declares no extra capability requirements; capability-gated admission is checked separately by sessions. This is a sequential host-debug service restart, not Roslyn image verification, an atomic live publication switch, generated invocation draining, sealed-object migration or managed hot reload. Those plan requirements remain.

### Supervisor replacement with verified static image

`--demo supervised-restart` requires exactly one boot-admitted SIP with `--sip-root`; use the Samples/VerifiedRoot artifact for a supported static root. The demo re-reads the bounded image, checks its boot digest and static proof, mints the exact supervisor control capability, registers and starts a component, then calls real CapabilityAwareServiceSupervisor.Replace. The next process uses the same ProcessId/DomainId with generation 2. Old service descriptors and supervisor instances must return StaleGeneration, replacement must report FreshAdmission, and final Drain must prove closure. Control capability is revoked after completion; partial admission is tracked for shutdown.

This explicit demo is excluded from `all` because it requires a verified image. Scripts/supervised-restart-showcase.txt and the hosted runner cover it. By default both generations use the same verified bytes. Supply `--replacement-sip <path>` for distinct generation-2 bytes; the same --sip-root is verified against the replacement image and its digest is checked against the published component snapshot. This demonstrates verified supervised runtime lifecycle replacement, not updated CLR code execution or atomic in-flight hot reload. No application is loaded or invoked and no provider effect is fabricated.

### Bounded Observatory watch

`watch <pid> [samples] [intervalMs] [inspectionCapabilityId]` captures live authority summaries through the existing AuthorityInspector. Defaults are 5 samples at 250 ms; limits are 1..20 samples and 1..1000 ms. The command returns automatically in scripts, with no open-ended shell loop. Each capture revalidates runtime inspection access and reports sequence, scope, consistency, node/edge counts and redaction. Grouped kind/state counts come directly from each snapshot; up to 100 groups are shown with explicit omitted count.

Self scope is the default. System scope requires the same exact kernel:authority-inspector:v1 Read capability as `authority`; watch creates no permissions or authority records. Captures are PointInTimeBestEffort, not atomic authority cuts, and local script commands wait until bounded sampling finishes. Scripts/watch-showcase.txt demonstrates the view before and after buffer release.

### Injected model compute fault (expected failure)

Run `--platform host-model --defer-compute --budget --trace --demo compute-fault --no-shell` in a dedicated process. The demo uses the existing Host provider AdvanceOperation on its exact submitted DSC1 operation to inject Faulted. Runtime must report PlatformFaulted, keep both buffer reservations inaccessible, preserve unpublished output bytes and refuse Region/domain reclaim with PlatformBindingActive. The command intentionally exits 1, then reports fault-contained incomplete teardown. The hosted runner checks this expected refusal separately; it is excluded from `all`.

This diagnostic cannot recover or reconcile a faulted DSC1 operation through current public APIs. It does not assert provider loss, budget quarantine/settlement or durable crash recovery. No receipt is fabricated and no forced cleanup releases pinned authority. Host DSC1 remains ModelOnly: local reference copying belongs to runtime, not physical hardware execution.

### Existing HybridCPU model adapter

`--platform hybridcpu-model` (alias `hybridcpu`) installs the existing HybridCpuPlatformAuthorityProvider with its default NeutralDomainRuntimeFacade ModelOnly composition. The platform command prints actual feature classification; RuntimeAdmission does not qualify physical hardware execution. No new backend is implemented in Boot. The no-platform default and Host-only DSC1 demos remain unchanged. `--defer-compute` remains Host-only.

`--demo platform-regions` is the same public runtime allocation/domain binding/capability mapping/revoke/reclaim workload for host-model and hybridcpu-model. It checks mapped Region reclaim refusal, unchanged bytes, explicit mapping revocation and final process cleanup. It is excluded from all because a platform must be selected. Scripts/platform-regions-showcase.txt and the hosted runner cover both providers. HybridCPU DSC1/Matrix compute and executable adapter qualification remain outside this demo.

For a reproducible different image, build Samples/VerifiedRoot/VerifiedRoot.csproj with `-c Next -p:NextGeneration=true`. This emits bin/Next/net11.0/VerifiedRoot.dll with a different arithmetic IL body. The hosted runner builds both versions and passes --replacement-sip for the supervisor scenario. No arithmetic body is invoked by Boot. An explicit replacement with identical bytes, unreadable data or a failed static root is refused before supervisor effects.

### Generated copied-value SIP round trip

`--demo generated-sip --budget --trace --no-shell` and Scripts/generated-sip-showcase.txt run the actual analyzer/generator sample bindings over RuntimeSipClientTransport and a capability-gated EndpointSession. CounterBindings exposes generated protocol/message/response metadata and client/dispatcher inside its own assembly; no protocol is handwritten. A cooperative host pump receives and accepts the exact runtime invocation, calls the generated copied-value dispatcher, then publishes the runtime response. Input 41 must yield 42. Revoking the admission capability must reject another call before implementation entry. Sessions and processes close normally.

The sample is a trusted linked host workload, not an arbitrary admitted managed image. Generated copied-value sentry/dispatcher is exercised; trusted resource-operation sentry context, resource donation, controlled CLR loading and ManagedCap execution qualification remain unimplemented. Runtime transport provides request/response ownership; the host pump never constructs a response envelope or grants authority. The demo is included in all and in the hosted runner.

With --trace, generated-sip starts real traces for both participants before service registration, then displays retained runtime snapshots after successful reclaim. Small buffers report dropped events and completeness. Capture precedes teardown; the current runtime does not emit dedicated session dispatch/response publication events, so this view does not claim a complete causal invocation trace.

generated-sip also binds a real CancellationScope to a queued invocation, requests cancellation before service acceptance, checks that execution acceptance is refused, then explicitly accepts cancellation and settles the response as Cancelled with no payload. Dispatcher count must remain unchanged; a subsequent generated call must succeed on the same session. This proves pre-effect cancellation and settlement, not cancellation of in-flight resource effects.

generated-sip checks the opposite cancellation ordering too: after service acceptance with allowInFlightCancellation=false, scope cancellation must return TooLateEffectMayExist, service cancellation query stays false, and the actual generated dispatcher response publishes normally. This disposition does not independently authorize reclaim; cleanup uses normal successful invocation settlement and runtime process teardown.

why-reclaim <pid> [inspectionCapabilityId] calls public AuthorityInspector.WhyReclaimBlocked with the same runtime access checks as authority. It displays visible process blockers, exact blocking-node identity, redaction and snapshot consistency; no visible reasons does not authorize reclaim. Scripts/reclaim-showcase.txt demonstrates an admitted local external operation retaining its blocker after cancellation, disappearing only after explicit release, then actual successful teardown. Run on a fresh --budget boot.

why-move <pid> <regionId> [inspectionCapabilityId] and why-free with the same arguments query public runtime Region explanations using the exact tracked handle. Visibility and inspection rights remain runtime-enforced. Scripts/region-explanations.txt shows active BorrowActive/Loaned blockers, their removal after real borrow-return and successful free/teardown. Explanations are best-effort observations, not mutation permits.

why-pinned <pid> <operationId> [inspectionCapabilityId] calls AuthorityInspector.WhyExternalOperationPinned for an exact shell-tracked handle with runtime visibility checks. The reclaim showcase compares its reasons with region-uses. Current runtime projection retains Active RegionUse descriptors from admission even after release: it can report RegionUseActive while current Region uses are empty and normal teardown succeeds. Boot displays that limitation verbatim; no closure or reclaim authority is inferred.

Incomplete process shutdown now automatically queries the public self-scoped AuthorityInspector reclaim explanation. It reports exact reason/node, redaction and best-effort consistency, or the precise inspection refusal, without retrying or bypassing cleanup. The compute-fault gate requires ProviderEffectUncontained and preserves expected exit 1; the observation does not authorize reclaim.

--demo backing, shell demo backing and Scripts/backing-showcase.txt exercise actual local RegionAuthority backing lifetime: duplicate reservation and premature Region reclaim refuse PlatformBindingActive, public inspection reports BackingLeaseActive, release invalidates the exact lease and permits normal Region/process cleanup. This is a local lifetime reservation; external backing providers, CXL residency and migration remain unimplemented.

The backing demo also reacquires a lease for the same Region and checks fresh lease identity, refusal of an old release handle, wrong-generation validation and wrong-owner release. All refusals must preserve the exact replacement lease, which is then explicitly released before normal cleanup. Local lease replacement is not external backing migration.

Shell service controls: service-register <pid> <name> registers the existing Boot ping-pong contract with required KernelService Read on the exact name; service <name> discovers runtime descriptors; service-state <name> <accepting|draining|unavailable> changes availability as a host control operation. session-open <pid> <name> [capabilityId ...] and session-close <pid> <sessionId> use runtime admission and exact owner checks. Shell handles retain generation for stale/closed refusal; runtime teardown closes outstanding sessions. Non-accepting services are excluded from discovery, while retained service identity permits restoring availability. Scripts/service-shell.txt is a fresh-boot lifecycle example. These commands manage session lifecycle; they do not install a request handler or execute a SIP image.

session-open accepts --lifetime-ms 1..86400000 once, passing the duration to RuntimeKernel.OpenSession. Runtime checks expiry on invocation; explicit close remains allowed. The sessions demo opens a short-lived exact session, waits a bounded 20ms, and verifies RuntimeSipClientTransport refuses invocation with SessionTimedOut before enqueue. The shell lifecycle script also exercises explicit lifetime admission. This is runtime session lifetime, independent of cancellation scope deadlines.

--platform hybridcpu-child connects the existing HybridCpuExecutableChildAdapter (ExternalRuntime V3 packages from its existing project) to HybridCpuPlatformAuthorityProvider alongside the neutral model parent facade. env/platform/status expose the exact runtime manifest and mixed feature classes. Current manifest advertises executable child artifact/guest-memory families but does not qualify a complete executable virtualization-domain workload. Scripts/hybridcpu-child-profile.txt and the runner verify actual feature admission and the same Region/map/reclaim lifecycle; guest artifact execution and DSC1 compute are not yet claimed. Default and hybridcpu-model behavior remain available.

--platform hybridcpu-child --demo child-domain --no-shell and shell demo child-domain now perform real RuntimeKernel.CreateVirtualDomain, ConfigureVirtualDomain, DestroyVirtualDomain and owner/parent binding teardown through the existing executable adapter. The current composition completes this lifecycle despite lacking aggregate VirtualizationDomains/BoundedVirtualIo claims. The explicit demo is excluded from all; artifact binding/execution remains separate and unverified. Runtime PlatformUnsupported/PlatformDenied is reported as a preserved admission boundary; other failures fail the demo.

child-execute (explicit, excluded from all) uses real executable guest mapping, BindVirtualDomainExecutable and StartVirtualDomain, with --platform hybridcpu-child. --guest-artifact <HCEXE path> selects a bounded (64 KiB) package; mapping is 16 MiB and execution budget is 100000 steps. The bundled guest.hce is compiler-generated from GuestRoot.Run, not handcrafted; its compiler schema is incompatible with the currently bundled ExternalRuntime (VersionSkew). The default demo therefore fails with exit 1 at HCEXE preflight, before guest mapping/artifact effects; normal owner cleanup succeeds and the runner gates that refusal. Successful execution with a compatible package remains unverified. Guest package admission is separate from SIP ManagedCap static verification and is not CLR execution.

child-execute now uses the public bundled HybridCpuRestrictedImageBuilderV1 inspector before creating guest memory/mapping. This is the same package implementation used by ExternalRuntime; it checks package compatibility and startup contract and grants no authority. Provider/runtime bind and start still run for accepted packages. Both available local compiler binaries produce the same rejected schema; no header downgrade or runtime acceptance change was made.

verify-guest <HCEXE path> inspects 1..65536 bytes under FileShare.Read with the bundled HCEXE startup inspector, prints status, exact input SHA-256 and bounded diagnostic codes/messages, and fails scripts on rejection. It needs no platform profile and creates no process, guest mapping or execution authority. Current sample reports VersionSkew/HCSTART2004. No compatible compiler package was found in the local NuGet cache/feed; guest execution remains unverified.

## Cooperative hosted application

Run a persistent generated Counter service from the shell:
counter-start <name>, counter-call <name> <Int32>, counters, counter-stop <name>.
Each application owns two runtime processes, a capability-gated service and one
EndpointSession. Repeated calls use the same session. The result and successful
call count update only after runtime response publication. Duplicate names,
unknown applications, Int32 overflow and revoked authority are refused.
The service identity is unique to the server process; restarting a stopped name
creates fresh processes and a fresh session. counters prints exact process/session
handles and the admission capability is printed at startup for revoke diagnostics.
With --trace both application processes get runtime traces; use trace-show <pid>
after stopping to inspect the captured snapshots. IPC events are runtime evidence;
there are no dedicated generated-dispatch trace events.

From the repository root:

    dotnet run --project src/Kernel/Boot/SingPlus.Boot -- --demo hosted-service --budget --trace --no-shell
    dotnet run --project src/Kernel/Boot/SingPlus.Boot -- --script src/Kernel/Boot/SingPlus.Boot/Scripts/hosted-counter.txt --no-shell
    pwsh -File src/Kernel/Boot/SingPlus.Boot/Scripts/Test-HostedCounter.ps1

hosted-service checks repeated calls, overflow and revoked-capability refusal,
then closes the session and reclaims both processes. counter-stop retains partial
cleanup progress for retries; shutdown also stops applications left open by a
script or the interactive shell. Failed setup attempts reclaim known processes;
incomplete cleanup is reported and remains tracked for shutdown.

This is the existing generated copied-value contract running cooperatively on the
trusted host CLR through RuntimeSipClientTransport, runtime acceptance and response
publication. It does not execute arbitrary admitted SIP images or emulate hardware.
## Current runtime integration (2026-10-08)

--platform host-model --demo virtual-domain --budget --trace --no-shell
exercises real runtime virtual-domain admission, a capability-authorized guest
mapping, premature Region release refusal, start/park/resume, cancellation of an
event wait without domain cancellation, exact-source virtual event delivery,
destroyed-domain refusal and endpoint/Region/process cleanup. This is ModelOnly
lifecycle and event evidence; no guest instructions or hardware isolation claimed.
The demo is explicit, excluded from generic --demo all, and also available as
shell demo virtual-domain or Scripts/virtual-domain-showcase.txt.

PowerShell launchers resolve all paths relative to themselves and work from any
directory. Run-Demo.ps1 supports Boot, Hosted, Memory, Compute, ComputeCancel,
VirtualDomain, HybridCpu, Services and All. Each scenario runs in a fresh process,
suppresses the interactive shell, checks exit code and required evidence, and
refuses incomplete shutdown. All selects these supported positive scenarios;
it is distinct from the CLI's historical --demo all. Negative provider-fault and
incompatible guest-artifact probes remain in the existing verification runners.

    pwsh -File Scripts/Run-HostDemo.ps1
    pwsh -File Scripts/Run-MemoryShowcase.ps1
    pwsh -File Scripts/Run-ComputeShowcase.ps1
    pwsh -File Scripts/Run-VirtualDomainShowcase.ps1
    pwsh -File Scripts/Run-ServiceShowcase.ps1
    pwsh -File Scripts/Run-AllDemos.ps1
    pwsh -File Scripts/Run-Demo.ps1 -Scenario ComputeCancel
    pwsh -File Scripts/Run-Demo.ps1 -Scenario HybridCpu

Each accepts -Configuration Debug|Release and -NoBuild. NoBuild requires an
existing output for that configuration. These commands assume the Boot directory;
absolute script paths can be used from anywhere. No additional backend or package
dependency is introduced. Current modernization status lives in
docs/SingNextOS-v6-modernize; older Boot backlog entries are historical and need
source revalidation before being treated as current runtime defects.
## ASCII snake

Play-Snake.ps1 launches a 16x10 ASCII snake game:

    pwsh -File Scripts/Play-Snake.ps1
    pwsh -File Scripts/Play-Snake.ps1 -Demo

Direct CLI: --game snake --budget --no-shell. From the interactive shell: snake.
Interactive movement runs every 500 ms. Use WASD or arrow keys without Enter.
R restarts, Q or Escape quits. Food is deterministic; eating grows the snake and increases
the score; wall/body collisions end the round. Reversing directly keeps the
current heading. The screen uses ASCII @, o, * and borders. Interactive frames redraw the terminal; scripted frames print sequentially. Interactive play requires terminal input and output. Script mode accepts snake <WASDRQ moves> (maximum 4096 characters);
Scripts/snake-showcase.txt exercises growth, collision, restart and cleanup.

The game owns a generated ISnakeService contract. Inputs and responses contain
bounded primitive values; the board is a fixed three-word bitset. Each step
passes through generated client, RuntimeSipClientTransport, real runtime session
acceptance, generated dispatcher and runtime response publication. Game state belongs to a restricted Windows child process. A trusted parent broker owns a runtime-accounted snapshot Region; this does not execute an arbitrary admitted DLL or SipJob. Console input and
rendering belong to Boot. --budget and --trace use the existing runtime paths.
Q, EOF and invocation errors close the session and reclaim game processes.
## Snake restricted child integration (2026-10-11)

Snake runs in Samples/Snake.Child, a separate .NET process without RuntimeKernel.
Samples/Snake contains the bounded contract, generated dispatcher and game logic.
Samples/Snake.Host is the trusted parent broker. Boot retains console/timing and
lifecycle orchestration. Build outputs deploy the child into snake-child alongside
Boot and the executable checks.

The parent uses existing RuntimeKernel capabilities, service registration, session
admission, request receive/acceptance and response publication. Only a monotonic
sequence and bounded direction/frame cross two inherited anonymous pipes. Replies
are checked before publication. Startup and reply waits are bounded. Launch failure
does not fall back to execution in the parent.

Windows launches the child suspended under a restricted low-integrity token with
privileges disabled; the launcher verifies integrity before resuming it in a Job.
The Job permits one process, caps committed memory at 512 MiB and CPU at 20%,
restricts UI operations and kills its process on close. Only the two pipe handles
are inherited. The environment contains SystemRoot and disables .NET diagnostics;
parent environment variables are not passed through.

The SIP/SipRegion runtime owner holds a 160-Int32 snapshot Region (640 budgeted
bytes), allocated by RuntimeKernel.AllocateBuffer and refunded on release. Actual
game state and CLR allocations are private child memory, outside runtime budget
accounting and constrained by the Windows Job. Stop joins the broker and confirms
child termination before releasing the snapshot and reclaiming runtime processes.
An exited child refuses further dispatch and its failure remains visible.
A failure after runtime acceptance can leave an invocation unresolved; cleanup
reports runtime refusal instead of fabricating a response or rollback.

    pwsh -File Scripts/Play-Snake.ps1
    pwsh -File Scripts/Play-Snake.ps1 -Demo
    pwsh -File Scripts/Test-Snake.ps1

Checks cover separate child PID, growth, cancellation before enqueue, capability
revocation without new dispatch, exact snapshot charge/refund, forced idle child
exit, retained failure, confirmed child death, idempotent stop, restart, traces and
reclaim. The ASCII showcase covers collision/restart/quit.

This is Windows host containment, not AppContainer: file reads and network access
are not comprehensively denied. It is not verified-image execution binding,
HybridCPU-v2 execution or SingNextOS hardware isolation. The trusted build output
is launched through the installed dotnet host; no verifier-to-image binding is
claimed. ResourceScheduler does not schedule child instructions. OS, runtime,
ISE/HybridCPU sources and backend providers are unchanged by this integration.
