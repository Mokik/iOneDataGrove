[CmdletBinding()]
param(
    [string]$SourceDatabase = "ionedatagrove",
    [string]$TestDatabase = "ionedatagrove_restore_test",
    [string]$PostgresUser = "postgres",
    [string]$HostName = "127.0.0.1",
    [int]$Port = 5432,
    [string]$BackupFolder = "C:\OneDrive\OneDrive - Gruppo Gavio\iOneDataGroveBackup",
    [string]$BackupPath
)

$ErrorActionPreference = "Stop"

$postgresBin = "C:\Program Files\PostgreSQL\17\bin"
$psql = Join-Path $postgresBin "psql.exe"
$createdb = Join-Path $postgresBin "createdb.exe"
$dropdb = Join-Path $postgresBin "dropdb.exe"
$pgRestore = Join-Path $postgresBin "pg_restore.exe"

foreach ($requiredProgram in @($psql, $createdb, $dropdb, $pgRestore)) {
    if (-not (Test-Path -LiteralPath $requiredProgram)) {
        throw "Programma PostgreSQL non trovato: $requiredProgram"
    }
}

if (-not $BackupPath) {
    $latestBackup = Get-ChildItem -LiteralPath $BackupFolder -Filter "${SourceDatabase}_*.dump" -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if (-not $latestBackup) {
        throw "Nessun backup di '$SourceDatabase' trovato in '$BackupFolder'."
    }

    $BackupPath = $latestBackup.FullName
}

if (-not (Test-Path -LiteralPath $BackupPath -PathType Leaf)) {
    throw "File di backup non trovato: $BackupPath"
}

if ($SourceDatabase -eq $TestDatabase) {
    throw "Il database di prova deve avere un nome diverso dal database operativo."
}

$securePassword = Read-Host "Password dell'utente PostgreSQL '$PostgresUser'" -AsSecureString
$passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
$testDatabaseCreated = $false

function Invoke-PsqlValue {
    param(
        [Parameter(Mandatory)] [string]$Database,
        [Parameter(Mandatory)] [string]$Sql
    )

    $value = & $psql `
        --host $HostName `
        --port $Port `
        --username $PostgresUser `
        --dbname $Database `
        --no-psqlrc `
        --tuples-only `
        --no-align `
        --set ON_ERROR_STOP=1 `
        --command $Sql

    if ($LASTEXITCODE -ne 0) {
        throw "Interrogazione PostgreSQL non riuscita sul database '$Database'."
    }

    return ($value | Out-String).Trim()
}

try {
    $env:PGPASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)

    Write-Host "Verifico la leggibilita del backup..."
    & $pgRestore --list $BackupPath | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Il backup non supera la verifica di leggibilita."
    }

    $databaseExists = Invoke-PsqlValue -Database "postgres" -Sql "SELECT 1 FROM pg_database WHERE datname = '$($TestDatabase.Replace("'", "''"))';"
    if ($databaseExists -eq "1") {
        throw "Il database temporaneo '$TestDatabase' esiste gia. Rimuoverlo o indicare un altro nome."
    }

    Write-Host "Creo il database temporaneo '$TestDatabase'..."
    & $createdb --host $HostName --port $Port --username $PostgresUser $TestDatabase
    if ($LASTEXITCODE -ne 0) {
        throw "Creazione del database temporaneo non riuscita."
    }
    $testDatabaseCreated = $true

    Write-Host "Ripristino il backup nel database temporaneo..."
    & $pgRestore `
        --host $HostName `
        --port $Port `
        --username $PostgresUser `
        --dbname $TestDatabase `
        --exit-on-error `
        --jobs 4 `
        $BackupPath

    if ($LASTEXITCODE -ne 0) {
        throw "Ripristino del backup non riuscito."
    }

    $tableEntries = & $psql `
        --host $HostName `
        --port $Port `
        --username $PostgresUser `
        --dbname $SourceDatabase `
        --no-psqlrc `
        --tuples-only `
        --no-align `
        --set ON_ERROR_STOP=1 `
        --field-separator "|" `
        --command "SELECT schemaname, tablename FROM pg_tables WHERE schemaname NOT IN ('pg_catalog', 'information_schema') AND schemaname NOT LIKE 'pg_toast%' ORDER BY schemaname, tablename;"

    if ($LASTEXITCODE -ne 0) {
        throw "Lettura dell'elenco tabelle non riuscita."
    }

    $tableEntries = @($tableEntries | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if ($tableEntries.Count -eq 0) {
        throw "Il database operativo non contiene tabelle applicative da confrontare."
    }

    Write-Host "Confronto i conteggi di $($tableEntries.Count) tabelle..."
    $differences = [System.Collections.Generic.List[string]]::new()
    $totalRows = [long]0

    foreach ($tableEntry in $tableEntries) {
        $parts = $tableEntry -split '\|', 2
        if ($parts.Count -ne 2) {
            throw "Nome tabella non riconosciuto: $tableEntry"
        }

        $schemaName = $parts[0]
        $tableName = $parts[1]
        $quotedSchemaName = '"' + $schemaName.Replace('"', '""') + '"'
        $quotedTableName = '"' + $tableName.Replace('"', '""') + '"'
        $sql = "SELECT count(*) FROM $quotedSchemaName.$quotedTableName;"
        $sourceCount = [long](Invoke-PsqlValue -Database $SourceDatabase -Sql $sql)
        $restoredCount = [long](Invoke-PsqlValue -Database $TestDatabase -Sql $sql)
        $totalRows += $restoredCount

        if ($sourceCount -ne $restoredCount) {
            $differences.Add("${schemaName}.${tableName}: operativo=$sourceCount, ripristinato=$restoredCount")
        }
    }

    if ($differences.Count -gt 0) {
        throw "Conteggi diversi dopo il ripristino:`n$($differences -join "`n")"
    }

    Write-Host "Prova di ripristino completata con successo." -ForegroundColor Green
    Write-Host "Backup : $BackupPath"
    Write-Host "Tabelle: $($tableEntries.Count)"
    Write-Host "Righe   : $totalRows"
}
finally {
    if ($testDatabaseCreated) {
        Write-Host "Elimino il database temporaneo '$TestDatabase'..."
        & $dropdb `
            --host $HostName `
            --port $Port `
            --username $PostgresUser `
            --if-exists `
            --force `
            $TestDatabase

        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Non e stato possibile eliminare il database temporaneo '$TestDatabase'."
        }
    }

    $env:PGPASSWORD = $null
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
}
