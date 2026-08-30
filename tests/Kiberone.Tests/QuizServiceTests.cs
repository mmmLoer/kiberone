using Kiberone.Core;
using Kiberone.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Kiberone.Tests;

public sealed class QuizServiceTests : IAsyncLifetime
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"kiberone-quiz-{Guid.NewGuid():N}.db");
    private DbContextOptions<ClassroomDbContext> options = null!;
    private ClientRegistry clients = null!;
    private ReliableCommandQueue commands = null!;
    private QuizService service = null!;
    private Guid studentId;

    public async Task InitializeAsync()
    {
        options = ClassroomDatabase.CreateOptions(databasePath);
        await ClassroomDatabase.InitializeAsync(options);
        await using var db = new ClassroomDbContext(options);
        var group = new ClassroomGroup { Name = "Quiz Group" };
        var student = new Student { FirstName = "Анна", LastName = "Смирнова", GroupId = group.Id };
        group.Students.Add(student);
        db.Groups.Add(group);
        await db.SaveChangesAsync();
        studentId = student.Id;
        clients = new ClientRegistry();
        clients.Heartbeat(new HeartbeatRequest("pc-quiz", "7", "PC-7", "C:\\Projects", BuildInfo.Version, studentId, null, new ClientRuntimeInfo(false, false, "", null)));
        commands = new ReliableCommandQueue(clients);
        service = new QuizService(options, clients, commands);
    }

    [Fact]
    public async Task CorrectAnswer_IsPersistedAndAwardsXpOnce()
    {
        var quiz = await service.StartAsync(new StartQuizRequest("Сколько будет 2 + 2?", ["3", "4", "5"], 1, 15, ["pc-quiz"]));
        Assert.Equal(ClassroomCommandKinds.QuizStart, commands.GetPending("pc-quiz").Single().Kind);

        var first = await service.SubmitAsync(new SubmitQuizAnswerRequest(quiz.Id, "pc-quiz", 1));
        var duplicate = await service.SubmitAsync(new SubmitQuizAnswerRequest(quiz.Id, "pc-quiz", 1));

        Assert.True(first.Correct);
        Assert.Equal(15, first.XpAwarded);
        Assert.Contains("уже", duplicate.Message);
        await using var db = new ClassroomDbContext(options);
        Assert.Equal(15, await db.Students.Where(x => x.Id == studentId).Select(x => x.Xp).SingleAsync());
        Assert.Single(await db.QuizAnswers.ToListAsync());
    }

    [Fact]
    public async Task InvalidQuiz_IsRejectedBeforeCommand()
    {
        await Assert.ThrowsAsync<LessonValidationException>(() => service.StartAsync(new StartQuizRequest("?", ["one"], 4, -1, ["pc-quiz"])));
        Assert.Empty(commands.GetPending("pc-quiz"));
    }

    public async Task DisposeAsync()
    {
        await Task.Yield();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { databasePath, databasePath + "-shm", databasePath + "-wal" }) if (File.Exists(path)) File.Delete(path);
    }
}
