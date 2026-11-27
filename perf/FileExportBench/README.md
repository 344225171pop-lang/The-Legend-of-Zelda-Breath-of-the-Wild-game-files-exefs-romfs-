# FileExportBench

日志**文件导出**实测工具：把选定范围的日志按应用真实链路写进新建文件，统计耗时、吞吐与内存分配，并与 v1.5 旧实现（整段字符串 + 1 KB 缓冲写）做同口径对比。

- 与生产代码同源：`Compile Include="..\..\android\BotwExtractor\Ui\*.cs"`，测的就是 `LogExporter.WriteTo` / `Format` 本身。
- 正确性门禁：每个量级都比对「现实现文件 == 旧实现文件 == `Format()` 内存输出」的字节，不一致立即抛异常中止。
- 口径：每次导出都新建文件、写完删除（等价于 SAF 新建文档），因此包含文件创建、flush、关闭（以及杀软扫描）成本。

## 运行

```bash
dotnet build perf/FileExportBench/FileExportBench.csproj -c Release
dotnet perf/FileExportBench/bin/Release/net8.0/FileExportBench.dll
```

输出为 Markdown：每个量级一张表，包含预览格式化 / 旧实现导出 / 现实现导出的 p50、min、吞吐、分配与字节一致性。

## 多轮取中位（报告采用 5 轮）

单次运行的 p50 会被杀软扫描等偶发因素放大，建议跑多轮再取中位数：

```powershell
1..5 | ForEach-Object {
  dotnet perf/FileExportBench/bin/Release/net8.0/FileExportBench.dll *> "run$_.md"
}
```

汇总时对每个量级的 p50 / min 分别取 5 轮中位数；对应结果见 `perf/file-export-bench-output.md` 的「附：5 轮聚合」，分析见 `docs/perf/file-export-benchmark-report.md`。

## 说明

- 本工具只覆盖 PC .NET 运行时；Android 真机导出还要叠加 SAF/文档提供方 IPC 与闪存 fsync 时延，PC 数值视为下限。
- 应用日志环形上限为 `LogStore.DefaultCapacity`（5,000 条），因此 5,000 条档位是真实用户场景的上界；20,000 / 100,000 条为容量放开后的压力参考。
