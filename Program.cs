using DotNetEnv;
using FileConverter.Models;
using FileConverter.Services;
using Syncfusion.Licensing;

Env.Load();
Env.Load("buildinfo.env");
var startedAt = DateTime.UtcNow;

var appInfo = new AppInfo(
    StartedAt: startedAt,
    Commit: env("GIT_COMMIT") ?? env("RENDER_GIT_COMMIT") ?? "local",
    Author: env("GIT_AUTHOR") ?? "unknown",
    Branch: env("GIT_BRANCH") ?? env("RENDER_GIT_BRANCH") ?? "local",
    Message: env("GIT_MESSAGE") ?? "unknown",
    BuildTime: env("BUILD_TIME") is { } bt && DateTime.TryParse(bt, out var bdt) ? bdt : startedAt
);

static string? env(string key) =>
    Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : null;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(appInfo);

// Đăng ký Syncfusion license — lấy miễn phí tại syncfusion.com/products/communitylicense
var syncfusionKey = Environment.GetEnvironmentVariable("SYNCFUSION_LICENSE_KEY") ?? "";

if (!string.IsNullOrEmpty(syncfusionKey))
    SyncfusionLicenseProvider.RegisterLicense(syncfusionKey);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "File Converter API", Version = "v1" });
});

builder.Services.AddHealthChecks();
builder.Services.AddScoped<IConversionService, ConversionService>();

builder.WebHost.ConfigureKestrel(opts =>
{
    opts.Limits.MaxRequestBodySize = 100 * 1024 * 1024; // 100 MB
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "File Converter API v1"));

app.UseHttpsRedirection();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
