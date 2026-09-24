"use client";

import { useEffect, useState } from "react";

import { linksHref, sourceHref, type EntityFocus } from "./explorer-navigation";

type LinkFacet = { relationType: string; count: number };
type KnowledgeLink = {
  id: number;
  sourceType: string;
  sourceId: number;
  sourceLabel: string;
  sourceUrl: string | null;
  targetType: string;
  targetId: number;
  targetLabel: string;
  targetUrl: string | null;
  relationType: string;
  evidenceType: string;
  evidenceText: string | null;
  refreshedAt: string;
  sourcePath: string | null;
  sourceLine: number | null;
  targetPath: string | null;
  targetLine: number | null;
  requiresReview: boolean;
};
type LinkCatalog = {
  repositoryId: number;
  totalLinks: number;
  matchedLinks: number;
  page: number;
  pageSize: number;
  totalPages: number;
  lastRefreshedAt: string | null;
  reviewLinks: number;
  facets: LinkFacet[];
  links: KnowledgeLink[];
};

const numberFormatter = new Intl.NumberFormat("it-IT");
const dateFormatter = new Intl.DateTimeFormat("it-IT", {
  day: "2-digit", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit",
});
const relationLabels: Record<string, string> = {
  references: "cita",
  closes: "chiude",
  contains_commit: "contiene il commit",
  modifies_file: "modifica il file",
  declares_symbol: "dichiara",
};
const entityLabels: Record<string, string> = {
  issue: "Issue",
  issue_comment: "Commento",
  pull_request: "Pull request",
  commit: "Commit",
  repository_file: "File",
  code_symbol: "Simbolo C#",
};
const relationMetrics = [
  { key: "closes", label: "Chiusure", description: "Issue chiuse esplicitamente" },
  { key: "references", label: "Riferimenti", description: "Citazioni testuali senza chiusura" },
  { key: "contains_commit", label: "Commit nelle PR", description: "Commit collegati alle pull request" },
  { key: "modifies_file", label: "File modificati", description: "Modifiche rilevate da GitHub" },
  { key: "declares_symbol", label: "Simboli C#", description: "Dichiarazioni nell’indice strutturale" },
] as const;

function facetCount(catalog: LinkCatalog, relationType: string) {
  return catalog.facets.find(facet => facet.relationType === relationType)?.count ?? 0;
}

function percentageLabel(count: number, total: number) {
  if (total === 0 || count === 0) return "0%";
  const percentage = (count / total) * 100;
  return percentage < 0.1 ? "<0,1%" : `${numberFormatter.format(Math.round(percentage * 10) / 10)}%`;
}


export function KnowledgeLinksExplorer({ repositoryId, focus, path, initialRelation = "all", initialReviewOnly = false }: { repositoryId: number; focus: EntityFocus | null; path: string | null; initialRelation?: string; initialReviewOnly?: boolean }) {
  const [catalog, setCatalog] = useState<LinkCatalog | null>(null);
  const [relation, setRelation] = useState(initialRelation);
  const [evidence, setEvidence] = useState("all");
  const [entityType, setEntityType] = useState("all");
  const [reviewOnly, setReviewOnly] = useState(initialReviewOnly);
  const [query, setQuery] = useState("");
  const [submittedQuery, setSubmittedQuery] = useState("");
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    async function load() {
      setLoading(true);
      try {
        const parameters = new URLSearchParams({ page: String(page), pageSize: "40" });
        if (focus) { parameters.set("focusType", focus.type); parameters.set("focusId", String(focus.id)); }
        if (path) parameters.set("path", path);
        if (submittedQuery) parameters.set("q", submittedQuery);
        if (relation !== "all") parameters.set("relation", relation);
        if (evidence !== "all") parameters.set("evidence", evidence);
        if (entityType !== "all") parameters.set("entityType", entityType);
        if (reviewOnly) parameters.set("review", "true");
        const response = await fetch(
          `http://127.0.0.1:5088/api/repositories/${repositoryId}/links?${parameters}`,
          { cache: "no-store", signal: controller.signal },
        );
        if (!response.ok) throw new Error(`servizio non disponibile (${response.status})`);
        setCatalog(await response.json() as LinkCatalog);
        setError(null);
      } catch (requestError) {
        if (!controller.signal.aborted) {
          setError(requestError instanceof Error ? requestError.message : "connessione non disponibile");
        }
      } finally {
        if (!controller.signal.aborted) setLoading(false);
      }
    }

    void load();
    return () => controller.abort();
  }, [repositoryId, submittedQuery, relation, evidence, entityType, reviewOnly, page, focus, path]);

  function submitSearch(event: React.FormEvent) {
    event.preventDefault();
    setPage(1);
    setSubmittedQuery(query.trim());
  }

  function resetFilters() {
    setQuery("");
    setSubmittedQuery("");
    setRelation("all");
    setEvidence("all");
    setEntityType("all");
    setReviewOnly(false);
    setPage(1);
  }

  return <section className="explorer-panel knowledge-links-panel">
    <div className="explorer-panel-heading knowledge-links-heading">
      <div>
        <p className="section-kicker">KNOWLEDGE LAYER</p>
        <h3>Collegamenti automatici</h3>
        <p>Relazioni ricavate dai dati GitHub, dalla struttura C# e dai riferimenti espliciti nei testi.</p>
      </div>
      <span className="panel-count">{numberFormatter.format(catalog?.totalLinks ?? 0)}</span>
    </div>

    {(focus || path) && <div className="knowledge-focus" role="status">
      <strong>{focus?.label ?? path}</strong>
      <span>Collegamenti in entrata e in uscita dall’elemento selezionato.</span>
      <button type="button" onClick={() => window.history.back()}>← Indietro</button>
      <a href="#tab=links">Tutti i collegamenti</a>
    </div>}
    {focus?.type === "commit" && <CommitFiles repositoryId={repositoryId} commitId={focus.id} />}
    {error && <div className="source-symbol-message source-symbol-error">
      <strong>Collegamenti non disponibili</strong>
      <span>{error}. Riprova tra qualche momento.</span>
    </div>}
    {!error && loading && <div className="source-symbol-message"><span className="source-loader" /><span>Caricamento dei collegamenti…</span></div>}
    {!error && !loading && catalog && <>
      <div className="knowledge-quality-metrics" aria-label="Indicatori del knowledge layer">
        {relationMetrics.map(metric => {
          const count = facetCount(catalog, metric.key);
          return <button
            type="button"
            key={metric.key}
            className={relation === metric.key ? "active" : ""}
            onClick={() => { setRelation(metric.key); setReviewOnly(false); setPage(1); }}
          >
            <span>{metric.label}</span>
            <strong>{numberFormatter.format(count)}</strong>
            <small>{percentageLabel(count, catalog.totalLinks)} · {metric.description}</small>
          </button>;
        })}
      </div>

      {catalog.reviewLinks > 0 && <div className="knowledge-quality-notice" role="note">
        <div>
          <strong>{numberFormatter.format(catalog.reviewLinks)} riferimenti testuali da verificare</strong>
          <span>I riferimenti standard dei merge commit sono già confermati e non compaiono in questo conteggio.</span>
        </div>
        <button type="button" onClick={() => { setRelation("references"); setEvidence("text_reference"); setReviewOnly(true); setPage(1); }}>Mostra solo questi</button>
      </div>}

      <div className="knowledge-link-toolbar">
        <div className="knowledge-link-facets">
          <button type="button" className={relation === "all" ? "active" : ""} onClick={() => { setRelation("all"); setReviewOnly(false); setPage(1); }}>
            Tutti <strong>{numberFormatter.format(catalog.totalLinks)}</strong>
          </button>
          {catalog.facets.map(facet => <button type="button" key={facet.relationType} className={relation === facet.relationType ? "active" : ""} onClick={() => { setRelation(facet.relationType); setReviewOnly(false); setPage(1); }}>
            {relationLabels[facet.relationType] ?? facet.relationType} <strong>{numberFormatter.format(facet.count)}</strong>
          </button>)}
        </div>
        <small>Aggiornati {catalog.lastRefreshedAt ? dateFormatter.format(new Date(catalog.lastRefreshedAt)) : "—"}</small>
      </div>

      <form className="knowledge-link-search" onSubmit={submitSearch}>
        <label><span className="sr-only">Cerca nei collegamenti</span><input value={query} onChange={event => setQuery(event.target.value)} placeholder="Cerca issue, PR, SHA, file o simbolo…" /></label>
        <select aria-label="Filtra per tipo di elemento" value={entityType} onChange={event => { setEntityType(event.target.value); setPage(1); }}>
          <option value="all">Tutti gli elementi</option>
          {Object.entries(entityLabels).map(([value, label]) => <option value={value} key={value}>{label}</option>)}
        </select>
        <select aria-label="Filtra per origine" value={evidence} onChange={event => { setEvidence(event.target.value); setPage(1); }}>
          <option value="all">Tutte le origini</option>
          <option value="text_reference">Riferimenti testuali</option>
          <option value="github_api">Relazioni GitHub</option>
          <option value="structural_index">Indice C#</option>
        </select>
        <button type="submit">Cerca</button>
        {(submittedQuery || relation !== "all" || evidence !== "all" || entityType !== "all" || reviewOnly) && <button className="source-clear" type="button" onClick={resetFilters}>Azzera</button>}
      </form>

      <div className="knowledge-result-summary">
        <span>{numberFormatter.format(catalog.matchedLinks)} collegamenti trovati</span>
        {reviewOnly && <small>Solo riferimenti da verificare</small>}
        {submittedQuery && <small>Ricerca: “{submittedQuery}”</small>}
      </div>

      <div className="knowledge-link-list">
        {catalog.links.map(link => {
          const ambiguous = link.requiresReview;
          return <article className={`knowledge-link-row${ambiguous ? " is-ambiguous" : ""}`} key={link.id}>
            <EntityLink type={link.sourceType} id={link.sourceId} label={link.sourceLabel} url={link.sourceUrl} path={link.sourcePath} line={link.sourceLine} />
            <div className={`knowledge-relation relation-${link.relationType}`}><span>→</span><strong>{relationLabels[link.relationType] ?? link.relationType}</strong></div>
            <EntityLink type={link.targetType} id={link.targetId} label={link.targetLabel} url={link.targetUrl} path={link.targetPath} line={link.targetLine} />
            <div className="knowledge-evidence">
              <span>{link.evidenceType === "text_reference" ? "Riferimento nel testo" : link.evidenceType === "structural_index" ? "Indice C#" : "GitHub"}</span>
              {ambiguous && <em className="knowledge-review-badge">Da verificare</em>}
              {link.evidenceText && <small>“{link.evidenceText}”</small>}
              {ambiguous && <small className="knowledge-review-help">La frase cita l’elemento senza indicare esplicitamente che lo chiude.</small>}
            </div>
          </article>;
        })}
        {catalog.links.length === 0 && <div className="source-symbol-message"><span>Nessun collegamento corrisponde ai filtri selezionati.</span></div>}
      </div>
      {catalog.totalPages > 1 && <nav className="knowledge-pagination" aria-label="Pagine dei collegamenti">
        <button type="button" disabled={catalog.page <= 1 || loading} onClick={() => setPage(value => Math.max(1, value - 1))}>← Precedente</button>
        <span>Pagina <strong>{catalog.page}</strong> di <strong>{catalog.totalPages}</strong></span>
        <button type="button" disabled={catalog.page >= catalog.totalPages || loading} onClick={() => setPage(value => value + 1)}>Successiva →</button>
      </nav>}
    </>}
  </section>;
}

function CommitFiles({ repositoryId, commitId }: { repositoryId: number; commitId: number }) {
  const [files, setFiles] = useState<{ filename: string; status: string; additions: number | null; deletions: number | null; sourceAvailable: boolean; htmlUrl: string }[] | null>(null);
  const [error, setError] = useState(false);
  useEffect(() => {
    const controller = new AbortController();
    fetch(`http://127.0.0.1:5088/api/repositories/${repositoryId}/commits/${commitId}/files`, { signal: controller.signal })
      .then(response => { if (!response.ok) throw new Error(); return response.json(); })
      .then(value => setFiles(value as NonNullable<typeof files>)).catch(() => { if (!controller.signal.aborted) setError(true); });
    return () => controller.abort();
  }, [repositoryId, commitId]);
  return <section className="knowledge-focus" aria-label="File modificati nel commit">
    <strong>File modificati nel commit{files ? ` (${files.length})` : ""}</strong>
    {error ? <span>Impossibile caricare i file del commit.</span> : files === null ? <span>Caricamento file…</span> :
      files.length === 0 ? <span>Nessun file modificato registrato per questo commit.</span> :
      files.map(file => <div className="commit-changed-file" key={file.filename}>
        <a href={file.htmlUrl} target="_blank" rel="noreferrer">{file.filename} ↗</a>
        <span> {file.additions == null || file.deletions == null ? "Statistiche righe non disponibili" : `+${file.additions} / −${file.deletions}`} · {file.status}</span>
        {file.sourceAvailable ? <a href={sourceHref(file.filename)}>Apri codice importato</a> :
          <small>Sorgente non disponibile nel catalogo locale; consulta la modifica su GitHub.</small>}
      </div>)}
  </section>;
}

function EntityLink({ type, id, label, url, path, line }: { type: string; id: number; label: string; url: string | null; path: string | null; line: number | null }) {
  return <div className="knowledge-entity">
    <small>{entityLabels[type] ?? type}</small>
    <a href={linksHref({ type, id, label })}><strong>{label}</strong></a>
    <div className="entity-navigation-actions">
      <a href={linksHref({ type, id, label })}>Esplora collegamenti →</a>
      {path && <a href={sourceHref(path, line)}>Apri codice{line ? ` · riga ${line}` : ""}</a>}
      {url && <a href={url} target="_blank" rel="noreferrer">GitHub ↗</a>}
    </div>
  </div>;
}
