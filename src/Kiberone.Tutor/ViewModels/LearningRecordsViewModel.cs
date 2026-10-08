using Kiberone.Core;
using Kiberone.Infrastructure;
namespace Kiberone.Tutor.ViewModels;
public partial class MainViewModel
{
    private async Task<IReadOnlyList<TypingPersonalBest>> SyncTypingRecordsAsync(Guid student, IReadOnlyList<TypingPersonalBest> records, CancellationToken ct)
    {
        var location = LocationName; var password = LocationUploadPassword;
        if (string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("Настройте локацию.");
        using var client = new LearningClient(MailServerUrl);
        return await client.PostAsync<List<TypingPersonalBest>>("api/learning/records", new TypingRecordsRequest(location, password, student, records), ct);
    }
}
