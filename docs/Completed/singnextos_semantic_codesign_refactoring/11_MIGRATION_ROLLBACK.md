# Migration and rollback

1. New semantic co-design gates remain OFF by default; current `VNextFeatureGates.IsEnabled` is hard-false at baseline.
2. Land descriptors/evaluators first; then host/fake shadow comparison; then exact HybridCPU provider adapter; then one staged MatrixMultiply contour.
3. Enabling a gate changes admission for **new** operation generations only. Existing in-flight binding is never reinterpreted under a newer contract.
4. Rollback disables new binds, drains/reconciles current-generation operations, keeps quarantined resources until closure/settlement proof, then removes optional adapter paths.
5. Source/package/API drift automatically demotes qualification until P18 reruns exact evidence.
6. No rollback path serializes or resurrects ephemeral authority.
