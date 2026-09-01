using System.Text.Json;
using Kiberone.Core;
using Kiberone.Infrastructure;

namespace Kiberone.Tests;

public sealed class NetworkCoordinationTests
{
    [Fact]
    public void Heartbeat_ChangesOnlineStateAfterFifteenSeconds()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-27T12:00:00Z"));
        var registry = new ClientRegistry(clock);

        registry.Heartbeat(CreateHeartbeat("pc-a"));
        Assert.True(registry.GetAll().Single().IsOnline);

        clock.Advance(TimeSpan.FromSeconds(16));
        Assert.False(registry.GetAll().Single().IsOnline);
    }

    [Fact]
    public void Command_IsRepeatedUntilExplicitAcknowledgement()
    {
        var registry = new ClientRegistry();
        registry.Heartbeat(CreateHeartbeat("pc-a"));
        var queue = new ReliableCommandQueue(registry);
        var command = queue.Enqueue(new EnqueueCommandRequest(
            ["pc-a"], ClassroomCommandKinds.Message, JsonSerializer.SerializeToElement(new { text = "Привет" })));

        Assert.Equal(command.Id, queue.GetPending("pc-a").Single().Id);
        Assert.Equal(command.Id, queue.GetPending("pc-a").Single().Id);

        var receipt = queue.Acknowledge("pc-a", new CommandAcknowledgement(command.Id, true));
        Assert.True(receipt.Succeeded);
        Assert.Empty(queue.GetPending("pc-a"));
    }

    [Fact]
    public void Broadcast_WaitsForEveryKnownClient()
    {
        var registry = new ClientRegistry();
        registry.Heartbeat(CreateHeartbeat("pc-a"));
        registry.Heartbeat(CreateHeartbeat("pc-b"));
        var queue = new ReliableCommandQueue(registry);
        var command = queue.Enqueue(new EnqueueCommandRequest(
            ["__all__"], ClassroomCommandKinds.SyncNow, JsonSerializer.SerializeToElement(new { })));

        queue.Acknowledge("pc-a", new CommandAcknowledgement(command.Id, true));
        Assert.Empty(queue.GetPending("pc-a"));
        Assert.Single(queue.GetPending("pc-b"));

        queue.Acknowledge("pc-b", new CommandAcknowledgement(command.Id, false, "Папка недоступна"));
        Assert.Empty(queue.GetPending("pc-b"));
        Assert.Equal(2, queue.GetReceipts().Count);
    }

    [Fact]
    public void Enqueue_NotifiesListenersWithResolvedTargets()
    {
        var registry = new ClientRegistry();
        registry.Heartbeat(CreateHeartbeat("pc-a"));
        registry.Heartbeat(CreateHeartbeat("pc-b"));
        var queue = new ReliableCommandQueue(registry);
        List<string>? targets = null;
        ClassroomCommand? pushed = null;
        queue.CommandQueued += (command, clientIds) =>
        {
            pushed = command;
            targets = clientIds.ToList();
        };

        var command = queue.Enqueue(new EnqueueCommandRequest(
            ["__all__"], ClassroomCommandKinds.LockScreen, JsonSerializer.SerializeToElement(new { })));

        Assert.Equal(command.Id, pushed?.Id);
        Assert.Equal(["pc-a", "pc-b"], targets!.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExpiredCommand_IsNotDelivered()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var registry = new ClientRegistry(clock);
        registry.Heartbeat(CreateHeartbeat("pc-a"));
        var queue = new ReliableCommandQueue(registry, clock);
        queue.Enqueue(new EnqueueCommandRequest(
            ["pc-a"], ClassroomCommandKinds.Message, JsonSerializer.SerializeToElement(new { text = "Короткая" }), 10));

        clock.Advance(TimeSpan.FromSeconds(11));

        Assert.Empty(queue.GetPending("pc-a"));
    }

    [Fact]
    public void UnsafeCommandKind_IsRejected()
    {
        var registry = new ClientRegistry();
        registry.Heartbeat(CreateHeartbeat("pc-a"));
        var queue = new ReliableCommandQueue(registry);

        Assert.Throws<LessonValidationException>(() => queue.Enqueue(new EnqueueCommandRequest(
            ["pc-a"], "run_shell", JsonSerializer.SerializeToElement(new { command = "anything" }))));
    }

    [Fact]
    public void DiscoveryPacket_RoundTripsAndRejectsGarbage()
    {
        var expected = new DiscoveryBeacon(
            DiscoveryProtocol.BeaconType, "a-very-long-random-token", "192.168.1.10", 8765, "server-1", "0.1.0");

        var actual = DiscoveryProtocol.Parse(DiscoveryProtocol.Serialize(expected));

        Assert.Equal(expected, actual);
        Assert.Null(DiscoveryProtocol.Parse("not-json"u8));
    }

    [Fact]
    public void Heartbeat_RejectsInvalidBatteryAndEmptyClient()
    {
        var registry = new ClientRegistry();
        Assert.Throws<LessonValidationException>(() => registry.Heartbeat(new HeartbeatRequest(
            "", "3", "PC-3", "C:\\Work", "0.1.0", null, null,
            new ClientRuntimeInfo(false, false, "", 90))));
        Assert.Throws<LessonValidationException>(() => registry.Heartbeat(CreateHeartbeat("pc-a") with
        {
            Extra = new ClientRuntimeInfo(false, false, "", 140)
        }));
    }

    [Fact]
    public void Command_UnknownClientAndAckMismatch_AreRejected()
    {
        var registry = new ClientRegistry();
        registry.Heartbeat(CreateHeartbeat("pc-a"));
        var queue = new ReliableCommandQueue(registry);

        Assert.Throws<LessonValidationException>(() => queue.Enqueue(new EnqueueCommandRequest(
            ["pc-missing"], ClassroomCommandKinds.Message, JsonSerializer.SerializeToElement(new { text = "x" }))));
        Assert.Throws<KeyNotFoundException>(() => queue.GetPending("pc-missing"));

        var command = queue.Enqueue(new EnqueueCommandRequest(
            ["pc-a"], ClassroomCommandKinds.LockScreen, JsonSerializer.SerializeToElement(new { })));
        Assert.Throws<KeyNotFoundException>(() =>
            queue.Acknowledge("pc-a", new CommandAcknowledgement(Guid.NewGuid(), true)));
        Assert.Equal(command.Id, queue.GetPending("pc-a").Single().Id);
    }

    [Fact]
    public void Heartbeat_AcceptsBatteryBoundsAndPreservesFirstSeen()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-27T12:00:00Z"));
        var registry = new ClientRegistry(clock);
        var first = registry.Heartbeat(CreateHeartbeat("pc-a") with
        {
            Extra = new ClientRuntimeInfo(true, true, "Code.exe", 0, true, true)
        });
        clock.Advance(TimeSpan.FromSeconds(5));
        var second = registry.Heartbeat(CreateHeartbeat("pc-a") with
        {
            Extra = new ClientRuntimeInfo(false, false, "", 100)
        });

        Assert.Equal(first.FirstSeenAt, second.FirstSeenAt);
        Assert.True(second.IsOnline);
        Assert.Equal(100, second.Extra.BatteryPercent);
        Assert.False(second.Extra.ScreenLocked);
        Assert.Throws<LessonValidationException>(() => registry.Heartbeat(CreateHeartbeat("pc-a") with
        {
            Extra = new ClientRuntimeInfo(false, false, "", -1)
        }));
        Assert.Throws<LessonValidationException>(() => registry.Heartbeat(new HeartbeatRequest(
            "pc-b", "1", "", "C:\\Work", "0.1.0", null, null,
            new ClientRuntimeInfo(false, false, "", null))));
    }

    [Fact]
    public void Enqueue_RejectsEmptyTargetsAndBroadcastWithoutClients()
    {
        var registry = new ClientRegistry();
        var queue = new ReliableCommandQueue(registry);
        Assert.Throws<LessonValidationException>(() => queue.Enqueue(new EnqueueCommandRequest(
            [], ClassroomCommandKinds.LockScreen, JsonSerializer.SerializeToElement(new { }))));
        Assert.Throws<LessonValidationException>(() => queue.Enqueue(new EnqueueCommandRequest(
            ["__all__"], ClassroomCommandKinds.LockScreen, JsonSerializer.SerializeToElement(new { }))));
    }

    [Fact]
    public void Acknowledge_RejectsForeignClient_AndKeepsCommand()
    {
        var registry = new ClientRegistry();
        registry.Heartbeat(CreateHeartbeat("pc-a"));
        registry.Heartbeat(CreateHeartbeat("pc-b"));
        var queue = new ReliableCommandQueue(registry);
        var command = queue.Enqueue(new EnqueueCommandRequest(
            ["pc-a"], ClassroomCommandKinds.FocusOn, JsonSerializer.SerializeToElement(new { })));

        Assert.Throws<InvalidOperationException>(() =>
            queue.Acknowledge("pc-b", new CommandAcknowledgement(command.Id, true)));
        Assert.Equal(command.Id, queue.GetPending("pc-a").Single().Id);
        Assert.Empty(queue.GetPending("pc-b"));
    }

    [Fact]
    public void ShortTtl_IsClampedSoCommandSurvivesTenSeconds()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var registry = new ClientRegistry(clock);
        registry.Heartbeat(CreateHeartbeat("pc-a"));
        var queue = new ReliableCommandQueue(registry, clock);
        queue.Enqueue(new EnqueueCommandRequest(
            ["pc-a"], ClassroomCommandKinds.Message, JsonSerializer.SerializeToElement(new { text = "x" }), 1));

        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Single(queue.GetPending("pc-a"));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Empty(queue.GetPending("pc-a"));
    }

    [Fact]
    public void DiscoveryPacket_RejectsWrongTypeAndEmptyHost()
    {
        var valid = new DiscoveryBeacon(
            DiscoveryProtocol.BeaconType, "token", "192.168.1.10", 8765, "server-1", "0.1.0");
        Assert.Null(DiscoveryProtocol.Parse(DiscoveryProtocol.Serialize(valid with { Type = "OTHER" })));
        Assert.Null(DiscoveryProtocol.Parse(DiscoveryProtocol.Serialize(valid with { Host = "" })));
        Assert.Null(DiscoveryProtocol.Parse(DiscoveryProtocol.Serialize(valid with { Port = 0 })));
        Assert.Null(DiscoveryProtocol.Parse([]));
    }

    private static HeartbeatRequest CreateHeartbeat(string clientId) => new(
        clientId, "3", "PC-3", "C:\\Work", "0.1.0", null, null,
        new ClientRuntimeInfo(false, false, "", 90));

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current += duration;
    }
}
