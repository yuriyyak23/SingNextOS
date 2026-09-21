# Remaining genuine open questions

Only questions not resolvable from current live code remain here.

1. For the selected Matrix provider contour, which resource dimensions can be **runtime-enforced** versus only measured, and what exact measurement contract/evidence is available?
2. Can the selected Lane6/Lane7/Matrix provider contour prove a bounded/non-late-effect containment closure using existing drain/fence/generation mechanisms without ISA changes? If not, containment guarantee remains Unsupported and quarantine/drain policy is used.
3. What numeric precision/rounding/overflow contract is required by SingNext MatrixMultiply, and which current HybridCPU MatrixTile/L7 backend exactly refines it?
4. What benchmark/SLO would justify sharding ResourceBudgetAuthority? Until defined and failed, P14 sharding remains deferred.
5. Is any remote/attested provider in the first qualification scope? If not, malicious/byzantine/attested evidence remains unsupported rather than an open implementation blocker.

Resolved/not-open: IFC is deferred by scope; shared-mutable Region authority is deferred until a concrete contour; OperationContract generator is optional; vector budget reservation already exists; direct coherent output is gated rather than unresolved.
