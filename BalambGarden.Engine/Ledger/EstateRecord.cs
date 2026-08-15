using System.Text.Json.Serialization;
using BalambGarden.Engine.Census;

namespace BalambGarden.Engine.Ledger;

/// <summary>Frame 2: an estate discovered on visit and remembered, with its learned
/// identity. Capacity shape is derived live from bindings + claimed beds, not stored.</summary>
public sealed class EstateRecord
{
    public required EstateKey Key { get; init; }
    public string Nickname { get; set; } = "";
    public DateTimeOffset FirstSeen { get; init; }
    public DateTimeOffset LastVisited { get; set; }

    [JsonIgnore]
    public string DisplayName => Nickname.Length > 0 ? Nickname : Key.DisplayLabel();
}
