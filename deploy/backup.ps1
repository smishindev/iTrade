<#
.SYNOPSIS
    Backs up the iTrade database to <DataDir>\backups\itrade-<UTC time>.dump (pg_dump, custom format).

.DESCRIPTION
    Reads the database only. The dump holds everything the platform knows (including the hash of the
    owner's password), so it stays in the data folder, outside the repository. Check a dump with
        .\deploy\restore.ps1 -File <dump> -Verify
    A copy that was never restored is not a backup.

.EXAMPLE
    .\deploy\backup.ps1
#>
param(
    [string]$Container = 'itrade-postgres',
    [string]$Database = 'itrade',
    [string]$User = 'itrade',
    [string]$OutDir
)

. "$PSScriptRoot\db-common.ps1"
Assert-DatabaseName $Database

if (-not $OutDir) { $OutDir = Join-Path (Get-ITradeDataDir) 'backups' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss') + 'Z'
$file = Join-Path $OutDir "$Database-$stamp.dump"
$inContainer = "/tmp/itrade-backup-$stamp.dump"

# Same flags as the integration test BackupRestoreTests: keep them in step.
Invoke-Docker exec $Container pg_dump -U $User -d $Database --format=custom "--file=$inContainer" | Out-Null
try {
    Invoke-Docker cp "${Container}:$inContainer" $file | Out-Null
}
finally {
    Invoke-Docker exec $Container rm -f $inContainer | Out-Null
}

$size = (Get-Item $file).Length
if ($size -le 0) { throw "The backup file $file is empty." }
$hash = (Get-FileHash -Algorithm SHA256 -Path $file).Hash
Write-Output "Backup written: $file ($size bytes, SHA-256 $hash)"
Write-Output "Verify it:      .\deploy\restore.ps1 -File `"$file`" -Verify"
