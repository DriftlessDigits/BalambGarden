using System.Text.Json;
using System.Text.Json.Serialization;
using BalambGarden.Engine.Census;

namespace BalambGarden.Engine.Ledger;

/// <summary>Append-only receipt log for ground-truthing. The engine never reads
/// this for state (spec: approach C - the trail is evidence, not memory).</summary>
public sealed class DebugTrail(string path)
{
    private static readonly JsonSerializerOptions Options = new()
        { Converters = { new JsonStringEnumConverter() } };

    public void Append(ReceiptEvent e)
        => File.AppendAllText(path, JsonSerializer.Serialize(e, Options) + Environment.NewLine);

    /// <summary>A tenancy reset, as one line: the row's identity and the reset's facts - never
    /// the whole row (ruled 2026-10-14: every rebase leaves a trail entry).</summary>
    public void Append(RebaseEvent e)
        => File.AppendAllText(path, JsonSerializer.Serialize(new RebaseLine(
            "Rebase", e.Bed.Estate, e.Bed.MapKey, e.Bed.PatchOrdinal, e.Bed.BedSlot, e.Bed.IsPot,
            e.Reason, e.Cut, e.By?.Slug, e.RemovedAnything), Options) + Environment.NewLine);

    private sealed record RebaseLine(
        string Kind, EstateKey Estate, int MapKey, int PatchOrdinal, int BedSlot, bool IsPot,
        RebaseReason Reason, DateTimeOffset Cut, string? By, bool RemovedAnything);

    public static IReadOnlyList<string> ReadLines(string path)
        => File.Exists(path) ? File.ReadAllLines(path) : [];
}
