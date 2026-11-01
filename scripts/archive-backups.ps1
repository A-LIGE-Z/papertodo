param(
    [int]$KeepDays = 14
)

$RootDir = (Resolve-Path "$PSScriptRoot\..").Path
$BackupsDir = Join-Path $RootDir "backups"
$ArchiveBackupsDir = Join-Path $RootDir "archive\backups"

if (-not (Test-Path $BackupsDir)) {
    Write-Host "Backups directory not found: $BackupsDir"
    exit 0
}

if (-not (Test-Path $ArchiveBackupsDir)) {
    New-Item -ItemType Directory -Path $ArchiveBackupsDir -Force | Out-Null
}

$Cutoff = (Get-Date).AddDays(-$KeepDays)
$FilesToArchive = Get-ChildItem -Path (Join-Path $BackupsDir "data-*.json") | Where-Object { $_.LastWriteTime -lt $Cutoff }

if ($FilesToArchive.Count -eq 0) {
    Write-Host "No backup files older than $KeepDays days found."
    exit 0
}

Write-Host "Found $($FilesToArchive.Count) backup files older than $KeepDays days."

# Group by YYYY-MM
$Groups = $FilesToArchive | Group-Object { $_.LastWriteTime.ToString("yyyy-MM") }

foreach ($Group in $Groups) {
    $MonthStr = $Group.Name
    $ZipPath = Join-Path $ArchiveBackupsDir "backups-$MonthStr.zip"
    Write-Host "Archiving $($Group.Count) files into $ZipPath ..."

    # Compress files into zip
    Compress-Archive -Path $Group.Group.FullName -DestinationPath $ZipPath -Update

    # Remove original files
    $Group.Group | Remove-Item -Force
}

Write-Host "Archive completed."
