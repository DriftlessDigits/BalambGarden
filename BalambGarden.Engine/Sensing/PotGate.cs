namespace BalambGarden.Engine.Sensing;

/// <summary>
/// The pot-gate: which DataMap keys are actually flowerpots.
///
/// <para>RECEIPT (2026-08-16, nine captures, every estate): decorative furnishings carry
/// pot-shaped DataMap data - Mounted Flower Vases, Oasis/Paramour Vases, aquariums,
/// Angler's Canvases all decoded as plants at the pot house (seven phantom rows against
/// four real pots). HousingItemCategory 14 was REFUTED as the discriminator: the vases and
/// aquariums are cat-14 too. What discriminates is the furniture id itself - the
/// HousingFurniture sheet holds exactly three Flowerpot rows (Riviera 65979, Glade 65980,
/// Oasis 65981), every real pot at every estate is one of them, and no phantom is.</para>
///
/// <para>An id gate also keeps an EMPTY pot (DataMap all zeros, seen at the chambers
/// 08-16) trackable, where any shape-of-the-data gate would drop it.</para>
/// </summary>
public static class PotGate
{
    /// <summary>The DataMap keys backed by a whitelisted flowerpot - furniture index IS
    /// map key for pots (receipted 08-15/08-16). Everything else at this estate is
    /// furniture that happens to hold plant-shaped bytes.</summary>
    public static List<int> Keys(
        IReadOnlyList<FurniturePlacement> furniture, IReadOnlyCollection<uint> flowerpotIds)
        => furniture.Where(f => flowerpotIds.Contains(f.Id)).Select(f => f.Index).ToList();
}
