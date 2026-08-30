using System.Collections.Concurrent;
using Kiberone.Core;

namespace Kiberone.Infrastructure;

public sealed class ReliableCommandQueue(ClientRegistry registry, TimeProvider? timeProvider = null)
{
    private readonly ConcurrentDictionary<Guid, PendingCommand> pending = [];
    private readonly ConcurrentQueue<CommandReceipt> receipts = [];
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public ClassroomCommand Enqueue(EnqueueCommandRequest request)
    {
        if (!ClassroomCommandKinds.SafeKnownKinds.Contains(request.Kind))
            throw new LessonValidationException([$"Неизвестный или небезопасный тип команды: {request.Kind}."]);
        if (request.ClientIds.Count == 0)
            throw new LessonValidationException(["Выберите хотя бы один клиент."]);
        var targetIds = request.ClientIds.Contains("__all__", StringComparer.Ordinal)
            ? registry.GetKnownClientIds()
            : request.ClientIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (targetIds.Count == 0)
            throw new LessonValidationException(["Нет известных клиентов для команды."]);
        var unknown = targetIds.Where(id => !registry.Contains(id)).ToList();
        if (unknown.Count > 0)
            throw new LessonValidationException([$"Неизвестные клиенты: {string.Join(", ", unknown)}."]);
        var ttl = TimeSpan.FromSeconds(Math.Clamp(request.TtlSeconds ?? 300, 10, 86_400));
        var now = clock.GetUtcNow();
        var command = new ClassroomCommand(Guid.NewGuid(), request.Kind, request.Payload.Clone(), now, now + ttl);
        if (!pending.TryAdd(command.Id, new PendingCommand(command, targetIds.ToHashSet(StringComparer.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Не удалось добавить команду.");
        CleanupExpired();
        return command;
    }

    public IReadOnlyList<ClassroomCommand> GetPending(string clientId)
    {
        if (!registry.Contains(clientId)) throw new KeyNotFoundException("Клиент не зарегистрирован через heartbeat.");
        CleanupExpired();
        return pending.Values
            .Where(item => IsPendingFor(item, clientId))
            .Select(item => item.Command)
            .OrderBy(command => command.CreatedAt)
            .ToList();
    }

    public CommandReceipt Acknowledge(string clientId, CommandAcknowledgement acknowledgement)
    {
        if (!pending.TryGetValue(acknowledgement.CommandId, out var item))
            throw new KeyNotFoundException("Команда не найдена или срок её действия истёк.");
        bool completed;
        lock (item.SyncRoot)
        {
            if (!item.Targets.Contains(clientId))
                throw new InvalidOperationException("Команда не предназначена этому клиенту.");
            item.AcknowledgedClients.Add(clientId);
            completed = item.AcknowledgedClients.IsSupersetOf(item.Targets);
        }
        var receipt = new CommandReceipt(
            item.Command.Id,
            clientId,
            acknowledgement.Succeeded,
            TrimError(acknowledgement.Error),
            clock.GetUtcNow());
        receipts.Enqueue(receipt);
        if (completed) pending.TryRemove(item.Command.Id, out _);
        return receipt;
    }

    public IReadOnlyList<CommandReceipt> GetReceipts(int limit = 200) =>
        receipts.Reverse().Take(Math.Clamp(limit, 1, 1000)).ToList();

    private void CleanupExpired()
    {
        var now = clock.GetUtcNow();
        foreach (var entry in pending.Where(entry => entry.Value.Command.ExpiresAt <= now))
            pending.TryRemove(entry.Key, out _);
    }

    private static string? TrimError(string? error) => string.IsNullOrWhiteSpace(error) ? null : error.Trim()[..Math.Min(error.Trim().Length, 500)];

    private static bool IsPendingFor(PendingCommand item, string clientId)
    {
        lock (item.SyncRoot)
            return item.Targets.Contains(clientId) && !item.AcknowledgedClients.Contains(clientId);
    }

    private sealed class PendingCommand(ClassroomCommand command, HashSet<string> targets)
    {
        public ClassroomCommand Command { get; } = command;
        public HashSet<string> Targets { get; } = targets;
        public HashSet<string> AcknowledgedClients { get; } = new(StringComparer.OrdinalIgnoreCase);
        public object SyncRoot { get; } = new();
    }
}
