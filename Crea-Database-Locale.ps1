[CmdletBinding()]
param(
    [string]$DatabaseName = "ionedatagrove",
    [string]$PostgresUser = "postgres",
    [int]$Port = 5432
)

$ErrorActionPreference = "Stop"

$psql = "C:\Program Files\PostgreSQL\17\bin\psql.exe"
$createdb = "C:\Program Files\PostgreSQL\17\bin\createdb.exe"
$projectRoot = Join-Path $PSScriptRoot "iOneDataGrove"
$persistenceProject = Join-Path $projectRoot "src\iOneDataGrove.Persistence\iOneDataGrove.Persistence.csproj"
$databaseScripts = Join-Path $projectRoot "database"

if (-not (Test-Path -LiteralPath $psql)) {
    throw "psql non trovato in $psql. Verificare l'installazione di PostgreSQL 17."
}

if (-not (Test-Path -LiteralPath $persistenceProject)) {
    throw "Progetto Persistence non trovato in $persistenceProject."
}

$efTool = Get-Command dotnet-ef -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1
if (-not $efTool) {
    $efTool = Get-ChildItem -Path "$env:USERPROFILE\.nuget\packages\dotnet-ef\*\tools\net8.0\any\dotnet-ef.exe" -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Directory.Parent.Parent.Parent.Name } -Descending |
        Select-Object -ExpandProperty FullName -First 1
}

if (-not $efTool) {
    throw "dotnet-ef non trovato. Installarlo con: dotnet tool install --global dotnet-ef"
}

$securePassword = Read-Host "Password dell'utente PostgreSQL '$PostgresUser'" -AsSecureString
$passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
$baselineFile = Join-Path ([IO.Path]::GetTempPath()) "ionedatagrove-baseline-$PID.sql"
$localBaselineFile = Join-Path ([IO.Path]::GetTempPath()) "ionedatagrove-baseline-local-$PID.sql"

try {
    $env:PGPASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)

    & $psql -h 127.0.0.1 -p $Port -U $PostgresUser -d postgres -v ON_ERROR_STOP=1 -tAc "SELECT 1" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Accesso a PostgreSQL non riuscito. Controllare la password."
    }

    $existingDatabase = & $psql -h 127.0.0.1 -p $Port -U $PostgresUser -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$DatabaseName'"
    if ($LASTEXITCODE -ne 0) {
        throw "Non è stato possibile verificare l'esistenza del database."
    }

    if (-not $existingDatabase) {
        & $createdb -h 127.0.0.1 -p $Port -U $PostgresUser --encoding UTF8 $DatabaseName
        if ($LASTEXITCODE -ne 0) {
            throw "Creazione del database non riuscita."
        }
        Write-Host "Database '$DatabaseName' creato."
    }
    else {
        Write-Host "Database '$DatabaseName' già presente: applico solo gli elementi mancanti."
    }

    dotnet build $persistenceProject --no-restore | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Compilazione del progetto Persistence non riuscita."
    }

    & $efTool migrations script --idempotent --no-build --project $persistenceProject --output $baselineFile
    if ($LASTEXITCODE -ne 0) {
        throw "Generazione dello schema di base non riuscita."
    }

    Copy-Item -LiteralPath $baselineFile -Destination $localBaselineFile

    & $psql -h 127.0.0.1 -p $Port -U $PostgresUser -d $DatabaseName -v ON_ERROR_STOP=1 -f $localBaselineFile
    if ($LASTEXITCODE -ne 0) {
        throw "Applicazione dello schema di base non riuscita."
    }

    Get-ChildItem -Path $databaseScripts -Filter "*.sql" |
        Where-Object Name -Match '^\d{3}_' |
        Sort-Object Name |
        ForEach-Object {
            Write-Host "Applico $($_.Name)..."
            & $psql -h 127.0.0.1 -p $Port -U $PostgresUser -d $DatabaseName -v ON_ERROR_STOP=1 -f $_.FullName
            if ($LASTEXITCODE -ne 0) {
                throw "Applicazione di $($_.Name) non riuscita."
            }
        }

    $tableCount = & $psql -h 127.0.0.1 -p $Port -U $PostgresUser -d $DatabaseName -tAc "SELECT count(*) FROM information_schema.tables WHERE table_schema IN ('github', 'ingestion', 'knowledge')"
    if ($LASTEXITCODE -ne 0) {
        throw "Verifica finale del database non riuscita."
    }

    Write-Host "Database locale pronto: $DatabaseName ($($tableCount.Trim()) tabelle applicative, nessun dato importato)." -ForegroundColor Green
}
finally {
    $env:PGPASSWORD = $null
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
    Remove-Item -LiteralPath $baselineFile, $localBaselineFile -Force -ErrorAction SilentlyContinue
}
