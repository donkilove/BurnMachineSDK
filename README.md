# BurnMachineSDK

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

基于 .NET 10 的 XW16Pro 烧录机串口通信库：指令构造、响应解析与单点烧录执行时序（9600 8N1）。

本库从 [BurnMachineHost](https://github.com/donkilove/BurnMachineHost) 机台烧录上位机中提取并发布为可复用组件，让多个上位机应用可以共享一套经过充分验证的烧录机通信栈，而无需重复维护源代码。

## 功能特性

- **指令构造** —— 清空（`` `F ``）/ 烧录（`` `P ``，支持通道掩码与可选条码）/ 查询（`` `C ``）/ UID 扩展查询（`` `U ``）指令按协议规格生成
- **响应解析** —— 五段帧校验、回显序列号校验（防串扰/粘包错位）、结果码精确匹配（0=成功）、帧长上限 256
- **结构化结果** —— `ParseResponseDetailed` 解析镜像号/主校验和/剩余次数/结果码（0/1/2/3）；`ParseUidResponse` 解析 UID 数据区（需固件 > 20240103000000）
- **通道选择** —— `ChannelMask`（A / B / 双通道），清空/烧录/查询全程按所选通道发送
- **条码写入** —— 烧录时可选携带条码字节（ASCII hex），大小端由镜像配置决定
- **单点烧录执行器 `BurnWorker`** —— 清空→烧录→查询完整时序，整轮最多尝试 2 次（失败自动重试 1 次，间隔 1 秒），协作式取消；UID 由轮询 U 查询完成轮响应直接带出（v0.6.0 起独立 UID 查询已退役）
- **指令级日志（v0.6.1）** —— 发送清空/烧录指令时经状态回调输出指令原文（如 `发送清空指令: `F00881289|00000001`），宿主执行日志可见
- **轮询等待（v0.3.0，v0.6.0 起唯一等待方式）** —— 不再固定等待烧录时间：按固定间隔轮询查询，结果码 0/1 判定完成、2/3 与无响应继续、超时判失败（默认间隔 30ms / 超时 3.5s，可配；行为经真实硬件验证）
- **时序实测收紧（v0.6.1~v0.6.3）** —— 清空→烧录间隔 100ms → 30ms → 10ms、轮询间隔 100ms → 50ms → 30ms，均基于真机实测（COM9/00911007，9600 波特）：清空 0ms 间隔即生效（结果码 2）、30ms 轮询 16~17 轮零丢帧
- **轮询 U 查询开关（v0.5.0）** —— `pollingQuery: PollingQueryKind.U` 让轮询循环改发 `U` 查询：结果码判定不变，烧录完成的那轮响应直接携带芯片 UID 到 `outcome.Uid`，无需完成后再补查一次（需固件 > 20240103000000；旧固件 U 无响应将超时判失败）
- **粘包/半包防护** —— 累积缓冲 + 换行帧切分，粘包只取第一帧，半包累积到帧边界
- **可注入串口通道** —— `ISerialChannel` 抽象 + `SerialPortChannel`（System.IO.Ports）真实实现 + `MockSerialChannel` 可编程模拟（离线开发/测试）

## 安装

```bash
dotnet add package BurnMachineSDK --version 0.8.2 \
  --source "https://nuget.pkg.github.com/donkilove/index.json"
```

> GitHub Packages 源需要认证，请配置具有 `read:packages` 权限的令牌。

## 快速开始

```csharp
using BurnMachine;
using BurnMachine.Channel;

var worker = new BurnWorker(() => new SerialPortChannel(), Console.WriteLine);
var outcome = await worker.ExecuteAsync(
    new BurnRequest("COM3", "00881289", "0765", burnTimeSeconds: 3)
    {
        Channels = ChannelMask.B,          // 可选：B 通道（默认 A）
        Barcode = [0x30, 0x31, 0x32],      // 可选：烧录时写入条码
    },
    CancellationToken.None);

Console.WriteLine(outcome.Success ? "烧录成功" : $"烧录失败: {outcome.Detail}");
```

轮询等待烧录完成（唯一等待方式，结果码 0/1 判定结束）：

```csharp
var outcome = await worker.ExecuteAsync(
    new BurnRequest("COM3", "00881289", "0765", burnTimeSeconds: 3),   // burnTimeSeconds 仅作记录
    CancellationToken.None,
    pollingIntervalMs: 30,       // 查询间隔，默认 30ms（30~10000；真机验证零丢帧）
    pollingTimeoutMs: 3500);     // 总超时，默认 3.5s（100~600000）；超时判失败
```

轮询改用 U 查询，烧录完成直接带回芯片 UID（需固件 > 20240103000000）：

```csharp
var outcome = await worker.ExecuteAsync(
    new BurnRequest("COM3", "00881289", "0765", burnTimeSeconds: 3),
    CancellationToken.None,
    pollingQuery: PollingQueryKind.U);   // 轮询发 U 命令；完成轮 outcome.Uid 携带 UID（无数据为 null）
if (outcome.Uid is { Count: > 0 })
{
    Console.WriteLine($"UID: {Convert.ToHexString(outcome.Uid.ToArray())}");
}
```

查询烧录结果详情（镜像号/校验和/剩余次数）：

```csharp
var result = BurnProtocol.ParseResponseDetailed(response, "00881289");
Console.WriteLine($"镜像号: {result.ImageNo}, 剩余次数: {result.RemainingCount}, 状态: {result.Status}");
```

无硬件环境可用 `MockSerialChannel` 离线开发：

```csharp
var mock = new MockSerialChannel();
mock.EnqueueResponse("`C00881289|00000001|0002|002A9717|0000000000016BC4|0\r\n");
var worker = new BurnWorker(() => mock);
```

## 协议摘要

| 指令 | 格式 | 说明 |
|---|---|---|
| 清空 | `` `F{burnId}\|{掩码8hex}\r\n `` | 清空烧录机指定通道 |
| 烧录 | `` `P{burnId}\|{掩码8hex}\|{program}[<nowiki>|</nowiki>{条码hex}]\r\n `` | 开始烧录（条码可选） |
| 查询 | `` `C{burnId}{掩码8hex}\r\n `` | 查询结果（注意无 `\|` 分隔） |
| UID 查询 | `` `U{burnId}{掩码8hex}\r\n `` | UID 扩展查询（固件 > 20240103000000） |

完整协议规格见 [XW16Pro 扩展串口控制协议](docs/XW16Pro扩展串口控制协议.md)。

## 版本历史

- **v0.8.2（待发布）** —— BM-02 明确轮询超时为终态、不触发重放；BM-03 对齐版本历史和实测测试数；BM-04 统一烧录串口 gate 的大小写键；BM-05 runner 固定到 3.0.0；BM-06 补齐规格/证据索引；BM-07 锁定 Timeout 不重放与轮询 IO 异常重试（134 测试全绿）
- **v0.8.1**（版本元数据提交：2026-10-01；Git 未找到发布标签）—— 批次 H 明确 BM-01 案A 的完整时序重试风险（纯文档/XML 注释变更；当时验证 131/131）
- **v0.8.0**（轻量标签目标提交：2026-09-29；无独立发布日期记录）—— 仓库与 NuGet 包更名为 BurnMachineSDK，PackageId、RepositoryUrl 和 README 安装命令同步；程序集与命名空间不变
- **v0.7.2**（2026-09-11）—— **公开轮询总超时边界常量**（`BurnWorker.MinPollingTimeoutMs` = 100 / `MaxPollingTimeoutMs` = 600000）：修复 IN-03——宿主仓（MachineWorker）此前只能复制私有常量做钳制，SDK 改边界后宿主运行期才暴露；新增边界一致性测试（131 测试全绿）
- **v0.7.1**（2026-08-29）—— 审计修复批次（BM-01~08 + 审核补强）：U 轮询 6 段帧缺 UID 区保留烧录判定（不再误判"烧录超时"NG）、超时独立 `BurnResultKind.Timeout`、按烧录串口键控执行互斥、状态回调异常隔离、通道掩码/条码校验、查询前先清残留、轮询文档退役标注（129 测试全绿）
- **v0.7.0**（2026-08-24）—— 升级 .NET 8 → .NET 10（TFM/CI setup-dotnet 10.0.x/依赖）；净版 net10 行为一致（119 测试全绿）
- **v0.6.3**（2026-08-22）—— 清空→烧录间隔 30ms → **10ms**；轮询间隔 50ms → **30ms**（下限同步）；真机极限压测依据（清空 0ms 三组零丢帧且烧录成功）
- **v0.6.2**（2026-08-22）—— 轮询间隔 100ms → **50ms**；清理未使用的读窗口常量
- **v0.6.1**（2026-08-22）—— 新增指令级日志（清空/烧录指令经状态回调输出）；清空→烧录间隔 100ms → **30ms**
- **v0.6.0** —— 固定等待退役，轮询为唯一等待方式；独立 UID 查询退役（UID 由轮询 U 查询带出）

## 构建与测试

```bash
dotnet build BurnMachine.sln
dotnet test BurnMachine.sln
```

## 项目结构

```
BurnMachine.sln
src/BurnMachine/            类库（net10.0，NuGet 包 BurnMachine）
├── BurnProtocol.cs         指令构造与响应解析（BurnResultKind / BurnResult / UidQueryResult）
├── BurnRequest.cs          单点烧录请求（通道/条码可选）
├── BurnOutcome.cs          单点烧录结果
├── BurnWorker.cs           清空→烧录→轮询查询执行时序（v0.6.0 起唯一等待方式；轮询可切 U 查询带出 UID）
├── PollingQueryKind.cs   轮询查询命令枚举（C 默认 / U，v0.5.0）
├── ChannelMask.cs          通道掩码与结果码枚举（v0.2.0）
├── BurnResult.cs           结构化烧录结果与 UID 查询结果（v0.2.0）
└── Channel/                串口通道
    ├── ISerialChannel.cs   通道抽象（可注入自定义实现）
    ├── SerialPortChannel.cs System.IO.Ports 实现
    └── MockSerialChannel.cs 可编程模拟通道
tests/BurnMachine.Tests/     协议 + 执行器测试（134 个，含轮询模式、加固与 U 轮询用例（v0.6.0 删固定模式与 QueryUidAsync））
```

## 许可协议

[MIT](LICENSE)
