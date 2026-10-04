# Batch6A BurnMachine 验证记录

> 生成：2026-10-03 · 规格：docs/specs/done/batch6a-burnmachine.md · 状态：完成（提交 `8388a84`）
> 原始输出：batch6a-burnmachine-red.txt、batch6a-burnmachine-green.txt、batch6a-burnmachine-check.txt

## AC ↔ 验证映射

| 验收标准 | 做法 | 结果 | 原始输出 |
|---|---|---|---|
| AC-1 BM-02 | 核对协议重试条件与 Timeout 直接返回控制流 | 文案已澄清：终态 Timeout 不重放，IO 异常仍走异常重试 | check |
| AC-2 BM-03 | 版本字段/安装示例/历史与本批实测数核对 | 0.8.2 与 README 对齐；测试数 134；历史发布日期来源明确 | green |
| AC-3 BM-04 | `Execute_ConcurrentCaseVariantSameSerial_Serialized`，断言 COM3/com3 gate 串行且原始值传给 Open | mutation Red: MaxActive=2；Green: Passed 1/1 | red / green |
| AC-4 BM-05 | 本地 NuGet feed restore 与 assets 检查 | runner 精确 3.0.0；无 NU1603；未联网 | green |
| AC-5 BM-06 | 双向索引、实体路径和 batch-H done 归档核对 | 结构通过，Batch6A SPEC 已归档 | check |
| AC-7 BM-07 timeout | `BurnWorker_NoResponse_IsTimeoutFailure` 与 `BurnWorker_BurnInProgressTimeout_DoesNotReplay`；验证两个 Timeout 出口都不重发 F/P | 两个 TimeoutException mutants 均 Red，恢复后两项 Green | red / green |
| AC-8 BM-07 poll IO | `BurnWorker_PollingReadIOException_RetriesFullSequence`；验证一次 Read IOException 后整轮重试 | no-retry mutant Red，恢复后 Green | red / green |
| AC-6 | `dotnet test BurnMachine.sln --no-restore` | Passed 134 / Failed 0 / Skipped 0 | green |

## Red（失败先行）

- BM-04：在共享工作树中规范化 gate 已存在后，Lead 临时将键还原为精确 `BurnSerial`；case-variant 用例失败，MaxActive 实际为 2、期望 1。源码随即恢复。
- BM-04 的实现/测试在 Lead 接手前已同时出现在共享工作树；原始作者及先行顺序不可追溯，因此该 Red 明确记为受控 mutation 验证，不声称原始 contributor 的提交顺序。
- BM-07 读窗结束 Timeout mutant：无响应用例中将 `remainingAfterRead <= 0` 的 Timeout 返回改为 `TimeoutException` 后，F/P 计数 Expected 1 / Actual 2，测试失败。
- BM-07 下一轮前 Timeout mutant：持续结果码 2 用例中将 `remainingMs <= 0` 的 Timeout 返回改为 `TimeoutException` 后，F/P 计数 Expected 1 / Actual 2，测试失败。
- BM-07 poll IO no-retry mutant：禁用 IOException retry 后，Open 次数 Expected 2 / Actual 1，测试失败。
- 所有临时生产源码 mutant 已立即恢复。

## Green（通过）

- BM-04 目标用例 Passed 1 / Failed 0 / Skipped 0。
- BM-07 三个目标用例（两个 Timeout 分支及 poll IOException retry）Passed 3 / Failed 0 / Skipped 0。
- 本地源 `C:\Users\Administrator\.nuget\packages` 的 restore 成功；runner assets 解析为 3.0.0，无 NU1603。
- 完整解决方案 `dotnet test BurnMachine.sln --no-restore` Passed 134 / Failed 0 / Skipped 0；README 当前测试数同步为 134。

## 未达成 / 待确认

无。Batch6A AC-1~8 均通过；提交 `8388a84`；根 TODO BM-02~07 已归档。

## 残余风险与后续

不进行真实设备测试、包发布或远程 NuGet 访问。v0.8.0 为轻量标签，v0.8.1 无发布标签；README 已区分可证的提交时间与实际发布日期。
