param([switch]$BuildOnly, [switch]$Run)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location -LiteralPath $repoRoot
$project = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'PaperTodo.csproj') -Raw)
$version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0].Trim()
$mobileSpec = Get-Content -LiteralPath (Join-Path $repoRoot 'mobile\pubspec.yaml') -Raw
$mobileVersion = [regex]::Match($mobileSpec, '(?m)^version:\s*([^+\s]+)').Groups[1].Value
if ($mobileVersion -ne $version) { throw 'Windows and Android product versions must match.' }
if ($BuildOnly) {
    dotnet build PaperTodo.csproj -c Release
    exit $LASTEXITCODE
}

$outputRoot = Join-Path $repoRoot '输出'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$staging = Join-Path $outputRoot ('publish-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
$packageDir = Join-Path $repoRoot "dist\PaperTodo-v$version"
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null
$artifactName = "PaperTodo-v$version-win-x64-self-contained-small.exe"

dotnet publish PaperTodo.csproj -c Release -r win-x64 --self-contained true -o $staging `
    /p:PublishSingleFile=true /p:PublishReadyToRun=false /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=true /p:PublishTrimmed=false /p:DebugType=none /p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "Publish failed. Diagnostic output remains in $staging" }
$artifact = Join-Path $packageDir $artifactName
Copy-Item -LiteralPath (Join-Path $staging 'PaperTodo.exe') -Destination $artifact -Force
$checksum = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath (Join-Path $packageDir 'SHA256SUMS.txt') -Value "$checksum  $artifactName" -Encoding utf8
$commit = (git rev-parse HEAD).Trim()
$workingChanges = @(git status --porcelain)
$sourceFiles = @(git ls-files --cached --others --exclude-standard | Sort-Object -Unique)
$sourceHashes = foreach ($relative in $sourceFiles) {
    $path = Join-Path $repoRoot $relative
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        "$((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant())  $relative"
    }
}
Set-Content -LiteralPath (Join-Path $packageDir 'SOURCE-SHA256SUMS.txt') -Value $sourceHashes -Encoding utf8
[ordered]@{
    version = $version; artifact = $artifactName; sha256 = $checksum
    gitCommit = $commit; includesWorkingChanges = $workingChanges.Count -gt 0
    builtAtUtc = [DateTime]::UtcNow.ToString('o')
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageDir 'build-manifest.json') -Encoding utf8

if ($Run) {
    $rootExe = Join-Path $repoRoot 'PaperTodo.exe'
    $running = @(Get-Process -Name PaperTodo -ErrorAction SilentlyContinue)
    if (@($running | Where-Object { $_.Path -ine $rootExe }).Count -gt 0) {
        throw 'Another PaperTodo installation is running. The package is ready; close that instance before running this checkout.'
    }
    if ($running.Count -gt 0) {
        $exitCommand = Start-Process -FilePath $rootExe -ArgumentList '--exit' -WindowStyle Hidden -PassThru
        if (-not $exitCommand.WaitForExit(5000)) { throw 'Exit command did not finish.' }
        foreach ($process in $running) {
            if (-not $process.WaitForExit(15000)) { throw 'PaperTodo has not finished saving and exiting.' }
        }
    }
    $previous = Join-Path $repoRoot ('backups\updates\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $previous -Force | Out-Null
    foreach ($name in @('PaperTodo.exe', 'data.json', 'data.backup.json')) {
        $source = Join-Path $repoRoot $name
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $previous }
    }
    Copy-Item -LiteralPath $artifact -Destination $rootExe -Force
    Start-Process -FilePath $rootExe -WorkingDirectory $repoRoot
}

# Remove only the unique staging folder created above, after checking its absolute boundary.
$resolvedStaging = (Resolve-Path -LiteralPath $staging).Path
$resolvedOutput = (Resolve-Path -LiteralPath $outputRoot).Path.TrimEnd('\') + '\'
if (-not $resolvedStaging.StartsWith($resolvedOutput, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Publish staging cleanup escaped the output directory.'
}
Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
Write-Output "Package ready: $artifact"
