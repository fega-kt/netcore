using FileConverter.Models;

namespace FileConverter.Services;

public interface IConversionService
{
    Task<ConversionResult> ConvertToPdfAsync(IFormFile file, CancellationToken ct = default);
    Task<ConversionResult> ConvertHtmlToPdfAsync(string htmlContent, CancellationToken ct = default);
}
