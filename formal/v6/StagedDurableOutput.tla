-------------------------- MODULE StagedDurableOutput --------------------------
EXTENDS Naturals

(***************************************************************************
Safety model for one named managed provider.  The persistence receipt is
evidence only.  Publication remains a separate owner action, and recovery
correlation cannot restore runtime authority without fresh admission.
***************************************************************************)

VARIABLES phase, dataPersisted, metadataPersisted, durable, published,
          crashed, recoveryRecord, freshAdmission

vars == << phase, dataPersisted, metadataPersisted, durable, published,
           crashed, recoveryRecord, freshAdmission >>

Init ==
    /\ phase = "Empty"
    /\ dataPersisted = FALSE /\ metadataPersisted = FALSE
    /\ durable = FALSE /\ published = FALSE /\ crashed = FALSE
    /\ recoveryRecord = FALSE /\ freshAdmission = FALSE

WriteData ==
    /\ phase = "Empty" /\ phase' = "DataWritten"
    /\ UNCHANGED << dataPersisted, metadataPersisted, durable, published,
                    crashed, recoveryRecord, freshAdmission >>

PersistData ==
    /\ phase = "DataWritten" /\ dataPersisted' = TRUE
    /\ phase' = "DataPersisted"
    /\ UNCHANGED << metadataPersisted, durable, published, crashed,
                    recoveryRecord, freshAdmission >>

WriteMetadata ==
    /\ phase = "DataPersisted" /\ phase' = "MetadataWritten"
    /\ UNCHANGED << dataPersisted, metadataPersisted, durable, published,
                    crashed, recoveryRecord, freshAdmission >>

PersistMetadata ==
    /\ phase = "MetadataWritten" /\ metadataPersisted' = TRUE
    /\ phase' = "MetadataPersisted"
    /\ UNCHANGED << dataPersisted, durable, published, crashed,
                    recoveryRecord, freshAdmission >>

CommitDurable ==
    /\ phase = "MetadataPersisted" /\ dataPersisted /\ metadataPersisted
    /\ durable' = TRUE /\ phase' = "Durable"
    /\ UNCHANGED << dataPersisted, metadataPersisted, published, crashed,
                    recoveryRecord, freshAdmission >>

Publish ==
    /\ phase = "Durable" /\ durable /\ published' = TRUE
    /\ phase' = "Published"
    /\ UNCHANGED << dataPersisted, metadataPersisted, durable, crashed,
                    recoveryRecord, freshAdmission >>

Crash ==
    /\ ~crashed /\ crashed' = TRUE /\ phase' = "Crashed"
    /\ UNCHANGED << dataPersisted, metadataPersisted, durable, published,
                    recoveryRecord, freshAdmission >>

Recover ==
    /\ phase = "Crashed" /\ recoveryRecord' = durable
    /\ freshAdmission' = FALSE /\ phase' = "Recovered"
    /\ UNCHANGED << dataPersisted, metadataPersisted, durable, published, crashed >>

FreshAdmit ==
    /\ phase = "Recovered" /\ recoveryRecord /\ freshAdmission' = TRUE
    /\ UNCHANGED << phase, dataPersisted, metadataPersisted, durable,
                    published, crashed, recoveryRecord >>

Next == WriteData \/ PersistData \/ WriteMetadata \/ PersistMetadata \/
        CommitDurable \/ Publish \/ Crash \/ Recover \/ FreshAdmit

Spec == Init /\ [][Next]_vars
TypeOK == phase \in {"Empty", "DataWritten", "DataPersisted", "MetadataWritten",
                     "MetadataPersisted", "Durable", "Published", "Crashed", "Recovered"}
PublishedOnlyAfterDurable == published => durable
DurableRequiresOrderedPersistence == durable => (dataPersisted /\ metadataPersisted)
RecoveryNeverRestoresAuthority == recoveryRecord => ~freshAdmission \/ phase = "Recovered"

=============================================================================
