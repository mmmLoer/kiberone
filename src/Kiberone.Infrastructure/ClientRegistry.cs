using System.Collections.Concurrent;
using Kiberone.Core;

namespace Kiberone.Infrastructure;

public sealed class ClientRegistry(TimeProvider? timeProvider = null)
{
    private readonly ConcurrentDictionary<string, ClientState> clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(15);

    public ClassroomClientSnapshot Heartbeat(HeartbeatRequest request)
    {
        Validate(request);
        var now = clock.GetUtcNow();
        var state = clients.AddOrUpdate(
            request.ClientId,
            _ => new ClientState(request, now, now),
            (_, previous) => new ClientState(request, previous.FirstSeenAt, now));
        return ToSnapshot(state, now);
    }

    public IReadOnlyList<ClassroomClientSnapshot> GetAll() =>
        clients.Values
            .Select(state => ToSnapshot(state, clock.GetUtcNow()))
            .OrderByDescending(client => client.IsOnline)
            .ThenBy(client => NaturalPcNumber(client.PcNumber))
            .ThenBy(client => client.Hostname, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public IReadOnlyList<string> GetKnownClientIds() => clients.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();

    public bool Contains(string clientId) => clients.ContainsKey(clientId);

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
