// Builds Balamb Garden's frozen domain data tables from community sources.
//
// Inputs (tools/source/, snapshotted 2026-08-12):
//   CropTimes.cs        - Ottermandias/Accountant (grow/wilt hours + ItemId/SeedId, community constants)
//   crossbreeding.json  - nick75g/FFXIV-Crossbreed-Helper (result -> parent pairs, name-keyed, data from ffxivgardening.com)
//   seeds.json          - same repo (seed -> y/yn/n = cross-only / both / gather-only)
//   othersources.json   - same repo (seed -> gather/vendor source strings)
//
// Outputs (Data/):
//   Crops.json           - one record per crop, ID-keyed, with grow/wilt/wither, flags, sources
//   CrossbreedPairs.json - result seedId -> [[parentSeedIdA, parentSeedIdB], ...]
//
// Gardening is mechanically frozen since ~2020 (see vault: Balamb Garden - Gardening Domain Research),
// so these are build-once assets. Re-run only if a patch ever touches outdoor gardening.
//
// Usage: node tools/build-domain-data.mjs

import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const src = (f) => join(root, "tools", "source", f);
const out = (f) => join(root, "Data", f);

// ---- 1. Parse Accountant's CropTimes.cs ------------------------------------
const cropTimesCs = readFileSync(src("CropTimes.cs"), "utf8");
const tupleRe = /\((\d+) \* (\d+), (\d+), (\d+), (\d+)\), \/\/ (.+)/g;
const crops = [];
for (const m of cropTimesCs.matchAll(tupleRe)) {
  const [, a, b, wilt, itemId, seedId, name] = m;
  const growHours = Number(a) * Number(b);
  if (name.trim() === "Nothing") continue;
  crops.push({
    name: name.trim(),
    growHours,
    wiltHours: Number(wilt),
    witherHours: Number(wilt) + 24, // wither = wilt + 24h continuous (community-verified)
    itemId: Number(itemId),
    seedId: Number(seedId),
  });
}

// ---- 2. Load Crossbreed Helper data ----------------------------------------
const crossbreeding = JSON.parse(readFileSync(src("crossbreeding.json"), "utf8"));
const seedFlags = JSON.parse(readFileSync(src("seeds.json"), "utf8"));
const otherSources = JSON.parse(readFileSync(src("othersources.json"), "utf8"));

// ---- 3. Join names: helper seed-name -> Accountant crop record -------------
// Helper keys are seed display names ("Almond Seeds", "Royal Fern Sori", "Popoto Set");
// Accountant comments are crop names ("Almond", "Royal Fern", "Dalamud Popoto Set").
const norm = (s) =>
  s
    .toLowerCase()
    .replace(/ seeds$/, "")
    .replace(/ sori$/, "")
    .replace(/ pips$/, "")
    .replace(/ pits$/, "")
    .replace(/ kernels$/, "")
    .replace(/ sapling$/, "")
    .replace(/ bulbs?$/, "")
    .replace(/ cor(m|ms)$/, "")
    .replace(/[^a-z0-9]/g, "");

// Known spelling/name divergences between the two sources.
const aliases = {
  // helper-normalized -> accountant-normalized
  broombush: "broombrush", // Accountant spells it Broombrush
  popotoset: "dalamudpopotoset",
  faerieapple: "fairieapple", // Accountant spells it Fairie
  gysahlgreen: "gysahlgreens",
  earthlight: "earthshard", // Accountant names the elemental crop by its shard
};

const byNorm = new Map(crops.map((c) => [norm(c.name), c]));
const resolve = (helperName) => {
  const n = norm(helperName);
  return byNorm.get(n) ?? byNorm.get(aliases[n]) ?? null;
};

// ---- 4. Attach flags + sources to crops, report unmatched ------------------
const unmatchedHelper = [];
for (const [seedName, flag] of Object.entries(seedFlags)) {
  const crop = resolve(seedName);
  if (!crop) {
    unmatchedHelper.push(seedName);
    continue;
  }
  crop.seedName = seedName;
  crop.crossOnly = flag === "y";
  crop.crossable = flag === "y" || flag === "yn";
  crop.gatherable = flag === "yn" || flag === "n";
  const sources = otherSources[seedName];
  if (sources) crop.sources = sources;
}
const unmatchedAccountant = crops.filter((c) => !("seedName" in c)).map((c) => c.name);

// ---- 5. Build ID-keyed crossbreed pair table --------------------------------
const pairTable = {};
const unmatchedCrossNames = new Set();
let pairCount = 0;
for (const [resultName, pairs] of Object.entries(crossbreeding)) {
  const result = resolve(resultName);
  if (!result) {
    unmatchedCrossNames.add(resultName);
    continue;
  }
  const idPairs = [];
  for (const [pa, pb] of pairs) {
    const a = resolve(pa);
    const b = resolve(pb);
    if (!a || !b) {
      if (!a) unmatchedCrossNames.add(pa);
      if (!b) unmatchedCrossNames.add(pb);
      continue;
    }
    idPairs.push([a.seedId, b.seedId]);
    pairCount++;
  }
  if (idPairs.length) pairTable[result.seedId] = idPairs;
}

// ---- 6. Species index (ward DataMap u16 -> crop) -----------------------------
// Lotlab/FFXIV-Gardening-Tracker's seeds.json carries the game's internal
// gardening index (the u16 the ward DataMap stores per bed) with GLOBAL
// seed/item ids (names are CN - ignored). Joined to Accountant crops by seedId.
// Desk-verified 2026-08-12: index 0x11 = Mirror Apple matches the founding
// dump exactly; 0x41 = Old World Fig (dissolves the founding session's
// "0x41 conflict" - a patch label was wrong, not the table).
// Indoor/deco tail: names absent from the Lotlab snapshot, verified via xivapi 2026-08-13.
// KNOWN GAP (08-15, ledgered): these are INDEX names, not necessarily the names the game
// speaks in dialogue. Species 103 is "Garden Sunflower" here, but a ripe pot's Talk calls
// it "Red Sunflowers" - so a name-keyed receipt join misses and the honest "cannot bind"
// path runs. Deliberately NOT aliased: how variants should be learned is a design
// question waiting on bench evidence, not something to guess into the generator.
// Colorless BASE names (08-16 receipt: color is pot pigment - b2 high nibble - not
// species; Drift's blue lupins were wearing "Red"). 82/93 receipted in his pots, item
// names xivapi-verified 08-16 (17999 "Yellow Daisies", 30371 "Purple Cosmos").
const SPECIES_NAME_OVERRIDES = {
  82: 'Daisies',
  93: 'Cosmos',
  100: 'Morning Glories',
  102: 'Lupins',
  103: 'Sunflowers',
  107: 'Tea Flowers',
};

const lotlab = JSON.parse(readFileSync(src("lotlab_seeds.json"), "utf8"));
const bySeedId = new Map(crops.map((c) => [c.seedId, c]));
const speciesIndex = {};
for (const entry of lotlab) {
  const crop = bySeedId.get(entry.Seed.Id);
  speciesIndex[entry.Index] = {
    seedId: entry.Seed.Id,
    itemId: entry.Item.Id,
    // null = flowerpot flower / not in outdoor crop table
    name: crop?.name ?? SPECIES_NAME_OVERRIDES[entry.Index] ?? null,
  };
}

// Ids the game uses that the Lotlab snapshot predates: LISTED with nothing filled in.
// Listing is not a claim - a null seedId means "this species exists, we have no join",
// and the Engine answers null rather than inventing one. 108 seen in-game 2026-08-13.
const SPECIES_TAIL_UNKNOWN = [108];
for (const ix of SPECIES_TAIL_UNKNOWN) {
  if (speciesIndex[ix]) continue;
  speciesIndex[ix] = { seedId: null, itemId: null, name: null };
}

const unnamed = Object.values(speciesIndex).filter((s) => !s.name).length;

// ---- 7. Emit ----------------------------------------------------------------
writeFileSync(out("Crops.json"), JSON.stringify(crops, null, 2));
writeFileSync(out("CrossbreedPairs.json"), JSON.stringify(pairTable, null, 2));
writeFileSync(out("SpeciesIndex.json"), JSON.stringify(speciesIndex, null, 2));
console.log(`species index: ${Object.keys(speciesIndex).length} (${unnamed} unnamed flowerpot entries)`);

// ---- 8. (retired) Generated C# species table --------------------------------
// SpeciesTable.g.cs is gone: the Engine's DomainTables reads SpeciesIndex.json directly
// (embedded resource), so a second hand-shipped copy of the same table could only ever
// drift from it. Everything that used to call SpeciesTable.Name now calls
// DomainTables.SpeciesName - including the debug probe.


console.log(`crops: ${crops.length}`);
console.log(`cross results: ${Object.keys(pairTable).length}, pairs: ${pairCount}`);
console.log(`unmatched helper seeds (${unmatchedHelper.length}):`, unmatchedHelper);
console.log(`accountant crops with no helper entry (${unmatchedAccountant.length}):`, unmatchedAccountant);
console.log(`unmatched cross names (${unmatchedCrossNames.size}):`, [...unmatchedCrossNames]);
