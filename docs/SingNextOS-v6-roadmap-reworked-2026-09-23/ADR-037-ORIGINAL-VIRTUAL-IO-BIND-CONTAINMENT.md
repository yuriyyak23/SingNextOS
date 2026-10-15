# ADR-037 — Original unknown Virtual-I/O Bind backing containment

Accepted 2026-10-11. Actual producing need: Bind can allocate private backing
and acquire a native device loan without publishing any usable lease. Kernel
retains its original quantum intent, but lease-based Close cannot clean that
invocation. A missing reply, absent query object or returned-object compensation
cannot release original backing or charge.

The external producer adds optional original parent-device admission containment.
It requires the same retained admission request and an intact authoritative intent,
root factory and user history, including an allocated intent without receipt.
Root→child gates serialize all private-buffer Bind/Transfer/Close/containment.
Containment installs its original fence before clearing backing and closes every
original IO user of that buffer. Future native Bind/Transfer is denied. Independent
devices/backing/users are untouched. A pure same-request query returns only the
retained scoped NoFurtherUse closure; absence/history loss is unknown. Captured
request/phase survives lost reply; native containment cannot repeat.

Optional platform original Bind invocation identity precedes possible effect and
flows through actual bridge/provider/SDK callers. Kernel captures it in its staged
IO/quantum record before invocation, outside owner gates. SDK retains native
admission/raw and containment request before cleanup effect. This cleanup does not
adopt a malformed returned receipt or publish a failed binding. Exact containment
plus fresh source/generation/revision permits native loan retirement; final local
consumption uses the same existing records. Completed cleanup does not need new
active permission. Copies, changed factories, revision exhaustion and unknown or
malformed containment preserve pins/no-repeat. Callbacks/query composition are
outside owner commit locks, and pending interlocks balance on every exit.

An issued marker protects the bridge's original invocation inventory. Optional
unknown invocation has its own child/device lifetime pin, rather than clearing or
reusing an independent global fault pin. Device/child teardown checks this history;
normal exact IO Close also consumes its original invocation. Existing unknown V1
paths keep conservative fault pins. Kernel process/domain cleanup routes unpublished
invocations through this contract and consumes attached ResourceBudget charge only
with exact closure in the existing local commit. Partial local consumption retries
retain raw/correlations and do not repeat native effects. Independent event/compute
interlocks still apply.

Actual adoption uses ExternalRuntime1.20.0 / Contracts1.28.0, additive optional APIs;
operationSchema1.4.0 and manifestContract1.3 unchanged. Native device loan and neutral
APIs remain local project-reference inputs. This is surviving-original accepted
private-backing intent containment, not no-effect inferred from missing acceptance,
durable all-fact-loss/reboot recovery, shared coherence or hardware qualification.
Gates OFF; ProductionQualified false; Java-only skipped; ISA NONE.
