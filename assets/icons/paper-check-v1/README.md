# PaperTodo · Blue paper

蓝色圆角底座、两张叠放的纸、一枚蓝色勾。前页折角与轻微投影保留纸张的感觉；大面积白色与克制的细节便于在小尺寸辨认。

- `papertodo.svg`：可编辑的主图，1024 × 1024。
- `papertodo-small.svg`：16–32 px 使用的简化图形。
- `papertodo-1024.png`、`papertodo-512.png`、`papertodo-256.png`：透明背景 PNG。
- `papertodo.ico`：包含 16、20、24、32、40、48、64、128、256 px 的 Windows 图标。
- `preview.png`：浅色、深色与实际小尺寸预览。

这套图标已用于 Windows 程序与托盘、Android 启动图标以及项目网站。图形直接使用 SVG 绘制，未使用生成式图像模型。主色为 `#2D70ED` / `#2454C9`，纸张为白色和淡蓝色。`papertodo-adaptive.svg` 是位于 Android 安全区域内的前景，`papertodo-monochrome.svg` 用于系统主题图标。

重新导出并同步应用资源：安装 `Pillow` 与 `resvg-py`，运行 `python render.py --install`。预览文字使用 Windows 自带微软雅黑字体；其他系统可加 `--skip-preview` 仅导出图标资源。
