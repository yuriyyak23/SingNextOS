# Optional Pester adapter; no module installation required.
Describe 'CXL infrastructure integration' {
    It 'passes isolated storage, safety and QEMU smoke checks' {
        { & "$PSScriptRoot/Test-Infrastructure.ps1" } | Should Not Throw
    }
    It 'runs explicit RAM persistent-region profile across two cold starts' {
        { & "$PSScriptRoot/Test-NativeWindowsRam.ps1" -BootSeconds 1 } | Should Not Throw
    }
}

