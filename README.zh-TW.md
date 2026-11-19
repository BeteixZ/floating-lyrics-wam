<div align="center">

<img src="icon.svg" width="112" alt="Apple Music Lyrics 圖示">

# Apple Music Lyrics

**為 Windows 版 Apple Music 帶來逐字同步的桌面歌詞。**

[![最新版本](https://img.shields.io/github/v/release/BeteixZ/floating-lyrics-wam?label=release&color=ff2f5f)](https://github.com/BeteixZ/floating-lyrics-wam/releases/latest)
[![下載次數](https://img.shields.io/github/downloads/BeteixZ/floating-lyrics-wam/total?color=ff4f8b)](https://github.com/BeteixZ/floating-lyrics-wam/releases)
[![建置](https://img.shields.io/github/actions/workflow/status/BeteixZ/floating-lyrics-wam/build.yml?branch=main&label=build)](https://github.com/BeteixZ/floating-lyrics-wam/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
![平台](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)

[English](README.md) · [简体中文](README.zh-CN.md) · **繁體中文**

[**下載**](https://github.com/BeteixZ/floating-lyrics-wam/releases/latest) · [功能](#功能) · [快速開始](#快速開始) · [常見問題](#常見問題) · [更新記錄](RELEASE_NOTES.md)

<img src=".github/assets/demo.gif" alt="純淨模式歌詞卡片：逐字高亮、長音發光、翻譯與下一句" width="720">

</div>

Windows 版 Apple Music 沒有桌面歌詞。Apple Music Lyrics 提供一個永遠置頂、跟隨播放進度的歌詞浮層：它直接讀取 Apple Music 已快取的歌詞，逐字高亮，顯示翻譯與羅馬音；當 Apple 的歌詞缺漏或不完整時，再從網易雲音樂、QQ 音樂與 LRCLIB 補齊。

> [!NOTE]
> 專案仍在積極開發中（0.x 版本），功能與設定在不同版本之間可能變動。

## 功能

**歌詞**

- 讀取 Apple Music 本身的歌詞快取，包括逐字時間軸、翻譯、羅馬音、對唱與和聲
- 可選用線上歌詞：網易雲音樂、QQ 音樂、LRCLIB，優先選用逐字與翻譯涵蓋更完整的來源
- Apple 有這首歌時，線上歌詞會對齊到 Apple 每行的起始時間，修正部分平台歌詞提前或延遲的問題
- 嚴格比對並驗證 Apple 曲目：寧可不顯示，也不顯示錯誤的歌詞
- 按右鍵可重新搜尋歌詞，或為目前歌曲手動選擇其他版本

**顯示**

- 逐字高亮；拉長音的字會像 Apple Music 一樣微微放大並發光
- 換行動畫流暢，並略早於演唱切換，確保下一句即時可讀
- 對唱時兩位歌手的歌詞以兩行同樣大小顯示，各有高亮與翻譯
- 羅馬音顯示在原文下方；日文歌詞缺少讀音時可離線產生羅馬字
- 單行、雙行與上下文三種版面

**視窗**

- 純淨模式：無邊框歌詞卡片隨內容自動調整大小，過渡平滑，縮小前會先預判接下來幾句
- 滑鼠穿透、懸停淡化，暫停時自動隱藏
- 支援每部顯示器各自的 DPI，一般模式與純淨模式分別記住位置

**個人化與隱私**

- 字型（拉丁文、中文、譯文可分別設定）、顏色、發光、不透明度與時間偏移，可按歌曲記住偏移
- 介面支援繁體中文、简体中文與英文，可透過 [JSON 語言套件](docs/localization.md)擴充（英文文件）
- 透過 GitHub Releases 自動更新：有新版本時系統匣提示，一鍵下載、驗證並重新啟動
- 關閉線上功能後可完全離線使用；不需要帳號、登入或 API 金鑰

## 快速開始

**系統需求：** Windows 10 19041 以上版本或 Windows 11、Microsoft Store 版 Apple Music，以及 x64 [.NET 10 桌面執行階段](https://dotnet.microsoft.com/download/dotnet/10.0)。

1. 從[最新版本](https://github.com/BeteixZ/floating-lyrics-wam/releases/latest)下載 `AppleMusicLyrics-vX.Y.Z-win-x64.zip`。
2. 解壓縮到任意位置，執行 `AppleMusicLyrics.App.exe`。
3. 在 Apple Music 播放歌曲，歌詞就會出現在浮層上；程式常駐於系統匣。

之後程式會自動檢查新版本並自行更新：可在系統匣選擇**檢查更新…**，或等待更新提示。設定儲存在 `%LOCALAPPDATA%\AppleMusicLyrics`，更新後保留。（0.9.0 之前的版本沒有更新功能，需要先從系統匣結束舊版本，手動替換一次資料夾。）

## 使用方式

| 操作 | 方式 |
| --- | --- |
| 顯示/隱藏、純淨模式、單行/雙行模式、滑鼠穿透、設定 | 系統匣圖示選單 |
| 移動浮層 | 直接拖曳（需先關閉**滑鼠穿透**） |
| 重新搜尋歌詞或選擇其他歌詞版本 | 在歌詞上按右鍵 |
| 歌詞偏早或偏晚 | **設定 → 歌詞 → 歌詞提前量**，可開啟**按歌曲記住歌詞偏移** |
| 開關逐字高亮、翻譯或羅馬音 | **設定 → 歌詞** |
| 停用所有網路連線 | **設定 → 進階** |

所有設定項目的說明請見 [Settings and usage](docs/settings.md)（英文）。

## 歌詞來源

播放歌曲時，程式依以下優先順序選擇能找到的最佳歌詞：

| 優先順序 | 來源 | 使用條件 |
| :---: | --- | --- |
| 1 | Apple Music（本機快取） | 已開啟的功能（逐字、翻譯）涵蓋歌曲的大部分 |
| 2 | 網易雲音樂 | 這些功能的涵蓋明顯更完整，且確認是同一份歌詞 |
| 3 | QQ 音樂 | 條件同上，且網易雲沒有更好的結果 |
| 4 | Apple Music（本機快取） | 其他情況；缺少的功能可從其他來源補到 Apple 的時間軸上 |
| 5 | LRCLIB | 以上皆無時 |

逐字高亮與翻譯都關閉時，只要 Apple 有歌詞就一律使用 Apple 的歌詞。網易雲或 QQ 的歌詞只有在確認與 Apple 是同一份歌詞時才會取代 Apple，並會對齊到 Apple 的逐行時間。完整規則與各服務會收到哪些資訊，請見 [Lyric sources, matching, and privacy](docs/lyric-sources.md)（英文）。

## 常見問題

<details>
<summary><b>某首歌沒有顯示歌詞。</b></summary>

程式會先尋找 Apple Music 已快取的歌詞；沒有相符結果且開啟了線上歌詞時，再搜尋網易雲、QQ 音樂與 LRCLIB。如果這些來源都沒有這首歌，或比對把握不足，就不會顯示。可以在右鍵選單選擇**重新搜尋歌詞**，或用**選擇歌詞…**手動挑選候選。進階設定中的**允許低可信度比對**會以準確度換取涵蓋率。
</details>

<details>
<summary><b>歌詞稍微偏早或偏晚。</b></summary>

在「歌詞」分頁調整**歌詞提前量**，用來補償音訊輸出延遲與個人習慣。開啟**按歌曲記住歌詞偏移**後，可以為個別歌曲保存單獨的校正。
</details>

<details>
<summary><b>沒有翻譯或逐字高亮。</b></summary>

這兩項都取決於歌詞來源是否提供；程式不會自行產生翻譯，也不會捏造逐字時間。請確認已開啟**逐字高亮**與**顯示歌詞翻譯**，並開啟線上歌詞，讓程式尋找提供這些內容的來源。
</details>

<details>
<summary><b>Windows SmartScreen 顯示警告。</b></summary>

發行版本沒有程式碼簽章，剛下載的新版本可能出現「Windows 已保護您的電腦」。如果檔案來自本儲存庫的 Releases 頁面，點選**其他資訊 → 仍要執行**即可。原始碼與建置流程皆公開。
</details>

<details>
<summary><b>程式跟隨了其他播放器或瀏覽器。</b></summary>

預設只跟隨 Apple Music 的媒體工作階段。進階設定中的**允許非 Apple Music 的媒體工作階段**會放寬限制，因此預設關閉。
</details>

<details>
<summary><b>可以離線使用嗎？連線時會傳送什麼？</b></summary>

關閉線上功能與自動檢查更新後，程式只讀取 Apple Music 的本機快取，不會傳送任何內容。開啟後，會將歌名與歌手（部分服務還包括專輯與長度）傳送給 Apple 的 iTunes Search API 及已啟用的歌詞服務，並向 GitHub 查詢最新版本號。詳見 [Network and privacy](docs/lyric-sources.md#network-and-privacy)（英文）。
</details>

## 已知限制

- 僅支援 Windows，且需要 Apple Music 執行中才能追蹤播放。
- 網易雲音樂與 QQ 音樂使用的是未公開介面，可能變更、受地區限制或被限流。
- 逐字時間與翻譯取決於各來源；缺漏或不相符時會退回一般的逐行顯示。
- 純淨模式調整大小時，透明分層視窗在部分顯示卡驅動程式上可能出現邊緣閃爍。

## 從原始碼建置

```powershell
dotnet build AppleMusicLyrics.sln -c Release
dotnet test AppleMusicLyrics.sln -c Release --no-build
./build.ps1   # 發行到 artifacts/publish/
```

環境需求、發行目錄結構與專案結構請見 [Development](docs/development.md)（英文）。

## 參與貢獻

歡迎提交 Issue 與 Pull Request。回報歌詞問題時，請附上歌名、歌手與診斷資訊（系統匣 → **顯示診斷摘要**，或透過**查看診斷詳情…**開啟完整報告）。修改比對邏輯時請以準確為優先，避免在每個畫面更新時進行媒體、磁碟或網路 I/O。

## 授權條款

本專案自有程式碼採用 [MIT 授權條款](LICENSE)。QQ 音樂與網易雲音樂的移植程式碼包含源自 LyricsKit 的檔案，採用 [Mozilla Public License 2.0](licenses/MPL-2.0.txt)。涉及的檔案、固定的上游版本、致謝與原始碼取得方式請見 [Third-party notices](THIRD_PARTY_NOTICES.md)。

軟體授權條款只適用於程式碼，並不授予使用音樂服務私有介面的權限，也不授予重製、再散布或商業使用所回傳歌詞的權利。

## 致謝

靈感來自 macOS 上 [ddddxxx 的 LyricsX](https://github.com/ddddxxx/LyricsX)。QQ 音樂與網易雲音樂的整合移植自 [MxIris-LyricsX-Project 維護的 LyricsKit 分支](https://github.com/MxIris-LyricsX-Project/LyricsKit)，其基礎為 [ddddxxx 的 LyricsKit](https://github.com/ddddxxx/LyricsKit)。所選基準為 LyricsKit 1.8.3，提交 [`6f071990d0c9c6f48d29284db805990bffb912ed`](https://github.com/MxIris-LyricsX-Project/LyricsKit/tree/6f071990d0c9c6f48d29284db805990bffb912ed)，即維護版 LyricsX v1.8.9 所用版本。致謝與授權條款詳情保留在 [Third-party notices](THIRD_PARTY_NOTICES.md) 中。
