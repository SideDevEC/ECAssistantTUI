namespace ECAssistant.Interfaces;

public record ProcessResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    bool TimedOut
);