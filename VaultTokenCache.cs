using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace FileConverter;

/// <summary>
/// Giữ token Vault trong RAM để lần chạy sau (dotnet watch khởi động lại) không phải đăng nhập lại;
/// đăng xuất / tắt máy là mất — giống vault-start, không bao giờ ghi token xuống đĩa.
/// - Windows: khóa registry VOLATILE (HKCU\Software\Zhizhu\VaultTokens) — chỉ nằm trong bộ nhớ, mất khi đăng xuất / khởi động lại.
/// - Linux: /run/user/&lt;uid&gt; (tmpfs, nằm trên RAM, xóa khi đăng xuất / khởi động lại), file chỉ chủ sở hữu đọc được.
/// - Nơi khác (macOS…): không lưu, lần nào cũng hỏi.
/// Mỗi địa chỉ Vault một token.
/// </summary>
public static class VaultTokenCache
{
    private const string WindowsKey = @"Software\Zhizhu\VaultTokens";

    public static string? Get(string addr)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.OpenSubKey(WindowsKey);
                return key?.GetValue(Name(addr)) as string;
            }
            var file = LinuxFile(addr);
            return file is not null && File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>Lưu token; trả về false nếu máy này không có chỗ lưu trên RAM</summary>
    public static bool Set(string addr, string token)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = OpenVolatileKey();
                key.SetValue(Name(addr), token);
                return true;
            }
            var file = LinuxFile(addr);
            if (file is null) return false;
            File.WriteAllText(file, token);
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static void Remove(string addr)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.OpenSubKey(WindowsKey, writable: true);
                key?.DeleteValue(Name(addr), throwOnMissingValue: false);
                return;
            }
            var file = LinuxFile(addr);
            if (file is not null && File.Exists(file)) File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }

    /// <summary>Tên theo địa chỉ Vault (băm để không lộ địa chỉ, không vướng ký tự lạ)</summary>
    private static string Name(string addr) =>
        "vault-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(addr.TrimEnd('/'))))[..16].ToLowerInvariant();

    /// <summary>Linux: thư mục runtime của phiên đăng nhập (tmpfs); không có thì không lưu</summary>
    private static string? LinuxFile(string addr)
    {
        if (!OperatingSystem.IsLinux()) return null;
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return !string.IsNullOrEmpty(runtimeDir) && Directory.Exists(runtimeDir) ? Path.Combine(runtimeDir, Name(addr)) : null;
    }

    /// <summary>
    /// Mở / tạo khóa registry volatile. .NET không có API tạo khóa volatile nên gọi RegCreateKeyEx với REG_OPTION_VOLATILE.
    /// Khóa cha (Software\Zhizhu) là khóa thường; chỉ khóa chứa token là volatile.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static RegistryKey OpenVolatileKey()
    {
        const int RegOptionVolatile = 0x1;
        const int KeyAllAccess = 0xF003F;
        using (Registry.CurrentUser.CreateSubKey(@"Software\Zhizhu")) { }
        var status = RegCreateKeyEx(new IntPtr(unchecked((int)0x80000001)), WindowsKey, 0, null, RegOptionVolatile, KeyAllAccess, IntPtr.Zero, out var handle, out _);
        if (status != 0) throw new IOException($"RegCreateKeyEx failed ({status})");
        return RegistryKey.FromHandle(handle);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegCreateKeyEx(
        IntPtr hKey, string lpSubKey, int reserved, string? lpClass, int dwOptions, int samDesired,
        IntPtr lpSecurityAttributes, out SafeRegistryHandle phkResult, out int lpdwDisposition);
}
