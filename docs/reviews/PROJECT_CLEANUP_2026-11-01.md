# 2026-11-01 项目整理记录

Windows 与 Android 的产品版本统一为 **0.0.2**。Windows 文件版本为 `0.0.2.0`；Android 内部安装编号递增到 `6`，已覆盖安装的独立测试包版本为 `0.0.2-validation`，保留测试身份与原应用数据。

## 文件布局

- 根目录保留 Windows 源码、`PaperTodo.exe` 入口和用户数据。
- 当前 Windows 程序与 Android 测试 APK 集中在 `dist/PaperTodo-v0.0.2/`，附校验文件和构建清单。
- 交付验收记录移至 `docs/reviews/`，原始同步需求移至 `docs/plans/`，Markdown 示例移至 `docs/examples/`。历史记录中的版本号保持原值。
- 保留 `backups/`、`archive/backups/`、Android SDK 配置、签名配置入口、资源、测试源码和 Git 工作区。
- 本地构建脚本不再向根目录和 `dist/` 顶层复制多份版本程序；升级备份改存 `backups/updates/`。本地打包和发布流程检查两端产品版本是否一致。

## 回收结果

核实 **80 项、约 9.55 GiB** 旧程序、重复发布包、验收输出与构建缓存进入 Windows 回收站；同时检查了对应的原路径记录和回收内容。回收站未清空。

**例外：** `mobile/build/` 的约 2.78 GiB 可再生成构建缓存，在调用系统回收 API 后已消失，但未找到对应回收站记录，因此不能确认其可从回收站恢复。它不包含项目源码或用户主数据。

清理前，从旧输出中另存并校验了 21 份数据快照和源码备份。升级前的主数据也已备份；升级后纸片内容保持一致，只有正常同步时间字段发生变化。

本机详细清单在被 Git 忽略的 `backups/project-cleanup-20261101/`：`recycle-plan.json`、`recycle-results.json`、`recycle-verification.csv`、`preserved-output.csv` 和 `summary.json`。这些文件用于本次清理的核对与恢复定位，不包含聊天中公开的同步凭据。

## 验证

- Windows Release 编译通过，0 警告、0 错误；自包含压缩单文件构建成功，当前进程版本为 `0.0.2.0`。
- Flutter 静态检查通过，27 项测试通过，Android AOT 测试包构建与覆盖安装成功。
- 构建脚本语法、两端版本一致性、产物 SHA-256 和移动后文档路径已检查。

后续重新开发时，先运行 `flutter pub get` 恢复移动端依赖索引；Windows 和 Android 构建会自动重新生成所需缓存。
