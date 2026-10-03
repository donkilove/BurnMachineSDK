using BurnMachine;
using BurnMachine.Channel;
using Xunit;

namespace BurnMachine.Tests;

/// <summary>
/// BurnWorker 集成验证：与协议层、串口通道抽象衔接
/// （v0.6.0 起烧录等待唯一方式为轮询，对照 BurnMachineHost docs/测试计划.md §5 手工冒烟的可自动化部分）。
/// </summary>
public class BurnWorkerTests
{
    private const string BurnSerial = "COM3";
    private const string BurnId = "00881289";
    private const string BurnProgram = "0765";
    private const double BurnTime = 0.1;   // 校验下限（与规格 0.1-600s 一致，审核修复）

    private static BurnRequest NewRequest()
        => new(BurnSerial, BurnId, BurnProgram, BurnTime);

    [Fact]
    public async Task BurnWorker_Success_SequenceAndResult()
    {
        var port = new MockSerialChannel();
        port.EnqueueResponse("`C00881289|00000001|0002|002A9717|0000000000016BC4|0\r\n");   // 实测真实格式
        var statuses = new List<string>();
        var worker = new BurnWorker(() => port, statuses.Add);

        var outcome = await worker.ExecuteAsync(NewRequest(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(BurnResultKind.Success, outcome.Kind);
        // 指令顺序：清空 → 烧录 → 查询
        Assert.Equal("`F00881289|00000001\r\n", port.Writes[0]);
        Assert.Equal("`P00881289|00000001|0765\r\n", port.Writes[1]);
        Assert.Equal("`C00881289 00000001\r\n".Replace(" ", ""), port.Writes[2]);
        Assert.Contains(statuses, s => s.Contains("烧录成功"));   // 轮询模式终止提示
        // v0.6.1：指令级日志——清空/烧录指令发送时经状态回调输出（供宿主执行日志可见）
        Assert.Contains(statuses, s => s.Contains("发送清空指令") && s.Contains("`F00881289|00000001"));
        Assert.Contains(statuses, s => s.Contains("发送烧录指令") && s.Contains("`P00881289|00000001|0765"));
        Assert.False(port.IsOpen);   // 已关闭
    }

    [Fact]
    public async Task BurnWorker_NoResponse_IsTimeoutFailure()
    {
        var port = new MockSerialChannel();   // 无响应：轮询循环至超时
        var worker = new BurnWorker(() => port);

        var outcome = await worker.ExecuteAsync(NewRequest(), CancellationToken.None, pollingTimeoutMs: 100);

        Assert.False(outcome.Success);
        Assert.Equal(1, port.Writes.Count(w => w.StartsWith("`F")));
        Assert.Equal(1, port.Writes.Count(w => w.StartsWith("`P")));
        Assert.Equal(BurnResultKind.Timeout, outcome.Kind);   // 审计 BM-02：超时独立 Kind（原 Failure）
        Assert.Contains("超时", outcome.Detail);
    }

    [Fact]
    public async Task BurnWorker_BurnInProgressTimeout_DoesNotReplay()
    {
        const string burnInProgress = "`C00881289|00000001|0002|002A9717|0000000000016BC4|2\r\n";
        var port = new MockSerialChannel();
        port.OnQueryWrite = _ => port.InjectDriverBytes(burnInProgress);
        var worker = new BurnWorker(() => port);

        var outcome = await worker.ExecuteAsync(
            NewRequest(), CancellationToken.None, pollingIntervalMs: 30, pollingTimeoutMs: 100);

        Assert.Equal(1, port.Writes.Count(w => w.StartsWith("`F")));
        Assert.Equal(1, port.Writes.Count(w => w.StartsWith("`P")));
        Assert.True(port.Writes.Count(w => w.StartsWith("`C")) >= 2);
        Assert.False(outcome.Success);
        Assert.Equal(BurnResultKind.Timeout, outcome.Kind);
    }

    [Fact]
    public async Task BurnWorker_PollingReadIOException_RetriesFullSequence()
    {
        var port = new PollingReadIOExceptionOnceChannel();
        var queryWrites = 0;
        port.OnQueryWrite = _ =>
        {
            queryWrites++;
            if (queryWrites == 2)
            {
                port.InjectDriverBytes("`C00881289|00000001|0002|002A9717|0000000000016BC4|0\r\n");
            }
        };
        var worker = new BurnWorker(() => port);

        var outcome = await worker.ExecuteAsync(
            NewRequest(), CancellationToken.None, pollingIntervalMs: 30, pollingTimeoutMs: 1000);

        Assert.Equal(2, port.OpenCount);
        Assert.Equal(2, port.Writes.Count(w => w.StartsWith("`F")));
        Assert.Equal(2, port.Writes.Count(w => w.StartsWith("`P")));
        Assert.Equal(2, port.Writes.Count(w => w.StartsWith("`C")));
        Assert.True(outcome.Success, outcome.Detail);
        Assert.Equal(BurnResultKind.Success, outcome.Kind);
    }

    [Fact]
    public async Task BurnWorker_OpenFailure_RetriesThenError()
    {
        var port = new MockSerialChannel { OpenError = "拒绝访问" };
        var worker = new BurnWorker(() => port);

        var outcome = await worker.ExecuteAsync(NewRequest(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(BurnResultKind.Error, outcome.Kind);
    }

    [Fact]
    public async Task BurnWorker_Cancellation_Throws()
    {
        var port = new MockSerialChannel();
        var worker = new BurnWorker(() => port);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            worker.ExecuteAsync(NewRequest(), cts.Token));
    }

    // ---- 审核修复：协议健壮性（粘包/半包，对照串口协议规格 §2.3 切帧） ----

    [Fact]
    public async Task BurnWorker_StickyFrames_TakesFirstFrameOnly()
    {
        var port = new MockSerialChannel();
        // 查询响应帧后粘着下一帧头（设备延迟回显/多帧场景）——必须只切第一帧，余量丢弃
        port.EnqueueResponse("`C00881289|00000001|0002|002A9717|0000000000016BC4|0\r\n`C00881289|");
        var worker = new BurnWorker(() => port);

        var outcome = await worker.ExecuteAsync(NewRequest(), CancellationToken.None);

        Assert.True(outcome.Success);   // 修复前：整块解析，末段为第二帧头残片 → 解析错误
    }

    [Fact]
    public async Task BurnWorker_HalfFrame_AccumulatesUntilNewline()
    {
        var port = new MockSerialChannel();
        port.EnqueueResponse("`C00881289|00000001|0002|002A9717|0000000000016BC4|0");   // 半帧（无换行）
        port.EnqueueResponse("\r\n");                                                     // 补帧尾
        var worker = new BurnWorker(() => port);

        var outcome = await worker.ExecuteAsync(NewRequest(), CancellationToken.None);

        Assert.True(outcome.Success);   // 累积到换行（帧边界）才结算
    }

    // ---- 审核修复：执行器健壮性（关闭异常隔离 / 残留缓冲清除 / 重试与取消回归） ----

    [Fact]
    public async Task BurnWorker_CloseThrows_ReturnsOutcomeWithoutRetry()
    {
        var channel = new CloseThrowingChannel();
        var worker = new BurnWorker(() => channel);

        var outcome = await worker.ExecuteAsync(NewRequest(), CancellationToken.None);

        Assert.True(outcome.Success);           // 关闭异常不影响已算出的结果
        Assert.Equal(1, channel.OpenAttempts);  // 不因关闭异常触发整轮重试
        Assert.Equal(1, channel.CloseCalls);
    }

    [Fact]
    public async Task BurnWorker_ReusedChannel_StaleBytesAreDiscardedBeforeQuery()
    {
        var port = new MockSerialChannel();
        var worker = new BurnWorker(() => port);

        port.EnqueueResponse("`C00881289|00000001|0002|002A9717|0000000000016BC4|0\r\n");
        var first = await worker.ExecuteAsync(NewRequest(), CancellationToken.None);
        Assert.True(first.Success);

        // 模拟上次切帧后设备才发出的残留帧头（已到达驱动缓冲，真实 Open 不会清它，靠 Discard 清除）
        port.InjectDriverBytes("`C0088");
        var resetsAfterFirst = port.ResetInputBufferCalls;

        port.EnqueueResponse("`C00881289|00000001|0002|002A9717|0000000000016BC4|0\r\n");
        var second = await worker.ExecuteAsync(NewRequest(), CancellationToken.None);

        // 修复前：残留帧头先被读到 → 回显序列号不匹配 → FormatError
        Assert.True(second.Success);
        // 每次执行至少清除 2 次：Open 后 1 次 + 查询写入后 1 次
        Assert.True(port.ResetInputBufferCalls >= resetsAfterFirst + 2);
    }

    [Fact]
    public async Task BurnWorker_OpenFailure_RetriesThenSucceeds()
    {
        var port = new MockSerialChannel { OpenFailuresRemaining = 1 };   // 第 1 次打开失败，第 2 次成功
        port.EnqueueResponse("`C00881289|00000001|0002|002A9717|0000000000016BC4|0\r\n");
        var statuses = new List<string>();
        var worker = new BurnWorker(() => port, statuses.Add);

        var outcome = await worker.ExecuteAsync(NewRequest(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(2, statuses.Count(s => s.Contains("尝试打开")));   // 整轮最多 2 次：失败 1 次 + 成功 1 次
    }

    [Fact]
    public async Task BurnWorker_OpenFailure_AttemptsExactlyTwice()
    {
        var port = new MockSerialChannel { OpenError = "拒绝访问" };
        var statuses = new List<string>();
        var worker = new BurnWorker(() => port, statuses.Add);

        var outcome = await worker.ExecuteAsync(NewRequest(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(BurnResultKind.Error, outcome.Kind);
        Assert.Equal(2, statuses.Count(s => s.Contains("尝试打开")));   // 整轮最多 2 次
    }

    [Fact]
    public async Task BurnWorker_Cancellation_DuringPolling_Throws()
    {
        var port = new MockSerialChannel();
        var worker = new BurnWorker(() => port);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100);   // 取消落在轮询等待期间

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            worker.ExecuteAsync(new BurnRequest("COM3", "00881289", "0765", 60), cts.Token));
    }

    /// <summary>Close 抛异常的通道：验证收尾异常不逃逸、不触发重试</summary>
    private sealed class CloseThrowingChannel : ISerialChannel
    {
        public int OpenAttempts { get; private set; }
        public int CloseCalls { get; private set; }
        public bool IsOpen { get; private set; }
        public void Open(string portName, int baudRate) { OpenAttempts++; IsOpen = true; }
        public void Write(string text) { }
        public string ReadAvailable() => "`C00881289|00000001|0002|002A9717|0000000000016BC4|0\r\n";
        public void ResetInputBuffer() { }
        public void Close() { CloseCalls++; IsOpen = false; throw new InvalidOperationException("关闭失败: 句柄无效"); }
        public void Dispose() { }
    }

    // ---- 审计 BM-05：先 Reset 后 Write（设备即时回包不被误清） ----

    [Fact]
    public async Task BurnWorker_ImmediateResponse_NotClearedByReset()
    {
        // 设备对查询"即时回包"（响应在 Write 返回前已进入驱动缓冲）——
        // 旧顺序（Write→Reset）会把响应清掉 → 轮询空转至超时；新顺序（Reset→Write）保留
        var port = new MockSerialChannel();
        port.OnQueryWrite = _ => port.InjectDriverBytes("`C00881289|00000001|0002|002A9717|0000000000016BC4|0\r\n");
        var worker = new BurnWorker(() => port);

        var outcome = await worker.ExecuteAsync(NewRequest(), CancellationToken.None, pollingTimeoutMs: 500);

        Assert.True(outcome.Success);
        Assert.Equal(BurnResultKind.Success, outcome.Kind);
        Assert.Equal(1, port.Writes.Count(w => w.StartsWith("`C")));   // 首轮即成功，不空转
    }

    // ---- BM-04：同一串口大小写变体必须使用同一 gate ----

    [Fact]
    public async Task Execute_ConcurrentCaseVariantSameSerial_Serialized()
    {
        var probe = new SharedConcurrencyProbe();
        const string ok = "`C00881289|00000001|0002|002A9717|0000000000016BC4|0\r\n";
        var portA = new ProbingChannel(probe);
        portA.EnqueueResponse(ok);
        var portB = new ProbingChannel(probe);
        portB.EnqueueResponse(ok);
        var workerA = new BurnWorker(() => portA);
        var workerB = new BurnWorker(() => portB);
        var reqA = new BurnRequest("COM3", "00881289", "0765", 0.1);
        var reqB = new BurnRequest("com3", "00881289", "0765", 0.1);

        var taskA = Task.Factory.StartNew(
            () => workerA.ExecuteAsync(reqA, CancellationToken.None, pollingTimeoutMs: 1000),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
        var taskB = Task.Factory.StartNew(
            () => workerB.ExecuteAsync(reqB, CancellationToken.None, pollingTimeoutMs: 1000),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        var results = await Task.WhenAll(taskA, taskB);

        Assert.All(results, result => Assert.True(result.Success));
        Assert.Equal(1, probe.MaxActive);
        Assert.Equal("COM3", portA.LastOpenedPort);
        Assert.Equal("com3", portB.LastOpenedPort);
    }

    // ---- 审计 BM-03：同一烧录串口并发执行串行化（键控互斥） ----

    [Fact]
    public async Task Execute_DifferentSerial_NotBlocked()
    {
        // 键控粒度：不同烧录串口应真实并行执行（共享并发探针检测重叠，
        // 防"退化为全局单 gate"的回归——若串行则探针 MaxActive 恒为 1）
        var probe = new SharedConcurrencyProbe();
        var portA = new ProbingChannel(probe);
        portA.EnqueueResponse("`C00881289|00000001|0002|002A9717|0000000000016BC4|0\r\n");
        var portB = new ProbingChannel(probe);
        portB.EnqueueResponse("`C00881290|00000001|0002|002A9717|0000000000016BC4|0\r\n");
        var workerA = new BurnWorker(() => portA);
        var workerB = new BurnWorker(() => portB);

        var reqA = new BurnRequest("COM3", "00881289", "0765", 0.1);
        var reqB = new BurnRequest("COM4", "00881290", "0765", 0.1);

        var results = await Task.WhenAll(
            workerA.ExecuteAsync(reqA, CancellationToken.None, pollingTimeoutMs: 1000),
            workerB.ExecuteAsync(reqB, CancellationToken.None, pollingTimeoutMs: 1000));

        Assert.All(results, r => Assert.True(r.Success));
        Assert.True(probe.MaxActive >= 2,
            $"不同串口应并行执行（并发探针 MaxActive={probe.MaxActive}）——疑似退化为全局 gate");
    }

    /// <summary>跨通道共享的并发活跃探针（审计 BM-03 测试增强）：
    /// barrier 同步——第二个执行进入时放行两者，随后保持 20ms 活跃，两执行必然重叠；
    /// 退化（全局单 gate）时先进入者等 2s 超时后继续，测试随后以 MaxActive&lt;2 失败。</summary>
    private sealed class SharedConcurrencyProbe
    {
        private readonly ManualResetEventSlim _bothEntered = new(false);
        private int _active;
        private int _entered;
        private int _maxActive;

        public int MaxActive => Volatile.Read(ref _maxActive);

        public void Enter()
        {
            var current = Interlocked.Increment(ref _active);
            UpdateMax(current);
            if (Interlocked.Increment(ref _entered) == 2)
            {
                _bothEntered.Set();
            }

            _bothEntered.Wait(TimeSpan.FromSeconds(2));   // 退化场景防死锁（超时后继续）
            Thread.Sleep(20);                              // 保持活跃窗口（重叠必然发生）
            Interlocked.Decrement(ref _active);
        }

        private void UpdateMax(int current)
        {
            while (true)
            {
                var observed = Volatile.Read(ref _maxActive);
                if (current <= observed
                    || Interlocked.CompareExchange(ref _maxActive, current, observed) == observed)
                {
                    return;
                }
            }
        }
    }

    /// <summary>包装通道：写入时进入共享探针（barrier 检测并行）</summary>
    private sealed class ProbingChannel : ISerialChannel
    {
        private readonly MockSerialChannel _inner = new();
        private readonly SharedConcurrencyProbe _probe;

        public ProbingChannel(SharedConcurrencyProbe probe) => _probe = probe;

        public bool IsOpen => _inner.IsOpen;

        public void EnqueueResponse(string response) => _inner.EnqueueResponse(response);

        public string? LastOpenedPort { get; private set; }

        public void Open(string portName, int baudRate)
        {
            LastOpenedPort = portName;
            _inner.Open(portName, baudRate);
        }

        public void Write(string text)
        {
            _probe.Enter();
            _inner.Write(text);
        }

        public string ReadAvailable() => _inner.ReadAvailable();
        public void ResetInputBuffer() => _inner.ResetInputBuffer();
        public void Close() => _inner.Close();
        public void Dispose() => _inner.Dispose();
    }

    private sealed class PollingReadIOExceptionOnceChannel : ISerialChannel
    {
        private readonly MockSerialChannel _inner = new();
        private bool _throwNextRead = true;

        public bool IsOpen => _inner.IsOpen;
        public int OpenCount { get; private set; }
        public IReadOnlyList<string> Writes => _inner.Writes;
        public Action<string>? OnQueryWrite
        {
            get => _inner.OnQueryWrite;
            set => _inner.OnQueryWrite = value;
        }

        public void InjectDriverBytes(string bytes) => _inner.InjectDriverBytes(bytes);

        public void Open(string portName, int baudRate)
        {
            OpenCount++;
            _inner.Open(portName, baudRate);
        }

        public void Write(string text) => _inner.Write(text);

        public string ReadAvailable()
        {
            if (_throwNextRead)
            {
                _throwNextRead = false;
                throw new System.IO.IOException("模拟一次性轮询读取异常");
            }

            return _inner.ReadAvailable();
        }

        public void ResetInputBuffer() => _inner.ResetInputBuffer();
        public void Close() => _inner.Close();
        public void Dispose() => _inner.Dispose();
    }

    // ---- 审计 BM-03：宿主状态回调异常隔离（不触发重试重发） ----

    [Fact]
    public async Task Execute_StatusCallbackThrows_StillSucceeds()
    {
        // 回调（UI/日志）抛异常不得被当作烧录错误触发重试重发（对同一芯片二次烧录）
        var port = new MockSerialChannel();
        port.EnqueueResponse("`C00881289|00000001|0002|002A9717|0000000000016BC4|0\r\n");
        var worker = new BurnWorker(() => port, _ => throw new InvalidOperationException("UI boom"));

        var outcome = await worker.ExecuteAsync(NewRequest(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(1, port.Writes.Count(w => w.StartsWith("`F")));   // 仅一轮清空指令，无重试重发
    }

}

