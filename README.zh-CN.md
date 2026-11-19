<div align="center">

<img src="icon.svg" width="112" alt="Apple Music Lyrics 图标">

# Apple Music Lyrics

**为 Windows 版 Apple Music 带来逐字同步的桌面歌词。**

[![最新版本](https://img.shields.io/github/v/release/BeteixZ/floating-lyrics-wam?label=release&color=ff2f5f)](https://github.com/BeteixZ/floating-lyrics-wam/releases/latest)
[![下载量](https://img.shields.io/github/downloads/BeteixZ/floating-lyrics-wam/total?color=ff4f8b)](https://github.com/BeteixZ/floating-lyrics-wam/releases)
[![构建](https://img.shields.io/github/actions/workflow/status/BeteixZ/floating-lyrics-wam/build.yml?branch=main&label=build)](https://github.com/BeteixZ/floating-lyrics-wam/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
![平台](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)

[English](README.md) · **简体中文** · [繁體中文](README.zh-TW.md)

[**下载**](https://github.com/BeteixZ/floating-lyrics-wam/releases/latest) · [功能](#功能) · [快速开始](#快速开始) · [常见问题](#常见问题) · [更新日志](RELEASE_NOTES.md)

<img src=".github/assets/demo.gif" alt="纯净模式歌词卡片：逐字高亮、长音发光、翻译与下一句" width="720">

</div>

Windows 版 Apple Music 没有桌面歌词。Apple Music Lyrics 提供一个始终置顶、跟随播放进度的歌词浮层：它直接读取 Apple Music 已缓存的歌词，逐字高亮，显示翻译和罗马音；当 Apple 的歌词缺失或不完整时，再从网易云音乐、QQ 音乐和 LRCLIB 补全。

> [!NOTE]
> 项目仍在积极开发中（0.x 版本），功能和设置在不同版本之间可能变化。

## 功能

**歌词**

- 读取 Apple Music 自身的歌词缓存，包括逐字时间轴、翻译、罗马音、对唱和和声
- 可选联网歌词：网易云音乐、QQ 音乐、LRCLIB，优先选用逐字和翻译覆盖更完整的来源
- Apple 有这首歌时，联网歌词会对齐到 Apple 的每行起始时间，修正部分平台歌词提前或滞后的问题
- 严格匹配并校验 Apple 曲目：宁可不显示，也不显示错误的歌词
- 右键可重新搜索歌词，或为当前歌曲手动选择其他版本

**显示**

- 逐字高亮；拖长音的字会像 Apple Music 一样微微放大并发光
- 换行动画流畅，并略早于演唱切换，保证新的一句及时可读
- 对唱时两位歌手的歌词以两行同等大小显示，各自有高亮和翻译
- 罗马音显示在原文下方；日语歌词缺少读音时可离线生成罗马字
- 单行、双行和上下文三种布局

**窗口**

- 纯净模式：无边框歌词卡片随内容自动调整大小，过渡平滑，缩小前会先预判后面几句
- 鼠标穿透、悬停淡化，暂停时自动隐藏
- 支持每显示器 DPI，普通模式与纯净模式分别记住位置

**个性化与隐私**

- 字体（拉丁文、中文、译文可分别设置）、颜色、发光、不透明度和时间偏移，可按歌曲记住偏移
- 界面支持简体中文、繁體中文和英文，可通过 [JSON 语言包](docs/localization.md)扩展（英文文档）
- 关闭联网功能后可完全离线使用；无需账号、登录或 API Key

## 快速开始

**系统要求：** Windows 10 19041 及以上版本或 Windows 11、Microsoft Store 版 Apple Music，以及 x64 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)。

1. 从[最新版本](https://github.com/BeteixZ/floating-lyrics-wam/releases/latest)下载 `AppleMusicLyrics-vX.Y.Z-win-x64.zip`。
2. 解压到任意位置，运行 `AppleMusicLyrics.App.exe`。
3. 在 Apple Music 中播放歌曲，歌词就会出现在浮层上；程序常驻系统托盘。

升级前请先从托盘退出旧版本。设置保存在 `%LOCALAPPDATA%\AppleMusicLyrics`。

## 使用方法

| 操作 | 方式 |
| --- | --- |
| 显示/隐藏、纯净模式、单行/双行模式、鼠标穿透、设置 | 托盘图标菜单 |
| 移动浮层 | 直接拖动（需先关闭**鼠标穿透**） |
| 重新搜索歌词或选择其他歌词版本 | 在歌词上点击右键 |
| 歌词偏早或偏晚 | **设置 → 歌词 → 歌词提前量**，可开启**按歌曲记住歌词偏移** |
| 开关逐字高亮、翻译或罗马音 | **设置 → 歌词** |
| 禁止一切联网 | **设置 → 高级** |

全部设置项说明见 [Settings and usage](docs/settings.md)（英文）。

## 歌词来源

播放歌曲时，程序按以下优先级选择能找到的最佳歌词：

| 优先级 | 来源 | 使用条件 |
| :---: | --- | --- |
| 1 | Apple Music（本地缓存） | 已开启的功能（逐字、翻译）覆盖了歌曲的大部分 |
| 2 | 网易云音乐 | 这些功能的覆盖明显更完整，且确认是同一份歌词 |
| 3 | QQ 音乐 | 条件同上，且网易云没有更好的结果 |
| 4 | Apple Music（本地缓存） | 其他情况；缺少的功能可从其他来源补到 Apple 的时间轴上 |
| 5 | LRCLIB | 以上都没有时 |

逐字高亮和翻译都关闭时，只要 Apple 有歌词就始终使用 Apple 的歌词。网易云或 QQ 的歌词只有在确认与 Apple 是同一份歌词时才会替换 Apple，并会对齐到 Apple 的逐行时间。完整规则以及各服务会收到哪些信息，见 [Lyric sources, matching, and privacy](docs/lyric-sources.md)（英文）。

## 常见问题

<details>
<summary><b>某首歌不显示歌词。</b></summary>

程序会先查找 Apple Music 已缓存的歌词；没有匹配且开启了联网歌词时，再搜索网易云、QQ 音乐和 LRCLIB。如果这些来源都没有这首歌，或者匹配把握不够，就不会显示。可以在右键菜单中选择**重新搜索歌词**，或用**选择歌词…**手动挑选候选。高级设置中的**允许低置信度匹配**会以准确度换取覆盖率。
</details>

<details>
<summary><b>歌词稍微偏早或偏晚。</b></summary>

在“歌词”选项卡中调整**歌词提前量**，它用于补偿音频输出延迟和个人习惯。开启**按歌曲记住歌词偏移**后，可以为单首歌曲保存单独的校正。
</details>

<details>
<summary><b>没有翻译或逐字高亮。</b></summary>

这两项都取决于歌词来源是否提供；程序不会自行生成翻译，也不会编造逐字时间。请确认已开启**逐字高亮**和**显示歌词翻译**，并开启联网歌词，以便程序寻找提供这些内容的来源。
</details>

<details>
<summary><b>Windows SmartScreen 提示风险。</b></summary>

发布版本没有代码签名，刚下载的新版本可能触发“Windows 已保护你的电脑”。如果文件来自本仓库的 Releases 页面，点击**更多信息 → 仍要运行**即可。源代码和构建流程均公开。
</details>

<details>
<summary><b>程序跟随了其他播放器或浏览器。</b></summary>

默认只跟随 Apple Music 的媒体会话。高级设置中的**允许非 Apple Music 的媒体会话**会放开限制，因此默认关闭。
</details>

<details>
<summary><b>能离线使用吗？联网时会发送什么？</b></summary>

关闭联网功能后，程序只读取 Apple Music 的本地缓存，不发送任何内容。开启后，会把歌名和歌手（部分服务还包括专辑和时长）发送给 Apple 的 iTunes Search API 和已启用的歌词服务。详见 [Network and privacy](docs/lyric-sources.md#network-and-privacy)（英文）。
</details>

## 已知限制

- 仅支持 Windows，且需要 Apple Music 处于运行状态才能跟踪播放。
- 网易云音乐和 QQ 音乐使用的是未公开接口，可能变更、受地区限制或被限流。
- 逐字时间和翻译取决于各来源；缺失或不匹配时会退回普通的逐行显示。
- 纯净模式调整大小时，透明分层窗口在部分显卡驱动上可能出现边缘闪烁。

## 从源码构建

```powershell
dotnet build AppleMusicLyrics.sln -c Release
dotnet test AppleMusicLyrics.sln -c Release --no-build
./build.ps1   # 发布到 artifacts/publish/
```

环境要求、发布目录结构和项目结构见 [Development](docs/development.md)（英文）。

## 参与贡献

欢迎提交 Issue 和 Pull Request。反馈歌词问题时，请附上歌名、歌手和诊断信息（托盘 → **显示诊断摘要**，或通过**查看诊断详情…**打开完整报告）。修改匹配逻辑时请以准确为先，避免在每帧中进行媒体、磁盘或网络 I/O。

## 许可证

本项目自有代码采用 [MIT 许可证](LICENSE)。QQ 音乐和网易云音乐的移植代码包含源自 LyricsKit 的文件，采用 [Mozilla Public License 2.0](licenses/MPL-2.0.txt)。涉及的文件、固定的上游版本、致谢及源码获取方式见 [Third-party notices](THIRD_PARTY_NOTICES.md)。

软件许可证只适用于代码，并不授予使用音乐服务私有接口的权限，也不授予复制、再分发或商业使用所返回歌词的权利。

## 致谢

灵感来自 macOS 上 [ddddxxx 的 LyricsX](https://github.com/ddddxxx/LyricsX)。QQ 音乐和网易云音乐的集成移植自 [MxIris-LyricsX-Project 维护的 LyricsKit 分支](https://github.com/MxIris-LyricsX-Project/LyricsKit)，其基础为 [ddddxxx 的 LyricsKit](https://github.com/ddddxxx/LyricsKit)。所选基线为 LyricsKit 1.8.3，提交 [`6f071990d0c9c6f48d29284db805990bffb912ed`](https://github.com/MxIris-LyricsX-Project/LyricsKit/tree/6f071990d0c9c6f48d29284db805990bffb912ed)，即维护版 LyricsX v1.8.9 所用版本。致谢与许可证详情保留在 [Third-party notices](THIRD_PARTY_NOTICES.md) 中。
