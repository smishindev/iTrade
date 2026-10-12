<#
.SYNOPSIS
    Restores a dump made by backup.ps1 into a NEW database. The working database is never overwritten.

.DESCRIPTION
    -Verify   restores into a temporary database, checks it (tables, migration history, the append-only
              guard of the audit journal) and removes it again. This is the regular check of a backup.
    default   restores into -TargetDatabase (default itrade_restore_<time>) and leaves it. To use it, stop
              the Host and point its connection string at that database:
                  dotnet user-secrets set "ConnectionStrings:ITrade" "...;Database=<name>;..." --project src/ITrade.Host
              Switching is the owner's decision; this script does not do it.

.EXAMPLE
    .\deploy\restore.ps1 -File D:\ITradeData\backups\itrade-20261012-070000Z.dump -Verify
#>
param(
    [Parameter(Mandatory = $true)][string]$File,
    [string]$TargetDatabase,
    [switch]$Verify,
    [string]$Container = 'itrade-postgres',
    [string]$User = 'itrade'
)

. "$PSScriptRoot\db-common.ps1"

if (-not (Test-Path -LiteralPath $File -PathType Leaf)) { throw "Backup file not found: $File" }
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd_HHmmss')
if (-not $TargetDatabase) {
    $prefix = if ($Verify) { 'itrade_verify_' } else { 'itrade_restore_' }
    $TargetDatabase = "$prefix$stamp"
}
Assert-DatabaseName $TargetDatabase

$exists = Invoke-Sql $Container $User 'postgres' "SELECT count(*) FROM pg_database WHERE datname = '$TargetDatabase'"
if ("$exists".Trim() -ne '0') {
    throw "Database '$TargetDatabase' already exists. This script only restores into a new database."
}

$inContainer = "/tmp/itrade-restore-$stamp.dump"
Invoke-Docker cp $File "${Container}:$inContainer" | Out-Null
$created = $false
try {
    Invoke-Docker exec $Container createdb -U $User $TargetDatabase | Out-Null
    $created = $true
    # Same flags as the integration test BackupRestoreTests: keep them in step.
    Invoke-Docker exec $Container pg_restore -U $User -d $TargetDatabase --exit-on-error $inContainer | Out-Null

    $tables = Invoke-Sql $Container $User $TargetDatabase "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' ORDER BY 1"
    $migrations = Invoke-Sql $Container $User $TargetDatabase 'SELECT count(*) FROM "__EFMigrationsHistory"'
    $guard = Invoke-Sql $Container $User $TargetDatabase "SELECT count(*) FROM pg_trigger WHERE tgrelid = 'audit_events'::regclass AND NOT tgisinternal"
    if ([int]"$migrations".Trim() -lt 1) { throw 'The restored database has no migration history.' }
    if ([int]"$guard".Trim() -lt 1) { throw 'The restored audit journal has lost its append-only triggers.' }

    Write-Output "Restored $File into database '$TargetDatabase':"
    foreach ($table in $tables) {
        $rows = Invoke-Sql $Container $User $TargetDatabase "SELECT count(*) FROM `"$table`""
        Write-Output ("  {0,-28} {1,8} rows" -f $table, "$rows".Trim())
    }
    Write-Output "  migrations applied: $("$migrations".Trim()); audit journal triggers: $("$guard".Trim())"
}
catch {
    if ($created) { Invoke-Docker exec $Container dropdb -U $User --if-exists $TargetDatabase | Out-Null }
    throw
}
finally {
    Invoke-Docker exec $Container rm -f $inContainer | Out-Null
}

if ($Verify) {
    Invoke-Docker exec $Container dropdb -U $User $TargetDatabase | Out-Null
    Write-Output "Backup is good: it restores. Temporary database '$TargetDatabase' removed."
}
else {
    Write-Output "Database '$TargetDatabase' is ready. The working database was not touched."
}
