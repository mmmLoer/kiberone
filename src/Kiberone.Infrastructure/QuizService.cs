using System.Text.Json;
using Kiberone.Core;
using Microsoft.EntityFrameworkCore;

namespace Kiberone.Infrastructure;

public sealed class QuizService(DbContextOptions<ClassroomDbContext> options, ClientRegistry clients, ReliableCommandQueue commands)
{
    public async Task<QuizSession> StartAsync(StartQuizRequest request, CancellationToken ct = default)
    {
        var question = request.Question.Trim();
        var answers = request.Options.Select(x => x.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        var errors = new List<string>();
        if (question.Length is < 3 or > 500) errors.Add("Вопрос должен содержать от 3 до 500 символов.");
        if (answers.Count is < 2 or > 6) errors.Add("Укажите от 2 до 6 вариантов ответа.");
        if (request.CorrectIndex < 0 || request.CorrectIndex >= answers.Count) errors.Add("Некорректный номер правильного ответа.");
        if (request.XpReward is < 0 or > 1000) errors.Add("Награда должна быть от 0 до 1000 XP.");
        if (request.ClientIds.Count == 0) errors.Add("Выберите хотя бы один компьютер.");
        if (errors.Count > 0) throw new LessonValidationException(errors);
        var session = new QuizSession { Question = question, OptionsJson = JsonSerializer.Serialize(answers), CorrectIndex = request.CorrectIndex, XpReward = request.XpReward };
        await using (var db = new ClassroomDbContext(options))
        {
            db.QuizSessions.Add(session);
            await db.SaveChangesAsync(ct);
        }
        var payload = JsonSerializer.SerializeToElement(new { session_id = session.Id, question, options = answers, xp_reward = request.XpReward });
        commands.Enqueue(new EnqueueCommandRequest(request.ClientIds, ClassroomCommandKinds.QuizStart, payload, 600));
        return session;
    }

    public async Task<QuizResult> SubmitAsync(SubmitQuizAnswerRequest request, CancellationToken ct = default)
    {
        await using var db = new ClassroomDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var existing = await db.QuizAnswers.SingleOrDefaultAsync(x => x.SessionId == request.SessionId && x.ClientId == request.ClientId, ct);
        if (existing is not null) return new QuizResult(existing.SessionId, existing.IsCorrect, existing.XpAwarded, "Ответ уже был принят.");
        var session = await db.QuizSessions.SingleOrDefaultAsync(x => x.Id == request.SessionId && x.IsActive, ct) ?? throw new KeyNotFoundException("Активная викторина не найдена.");
        var optionsList = JsonSerializer.Deserialize<List<string>>(session.OptionsJson) ?? [];
        if (request.SelectedIndex < 0 || request.SelectedIndex >= optionsList.Count) throw new LessonValidationException(["Некорректный вариант ответа."]);
        var studentId = clients.GetAll().FirstOrDefault(x => x.ClientId == request.ClientId)?.StudentId;
        var correct = request.SelectedIndex == session.CorrectIndex;
        var xp = correct && studentId is not null ? session.XpReward : 0;
        if (xp > 0)
        {
            var student = await db.Students.SingleOrDefaultAsync(x => x.Id == studentId, ct);
            if (student is not null) student.Xp = checked(student.Xp + xp);
        }
        var answer = new QuizAnswer { SessionId = session.Id, ClientId = request.ClientId, StudentId = studentId, SelectedIndex = request.SelectedIndex, IsCorrect = correct, XpAwarded = xp };
        db.QuizAnswers.Add(answer);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new QuizResult(session.Id, correct, xp, correct ? "Правильно!" : "Ответ принят.");
    }

    public async Task<IReadOnlyList<QuizAnswer>> GetAnswersAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var db = new ClassroomDbContext(options);
        return await db.QuizAnswers.AsNoTracking().Where(x => x.SessionId == sessionId).ToListAsync(ct);
    }
}
