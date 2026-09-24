# iOneDataGrove — stato del progetto e passaggio consegne

> Aggiornato al 24 settembre 2026. Questo documento serve come contesto iniziale per continuare il lavoro in una nuova chat.

## Aggiornamento 24 settembre: ambiente locale completo e importazione consolidata

### Stato operativo

- PostgreSQL 17 è installato localmente e il database operativo è `ionedatagrove`.
- L'utenza applicativa `ionedatagrove_app` è configurata; le credenziali restano nei .NET User Secrets condivisi da importatore e dashboard e non sono salvate nel repository.
- La dashboard è stata verificata su `http://localhost:3000` con API locale su `http://127.0.0.1:5088`.
- La dashboard riporta **1.047.616 record importati**, **199 repository attivi**, copertura **1.194/1.194 (100%)**, **0 errori correnti** e **0 sincronizzazioni attive**.
- Tutti i sei tipi di risorsa (`issues`, `pull_requests`, `commits`, `repository_files`, `code_symbols`, `knowledge_links`) risultano completati per tutti i 199 repository. Non risultano stati di sincronizzazione incompleti.

### Affidabilità dell'importatore

- In caso di esaurimento del limite GitHub, l'importatore attende il valore comunicato da `X-RateLimit-Reset` più un piccolo margine, invece di fallire dopo tentativi a intervallo fisso.
- Le pull request con almeno 3.000 file ricostruiscono l'elenco completo confrontando gli alberi Git. Sono gestiti file aggiunti, rimossi, modificati e rinominati.
- L'albero Git vuoto (`4b825dc642cb6eb9a060e54bf8d69288fbee4904`) è riconosciuto localmente, evitando richieste GitHub che restituirebbero 404.
- Prima del salvataggio, stringhe e JSON vengono normalizzati per PostgreSQL. I caratteri NUL semantici provenienti dai dati GitHub sono sostituiti con U+FFFD, mentre le sequenze letterali `\\u0000` restano inalterate.
- I messaggi di errore di sincronizzazione includono ora le cause interne delle eccezioni EF Core/PostgreSQL.
- Verificati con successo casi reali di repository e pull request molto grandi, fra cui `Iportal` (PR da 6.894 file) e `PortaleGestoriGiap` (PR da 4.563 file). Completati inoltre i recuperi di `iOneCostantin`, `IOneAssetIp`, `iOneAt`, `iOneRetitalia`, `iOneSpeedy` e `IOneTotalLube`.
- Test dell'importatore: **23/23 superati** in configurazione Release.

### Qualità del knowledge layer

- La sezione Collegamenti mostra indicatori separati, conteggio e percentuale per `closes`, `references`, `contains_commit`, `modifies_file` e `declares_symbol`.
- I collegamenti `references` indicano una citazione senza una formula esplicita di chiusura. I messaggi standard `Merge pull request #…` sono riconosciuti automaticamente come riferimenti confermati; soltanto i casi realmente ambigui sono evidenziati come “Da verificare”.
- È disponibile il filtro diretto “Mostra riferimenti” per esaminare rapidamente i casi potenzialmente ambigui.
- Il test API `Dashboard/tests/knowledge-quality-api.test.mjs` verifica in sola lettura tre catene reali complete: iOneCostantin #1 → PR #2, iOneGavio #48 → PR #49 e iOneIpWow #14 → PR #15, fino a commit, file e simbolo C#. Verifica inoltre che i riferimenti dei messaggi standard di merge non richiedano revisione manuale.
- Il test si esegue con `KNOWLEDGE_QUALITY_TEST_API=http://127.0.0.1:5088/api` e `npm run test:knowledge` usando Node 22 o successivo.
- Il riconoscimento delle chiusure comprende ora `Chiude`, `Chiudono`, `Risolve`, `Risolvono`, `Corregge`, `Correggono`, oltre alle forme `Chiuso/a/i/e da`, `Risolto/a/i/e da` e `Corretto/a/i/e da`.
- Le tre catene automatiche, TypeScript, lint, build frontend e build API risultano superati; la resa è stata controllata anche nel browser.

### Backup locale

- Lo script manuale è `C:\Progetti\iOneDataGrove\Backup-Database-Locale.ps1`.
- La destinazione predefinita è `C:\OneDrive\OneDrive - Gruppo Gavio\iOneDataGroveBackup`.
- Lo script chiede interattivamente la password PostgreSQL, quindi non la conserva nel file.
- Ogni esecuzione crea un backup compresso del database (`ionedatagrove_*.dump`) e un file separato con ruoli e permessi (`postgres_globals_*.sql`).
- Il backup viene prima scritto con estensione `.partial`, verificato con `pg_restore --list` e rinominato solo se leggibile.
- Primo backup verificato il 24 settembre 2026: **1.699,38 MB**, con database e ruoli presenti nella cartella di destinazione.
- La prova reale di ripristino è stata completata il 24 settembre 2026 in un database temporaneo separato: **16 tabelle** e **8.042.540 righe** confrontate con conteggi identici al database operativo. Schema, dati, vincoli e indici sono stati ricreati senza errori; il database temporaneo è stato eliminato al termine.
- Lo script riutilizzabile `C:\Progetti\iOneDataGrove\Test-Ripristino-Database-Locale.ps1` seleziona il backup più recente, ripristina in `ionedatagrove_restore_test`, confronta tutte le tabelle applicative e rimuove sempre la copia temporanea.

## Aggiornamento 9 settembre: navigazione dashboard

### Correzione mirata issue #567

- Il commit `8d98f4036ba9534de7210af9af264d3cec0d5d4f` non era nel database: l'importazione ordinaria acquisisce i commit tramite PR, non l'intera cronologia del branch.
- Aggiunto `GitHubCommitRecovery` e modalità `--RecoverCommit:Repository <owner/repo> --RecoverCommit:Sha <sha-completo>` nell'importatore. Recupera il solo commit con tutti i suoi file tramite GitHub, rispetta esclusioni/sospensioni e il blocco condiviso. Non avanza i watermark delle importazioni generali.
- `KnowledgeLinkIndexer.IndexCommitAsync` aggiunge soltanto i collegamenti del commit recuperato, senza cancellare o ricostruire l'indice del repository. Un semplice `#567` produce `references`, non `closes`.
- Correzione applicata a iOneGavio: commit interno 5430 → issue interna 1320 (#567); un file modificato, `IOne.Domain/Helpers/PrefatturazioneHelper.cs`, +1/−1. Ripetizione verificata: stesso ID commit, zero nuovi collegamenti.
- Il sorgente del file non è nel catalogo locale. La vista del commit ora mostra tutti i file modificati dai metadati importati, con link GitHub alla revisione e indicazione della disponibilità del sorgente locale.
- Verificato nel browser il percorso issue → commit → file. Test importatore: 11/11 superati.
- Implementata successivamente l'acquisizione ordinaria dei commit del branch principale, indipendentemente dalle PR (vedi sotto).

### Importazione autonoma dei commit del branch principale

- `GitHubBranchCommitImporter` viene eseguito nel flusso ordinario dopo le PR, con stato separato `commits` visibile nella dashboard come “Commit del branch principale”.
- Il primo giro legge tutta la cronologia fissando lo SHA della punta per tutte le pagine. I commit completi già importati dalle PR o da recuperi mirati vengono riutilizzati; i dettagli e i file mancanti vengono acquisiti.
- I giri successivi confrontano il precedente SHA completato con quello attuale. Non usano le date come filtro, per includere anche commit vecchi uniti successivamente. Branch cambiato, cronologia divergente o precedente SHA non più disponibile fanno ripartire la scansione della cronologia, riutilizzando i dati già completi.
- Il cursore è salvato solo dopo completamento di acquisizione e collegamenti. Un'interruzione mantiene l'ultimo cursore valido; i dati salvati vengono riutilizzati alla ripresa.
- Recupero GitHub con al massimo quattro letture concorrenti; scritture PostgreSQL sequenziali. Collegamenti aggiunti per gruppi di commit, senza cancellare l'indice esistente.
- Per diff di almeno 3.000 file si ricostruisce l'elenco completo dagli alberi Git rispetto al primo genitore (albero vuoto per il commit iniziale). Patch e conteggi di righe non disponibili restano null; le rinomine sono rappresentate come rimozione/aggiunta. Gli alberi troncati causano errore, senza dichiarare completa la sincronizzazione.
- `--Import:Repository owner/repo --Import:CommitsOnly true` esegue solo questa fase su un repository già acquisito, senza reimportare issue, PR o sorgenti. `--full` forza il ricontrollo della cronologia e riutilizza comunque i dettagli immutabili completi.
- Ambito: branch principale corrente e commit già acquisiti tramite PR; non viene acquisita automaticamente la cronologia di ogni branch secondario.
- Recupero iOneGavio completato il 9 settembre: 1.385 commit del branch principale esaminati; catalogo complessivo passato da 572 a 1.947 commit (+1.375), conservando quelli delle PR. Collegamenti passati da 58.052 a 63.663 (+5.611).
- Commit iniziale `a18eb4dd77a9c505effbb4b3fb334965aeca2584` verificato con tutti i 4.249 file; collegamento issue #567 → commit `8d98f403` confermato.
- Giro successivo verificato: `mode=unchanged`, zero commit importati/riparati e zero nuovi collegamenti; conteggi invariati. Stato `commits=completed`.
- Verifiche: 18/18 test importatore, build importatore senza avvisi/errori, TypeScript e lint frontend, build dashboard, 2 test rendering e test API di navigazione superati.

### Navigazione e tipi

- Da issue, pull request e commit è disponibile “Esplora collegamenti”.
- Ogni elemento dei collegamenti apre le sue relazioni in entrata e in uscita, filtrate per tipo e ID in PostgreSQL prima della paginazione.
- File e simboli aprono il lettore locale del codice; per i simboli viene evidenziata la riga di dichiarazione.
- Dal lettore del codice si torna ai commit, alle pull request e ai simboli del file.
- Sezione, elemento e destinazione del codice sono rappresentati nell'URL: link diretti e navigazione Indietro/Avanti del browser. I filtri testuali e di relazione rimangono locali alla vista e vengono azzerati cambiando elemento.
- Restano disponibili i link a GitHub. Il codice mostrato è quello importato, non una ricostruzione del file alla revisione del commit.
- Verificati sui dati di iOneGavio: issue → PR, commit → file → simbolo, apertura alla riga 27 di TrasportiHelper.cs e ritorno alla vista file.
- Test API ripetibile in sola lettura: impostare `NAVIGATION_TEST_API=http://127.0.0.1:5088/api/repositories/1` ed eseguire `npm run test:navigation` dalla cartella Dashboard. Copre filtro bidirezionale, paginazione, sorgente/riga, percorso file, input incompleto e assenza di relazioni.
- Build API, build frontend, lint e test dashboard superati. Risolti anche i tipi Cloudflare mancanti: `npm run typecheck` controlla l'intero progetto senza errori. Le dichiarazioni ufficiali sono in `Dashboard/worker/runtime.d.ts`, rigenerabili con `npm run types:cloudflare`; `worker/env.d.ts` dichiara D1 facoltativo, coerentemente con la dashboard locale su PostgreSQL.

## Obiettivo finale

iOneDataGrove deve diventare un **secondo cervello tecnico aziendale**: un sistema capace di acquisire dati da GitHub e, in seguito, da altre fonti, conservarli in PostgreSQL, collegarli tra loro e renderli consultabili da una dashboard e da funzionalità di ricerca/AI.

Il risultato finale desiderato non è un semplice archivio di repository. Dovrà permettere di:

- ricostruire la storia e lo stato dei progetti;
- cercare in repository, issue, pull request, commit e codice sorgente;
- comprendere le relazioni tra richieste, modifiche e componenti software;
- individuare rapidamente dove è implementata una funzionalità e perché è stata modificata;
- interrogare in linguaggio naturale la conoscenza tecnica aziendale;
- aggiungere in futuro altre fonti senza dipendere esclusivamente da GitHub.

## Stato attuale in breve

Il primo flusso completo è funzionante:

```text
GitHub (token read-only)
        ↓
Importatore .NET / EF Core
        ↓
PostgreSQL
        ↓
API ASP.NET Core
        ↓
Dashboard locale React / vinext
```

Il repository usato principalmente per sviluppo e verifica è `iOneSolutionsSrl/iOneGavio`, ma l'importatore individua automaticamente **tutti i repository accessibili al token**.

## Struttura principale

```text
C:\Progetti\iOneDataGrove
├── Dashboard
│   ├── app                         # interfaccia React
│   ├── server                      # API ASP.NET Core
│   ├── tests                       # test dashboard
│   └── Avvia-Dashboard.ps1
├── iOneDataGrove
│   ├── database                    # script SQL evolutivi 001-005
│   ├── src
│   │   ├── iOneDataGrove.Importer
│   │   └── iOneDataGrove.Persistence
│   ├── tests
│   │   └── iOneDataGrove.Importer.Tests
│   └── iOneDataGrove.sln
├── Backup-Database-Locale.ps1      # backup manuale verificato di database e ruoli
├── Crea-Database-Locale.ps1        # creazione del database PostgreSQL locale
├── Crea-Utenza-Applicativa-Locale.ps1
├── Test-Ripristino-Database-Locale.ps1 # prova isolata e confronto completo
├── ISTRUZIONI-AVVIO.md             # soli comandi di avvio
└── STATO-PROGETTO.md               # questo documento
```

## Funzionalità completate

### Importazione GitHub

- autenticazione tramite token GitHub con permessi di sola lettura;
- scoperta automatica di tutti i repository accessibili;
- stato di sincronizzazione separato per ogni repository;
- importazione incrementale e importazione completa;
- acquisizione di repository, utenti, issue e commenti;
- acquisizione di pull request, relativi file e commit;
- acquisizione del codice sorgente idoneo;
- riconoscimento di file eliminati o ripristinati;
- gestione dei commenti eliminati e riconciliazione dei relativi conteggi;
- esclusione dei commenti appartenenti alle pull request dal catalogo delle issue;
- blocco condiviso PostgreSQL per impedire due importazioni contemporanee;
- possibilità di disabilitare la sincronizzazione di un repository;
- possibilità di escludere e cancellare dalla dashboard i dati di un repository;
- gestione degli errori per repository, evitando che un singolo problema interrompa l'intero giro.

### Indicizzazione del codice

- salvataggio del contenuto dei file sorgente in PostgreSQL;
- ricerca full-text PostgreSQL sul codice;
- primo livello prudente di analisi strutturale dedicato a C#;
- indicizzazione di namespace, classi, interfacce, record, enum, metodi e altri simboli C#;
- elaborazione incrementale: i file invariati non vengono analizzati nuovamente.

### Knowledge layer iniziale

Sono creati automaticamente collegamenti fra:

- pull request e commit;
- pull request e file modificati;
- commit e file modificati;
- file C# e simboli dichiarati;
- issue, commenti e pull request che contengono riferimenti testuali riconoscibili.

I tipi principali di relazione sono `references`, `closes`, `contains_commit`, `modifies_file` e `declares_symbol`.

### Dashboard locale

- riepilogo generale delle importazioni;
- elenco dei repository e relativo stato;
- dettaglio e Repository Explorer;
- Source Code Explorer;
- ricerca PostgreSQL full-text;
- visualizzazione della struttura C#;
- controlli di integrità per dati mancanti, differenze di stato e sincronizzazioni obsolete;
- gestione dei flag di sincronizzazione/esclusione;
- sezione **Collegamenti** per esplorare il knowledge layer;
- ricerca e filtri dei collegamenti eseguiti lato PostgreSQL;
- filtri per relazione, origine e tipo di entità;
- paginazione e conteggio dei risultati filtrati.

## Database

PostgreSQL locale è la fonte dati principale. EF Core viene usato per mapping e accesso ai dati; lo schema iniziale era già stato creato tramite SQL e la migration presente rappresenta una baseline dello schema esistente. Al momento non è previsto un database Google o cloud.

Gli script evolutivi si trovano in `iOneDataGrove\database`:

1. `001_repository_files.sql` — file sorgente;
2. `002_repository_controls.sql` — controlli di sincronizzazione/esclusione;
3. `003_repository_files_full_text.sql` — ricerca full-text;
4. `004_csharp_structural_index.sql` — struttura C#;
5. `005_automatic_knowledge_links.sql` — collegamenti automatici.

Non inserire token o password nel codice o nella dashboard. Le credenziali sono gestite dalla configurazione locale/user secrets già predisposta.

## Ultima importazione verificata

Il recupero completo terminato il 24 settembre 2026 ha prodotto questo stato:

- repository attivi: **199**;
- record importati: **1.047.616**;
- risorse completate: **1.194/1.194 (100%)**;
- sincronizzazioni incomplete: **0**;
- errori correnti: **0**;
- sincronizzazioni attive: **0**.

Per ognuno dei 199 repository risultano completate issue, pull request, commit del branch principale, file sorgente, simboli e collegamenti. La dashboard e le API locali sono state aperte sui dati reali e verificate dopo il recupero.

Nota importante: GitHub non aggiorna sempre `issue.updated_at` quando cambia un commento. Per questo l'importatore legge a ogni giro incrementale il catalogo completo delle issue per ottenere conteggi autorevoli, ma aggiorna i contenuti completi soltanto quando necessario. I commenti restano importati incrementalmente e vengono riconciliati con il conteggio corrente.

## Ultime verifiche tecniche

- importazione completa e recuperi mirati: riusciti senza errori residui;
- compilazione API dashboard: riuscita con 0 errori e 0 avvisi;
- test importatore: **23/23 superati** in Release;
- build dashboard: riuscita;
- test dashboard: **2/2 superati**;
- lint frontend: superato;
- controllo TypeScript globale, inclusi i tipi Cloudflare: superato;
- backup PostgreSQL compresso: creato e verificato con `pg_restore --list`;
- ripristino reale isolato: riuscito, con 16 tabelle e 8.042.540 righe corrispondenti.

## Come avviare il progetto

### Dashboard

```powershell
cd C:\Progetti\iOneDataGrove
.\Dashboard\Avvia-Dashboard.ps1
```

Aprire `http://localhost:3000`.

### Importazione incrementale

```powershell
cd C:\Progetti\iOneDataGrove
dotnet run --project .\iOneDataGrove\src\iOneDataGrove.Importer\iOneDataGrove.Importer.csproj
```

### Importazione completa

```powershell
cd C:\Progetti\iOneDataGrove
dotnet run --project .\iOneDataGrove\src\iOneDataGrove.Importer\iOneDataGrove.Importer.csproj -- --full
```

### Backup manuale

```powershell
cd C:\Progetti\iOneDataGrove
.\Backup-Database-Locale.ps1
```

Inserire la password dell'utente PostgreSQL quando richiesta. I file vengono salvati in `C:\OneDrive\OneDrive - Gruppo Gavio\iOneDataGroveBackup`.

### Prova manuale di ripristino

```powershell
cd C:\Progetti\iOneDataGrove
.\Test-Ripristino-Database-Locale.ps1
```

Lo script usa un database temporaneo separato, confronta tutte le tabelle con il database operativo e lo elimina al termine. Non modifica `ionedatagrove`.

## File particolarmente rilevanti

- `iOneDataGrove\src\iOneDataGrove.Importer\ImporterApplication.cs` — coordinamento del processo;
- `iOneDataGrove\src\iOneDataGrove.Importer\GitHubApiClient.cs` — chiamate GitHub, rate limit e ricostruzione dei file tramite alberi Git;
- `GitHubRepositoryDiscovery.cs` — scoperta repository;
- `GitHubIssueImporter.cs` — issue, commenti e riconciliazione;
- `GitHubPullRequestImporter.cs` — pull request, commit e file;
- `GitHubSourceImporter.cs` — codice sorgente;
- `CSharpSymbolIndexer.cs` — indice strutturale C#;
- `KnowledgeLinkIndexer.cs` — creazione dei collegamenti;
- `iOneDataGrove\src\iOneDataGrove.Persistence\Data\IOneDataGroveDbContext.cs` — mapping EF Core;
- `iOneDataGrove\src\iOneDataGrove.Persistence\Data\PostgreSqlValueSanitizer.cs` — normalizzazione sicura di testo e JSON per PostgreSQL;
- `Dashboard\server\Program.cs` — API e controlli dashboard;
- `Dashboard\app\repositories\[id]\RepositoryExplorerClient.tsx` — repository explorer;
- `Dashboard\app\repositories\[id]\SourceCodeExplorer.tsx` — esplorazione sorgenti;
- `Dashboard\app\repositories\[id]\KnowledgeLinksExplorer.tsx` — esplorazione collegamenti.

## Decisioni progettuali già prese

- PostgreSQL resta il sistema autorevole per i dati importati e derivati.
- GitHub viene interrogato esclusivamente in lettura.
- Ogni repository mantiene uno stato di sincronizzazione indipendente.
- La modalità ordinaria deve essere incrementale e ripetibile in sicurezza.
- I dati grezzi GitHub vengono conservati insieme ai campi normalizzati quando utile.
- Le elaborazioni costose sul codice devono saltare i file invariati.
- La dashboard non deve contenere credenziali né collegarsi direttamente al database dal browser: passa sempre dall'API server.
- I filtri su insiemi grandi devono essere applicati da PostgreSQL prima della paginazione.

## Prossimi passi consigliati

La base locale e l'importazione completa sono ora operative. Procedere in questo ordine:

1. mantenere manuale il backup per questa fase e decidere in seguito frequenza e numero di copie da conservare;
2. usare normalmente l'importazione incrementale e controllare dalla dashboard che non compaiano errori o risorse incomplete;
3. ripetere periodicamente la prova di ripristino, soprattutto dopo modifiche allo schema PostgreSQL;
4. controllare un campione di collegamenti `references` e `closes`, distinguendo quelli corretti dai falsi positivi;
5. estendere nel tempo i casi verificati quando emergono nuove relazioni significative;
6. progettare chunk, embedding e ricerca ibrida soltanto dopo la validazione qualitativa dei collegamenti;
7. aggiungere altre fonti dati mantenendo un modello comune di provenienza, sincronizzazione ed entità.

## Direzione futura verso l'AI

Il passo AI non dovrebbe limitarsi a inviare file interi a un modello. La base consigliata è:

```text
dati normalizzati + ricerca full-text + simboli C# + collegamenti verificati
                              ↓
                    chunk con provenienza
                              ↓
                  embedding / ricerca ibrida
                              ↓
          risposte AI con riferimenti alle fonti
```

Ogni risposta futura dovrebbe poter indicare repository, file, simbolo, issue, pull request o commit da cui deriva. Questo mantiene il secondo cervello verificabile e utile anche senza AI.

## Prompt suggerito per la nuova chat

```text
Continua lo sviluppo di iOneDataGrove usando come contesto il file
C:\Progetti\iOneDataGrove\STATO-PROGETTO.md.
Prima verifica lo stato effettivo dei file coinvolti e non rieseguire importazioni
o modifiche distruttive senza necessità. Il repository di riferimento per le prove
è iOneSolutionsSrl/iOneGavio.
```
