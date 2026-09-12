using DutyAgent.Services;
using Xunit;

namespace DutyAgent.Tests;

/// <summary>
/// HostConfigFileLock（O_EXCL 文件锁）并发互斥回归：
/// 协议与 Assets_Duty/state_ops.py acquire_file_lock 对齐（PID + 创建时间两行、
/// 所有权校验释放）。这里只测纯逻辑：串行获取/释放、并发下临界区互斥、
/// 双重释放幂等。
/// </summary>
public class HostConfigFileLockTests : IDisposable
{
    private readonly string _directory;

    public HostConfigFileLockTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            $"dutyagent-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论（锁文件可能被并发读者短暂持有）。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string LockPath => Path.Combine(_directory, "host-config.json.lock");

    [Fact]
    public void Acquire_then_Dispose_allows_reacquire()
    {
        var first = HostConfigFileLock.Acquire(LockPath);
        Assert.Equal(Environment.ProcessId, FirstOwnerPid());

        first.Dispose();

        // 释放后锁文件已删除：可立即再次获取。
        using var second = HostConfigFileLock.Acquire(LockPath);
        Assert.Equal(Environment.ProcessId, FirstOwnerPid());
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var @lock = HostConfigFileLock.Acquire(LockPath);
        @lock.Dispose();
        @lock.Dispose();
        Assert.False(File.Exists(LockPath));
    }

    [Fact]
    public void Lock_file_records_pid_and_timestamp()
    {
        using var @lock = HostConfigFileLock.Acquire(LockPath);
        var lines = File.ReadAllLines(LockPath);
        Assert.True(lines.Length >= 2, "lock file must carry PID + ISO timestamp lines");
        Assert.True(int.TryParse(lines[0], out var pid) && pid == Environment.ProcessId);
        Assert.True(DateTimeOffset.TryParse(lines[1], out _), "second line must be ISO 8601");
    }

    [Fact]
    public void Concurrent_acquirers_never_share_the_critical_section()
    {
        const int threadCount = 8;
        var barrier = new Barrier(threadCount);
        var insideCount = 0;
        var overlapViolations = 0;
        var acquiredCount = 0;
        var errors = new List<Exception>();

        var threads = Enumerable.Range(0, threadCount)
            .Select(_ => new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                    using var @lock = HostConfigFileLock.Acquire(LockPath);
                    Interlocked.Increment(ref acquiredCount);
                    // 临界区互斥断言：同一时刻只允许一个持有者。
                    if (Interlocked.Increment(ref insideCount) != 1)
                    {
                        Interlocked.Increment(ref overlapViolations);
                    }

                    Thread.Sleep(10); // 拉长临界区，放大竞争窗口
                    Interlocked.Decrement(ref insideCount);
                }
                catch (Exception ex)
                {
                    lock (errors)
                    {
                        errors.Add(ex);
                    }
                }
            }))
            .ToList();

        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "worker thread hung");
        }

        Assert.Empty(errors);
        Assert.Equal(threadCount, Volatile.Read(ref acquiredCount));
        Assert.Equal(0, Volatile.Read(ref overlapViolations));
    }

    private int? FirstOwnerPid()
    {
        var firstLine = File.ReadLines(LockPath).FirstOrDefault()?.Trim();
        return int.TryParse(firstLine, out var pid) ? pid : null;
    }
}
