namespace BalambGarden.Engine.Sensing;

/// <summary>One decoded indoor pot. Recognized=false -> species newer than our index:
/// track it, display "Unknown (0xNN)", never guess. Color is the pigment nibble
/// (b2 high, 08-16 receipt): 0 = unpigmented crop, named values in
/// <see cref="PotPigment"/>. Wilt is byte[4] raw: 1 = wilting (08-16, Papa's Krakka
/// twins - byte-identical except +4, exactly one drooping); 2 = seen on every ripe
/// flower, meaning unreceipted; 0 = neither. Only the value 1 is ever interpreted.</summary>
public sealed record PotReading(
    ushort SpeciesIndex, byte Stage, byte Color, byte Extra, bool Occupied, bool Recognized,
    byte Wilt = 0);

/// <summary>Pigment nibble names, receipts only (08-16, Drift's eyes at three estates):
/// 1 = Blue (lupins AND morning glories agreed), 2 = Yellow (daisies + item 17999
/// "Yellow Daisies"), 5 = Purple (FC cosmos - screenshot AND the game's own Talk line
/// "Purple Cosmos"). An unreceipted nibble answers null and the plant renders as the
/// bare species - a color is never guessed.</summary>
public static class PotPigment
{
    public static string? Name(byte pigment) => pigment switch
    {
        1 => "Blue",
        2 => "Yellow",
        5 => "Purple",
        _ => null,
    };
}
