[CmdletBinding()]
param(
    [string]$DatabaseName = "ionedatagrove",
    [string]$PostgresUser = "postgres",
    [string]$HostName = "127.0.0.1",
    [int]$Port = 5432,
    [string]$Destination = "C:\OneDrive\OneDrive - Gruppo Gavio\iOneDataGroveBackup"
)

$ErrorActionPreference = "Stop"

$postgresBin = "C:\Program Files\PostgreSQL\17\bin"
$pgDump = Join-Path $postgresBin "pg_dump.exe"
$pgDumpAll = Join-Path $postgresBin "pg_dumpall.exe"
$pgRestore = Join-Path $postgresBin "pg_restore.exe"

foreach ($requiredProgram in @($pgDump, $pgDumpAll, $pgRestore)) {
    if (-not (Test-Path -LiteralPath $requiredProgram)) {
        throw "Programma PostgreSQL non trovato: $requiredProgram"
    }
}

New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$Destination = (Resolve-Path -LiteralPath $Destination).Path

$timestamp = Get-Date -Format "yyyy-MM-dd_HHmmss"
$databaseBackup = Join-Path $Destination "${DatabaseName}_${timestamp}.dump"
$globalsBackup = Join-Path $Destination "postgres_globals_${timestamp}.sql"
$databaseBackupPartial = "$databaseBackup.partial"
$globalsBackupPartial = "$globalsBackup.partial"

$securePassword = Read-Host "Password dell'utente PostgreSQL '$PostgresUser'" -AsSecureString
$passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)

try {
    $env:PGPASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)

    Write-Host "Creo il backup compresso del database '$DatabaseName'..."
    & $pgDump `
        --host $HostName `
        --port $Port `
        --username $PostgresUser `
        --format custom `
        --compress 6 `
        --file $databaseBackupPartial `
        $DatabaseName

    if ($LASTEXITCODE -ne 0) {
        throw "Backup del database non riuscito."
    }

    Write-Host "Salvo utenti e permessi PostgreSQL..."
    & $pgDumpAll `
        --host $HostName `
        --port $Port `
        --username $PostgresUser `
        --globals-only `
        --file $globalsBackupPartial

    if ($LASTEXITCODE -ne 0) {
        throw "Backup di utenti e permessi non riuscito."
    }

    & $pgRestore --list $databaseBackupPartial | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Il file di backup è stato creato ma non supera la verifica di leggibilità."
    }

    Move-Item -LiteralPath $databaseBackupPartial -Destination $databaseBackup
    Move-Item -LiteralPath $globalsBackupPartial -Destination $globalsBackup

    $databaseSizeMb = [Math]::Round((Get-Item -LiteralPath $databaseBackup).Length / 1MB, 2)
    $databaseHash = (Get-FileHash -LiteralPath $databaseBackup -Algorithm SHA256).Hash

    Write-Host "Backup completato e verificato." -ForegroundColor Green
    Write-Host "Database : $databaseBackup"
    Write-Host "Dimensione: $databaseSizeMb MB"
    Write-Host "SHA-256   : $databaseHash"
    Write-Host "Ruoli     : $globalsBackup"
}
finally {
    $env:PGPASSWORD = $null
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
    Remove-Item -LiteralPath $databaseBackupPartial, $globalsBackupPartial -Force -ErrorAction SilentlyContinue
}
