# Batch6A：BurnMachineSDK 重试、互斥与文档基线（BM-02~07）

> 状态：in-progress · 日期：2026-10-03

## 目标

修正 BurnMachine 文档与运行时重试语义不一致，关闭 BM-03~05 的版本、串口 gate 和测试依赖漂移，并补齐本仓 SPEC/evidence 索引及 done 规格归档。

## 范围与非目标

范围内：
- `src/BurnMachine/BurnWorker.cs`：只规范化进程内串口 gate 的键
- `tests/BurnMachine.Tests/BurnWorkerTests.cs`：大小写变体互斥用例
- `tests/BurnMachine.Tests/BurnMachine.Tests.csproj`：固定测试 runner 版本
- `src/BurnMachine/BurnMachine.csproj`：产品版本 0.8.1→0.8.2
- `README.md`、`docs/XW16Pro扩展串口控制协议.md`
- `docs/specs/README.md`、`docs/specs/done/README.md`、`docs/evidence/README.md`、本 SPEC 和验证证据
- 将已标 `done` 的 `docs/specs/batch-h-burn-retry-semantics.md` 迁入 `docs/specs/done/`
- 工作区根 `TODO.md` 中 BM-02~07 条目

非目标：改变公开 API/串口协议/重试算法、修订未标完成的 `polling-uid.md` 与 `timing-tighten.md` 内容、真实串口/硬件验证、包发布、push，以及其他仓库。

## 验收标准

- **AC-1（BM-02）**：协议第 12 章明确轮询超时返回终态 Timeout、不重发 F/P；轮询阶段 IO 异常等进入异常分支的失败仍按现有语义重试。文案必须与 `BurnWorker.ExecuteCoreAsync` 和 `WaitForBurnCompletionAsync` 路径一致。
- **AC-2（BM-03）**：产品版本改为 0.8.2，README 安装示例及历史补齐 v0.8.0/v0.8.1/v0.8.2；测试数只填写本批实际测试发现数，旧版本历史数字保持历史值，变更摘要由 Git 历史核实，不臆造。
- **AC-3（BM-04）**：gate 键大小写不敏感，传给 `ISerialChannel.Open` 的原始 `BurnSerial` 不改变。新增 `Execute_ConcurrentCaseVariantSameSerial_Serialized`，以两个 `TaskCreationOptions.LongRunning` 执行和现有 `SharedConcurrencyProbe` barrier 检验 COM3/com3 的 `MaxActive==1`，并断言 Open 收到原始大小写；保留不同串口并行用例。当前未规范化实现下 probe 应观察到重叠并 Red，之后最小规范化实现后 Green。
- **AC-4（BM-05）**：`xunit.runner.visualstudio` 精确声明已由当前资产解析并实测可用的 3.0.0；restore/build 输出无 NU1603，assets 解析版本与声明一致。
- **AC-5（BM-06）**：新增规格、done、evidence 索引，登记全部当前 Markdown 文档实体；`burn-polling.md` 按其现有头部登记为退役历史参考，`polling-uid.md` 与 `timing-tighten.md` 保持现有规格状态；batch-H 规格迁入 done 并附决策记录。所有索引链接可解析、实体与索引双向一致。
- **AC-6**：全量 `dotnet test BurnMachine.sln` 通过；README 当前测试数等于实测发现数；BM-02~07 独立 reviewer 核对通过。
- **AC-7（BM-07 timeout 不重放）**：`BurnWorker_NoResponse_IsTimeoutFailure` 使用最小合法 100ms 超时覆盖读窗耗尽分支；新增 `BurnWorker_BurnInProgressTimeout_DoesNotReplay` 连续返回结果码 2，覆盖下一轮开始前的超时分支。两种情形均断言 F/P 各只发送一次。分别将对应 Timeout return 临时突变为 `TimeoutException` 并进入 catch 重试时，目标断言必须 Red。
- **AC-8（BM-07 轮询 IO 异常重试）**：新增 `BurnWorker_PollingReadIOException_RetriesFullSequence`；首轮轮询 `ReadAvailable` 抛一次 IOException，第二轮返回成功响应；断言成功、工厂/open 共两次且 F/P 各两次。临时禁止异常重试时用例必须 Red。测试只用 fake channel，不改生产重试逻辑。

## AC ↔ 测试/验证映射

| 验收标准 | 测试/验证 | 位置 |
|---|---|---|
| AC-1 | 对照 Timeout 直接返回路径、异常重试 catch 与 `MachineWorker.Core.Tests.BurnStepTests.Execute_Timeout_DoesNotRetry`；协议第 12 章静态核对 | BurnMachine `BurnWorker.cs`；MachineWorker 现有测试仅作既有语义佐证，不在本批修改 |
| AC-2 | `dotnet test BurnMachine.sln` 实测发现数；csproj/README 版本与历史交叉核对 | BurnMachine.csproj、README.md |
| AC-3 | `Execute_ConcurrentCaseVariantSameSerial_Serialized`（COM3/com3 的 `MaxActive==1` 且 Open 保留原始大小写）；`Execute_DifferentSerial_NotBlocked`（不同端口 `MaxActive>=2`）；gate key mutant Red | `tests/BurnMachine.Tests/BurnWorkerTests.cs` |
| AC-4 | restore/测试输出中无 NU1603，实际解析 3.0.0 | `tests/BurnMachine.Tests/BurnMachine.Tests.csproj` |
| AC-5 | Markdown 实体清单与 `docs/specs/README.md`、`done/README.md`、`docs/evidence/README.md` 双向链接检查 | BurnMachine docs |
| AC-7 | `BurnWorker_NoResponse_IsTimeoutFailure`（读窗耗尽）与 `BurnWorker_BurnInProgressTimeout_DoesNotReplay`（下一轮前 Timeout）；各断言 F/P 一次，并分别验证 TimeoutException mutant Red | `tests/BurnMachine.Tests/BurnWorkerTests.cs` |
| AC-8 | `BurnWorker_PollingReadIOException_RetriesFullSequence`（一次 Read IOException 后成功且 F/P 各两次；no-retry mutant Red） | `tests/BurnMachine.Tests/BurnWorkerTests.cs` |
| AC-6 | `dotnet test BurnMachine.sln`，报告实际 Passed/Failed/Skipped | BurnMachine solution |

## 数据流与接口变化

`BurnRequest.BurnSerial` 原值继续传给串口 `Open`；仅 `SerialGates` 查询使用 `ToUpperInvariant()` 规范化键，使同一进程中 COM3/com3 互斥。未改变公共 API、协议帧、持久化或序列化。NuGet 版本元数据更新到 0.8.2；测试 runner 固定版本，不发布包。

## 边界情况与失败处理

- COM3/com3 必须串行；COM3/COM4 必须继续并行；被取消的请求仍使用原 cancellation 语义。
- Timeout 仍是终态，不触发整轮重放；轮询 IO 异常仍进入现有异常重试分支。
- README 测试数取当前完整测试执行的真实发现数，不改写 v0.7.1/v0.7.2 历史测试数。
- package restore 前检查本地 3.0.0 cache 与源配置；若需要访问远端 NuGet 源，停止并单独征求网络确认。不得安装新工具或发布包。
- 无真实串口、硬件或用户数据操作。

## 测试方案

沿用仓库现有 xUnit 2 / `BurnMachine.sln`。AC-3 用例先对当前 gate 缺陷 Red，再规范化 key 后 Green；AC-7/8 用指定的 Timeout/禁用重试 mutant 验证计数与异常路径 Red，恢复生产逻辑后 Green。BM-05 通过本地 NuGet 缓存离线 restore，禁止远端源访问；随后运行全量测试。Red/Green/结构检查输出写入 `docs/evidence/batch6a-burnmachine-*.txt` 与 `.verify.md`。

## 假设与风险

风险为 M：一处内部互斥键行为、版本/测试依赖和文档索引，无公开接口或数据变更。版本 patch 0.8.2 只更新仓库元数据，不代表获准发布。Batch6A 独立于其他仓库，可单批回滚；提交/发布/push 均需另行确认。

## 变更记录

| 日期 | 变化点 | 重新确认结果 |
|---|---|---|
| 2026-10-03 | 按 BM-02~07 现场复核结果细化范围与 AC | 用户批准 Batch6A |
| 2026-10-03 | 独立复核发现 SDK 层 Timeout 重放与 poll IOException 重试缺少区分性断言；补入 BM-07 | 用户批准扩展 |

## 决策记录（完成后追加）

- 决策日期：
- 决策：
- 理由：
- 被放弃的备选：
- 未达成的 AC：
