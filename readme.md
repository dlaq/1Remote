# 1Remote（个人定制增强版）

> 本仓库基于开源项目 [1Remote/1Remote](https://github.com/1Remote/1Remote) 二次定制，重点增强了 **覆盖任务栏真全屏、可停靠悬浮标签栏、自适应缩放、标签拖拽排序与快捷打开连接** 等操作体验。

## ✨ 本定制版主要改动内容

1. **全屏悬浮标签栏（解决全屏切桌面/切会话遮挡问题）**
   - 在覆盖任务栏的全屏状态下，启用 **“自动隐藏标签栏”** 后改为独立置顶悬浮工具条，鼠标移至停靠边缘呼出、移出约 **1.8~2.0 秒** 延迟自动隐藏，不再反复推挤底层窗口或重置 RDP 分辨率。
   - **四向布局与竖排支持**：可拖动悬浮条至屏幕 **顶部 / 左侧 / 右侧** 吸附停靠，或留在桌面任意位置 **自由悬浮**；吸附左右两侧时自动转为竖条，中文直立竖排、英文/数字/IP 顺时针旋转 `90°` 竖排；左右停靠时当前标签高亮指示条自动移至内侧，并去除贴屏外缘点击死区。
   - **长度与高度/粗细自由调整**：支持拖动末端或侧边/角点手柄自由调整标签栏长度及高度/宽度（横排高度 `18~64 DIP`，竖排宽度 `20~64 DIP`），缩放时标签字号、连接图标、关闭按钮及功能按钮同步平滑适配。
   - **长列表不丢失 + 全部标签菜单**：标签较多时按 Chrome 样式自适应压缩宽度，始终完整显示各远程桌面标签，并提供 `▾` 下拉菜单一键切换任意已打开标签页。
   - **悬浮标签右键菜单**：在全屏悬浮栏中右键点击任意标签页，可直接执行 **重新连接**、**让远程分辨率适应窗口**、`Ctrl + Alt + Del` 或带确认弹窗的 **关闭**。
2. **标签栏快捷打开已保存连接**
   - 在标签栏（含窗口标题栏和全屏悬浮栏）新增快捷连接按钮，点击下拉直接列出主界面保存的所有连接；点击即可在当前标签栏新建标签打开对应远程桌面，若该会话已打开则直接激活切换过去。
3. **拖动标签重新排序（双向实时联动）**
   - 在全屏悬浮栏（顶部/悬浮横排、左右竖排）以及普通窗口标签栏中，均支持按住左键直接拖动标签实时重新排序；悬浮栏顺序、普通窗口顺序、`▾` 菜单顺序与 `Ctrl+1..9` 快捷键顺序保持实时同步，且不中断正在进行的 RDP 会话。
4. **真全屏 `1920×1080` 无损适配、RDP 清晰度与 DPI 优化**
   - 根据实际 Win32 客户区与显示器工作区/全屏边界动态计算最大化外溢边距并隐藏全屏连接标识色边框，修复此前右键最大化全屏丢失 `2px`（`1918×1078` 导致动态分辨率文字发虚、固定 `1920×1080` 出现滚动条）的问题；并在覆盖全屏下保持像素级精确对齐。
   - 声明 `PerMonitorV2` 高 DPI 感知，修复 RDP `Auto` / `High` 性能档默认色深（32 位）、LAN 连接类型与 ClearType 字体平滑标志，改善全屏文字清晰度。
   - **右键点击“最大化/还原”按钮直接进入真全屏**，不再触发普通最大化/还原或弹出右键菜单。
5. **常驻固定、防误关与界面细节**
   - 原置顶按钮改为 **固定悬浮标签栏（取消自动隐藏，永远显示）**；关闭单个标签页或关闭全部标签页时均弹窗二次确认，防止手误点击。
   - 修改了程序主图标与托盘图标。

---

## 📖 核心功能操作方法

| 操作 | 方法 |
| --- | --- |
| **开启悬浮标签栏** | 进入 **设置 → 通用**，勾选 **“自动隐藏标签栏（全屏时移至停靠边缘显示悬浮标签栏）”**（也可在标签栏按钮区或悬浮条右键菜单快速切换）。 |
| **右键直接真全屏** | **右键点击** 标签栏右上角的 **“最大化/还原”** 按钮，直接进入覆盖任务栏的 `1920×1080` 真全屏（按 `F11` 也可切换全屏/还原；**左键** 点击仍为普通最大化/还原）。 |
| **呼出 / 隐藏悬浮条** | 将鼠标移动到悬浮条当前停靠的屏幕边缘（默认顶部，或左侧/右侧）即可呼出；移开约 `1.8s` 后自动隐藏；也可使用全局热键 **`Ctrl + Alt + H`** 手动显隐。 |
| **停靠与自由悬浮** | 按住悬浮条前端的 **网格拖拽手柄（或空白区域）** 拖动：靠近顶部/左/右边缘松手自动吸附停靠，拖到屏幕中间松手则保持自由悬浮。 |
| **调整长度、高度与粗细** | 拖动悬浮条的 **右边缘/底边缘/角点** 即可调节长度、高度或竖排宽度，字体与图标会随尺寸自动缩放并持久保存。 |
| **固定悬浮条永远显示** | 点击悬浮条或窗口标题栏上的 **图钉图标（📌）**，可取消自动隐藏让标签栏常驻显示（不改变远程桌面分辨率）。 |
| **快捷打开主界面连接** | 点击标签栏上的 **快捷打开按钮（`+` / 显示器加号图标）**，在下拉菜单中直接点击主界面保存的任意连接即可在当前标签栏打开或切换过去。 |
| **拖动调整标签顺序** | 按住任意远程桌面标签左右（竖排时上下）拖动，即可实时调整标签排列顺序（`Ctrl + 1..9` 会同步按新顺序生效）。 |
| **悬浮标签重连 / 关闭** | 在全屏悬浮标签栏中 **右键点击任意远程桌面标签**，即可执行 **重新连接**、**让远程分辨率适应窗口**、`Ctrl + Alt + Del` 或 **关闭**。 |
| **刷新远程自适应分辨率** | 进入全屏后如曾手工调整过状态，点击功能区的 **“立即让远程分辨率适应窗口”** 按钮（框选图标）即可一步拉齐至显示器原生分辨率。 |

---

# 1Remote (Upstream Documentation)

English | [中文](https://github.com/1Remote/1Remote/wiki/Intro%E2%80%90ZhCn)

[![version](https://img.shields.io/github/v/release/1Remote/1Remote?color=Green&include_prereleases)](https://github.com/1Remote/1Remote/releases)
[![issues](https://img.shields.io/github/issues/1Remote/1Remote)](https://github.com/1Remote/1Remote/issues)
[![license](https://img.shields.io/github/license/1Remote/1Remote?color=blue)](https://github.com/1Remote/1Remote/blob/main/LICENSE)
![](https://github.com/1Remote/1Remote/actions/workflows/build-on-dev-push.yml/badge.svg)

1Remote is a modern personal remote session manager and launcher. It is a single place to manage all your remote sessions supporting number of different protocols.

## Features

- Supports RDP, SSH, VNC, Telnet, (S)FTP, [RemoteApp](https://1remote.github.io/usage/protocol/especial/remoteapp/), [NoMachine and other app](https://1remote.github.io/usage/protocol/especial/app/)
- Quick and convenient remote session launcher (Alt + M)
- Multi-screen and HiDPI RDP connection (Test on **Win10 + 4k monitor *2** RDP TO **Win2016**)
- Detailed connection configuration: tags, icons, colors, connection scripts etc.
- Multiple languages, themes and tabbed interface (tabs can be dragged out into separate windows and docked back)
- [Import connections from mRemoteNG](https://1remote.github.io/usage/overview/#importing-from-mremoteng)
- Customizable runners, in SFTP \ FTP \ VNC \ etc. protocols, you can replace the internal runner with your favourite tools.[wiki](https://1remote.github.io/usage/protocol/runner/)
- Portable - just unpack and run

## 🚩Installation

Latest Version: 1.3

### 🗺[Quick start](https://1remote.github.io/usage/quick-start/)

### 🔻[Download](https://1remote.github.io/download/)

## 👓Overview

<img src="https://1remote.github.io/img/home_override/hero1.png" width="800" />

<p align="center">
    <img src="https://1remote.github.io/img/home_override/protocols.png" width="400" />
</p>
<p align="center">
    <img src="https://1remote.github.io/img/home_override/hero2.gif" width="400"/>
</p>

<p align="center">
    ↑ Launcher(Alt + M) open RDP connection & resizing
</p>

<p align="center">
    <img src="https://raw.githubusercontent.com/1Remote/PRemoteM/Doc/DocPic/multi-screen.jpg" width="500"/>
</p>

<p align="center">
    ↑ RDP with Multi-monitors
</p>

<p align="center">
    <img src="https://raw.githubusercontent.com/1Remote/PRemoteM/Doc/DocPic/RemoteApp/demo.jpg" width="800"/>
</p>

<p align="center">
    ↑ RemoteApp via RDP
</p>

<p align="center">
    <img src="https://raw.githubusercontent.com/1Remote/PRemoteM/Doc/DocPic/Runner/vnc_runners.jpg" width="800"/>
</p>

<p align="center">
    ↑ Customizable runners
</p>

## Make 1Remote Stronger

If you like **1Remote**, help us make it stronger by doing any of the following:

1. Simply star this repository
2. [Help translation](https://1remote.github.io/usage/misc/help-translation/)
3. [Buy a coffee](https://ko-fi.com/VShawn)
4. [Join DEV](DEVELOP.md)

## Special thanks

<a href="http://www.jetbrains.com/resharper/"><img src="http://www.tom-englert.de/Images/icon_ReSharper.png" alt="ReSharper" width="64" height="64" /></a>
