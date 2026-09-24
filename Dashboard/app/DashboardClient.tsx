"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";

type Totals = {
  records: number; repositories: number; users: number; issues: number;
  openIssues: number; comments: number; pullRequests: number;
  openPullRequests: number; mergedPullRequests: number; commits: number;
  changedFileRecords: number; sourceFiles: number;
  activeRepositories: number; pausedRepositories: number; excludedRepositories: number;
};

type SyncOverview = {
  trackedResources: number; completedResources: number; failedResources: number;
  runningResources: number; pendingResources: number; totalRuns: number;
  staleResources: number; untrackedRepositories: number;
  failedRunsInHistory: number; latestRunAt: string | null;
};

type Repository = {
  id: number; fullName: string; description: string | null; htmlUrl: string;
  isPrivate: boolean; isArchived: boolean; defaultBranch: string | null;
  primaryLanguage: string | null; pushedAt: string | null; syncedAt: string;
  isSyncEnabled: boolean; syncDisabledAt: string | null;
  isExcluded: boolean; excludedAt: string | null;
  issues: number; openIssues: number; pullRequests: number;
  openPullRequests: number; mergedPullRequests: number; commits: number;
};

type SyncState = {
  repositoryId: number; repositoryFullName: string; resourceType: string;
  status: string; lastSuccessfulSync: string | null;
  lastGithubUpdatedAt: string | null; updatedAt: string;
};

type SyncRun = {
  id: number; repositoryId: number | null; repositoryFullName: string | null;
  resourceType: string | null; syncType: string; status: string;
  startedAt: string; completedAt: string | null; itemsRead: number;
  itemsInserted: number; itemsUpdated: number; itemsFailed: number;
  errorMessage: string | null;
};

type KnowledgeReviewRepository = {
  repositoryId: number; repositoryFullName: string; reviewLinks: number;
};

type KnowledgeQuality = {
  reviewLinks: number; repositoriesWithReviewLinks: number;
  repositories: KnowledgeReviewRepository[];
};

type DashboardData = {
  generatedAt: string;
  status: "healthy" | "running" | "attention" | "empty";
  trackingStatus: "active" | "running" | "attention" | "not_initialized";
  latestDataSync: string | null; totals: Totals; syncOverview?: SyncOverview; knowledgeQuality: KnowledgeQuality;
  repositories: Repository[]; syncStates: SyncState[]; recentRuns: SyncRun[];
};

type RunFilter = "all" | "failed" | "running" | "completed";
type RepositoryFilter = "all" | "active" | "paused" | "excluded";

const apiBaseUrl = "http://127.0.0.1:5088/api";
const apiUrl = `${apiBaseUrl}/dashboard`;
const numberFormatter = new Intl.NumberFormat("it-IT");
const dateFormatter = new Intl.DateTimeFormat("it-IT", {
  day: "2-digit", month: "short", hour: "2-digit", minute: "2-digit", second: "2-digit",
});

function formatNumber(value: number | undefined) {
  return value === undefined ? "—" : numberFormatter.format(value);
}

function formatDate(value: string | null | undefined) {
  return value ? dateFormatter.format(new Date(value)) : "—";
}

function formatDuration(startedAt: string, completedAt: string | null) {
  const end = completedAt ? new Date(completedAt).getTime() : Date.now();
  const seconds = Math.max(0, Math.round((end - new Date(startedAt).getTime()) / 1000));
  return seconds < 60 ? `${seconds}s` : `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
}

function humanizeResource(value: string | null) {
  if (!value) return "Generale";
  const labels: Record<string, string> = {
    issues: "Issue e commenti", issue_comments: "Commenti", pull_requests: "Pull request",
    pull_request_files: "File PR", commits: "Commit del branch principale", commit_files: "File commit",
    repository: "Repository", repository_files: "Codice sorgente", code_symbols: "Struttura C#",
  };
  return labels[value] ?? value.replaceAll("_", " ");
}

function statusLabel(value: string) {
  const labels: Record<string, string> = {
    completed: "Completata", failed: "Errore", running: "In corso", pending: "In attesa",
  };
  return labels[value.toLowerCase()] ?? value;
}

async function readApiError(response: Response) {
  try {
    const payload = await response.json() as { message?: string; detail?: string; title?: string };
    return payload.message ?? payload.detail ?? payload.title ?? `operazione non riuscita (${response.status})`;
  } catch {
    return `operazione non riuscita (${response.status})`;
  }
}

export function DashboardClient() {
  const [data, setData] = useState<DashboardData | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [repositoryQuery, setRepositoryQuery] = useState("");
  const [repositoryFilter, setRepositoryFilter] = useState<RepositoryFilter>("all");
  const [selectedRepository, setSelectedRepository] = useState("all");
  const [runFilter, setRunFilter] = useState<RunFilter>("all");
  const [repositoryActionId, setRepositoryActionId] = useState<number | null>(null);
  const [managementMessage, setManagementMessage] = useState<string | null>(null);
  const [managementError, setManagementError] = useState<string | null>(null);
  const dashboardRequestId = useRef(0);

  const loadDashboard = useCallback(async () => {
    const requestId = ++dashboardRequestId.current;
    setRefreshing(true);
    try {
      const response = await fetch(apiUrl, { cache: "no-store" });
      if (!response.ok) throw new Error(`servizio non disponibile (${response.status})`);
      const nextData = await response.json() as DashboardData;
      if (requestId === dashboardRequestId.current) {
        setData(nextData);
        setError(null);
      }
    } catch (requestError) {
      if (requestId === dashboardRequestId.current) {
        setError(requestError instanceof Error ? requestError.message : "connessione non disponibile");
      }
    } finally {
      if (requestId === dashboardRequestId.current) {
        setRefreshing(false);
      }
    }
  }, []);

  useEffect(() => {
    const initialLoad = window.setTimeout(() => void loadDashboard(), 0);
    const timer = window.setInterval(() => {
      if (document.visibilityState === "visible") void loadDashboard();
    }, 30_000);
    const handleVisibilityChange = () => {
      if (document.visibilityState === "visible") void loadDashboard();
    };
    document.addEventListener("visibilitychange", handleVisibilityChange);
    return () => {
      window.clearTimeout(initialLoad);
      window.clearInterval(timer);
      document.removeEventListener("visibilitychange", handleVisibilityChange);
    };
  }, [loadDashboard]);

  const overview = useMemo<SyncOverview>(() => {
    if (data?.syncOverview) return data.syncOverview;
    const states = data?.syncStates ?? [];
    const runs = data?.recentRuns ?? [];
    return {
      trackedResources: states.length,
      completedResources: states.filter(item => item.status === "completed").length,
      failedResources: states.filter(item => item.status === "failed").length,
      runningResources: states.filter(item => item.status === "running").length,
      pendingResources: states.filter(item => item.status === "pending").length,
      staleResources: 0,
      untrackedRepositories: 0,
      totalRuns: runs.length,
      failedRunsInHistory: runs.filter(item => item.status === "failed").length,
      latestRunAt: runs[0]?.startedAt ?? null,
    };
  }, [data]);

  const failedRuns = useMemo(() => {
    if (!data) return [];
    const failedStateKeys = new Set(data.syncStates
      .filter(state => state.status.toLowerCase() === "failed")
      .map(state => `${state.repositoryId}:${state.resourceType}`));
    return data.recentRuns.filter(run =>
      run.status.toLowerCase() === "failed" &&
      run.repositoryId != null && run.resourceType != null &&
      failedStateKeys.has(`${run.repositoryId}:${run.resourceType}`));
  }, [data]);

  const filteredRepositories = useMemo(() => {
    const query = repositoryQuery.trim().toLocaleLowerCase("it-IT");
    return data?.repositories.filter(repository => {
      const matchesQuery = !query ||
        repository.fullName.toLocaleLowerCase("it-IT").includes(query) ||
        repository.primaryLanguage?.toLocaleLowerCase("it-IT").includes(query);
      const matchesStatus = repositoryFilter === "all" ||
        (repositoryFilter === "active" && repository.isSyncEnabled && !repository.isExcluded) ||
        (repositoryFilter === "paused" && !repository.isSyncEnabled && !repository.isExcluded) ||
        (repositoryFilter === "excluded" && repository.isExcluded);
      return matchesQuery && matchesStatus;
    }) ?? [];
  }, [data, repositoryFilter, repositoryQuery]);

  async function setRepositorySynchronization(repository: Repository, enabled: boolean) {
    setRepositoryActionId(repository.id);
    setManagementMessage(null);
    setManagementError(null);
    try {
      const response = await fetch(`${apiBaseUrl}/repositories/${repository.id}/synchronization`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json", "X-iOneDataGrove-Management": "dashboard-local" },
        body: JSON.stringify({ enabled }),
      });
      if (!response.ok) throw new Error(await readApiError(response));
      setManagementMessage(enabled
        ? `${repository.fullName} verrà incluso nella prossima importazione.`
        : `${repository.fullName} è stato sospeso: i dati esistenti restano disponibili.`);
      await loadDashboard();
    } catch (requestError) {
      setManagementError(requestError instanceof Error ? requestError.message : "impostazione non salvata");
    } finally {
      setRepositoryActionId(null);
    }
  }

  async function deleteRepositoryData(repository: Repository) {
    const confirmed = window.confirm(
      `Cancellare tutti i dati importati di ${repository.fullName}?\n\n` +
      "Il repository resterà escluso dalle importazioni future e potrà essere ripristinato dalla dashboard.",
    );
    if (!confirmed) return;

    setRepositoryActionId(repository.id);
    setManagementMessage(null);
    setManagementError(null);
    try {
      const response = await fetch(`${apiBaseUrl}/repositories/${repository.id}`, {
        method: "DELETE",
        headers: { "Content-Type": "application/json", "X-iOneDataGrove-Management": "dashboard-local" },
        body: JSON.stringify({ repositoryFullName: repository.fullName }),
      });
      if (!response.ok) throw new Error(await readApiError(response));
      const result = await response.json() as { deletedRecords: number };
      setManagementMessage(
        `${repository.fullName} escluso; cancellati ${formatNumber(result.deletedRecords)} record importati.`,
      );
      await loadDashboard();
    } catch (requestError) {
      setManagementError(requestError instanceof Error ? requestError.message : "dati non cancellati");
    } finally {
      setRepositoryActionId(null);
    }
  }

  async function restoreRepository(repository: Repository) {
    setRepositoryActionId(repository.id);
    setManagementMessage(null);
    setManagementError(null);
    try {
      const response = await fetch(`${apiBaseUrl}/repositories/${repository.id}/restore`, {
        method: "POST",
        headers: { "X-iOneDataGrove-Management": "dashboard-local" },
      });
      if (!response.ok) throw new Error(await readApiError(response));
      setManagementMessage(
        `${repository.fullName} ripristinato: i dati verranno acquisiti alla prossima importazione.`,
      );
      await loadDashboard();
    } catch (requestError) {
      setManagementError(requestError instanceof Error ? requestError.message : "repository non ripristinato");
    } finally {
      setRepositoryActionId(null);
    }
  }

  const filteredRuns = useMemo(() => data?.recentRuns.filter(run => {
    const matchesStatus = runFilter === "all" || run.status.toLowerCase() === runFilter;
    const matchesRepository = selectedRepository === "all" ||
      String(run.repositoryId) === selectedRepository;
    return matchesStatus && matchesRepository;
  }) ?? [], [data, runFilter, selectedRepository]);

  const filteredStates = useMemo(() => data?.syncStates.filter(state =>
    selectedRepository === "all" || String(state.repositoryId) === selectedRepository,
  ) ?? [], [data, selectedRepository]);

  const statusCopy = useMemo(() => {
    if (error) return { title: "Servizio locale non raggiungibile", text: "Avvia il servizio della dashboard per leggere PostgreSQL.", tone: "attention" };
    if (!data) return { title: "Lettura dati in corso", text: "Sto interrogando PostgreSQL in sola lettura.", tone: "loading" };
    if (data.status === "attention") {
      const attentionCount = overview.failedResources + overview.staleResources + overview.untrackedRepositories;
      return { title: "Importazione da verificare", text: `${attentionCount} risorse o repository richiedono attenzione. I dettagli sono disponibili qui sotto.`, tone: "attention" };
    }
    if (data.status === "running") return { title: "Importazione in corso", text: "I dati si stanno aggiornando. La pagina si aggiorna automaticamente.", tone: "running" };
    if (data.status === "empty") return { title: "Pronto per la prima importazione", text: "La connessione è attiva, ma non sono ancora presenti repository.", tone: "empty" };
    return { title: "Dati disponibili", text: "PostgreSQL è disponibile e le sincronizzazioni monitorate risultano recenti.", tone: "healthy" };
  }, [data, error, overview.failedResources, overview.staleResources, overview.untrackedRepositories]);

  const metrics = [
    { label: "Record importati", value: data?.totals.records, note: "Tabelle GitHub, inclusi i sorgenti" },
    { label: "Issue", value: data?.totals.issues, note: `${formatNumber(data?.totals.openIssues)} aperte` },
    { label: "Pull request", value: data?.totals.pullRequests, note: `${formatNumber(data?.totals.mergedPullRequests)} unite` },
    { label: "Codice sorgente", value: data?.totals.sourceFiles, note: "file attivi importati" },
  ];

  const composition = data ? [
    { label: "Issue", value: data.totals.issues, color: "#6f9f78" },
    { label: "Pull request", value: data.totals.pullRequests, color: "#315f71" },
    { label: "Commit", value: data.totals.commits, color: "#c08a55" },
    { label: "Commenti", value: data.totals.comments, color: "#8c76a5" },
    { label: "Utenti", value: data.totals.users, color: "#7f8d98" },
    { label: "File sorgente", value: data.totals.sourceFiles, color: "#b3b86b" },
  ] : [];
  const compositionTotal = composition.reduce((sum, item) => sum + item.value, 0);
  const completionPercent = overview.trackedResources
    ? Math.round((overview.completedResources / overview.trackedResources) * 100)
    : 0;
  const repositoryManagementLocked = overview.runningResources > 0;
  const attentionItemCount = overview.failedResources + overview.staleResources + overview.untrackedRepositories;
  const knowledgeQuality = data?.knowledgeQuality;

  return (
    <main className="app-shell">
      <aside className="sidebar">
        <div className="brand-mark" aria-label="iOne Data Grove">
          <span className="brand-symbol">iO</span>
          <span><strong>Data Grove</strong><small>Import control</small></span>
        </div>
        <nav className="nav-list" aria-label="Navigazione principale">
          <a className="nav-item active" href="#panoramica">Panoramica</a>
          <a className="nav-item" href="/search">Ricerca</a>
          <a className="nav-item" href="#sorgenti">Sorgenti</a>
          <a className="nav-item" href="#qualita">Qualità</a>
          <a className="nav-item" href="#repository">Repository</a>
          <a className="nav-item" href="#attivita">Attività</a>
        </nav>
        <div className="sidebar-foot"><span className="pulse-dot" />Servizio locale</div>
      </aside>

      <section className="workspace">
        <header className="topbar">
          <div><p className="eyebrow">Import intelligence</p><h1>Panoramica</h1></div>
          <div className="topbar-actions">
            <span className="auto-refresh"><span /> Auto · 30 sec</span>
            <button className="refresh-button" type="button" onClick={() => void loadDashboard()} disabled={refreshing}>
              <span className={refreshing ? "spin" : ""}>↻</span> {refreshing ? "Aggiorno" : "Aggiorna"}
            </button>
          </div>
        </header>

        <div className="content" id="panoramica">
          {error && <div className="error-banner"><strong>Connessione sospesa.</strong> {error}. La pagina continuerà a riprovare automaticamente.</div>}

          <section className={`welcome-panel status-${statusCopy.tone}`}>
            <div>
              <p className="section-kicker">STATO DEL SISTEMA</p>
              <h2>{statusCopy.title}</h2>
              <p>{statusCopy.text}</p>
              <div className="hero-meta">
                <span>Ultimo dato: <strong>{formatDate(data?.latestDataSync)}</strong></span>
                <span>Ultima esecuzione: <strong>{formatDate(overview.latestRunAt)}</strong></span>
              </div>
            </div>
            <div className="status-orbit" aria-hidden="true">
              <span className="orbit-ring" /><span className="orbit-core">{data?.totals.activeRepositories ?? "—"}</span><small>repository attivi</small>
            </div>
          </section>

          <section className="metrics-grid metrics-four" aria-label="Indicatori principali">
            {metrics.map(metric => <article className="metric" key={metric.label}><p>{metric.label}</p><strong>{formatNumber(metric.value)}</strong><span>{metric.note}</span></article>)}
          </section>

          <section className="health-strip" aria-label="Salute delle sincronizzazioni">
            <article><span>Copertura</span><strong>{formatNumber(overview.completedResources)} / {formatNumber(overview.trackedResources)}</strong><small>{completionPercent}% completato</small></article>
            <article><span>Da verificare</span><strong className={attentionItemCount ? "danger-value" : ""}>{formatNumber(attentionItemCount)}</strong><small>errori, dati non recenti o non tracciati</small></article>
            <article><span>In esecuzione</span><strong>{formatNumber(overview.runningResources)}</strong><small>risorse attive ora</small></article>
            <article><span>Esecuzioni</span><strong>{formatNumber(overview.totalRuns)}</strong><small>nello storico</small></article>
          </section>

          <section className={`knowledge-overview-panel${knowledgeQuality?.reviewLinks ? " has-review-items" : ""}`} id="qualita" aria-label="Qualità dei collegamenti">
            <div className="panel-heading knowledge-overview-heading">
              <div><p className="section-kicker">QUALITÀ COLLEGAMENTI</p><h3>Riferimenti testuali da verificare</h3><p className="panel-description">Citazioni prive di una formula esplicita di chiusura, raggruppate per repository. I normali merge commit sono già esclusi.</p></div>
              <div className="knowledge-overview-total"><strong>{formatNumber(knowledgeQuality?.reviewLinks)}</strong><small>in {formatNumber(knowledgeQuality?.repositoriesWithReviewLinks)} repository</small></div>
            </div>
            {!data && <div className="knowledge-overview-empty"><span className="source-loader" /><span>Controllo dei collegamenti…</span></div>}
            {data && knowledgeQuality && knowledgeQuality.reviewLinks === 0 && <div className="knowledge-overview-empty knowledge-overview-ok"><strong>Nessun riferimento richiede revisione</strong><span>I collegamenti testuali riconosciuti risultano classificati correttamente.</span></div>}
            {knowledgeQuality && knowledgeQuality.repositories.length > 0 && <div className="knowledge-overview-list">
              {knowledgeQuality.repositories.map(repository => <a key={repository.repositoryId} href={`/repositories/${repository.repositoryId}#tab=links&relation=references&review=required`}>
                <span><strong>{repository.repositoryFullName}</strong><small>Apri direttamente i riferimenti da controllare</small></span>
                <span className="knowledge-overview-count"><strong>{formatNumber(repository.reviewLinks)}</strong><small>da verificare →</small></span>
              </a>)}
            </div>}
          </section>

          {failedRuns.length > 0 && (
            <section className="incident-panel" aria-label="Errori di sincronizzazione">
              <div className="incident-heading"><div><p className="section-kicker">AZIONE RICHIESTA</p><h3>{failedRuns.length} sincronizzazioni non riuscite</h3></div><button type="button" onClick={() => { setRunFilter("failed"); document.querySelector("#attivita")?.scrollIntoView(); }}>Mostra solo errori</button></div>
              <div className="incident-list">
                {failedRuns.slice(0, 4).map(run => (
                  <article key={run.id}>
                    <span className="incident-mark">!</span>
                    <div><strong>{run.repositoryFullName ?? "Repository non indicato"}</strong><small>{humanizeResource(run.resourceType)} · {formatDate(run.startedAt)}</small><p>{run.errorMessage ?? "Errore senza dettaglio disponibile."}</p></div>
                  </article>
                ))}
              </div>
            </section>
          )}

          <section className="dashboard-grid">
            <article className="source-panel" id="sorgenti">
              <div className="panel-heading"><div><p className="section-kicker">SORGENTI</p><h3>Connettori dati</h3></div><span className="panel-count">1 attiva</span></div>
              <div className="source-row source-row-live">
                <div className="source-icon">GH</div>
                <div className="source-copy"><strong>GitHub</strong><span>Repository, issue, pull request, commit e file</span></div>
                <div className={`source-state ${data?.status === "attention" ? "state-attention" : ""}`}><span /> {error ? "Non raggiungibile" : data ? "Dati disponibili" : "Connessione"}</div>
              </div>
              <div className="tracking-row">
                <div><span className="tracking-label">Tracking incrementale</span><strong>{overview.completedResources} di {overview.trackedResources} risorse</strong></div>
                <div className="tracking-progress"><span style={{ width: `${completionPercent}%` }} /><small>{attentionItemCount ? `${attentionItemCount} da verificare` : "Tutto aggiornato"}</small></div>
              </div>
              <div className="future-source"><span>Pronta per crescere</span>Altre fonti potranno essere aggiunte come connettori indipendenti, mantenendo questa stessa vista operativa.</div>
            </article>

            <article className="composition-panel">
              <div className="panel-heading"><div><p className="section-kicker">COMPOSIZIONE</p><h3>Dati GitHub</h3></div></div>
              <div className="composition-body">
                <div className="composition-bar" aria-label="Composizione dei dati">{composition.map(item => <span key={item.label} style={{ width: `${compositionTotal ? (item.value / compositionTotal) * 100 : 0}%`, background: item.color }} />)}</div>
                <div className="legend-list">
                  {composition.map(item => <div className="legend-row" key={item.label}><span className="legend-dot" style={{ background: item.color }} /><span>{item.label}</span><strong>{formatNumber(item.value)}</strong></div>)}
                  {!data && <div className="skeleton-lines">Lettura indicatori…</div>}
                </div>
              </div>
            </article>
          </section>

          <section className="table-panel" id="repository">
            <div className="panel-heading table-heading">
              <div><p className="section-kicker">CATALOGO</p><h3>Gestione repository</h3><p className="panel-description">Sospendi gli aggiornamenti mantenendo i dati, oppure cancella ed escludi ciò che non deve essere acquisito.</p></div>
              <div className="table-tools">
                <label><span className="sr-only">Cerca repository</span><input type="search" value={repositoryQuery} onChange={event => setRepositoryQuery(event.target.value)} placeholder="Cerca repository o linguaggio" /></label>
                <label><span className="sr-only">Filtra repository per stato</span><select value={repositoryFilter} onChange={event => setRepositoryFilter(event.target.value as RepositoryFilter)}><option value="all">Tutti gli stati</option><option value="active">Attivi ({formatNumber(data?.totals.activeRepositories)})</option><option value="paused">In pausa ({formatNumber(data?.totals.pausedRepositories)})</option><option value="excluded">Esclusi ({formatNumber(data?.totals.excludedRepositories)})</option></select></label>
                <span className="panel-count">{filteredRepositories.length} / {formatNumber(data?.totals.repositories)}</span>
              </div>
            </div>
            {managementMessage && <div className="management-feedback management-success" role="status">{managementMessage}</div>}
            {managementError && <div className="management-feedback management-error" role="alert">{managementError}</div>}
            {repositoryManagementLocked && <div className="management-feedback management-info" role="status">Le azioni sui repository sono temporaneamente disabilitate mentre l’importazione è in corso.</div>}
            <div className="table-scroll">
              <table className="repository-management-table">
                <thead><tr><th>Repository</th><th>Acquisizione</th><th>Branch</th><th>Issue</th><th>Pull request</th><th>Commit</th><th>Ultimo dato</th><th>Azioni</th></tr></thead>
                <tbody>
                  {filteredRepositories.map(repository => (
                    <tr key={repository.id} className={repository.isExcluded ? "repository-row-excluded" : !repository.isSyncEnabled ? "repository-row-paused" : ""}>
                      <td>{repository.isExcluded ? <strong className="repository-name">{repository.fullName}</strong> : <a href={`/repositories/${repository.id}`}>{repository.fullName}</a>}<small>{repository.isPrivate ? "Privato" : "Pubblico"} · {repository.primaryLanguage ?? "n/d"}{!repository.isExcluded && " · Apri esploratore"}</small></td>
                      <td>{repository.isExcluded ? <><span className="repository-state repository-state-excluded">Escluso</span><small>dal {formatDate(repository.excludedAt)}</small></> : repository.isSyncEnabled ? <><span className="repository-state repository-state-active">Attivo</span><small>sincronizzazione abilitata</small></> : <><span className="repository-state repository-state-paused">In pausa</span><small>dal {formatDate(repository.syncDisabledAt)}</small></>}</td>
                      <td><code>{repository.defaultBranch ?? "—"}</code></td>
                      <td><strong>{formatNumber(repository.issues)}</strong><small>{formatNumber(repository.openIssues)} aperte</small></td>
                      <td><strong>{formatNumber(repository.pullRequests)}</strong><small>{formatNumber(repository.mergedPullRequests)} unite</small></td>
                      <td><strong>{formatNumber(repository.commits)}</strong></td><td>{repository.isExcluded ? "—" : formatDate(repository.syncedAt)}</td>
                      <td><div className="repository-actions">{repository.isExcluded ? <button type="button" onClick={() => void restoreRepository(repository)} disabled={repositoryActionId === repository.id || repositoryManagementLocked}>{repositoryActionId === repository.id ? "Ripristino…" : "Ripristina"}</button> : <><button type="button" onClick={() => void setRepositorySynchronization(repository, !repository.isSyncEnabled)} disabled={repositoryActionId === repository.id || repositoryManagementLocked}>{repositoryActionId === repository.id ? "Salvataggio…" : repository.isSyncEnabled ? "Sospendi" : "Riattiva"}</button><button className="danger-action" type="button" onClick={() => void deleteRepositoryData(repository)} disabled={repositoryActionId === repository.id || repositoryManagementLocked}>Cancella dati</button></>}</div></td>
                    </tr>
                  ))}
                  {data && filteredRepositories.length === 0 && <tr><td colSpan={8} className="table-empty">Nessun repository corrisponde ai filtri selezionati.</td></tr>}
                  {!data && <tr><td colSpan={8} className="table-empty">Caricamento repository…</td></tr>}
                </tbody>
              </table>
            </div>
          </section>

          <section className="activity-grid" id="attivita">
            <article className="runs-panel">
              <div className="activity-heading">
                <div><p className="section-kicker">ATTIVITÀ</p><h3>Sincronizzazioni</h3></div>
                <div className="activity-controls">
                  <div className="filter-tabs" aria-label="Filtra sincronizzazioni">
                    {(["all", "failed", "running", "completed"] as RunFilter[]).map(filter => <button key={filter} type="button" aria-pressed={runFilter === filter} onClick={() => setRunFilter(filter)}>{filter === "all" ? "Tutte" : filter === "failed" ? `Errori (${failedRuns.length})` : filter === "running" ? "In corso" : "Completate"}</button>)}
                  </div>
                  <select aria-label="Filtra per repository" value={selectedRepository} onChange={event => setSelectedRepository(event.target.value)}>
                    <option value="all">Tutti i repository</option>
                    {data?.repositories.map(repository => <option key={repository.id} value={repository.id}>{repository.fullName}</option>)}
                  </select>
                </div>
              </div>
              {filteredRuns.length ? <div className="run-list">{filteredRuns.map(run => (
                <div className={`run-row ${run.status === "failed" ? "run-row-failed" : ""}`} key={run.id}>
                  <span className={`run-status status-${run.status.toLowerCase()}`} />
                  <div className="run-main"><strong>{run.repositoryFullName ?? "Repository non indicato"}</strong><small>{humanizeResource(run.resourceType)} · {run.syncType} · {formatDate(run.startedAt)}</small>{run.errorMessage && <p className="run-error">{run.errorMessage}</p>}</div>
                  <div className="run-numbers"><strong>{formatNumber(run.itemsRead)}</strong><small>letti</small></div>
                  <div className="run-numbers"><strong>+{formatNumber(run.itemsInserted)} / ~{formatNumber(run.itemsUpdated)}</strong><small>nuovi / aggiornati</small></div>
                  <div className="run-result"><span className={`state-badge badge-${run.status.toLowerCase()}`}>{statusLabel(run.status)}</span><small>{formatDuration(run.startedAt, run.completedAt)}</small></div>
                </div>
              ))}</div> : <div className="empty-state"><div className="empty-icon">↻</div><strong>Nessuna sincronizzazione trovata</strong><p>Modifica i filtri oppure attendi la prossima importazione.</p></div>}
            </article>

            <article className="state-panel">
              <div className="panel-heading"><div><p className="section-kicker">RISORSE</p><h3>Stato corrente</h3></div><span className="panel-count">{filteredStates.length}</span></div>
              {filteredStates.length ? <div className="state-list">{filteredStates.map(state => (
                <div className="state-row" key={`${state.repositoryId}-${state.resourceType}`}><div><strong>{state.repositoryFullName ?? "Repository non indicato"}</strong><small>{humanizeResource(state.resourceType)} · {formatDate(state.lastSuccessfulSync)}</small></div><span className={`state-badge badge-${state.status.toLowerCase()}`}>{statusLabel(state.status)}</span></div>
              ))}</div> : <div className="empty-state compact"><div className="empty-icon">○</div><strong>Nessuno stato disponibile</strong><p>Il filtro selezionato non contiene risorse.</p></div>}
            </article>
          </section>
        </div>
      </section>
    </main>
  );
}
