# 第三方说明

客户端与维护者工具采用 .NET / WPF，自包含发布包含微软 .NET 运行时；其许可证与第三方说明随 .NET SDK / runtime 分发，源代码见 https://github.com/dotnet/runtime 与 https://github.com/dotnet/wpf 。

安装版由 Inno Setup 6 生成。Inno Setup Copyright © 1997–2026 Jordan Russell；有关许可证见 https://github.com/jrsoftware/issrc/blob/main/license.txt 。

`packaging/ChineseSimplified.isl` 来自 Inno Setup 官方源代码标签 `is-6_7_3` 的 `Files/Languages/Unofficial/ChineseSimplified.isl`，保留原维护者署名与文件头，随 Inno Setup 使用。

`assets/brand.svg` 与程序图标为本项目的代码生成资产。`scripts/Generate-Icon.py` 仅在重新生成图标时依赖 Pillow，客户端不依赖 Pillow。
