using System.Text.RegularExpressions;
using ThothTAS.Core;

namespace ThothTAS.Editor;

/// <summary>Changes only the explicit rules header; the parser remains the authority for validation.</summary>
internal static class RulesDraftHeader
{
    private static readonly Regex Header = new(@"(?m)^[\t ]*Rules[\t ]*,[^\r\n]*(?:\r?\n|$)");
    private static readonly Regex Version = new(@"(?m)^(?:\uFEFF)?[\t ]*Version[\t ]*,[\t ]*1[\t ]*(?:\r?\n|$)");

    internal static TasRules Read(string text)
    {
        var match = Header.Match(text);
        if (!match.Success) return TasRules.Original;
        return match.Value.Trim().Split(',').Last().Trim() == "Deterministic-1" ? TasRules.Deterministic1 : TasRules.Original;
    }

    internal static string Set(string text, TasRules rules)
    {
        var withoutRules = Header.Replace(text, "");
        if (rules == TasRules.Original) return withoutRules;
        if (rules != TasRules.Deterministic1) throw new ArgumentOutOfRangeException(nameof(rules));
        var version = Version.Match(withoutRules);
        if (!version.Success) throw new FormatException("请先填写 Version,1，再选择游戏规则。");
        var newline = withoutRules.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var position = version.Index + version.Length;
        var prefix = version.Value.EndsWith('\n') ? "" : newline;
        return withoutRules.Insert(position, prefix + "Rules,Deterministic-1" + newline);
    }
}
