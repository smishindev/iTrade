# Shared by backup.ps1 and restore.ps1 (dot-sourced). ASCII only: Windows PowerShell 5.1 reads scripts as ANSI.

$ErrorActionPreference = 'Stop'

function Invoke-Docker {
    # Runs docker and stops the script when it fails (native commands do not throw by themselves).
    # A plain function on purpose: with a param() block PowerShell would take flags such as -v or -d for its own.
    # Inside, errors are not terminating: Windows PowerShell 5.1 turns every stderr line of a native program
    # into an error record, and a warning must not stop the script - the exit code decides.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & docker @args 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    if ($code -ne 0) {
        throw "docker $($args -join ' ') failed ($code): $($output -join ' | ')"
    }
    return $output
}

function Get-ITradeDataDir {
    # The data folder outside the repository: ITRADE_DATA_DIR, else data_dir of config/paths.toml.
    if ($env:ITRADE_DATA_DIR) { return $env:ITRADE_DATA_DIR }
    $paths = Join-Path (Split-Path $PSScriptRoot -Parent) 'config\paths.toml'
    $line = Select-String -Path $paths -Pattern '^\s*data_dir\s*=\s*"([^"]+)"' | Select-Object -First 1
    if (-not $line) { throw "data_dir was not found in $paths" }
    return $line.Matches[0].Groups[1].Value -replace '/', '\'
}

function Assert-DatabaseName {
    param([string]$Name)
    if ($Name -notmatch '^[a-z][a-z0-9_]{0,50}$') {
        throw "Database name '$Name' is not allowed (lower-case letters, digits and _ only)."
    }
}

function Invoke-Sql {
    # One value or one row per line, no headers; runs inside the container, where local connections need no password.
    param([string]$Container, [string]$User, [string]$Database, [string]$Sql)
    # Windows PowerShell 5.1 drops double quotes inside an argument of a native program unless they are escaped.
    $escaped = $Sql.Replace('"', '\"')
    return Invoke-Docker exec $Container psql -U $User -d $Database --set=ON_ERROR_STOP=1 -At -c $escaped
}
