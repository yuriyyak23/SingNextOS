# 16. Compiler Secure/Virtual Intent And CXL Boundary

## Goal

Modernize the HybridCPU compiler so secure/virtualized external execution is expressed as semantic intent while runtime authority remains entirely outside the compiler.

## Required compiler IR semantics

Introduce provider-neutral requirements equivalent to:

- execution-domain requirement: host / virtualized / secure / secure+virtualized;
- external-effect class and replay safety;
- staged publication requirement/preference;
- secure evidence requirement and minimum assurance class;
- virtual-domain binding requirement;
- bounded virtual-I/O requirement;
- cancellation/containment requirement;
- memory access intent: device-readable, device-writable, coherent-required/optional.

The compiler must not carry runtime handles, CXL endpoint identities, Fabric Manager bindings, HDM decoder IDs, DPA, switch routes or security-evidence generations in ordinary IR.

## Lowering rules

- IR intent lowers into existing typed-slot/bundle carriers wherever possible.
- No new `Remote Lane` or CXL execution lane is introduced.
- lane6 DSC and lane7 L7-SDC remain semantically distinct.
- secure/virtual requirements may constrain provider selection and runtime admission but cannot make a program legal after runtime authority rejects it.
- replay metadata may classify an external effect but never authorizes re-submission.
- direct coherent output remains future-gated until CPU alias exclusion and symmetric external closure are proven.

## Runtime handoff

The compiler/runtime boundary passes immutable semantic requirements. At execution time HybridCPU ExternalRuntime obtains exact child/secure/mapping/device authority from SingNextOS and returns opaque generation-bound receipts.

A boolean such as `RequiresVirtualizedDomain` may remain a planning hint, but actual provider-effect admission must correlate with an exact virtual-domain context. The same applies to secure evidence requirements.

## Validation

Add compiler tests rejecting topology-bearing CXL fields in IR/lowering contracts, secure+virtual operations lowered without both semantic requirements, direct coherent required paths when only staged publication is admitted, unsafe replay of external effects, and bundles missing the required typed external-operation descriptor.

## Exit criteria

The compiler can express all required semantics without knowing whether the final provider is CXL, PCIe, host memory or another future substrate, and the runtime remains the sole source of exact authority.
