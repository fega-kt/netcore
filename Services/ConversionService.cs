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

    /// <summary>Giới hạn số lượt chuyển chạy cùng lúc (Syncfusion dựng cả tài liệu trong RAM); lượt sau xếp hàng chờ</summary>
    private static readonly SemaphoreSlim ConversionGate = new(ConverterLimits.MaxConcurrentConversions);

    public async Task<ConversionResult> ConvertToPdfAsync(IFormFile file, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var header = await ReadHeaderAsync(file);

        if (!IsValidMagic(header, ext))
        {
            logger.LogWarning("Magic bytes mismatch: {File} declared as {Ext}", file.FileName, ext);
            return new ConversionResult { Success = false, ErrorMessage = $"File content does not match extension '{ext}'." };
        }

        await ConversionGate.WaitAsync(ct);
        try
        {
            return await ConvertGatedAsync(file, ext, ct);
        }
        finally
        {
            ConversionGate.Release();
        }
    }

    private async Task<ConversionResult> ConvertGatedAsync(IFormFile file, string ext, CancellationToken ct)
    {
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
        var path = NewTempPdfPath();
        await using (var output = File.Create(path))
            await file.CopyToAsync(output, ct);
        return new ConversionResult { Success = true, PdfPath = path, FileName = file.FileName };
    }

    /// <summary>
    /// Tệp lớn không giữ trong RAM: đầu vào đọc thẳng từ tệp tạm ASP.NET đã ghi khi nhận multipart,
    /// PDF ghi ra tệp tạm rồi controller trả dạng luồng (xoá khi gửi xong)
    /// </summary>
    private static string NewTempPdfPath() => Path.Combine(Path.GetTempPath(), $"fc-{Guid.NewGuid():N}.pdf");

    /// <summary>Ghi PDF ra tệp tạm; lỗi giữa chừng thì xoá tệp dở</summary>
    private static string SavePdfToTemp(Action<Stream> save)
    {
        var path = NewTempPdfPath();
        try
        {
            using var output = File.Create(path);
            save(output);
            return path;
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    // Font Microsoft không có trong container Linux → font cùng số đo (chữ không xô dòng) đã cài trong image.
    // Không khai báo thì Syncfusion tự chọn một font thiếu chữ tiếng Việt (mất ơ, ư, chữ dính nhau)
    // Chỉ dùng font đủ chữ tiếng Việt: Liberation 2.x (gói fonts-liberation2, KHÔNG phải fonts-liberation 1.07),
    // Carlito, DejaVu, Noto. Caladea và bộ URW (P052, C059, URW Bookman/Gothic…) thiếu ả, ạ, ế… nên không dùng:
    // ký tự thiếu bị vẽ bằng font khác cỡ chữ → chữ dính, hở, dấu lệch
    private static readonly Dictionary<string, string> FontSubstitutes = BuildFontSubstitutes(
        // Có chân (serif)
        ("Liberation Serif", ["Times New Roman", "Times"]),
        ("Noto Serif", ["Cambria", "Cambria Math", "Georgia", "Garamond", "Constantia", "Palatino Linotype", "Palatino", "Book Antiqua",
            "Century Schoolbook", "Century", "Bookman Old Style", "Bookman", "Monotype Corsiva"]),
        // Không chân (sans)
        ("Liberation Sans", ["Arial", "Helvetica", "Arial Black", "Arial Narrow", "Microsoft Sans Serif"]),
        ("Carlito", ["Calibri", "Calibri Light"]),
        ("DejaVu Sans", ["Verdana", "Tahoma", "Trebuchet MS", "Lucida Sans Unicode", "Arial Unicode MS"]),
        ("Noto Sans", ["Segoe UI", "Aptos", "Aptos Display", "Aptos Narrow", "Century Gothic", "Candara", "Corbel", "Franklin Gothic Medium"]),
        // Đơn cách (mono)
        ("Liberation Mono", ["Courier New", "Courier", "Consolas", "Lucida Console"]),
        // Trung / Nhật / Hàn
        ("Noto Serif CJK SC", ["SimSun", "宋体", "NSimSun", "新宋体", "MingLiU", "PMingLiU", "新細明體", "MS Mincho", "MS PMincho", "Batang"]),
        ("Noto Sans CJK SC", ["SimHei", "黑体", "Microsoft YaHei", "微软雅黑", "Microsoft JhengHei", "MS Gothic", "MS PGothic", "Meiryo", "Yu Gothic", "Malgun Gothic", "Gulim", "Dotum"]));

    /// <summary>Font lạ không có trong bảng: theo tên đoán có chân hay không, còn lại dùng Noto Sans (đủ chữ tiếng Việt)</summary>
    private static string SubstituteFor(string fontName) =>
        FontSubstitutes.TryGetValue(fontName, out var name) ? name
        : fontName.Contains("Times", StringComparison.OrdinalIgnoreCase) || fontName.Contains("Serif", StringComparison.OrdinalIgnoreCase) && !fontName.Contains("Sans", StringComparison.OrdinalIgnoreCase) ? "Liberation Serif"
        : fontName.Contains("Mono", StringComparison.OrdinalIgnoreCase) || fontName.Contains("Courier", StringComparison.OrdinalIgnoreCase) ? "Liberation Mono"
        : "Noto Sans";

    private static Dictionary<string, string> BuildFontSubstitutes(params (string Substitute, string[] Originals)[] groups) =>
        groups.SelectMany(group => group.Originals.Select(original => (original, group.Substitute)))
            .ToDictionary(pair => pair.original, pair => pair.Substitute, StringComparer.OrdinalIgnoreCase);

    private static void UseLinuxFonts(Syncfusion.DocIO.DLS.FontSettings settings)
    {
        settings.SubstituteFont += (_, args) => args.AlternateFontName = SubstituteFor(args.OriginalFontName);
        // Ký tự font đang dùng không có thì lấy từ font có đủ chữ
        void Fallback(uint start, uint end, string font) => settings.FallbackFonts.Add(new Syncfusion.Office.FallbackFont(start, end, font));
        Fallback(0x0100, 0x024F, "Noto Sans"); // Latin mở rộng: ă, đ, ơ, ư…
        Fallback(0x0300, 0x036F, "Noto Sans"); // dấu tổ hợp
        Fallback(0x1E00, 0x1EFF, "Noto Sans"); // Latin mở rộng bổ sung: ạ, ế, ợ…
        Fallback(0x3000, 0x30FF, "Noto Sans CJK SC"); // dấu câu CJK, kana Nhật
        Fallback(0x4E00, 0x9FFF, "Noto Sans CJK SC"); // chữ Hán
        Fallback(0xAC00, 0xD7AF, "Noto Sans CJK SC"); // chữ Hàn
        Fallback(0xFF00, 0xFFEF, "Noto Sans CJK SC"); // ký tự toàn độ rộng
    }

    private async Task<ConversionResult> ConvertWordAsync(IFormFile file, CancellationToken ct)
    {
        try
        {
            await using var input = file.OpenReadStream();
            using var doc = new WordDocument(input, Syncfusion.DocIO.FormatType.Automatic);
            UseLinuxFonts(doc.FontSettings);
            // Nhúng font vào PDF: không nhúng thì trình xem tự thay font của nó → chữ lệch, hở
            using var renderer = new DocIORenderer { Settings = { EmbedFonts = true } };
            using var pdf = renderer.ConvertToPDF(doc);

            return Success(file, SavePdfToTemp(pdf.Save));
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
            await using var input = file.OpenReadStream();
            using var engine = new ExcelEngine();
            var application = engine.Excel;
            application.DefaultVersion = ExcelVersion.Xlsx;
            var workbook = application.Workbooks.Open(input);
            logger.LogInformation("Excel opened: {Sheets} sheets, {Rows} rows (sheet 1)", workbook.Worksheets.Count, workbook.Worksheets[0].UsedRange.LastRow);

            foreach (var sheet in workbook.Worksheets)
                sheet.EnableSheetCalculations();

            var renderer = new XlsIORenderer();
            using var pdf = renderer.ConvertToPDF(workbook);

            return Success(file, SavePdfToTemp(pdf.Save));
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
            await using var input = file.OpenReadStream();
            using var presentation = Presentation.Open(input);
            using var pdf = PresentationToPdfConverter.Convert(presentation);

            return Success(file, SavePdfToTemp(pdf.Save));
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
            // Ảnh: iText cần cả mảng byte của ảnh
            using var inputMs = new MemoryStream();
            await file.CopyToAsync(inputMs, ct);

            var path = SavePdfToTemp(output =>
            {
                using var writer = new PdfWriter(output);
                writer.SetCloseStream(false);
                using var pdf = new PdfDocument(writer);
                using var doc = new iText.Layout.Document(pdf);
                var imageData = ImageDataFactory.Create(inputMs.ToArray());
                var image = new iText.Layout.Element.Image(imageData).SetAutoScale(true);
                doc.Add(image);
            });

            return Success(file, path);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Image to PDF failed: {File}", file.FileName);
            return new ConversionResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private static ConversionResult Success(IFormFile file, string pdfPath) => new()
    {
        Success = true,
        PdfPath = pdfPath,
        FileName = Path.GetFileNameWithoutExtension(file.FileName) + ".pdf"
    };
}
