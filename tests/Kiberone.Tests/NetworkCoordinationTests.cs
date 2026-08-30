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
