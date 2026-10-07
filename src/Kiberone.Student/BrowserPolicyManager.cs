using Kiberone.Core;
using Microsoft.Win32;
using System.Text.Json;

namespace Kiberone.Student;

internal sealed class BrowserPolicyManager : IDisposable
{
    private sealed record PolicyEntry(string Path, string Name, string Value);
    private readonly List<PolicyEntry> written = [];
    private readonly string journal = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIBERone Classroom", "browser-policy.json");
    private static readonly string[] Browsers = [@"SOFTWARE\Policies\Google\Chrome", @"SOFTWARE\Policies\Microsoft\Edge"];
    public BrowserPolicyManager()
    {
        if (!File.Exists(journal)) return;
        try
        {
            written.AddRange(JsonSerializer.Deserialize<List<PolicyEntry>>(File.ReadAllText(journal)) ?? []);
            Dispose();
        }
        catch (Exception error) { System.Diagnostics.Trace.TraceError("Browser policy recovery: {0}", error.Message); }
    }
    public string Apply(ClassroomAccessPolicy policy)
    {
        Dispose();
        if (!policy.Enabled) return "Правила сайтов выключены";
        foreach (var browser in Browsers)
        {
            Write(browser + @"\URLBlocklist", policy.OnlyAllowedSites ? ["*"] : policy.BlockedSites);
            Write(browser + @"\URLAllowlist", policy.AllowedSites);
        }
        return "Правила переданы Chrome и Edge. Применятся после обновления политик браузера.";
    }
    private void Write(string path, IReadOnlyList<string> values)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path);
        var index = 900001;
        foreach (var value in values)
        {
            while (key.GetValue(index.ToString()) is not null) index++;
            var name = (index++).ToString();
            written.Add(new PolicyEntry(path, name, value));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(journal)!);
            File.WriteAllText(journal + ".tmp", JsonSerializer.Serialize(written));
            File.Move(journal + ".tmp", journal, true);
            key.SetValue(name, value, RegistryValueKind.String);
        }
    }
    public void Dispose()
    {
        foreach (var (path, name, value) in written)
        {
            if (!Browsers.Any(browser => path == browser + @"\URLBlocklist" || path == browser + @"\URLAllowlist")
                || !int.TryParse(name, out var index) || index < 900001) continue;
            using var key = Registry.CurrentUser.OpenSubKey(path, true);
            if (key?.GetValue(name) is string current && current == value) key.DeleteValue(name, false);
        }
        written.Clear();
        if (File.Exists(journal)) File.Delete(journal);
    }
}
