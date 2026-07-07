using System.ComponentModel;
using Microsoft.Extensions.Logging;
using PrInbox.Core.Config;
using PrInbox.Core.Credentials;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PrInbox.Cli.Commands;

internal sealed class ConfigDoctorSettings : CommandSettings
{
    [CommandOption("--config <PATH>")]
    public string? ConfigPath { get; init; }
}

internal sealed class ConfigDoctorCommand : AsyncCommand<ConfigDoctorSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ConfigDoctorSettings settings, CancellationToken cancellationToken)
    {
        var log = CliLoggerFactory.Instance.CreateLogger<ConfigDoctorCommand>();
        log.LogInformation("CLI config doctor started (configPath={ConfigPath}).", settings.ConfigPath ?? "<default>");

        var config = await PrInboxConfig.LoadAsync(settings.ConfigPath);

        AnsiConsole.MarkupLine("[bold]pr-inbox config doctor[/]");
        AnsiConsole.MarkupLine($"  config: [cyan]{Markup.Escape(settings.ConfigPath ?? PrInboxConfig.DefaultPath)}[/]");
        AnsiConsole.WriteLine();

        if (config.Sources.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No sources configured.[/]");
            AnsiConsole.MarkupLine("Run [bold]pr-inbox config init[/] then add sources.");
            return 1;
        }

        var allOk = true;
        foreach (var sc in config.Sources)
        {
            AnsiConsole.Markup($"  [cyan]{Markup.Escape(sc.Id)}[/] ({sc.Kind}, host=[white]{Markup.Escape(sc.Host ?? "n/a")}[/]) ");
            if (!sc.Enabled)
            {
                AnsiConsole.MarkupLine("[grey]disabled[/]");
                continue;
            }

            try
            {
                ITokenProvider provider = sc.Kind switch
                {
                    SourceConfigKind.GitHub or SourceConfigKind.GitHubEnterprise =>
                        new GhCliTokenProvider(sc.Id, sc.Host ?? throw new InvalidOperationException("host required"), sc.Identity),
                    SourceConfigKind.AzureDevOps =>
                        new AzureCliTokenProvider(sc.Id),
                    _ => throw new InvalidOperationException($"Unknown kind {sc.Kind}"),
                };

                var token = await provider.GetTokenAsync();
                var identity = await provider.GetAuthenticatedIdentityAsync();
                AnsiConsole.MarkupLine($"[green]ok[/] (token length {token.Length}, identity: [white]{Markup.Escape(identity ?? "<unknown>")}[/])");
            }
            catch (TokenAcquisitionException ex)
            {
                allOk = false;
                AnsiConsole.MarkupLine("[red]failed[/]");
                var firstLine = ex.Message.Split('\n')[0];
                AnsiConsole.MarkupLine($"    [grey]{Markup.Escape(firstLine)}[/]");
            }
        }

        AnsiConsole.WriteLine();
        if (config.Ado.Projects.Count > 0)
        {
            AnsiConsole.MarkupLine($"[grey]ADO projects configured: {config.Ado.Projects.Count}[/]");
            foreach (var p in config.Ado.Projects)
            {
                AnsiConsole.MarkupLine($"  - {Markup.Escape(p.Org)}/{Markup.Escape(p.Project)}");
            }
        }

        log.LogInformation("CLI config doctor completed (sourceCount={SourceCount}, allOk={AllOk}).", config.Sources.Count, allOk);
        return allOk ? 0 : 1;
    }
}

internal sealed class ConfigInitSettings : CommandSettings
{
    [CommandOption("--config <PATH>")]
    public string? ConfigPath { get; init; }
}

internal sealed class ConfigInitCommand : AsyncCommand<ConfigInitSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ConfigInitSettings settings, CancellationToken cancellationToken)
    {
        var log = CliLoggerFactory.Instance.CreateLogger<ConfigInitCommand>();
        var path = settings.ConfigPath ?? PrInboxConfig.DefaultPath;
        if (File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[yellow]Config already exists at {Markup.Escape(path)}.[/]");
            AnsiConsole.MarkupLine("Edit it directly, or use the [bold]config add-source[/] commands.");
            log.LogInformation("CLI config init skipped: config already exists at {Path}.", path);
            return 0;
        }

        var seed = new PrInboxConfig
        {
            Sources =
            {
                new SourceConfig
                {
                    Id = "gh.com",
                    Kind = SourceConfigKind.GitHub,
                    Host = "github.com",
                    Identity = "default",
                    Enabled = true,
                },
            },
            Bots = new BotConfig { ExtraLogins = { "Copilot" } },
        };

        await seed.SaveAsync(path);
        AnsiConsole.MarkupLine($"[green]Initialized config[/] at [cyan]{Markup.Escape(path)}[/]");
        AnsiConsole.MarkupLine("[grey]Edit to add more sources or run [bold]pr-inbox config doctor[/] to verify auth.[/]");
        log.LogInformation("CLI config init created config at {Path}.", path);
        return 0;
    }
}

internal sealed class AddSourceSettings : CommandSettings
{
    [CommandArgument(0, "<KIND>")]
    [Description("Source kind: github | github-enterprise | azure-devops")]
    public required string Kind { get; init; }

    [CommandArgument(1, "<HOST_OR_ORG>")]
    [Description("GitHub: hostname (e.g. github.com or github.contoso.com). ADO: org name.")]
    public required string HostOrOrg { get; init; }

    [CommandOption("--id <ID>")]
    public string? Id { get; init; }

    [CommandOption("--config <PATH>")]
    public string? ConfigPath { get; init; }
}

internal sealed class AddSourceCommand : AsyncCommand<AddSourceSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, AddSourceSettings settings, CancellationToken cancellationToken)
    {
        var log = CliLoggerFactory.Instance.CreateLogger<AddSourceCommand>();
        var config = await PrInboxConfig.LoadAsync(settings.ConfigPath);
        var kind = settings.Kind.ToLowerInvariant() switch
        {
            "github" => SourceConfigKind.GitHub,
            "github-enterprise" or "ghe" => SourceConfigKind.GitHubEnterprise,
            "azure-devops" or "ado" => SourceConfigKind.AzureDevOps,
            _ => throw new ArgumentException($"Unknown kind '{settings.Kind}'."),
        };

        var id = settings.Id ?? kind switch
        {
            SourceConfigKind.GitHub => settings.HostOrOrg == "github.com" ? "gh.com" : $"gh.{settings.HostOrOrg}",
            SourceConfigKind.GitHubEnterprise => $"ghe.{settings.HostOrOrg}",
            SourceConfigKind.AzureDevOps => $"ado:{settings.HostOrOrg}",
            _ => settings.HostOrOrg,
        };

        if (config.Sources.Any(s => s.Id == id))
        {
            AnsiConsole.MarkupLine($"[yellow]Source '{Markup.Escape(id)}' already exists.[/]");
            log.LogInformation("CLI add-source skipped: source {SourceId} already exists.", id);
            return 0;
        }

        config.Sources.Add(new SourceConfig
        {
            Id = id,
            Kind = kind,
            Host = kind == SourceConfigKind.AzureDevOps ? null : settings.HostOrOrg,
            Identity = "default",
            Enabled = true,
        });

        await config.SaveAsync(settings.ConfigPath);
        AnsiConsole.MarkupLine($"[green]Added source[/] [cyan]{Markup.Escape(id)}[/] ({kind})");
        log.LogInformation("CLI add-source completed (sourceId={SourceId}, kind={Kind}, hostOrOrg={HostOrOrg}).", id, kind, settings.HostOrOrg);
        return 0;
    }
}

internal sealed class AddAdoProjectSettings : CommandSettings
{
    [CommandArgument(0, "<ORG>")]
    public required string Org { get; init; }

    [CommandArgument(1, "<PROJECT>")]
    public required string Project { get; init; }

    [CommandOption("--config <PATH>")]
    public string? ConfigPath { get; init; }
}

internal sealed class AddAdoProjectCommand : AsyncCommand<AddAdoProjectSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, AddAdoProjectSettings settings, CancellationToken cancellationToken)
    {
        var log = CliLoggerFactory.Instance.CreateLogger<AddAdoProjectCommand>();
        var config = await PrInboxConfig.LoadAsync(settings.ConfigPath);
        if (config.Ado.Projects.Any(p => p.Org == settings.Org && p.Project == settings.Project))
        {
            AnsiConsole.MarkupLine("[yellow]Already configured.[/]");
            log.LogInformation("CLI add-ado-project skipped: {Org}/{Project} already configured.", settings.Org, settings.Project);
            return 0;
        }
        config.Ado.Projects.Add(new AdoProjectConfig { Org = settings.Org, Project = settings.Project });
        await config.SaveAsync(settings.ConfigPath);
        AnsiConsole.MarkupLine($"[green]Added ADO project[/] [cyan]{Markup.Escape(settings.Org)}/{Markup.Escape(settings.Project)}[/]");
        log.LogInformation("CLI add-ado-project completed ({Org}/{Project}).", settings.Org, settings.Project);
        return 0;
    }
}

internal sealed class SetPlatformLauncherSettings : CommandSettings
{
    [CommandArgument(0, "<PLATFORM>")]
    [Description("Platform: windows | macos | linux")]
    public required string Platform { get; init; }

    [CommandOption("--launch-command <CMD>")]
    [Description("Override review launch command template for this platform.")]
    public string? LaunchCommand { get; init; }

    [CommandOption("--terminal-program <PROGRAM>")]
    [Description("Terminal host executable (e.g. wt.exe, osascript, gnome-terminal). Empty string clears.")]
    public string? TerminalProgram { get; init; }

    [CommandOption("--terminal-args <ARGS>")]
    [Description("Arguments template for terminal program. Empty string clears.")]
    public string? TerminalArgs { get; init; }

    [CommandOption("--terminal-raw <CMD>")]
    [Description("Raw shell command override. Empty string clears.")]
    public string? TerminalRaw { get; init; }

    [CommandOption("--keep-open <BOOL>")]
    [Description("Whether terminal should remain open after completion (true/false).")]
    public bool? KeepOpen { get; init; }

    [CommandOption("--config <PATH>")]
    public string? ConfigPath { get; init; }
}

internal sealed class SetPlatformLauncherCommand : AsyncCommand<SetPlatformLauncherSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, SetPlatformLauncherSettings settings, CancellationToken cancellationToken)
    {
        var log = CliLoggerFactory.Instance.CreateLogger<SetPlatformLauncherCommand>();
        if (!PlatformKindDetector.TryParse(settings.Platform, out var platform))
        {
            AnsiConsole.MarkupLine($"[red]Invalid platform:[/] {Markup.Escape(settings.Platform)}");
            AnsiConsole.MarkupLine("[grey]Expected: windows | macos | linux[/]");
            log.LogWarning("CLI set-platform-launcher rejected invalid platform {Platform}.", settings.Platform);
            return 1;
        }

        var update = new PlatformLauncherOverrideUpdate(
            LaunchCommand: settings.LaunchCommand,
            TerminalProgram: settings.TerminalProgram,
            TerminalArgsTemplate: settings.TerminalArgs,
            TerminalRawCommand: settings.TerminalRaw,
            KeepTerminalOpen: settings.KeepOpen);

        if (update is { LaunchCommand: null, TerminalProgram: null, TerminalArgsTemplate: null, TerminalRawCommand: null, KeepTerminalOpen: null })
        {
            AnsiConsole.MarkupLine("[yellow]No platform launcher fields specified.[/]");
            log.LogWarning("CLI set-platform-launcher rejected request with no update fields for platform {Platform}.", platform);
            return 1;
        }

        var svc = new PrInbox.Core.Config.ConfigService(configPath: settings.ConfigPath);
        await svc.SetPlatformLauncherOverrideAsync(platform, update, cancellationToken);
        AnsiConsole.MarkupLine($"[green]Updated platform launcher[/] for [cyan]{platform}[/].");
        log.LogInformation("CLI set-platform-launcher completed for {Platform}.", platform);
        return 0;
    }
}
