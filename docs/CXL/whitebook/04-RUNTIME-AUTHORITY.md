# Runtime authority после handoff

`HybridBootInfoImporter` отделяет firmware evidence от runtime authority.
`KernelOwnedPhysicalBootInfoReader` разрешает copy только из normal RAM, с range,
image-overlap и epoch checks. `HybridBootAuthorityAdmission` затем проверяет
fresh endpoint через `CxlAuthorityBridge`; quarantined firmware aperture не
принимается.

Runtime CXL decomposed на discovery, I/O, fabric, Type-3 memory, coherent access,
security и Fabric Manager provider roles. `RegionAuthority` остаётся корнем
memory ownership. Type-3 backing удерживает exact backing lease и cleanup facts;
health evidence не меняет Region само по себе. Type-2 path staged by default.

`DirectCoherentWrite`, `SharedReadMostly`, distributed writable multi-host и
physical production security не становятся доступными от одного capability flag.
Local fence или lost host сохраняет original uses/charges до original remote
closure; model provider не является hardware qualification.

Подробности: [runtime seam map](../cxl-refactoring-roadmap/README.md).
