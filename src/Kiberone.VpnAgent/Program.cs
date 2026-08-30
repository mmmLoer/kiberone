using System.Net;
using System.Security.Cryptography;
using System.Text;
using Kiberone.VpnAgent;
using Kiberone.VpnAgent.WireGuard;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

// Official embeddable-dll-service entry: same EXE with /service <conf>
if (args is ["/service", var configFile, ..])
{
    var ok = TunnelService.Run(configFile);
    return ok ? 0 : 1;
}

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options => options.ServiceName = "KiberoneVpnAgent");

builder.Services.Configure<VpnAgentOptions>(builder.Configuration.GetSection(VpnAgentOptions.SectionName));
builder.Services.AddSingleton(sp =>
{
    var opts = sp.GetRequiredService<IOptions<VpnAgentOptions>>().Value;
    opts.Validate();
    return opts;
});
builder.Services.AddSingleton<VpnTunnelManager>();

var earlyOptions = builder.Configuration.GetSection(VpnAgentOptions.SectionName).Get<VpnAgentOptions>() ?? new VpnAgentOptions();
builder.WebHost.UseUrls($"http://0.0.0.0:{earlyOptions.Port}");

var app = builder.Build();
var options = app.Services.GetRequiredService<VpnAgentOptions>();
var allowlist = options.ParseAllowlist();

app.Use(async (context, next) =>
{
    if (allowlist.Count > 0)
    {
        var remote = context.Connection.RemoteIpAddress;
        var allowed = remote is not null && allowlist.Any(entry =>
            IPAddress.TryParse(entry, out var ip) &&
            (ip.Equals(remote) || (remote.IsIPv4MappedToIPv6 && ip.Equals(remote.MapToIPv4()))));
        if (!allowed)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "forbidden_remote" });
            return;
        }
    }

    if (context.Request.Path.Equals("/health") || context.Request.Path.Equals("/v1/health"))
    {
        await next(context);
        return;
    }

    var supplied = context.Request.Headers["X-Vpn-Token"].ToString();
    if (!TokensMatch(supplied, options.ApiToken))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "unauthorized" });
        return;
    }

    await next(context);
});

app.MapGet("/health", () => Results.Ok(new { ok = true, service = "Kiberone.VpnAgent", version = "0.1.0" }));
app.MapGet("/v1/health", () => Results.Ok(new { ok = true, service = "Kiberone.VpnAgent", version = "0.1.0" }));

app.MapGet("/v1/status", (VpnTunnelManager vpn) => Results.Ok(vpn.Status()));
app.MapPost("/v1/connect", (VpnTunnelManager vpn) =>
{
    try { return Results.Ok(vpn.Connect()); }
    catch (FileNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});
app.MapPost("/v1/disconnect", (VpnTunnelManager vpn) =>
{
    try { return Results.Ok(vpn.Disconnect()); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});

// Flat aliases for simple router scripts
app.MapGet("/status", (VpnTunnelManager vpn) => Results.Ok(vpn.Status()));
app.MapPost("/connect", (VpnTunnelManager vpn) =>
{
    try { return Results.Ok(vpn.Connect()); }
    catch (FileNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});
app.MapPost("/disconnect", (VpnTunnelManager vpn) =>
{
    try { return Results.Ok(vpn.Disconnect()); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});

app.Logger.LogInformation("Kiberone.VpnAgent listening on 0.0.0.0:{Port}, config={Config}", options.Port, options.ConfigPath);
await app.RunAsync();
return 0;

static bool TokensMatch(string supplied, string expected)
{
    var a = Encoding.UTF8.GetBytes(supplied ?? string.Empty);
    var b = Encoding.UTF8.GetBytes(expected);
    return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
}
