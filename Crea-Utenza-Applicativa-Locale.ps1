[CmdletBinding()]
param(
    [string]$DatabaseName = "ionedatagrove",
    [string]$AdminUser = "postgres",
    [string]$ApplicationUser = "ionedatagrove_app",
    [int]$Port = 5432
)

$ErrorActionPreference = "Stop"

$psql = "C:\Program Files\PostgreSQL\17\bin\psql.exe"
$importerProject = Join-Path $PSScriptRoot "iOneDataGrove\src\iOneDataGrove.Importer\iOneDataGrove.Importer.csproj"

if (-not (Test-Path -LiteralPath $psql)) {
    throw "psql non trovato in $psql."
}

if (-not (Test-Path -LiteralPath $importerProject)) {
    throw "Progetto Importer non trovato in $importerProject."
}

function ConvertTo-PlainText([Security.SecureString]$SecureValue) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureValue)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

$adminPassword = ConvertTo-PlainText (Read-Host "Password dell'utente PostgreSQL '$AdminUser'" -AsSecureString)
$applicationPassword = ConvertTo-PlainText (Read-Host "Nuova password per '$ApplicationUser'" -AsSecureString)
$applicationPasswordConfirmation = ConvertTo-PlainText (Read-Host "Ripeti la nuova password per '$ApplicationUser'" -AsSecureString)

if ([string]::IsNullOrWhiteSpace($applicationPassword)) {
    throw "La password applicativa non può essere vuota."
}

if ($applicationPassword -cne $applicationPasswordConfirmation) {
    throw "Le due password applicative non coincidono."
}

$escapedApplicationPassword = $applicationPassword.Replace("'", "''")
$roleSql = @"
DO `$role`$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$ApplicationUser') THEN
        ALTER ROLE $ApplicationUser WITH LOGIN PASSWORD '$escapedApplicationPassword';
    ELSE
        CREATE ROLE $ApplicationUser WITH LOGIN PASSWORD '$escapedApplicationPassword';
    END IF;
END
`$role`$;

GRANT CONNECT ON DATABASE $DatabaseName TO $ApplicationUser;
GRANT USAGE ON SCHEMA github, ingestion, knowledge TO $ApplicationUser;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA github, ingestion, knowledge TO $ApplicationUser;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA github, ingestion, knowledge TO $ApplicationUser;

ALTER DEFAULT PRIVILEGES FOR ROLE $AdminUser IN SCHEMA github
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO $ApplicationUser;
ALTER DEFAULT PRIVILEGES FOR ROLE $AdminUser IN SCHEMA ingestion
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO $ApplicationUser;
ALTER DEFAULT PRIVILEGES FOR ROLE $AdminUser IN SCHEMA knowledge
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO $ApplicationUser;

ALTER DEFAULT PRIVILEGES FOR ROLE $AdminUser IN SCHEMA github
    GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO $ApplicationUser;
ALTER DEFAULT PRIVILEGES FOR ROLE $AdminUser IN SCHEMA ingestion
    GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO $ApplicationUser;
ALTER DEFAULT PRIVILEGES FOR ROLE $AdminUser IN SCHEMA knowledge
    GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO $ApplicationUser;
"@

try {
    $env:PGPASSWORD = $adminPassword
    $roleSql | & $psql -h 127.0.0.1 -p $Port -U $AdminUser -d $DatabaseName -v ON_ERROR_STOP=1
    if ($LASTEXITCODE -ne 0) {
        throw "Creazione o configurazione dell'utenza applicativa non riuscita."
    }

    $env:PGPASSWORD = $applicationPassword
    $tableCount = & $psql -h 127.0.0.1 -p $Port -U $ApplicationUser -d $DatabaseName -tAc "SELECT count(*) FROM information_schema.tables WHERE table_schema IN ('github', 'ingestion', 'knowledge')"
    if ($LASTEXITCODE -ne 0) {
        throw "Verifica dell'accesso con l'utenza applicativa non riuscita."
    }

    $connectionString = "Host=127.0.0.1;Port=$Port;Database=$DatabaseName;Username=$ApplicationUser;Password=$applicationPassword"
    dotnet user-secrets set "ConnectionStrings:iOneDataGrove" $connectionString --project $importerProject | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Configurazione della stringa di connessione nei Secret utente non riuscita."
    }

    Write-Host "Utenza '$ApplicationUser' pronta e configurata ($($tableCount.Trim()) tabelle accessibili)." -ForegroundColor Green
}
finally {
    $env:PGPASSWORD = $null
    $adminPassword = $null
    $applicationPassword = $null
    $applicationPasswordConfirmation = $null
    $escapedApplicationPassword = $null
    $connectionString = $null
}
