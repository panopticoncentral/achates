using Achates.Server.Tools;

namespace Achates.Tests;

/// <summary>
/// Messages presents one conversation per participant set, but the database keeps a
/// separate <c>chat</c> row per service — a thread that moved between RCS, SMS and
/// iMessage is several rows. Reading one row gives a slice of the conversation with
/// nothing to say the rest exists.
/// </summary>
public class ChatRosterTests
{
    [Fact]
    public void Keeps_a_named_conversation_apart_from_an_unnamed_one_with_the_same_people()
    {
        // Messages shows a named group and an unnamed group with the same members
        // as two conversations. Merging them splices two histories together.
        var siblings = ChatRoster.GroupByRoster(
        [
            (107, "Paul & the Boys", "+12062001264"), (107, "Paul & the Boys", "+12066963185"),
            (579, null, "+12062001264"), (579, null, "+12066963185"),
            (585, "", "+12062001264"), (585, "", "+12066963185"),
        ]);

        Assert.Equal([107], siblings[107]);
        Assert.Equal([579, 585], siblings[579]);
    }

    [Fact]
    public void Groups_rows_that_share_both_participants_and_name()
    {
        var siblings = ChatRoster.GroupByRoster(
        [
            (10, "Book Club", "+15550001111"),
            (20, "Book Club", "+15550001111"),
        ]);

        Assert.Equal([10, 20], siblings[10]);
    }

    [Fact]
    public void Matches_conversation_names_regardless_of_case()
    {
        var siblings = ChatRoster.GroupByRoster(
        [
            (10, "Book Club", "+15550001111"),
            (20, "book club", "+15550001111"),
        ]);

        Assert.Equal([10, 20], siblings[10]);
    }

    [Fact]
    public void Groups_chats_that_address_an_identical_handle_set()
    {
        var siblings = ChatRoster.GroupByRoster(
        [
            (148, null, "+12062001264"), (148, null, "+12066963185"),
            (582, null, "+12062001264"), (582, null, "+12066963185"),
        ]);

        Assert.Equal([148, 582], siblings[148]);
        Assert.Equal([148, 582], siblings[582]);
    }

    [Fact]
    public void Keeps_chats_with_different_participants_apart()
    {
        // These two differ by a single digit — a real pair from a live database.
        var siblings = ChatRoster.GroupByRoster(
        [
            (148, null, "+12062001264"), (148, null, "+12066963185"),
            (117, null, "+12062001264"), (117, null, "+12066963785"),
        ]);

        Assert.Equal([148], siblings[148]);
        Assert.Equal([117], siblings[117]);
    }

    [Fact]
    public void Does_not_group_a_chat_with_a_superset_of_its_participants()
    {
        var siblings = ChatRoster.GroupByRoster(
        [
            (10, null, "+15550001111"),
            (20, null, "+15550001111"), (20, null, "+15550002222"),
        ]);

        Assert.Equal([10], siblings[10]);
        Assert.Equal([20], siblings[20]);
    }

    [Fact]
    public void Treats_a_handle_repeated_across_services_as_one_participant()
    {
        // The same address gets its own `handle` row per service, so a roster can
        // list it twice; that must not make the set look larger than it is.
        var siblings = ChatRoster.GroupByRoster(
        [
            (10, null, "+15550001111"), (10, null, "+15550001111"),
            (20, null, "+15550001111"),
        ]);

        Assert.Equal([10, 20], siblings[10]);
    }

    [Fact]
    public void Matches_email_handles_regardless_of_case()
    {
        var siblings = ChatRoster.GroupByRoster(
        [
            (10, null, "Sam@Example.com"),
            (20, null, "sam@example.com"),
        ]);

        Assert.Equal([10, 20], siblings[10]);
    }

    [Fact]
    public void Orders_each_group_so_the_result_is_stable()
    {
        var siblings = ChatRoster.GroupByRoster(
        [
            (582, null, "+15550001111"),
            (148, null, "+15550001111"),
            (560, null, "+15550001111"),
        ]);

        Assert.Equal([148, 560, 582], siblings[582]);
    }
}
