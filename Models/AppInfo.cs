namespace FileConverter.Models;

public record AppInfo(
    DateTime StartedAt,
    string Commit,
    string Author,
    string Message,
    string Branch
);
