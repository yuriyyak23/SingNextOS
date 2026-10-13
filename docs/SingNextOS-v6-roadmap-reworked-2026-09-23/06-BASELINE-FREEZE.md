# Baseline observation и strict freeze

Текущий working-tree observation находится в [41](41-CURRENT-STATE-AND-OPEN-SLICES.md). Нельзя подменять dirty source tuple одним Git HEAD, старым package version или SHA репозитория HybridCPU без metadata. Package presence не adapter qualification.

Research/dev использует [42](42-RESEARCH-DEV-VERIFICATION.md): selected relevant inputs/diff и resolved dependency/toolchain identity, без mandatory полного inventory каждого документа. Перечитывать affected files/diff перед записью; не менять tested inputs при живом процессе.

Strict checkpoint eng/v6/Freeze-V6Baseline.ps1 сохраняет прежнюю semantics, включая manifest validation. Запускать для настоящего freeze/qualification; перед этим явно актуализировать manifest. Изменение source/dependency/toolchain в dev требует affected verification и attribution по modernize/03, а full freeze — только при применимом qualification/claim требовании или конкретном unresolved integrity risk. Historical manifests и failed runs не выдавать за current verification. Documentation-only изменения не требуют P00/build/restore.

Оригинальные freeze records 2026-09-23/09-30 доступны в artifacts/v6/iteration-20261004-roadmap-dev-workflow/history/06-BASELINE-FREEZE.md. Они не current facts и не разрешение gates.
