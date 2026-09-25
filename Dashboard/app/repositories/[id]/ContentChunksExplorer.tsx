"use client";

import { FormEvent, useCallback, useEffect, useRef, useState } from "react";
import { linksHref, sourceHref } from "./explorer-navigation";

type ChunkFacet = { sourceType: string; count: number };
type ContentChunk = {
  id: number; sourceType: string; sourceEntityId: number; title: string;
  sourcePath: string | null; startLine: number | null; endLine: number | null;
  htmlUrl: string | null; ordinal: number; content: string;
  characterCount: number; estimatedTokens: number; indexedAt: string;
};
type ChunkCatalog = {
  repositoryId: number; totalChunks: number; totalSources: number;
  totalCharacters: number; estimatedTokens: number; matchedChunks: number;
  page: number; pageSize: number; totalPages: number;
  facets: ChunkFacet[]; chunks: ContentChunk[];
};

const numberFormatter = new Intl.NumberFormat("it-IT");
const sourceLabels: Record<string, string> = {
  repository_file: "File sorgente", code_symbol: "Simbolo C#", issue: "Issue",
  issue_comment: "Commento", pull_request: "Pull request", commit: "Commit",
};
const formatNumber = (value: number) => numberFormatter.format(value);

function lineLabel(chunk: ContentChunk) {
  if (!chunk.sourcePath) return null;
  if (!chunk.startLine) return chunk.sourcePath;
  return `${chunk.sourcePath}:${chunk.startLine}${chunk.endLine && chunk.endLine !== chunk.startLine ? `–${chunk.endLine}` : ""}`;
}

export function ContentChunksExplorer({ repositoryId }: { repositoryId: number }) {
  const [catalog, setCatalog] = useState<ChunkCatalog | null>(null);
  const [query, setQuery] = useState("");
  const [submittedQuery, setSubmittedQuery] = useState("");
  const [sourceType, setSourceType] = useState("all");
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const requestId = useRef(0);

  const load = useCallback(async () => {
    const currentRequest = ++requestId.current;
    const parameters = new URLSearchParams({ page: String(page), pageSize: "30" });
    if (submittedQuery) parameters.set("q", submittedQuery);
    if (sourceType !== "all") parameters.set("sourceType", sourceType);
    setLoading(true);
    try {
      const response = await fetch(`http://127.0.0.1:5088/api/repositories/${repositoryId}/chunks?${parameters}`, { cache: "no-store" });
      if (!response.ok) throw new Error(`servizio non disponibile (${response.status})`);
      const nextCatalog = await response.json() as ChunkCatalog;
      if (currentRequest === requestId.current) { setCatalog(nextCatalog); setError(null); }
    } catch (requestError) {
      if (currentRequest === requestId.current) setError(requestError instanceof Error ? requestError.message : "connessione non disponibile");
    } finally {
      if (currentRequest === requestId.current) setLoading(false);
    }
  }, [page, repositoryId, sourceType, submittedQuery]);

  useEffect(() => { const timer = window.setTimeout(() => void load(), 0); return () => window.clearTimeout(timer); }, [load]);

  function submitSearch(event: FormEvent) {
    event.preventDefault();
    setPage(1);
    setSubmittedQuery(query.trim());
  }

  function resetFilters() {
    setQuery(""); setSubmittedQuery(""); setSourceType("all"); setPage(1);
  }

  const activeFilters = Boolean(submittedQuery) || sourceType !== "all";

  return <section className="chunk-explorer">
    <header className="chunk-header">
      <div><p className="section-kicker">CONOSCENZA INDICIZZATA</p><h3>Chunk con provenienza</h3><p>Porzioni pronte per la ricerca e per le risposte assistite, sempre collegate alla fonte originale.</p></div>
      {catalog && <span className="chunk-header-count">{formatNumber(catalog.totalChunks)} chunk</span>}
    </header>

    {catalog && <div className="chunk-metrics">
      <article><span>Chunk</span><strong>{formatNumber(catalog.totalChunks)}</strong><small>unità indicizzate</small></article>
      <article><span>Fonti</span><strong>{formatNumber(catalog.totalSources)}</strong><small>documenti e simboli</small></article>
      <article><span>Token stimati</span><strong>{formatNumber(catalog.estimatedTokens)}</strong><small>volume interrogabile</small></article>
      <article><span>Caratteri</span><strong>{formatNumber(catalog.totalCharacters)}</strong><small>testo conservato</small></article>
    </div>}

    <div className="chunk-toolbar">
      <div className="chunk-type-filters" aria-label="Filtra i chunk per provenienza">
        <button type="button" aria-pressed={sourceType === "all"} onClick={() => { setSourceType("all"); setPage(1); }}>Tutte <strong>{catalog ? formatNumber(catalog.totalChunks) : "—"}</strong></button>
        {catalog?.facets.map(facet => <button type="button" key={facet.sourceType} aria-pressed={sourceType === facet.sourceType} onClick={() => { setSourceType(facet.sourceType); setPage(1); }}>{sourceLabels[facet.sourceType] ?? facet.sourceType} <strong>{formatNumber(facet.count)}</strong></button>)}
      </div>
      <form className="chunk-search" onSubmit={submitSearch}>
        <label><span className="sr-only">Cerca nel contenuto dei chunk</span><input value={query} onChange={event => setQuery(event.target.value)} placeholder="Cerca nel contenuto o nella provenienza…" /></label>
        <button type="submit" disabled={loading}>{loading ? "Lettura…" : "Cerca"}</button>
        {activeFilters && <button className="chunk-clear" type="button" onClick={resetFilters}>Azzera</button>}
      </form>
    </div>

    {error && <div className="chunk-message chunk-error"><strong>Chunk non disponibili.</strong><span>{error}. Verifica che l’importazione e lo script 007 siano stati completati.</span></div>}
    {!error && loading && !catalog && <div className="chunk-message"><span className="source-loader" /><strong>Caricamento dei chunk…</strong></div>}
    {!error && catalog && <>
      <div className="chunk-result-summary"><span>{formatNumber(catalog.matchedChunks)} risultati{activeFilters ? " per i filtri selezionati" : ""}</span>{catalog.totalPages > 0 && <small>Pagina {catalog.page} di {catalog.totalPages}</small>}</div>
      {catalog.chunks.length > 0 ? <div className="chunk-list">
        {catalog.chunks.map(chunk => {
          const provenanceHref = chunk.sourcePath ? sourceHref(chunk.sourcePath, chunk.startLine) : linksHref({ type: chunk.sourceType, id: chunk.sourceEntityId, label: chunk.title });
          return <article className="chunk-row" key={chunk.id}>
            <div className="chunk-row-heading"><div><span className={`chunk-source-type type-${chunk.sourceType}`}>{sourceLabels[chunk.sourceType] ?? chunk.sourceType}</span><small>Chunk {chunk.ordinal + 1}</small></div><strong>{chunk.title}</strong>{lineLabel(chunk) && <code>{lineLabel(chunk)}</code>}</div>
            <pre>{chunk.content}</pre>
            <footer><span>{formatNumber(chunk.characterCount)} caratteri · circa {formatNumber(chunk.estimatedTokens)} token</span><div><a href={provenanceHref}>Apri provenienza →</a>{chunk.htmlUrl && <a href={chunk.htmlUrl} target="_blank" rel="noreferrer">Apri origine ↗</a>}</div></footer>
          </article>;
        })}
      </div> : <div className="chunk-message"><strong>Nessun chunk trovato.</strong><span>Prova a cambiare testo o tipo di provenienza.</span></div>}
      {catalog.totalPages > 1 && <nav className="chunk-pagination" aria-label="Pagine dei chunk"><button type="button" disabled={loading || page <= 1} onClick={() => setPage(current => Math.max(1, current - 1))}>← Precedente</button><span>Pagina {catalog.page} di {catalog.totalPages}</span><button type="button" disabled={loading || page >= catalog.totalPages} onClick={() => setPage(current => current + 1)}>Successiva →</button></nav>}
    </>}
  </section>;
}
