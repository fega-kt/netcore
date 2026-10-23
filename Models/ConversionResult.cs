namespace FileConverter.Models;

public class ConversionResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    /// <summary>PDF nhỏ dựng sẵn trong RAM (HTML)</summary>
    public byte[]? PdfBytes { get; init; }
    /// <summary>PDF ghi ra tệp tạm (Word / Excel / PowerPoint / ảnh / PDF): controller trả dạng luồng rồi xoá tệp</summary>
    public string? PdfPath { get; init; }
    public string? FileName { get; init; }
}
