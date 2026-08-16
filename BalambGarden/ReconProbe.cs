#if DEBUG
using System;
using System.Text;
using BalambGarden.Engine.Sensing;
using BalambGarden.Game;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BalambGarden;

/// <summary>
/// One-shot recon instruments, log-only, DEBUG builds only - a Release plugin carries
/// none of this. Two questions it was built to answer:
/// (1) What does estate identity look like from HousingManager (ward/plot/room,
///     inside/outside) - the rebuild's estate-roster key.
/// (2) Do bed objects carry plant/growth state passively in native memory? The
///     dump is a diff instrument: capture beds in different states (young plant,
///     ripe, empty, neighbor's) and diff the hex - the bytes that differ per
///     state ARE the answer.
///
/// <para>Both were answered (08-12/08-13 captures) and the answers became MapSensor +
/// MapFormat + ObjectSensor. It is kept because the next time the game moves something,
/// an instrument that no longer exists cannot be switched on.</para>
///
/// <para>IMPORTANT: the probe reads through the SAME sensors the app uses
/// (<see cref="MapSensor.ReadRawEntries"/>, <see cref="ObjectSensor"/>). An instrument
/// with its own route could disagree with the app about what was on screen, and then
/// neither reading is evidence. The raw hex is dumped verbatim beside the decode, so a
/// decoder bug is visible rather than hidden.</para>
/// </summary>
internal static unsafe class ReconProbe
{
    internal static void LogHousingLocation()
    {
        try
        {
            var housing = HousingManager.Instance();
            if (housing == null)
            {
                Plugin.Log.Information("[Probe] HousingManager: null (not in a housing area?)");
                return;
            }

            Plugin.Log.Information(
                $"[Probe] Housing: territory={Plugin.ClientState.TerritoryType} "
                + $"ward={housing->GetCurrentWard()} plot={housing->GetCurrentPlot()} "
                + $"room={housing->GetCurrentRoom()} inside={housing->IsInside()}");

            // The raw indoor identity, always - the sensor's refusal warning used to be
            // the only surface printing it, and a silent-null path (apartments: the 08-15
            // capture showed plot=-128 short-circuits before any HouseId read) meant the
            // one receipt we wanted most never landed in a capture.
            if (housing->IsInside())
            {
                var houseId = housing->GetCurrentIndoorHouseId();
                Plugin.Log.Information(
                    $"[Probe] HouseId: 0x{houseId.Id:X16} territory={houseId.TerritoryTypeId} "
                    + $"world={houseId.WorldId} ward={houseId.WardIndex} plot={houseId.PlotIndex} "
                    + $"room={houseId.RoomNumber} isApartment={houseId.IsApartment} "
                    + $"apartmentDivision={houseId.ApartmentDivision} isWorkshop={houseId.IsWorkshop}");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Probe] housing location read failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Walks the housing territory's own records: the furniture vector (item ids +
    /// positions, read directly - no sensor covers it) and the gardening DataMap, read
    /// through MapSensor and decoded through MapFormat exactly as the census does.
    /// </summary>
    internal static void DumpHousingRecords()
    {
        try
        {
            DumpFurnitureVector();
            DumpPotDiscriminator();
            DumpDataMap();
            DumpBedGimmicks();
            DumpManagerDiff();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Probe] housing records dump failed: {ex}");
        }
    }

    private static byte[]? managerSnapshot;

    /// <summary>The wilt hunt's last unsearched room (Sam's push, 08-16: "hard to believe
    /// wilting isn't stored in data somewhere" - he was right to push; the DataMap entry
    /// and the whole 0x1C0 EventObject were both exhausted honestly, but
    /// HousingObjectManager is 0x12E8 bytes and ClientStructs maps 0x18 of it). Two-press
    /// isolation: first capture snapshots the manager, the player changes ONE thing
    /// (water one wilted bed), the next capture prints exactly which bytes moved. Pointer
    /// churn from the StdMap is expected noise; a per-bed stride correlating with the
    /// watered slot is the signal.</summary>
    private static unsafe void DumpManagerDiff()
    {
        var furniture = MapSensor.CurrentFurniture();
        if (furniture == null)
        {
            managerSnapshot = null;
            return;
        }

        const int Size = 0x12E8;
        var mgr = (byte*)&furniture->ObjectManager;
        var now = new byte[Size];
        for (var i = 0; i < Size; i++)
            now[i] = mgr[i];

        if (managerSnapshot is { } prev)
        {
            var changed = 0;
            for (var i = 0; i < Size; i++)
            {
                if (prev[i] == now[i])
                    continue;
                if (changed++ < 200)
                    Plugin.Log.Information($"[Probe] mgr-diff +0x{i:X4}: {prev[i]:X2} -> {now[i]:X2}");
            }
            Plugin.Log.Information($"[Probe] mgr-diff: {changed} byte(s) changed since last capture");
        }
        else
        {
            Plugin.Log.Information($"[Probe] mgr-diff: baseline snapshot taken (0x{Size:X} bytes)");
        }

        managerSnapshot = now;
    }

    /// <summary>Furniture positions/item ids. The app reads this vector too now
    /// (<see cref="MapSensor.ReadFurniture"/> - a pot's index is its DataMap key, receipted
    /// 08-15), but it only keeps index and position; the probe prints the item id, stain and
    /// distance beside them, which is what turned the correspondence up in the first place.
    /// The TERRITORY is chosen by the sensor either way, so the two cannot end up describing
    /// different houses.</summary>
    private static void DumpFurnitureVector()
    {
        var furniture = MapSensor.CurrentFurniture();
        if (furniture == null)
        {
            Plugin.Log.Information("[Probe] no housing territory / furniture manager");
            return;
        }

        var me = ECommons.GameHelpers.Player.Object;
        var myPos = me?.Position ?? default;

        Plugin.Log.Information(
            $"[Probe] furniture vector: {furniture->FurnitureVector.Count} entries, "
            + $"object array count: {furniture->ObjectManager.ObjectArray.ObjectCount}");

        var logged = 0;
        foreach (var pointer in furniture->FurnitureVector)
        {
            var item = pointer.Value;
            if (item == null || item->Id == 0)
                continue;
            var distance = System.Numerics.Vector3.Distance(myPos, item->Position);
            if (distance > 45f)
                continue;

            Plugin.Log.Information(
                $"[Probe] furniture idx={item->Index} id={item->Id} stain={item->Stain} "
                + $"pos={item->Position:F1} dist={distance:F1}y");
            logged++;
        }
        Plugin.Log.Information($"[Probe] furniture within 45y: {logged}");
    }

    /// <summary>The pot-gate receipt (08-16): every furniture entry beside what the game's
    /// own sheets say it IS - Item name and HousingFurniture category - and what the
    /// DataMap holds at that index. Phantom pots (this estate: seven of them) decode as
    /// plants out of non-pot furniture data; the discriminator for sightings-record-pots
    /// comes off this dump, never off a hardcoded item-id list.</summary>
    private static void DumpPotDiscriminator()
    {
        var furniture = MapSensor.CurrentFurniture();
        if (furniture == null)
        {
            Plugin.Log.Information("[Probe] pot-gate: no furniture manager");
            return;
        }

        var items = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        var housing = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.HousingFurniture>();
        var map = MapSensor.ReadRawEntries();

        // Sheet reconnaissance (08-16): neither raw id nor low-word keyed either sheet,
        // so walk the sheet itself - row range, then every Flowerpot row it holds. The
        // mapping from furniture id to sheet row becomes arithmetic we can SEE.
        Plugin.Log.Information(
            $"[Probe] pot-gate sheets: Item rows={items.Count}, HousingFurniture rows={housing.Count}");
        uint hfMin = uint.MaxValue, hfMax = 0;
        foreach (var row in housing)
        {
            if (row.RowId < hfMin) hfMin = row.RowId;
            if (row.RowId > hfMax) hfMax = row.RowId;
            var name = row.Item.ValueNullable?.Name.ExtractText() ?? "";
            if (name.Contains("Flowerpot", StringComparison.OrdinalIgnoreCase))
                Plugin.Log.Information(
                    $"[Probe] pot-gate HF row={row.RowId} itemRow={row.Item.RowId} "
                    + $"name={name} cat={row.HousingItemCategory}");
        }
        Plugin.Log.Information($"[Probe] pot-gate HF row range: {hfMin}..{hfMax}");

        Plugin.Log.Information("[Probe] pot-gate: furniture x sheets x datamap");
        foreach (var pointer in furniture->FurnitureVector)
        {
            var item = pointer.Value;
            if (item == null || item->Id == 0)
                continue;

            // Which sheet does item->Id key? Print the answers; the log gets to say.
            // 08-16 lead: real furniture ids all sit above 0x10000 - try the low word as
            // a HousingFurniture row (65981 -> 445) alongside the raw id.
            var asItem = items.GetRowOrDefault(item->Id);
            // RECEIPT (08-16): furniture id + 0x20000 = HousingFurniture row. Oasis
            // Flowerpot 65981 -> 197053, Riviera 65979 -> 197051, both receipted pots.
            var asHousing = housing.GetRowOrDefault(item->Id + 0x20000u);

            var line = $"[Probe] pot-gate idx={item->Index} id={item->Id}"
                + $" | Item: {(asItem is { } i ? i.Name.ExtractText() : "-")}"
                + $" | HousingFurniture: {(asHousing is { } h
                    ? $"cat={h.HousingItemCategory} item={h.Item.ValueNullable?.Name.ExtractText() ?? "-"}"
                    : "-")}";

            line += map.TryGetValue(item->Index, out var bytes)
                ? $" | datamap: word0=0x{(ushort)(bytes[0] | (bytes[1] << 8)):X} b2={bytes[2]} b3={bytes[3]}"
                : " | datamap: none";

            Plugin.Log.Information(line);
        }
    }

    /// <summary>
    /// The gardening DataMap: raw hex per entry (kept verbatim - the hex is the receipt),
    /// then the same bytes decoded by MapFormat and named by the Engine's species table.
    /// Both outdoor (8 beds) and indoor (pot, sub-entry 0) decodes are printed, because
    /// the probe is often the thing establishing WHICH one an entry is.
    /// </summary>
    private static void DumpDataMap()
    {
        var entries = MapSensor.ReadRawEntries();
        Plugin.Log.Information($"[Probe] datamap: {entries.Count} entries (via MapSensor)");

        var logged = 0;
        foreach (var (key, bytes) in entries)
        {
            var raw = new StringBuilder();
            foreach (var b in bytes)
                raw.Append($"{b:X2} ");
            Plugin.Log.Information($"[Probe] datamap key={key} bytes: {raw}");

            DecodeAsOutdoor(key, bytes);
            DecodeAsIndoor(key, bytes);

            logged++;
            if (logged >= 80)
            {
                Plugin.Log.Information("[Probe] datamap cap 80 reached");
                break;
            }
        }
        Plugin.Log.Information($"[Probe] datamap entries logged: {logged}");
    }

    private static void DecodeAsOutdoor(int key, byte[] bytes)
    {
        try
        {
            var beds = MapFormat.DecodeOutdoorEntry(bytes);
            var occupied = 0;
            var decoded = new StringBuilder();
            foreach (var bed in beds)
            {
                if (!bed.Occupied)
                    continue;
                occupied++;
                decoded.Append(
                    $"\n[Probe]   slot {bed.Slot}: {Plugin.Tables.SpeciesName(bed.SpeciesIndex)} "
                    + $"(0x{bed.SpeciesIndex:X2}) stage={bed.Stage:X2} extra={bed.Extra:X2}");
            }
            if (occupied > 0)
                Plugin.Log.Information($"[Probe] key={key} outdoor decode ({occupied}/8 occupied):{decoded}");
        }
        catch (Exception ex)
        {
            // Same failure the census would report as "unreadable" - said out loud here.
            Plugin.Log.Information($"[Probe] key={key} outdoor decode failed: {ex.Message}");
        }
    }

    private static void DecodeAsIndoor(int key, byte[] bytes)
    {
        try
        {
            if (MapFormat.DecodeIndoorEntry(bytes, s => Plugin.Tables.SeedIdBySpeciesIndex(s) is not null)
                is not { Occupied: true } pot)
                return;   // multi-slot furniture or an empty pot - not a pot reading

            Plugin.Log.Information(
                $"[Probe] key={key} indoor decode: {Plugin.Tables.SpeciesName(pot.SpeciesIndex)} "
                + $"(0x{pot.SpeciesIndex:X2}) stage={pot.Stage:X2} extra={pot.Extra:X2} "
                + $"recognized={pot.Recognized}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Information($"[Probe] key={key} indoor decode failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Key&lt;-&gt;patch binding (2026-08-12, two-estate verified): GimmickId =
    /// [bed 0-7][patch ordinal][patch-id u16]. Map keys preserve the patch-ids'
    /// PAIRWISE DIFFS per estate (Chelsea +6,+1 -&gt; keys 110/116/117; FC +20,+6
    /// -&gt; 1293/1313/1319) with a per-plot offset. The earlier low-byte-equality
    /// rule was an FC offset coincidence - killed at Chelsea's. Census join:
    /// diff-pattern shortlist, confirm with one tend receipt, cache (keys stable).
    /// <para>Recon keeps the wide 40y sweep on purpose; the working sensor caps at
    /// <see cref="ObjectSensor.PatchSweepRange"/>.</para>
    /// </summary>
    private static void DumpBedGimmicks()
    {
        foreach (var bed in ObjectSensor.NearbyBeds(40f))
        {
            Plugin.Log.Information(
                $"[Probe] bed patch=0x{bed.Gimmick.PatchId:X4} ordinal={bed.Gimmick.PatchOrdinal} "
                + $"slot={bed.Gimmick.BedIndex} dist={bed.Distance:F1}y "
                + $"targetable={bed.Targetable} pos={bed.Object.Position:F1}");
        }
    }

    /// <summary>
    /// The access roster, side by side with where we are standing - the Permission
    /// Architecture's load-bearing capture (spec: Balamb Garden - Permission Architecture,
    /// 2026-08-15). The spec claims a teleport-list row matches the current estate by
    /// DIRECT HouseId equality; this dump is what proves or breaks that, one press per
    /// estate. Three views of the same fact are printed so any disagreement is visible
    /// in a single capture block:
    /// (1) every estate-shaped teleport-list row, raw (HouseId hex beside the game's
    ///     own decode of it),
    /// (2) the current location's HouseId as the sensors read it right here,
    /// (3) HousingManager's owned-estate answers per estate type (the corroborating
    ///     read - a roster row and an owned-house answer that disagree is a finding).
    /// <para>Estate-shaped filter is deliberately wide (ward, plot, sub-index, or a
    /// nonzero HouseId - ANY of them): FreeCompanyEstate is enum value ZERO, so nothing
    /// here may treat a zero as "not an estate". Rows the filter passes over are counted
    /// out loud, never silently dropped.</para>
    /// </summary>
    internal static void DumpAccessRoster()
    {
        try
        {
            var telepo = FFXIVClientStructs.FFXIV.Client.Game.UI.Telepo.Instance();
            if (telepo == null)
            {
                Plugin.Log.Information("[Roster] Telepo: null - no roster read possible");
                return;
            }
            if (!ECommons.GameHelpers.Player.Available)
            {
                Plugin.Log.Information("[Roster] no local player - refresh would be a lie, refusing");
                return;
            }

            // The refresh contract (Dalamud's own IAetheryteList does exactly this before
            // every enumeration): the list is stale until asked, and a null back means
            // the game refused - which is itself the capture.
            if (telepo->UpdateAetheryteList() == null)
            {
                Plugin.Log.Information("[Roster] UpdateAetheryteList returned null - game refused the refresh");
                return;
            }

            var total = 0;
            var estateRows = 0;
            foreach (var row in telepo->TeleportList)
            {
                total++;
                var estateShaped =
                    row.Ward > 0 || row.Plot > 0 || row.SubIndex > 0 || row.HouseId.Id != 0;
                if (!estateShaped)
                    continue;
                estateRows++;

                var houseId = row.HouseId;
                Plugin.Log.Information(
                    $"[Roster] row aetheryte={row.AetheryteId} territory={row.TerritoryId} "
                    + $"estateType={row.EstateType}({(int)row.EstateType}) "
                    + $"ward={row.Ward} plot={row.Plot} subIndex={row.SubIndex} "
                    + $"sharedHouse={row.IsSharedHouse} apartment={row.IsApartment} "
                    + $"gil={row.GilCost}");
                Plugin.Log.Information(
                    $"[Roster]   houseId=0x{houseId.Id:X16} territory={houseId.TerritoryTypeId} "
                    + $"world={houseId.WorldId} ward={houseId.WardIndex} plot={houseId.PlotIndex} "
                    + $"room={houseId.RoomNumber} isApartment={houseId.IsApartment} "
                    + $"apartmentDivision={houseId.ApartmentDivision} isWorkshop={houseId.IsWorkshop}");
            }
            Plugin.Log.Information(
                $"[Roster] teleport list: {total} rows, {estateRows} estate-shaped (rest are plain aetherytes)");

            // (2) Where we are standing, by the same identity the roster rows carry.
            // GetCurrentHouseId is the general read; the indoor one is printed too when it
            // applies, because which of them equals the roster row IS the masking question.
            var housing = HousingManager.Instance();
            if (housing == null)
            {
                Plugin.Log.Information("[Roster] here: HousingManager null (not in a housing area)");
            }
            else
            {
                var current = housing->GetCurrentHouseId();
                Plugin.Log.Information(
                    $"[Roster] here: currentHouseId=0x{current.Id:X16} "
                    + $"ward={housing->GetCurrentWard()} plot={housing->GetCurrentPlot()} "
                    + $"room={housing->GetCurrentRoom()} inside={housing->IsInside()} "
                    + $"hasHousePermissions={housing->HasHousePermissions()}");
                if (housing->IsInside())
                {
                    var indoor = housing->GetCurrentIndoorHouseId();
                    Plugin.Log.Information($"[Roster] here: indoorHouseId=0x{indoor.Id:X16}");
                }
            }

            // (3) The corroborating read: what the client says we own/hold, per estate
            // type. SharedEstate takes a slot index (two slots exist); everything else
            // ignores it. An all-F answer is the game's own "none".
            if (housing != null)
            {
                LogOwned(EstateType.FreeCompanyEstate, 0);
                LogOwned(EstateType.PersonalChambers, 0);
                LogOwned(EstateType.PersonalEstate, 0);
                LogOwned(EstateType.SharedEstate, 0);
                LogOwned(EstateType.SharedEstate, 1);
                LogOwned(EstateType.ApartmentRoom, 0);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Roster] roster dump failed: {ex}");
        }

        static void LogOwned(EstateType type, int sharedIndex)
        {
            var id = HousingManager.GetOwnedHouseId(type, sharedIndex);
            var label = type == EstateType.SharedEstate ? $"{type}[{sharedIndex}]" : $"{type}";
            Plugin.Log.Information($"[Roster] owned {label}: 0x{id.Id:X16}");
        }
    }

    /// <summary>Hex-dumps the first bytes of each nearby bed's native GameObject.</summary>
    internal static void DumpBedStructs()
    {
        const int windowBytes = 0x220;
        const int maxObjects = 32;

        var dumped = 0;
        // Outdoor beds by the sensor's own DataId filter; indoors, any close housing
        // object (pots have per-model DataIds, so proximity is the honest filter and the
        // probe deliberately keeps a wider net than the app's name-filtered pot sensor).
        foreach (var (obj, distance) in ObjectSensor.ReconObjects(bedRange: 40f, housingRange: 10f))
        {
            if (dumped >= maxObjects)
            {
                Plugin.Log.Information($"[Probe] dump cap ({maxObjects}) reached - move closer to the beds you care about");
                break;
            }

            try
            {
                var address = obj.Address;
                Plugin.Log.Information(
                    $"[Probe] object entity={obj.EntityId:X8} index={obj.ObjectIndex} "
                    + $"name='{obj.Name.TextValue}' pos={obj.Position:F1} dist={distance:F2}y addr={address:X}");

                var bytes = new byte[windowBytes];
                System.Runtime.InteropServices.Marshal.Copy(address, bytes, 0, windowBytes);
                for (var offset = 0; offset < windowBytes; offset += 16)
                {
                    var line = new StringBuilder($"[Probe]   +{offset:X3}: ");
                    for (var i = 0; i < 16 && offset + i < windowBytes; i++)
                        line.Append($"{bytes[offset + i]:X2} ");
                    Plugin.Log.Information(line.ToString());
                }

                dumped++;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[Probe] object dump failed at {distance:F1}y: {ex.Message}");
            }
        }

        Plugin.Log.Information($"[Probe] dumped {dumped} objects ({windowBytes:X} bytes each)");
    }
}
#endif
