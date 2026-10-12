using System.Security.Cryptography;
using System.Text.Json;
using ThothTAS.Core;

namespace ThothTAS.Host;

public static class ProjectFiles
{
    public static string FindRoot(string? explicitRoot = null)
    {
        if (explicitRoot != null) return Path.GetFullPath(explicitRoot);
        foreach (var candidate in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (DirectoryInfo? dir = new(candidate); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "docs", "game-fingerprint.json"))) return dir.FullName;
        throw new InvalidOperationException("未找到项目根目录；请使用 --root 指定包含 game 和 docs 的项目。");
    }

    public static HashSet<string> ReadStarts(string root)
    {
        var path = Path.Combine(root, "docs", "levels.txt");
        if (!File.Exists(path)) throw new FileNotFoundException("缺少普通关卡清单 docs/levels.txt。", path);
        var starts = File.ReadAllLines(path).Select(s => s.Trim()).Where(s => s.Length > 0 && !s.StartsWith('#')).ToHashSet(StringComparer.Ordinal);
        if (starts.Count == 0) throw new InvalidDataException("普通关卡清单为空，拒绝启动。");
        return starts;
    }

    public static TasScript Parse(string root, string text) => ScriptParser.Parse(text, ReadStarts(root));

    public static void EnsurePrivateGameCopy(string root)
    {
        var game = Path.Combine(root, "game");
        if (!Directory.Exists(game)) throw new DirectoryNotFoundException("缺少独立 game 副本目录。");
        foreach (var path in new[] { game, Path.Combine(game, "THOTH.exe"), Path.Combine(game, "THOTH.sav") })
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("独立副本目录、EXE 与存档不得为符号链接或目录联接，以免写入原安装：" + path);
    }

    public static async Task ValidateFingerprintAsync(string root, CancellationToken cancellation = default)
    {
        var manifestPath = Path.Combine(root, "docs", "game-fingerprint.json");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellation));
        var files = doc.RootElement.GetProperty("files");
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "THOTH.exe", "THOTH_Data/Managed/Assembly-CSharp.dll", "THOTH_Data/Managed/Assembly-CSharp-firstpass.dll",
            "THOTH_Data/Managed/Assembly-UnityScript-firstpass.dll"
        };
        var game = Path.GetFullPath(Path.Combine(root, "game")) + Path.DirectorySeparatorChar;
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        bool hasLevelResources = false;
        foreach (var entry in files.EnumerateArray())
        {
            var name = entry.GetProperty("name").GetString() ?? throw new InvalidDataException("指纹条目缺少 name。");
            var hash = entry.GetProperty("sha256").GetString() ?? throw new InvalidDataException("指纹条目缺少 sha256。");
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("SHA256 格式错误：" + name);
            var relative = name.Replace('\\', '/');
            if (!listed.Add(relative)) throw new InvalidDataException("游戏指纹条目重复：" + name);
            var path = Path.GetFullPath(Path.Combine(game, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(game, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("指纹路径越界：" + name);
            if (!File.Exists(path)) throw new FileNotFoundException("游戏版本文件缺失：" + name, path);
            await using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation));
            if (!string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"游戏版本指纹不匹配，拒绝回放：{name}\n期望 {hash}\n实际 {actual}");
            required.Remove(relative);
            if (relative.StartsWith("THOTH_Data/", StringComparison.OrdinalIgnoreCase) && !relative.StartsWith("THOTH_Data/Managed/", StringComparison.OrdinalIgnoreCase)) hasLevelResources = true;
            count++;
        }
        if (required.Count != 0 || !hasLevelResources || count < 5)
            throw new InvalidDataException("游戏指纹清单不完整：必须包含 EXE、三个游戏程序集及关卡资源。");
        foreach (var path in Directory.EnumerateFiles(Path.Combine(game, "THOTH_Data"), "*", SearchOption.TopDirectoryOnly))
        {
            var filename = Path.GetFileName(path);
            if (filename.StartsWith("level", StringComparison.OrdinalIgnoreCase) || filename.StartsWith("sharedassets", StringComparison.OrdinalIgnoreCase) || filename.StartsWith("resources", StringComparison.OrdinalIgnoreCase) || filename.StartsWith("globalgamemanagers", StringComparison.OrdinalIgnoreCase) || filename == "mainData")
            {
                var relative = "THOTH_Data/" + filename;
                if (!listed.Contains(relative)) throw new InvalidDataException("关卡或共享资源未列入版本指纹，拒绝回放：" + relative);
            }
        }
    }
}
