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

    public async Task AcceptAsync(HttpContext context, string clientId, ReliableCommandQueue commands, CancellationToken cancellationToken)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var session = new Session(socket);
        var id = Guid.NewGuid();
        var bag = sessions.GetOrAdd(clientId, _ => new ConcurrentDictionary<Guid, Session>());
        bag[id] = session;
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
            if (bag.IsEmpty)
                sessions.TryRemove(clientId, out _);
            await session.DisposeAsync();
        }
    }

    public void Push(IReadOnlyList<string> clientIds, ClassroomCommand command)
    {
        _ = PushAsync(clientIds, command);
    }

    public async Task PushAsync(IReadOnlyList<string> clientIds, ClassroomCommand command)
    {
        foreach (var clientId in clientIds)
        {
            if (!sessions.TryGetValue(clientId, out var bag)) continue;
            foreach (var session in bag.Values)
            {
                try
                {
                    await session.SendAsync(command, CancellationToken.None);
                }
                catch (Exception)
                {
                    await session.DisposeAsync();
                }
            }
        }
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
            await sendLock.WaitAsync(cancellationToken);
            try
            {
                if (socket.State != WebSocketState.Open) return;
                await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
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
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch
            {
            }
            socket.Dispose();
            sendLock.Dispose();
        }
    }
}
