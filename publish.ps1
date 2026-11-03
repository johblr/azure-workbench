param(
    [string]$Runtime,
    [string]$Output
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# Usage: ./publish.ps1 [-Runtime <rid>] [-Output <dir>]
# Runtime defaults to the current platform and architecture.
# Output defaults to artifacts/publish/<runtime>, relative to this script.

if (-not $Runtime) {
    if ($env:OS -eq 'Windows_NT') {
        if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') {
            $Runtime = 'win-arm64'
        } else {
            $Runtime = 'win-x64'
        }
    } else {
        $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
        $archName = switch ($arch) {
            ([System.Runtime.InteropServices.Architecture]::X64)   { 'x64' }
            ([System.Runtime.InteropServices.Architecture]::Arm64) { 'arm64' }
            default { throw "Unsupported architecture: $arch" }
        }

        if ($IsLinux) {
            $Runtime = "linux-$archName"
        } elseif ($IsMacOS) {
            $Runtime = "osx-$archName"
        } else {
            throw 'Unsupported operating system.'
        }
    }
}

if (-not $Output) {
    $Output = Join-Path 'artifacts/publish' $Runtime
}

$outputFull = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)

Write-Host "Publishing AzureWorkbench for $Runtime to $outputFull"
dotnet publish src/AzureWorkbench `
    -c Release `
    -r $Runtime `
    -o $outputFull

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Write-Host "Output: $outputFull"
