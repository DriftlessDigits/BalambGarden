using BalambGarden.Engine.Sync;
using Xunit;

namespace BalambGarden.Engine.Tests;

/// <summary>The soil note (v0.4.1, Sow Capture design seam 3): the sow question names the soil,
/// the sow line that follows takes it. The one captured pair (2026-08-23 10:59, a pot) put the
/// question at :45.897 and the line at :48.286 - 2.39s, the player's Yes included - so the note
/// is keyed to the question CLOSING, not to it appearing: a player can sit on the question as long
/// as they like.</summary>
public class SoilNoteTests
{
    private static readonly DateTimeOffset Asked = new(2026, 8, 23, 15, 59, 45, 897, TimeSpan.Zero);
    private const uint Grade3Thanalan = 7766, PottingSoil = 5810;

    [Fact] // the captured pot sow: asked, Yes, line 2.39s after the question appeared
    public void The_line_after_a_yes_takes_the_soil()
    {
        var note = new SoilNote();
        note.Offer(PottingSoil, Asked);
        note.Closed(Asked + TimeSpan.FromMilliseconds(400));
        Assert.Equal(PottingSoil, note.Take(Asked + TimeSpan.FromMilliseconds(2389)));
    }

    [Fact] // the player sits on the question; the note waits with them
    public void A_question_still_open_holds_its_soil()
    {
        var note = new SoilNote();
        note.Offer(Grade3Thanalan, Asked);
        Assert.Equal(Grade3Thanalan, note.Take(Asked + TimeSpan.FromSeconds(90)));
    }

    [Fact] // a No prints no line; the note runs out and the next sow does not inherit it
    public void A_no_expires_the_note()
    {
        var note = new SoilNote();
        note.Offer(Grade3Thanalan, Asked);
        var closed = Asked + TimeSpan.FromSeconds(1);
        note.Closed(closed);
        Assert.Null(note.Take(closed + SoilNote.AfterClose + TimeSpan.FromMilliseconds(1)));
    }

    [Fact] // the window's edge is inside
    public void A_line_at_the_window_edge_still_takes()
    {
        var note = new SoilNote();
        note.Offer(Grade3Thanalan, Asked);
        note.Closed(Asked);
        Assert.Equal(Grade3Thanalan, note.Take(Asked + SoilNote.AfterClose));
    }

    [Fact] // a second question replaces the first, soil and clock both
    public void A_new_question_replaces_the_old_note()
    {
        var note = new SoilNote();
        note.Offer(Grade3Thanalan, Asked);
        note.Closed(Asked + TimeSpan.FromSeconds(1));
        note.Offer(PottingSoil, Asked + TimeSpan.FromSeconds(3));
        Assert.Equal(PottingSoil, note.Take(Asked + TimeSpan.FromSeconds(4)));
    }

    [Fact] // a question whose soil could not be named still wipes the old soil - a stale one is worse than none
    public void An_unnamed_soil_clears_the_old_note()
    {
        var note = new SoilNote();
        note.Offer(Grade3Thanalan, Asked);
        note.Offer(null, Asked + TimeSpan.FromSeconds(3));
        Assert.Null(note.Take(Asked + TimeSpan.FromSeconds(4)));
    }

    [Fact] // two quick sows: each line takes its own question's soil, and a take clears the note
    public void Two_quick_sows_never_trade_soils()
    {
        var note = new SoilNote();
        note.Offer(Grade3Thanalan, Asked);
        note.Closed(Asked + TimeSpan.FromMilliseconds(500));
        Assert.Equal(Grade3Thanalan, note.Take(Asked + TimeSpan.FromSeconds(2)));
        Assert.Null(note.Take(Asked + TimeSpan.FromSeconds(2.5)));

        note.Offer(PottingSoil, Asked + TimeSpan.FromSeconds(3));
        note.Closed(Asked + TimeSpan.FromSeconds(3.5));
        Assert.Equal(PottingSoil, note.Take(Asked + TimeSpan.FromSeconds(5)));
    }

    [Fact] // nothing asked, nothing taken; a line older than its question is not its line
    public void No_question_or_a_line_before_it_takes_nothing()
    {
        Assert.Null(new SoilNote().Take(Asked));

        var note = new SoilNote();
        note.Offer(Grade3Thanalan, Asked);
        Assert.Null(note.Take(Asked - TimeSpan.FromSeconds(1)));
    }

    [Fact] // a close the listener never heard cannot keep a soil alive forever
    public void An_open_question_runs_out_at_the_open_limit()
    {
        var note = new SoilNote();
        note.Offer(Grade3Thanalan, Asked);
        Assert.Null(note.Take(Asked + SoilNote.OpenLimit + TimeSpan.FromMilliseconds(1)));
    }
}
