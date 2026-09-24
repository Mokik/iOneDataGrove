import assert from "node:assert/strict";
import test from "node:test";

// Opt in against an already running local API. The test is read-only.
const configuredBase = process.env.GLOBAL_SEARCH_TEST_API;
const base = configuredBase?.replace(/\/$/, "");

const samples = [
  { repository: "iOneSolutionsSrl/iOneCostantin", query: "FiltersArgs" },
  { repository: "iOneSolutionsSrl/iOneGavio", query: "ParticolareImportRete" },
  { repository: "iOneSolutionsSrl/iOneIpWow", query: "RegistriTestataController" },
];

async function readJson(path, parameters) {
  const query = parameters ? `?${new URLSearchParams(parameters)}` : "";
  const response = await fetch(`${base}${path}${query}`);
  assert.equal(response.status, 200, `${path}${query} deve rispondere 200`);
  return response.json();
}

test("global search finds real symbols and files in the selected repository", { skip: !base }, async () => {
  const dashboard = await readJson("/dashboard");

  for (const sample of samples) {
    const repository = dashboard.repositories.find(item => item.fullName === sample.repository);
    assert.ok(repository, `Repository non trovato: ${sample.repository}`);

    const catalog = await readJson("/search", {
      q: sample.query,
      repositoryId: String(repository.id),
      pageSize: "50",
    });

    assert.ok(catalog.matchedResults > 0, `${sample.repository}: nessun risultato per ${sample.query}`);
    assert.ok(catalog.results.length > 0, `${sample.repository}: prima pagina vuota`);
    assert.ok(catalog.results.every(result => result.repositoryId === repository.id));

    const sourceResult = catalog.results.find(result =>
      result.entityType === "code_symbol" || result.entityType === "repository_file");
    assert.ok(sourceResult, `${sample.repository}: manca un risultato navigabile nel codice`);
    assert.ok(sourceResult.sourcePath, `${sample.repository}: percorso sorgente mancante`);
  }
});

test("global search spans repositories and entity types", { skip: !base }, async () => {
  const catalog = await readJson("/search", { q: "ParticolareImportRete", pageSize: "50" });
  assert.ok(catalog.matchedResults > 0);
  assert.ok(catalog.results.some(result => result.repositoryFullName === "iOneSolutionsSrl/iOneGavio"));
  assert.ok(catalog.results.some(result => result.entityType === "code_symbol"));
  assert.ok(catalog.results.some(result => result.entityType === "repository_file"));
  assert.ok(catalog.results.some(result => result.entityType === "commit"));
});
test("global search validates filters and query length", { skip: !base }, async () => {
  const invalidType = await fetch(`${base}/search?q=FiltersArgs&entityType=unknown`);
  assert.equal(invalidType.status, 400);

  const shortQuery = await fetch(`${base}/search?q=x`);
  assert.equal(shortQuery.status, 400);
});

