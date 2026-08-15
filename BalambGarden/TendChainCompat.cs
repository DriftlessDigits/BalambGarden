using System.Collections.Generic;
using System.Numerics;
using BalambGarden.Game;

namespace BalambGarden;

/// <summary>
/// Bridge shape kept alive for the POC <see cref="TendChain"/>, whose signatures still
/// speak the old scanner's patch struct. GardenScanner is gone (its scanning is now
/// ObjectSensor); this carries only what TendChain reads - the bed list - so the chain
/// keeps compiling until Task 7 rewrites it against PatchGroup directly.
/// </summary>
internal readonly record struct PatchSighting(
    Vector3 Position,
    List<BedObject> Beds,
    float Distance);
