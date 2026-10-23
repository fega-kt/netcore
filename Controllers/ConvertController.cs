using FileConverter.Services;
using Microsoft.AspNetCore.Mvc;

namespace FileConverter.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConvertController(IConversionService conversionService, ILogger<ConvertController> logger) : ControllerBase
{
    /// <summary>Convert an uploaded file to PDF.</summary>
    /// <remarks>
    /// Supported formats: .docx, .doc, .xlsx, .xls, .pptx, .ppt, .html, .htm,
    /// .jpg, .jpeg, .png, .gif, .bmp, .tiff, .webp, .pdf.
    /// Max file size: MAX_FILE_MB (default 200 MB) — larger files get 413.
    /// </remarks>
    [HttpPost("file")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> ConvertFile(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "No file provided." });

        if (file.Length > ConverterLimits.MaxFileBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                new { error = $"File exceeds maximum allowed size of {ConverterLimits.MaxFileBytes / 1024 / 1024} MB." });

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var sizeKb = file.Length / 1024.0;
        logger.LogInformation("Convert started: {FileName} | type={Ext} | size={SizeKB:F1} KB", file.FileName, ext, sizeKb);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await conversionService.ConvertToPdfAsync(file, ct);
        sw.Stop();

        if (!result.Success)
        {
            logger.LogWarning("Convert failed: {FileName} | {ElapsedMs} ms | {Error}", file.FileName, sw.ElapsedMilliseconds, result.ErrorMessage);
            return BadRequest(new { error = result.ErrorMessage });
        }

        var outputBytes = result.PdfPath is not null ? new FileInfo(result.PdfPath).Length : result.PdfBytes!.Length;
        logger.LogInformation("Convert done: {FileName} | {ElapsedMs} ms | output={OutputKB:F1} KB", file.FileName, sw.ElapsedMilliseconds, outputBytes / 1024.0);
        return PdfResult(result);
    }

    /// <summary>PDF trên tệp tạm: trả dạng luồng, tệp tự xoá khi gửi xong (DeleteOnClose); PDF trong RAM (HTML) trả thẳng</summary>
    private FileResult PdfResult(Models.ConversionResult result)
    {
        var name = result.FileName ?? "converted.pdf";
        if (result.PdfPath is null)
            return File(result.PdfBytes!, "application/pdf", name);
        var stream = new FileStream(result.PdfPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        return File(stream, "application/pdf", name);
    }

    /// <summary>Convert raw HTML content to PDF.</summary>
    [HttpPost("html")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ConvertHtml([FromBody] HtmlConvertRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Html))
            return BadRequest(new { error = "HTML content is required." });

        var result = await conversionService.ConvertHtmlToPdfAsync(request.Html, ct);

        if (!result.Success)
            return BadRequest(new { error = result.ErrorMessage });

        return File(result.PdfBytes!, "application/pdf", result.FileName ?? "converted.pdf");
    }
}

public record HtmlConvertRequest(string Html);
