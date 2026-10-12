using System.Text.Json;
using DutyAgentBridge.Services;
using Xunit;

namespace DutyAgentBridge.Tests;

/// <summary>
/// 握手文件选取规则回归。重点是"第一个存在的"→"最应该连的那一个"的修正：
/// 旧路径上残留的握手文件（进程已死、或昨天那个客户端还在跑）不能抢走连接。
/// </summary>
public sealed class BridgeMetaResolverTests : IDisposable
{
    private readonly string _root;
    private int _nextPid = 40000;

    public BridgeMetaResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "duty-meta-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void PrefersFresherFile_WhenOldPathLeftStaleFile()
    {
        // 用户报告的场景：旧（主）路径留着昨天的文件，新路径已经是今天的实例。
        var stale = WriteMeta("legacy", startedAt: "2026-10-11T08:00:00+00:00", port: 1111);
        var fresh = WriteMeta("safe", startedAt: "2026-10-12T08:00:00+00:00", port: 2222);

        var best = BridgeMetaResolver.Resolve(
            new[] { stale, fresh },
            out var allValid,
            processCheck: _ => true);

        Assert.Equal(fresh, best?.Path);
        Assert.Equal(2222, best?.Meta.Port);
        // 两个文件都"有效"（进程活着），说明这不是异常状态，无需告警。
        Assert.Equal(2, allValid.Count);
    }

    [Fact]
    public void SkipsFile_WhenItsProcessIsDead()
    {
        var stale = WriteMeta("legacy", startedAt: "2026-10-12T08:00:00+00:00", port: 1111);
        var fresh = WriteMeta("safe", startedAt: "2026-10-11T08:00:00+00:00", port: 2222);
        var stalePid = ReadPid(stale);

        var best = BridgeMetaResolver.Resolve(
            new[] { stale, fresh },
            out _,
            processCheck: pid => pid != stalePid);

        Assert.Equal(fresh, best?.Path);
    }

    [Fact]
    public void SkipsFile_WhenPidWasReusedByAnotherProcess()
    {
        // PID 复用：进程活着但不再是 duty-agent，连接必然失败。
        var path = WriteMeta("safe", startedAt: "2026-10-12T08:00:00+00:00", port: 2222);

        var best = BridgeMetaResolver.Resolve(
            new[] { path },
            out _,
            processCheck: _ => false);

        Assert.Null(best);
    }

    [Fact]
    public void ReturnsNull_WhenNoCandidateExists()
    {
        var best = BridgeMetaResolver.Resolve(
            new[] { Path.Combine(_root, "missing", "a.json"), Path.Combine(_root, "missing", "b.json") },
            out var allValid,
            processCheck: _ => true);

        Assert.Null(best);
        Assert.Empty(allValid);
    }

    [Fact]
    public void UsesFallbackTier_OnlyWhenPrimaryYieldsNothing()
    {
        var primary = WriteMeta("primary-missing-content", startedAt: "2026-10-12T08:00:00+00:00", port: 1111);
        File.Delete(primary);
        var fallback = WriteMeta("packages-redirected", startedAt: "2026-10-12T08:00:00+00:00", port: 2222);

        var best = BridgeMetaResolver.ResolveWithFallback(
            new[] { primary },
            new[] { fallback },
            out _,
            out var usedFallback,
            processCheck: _ => true);

        Assert.Equal(fallback, best?.Path);
        Assert.True(usedFallback);
    }

    [Fact]
    public void IgnoresFallbackTier_WhenPrimaryAlreadyValid()
    {
        var primary = WriteMeta("legacy", startedAt: "2026-10-12T08:00:00+00:00", port: 1111);
        var fallback = WriteMeta("packages-redirected", startedAt: "2026-10-13T08:00:00+00:00", port: 2222);

        var best = BridgeMetaResolver.ResolveWithFallback(
            new[] { primary },
            new[] { fallback },
            out _,
            out var usedFallback,
            processCheck: _ => true);

        Assert.Equal(primary, best?.Path);
        Assert.False(usedFallback, "主层已解析出结果时不应展开兜底层");
    }

    [Fact]
    public void BreaksTieByFileMtime_ThenByCandidateOrder()
    {
        // started_at 相同时（同一实例双写），靠 mtime 决定；再相同则靠路径顺序。
        var older = WriteMeta("a", startedAt: "2026-10-12T08:00:00+00:00", port: 1111);
        var newer = WriteMeta("b", startedAt: "2026-10-12T08:00:00+00:00", port: 2222);
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

        var best = BridgeMetaResolver.Resolve(
            new[] { older, newer },
            out _,
            processCheck: _ => true);

        Assert.Equal(newer, best?.Path);
    }

    [Fact]
    public void PrefersEarlierCandidate_WhenEverythingElseTies()
    {
        var first = WriteMeta("a", startedAt: "2026-10-12T08:00:00+00:00", port: 1111);
        var second = WriteMeta("b", startedAt: "2026-10-12T08:00:00+00:00", port: 2222);
        var sameMtime = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(first, sameMtime);
        File.SetLastWriteTimeUtc(second, sameMtime);

        var best = BridgeMetaResolver.Resolve(
            new[] { first, second },
            out _,
            processCheck: _ => true);

        Assert.Equal(first, best?.Path);
    }

    [Fact]
    public void IgnoresUnparsableAndEmptyFiles()
    {
        var broken = Path.Combine(_root, "broken.json");
        File.WriteAllText(broken, "{ not json");
        var noPid = Path.Combine(_root, "nopid.json");
        File.WriteAllText(noPid, "{\"started_at\":\"2026-10-12T08:00:00+00:00\"}");
        var good = WriteMeta("good", startedAt: "2026-10-12T08:00:00+00:00", port: 2222);

        var best = BridgeMetaResolver.Resolve(
            new[] { broken, noPid, good },
            out var allValid,
            processCheck: _ => true);

        Assert.Equal(good, best?.Path);
        Assert.Single(allValid);
    }

    private string WriteMeta(string name, string startedAt, int port)
    {
        var path = Path.Combine(_root, name + ".json");
        var pid = _nextPid++;
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            version = "0.50.0",
            pid,
            port,
            token_mode = "dynamic",
            token = "t",
            started_at = startedAt,
        }));
        return path;
    }

    private static int ReadPid(string metaPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(metaPath));
        return document.RootElement.GetProperty("pid").GetInt32();
    }
}
