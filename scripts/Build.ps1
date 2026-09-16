param(
    [string]$Config = "$PSScriptRoot\..\config\publish.example.json",
    [string]$RuntimeConfig = "$PSScriptRoot\..\config\runtime.example.json"
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$settings = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json
$configDir = Split-Path ([IO.Path]::GetFullPath($Config))
if ($settings.version -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$') { throw 'Invalid release version.' }
$outputRoot = [IO.Path]::GetFullPath((Join-Path $configDir $settings.buildOutputDirectory))
$build = Join-Path $outputRoot ($settings.version + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$release = Join-Path $build ('releases\' + $settings.version)
New-Item -ItemType Directory -Path $release -Force | Out-Null
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (!(Test-Path (Join-Path $fw 'csc.exe'))) { $fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$csc = Join-Path $fw 'csc.exe'
if (!(Test-Path $csc)) { throw '.NET Framework 4.x C# compiler not found.' }
$references = @('/reference:System.dll','/reference:System.Xml.dll','/reference:System.Core.dll','/reference:System.Web.Extensions.dll',"/reference:$fw\System.IO.Compression.dll")
# Проверки входят в сборку: каталог не считается готовым релизом до их успешного завершения.
$coreSources = @(Get-ChildItem -LiteralPath (Join-Path $repo 'src\Core') -Filter '*.cs' | ForEach-Object FullName)
& $csc /nologo /target:library /optimize+ "/out:$release\ArtiosCAD-FE.Core.dll" @references @coreSources
if ($LASTEXITCODE) { throw 'Core compilation failed.' }
& $csc /nologo /target:winexe /optimize+ "/out:$release\ArtiosCAD-FE.exe" @references /reference:System.Windows.Forms.dll "/reference:$release\ArtiosCAD-FE.Core.dll" "$repo\src\Runtime\Program.cs"
if ($LASTEXITCODE) { throw 'Runtime compilation failed.' }
& $csc /nologo /target:winexe /optimize+ "/out:$build\Setup.exe" @references /reference:System.Windows.Forms.dll "$repo\src\Core\Storage.cs" "$repo\src\Launcher\Program.cs"
if ($LASTEXITCODE) { throw 'Launcher compilation failed.' }
# Старый csc вносит разные служебные идентификаторы при повторной компиляции.
# Сравниваем исходники загрузчика, чтобы обновление runtime не требовало замены работающих EXE.
$launcherSources = (Get-FileHash "$repo\src\Core\Storage.cs").Hash + (Get-FileHash "$repo\src\Launcher\Program.cs").Hash
[IO.File]::WriteAllText("$build\launcher-source.sha256", $launcherSources, [Text.Encoding]::ASCII)
foreach ($name in @('Begin-plotter','Finish-plotter','Begin-plotter-pdf','Finish-plotter-pdf','Begin-plotter-pdf-eps','Finish-plotter-pdf-eps','Reset','Diagnostics')) {
    Copy-Item -LiteralPath "$build\Setup.exe" -Destination "$build\$name.exe"
}
Copy-Item -LiteralPath $RuntimeConfig -Destination "$release\runtime.json"
Copy-Item -LiteralPath "$repo\templates\prototype-outputs.xml" -Destination $release
Copy-Item -LiteralPath "$repo\docs\PILOT_RU.md" -Destination $build
[IO.File]::WriteAllText("$build\active-release.json", (@{release=$settings.version} | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
foreach ($pair in @(@('Reset Pilot.cmd','Reset.exe'),@('Open Diagnostics.cmd','Diagnostics.exe'),@('Configure.cmd','Setup.exe'))) {
    [IO.File]::WriteAllText((Join-Path $build $pair[0]), ('@echo off' + "`r`n" + 'start "" /wait "%~dp0' + $pair[1] + '"' + "`r`n"), [Text.Encoding]::ASCII)
}
$testDir = Join-Path $build 'checks'
New-Item -ItemType Directory $testDir | Out-Null
Copy-Item -LiteralPath "$release\ArtiosCAD-FE.Core.dll" -Destination $testDir
& $csc /nologo /target:exe "/out:$testDir\ArtiosCADMock.exe" "$repo\tests\ArtiosCADMock.cs"
if ($LASTEXITCODE) { throw 'Mock compilation failed.' }
& $csc /nologo /target:exe "/out:$testDir\Tests.exe" @references "/reference:$release\ArtiosCAD-FE.Core.dll" "$repo\tests\Tests.cs"
if ($LASTEXITCODE) { throw 'Tests compilation failed.' }
& "$testDir\Tests.exe" "$repo\templates\prototype-outputs.xml"
if ($LASTEXITCODE) { throw 'Tests failed. Release must not be published.' }
$hashes = @{}
Get-ChildItem -LiteralPath $build -File -Recurse | Where-Object { !$_.FullName.StartsWith($testDir + '\') } | ForEach-Object {
    $relative = $_.FullName.Substring($build.Length + 1)
    $hashes[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
}
[IO.File]::WriteAllText("$build\checksums.json", ($hashes | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
# В архив попадают только runtime и файлы запуска. Синтетические проверки и их данные остаются локально.
Add-Type -AssemblyName System.IO.Compression
$zipPath = $build + '.zip'
$zipStream = [IO.File]::Create($zipPath)
$zip = New-Object IO.Compression.ZipArchive($zipStream, ([IO.Compression.ZipArchiveMode]::Create))
try {
    Get-ChildItem -LiteralPath $build -File -Recurse | Where-Object { !$_.FullName.StartsWith($testDir + '\') } | ForEach-Object {
        $entry = $zip.CreateEntry($_.FullName.Substring($build.Length + 1).Replace('\','/'))
        $input = [IO.File]::OpenRead($_.FullName)
        $output = $entry.Open()
        try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
    }
} finally { $zip.Dispose(); $zipStream.Dispose() }
[IO.File]::WriteAllText((Join-Path $outputRoot 'latest-build.txt'), $build, (New-Object Text.UTF8Encoding($false)))
Write-Output "BUILD_OK: $build"
Write-Output "ZIP: $zipPath"
