param(
    [Parameter(Mandatory=$true)][string]$BuildDirectory,
    [string]$Config = "$PSScriptRoot\..\config\publish.local.json"
)
$ErrorActionPreference = 'Stop'
$settings = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json
$destination = [IO.Path]::GetFullPath($settings.publishDirectory)
if (!$destination.StartsWith('\\') -or $destination -match '\\SERVER\\') { throw 'Set a real UNC publishDirectory in config/publish.local.json.' }
$build = [IO.Path]::GetFullPath($BuildDirectory)
$active = Get-Content -LiteralPath "$build\active-release.json" -Raw | ConvertFrom-Json
if ($active.release -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$') { throw 'Invalid release.' }
$checksums = Get-Content -LiteralPath "$build\checksums.json" -Raw | ConvertFrom-Json
foreach ($entry in $checksums.PSObject.Properties) {
    $file = [IO.Path]::GetFullPath((Join-Path $build $entry.Name))
    if (!$file.StartsWith($build.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid checksum path.' }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Value) { throw "Build changed: $($entry.Name)" }
}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$lock = [IO.File]::Open((Join-Path $destination 'publish.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $releases = Join-Path $destination 'releases'
    New-Item -ItemType Directory -Path $releases -Force | Out-Null
    $final = Join-Path $releases $active.release
    if (Test-Path -LiteralPath $final) { throw 'This version already exists. Use a new version; published releases are immutable.' }
    # Не заменяем запущенный загрузчик. Его изменение требует отдельного окна обслуживания.
    $sameLauncherSource = (Test-Path -LiteralPath "$destination\launcher-source.sha256") -and ((Get-Content -LiteralPath "$destination\launcher-source.sha256" -Raw) -eq (Get-Content -LiteralPath "$build\launcher-source.sha256" -Raw))
    foreach ($exe in Get-ChildItem -LiteralPath $build -Filter '*.exe' -File) {
        $target = Join-Path $destination $exe.Name
        if ((Test-Path -LiteralPath $target) -and !$sameLauncherSource -and (Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $exe.FullName).Hash) {
            throw 'Launcher changed. Stop all exports and update root launcher EXEs in a maintenance window; then retry publishing.'
        }
    }
    $staging = Join-Path $releases ('.upload-' + [Guid]::NewGuid().ToString('N'))
    Copy-Item -LiteralPath "$build\releases\$($active.release)" -Destination $staging -Recurse
    foreach ($file in Get-ChildItem -LiteralPath $staging -File -Recurse) {
        $relative = $file.FullName.Substring($staging.Length + 1)
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path "$build\releases\$($active.release)" $relative)).Hash) { throw 'Upload verification failed.' }
    }
    Move-Item -LiteralPath $staging -Destination $final
    Get-ChildItem -LiteralPath $build -File | Where-Object { $_.Name -notin @('active-release.json','checksums.json') } | ForEach-Object {
        if (!($_.Extension -eq '.exe' -and (Test-Path -LiteralPath (Join-Path $destination $_.Name)))) {
            Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
        }
    }
    # Переключаем указатель последним: незавершённая загрузка не видна пользователям.
    $pointer = Join-Path $destination 'active-release.json'
    $tempPointer = Join-Path $destination ('active-release.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    Copy-Item -LiteralPath "$build\active-release.json" -Destination $tempPointer
    if (Test-Path -LiteralPath $pointer) { [IO.File]::Replace($tempPointer, $pointer, (Join-Path $destination ('previous-release.' + [Guid]::NewGuid().ToString('N') + '.json'))) }
    else { [IO.File]::Move($tempPointer, $pointer) }
    Write-Output "PUBLISHED: $destination ($($active.release))"
} finally { $lock.Dispose() }
