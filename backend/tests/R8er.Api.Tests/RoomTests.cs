using R8er.Api;

namespace R8er.Api.Tests;

// Direct, deterministic tests of the signaling rendezvous invariant: two peers
// per room, and a peer only ever sees its own room-mate. The live WebSocket
// wire path is covered end-to-end by the Railway signaling E2E (see
// docs/protocol.md); WebSocket-over-TestServer integration tests were dropped
// as flaky teardown hangs on the .NET 10 in-memory transport.
public class RoomTests
{
    [Fact]
    public void Caps_at_two_peers()
    {
        var room = new SignalingHub.Room<object>();
        Assert.True(room.TryJoin(new object()));
        Assert.True(room.TryJoin(new object()));
        Assert.False(room.TryJoin(new object())); // third rejected
    }

    [Fact]
    public void Other_returns_the_room_mate()
    {
        var room = new SignalingHub.Room<object>();
        var a = new object();
        var b = new object();
        room.TryJoin(a);
        room.TryJoin(b);

        Assert.Same(b, room.Other(a));
        Assert.Same(a, room.Other(b));
    }

    [Fact]
    public void A_lone_peer_has_no_room_mate()
    {
        var room = new SignalingHub.Room<object>();
        var a = new object();
        room.TryJoin(a);

        Assert.Null(room.Other(a)); // nothing to relay to
    }

    [Fact]
    public void Leaving_frees_a_slot_and_empties_the_room()
    {
        var room = new SignalingHub.Room<object>();
        var a = new object();
        var b = new object();
        room.TryJoin(a);
        room.TryJoin(b);

        room.Leave(a);
        var c = new object();
        Assert.True(room.TryJoin(c)); // freed slot reused
        Assert.False(room.IsEmpty);

        room.Leave(b);
        room.Leave(c);
        Assert.True(room.IsEmpty);
    }
}
