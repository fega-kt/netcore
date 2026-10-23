using DotNetEnv;
using FileConverter;
using FileConverter.Services;
using Microsoft.AspNetCore.Http.Features;
using Syncfusion.Licensing;

Env.Load();
// Chạy local: lấy secret từ Vault theo .vault.json (không ghi .env); server không có file này nên bỏ qua
VaultEnv.Load();

var builder = WebApplication.CreateBuilder(args);

var syncfusionKey = Environment.GetEnvironmentVariable("SYNCFUSION_LICENSE_KEY") ?? "";
if (!string.IsNullOrEmpty(syncfusionKey))
    SyncfusionLicenseProvider.RegisterLicense(syncfusionKey);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "File Converter API", Version = "v1" });
});

builder.Services.AddScoped<IConversionService, ConversionService>();

// Request = tệp + phần bọc multipart: cả Kestrel lẫn bộ đọc form (mặc định 128 MB) phải nhận được tệp sát trần MAX_FILE_MB
builder.WebHost.ConfigureKestrel(opts =>
{
    opts.Limits.MaxRequestBodySize = ConverterLimits.MaxRequestBytes;
});
builder.Services.Configure<FormOptions>(opts =>
{
    opts.MultipartBodyLengthLimit = ConverterLimits.MaxRequestBytes;
});

var app = builder.Build();

var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
if (string.IsNullOrEmpty(syncfusionKey))
    startupLogger.LogWarning("[33mSYNCFUSION_LICENSE_KEY not set — PDF output may contain watermark.[0m");
else
    startupLogger.LogInformation("[32mSyncfusion license registered.[0m");

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "File Converter API v1"));

app.Use(async (ctx, next) =>
{
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var forwarded = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault();
    var clientIp = forwarded?.Split(',')[0].Trim() ?? ip;
    var ua = ctx.Request.Headers.UserAgent.ToString() is { Length: > 0 } s ? s : "-";
    var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("{Method} {Path} — ip={IP} ua={UA}",
        ctx.Request.Method, ctx.Request.Path, clientIp, ua);
    await next();
});

// Tệp quá lớn: báo 413 ngay theo Content-Length, không đợi nhận hết body rồi mới lỗi đọc form (400 chung chung)
app.Use(async (ctx, next) =>
{
    if (ctx.Request.ContentLength > ConverterLimits.MaxRequestBytes)
    {
        ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        await ctx.Response.WriteAsJsonAsync(new { error = $"File exceeds maximum allowed size of {ConverterLimits.MaxFileBytes / 1024 / 1024} MB." });
        return;
    }
    await next();
});

app.UseHttpsRedirection();
app.MapControllers();

app.Run();
