# iOneDataGrove — istruzioni di avvio

## Dashboard

Aprire PowerShell ed eseguire:

```powershell
cd C:\Progetti\iOneDataGrove
.\Dashboard\Avvia-Dashboard.ps1
```

Aprire nel browser: http://localhost:3000

## Importazione incrementale

Aprire PowerShell ed eseguire:

```powershell
cd C:\Progetti\iOneDataGrove
dotnet run --project .\iOneDataGrove\src\iOneDataGrove.Importer\iOneDataGrove.Importer.csproj
```

## Importazione completa

```powershell
cd C:\Progetti\iOneDataGrove
dotnet run --project .\iOneDataGrove\src\iOneDataGrove.Importer\iOneDataGrove.Importer.csproj -- --full
```

## Solo commit del branch principale di iOneGavio

```powershell
cd C:\Progetti\iOneDataGrove
dotnet run --project .\iOneDataGrove\src\iOneDataGrove.Importer\iOneDataGrove.Importer.csproj -- --Import:Repository iOneSolutionsSrl/iOneGavio --Import:CommitsOnly true
```

## Importazione di un solo repository

```powershell
cd C:\Progetti\iOneDataGrove
dotnet run --project .\iOneDataGrove\src\iOneDataGrove.Importer\iOneDataGrove.Importer.csproj -- --Import:Repository iOneSolutionsSrl/iOneGavio
```

## Generazione locale dei chunk

Genera o aggiorna i chunk di tutti i repository attivi usando i dati già presenti in PostgreSQL, senza chiamare GitHub:

```powershell
cd C:\Progetti\iOneDataGrove
dotnet run --project .\iOneDataGrove\src\iOneDataGrove.Importer\iOneDataGrove.Importer.csproj -- --chunks-only
```

Per limitare l'operazione a un repository:

```powershell
cd C:\Progetti\iOneDataGrove
dotnet run --project .\iOneDataGrove\src\iOneDataGrove.Importer\iOneDataGrove.Importer.csproj -- --chunks-only --Import:Repository iOneSolutionsSrl/iOneGavio
```
