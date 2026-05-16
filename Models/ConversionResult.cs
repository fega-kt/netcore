namespace FileConverter.Models;

public class ConversionResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public byte[]? PdfBytes { get; init; }
    public string? FileName { get; init; }
}
