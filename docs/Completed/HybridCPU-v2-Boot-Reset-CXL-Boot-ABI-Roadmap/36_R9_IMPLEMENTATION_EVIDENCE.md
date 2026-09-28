# R9 Implementation Evidence — A/B, Anti-Rollback, Recovery

Baseline HEAD `472b7c9345605f1558958f0d00e4e2f1b4177fa4`; dirty worktree preserved. Existing R3 protected-state model, semantic selector, and recovery contracts were audited.

Implemented a deterministic A/B update and recovery model: inactive payload is persisted first, read back and SHA-384 checked, manifest is persisted last, and only then is a bounded protected trial record published. Confirmation is authenticated by exact image ID, image generation, and nonce. The rollback floor is separate per rollback domain and advances only after successful confirmation. Trial attempts are bounded; selection falls back trial → confirmed → valid replica → signed local recovery → halt.

Power loss is injected by deterministic persistence-barrier ordinal, not wall-clock timing. Tests interrupt after each of four barriers and prove that neither the confirmed image nor signed recovery route is lost. A partial/bad payload cannot publish a manifest or trial. Persistent CXL media does not own confirmed/trial choice or the only rollback floor.

Classification is `ModelOnly`. The in-memory store is explicitly not production OTP/NVRAM. Production atomicity, monotonicity, authenticated OS confirmation, and recovery storage are `FutureGatedRequiresHardware`, owned by platform security/protected-store firmware; missing prerequisites are a qualified atomic monotonic store, hardware trust root, power-loss contract, and authenticated confirmation channel.

Qualification: focused 7/0; adapter regression 65/0; solution build 0 warnings/errors; full tests adapter 65/0, neutral 58/0, platform 60/0, main 1205 passed/0 failed/2 skipped. Changed: `Boot/AbRecoveryModel.cs`, `AbRecoveryModelTests.cs`, this evidence and traceability matrix. Claim level: `ModelValidated`. No HybridCPU core/ISE/ISA/compiler/architecture implementation changed.

