using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using BurnMachine.Channel;

namespace BurnMachine;

/// <summary>
/// 单点烧录执行器：按 XW16Pro 协议执行 清空→烧录→轮询查询 时序（整轮最多尝试 2 次）。
/// 烧录等待唯一方式为轮询（v0.6.0 起，固定等待已退役）。
/// 响应读取用累积缓冲 + 换行帧切分（修复 BurnMachineHost 原版粘包/半包问题）。
/// 同一烧录串口（BurnSerial）的并发执行被进程内键控互斥串行化（审计 BM-03）；
/// 跨进程共用同一串口需外部互斥（SDK 无跨进程协调）。
/// </summary>
public sealed class BurnWorker
{
    private const int MaxRetries = 2;
    private const int RetryDelayMs = 1000;

    /// <summary>审计 BM-03：按烧录串口键控的执行互斥（进程内；条目生命周期=进程，烧录机串口数量有限）</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SerialGates = new();

    /// <summary>
    /// 轮询模式默认查询间隔（v0.6.3：50ms → 30ms。真机实测 2026-08-22（COM9/00911007，9600 波特）：
    /// 30ms 间隔 16~17 轮查询零丢帧、完成检测正常；下限同步 50 → 30）。
    /// </summary>
    public const int DefaultPollingIntervalMs = 30;

    /// <summary>轮询模式默认总超时（ms）</summary>
    public const int DefaultPollingTimeoutMs = 3500;

    /// <summary>
    /// 轮询单轮读窗口（实测完整帧最迟 C 136ms / U 202ms（COM9/00911008，9600 波特）；
    /// 250ms 对 U 帧留 ~24% 余量，详见 docs/specs/timing-tighten.md）
    /// </summary>
    private const int PollingReadWindowMs = 250;

    /// <summary>轮询单轮读取粒度（实测 100ms 粒度会白等一拍至 200ms 才能读到完整帧；20ms 拍 ~140ms 即读到）</summary>
    private const int PollingReadPollMs = 20;

    /// <summary>
    /// 清空→烧录指令间隔（v0.6.1：100ms → 30ms；v0.6.3：30ms → 10ms。真机实测 2026-08-22
    /// （COM9/00911007，9600 波特）：清空间隔 0ms 极限压测 3 组零丢帧且烧录成功，10ms 留 5 倍余量）。
    /// </summary>
    private const int ClearToBurnDelayMs = 10;

    private const int MinPollingIntervalMs = 30;
    private const int MaxPollingIntervalMs = 10000;

    /// <summary>轮询总超时下限（ms）——宿主仓映射 burn_time_seconds 时据此钳制，
    /// 无需复制本值（IN-03：此前为 private const，宿主只能硬编码 100）。</summary>
    public const int MinPollingTimeoutMs = 100;

    /// <summary>轮询总超时上限（ms，与 BurnTimeSeconds 上限 600s 对齐）——宿主仓映射时据此钳制，
    /// 无需复制本值（IN-03）。</summary>
    public const int MaxPollingTimeoutMs = 600000;

    private readonly Func<ISerialChannel> _channelFactory;
    private readonly Action<string>? _status;
    private readonly int _baudRate;

    /// <param name="channelFactory">每次执行新建串口通道的工厂（执行结束即关闭释放）</param>
    /// <param name="status">可选状态回调（如宿主状态栏）；SDK 独立使用可不传</param>
    /// <param name="baudRate">烧录机串口波特率（XW16Pro 协议为 9600 8N1）</param>
    public BurnWorker(Func<ISerialChannel> channelFactory, Action<string>? status = null, int baudRate = 9600)
    {
        _channelFactory = channelFactory;
        _status = WrapStatusCallback(status);   // 审计 BM-03：宿主状态回调与控制流隔离（MC-01 同族）
        _baudRate = baudRate;
    }

    /// <summary>
    /// 宿主状态回调与控制流隔离（审计 BM-03）：回调（UI/日志）异常不得被当作烧录错误，
    /// 否则指令已发送后回调抛异常会触发整轮重试、重发清空/烧录指令（对同一芯片二次烧录）。
    /// 仅 Debug 记录便于宿主排查自身缺陷，Release 零开销。
    /// </summary>
    private static Action<string>? WrapStatusCallback(Action<string>? status)
    {
        if (status is null)
        {
            return null;
        }

        return message =>
        {
            try
            {
                status(message);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BurnMachine] 状态回调异常已隔离: {ex}");
            }
        };
    }

    /// <summary>执行单点烧录：发送清空指令 → 延时 → 发送烧录指令 → 轮询结果直到出结果码或超时。</summary>
    /// <remarks>
    /// BM-01 重试语义（产线设计确认）：<b>任何重试都是完整时序重放</b>——轮询阶段
    /// （烧录指令已发出后）的 IO 异常与未发送指令的失败同等触发「清空 → 烧录」完整序列
    /// 重试，同一芯片可能被烧录两次（XW16Pro 烧写计数/擦写寿命双扣）。
    /// 取舍依据：对已烧录芯片重复烧录的代价低于直接 NG（重试对瞬时串口故障有自愈价值）。
    /// 若产线出现计数损耗投诉，可改为「已发烧录指令 → 仅重试查询不重发」（审查 BM-01 案B）。
    /// </remarks>
    /// <param name="request">单点烧录请求（BurnTimeSeconds 仅作请求记录，实际等待由 pollingTimeoutMs 决定）</param>
    /// <param name="ct">取消令牌</param>
    /// <param name="pollingIntervalMs">两次轮询查询之间的间隔（ms，30~10000，默认 30）</param>
    /// <param name="pollingTimeoutMs">轮询总超时（ms，100~600000，默认 3500）；超时未出结果判失败</param>
    /// <param name="pollingQuery">轮询查询命令（默认 C；U 时完成轮响应携带 UID 到 outcome.Uid，需固件 &gt; 20240103000000）</param>
    public async Task<BurnOutcome> ExecuteAsync(
        BurnRequest request,
        CancellationToken ct,
        int pollingIntervalMs = DefaultPollingIntervalMs,
        int pollingTimeoutMs = DefaultPollingTimeoutMs,
        PollingQueryKind pollingQuery = PollingQueryKind.C)
    {
        ValidatePollingParameters(pollingIntervalMs, pollingTimeoutMs);

        // 审计 BM-03：按烧录串口键控互斥——同一 BurnSerial 的并发执行串行化
        // （防同口并发争抢；不同串口互不影响；跨进程需外部互斥）
        var gate = SerialGates.GetOrAdd(request.BurnSerial.ToUpperInvariant(), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ExecuteCoreAsync(request, ct, pollingIntervalMs, pollingTimeoutMs, pollingQuery)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<BurnOutcome> ExecuteCoreAsync(
        BurnRequest request,
        CancellationToken ct,
        int pollingIntervalMs,
        int pollingTimeoutMs,
        PollingQueryKind pollingQuery)
    {
        for (var retry = 0; retry < MaxRetries; retry++)
        {
            ISerialChannel? ser = null;
            try
            {
                _status?.Invoke($"尝试打开烧录机串口 {request.BurnSerial}，第 {retry + 1}/{MaxRetries} 次");
                ser = _channelFactory();
                ser.Open(request.BurnSerial, _baudRate);
                ser.ResetInputBuffer();   // 审核修复：清打开时驱动缓冲残留（设备上电噪声/上次会话数据）

                var clearCmd = BurnProtocol.BuildClearCommand(request.BurnId, request.Channels);
                _status?.Invoke($"发送清空指令: {clearCmd.Trim()}");
                ser.Write(clearCmd);
                await Task.Delay(ClearToBurnDelayMs, ct);

                var burnCmd = BurnProtocol.BuildBurnCommand(request.BurnId, request.BurnProgram, request.Channels, request.Barcode);
                _status?.Invoke($"发送烧录指令: {burnCmd.Trim()}");
                ser.Write(burnCmd);

                // 轮询模式（唯一等待方式）：按间隔轮询 C/U 查询直到结果码 0/1 或超时
                return await WaitForBurnCompletionAsync(ser, request, ct, pollingIntervalMs, pollingTimeoutMs, pollingQuery);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _status?.Invoke($"烧录错误: {e.Message}");
                if (retry < MaxRetries - 1)
                {
                    _status?.Invoke($"等待 {RetryDelayMs / 1000.0:0} 秒后重试...");
                    await Task.Delay(RetryDelayMs, ct);
                }
                else
                {
                    return new BurnOutcome(false, BurnResultKind.Error, $"烧录错误: {e.Message}");
                }
            }
            finally
            {
                if (ser is not null)
                {
                    try
                    {
                        if (ser.IsOpen)
                        {
                            ser.Close();
                            _status?.Invoke($"已关闭烧录机串口 {request.BurnSerial}");
                        }
                    }
                    catch (Exception e)
                    {
                        // 审核修复：关闭/释放异常属收尾噪音，不得逃逸破坏返回值，也不触发重试
                        _status?.Invoke($"关闭烧录机串口异常: {e.Message}");
                    }

                    try
                    {
                        ser.Dispose();
                    }
                    catch (Exception e)
                    {
                        _status?.Invoke($"释放烧录机串口异常: {e.Message}");
                    }
                }
            }
        }

        // 语义上不可达：循环内必然 return（成功/失败结果）或 throw（取消）；保留语句仅为通过编译
        throw new UnreachableException("ExecuteAsync 循环内必然返回或抛出");
    }

    /// <summary>
    /// 轮询等待烧录完成（v0.3.0 新增；v0.5.0 支持 U 查询开关；v0.6.0 起为唯一等待方式）：发 P 后按固定间隔轮询 C/U 查询，
    /// 结果码 0（成功）/1（失败）判定烧录结束，2/3 与无响应/无效帧视为仍在烧录继续轮询，
    /// 超过 timeoutMs 判失败（严格超时：读窗口与等待间隔均受剩余时间限制，总耗时不超过 timeoutMs）。
    /// U 轮询时（pollingQuery=U）完成轮响应携带 UID 数据到 outcome.Uid。
    /// 终止语义由真实硬件实测确认（烧录中查询返回 2，完成后变 0；U 与 C 行为一致）。
    /// </summary>
    private async Task<BurnOutcome> WaitForBurnCompletionAsync(
        ISerialChannel ser, BurnRequest request, CancellationToken ct, int intervalMs, int timeoutMs,
        PollingQueryKind pollingQuery)
    {
        var sw = Stopwatch.StartNew();
        var query = pollingQuery == PollingQueryKind.U
            ? BurnProtocol.BuildUidQueryCommand(request.BurnId, request.Channels)
            : BurnProtocol.BuildQueryCommand(request.BurnId, request.Channels);
        var round = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            round++;

            // 严格超时：剩余时间不足则立即判定超时，不发起新一轮查询
            var remainingMs = timeoutMs - sw.ElapsedMilliseconds;
            if (remainingMs <= 0)
            {
                var msg = $"烧录超时：{timeoutMs}ms 内未查询到完成结果";
                _status?.Invoke(msg);
                return new BurnOutcome(false, BurnResultKind.Timeout, msg);   // 审计 BM-02：超时独立 Kind
            }

            ser.ResetInputBuffer();   // 审计 BM-05：先清残留再发查询——设备"即时回包"（响应在 Write 返回前已到达）不被误清
            ser.Write(query);

            // 单轮读窗口不超过剩余时间（保证总耗时严格 ≤ timeoutMs）
            var windowMs = (int)Math.Min(PollingReadWindowMs, remainingMs);
            var response = await ReadResponseAsync(ser, ct, windowMs, PollingReadPollMs);
            if (!string.IsNullOrEmpty(response))
            {
                _status?.Invoke($"轮询查询（第{round}次）: {response}");
            }

            BurnStatus? status;
            IReadOnlyList<byte>? uid = null;
            if (pollingQuery == PollingQueryKind.U)
            {
                // U 轮询：结果码与 UID 同帧解析（复用 ParseUidResponse 的回显校验与容错语义）
                var r = BurnProtocol.ParseUidResponse(response, request.BurnId);
                status = r.Base.Status;
                uid = r.Uid;
            }
            else
            {
                status = BurnProtocol.ParseQueryStatus(response, request.BurnId);
            }

            switch (status)
            {
                case BurnStatus.Success:
                    _status?.Invoke("烧录成功");
                    return new BurnOutcome(true, BurnResultKind.Success, "烧录成功") { Uid = uid };

                case BurnStatus.Failed:
                    _status?.Invoke("烧录失败");
                    return new BurnOutcome(false, BurnResultKind.Failure, "烧录失败") { Uid = uid };
            }

            // 结果码 2/3（仍在烧录）或帧无效/无响应：继续轮询，等待间隔不超过剩余时间
            var remainingAfterRead = timeoutMs - sw.ElapsedMilliseconds;
            if (remainingAfterRead <= 0)
            {
                var msg = $"烧录超时：{timeoutMs}ms 内未查询到完成结果";
                _status?.Invoke(msg);
                return new BurnOutcome(false, BurnResultKind.Timeout, msg);   // 审计 BM-02：超时独立 Kind
            }

            _status?.Invoke($"烧录进行中（第{round}次查询，已等待{sw.ElapsedMilliseconds}ms）");
            await Task.Delay(Math.Min(intervalMs, (int)remainingAfterRead), ct);
        }
    }

    /// <summary>校验轮询参数（范围与 BurnTimeSeconds 规则对齐）</summary>
    private static void ValidatePollingParameters(int pollingIntervalMs, int pollingTimeoutMs)
    {
        if (pollingIntervalMs < MinPollingIntervalMs || pollingIntervalMs > MaxPollingIntervalMs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollingIntervalMs),
                $"轮询间隔必须在{MinPollingIntervalMs}-{MaxPollingIntervalMs}ms之间");
        }

        if (pollingTimeoutMs < MinPollingTimeoutMs || pollingTimeoutMs > MaxPollingTimeoutMs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollingTimeoutMs),
                $"轮询超时必须在{MinPollingTimeoutMs}-{MaxPollingTimeoutMs}ms之间");
        }
    }

    /// <summary>
    /// 读响应：累积缓冲，总超时 windowMs，收到换行（帧边界）提前结束；
    /// 读取粒度 pollMs（轮询模式传 20ms 以免白等一拍）。
    /// 审核修复：按协议规格 §2.3 切帧——粘包（缓冲含多帧）时只取第一帧（到第一个 \n 为止），
    /// 余量丢弃（查询为 request-response 模式，下次查询前会 ResetInputBuffer），避免整块解析误判。
    /// </summary>
    private static async Task<string> ReadResponseAsync(
        ISerialChannel ser, CancellationToken ct, int windowMs, int pollMs)
    {
        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < windowMs)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = ser.ReadAvailable();
            if (chunk.Length > 0)
            {
                var newline = chunk.IndexOf('\n');
                if (newline >= 0)
                {
                    sb.Append(chunk[..(newline + 1)]);   // 只取到第一个换行（含），丢弃粘包余量
                    break;
                }

                sb.Append(chunk);
            }

            await Task.Delay(pollMs, ct);
        }

        return sb.ToString().Trim();
    }
}
