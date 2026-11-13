// teams-channel-notes.exe: every command, with output. watch echoes its log lines to the console. Output is
// Unicode to a console and UTF-8 when redirected, as the Python version's was (see ConsoleText).
var output = WinBackground.ConsoleText.Out;
return TeamsChannelNotes.Cli.Run(args, new TeamsChannelNotes.CliHost
{
    Out = output,
    Err = WinBackground.ConsoleText.Error,
    LogEcho = output,
});
