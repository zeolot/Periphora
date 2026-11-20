# Screenshots

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
