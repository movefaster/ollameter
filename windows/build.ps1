# Builds a self-contained Ollameter.exe that needs no .NET install, and zips it. The exe isn't compressed: a
# compressed one unpacks its assemblies into memory, which cost about 56 MB of private memory for 70 MB of disk.
# Usage: windows/build.ps1 [-Version 0.4.0] [-Runtime win-x64|win-arm64] [-Label name-in-zip]
param(
    [string]$Version = "0.0.0",
    [string]$Runtime = "win-x64",
    [string]$Label = $Version
)
$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    $output = Join-Path $PSScriptRoot "build/$Runtime"
    if (Test-Path $output) { Remove-Item -Recurse -Force $output }

    dotnet publish Ollameter/Ollameter.csproj -c Release -r $Runtime --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false `
        -p:DebugType=none -p:Version=$Version -o $output
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

    $architecture = $Runtime -replace "^win-", ""
    $zip = Join-Path $PSScriptRoot "build/Ollameter-$Label-windows-$architecture.zip"
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Compress-Archive -Path (Join-Path $output "Ollameter.exe") -DestinationPath $zip
    Write-Output "Built $zip"
}
finally {
    Pop-Location
}
