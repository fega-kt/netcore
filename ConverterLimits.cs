namespace FileConverter;

/// <summary>Giới hạn của service, đọc từ biến môi trường (đổi mức không cần build lại)</summary>
public static class ConverterLimits
{
    /// <summary>Dung lượng tệp tối đa nhận chuyển — <c>MAX_FILE_MB</c>, mặc định 200 (khớp trần upload của backend)</summary>
    public static readonly long MaxFileBytes = ReadInt("MAX_FILE_MB", 200) * 1024L * 1024L;

    /// <summary>Cả request multipart = tệp + phần bọc (boundary, header): chừa thêm 10 MB để tệp sát trần không bị cắt</summary>
    public static readonly long MaxRequestBytes = MaxFileBytes + 10L * 1024 * 1024;

    /// <summary>
    /// Số lượt chuyển chạy cùng lúc — <c>MAX_CONCURRENT_CONVERSIONS</c>, mặc định 4.
    /// Syncfusion dựng cả tài liệu trong RAM: tệp lớn chạy song song nhiều là tràn bộ nhớ, lượt sau xếp hàng chờ.
    /// </summary>
    public static readonly int MaxConcurrentConversions = ReadInt("MAX_CONCURRENT_CONVERSIONS", 4);

    private static int ReadInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
}
