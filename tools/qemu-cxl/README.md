# QEMU CXL tools

Canonical documentation lives in **[docs/QEMU_CXL](../../docs/QEMU_CXL/README.md)**.
Scripts/config stay here; VM artifacts/private VARS stay in `CXL/`.

* [Environment and commands](../../docs/QEMU_CXL/ENVIRONMENT-RU.md)
* [Current evidence and limitations](../../docs/QEMU_CXL/CURRENT-STATE-RU.md)
* [Modernization plan](../../docs/QEMU_CXL/MODERNIZATION-PLAN-RU.md)
* [Continuation prompt](../../docs/QEMU_CXL/CONTINUATION-PROMPT-RU.md)

Use explicit NativeWindowsRam with the installed Windows build. RAM-backed
persistent-memdev is volatile. Standalone EFI verifier success is not full SingBoot
boot, production isolation or RuntimeKernel execution.

The original infrastructure guide remains available in [history](../../docs/QEMU_CXL/history/README.md).
