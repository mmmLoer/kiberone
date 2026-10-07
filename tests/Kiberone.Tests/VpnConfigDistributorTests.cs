using Kiberone.Core;

namespace Kiberone.Tests;

public sealed class VpnConfigDistributorTests
{
    [Fact]
    public void Assign_matches_by_pc_number_then_fills_remaining_in_order()
    {
        using var folder = new TempDirectory();
        File.WriteAllText(Path.Combine(folder.Path, "03.conf"), "[Interface]\nPrivateKey = key-03");
        File.WriteAllText(Path.Combine(folder.Path, "07.conf"), "[Interface]\nPrivateKey = key-07");
        File.WriteAllText(Path.Combine(folder.Path, "peer-10.conf"), "[Interface]\nPrivateKey = key-10");

        var clients = new[]
        {
            CreateClient("c-1", "07", "PC-07"),
            CreateClient("c-2", "03", "PC-03"),
            CreateClient("c-3", "11", "PC-11")
        };

        var assignments = VpnConfigDistributor.Assign(clients, folder.Path);

        Assert.Equal(3, assignments.Count);
        Assert.Equal("07.conf", assignments.Single(x => x.ClientId == "c-1").ConfigFileName);
        Assert.Equal("03.conf", assignments.Single(x => x.ClientId == "c-2").ConfigFileName);
        Assert.Equal("peer-10.conf", assignments.Single(x => x.ClientId == "c-3").ConfigFileName);
    }

    [Fact]
    public void Assign_reports_unassigned_clients_when_configs_are_missing()
    {
        using var folder = new TempDirectory();
        File.WriteAllText(Path.Combine(folder.Path, "01.conf"), "[Interface]\nPrivateKey = key-01");

        var clients = new[]
        {
            CreateClient("c-1", "01", "PC-01"),
            CreateClient("c-2", "02", "PC-02")
        };

        var assignments = VpnConfigDistributor.Assign(clients, folder.Path);
        var summary = VpnConfigDistributor.DescribeAssignments(assignments, clients.Length, 1);

        Assert.Single(assignments);
        Assert.Contains("без конфига: 1", summary);
    }

    [Fact]
    public void Assign_never_distributes_duplicate_private_keys()
    {
        using var folder = new TempDirectory();
        File.WriteAllText(Path.Combine(folder.Path, "01.conf"), "[Interface]\nPrivateKey = same-key");
        File.WriteAllText(Path.Combine(folder.Path, "02.conf"), "# another file\n[Interface]\nPrivateKey = same-key");
        var assignments = VpnConfigDistributor.Assign(
            [CreateClient("c-1", "01", "PC-01"), CreateClient("c-2", "02", "PC-02")], folder.Path);
        Assert.Single(assignments);
    }

    private static ClassroomClientSnapshot CreateClient(string clientId, string pcNumber, string hostname) =>
        new(
            clientId,
            pcNumber,
            hostname,
            "C:\\Projects",
            BuildInfo.Version,
            null,
            null,
            new ClientRuntimeInfo(false, false, string.Empty, null, false),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            true);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kiberone-vpn-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}
