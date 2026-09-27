# UaaL Android 正式客户端骨架

原生 Android 壳 + Unity 导出的 `unityLibrary`，用于验证 Unity as a Library（UaaL）的正式客户端结构。

本阶段把 POC 的 Java + `Button` 控制层替换为：

- Kotlin `MainActivity`；
- Jetpack Compose 控制层；
- Unity 视图仍作为底层渲染区域；
- Compose 原生控制层只占顶部安全区，不永久遮挡 Unity 字幕；
- 通过固定 GameObject `AndroidTutorialBridge` 的字符串协议控制播放。

本阶段已接入内容 manifest / 本地内容仓库 / 增量下载 v1；仍不做 ASR/TTS/问答、不拆仓、不改游戏 JSON / animation / compiled 语义。

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
    src/main/java/com/boardai/tutorial/uaal/content/
      ContentManifest.kt                  manifest JSON 模型
      ContentStore.kt                     本地版本仓库 / active.json
      ContentUpdater.kt                   manifest 拉取、SHA-256、增量下载
      ContentUpdateState.kt               Compose 更新状态
```

Unity 侧：

```text
clients/unity/Assets/Scripts/Tutorial/AndroidTutorialBridge.cs
clients/unity/Assets/Scripts/Tutorial/TutorialCuePlayer.cs
clients/unity/Assets/Scripts/Tutorial/TutorialTouchControls.cs
```

## 构建环境：集中配置

机器相关路径全部集中到 `build-uaal.bat` 顶部配置块，`gradlew.bat` 不再写死用户路径。

默认值：

| 变量 | 默认值 |
| --- | --- |
| `UNITY_ROOT` | `D:\Unity\Hub\Editor\6000.5.8f1` |
| `UNITY_EXE` | `%UNITY_ROOT%\Editor\Unity.exe` |
| `JAVA_HOME` | `%UNITY_ROOT%\Editor\Data\PlaybackEngines\AndroidPlayer\OpenJDK` |
| `ANDROID_HOME` | `%UNITY_ROOT%\Editor\Data\PlaybackEngines\AndroidPlayer\SDK` |
| `UNITY_GRADLE_HOME` | `%UNITY_ROOT%\Editor\Data\PlaybackEngines\AndroidPlayer\Tools\gradle` |
| `GRADLE_USER_HOME` | `%~d0\gradle-home-boardai` |

可在调用前覆盖任意变量，例如：

```bat
set UNITY_EXE=D:\MyUnity\Editor\Unity.exe
set JAVA_HOME=C:\Java\jdk-17
set ANDROID_HOME=D:\Android\Sdk
set UNITY_GRADLE_HOME=D:\MyUnity\...\Tools\gradle
set GRADLE_USER_HOME=D:\gradle-home-boardai
```

`gradlew.bat` 行为：

1. 如果设置了 `UNITY_GRADLE_HOME` 且其中存在 Unity 自带 Gradle，优先复用；
2. 否则回退到标准 Gradle Wrapper；
3. `JAVA_HOME` 未设置时直接使用 `PATH` 中的 `java`。

`local.properties` 由 `build-uaal.bat` 在缺失时根据 `ANDROID_HOME` 生成，不入 Git。可复制 `local.properties.example` 后手工维护：

```properties
sdk.dir=D:/Unity/Hub/Editor/6000.5.8f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK
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
cd D:\workspace\board\clients\android
build-uaal.bat
```

APK：

```text
clients\android\app\build\outputs\apk\debug\app-debug.apk
```

如果只想在 Unity 已导出后重新构建原生壳：

```bat
cd D:\workspace\board\clients\android
set JAVA_HOME=D:\Unity\Hub\Editor\6000.5.8f1\Editor\Data\PlaybackEngines\AndroidPlayer\OpenJDK
set UNITY_GRADLE_HOME=D:\Unity\Hub\Editor\6000.5.8f1\Editor\Data\PlaybackEngines\AndroidPlayer\Tools\gradle
gradlew.bat assembleDebug
```

Unity 脚本检查：

```bash
python3 tools/ops/check_unity_scripts.py
```

### 旧 Unity 独立 APK 构建入口

旧入口仍在：

```text
clients/unity/Assets/Editor/AndroidDebugBuild.cs
```

产物为旧包名 `com.boardai.tutorial` 的：

```text
clients/unity/Builds/Android/BoardAI.apk
```

Windows 中文用户名下，Unity 内部 Gradle 的 prefab 步骤会因为 `C:\Users\<中文名>\.gradle` 编码问题失败，所以直接构建旧 APK 时也必须设置 ASCII 的 `GRADLE_USER_HOME`：

```bat
set GRADLE_USER_HOME=D:\gradle-home-boardai
set JAVA_HOME=D:\Unity\Hub\Editor\6000.5.8f1\Editor\Data\PlaybackEngines\AndroidPlayer\OpenJDK
set ANDROID_HOME=D:\Unity\Hub\Editor\6000.5.8f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK
set ANDROID_SDK_ROOT=%ANDROID_HOME%

D:\Unity\Hub\Editor\6000.5.8f1\Editor\Unity.exe ^
  -batchmode -quit ^
  -projectPath D:\workspace\board\clients\unity ^
  -executeMethod AndroidDebugBuild.BuildApk ^
  -logFile D:\workspace\board\clients\unity\Builds\Android\build.log
```

该入口与 UaaL 正式客户端互不影响，不会覆盖 `com.boardai.tutorial.uaal`。

## 原生控制层

`MainActivity` 直接继承 `com.unity3d.player.UnityPlayerGameActivity`。`UnityPlayerGameActivity` 已基于 AndroidX `ComponentActivity`，因此可以承载标准 `ComposeView` 生命周期。

控制层实现：

- `ComposeView` 通过 `android.R.id.content.addView` 叠加在 Unity SurfaceView 上方；
- ComposeView 高度为 `WRAP_CONTENT`，位于顶部安全区；
- 背景半透明黑色，底角圆角；
- 大按钮 56dp 高，横向均分；
- 按钮：暂停/继续、-15 秒、+15 秒、音量 -、音量 +；
- 状态：播放中/已暂停、cue `当前/总数 · id`、音量百分比；
- 本轮已移除 `android:hardwareAccelerated=false`，让 Unity Surface 和 Compose 都走硬件加速；控制按钮暂时仍保留无 ripple 的 Compose 自定义实现，Material ripple 留作后续单独验证。

Unity ready 握手：

- `AndroidTutorialBridge` 在 `Start()` 后通过状态 JSON 上报 `unityReady`；
- `MainActivity` 不再使用固定 `postDelayed` 发送 Unity 命令，而是观察 `UnityStatusHolder.status`；
- 收到 `unityReady=true` 后只执行一次 `SetUnityTouchControlsEnabled("false")` 和 `checkContentUpdate()`；
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
| `CheckContentUpdate` | `""` | 协议占位；更新由 Compose 原生层触发 |
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
2. `Application.persistentDataPath/board-content/active.json` 中当前 game 的 `root`（旧外部路径兼容读取，Android 正式流程不再依赖）；
3. 旧 fallback：`Application.persistentDataPath/{gameId}`（手动 adb push）；
4. StreamingAssets / 仓库 `content/games/{gameId}`。

## Debug APK 调试模式（当前 cue + zone 范围）

只有 Debug APK 会在播放控制条显示“调试 开/关”按钮：

- 打开后，Unity 左上角显示当前 cue 的序号/id、章节路径、文本和播放时间，同时绘制当前 stage 中所有 on-stage zone 的彩色范围框与名称；
- 关闭后两者消失；
- 开关状态保存在原生 Compose 状态和 Unity `TutorialCuePlayer` 请求状态中，换游戏 / 内容 reload 后保留。

Release APK 没有调试入口，并且原生层会显式关闭 Unity 调试开关：`MainActivity.onUnityReady()` 在 `playerSession.onUnityReady()` 之后发送 `SetDebugBuild("0")`。Unity 侧 `TutorialAnimPlayer` 的键盘 Z 切换受 `debugToggleEnabled` 门禁，release 下无法通过键盘打开。

相关代码位置：

- `MainActivity.kt`: `debugOverlayEnabled`、`toggleDebugOverlay()`、`onUnityReady()`
- `TutorialPlayerOverlay.kt` / `PlayerTransportBar.kt`: `debugOverlayEnabled` 参数和调试按钮
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

Android 构建默认不再挂载 `TutorialTouchControls`：

```csharp
#if UNITY_ANDROID && !UNITY_EDITOR
    SetUnityTouchControlsEnabled(false);
#else
    SetUnityTouchControlsEnabled(true);
#endif
```

作用：

- 正式 UaaL 客户端只显示一套 Compose 控件；
- Editor / Desktop 仍保留旧 Unity 控制层，方便调试；
- 原生层启动后也会显式发送 `SetUnityTouchControlsEnabled("false")`；
- 旧 Unity 独立 APK 构建入口仍保留在 `Assets/Editor/AndroidDebugBuild.cs`，使用旧包名 `com.boardai.tutorial`，不会覆盖 `com.boardai.tutorial.uaal`。

## 内容更新 v1

### 生成 manifest

PC 侧：

```bash
cd D:\workspace\board
python3 tools/content/build_content_manifest.py --game splendor
```

输出：

```text
content/manifests/splendor.json
```

manifest 记录每个可运行文件的 `path / size / sha256 / url`；`version` 只由内容决定，文件不变则版本不变。生成物不入 Git。

### 后端接口

```text
GET /api/content/games/{game}/manifest
GET /api/content/games/{game}/files/{version}/{**filePath}
GET /api/content/games/{game}/files/{**filePath}   # 兼容旧 URL
```

后端从 `content/manifests/{game}.json` 和 `content/games/{game}/...` 实时读取，不经过 Qdrant，manifest 文件变化无需重启 API。manifest 里的文件 URL 已升级为 versioned URL，成功响应返回 `Cache-Control: public, max-age=31536000, immutable` + 基于 `Length + LastWriteTimeUtc.Ticks` 的 `ETag`，支持 `If-None-Match` 304；version 过期返回 `409 Conflict`，客户端会重新拉 manifest。旧无版本 URL 继续保留，返回 `no-cache, must-revalidate`，未来可删除。

### Android 本地仓库

`context.filesDir/board-content/`（App 内部私有目录）：

```text
board-content/
  active.json
  versions/
    {version}/
      complete.json
      splendor/
        tutorial/
        media/
        concepts.json
        ...
    {version}.partial/
      progress.json
      splendor/
        ...
        media/marker/xxx.jpg.part
```

- `complete.json` 记录版本、game 和已校验文件；
- 更新先写 `versions/{version}.partial`，全部文件 SHA-256 校验通过并写 `complete.json` 后，原子 rename 为 `versions/{version}`；
- `active.json` 先写 `active.json.tmp`，再原子 rename；失败时保留旧 active，不删除旧版本；
- 下载循环开始前一次性建立旧版本复用索引（`path -> sha256 -> File`）；每个旧版本只读取、解析一次 `complete.json`，循环内只查索引，网络只下载新增/变化文件；
- 下载使用同目录 `*.part` + HTTP Range 续传；`progress.json` 只用于 UI 显示“已暂停 xx%”，实际断点以 `.part` 文件长度为准；
- 下载开始时若服务端 manifest 版本与 partial 目录版本不一致，会清理 stale partial；
- 默认保留当前版本和上一版本；旧的外部 `getExternalFilesDir(null)/board-content` 在 `ContentStore` 初始化时直接删除，不做迁移。

### 配置服务端地址与 adb reverse

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

播放页不再显示“内容”按钮，所有资源状态和更新入口统一在首页：

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

其中 `versionRoot = context.filesDir/board-content/versions/{version}`，Unity 直接读取内部私有目录，不依赖 `Application.persistentDataPath`。

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
%ADB% install -r D:\workspace\board\clients\android\app\build\outputs\apk\debug\app-debug.apk
```

如果 MIUI 报 `INSTALL_FAILED_USER_RESTRICTED`：

```bat
%ADB% push D:\workspace\board\clients\android\app\build\outputs\apk\debug\app-debug.apk /sdcard/Download/BoardAI-uaal.apk
```

然后手机手动安装。

启动并抓日志：

```bat
%ADB% logcat -c
%ADB% shell monkey -p com.boardai.tutorial.uaal -c android.intent.category.LAUNCHER 1
ping -n 20 127.0.0.1 >nul
%ADB% logcat -d -v time > D:\workspace\board\clients\unity\Builds\Android\native-shell-logcat.txt
%ADB% logcat -d -s Unity:V > D:\workspace\board\clients\unity\Builds\Android\native-shell-unity-logcat.txt
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
- 不再出现 Unity 第二套触控条；
- logcat 无 FATAL EXCEPTION / JNI 崩溃 / 内容缺失；
- Unity 日志中仍可能看到可选 Play Asset Delivery 模块探测产生的
  `ClassNotFoundException: com.google.android.play.core.assetpacks.AssetPackManager`，
  这是 Unity 的可选集成探测，不影响本阶段播放功能。

## 不提交

- `clients/android/build/`
- `clients/android/app/build/`
- `clients/android/.gradle/`
- `clients/android/.kotlin/`
- `clients/android/local.properties`
- `clients/unity/Builds/`
- APK / logcat / 截图
- 生成的 `unityLibrary`

## 下一步建议

- 把 `UnityBridgeCallback` 的状态字段扩展到进度条和章节树；
- 后续可做内容版本回滚和更积极的旧版本清理；
- 将 Unity 状态回传节流策略改为事件驱动，降低每 0.5 秒的 JNI 调用频率；
- 后续可在较新 Android 版本上单独恢复 Material ripple 并做对比验证。
