using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using DutyAgentBridge.Models;

namespace DutyAgentBridge.Services;

/// <summary>
/// 一份可用的桥接握手候选：meta 内容 + 它来自哪个物理文件 + 用于排序的新鲜度信息。
/// </summary>
internal sealed record BridgeMetaCandidate(
    string Path,
    StandaloneMeta Meta,
    DateTimeOffset StartedAt,
    DateTime LastWriteUtc,
    int Order);

/// <summary>
/// 握手 meta 文件的选取规则。
///
/// 为什么不能"取第一个存在的"：客户端会把同一份 meta 写到多个物理位置
/// （宿主布局目录、LocalAppData、以及 MSIX 包身份下可能被重定向到的
/// <c>Packages\&lt;PFN&gt;\LocalCache\Local</c> 或 <c>LocalState</c>）。
/// 于是旧路径完全可能留着：
///  - 昨天那次退出时没删掉的文件（进程已死）；
///  - 昨天那个客户端进程还在跑（zip 版与商店版同机并存时真的会发生）。
/// 两种都满足"文件存在"，但都不是当前该连的实例。
///
/// 规则：
///  1. 有效性：文件存在 → 能解析 → pid &gt; 0 → 该 pid 活着且进程名是 duty-agent（防 PID 复用）
///  2. 新鲜度：全部有效候选里取 <c>started_at</c> 最新的；相同取文件 mtime 更新的；
///     再相同取候选列表更靠前的（更"正统"的路径）
///  3. 返回"最应该连的那一个"，而不是"最先看到的那一个"
/// </summary>
internal static class BridgeMetaResolver
{
    /// <summary>客户端进程名（不含扩展名），用于拒绝 PID 复用。</summary>
    private const string ExpectedProcessName = "duty-agent";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 从候选路径中选出唯一应连接的 meta。
    /// </summary>
    /// <param name="paths">候选路径，优先级从高到低（仅用于同分 tie-break）。</param>
    /// <param name="allValid">全部通过有效性检查的候选，诊断用。多于 1 说明存在多实例或残留。</param>
    /// <param name="processCheck">进程有效性判定，测试可注入；默认用真实进程检查。</param>
    public static BridgeMetaCandidate? Resolve(
        IReadOnlyList<string> paths,
        out IReadOnlyList<BridgeMetaCandidate> allValid,
        Func<int, bool>? processCheck = null)
    {
        var collected = Collect(paths, processCheck);
        allValid = collected;
        return collected.Count == 0 ? null : collected[0];
    }

    /// <summary>
    /// 主层优先，主层没有有效候选时才展开兜底层（扫描 MSIX 重定向目录）。
    /// 这样非商店部署完全不付通配扫描的代价。
    /// </summary>
    public static BridgeMetaCandidate? ResolveWithFallback(
        IReadOnlyList<string> primary,
        IReadOnlyList<string> fallback,
        out IReadOnlyList<BridgeMetaCandidate> allValid,
        out bool usedFallback,
        Func<int, bool>? processCheck = null)
    {
        var best = Resolve(primary, out var primaryValid, processCheck);
        if (best is not null)
        {
            allValid = primaryValid;
            usedFallback = false;
            return best;
        }

        best = Resolve(fallback, out var fallbackValid, processCheck);
        allValid = fallbackValid;
        usedFallback = best is not null;
        return best;
    }

    /// <summary>
    /// 收集全部有效候选，按新鲜度降序排列。
    /// </summary>
    public static IReadOnlyList<BridgeMetaCandidate> Collect(
        IReadOnlyList<string> paths,
        Func<int, bool>? processCheck = null)
    {
        var isExpected = processCheck ?? IsExpectedProcess;
        var valid = new List<BridgeMetaCandidate>(paths.Count);
        for (var index = 0; index < paths.Count; index++)
        {
            var candidate = TryLoad(paths[index], index, isExpected);
            if (candidate is not null)
            {
                valid.Add(candidate);
            }
        }

        valid.Sort(CompareByFreshness);
        return valid;
    }

    /// <summary>
    /// 进程不仅活着，还得确实是 duty-agent：否则 PID 复用会让陈旧 meta 骗过存活检查，
    /// 桥接会连到一个早已不存在的端口上。
    /// </summary>
    public static bool IsExpectedProcess(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return false;
            }

            return string.Equals(process.ProcessName, ExpectedProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>诊断用：把候选情况压成一行，便于日志比对多个物理路径。</summary>
    public static string Describe(IReadOnlyList<BridgeMetaCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return "无有效握手文件";
        }

        return string.Join(" | ", candidates.Select(c =>
            $"{c.Path} (pid={c.Meta.ProcessId}, port={c.Meta.Port}, started={c.StartedAt:O})"));
    }

    private static BridgeMetaCandidate? TryLoad(string? path, int order, Func<int, bool> isExpectedProcess)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            var meta = JsonSerializer.Deserialize<StandaloneMeta>(json, JsonOptions);
            if (meta is null || meta.ProcessId <= 0)
            {
                return null;
            }

            if (!isExpectedProcess(meta.ProcessId))
            {
                return null;
            }

            var startedAt = DateTimeOffset.TryParse(
                meta.StartedAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;

            return new BridgeMetaCandidate(
                path,
                meta,
                startedAt,
                File.GetLastWriteTimeUtc(path),
                order);
        }
        catch
        {
            // 单个候选坏掉（并发写/文件占用/权限）不应拖垮其余候选。
            return null;
        }
    }

    private static int CompareByFreshness(BridgeMetaCandidate left, BridgeMetaCandidate right)
    {
        var byStart = right.StartedAt.CompareTo(left.StartedAt);
        if (byStart != 0)
        {
            return byStart;
        }

        var byWrite = right.LastWriteUtc.CompareTo(left.LastWriteUtc);
        if (byWrite != 0)
        {
            return byWrite;
        }

        return left.Order.CompareTo(right.Order);
    }
}
