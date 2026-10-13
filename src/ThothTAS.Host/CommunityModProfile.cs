using System.Security.Cryptography;

namespace ThothTAS.Host;

/// <summary>The one reviewed community assembly; the stock game manifest remains authoritative.</summary>
public static class CommunityModProfile
{
    public const string Version = "v2.8";
    public const string ExpectedSha256 = "2A35DDEA8A48144E926AC33796FFD63267A5AC129FD2495EF7F10A7D893E33FD";
    public const string DownloadUrl = "https://github.com/rmadlal/thoth-mod/releases/download/v2.8/Assembly-CSharp.dll";
    public static string AssemblyPath(string root) => Path.GetFullPath(Path.Combine(root, "work", "community-mod", Version, "Assembly-CSharp.dll"));

    public static FileStream? ValidateAndLock(string root, bool enabled)
    {
        if (!enabled) return null;
        var path = AssemblyPath(root);
        if (!File.Exists(path)) throw new FileNotFoundException("社区 Speedrun Mod v2.8 尚未准备，请先运行 scripts/Prepare-CommunityMod.ps1。Community Speedrun Mod is missing; run scripts/Prepare-CommunityMod.ps1.", path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("社区 Mod 文件不得是符号链接。Community Mod cannot be a symbolic link.");
        var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var actual = Convert.ToHexString(SHA256.HashData(locked));
            if (actual != ExpectedSha256) throw new InvalidDataException("社区 Mod v2.8 指纹不匹配，拒绝加载。Community Mod hash mismatch. Expected " + ExpectedSha256 + "; actual " + actual);
            locked.Position = 0;
            return locked;
        }
        catch { locked.Dispose(); throw; }
    }
}
