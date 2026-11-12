// teams-channel-notes-background.exe: no window, ever. What the logon task runs (`--config SETTINGS watch`);
// anything else runs as the console program would, with nothing shown.
return TeamsChannelNotes.Cli.Run(args, new TeamsChannelNotes.CliHost { Out = TextWriter.Null, Err = TextWriter.Null });
