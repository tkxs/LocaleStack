# 维护者指南

从 v0.2.0 开始，推荐使用 [图形维护者工作台](maintainer-gui.md)。它可以选择原版和汉化目录，扫描勾选文件，一键打包上传 GitHub，并自动提交游戏目录。无需 SDK、Git 或命令行。下文保留高级 CLI 工作流。

## 1. 准备原版和汉化文件

为每种实际测试过的游戏版本分别保留：原版目录、汉化目录、游戏来源和版本。汉化目录可以是完整游戏，也可以仅包含修改后的文件。原版目录至少应含所有被替换文件及额外用于识别版本的文件。

使用 `.NET 10 SDK` 从源代码运行 CLI，或解压维护工具 ZIP 后执行 `LocaleStack.Cli.exe`。下面示例的 `dotnet run --project src/LocaleStack.Cli --` 可直接替换为该 EXE。

复制 [多来源配置示例](../examples/multi-store.release.json)，按实际修改：

- `gameId` 为固定小写 slug，第一次确定后保持不变。
- `version` 是汉化版本，后续更新递增，例如 `1.0.0 → 1.0.1`。
- 每个变体填写自己的原版、汉化目录和文件列表。
- Steam 可限制 `steamBuildIds`，其他来源使用 `standalone`，文件相同且跨来源实际验证过可使用 `any`。
- `fingerprintFiles` 可额外加入 exe 或版本资源；只新增中文文件而不替换原版文件时，必须额外填写至少一个原版指纹文件。

## 2. 生成与校验包

```powershell
dotnet run --project src/LocaleStack.Cli -- capture --config work/my-release.json --output work/my-package
dotnet run --project src/LocaleStack.Cli -- pack --source work/my-package --output artifacts/my-game-1.0.0.lspatch
dotnet run --project src/LocaleStack.Cli -- verify --package artifacts/my-game-1.0.0.lspatch
dotnet run --project src/LocaleStack.Cli -- inspect --package artifacts/my-game-1.0.0.lspatch --game-directory D:/Games/MyGame
```

`capture` 自动计算原版与汉化文件哈希，跳过未改动文件，生成 manifest 与 payload。`pack` 仅打包清单引用文件，再完整解包验证。工具拒绝覆盖已有输出目录/包，避免混入旧内容。

用桌面客户端“导入离线补丁”实际测试安装、重复安装、更新、恢复及游戏启动。`verify` 只证明格式和内容校验通过，不证明游戏中的翻译或运行正确。

## 3. 添加游戏条目

在 `catalog/catalog.json` 中添加游戏 ID、英文标题、简介、可选的 Steam App ID 与 exe 名称。尚未发布时使用：

```json
{
  "id": "my-game",
  "title": "My Game",
  "summary": "游戏简介与汉化范围。",
  "steamAppId": null,
  "executableNames": ["MyGame.exe"],
  "status": "planned",
  "patch": null
}
```

## 4. 上传补丁，再更新目录

在 GitHub 创建一个补丁 Release，例如 `patch-my-game-v1.0.0`，把 `.lspatch` 作为附件上传。**设置为非 latest**。不要把游戏资源放进 Git 提交；大文件使用 Releases 附件。

用 GitHub CLI 可这样发布：

```powershell
gh release create patch-my-game-v1.0.0 artifacts/my-game-1.0.0.lspatch --title 'My Game 汉化 1.0.0' --notes '修正内容、游戏版本和已测试范围。' --latest=false
```

确认附件可下载后，让工具生成目录的大小和校验值：

```powershell
dotnet run --project src/LocaleStack.Cli -- catalog-add --catalog catalog/catalog.json --game-id my-game --package artifacts/my-game-1.0.0.lspatch --url https://github.com/tkxs/LocaleStack/releases/download/patch-my-game-v1.0.0/my-game-1.0.0.lspatch --notes '首版汉化，支持独立安装版 1.0。'
dotnet run --project src/LocaleStack.Cli -- catalog-validate --catalog catalog/catalog.json
git add catalog/catalog.json
git commit -m 'Publish My Game localization 1.0.0'
git push
```

玩家同步目录后即可看到新版本，不必重新下载译栈。后续版本按相同顺序执行。不要复用旧版本号替换内容，避免缓存和安装记录混淆。

## 5. 发布客户端

框架版本定义在 `Directory.Build.props`。发布时使用 `v0.1.0` 这类标签：

```powershell
git tag v0.1.0
git push origin v0.1.0
```

`release.yml` 会运行测试，构建内含 .NET 运行时的便携程序和维护工具，使用 Inno Setup 构建当前用户安装版，生成 `SHA256SUMS.txt` 并发布为 latest。无需维护网站或单独的更新服务器。

程序更新检查依赖 GitHub Release 附件的 `digest` 字段；缺失时不会自动启动未经校验的安装程序。

## 6. 当前边界

首版官方源固定为 `tkxs/LocaleStack`；不接受第三方网络源，但可以手动导入标准离线包。没有可视化翻译编辑器，汉化内容由外部工具制作。首版仅完整文件替换；需要执行脚本、修改注册表、内存注入或二进制差分的安装流程应先扩展协议、实现验证后再接入。

仓库中的示例路径、Build ID 和哈希都不等于自动适配承诺。每个实际游戏版本都需要对应内容和运行验证。
