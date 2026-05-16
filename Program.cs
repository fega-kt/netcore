using DotNetEnv;
using FileConverter.Services;
using Syncfusion.Licensing;

Env.Load();

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

builder.Services.AddHealthChecks();
builder.Services.AddScoped<IConversionService, ConversionService>();

builder.WebHost.ConfigureKestrel(opts =>
{
    opts.Limits.MaxRequestBodySize = 100 * 1024 * 1024;
});

var app = builder.Build();

var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
if (string.IsNullOrEmpty(syncfusionKey))
    startupLogger.LogWarning("[33mSYNCFUSION_LICENSE_KEY not set — PDF output may contain watermark.[0m");
else
    startupLogger.LogInformation("[32mSyncfusion license registered.[0m");

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "File Converter API v1"));

app.UseHttpsRedirection();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
