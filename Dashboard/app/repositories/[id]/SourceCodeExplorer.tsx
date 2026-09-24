"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";

import { fileLinksHref } from "./explorer-navigation";

type SourceFacet = { name: string; count: number };
type SourceFileSummary = {
  path: string;
  fileName: string;
  extension: string | null;
  language: string | null;
  branch: string;
  blobSha: string;
  sizeBytes: number;
  lineCount: number;
  syncedAt: string;
};
type SourceCatalog = {
  repositoryId: number;
  repositoryFullName: string;
  defaultBranch: string | null;
  fileCount: number;
  directoryCount: number;
  totalLines: number;
  totalSizeBytes: number;
  lastSyncedAt: string | null;
  languages: SourceFacet[];
  extensions: SourceFacet[];
  files: SourceFileSummary[];
};
type SourceFileContent = SourceFileSummary & {
  contentEncoding: string;
  content: string;
  htmlUrl: string;
};
type SourceSearchMatch = {
  path: string;
  language: string | null;
  extension: string | null;
  lineNumber: number | null;
  matchType: "path" | "content";
  snippet: string;
  relevance: number;
};
type SourceSearchResult = {
  query: string;
  extension: string | null;
  matchedFiles: number;
  isLimited: boolean;
  searchMode: "postgresql_full_text";
  matches: SourceSearchMatch[];
};
type CSharpSymbol = {
  kind: string;
  name: string;
  qualifiedName: string;
  signature: string;
  containingSymbol: string | null;
  accessibility: string | null;
  modifiers: string[];
  returnType: string | null;
  startLine: number;
  endLine: number;
  path: string;
};
type CSharpSymbolCatalog = {
  repositoryId: number;
  cSharpFiles: number;
  indexedFiles: number;
  partialFiles: number;
  symbolCount: number;
  matchedSymbols: number;
  isLimited: boolean;
  lastIndexedAt: string | null;
  query: string | null;
  kind: string | null;
  kinds: SourceFacet[];
  symbols: CSharpSymbol[];
};
type TreeNode = {
  name: string;
  path: string;
  kind: "directory" | "file";
  children: TreeNode[];
  file?: SourceFileSummary;
};
type MutableTreeNode = TreeNode & { childMap: Map<string, MutableTreeNode> };

const apiBaseUrl = "http://127.0.0.1:5088/api";
const numberFormatter = new Intl.NumberFormat("it-IT");
const dateFormatter = new Intl.DateTimeFormat("it-IT", {
  day: "2-digit",
  month: "short",
  year: "numeric",
  hour: "2-digit",
  minute: "2-digit",
});

const keywordGroups: Record<string, Set<string>> = {
  "C#": new Set(["abstract", "async", "await", "bool", "break", "case", "catch", "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "false", "finally", "float", "for", "foreach", "from", "get", "if", "in", "int", "interface", "internal", "is", "long", "namespace", "new", "null", "object", "override", "private", "protected", "public", "record", "return", "set", "static", "string", "struct", "switch", "this", "throw", "true", "try", "using", "var", "virtual", "void", "when", "where", "while"]),
  JavaScript: new Set(["async", "await", "break", "case", "catch", "class", "const", "continue", "default", "delete", "do", "else", "export", "extends", "false", "finally", "for", "from", "function", "if", "import", "in", "instanceof", "let", "new", "null", "of", "return", "static", "super", "switch", "this", "throw", "true", "try", "typeof", "undefined", "var", "void", "while", "yield"]),
  TypeScript: new Set(["any", "as", "async", "await", "boolean", "break", "case", "catch", "class", "const", "continue", "default", "do", "else", "enum", "export", "extends", "false", "finally", "for", "from", "function", "if", "implements", "import", "in", "interface", "keyof", "let", "namespace", "never", "new", "null", "number", "object", "of", "private", "protected", "public", "readonly", "return", "static", "string", "super", "switch", "this", "throw", "true", "try", "type", "typeof", "undefined", "unknown", "var", "void", "while"]),
  SQL: new Set(["alter", "and", "as", "begin", "by", "case", "commit", "create", "delete", "distinct", "drop", "else", "end", "exists", "false", "from", "group", "having", "in", "index", "insert", "into", "is", "join", "limit", "not", "null", "on", "or", "order", "outer", "primary", "references", "returning", "select", "set", "table", "then", "true", "union", "unique", "update", "values", "when", "where"]),
  Python: new Set(["and", "as", "async", "await", "break", "class", "continue", "def", "del", "elif", "else", "except", "False", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "None", "not", "or", "pass", "raise", "return", "True", "try", "while", "with", "yield"]),
};

const symbolKindLabels: Record<string, string> = {
  namespace: "Namespace",
  class: "Classi",
  interface: "Interfacce",
  enum: "Enum",
  struct: "Struct",
  record: "Record",
  method: "Metodi",
  constructor: "Costruttori",
  property: "Proprietà",
};

function formatDate(value: string | null | undefined) {
  return value ? dateFormatter.format(new Date(value)) : "—";
}

function formatBytes(value: number) {
  if (value < 1024) return `${value} B`;
  if (value < 1024 * 1024) return `${(value / 1024).toLocaleString("it-IT", { maximumFractionDigits: 1 })} KB`;
  return `${(value / (1024 * 1024)).toLocaleString("it-IT", { maximumFractionDigits: 1 })} MB`;
}

async function readApiError(response: Response) {
  try {
    const payload = await response.json() as { message?: string; detail?: string; title?: string };
    return payload.message ?? payload.detail ?? payload.title ?? `servizio non disponibile (${response.status})`;
  } catch {
    return `servizio non disponibile (${response.status})`;
  }
}

function buildTree(files: SourceFileSummary[]): TreeNode[] {
  const root = new Map<string, MutableTreeNode>();

  for (const file of files) {
    const parts = file.path.split("/").filter(Boolean);
    let level = root;
    let currentPath = "";

    parts.forEach((part, index) => {
      currentPath = currentPath ? `${currentPath}/${part}` : part;
      const isFile = index === parts.length - 1;
      let node = level.get(part);
      if (!node) {
        node = {
          name: part,
          path: currentPath,
          kind: isFile ? "file" : "directory",
          children: [],
          childMap: new Map<string, MutableTreeNode>(),
          file: isFile ? file : undefined,
        };
        level.set(part, node);
      }
      level = node.childMap;
    });
  }

  const finalize = (nodes: Iterable<MutableTreeNode>): TreeNode[] =>
    Array.from(nodes)
      .sort((left, right) => {
        if (left.kind !== right.kind) return left.kind === "directory" ? -1 : 1;
        return left.name.localeCompare(right.name, "it", { sensitivity: "base" });
      })
      .map(node => ({
        name: node.name,
        path: node.path,
        kind: node.kind,
        file: node.file,
        children: finalize(node.childMap.values()),
      }));

  return finalize(root.values());
}

function commentMarker(language: string | null) {
  if (language === "SQL") return "--";
  if (language === "Python" || language === "PowerShell" || language === "YAML") return "#";
  return "//";
}

function HighlightedLine({ line, language }: { line: string; language: string | null }) {
  const marker = commentMarker(language);
  const commentIndex = line.indexOf(marker);
  const code = commentIndex >= 0 ? line.slice(0, commentIndex) : line;
  const comment = commentIndex >= 0 ? line.slice(commentIndex) : "";
  const keywords = keywordGroups[language ?? ""] ?? keywordGroups.JavaScript;
  const tokenPattern = /("(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|`(?:\\.|[^`\\])*`|\b\d+(?:\.\d+)?\b|\b[A-Za-z_$][\w$]*\b)/g;
  const fragments: React.ReactNode[] = [];
  let cursor = 0;

  for (const match of code.matchAll(tokenPattern)) {
    const index = match.index ?? 0;
    if (index > cursor) fragments.push(code.slice(cursor, index));
    const token = match[0];
    const className = token.startsWith('"') || token.startsWith("'") || token.startsWith("`")
      ? "syntax-string"
      : /^\d/.test(token)
        ? "syntax-number"
        : keywords.has(token) || keywords.has(token.toLowerCase())
          ? "syntax-keyword"
          : "syntax-identifier";
    fragments.push(<span className={className} key={`${index}-${token}`}>{token}</span>);
    cursor = index + token.length;
  }

  if (cursor < code.length) fragments.push(code.slice(cursor));
  if (comment) fragments.push(<span className="syntax-comment" key="comment">{comment}</span>);
  return <>{fragments}</>;
}

function HighlightedSnippet({ snippet }: { snippet: string }) {
  const parts = snippet.split(/(⟦[\s\S]*?⟧)/g).filter(Boolean);
  return <>{parts.map((part, index) => part.startsWith("⟦") && part.endsWith("⟧")
    ? <mark key={index}>{part.slice(1, -1)}</mark>
    : <span key={index}>{part}</span>)}</>;
}

export function SourceCodeExplorer({ repositoryId, initialPath = null, initialLine = null }: { repositoryId: number; initialPath?: string | null; initialLine?: number | null }) {
  const [catalog, setCatalog] = useState<SourceCatalog | null>(null);
  const [catalogLoading, setCatalogLoading] = useState(true);
  const [catalogError, setCatalogError] = useState<string | null>(null);
  const [openDirectories, setOpenDirectories] = useState<Set<string>>(new Set());
  const [selectedPath, setSelectedPath] = useState<string | null>(null);
  const [selectedFile, setSelectedFile] = useState<SourceFileContent | null>(null);
  const [fileLoading, setFileLoading] = useState(false);
  const [fileError, setFileError] = useState<string | null>(null);
  const [selectedLine, setSelectedLine] = useState<number | null>(null);
  const [searchQuery, setSearchQuery] = useState("");
  const [extension, setExtension] = useState("");
  const [searchResult, setSearchResult] = useState<SourceSearchResult | null>(null);
  const [searching, setSearching] = useState(false);
  const [searchError, setSearchError] = useState<string | null>(null);
  const [copyLabel, setCopyLabel] = useState("Copia codice");
  const [symbolCatalog, setSymbolCatalog] = useState<CSharpSymbolCatalog | null>(null);
  const [symbolsLoading, setSymbolsLoading] = useState(true);
  const [symbolsError, setSymbolsError] = useState<string | null>(null);
  const [symbolQuery, setSymbolQuery] = useState("");
  const [symbolKind, setSymbolKind] = useState("");
  const fileRequestId = useRef(0);
  const searchRequestId = useRef(0);
  const symbolRequestId = useRef(0);

  const openFile = useCallback(async (path: string, lineNumber?: number | null) => {
    const requestId = ++fileRequestId.current;
    setSelectedPath(path);
    setSelectedLine(lineNumber ?? null);
    setFileLoading(true);
    setFileError(null);
    try {
      const parameters = new URLSearchParams({ path });
      const response = await fetch(
        `${apiBaseUrl}/repositories/${repositoryId}/source/file?${parameters}`,
        { cache: "no-store" },
      );
      if (!response.ok) throw new Error(await readApiError(response));
      const nextFile = await response.json() as SourceFileContent;
      if (requestId === fileRequestId.current) {
        setSelectedFile(nextFile);
      }
    } catch (requestError) {
      if (requestId === fileRequestId.current) {
        setSelectedFile(null);
        setFileError(requestError instanceof Error ? requestError.message : "file non disponibile");
      }
    } finally {
      if (requestId === fileRequestId.current) {
        setFileLoading(false);
      }
    }
  }, [repositoryId]);

  const loadSymbols = useCallback(async (query = "", kind = "") => {
    const requestId = ++symbolRequestId.current;
    setSymbolsLoading(true);
    setSymbolsError(null);
    try {
      const parameters = new URLSearchParams();
      if (query.trim()) parameters.set("q", query.trim());
      if (kind) parameters.set("kind", kind);
      const queryString = parameters.toString();
      const suffix = queryString ? `?${queryString}` : "";
      const response = await fetch(
        `${apiBaseUrl}/repositories/${repositoryId}/source/symbols${suffix}`,
        { cache: "no-store" },
      );
      if (!response.ok) throw new Error(await readApiError(response));
      const nextCatalog = await response.json() as CSharpSymbolCatalog;
      if (requestId === symbolRequestId.current) {
        setSymbolCatalog(nextCatalog);
      }
    } catch (requestError) {
      if (requestId === symbolRequestId.current) {
        setSymbolsError(requestError instanceof Error ? requestError.message : "indice strutturale non disponibile");
      }
    } finally {
      if (requestId === symbolRequestId.current) {
        setSymbolsLoading(false);
      }
    }
  }, [repositoryId]);

  useEffect(() => {
    const controller = new AbortController();
    async function loadCatalog() {
      setCatalogLoading(true);
      setCatalogError(null);
      try {
        const response = await fetch(`${apiBaseUrl}/repositories/${repositoryId}/source`, {
          cache: "no-store",
          signal: controller.signal,
        });
        if (!response.ok) throw new Error(await readApiError(response));
        const nextCatalog = await response.json() as SourceCatalog;
        setCatalog(nextCatalog);
        if (initialPath) {
          void openFile(initialPath, initialLine);
        } else if (nextCatalog.files.length > 0) {
          void openFile(nextCatalog.files[0].path);
        }
      } catch (requestError) {
        if (requestError instanceof DOMException && requestError.name === "AbortError") return;
        setCatalogError(requestError instanceof Error ? requestError.message : "catalogo non disponibile");
      } finally {
        setCatalogLoading(false);
      }
    }
    void loadCatalog();
    return () => controller.abort();
  }, [openFile, repositoryId, initialPath, initialLine]);

  useEffect(() => {
    const timer = window.setTimeout(() => void loadSymbols(), 0);
    return () => window.clearTimeout(timer);
  }, [loadSymbols]);

  useEffect(() => {
    if (!selectedLine || fileLoading) return;
    const timer = window.setTimeout(() => {
      document.getElementById(`source-line-${selectedLine}`)?.scrollIntoView({ block: "center" });
    }, 0);
    return () => window.clearTimeout(timer);
  }, [fileLoading, selectedFile, selectedLine]);

  const tree = useMemo(() => buildTree(catalog?.files ?? []), [catalog]);
  const contentLines = useMemo(
    () => selectedFile?.content.replace(/\r\n/g, "\n").split("\n") ?? [],
    [selectedFile],
  );

  function toggleDirectory(path: string) {
    setOpenDirectories(current => {
      const next = new Set(current);
      if (next.has(path)) next.delete(path);
      else next.add(path);
      return next;
    });
  }

  async function search(event: React.FormEvent) {
    event.preventDefault();
    const query = searchQuery.trim();
    if (query.length < 2) {
      setSearchError("Inserisci almeno due caratteri.");
      return;
    }

    const requestId = ++searchRequestId.current;
    setSearching(true);
    setSearchError(null);
    try {
      const parameters = new URLSearchParams({ q: query });
      if (extension) parameters.set("extension", extension);
      const response = await fetch(
        `${apiBaseUrl}/repositories/${repositoryId}/source/search?${parameters}`,
        { cache: "no-store" },
      );
      if (!response.ok) throw new Error(await readApiError(response));
      const nextResult = await response.json() as SourceSearchResult;
      if (requestId === searchRequestId.current) {
        setSearchResult(nextResult);
      }
    } catch (requestError) {
      if (requestId === searchRequestId.current) {
        setSearchError(requestError instanceof Error ? requestError.message : "ricerca non disponibile");
      }
    } finally {
      if (requestId === searchRequestId.current) {
        setSearching(false);
      }
    }
  }

  function clearSearch() {
    searchRequestId.current++;
    setSearchQuery("");
    setExtension("");
    setSearchResult(null);
    setSearchError(null);
    setSearching(false);
  }

  async function searchSymbols(event: React.FormEvent) {
    event.preventDefault();
    await loadSymbols(symbolQuery, symbolKind);
  }

  function clearSymbolSearch() {
    setSymbolQuery("");
    setSymbolKind("");
    void loadSymbols();
  }

  async function copyCode() {
    if (!selectedFile) return;
    try {
      await navigator.clipboard.writeText(selectedFile.content);
      setCopyLabel("Copiato");
      window.setTimeout(() => setCopyLabel("Copia codice"), 1500);
    } catch {
      setCopyLabel("Copia non riuscita");
    }
  }

  if (catalogLoading) {
    return <section className="source-explorer source-loading"><span className="source-loader" /><div><strong>Caricamento del codice sorgente</strong><p>Sto preparando cartelle, file e statistiche.</p></div></section>;
  }

  if (catalogError || !catalog) {
    return <section className="source-explorer source-empty"><strong>Codice sorgente non disponibile</strong><p>{catalogError ?? "Il catalogo non contiene dati."}</p></section>;
  }

  if (catalog.fileCount === 0) {
    return <section className="source-explorer source-empty"><strong>Nessun file sorgente importato</strong><p>Esegui nuovamente l’importazione per acquisire il branch principale del repository.</p></section>;
  }

  return (
    <section className="source-explorer">
      <header className="source-header">
        <div><p className="section-kicker">SOURCE CODE EXPLORER</p><h3>Codice del branch {catalog.defaultBranch ?? "principale"}</h3><p>Esplora la struttura importata e cerca testo nei contenuti salvati in PostgreSQL.</p></div>
        <div className="source-language-summary">{catalog.languages.slice(0, 4).map(item => <span key={item.name}>{item.name} <strong>{item.count}</strong></span>)}</div>
      </header>

      <div className="source-metrics">
        <article><span>File</span><strong>{numberFormatter.format(catalog.fileCount)}</strong><small>{numberFormatter.format(catalog.directoryCount)} cartelle</small></article>
        <article><span>Righe</span><strong>{numberFormatter.format(catalog.totalLines)}</strong><small>contenuto indicizzato</small></article>
        <article><span>Dimensione</span><strong>{formatBytes(catalog.totalSizeBytes)}</strong><small>testo archiviato</small></article>
        <article><span>Aggiornato</span><strong>{formatDate(catalog.lastSyncedAt)}</strong><small>ultima sincronizzazione</small></article>
      </div>

      <details className="source-symbol-index" open>
        <summary>
          <div><span>INDICIZZAZIONE STRUTTURALE</span><strong>Struttura C#</strong></div>
          <div className="source-symbol-summary">
            <strong>{numberFormatter.format(symbolCatalog?.symbolCount ?? 0)} simboli</strong>
            <small>{numberFormatter.format(symbolCatalog?.indexedFiles ?? 0)} / {numberFormatter.format(symbolCatalog?.cSharpFiles ?? 0)} file analizzati</small>
          </div>
        </summary>
        <div className="source-symbol-body">
          <div className="source-symbol-intro">
            <p>Namespace, tipi e membri riconosciuti dal parser C#. Seleziona un risultato per aprire la dichiarazione nel file.</p>
            {symbolCatalog?.lastIndexedAt && <small>Aggiornato {formatDate(symbolCatalog.lastIndexedAt)}</small>}
          </div>
          <form className="source-symbol-search" onSubmit={searchSymbols}>
            <label><span className="sr-only">Cerca un simbolo C#</span><input value={symbolQuery} onChange={event => setSymbolQuery(event.target.value)} placeholder="Cerca classe, metodo o nome completo…" /></label>
            <select aria-label="Filtra per tipo di simbolo" value={symbolKind} onChange={event => setSymbolKind(event.target.value)}>
              <option value="">Tutti i simboli</option>
              {(symbolCatalog?.kinds ?? []).map(item => <option key={item.name} value={item.name}>{symbolKindLabels[item.name] ?? item.name} ({item.count})</option>)}
            </select>
            <button type="submit" disabled={symbolsLoading}>{symbolsLoading ? "Carico…" : "Filtra"}</button>
            {(symbolQuery || symbolKind) && <button className="source-clear" type="button" onClick={clearSymbolSearch}>Azzera</button>}
          </form>
          {symbolsError && <div className="source-symbol-message source-symbol-error"><strong>Indice non disponibile</strong><span>{symbolsError}. Esegui lo script 004 e poi una nuova importazione.</span></div>}
          {!symbolsError && symbolsLoading && <div className="source-symbol-message"><span className="source-loader" /><span>Caricamento della struttura C#…</span></div>}
          {!symbolsError && !symbolsLoading && symbolCatalog && symbolCatalog.indexedFiles === 0 && <div className="source-symbol-message"><strong>Indicizzazione ancora da eseguire</strong><span>Il repository contiene {numberFormatter.format(symbolCatalog.cSharpFiles)} file C#. Avvia l’importatore dopo avere applicato lo script 004.</span></div>}
          {!symbolsError && !symbolsLoading && symbolCatalog && symbolCatalog.indexedFiles > 0 && <>
            <div className="source-symbol-facets">
              {symbolCatalog.kinds.map(item => <span key={item.name}>{symbolKindLabels[item.name] ?? item.name}<strong>{numberFormatter.format(item.count)}</strong></span>)}
              {symbolCatalog.partialFiles > 0 && <span className="partial">File parziali<strong>{numberFormatter.format(symbolCatalog.partialFiles)}</strong></span>}
            </div>
            <div className="source-symbol-results">
              {symbolCatalog.symbols.map((symbol, index) => <button type="button" key={`${symbol.path}-${symbol.startLine}-${symbol.kind}-${index}`} onClick={() => void openFile(symbol.path, symbol.startLine)}>
                <span className={`source-symbol-kind kind-${symbol.kind}`}>{symbolKindLabels[symbol.kind] ?? symbol.kind}</span>
                <strong>{symbol.qualifiedName}</strong>
                <code>{symbol.signature}</code>
                <small>{symbol.path} · righe {symbol.startLine}{symbol.endLine !== symbol.startLine ? `–${symbol.endLine}` : ""}{symbol.accessibility ? ` · ${symbol.accessibility}` : ""}</small>
              </button>)}
              {symbolCatalog.symbols.length === 0 && <div className="source-symbol-message"><span>Nessun simbolo corrisponde ai filtri selezionati.</span></div>}
            </div>
            {symbolCatalog.isLimited && <div className="source-symbol-limit">Mostrati i primi 250 di {numberFormatter.format(symbolCatalog.matchedSymbols)} simboli. Restringi la ricerca per trovare una dichiarazione specifica.</div>}
          </>}
        </div>
      </details>

      <form className="source-search" onSubmit={search}>
        <label><span className="sr-only">Cerca nel codice sorgente</span><input value={searchQuery} onChange={event => setSearchQuery(event.target.value)} placeholder="Cerca una classe, un metodo, una rotta o un testo…" /></label>
        <select aria-label="Filtra per estensione" value={extension} onChange={event => setExtension(event.target.value)}><option value="">Tutte le estensioni</option>{catalog.extensions.map(item => <option key={item.name} value={item.name}>{item.name} ({item.count})</option>)}</select>
        <button type="submit" disabled={searching}>{searching ? "Cerco…" : "Cerca nel codice"}</button>
        {(searchResult || searchQuery || extension) && <button className="source-clear" type="button" onClick={clearSearch}>Azzera</button>}
      </form>
      <div className="source-search-help"><span>Ricerca PostgreSQL full-text ordinata per rilevanza.</span><span><code>&quot;frase esatta&quot;</code> per una frase</span><span><code>email OR firebase</code> per alternative</span><span><code>email -test</code> per escludere</span></div>
      {searchError && <div className="source-request-error" role="alert">{searchError}</div>}

      <div className="source-workbench">
        <aside className="source-browser">
          <div className="source-pane-heading"><div><span>{searchResult ? "POSTGRESQL FULL-TEXT" : "STRUTTURA"}</span><strong>{searchResult ? `${searchResult.matchedFiles} file trovati` : catalog.repositoryFullName}</strong></div><small>{searchResult?.isLimited ? "primi 100" : `${catalog.fileCount} file`}</small></div>
          <div className="source-tree" role="tree" aria-label={searchResult ? "Risultati ricerca" : "Albero dei file"}>
            {searchResult ? (
              searchResult.matches.length > 0 ? searchResult.matches.map(match => <button className={`source-search-result ${selectedPath === match.path ? "selected" : ""}`} type="button" role="treeitem" aria-selected={selectedPath === match.path} key={match.path} onClick={() => void openFile(match.path, match.lineNumber)}><span className="source-result-path">{match.path}</span><span className="source-result-context">{match.lineNumber ? `Riga ${match.lineNumber}` : "Percorso"} · {match.language ?? match.extension ?? "testo"} · rilevanza {Math.round(match.relevance * 100)}%</span><code><HighlightedSnippet snippet={match.snippet} /></code></button>) : <div className="source-no-results">Nessun file contiene “{searchResult.query}”.</div>
            ) : (
              tree.map(node => <TreeBranch key={node.path} node={node} depth={0} openDirectories={openDirectories} selectedPath={selectedPath} onToggle={toggleDirectory} onOpen={path => void openFile(path)} />)
            )}
          </div>
        </aside>

        <article className="source-code-panel">
          {fileLoading && <div className="source-code-placeholder"><span className="source-loader" /><strong>Apertura del file…</strong></div>}
          {!fileLoading && fileError && <div className="source-code-placeholder source-code-error"><strong>File non disponibile</strong><p>{fileError}</p></div>}
          {!fileLoading && !fileError && selectedFile && <>
            <header className="source-file-heading">
              <div><strong>{selectedFile.path}</strong><a href={fileLinksHref(selectedFile.path)}>Esplora commit, pull request e simboli →</a><span>{selectedFile.language ?? selectedFile.extension ?? "Testo"} · {numberFormatter.format(selectedFile.lineCount)} righe · {formatBytes(selectedFile.sizeBytes)} · SHA {selectedFile.blobSha.slice(0, 8)}</span></div>
              <div><button type="button" onClick={() => void copyCode()}>{copyLabel}</button><a href={selectedFile.htmlUrl} target="_blank" rel="noreferrer">GitHub ↗</a></div>
            </header>
            <div className="source-code" role="region" aria-label={`Contenuto di ${selectedFile.path}`}>
              {contentLines.map((line, index) => {
                const lineNumber = index + 1;
                return <div id={`source-line-${lineNumber}`} className={`source-code-line ${selectedLine === lineNumber ? "selected-line" : ""}`} key={lineNumber}><span className="source-line-number">{lineNumber}</span><code><HighlightedLine line={line} language={selectedFile.language} /></code></div>;
              })}
            </div>
          </>}
          {!fileLoading && !fileError && !selectedFile && <div className="source-code-placeholder"><strong>Seleziona un file</strong><p>Il contenuto apparirà qui con numeri di riga e metadati.</p></div>}
        </article>
      </div>
    </section>
  );
}

function TreeBranch({ node, depth, openDirectories, selectedPath, onToggle, onOpen }: {
  node: TreeNode;
  depth: number;
  openDirectories: Set<string>;
  selectedPath: string | null;
  onToggle: (path: string) => void;
  onOpen: (path: string) => void;
}) {
  const isOpen = openDirectories.has(node.path);
  if (node.kind === "file") {
    return <button className={`source-tree-row source-tree-file ${selectedPath === node.path ? "selected" : ""}`} style={{ paddingLeft: `${14 + depth * 15}px` }} type="button" role="treeitem" aria-selected={selectedPath === node.path} onClick={() => onOpen(node.path)}><span className="source-tree-icon">·</span><span>{node.name}</span><small>{node.file?.lineCount ?? 0}</small></button>;
  }

  return <div role="treeitem" aria-expanded={isOpen} aria-selected={false}>
    <button className="source-tree-row source-tree-directory" style={{ paddingLeft: `${14 + depth * 15}px` }} type="button" onClick={() => onToggle(node.path)}><span className="source-tree-icon">{isOpen ? "▾" : "▸"}</span><span>{node.name}</span><small>{node.children.length}</small></button>
    {isOpen && <div role="group">{node.children.map(child => <TreeBranch key={child.path} node={child} depth={depth + 1} openDirectories={openDirectories} selectedPath={selectedPath} onToggle={onToggle} onOpen={onOpen} />)}</div>}
  </div>;
}
