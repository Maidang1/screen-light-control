$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework 4.x is required.' }
$output = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /win32manifest:"$PSScriptRoot\app.manifest" /reference:System.Management.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /out:"$output\ScreenLight.exe" @sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'App.config') -Destination "$output\ScreenLight.exe.config"
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt') -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $output
Compress-Archive -LiteralPath "$output\ScreenLight.exe", "$output\ScreenLight.exe.config", "$output\THIRD-PARTY-NOTICES.txt", "$output\README.md" -DestinationPath "$output\ScreenLight-portable.zip" -Force
Write-Output "Built $output\ScreenLight.exe"
