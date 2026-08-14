using BalambGarden.Engine.Ledger;

namespace BalambGarden.Engine.Derivations;

/// <summary>Every timer is a window, never a point. Provenance says what kind of
/// claim the window is making (spec: timing model).</summary>
public sealed record EtaWindow(DateTimeOffset Earliest, DateTimeOffset Latest, Provenance Provenance);
