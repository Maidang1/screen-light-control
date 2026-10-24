param([switch]$Integration)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path $PSScriptRoot 'test-output'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
& $compiler /nologo /target:exe /main:ScreenLight.Tests /utf8output /win32manifest:"$PSScriptRoot\app.manifest" /reference:System.Management.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /out:"$output\Tests.exe" @sources "$PSScriptRoot\tests\Tests.cs"
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
Copy-Item -LiteralPath "$PSScriptRoot\App.config" -Destination "$output\Tests.exe.config"
if ($Integration) { & "$output\Tests.exe" $output --integration } else { & "$output\Tests.exe" $output }
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
