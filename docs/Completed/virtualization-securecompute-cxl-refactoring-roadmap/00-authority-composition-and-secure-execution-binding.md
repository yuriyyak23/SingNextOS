# 00. Authority Composition And SecureExecutionBinding

## Problem

SingNextOS already has strong `VirtualDomain` and `SecureDomain` contours, but they are parallel. The kernel lacks one exact relation proving that a specific secure domain protects a specific virtual child execution context.

## Decision

Introduce an internal kernel-owned `SecureExecutionBinding` relation. It references exact current identities/generations for parent platform domain binding, `VirtualDomainHandle`, provider child-domain binding when present, `SecureDomainHandle`, secure provider lease, and secure policy/protection generation.

It is a composition object, not a new authority root. Creation requires both existing local capabilities and the external provider's positive ProductionSecure contract.

## Required rules

- exact parent/domain/generation equality before every provider effect and publication;
- child and secure authority may not amplify the parent;
- evidence records cannot create this binding;
- no implicit binding from VMX state, CXL features, IDE/TSP evidence or compiler metadata;
- failure after partial external materialization must close exactly or quarantine the binding;
- destroying either participating domain must first drain dependent composed bindings.

## API direction

Prefer narrow kernel operations such as `BindSecureExecution(virtualDomain, secureDomain, ...)` and `CloseSecureExecution(binding)`. Do not expose raw platform/provider handles to SIPs.

## Exit criteria

- exact stale generation tests for both virtual and secure domains;
- parent mismatch cannot compose;
- ambiguous external bind/close pins authority and blocks process reclaim;
- process teardown drains composed bindings before standalone secure/virtual domain closure.
