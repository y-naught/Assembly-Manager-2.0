[CmdletBinding()]
param(
    [string]$YakPath = 'C:\Program Files\Rhino 8\System\Yak.exe',
    [ValidateSet('any', 'win', 'mac')][string]$Platform = 'any',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$pluginRoot = Join-Path $repoRoot 'AssemblyManagerPlugin'
$projectPath = Join-Path $pluginRoot 'AssemblyManagerPlugin.csproj'
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') { throw 'Expected an explicit semantic package version.' }
$manifestPath = Join-Path $pluginRoot 'manifest.yml'
$manifest = Get-Content -LiteralPath $manifestPath -Raw
if ($manifest -notmatch "(?m)^version:\s*$([regex]::Escape($version))\s*$") { throw 'Project and manifest versions must match.' }
$packageRoot = Join-Path $pluginRoot "dist/Gazelle-$version-yak"
if (Test-Path -LiteralPath $packageRoot) {
    if (Get-ChildItem -LiteralPath $packageRoot -Filter '*.yak' -File) {
        throw "A package already exists in $packageRoot. Preserve released artifacts; use a new version or an explicitly reviewed clean staging directory."
    }
}
if (-not (Test-Path -LiteralPath $YakPath -PathType Leaf)) { throw "Yak not found: $YakPath" }
if (-not $SkipBuild) {
    & dotnet build $projectPath -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
}
$buildRoot = Join-Path $pluginRoot 'bin/Release/net7.0'
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $buildRoot 'Gazelle.rhp')).Version
if ($assemblyVersion.ToString(3) -ne ($version -split '-')[0]) { throw 'Release binary version does not match the manifest.' }

New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
$expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
function Copy-PackageFile([string]$source, [string]$relative) {
    $destination = Join-Path $packageRoot $relative
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination
    [void]$expected.Add($relative.Replace('\', '/'))
}
foreach ($name in @('Gazelle.rhp', 'Gazelle.deps.json', 'Gazelle.runtimeconfig.json')) {
    Copy-PackageFile (Join-Path $buildRoot $name) $name
}
foreach ($name in @('manifest.yml', 'README.md', 'CHANGELOG.md')) {
    Copy-PackageFile (Join-Path $pluginRoot $name) $name
}
Copy-PackageFile (Join-Path $pluginRoot 'dist/Gazelle-1.0.3-yak/icon.png') 'icon.png'
foreach ($folder in @('Docs', 'Samples')) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $pluginRoot $folder) -File -Recurse) {
        Copy-PackageFile $file.FullName ([IO.Path]::GetRelativePath($pluginRoot, $file.FullName))
    }
}

# Keep operator-to-operator links local. Technical references not included in the
# runtime package point to the matching source tag, without shipping source/tests.
$sourceBase = "https://github.com/y-naught/Assembly-Manager-2.0/blob/v$version/"
foreach ($relative in @($expected | Where-Object { $_.EndsWith('.md') })) {
    $packagedFile = Join-Path $packageRoot $relative
    $originalFile = Join-Path $pluginRoot $relative
    $content = Get-Content -LiteralPath $packagedFile -Raw
    $content = [regex]::Replace($content, '(\[[^\]]*\]\()([^\)]+)(\))', {
        param($match)
        $target = $match.Groups[2].Value
        if ($target -match '^(?:[a-z]+:|#)') { return $match.Value }
        $parts = $target -split '#', 2
        $packageTarget = [IO.Path]::GetFullPath((Join-Path (Split-Path $packagedFile -Parent) $parts[0]))
        if ($packageTarget.StartsWith($packageRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath $packageTarget)) { return $match.Value }
        $sourceTarget = [IO.Path]::GetFullPath((Join-Path (Split-Path $originalFile -Parent) $parts[0]))
        if (-not $sourceTarget.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $sourceTarget)) { throw "Unresolved package documentation link: $relative -> $target" }
        $sourceRelative = [IO.Path]::GetRelativePath($repoRoot, $sourceTarget).Replace('\', '/')
        $anchor = if ($parts.Length -gt 1) { '#' + $parts[1] } else { '' }
        return $match.Groups[1].Value + $sourceBase + $sourceRelative + $anchor + $match.Groups[3].Value
    })
    [IO.File]::WriteAllText($packagedFile, $content, [Text.UTF8Encoding]::new($false))
}
foreach ($file in Get-ChildItem -LiteralPath $packageRoot -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath($packageRoot, $file.FullName).Replace('\', '/')
    if (-not $expected.Contains($relative)) { throw "Unexpected file in package staging: $relative" }
}
Push-Location $packageRoot
try {
    & $YakPath build --platform $Platform
    if ($LASTEXITCODE -ne 0) { throw 'Yak build failed.' }
} finally { Pop-Location }
$packages = @(Get-ChildItem -LiteralPath $packageRoot -Filter '*.yak' -File)
if ($packages.Count -ne 1) { throw 'Expected exactly one generated Yak package.' }
$package = $packages[0]
$hash = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($package.FullName + '.sha256', "$hash  $($package.Name)`n", [Text.UTF8Encoding]::new($false))
Write-Output "Package: $($package.FullName)"
Write-Output "SHA256: $hash"
