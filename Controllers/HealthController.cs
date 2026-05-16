using FileConverter.Models;
using Microsoft.AspNetCore.Mvc;

namespace FileConverter.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController(AppInfo appInfo) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Get() => Ok(new
    {
        result = new
        {
            appInfo.Commit,
            appInfo.Author,
            appInfo.Branch,
            appInfo.Message,
            buildTime = appInfo.BuildTime,
            uptime = (DateTime.UtcNow - appInfo.StartedAt).TotalSeconds,
            timestamp = DateTime.UtcNow,
        }
    });
}
