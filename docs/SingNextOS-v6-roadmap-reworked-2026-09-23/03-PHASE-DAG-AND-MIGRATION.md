# Corrected Phase DAG and Migration

## 1. Key change

The phase numbers are retained for traceability, but execution is not strictly numeric. P05 is a cross-cutting refinement spine, while optional contours are removed from the critical path.

## 2. Dependency graph

```text
P00 Baseline/owner freeze
  |
  +--> C0 Contract extension/canonicalization (07)
  |      |
  |      +--> P01 Memory semantics --------+
  |      |                                  |
  |      |                                  +--> P04 Translation/DMA ----+
  |      |                                  |                            |
  |      |                                  |                            +--> QV1 first qualification vertical
  |      |                                  |                            |
  |      |                                  +--> P02 Durability          +--> P08 Preemption/resume
  |      |                                  |                            |
  |      |                                  +--> P10 RAS ----------------+
  |      |
  |      +--> P03 Temporal/resource semantics --> P08
  |      |                                      --> P12 Energy (optional)
  |      |
  |      +--> P05 Formal/refinement spine (starts early; closes per contour)
  |
  +--> P09 Trust/attestation (after device/provider generation vocabulary; optional until secure-device contour)
  +--> P07 Locality/data motion (after P01/P04 resource semantics; optional/advisory)
  +--> P06 IFC (after P01; protected-contour optional)

P01 + P04 + P05 + P10 (+ P02 when persistent) --> P11 Multi-host (FutureGated)
P05 stable lowering semantics -----------------> PCL (optional optimization track)
```

## 3. Dependency classification

| Phase | Hard dependencies | Soft dependencies | Parallelizable | Critical path? |
|---|---|---|---|---|
| P01 memory | P00, C0 | P05 model skeleton | with P03 | YES |
| P04 translation/DMA | P01, C0 | P05 | with P02 after P01 | YES for DMA vertical |
| P05 refinement | C0; contour-specific semantics as they stabilize | all phases feed it | cross-cutting | YES as evidence spine, not monolithic phase |
| P02 durability | P01; P05 memory/visibility model | P10 | with P03/P04 | NO for first vertical |
| P03 temporal | P00, C0, existing budget algebra | P05 numeric model | with P01 | NO for first vertical unless selected |
| P08 preemption | P03 for temporal guarantees; P04 for DMA/device contours; P05 failure model | P10 | after first vertical | NO initially |
| P10 RAS | P01, C0; P04 for device/DMA consequences | P02 for persistent contour | with P03 | YES before multi-host; not first vertical |
| P09 trust | provider/device generation vocabulary | P04, P10, SecureCompute | optional | NO |
| P07 locality | P01, resource accounting; P04 for data motion | P12 | optional | NO |
| P12 energy | P03/resource algebra | P07 | optional | NO |
| P06 IFC | P01 | SecureCompute, P05 | optional | NO |
| PCL | stable P05 clauses + compiler contract | P01/P08 safe points | optional | NO |
| P11 multi-host | P01, P04, P05, P10; P02 if persistent | P09 | last-wave | NO single-host |

## 4. Migration rules

1. Existing V1/staged path remains the fallback and compatibility oracle.
2. New contracts are additive sidecars until a breaking V2 need is proven.
3. Every gate is OFF by default and enabled per contour.
4. Shared mutable/atomic direct paths stay OFF until P01+P04+P05 evidence closes that exact path.
5. Durable output stays staged until P02 provider-specific qualification.
6. Guaranteed deadline stays OFF until P03 + provider enforcement + schedulability evidence.
7. Stateful resume stays OFF until P08 capture/resume state is executable for the named provider.
8. Multi-host stays OFF until single-host reclaim/failure semantics are closed.
9. Rollback always performs fresh admission on the fallback path.

## 5. Critical-path definition

The minimum v6 architecture proof is not “all P01–P12 complete”. It is:

```text
P00 + C0 + P01 + P04 + minimal P05
+ one staged heterogeneous executable vertical
+ qualification/negative/race/fault evidence
```

This closes the architecture before optional persistence, IFC, trust, locality, energy and multi-host features expand scope.
