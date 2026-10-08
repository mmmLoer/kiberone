using Kiberone.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
namespace Kiberone.Infrastructure;
public static class LearningApi
{
    public static void Map(WebApplication app, LearningStore store)
    {
        app.MapPost("/api/learning/quizzes/list", (LearningAuth auth) => Execute(() => store.List(auth)));
        app.MapPost("/api/learning/quizzes/publish", (PublishQuizRequest request) => Execute(() => store.Publish(request)));
        app.MapPost("/api/learning/records", (TypingRecordsRequest request) => Execute(() => store.Records(request)));
    }
    private static IResult Execute<T>(Func<T> action)
    {
        try { return Results.Ok(action()); }
        catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        catch (LearningConflictException) { return Results.Conflict(new { error = "На сервере есть новая версия. Обновите библиотеку." }); }
        catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
    }
}
public sealed class LearningClient : IDisposable
{
    private readonly HttpClient http;
    public LearningClient(string url)
    {
        var uri = new Uri(url.TrimEnd('/') + "/");
        if (uri.Scheme != "https" && !uri.IsLoopback) throw new ArgumentException("Нужен HTTPS-сервер.");
        http = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(25) };
    }
    public async Task<T> PostAsync<T>(string path, object data, CancellationToken ct = default)
    {
        using var response = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(http, path, data, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict) throw new LearningConflictException();
        response.EnsureSuccessStatusCode();
        return await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<T>(response.Content, cancellationToken: ct) ?? throw new IOException("Пустой ответ сервера.");
    }
    public void Dispose() => http.Dispose();
}
