using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Kiberone.Infrastructure;

/// <summary>
/// Starts the Linux release systemd unit when GitHub pushes to main.
/// </summary>
public static class GithubPushReleaseHook
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/hooks/github", async (HttpRequest request) =>
        {
            var secret = Environment.GetEnvironmentVariable("KIBERONE_GITHUB_WEBHOOK_SECRET");
            if (string.IsNullOrWhiteSpace(secret))
                return Results.NotFound();

            var payload = await new StreamReader(request.Body).ReadToEndAsync();
            if (!VerifySignature(secret, payload, request.Headers["X-Hub-Signature-256"].ToString()))
                return Results.Json(new { error = "Неверная подпись webhook." }, statusCode: 401);

            var eventName = request.Headers["X-GitHub-Event"].ToString();
            if (string.Equals(eventName, "ping", StringComparison.OrdinalIgnoreCase))
                return Results.Ok(new { ok = true, message = "pong" });

            if (!string.Equals(eventName, "push", StringComparison.OrdinalIgnoreCase))
                return Results.Ok(new { ok = true, skipped = true, reason = $"event:{eventName}" });

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var branchRef = root.TryGetProperty("ref", out var refProperty) ? refProperty.GetString() : null;
            var expectedBranch = Environment.GetEnvironmentVariable("KIBERONE_GIT_BRANCH") ?? "main";
            if (!string.Equals(branchRef, $"refs/heads/{expectedBranch}", StringComparison.Ordinal))
                return Results.Ok(new { ok = true, skipped = true, reason = $"ref:{branchRef}" });

            var unit = Environment.GetEnvironmentVariable("KIBERONE_RELEASE_UNIT") ?? "kiberone-release.service";
            try
            {
                StartReleaseUnit(unit);
            }
            catch (Exception error)
            {
                return Results.Json(new { error = error.Message }, statusCode: 500);
            }

            var after = root.TryGetProperty("after", out var afterProperty) ? afterProperty.GetString() : null;
            return Results.Ok(new { ok = true, started = unit, commit = after });
        });
    }

    private static bool VerifySignature(string secret, string payload, string header)
    {
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
            return false;
        var expectedHex = header["sha256=".Length..].Trim();
        var key = Encoding.UTF8.GetBytes(secret);
        var body = Encoding.UTF8.GetBytes(payload);
        var hash = HMACSHA256.HashData(key, body);
        var actualHex = Convert.ToHexString(hash);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(actualHex.ToLowerInvariant()),
            Encoding.ASCII.GetBytes(expectedHex.ToLowerInvariant()));
    }

    private static void StartReleaseUnit(string unit)
    {
        // Don't wait for the long build — just queue the oneshot unit.
        var start = new ProcessStartInfo
        {
            FileName = "/bin/systemctl",
            ArgumentList = { "restart", "--no-block", unit },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Не удалось запустить systemctl.");
        process.WaitForExit(10_000);
        if (process.ExitCode != 0)
        {
            var err = process.StandardError.ReadToEnd();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(err)
                ? $"systemctl exit {process.ExitCode}"
                : err.Trim());
        }
    }
}
