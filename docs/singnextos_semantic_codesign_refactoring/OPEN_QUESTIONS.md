# Open Research Questions

Only questions that remain genuinely contingent on provider implementation are left open:

1. Which HybridCPU execution classes can provide a measured/enforced maximum non-preemptible interval rather than merely a scheduler intent?
2. Can a provider-level `CloseEpoch` prove “no future external effect” across Lane6/Lane7 external accelerators without a hardware-specific reset/drain primitive, and what exact trust boundary signs that closure?
3. Which resource dimensions can be enforced as upper bounds versus only measured/accounted, especially SMT-shared compute, memory bandwidth and provider overhead?
4. Can direct-coherent output achieve Level 3 with robust alias exclusion using existing runtime/IOMMU/mapping controls, or must it remain gated?
5. What minimum provider evidence is trustworthy for authenticated remote/attested providers, and which fault model is supported beyond crash/omission?
6. Does the product security thesis require information-flow control beyond capability read/write authority? Default decision: defer IFC unless a concrete confidentiality policy requires cross-domain flow constraints.
7. Which shared-mutable contour justifies RegionAuthority mutation/atomic-operation delegation without creating an unusable lowest-common-denominator model?
8. What bounded quarantine/reconciliation SLO is acceptable for provider partitions where no definitive containment proof is available?
