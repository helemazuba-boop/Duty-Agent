using System;
using DutyAgentBridge.Services;
using Xunit;

namespace DutyAgentBridge.Tests;

/// <summary>
/// 值日展示日期的边界规则。Python state_ops.resolve_display_duty_date /
/// TS resolveDisplayDutyDate / C# DutyStateCache.ResolveDisplayDutyDate
/// 三端实现同一张表；"今天"始终由 DateTime.Now.Date 决定，不被本规则改写。
/// </summary>
public sealed class DutyDisplayDateTests
{
    private static readonly TimeSpan Boundary = new(8, 0, 0);

    private static DateTime Resolve(int day, int hour, int minute, bool enabled = true, TimeSpan? boundary = null)
        => DutyStateCache.ResolveDisplayDutyDate(
            new DateTime(2026, 11, day, hour, minute, 0),
            boundary ?? Boundary,
            enabled);

    [Fact]
    public void BeforeBoundary_StaysOnCalendarToday()
    {
        Assert.Equal(new DateTime(2026, 11, 11), Resolve(11, 7, 59));
    }

    [Fact]
    public void AtBoundary_AdvancesToNextDay()
    {
        Assert.Equal(new DateTime(2026, 11, 12), Resolve(11, 8, 0));
        Assert.Equal(new DateTime(2026, 11, 12), Resolve(11, 23, 59));
    }

    [Fact]
    public void CrossingMidnight_IsContinuous()
    {
        // 同一日期在 00:00 只把「明日」标签换成「今日」，内容不跳。
        Assert.Equal(new DateTime(2026, 11, 12), Resolve(12, 0, 0));
        Assert.Equal(new DateTime(2026, 11, 12), Resolve(12, 7, 59));
        Assert.Equal(new DateTime(2026, 11, 13), Resolve(12, 8, 0));
    }

    [Fact]
    public void DisabledPreview_NeverAdvances()
    {
        foreach (var hour in new[] { 0, 8, 12, 23 })
        {
            Assert.Equal(new DateTime(2026, 11, 11), Resolve(11, hour, 0, enabled: false));
        }
    }

    [Fact]
    public void MidnightAndLastMinuteBoundaries()
    {
        Assert.Equal(new DateTime(2026, 11, 12), Resolve(11, 0, 0, boundary: TimeSpan.Zero));
        Assert.Equal(new DateTime(2026, 11, 11), Resolve(11, 23, 58, boundary: new TimeSpan(23, 59, 0)));
        Assert.Equal(new DateTime(2026, 11, 12), Resolve(11, 23, 59, boundary: new TimeSpan(23, 59, 0)));
    }

    [Fact]
    public void ParsePreviewBoundary_FallsBackToEightOnInvalidInput()
    {
        Assert.Equal(new TimeSpan(8, 0, 0), DutyStateCache.ParsePreviewBoundary(null));
        Assert.Equal(new TimeSpan(8, 0, 0), DutyStateCache.ParsePreviewBoundary("not-a-time"));
        Assert.Equal(new TimeSpan(8, 0, 0), DutyStateCache.ParsePreviewBoundary("24:00"));
        Assert.Equal(new TimeSpan(18, 30, 0), DutyStateCache.ParsePreviewBoundary("18:30"));
    }
}
