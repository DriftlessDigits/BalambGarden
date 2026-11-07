namespace BalambGarden.Engine.Sync;

/// <summary>The soil a sow with no chain is about to use (v0.4.1, Sow Capture design ruling 4).
/// The game asks before every sow - "Prepare the bed with a bag of grade 3 thanalan topsoil and a
/// bag of krakka root seeds?" - and the sow line that follows names its bed but never its soil, so
/// the question is the only witness. A passive listener OFFERS the question's soil here; the sow
/// line TAKES it. Shaped like the harvest's seed window (<see cref="HarvestGather"/>): one note,
/// a short window, and a missed take is "not heard", never a guess.
///
/// <para>KEYED TO THE QUESTION CLOSING, NOT APPEARING. The only captured pair (2026-08-23, a pot)
/// put the line 2.39s after the question appeared, the player's own Yes included - so how long the
/// question sits open is the player's time, not the game's. The note holds while the question is
/// open and for <see cref="AfterClose"/> once it closes. A No closes it and no line ever comes, so
/// the note simply runs out.</para>
///
/// <para>NEVER TRADED. A new question replaces the note (soil and clock), a question whose soil
/// could not be named clears it, and a take clears it - so two quick sows each carry their own
/// question's soil, and one question can never feed two lines.</para></summary>
public sealed class SoilNote
{
    /// <summary>How long after the question closes its line may still arrive. The capture bounds
    /// Yes-to-line under 2.39s (the gap from the question APPEARING, Yes included); 6s is the
    /// receipt lifecycle's own act-to-line window (<see cref="ReceiptLifecycle.CloseWindow"/>).</summary>
    public static readonly TimeSpan AfterClose = ReceiptLifecycle.CloseWindow;

    /// <summary>A question whose close was never heard does not hold a soil forever.</summary>
    public static readonly TimeSpan OpenLimit = TimeSpan.FromMinutes(10);

    private uint? soil;
    private DateTimeOffset askedAt;
    private DateTimeOffset? closedAt;

    /// <summary>The sow question appeared. Null = it named a soil we could not resolve, which
    /// still wipes the old note.</summary>
    public void Offer(uint? namedSoil, DateTimeOffset at)
    {
        soil = namedSoil;
        askedAt = at;
        closedAt = null;
    }

    /// <summary>The sow question went away (Yes or No - the listener cannot tell, and need not).</summary>
    public void Closed(DateTimeOffset at)
    {
        if (soil is not null && closedAt is null)
            closedAt = at;
    }

    /// <summary>A sow line at <paramref name="at"/> takes the noted soil, or null when nothing
    /// covers it. Any take clears the note.</summary>
    public uint? Take(DateTimeOffset at)
    {
        var taken = soil;
        var fresh = taken is not null && at >= askedAt
            && (closedAt is { } closed ? at - closed <= AfterClose : at - askedAt <= OpenLimit);
        if (at >= askedAt)
            soil = null;
        return fresh ? taken : null;
    }
}
