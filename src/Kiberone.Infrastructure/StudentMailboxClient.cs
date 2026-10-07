using System.Net.Http.Json;
using Kiberone.Core;

namespace Kiberone.Infrastructure;

public sealed class StudentMailboxClient(string baseUrl) : IDisposable
{
    private readonly HttpClient http = new() { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(20) };
    public async Task<StudentMailAccount> ProvisionAsync(StudentMailProvisionRequest body, CancellationToken ct = default)
    {
        EnsureHttps();
        using var response = await http.PostAsJsonAsync("api/mail/accounts", body, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<StudentMailAccount>(cancellationToken: ct) ?? throw new InvalidDataException("Не получены данные почты.");
    }
    public async Task<IReadOnlyList<StudentMailMessage>> ReadAsync(StudentMailAccount account, CancellationToken ct = default)
    {
        EnsureHttps();
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/mail/inbox");
        request.Headers.Add("X-Mailbox-Id", account.StudentId.ToString("N"));
        request.Headers.Add("X-Mailbox-Password", account.Password);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            throw new InvalidOperationException("Почтовый API ещё не настроен на сервере.");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("На сервере пока нет почтового API. Требуется обновление Hub.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<StudentMailMessage>>(cancellationToken: ct) ?? [];
    }
    private void EnsureHttps()
    {
        if (http.BaseAddress!.Scheme != "https" && !http.BaseAddress.IsLoopback)
            throw new InvalidOperationException("Для почты нужен HTTPS-адрес сервера.");
    }
    public void Dispose() => http.Dispose();
}
