// teams-channel-notes.exe: every command, with output. watch echoes its log lines to the console.
return TeamsChannelNotes.Cli.Run(args, new TeamsChannelNotes.CliHost { LogEcho = Console.Out });
