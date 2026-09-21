# Rejected Alternatives

- SingNext capability IDs, Region handles, resource lease handles or session handles in ISA.
- OS-specific VLIW encoding, lane/slot/queue IDs in authority API, tagged pointers/memory or capability-aware LOAD/STORE/FETCH.
- CPU-owned `ResourceBudgetAuthority` or provider mutation of OS accounting truth.
- Provider receipt/certificate/measurement/replay evidence as capability or publication authority.
- Provider-owned OS publication policy; `Complete == Visible == Published` shortcuts.
- Universal capability/resource/operation state object.
- Second Region ledger for shared/coherent mutation.
- Second temporal/resource authority instead of existing `CapabilityAuthority` + `ResourceBudgetAuthority`.
- Generic scalar resource accounting across time/bandwidth/occupancy.
- Scheduler `authorized=true` cache or policy in CPU.
- Compiler/certificate/typed-slot metadata as runtime legality authority.
- Generic `Cancel()` as proof of no future effect.
- Provider loss as proof of refund/reclaim safety.
- Replay evidence as direct submit permission.
- Boolean `Secure` instead of typed isolation dimensions.
- New general `ExecutionBinding` name that collides semantically with current `SecureExecutionBinding`; use a distinct name.
