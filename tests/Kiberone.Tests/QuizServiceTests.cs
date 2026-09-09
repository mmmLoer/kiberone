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

    [Fact]
    public async Task WrongAnswer_AwardsNoXp_AndRejectsOutOfRangeIndex()
    {
        var quiz = await service.StartAsync(new StartQuizRequest("Столица Франции?", ["Берлин", "Париж", "Рим"], 1, 10, ["pc-quiz"]));
        var wrong = await service.SubmitAsync(new SubmitQuizAnswerRequest(quiz.Id, "pc-quiz", 0));

        Assert.False(wrong.Correct);
        Assert.Equal(0, wrong.XpAwarded);
        await Assert.ThrowsAsync<LessonValidationException>(() =>
            service.SubmitAsync(new SubmitQuizAnswerRequest(quiz.Id, "other-pc", 9)));
        await using var db = new ClassroomDbContext(options);
        Assert.Equal(0, await db.Students.Where(x => x.Id == studentId).Select(x => x.Xp).SingleAsync());
    }

    [Fact]
    public async Task MultipleCorrect_RequiresExactSet_AndPersistsIndices()
    {
        var quiz = await service.StartAsync(new StartQuizRequest(
            "Какие числа чётные?",
            ["1", "2", "3", "4"],
            1,
            20,
            ["pc-quiz"],
            CorrectIndices: [1, 3]));

        await using (var db = new ClassroomDbContext(options))
        {
            var session = await db.QuizSessions.SingleAsync();
            Assert.True(session.IsMultiple);
            Assert.Equal(1, session.CorrectIndex);
            Assert.Equal("[1,3]", session.CorrectIndicesJson.Replace(" ", ""));
        }

        Assert.True(commands.GetPending("pc-quiz").Single().Payload.GetProperty("allow_multiple").GetBoolean());

        var partial = await service.SubmitAsync(new SubmitQuizAnswerRequest(quiz.Id, "pc-quiz", 1, [1]));
        Assert.False(partial.Correct);
        Assert.Equal(0, partial.XpAwarded);

        clients.Heartbeat(new HeartbeatRequest("pc-quiz-2", "8", "PC-8", "C:\\Projects", BuildInfo.Version, studentId, null, new ClientRuntimeInfo(false, false, "", null)));
        var extra = await service.SubmitAsync(new SubmitQuizAnswerRequest(quiz.Id, "pc-quiz-2", 1, [1, 2, 3]));
        Assert.False(extra.Correct);

        clients.Heartbeat(new HeartbeatRequest("pc-quiz-3", "9", "PC-9", "C:\\Projects", BuildInfo.Version, studentId, null, new ClientRuntimeInfo(false, false, "", null)));
        var exact = await service.SubmitAsync(new SubmitQuizAnswerRequest(quiz.Id, "pc-quiz-3", 1, [3, 1]));
        Assert.True(exact.Correct);
        Assert.Equal(20, exact.XpAwarded);

        await using (var db = new ClassroomDbContext(options))
        {
            var stored = await db.QuizAnswers.SingleAsync(x => x.ClientId == "pc-quiz-3");
            Assert.Equal("[1,3]", stored.SelectedIndicesJson.Replace(" ", ""));
            Assert.Equal(20, await db.Students.Where(x => x.Id == studentId).Select(x => x.Xp).SingleAsync());
        }
    }

    [Fact]
    public async Task MultipleCorrect_ShuffleRemapsAllIndices()
    {
        var quiz = await service.StartAsync(new StartQuizRequest(
            "Выберите гласные",
            ["А", "Б", "У", "Г"],
            0,
            10,
            ["pc-quiz"],
            ShuffleAnswers: true,
            CorrectIndices: [0, 2]));

        await using var db = new ClassroomDbContext(options);
        var session = await db.QuizSessions.SingleAsync();
        var launchOptions = System.Text.Json.JsonSerializer.Deserialize<List<string>>(session.OptionsJson) ?? [];
        var correct = System.Text.Json.JsonSerializer.Deserialize<List<int>>(session.CorrectIndicesJson) ?? [];
        Assert.Equal(2, correct.Count);
        Assert.Equal(new[] { "А", "У" }, correct.Select(i => launchOptions[i]).OrderBy(x => x).ToArray());

        var selected = launchOptions.Select((text, i) => (text, i)).Where(x => x.text is "А" or "У").Select(x => x.i).ToList();
        var result = await service.SubmitAsync(new SubmitQuizAnswerRequest(quiz.Id, "pc-quiz", selected[0], selected));
        Assert.True(result.Correct);
        Assert.Equal(10, result.XpAwarded);
    }

    [Fact]
    public void QuizDocument_PreservesMultipleCorrectIndices()
    {
        var question = new QuizDocumentQuestion
        {
            Text = "Выберите верное",
            Options = ["A", "B", "C", "D"],
            CorrectIndex = 0,
            CorrectIndices = [0, 2]
        };

        Assert.Equal(new[] { 0, 2 }, question.ResolvedCorrectIndices());

        var legacy = new QuizDocumentQuestion { Options = ["A", "B"], CorrectIndex = 1 };
        Assert.Equal(new[] { 1 }, legacy.ResolvedCorrectIndices());

        var unmarked = new QuizDocumentQuestion { Options = ["A", "B"], CorrectIndex = 0, CorrectIndices = [] };
        Assert.Empty(unmarked.ResolvedCorrectIndices());
    }

    public async Task DisposeAsync()
    {
        await Task.Yield();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { databasePath, databasePath + "-shm", databasePath + "-wal" }) if (File.Exists(path)) File.Delete(path);
    }
}
