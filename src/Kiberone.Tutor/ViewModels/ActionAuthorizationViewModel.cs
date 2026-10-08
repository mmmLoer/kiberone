using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kiberone.Infrastructure;

namespace Kiberone.Tutor.ViewModels;

public partial class MainViewModel
{
    private TaskCompletionSource<bool>? pendingAuthorization;
    private string authorizationLocation = "";
    private string authorizationServer = "";
    [ObservableProperty] private bool showActionAuthorization;
    [ObservableProperty] private string authorizationAction = "";
    [ObservableProperty] private string authorizationPassword = "";
    [ObservableProperty] private string authorizationError = "";
    [ObservableProperty] private bool authorizationBusy;
    public Func<string, string, string, Task<bool>>? LocationPasswordValidator { get; set; }

    public Task<bool> RequestLocationAuthorizationAsync(string action, bool requireConfirmation = true)
    {
        if (string.IsNullOrWhiteSpace(LocationName))
        { ShowSelectionError("Выберите локацию."); return Task.FromResult(false); }
        if (!requireConfirmation)
        {
            if (string.IsNullOrWhiteSpace(LocationUploadPassword)) LocationUploadPassword = ReadLocationPassword(LocationName);
            if (!string.IsNullOrWhiteSpace(LocationUploadPassword)) return Task.FromResult(true);
        }
        if (pendingAuthorization is not null) return Task.FromResult(false);
        authorizationLocation = LocationName;
        authorizationServer = CredentialServer;
        AuthorizationAction = action;
        AuthorizationPassword = "";
        AuthorizationError = "";
        pendingAuthorization = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ShowActionAuthorization = true;
        return pendingAuthorization.Task;
    }

    private bool CanConfirmActionAuthorization() => !AuthorizationBusy && !string.IsNullOrWhiteSpace(AuthorizationPassword);
    partial void OnAuthorizationPasswordChanged(string value) { AuthorizationError = ""; ConfirmActionAuthorizationCommand.NotifyCanExecuteChanged(); }
    partial void OnAuthorizationBusyChanged(bool value) { ConfirmActionAuthorizationCommand.NotifyCanExecuteChanged(); CancelActionAuthorizationCommand.NotifyCanExecuteChanged(); }

    [RelayCommand(CanExecute = nameof(CanConfirmActionAuthorization))]
    private async Task ConfirmActionAuthorizationAsync()
    {
        if (pendingAuthorization is null) return;
        AuthorizationBusy = true;
        var password = AuthorizationPassword;
        try
        {
            var expected = ReadLocationPassword(authorizationLocation);
            bool valid;
            if (LocationPasswordValidator is not null)
                valid = await LocationPasswordValidator(authorizationServer, authorizationLocation, password);
            else if (!string.IsNullOrEmpty(expected))
            {
                valid = CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(password)));
                if (!valid) valid = await new ClassroomHubClient(authorizationServer).DownloadAsync(authorizationLocation, password) is not null;
            }
            else
                valid = await new ClassroomHubClient(authorizationServer).DownloadAsync(authorizationLocation, password) is not null;
            if (LocationName != authorizationLocation || CredentialServer != authorizationServer)
            { CompleteAuthorization(false); return; }
            if (!valid) { AuthorizationError = "Неверный пароль."; return; }
            LocationUploadPassword = password;
            RememberLocationPassword(authorizationServer, authorizationLocation, password);
            CompleteAuthorization(true);
        }
        catch (UnauthorizedAccessException) { AuthorizationError = "Неверный пароль."; }
        catch { AuthorizationError = "Не удалось подтвердить действие. Попробуйте ещё раз."; }
        finally { AuthorizationBusy = false; }
    }

    private bool CanCancelActionAuthorization() => !AuthorizationBusy;
    [RelayCommand(CanExecute = nameof(CanCancelActionAuthorization))]
    private void CancelActionAuthorization() => CompleteAuthorization(false);

    private void CompleteAuthorization(bool accepted)
    {
        var pending = pendingAuthorization;
        pendingAuthorization = null;
        AuthorizationPassword = "";
        AuthorizationError = "";
        ShowActionAuthorization = false;
        pending?.TrySetResult(accepted);
    }
}
