using System.Text.Json;
using Kiberone.Core;

namespace Kiberone.Tests;

public sealed class FocusModePolicyTests
{
    [Fact]
    public void Parses_multiline_group_rules_and_command_payload()
    {
        Assert.Equal(["Яндекс Игры", "Roblox"], FocusModeBlocklist.Parse("Яндекс Игры; Roblox\nroblox"));
        using var document = JsonDocument.Parse("""{"blocked_titles":["Яндекс Игры"],"allowed_apps":["chrome.exe","code.exe"]}""");
        Assert.Equal(["Яндекс Игры"], FocusModeBlocklist.FromPayload(document.RootElement));
        Assert.Equal(["chrome.exe", "code.exe"], FocusModeBlocklist.AllowedAppsFromPayload(document.RootElement));
    }
}
