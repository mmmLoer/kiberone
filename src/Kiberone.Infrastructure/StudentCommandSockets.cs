using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Kiberone.Core;
using Microsoft.AspNetCore.Http;

namespace Kiberone.Infrastructure;

public sealed class StudentCommandSockets : IAsyncDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Session>> sessions =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task AcceptAsync(
        HttpContext context,
        string clientId,
        ReliableCommandQueue commands,
        CancellationToken cancellationToken,
        Func<Task>? onChannelOpened = null,
        Func<Task>? onChannelClosed = null)
    {
        // ClassroomServer authenticates the device and checks the query identity before entry.
        // Receive frames never select a different client; the session stays bound to clientId.
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var session = new Session(socket);
        var id = Guid.NewGuid();
        var bag = sessions.GetOrAdd(clientId, _ => new ConcurrentDictionary<Guid, Session>());
        var opened = bag.IsEmpty;
        bag[id] = session;
        if (opened && onChannelOpened is not null)
        {
            try { await onChannelOpened(); } catch { }
        }
        try
        {
            await SendPendingAsync(session, clientId, commands, cancellationToken);
            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            bag.TryRemove(id, out _);
            var closed = bag.IsEmpty;
            // Retain the per-client bag: a reconnect may already be adding a new session.
            await session.DisposeAsync();
            if (closed && onChannelClosed is not null)
            {
                try { await onChannelClosed(); } catch { }
            }
        }
    }

    public void Push(IReadOnlyList<string> clientIds, ClassroomCommand command)
    {
        _ = PushAsync(clientIds, command);
    }

    public async Task PushAsync(IReadOnlyList<string> clientIds, ClassroomCommand command)
    {
        var deliveries = clientIds.Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(clientId => sessions.TryGetValue(clientId, out var bag) ? bag.Values.ToArray() : [])
            .Select(async session =>
            {
                try { await session.SendAsync(command, CancellationToken.None); }
                catch (Exception) { await session.DisposeAsync(); }
            });
        await Task.WhenAll(deliveries);
    }

    private static async Task SendPendingAsync(Session session, string clientId, ReliableCommandQueue commands, CancellationToken cancellationToken)
    {
        IReadOnlyList<ClassroomCommand> pending;
        try
        {
            pending = commands.GetPending(clientId);
        }
        catch (KeyNotFoundException)
        {
            return;
        }

        foreach (var command in pending)
            await session.SendAsync(command, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var bag in sessions.Values)
        {
            foreach (var session in bag.Values)
                await session.DisposeAsync();
        }
        sessions.Clear();
    }

    private sealed class Session(WebSocket socket) : IAsyncDisposable
    {
        private readonly SemaphoreSlim sendLock = new(1, 1);
        private int disposed;

        public async Task SendAsync(ClassroomCommand command, CancellationToken cancellationToken)
        {
            if (socket.State != WebSocketState.Open) return;
            var payload = JsonSerializer.SerializeToUtf8Bytes(command, JsonOptions);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await sendLock.WaitAsync(timeout.Token);
            try
            {
                if (socket.State != WebSocketState.Open) return;
                await socket.SendAsync(payload, WebSocketMessageType.Text, true, timeout.Token);
            }
            finally
            {
                sendLock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 1) return;
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
                }
            }
            catch
            {
            }
            socket.Dispose();
            // In-flight sends release the semaphore after the socket is disposed.
        }
    }
}
