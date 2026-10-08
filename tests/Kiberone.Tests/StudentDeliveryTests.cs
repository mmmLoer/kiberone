using System.Net;
using System.Reflection;
using System.Text.Json;
using Kiberone.Core;
using Kiberone.Infrastructure;

namespace Kiberone.Tests;

public sealed class StudentDeliveryTests
{
    [Fact]
    public async Task LostAcknowledgement_RetriesWithoutExecutingAgain()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            await using var agent = new StudentAgent(watchFolder: root);
            var executions = 0;
            agent.CommandHandler = (_, _) => { executions++; return Task.FromResult(CommandExecutionResult.Success); };
            using var handler = new Replies(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK, HttpStatusCode.NotFound);
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
            var command = new ClassroomCommand(Guid.NewGuid(), ClassroomCommandKinds.Notification,
                JsonSerializer.SerializeToElement(new { message = "test" }), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5));
            await Assert.ThrowsAsync<HttpRequestException>(() => Execute(agent, http, command));
            await Execute(agent, http, command);
            await Execute(agent, http, command);
            Assert.Equal(1, executions);
            Assert.Equal(3, handler.Calls);
            await Execute(agent, http, command with { Id = Guid.NewGuid(), ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) });
            Assert.Equal(1, executions);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExpiredQuizAnswer_DoesNotBlockFollowingAnswers()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            await using var agent = new StudentAgent(watchFolder: root);
            agent.SubmitQuizAnswer(Guid.NewGuid(), 0);
            agent.SubmitQuizAnswer(Guid.NewGuid(), 1);
            using var handler = new Replies(HttpStatusCode.NotFound, HttpStatusCode.OK);
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
            await Invoke(agent, "FlushQuizAnswersAsync", http, CancellationToken.None);
            await Invoke(agent, "FlushQuizAnswersAsync", http, CancellationToken.None);
            Assert.Equal(2, handler.Calls);
        }
        finally { Directory.Delete(root, true); }
    }

    private static Task Execute(StudentAgent agent, HttpClient http, ClassroomCommand command) =>
        Invoke(agent, "ExecuteAndAckAsync", http, command, CancellationToken.None);

    private static Task Invoke(StudentAgent agent, string method, params object[] arguments) =>
        (Task)typeof(StudentAgent).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(agent, arguments)!;

    private sealed class Replies(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statuses[Calls++]) { Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json") });
    }
}
