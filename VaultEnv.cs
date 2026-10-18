using System.Net.Http.Json;
using System.Text.Json;

namespace FileConverter;

/// <summary>
/// Chạy local: lấy secret từ HashiCorp Vault theo <c>.vault.json</c> (giống backend) và nạp thẳng vào biến môi trường
/// của tiến trình — không ghi file .env. Môi trường lấy từ biến <c>VAULT_ENV</c> (launchSettings đặt sẵn), hoặc
/// môi trường duy nhất trong <c>envs</c>.
///
/// Token Vault lấy theo thứ tự: biến <c>VAULT_TOKEN</c> → token đã giữ trong RAM (VaultTokenCache, mất khi đăng xuất / tắt máy)
/// → hỏi trên console (dán token, hoặc đăng nhập userpass). Không bao giờ ghi token xuống đĩa.
///
/// Lấy được thì in dòng màu xanh; không lấy được (cấu hình sai, không có token, sai đường dẫn, không kết nối được) thì in
/// dòng màu đỏ và chạy tiếp — biến có thể đã có từ file .env (DotNetEnv).
/// Trên server không có <c>.vault.json</c> trong image (đã loại ở .dockerignore) nên bỏ qua; secret do up.sh ghi ra .env.
/// Biến đã có sẵn trong môi trường không bị ghi đè.
/// </summary>
public static class VaultEnv
{
    private const string ConfigFile = ".vault.json";
    private const string Green = "[32m";
    private const string Red = "[31m";
    private const string Reset = "[0m";

    private sealed record VaultConfig(string? Addr, Dictionary<string, string>? Envs);

    public static void Load()
    {
        // Chỉ chạy local (launchSettings đặt Development). Server / Docker chạy Production: secret lấy từ .env do up.sh ghi,
        // không gọi Vault — kể cả khi .vault.json lỡ lọt vào image
        if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") != "Development") return;

        var configPath = FindConfigFile();
        if (configPath is null)
        {
            Error($"Không tìm thấy {ConfigFile} (thư mục hiện tại: {Directory.GetCurrentDirectory()}) — bỏ qua Vault");
            return;
        }

        VaultConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<VaultConfig>(File.ReadAllText(configPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException e)
        {
            Error($"{configPath} không phải JSON hợp lệ: {e.Message}");
            return;
        }
        if (string.IsNullOrWhiteSpace(config?.Addr) || config.Envs is not { Count: > 0 })
        {
            Error($"{configPath} thiếu \"addr\" hoặc \"envs\"");
            return;
        }

        var envName = Environment.GetEnvironmentVariable("VAULT_ENV");
        if (string.IsNullOrEmpty(envName) && config.Envs.Count == 1) envName = config.Envs.Keys.First();
        if (string.IsNullOrEmpty(envName) || !config.Envs.TryGetValue(envName, out var secretPath))
        {
            Error($"VAULT_ENV phải là một trong: {string.Join(", ", config.Envs.Keys)}");
            return;
        }

        var addr = config.Addr.TrimEnd('/');
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            // ResolveToken tự log lý do khi không có token
            var token = ResolveToken(http, addr);
            if (token is null) return;

            var secrets = FetchSecrets(http, addr, secretPath, token);
            if (secrets is null && Environment.GetEnvironmentVariable("VAULT_TOKEN") is null && VaultTokenCache.Get(addr) is not null)
            {
                // Token đã lưu hết hạn / bị thu hồi: đăng nhập lại một lần
                VaultTokenCache.Remove(addr);
                token = ResolveToken(http, addr);
                if (token is not null) secrets = FetchSecrets(http, addr, secretPath, token);
            }
            if (secrets is null)
            {
                Error($"Không đọc được secret {secretPath} từ {addr} (sai đường dẫn, hoặc token không có quyền đọc)");
                return;
            }

            var loaded = 0;
            foreach (var (key, value) in secrets)
            {
                if (Environment.GetEnvironmentVariable(key) is not null) continue;
                Environment.SetEnvironmentVariable(key, value);
                loaded++;
            }
            Console.WriteLine($"{Green}[vault] {loaded} biến từ {secretPath} ({envName}){Reset}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            Error($"Không kết nối được Vault {addr}: {e.Message}");
        }
    }

    /// <summary>
    /// .vault.json ở thư mục hiện tại, hoặc ở thư mục project: dò ngược lên từ thư mục chứa file build (bin/Debug/net8.0)
    /// — chạy từ IDE / dotnet run --project thì thư mục hiện tại có thể không phải thư mục project.
    /// </summary>
    private static string? FindConfigFile()
    {
        var current = Path.Combine(Directory.GetCurrentDirectory(), ConfigFile);
        if (File.Exists(current)) return current;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ConfigFile);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? ResolveToken(HttpClient http, string addr)
    {
        var token = Environment.GetEnvironmentVariable("VAULT_TOKEN");
        if (!string.IsNullOrWhiteSpace(token)) return token.Trim();
        var cached = VaultTokenCache.Get(addr);
        if (!string.IsNullOrEmpty(cached)) return cached;

        // Không có console để hỏi (chạy trong IDE / bị chuyển hướng input): để Load báo lỗi
        if (Console.IsInputRedirected)
        {
            Error("Chưa có token Vault và không có console để đăng nhập (chạy trong IDE / input bị chuyển hướng): đặt VAULT_TOKEN, hoặc chạy bằng terminal");
            return null;
        }
        Console.WriteLine("[vault] Đăng nhập Vault bằng: 1) Token  2) Userpass");
        Console.Write("[vault] Chọn (1/2): ");
        token = Console.ReadLine()?.Trim() == "1" ? PromptToken(http, addr) : LoginUserpass(http, addr);
        // Giữ trong RAM để lần chạy sau (dotnet watch khởi động lại) không phải hỏi; tắt máy / đăng xuất là mất
        if (token is not null && !VaultTokenCache.Set(addr, token)) Error("Máy này không giữ được token trong RAM — lần sau sẽ hỏi lại");
        return token;
    }

    /// <summary>Dán token có sẵn; kiểm tra token còn dùng được (auth/token/lookup-self) trước khi lưu</summary>
    private static string? PromptToken(HttpClient http, string addr)
    {
        Console.Write("[vault] Token: ");
        var token = ReadPassword().Trim();
        if (string.IsNullOrEmpty(token))
        {
            Error("Chưa nhập token — bỏ qua Vault");
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{addr}/v1/auth/token/lookup-self");
        request.Headers.Add("X-Vault-Token", token);
        using var res = http.Send(request);
        if (res.IsSuccessStatusCode) return token;
        Error($"Token không hợp lệ hoặc đã hết hạn ({(int)res.StatusCode})");
        return null;
    }

    private static string? LoginUserpass(HttpClient http, string addr)
    {
        Console.Write("[vault] Username: ");
        var username = Console.ReadLine()?.Trim();
        Console.Write("[vault] Password: ");
        var password = ReadPassword();
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            Error("Chưa nhập username / password — bỏ qua Vault");
            return null;
        }

        var res = http.PostAsJsonAsync($"{addr}/v1/auth/userpass/login/{Uri.EscapeDataString(username)}", new { password }).GetAwaiter().GetResult();
        if (!res.IsSuccessStatusCode)
        {
            Error($"Đăng nhập Vault thất bại ({(int)res.StatusCode})");
            return null;
        }
        using var doc = JsonDocument.Parse(res.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        return doc.RootElement.GetProperty("auth").GetProperty("client_token").GetString();
    }

    /// <summary>Đường dẫn ghi kiểu backend (secret/data/...): KV v2 → secret nằm ở data.data</summary>
    private static Dictionary<string, string>? FetchSecrets(HttpClient http, string addr, string secretPath, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{addr}/v1/{secretPath.TrimStart('/')}");
        request.Headers.Add("X-Vault-Token", token);
        using var res = http.Send(request);
        if (!res.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(res.Content.ReadAsStream());
        var data = doc.RootElement.GetProperty("data");
        if (data.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Object) data = inner;
        return data.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText());
    }

    private static string ReadPassword()
    {
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); }
            else chars.Add(key.KeyChar);
        }
        Console.WriteLine();
        return new string(chars.ToArray());
    }

    private static void Error(string message) => Console.WriteLine($"{Red}[vault] {message}{Reset}");
}
