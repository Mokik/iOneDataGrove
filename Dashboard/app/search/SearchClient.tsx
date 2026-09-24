"use client";

import Link from "next/link";
import { useCallback, useEffect, useRef, useState } from "react";

type RepositoryOption = { id: number; fullName: string; isExcluded: boolean };
type SearchResult = {
  entityType: string; entityId: number; repositoryId: number; repositoryFullName: string;
  title: string; snippet: string; htmlUrl: string | null; sourcePath: string | null;
  sourceLine: number | null; updatedAt: string; relevance: number;
};
type SearchCatalog = {
  query: string; repositoryId: number | null; entityType: string | null;
  matchedResults: number; page: number; pageSize: number; totalPages: number;
  results: SearchResult[];
};
type DashboardCatalog = { repositories: RepositoryOption[] };

const apiBaseUrl = "http://127.0.0.1:5088/api";
const numberFormatter = new Intl.NumberFormat("it-IT");
const dateFormatter = new Intl.DateTimeFormat("it-IT", { day: "2-digit", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit" });
const typeLabels: Record<string, string> = {
  all: "Tutti", repository: "Repository", issue: "Issue", issue_comment: "Commenti",
  pull_request: "Pull request", commit: "Commit", repository_file: "File", code_symbol: "Simboli C#",
};
const examples = ["FiltersArgs", "ParticolareImportRete", "RegistriTestataController"];

function resultHref(result: SearchResult) {
  if (result.entityType === "repository") return `/repositories/${result.repositoryId}`;
  if ((result.entityType === "repository_file" || result.entityType === "code_symbol") && result.sourcePath) {
    const parameters = new URLSearchParams({ tab: "source", path: result.sourcePath });
    if (result.sourceLine) parameters.set("line", String(result.sourceLine));
    return `/repositories/${result.repositoryId}#${parameters}`;
  }
  const parameters = new URLSearchParams({
    tab: "links", focusType: result.entityType, focusId: String(result.entityId), label: result.title,
  });
  return `/repositories/${result.repositoryId}#${parameters}`;
}

function HighlightedSnippet({ text }: { text: string }) {
  const parts = text.split(/(⟦.*?⟧)/g).filter(Boolean);
  return <>{parts.map((part, index) => part.startsWith("⟦") && part.endsWith("⟧")
    ? <mark key={index}>{part.slice(1, -1)}</mark>
    : <span key={index}>{part}</span>)}</>;
}

export function SearchClient() {
  const [repositories, setRepositories] = useState<RepositoryOption[]>([]);
  const [query, setQuery] = useState("");
  const [submittedQuery, setSubmittedQuery] = useState("");
  const [repositoryId, setRepositoryId] = useState("all");
  const [entityType, setEntityType] = useState("all");
  const [page, setPage] = useState(1);
  const [searchRevision, setSearchRevision] = useState(0);
  const [catalog, setCatalog] = useState<SearchCatalog | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const requestId = useRef(0);

  useEffect(() => {
    const controller = new AbortController();
    async function loadRepositories() {
      try {
        const response = await fetch(`${apiBaseUrl}/dashboard`, { cache: "no-store", signal: controller.signal });
        if (!response.ok) throw new Error(`servizio non disponibile (${response.status})`);
        const dashboard = await response.json() as DashboardCatalog;
        setRepositories(dashboard.repositories.filter(repository => !repository.isExcluded));
      } catch (requestError) {
        if (!controller.signal.aborted) setError(requestError instanceof Error ? requestError.message : "catalogo non disponibile");
      }
    }
    void loadRepositories();
    return () => controller.abort();
  }, []);

  const loadSearch = useCallback(async () => {
    if (!submittedQuery) return;
    const currentRequest = ++requestId.current;
    setLoading(true);
    try {
      const parameters = new URLSearchParams({ q: submittedQuery, page: String(page), pageSize: "20" });
      if (repositoryId !== "all") parameters.set("repositoryId", repositoryId);
      if (entityType !== "all") parameters.set("entityType", entityType);
      const response = await fetch(`${apiBaseUrl}/search?${parameters}`, { cache: "no-store" });
      if (!response.ok) {
        const payload = await response.json().catch(() => ({})) as { message?: string; detail?: string };
        throw new Error(payload.message ?? payload.detail ?? `ricerca non disponibile (${response.status})`);
      }
      const nextCatalog = await response.json() as SearchCatalog;
      if (currentRequest === requestId.current) {
        setCatalog(nextCatalog);
        setError(null);
      }
    } catch (requestError) {
      if (currentRequest === requestId.current) setError(requestError instanceof Error ? requestError.message : "ricerca non disponibile");
    } finally {
      if (currentRequest === requestId.current) setLoading(false);
    }
  }, [submittedQuery, repositoryId, entityType, page]);

  useEffect(() => { const timer = window.setTimeout(() => void loadSearch(), 0); return () => window.clearTimeout(timer); }, [loadSearch, searchRevision]);

  function submit(event: React.FormEvent) {
    event.preventDefault();
    const normalized = query.trim();
    if (normalized.length < 2) {
      setError("Inserisci almeno due caratteri da cercare.");
      return;
    }
    setPage(1);
    setSubmittedQuery(normalized);
    setSearchRevision(value => value + 1);
  }

  function selectExample(value: string) {
    setQuery(value);
    setPage(1);
    setSubmittedQuery(value);
    setSearchRevision(revision => revision + 1);
  }

  return <main className="search-shell">
    <aside className="search-sidebar">
      <Link className="brand-mark explorer-brand" href="/"><span className="brand-symbol">iO</span><span><strong>Data Grove</strong><small>Ricerca trasversale</small></span></Link>
      <nav className="search-nav" aria-label="Navigazione ricerca">
        <Link href="/">Panoramica</Link>
        <Link className="active" href="/search">Ricerca</Link>
      </nav>
      <div className="sidebar-foot"><span className="pulse-dot" />PostgreSQL · dati locali</div>
    </aside>

    <section className="search-workspace">
      <header className="search-topbar"><div><p className="eyebrow">Knowledge discovery</p><h1>Ricerca trasversale</h1></div><Link href="/">← Dashboard</Link></header>
      <div className="search-content">
        <section className="search-hero">
          <p className="section-kicker">CERCA IN TUTTO IL DATA GROVE</p>
          <h2>Issue, pull request, commit, codice e simboli in un solo punto.</h2>
          <p>La ricerca usa PostgreSQL e apre ogni risultato direttamente nel contesto del repository.</p>
          <form onSubmit={submit} className="global-search-form">
            <label><span className="sr-only">Testo da cercare</span><input value={query} onChange={event => setQuery(event.target.value)} placeholder="Es. nome metodo, ticket, SHA o testo della modifica" /></label>
            <button type="submit" disabled={loading}>{loading ? "Ricerca…" : "Cerca"}</button>
          </form>
          <div className="search-examples"><span>Prova:</span>{examples.map(example => <button type="button" key={example} onClick={() => selectExample(example)}>{example}</button>)}</div>
        </section>

        <section className="search-controls" aria-label="Filtri ricerca">
          <label><span>Repository</span><select value={repositoryId} onChange={event => { setRepositoryId(event.target.value); setPage(1); }}><option value="all">Tutti i repository</option>{repositories.map(repository => <option value={repository.id} key={repository.id}>{repository.fullName}</option>)}</select></label>
          <div className="search-type-filters" aria-label="Tipo di risultato">{Object.entries(typeLabels).map(([value, label]) => <button type="button" key={value} aria-pressed={entityType === value} onClick={() => { setEntityType(value); setPage(1); }}>{label}</button>)}</div>
        </section>

        {error && <div className="error-banner"><strong>Ricerca non completata.</strong> {error}</div>}
        {!submittedQuery && !error && <section className="search-empty"><span>⌕</span><h3>Inserisci un termine per iniziare</h3><p>Puoi cercare un concetto funzionale, un identificatore tecnico o una frase presente nei dati GitHub.</p></section>}
        {submittedQuery && <section className="search-results-panel">
          <div className="search-results-heading"><div><p className="section-kicker">RISULTATI</p><h3>“{submittedQuery}”</h3></div><span>{loading ? "Aggiornamento…" : `${numberFormatter.format(catalog?.matchedResults ?? 0)} trovati`}</span></div>
          {loading && !catalog && <div className="search-empty compact"><span className="source-loader" /><p>Interrogo PostgreSQL…</p></div>}
          {!loading && catalog?.results.length === 0 && <div className="search-empty compact"><span>○</span><h3>Nessuna corrispondenza</h3><p>Prova un termine diverso oppure amplia i filtri.</p></div>}
          {catalog && catalog.results.length > 0 && <div className="global-result-list">{catalog.results.map(result => <article key={`${result.entityType}-${result.entityId}`}>
            <div className="global-result-meta"><span>{typeLabels[result.entityType] ?? result.entityType}</span><Link href={`/repositories/${result.repositoryId}`}>{result.repositoryFullName}</Link><time>{dateFormatter.format(new Date(result.updatedAt))}</time></div>
            <h3><Link href={resultHref(result)}>{result.title}</Link></h3>
            <p><HighlightedSnippet text={result.snippet} /></p>
            <div className="global-result-actions"><Link href={resultHref(result)}>Apri nel contesto →</Link>{result.htmlUrl && <a href={result.htmlUrl} target="_blank" rel="noreferrer">GitHub ↗</a>}</div>
          </article>)}</div>}
          {catalog && catalog.totalPages > 1 && <nav className="global-search-pagination" aria-label="Pagine dei risultati"><button type="button" disabled={catalog.page <= 1 || loading} onClick={() => setPage(value => Math.max(1, value - 1))}>← Precedente</button><span>Pagina <strong>{catalog.page}</strong> di <strong>{catalog.totalPages}</strong></span><button type="button" disabled={catalog.page >= catalog.totalPages || loading} onClick={() => setPage(value => value + 1)}>Successiva →</button></nav>}
        </section>}
      </div>
    </section>
  </main>;
}

