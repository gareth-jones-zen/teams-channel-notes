using PyCompat;
using System.ComponentModel;
using System.Reflection;
using M365Graph;
using WinBackground;

namespace TeamsChannelSync;

/// <summary>Everything the command line touches outside itself, so tests can give it fakes.</summary>
public sealed class CliHost
{
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Err { get; init; } = Console.Error;

    /// <summary>Where watch's log lines are echoed (the console exe: stdout; the background exe: nowhere).</summary>
    public TextWriter? LogEcho { get; init; }

    public Func<string, string?> Env { get; init; } = Config.Environment;

    /// <summary>client(config, interactive): Graph for these settings.</summary>
    public Func<Config, bool?, IGraph> Graph { get; init; } = (config, interactive) => M365GraphClient.For(config, interactive);

    public Func<string, ITask> Tasks { get; init; } = Autostart.Create;
    public string ProgramDir { get; init; } = AppContext.BaseDirectory;
    public WatchHost? Watch { get; init; }
}

/// <summary>teams-channel-sync: keep a folder of markdown notes in step with a Teams channel's posts. The same
/// commands, options and exit codes as the Python version: 0; 2 for a sign-in needed, no settings file, settings
/// that can't be used, a lock timeout, or a usage error; 1 from `check` when anything differs (and for anything
/// unexpected); 3 from `watch` when another watcher is running.</summary>
public static class Cli
{
    public const string Prog = "teams-channel-sync";

    private const string Description = """
        teams-channel-sync: keep a folder of markdown notes in step with a Teams channel's posts.

            teams-channel-sync config             create the settings file (if missing) and say where it is
            teams-channel-sync sign-in            sign in to Microsoft (once; opens a browser)
            teams-channel-sync channels           your teams and channels, with the ids a source needs
            teams-channel-sync ids "<link>"       the same ids, from a channel's Copy link (no sign-in)
            teams-channel-sync sync               write new posts, and posts edited since they were filed
            teams-channel-sync sync --dry-run     say what it would write
            teams-channel-sync sync --hook        then run the settings' after_run command, if anything was written
            teams-channel-sync check              compare the recent posts with the files, line by line
            teams-channel-sync adopt              take on files that were copied in by hand, as they stand
            teams-channel-sync watch              stay running, and sync every watch_every_minutes
            teams-channel-sync autostart on       run the watcher at every logon, and start it now (off, status)
        """;

    private static readonly string[] Commands = ["config", "sign-in", "channels", "ids", "sync", "check", "adopt", "watch", "autostart"];

    private const string Usage = "usage: teams-channel-sync [-h] [--version] [--config CONFIG]\n"
        + "                          {config,sign-in,channels,ids,sync,check,adopt,watch,autostart} ...";

    public static string Version =>
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    private sealed class UsageError(string message) : Exception(message);

    private sealed record Args(string? Config, string Command, string? Source, bool Json, bool DryRun, bool Hook, string? Link, string? Action);

    public static int Run(string[] argv, CliHost host)
    {
        Args args;
        try
        {
            var parsed = Parse(argv, host);
            if (parsed is null)
                return 0;   // --help or --version
            args = parsed;
        }
        catch (UsageError error)
        {
            Print(host.Err, $"{Usage}\n{Prog}: error: {error.Message}");
            return 2;
        }

        try
        {
            switch (args.Command)
            {
                case "config":
                    Print(host.Out, !string.IsNullOrEmpty(args.Config) ? args.Config : TeamsChannelSync.Config.CreateDefault(host.Env));
                    return 0;
                case "ids":
                    try
                    {
                        var (team, channel) = Notes.FromLink(args.Link!);
                        Print(host.Out, $"team = \"{team}\"\nchannel = \"{channel}\"");
                        return 0;
                    }
                    catch (PyValueError error)
                    {
                        Print(host.Err, error.Message);
                        return 2;
                    }
                case "watch":
                    var watchHost = host.Watch ?? new WatchHost
                    {
                        Logger = config => Logger.Create(config.LogFile, host.LogEcho),
                    };
                    return TeamsChannelSync.Watch.Run(args.Config, watchHost, host.Env);
                case "autostart":
                    return AutostartCommand(args, host);
            }

            var settings = TeamsChannelSync.Config.Load(args.Config, host.Env);
            switch (args.Command)
            {
                case "sign-in":
                    host.Graph(settings, true).Token(TeamsChannelSync.Config.Scopes);
                    Print(host.Out, "Signed in.");
                    return 0;
                case "channels":
                    Notes.Channels(host.Graph(settings, true), text => Print(host.Out, text));
                    return 0;
                case "check":
                    return Notes.Check(settings, host.Graph(settings, null), text => Print(host.Out, text));
            }
            var log = args.Json ? (Action<string>)(text => Print(host.Err, text)) : text => Print(host.Out, text);
            var report = TeamsChannelSync.Run.Once(settings, host.Graph(settings, null), args.DryRun, args.Command == "adopt",
                args.Hook, args.Source, log);
            Print(host.Out, args.Json ? report.ToJson() : report.Summary());
            return 0;
        }
        catch (Exception error) when (error is SignInNeededException or FileNotFoundException or PyValueError or TimeoutException)
        {
            Print(host.Err, error.Message);
            return 2;
        }
        catch (Exception error)
        {
            Print(host.Err, $"{error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    private static int AutostartCommand(Args args, CliHost host)
    {
        var task = host.Tasks(Autostart.TaskName);
        try
        {
            if (args.Action == "on")
            {
                var config = TeamsChannelSync.Config.Load(args.Config, host.Env);   // check the settings load before scheduling them
                var (exe, taskArgs) = Autostart.Command(host.ProgramDir, config.Path);
                var command = task.Enable(exe, taskArgs, Path.GetDirectoryName(config.Path));
                Print(host.Out, $"Autostart is on: '{Autostart.TaskName}' runs at every logon, and has started now.\n  {command}\n"
                                + $"  log: {config.LogFile}");
            }
            else if (args.Action == "off")
                Print(host.Out, task.Disable() ? "Autostart is off." : "Autostart was already off.");
            else
            {
                var info = task.Status();
                if (info is null)
                    Print(host.Out, "Autostart is off.");
                else
                {
                    Print(host.Out, $"Autostart is on ('{Autostart.TaskName}').");
                    foreach (var key in new[] { "state", "last_run", "last_result", "command" })
                        Print(host.Out, $"  {key.Replace('_', ' '),-12} {info.GetValueOrDefault(key, "")}");
                }
            }
            return 0;
        }
        catch (Exception error) when (error is InvalidOperationException or FileNotFoundException or PyValueError or Win32Exception)
        {
            Print(host.Err, error.Message);
            return 2;
        }
    }

    /// <summary>print(): a line, with "\n" written as "\r\n" as Python's stdout does on Windows.</summary>
    private static void Print(TextWriter writer, string text)
    {
        try
        {
            writer.Write(PyStr.WindowsNewlines(text + "\n"));
            writer.Flush();
        }
        catch (IOException)
        {
            // a closed console mustn't stop the tool
        }
    }

    // ---- argparse, as far as these commands use it ------------------------------------------------------------

    private static Args? Parse(string[] argv, CliHost host)
    {
        string? config = null;
        var i = 0;
        string[] mainOptions = ["--help", "--version", "--config"];
        while (i < argv.Length && argv[i].StartsWith('-') && argv[i] != "-")
        {
            var (name, value) = Option(argv[i], mainOptions, "-h");
            switch (name)
            {
                case "--help":
                    Print(host.Out, $"{Usage}\n\n{Description}");
                    return null;
                case "--version":
                    Print(host.Out, Version);
                    return null;
                case "--config":
                    config = value ?? (i + 1 < argv.Length && !IsOption(argv[i + 1]) ? argv[++i] : throw new UsageError("argument --config: expected one argument"));
                    break;
            }
            i++;
        }
        if (i >= argv.Length)
            throw new UsageError("the following arguments are required: command");
        var command = argv[i++];
        if (!Commands.Contains(command))
            throw new UsageError($"argument command: invalid choice: '{command}' (choose from {string.Join(", ", Commands)})");

        string[] options = command switch
        {
            "sync" => ["--help", "--source", "--json", "--dry-run", "--hook"],
            "check" => ["--help", "--source"],
            "adopt" => ["--help", "--source", "--json"],
            _ => ["--help"],
        };
        string? source = null, positional = null;
        bool json = false, dryRun = false, hook = false;
        var unrecognised = new List<string>();
        var onlyPositional = false;
        for (; i < argv.Length; i++)
        {
            var arg = argv[i];
            if (!onlyPositional && arg == "--")
            {
                onlyPositional = true;
                continue;
            }
            if (!onlyPositional && IsOption(arg))
            {
                string name;
                string? value;
                try
                {
                    (name, value) = Option(arg, options, "-h");
                }
                catch (UsageError) when (!arg.StartsWith("--", StringComparison.Ordinal) || !options.Any(o => o.StartsWith(arg.Split('=')[0], StringComparison.Ordinal)))
                {
                    unrecognised.Add(arg);
                    continue;
                }
                switch (name)
                {
                    case "--help":
                        Print(host.Out, $"usage: {Prog} {command} [-h]{OptionsUsage(command)}");
                        return null;
                    case "--source":
                        source = value ?? (i + 1 < argv.Length && !IsOption(argv[i + 1]) ? argv[++i] : throw new UsageError("argument --source: expected one argument"));
                        break;
                    case "--json": json = Flag(value, name); break;
                    case "--dry-run": dryRun = Flag(value, name); break;
                    case "--hook": hook = Flag(value, name); break;
                }
                continue;
            }
            if (command is "ids" or "autostart" && positional is null)
                positional = arg;
            else
                unrecognised.Add(arg);
        }
        if (command == "ids" && positional is null)
            throw new UsageError("the following arguments are required: link");
        if (command == "autostart")
        {
            if (positional is null)
                throw new UsageError("the following arguments are required: action");
            if (positional is not ("on" or "off" or "status"))
                throw new UsageError($"argument action: invalid choice: '{positional}' (choose from on, off, status)");
        }
        if (unrecognised.Count > 0)
            throw new UsageError($"unrecognized arguments: {string.Join(' ', unrecognised)}");
        return new Args(config, command, source, json, dryRun, hook, command == "ids" ? positional : null, command == "autostart" ? positional : null);
    }

    private static string OptionsUsage(string command) => command switch
    {
        "sync" => " [--source SOURCE] [--json] [--dry-run] [--hook]",
        "check" => " [--source SOURCE]",
        "adopt" => " [--source SOURCE] [--json]",
        "ids" => " link",
        "autostart" => " {on,off,status}",
        _ => "",
    };

    // argparse treats "-5" as a value, not an option, when no option looks like a number
    private static bool IsOption(string arg) => arg.Length > 1 && arg[0] == '-' && !(arg.Length > 1 && char.IsAsciiDigit(arg[1]));

    private static bool Flag(string? value, string name) =>
        value is null ? true : throw new UsageError($"argument {name}: ignored explicit argument '{value}'");

    /// <summary>An option and its "=value", with argparse's unambiguous prefixes ("--dry" for "--dry-run").</summary>
    private static (string Name, string? Value) Option(string arg, string[] known, string shortHelp)
    {
        if (arg == shortHelp)
            return ("--help", null);
        var eq = arg.IndexOf('=');
        var name = eq >= 0 ? arg[..eq] : arg;
        var value = eq >= 0 ? arg[(eq + 1)..] : null;
        if (known.Contains(name))
            return (name, value);
        if (name.StartsWith("--", StringComparison.Ordinal))
        {
            var matches = known.Where(k => k.StartsWith(name, StringComparison.Ordinal)).ToList();
            if (matches.Count == 1)
                return (matches[0], value);
            if (matches.Count > 1)
                throw new UsageError($"ambiguous option: {name} could match {string.Join(", ", matches)}");
        }
        throw new UsageError($"unrecognized arguments: {arg}");
    }
}
