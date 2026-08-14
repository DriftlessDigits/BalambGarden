namespace BalambGarden.Engine.Domain;

public sealed record Crop(
    string Name,
    int GrowHours,
    int WiltHours,
    int WitherHours,
    uint ItemId,
    uint SeedId,
    string SeedName,
    bool Crossable);
