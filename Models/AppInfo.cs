namespace FileConverter.Models;

public record AppInfo(
    DateTime StartedAt,
    DateTime BuildTime,
    string Commit,
    string Author,
    string Message,
    string Branch
);
