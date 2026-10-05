param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$regentRoot = Split-Path $PSScriptRoot -Parent
Push-Location $regentRoot
try {
    dotnet run --project tests/DisplayRegent.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Release tests failed' }
    dotnet publish src/DisplayRegent/DisplayRegent.csproj -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o "artifacts/$Runtime"
    if ($LASTEXITCODE -ne 0) { throw 'App build failed' }
    Copy-Item -LiteralPath LICENSE -Destination "artifacts/$Runtime/LICENSE.txt"
    Copy-Item -LiteralPath docs/QUICKSTART.md -Destination "artifacts/$Runtime/QUICKSTART.txt"
    Compress-Archive -Path "artifacts/$Runtime/*" -DestinationPath artifacts/payload.zip -Force
    Copy-Item -LiteralPath artifacts/payload.zip -Destination "artifacts/DisplayRegent-0.1.2-preview-$Runtime-Portable.zip"
    Copy-Item -LiteralPath "artifacts/$Runtime/DisplayRegent.exe" -Destination "artifacts/DisplayRegent-0.1.2-preview-$Runtime-Setup.exe"
    Get-ChildItem artifacts -File | Where-Object Name -Match "^DisplayRegent-0.1.2-preview-.*(Setup\.exe|Portable\.zip)$" | ForEach-Object { $regentHash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLower(); "$regentHash  $($_.Name)" } | Set-Content -Encoding ascii artifacts/SHA256SUMS.txt
} finally { Pop-Location }
