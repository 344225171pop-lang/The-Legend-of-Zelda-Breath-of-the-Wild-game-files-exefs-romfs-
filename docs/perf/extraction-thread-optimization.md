# exefs / romfs 提取线程优化日志（v1.6 → v1.7）

- 日期：2026-11-23
- 范围：BOTW Extractor 提取核心（`core/Extractor.Core`）的 exefs 与 romfs 导出链路
- 目标：在不改变输出结果（目录结构 + 文件内容字节级一致）的前提下，通过多线程并发显著提升 exefs / romfs 导出速度
- 结论：真实 14.12 GiB 镜像提取 **60.0 s → 36.0 s（1.7x）**，反向顺序复测 56.8 s → 35.9 s（1.6x），输出逐字节一致；84/84 测试通过。详见 [extraction-benchmark-report.md](extraction-benchmark-report.md)

---

## 1. 优化前链路分析（v1.6）

```text
GameExtractor.Run
 ├─ ResolveNca (NSP/NSZ → 临时 NCA)
 ├─ ExtractSection(Code → exefs)     ← 同步，完全结束后
 └─ ExtractSection(Data → romfs)     ← 才开始
        └─ foreach entry: ExtractFile  ← 单线程逐文件 1 MB 缓冲复制
```

| # | 环节 | 位置（v1.6） | 瓶颈 |
|---|------|--------------|------|
| 1 | 段间串行 | `GameExtractor.Run` | exefs 必须全部完成后 romfs 才开始；两段本可使用独立句柄并行 |
| 2 | 文件串行 | `ExtractSection` | 41,062 个 romfs 文件逐个复制，单线程无法利用多核与存储队列深度 |
| 3 | 单文件串行 | `ExtractFile` | 单个 GB 级大文件（`Pack/*.pack`）只能顺序读，耗时受单流带宽限制 |
| 4 | 无缓冲区复用 | `ExtractFile` | 每个文件重新分配 1 MB 缓冲 |
| 5 | 进度粒度 | `ExtractSection` | 每 200 文件上报，无法反映并行吞吐 |

## 2. 优化方案

### 2.1 线程模型

```text
GameExtractor.Run
 ├─ LongRunning Task: exefs (小并行度)
 └─ LongRunning Task: romfs (主并行度)        ← 两段同时执行
        └─ ParallelSectionCopier.Copy
             ├─ 单线程：枚举条目、预建目录、预分配大文件、构建任务
             └─ N 个 LongRunning worker：从 ConcurrentQueue 取任务复制
```

- 默认并行度 `min(逻辑核心数, 6)`，可通过 `ExtractOptions.Parallelism` 覆盖（`1` = 串行路径，便于基准对比与慢速存储）。
- exefs 与 romfs 同时执行时，exefs 分配 `min(2, maxPar/2)`、romfs 获得剩余配额，避免小任务抢占大任务。
- worker 使用 `TaskCreationOptions.LongRunning`（专用线程）而非线程池，避免长期阻塞造成的线程池饥饿与启动延迟。

### 2.2 线程安全边界：每个 worker 独立 NCA 会话

LibHac 的 `IFileSystem` 不是线程安全的。方案为**每个 worker 独立建立一套读取栈**：

```text
LocalStorage(base.nca) → Nca → OpenFileSystem(Code/Data)      ← worker 1
LocalStorage(base.nca) → Nca → OpenFileSystem(Code/Data)      ← worker 2
...
（有 patch 时：LocalStorage(update.nca) → Nca(patch) → OpenFileSystemWithPatch）
```

- 各会话持有独立的文件句柄、解密上下文与 sector cache，彼此无共享可变状态。
- `KeySet` 只读共享，安全。
- 枚举只做一次（单线程临时会话），结果以 `SourceEntry` 列表传递，避免重复解析 romfs 元数据。

### 2.3 大文件分块并行

- 文件大于 `ChunkThresholdBytes`（默认 32 MB）时，按 `min(并行度×2, 文件大小/阈值)` 切分为等长 chunk。
- 主线程预创建文件并 `SetLength` 预分配，worker 用 `RandomAccess.Write(SafeFileHandle, buffer, offset)` 按固定偏移写入，不共享文件位置指针。
- 小文件保持"整文件单 worker"路径（`FileMode.Create` + 顺序写），避免定位写开销。
- 任务按大小降序入队：大任务先启动，小文件填充空闲 worker，缩短尾部。

### 2.4 内存复用与 IO

- 每个 worker 复用一块 1 MB 读缓冲（不再每文件分配）。
- 顺序写使用 `FileOptions.SequentialScan` 提示，分块写使用 `FileOptions.RandomAccess`。
- 进度按累计字节阈值（默认 32 MB）节流上报，避免高频跨线程回调。

### 2.5 进度 / 取消 / 异常

- 进度：`Interlocked` 累计字节与**真实完成文件数**（分块文件在最后一个 chunk 完成时才计数），romfs 额外上报百分比；exefs 不上报百分比，避免与 romfs 进度条互相覆盖。
- 取消：worker 每读一块检查 `CancellationToken`；`CreateLinkedTokenSource` 保证任一线程出错时其余线程立即停止。
- 异常：首个异常被记录并触发内部取消，`Task.WaitAll` 后重新抛原始异常（优先抛出非 `OperationCanceledException`），不会吞错或死锁。

## 3. 正确性保障

新增 `ParallelSectionCopierTests`（10 个测试，纯内存数据源，不依赖 Android/LibHac）：

| 场景 | 验证点 |
|------|--------|
| 小文件树 | 内容逐字节、嵌套目录、空目录、中文文件名 |
| 大文件分块 | 300 KB / 64 KB 阈值 → 多 chunk 拼接后与源完全一致 |
| 分块边界 | 大小恰为阈值整数倍时无多余/缺失字节 |
| 串并行一致 | 同一数据集 `Parallelism=1` 与 `6` 输出树完全相同 |
| 空文件 / 空清单 | 空文件正常创建；空清单为 no-op 且 worker=0 |
| 预先取消 | 立即抛 `OperationCanceledException` |
| 复制中取消 | 达到阈值后取消，抛异常且不死锁 |
| 源读取失败 | 异常穿透到调用方且包含原始信息 |
| 会话数量 | worker 数 ≤ 配置并行度，会话按 worker 复用 |
| 进度回调 | 最终回调值等于真实文件数与总字节 |

全量测试：**84/84 通过**（原 74 + 新增 10）。

端到端一致性：`perf/ExtractBench` 用真实游戏镜像分别跑串行（v1.6 算法副本）与并行（v1.7），比较文件数、总字节、路径+大小以及抽样 SHA256（含最大的 40 个文件），结果见测试报告。

## 4. 变更文件清单

| 文件 | 变更 |
|------|------|
| `core/Extractor.Core/ParallelSectionCopier.cs` | **新增**：并行任务调度、分块规划、worker 池、进度/取消/异常处理 |
| `core/Extractor.Core/GameExtractor.cs` | exefs/romfs 并行执行；新增每 worker 独立 NCA 会话工厂；新增 `ExtractOptions.Parallelism`；移除串行 `ExtractSection`/`ExtractFile` |
| `android/BotwExtractor.Tests/ParallelSectionCopierTests.cs` | **新增** 10 个并发正确性测试 |
| `android/BotwExtractor.Tests/BotwExtractor.Tests.csproj` | 共享 `ParallelSectionCopier.cs` 编译 |
| `perf/ExtractBench/` | **新增** 真实镜像串行 vs 并行端到端基准 |
| `android/BotwExtractor/BotwExtractor.csproj` | 版本 1.6 → 1.7（versionCode 8） |

## 5. 复现命令

```powershell
$env:NUGET_PACKAGES='E:\botw_work\nuget_packages'
$env:DOTNET_CLI_HOME='E:\botw_work\dotnet_home'
# 单元 / 集成测试
E:\botw_work\dotnet\dotnet.exe test E:\botw_work\android\BotwExtractor.Tests\BotwExtractor.Tests.csproj -c Release
# 端到端基准（首次需 --prepare 解包镜像）
E:\botw_work\dotnet\dotnet.exe run --project E:\botw_work\perf\ExtractBench\ExtractBench.csproj -c Release -- --prepare
E:\botw_work\dotnet\dotnet.exe run --project E:\botw_work\perf\ExtractBench\ExtractBench.csproj -c Release
```

## 6. 已知限制

- 并行会同时打开 `并行度` 份 NCA 读取栈，每个会话需解析一次 romfs 元数据并持有独立的缓存，内存占用随并行度线性增长（约几十 MB 量级），默认上限 6 已考虑移动设备内存。
- 大文件分块并行会打散单文件的顺序 IO；对机械硬盘或极慢存储，可通过 `ExtractOptions.Parallelism = 1` 回到串行路径。
- 输出正确性与串行完全一致，但**文件写入顺序**不同，因此中断后残留的"半成品文件"集合可能与串行不同（两者都会残留，均需重新提取）。
