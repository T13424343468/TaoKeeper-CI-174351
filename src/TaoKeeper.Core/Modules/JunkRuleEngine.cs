using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TaoKeeper.Core.Models;

namespace TaoKeeper.Core.Modules;

/// <summary>一条垃圾文件清理规则。</summary>
public sealed record JunkRule(
    string Id,
    string Title,
    string Description,
    /// <summary>命中的目录（绝对路径或以 %VAR% 表示的环境变量占位，如 %TEMP%）。</summary>
    IReadOnlyList<string> Directories,
    /// <summary>目录内按此模式匹配文件名；null 表示整个目录候选。</summary>
    string? FileNamePattern,
    /// <summary>仅当文件早于该天数时才入选；null 表示不按时间筛。</summary>
    int? OlderThanDays,
    /// <summary>风险等级：低=纯缓存可再生，中=需用户过目，高=默认不勾选。</summary>
    JunkRisk Risk)
{
    public string RootExpression => Directories.FirstOrDefault() ?? string.Empty;
}

/// <summary>规则风险等级。道系边界：清理动作必须经用户确认，这里只产出候选，不做任何删除。</summary>
public enum JunkRisk { Low, Medium, High }

/// <summary>一条垃圾清理候选（绝不自动删除，仅列给用户勾选）。</summary>
public sealed record JunkCandidate(
    JunkRule Rule,
    string FullPath,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc,
    string Reason);

/// <summary>垃圾清理扫描结果。</summary>
public sealed record JunkScanResult(
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    IReadOnlyList<JunkCandidate> Candidates,
    long TotalBytes)
{
    public int TotalCount => Candidates.Count;
}

/// <summary>
/// 垃圾清理规则引擎（模块 M1 之二，可移植逻辑层）。
/// 规则表驱动：Windows 真机环境变量展开后命中；沙箱测试直接用绝对路径验证逻辑。
/// 只读：引擎只产出候选清单，删除动作永远属于用户确认后的上层。
/// </summary>
public sealed class JunkRuleEngine
{
    private readonly IReadOnlyList<JunkRule> _rules;

    public JunkRuleEngine(IReadOnlyList<JunkRule>? rules = null)
    {
        _rules = rules ?? DefaultRules();
    }

    /// <summary>默认规则表：系统临时、应用缓存、浏览器缓存、崩溃转储、缩略图缓存等。
    /// 目录用环境变量占位表达，Windows 上展开、其他平台按展开失败跳过。</summary>
    public static IReadOnlyList<JunkRule> DefaultRules() => new List<JunkRule>
    {
        new("system-temp", "系统临时文件", "%TEMP% 下的临时文件，关闭程序后通常不再需要",
            new[] { "%TEMP%" }, "*", null, JunkRisk.Low),
        new("windows-temp", "Windows 临时目录", "C:\\Windows\\Temp 的系统级临时文件",
            new[] { "%WINDIR%\\Temp" }, "*", null, JunkRisk.Medium),
        new("prefetch-aging", "过期预读文件", "预读缓存中超过 14 天未命中的文件",
            new[] { "%WINDIR%\\Prefetch" }, "*.pf", 14, JunkRisk.Medium),
        new("crash-dumps", "崩溃转储", "应用与系统崩溃留下的 .dmp 文件",
            new[] { "%LOCALAPPDATA%\\CrashDumps", "%WINDIR%\\Minidump" }, "*.dmp", null, JunkRisk.Low),
        new("thumbnail-cache", "缩略图缓存", "资源管理器缩略图缓存，删除后自动重建",
            new[] { "%LOCALAPPDATA%\\Microsoft\\Windows\\Explorer" }, "thumbcache_*.db", null, JunkRisk.Low),
        new("browser-cache", "浏览器缓存", "各浏览器 HTTP 缓存目录中的缓存文件",
            new[]
            {
                "%LOCALAPPDATA%\\Google\\Chrome\\User Data\\Default\\Cache",
                "%LOCALAPPDATA%\\Microsoft\\Edge\\User Data\\Default\\Cache",
                "%APPDATA%\\Mozilla\\Firefox\\Profiles",
            }, "*_cache_*", null, JunkRisk.Low),
        new("installer-residue", "安装包残留", "下载目录中的安装包，装完即可清理（需过目）",
            new[] { "%USERPROFILE%\\Downloads" }, "*.{msi,exe,zip}", 30, JunkRisk.Medium),
    };

    /// <summary>
    /// 按规则表扫描候选垃圾。
    /// envProvider 允许测试注入虚拟环境；真机传 Environment.ExpandEnvironmentVariables。
    /// 白名单中的路径整条排除（含子项），白名单优先于一切规则。
    /// </summary>
    public Task<JunkScanResult> ScanAsync(
        Func<string, string?> envProvider,
        ISet<string> whitelist,
        IProgress<JunkScanProgress>? progress = null,
        CancellationToken cancellation = default)
        => ScanAsync(envProvider, whitelist, rootsOverride: null, progress, cancellation);

    /// <summary>沙箱测试重载：直接给定待检根目录（绕过环境变量展开）。</summary>
    public Task<JunkScanResult> ScanAsync(
        Func<string, string?> envProvider,
        ISet<string> whitelist,
        IReadOnlyList<string>? rootsOverride,
        IProgress<JunkScanProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        var started = DateTimeOffset.UtcNow;
        var candidates = new List<JunkCandidate>();
        var now = DateTimeOffset.UtcNow;

        var rules = rootsOverride is null
            ? _rules
            : _rules.Select(r => r with { Directories = rootsOverride }).ToList();

        foreach (var rule in rules)
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (var rawDir in rule.Directories)
            {
                // 绝对路径直接使用；仅含 %VAR% 占位的路径才交给环境展开器。
                var dir = rawDir.Contains('%') ? envProvider(rawDir) : rawDir;
                if (dir is null || !Directory.Exists(dir)) continue;
                if (Whitelisted(dir, whitelist)) continue;

                foreach (var file in SafeEnumerate(dir))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (Whitelisted(file, whitelist)) continue;

                    if (rule.FileNamePattern is not null && rule.FileNamePattern != "*"
                        && !FileNameMatches(file, rule.FileNamePattern)) continue;

                    var info = new FileInfo(file);
                    if (rule.OlderThanDays is { } days
                        && now - new DateTimeOffset(info.LastWriteTimeUtc) < TimeSpan.FromDays(days)) continue;

                    candidates.Add(new JunkCandidate(
                        rule, file, info.Length,
                        new DateTimeOffset(info.LastWriteTimeUtc),
                        rule.Description));
                }
            }
            progress?.Report(new JunkScanProgress(rule.Id, candidates.Count, started));
        }

        var ordered = candidates.OrderByDescending(c => c.SizeBytes).ToList();
        return Task.FromResult(new JunkScanResult(
            started, DateTimeOffset.UtcNow, ordered, ordered.Sum(c => c.SizeBytes)));
    }

    private static IEnumerable<string> SafeEnumerate(string dir)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories); }
        catch (UnauthorizedAccessException) { yield break; }
        catch (DirectoryNotFoundException) { yield break; }
        catch (IOException) { yield break; }
        foreach (var f in files) yield return f;
    }

    private static bool Whitelisted(string path, ISet<string> whitelist)
    {
        foreach (var w in whitelist)
        {
            var normPath = path.Replace('/', '\\').TrimEnd('\\');
            var normW = w.Replace('/', '\\').TrimEnd('\\');
            if (string.Equals(normPath, normW, StringComparison.OrdinalIgnoreCase)) return true;
            if (normPath.StartsWith(normW + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>极简通配匹配（* 与 ?），支持 {a,b} 花括号扩展，避免依赖正则带来的误伤。</summary>
    private static bool FileNameMatches(string path, string pattern)
    {
        var name = Path.GetFileName(path);
        return ExpandBraces(pattern).Any(p => WildcardMatch(name, p));
    }

    /// <summary>把形如 "a.{b,c}d.e" 的模式展开为 ["a.bd.e", "a.cd.e"] 的完整模式列表。</summary>
    private static IEnumerable<string> ExpandBraces(string pattern)
    {
        var open = pattern.IndexOf('{');
        if (open < 0) { yield return pattern; yield break; }
        var close = pattern.IndexOf('}', open + 1);
        if (close < 0) { yield return pattern; yield break; }
        var head = pattern[..open];
        var tail = pattern[(close + 1)..];
        foreach (var alt in pattern[(open + 1)..close].Split(','))
            foreach (var expanded in ExpandBraces(head + alt + tail))
                yield return expanded;
    }

    private static bool WildcardMatch(string text, string pattern)
    {
        int t = 0, p = 0, starP = -1, starT = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t])) { t++; p++; }
            else if (p < pattern.Length && pattern[p] == '*') { starP = p++; starT = t; }
            else if (starP >= 0) { p = starP + 1; t = ++starT; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}

/// <summary>垃圾扫描进度。</summary>
public sealed record JunkScanProgress(string RuleId, int CandidateCount, DateTimeOffset StartedUtc);
