# 日志导出性能优化日志（v1.5 → v1.6）

- 日期：2026-11-23
- 范围：BOTW Extractor Android 应用「设置 → 导出日志」完整链路
- 目标：在不改变导出结果（字节级一致）的前提下，显著提升导出执行速度、降低内存分配、消除 UI 线程阻塞
- 结论：100k 条端到端提速 1.7x、1M 条提速 1.8x；内存分配降低 66%–77%；范围筛选（最近 24 小时）提速 6.9x–26.7x；导出写入完全移出 UI 线程。74/74 单元/集成测试通过，5 档数据量字节一致性校验全部通过。

---

## 1. 优化前链路分析（v1.5）

导出流程：`ShowRangeDialog → ShowPreview → StartExport(SAF 选择文件) → OnActivityResult 写文件`。

| # | 环节 | 位置（v1.5） | 瓶颈 |
|---|------|--------------|------|
| 1 | 数据查询 | `Ui/LogStore.cs` `Filter` | `lock` 内 LINQ `Where(...).ToList()`：每次调用分配闭包+迭代器，全表 O(n) 扫描，结果 List 从 0 反复扩容 |
| 2 | 格式转换 | `Ui/LogStore.cs` `LogExporter.Format` | 每行 5 段字符串拼接（`"[" + ts + "] [" + level + "] " + msg`）产生大量临时串；`StringBuilder` 默认容量 16 起步反复倍增拷贝；`ToString()` 再全量拷贝一次 |
| 3 | 预览 | `SettingsActivity.ShowPreview` | `Format` 生成全文巨串后，`Encoding.UTF8.GetByteCount(content)` 再做一次全量遍历；全文同时显示在预览 TextView |
| 4 | IO 写入 | `SettingsActivity.OnActivityResult` | **UI 线程**同步 `StreamWriter`（默认 1KB 缓冲）`.Write(_pendingExport 巨串)`：大日志时阻塞主线程造成卡顿/ANR 风险；巨串在预览后一直驻留内存 |

## 2. 优化方案与实施

### 2.1 数据查询：二分快路径 + 预分配（`Ui/LogStore.cs`）
- 新增 `_sorted` 标志：`Add(LogEntry)` 时若新时间戳早于队尾则置 false（`Add(string,string)` 用 `DateTime.Now` 天然单调；乱序注入/并发交错自动降级）。
- `Filter` 有序时走两次二分（`LowerBound(from)` / `UpperBound(to)`）定位闭区间边界，O(log n) 定位 + O(k) 拷贝，结果 List 按命中数**一次性预分配**；乱序时退化为单次线性扫描（仍无 LINQ）。
- `Clear()` 重置 `_sorted = true`，清空后恢复快路径。
- 移除 `System.Linq` 依赖，消除闭包/迭代器分配。

### 2.2 格式转换：流式 `WriteTo` 成为唯一格式源（`Ui/LogStore.cs`）
- 新增 `LogExporter.WriteTo(TextWriter, entries, ctx)`：头部 10 行 + 逐行条目直接写入任意 `TextWriter`，**不再生成中间巨串**。
- 行格式化采用**可复用 char 缓冲**（内存复用）：`DateTime.TryFormat` 直接写入缓冲（零时间戳字符串分配），level/message 以 `Span.CopyTo` 拷入，每行仅 1 次 `Write(char[],0,len)` 调用；缓冲不足时倍增扩容。
- `Format` 改为在预容量 `StringBuilder`（`256 + n*80`）上包 `StringWriter` 委托 `WriteTo`——预览与导出文件**共用同一格式实现**，从机制上保证字节一致，且避免 StringBuilder 反复扩容。

### 2.3 IO 写入：后台异步 + 64KB 缓冲流式写（`SettingsActivity.cs`）
- 预览阶段保存 `_pendingEntries` + `_pendingCtx`（原始数据），不再长期持有导出巨串 `_pendingExport`（字段已删除）。
- `OnActivityResult` 改为 `Task.Run` 后台线程：`ContentResolver.OpenOutputStream` → `StreamWriter(UTF8 无 BOM, 64KB 缓冲)` → `LogExporter.WriteTo` 流式写入 SAF 目标；完成/失败经 `RunOnUiThread` 回主线程 Toast 与写日志（与既有 `DoClean` 模式一致）。
- 效果：UI 线程零阻塞；写入与格式化合并为单遍流式处理；64KB 缓冲将系统调用次数降为原来的约 1/64。

### 2.4 预览环节
- 预览 UX 保持不变（范围选择 → 全文预览 → 导出）；`Format` 本身已提速且分配减半。`GetByteCount` 保留以显示精确 UTF-8 大小（原生向量化实现，开销占比小）。

## 3. 迭代记录（可追溯）

| 步骤 | 内容 | 结果 |
|------|------|------|
| 1 | 实施 2.1/2.2（初版 WriteTo 为逐段 Write/WriteLine）/2.3 | 74 测试中 73 过；1 个失败为**测试自身缺陷**（null ctx 两次调用各自取 `DateTime.Now` 相差 1ms），改用固定时间上下文修复 |
| 2 | 首轮基准 | 发现初版流式写入每行 6 次小 Write 调用开销过高，"仅写入"阶段反而慢于旧巨串 Write（0.0x–0.4x）；端到端仅 1.3x |
| 3 | WriteTo 改为复用 char 缓冲 + `TryFormat` + 每行单次 Write；基准"导出核心"改为同口径（格式化+写入）对比 | 74/74 通过；100k/1M 端到端 1.7x/1.8x，分配降 73%/77% |
| 4 | 提高小数据量迭代次数（20 次）并补充 5000 条档位（应用真实容量上限 `DefaultCapacity=5000`） | 最终数据见 `export-benchmark-report.md` |

## 4. 正确性保障

- 新增 `android/BotwExtractor.Tests/ExportOptimizationTests.cs`（13 个测试）：内置 v1.5 旧算法**参考副本**，逐项断言新实现输出与旧实现完全一致——
  - Format：null 输入、空集合、null 字段、中文/内嵌换行/超长消息、500 条固定种子随机数据（含 UTF-8 字节比较）；
  - WriteTo：与 Format/旧算法逐字节一致、UTF-8 无 BOM 流式输出字节一致；
  - Filter：有序/乱序（固定种子洗牌）/容量裁剪后/清空重建/重复时间戳/空库/从>到/边界闭区间，与旧 LINQ 结果逐项引用相等。
- 全套件 **74/74 通过**（原 61 + 新增 13）。
- 基准程序在 1k/5k/10k/100k/1M 每档先做**新旧完整管线输出字节比对**，不一致即抛异常终止——全部通过。
- 格式契约未变：时间戳 `yyyy-MM-dd HH:mm:ss.fff`、10 行头部、`[ts] [LEVEL] message` 行、`Environment.NewLine` 换行、UTF-8 无 BOM。

## 5. 变更文件清单

| 文件 | 变更 |
|------|------|
| `android/BotwExtractor/Ui/LogStore.cs` | Filter 二分快路径+预分配；Add/Clear 维护有序标志；LogExporter 新增 WriteTo（char 缓冲流式），Format 委托 WriteTo |
| `android/BotwExtractor/SettingsActivity.cs` | 导出改后台异步 64KB 缓冲流式写 SAF；删除巨串字段；版本回退值 1.6 |
| `android/BotwExtractor.Tests/ExportOptimizationTests.cs` | 新增 13 个正确性测试 |
| `perf/ExportBench/` | 新增可复现基准工程（共享生产 Ui 代码 + 旧算法基线副本） |
| `docs/perf/` | 本优化日志 + 性能测试报告 |
| `android/BotwExtractor/BotwExtractor.csproj` | 版本 1.5→1.6（versionCode 6→7） |

## 6. 复现命令

```powershell
$env:NUGET_PACKAGES='E:\botw_work\nuget_packages'
$env:DOTNET_CLI_HOME='E:\botw_work\dotnet_home'
# 正确性测试
E:\botw_work\dotnet\dotnet.exe test E:\botw_work\android\BotwExtractor.Tests\BotwExtractor.Tests.csproj -c Release
# 性能基准
E:\botw_work\dotnet\dotnet.exe run --project E:\botw_work\perf\ExportBench\ExportBench.csproj -c Release
```
