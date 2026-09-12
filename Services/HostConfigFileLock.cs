using System.Diagnostics;

namespace DutyAgent.Services;

/// <summary>
/// host-config.json 跨进程写锁，协议对齐 Assets_Duty/state_ops.py 的
/// acquire_file_lock / release_file_lock（Python 后端与独立客户端共用同一协议）：
/// - 锁文件为 <c>&lt;目标文件&gt;.lock</c>，以 O_EXCL 等价（FileMode.CreateNew + FileShare.None）创建；
/// - 锁内容第一行为持有者 PID，第二行为创建时间（ISO 8601），
///   供 Python 侧 _read_lock_metadata 读取并做陈旧锁判定；
/// - 获取失败按 50ms 短重试，等待超过 10s（或锁文件年龄超过 10s）视为陈旧锁，删除重建；
/// - 释放时做所有权校验：仅当锁内容 PID 是当前进程（或内容不可读）才删除，
///   避免误删他人接管后的锁；删除失败时把残留锁留给陈旧回收，不掩盖已完成的写入。
/// </summary>
public sealed class HostConfigFileLock : IDisposable
{
    private const int RetryIntervalMilliseconds = 50;
    private static readonly TimeSpan StaleLockThreshold = TimeSpan.FromSeconds(10);
    private const int ReleaseDeleteRetryCount = 6;

    private readonly string _lockPath;
    private bool _disposed;

    private HostConfigFileLock(string lockPath)
    {
        _lockPath = lockPath;
    }

    /// <summary>
    /// 获取锁。获取失败（陈旧锁接管后被其他写者抢先、持续 IO 错误）时抛出异常，
    /// 调用方按写入失败处理 —— 与 Python 侧 TimeoutError 行为对齐。
    /// </summary>
    public static HostConfigFileLock Acquire(string lockPath)
    {
        var directory = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var stopwatch = Stopwatch.StartNew();
        var stealAttempted = false;

        while (true)
        {
            try
            {
                using (var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream))
                {
                    writer.WriteLine(Environment.ProcessId);
                    writer.WriteLine(DateTimeOffset.Now.ToString("O"));
                }

                return new HostConfigFileLock(lockPath);
            }
            catch (IOException) when (File.Exists(lockPath))
            {
                // O_EXCL 冲突：锁已被持有。
            }

            var lockAge = GetLockAge(lockPath);
            if (lockAge >= StaleLockThreshold && !stealAttempted)
            {
                // 超过 10s 视为陈旧锁接管（删除重建）。只偷一次：接管后立即出现的
                // 新锁属于活跃写者（可能是与我们竞争的接管者），再删会形成互删活锁。
                stealAttempted = true;
                Debug.WriteLine($"HostConfigFileLock: stealing stale lock {lockPath} (age {lockAge.TotalSeconds:F1}s).");
                TryDeleteLock(lockPath);
                continue;
            }

            if (stealAttempted || stopwatch.Elapsed >= StaleLockThreshold)
            {
                throw new TimeoutException(
                    $"Timed out waiting for host-config file lock: {lockPath} " +
                    $"(waited {stopwatch.ElapsedMilliseconds}ms, lock age {lockAge.TotalSeconds:F1}s).");
            }

            Thread.Sleep(RetryIntervalMilliseconds);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 所有权校验：锁内容 PID 不是本进程（被接管/已易主）时不删除。
        var ownerPid = TryReadOwnerPid(_lockPath);
        if (ownerPid.HasValue && ownerPid.Value != Environment.ProcessId)
        {
            return;
        }

        // Windows 上并发等待者会周期性打开锁文件读陈旧元数据，unlink 可能瞬时
        // PermissionError —— 短重试若干次；最终失败则留给陈旧回收，不抛错掩盖
        // 受保护写入已成功的事实（对齐 Python release_file_lock）。
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Delete(_lockPath);
                return;
            }
            catch (FileNotFoundException)
            {
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (IOException) when (attempt < ReleaseDeleteRetryCount - 1)
            {
                Thread.Sleep(RetryIntervalMilliseconds * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < ReleaseDeleteRetryCount - 1)
            {
                Thread.Sleep(RetryIntervalMilliseconds * (attempt + 1));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"HostConfigFileLock: failed to release lock {_lockPath}: {ex.Message}");
                return;
            }
        }
    }

    private static TimeSpan GetLockAge(string lockPath)
    {
        try
        {
            var lastWriteUtc = File.GetLastWriteTimeUtc(lockPath);
            if (lastWriteUtc == DateTime.MinValue || lastWriteUtc > DateTime.UtcNow.AddMinutes(1))
            {
                return TimeSpan.Zero;
            }

            var age = DateTime.UtcNow - lastWriteUtc;
            return age < TimeSpan.Zero ? TimeSpan.Zero : age;
        }
        catch (Exception)
        {
            // 读不到年龄（刚被删除等）：按陈锁对待，让接管路径继续。
            return StaleLockThreshold;
        }
    }

    private static void TryDeleteLock(string lockPath)
    {
        try
        {
            File.Delete(lockPath);
        }
        catch (Exception)
        {
            // 删除失败（被他人抢先删除/瞬时共享冲突）：下一轮 CreateNew 会给出真实结果。
        }
    }

    private static int? TryReadOwnerPid(string lockPath)
    {
        try
        {
            var firstLine = File.ReadLines(lockPath).FirstOrDefault()?.Trim();
            return int.TryParse(firstLine, out var pid) ? pid : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
