namespace YAKSys_Hybrid_CPU.Core;

[Flags]
public enum NeutralMemoryAccess { None = 0, Read = 1, Write = 2 }
public enum NeutralMemoryCoherenceModel { NonCoherent, Coherent }
public enum NeutralMemoryVisibilityRequirement { CoherentAccess, PublicationFence, CacheMaintenance }
public enum NeutralMemoryVisibilityOutcome { Coherent, PublicationFenceSatisfied, CacheMaintenanceSatisfied, Unsupported }
public enum NeutralMemoryAcquireRequirement { AcquisitionFence }
public enum NeutralMemoryAcquireOutcome { AcquisitionFenceSatisfied, Unsupported }
