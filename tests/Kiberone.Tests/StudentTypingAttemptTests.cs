using System.Text.Json;
using Kiberone.Core;
using Kiberone.Student.ViewModels;

namespace Kiberone.Tests;

public sealed class StudentTypingAttemptTests
{
    [Fact]
    public void GoalReached_DoesNotFinishUntilTextEnds()
    {
        var model = new MainViewModel { TargetText = new string('a', 150), GoalCharacters = 120 };
        for (var i = 0; i < 121; i++) model.HandleCharacter('a');
        Assert.False(model.IsFinished);
        Assert.Equal(121, model.CorrectKeys);
        Assert.Equal(100, model.Progress);
        for (var i = 121; i < 150; i++) model.HandleCharacter('a');
        Assert.True(model.IsFinished);
    }

    [Fact]
    public void PersonalRecords_StartAt100AndRemainAfterRestartForSameStudent()
    {
        var owner = Guid.NewGuid();
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIBERone Classroom", "typing-records", owner.ToString("N"));
        try
        {
            MainViewModel Create()
            {
                var model = new MainViewModel { TargetText = new string('a', 150), GoalCharacters = 120 };
                model.SetStudents([new StudentSummary(owner, "Тест", 10, Guid.NewGuid(), "Группа", 0, 0, 1)]);
                model.ConfirmStudentCommand.Execute(null);
                return model;
            }
            var first = Create();
            for (var i = 0; i < 99; i++) first.HandleCharacter('a');
            Assert.Equal("Рекорд: —", first.PersonalSpeedRecord);
            first.HandleCharacter('a');
            Assert.NotEqual("Рекорд: —", first.PersonalSpeedRecord);
            Assert.Equal("Рекорд: 100%", first.PersonalAccuracyRecord);
            first.HandleCharacter('b');
            Assert.Equal("Рекорд: 100%", first.PersonalAccuracyRecord);
            var restarted = Create();
            Assert.Equal(first.PersonalSpeedRecord, restarted.PersonalSpeedRecord);
            Assert.Equal(first.PersonalAccuracyRecord, restarted.PersonalAccuracyRecord);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void TypingTrainer_KeepsExpectedGlyphCenteredAndShowsRemainingText()
    {
        var model = new MainViewModel { TargetText = "abcdef" };
        model.StartLesson();

        Assert.Equal(15, model.TextGlyphs.Count);
        Assert.Equal("a", model.TextGlyphs[7].DisplayCharacter);
        Assert.Equal("abcdef", model.RemainingText);

        model.HandleCharacter('a');
        Assert.Equal("b", model.TextGlyphs[7].DisplayCharacter);
        Assert.Equal("bcdef", model.RemainingText);

        model.HandleCharacter('ц');
        Assert.Equal("b", model.TextGlyphs[7].DisplayCharacter);
        Assert.Equal("Переключить на английский", model.SwitchLayoutButtonText);
        Assert.True(model.HasLayoutWarning);
        Assert.Equal(1, model.WrongKeys);
    }

    [Fact]
    public void FinishedAssignedLesson_EmitsOneAttemptWithLessonAndMetrics()
    {
        var lessonId = Guid.NewGuid();
        var model = new MainViewModel();
        var sent = new List<TypingAttemptDraft>();
        model.TypingAttemptCompleted = sent.Add;
        var command = new ClassroomCommand(
            Guid.NewGuid(), ClassroomCommandKinds.TypingStart,
            JsonSerializer.SerializeToElement(new
            {
                lesson_id = lessonId,
                lesson_name = "Проверка печати",
                text = "аб",
                minimum_characters = 2
            }),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.True(model.ApplyCommand(command).Succeeded);
        model.HandleCharacter('а');
        model.HandleCharacter('в');
        model.FinishAttemptCommand.Execute(null);
        model.FinishAttemptCommand.Execute(null);

        var attempt = Assert.Single(sent);
        Assert.Equal(lessonId, attempt.LessonId);
        Assert.Equal("Проверка печати", attempt.LessonName);
        Assert.Equal(1, attempt.CorrectKeys);
        Assert.Equal(1, attempt.WrongKeys);
        Assert.Equal("аб", attempt.Text);
        Assert.Equal(2, attempt.GoalCharacters);
    }
}
