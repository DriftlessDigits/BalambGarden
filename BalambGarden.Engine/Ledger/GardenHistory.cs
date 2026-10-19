using System.Text.Json;
using System.Text.Json.Serialization;
using BalambGarden.Engine.Census;
using BalambGarden.Engine.Sync;

namespace BalambGarden.Engine.Ledger;

/// <summary>Plants and harvests only (ruled 2026-10-14: "tends/fertilization can be ignored").</summary>
public enum HistoryKind { Plant, Harvest }

/// <summary>One line of the garden's permanent history: who planted what where, who harvested
/// what and how much. <see cref="By"/> is a wire slug or <see cref="Hand.OwnSlug"/> - never a
/// name (the local alias map in plugin config is the only place a label lives, and it never
/// serializes). <see cref="At"/> is the act's own instant, not arrival. <see cref="Id"/> is the
/// wire id ("user:era:counter") for both hands - own rows use the install's real slug so a relay
/// echo of the same receipt can never land twice. <see cref="Yields"/> is the reserved wire
/// field, finally filled (Q3, ruled 2026-10-14). <see cref="SeedQty"/>/<see cref="SeedItem"/> are a
/// harvest's cross seed in three states (ruled 2026-10-18, <see cref="HarvestGather"/>): crossed
/// (qty, item), missed (0, null), not heard (null, null).</summary>
public sealed record HistoryEntry(
    string Id,
    DateTimeOffset At,
    HistoryKind Kind,
    ushort Species,
    EstateKey Estate,
    bool IsPot,
    int PatchOrdinal,
    int BedSlot,
    string By,
    SyncSource Source,
    IReadOnlyList<SyncYield>? Yields,
    int? SeedQty = null,
    uint? SeedItem = null)
{
    [JsonIgnore] public bool IsOwn => By == Hand.OwnSlug;

    [JsonIgnore] public int YieldQty => Yields?.Sum(y => y.Qty) ?? 0;

    public static HistoryKind? KindOf(SyncKind kind) => kind switch
    {
        SyncKind.Plant => HistoryKind.Plant,
        SyncKind.Harvest => HistoryKind.Harvest,
        _ => null,
    };

    /// <summary>An own act, from the receipt log's entry for it. Null for any kind the history
    /// does not keep. Pot entries carry the pot key as their patch ordinal (slot 0), the same
    /// identity convention the ledger row uses.</summary>
    public static HistoryEntry? FromOwn(LoggedReceipt r, string ownUser)
    {
        if (KindOf(r.Kind) is not { } kind)
            return null;
        return new HistoryEntry(
            Id: SyncId.Format(ownUser, r.Era, r.Seq),
            At: r.At,
            Kind: kind,
            Species: r.Data.Species ?? 0,
            Estate: r.Estate,
            IsPot: r.IsPot,
            PatchOrdinal: r.IsPot ? r.PotKey : r.PatchOrdinal,
            BedSlot: r.IsPot ? 0 : r.BedSlot,
            By: Hand.OwnSlug,
            Source: r.Source,
            Yields: r.Data.Yields,
            SeedQty: r.Data.SeedQty,
            SeedItem: r.Data.SeedItem);
    }

    /// <summary>A household act, from the wire receipt and the row it landed on. Null for any
    /// kind the history does not keep.</summary>
    public static HistoryEntry? FromForeign(SyncReceipt r, ClaimedBed bed, Hand hand)
    {
        if (KindOf(r.Kind) is not { } kind)
            return null;
        return new HistoryEntry(
            Id: r.Id,
            At: r.At,
            Kind: kind,
            Species: r.Data.Species ?? 0,
            Estate: bed.Estate,
            IsPot: bed.IsPot,
            PatchOrdinal: bed.PatchOrdinal,
            BedSlot: bed.BedSlot,
            By: hand.Slug,
            Source: r.Source,
            Yields: r.Data.Yields,
            SeedQty: r.Data.SeedQty,
            SeedItem: r.Data.SeedItem);
    }
}

/// <summary>The garden history: append-only, never the outbox, never pruned, born empty
/// (Q4, ruled 2026-10-14: no back-fill from the receipt log). Its own file beside the ledger -
/// <c>history.json</c> - with its own lifetime: a parked ledger must not take the history with
/// it. Dedup by id, so a relay replay and a retried page both land once. <see cref="Dirty"/>
/// is what the save contract reads: the inbox cursor advances only after a dirty history is
/// on disk (<see cref="ForeignApply.AbsorbPage"/>).</summary>
public sealed class GardenHistory
{
    public int Version { get; set; } = 1;
    public List<HistoryEntry> Entries { get; init; } = [];

    private HashSet<string>? ids;
    private HashSet<string> Ids => ids ??= Entries.Select(e => e.Id).ToHashSet();

    /// <summary>Unsaved rows exist. Set by <see cref="Record"/>, cleared by <see cref="MarkSaved"/>
    /// (the plugin's file write calls it on success).</summary>
    [JsonIgnore] public bool Dirty { get; private set; }

    /// <summary>Appends unless this id is already here. True = a new row.</summary>
    public bool Record(HistoryEntry? entry)
    {
        if (entry is null || !Ids.Add(entry.Id))
            return false;
        Entries.Add(entry);
        Dirty = true;
        return true;
    }

    public void MarkSaved() => Dirty = false;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static GardenHistory FromJson(string json)
        => JsonSerializer.Deserialize<GardenHistory>(json, Options)
           ?? throw new InvalidOperationException("History JSON deserialized to null");
}

/// <summary>Which rows: every field optional, all given fields must match.</summary>
public sealed record HistoryFilter(
    EstateKey? Estate = null,
    ushort? Species = null,
    string? By = null,
    HistoryKind? Kind = null,
    bool? IsPot = null);

/// <summary>The read side (phase 1: the query surface exists; the screens that use it are
/// phase 2). Newest first everywhere.</summary>
public static class HistoryQuery
{
    public static IReadOnlyList<HistoryEntry> Find(GardenHistory history, HistoryFilter filter)
        => history.Entries
            .Where(e => (filter.Estate is null || e.Estate == filter.Estate)
                        && (filter.Species is null || e.Species == filter.Species)
                        && (filter.By is null || e.By == filter.By)
                        && (filter.Kind is null || e.Kind == filter.Kind)
                        && (filter.IsPot is null || e.IsPot == filter.IsPot))
            .OrderByDescending(e => e.At)
            .ToList();

    /// <summary>Every harvest of one species, newest first - the onion question.</summary>
    public static IReadOnlyList<HistoryEntry> Harvests(GardenHistory history, ushort species)
        => Find(history, new HistoryFilter(Species: species, Kind: HistoryKind.Harvest));

    /// <summary>Total quantity harvested across these rows (rows with no yield count 0).</summary>
    public static int YieldTotal(IEnumerable<HistoryEntry> rows) => rows.Sum(r => r.YieldQty);

    /// <summary>Harvest count per hand across these rows, most prolific first.</summary>
    public static IReadOnlyList<(string By, int Count)> ByHand(IEnumerable<HistoryEntry> rows)
        => rows.GroupBy(r => r.By)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(x => x.Item2)
            .ToList();

    /// <summary>The newest row for one bed or pot, any kind, or null.</summary>
    public static HistoryEntry? LastAt(GardenHistory history, ClaimedBed bed)
        => history.Entries
            .Where(e => e.Estate == bed.Estate && e.IsPot == bed.IsPot
                        && e.PatchOrdinal == bed.PatchOrdinal && e.BedSlot == bed.BedSlot)
            .MaxBy(e => e.At);
}
