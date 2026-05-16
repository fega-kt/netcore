using Microsoft.AspNetCore.Mvc;

namespace FileConverter.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    private static string Env(string key) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : "unknown";

    private static readonly DateTime StartedAt = DateTime.UtcNow;

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Get() => Ok(new
    {
        result = new
        {
            commit = Env("GIT_COMMIT"),
            author = Env("GIT_AUTHOR"),
            branch = Env("GIT_BRANCH"),
            message = Env("GIT_MESSAGE"),
            buildTime = Env("BUILD_TIME"),
        }
    });
}
