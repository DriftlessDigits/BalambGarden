// Builds Data/Soils.json from xivapi: every "* Topsoil" gardening item.
//
// Soil is one of the two things you hand a bed at sow time (soil + seed), and its
// grade is what shifts crossbreed odds - so the plant chain needs the item ids by
// name/grade, not a hand-typed list that rots.
//
// Run: node tools/build-soils.mjs   (network required; snapshot lands in tools/source/)
//
// Shape note (verified 2026-08-14 against the live response, snapshot is the receipt):
// the brief assumed ItemUICategory.Name == "Gardening Items"; the real category name
// is "Gardening" (row 82). Filtering on the brief's string would have yielded zero
// soils. The snapshot in tools/source/xivapi_topsoil.json is the evidence.

import { writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");

const url =
  "https://v2.xivapi.com/api/search?sheets=Item&query=Name~%22topsoil%22&fields=Name,ItemUICategory.Name&limit=100";
const res = await fetch(url);
if (!res.ok) throw new Error(`xivapi ${res.status}`);
const body = await res.json();
writeFileSync(join(root, "tools", "source", "xivapi_topsoil.json"), JSON.stringify(body, null, 2));

const soils = body.results
  .filter((r) => r.fields.ItemUICategory?.fields?.Name === "Gardening")
  .map((r) => ({
    itemId: r.row_id,
    name: r.fields.Name,
    grade: /Grade (\d)/.exec(r.fields.Name) ? Number(/Grade (\d)/.exec(r.fields.Name)[1]) : 0,
  }))
  .sort((a, b) => a.itemId - b.itemId);

if (soils.length < 9) throw new Error(`only ${soils.length} soils - xivapi shape changed?`);
writeFileSync(join(root, "Data", "Soils.json"), JSON.stringify(soils, null, 2));
console.log(`wrote ${soils.length} soils`);
