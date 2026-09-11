using BurnMachine;
using BurnMachine.Channel;
using Xunit;

namespace BurnMachine.Tests;

/// <summary>
/// 轮询边界常量的公开性（IN-03 修复：宿主仓此前只能复制私有常量 100/600000，
/// SDK 升级改边界后宿主运行期才会暴露）。断言公开常量存在、取值正确，
/// 且与 ValidatePollingParameters 的实际校验边界一致——改边界即测试红。
/// </summary>
public class BurnWorkerPollingBoundsTests
{
    [Fact]
    public void PollingTimeoutBounds_ArePublicConstants_WithDocumentedValues()
    {
        // 规格：burn_time_seconds 映射的轮询总超时合法范围 [100ms, 600000ms]（= 0.1s ~ 600s）
        Assert.Equal(100, BurnWorker.MinPollingTimeoutMs);
        Assert.Equal(600_000, BurnWorker.MaxPollingTimeoutMs);
    }

    [Fact]
    public async Task ValidatePollingParameters_UsesPublicBounds_AsInclusiveRange()
    {
        // 边界值合法（闭区间）：无响应 mock 下走的是超时判定，不得抛参数越界
        var atMin = await NewWorker().ExecuteAsync(
            NewRequest(), CancellationToken.None, pollingTimeoutMs: BurnWorker.MinPollingTimeoutMs);
        Assert.Equal(BurnResultKind.Timeout, atMin.Kind);

        // 下限 -1 → 参数越界（证明公开下限即真实校验下界）
        var below = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => NewWorker().ExecuteAsync(
                NewRequest(), CancellationToken.None, pollingTimeoutMs: BurnWorker.MinPollingTimeoutMs - 1));
        Assert.Contains("轮询超时", below.Message);

        // 上限 +1 → 参数越界（证明公开上限即真实校验上界）
        var above = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => NewWorker().ExecuteAsync(
                NewRequest(), CancellationToken.None, pollingTimeoutMs: BurnWorker.MaxPollingTimeoutMs + 1));
        Assert.Contains("轮询超时", above.Message);
    }

    private static BurnWorker NewWorker() => new(() => new MockSerialChannel());

    private static BurnRequest NewRequest() => new("COM3", "00881289", "0765", 0.1);
}
