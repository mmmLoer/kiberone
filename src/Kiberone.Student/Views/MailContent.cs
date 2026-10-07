using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Diagnostics;
using Forms = System.Windows.Forms;
namespace Kiberone.Student.Views;
public sealed class MailContent : NativeControlHost
{
    public static readonly StyledProperty<string?> HtmlProperty = AvaloniaProperty.Register<MailContent, string?>(nameof(Html));
    public string? Html { get => GetValue(HtmlProperty); set => SetValue(HtmlProperty, value); }
    private Forms.Panel? panel;
    private WebView2? browser;
    public static readonly StyledProperty<bool> IsReadyProperty = AvaloniaProperty.Register<MailContent, bool>(nameof(IsReady));
    public bool IsReady { get => GetValue(IsReadyProperty); private set => SetValue(IsReadyProperty, value); }
    private bool ready;
    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        Trace.WriteLine("MailContent: create native host");
        Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
        panel = new Forms.Panel();
        browser = new WebView2 { Dock = Forms.DockStyle.Fill };
        panel.Controls.Add(browser);
        Trace.WriteLine("MailContent: acquire handle");
        var handle = panel.Handle;
        Trace.WriteLine("MailContent: initialize browser");
        _ = InitializeAsync(browser);
        return new PlatformHandle(handle, "HWND");
    }
    private async Task InitializeAsync(WebView2 view)
    {
        try
        {
            Trace.WriteLine("MailContent: create environment");
            var environment = await CoreWebView2Environment.CreateAsync(null, System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIBERone Classroom", "mail-browser"));
            Trace.WriteLine("MailContent: environment created");
            if (view.IsDisposed) return;
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = true;
            await view.EnsureCoreWebView2Async(environment, options);
            Trace.WriteLine("MailContent: browser ready");
            if (view.IsDisposed) return;
            var core = view.CoreWebView2;
            core.Settings.IsScriptEnabled = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.NewWindowRequested += (_, e) => { e.Handled = true; if (e.IsUserInitiated) OpenLink(e.Uri); };
            core.NavigationCompleted += (_, e) => { IsReady = e.IsSuccess && !string.IsNullOrWhiteSpace(Html); if (!e.IsSuccess) Trace.TraceWarning($"Mail rendering failed: {e.WebErrorStatus}"); };
            core.NavigationStarting += (_, e) =>
            {

                if (!e.IsUserInitiated && (e.Uri == "about:blank" || IsOwnHtml(e.Uri))) return;
                e.Cancel = true;
                if (e.IsUserInitiated) OpenLink(e.Uri);
            };
            ready = true;
            Render();
        }
        catch (Exception error) { Trace.TraceError(error.ToString()); IsReady = false; IsVisible = false; }
    }
    private static void OpenLink(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0)
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); } catch (Exception error) { Trace.TraceError(error.ToString()); }
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HtmlProperty) Render();
    }
    private bool IsOwnHtml(string uri)
    {
        const string prefix = "data:text/html;charset=utf-8;base64,";
        if (!uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        return uri[prefix.Length..] == Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Html ?? "<html></html>"));
    }
    private void Render() { if (ready && browser is not null && !browser.IsDisposed) { IsReady = false; browser.CoreWebView2.NavigateToString(Html ?? "<html></html>"); } }
    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        ready = false; IsReady = false;
        browser?.Dispose(); browser = null;
        panel?.Dispose(); panel = null;
    }
}
