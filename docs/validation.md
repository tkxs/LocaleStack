# 0.1.0 验证记录

验证日期：2026-10-23。验证环境为 Windows x64，.NET 10 SDK，自包含发布的桌面程序。

## 核心回归

`dotnet run --project tests/LocaleStack.Tests -c Release`：42 / 42 通过。

覆盖：独立安装、重复安装、升级与移除旧补丁文件、恢复原版、降级拒绝、原版不匹配、现有汉化被修改、备份损坏、操作互斥、取消准备、安装/升级/恢复故障注入回滚、持久化中断恢复、外部修改保护、文件校验、危险路径与恶意 ZIP、Windows 保留名、Steam 多库与新旧 VDF、Build ID 和来源规则、版本比较、下载缓存、坏哈希、非法重定向、客户端更新元数据。

文件操作测试在隔离的临时目录执行，包括带中文和空格的游戏目录。测试没有修改真实游戏文件。

## 维护者与协议

`scripts/Test-PackageWorkflow.ps1` 已实际执行 capture → pack → verify → inspect → catalog-add → catalog-validate，并核对生成的发布校验值及原版输入未改动。

`scripts/Validate-Schemas.py` 验证所有 Schema、官方目录和多来源配置示例，并验证缺少原版哈希、父级路径、未知字段、独立版误用 Steam Build ID 等反例被拒绝。实际生成的包清单也通过 v1 Schema。

GitHub Actions 的 [首次主分支 CI](https://github.com/tkxs/LocaleStack/actions/runs/39978210730) 已通过构建、42 项核心回归、维护流程和 Schema 验证。

## 桌面与分发

自包含桌面程序已实际启动，游戏库、任务、帮助、设置和最小窗口尺寸均完成启动及渲染检查，退出码为 0。已人工查看界面截图；截图可由 `scripts/Test-Desktop.ps1` 重新生成。

安装版使用 Inno Setup 6.7.3 构建。已安装到工作区隔离目录，核对安装后的 exe 与源发布文件 SHA-256 一致，运行同样的五项桌面检查后卸载，安装与卸载均成功。安装、卸载日志保存在本地 `artifacts/`。

在线官方目录已从实际 GitHub 地址读取成功。下载与更新协议有注入网络测试；真实公开发行附件的可用性应在每次发布后核对。

## 验证边界

本机没有 A Sector Darkly Demo 或旧汉化内容，本轮没有验证真实游戏中的汉化效果或已确认文字修订。核心测试证明文件安装管理流程，不等于所有游戏/版本都已适配。每个游戏包仍需在维护者对应游戏构建中完成运行验证。
