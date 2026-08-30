namespace Kiberone.Core;

public static class VpnConfigDistributor
{
    public sealed record Assignment(
        string ClientId,
        string PcNumber,
        string Hostname,
        string ConfigFilePath,
        string ConfigFileName);

    public static IReadOnlyList<Assignment> Assign(
        IEnumerable<ClassroomClientSnapshot> clients,
        string configsFolder)
    {
        if (!Directory.Exists(configsFolder))
            return [];

        var onlineClients = clients
            .Where(client => client.IsOnline)
            .OrderBy(client => NaturalPcNumber(client.PcNumber))
            .ThenBy(client => client.Hostname, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (onlineClients.Count == 0)
            return [];

        var configFiles = Directory
            .GetFiles(configsFolder, "*.conf", SearchOption.TopDirectoryOnly)
            .Select(path =>
            {
                var fileName = Path.GetFileName(path);
                var (sortKey, key) = ExtractKey(Path.GetFileNameWithoutExtension(fileName));
                return new ConfigEntry(path, fileName, sortKey, key);
            })
            .OrderBy(entry => entry.SortKey)
            .ThenBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (configFiles.Count == 0)
            return [];

        var assignments = new List<Assignment>();
        var usedConfigs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unmatchedClients = new List<ClassroomClientSnapshot>();

        foreach (var client in onlineClients)
        {
            var match = FindBestMatch(client, configFiles, usedConfigs);
            if (match is null)
            {
                unmatchedClients.Add(client);
                continue;
            }

            usedConfigs.Add(match.Path);
            assignments.Add(new Assignment(client.ClientId, client.PcNumber, client.Hostname, match.Path, match.FileName));
        }

        var remainingConfigs = configFiles.Where(entry => !usedConfigs.Contains(entry.Path)).ToList();
        for (var index = 0; index < unmatchedClients.Count && index < remainingConfigs.Count; index++)
        {
            var client = unmatchedClients[index];
            var config = remainingConfigs[index];
            assignments.Add(new Assignment(client.ClientId, client.PcNumber, client.Hostname, config.Path, config.FileName));
        }

        return assignments;
    }

    public static string DescribeAssignments(IReadOnlyList<Assignment> assignments, int onlineClientCount, int configCount)
    {
        if (configCount == 0)
            return "В папке нет .conf файлов.";

        if (onlineClientCount == 0)
            return "Нет онлайн-учеников для распределения.";

        if (assignments.Count == 0)
            return $"{configCount} конфигов · {onlineClientCount} учеников · совпадений нет.";

        var unassignedClients = Math.Max(0, onlineClientCount - assignments.Count);
        var unusedConfigs = Math.Max(0, configCount - assignments.Count);
        var summary = $"Распределено {assignments.Count} из {Math.Min(onlineClientCount, configCount)}";
        if (unassignedClients > 0)
            summary += $" · без конфига: {unassignedClients}";
        if (unusedConfigs > 0)
            summary += $" · лишних конфигов: {unusedConfigs}";
        return summary;
    }

    private static ConfigEntry? FindBestMatch(
        ClassroomClientSnapshot client,
        IReadOnlyList<ConfigEntry> configFiles,
        ISet<string> usedConfigs)
    {
        var pcNumber = client.PcNumber.Trim();
        var hostname = client.Hostname.Trim();

        foreach (var config in configFiles)
        {
            if (usedConfigs.Contains(config.Path))
                continue;

            var stem = Path.GetFileNameWithoutExtension(config.FileName);
            if (string.Equals(stem, pcNumber, StringComparison.OrdinalIgnoreCase)
                || string.Equals(stem, hostname, StringComparison.OrdinalIgnoreCase)
                || string.Equals(config.Key, pcNumber, StringComparison.OrdinalIgnoreCase))
            {
                return config;
            }
        }

        foreach (var config in configFiles)
        {
            if (usedConfigs.Contains(config.Path))
                continue;

            var stem = Path.GetFileNameWithoutExtension(config.FileName);
            if (stem.Length >= 2
                && (hostname.Contains(stem, StringComparison.OrdinalIgnoreCase)
                    || pcNumber.Contains(stem, StringComparison.OrdinalIgnoreCase)))
            {
                return config;
            }
        }

        foreach (var config in configFiles)
        {
            if (usedConfigs.Contains(config.Path))
                continue;

            if (config.Key.Length > 0
                && (hostname.Contains(config.Key, StringComparison.OrdinalIgnoreCase)
                    || pcNumber.Contains(config.Key, StringComparison.OrdinalIgnoreCase)))
            {
                return config;
            }
        }

        return null;
    }

    private static int NaturalPcNumber(string value) => int.TryParse(value, out var number) ? number : int.MaxValue;

    private static (int SortKey, string Key) ExtractKey(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length > 0 && int.TryParse(digits, out var number))
            return (number, digits.TrimStart('0').Length == 0 ? "0" : digits.TrimStart('0'));

        return (int.MaxValue, value.ToLowerInvariant());
    }

    private sealed record ConfigEntry(string Path, string FileName, int SortKey, string Key);
}
