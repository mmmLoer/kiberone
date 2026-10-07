using System.Text.Json;
using Kiberone.Core;

namespace Kiberone.Infrastructure;

public sealed class TypingAttemptOutbox
{
    private readonly object gate = new();
    private readonly string path;
    private readonly List<SubmitTypingAttemptRequest> pending;

    public TypingAttemptOutbox(string path)
    {
        this.path = path;
        pending = File.Exists(path)
            ? JsonSerializer.Deserialize<List<SubmitTypingAttemptRequest>>(File.ReadAllText(path)) ?? []
            : [];
    }

    public void Enqueue(SubmitTypingAttemptRequest attempt)
    {
        lock (gate)
        {
            Save([.. pending, attempt]);
            pending.Add(attempt);
        }
    }

    public bool TryGetNextForStudent(Guid studentId, out SubmitTypingAttemptRequest? attempt)
    {
        lock (gate)
        {
            attempt = pending.FirstOrDefault(x => x.StudentId == studentId);
            return attempt is not null;
        }
    }

    public void Acknowledge(Guid attemptId)
    {
        lock (gate)
        {
            var index = pending.FindIndex(x => x.Attempt.AttemptId == attemptId);
            if (index < 0) return;
            var remaining = pending.Where((_, position) => position != index).ToList();
            Save(remaining);
            pending.RemoveAt(index);
        }
    }

    private void Save(IReadOnlyList<SubmitTypingAttemptRequest> attempts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(attempts));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
