# 译栈补丁格式 v1

本文定义客户端与维护者之间的协议。JSON Schema 位于 [package.schema.json](../schemas/package.schema.json)、[catalog.schema.json](../schemas/catalog.schema.json) 与 [capture.schema.json](../schemas/capture.schema.json)。Schema 负责编辑器提示，运行时还执行路径、哈希、来源及跨字段约束；两者共同构成协议。

## 包的结构

`.lspatch` 是普通 ZIP，UTF-8 文件名。包根必须包含 `manifest.json`，以及清单所引用的 payload 文件：

```text
example-1.0.0.lspatch
├── manifest.json
└── payload/
    ├── steam/text.bin
    └── standalone/text.bin
```

不允许 ZIP 目录条目、未声明文件、重复路径、符号链接、绝对路径、父级路径、反斜杠、Windows 设备名称、NTFS 备用数据流、尾部空格/句点，或覆盖 `.localestack`。路径必须使用 `/`；Windows 下按不区分大小写判断路径冲突。文件路径不能同时是其他文件的父目录。

下载包上限 2 GiB；单个解压文件上限 2 GiB，包内解压总量上限 8 GiB，最多 11,000 个 ZIP 条目，每个变体 1–10,000 个目标文件、总计 1–32 个变体；清单上限 4 MiB。v1 采用完整文件替换，不使用二进制差分、自动执行脚本或注册表修改。

## manifest.json

```json
{
  "schemaVersion": 1,
  "gameId": "example-game",
  "gameTitle": "Example Game",
  "version": "1.0.0",
  "language": "zh-CN",
  "steamAppId": "123456",
  "executableNames": ["ExampleGame.exe"],
  "variants": [
    {
      "id": "steam-build100",
      "label": "Steam · Build 100",
      "storefront": "steam",
      "steamBuildIds": ["100"],
      "fingerprints": [
        { "path": "data/text.bin", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }
      ],
      "files": [
        {
          "path": "data/text.bin",
          "payload": "payload/steam/text.bin",
          "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
          "originalSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        },
        {
          "path": "fonts/zh.ttf",
          "payload": "payload/steam/zh.ttf",
          "sha256": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
          "originalSha256": null
        }
      ]
    }
  ]
}
```

示例哈希为占位示意，实际必须由工具对文件计算。JSON 拒绝未知字段，不支持注释。不要在实际清单里添加 `$schema`；可使用仓库内的 VS Code schema 关联。

| 字段 | 含义 |
| --- | --- |
| `schemaVersion` | 协议版本；v1 固定为数字 1，未来不兼容版本会拒绝安装 |
| `gameId` | 稳定的小写 slug，最大 80 字符，不能随标题或发行版本改变 |
| `version` | 汉化版本，严格使用三段稳定版本 `major.minor.patch`；不同于游戏版本 |
| `steamAppId` | 可空；Steam 游戏自动定位使用，非 Steam 游戏不需要 |
| `executableNames` | 需要关闭的游戏进程文件名；请填写主进程与子进程的 exe 文件名，不含路径 |
| `storefront` | `steam`、`standalone` 或 `any`，默认 `any` |
| `steamBuildIds` | 可选白名单；仅 Steam 变体可设置非空数组，且需要 App ID |
| `fingerprints` | 原版识别指纹；所有项必须同时匹配，至少一项 |
| `path` | 以游戏根目录为基准的目标文件路径 |
| `payload` | 包内路径，必须位于 `payload/` 下 |
| `sha256` | 安装后文件的 SHA-256 |
| `originalSha256` | 原版文件的 SHA-256；`null` 表示原版不存在这个文件，首次安装时也必须不存在 |

每个变体中的 `id`、每个目标路径、每个指纹路径必须唯一。共享 payload 路径的校验值必须相同。若指纹也是被替换文件，其哈希必须等于 `originalSha256`。至少一个完整变体匹配才允许安装；多个同时匹配也会拒绝，以免选择错误变体。

## 来源与版本语义

Steam 定位读取 Steam 注册表、`libraryfolders.vdf` 和 `appmanifest_{appId}.acf`，支持多库与旧/新 VDF。手动选择的目录若可和对应 Steam manifest 的真实安装目录关联，仍识别为 Steam；否则识别为 `standalone`。

所有来源始终检查文件指纹与每个待替换文件的原版哈希。仅凭 App ID、目录名称或 Build ID 不能证明兼容。`any` 表示来源不限，文件验证不变。GOG / Epic / 独立版可分别建立不同 ID 的 `standalone` 变体，以文件指纹区分。

更新已安装汉化时，先验证现有汉化文件未被修改，再使用校验过的原版备份参与兼容性匹配。游戏平台替换了游戏文件后，旧安装记录不能直接当作兼容依据。建议更新游戏前恢复原版；若 Steam 自动更新已覆盖文件，应先核对并通过游戏平台验证文件，不要手动删除记录后强装旧补丁。

v1 不直接降级汉化版本；需要降级时，先恢复原版，再装旧包。

## 本地安装协议

```text
游戏根目录/
└── .localestack/
    ├── operation.lock
    ├── transactions/{操作ID}/
    │   ├── journal.json
    │   └── 0.bak ...
    └── {gameId}/
        ├── state.json
        └── backups/{原版SHA256}
```

安装分为校验、备份、准备事务、写入与提交。游戏目录有独占操作锁。进入提交前可以取消；写入开始后完成事务或回滚，不能在写到一半时直接取消。每个文件通过同目录临时文件及重命名替换，并在写入后核对哈希。安装记录最后提交。

更新包不再包含的旧补丁文件会恢复为原版；旧包新增的文件会删除。恢复原版时只还原安装记录中的文件，不接触存档目录和未声明的游戏文件，备份保留。

失败时从事务快照恢复。若进程中断，下次安装、检查或恢复时会处理未提交事务；如果中断后文件又被其他程序修改，会停止并保留现场。v1 没有对突发断电、硬盘损坏或备份被删除作无损保证。请保留 `.localestack`。

## 官方目录与下载

客户端固定读取 `https://raw.githubusercontent.com/tkxs/LocaleStack/main/catalog/catalog.json`。根结构：

```json
{
  "schemaVersion": 1,
  "repository": "tkxs/LocaleStack",
  "games": [
    {
      "id": "example-game",
      "title": "Example Game",
      "status": "available",
      "patch": {
        "version": "1.0.0",
        "downloadUrl": "https://github.com/tkxs/LocaleStack/releases/download/patch-example-game-v1.0.0/example-1.0.0.lspatch",
        "sha256": "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
        "bytes": 1234,
        "notes": "首个汉化版本",
        "variants": ["Steam · Build 100"]
      }
    }
  ]
}
```

尚未发布的条目使用 `status: planned`、`patch: null`。不能用空地址冒充可用补丁。

只允许当前官方仓库的 HTTPS Releases 地址。GitHub CDN 重定向仍限制为 GitHub 相关 HTTPS 域名。先检查下载大小与 SHA-256，再校验包内文件。在线目录不可用时保留缓存或内置目录；离线包依然可以导入。

v1 的信任边界是官方仓库、HTTPS 和维护者发布的哈希，尚未引入独立的签名密钥。SHA-256 检测损坏和不一致，不抵御官方仓库账号被盗。后续可通过新的协议版本加入签名。

## 客户端更新

程序读取 GitHub `/releases/latest`，只处理 `v{三段稳定版本}` 标签。寻找 `LocaleStack-Setup-win-x64.exe`，使用 GitHub 返回的 `sha256:` digest 校验后由用户点击启动安装程序。客户端 Release 应设置为 latest；游戏补丁 Release 应设置为非 latest，避免覆盖客户端更新入口。
