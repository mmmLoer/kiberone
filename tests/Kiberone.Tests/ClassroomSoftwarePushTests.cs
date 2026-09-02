using Kiberone.Core;
using Kiberone.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Kiberone.Tests;

public sealed class ClassroomSoftwarePushTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"kiberone-push-{Guid.NewGuid():N}");

    [Fact]
    public async Task InstallStarterPack_DownloadsFolderAndSkipsUnchangedHash()
    {
        Directory.CreateDirectory(Path.Combine(root, "data"));
        var assets = new AssetDistributionService(Path.Combine(root, "app"), Path.Combine(root, "data"));
        var source = Path.Combine(root, "pack-src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "hello.txt"), "class");
        File.WriteAllText(Path.Combine(source, "Setup.exe"), "installer");
        assets.AddStarterFolder(source);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/starter-pack", () => assets.ListStarterPack());
        app.MapGet("/starter-pack/file", (string name) => assets.OpenStarterAsset(name) is { } asset
            ? Results.File(asset.Content, asset.ContentType, asset.FileName)
            : Results.NotFound());
        await app.StartAsync();

        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        var destination = Path.Combine(root, "student");
        var state = Path.Combine(root, "state.json");
        var launched = new List<string>();

        var first = await ClassroomSoftwarePush.InstallStarterPackAsync(
            http, destination, state, true, path => { launched.Add(path); return CommandExecutionResult.Success; }, null, CancellationToken.None);
        var second = await ClassroomSoftwarePush.InstallStarterPackAsync(
            http, destination, state, true, path => { launched.Add(path); return CommandExecutionResult.Success; }, null, CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.True(File.Exists(Path.Combine(destination, "pack-src", "hello.txt")));
        Assert.Single(launched);
        Assert.Contains("Setup.exe", launched[0]);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
