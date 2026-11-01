# PaperTodo Android 伴侣端

当前产品版本与 Windows 统一为 0.0.2，源码版本为 0.0.2+6。`+6` 是 Android 内部安装编号，继续递增以支持覆盖升级；测试包保留 `-dev` 或 `-validation` 标记。支持离线待办与笔记、书签/卡片浏览、WebDAV/S3/独立服务同步、桌面待办小部件、纸片导入导出与到期提醒。手机界面目前以中文为主，固定浅色；原生小部件支持系统深浅色。

## 使用与数据保护

首次进入后在设置中配置与 Windows 相同的远端位置。同步文件仅包含纸片、待办和删除记录，不包含密码或桌面几何。修改后约 3 秒触发同步；前台每 30 秒拉取，回到前台时刷新。后台由 Android JobScheduler 调度，周期至少 15 分钟，可能被省电策略延后。

从系统桌面的小部件列表添加“PaperTodo 待办”。勾选操作先持久化，随后由后台任务写入本地数据并同步；离线操作也会保留。顶部标题打开应用，刷新按钮请求同步。

截止时间面板使用内嵌中文月历与 24 小时时分滚轮。展开月历后可翻月、直接跳转年份和月份，或回到今天；今天以描边标记，所选日期显示蓝底。选好日期后点击“选择时间”返回滚轮，原时分保持不变。底部摘要与保存按钮固定，小屏或大字体下可滚动浏览；点击保存后应用选择，关闭面板则取消。

开启系统通知权限后可接收到期提醒。提醒由原生闹钟调度，重启、时区/时间变化后重新安排；不申请精确闹钟权限，系统休眠、强行停止、厂商后台限制可能延迟或阻止执行。强行停止后需重新打开应用。

本地主文件保存使用串行队列、跨引擎锁、原子替换和验证过的备份。主文件损坏时保留原文件；存在有效备份则恢复并提示，否则显示读取失败，停止写入并允许重试。

读取失败页也可以从外部纸片备份恢复。选择有效文件并确认后，当前主文件和备份会另存保留，再恢复所选纸片；同步连接需要重新配置。

设置中的“导出纸片”通过系统文件选择器保存内容备份，不导出同步密码；“导入纸片”按同步规则合并。更换安装签名或卸载前先导出并确认备份能读取。导入不是对当前数据的强制历史回滚。

## 构建与签名

使用 Flutter 3.44.4 / Dart 3.12.2、Java 17、Android SDK。普通开发包使用独立包名 `com.papertodo.mobile.dev`，不会覆盖正式应用的数据：

```powershell
flutter pub get
flutter analyze --no-pub
flutter test --no-pub
flutter build apk --debug --no-pub
```

正式构建必须配置已有的正式证书。复制 `android/key.properties.example` 为被 Git 忽略的 `android/key.properties`，或设置以下环境变量：

- `PAPERTODO_KEYSTORE_PATH`：证书完整路径。
- `PAPERTODO_KEY_ALIAS`：证书别名。
- `PAPERTODO_KEYSTORE_PASSWORD`、`PAPERTODO_KEY_PASSWORD`：密码。

```powershell
flutter build apk --release --no-pub
```

缺失正式签名时构建会失败，绝不自动回退到调试证书。CI 的显式 `-PpapertodoUnsignedValidation=true` 仅验证 Release 编译，产物不能交付安装。GitHub 发布还需要 `PAPERTODO_KEYSTORE_BASE64` 等同名 repository secrets；任何 keystore、密码、`key.properties` 不得提交。

需要在真机测试 AOT 后台入口时，可显式使用 `-PpapertodoReleaseValidation=true`。它采用 `.dev` 独立包名、调试证书和 `-validation` 版本后缀，仅用于验证，不能发布为正式包。

旧 0.0.1 APK 使用调试签名。若正式证书不同，不能直接覆盖安装，也不能先卸载而丢数据。可在持有原调试证书的环境显式构建迁移版本：

```powershell
$env:ORG_GRADLE_PROJECT_papertodoLegacyMigration = 'true'
flutter build apk --release --no-pub
Remove-Item Env:ORG_GRADLE_PROJECT_papertodoLegacyMigration
```

先用 `apksigner verify --print-certs` 确认迁移包与已安装旧包签名相同，再覆盖安装并导出备份。确认备份后由用户决定卸载旧签名应用、安装正式版并导入。迁移包必须标注“旧证书迁移包”，不作为正式 Release 资产。

## 回归验证

- `test/sync_reliability_test.dart`：保存/恢复、字段保留、引用身份、上传失败、凭据隔离、删除和冲突。
- `test/widget_test.dart`：实际首页读取与损坏文件界面；这不是 Android 桌面小部件测试。
- `src/androidTest/.../CompanionTestRunner.kt`：仅允许 `.integration` 自动化专用应用，运行实际原生队列和 Dart 后台入口。普通 `.dev` 新版试用端可能存有真实内容，禁止在其中注入验收样例。
- 自动化包显式使用 `-PpapertodoAutomationTest=true` 构建，显示为“PaperTodo 自动化测试”；它的样例、地址与 Token 不得交给用户继续使用。
- `test/custom_connection_test.dart`：验证旧服务端公开 `/ping` 时错误 Token 仍被拒绝，登录 HTML 页面不会被误认为有效同步数据。
- 仓库 `tests/phone_sync_server.py` 提供仅监听本机的测试服务，主动制造一次 ETag 冲突；配合 `adb reverse` 和隔离测试包使用。严禁用用户的真实数据目录运行故障测试。

正式交付前仍需在目标手机验证系统桌面排版、通知权限拒绝/恢复、熄屏/省电/重启及升级保留数据。代码测试通过不等于所有厂商后台策略都已验收。
