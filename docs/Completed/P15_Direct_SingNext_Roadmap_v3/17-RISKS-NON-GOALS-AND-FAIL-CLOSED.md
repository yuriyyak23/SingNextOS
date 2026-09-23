# Risks, Non-Goals and Fail-Closed Policy

## Highest-risk mistakes

- treating BootInfo/BDF/DSN/HPA/DPA/decoder/route as authority;
- assuming the current BootInfo importer is absent and creating a competing owner;
- leaving `IFreshCxlBootDiscovery` or `IFirmwareApertureRetirement` test-only while claiming production handoff;
- letting importer or capsule mint provider/Region generations;
- repurposing host-debug `SingPlus.Boot` as the capsule despite its Runtime/Host/ManagedGc closure;
- moving Boot.Contracts/model code for naming reasons and accidentally changing dependency/security classification;
- direct use of allocation-heavy codecs in a selected no-heap capsule;
- conflating architectural reset with `ObservePlatformBackendReset()`;
- optimistic HDM release after uncertain compensation;
- changing HybridCPU SHA without requalification;
- calling NativeAOT isolation;
- deleting model oracles before differential closure.

## Mandatory fail-closed states

Ambiguous identity/liveness/generation, partial decoder state, unknown retirement, reset during unresolved operation, protected-state split brain, unauthenticated rollback floor, provider refusal, malformed BootInfo, missing external gate => `Stale`, `Quarantined`, `ReclaimBlocked`, bounded recovery, reset or fail-stop according to the owning state machine.

Never publish runtime authority as a best-effort fallback.

## Non-goals

Do not redesign existing CXL/Region/platform authority ledgers. Add narrow adapters only where P15-00 proves a missing integration seam.
