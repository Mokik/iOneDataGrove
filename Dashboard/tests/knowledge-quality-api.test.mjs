import assert from "node:assert/strict";
import test from "node:test";

// Opt in against an already running local API. The test is read-only.
const configuredBase = process.env.KNOWLEDGE_QUALITY_TEST_API;
const base = configuredBase?.replace(/\/$/, "");

const verifiedChains = [
  {
    repository: "iOneSolutionsSrl/iOneCostantin",
    issue: 1,
    pullRequest: 2,
    commit: "d37994a4",
    file: "IOne18OilS001/Api/Trasporti/ViaggiController.cs",
    symbol: "IOne18OilS001.Api.Trasporti.ViaggiRepository.FiltersArgs",
  },
  {
    repository: "iOneSolutionsSrl/iOneGavio",
    issue: 48,
    pullRequest: 49,
    commit: "c2c46d88",
    file: "IOne.Domain/Helpers/OrdiniHelper.cs",
    symbol: "IOne.Domain.Helpers.TrasportiPetroliferoReteOrdine.ParticolareImportRete",
  },
  {
    repository: "iOneSolutionsSrl/iOneIpWow",
    issue: 14,
    pullRequest: 15,
    commit: "ca56344a",
    file: "IOneIpWow/Api/Magazzini/RegistriTestataController.cs",
    symbol: "IOneIpWow.Api.Magazzini.RegistriTestataController",
  },
];

async function readJson(path, parameters) {
  const query = parameters ? `?${new URLSearchParams(parameters)}` : "";
  const response = await fetch(`${base}${path}${query}`);
  assert.equal(response.status, 200, `${path}${query} deve rispondere 200`);
  return response.json();
}

test("verified issue to symbol chains remain navigable", { skip: !base }, async () => {
  const dashboard = await readJson("/dashboard");
  const quality = dashboard.knowledgeQuality;
  assert.ok(quality, "Il riepilogo qualità deve essere presente nella dashboard");
  assert.equal(
    quality.reviewLinks,
    quality.repositories.reduce((total, repository) => total + repository.reviewLinks, 0),
    "Il totale dei riferimenti deve corrispondere alla somma per repository",
  );
  assert.equal(quality.repositoriesWithReviewLinks, quality.repositories.length);

  for (const repositoryQuality of quality.repositories) {
    assert.ok(repositoryQuality.reviewLinks > 0, `${repositoryQuality.repositoryFullName}: conteggio qualità non valido`);
    assert.ok(dashboard.repositories.some(repository => repository.id === repositoryQuality.repositoryId), `${repositoryQuality.repositoryFullName}: repository non presente nel catalogo`);
  }
  if (quality.repositories.length > 0) {
    const repositoryQuality = quality.repositories[0];
    const reviewCatalog = await readJson(`/repositories/${repositoryQuality.repositoryId}/links`, {
      relation: "references",
      review: "true",
      pageSize: "100",
    });
    assert.ok(reviewCatalog.matchedLinks > 0, `${repositoryQuality.repositoryFullName}: filtro qualità vuoto`);
    assert.ok(reviewCatalog.links.every(link => link.requiresReview), `${repositoryQuality.repositoryFullName}: il filtro include riferimenti già confermati`);
  }

  for (const expected of verifiedChains) {
    const repository = dashboard.repositories.find(item => item.fullName === expected.repository);
    assert.ok(repository, `Repository non trovato: ${expected.repository}`);

    const closingCatalog = await readJson(`/repositories/${repository.id}/links`, {
      relation: "closes",
      q: `#${expected.issue}`,
      pageSize: "100",
    });
    const closingLink = closingCatalog.links.find(link =>
      link.sourceType === "pull_request" &&
      link.sourceLabel.startsWith(`PR #${expected.pullRequest} ·`) &&
      link.targetType === "issue" &&
      link.targetLabel.startsWith(`#${expected.issue} ·`));
    assert.ok(closingLink, `${expected.repository}: issue → PR non trovato`);

    const pullRequestCatalog = await readJson(`/repositories/${repository.id}/links`, {
      focusType: "pull_request",
      focusId: String(closingLink.sourceId),
      relation: "contains_commit",
      pageSize: "100",
    });
    const commitLink = pullRequestCatalog.links.find(link =>
      link.targetType === "commit" && link.targetLabel.startsWith(`${expected.commit} ·`));
    assert.ok(commitLink, `${expected.repository}: PR → commit non trovato`);

    const commitCatalog = await readJson(`/repositories/${repository.id}/links`, {
      focusType: "commit",
      focusId: String(commitLink.targetId),
      relation: "modifies_file",
      pageSize: "100",
    });
    const fileLink = commitCatalog.links.find(link =>
      link.targetType === "repository_file" && link.targetPath === expected.file);
    assert.ok(fileLink, `${expected.repository}: commit → file non trovato`);

    const symbolCatalog = await readJson(`/repositories/${repository.id}/links`, {
      focusType: "repository_file",
      focusId: String(fileLink.targetId),
      relation: "declares_symbol",
      q: expected.symbol,
      pageSize: "100",
    });
    const symbolLink = symbolCatalog.links.find(link =>
      link.targetType === "code_symbol" && link.targetLabel === expected.symbol);
    assert.ok(symbolLink, `${expected.repository}: file → simbolo non trovato`);
    assert.ok(symbolLink.targetLine > 0, `${expected.repository}: riga simbolo non valida`);
  }

  const costantin = dashboard.repositories.find(item => item.fullName === "iOneSolutionsSrl/iOneCostantin");
  const mergeReferences = await readJson(`/repositories/${costantin.id}/links`, {
    relation: "references",
    q: "Merge pull request",
    pageSize: "100",
  });
  assert.ok(mergeReferences.links.length > 0, "Sono richiesti riferimenti standard da merge commit");
  assert.equal(mergeReferences.reviewLinks, 0, "I merge commit standard non richiedono verifica");
  assert.ok(mergeReferences.links.every(link => link.requiresReview === false));
});