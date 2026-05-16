using FileConverter.Models;
using iText.IO.Image;
using iText.Kernel.Pdf;
using iText.Layout;
using PuppeteerSharp;
using PuppeteerSharp.Media;
using Syncfusion.DocIO.DLS;
using Syncfusion.DocIORenderer;
using Syncfusion.Presentation;
using Syncfusion.PresentationRenderer;
using Syncfusion.XlsIO;
using Syncfusion.XlsIORenderer;

namespace FileConverter.Services;

public class ConversionService(ILogger<ConversionService> logger) : IConversionService
{
    private static readonly HashSet<string> WordExtensions = [".docx", ".doc"];
    private static readonly HashSet<string> ExcelExtensions = [".xlsx", ".xls"];
    private static readonly HashSet<string> PowerPointExtensions = [".pptx", ".ppt"];
    private static readonly HashSet<string> ImageExtensions = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".webp"];

    // Magic bytes: ZIP (docx/xlsx/pptx), OLE2 (doc/xls/ppt), PDF, images
    private static readonly byte[] SigZip  = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] SigOle2 = [0xD0, 0xCF, 0x11, 0xE0];
    private static readonly byte[] SigPdf  = [0x25, 0x50, 0x44, 0x46]; // %PDF
    private static readonly byte[] SigPng  = [0x89, 0x50, 0x4E, 0x47];
    private static readonly byte[] SigJpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] SigGif  = [0x47, 0x49, 0x46, 0x38]; // GIF8
    private static readonly byte[] SigBmp  = [0x42, 0x4D];
    private static readonly byte[] SigTiff1 = [0x49, 0x49, 0x2A, 0x00]; // little-endian
    private static readonly byte[] SigTiff2 = [0x4D, 0x4D, 0x00, 0x2A]; // big-endian
    private static readonly byte[] SigWebp = [0x52, 0x49, 0x46, 0x46]; // RIFF (cần check thêm offset 8)

    private static async Task<byte[]> ReadHeaderAsync(IFormFile file, int count = 12)
    {
        var buf = new byte[count];
        await using var stream = file.OpenReadStream();
        _ = await stream.ReadAsync(buf);
        return buf;
    }

    private static bool StartsWith(byte[] header, byte[] sig) =>
        header.Length >= sig.Length && header.AsSpan(0, sig.Length).SequenceEqual(sig);

    private static bool IsValidMagic(byte[] header, string ext) => ext switch
    {
        ".docx" or ".xlsx" or ".pptx" => StartsWith(header, SigZip),
        ".doc"  or ".xls"  or ".ppt"  => StartsWith(header, SigOle2),
        ".pdf"  => StartsWith(header, SigPdf),
        ".png"  => StartsWith(header, SigPng),
        ".jpg" or ".jpeg" => StartsWith(header, SigJpeg),
        ".gif"  => StartsWith(header, SigGif),
        ".bmp"  => StartsWith(header, SigBmp),
        ".tiff" => StartsWith(header, SigTiff1) || StartsWith(header, SigTiff2),
        // WebP: RIFF ở 0-3, WEBP ở 8-11
        ".webp" => StartsWith(header, SigWebp) && header.AsSpan(8, 4).SequenceEqual("WEBP"u8),
        ".html" or ".htm" => true, // HTML là text, không có magic bytes cố định
        _ => false
    };

    public async Task<ConversionResult> ConvertToPdfAsync(IFormFile file, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var header = await ReadHeaderAsync(file);

        if (!IsValidMagic(header, ext))
        {
            logger.LogWarning("Magic bytes mismatch: {File} declared as {Ext}", file.FileName, ext);
            return new ConversionResult { Success = false, ErrorMessage = $"File content does not match extension '{ext}'." };
        }

        if (ext == ".pdf")
            return await PassThroughAsync(file, ct);

        if (WordExtensions.Contains(ext))
            return await ConvertWordAsync(file, ct);

        if (ExcelExtensions.Contains(ext))
            return await ConvertExcelAsync(file, ct);

        if (PowerPointExtensions.Contains(ext))
            return await ConvertPowerPointAsync(file, ct);

        if (ImageExtensions.Contains(ext))
            return await ConvertImageAsync(file, ct);

        if (ext is ".html" or ".htm")
        {
            using var reader = new StreamReader(file.OpenReadStream());
            var html = await reader.ReadToEndAsync(ct);
            return await ConvertHtmlToPdfAsync(html, ct);
        }

        return new ConversionResult { Success = false, ErrorMessage = $"Unsupported file type: {ext}" };
    }

    public async Task<ConversionResult> ConvertHtmlToPdfAsync(string htmlContent, CancellationToken ct = default)
    {
        try
        {
            var executablePath = Environment.GetEnvironmentVariable("PUPPETEER_EXECUTABLE_PATH");
            if (string.IsNullOrEmpty(executablePath))
                await new BrowserFetcher().DownloadAsync();

            await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                Headless = true,
                ExecutablePath = executablePath,
                Args = ["--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage"]
            });
            await using var page = await browser.NewPageAsync();
            await page.SetContentAsync(htmlContent, new NavigationOptions { WaitUntil = [WaitUntilNavigation.Networkidle0] });
            var pdfBytes = await page.PdfDataAsync(new PdfOptions { Format = PaperFormat.A4, PrintBackground = true });
            return new ConversionResult { Success = true, PdfBytes = pdfBytes, FileName = "converted.pdf" };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "HTML to PDF failed");
            return new ConversionResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private static async Task<ConversionResult> PassThroughAsync(IFormFile file, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return new ConversionResult { Success = true, PdfBytes = ms.ToArray(), FileName = file.FileName };
    }

    private async Task<ConversionResult> ConvertWordAsync(IFormFile file, CancellationToken ct)
    {
        try
        {
            using var inputMs = new MemoryStream();
            await file.CopyToAsync(inputMs, ct);
            inputMs.Position = 0;

            using var doc = new WordDocument(inputMs, Syncfusion.DocIO.FormatType.Automatic);
            using var renderer = new DocIORenderer();
            using var pdf = renderer.ConvertToPDF(doc);
            using var outputMs = new MemoryStream();
            pdf.Save(outputMs);

            return Success(file, outputMs);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Word to PDF failed: {File}", file.FileName);
            return new ConversionResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private async Task<ConversionResult> ConvertExcelAsync(IFormFile file, CancellationToken ct)
    {
        try
        {
            using var inputMs = new MemoryStream();
            await file.CopyToAsync(inputMs, ct);
            inputMs.Position = 0;

            using var engine = new ExcelEngine();
            var application = engine.Excel;
            application.DefaultVersion = ExcelVersion.Xlsx;
            var workbook = application.Workbooks.Open(inputMs);
            logger.LogInformation("Excel opened: {Sheets} sheets, {Rows} rows (sheet 1)", workbook.Worksheets.Count, workbook.Worksheets[0].UsedRange.LastRow);

            foreach (var sheet in workbook.Worksheets)
                sheet.EnableSheetCalculations();

            var renderer = new XlsIORenderer();
            using var pdf = renderer.ConvertToPDF(workbook);
            using var outputMs = new MemoryStream();
            pdf.Save(outputMs);

            return Success(file, outputMs);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Excel to PDF failed: {File}", file.FileName);
            return new ConversionResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private async Task<ConversionResult> ConvertPowerPointAsync(IFormFile file, CancellationToken ct)
    {
        try
        {
            using var inputMs = new MemoryStream();
            await file.CopyToAsync(inputMs, ct);
            inputMs.Position = 0;

            using var presentation = Presentation.Open(inputMs);
            using var pdf = PresentationToPdfConverter.Convert(presentation);
            using var outputMs = new MemoryStream();
            pdf.Save(outputMs);

            return Success(file, outputMs);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PowerPoint to PDF failed: {File}", file.FileName);
            return new ConversionResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private async Task<ConversionResult> ConvertImageAsync(IFormFile file, CancellationToken ct)
    {
        try
        {
            using var inputMs = new MemoryStream();
            await file.CopyToAsync(inputMs, ct);

            using var outputMs = new MemoryStream();
            using (var writer = new PdfWriter(outputMs))
            {
                writer.SetCloseStream(false);
                using var pdf = new PdfDocument(writer);
                using var doc = new iText.Layout.Document(pdf);
                var imageData = ImageDataFactory.Create(inputMs.ToArray());
                var image = new iText.Layout.Element.Image(imageData).SetAutoScale(true);
                doc.Add(image);
            }

            return Success(file, outputMs);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Image to PDF failed: {File}", file.FileName);
            return new ConversionResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private static ConversionResult Success(IFormFile file, MemoryStream ms) => new()
    {
        Success = true,
        PdfBytes = ms.ToArray(),
        FileName = Path.GetFileNameWithoutExtension(file.FileName) + ".pdf"
    };
}
