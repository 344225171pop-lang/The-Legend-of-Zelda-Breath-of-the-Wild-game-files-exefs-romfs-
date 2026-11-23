# exefs / romfs 提取性能测试报告（v1.6 串行 vs v1.7 多线程）

- 测试日期：2026-11-23
- 测试工具：`perf/ExtractBench`（net8.0 控制台基准，直接引用生产 `Extractor.Core`；"串行"为 v1.6 实现逐字副本）
- 原始输出：`perf/extract-bench-output.md`（正向顺序）、`perf/extract-bench-output-reverse.md`（反向顺序）

## 1. 测试环境

| 项 | 值 |
|----|----|
| 运行时 | .NET 8.0.34 |
| 操作系统 | Microsoft Windows 10.0.29698 |
| CPU 逻辑核心 | 20 |
| 并行度 | auto = min(20, 6) = **6** |
| 输入 | base NCA 13.48 GiB + patch NCA 0.92 GiB（真实《旷野之息》v0 + v1.9.0 补丁） |
| 输出 | 14.12 GiB / 41,067 文件（exefs 5 文件 + romfs 41,062 文件） |

## 2. 测试方法

1. `--prepare`：将发行版 NSZ（PFS0 容器，内含 NCZ）解包为标准 NCA，并把更新 NSP 中的 Program NCA 提取出来，缓存到 `perf/cache/`（一次性，不计入耗时）。
2. 串行基线：v1.6 原始代码路径（exefs → romfs 顺序、单线程逐文件 1 MB 缓冲复制），输出到 `perf/out_serial`。
3. 并行：v1.7 `GameExtractor.Run`（exefs/romfs 并行 + 6 worker + 大文件分块），输出到 `perf/out_parallel`。
4. 一致性校验：文件数、总字节、全部 41,067 条的"路径 + 大小"，并对最大的 40 个文件 + 等距抽样共 100 个文件做 SHA256 逐字节比对（覆盖 1.97 GiB）。
5. 为排除页缓存顺序偏差，交换执行顺序再跑一轮（`--reverse`）。

## 3. 测试结果

### 3.1 正向顺序（串行 → 并行）

| 指标 | v1.6 串行 | v1.7 多线程 | 变化 |
|------|-----------|-------------|------|
| 总耗时 | **60.0 s** | **36.0 s** | **提速 1.7x（-40%）** |
| 文件数 | 41,067 | 41,067 | 一致 |
| 总字节 | 15,157,992,803 | 15,157,992,803 | 一致 |

分阶段观察（来自进度日志）：

- 串行：exefs 5 个文件先完成后才开始 romfs，romfs 全程单线程推进。
- 并行：romfs 前 10 秒即完成约 9.8 GiB（大文件分块并行 + 6 路并发），exefs 被完全隐藏在 romfs 执行期间。

### 3.2 反向顺序（并行 → 串行，抵消页缓存偏差）

| 指标 | v1.6 串行 | v1.7 多线程 | 变化 |
|------|-----------|-------------|------|
| 总耗时 | **56.8 s** | **35.9 s** | **提速 1.6x（-37%）** |
| 文件数 / 总字节 | 41,067 / 15,157,992,803 | 相同 | 一致 |

两轮结果高度一致（串行 56.8–60.0 s，并行 35.9–36.0 s），说明提升来自并行本身，而非执行顺序带来的页缓存红利。

### 3.3 输出一致性

| 校验项 | 正向 | 反向 |
|--------|------|------|
| 文件数 | 41,067 = 41,067 ✅ | 41,067 = 41,067 ✅ |
| 总字节 | 15,157,992,803 = 15,157,992,803 ✅ | 相同 ✅ |
| 路径 + 大小（全量 41,067 条） | 全部一致 ✅ | 全部一致 ✅ |
| SHA256 抽样（100 个文件 / 1.97 GiB，含最大的 40 个） | 全部一致 ✅ | 全部一致 ✅ |
| 目录结构（含空目录） | 一致 ✅ | 一致 ✅ |

### 3.4 单元 / 集成测试

```
已通过! - 失败: 0，通过: 84，已跳过: 0，总计: 84
```

其中新增 `ParallelSectionCopierTests` 10 个用例，覆盖内容一致性、嵌套目录、空文件、空目录、分块拼接、分块边界、串行/并行输出一致、预先取消、复制中取消、源读取失败、会话数量、进度回调终值。

## 4. 结论

1. **速度**：真实 14.12 GiB 镜像提取 60.0 s → 36.0 s（正向，1.7x）、56.8 s → 35.9 s（反向，1.6x），两轮均稳定在 **1.6–1.7x**；romfs 阶段的大文件（`Pack/*.pack`）因分块并行，前半程吞吐提升最明显。
2. **正确性**：41,067 个文件的路径、大小、抽样内容哈希在两轮中均与串行基线一致，格式与目录结构未变。
3. **exefs**：串行时 exefs 必须等待完成后 romfs 才开始；并行后 exefs 与 romfs 同时执行，其耗时被完全隐藏，wall time 收益全部体现在总时长上。
4. **可回退**：`ExtractOptions.Parallelism = 1` 可强制回到串行路径，便于慢速存储或问题排查。

## 5. 复现

```powershell
$env:NUGET_PACKAGES='E:\botw_work\nuget_packages'
$env:DOTNET_CLI_HOME='E:\botw_work\dotnet_home'
E:\botw_work\dotnet\dotnet.exe run --project E:\botw_work\perf\ExtractBench\ExtractBench.csproj -c Release -- --prepare
E:\botw_work\dotnet\dotnet.exe run --project E:\botw_work\perf\ExtractBench\ExtractBench.csproj -c Release
E:\botw_work\dotnet\dotnet.exe run --project E:\botw_work\perf\ExtractBench\ExtractBench.csproj -c Release -- --reverse
```

相关文档：[extraction-thread-optimization.md](extraction-thread-optimization.md)（线程模型、线程安全边界、变更清单与已知限制）。
