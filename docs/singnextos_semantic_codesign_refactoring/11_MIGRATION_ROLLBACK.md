# Migration and Rollback Strategy

1. Land semantic vocabulary and pure evaluators with all gates OFF.
2. Add shadow computation: build obligations/guarantees and record mismatch telemetry without affecting current path.
3. Add exact binding in parallel with current external-operation records; no behavior change.
4. Enable admission sentry only for host/fake provider conformance tests.
5. Enable one staged HybridCPU MatrixMultiply contour after exact cross-repo qualification.
6. Keep current Level-1 adapter as fallback only if fallback still independently satisfies the original operation contract; never silently downgrade mandatory obligations.
7. Rollback disables the new feature gate and stops creating new bindings. Existing submitted operations complete under the contract version they were admitted with; rollback must not reinterpret in-flight state.
8. Remove compatibility fields only after source search + API baseline + telemetry prove no live consumers.
9. Package/source drift automatically demotes qualification until re-audited.
