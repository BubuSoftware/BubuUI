namespace EclipseUI.Rendering;

/// <summary>
/// 字体资源：原始 OpenType/TrueType 字节 + 面索引。
/// 后端按引用缓存已加载的原生字体对象；契约层不持有原生句柄。
/// </summary>
public sealed class FontFace
{
    public FontFace(string name, ReadOnlyMemory<byte> data, int faceIndex = 0)
    {
        if (data.IsEmpty) throw new ArgumentException("font data must not be empty", nameof(data));
        Name = name;
        Data = data;
        FaceIndex = faceIndex;
    }

    public string Name { get; }

    public ReadOnlyMemory<byte> Data { get; }

    public int FaceIndex { get; }

    public static FontFace FromBytes(string name, byte[] data, int faceIndex = 0)
        => new(name, data, faceIndex);

    public static FontFace Load(string path, int faceIndex = 0)
        => new(Path.GetFileName(path), File.ReadAllBytes(path), faceIndex);

    /// <summary>Windows 自带字体（无该字体的平台返回 null）。</summary>
    public static FontFace? TryLoadSystemFont(string fileName)
    {
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (string.IsNullOrEmpty(fonts)) return null;
        var path = Path.Combine(fonts, fileName);
        return File.Exists(path) ? Load(path) : null;
    }

    public override string ToString() => $"{Name}#{FaceIndex} ({Data.Length} bytes)";
}
