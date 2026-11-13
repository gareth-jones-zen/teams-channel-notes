// teams-channel-sync-background.exe: no window, ever. What the logon task runs (`--config SETTINGS watch`);
// anything else runs as the console program would, with nothing shown.
return TeamsChannelSync.Cli.Run(args, new TeamsChannelSync.CliHost { Out = TextWriter.Null, Err = TextWriter.Null });
