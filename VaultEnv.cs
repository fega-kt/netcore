using System.Net.Http.Json;
using System.Text.Json;

namespace FileConverter;

/// <summary>
/// Chạy local: lấy secret từ HashiCorp Vault theo <c>.vault.json</c> (giống backend) và nạp thẳng vào biến môi trường
/// của tiến trình — không ghi file .env. Môi trường lấy từ biến <c>VAULT_ENV</c> (launchSettings đặt sẵn), hoặc
/// môi trường duy nhất trong <c>envs</c>.
///
/// Token Vault lấy theo thứ tự: biến <c>VAULT_TOKEN</c> → file <c>~/.vault-token</c> (cũng là file lệnh <c>vault login</c> ghi)
/// → hỏi trên console (dán token, hoặc đăng nhập userpass) rồi lưu token vào <c>~/.vault-token</c> để lần chạy sau (dotnet watch) không hỏi lại.
///
/// Trên server không có <c>.vault.json</c> trong image (đã loại ở .dockerignore) nên bỏ qua; secret do up.sh ghi ra .env.
/// Biến đã có sẵn trong môi trường không bị ghi đè.
/// </summary>
public static class VaultEnv
{
    private const string ConfigFile = ".vault.json";
    private static readonly string TokenFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vault-token");

    private sealed record VaultConfig(string? Addr, Dictionary<string, string>? Envs);

    public static void Load()
    {
        if (!File.Exists(ConfigFile)) return;

        VaultConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<VaultConfig>(File.ReadAllText(ConfigFile), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException e)
        {
            Warn($"{ConfigFile} không phải JSON hợp lệ ({e.Message}) — bỏ qua Vault");
            return;
        }
        if (string.IsNullOrWhiteSpace(config?.Addr) || config.Envs is not { Count: > 0 })
        {
            Warn($"{ConfigFile} thiếu \"addr\" hoặc \"envs\" — bỏ qua Vault");
            return;
        }

        var envName = Environment.GetEnvironmentVariable("VAULT_ENV");
        if (string.IsNullOrEmpty(envName) && config.Envs.Count == 1) envName = config.Envs.Keys.First();
        if (string.IsNullOrEmpty(envName) || !config.Envs.TryGetValue(envName, out var secretPath))
        {
            Warn($"Đặt VAULT_ENV là một trong: {string.Join(", ", config.Envs.Keys)} — bỏ qua Vault");
            return;
        }

        var addr = config.Addr.TrimEnd('/');
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            var token = ResolveToken(http, addr);
            if (token is null) return;

            var secrets = FetchSecrets(http, addr, secretPath, token);
            if (secrets is null && Environment.GetEnvironmentVariable("VAULT_TOKEN") is null && File.Exists(TokenFile))
            {
                // Token đã lưu hết hạn / bị thu hồi: đăng nhập lại một lần
                File.Delete(TokenFile);
                token = ResolveToken(http, addr);
                if (token is not null) secrets = FetchSecrets(http, addr, secretPath, token);
            }
            if (secrets is null)
            {
                Warn($"Không đọc được secret {secretPath} (token / quyền / đường dẫn?)");
                return;
            }

            var loaded = 0;
            foreach (var (key, value) in secrets)
            {
                if (Environment.GetEnvironmentVariable(key) is not null) continue;
                Environment.SetEnvironmentVariable(key, value);
                loaded++;
            }
            Console.WriteLine($"[vault] {loaded} biến từ {secretPath} ({envName})");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            Warn($"Không kết nối được Vault {addr}: {e.Message}");
        }
    }

    private static string? ResolveToken(HttpClient http, string addr)
    {
        var token = Environment.GetEnvironmentVariable("VAULT_TOKEN");
        if (!string.IsNullOrWhiteSpace(token)) return token.Trim();
        if (File.Exists(TokenFile)) return File.ReadAllText(TokenFile).Trim();

        if (Console.IsInputRedirected)
        {
            Warn("Chưa có token Vault: đặt VAULT_TOKEN hoặc chạy `vault login` (tạo ~/.vault-token)");
            return null;
        }
        Console.WriteLine("[vault] Đăng nhập Vault bằng: 1) Token  2) Userpass");
        Console.Write("[vault] Chọn (1/2): ");
        token = Console.ReadLine()?.Trim() == "1" ? PromptToken(http, addr) : LoginUserpass(http, addr);
        // Lưu lại để lần chạy sau (dotnet watch khởi động lại) không phải hỏi
        if (token is not null) File.WriteAllText(TokenFile, token);
        return token;
    }

    /// <summary>Dán token có sẵn; kiểm tra token còn dùng được (auth/token/lookup-self) trước khi lưu</summary>
    private static string? PromptToken(HttpClient http, string addr)
    {
        Console.Write("[vault] Token: ");
        var token = ReadPassword().Trim();
        if (string.IsNullOrEmpty(token)) return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{addr}/v1/auth/token/lookup-self");
        request.Headers.Add("X-Vault-Token", token);
        using var res = http.Send(request);
        if (res.IsSuccessStatusCode) return token;
        Warn($"Token không hợp lệ hoặc đã hết hạn ({(int)res.StatusCode})");
        return null;
    }

    private static string? LoginUserpass(HttpClient http, string addr)
    {
        Console.Write("[vault] Username: ");
        var username = Console.ReadLine()?.Trim();
        Console.Write("[vault] Password: ");
        var password = ReadPassword();
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) return null;

        var res = http.PostAsJsonAsync($"{addr}/v1/auth/userpass/login/{Uri.EscapeDataString(username)}", new { password }).GetAwaiter().GetResult();
        if (!res.IsSuccessStatusCode)
        {
            Warn($"Đăng nhập Vault thất bại ({(int)res.StatusCode})");
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

    private static void Warn(string message) => Console.WriteLine($"[vault] {message}");
}
