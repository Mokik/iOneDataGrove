import assert from "node:assert/strict";
import test from "node:test";

// Opt in against an already running API. These tests only read imported data.
const base = process.env.NAVIGATION_TEST_API;

test("entity navigation filters both directions before pagination and resolves source lines", { skip: !base }, async () => {
  async function read(parameters) {
    const response = await fetch(`${base}/links?${new URLSearchParams(parameters)}`);
    assert.equal(response.status, 200);
    return response.json();
  }
  const sample = await read({ relation: "declares_symbol", pageSize: "1" });
  assert.ok(sample.links.length, "Requires an imported repository with C# symbols");
  const symbol = sample.links[0];
  const fileFocus = { focusType: "repository_file", focusId: String(symbol.sourceId), pageSize: "2" };
  const first = await read(fileFocus);
  assert.ok(first.matchedLinks > 0);
  for (const link of first.links) {
    assert.ok((link.sourceType === "repository_file" && link.sourceId === symbol.sourceId) ||
      (link.targetType === "repository_file" && link.targetId === symbol.sourceId));
  }
  if (first.totalPages > 1) {
    const second = await read({ ...fileFocus, page: "2" });
    assert.equal(second.matchedLinks, first.matchedLinks);
    assert.ok(second.links.every(link => !first.links.some(previous => previous.id === link.id)));
  }
  const reverse = await read({ focusType: "code_symbol", focusId: String(symbol.targetId) });
  assert.ok(reverse.links.some(link => link.id === symbol.id));
  assert.ok(symbol.targetPath);
  assert.ok(symbol.targetLine > 0);
  const file = await fetch(`${base}/source/file?${new URLSearchParams({ path: symbol.targetPath })}`);
  assert.equal(file.status, 200);
  const content = await file.json();
  assert.ok(content.content.split("\n").length >= symbol.targetLine);
  const byPath = await read({ path: symbol.targetPath, pageSize: "2" });
  assert.ok(byPath.links.every(link => link.sourcePath === symbol.targetPath || link.targetPath === symbol.targetPath));
  const invalid = await fetch(`${base}/links?focusType=issue`);
  assert.equal(invalid.status, 400);
  const absent = await read({ focusType: "issue", focusId: "9223372036854775807" });
  assert.equal(absent.matchedLinks, 0);
  assert.deepEqual(absent.links, []);
});
