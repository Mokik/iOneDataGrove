"use client";

import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from "react";
import Link from "next/link";
import { SourceCodeExplorer } from "./SourceCodeExplorer";
import { KnowledgeLinksExplorer } from "./KnowledgeLinksExplorer";

import { linksHref, sourceHref, subscribeNavigation, readNavigation, emptyNavigation } from "./explorer-navigation";

type Tab = "overview" | "source" | "links" | "issues" | "pulls" | "commits" | "files";
type StateFilter = "all" | "open" | "closed" | "merged";

type RepositoryDetail = {
  id: number; fullName: string; description: string | null; htmlUrl: string;
  isPrivate: boolean; isArchived: boolean; defaultBranch: string | null;
  isSyncEnabled: boolean; syncDisabledAt: string | null;
  isExcluded: boolean; excludedAt: string | null;
  primaryLanguage: string | null; createdAt: string | null; pushedAt: string | null;
  syncedAt: string; issues: number; openIssues: number; pullRequests: number;
  openPullRequests: number; mergedPullRequests: number; commits: number; files: number; sourceFiles: number;
};
type RepositoryOption = { id: number; fullName: string };
type SyncState = { resourceType: string; status: string; lastSuccessfulSync: string | null; lastGithubUpdatedAt: string | null; updatedAt: string };
type Issue = { id: number; number: number; title: string; state: string; author: string | null; comments: number; createdAt: string; updatedAt: string; closedAt: string | null; htmlUrl: string | null };
type PullRequest = { id: number; number: number; title: string; state: string; merged: boolean; isDraft: boolean; author: string | null; baseBranch: string | null; headBranch: string | null; commits: number | null; changedFiles: number | null; additions: number | null; deletions: number | null; createdAt: string; updatedAt: string; mergedAt: string | null; htmlUrl: string | null };
type Commit = { id: number; sha: string; message: string; author: string | null; authoredAt: string | null; committedAt: string | null; additions: number | null; deletions: number | null; filesChanged: number | null; htmlUrl: string | null };
type FileItem = { filename: string; touches: number; additions: number; deletions: number; changes: number; lastTouchedAt: string | null };
type ExplorerData = { generatedAt: string; repository: RepositoryDetail; repositoryOptions: RepositoryOption[]; syncStates: SyncState[]; issues: Issue[]; pullRequests: PullRequest[]; commits: Commit[]; files: FileItem[]; resultCounts: { issues: number; pullRequests: number; commits: number; files: number; sourceFiles: number } };
type VerificationCheck = { key: string; label: string; status: "healthy" | "warning" | "error"; summary: string; count: number };
type VerificationFinding = { category: string; severity: "warning" | "error"; title: string; detail: string; entityType: string; entityNumber: number | null; htmlUrl: string | null };
type VerificationData = { checkedAt: string; repositoryId: number; repositoryFullName: string; status: "healthy" | "attention" | "error"; summary: string; errorCount: number; warningCount: number; gitHubRateLimitRemaining: string | null; checks: VerificationCheck[]; findings: VerificationFinding[] };

const numberFormatter = new Intl.NumberFormat("it-IT");
const dateFormatter = new Intl.DateTimeFormat("it-IT", { day: "2-digit", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit" });
const formatDate = (value: string | null | undefined) => value ? dateFormatter.format(new Date(value)) : "—";
const formatNumber = (value: number | null | undefined) => value == null ? "—" : numberFormatter.format(value);
const resourceLabel = (value: string) => ({ issues: "Issue e commenti", pull_requests: "Pull request", commits: "Commit del branch principale", repository_files: "Codice sorgente", code_symbols: "Struttura C#", knowledge_links: "Collegamenti automatici" }[value] ?? value.replaceAll("_", " "));

export function RepositoryExplorerClient({ repositoryId }: { repositoryId: number }) {
  const [data, setData] = useState<ExplorerData | null>(null);
  const navigation = useSyncExternalStore(subscribeNavigation, readNavigation, emptyNavigation);
  const parameters = new URLSearchParams(navigation);
  const requestedTab = parameters.get("tab");
  const tab = (["overview", "source", "links", "issues", "pulls", "commits", "files"].includes(requestedTab ?? "") ? requestedTab : "overview") as Tab;
  const focusId = Number(parameters.get("focusId"));
  const focus = parameters.get("focusType") && Number.isSafeInteger(focusId) && focusId > 0
    ? { type: parameters.get("focusType")!, id: focusId, label: parameters.get("label") ?? "Elemento selezionato" } : null;
  const sourcePath = parameters.get("path");
  const sourceLine = Number(parameters.get("line")) || null;
  const initialLinkRelation = parameters.get("relation") === "references" ? "references" : "all";
  const initialReviewOnly = parameters.get("review") === "required";
  const [query, setQuery] = useState("");
  const [submittedQuery, setSubmittedQuery] = useState("");
  const [stateFilter, setStateFilter] = useState<StateFilter>("all");
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [verification, setVerification] = useState<VerificationData | null>(null);
  const [verifying, setVerifying] = useState(false);
  const [verificationError, setVerificationError] = useState<string | null>(null);
  const explorerRequestId = useRef(0);

  const load = useCallback(async (search = submittedQuery, state = stateFilter) => {
    const requestId = ++explorerRequestId.current;
    setLoading(true);
    const parameters = new URLSearchParams();
    if (search.trim()) parameters.set("q", search.trim());
    if (state !== "all") parameters.set("state", state);
    try {
      const response = await fetch(`http://127.0.0.1:5088/api/repositories/${repositoryId}?${parameters}`, { cache: "no-store" });
      if (!response.ok) throw new Error(response.status === 404 ? "Repository non trovato" : `servizio non disponibile (${response.status})`);
      const nextData = await response.json() as ExplorerData;
      if (requestId === explorerRequestId.current) {
        setData(nextData);
        setError(null);
      }
    } catch (requestError) {
      if (requestId === explorerRequestId.current) {
        setError(requestError instanceof Error ? requestError.message : "connessione non disponibile");
      }
    } finally {
      if (requestId === explorerRequestId.current) {
        setLoading(false);
      }
    }
  }, [repositoryId, stateFilter, submittedQuery]);

  useEffect(() => { const timer = window.setTimeout(() => void load(), 0); return () => window.clearTimeout(timer); }, [load]);

  const recentActivity = useMemo(() => {
    if (!data) return [];
    return [
      ...data.issues.slice(0, 6).map(item => ({ key: `i-${item.id}`, kind: "Issue", title: `#${item.number} ${item.title}`, date: item.updatedAt, tone: item.state })),
      ...data.pullRequests.slice(0, 6).map(item => ({ key: `p-${item.id}`, kind: "Pull request", title: `#${item.number} ${item.title}`, date: item.updatedAt, tone: item.merged ? "merged" : item.state })),
      ...data.commits.slice(0, 6).map(item => ({ key: `c-${item.id}`, kind: "Commit", title: item.message.split("\n")[0], date: item.committedAt ?? item.authoredAt ?? data.generatedAt, tone: "commit" })),
    ].sort((a, b) => new Date(b.date).getTime() - new Date(a.date).getTime()).slice(0, 10);
  }, [data]);

  function submitSearch(event: React.FormEvent) {
    event.preventDefault();
    setSubmittedQuery(query.trim());
  }

  function changeState(value: StateFilter) {
    setStateFilter(value);
  }

  function changeTab(nextTab: Tab) {
    window.location.assign(`#tab=${nextTab}`);
    if ((nextTab !== "issues" && nextTab !== "pulls") ||
        (nextTab === "issues" && stateFilter === "merged")) {
      setStateFilter("all");
    }
  }

  async function verifyRepository() {
    setVerifying(true);
    setVerificationError(null);
    try {
      const response = await fetch(`http://127.0.0.1:5088/api/repositories/${repositoryId}/verify`, { cache: "no-store" });
      if (!response.ok) throw new Error(`verifica non disponibile (${response.status})`);
      setVerification(await response.json() as VerificationData);
    } catch (requestError) {
      setVerificationError(requestError instanceof Error ? requestError.message : "verifica non disponibile");
    } finally {
      setVerifying(false);
    }
  }

  const repository = data?.repository;
  const tabs: { id: Tab; label: string; count?: number }[] = [
    { id: "overview", label: "Panoramica" },
    { id: "source", label: "Codice", count: data?.resultCounts.sourceFiles },
    { id: "links", label: "Collegamenti" },
    { id: "issues", label: "Issue", count: data?.resultCounts.issues },
    { id: "pulls", label: "Pull request", count: data?.resultCounts.pullRequests },
    { id: "commits", label: "Commit", count: data?.resultCounts.commits },
    { id: "files", label: "File", count: data?.resultCounts.files },
  ];

  return (
    <main className="explorer-shell">
      <aside className="explorer-sidebar">
        <Link className="brand-mark explorer-brand" href="/"><span className="brand-symbol">iO</span><span><strong>Data Grove</strong><small>Repository explorer</small></span></Link>
        <Link className="back-link" href="/">← Torna alla dashboard</Link>
        <div className="repo-switcher">
          <label htmlFor="repository-select">Repository con dati</label>
          <select id="repository-select" value={repositoryId} onChange={event => { window.location.href = `/repositories/${event.target.value}`; }} disabled={!data}>
            {!data && <option value={repositoryId}>Caricamento…</option>}
            {data?.repositoryOptions.map(option => <option value={option.id} key={option.id}>{option.fullName}</option>)}
          </select>
        </div>
        <nav className="explorer-nav" aria-label="Sezioni repository">
          {tabs.map(item => <button key={item.id} type="button" className={tab === item.id ? "active" : ""} onClick={() => changeTab(item.id)}><span>{item.label}</span>{item.count != null && <small>{formatNumber(item.count)}</small>}</button>)}
        </nav>
        <div className="sidebar-foot"><span className="pulse-dot" />PostgreSQL · gestione locale</div>
      </aside>

      <section className="explorer-workspace">
        <header className="explorer-topbar">
          <div><p className="eyebrow">Repository intelligence</p><h1>{repository?.fullName ?? "Caricamento repository"}</h1></div>
          {repository && <a className="github-link" href={repository.htmlUrl} target="_blank" rel="noreferrer">Apri su GitHub ↗</a>}
        </header>

        <div className="explorer-content">
          {error && <div className="error-banner"><strong>Impossibile leggere il repository.</strong> {error}</div>}
          <section className="repo-hero">
            <div><div className="repo-tags"><span>{repository?.isPrivate ? "Privato" : "Pubblico"}</span><span>{repository?.primaryLanguage ?? "Linguaggio n/d"}</span><span>branch {repository?.defaultBranch ?? "n/d"}</span>{repository && <span>{repository.isExcluded ? "Escluso" : repository.isSyncEnabled ? "Import attivo" : "Import in pausa"}</span>}</div><h2>{repository?.fullName.split("/").at(-1) ?? "Repository"}</h2><p>{repository?.description ?? "Nessuna descrizione disponibile per questo repository."}</p></div>
            <div className="repo-sync"><span className={data?.syncStates.some(item => item.status === "failed") ? "sync-light danger" : "sync-light"} /><div><small>Ultima lettura dati</small><strong>{formatDate(repository?.syncedAt)}</strong></div></div>
          </section>

          {tab !== "source" && tab !== "links" && <>
            <form className="explorer-search" onSubmit={submitSearch}>
              <label><span className="sr-only">Cerca nei dati del repository</span><input value={query} onChange={event => setQuery(event.target.value)} placeholder="Cerca titolo, autore, SHA o percorso file…" /></label>
              <button type="submit" disabled={loading}>{loading ? "Lettura…" : "Cerca"}</button>
              {submittedQuery && <button type="button" className="clear-search" onClick={() => { setQuery(""); setSubmittedQuery(""); }}>Azzera</button>}
              {(tab === "issues" || tab === "pulls") && <div className="state-filter">
                {(["all", "open", "closed", ...(tab === "pulls" ? ["merged"] : [])] as StateFilter[]).map(value => <button type="button" key={value} aria-pressed={stateFilter === value} onClick={() => changeState(value)}>{({ all: "Tutti", open: "Aperti", closed: "Chiusi", merged: "Unite" })[value]}</button>)}
              </div>}
            </form>
            {submittedQuery && <p className="search-context">Risultati per <strong>“{submittedQuery}”</strong>. I conteggi indicano tutte le corrispondenze.</p>}
          </>}

          {tab === "overview" && <>
            <section className={`verification-panel verification-${verification?.status ?? "idle"}`}>
              <div className="verification-intro">
                <div><p className="section-kicker">CONTROLLO QUALITÀ</p><h3>{verification ? verification.summary : "Verifica coerenza dei dati"}</h3><p>{verification ? `Controllo completato il ${formatDate(verification.checkedAt)}.` : "Confronta PostgreSQL con GitHub e controlla completezza, stati e aggiornamento delle sincronizzazioni."}</p></div>
                <button type="button" onClick={() => void verifyRepository()} disabled={verifying}>{verifying ? "Verifica in corso…" : verification ? "Ripeti verifica" : "Verifica adesso"}</button>
              </div>
              {verificationError && <div className="verification-request-error">{verificationError}. Riprova tra qualche momento.</div>}
              {verification && <>
                <div className="verification-checks">{verification.checks.map(check => <article key={check.key}><span className={`check-indicator check-${check.status}`}>{check.status === "healthy" ? "✓" : "!"}</span><div><strong>{check.label}</strong><p>{check.summary}</p></div></article>)}</div>
                {verification.findings.length > 0 ? <div className="finding-list">{verification.findings.map((finding, index) => <article key={`${finding.category}-${finding.entityType}-${finding.entityNumber ?? index}-${index}`} className={`finding-row finding-${finding.severity}`}><span>{finding.severity === "error" ? "!" : "△"}</span><div><strong>{finding.title}</strong><p>{finding.detail}</p></div>{finding.htmlUrl && <a href={finding.htmlUrl} target="_blank" rel="noreferrer">Apri ↗</a>}</article>)}</div> : <div className="verification-success"><span>✓</span><div><strong>Dati coerenti</strong><p>Il campione recente coincide con GitHub e non risultano collegamenti incompleti.</p></div></div>}
                <div className="verification-foot">Confronto live completo fino a 1.000 issue e pull request · Commit PR verificati per completezza locale · GitHub API rimanente: {verification.gitHubRateLimitRemaining ?? "n/d"}</div>
              </>}
            </section>
            <section className="repo-metrics">
              <article><span>Issue</span><strong>{formatNumber(repository?.issues)}</strong><small>{formatNumber(repository?.openIssues)} aperte</small></article>
              <article><span>Pull request</span><strong>{formatNumber(repository?.pullRequests)}</strong><small>{formatNumber(repository?.mergedPullRequests)} unite</small></article>
              <article><span>Commit</span><strong>{formatNumber(repository?.commits)}</strong><small>storico importato</small></article>
              <article><span>Codice sorgente</span><strong>{formatNumber(repository?.sourceFiles)}</strong><small>file attivi importati</small></article>
            </section>
            <section className="overview-grid">
              <article className="explorer-panel"><div className="explorer-panel-heading"><div><p className="section-kicker">ATTIVITÀ</p><h3>Ultimi eventi</h3></div><span className="panel-count">{recentActivity.length}</span></div><div className="timeline-list">{recentActivity.map(item => <div className="timeline-row" key={item.key}><span className={`timeline-dot tone-${item.tone}`} /><div><small>{item.kind}</small><strong>{item.title}</strong></div><time>{formatDate(item.date)}</time></div>)}</div></article>
              <article className="explorer-panel"><div className="explorer-panel-heading"><div><p className="section-kicker">SINCRONIZZAZIONE</p><h3>Risorse monitorate</h3></div></div><div className="sync-list">{data?.syncStates.map(item => <div className="sync-row" key={item.resourceType}><div><strong>{resourceLabel(item.resourceType)}</strong><small>{formatDate(item.lastSuccessfulSync)}</small></div><span className={`state-badge badge-${item.status}`}>{item.status === "completed" ? "Completata" : item.status}</span></div>)}</div></article>
            </section>
            <section className="explorer-panel top-files-panel"><div className="explorer-panel-heading"><div><p className="section-kicker">HOTSPOT</p><h3>File più toccati</h3></div><button type="button" onClick={() => changeTab("files")}>Vedi tutti</button></div><FileTable items={data?.files.slice(0, 10) ?? []} /></section>
          </>}

          {tab === "source" && <SourceCodeExplorer key={`${repositoryId}-${navigation}`} repositoryId={repositoryId} initialPath={sourcePath} initialLine={sourceLine} />}
          {tab === "links" && <KnowledgeLinksExplorer key={`${repositoryId}-${navigation}`} repositoryId={repositoryId} focus={focus} path={sourcePath} initialRelation={initialLinkRelation} initialReviewOnly={initialReviewOnly} />}
          {tab === "issues" && <DataPanel title="Issue importate" shown={data?.issues.length ?? 0} total={data?.resultCounts.issues ?? 0}>{<IssueTable items={data?.issues ?? []} />}</DataPanel>}
          {tab === "pulls" && <DataPanel title="Pull request importate" shown={data?.pullRequests.length ?? 0} total={data?.resultCounts.pullRequests ?? 0}>{<PullTable items={data?.pullRequests ?? []} />}</DataPanel>}
          {tab === "commits" && <DataPanel title="Commit importati" shown={data?.commits.length ?? 0} total={data?.resultCounts.commits ?? 0}>{<CommitTable items={data?.commits ?? []} />}</DataPanel>}
          {tab === "files" && <DataPanel title="File nei commit" shown={data?.files.length ?? 0} total={data?.resultCounts.files ?? 0}>{<FileTable items={data?.files ?? []} />}</DataPanel>}
        </div>
      </section>
    </main>
  );
}

function DataPanel({ title, shown, total, children }: { title: string; shown: number; total: number; children: React.ReactNode }) {
  return <section className="explorer-panel data-panel"><div className="explorer-panel-heading"><div><p className="section-kicker">DATI IMPORTATI</p><h3>{title}</h3></div><span className="panel-count">{shown < total ? `${formatNumber(shown)} di ${formatNumber(total)}` : formatNumber(total)}</span></div>{children}</section>;
}

function EmptyRows({ columns }: { columns: number }) { return <tbody><tr><td colSpan={columns} className="table-empty">Nessun dato corrisponde ai filtri selezionati.</td></tr></tbody>; }

function ExternalItemLink({ href, children }: { href: string | null; children: React.ReactNode }) {
  return href ? <a href={href} target="_blank" rel="noreferrer">{children}</a> : <strong>{children}</strong>;
}
function IssueTable({ items }: { items: Issue[] }) { return <div className="table-scroll"><table><thead><tr><th>Issue</th><th>Stato</th><th>Autore</th><th>Commenti</th><th>Aggiornata</th></tr></thead>{items.length ? <tbody>{items.map(item => <tr key={item.id}><td><ExternalItemLink href={item.htmlUrl}>#{item.number} · {item.title}</ExternalItemLink><a className="entity-navigation-link" href={linksHref({ type: "issue", id: item.id, label: `#${item.number} · ${item.title}` })}>Esplora collegamenti →</a></td><td><span className={`state-badge badge-${item.state}`}>{item.state}</span></td><td>{item.author ?? "—"}</td><td>{formatNumber(item.comments)}</td><td>{formatDate(item.updatedAt)}</td></tr>)}</tbody> : <EmptyRows columns={5} />}</table></div>; }
function PullTable({ items }: { items: PullRequest[] }) { return <div className="table-scroll"><table><thead><tr><th>Pull request</th><th>Stato</th><th>Autore</th><th>Branch</th><th>Modifiche</th><th>Aggiornata</th></tr></thead>{items.length ? <tbody>{items.map(item => <tr key={item.id}><td><ExternalItemLink href={item.htmlUrl}>#{item.number} · {item.title}</ExternalItemLink><a className="entity-navigation-link" href={linksHref({ type: "pull_request", id: item.id, label: `#${item.number} · ${item.title}` })}>Esplora collegamenti →</a><small>{item.isDraft ? "Bozza" : `${formatNumber(item.commits)} commit`}</small></td><td><span className={`state-badge badge-${item.merged ? "merged" : item.state}`}>{item.merged ? "unita" : item.state}</span></td><td>{item.author ?? "—"}</td><td><code>{item.headBranch ?? "—"}</code><small>→ {item.baseBranch ?? "—"}</small></td><td><span className="diff-plus">+{formatNumber(item.additions)}</span> <span className="diff-minus">−{formatNumber(item.deletions)}</span><small>{formatNumber(item.changedFiles)} file</small></td><td>{formatDate(item.updatedAt)}</td></tr>)}</tbody> : <EmptyRows columns={6} />}</table></div>; }
function CommitTable({ items }: { items: Commit[] }) { return <div className="table-scroll"><table><thead><tr><th>Commit</th><th>Autore</th><th>Modifiche</th><th>Data</th></tr></thead>{items.length ? <tbody>{items.map(item => <tr key={item.id}><td><ExternalItemLink href={item.htmlUrl}>{item.message.split("\n")[0]}</ExternalItemLink><a className="entity-navigation-link" href={linksHref({ type: "commit", id: item.id, label: item.sha.slice(0, 8) })}>Esplora collegamenti →</a><small><code>{item.sha.slice(0, 8)}</code></small></td><td>{item.author ?? "—"}</td><td><span className="diff-plus">+{formatNumber(item.additions)}</span> <span className="diff-minus">−{formatNumber(item.deletions)}</span><small>{formatNumber(item.filesChanged)} file</small></td><td>{formatDate(item.committedAt ?? item.authoredAt)}</td></tr>)}</tbody> : <EmptyRows columns={4} />}</table></div>; }
function FileTable({ items }: { items: FileItem[] }) { return <div className="table-scroll"><table className="files-table"><thead><tr><th>Percorso</th><th>Occorrenze</th><th>Modifiche</th><th>Ultimo tocco</th></tr></thead>{items.length ? <tbody>{items.map(item => <tr key={item.filename}><td><a href={sourceHref(item.filename)}><code>{item.filename}</code></a></td><td>{formatNumber(item.touches)}</td><td><span className="diff-plus">+{formatNumber(item.additions)}</span> <span className="diff-minus">−{formatNumber(item.deletions)}</span></td><td>{formatDate(item.lastTouchedAt)}</td></tr>)}</tbody> : <EmptyRows columns={4} />}</table></div>; }
