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

    public static IReadOnlyList<string> ReadLines(string path)
        => File.Exists(path) ? File.ReadAllLines(path) : [];
}
