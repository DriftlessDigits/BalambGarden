# Glossary

The vocabulary of Balamb Garden: one tight definition per term and the words to avoid. Agents and contributors use these words in issues, tests, hypotheses, and docs; a concept missing here is either invented language (reconsider) or a real gap (add it). Rulings (the why behind decisions) live in the maintainer's private notes and are summarised as mechanism in `CLAUDE.md`. The _Avoid_ lines cover the words the player reads on screen too: a mock's words are checked against them, and each clash is put to the player with the mock, who rules it.


Balamb Garden is a Dalamud plugin that keeps the household's FFXIV gardens alive: it counts what grows where, keeps honest water and ripeness clocks, drives one-press tend, harvest and replant runs, and shares what each install witnessed with the other install over a relay. Its first law shapes most of this vocabulary: receipts, never inference. The words below are the ones the docs and the code already use; where several words were in play, the best one wins and the rest go under _Avoid_.

## Language

### Receipts and witnesses

**Receipt**:
A fact the game itself committed to, recorded as it landed: a dialogue completion, a chat line, a map read. Every binding, claim and clock in the plugin rests on one.
_Avoid_: event, inference, observation (the retired ring's word), log entry

**Sighting**:
A receipt made by reading the map: what species and stage a slot showed at an instant. Sightings are receipts too; they create and refresh records on covered estates and travel on the wire only when the stage changed.
_Avoid_: observation, poll, scan, snapshot (except for the raw instrument dumps)

**Pending**:
The open half of a two-phase receipt: the plugin acted and has not yet heard the game confirm. Opened at the act, never at delivery; one in flight at most; never persisted.
_Avoid_: in-flight receipt, draft receipt, armed receipt

**Open at the act**:
The rule that a pending is minted at the commit moment of the plugin's own act (the option click, the sow Yes), so the game's line always finds a pending waiting. The one honest exception is a pot whose identity is only born from the map diff after the act.
_Avoid_: open at delivery, open on receipt

**Close**:
The moment the game's own chat line confirms a pending: same verb, same container kind, inside the close window. One receipt, two witnesses: the chain supplies identity, the game supplies the fact.
_Avoid_: match, dedup, merge

**Close window**:
The short span (six seconds) after an act in which a chat line may close its pending. Outside it the pending times out.
_Avoid_: timeout window, grace period

**Chain receipt**:
A receipt whose pending was opened by one of the plugin's own runs and closed by the game's line. Strongest identity, confirmed fact.
_Avoid_: mod receipt, automated receipt, armed receipt

**Chat receipt**:
A receipt made from a recognised game line with no pending open and no run driving: the player's own hand did it. Identity comes from the previous target at line time and is defended fail-closed.
_Avoid_: manual receipt, hand receipt, unclaimed close (the branch name, not the receipt)

**Unclaimed close**:
The branch a recognised line takes when no pending is open: it becomes a chat receipt on its own. Shut while any run is driving (one character, one pair of hands).
_Avoid_: manual branch, fallback parse

**Chain-unconfirmed**:
The honest third source word: the run delivered its act but no closing line ever came, so the receipt is written at timeout under this label and logged loudly. Its count is the instrument that would earn a rethink of chat as the primary witness.
_Avoid_: unconfirmed, timed out, orphan receipt, degraded receipt

**Cancel**:
What happens to a pending that was never delivered and never closed: nothing is written, the attempt is logged. Two absent witnesses are no fact.
_Avoid_: discard, drop silently, failed receipt

**Source**:
Which witness authored a receipt: chain, chat, sighting, or chain-unconfirmed. It travels on the wire for honesty and debugging; derivations never branch on it.
_Avoid_: provenance (that word belongs to timing windows), origin, channel

**Refusal**:
The game declining to act, in its own error line. A refusal receipts nothing, ever, and "already fed" is a refusal.
_Avoid_: failure, error receipt, negative receipt

**Obtain lane**:
The harvest close path: a loot line naming an item, matched on its shape rather than its exact text, with the item's species as an extra witness. The only verb that closes on a different channel from the rest.
_Avoid_: loot parser, harvest line, obtain watch (the listener's name)

**Corroboration**:
A second, independent game-authored witness demanded before a weakly identified receipt is believed. Manual harvests require two agreeing witnesses (the obtained item's species and the container's last-read species); manual bed fertilizes require the crop-name inspect line.
_Avoid_: validation, confirmation, cross-check (fine for the kind-vs-object test, not for the pattern)

**Line catalog**:
The exact game texts that close each verb, each one entered only from a captured tape. Both cells once missing - the bed sow line and the hand bed-harvest path - were captured and field-fired 2026-10-18.
_Avoid_: string table, regex list, parser rules

**Capture**:
A recorded game session kept as evidence (chat tape, probe dump); the only way a line, byte or alias enters the catalog or the tables. Reveal, never invent.
_Avoid_: sample, trace, fixture (that is a capture reused by a desk test)

### Census and claiming

**Census**:
The join-and-record brain: it takes sightings and receipts at an estate and turns them into bound patches and planting records. Acting is censusing: every run's receipts flow back through it.
_Avoid_: scan, survey, sync (that is the household channel), tracker

**Estate**:
One place the plugin can garden: a house plot (yard and interior together), a private room inside a house, or an apartment. Identified by values that read the same every visit; the game's own house identity decides "ours".
_Avoid_: plot (one kind of estate), house, location, property, zone

**Access roster**:
The estates the game itself lists for this player (the teleport list plus owned-estate reads). A roster row proves entry, nothing more; it never claims garden rights.
_Avoid_: roster (ambiguous with the rollup), claimed set, whitelist, permission list

**Covered estate**:
An estate on the access roster. Coverage is the census scope: not covered means not tracked, no matter what the sensors can see there.
_Avoid_: rostered (same idea, less used), actionable estate (the permission-design word; coverage is the mechanism that shipped), tracked estate

**Patch**:
An outdoor garden plot holding eight beds (the Deluxe shape the household runs; Round holds four, Oblong six). Identified at an estate by its ordinal and bound to a map key by receipt.
_Avoid_: garden, plot, bed group, patch key (the key is the binding target, not the patch)

**Bed**:
One outdoor grow slot in a patch, slot 0 to 7. The game's "Nth Bed" is one-based; the slot is N minus one. The eight beds ring the patch clockwise from the top-left corner: 1 2 3 across the top, 4 and 5 down the right, 6 and 7 back along the bottom, 8 on the left (checked in the field 2026-11-07), so every bed touches exactly two others, n-1 and n+1, and bed 1 touches 2 and 8.
_Avoid_: tile, cell (that is the patch strip's word), bed index (the raw game field)

**Pot**:
One indoor grow slot: a placed flowerpot, identified by the object itself, never by position. Pots do not crossbreed, do publish their wilt, and group as one unit per estate on the dashboard.
_Avoid_: flowerpot (the item name; fine in prose about the game), planter, indoor bed

**Container**:
A bed or a pot, when a rule applies to both. The game's own verbs differ by container kind, so the receipt vocabulary keeps them apart.
_Avoid_: slot (that is the bed's position), entity (the wire's word), target

**Join shortlist**:
The candidate pairings between a patch and a map key that a diff between reads proposes. It can propose, never bind.
_Avoid_: pattern match, inferred binding, auto-bind

**Binding**:
The receipted link between a patch (or pot) and its map key. Exactly one surviving candidate at receipt time binds, or nothing does; bindings keep forever, and a later mismatch re-binds rather than trusting silently.
_Avoid_: mapping, association, claim (binding is identity, claim was permission)

**Claim-on-action**:
The founding rule that nothing is claimed except through a completed, receipted interaction: no Claim button, no claim method. Today its teeth are in binding and in the menu (no rights, no menu, no receipt); records themselves are also created by sightings on covered estates (Permission Architecture, 2026-08-15).
_Avoid_: claim ceremony, claim as I go (the retired checkbox), opt-in tracking

**Composed menu**:
The interaction menu the server builds for this character on this press. It is the permission system delivered at act time: the verb is offered or it is not, and a missing verb is a permission answer, not an error.
_Avoid_: context menu, permission check, grant lookup

**Map read**:
One reading of the housing data the game publishes for a zone: species and stage per slot outdoors, species, stage, pigment and wilt per pot indoors. Zeroed entries are the map positively saying "empty".
_Avoid_: DataMap dump (the instrument), poll, snapshot

**Settled read**:
A map read taken once the map has answered after a zone line; it can still lag a stage, which the room re-read catches up. The map populates a beat late; a read that races it stands stale for the whole visit, so arrival and the front-door flip both retry until the map answers, and a chain sow waits for the read that shows its seed before its empty bed counts as news.
_Avoid_: fresh read, confirmed read, stable read

**Room re-read**:
The census reading the side underfoot again after a settled read, a bounded few times: in the first minute after an arrival or a door, and a few seconds after an own-hand gardening line. A settled read can still be a stage behind, and the map catches up by itself; a re-read is how the record catches up with it, without the player leaving or pressing a verb. Never in an act's lag, never anywhere but the place that armed it.
_Avoid_: poll, refresh (the display-only read), rescan, recheck

**Look rule**:
When a map read may overrule a planting's clock. The arrival read never does: every planting on that side whose clock speaks is held, and the card believes the clock. The room re-read's first arrival read (the 5 s look) releases a planting whose look agrees with the clock; one that disagrees stays held until the second (the 15 s look), which is then the truth, logged at warning level with the clock's state and both looks. Every read after is the room's truth. Leaving before the 15 s look leaves the clock speaking until the next arrival there. A look agrees on growing, ripe or dead and, for a pot, its wilt flag; a bed's living read agrees with a clock's wilting. Held in memory only.
_Avoid_: trusted read (the soul's word for what the rule produces), confirmed read, stale read

**The game wins**:
The reconcile law: when the ledger and a live read disagree on anything the game publishes (species, stage, emptiness), the read is right. The ledger keeps only what the game does not publish.
_Avoid_: live wins, map authority, override

### The ledger and the record

**Ledger**:
The persisted store of what the census has learned: one planting record per bed and pot, the bindings, the known estates, the relay cursor and the parked foreign receipts. Fails closed: an unreadable ledger is parked beside itself, never overwritten.
_Avoid_: database, cache, config (the phrase and slugs live there, not the garden)

**Planting**:
One plant's life in one container, from its sowing (or first sighting) to the moment it left (ruled 2026-10-15; the record, its fence, and the rebase reason all say planting). Everything the record holds describes the current planting and nothing before it.
_Avoid_: tenancy (retired 2026-10-15 - it also meant estate access in the permission doc), cycle, generation. The act of sowing is a sow or a plant receipt, never 'a planting'.

**Planting record**:
The one bounded entry the ledger keeps per bed or pot: the latest reading, the planted-at anchor and who sowed, one first-and-last-seen bracket per stage, the last tend and whose hand, and the fence. Nothing in it is ever evicted; a reset empties it.
_Avoid_: claimed bed (the legacy name that survives in code), row, ring, observation ring, bed record

**Latest**:
The newest content reading on a record: species, stage, when, from which witness. It is "the last time we looked", not the last time something changed; empty content shows as no latest at all.
_Avoid_: current state, last seen (ambiguous with the stage bracket), snapshot

**Planted-at**:
The sowing instant, kept only when a plant receipt witnessed it, together with the hand that sowed. The one legitimate anchor for an exact ripe time.
_Avoid_: plant time, sow time, t0, anchor (planted-at is the anchor; the window is anchored)

**Stage bracket**:
The first and last instant a record's plant was seen at one stage. One bracket per stage, only ever widened by evidence, replay-safe; the material every bracketed ripe window is cut from.
_Avoid_: stage history, stage flip, sighting pair, ring entry

**Last tended**:
The water clock: the instant of the newest tend or pot-water on the record, with the hand that did it. The game never publishes it, so the receipt is the only source.
_Avoid_: watered-at (the same clock; use last tended in docs and keep "watered" for the dashboard column), tend time, last water

**Hand**:
Whose act a receipt records: this install (own) or another household install, named only by its opaque wire slug. No name, character or account exists anywhere in the engine or its files.
_Avoid_: user, player, person, install (fine for the machine, not for the actor), a person's name (never in this repo)

**Own hand**:
An act performed on this install, by its run or by the player at this keyboard. Own acts are live: never fenced, never "older".
_Avoid_: local, mine, internal

**Foreign hand**:
An act that arrived over the relay from another household install. Applied under THE RULE, credited by slug, displayed through a local alias.
_Avoid_: external (the hover's display word only), household hand (the settings window's heading "Household hands" only, approved on screen), remote, peer, a person's name

**Fence**:
The instant of a record's last reset, with its reason. Foreign evidence at or before the fence describes a plant that already left and changes nothing; the fence only moves forward.
_Avoid_: reset marker, watermark, cutoff (that is the cut), tombstone

**THE RULE**:
An older receipt never overwrites newer evidence. If it agrees with the current plant it adds its evidence; if it contradicts, it goes to history only; neither ever resets a container. Only evidence newer than everything it contradicts may reset.
_Avoid_: last write wins, newest wins (half of it), ordering rule, conflict resolution

**Rebase**:
The one path that resets a record's planting: contents and clocks leave, the row stays, the fence advances. Every rebase names a reason, carries a cut, is logged and is written to the trail.
_Avoid_: reset (the effect, not the operation), clear, wipe, reconcile (the broader activity)

**Rebase reason**:
Why a record was reset, one of five: a harvest was witnessed; a live read found the slot empty; a new planting was sighted (species changed, stage regressed, or any stage below dead read after a dead read); a pot's entry vanished from a settled indoor read; a plant receipt started a new planting.
_Avoid_: reset cause, trigger, rebase kind

**Cut**:
The instant a rebase removes evidence up to: tends older than the cut leave with the old plant, newer ones survive. A plant receipt cuts at the sowing; a harvest, empty read or absent pot cuts at the trigger; a sighted replant cuts at the old planting's newest evidence.
_Avoid_: cutoff, boundary, horizon, fence (the fence is what the cut leaves behind)

**Keep the watering**:
The ruling for the ambiguous window: a tend newer than the old plant's last sighting but possibly before the new sowing stays on the new record. Event ordering is trusted; the bed sow capture makes it exact.
_Avoid_: drop the tend, conservative reset

**History**:
The permanent record of every plant and harvest by every hand, with yields, forever: the garden's scoreboard. Born empty on 2026-10-15, never back-filled, never the outbox, never pruned. Tends and fertilizes are state, not history. The History tab lists it, newest first.
_Avoid_: log (that is the receipt log), archive, timeline, scoreboard (the purpose, not the name)

**Receipt log**:
The append-only file of this install's own acts and own stage changes. It doubles as the sync outbox; nothing in it is ever edited, including duplicates a since-fixed bug produced.
_Avoid_: receipts file, event log, history (that is plants and harvests, both hands)

**Trail**:
The append-only evidence file the engine writes and never reads back: every receipt and every rebase as it happened.
_Avoid_: debug log, audit log, journal

**Parked file**:
What fail-closed means on disk: a store that cannot be read is renamed beside itself with a stamp and a fresh one starts, so no data is overwritten by a bad load.
_Avoid_: backup (the v3 pre-fold copy is a backup; a park is a quarantine), corrupted file, reset

### Derivations

**Window**:
Every timer: a span from earliest to latest, never a point, carrying its provenance. Precision equals visit cadence, displayed honestly.
_Avoid_: ETA (fine as the column label, never as the type of claim), estimate; for a ripe time, countdown and deadline (those are the water deadline's words)

**Provenance**:
What kind of claim a window is making: anchored, bracketed or estimated. Rendered on every number as a glyph, never by color alone.
_Avoid_: confidence (a number; provenance is a kind), source (that is the receipt's witness), quality

**Anchored**:
A zero-width window: the sowing was witnessed, so the ripe time is exact.
_Avoid_: exact, known, gold-standard

**Bracketed**:
A window cut from two or more stage brackets on one record; it only tightens with visits.
_Avoid_: bounded, inferred, approximate

**Estimated**:
A window from a single stage bracket, or from a record whose brackets contradict each other: an honest guess off the newest stage, never dressed as a bracket.
_Avoid_: guessed, unknown, fallback

**Ripe window**:
When a container's plant ripens or ripened, derived from the planting record and the crop's grow hours. Null only when the record holds no stage evidence at all.
_Avoid_: harvest ETA, maturity estimate, done time

**Stage band**:
The model's slice of grow time each stage occupies (equal thirds for stages one to three; stage four is the end). A tunable model constant, calibration pending.
_Avoid_: stage fraction (the ratio), growth curve, timing table

**Wilt clock**:
The derived water status of a bed or pot from where its clock starts (the last heard watering, else the witnessed sowing, raised for a pot by its seen-fine floor) plus the crop's wilt and wither hours: watered, due, overdue, danger. A crop wilts wherever it grows; the flowerpot flowers never wilt and get no clock. Where the game shows a pot's wilt (its live read in the room, a kept wilt sighting away), the game's word wins the look. A plant seen ripe makes no water claim.
_Avoid_: wilt timer, thirst meter, water ETA, wilt model (the design word for the same thing)

**Water state**:
One of the clock's verdicts: watered (under three quarters of the wilt hours), due (approaching wilt), overdue (wilt likely live), danger (halfway to wither), plus unknown and not applicable.
_Avoid_: wilt state (the game's own condition, which the plugin cannot read outdoors), thirst level, status

**Water deadline**:
When a bed or pot reaches its crop's wilt hours if nobody waters it: the wilt clock's start plus the wilt hours. A visit's deadline is its earliest bed's or pot's, and the card names which kind and where ("A pot wilts at ...", "A bed in patch 1 wilts at ..."), never a number. Said as "water by tonight ~6:15pm" with a countdown beside it; one with no watering recorded counts from its sowing and is marked so.
_Avoid_: water ETA, thirst timer, age (the age is the clock's input, the deadline its answer)

**Pot wilt rule**:
A pot claims water only when its map wilt flag is set and its stage is below ripe. Ripe pots broadcast a nonzero flag while the game offers no watering verb.
_Avoid_: pot wilt byte (the raw field), indoor wilt check

**Wilt sighting**:
The last instant this install's live map read showed a pot wilting under the pot wilt rule, kept on the pot's record so its card can say "pot wilting (seen 2h ago)" away from the room. A sighting in the map-read sense, but local only: it never travels, and the relay's pot-wilt kind is never applied. Cleared by a read showing the pot not wilting, a watering newer than it, or a reset.
_Avoid_: wilt flag (the raw byte), wilt state, saved wilt

**Seen fine**:
The last instant this install's live map read showed a growing pot (below ripe) not wilting. A crop not wilting was watered within its wilt hours, so it floors the pot's wilt clock at that instant minus them: a bound, never a receipt. A pot whose clock rests on the floor alone has its deadline at the look itself, so it reads water soon with a "?" ("may wilt any time - water to be safe"), never wilting, and dying ("may be dying - water now") from the floor's dying point, the earliest it could be. Said "seen fine 10m ago". Local only, like the wilt sighting; cleared by a reset.
_Avoid_: watered (nothing was recorded), last checked, fine sighting

**Tend sweep**:
A run across many beds (Tend All, Water Patch): it skips only what the live map confirmed ripe or empty and fails open on map silence. A deliberate single press is never a sweep and is never filtered.
_Avoid_: tend all (the button), batch tend, mass water

**Yield book**:
The Yields tab's content: per crop, beds and pots apart, every sowing in the history sorted into harvested (a harvest paired with its sowing in the same container), growing (the ledger still holds it alive) and lost (the rest), plus what was gathered and, for beds, the cross rate and the seed shares. All time, from the history's first day. A harvest whose sowing is not on record is never "harvested". A bed crop harvested beside two or more partners splits into a row per **Partner**; soil splits nothing. It replaced Tips (advisory sentences over the plantings, retired 2026-11-08: never opened).
_Avoid_: tips (retired), stats, scoreboard, pipeline view

**Cross rate**:
The harvests that gave a cross seed over the harvests whose cross was heard either way; a harvest not heard never counts as a miss. Shown as a percentage with its count on hover, dim when it rests on fewer than ten harvests. Beside it, the **seed shares**: each cross seed's part of the crossing harvests, rounded to add to 100.
_Avoid_: crossbreed chance (the game's hidden roll), success rate, proc rate

**Unmatched harvest**:
A harvest in the history with no sowing on record in the same container: sown before the history began, or a pot whose key moved. Never counted as harvested (so harvested never tops planted); its items and cross seed still count in the yield book.
_Avoid_: orphan harvest, unpaired, phantom harvest (that is a harvest that never happened)

**Partner**:
The crop a bed sowing could cross with: the first bed beside it in the crossbreed walk holding another crop that forms a crossbreed pair with it, read from its patch at sow when the yield book is built, never stored. A patch with nothing to cross is "no partner"; a sowing with no patch on record (every one before v0.4.1) is "partner not recorded", and so is a harvest with no sowing on record. A crop whose harvests came from two or more partners splits into a row per partner on the Yields tab ("beside Krakka Root"); until then its partners ride the crop name's hover. Pots never have one.
_Avoid_: neighbor (the raw bed beside it, partner or not), parent (the pairs table's word), pair (the two crops together)

**Pipeline**:
A chain of crossbreed recipes whose outputs feed the next recipe's inputs across estates. The household's onion pipeline (figs and mirrors to kukuru, krakka and mirrors to curiel, kukuru and curiel to onion, pots as grow-out) is user story one.
_Avoid_: chain (that is a run), recipe tree, cross plan

**Onion mark**:
The gold the pipeline's goal crop wears, Thavnairian Onion only: on the History tab an onion harvest's crop name and its yield when heard, on the Yields tab the onion row's name and its gathered count above zero (never a split row), and over the tables the **trophy line** ("6 onions harvested, 3 seeds gathered": the onions gathered and the onion seeds the crosses gave) between two gold sparkles, a slow shimmer crossing it. A sowing, another onion (Onion Prince, Wild Onion Set) and a yield not heard take none. It is the only celebration and lives on the tab you open: nothing speaks unprompted.
_Avoid_: badge, notification, highlight (a hover's), goal (alone: say goal crop)

**Rollup**:
The per-patch summary the dashboard rows carry: counts of claimed, ripe, due, overdue, danger, unknown, and the next ripe window. Pots roll up as one group per estate.
_Avoid_: aggregate, summary row, stats

**Garden picture**:
The Garden tab's small picture of an estate: each patch with its eight beds where the game puts them (the ring, see Bed), then the pots, every container coloured by its state (growing by stage, water soon, wilting, dying, ripe, empty, dead under a cross, never read), a dashed outline on a bed or pot with no watering recorded, a "?" on a pot whose clock rests on its seen-fine floor alone. The patch strip's successor.
_Avoid_: map, mini-map, heatmap, grid

**Palette**:
The one home of the plugin's colours, each named by the one thing it means under the colour map: growing (a green ramp by stage), water soon, wilting, dying, ripe, empty, dead (the earth of empty under a cross), onion (gold and its shine, the **Onion mark**), and the no-claim greys (unclaimed, unread, a water clock that is fine); plus plain and dim text and the structure greys of frames, rules and chips. A screen picks a meaning, never a value; what colour does not carry (not heard, provenance, status) goes to line style, glyphs and words.
_Avoid_: theme (ImGui's own style), colour scheme, color constants

**Patch strip**:
The one-cell-per-slot view of a patch, the same census spread out instead of summed. A cell is unclaimed, unknown, growing or ripe.
_Avoid_: bed grid (the expanded table underneath), heatmap, mini-map

**Visit**:
One trip to one estate, where the water, harvest, replant and pot care due there happen together; the unit the Garden tab is built around, one card per planted estate. Its deadline is the estate's water deadline.
_Avoid_: trip, run (a chain's word), route, schedule

**Here card**:
The visit card of the estate the player stands at: first on the Garden tab, larger, and the one card that carries the verbs (Tend All and Replant ripe on it; a click on a patch or a container in its picture opens a small menu of what that state allows, closed by a press or a click elsewhere). Every other card carries Teleport.
_Avoid_: active card, current card, home card

**Reads empty now**:
A container the record holds planted that the live map where the player stands shows empty: empty, not confirmed (earth, dashed). No water or harvest claim; counted toward replant. The game wins on a content mismatch, and the next census read empties the record.
_Avoid_: missing, gone, harvested (unless a harvest was heard)

**Timeline lane**:
One visit's row in "the next six days" under the Garden tab's cards, in card order: the water bar from the deadline bed's watering (or sowing) to the drop at the water deadline, and the ripe times as marks (a tick when anchored, a band as wide as the window otherwise, striped when estimated, a dot at the now line for what is seen ripe), the pots on a row of their own.
_Avoid_: gantt, schedule, calendar, timeline strip (the mock's smaller version, not built)

**Verdict**:
The one sentence above the tabs: across every estate, the worst thing that wants a human and where. Thirst outranks ripeness; when nothing needs anyone it says so and names the next window.
_Avoid_: status line, summary, alert (there are no alert receipts, ever)

**Pace**:
The measured time per unit a run is actually taking, tracked as a moving average behind the run log's countdown. Tracked, never predicted; measured or absent, never borrowed.
_Avoid_: speed, ETA (the output), estimate, timing seed (the per-chain starting value)

### Household sync

**Household**:
Everyone who typed the same phrase: the set of installs whose receipts land in one sealed bucket on the relay. A second household on the same relay is possible by construction.
_Avoid_: team, account, group, the two of us

**Phrase**:
The one secret a household shares, typed into each install's settings and never sent anywhere. From it the install derives both its relay credential and its entity-naming key. Empty phrase means sync off and zero network surface.
_Avoid_: passphrase, password, token (the derived credential), key (the derived naming key)

**Household id**:
The hash of the derived relay credential: the household's namespace on the relay. Debug-only display; nothing is ever set server-side.
_Avoid_: fingerprint (the retired admin-flow word), bucket id, tenant id

**Open household**:
The relay model: the relay configures no credential; any well-formed credential is a household, its hash is the namespace, and typing the phrase is the entire setup. Caps, not secrets, are the anti-abuse defence.
_Avoid_: guest list, admin split (both retired 2026-08-29), shared secret auth

**Relay**:
The one dumb service between installs: it checks a receipt's envelope, stores it once by id, hands it back in order from a cursor, and forgets it after twenty-one days. It never understands gardening.
_Avoid_: server, backend, sync service, cloud

**Dumb relay**:
The law that the relay parses nothing past the envelope: it never validates verb vocabulary or payload shapes, so an old relay passes a newer plugin's receipts through untouched. The ledgers do all the thinking.
_Avoid_: thin server, passthrough, smart relay (the thing it must never become)

**Caps**:
The relay's defence against storage junk: a per-household row ceiling, a distinct-household fuse paid on first write, and a client stamp that is a speed bump, not a lock.
_Avoid_: rate limit, quota, auth

**Envelope**:
The six fields every wire receipt carries: id, entity, entity type, kind, instant, source, plus the per-kind payload. The relay checks the envelope and nothing else.
_Avoid_: schema (the design doc), header, message

**Wire id**:
A receipt's identity on the wire, three parts: user, era, counter. All random slugs or numbers, nothing derived from anything meaningful.
_Avoid_: receipt id (fine locally), install id (the retired two-part shape), GUID

**User slug**:
The stable random slug minted once per install that says "mine versus someone else's" forever. It is the hand on every record and history row.
_Avoid_: user id, install id, player id, name

**Era**:
One counter-life of an install's receipt log, named by a volatile random slug. Re-minted on a counter rewind, a fresh log or a machine change, so a rewound counter can never reuse an id.
_Avoid_: epoch, generation, session, log id

**Counter**:
The monotonic sequence number within an era. Gaps in it are how a missing receipt gets noticed.
_Avoid_: seq (the field), sequence, index

**Entity**:
A bed or pot as it travels: a keyed pseudonym derived from constant game data (house, patch, slot for beds; house and furniture index for pots), minted independently and identically by every install standing at the estate. Pots mint indoors and beds outdoors, never across the door.
_Avoid_: pseudonym (the technique), entity id (the field), hash, bed id

**Entity type**:
The plaintext word bed or pot beside the entity, so a receiving install can classify receipts for estates it has never visited.
_Avoid_: container kind (the local word), is-pot flag

**Kind**:
The wire verb: the actions plant, harvest, tend, fertilize, pot-water, and the observations stage, ripe, pot-wilt. Observations travel on change only. There is no alert kind, ever.
_Avoid_: event type, verb (the local receipt word), action

**Outbox**:
The unsent tail of the receipt log, scoped to the current era, sent in batches and marked by a high-water mark. Offline means the outbox holds; nothing is ever lost or re-shipped from an old era.
_Avoid_: queue, send buffer, pending (that is a receipt awaiting its close)

**Cursor**:
The install's claim about how far it has absorbed the relay's feed. It rides the ledger file, because it is a claim about what that file holds; zero means full replay of the retained window.
_Avoid_: offset, checkpoint, since (the wire parameter), bookmark

**Page**:
One batch the poller pulls from the cursor forward. A page is applied to the ledger, its plants and harvests saved to history, and only then does the cursor advance; a failed save holds the cursor and the page simply re-arrives.
_Avoid_: batch (the sender's word), feed, chunk

**Foreign replay**:
Running another install's receipts through this install's own ledger rules: harvest resets, tend sets the clock, plant starts a planting, stage tightens a bracket, fertilize and pot-wilt are honest no-ops. Every apply is idempotent by id.
_Avoid_: import, merge, sync apply, ingest

**Parked receipt**:
A foreign receipt whose entity this install has not minted yet, held beside the cursor and retried on every fresh mint, pruned after thirty days. No access, no history: a receipt for an estate this install never censuses parks and expires.
_Avoid_: foreign hold (the store's name), orphan, unmatched (the log word), dead letter

**Alias**:
A local label the player types for a hand's slug in the settings window, shown in place of the slug on this install only (the History tab's Hand cells and hand filter). Never written to the wire, the relay, the ledger, the history or the repo; an unlabelled slug shows as "household".
_Avoid_: name, nickname (that is an estate's label in code and docs; on screen the player's word for an alias, approved), display name, mapping

**Household reset**:
What a phrase change means: everything household-scoped goes (entity cache, parked receipts, cursor, foreign hands on records, aliases); timestamps and own receipts stay, because they are facts about the garden, not the household.
_Avoid_: resync, re-pair, wipe

**External display**:
The hover's word for a clock set by a foreign hand (" - external"), derived from the record's hand each time, never stored twice.
_Avoid_: external (as a doc word for the hand itself), remote marker

### Domain

**Crop**:
One growable plant kind with its grow hours, wilt hours and wither hours, from the frozen table of eighty-one. Flowers are crops that resolve to no species when harvested as a bouquet.
_Avoid_: plant (the act), seed (the item that sows it), species (the game's index number)

**Species**:
The game's own two-byte index for what grows in a slot, the vocabulary the map and the wire speak. Numbers travel; names are a local join. Unknown shows as unknown, never mislabelled.
_Avoid_: crop id, seed id, plant name

**Stage**:
How far along a plant is, one to four as the map publishes it; four is ripe, and five is dead (see Dead). A reading of zero is no reading, not a stage; the one exception is a plant read at zero after a dead read, which is a new sowing (the read replaces the dead one either way).
_Avoid_: growth phase, progress, level

**Ripe**:
Stage four: harvestable, the payoff signal, its own wire kind. A ripe plant cannot wilt or die; the game offers no tend verb at ripe, only harvest. A planting the plugin saw sown is also **ripe by the clock** an hour after its ripe time (the sowing plus its grow hours; the game updates plants about hourly) unless its clock said dead first, until a read shows it: drawn hollow, said "ripe by the clock", making no water claim. A read showing it still growing past that hour moves it to an hour after that read.
_Avoid_: mature, done, ready, grown

**Wilt**:
The game's visible thirst state, reached a crop-specific number of hours after the last tend. Published indoors (a pot's wilt flag), never published outdoors: outside, the clock and the game's prose are the only channels, by receipt. The Garden tab calls a bed "wilting" once its wilt clock passes the crop's wilt hours (the colour map, purple): a derived claim from the clock, never a reading. A pot is "wilting" the same way by its clock, and its flag wins where it speaks: the live read in its room (up = wilting; down = not wilting, whatever the clock says), or elsewhere the last time this install saw the flag up, kept until a watering, a read without it, or a reset answers it ("pot wilting (seen 2h ago)").
_Avoid_: thirsty (the dashboard's pre-wilt warning, a different line), drooping, dehydrated

**Wither**:
Death: twenty-four hours of continuous wilt. Mature plants never wither. "Dying" is the colour map's word for the danger window before it (past halfway from wilt to wither, red), never for the withering itself.
_Avoid_: die, dead, expire (for the withering)

**Dead**:
A plant past saving, as the game reads it: the map publishes stage five, and the game's inspect line says "This crop is beyond hope." (receipted on four onion pots, 2026-11-10; a dead bed's stage is unreceipted and read the same way). Never ripe and never growing, it claims no water and counts as a container to clear and sow. The hover says the game's words and when the read was taken: "beyond hope (seen 7m ago)". A later read showing a living plant or an empty container replaces it, and a living one, whatever its crop, is a new planting: the record resets, the dead plant's sowing and watering leave with it.
A planting the plugin saw sown is **dead by the clock** once its death time (where its water clock starts plus the crop's wither hours) passes, when that time comes before its ripe time: drawn hollow, said "dead by the clock since ...". A watering the plugin never heard makes it say so of a plant that is fine; a bed read alive past that time drops its water claim until a watering is recorded.
_Avoid_: withered (the event, see Wither), ripe (stage four only)

**By the clock**:
A state the plugin's clocks hold and no read has shown: ripe, wilting (a bed's wilting always, since beds publish no wilt), dying or dead. Drawn hollow (the state's colour as a ring, filled being what the game showed) and said "by the clock" in hovers, chips and the line above the tabs. One state at a time for a planting the plugin saw sown: growing, ripe (an hour after its ripe time, unless dead first), wilting (past its wilt time, not ripe, not dead), dead (its death time before its ripe time).
_Avoid_: estimated (a ripe window's provenance word), guessed, predicted

**Grow hours**:
A crop's total time from sowing to ripe, from the frozen table; the number every ripe window is cut from.
_Avoid_: grow time (fine in prose), duration, cycle length

**Tend**:
Watering an outdoor bed, the plugin's and the household's most frequent act. Ungated game-wide: anyone who can reach a bed can tend it.
_Avoid_: water (keep it for pots; the player-facing water deadline and "no watering recorded" are the ruled exception), care, maintain

**Pot-water**:
Watering a pot. The game calls the verb "Tend Crop", there is no separate water verb, and the plugin keeps it a distinct kind from tend because pots and beds diverge in verbs, wording and wilt publication.
_Avoid_: tend (for pots), pot tend, indoor water

**Fertilize**:
Feeding a plant to shorten its remaining grow time. Permission-gated, once per hour per plant; the refusal line is how "already fed" is known. Receipted and logged, never a reading and never history.
_Avoid_: feed (fine in prose; the hover says "last fed"), fish meal, pomace (the items)

**Sow**:
Planting a seed in soil in an empty container; soil is consumed every time. The sowing instant is the only anchor a ripe window can have.
_Avoid_: plant (the wire kind and the receipt verb are "plant"; in prose, sow is the act and plant is the thing), replant (a sow after a harvest in the same run)

**Patch at sow**:
What all eight beds of a patch held at the instant one of them was sown, copied exactly as the map read them: each bed by the game's own number, with what the map said of it (occupied or not, species, stage), never interpreted. Raw positions only; which beds count as neighbors is worked out when the record is read, never stored. Carried on every bed sow; pots have none.
_Avoid_: snapshot (that word is the raw instrument dumps'), neighbors (the derived reading, not the record), patch state

**Harvest**:
Taking a ripe plant, which empties its container. The game's loot line is the witness; the receipt is the emptying witness that resets the record at once.
_Avoid_: collect, pick, reap

**Yield**:
What a harvest produced: item and quantity, parsed from the loot line at close, carried on the wire and into history for both hands.
_Avoid_: harvest count, loot, output, drops

**Soil**:
The consumable every sowing needs. Thanalan raises crossbreed chance, Shroud raises yield, La Noscean is dead since the HQ removal, potting soil is filler. Exactly one soil kind in the bags is the soil; otherwise the picker asks. Every sow records its soil by item id, both hands, beds and pots; absent means not heard. The topsoil table holds only the nine outdoor soils, so a soil name resolves through the game's item list when the table does not know it.
_Avoid_: topsoil (the game's item prefix), dirt, fertilizer (a different consumable)

**Sow question**:
The game's yes/no prompt before every sowing: "Prepare the bed with a bag of grade 3 thanalan topsoil and a bag of krakka root seeds?" The only place the game names a sowing's soil. A chain checks it against its plan before answering Yes; for a sowing with no chain, a passive listener reads it and never answers it.
_Avoid_: confirm prompt (fine in prose), dialog, popup

**Soil note**:
The sow question's soil, held for the sow line that follows. It holds while the question is open (ten minutes at most, in case the close is never heard) and for a few seconds after it closes; a sow line takes it and clears it; a new question replaces it. A No prints no sow line, so the note runs out unused. Two quick sowings never trade soils.
_Avoid_: soil cache, pending soil, remembered soil (nothing about soil is remembered past one sowing)

**Crossbreed pair**:
Two different species that, adjacent at sowing time, can produce a third seed. Decided at planting, walked right, down, up, left from the new bed, first valid partner wins; never in pots. The frozen table holds three thousand eight hundred and eighty-six pairs.
_Avoid_: cross (fine in prose), recipe, combo, intercross

**Pigment**:
The colour a flower in a pot wears, steered by coloured pomace and published in the pot's map entry beside the stage. Species names are colourless; harvest items and talk lines carry the colour word.
_Avoid_: color flag, flower variant, dye

**Ward tick**:
The game's roughly sixty-three-minute server cycle on which garden timers are checked; a finished plant can sit un-flagged until the next tick. Fertilizing forces an immediate check.
_Avoid_: server tick, refresh interval, housing cron

**Flowerpot seed list**:
The pot-only cosmetic seeds that arrive about one per game patch, kept as an easily updated table because everything else in gardening has been frozen since 2020.
_Avoid_: flower table, cosmetic crops, new seeds
