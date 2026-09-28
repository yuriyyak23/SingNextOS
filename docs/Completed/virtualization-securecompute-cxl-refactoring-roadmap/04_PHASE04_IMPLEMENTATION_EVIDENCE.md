# Phase 04 implementation evidence: fault, teardown, and reclaim

## Ordering and ownership

Process teardown now creates its local admission-stop record and revokes local
admission authority before calling external providers.  Provider calls execute
without `_platformMemoryUseGate`.  The composed close order is:

1. Type-2 and other registered composed provider work;
2. secure guest overlays;
3. Virtual I/O;
4. guest mappings;
5. SecureExecution bindings;
6. SecureDomain roots;
7. VirtualDomain children and their platform parent where owned;
8. existing CXL/external-operation/device/mapping teardown;
9. local process and region reclaim.

Virtual-domain destruction follows the same dependency direction and closes
its guest mappings instead of requiring a caller to reclaim them first.

## Ambiguity and recovery

Ambiguous provider outcomes retain a live/quarantined registry record and block
lower authority reclaim.  `ProviderUnavailable`, disconnect, timeout, stale or
not-found responses are not treated as terminal closure.  A later teardown or
explicit close may retry the exact provider identity.  Secure-region and
Virtual-I/O closure retries preserve the provider lease; exact terminal closure
removes the corresponding pin once, while repeated local close is idempotent.

The process teardown snapshot records `PlatformFaulted` even when failure occurs
during the composed pre-close stage.  A later teardown call may retry exact
closure rather than treating that snapshot as containment.  Stale mapping
entries are ignored only when the kernel's own tracking ledger proves that the
exact mapping was already finalized by the composed guest close path.

## Audit corrections

- moved composed provider close calls out of the global platform-memory lock;
- closed Type-2, overlays, Virtual I/O, and guest mappings before authority
  roots;
- added persistent virtual-compute dependency pins;
- made secure-overlay and Virtual-I/O quarantines retryable by exact identity;
- retained reclaim blocks for live/quarantined secure execution and provider
  effects;
- updated virtualization regressions for automatic dependency-ordered destroy.

## Boundary

No Phase 05 promotion or exit work was performed.  This evidence does not claim
ProductionSecure status, production CXL transport support, confidential
migration, multi-host writable secure memory, or production security.

