# Repository instructions

- Markdown 行内公式用 `$...$`，独立公式用 `$$...$$`。
- 构建、发布和测试产物统一放在仓库根目录的 `artifacts/`。
- 生成供用户运行的最新版时执行 `./build.ps1`。程序关闭时输出到 `artifacts/publish/win-x64/`，成功后替换上一份；程序运行时输出到 `artifacts/publish/win-x64-yyyyMMdd-HHmmss-fff/`，保留正在运行的程序及其文件，不关闭或重启程序。以脚本最后输出的 `Latest build:` 路径为准。
- 除程序运行时由 `build.ps1` 自动生成的时间戳发布目录外，不再创建按日期、任务名、功能名或版本号命名的本地 build/publish 目录或 ZIP；GitHub Release 的版本化附件不受此限制。
- 普通 `dotnet build` 的编译文件和中间文件由 `Directory.Build.props` 统一放在 `artifacts/bin/`、`artifacts/obj/`；测试报告使用 `artifacts/test-results/`。
- 清理历史产物时保留用户设置、诊断脚本和报告；`artifacts/diagnostics/` 用于历史诊断资料，不能作为发布目录。
