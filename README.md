# Claude Quota Monitor / Claude 額度監控

A tiny Windows desktop app that watches the usage of one or more **Claude subscription** accounts (Pro / Max) at a glance: 5-hour session window, 7-day weekly limit, reset countdowns, per-model limits and extra-usage balance.

<!-- -->

一個輕巧的 Windows 桌面小工具，一眼盯著一支或多支 **Claude 訂閱**帳號（Pro / Max）的用量：5 小時工作階段、7 天週限、重置倒數、各模型分項與加購額度。

Author / 作者: [YangMieh](https://github.com/YangMieh) (小咩)

Version / 版本: v1.2.6

Phone dashboard / 手機儀表板: **https://claude-quota.web.app** (sign in with Google / 用 Google 登入)

On your phone, use the browser menu's **Add to Home Screen** to install it as an app. / 在手機上用瀏覽器選單的「**加入主畫面 / 新增至主畫面**」就能裝成 App。

---

## Features

- Multiple accounts at once, each on its own card (percent used, reset countdown, plan tier).
- The account your Claude CLI is currently signed into is pinned to the top with a badge.
- Adds an account by running the official `claude` login **into an isolated config folder**, so it never touches your real `~/.claude` login.
- Reads the subscription-usage token the login produces, refreshes it automatically, and never asks you to copy-paste anything.
- Tokens are stored per-Windows-user, DPAPI-encrypted; the exe itself ships with zero tokens.
- Optional cloud sync: sign in with Google once and the PC pushes your usage to your own dashboard at **https://claude-quota.web.app**, so you can watch it from your phone or any browser. Only the usage numbers are pushed — tokens never leave the PC.
- On your phone, open the browser menu and choose **Add to Home Screen** — the dashboard then installs and runs like an app.

<!-- -->

## 功能

- 一次盯多支帳號，每支一張卡（用了幾 %、重置倒數、訂閱方案）。
- 你 Claude CLI 目前登入的那支會**置頂並標上徽章**。
- 加帳號 = 把官方 `claude` 登入**跑進一個獨立設定資料夾**，完全不動你真正的 `~/.claude` 登入。
- 讀取登入產生的訂閱用量權杖、自動續命，全程不用你複製貼上任何東西。
- 權杖以 Windows 使用者身分 DPAPI 加密存放；exe 本身不含任何權杖。
- 可選的雲端同步：用 Google 登入一次，電腦就會把用量推到你自己的儀表板 **<https://claude-quota.web.app>**，手機或任何瀏覽器都能看。只推用量數字，權杖永遠留在電腦。
- 在手機上打開瀏覽器選單，選「**加入主畫面 / 新增至主畫面**」，儀表板就會安裝成、用起來像一個 App。

---

## Phone / cloud dashboard · 手機 / 雲端儀表板

Open **https://claude-quota.web.app** and sign in with Google. On the desktop app click "☁ 手機同步" then "用 Google 登入同步" (the same Google account) — the PC pushes your usage to the cloud every 30 seconds and the phone page updates live. Only usage data is synced; your Claude tokens stay encrypted on the PC.

Want it to feel like a real app? On your phone, open the browser menu and choose **Add to Home Screen** — the dashboard installs as an icon and opens full-screen, no native APK/iOS build needed.

<!-- -->

開 **https://claude-quota.web.app** 用 Google 登入。桌面程式點「☁ 手機同步」→「用 Google 登入同步」（同一個 Google 帳號）——電腦每 30 秒把用量推上雲，手機那頁即時更新。只同步用量數字，Claude 權杖永遠加密留在電腦。

想更像一個 App？在手機上打開瀏覽器選單，選「**加入主畫面 / 新增至主畫面**」，儀表板就會裝成一個圖示、開起來是全螢幕，不用另外寫原生 APK/iOS。

---

## How it works

Claude only issues a usage-capable token (with the `user:profile` scope) through a real `claude` sign-in — `setup-token` gives an inference-only token that returns 403 on the usage endpoint, and rolling your own browser OAuth is blocked by anti-bot on the authorize step. So this app takes the one path that works:

1. It creates a throwaway config folder and runs `claude auth login` with `CLAUDE_CONFIG_DIR` pointed at it. The official login opens your browser (which passes the human check), and writes the account's credentials into that isolated folder only.
2. The app reads the token from that folder, stores it encrypted, and **deletes the folder immediately** so no plaintext token lingers.
3. It polls `GET /api/oauth/usage` per account (throttled), and refreshes each token on its own when it nears expiry.

Because every account uses its own isolated folder, multiple accounts coexist without conflict and your main CLI login is never modified.

<!-- -->

## 運作原理

能讀用量的權杖（有 `user:profile` 範圍）只有「真的 `claude` 登入」才會產生——`setup-token` 給的是「只能跑推論」的權杖，打用量端點會回 403；自己刻瀏覽器 OAuth 又在授權那步被反機器人擋掉。所以本工具走唯一可行的那條路：

1. 建一個用完即丟的設定資料夾，把 `CLAUDE_CONFIG_DIR` 指到它、跑 `claude auth login`。官方登入會開你的瀏覽器（真瀏覽器過得了人機驗證），並只把該帳號的憑證寫進那個獨立資料夾。
2. 從那個資料夾讀出權杖、加密存起來，然後**立刻刪掉資料夾**，磁碟上不留明碼權杖。
3. 每支帳號各自輪詢 `GET /api/oauth/usage`（有節流），權杖快到期時各自自動刷新。

因為每支帳號用自己的獨立資料夾，多帳號並存不衝突，你主要的 CLI 登入也完全不會被改動。

---

## Requirements

- Windows 10 / 11, with the WebView2 Runtime (built into Win11).
- .NET Framework 4.x (built into Windows).
- [Claude Code](https://claude.com/claude-code) (`claude` CLI) installed — needed to sign an account in.

<!-- -->

## 需求

- Windows 10 / 11，含 WebView2 Runtime（Win11 內建）。
- .NET Framework 4.x（Windows 內建）。
- 已安裝 [Claude Code](https://claude.com/claude-code)（`claude` CLI）——加帳號登入時需要。

---

## Build & Run

Build with the bundled script (uses the .NET Framework `csc`):

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

The build produces a **single self-contained `ClaudeQuotaMonitor.exe`** — the dashboard, icon and WebView2 dlls are all embedded, so there is just one file to run and to share (the native loader is extracted to `%LOCALAPPDATA%` on first launch). Run it with normal user rights; it lives in the system tray and opens a window. To add an account, click "+ 加入帳號" then "開始登入" and authorize in the browser that pops up.

<!-- -->

## 建置與執行

用附的腳本編譯（走 .NET Framework 的 `csc`）：

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

產出的是**單一自帶的 `ClaudeQuotaMonitor.exe`**——介面、圖示、WebView2 dll 全部內嵌，所以只有一個檔要跑、要分享（原生 loader 會在首次啟動時自解到 `%LOCALAPPDATA%`）。用一般權限執行，它會在系統匣、開一個視窗。加帳號點「＋ 加入帳號」→「開始登入」，在跳出的瀏覽器授權即可。

---

## Privacy & Security

- Tokens are DPAPI-encrypted (CurrentUser) in `%APPDATA%\ClaudeQuotaMonitor\accounts.json`; only your Windows account can decrypt them.
- The exe contains no tokens. When you share it, share only the exe + support files, never your `%APPDATA%\ClaudeQuotaMonitor\` folder.
- The isolated login folder (with its plaintext credential file) is deleted the moment the token is read.
- The local server binds to `localhost` only.

<!-- -->

## 隱私與安全

- 權杖以 DPAPI（CurrentUser）加密存在 `%APPDATA%\ClaudeQuotaMonitor\accounts.json`，只有你這個 Windows 帳號解得開。
- exe 不含任何權杖。分享時只給 exe＋相依檔，**絕不要**給你的 `%APPDATA%\ClaudeQuotaMonitor\` 資料夾。
- 隔離登入的資料夾（含裡面的明碼憑證檔）在讀出權杖的當下就被刪除。
- 本機伺服器只綁 `localhost`。

---

## Dev log

- Tried to replicate Claude's browser OAuth inside the app (embedded WebView, then the real browser). Every parameter combination was rejected at `claude.ai/v1/oauth/.../authorize` with "Invalid request format" — the grant is gated by Arkose anti-bot plus a first-party client check, so third-party replication is a dead end.
- Switched to `claude setup-token`. Its token turned out to be inference-scoped: `GET /api/oauth/usage` returned 403 "does not meet scope requirement user:profile". Wrong tool for reading usage.
- Confirmed the only usage-capable token comes from a normal `claude` sign-in. Discovered `CLAUDE_CONFIG_DIR` lets each sign-in live in its own folder.
- Final design: run `claude auth login` into an isolated folder, read the token, delete the folder, and self-refresh. Verified end to end — multiple accounts, correct scope, auto-refresh, and the main CLI login untouched.
- Added: current-CLI-account pinning via `claude auth status --json`, plan tier under the email, and a re-login button that only appears when a card goes offline.
- v1.2.5: optional Firebase cloud sync. Sign in with Google on the desktop app (desktop OAuth + PKCE) and it pushes usage to https://claude-quota.web.app, viewable from any phone or browser. Only usage numbers are synced; tokens stay encrypted on the PC. The mobile page is a PWA, so opening the phone browser menu and choosing **Add to Home Screen** installs it like an app — no native APK/iOS build is needed.
- v1.2.6: system-tray polish — minimize or close hides to the tray, plus an "auto-start on boot" toggle and an optional "start minimized" for boot. Cloud push keeps running in the background regardless of the window.

<!-- -->

## 開發日誌

- 先試在 App 內自幹 Claude 的瀏覽器 OAuth（內嵌 WebView，再換真瀏覽器）。所有參數組合都在 `claude.ai/v1/oauth/.../authorize` 被回「Invalid request format」——授權那步被 Arkose 反機器人＋第一方 client 驗證擋著，第三方自幹是死路。
- 改用 `claude setup-token`。結果它的權杖是「只能跑推論」的：打 `GET /api/oauth/usage` 回 403「缺 user:profile 範圍」。讀用量用錯工具了。
- 確認能讀用量的權杖只有「一般 `claude` 登入」會產生。發現 `CLAUDE_CONFIG_DIR` 可以讓每次登入各自住在自己的資料夾。
- 最終設計：把 `claude auth login` 跑進獨立資料夾、讀出權杖、刪掉資料夾、自己刷新。端到端實測通過——多帳號、範圍正確、自動刷新，且主 CLI 登入完全沒被動到。
- 另加：用 `claude auth status --json` 置頂目前 CLI 帳號、email 下方顯示訂閱方案、卡片斷線時才出現的重新登入鈕。
- v1.2.5：可選的 Firebase 雲端同步。桌面用 Google 登入（桌面 OAuth + PKCE），把用量推到 **<https://claude-quota.web.app>**，手機或任何瀏覽器都能看。只同步用量數字，權杖仍加密留在電腦。手機頁是 PWA，在手機瀏覽器選單選「**加入主畫面 / 新增至主畫面**」就會裝成 App，不用寫原生 APK/iOS。
- v1.2.6：系統匣打磨——最小化或關閉都縮到匣，加上「開機自動啟動」開關與可選的「最小化啟動」（給開機用）。雲端推送跟視窗無關，縮小也照推。
