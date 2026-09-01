using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Kiberone.Core;
using Kiberone.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Kiberone.Tests;

public sealed class CommandPushTests
{
    [Fact]
    public async Task WebSocket_ReceivesCommandAfterEnqueue()
    {
        var registry = new ClientRegistry();
        registry.Heartbeat(new HeartbeatRequest(
            "pc-ws", "1", "PC-1", "C:\\Work", "0.9.0", null, null,
            new ClientRuntimeInfo(false, false, "", null)));
        var queue = new ReliableCommandQueue(registry);
        var sockets = new StudentCommandSockets();
        queue.CommandQueued += (command, targets) => sockets.Push(targets, command);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Map("/ws", async (HttpContext context) =>
        {
            var clientId = context.Request.Query["client_id"].ToString();
            await sockets.AcceptAsync(context, clientId, queue, context.RequestAborted);
        });
        await app.StartAsync();
        var baseUrl = app.Urls.First().Replace("http://", "ws://", StringComparison.Ordinal);
        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"{baseUrl}/ws?client_id=pc-ws"), CancellationToken.None);
        await Task.Delay(200);

        var command = queue.Enqueue(new EnqueueCommandRequest(
            ["pc-ws"], ClassroomCommandKinds.LockScreen, JsonSerializer.SerializeToElement(new { })));

        var buffer = new byte[4096];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await client.ReceiveAsync(buffer, timeout.Token);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
        var received = JsonSerializer.Deserialize<ClassroomCommand>(json, StudentCommandSockets.JsonOptions);
        Assert.Equal(command.Id, received?.Id);
        Assert.Equal(ClassroomCommandKinds.LockScreen, received?.Kind);

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await sockets.DisposeAsync();
    }

    [Fact]
    public async Task WebSocket_ReceivesPendingCommandOnConnect()
    {
        var registry = new ClientRegistry();
        registry.Heartbeat(new HeartbeatRequest(
            "pc-late", "2", "PC-2", "C:\\Work", "0.9.0", null, null,
            new ClientRuntimeInfo(false, false, "", null)));
        var queue = new ReliableCommandQueue(registry);
        var sockets = new StudentCommandSockets();
        var command = queue.Enqueue(new EnqueueCommandRequest(
            ["pc-late"], ClassroomCommandKinds.FocusOn, JsonSerializer.SerializeToElement(new { })));

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Map("/ws", async (HttpContext context) =>
        {
            var clientId = context.Request.Query["client_id"].ToString();
            await sockets.AcceptAsync(context, clientId, queue, context.RequestAborted);
        });
        await app.StartAsync();
        var baseUrl = app.Urls.First().Replace("http://", "ws://", StringComparison.Ordinal);
        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"{baseUrl}/ws?client_id=pc-late"), CancellationToken.None);

        var buffer = new byte[4096];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await client.ReceiveAsync(buffer, timeout.Token);
        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
        var received = JsonSerializer.Deserialize<ClassroomCommand>(json, StudentCommandSockets.JsonOptions);
        Assert.Equal(command.Id, received?.Id);
        Assert.Equal(ClassroomCommandKinds.FocusOn, received?.Kind);

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await sockets.DisposeAsync();
    }

    [Fact]
    public async Task WebSocket_UnknownClientConnectsWithoutPendingAndPushIsSafe()
    {
        var registry = new ClientRegistry();
        var queue = new ReliableCommandQueue(registry);
        var sockets = new StudentCommandSockets();
        queue.CommandQueued += (command, targets) => sockets.Push(targets, command);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Map("/ws", async (HttpContext context) =>
        {
            var clientId = context.Request.Query["client_id"].ToString();
            await sockets.AcceptAsync(context, clientId, queue, context.RequestAborted);
        });
        await app.StartAsync();
        var baseUrl = app.Urls.First().Replace("http://", "ws://", StringComparison.Ordinal);
        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"{baseUrl}/ws?client_id=ghost"), CancellationToken.None);

        registry.Heartbeat(new HeartbeatRequest(
            "pc-late", "2", "PC-2", "C:\\Work", "0.9.0", null, null,
            new ClientRuntimeInfo(false, false, "", null)));
        queue.Enqueue(new EnqueueCommandRequest(
            ["pc-late"], ClassroomCommandKinds.UnlockScreen, JsonSerializer.SerializeToElement(new { })));
        await sockets.PushAsync(["missing"], new ClassroomCommand(
            Guid.NewGuid(), ClassroomCommandKinds.LockScreen, JsonSerializer.SerializeToElement(new { }),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1)));

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var buffer = new byte[256];
        await Assert.ThrowsAnyAsync<Exception>(() => client.ReceiveAsync(buffer, timeout.Token));

        if (client.State == WebSocketState.Open)
            await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await sockets.DisposeAsync();
    }
}
