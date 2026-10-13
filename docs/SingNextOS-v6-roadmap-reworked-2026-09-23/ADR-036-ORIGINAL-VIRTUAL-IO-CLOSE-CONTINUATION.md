# ADR-036 — Original composite Virtual-I/O Close continuation

Accepted 2026-10-11. Producing need: actual kernel CloseVirtualIo could retain
native IO closure after its pure query failed, while external parent-device
backing and native device loan still needed cleanup. A generic Close retry must
not repeat an unknown invocation; an IO Closed projection is not composite closure.

Add optional platform and neutral original Close requests, retained by existing
bridge/provider/SDK owners before invocation. Each carries reference-continuous
identity and the original admitted lease; the neutral request also references
the original Bind request. Copies, different admissions and missing requests
cannot continue. V1 callers remain unchanged; incapable paths stay pinned.

The first optional Close captures the SDK native Close request before effect.
Continue is cleanup-only: it requires the retained admitted request and does not
create a new lifetime. Invoked native IO/device Close phases are queried only;
uninvoked parent-device cleanup may run once after exact original IO containment
and fresh source/revision checks. Raw facts survive callback/query failures.
Malformed positive facts are never replaced with a queried canonical copy.

Only exact original IO and parent-device NoFurtherUse facts allow native loan
retirement. Fresh generation and local revision are checked after callbacks and
before final consumption. Existing interlocks deny reentry/overlap and balance on
every exit; external callbacks run outside owner gates. Known completed scopes
return retained closure without renewed active permission. The bridge and kernel
consume that closure through the existing local commit and quantitative charge
release; partial local consumption retries do not repeat provider/native effects.

Missing/history loss/source ABA, independent event/compute/backing users and
incapable providers preserve pins. This is surviving-original, in-process private
buffer cleanup, not durable/all-fact-loss recovery or a general containment theorem.
ExternalRuntime1.19.1/Contracts1.27.0/schema1.4.0 are unchanged; new neutral APIs are
local project-reference inputs. Gates OFF; ProductionQualified false;
Java-only skipped; ISA NONE.
