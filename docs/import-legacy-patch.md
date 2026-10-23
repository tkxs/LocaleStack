# 从原电脑导入 A Sector Darkly Demo 汉化

原任务记录显示已制作 `Chinese_Patch`、英文备份与构建脚本。当前电脑没有这些文件，译栈仅预留游戏目录和修订记录。请在原电脑完成以下步骤。

## 准备资料

- 完整的 `outputs/Chinese_Patch`，不能只取 `data.win`；还包含 JSON/INI 文本和运行时显示词典。
- 同一游戏构建的原版备份，旧记录名称为 `_zh_cn_backup_build26147432`。不要混入更早构建的原版文件。
- 如果需要修正“太空雷”，还需旧 `work` 中的局部文本映射和构建脚本。

小行星操作菜单中的 `MINE` 应译为“采矿”。[corrections.json](../games/a-sector-darkly-demo/corrections.json) 记录了准确语境；武器的 mine 保留“太空雷”。在局部显示映射中改好后重新构建汉化文件，再打包。框架不会对二进制文件做全局字词替换。

## 自动列出旧包全部文件

在原电脑克隆本仓库后运行：

```powershell
./scripts/Import-LegacyPatch.ps1 `
  -OriginalDirectory 'F:/SteamLibrary/steamapps/common/A Sector Darkly Demo/_zh_cn_backup_build26147432' `
  -PatchDirectory 'C:/实际旧工作目录/outputs/Chinese_Patch' `
  -OutputConfig 'work/asd-release.json' `
  -Version '1.0.1' `
  -SteamBuildId '26147432'
```

脚本扫描 `Chinese_Patch` 中的全部文件，生成 capture 配置。检查目录和游戏实际 Build ID，再执行：

```powershell
dotnet run --project src/LocaleStack.Cli -- capture --config work/asd-release.json --output work/asd-package
dotnet run --project src/LocaleStack.Cli -- pack --source work/asd-package --output artifacts/asd-demo-1.0.1.lspatch
dotnet run --project src/LocaleStack.Cli -- verify --package artifacts/asd-demo-1.0.1.lspatch
```

无 SDK 时使用维护者便携版 `LocaleStack.Cli.exe` 替代前缀。`Import-LegacyPatch.ps1` 仅生成配置，不修改游戏、翻译或原版备份。

## 测试与发布

先在干净且匹配构建的副本中，用译栈导入包、安装、启动游戏和恢复原版。现有旧汉化安装不是译栈的已管理安装，不会被直接覆盖；请用旧版恢复工具恢复匹配的英文原版后再安装。不要把旧包的 `Install_Chinese.ps1` 或恢复脚本放进 payload。

发布到 `patch-a-sector-darkly-demo-v1.0.1` Release，设为非 latest，附件使用 `asd-demo-1.0.1.lspatch`。随后运行 `catalog-add` 更新目录，再提交：

```powershell
dotnet run --project src/LocaleStack.Cli -- catalog-add --catalog catalog/catalog.json --game-id a-sector-darkly-demo --package artifacts/asd-demo-1.0.1.lspatch --url https://github.com/tkxs/LocaleStack/releases/download/patch-a-sector-darkly-demo-v1.0.1/asd-demo-1.0.1.lspatch --notes '适配 Steam Demo Build 26147432；修正小行星菜单采矿用语。'
```

如果之后游戏已更新，需重新取得新构建原版、移植汉化并新增/调整变体；不要只修改 Build ID 来绕过验证。
