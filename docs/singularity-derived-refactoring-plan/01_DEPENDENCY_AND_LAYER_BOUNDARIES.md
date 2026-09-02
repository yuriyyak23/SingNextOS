# Phase 1 — Dependency and layer boundaries

## Goal

Make the strongest structural lesson from `base/Interfaces`, `base/Kernel`, `base/Libraries` and `base/Drivers` mechanically enforceable in SingNextOS.

## Target layers

```text
Applications
    ↓
Native Libraries / SDK / compatibility personalities
    ↓
Generated SIP Contracts + public resource abstractions
    ↓
Isolated Services / Drivers
    ↓
Minimal privileged Sing runtime/kernel
    ↓
Platform Authority Bridge
    ↓
HybridCPU neutral providers
```

## Required dependency rules

- contract assemblies MUST NOT reference kernel/runtime implementations;
- public SIP contracts MUST NOT reference platform-provider implementations;
- libraries MAY reference public contracts but MUST NOT reference bridge-private lease types;
- applications MUST NOT reference HAL/platform-provider assemblies;
- drivers/services MUST receive authority through capabilities/resources, not by referencing privileged globals;
- provider adapters MAY reference HybridCPU neutral facade types, but those types MUST NOT leak upward through public contracts;
- compatibility personalities MUST depend on native contracts, never become an authority root.

## Architecture tests

Add automated dependency/conformance tests that fail when:

- `Contracts` references runtime internals;
- a SIP DTO contains provider lease/domain/IOMMU/physical-address/raw-lane/raw-opcode types;
- a driver references forbidden privileged implementation namespaces;
- a source-facing library invokes platform-provider APIs directly;
- a compatibility package has a privileged dependency unavailable to the equivalent native service.

Use namespace/assembly policy that matches the actual repository layout; do not force a directory rename merely to resemble the historical tree.

## Migration strategy

1. inventory current project references;
2. classify each project as contract, privileged mechanism, service/driver, library/SDK, provider adapter or test;
3. document temporary exceptions;
4. add architecture tests in warning/report mode if needed;
5. remove exceptions incrementally;
6. make violations CI-failing once current known debt is cleared.

## Acceptance criteria

A contributor must be unable to accidentally create a new dependency path from application/library/SIP code to raw HybridCPU/provider authority without a failing architecture test.
