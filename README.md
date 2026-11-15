# L4D2 MOD 管理工具 · 本地版

直接管理**本机游戏目录**里已装的求生之路 2 MOD：看清单、开关、找重复。**不复制、不移动、不删除任何文件**（唯一的写操作是 `left4dead2\addonlist.txt` 里的 1/0）。

> 这是「本地版」。随身带着库去别的机器批量装机的那套是**便携版**（另一个仓库），两者不通用。

## 它做什么

- 扫 `<游戏目录>\left4dead2\addons` 根目录 + `addons\workshop` 下的每个 `.vpk`，一行一个
- 点「状态」列（或双击）立刻开关：写进 `addonlist.txt`，进游戏生效，文件不动
- 「全部开启」「全部关闭」一键收拾
- 搜索过滤、按大小看、区分「自己放的」和「创意工坊」
- 一键启动游戏、一键打开 addons 文件夹

## 环境要求

Windows 10 / 11，自带 .NET Framework 4.x 即可，不需要装 SDK、不需要管理员（游戏装在 `Program Files` 下且写不动 addonlist 时再考虑管理员运行）。

## 用

1. 双击 `L4D2LocalSetup.exe`。没有代码签名，SmartScreen 拦一下：更多信息 → 仍要运行
2. 点「自动找」；找不到就「浏览…」选到 `...\steamapps\common\Left 4 Dead 2`（有 `left4dead2.exe` 的那一层）
3. 列表里点「状态」列开关，进游戏就是改后的样子

配置写在 exe 旁边的 `L4D2MOD管理工具.本地版.ini`，日志在 `_本地版记录\`。

## 自己编译

```
build.bat
```

只用 Windows 自带的 `csc.exe`（.NET Framework 4.x），不出网、不装包，产物是 `L4D2LocalSetup.exe`。

## 版本

- 0.1.0 — 第一版：扫描 / 开关 / 搜索 / 启动游戏

## 许可

个人自用工具，随缘更新。
