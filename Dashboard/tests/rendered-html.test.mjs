import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

async function render() {
  const workerUrl = new URL("../dist/server/index.js", import.meta.url);
  workerUrl.searchParams.set("test", `${process.pid}-${Date.now()}`);
  const { default: worker } = await import(workerUrl.href);

  return worker.fetch(
    new Request("http://localhost/", { headers: { accept: "text/html" } }),
    { ASSETS: { fetch: async () => new Response("Not found", { status: 404 }) } },
    { waitUntil() {}, passThroughOnException() {} },
  );
}

test("server-renders the iOne Data Grove dashboard", async () => {
  const response = await render();
  assert.equal(response.status, 200);
  assert.match(response.headers.get("content-type") ?? "", /^text\/html\b/i);

  const html = await response.text();
  assert.match(html, /<title>iOne Data Grove — Dashboard<\/title>/i);
  assert.match(html, /Panoramica/);
  assert.match(html, /GitHub/);
  assert.match(html, /Gestione repository/);
  assert.match(html, /Salute delle sincronizzazioni|Copertura/);
  assert.match(html, /Errori \(0\)/);
  assert.match(html, /PostgreSQL in sola lettura/);
  assert.doesNotMatch(html, /codex-preview|Your site is taking shape|SkeletonPreview/i);
});

test("keeps secrets and database access outside the browser bundle", async () => {
  const [client, sourceExplorer, knowledgeLinks, server, page, packageJson] = await Promise.all([
    readFile(new URL("../app/DashboardClient.tsx", import.meta.url), "utf8"),
    readFile(new URL("../app/repositories/[id]/SourceCodeExplorer.tsx", import.meta.url), "utf8"),
    readFile(new URL("../app/repositories/[id]/KnowledgeLinksExplorer.tsx", import.meta.url), "utf8"),
    readFile(new URL("../server/Program.cs", import.meta.url), "utf8"),
    readFile(new URL("../app/page.tsx", import.meta.url), "utf8"),
    readFile(new URL("../package.json", import.meta.url), "utf8"),
  ]);

  assert.match(client, /127\.0\.0\.1:5088\/api/);
  assert.match(client, /Sospendi/);
  assert.match(client, /Cancella dati/);
  assert.match(client, /Ripristina/);
  assert.doesNotMatch(client, /ConnectionStrings|GitHub:Token|Host=|Password=/i);
  assert.match(server, /GetConnectionString\("iOneDataGrove"\)/);
  assert.match(server, /AsNoTracking\(\)/);
  assert.match(server, /RepositoryFullName/);
  assert.match(server, /SyncOverviewDto/);
  assert.match(server, /MapPatch\("\/api\/repositories\/\{id:long\}\/synchronization"/);
  assert.match(server, /MapDelete\("\/api\/repositories\/\{id:long\}"/);
  assert.match(server, /\/source\/file/);
  assert.match(server, /\/source\/search/);
  assert.match(server, /\/source\/symbols/);
  assert.match(server, /websearch_to_tsquery\('simple'/);
  assert.match(server, /ts_rank_cd/);
  assert.match(server, /knowledge\.code_symbols/);
  assert.match(server, /\/links/);
  assert.match(server, /LIMIT @page_size OFFSET @offset/);
  assert.match(server, /source_label ILIKE/);
  assert.match(sourceExplorer, /Source Code Explorer/i);
  assert.match(sourceExplorer, /Cerca nel codice/);
  assert.match(sourceExplorer, /Albero dei file/);
  assert.match(sourceExplorer, /PostgreSQL full-text/i);
  assert.match(sourceExplorer, /Struttura C#/i);
  assert.match(sourceExplorer, /INDICIZZAZIONE STRUTTURALE/i);
  assert.match(sourceExplorer, /cSharpFiles/);
  assert.match(sourceExplorer, /parameters\.toString\(\)/);
  assert.match(sourceExplorer, /symbolRequestId/);
  assert.doesNotMatch(sourceExplorer, /ConnectionStrings|GitHub:Token|Host=|Password=/i);
  assert.match(knowledgeLinks, /Collegamenti automatici/);
  assert.match(knowledgeLinks, /pageSize/);
  assert.match(knowledgeLinks, /text_reference/);
  assert.match(knowledgeLinks, /entityType/);
  assert.doesNotMatch(knowledgeLinks, /ConnectionStrings|GitHub:Token|Host=|Password=/i);
  assert.match(client, /Filtra per repository/);
  assert.match(client, /Errori \(/);
  assert.match(client, /Mostra solo errori/);
  assert.match(page, /<DashboardClient \/>/);
  assert.doesNotMatch(packageJson, /react-loading-skeleton|site-creator-vinext-starter/);
});
