# UaaL Android 客户端

原生 Android 壳 + Unity 导出的 `unityLibrary`；首页、资源管理、教程播放与语音问答都走 Compose 控制层，Unity 负责底层动画与音频的确定性播放。

当前客户端已经把 POC 的 Java + `Button` 控制层替换为：

- Kotlin `MainActivity` + Jetpack Compose 全屏控制层，Unity 仍作为底层渲染区域；
- 首页游戏目录 / 搜索 / 历史 / 资源管理，manifest 驱动的本地内容仓库与增量下载 / 断点续传 v1；
- 教程播放器控制层（时间轴 / 章节 / 播放控制）与 Unity 状态回传；
- 问答面板支持键盘 / 语音模式切换，切换按钮固定在输入行最左侧：文字模式显示输入框和发送按钮，语音模式只显示“按住说话”并在松手后直接发送识别结果；切回文字模式不自动弹出键盘，点输入框后才唤起；切换模式保留未发送的文字草稿，语音识别不覆盖文字草稿；
- 语音 PTT 录音时按钮内显示动态“正在聆听.” / “正在聆听..” / “正在聆听...”和“放手结束”；输入区不再常驻语音状态文字；
- 松手后的 ASR 识别期间，消息区右侧显示带跳动圆点的临时用户气泡；识别失败时移除并弹出短 Toast，识别成功后由识别出的用户消息气泡接替；
- 语音 PTT 上划任意高度进入取消通道后松手可放弃本次识别，左右或下滑会恢复发送、ASR 识别完成后自动发送、回答 TTS、自动播报 / 重播 / 继续播放；
- 问答页支持在顶部栏或空白区域向上滑动返回播放页；消息列表内上滑仍用于滚动，避免手势冲突；
- 点击问答页空白区域会清除输入焦点并收起软键盘；
- 播放页支持向下滑动打开问答页；从底部播放控制条起手的滑动不触发，避免和音量/进度操作冲突；
- 通过固定 GameObject `AndroidTutorialBridge` 的字符串协议控制播放。

不变的是：不拆仓、不改游戏 JSON / animation / compiled 语义，Unity 仍只负责确定性播放。

## 目录

```text
clients/android/
  settings.gradle       include :app + 外部 :unityLibrary
  build.gradle          Android / Kotlin / Compose 插件版本
  gradle.properties     AndroidX、Kotlin、Compose 通用配置
  gradlew / gradlew.bat Gradle 入口；路径不写死在 wrapper 中
  build-uaal.bat        唯一的机器路径配置块 + 固定构建顺序
  local.properties.example  本地配置模板（真正的 local.properties 不入 Git）
  app/
    build.gradle
    src/main/AndroidManifest.xml
    src/debug/AndroidManifest.xml          debug 明文 HTTP 配置
    src/main/java/com/boardai/tutorial/uaal/MainActivity.kt
    src/main/java/com/boardai/tutorial/uaal/UnityBridgeCallback.kt
    src/main/java/com/boardai/tutorial/uaal/content/      manifest 模型 / 本地版本仓库 / 增量下载 / 断点续传
    src/main/java/com/boardai/tutorial/uaal/catalog/      游戏目录拉取与搜索
    src/main/java/com/boardai/tutorial/uaal/home/         首页 / 搜索 / 历史 / 资源管理
    src/main/java/com/boardai/tutorial/uaal/player/       播放控制 / 时间轴 / 章节 / Unity 状态
    src/main/java/com/boardai/tutorial/uaal/qa/           问答面板 / 会话 / 问答 TTS
    src/main/java/com/boardai/tutorial/uaal/timeline/     播放时间轴模型
    src/main/java/com/boardai/tutorial/uaal/voice/        ASR / TTS / 录音与回答播放
```

Unity 侧：

```text
clients/unity/Assets/Scripts/Tutorial/AndroidTutorialBridge.cs
clients/unity/Assets/Scripts/Tutorial/TutorialCuePlayer.cs
clients/unity/Assets/Scripts/Tutorial/TutorialTouchControls.cs
```

## 构建环境：集中配置

本机绝对路径统一记录在仓库根目录 `config/local.properties`（已 gitignore，模板为 `config/local.properties.example`）；README 中的 `%..._WIN%` 均引用该文件中的同名键。`build-uaal.bat` 仍有自己的可移植默认配置块，调用前用下列变量值覆盖即可。

| 环境变量 | 配置键（`config/local.properties`） |
| --- | --- |
| `UNITY_ROOT` | `UNITY_ROOT_WIN` |
| `UNITY_EXE` | `UNITY_EXE_WIN` |
| `JAVA_HOME` | `JAVA_HOME_WIN` |
| `ANDROID_HOME` | `ANDROID_HOME_WIN` |
| `UNITY_GRADLE_HOME` | `UNITY_GRADLE_HOME_WIN` |
| `GRADLE_USER_HOME` | `GRADLE_USER_HOME_WIN` |

可在调用前用配置文件中的值覆盖任意变量，例如：

```bat
rem 先把 config/local.properties 中对应值设成这些 *_WIN 环境变量
set UNITY_ROOT=%UNITY_ROOT_WIN%
set UNITY_EXE=%UNITY_EXE_WIN%
set JAVA_HOME=%JAVA_HOME_WIN%
set ANDROID_HOME=%ANDROID_HOME_WIN%
set UNITY_GRADLE_HOME=%UNITY_GRADLE_HOME_WIN%
set GRADLE_USER_HOME=%GRADLE_USER_HOME_WIN%
```

`gradlew.bat` 行为：

1. 如果设置了 `UNITY_GRADLE_HOME` 且其中存在 Unity 自带 Gradle，优先复用；
2. 否则回退到标准 Gradle Wrapper；
3. `JAVA_HOME` 未设置时直接使用 `PATH` 中的 `java`。

`local.properties` 由 `build-uaal.bat` 在缺失时根据 `ANDROID_HOME` 生成，不入 Git；其中 `sdk.dir` 取 `config/local.properties` 的 `ANDROID_HOME_WIN`（写成正斜杠）。手工维护时只需确认 API 地址：

```properties
board.api.baseUrl=http://127.0.0.1:5000
```

`board.api.baseUrl` 通过 `BuildConfig.BOARD_API_BASE_URL` 注入；不写死 PC IP。开发时推荐真机 `adb reverse`，见下文“内容更新 v1”。

## 构建 UaaL APK

固定顺序：

1. Unity 导出 Android Library；
2. 校验 `unityLibrary`；
3. Gradle `assembleDebug`；
4. 输出 APK 路径。

```bat
rem %BOARD_WORKSPACE_WIN% 来自 config/local.properties
cd %BOARD_WORKSPACE_WIN%\clients\android
build-uaal.bat
```

APK：

```text
clients\android\app\build\outputs\apk\debug\app-debug.apk
```

如果只想在 Unity 已导出后重新构建原生壳：

```bat
rem 变量值来自 config/local.properties
cd %BOARD_WORKSPACE_WIN%\clients\android
set JAVA_HOME=%JAVA_HOME_WIN%
set UNITY_GRADLE_HOME=%UNITY_GRADLE_HOME_WIN%
gradlew.bat assembleDebug
```

Unity 脚本检查：

```bash
python3 tools/ops/check_unity_scripts.py
```

### JVM 单元测试

当前 12 个测试文件覆盖内容状态 / 版本清理 / 更新器 / 首页协调 / 播放会话 / 时间轴 / Unity 加载队列 / 问答语音控制器：

```bat
rem %BOARD_WORKSPACE_WIN% 来自 config/local.properties
cd %BOARD_WORKSPACE_WIN%\clients\android
gradlew.bat testDebugUnitTest
```

该命令需要已导出的 `unityLibrary`、`local.properties` 中的 Android SDK 与可用 JDK；文档更新时未重新执行 Android 测试，真机验收范围也待复测。

### 旧 Unity 独立 APK 构建入口

旧入口仍在：

```text
clients/unity/Assets/Editor/AndroidDebugBuild.cs
```

产物为旧包名 `com.boardai.tutorial` 的：

```text
clients/unity/Builds/Android/BoardAI.apk
```

Windows 中文用户名下，Unity 内部 Gradle 的 prefab 步骤会因为 `%USERPROFILE%\.gradle` 编码问题失败，所以直接构建旧 APK 时也必须设置 ASCII 的 `GRADLE_USER_HOME`：

```bat
rem 变量值来自 config/local.properties
set GRADLE_USER_HOME=%GRADLE_USER_HOME_WIN%
set JAVA_HOME=%JAVA_HOME_WIN%
set ANDROID_HOME=%ANDROID_HOME_WIN%
set ANDROID_SDK_ROOT=%ANDROID_HOME%

%UNITY_EXE_WIN% ^
  -batchmode -quit ^
  -projectPath %BOARD_WORKSPACE_WIN%\clients\unity ^
  -executeMethod AndroidDebugBuild.BuildApk ^
  -logFile %BOARD_WORKSPACE_WIN%\clients\unity\Builds\Android\build.log
```

该入口与 UaaL 正式客户端互不影响，不会覆盖 `com.boardai.tutorial.uaal`。

## 原生控制层

`MainActivity` 直接继承 `com.unity3d.player.UnityPlayerGameActivity`。`UnityPlayerGameActivity` 已基于 AndroidX `ComponentActivity`，因此可以承载标准 `ComposeView` 生命周期。

控制层实现：

- `ComposeView` 通过 `android.R.id.content.addView` 全屏叠加在 Unity SurfaceView 上方；画面区域触摸透传给 Unity，只有实际控件接收 Compose 手势；
- 顶部 `PlayerTopBar` 提供返回首页与当前章节路径；
- 底部 `PlayerTransportBar` 集中了合并时间轴 / 章节分段 / 进度与播放头（`PlayerTimelineBar`）、cue 文本、上一 cue / 下一 cue、中央播放 / 暂停、-15 秒 / +15 秒、音量、章节列表与 debug 调试开关；
- 首页使用“播放动画 / 提问”模式切换，同一套游戏列表按当前模式复用：选“播放动画”后点游戏进入教程，选“提问”后点游戏直接进入问答；播放页仍保留右下角常驻的 `QaEntryButton`，点击默认使用当前游戏与当前播放位置；
- 单击画面显示 / 隐藏控制层，双击左 / 右半屏相对快退 / 快进，进度条支持拖动 seek；
- 打开问答时暂停动画并淡出控制层，关闭后按原播放状态继续或重播；
- 控制按钮暂时保留无 ripple 的 Compose 自定义实现，Material ripple 留作后续单独验证。

Unity ready 握手：

- `AndroidTutorialBridge` 在 `Start()` 后通过状态 JSON 上报 `unityReady`；
- `MainActivity` / `PlayerSessionController` 观察 `UnityStatusHolder.status`，不使用固定 `postDelayed`；
- `UnityLoadQueue.onUnityReady()` 只执行一次：关闭 Unity 旧触控层、执行 pending unload/load（`LoadGameWithRoot` / `LoadGame` / `UnloadGame`）、发送 `RequestStatus`；
- `MainActivity.onUnityReady()` 再发送 `SetDebugBuild`（debug 下附带 `SetDebugOverlay`）；
- Unity 未就绪时控制条状态显示“等待 Unity…”。

字幕避让：

- Unity 字幕绘制在底部安全区；
- Compose 控制层固定在顶部状态栏下方，高度约 100dp；
- 两者区域不重叠，不永久遮挡字幕。

屏幕常亮：

```kotlin
window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
```

## Unity bridge 协议

固定 GameObject 名称不变：

```csharp
public const string GameObjectName = "AndroidTutorialBridge";
```

`UnitySendMessage` 只能传 string，所以所有 bridge 参数均为 string，内部解析后调用 `TutorialCuePlayer`：

| 方法 | 参数 | 作用 |
| --- | --- | --- |
| `TogglePause` | `""` | 切换暂停/继续 |
| `Pause` | `""` | 暂停 |
| `Resume` | `""` | 继续 |
| `SeekRelative` | `"-15"` / `"15"` | 相对快退/快进 |
| `AdjustVolume` | `"-0.1"` / `"0.1"` | 相对调节音量 |
| `SetVolume` | `"0"` ~ `"1"` | 绝对设置音量 |
| `LoadGameWithRoot` | `{gameId}|{versionRoot}` | 正式流程：设置 `gameId` 和 `tutorialRoot`（`versionRoot`）后 `ReloadGame`；`versionRoot` 为 `context.filesDir/board-content/versions/{version}` |
| `SetContentRoot` | version 目录 | 旧接口兼容；Unity 仍会拼接 `gameId`，正式流程使用 `LoadGameWithRoot` |
| `ReloadGame` | `""` | 停止当前播放，重新 ResolveGameRoot + LoadAndPlay |
| `CheckContentUpdate` | `""` | 协议占位；内容检查由 Compose 原生层在首页/资源管理打开时触发 |
| `SetUnityTouchControlsEnabled` | `"true"` / `"false"` | 启用/禁用旧 Unity IMGUI 触控层 |
| `SetDebugBuild` | `"1"` / `"0"` | 原生告知 Unity 当前是否是 debug 可调包包；release 显式关闭调试门禁 |
| `SetDebugOverlay` | `"1"` / `"0"` | 打开/关闭当前 cue 信息和 on-stage zone 范围框 |

`TutorialCuePlayer` 新增：

```csharp
public void Pause()
public void Resume()
public void ReloadGame()
public void SetVolume(float value)
public void SetUnityTouchControlsEnabled(bool enabled)
public float Position
public float Duration
public bool UnityTouchControlsEnabled
```

`TutorialCuePlayer.ResolveGameRoot()` 优先级：

1. 显式 `tutorialRoot`（Android 正式流程由 `LoadGameWithRoot` 设置，指向 App 内部私有版本目录）；
2. `Application.persistentDataPath/board-content/active.json` 中当前 game 的 `root`（兼容读取 root 元数据；Android 正式流程不依赖）；
3. 旧 fallback：`Application.persistentDataPath/{gameId}`（手动 adb push）；
4. StreamingAssets / 仓库 `content/games/{gameId}`。

## Debug APK 调试模式（当前 cue + zone 范围）

只有 Debug APK 会在首页顶部显示统一的“调试”开关；开关状态对动画与问答共用：

- Debug APK 启动时默认开启调试；Release 初始状态关闭；
- 打开后，Unity 左上角显示当前 cue 的序号/id、章节路径、文本和播放时间，同时绘制当前 stage 中所有 on-stage zone 的彩色范围框与名称；
- 打开后，问答回答下方显示一行回答完成摘要，例如 `回答完成｜6.87 秒｜模型调用 3 次｜规则查询 2 次｜正式证据 2 条｜协议纠偏 0 次｜经过一次候选选择`；关闭时不显示该行；
- 摘要行同时写入 Android Logcat，tag 为 `BoardAI-QaVoice`，不受 UI 开关影响；
- 开关状态保存在原生 Compose 状态和 Unity `TutorialCuePlayer` 请求状态中，换游戏 / 内容 reload 后保留。

Release APK 没有调试入口，并且原生层会显式关闭 Unity 调试开关：`MainActivity.onUnityReady()` 在 `playerSession.onUnityReady()` 之后发送 `SetDebugBuild("0")`。Unity 侧 `TutorialAnimPlayer` 的键盘 Z 切换受 `debugToggleEnabled` 门禁，release 下无法通过键盘打开。

相关代码位置：

- `MainActivity.kt`: `debugOverlayEnabled`、`toggleDebugOverlay()`、`onUnityReady()`
- `HomeScreen.kt`: 首页统一调试 Switch
- `qa/QaEvidence.kt` / `qa/QaPanel.kt`: 回答完成摘要生成、日志和 Debug 可见性
- `AndroidTutorialBridge.cs`: `SetDebugBuild(string)` / `SetDebugOverlay(string)`
- `TutorialCuePlayer.cs`: `SetDebugBuildAllowed(bool)` / `SetDebugOverlay(bool)` / `ApplyDebugOverlay()`
- `TutorialAnimPlayer.cs`: `debugToggleEnabled` / `SetDebugOverlay(bool)`

## 状态回传

已实现 Unity -> Android 状态回传。

Unity 每 0.5 秒或控制命令执行后，通过：

```csharp
AndroidJavaClass("com.boardai.tutorial.uaal.UnityBridgeCallback")
    .CallStatic("postStatus", json);
```

把 JSON 发送到 Kotlin：

```kotlin
com.boardai.tutorial.uaal.UnityBridgeCallback.postStatus(String)
```

Kotlin 侧用主线程 `Handler(Looper.getMainLooper())` 更新 Compose 状态。状态字段：

```text
unityReady, isPlaying, isPaused, volume, cueId, cueText, cueIndex, cueTotal,
position, duration, touchControlsEnabled
```

## Unity 临时触控层策略

Android 构建不挂载 `TutorialTouchControls`：

```csharp
#if UNITY_ANDROID && !UNITY_EDITOR
    SetUnityTouchControlsEnabled(false);
#else
    SetUnityTouchControlsEnabled(true);
#endif
```

作用：

- 正式 UaaL 客户端只显示一套 Compose 控件；
- Editor / Desktop 使用 Unity 控制层，方便调试；
- 原生层启动后也会显式发送 `SetUnityTouchControlsEnabled("false")`；
- Unity 独立 APK 构建入口在 `Assets/Editor/AndroidDebugBuild.cs`，包名 `com.boardai.tutorial`，不会覆盖 `com.boardai.tutorial.uaal`。

## 内容更新 v1

### 生成 manifest

PC 侧：

```bat
rem %BOARD_WORKSPACE_WIN% 来自 config/local.properties
cd /d %BOARD_WORKSPACE_WIN%
python3 tools/content/build_content_manifest.py --game splendor
```

输出：

```text
content/manifests/splendor.json
content/releases/splendor/{version}/...
```

builder 只收集 Unity/Android 真正运行依赖：`tutorial/{track}.runtime.json`、`tutorial/anim/v2/{track}.compiled.json`，以及 JSON 中实际引用的音频/图片等媒体。QA 日志、`.pyc`、动画源、stage 源、文档、脚本、`.lrc` 均不进入 package，也不会影响 `version`。`version` 只由 package 内 `path + sha256` 决定。生成物不入 Git。

发布采用 `{version}.tmp` 暂存、逐文件 size/sha256 校验、原子 rename，然后 manifest 也通过 `.tmp + atomic rename` 切换。已存在的同 version release 字节不一致时明确失败，永不覆盖；服务端保留当前 + 最近 2 个历史 release。

### 后端接口

```text
GET /api/content/games/{game}/manifest
GET /api/content/games/{game}/files/{version}/{**filePath}
GET /api/content/games/{game}/files/{**filePath}   # 无版本兼容 URL
```

后端 manifest 仍从 `content/manifests/{game}.json` 实时读取，不经过 Qdrant；versioned URL 从 `content/releases/{game}/{version}/` 读取，缺文件直接 404，不回退到可修改的 `content/games`。成功响应返回 `Cache-Control: public, max-age=31536000, immutable` + 基于 `Length + LastWriteTimeUtc.Ticks` 的 `ETag`，支持 `If-None-Match` 304；version 过期或不是当前 manifest 时返回 `409 Conflict`。无版本 URL 才走 `content/games` 兼容路径，返回 `no-cache, must-revalidate`。

catalog 额外返回 `rules_ready` / `tutorial_ready` / `tutorial_tracks`。Android 首页据此处理：`tutorial_ready=false` 且 `rules_ready=true` 的游戏显示“仅规则问答”，点击直接打开问答面板，不显示下载/更新/教程播放入口；`tutorial_track` 只保留为旧缓存兼容字段。

### Android 本地仓库

`context.filesDir/board-content/`（App 内部私有目录）：

```text
board-content/
  active.json
  versions/
    {game}/
      {version}/
        complete.json
        {game}/
          tutorial/
          media/
          concepts.json
          ...
      {version}.partial/
        progress.json
        {game}/
          ...
          media/marker/xxx.jpg.part
```

- `complete.json` 记录版本、game 和已校验文件；
- 更新先写 `versions/{game}/{version}.partial`，全部文件 SHA-256 校验通过并写 `complete.json` 后，原子 rename 为 `versions/{game}/{version}`；
- `active.json` 先写 `active.json.tmp`，再原子 rename；失败时保留旧 active，不删除旧版本；
- 下载循环开始前一次性建立旧版本复用索引（`path -> sha256 -> File`）；每个旧版本只读取、解析一次 `complete.json`，循环内只查索引，网络只下载新增/变化文件；
- 下载使用同目录 `*.part` + HTTP Range 续传；`progress.json` 只用于 UI 显示“已暂停 xx%”，实际断点以 `.part` 文件长度为准；
- 下载开始时若服务端 manifest 版本与 partial 目录版本不一致，会清理当前 game 的 stale partial，不影响其他游戏；
- 默认保留当前版本和上一版本；旧的外部 `getExternalFilesDir(null)/board-content` 在 `ContentStore` 初始化时直接删除，不做迁移；
- 新布局按游戏隔离，更新 A 的清理不会候选到 B 的版本或 partial；旧扁平布局 `versions/{version}/...` 仍可读取和清理，不会要求用户清数据重装。

### 配置服务端地址与 adb reverse

本机 adb 路径统一记录在 `config/local.properties`：

- Windows 键：`ADB_WIN`
- WSL 键：`ADB_WSL`

下文 `%ADB%` 均指 `%ADB_WIN%`；WSL 中使用 `"$ADB_WSL"`。

`local.properties`：

```properties
board.api.baseUrl=http://127.0.0.1:5000
```

开发机执行：

```bat
%ADB% reverse tcp:5000 tcp:5000
```

App 访问 `http://127.0.0.1:5000` 即转发到 PC `5000` 端口。使用真机内网时，把 `board.api.baseUrl` 改成 PC 的局域网 IP，并放行防火墙。

### 资源管理与下载 UI

播放页只保留播放控制，资源状态和更新入口统一放在首页：

- 首页顶部“资源管理”：
  - 每款游戏显示本地版本、服务端版本、状态和大小；
  - 操作按钮按状态动态显示：`下载` / `继续下载 xx%` / `更新` / `继续更新 xx%` / `删除本地资源`；
  - `检查更新` 只刷新 catalog 和状态，不自动下载；
- 游戏卡右下角显示：`未下载` / `已暂停 xx%` / `更新暂停 xx%` / `已是最新` / `可更新到 vXXXX` / `暂无资源` / `已安装`；
- 首次点击游戏按状态弹窗；下载/更新显示全屏进度层（阶段、当前文件、字节与百分比、暂停按钮）；
- 下载中按返回键或“暂停并返回”：
  - `DownloadControl.requestPause()`；
  - 保存 `.part` 和 `progress.json`；
  - 回首页显示“已暂停 xx%”；
  - 下次点击可继续，不删除 partial；
- 下载成功后才进入教程；失败或暂停不修改旧 `active.json`；
- 正式流程使用 `LoadGameWithRoot`：

```text
LoadGameWithRoot("{gameId}|{versionRoot}")
```

其中 `versionRoot = context.filesDir/board-content/versions/{game}/{version}`；旧扁平布局设备会回退到 `context.filesDir/board-content/versions/{version}`。Unity 直接读取内部私有目录，不依赖 `Application.persistentDataPath`。

### 手动内容 fallback

旧的手动 push 仍可用：

```text
Application.persistentDataPath/splendor
```

Android 上：

```text
/sdcard/Android/data/com.boardai.tutorial.uaal/files/splendor
```

它位于 Unity active pointer 之后，因此没有新 active 版本时仍能播放。

## 安装与验收

安装：

```bat
%ADB% install -r %BOARD_WORKSPACE_WIN%\clients\android\app\build\outputs\apk\debug\app-debug.apk
```

如果 MIUI 报 `INSTALL_FAILED_USER_RESTRICTED`：

```bat
%ADB% push %BOARD_WORKSPACE_WIN%\clients\android\app\build\outputs\apk\debug\app-debug.apk /sdcard/Download/BoardAI-uaal.apk
```

然后手机手动安装。

启动并抓日志：

```bat
%ADB% logcat -c
%ADB% shell monkey -p com.boardai.tutorial.uaal -c android.intent.category.LAUNCHER 1
ping -n 20 127.0.0.1 >nul
%ADB% logcat -d -v time > %BOARD_WORKSPACE_WIN%\clients\unity\Builds\Android\native-shell-logcat.txt
%ADB% logcat -d -s Unity:V > %BOARD_WORKSPACE_WIN%\clients\unity\Builds\Android\native-shell-unity-logcat.txt
```

已在本机 Redmi K30 Pro / Android 10 / arm64-v8a 上验证：

- App 启动不崩溃；
- Unity 动画与音频正常；
- Compose 控制层显示，状态回传为“播放中 / cue N / 音量 100%”；
- 暂停、继续生效；
- -15 秒 / +15 秒会改变 cue；
- 音量 - / + 生效并回传百分比；
- 屏幕常亮；
- 字幕位于底部，控制层位于顶部，不永久遮挡；
- 只显示一套原生 Compose 触控条，没有 Unity 第二套触控条；
- logcat 无 FATAL EXCEPTION / JNI 崩溃 / 内容缺失；
- Unity 日志中仍可能看到可选 Play Asset Delivery 模块探测产生的
  `ClassNotFoundException: com.google.android.play.core.assetpacks.AssetPackManager`，
  这是 Unity 的可选集成探测，不影响本阶段播放功能。

## 不提交

- `config/local.properties`（仓库根目录的本机绝对路径配置）
- `clients/android/build/`
- `clients/android/app/build/`
- `clients/android/.gradle/`
- `clients/android/.kotlin/`
- `clients/android/local.properties`
- `clients/unity/Builds/`
- APK / logcat / 截图
- 生成的 `unityLibrary`

## 下一步建议

- 完成真机端到端验收：PTT → ASR → `/api/chat` → 回答 TTS，以及问答结束后回到动画 / 重播的完整链路；
- 继续完善内容版本回滚和更积极的旧版本清理；
- 将 Unity 状态回传节流策略改为事件驱动，降低每 0.5 秒的 JNI 调用频率；
- 在较新 Android 版本上单独恢复 Material ripple 并做对比验证。
