using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

const string PreferredPortEnvVar = "PRINBOX_TRAY_PORT";
const string WebExeOverrideEnvVar = "PRINBOX_WEB_EXE";
const string WebDllOverrideEnvVar = "PRINBOX_WEB_DLL";
const int DefaultPort = 7341;

var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
var port = PickPort();
var baseUrl = $"http://localhost:{port}";
var webHost = ResolveWebHost();

Console.WriteLine($"PR Inbox Launcher");

if (webHost is null)
{
    Console.Error.WriteLine("Error: web server host not found.");
    Console.Error.WriteLine($"Searched relative to: {AppContext.BaseDirectory}");
    Console.Error.WriteLine(
        $"Set {WebExeOverrideEnvVar} (native host) or {WebDllOverrideEnvVar} (.dll), " +
        "or build the solution first: dotnet build PrInbox.slnx");
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var psi = new ProcessStartInfo
{
    FileName = webHost.LaunchFileName,
    Arguments = webHost.Arguments,
    WorkingDirectory = webHost.WorkingDirectory,
    UseShellExecute = false,
};
psi.Environment["ASPNETCORE_URLS"] = baseUrl;
psi.Environment["PRINBOX_SHUTDOWN_TOKEN"] = token;

using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start web process.");
Console.WriteLine($"Started web host (pid {process.Id}) on {baseUrl} — waiting for server...");

var healthy = await WaitForHealthyAsync(baseUrl, TimeSpan.FromSeconds(40), cts.Token);
if (!healthy)
{
    Console.Error.WriteLine("Server did not become healthy in time.");
    if (!process.HasExited)
        process.Kill(entireProcessTree: true);
    return 1;
}

Console.WriteLine($"Ready. Opening {baseUrl} in browser...");
Console.WriteLine("Press Ctrl+C to stop.");
OpenBrowser(baseUrl);

try
{
    await process.WaitForExitAsync(cts.Token);
}
catch (OperationCanceledException)
{
    // Ctrl+C
}

if (!process.HasExited)
{
    Console.WriteLine("\nShutting down...");
    await StopAsync(baseUrl, token, process);
}

Console.WriteLine("Stopped.");
return 0;

// ── helpers ──────────────────────────────────────────────────────────────────

async Task<bool> WaitForHealthyAsync(string url, TimeSpan timeout, CancellationToken ct)
{
    var deadline = DateTime.UtcNow + timeout;
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
    {
        try
        {
            using var resp = await http.GetAsync($"{url}/healthz", ct);
            if (resp.IsSuccessStatusCode)
                return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch { }

        try { await Task.Delay(250, ct); }
        catch (OperationCanceledException) { return false; }
    }
    return false;
}

async Task StopAsync(string url, string shutdownToken, Process proc)
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{url}/shutdown");
        req.Headers.TryAddWithoutValidation("X-Shutdown-Token", shutdownToken);
        await http.SendAsync(req);
    }
    catch { }

    try
    {
        using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await proc.WaitForExitAsync(killCts.Token);
    }
    catch (OperationCanceledException) { }

    if (!proc.HasExited)
    {
        try { proc.Kill(entireProcessTree: true); }
        catch { }
    }
}

void OpenBrowser(string url)
{
    try
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Could not open browser automatically: {ex.Message}");
        Console.WriteLine($"Open manually: {url}");
    }
}

int PickPort()
{
    var configured = Environment.GetEnvironmentVariable(PreferredPortEnvVar);
    var preferred = int.TryParse(configured, out var p) && p is > 0 and < 65536 ? p : DefaultPort;

    if (IsPortFree(preferred))
        return preferred;

    // Preferred port is busy (e.g. a dev instance already running) — grab a free ephemeral port.
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var freePort = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return freePort;
}

bool IsPortFree(int p)
{
    try
    {
        var l = new TcpListener(IPAddress.Loopback, p);
        l.Start();
        l.Stop();
        return true;
    }
    catch { return false; }
}

WebHostLaunchSpec? ResolveWebHost()
{
    var exeName = OperatingSystem.IsWindows() ? "pr-inbox-web.exe" : "pr-inbox-web";
    const string dllName = "pr-inbox-web.dll";

    static WebHostLaunchSpec? FromExecutable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return null;
        }

        var workDir = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(workDir))
        {
            return null;
        }

        return new WebHostLaunchSpec(executablePath, "", workDir);
    }

    static WebHostLaunchSpec? FromDotnetDll(string? dllPath)
    {
        if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath))
        {
            return null;
        }

        var workDir = Path.GetDirectoryName(dllPath);
        if (string.IsNullOrWhiteSpace(workDir))
        {
            return null;
        }

        return new WebHostLaunchSpec("dotnet", $"\"{dllPath}\"", workDir);
    }

    // 1. Explicit overrides.
    var exeOverride = Environment.GetEnvironmentVariable(WebExeOverrideEnvVar);
    var dllOverride = Environment.GetEnvironmentVariable(WebDllOverrideEnvVar);
    var overrideHost = FromExecutable(exeOverride) ?? FromDotnetDll(dllOverride);
    if (overrideHost is not null)
    {
        return overrideHost;
    }

    var baseDir = AppContext.BaseDirectory;

    // 2. Tool-pack layout: launcher next to "webhost/pr-inbox-web.dll".
    var toolPackDll = Path.Combine(baseDir, "webhost", dllName);
    var toolPackHost = FromDotnetDll(toolPackDll);
    if (toolPackHost is not null)
    {
        return toolPackHost;
    }

    // 3. Side-by-side publish layout: launcher + web executable.
    var sideBySideExe = Path.Combine(baseDir, exeName);
    var sideBySideHost = FromExecutable(sideBySideExe);
    if (sideBySideHost is not null)
    {
        return sideBySideHost;
    }

    // 4. Dev layout: src/PrInbox.Launcher/bin/<Config>/net10.0/ → src/PrInbox.Web/bin/<Config>/net10.0/
    try
    {
        var tfmDir = new DirectoryInfo(baseDir.TrimEnd(Path.DirectorySeparatorChar));
        var configDir = tfmDir.Parent;              // <Config>
        var srcDir = configDir?.Parent?.Parent?.Parent; // bin → PrInbox.Launcher → src
        if (configDir is not null && srcDir is not null)
        {
            var devExe = Path.Combine(srcDir.FullName, "PrInbox.Web", "bin", configDir.Name, "net10.0", exeName);
            var devHost = FromExecutable(devExe);
            if (devHost is not null)
            {
                return devHost;
            }

            var devDll = Path.Combine(srcDir.FullName, "PrInbox.Web", "bin", configDir.Name, "net10.0", dllName);
            var devDllHost = FromDotnetDll(devDll);
            if (devDllHost is not null)
            {
                return devDllHost;
            }
        }
    }
    catch { }

    return null;
}

internal sealed record WebHostLaunchSpec(string LaunchFileName, string Arguments, string WorkingDirectory);
