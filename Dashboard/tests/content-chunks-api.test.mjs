import assert from "node:assert/strict";
import test from "node:test";

// Opt in against an already running local API. The test is read-only.
const configuredBase = process.env.CONTENT_CHUNKS_TEST_API;
const base = configuredBase?.replace(/\/$/, "");

async function readJson(path, parameters) {
  const query = parameters ? `?${new URLSearchParams(parameters)}` : "";
  const response = await fetch(`${base}${path}${query}`);
  assert.equal(response.status, 200, `${path}${query} deve rispondere 200`);
  return response.json();
}

test("content chunks preserve source provenance and support full-text search", { skip: !base }, async () => {
  const dashboard = await readJson("/dashboard");
  const repository = dashboard.repositories.find(item => item.fullName === "iOneSolutionsSrl/iOneGavio");
  assert.ok(repository, "Repository iOneGavio non trovato");

  const catalog = await readJson(`/repositories/${repository.id}/chunks`, { pageSize: "5" });
  assert.ok(catalog.totalChunks > 0, "Nessun chunk indicizzato");
  assert.ok(catalog.totalSources > 0, "Nessuna fonte indicizzata");
  assert.ok(catalog.estimatedTokens > 0, "Stima token assente");
  assert.ok(catalog.facets.some(facet => facet.sourceType === "repository_file"));
  assert.ok(catalog.facets.some(facet => facet.sourceType === "code_symbol"));
  assert.ok(catalog.chunks.some(chunk => chunk.sourcePath && chunk.startLine > 0 && chunk.endLine >= chunk.startLine));

  const search = await readJson(`/repositories/${repository.id}/chunks`, {
    q: "ParticolareImportRete",
    pageSize: "50",
  });
  assert.ok(search.matchedChunks > 0, "La ricerca non trova ParticolareImportRete");
  assert.ok(search.chunks.some(chunk => chunk.content.includes("ParticolareImportRete")));
  assert.ok(search.chunks.some(chunk => chunk.sourcePath && chunk.startLine > 0));
});

test("content chunks validate source filters", { skip: !base }, async () => {
  const dashboard = await readJson("/dashboard");
  const repository = dashboard.repositories.find(item => item.fullName === "iOneSolutionsSrl/iOneGavio");
  assert.ok(repository);

  const filtered = await readJson(`/repositories/${repository.id}/chunks`, {
    sourceType: "code_symbol",
    pageSize: "10",
  });
  assert.ok(filtered.chunks.length > 0);
  assert.ok(filtered.chunks.every(chunk => chunk.sourceType === "code_symbol"));

  const invalid = await fetch(`${base}/repositories/${repository.id}/chunks?sourceType=unknown`);
  assert.equal(invalid.status, 400);
});
