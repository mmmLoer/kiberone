using CommunityToolkit.Mvvm.ComponentModel;
using Kiberone.Infrastructure;
namespace Kiberone.Tutor.ViewModels;
public partial class MainViewModel
{
    private readonly LocationCredentialStore locationCredentials = credentialStore ?? new();
    [ObservableProperty] private string setupLocationPassword = "";
    [ObservableProperty] private string credentialStatus = "Введите пароль один раз. После подключения он сохранится для этой локации.";
    private string CredentialServer => string.IsNullOrWhiteSpace(HubUrl) ? ClassroomHubClient.DefaultBaseUrl : HubUrl;
    partial void OnSetupLocationPasswordChanged(string value) => ConfirmSetupLocationCommand.NotifyCanExecuteChanged();
    private string ReadLocationPassword(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return "";
        try { return locationCredentials.Read(CredentialServer, location) ?? ""; }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning($"Location credential read failed: {error.GetType().Name}");
            CredentialStatus = "Не удалось прочитать сохранённый пароль. Введите его ещё раз.";
            return "";
        }
    }
    private void RestoreLocationPassword()
    {
        CredentialStatus = "Введите пароль один раз. После подключения он сохранится для этой локации.";
        LocationUploadPassword = ReadLocationPassword(LocationName);
        if (!string.IsNullOrWhiteSpace(LocationUploadPassword)) CredentialStatus = "Пароль сохранён. Повторно вводить его не нужно.";
    }
    private void RememberLocationPassword(string server, string location, string password)
    {
        try
        {
            locationCredentials.Save(server, location, password);
            SetCredentialStatus("Пароль сохранён. Повторно вводить его не нужно.");
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning($"Location credential save failed: {error.GetType().Name}");
            SetCredentialStatus("Подключение работает, но пароль не удалось сохранить. После перезапуска потребуется ввести его снова.");
        }
    }
    private void SetCredentialStatus(string text)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) CredentialStatus = text;
        else Avalonia.Threading.Dispatcher.UIThread.Post(() => CredentialStatus = text);
    }
    partial void OnHubUrlChanged(string value) { if (!loadingSettings) RestoreLocationPassword(); }
}
