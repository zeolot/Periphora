# 开发文档

面向改代码的人：截图怎么来的、提交信息怎么写。安装与使用见仓库根目录的 [README](../README.md)。

## 截图

`README.md` 里的面板截图由程序自己渲染，不需要真机和真实外设：

```bash
# 任意平台的预览构建都行；这里用 win-x64 举例
dotnet publish linux/Periphora.Linux.csproj -c Release -r win-x64 --self-contained false -o out/shot

PERIPHORA_SHOT=$PWD/docs/panel.png            PERIPHORA_SHOT_SCALE=2 out/shot/Periphora --demo=3
PERIPHORA_SHOT=$PWD/docs/panel-4-devices.png  PERIPHORA_SHOT_SCALE=2 out/shot/Periphora --demo=4
PERIPHORA_SHOT=$PWD/docs/panel-2-devices.png  PERIPHORA_SHOT_SCALE=2 out/shot/Periphora --demo=2
PERIPHORA_SHOT=$PWD/docs/empty.png            PERIPHORA_SHOT_SCALE=2 out/shot/Periphora --demo=empty
```

| 文件 | 场景 | 尺寸（2 倍） |
|---|---|---|
| `panel.png` | 三台设备，第三台休眠并保留上次读数 | 1408 × 1248 |
| `panel-4-devices.png` | 四台设备，含一台充电中 | 1408 × 1548 |
| `panel-2-devices.png` | 两台设备 | 1408 × 948 |
| `empty.png` | 没有检测到设备 | 1408 × 648 |
| `tray-menu.png` | Windows 版托盘菜单 | 手工截图，裁到弹窗范围 |

`--demo` 用合成读数渲染；其中的休眠设备会先以在线状态上报一次再转为休眠，因此能复现「保留最后一次读数」的淡灰色卡片。改动界面版式后请重新跑一遍，避免文档与实现脱节。

托盘菜单无法离屏渲染：Linux 下由桌面环境（StatusNotifierItem）绘制，Windows 下是原生菜单。需要更新时手工截图，并在 README 的图注里注明是哪个平台。

图标由 `tools/MakeIcon.cs` 生成，`build-icon.ps1` 一次产出 `periphora.ico`、`app.ico`、`docs/icon.png`（512）与 `linux/Assets/periphora.png`（256），四者必须同源，不要单独替换其中一个。

## 提交信息约定

提交信息一律用中文，格式沿用 [Conventional Commits](https://www.conventionalcommits.org/zh-hans/)：

```
<类型>(<范围>): <简述>

<正文，可选>
```

- **类型**：`feat`（用户可见的新增）、`fix`（修缺陷）、`docs`（文档与截图）、`build`（构建、打包、版本号）、`refactor`（不改变行为的调整）、`perf`、`chore`（依赖、资源等杂项）。
- **范围**：可省略。用一到两个词点明模块，如 `fix(休眠)`、`feat(唤醒)`、`feat(图标)`、`build(打包)`。
- **简述**：一句话说清「做了什么」，不用句号结尾。能看出前后差异时写成「改为……，不再……」更省事。
- **正文**：要解释动机、取舍或副作用时才加，与简述之间空一行，每行控制在 72 个字宽以内。正文讲「为什么」和「注意什么」，不复述改动了哪些文件。
- 一次提交只做一件事。纯格式化、纯重命名不要混进功能提交，否则 review 和回滚都会变难。
- 版本号只在 `linux/packaging/package.py` 与 README 的体积表两处出现，改版本时一起改。

示例：

```
feat(唤醒): 设备被唤醒后立即刷新，不再等下次轮询

Windows 用 Raw Input 全局输入事件，Linux 对接收器输入节点做非阻塞
poll。只在空闲 4 秒后的第一个报文触发，并带 8 秒冷却，避免把接收器
问得太频繁而停止应答。监听装不上时退回纯定时轮询。
```
