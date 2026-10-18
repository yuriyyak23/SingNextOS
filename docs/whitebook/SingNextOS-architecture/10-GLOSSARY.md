# Glossary

| Term | Precise meaning in this Whitebook |
| --- | --- |
| Authority / owner | Component that owns one canonical logical fact and its valid transitions |
| Admission | Joint predicate over live permission, resource state, budget, source freshness, provider acceptance and applicable legality |
| Original | Exact factory/owner-held request, attempt, raw observation or resource record retained across a possible effect |
| Realm | Authority namespace preventing cross-runtime token equivalence |
| Incarnation | Specific live process, service or provider lifetime |
| Generation / epoch | Mutable lifetime/source freshness markers; never sufficient without canonical ownership |
| Possible effect | Boundary after which missing results do not permit indiscriminate replay |
| No-effect | Proven absence of effect for one exact attempt |
| No-further-use / closure | Verified termination of the specified external use |
| Completion | Reported execution/operation completion, not necessarily visibility, publication or release |
| Visibility | Specific memory/provider visibility condition |
| Publication | Commit making the result visible to the authoritative local publication owner |
| Consumption | Local owner accepts and records an exact terminal or closure fact |
| Reclaim | Release after every independent use, backing and charge obligation closes |
| Pin | Dependency retention protecting a lifetime, not a permission grant |
| Quarantine | Retention while unsafe use is denied pending reliable resolution |
| StaticAdmission | Compile-time or artifact-time validation for a named input |
| ModelOnly | Named model execution, not physical qualification |
| RuntimeEnforced | Enforced at a specific live runtime boundary |
| ExecutableAdapter | Actual adapter implementation for a bounded contract |
| FutureGated | Not enabled pending prerequisites |
| ProductionQualified | Requirement-by-requirement qualified deployment, not overall project self-description |
| SIP | Typed service interface/protocol layer |
| HCEXE | Compiler-produced guest executable artifact |
| IOVA / PASID | Translation/device identifiers; not Region ownership |
| RegionUse | Independently owned lifetime of access to a Region |
| Resource charge | Quantitative obligation tracked by `ResourceBudgetAuthority` |
| IFC | Information-flow-control labels and checks for a named protected contour |
| RAS | Reliability, availability and serviceability/failure-domain work |
| CXL | Compute Express Link contracts for memory/accelerator/fabric resources |
| PCL | Proof-carrying lowering evidence; not capability issuance |
| ISA NONE | This documentation change does not authorize HybridCPU ISA/encoding/retire modifications |

Related canonical names can be followed through [the source atlas](09-SOURCE-ATLAS.md) and [authority map](01-ARCHITECTURE.md).
