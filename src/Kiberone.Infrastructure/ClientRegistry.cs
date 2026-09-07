using System.Collections.Concurrent;
using Kiberone.Core;

namespace Kiberone.Infrastructure;

public sealed record ClientPresenceEvent(
    string ClientId,
    string PcNumber,
    string Hostname,
    Guid? StudentId,
    bool Online);

public sealed class ClientRegistry(TimeProvider? timeProvider = null)
{
    private readonly ConcurrentDictionary<string, ClientState> clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> lastKnownOnline = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<ClientPresenceEvent> presenceEvents = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(15);

    public ClassroomClientSnapshot Heartbeat(HeartbeatRequest request)
    {
        Validate(request);
        var now = clock.GetUtcNow();
        var becameOnline = false;
        var state = clients.AddOrUpdate(
            request.ClientId,
            _ =>
            {
                becameOnline = true;
                return new ClientState(request, now, now);
            },
            (_, previous) =>
            {
                if (now - previous.LastSeenAt >= OnlineWindow)
                    becameOnline = true;
                return new ClientState(request, previous.FirstSeenAt, now);
            });

        lastKnownOnline[request.ClientId] = true;
        if (becameOnline)
        {
            presenceEvents.Enqueue(new ClientPresenceEvent(
                request.ClientId, request.PcNumber, request.Hostname, request.StudentId, Online: true));
        }

        SweepOffline(now);
        return ToSnapshot(state, now);
    }

    public IReadOnlyList<ClassroomClientSnapshot> GetAll()
    {
        SweepOffline(clock.GetUtcNow());
        return clients.Values
            .Select(state => ToSnapshot(state, clock.GetUtcNow()))
            .OrderByDescending(client => client.IsOnline)
            .ThenBy(client => NaturalPcNumber(client.PcNumber))
            .ThenBy(client => client.Hostname, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> GetKnownClientIds() => clients.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();

    public bool Contains(string clientId) => clients.ContainsKey(clientId);

    public IReadOnlyList<ClientPresenceEvent> DrainPresenceEvents()
    {
        SweepOffline(clock.GetUtcNow());
        var drained = new List<ClientPresenceEvent>();
        while (presenceEvents.TryDequeue(out var item))
            drained.Add(item);
        return drained;
    }

    private void SweepOffline(DateTimeOffset now)
    {
        foreach (var pair in clients)
        {
            var online = now - pair.Value.LastSeenAt < OnlineWindow;
            if (lastKnownOnline.TryGetValue(pair.Key, out var wasOnline) && wasOnline && !online)
            {
                presenceEvents.Enqueue(new ClientPresenceEvent(
                    pair.Key,
                    pair.Value.Request.PcNumber,
                    pair.Value.Request.Hostname,
                    pair.Value.Request.StudentId,
                    Online: false));
            }
            lastKnownOnline[pair.Key] = online;
        }
    }

    private static void Validate(HeartbeatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId) || request.ClientId.Length > 160)
            throw new LessonValidationException(["Некорректный client_id."]);
        if (string.IsNullOrWhiteSpace(request.Hostname) || request.Hostname.Length > 255)
            throw new LessonValidationException(["Некорректное имя компьютера."]);
        if (request.PcNumber.Length > 32 || request.AppVersion.Length > 32 || request.WatchFolder.Length > 1024)
            throw new LessonValidationException(["Heartbeat содержит слишком длинное поле."]);
        if (request.Extra.BatteryPercent is < 0 or > 100)
            throw new LessonValidationException(["Заряд батареи должен быть от 0 до 100%."]);
    }

    private static int NaturalPcNumber(string value) => int.TryParse(value, out var number) ? number : int.MaxValue;

    private static ClassroomClientSnapshot ToSnapshot(ClientState state, DateTimeOffset now) => new(
        state.Request.ClientId,
        state.Request.PcNumber,
        state.Request.Hostname,
        state.Request.WatchFolder,
        state.Request.AppVersion,
        state.Request.StudentId,
        state.Request.SessionId,
        state.Request.Extra,
        state.FirstSeenAt,
        state.LastSeenAt,
        now - state.LastSeenAt < OnlineWindow);

    private sealed record ClientState(HeartbeatRequest Request, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt);
}
