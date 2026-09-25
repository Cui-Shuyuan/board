# UaaL Android 正式客户端骨架

原生 Android 壳 + Unity 导出的 `unityLibrary`，用于验证 Unity as a Library（UaaL）的正式客户端结构。

本阶段把 POC 的 Java + `Button` 控制层替换为：

- Kotlin `MainActivity`；
- Jetpack Compose 控制层；
- Unity 视图仍作为底层渲染区域；
- Compose 原生控制层只占顶部安全区，不永久遮挡 Unity 字幕；
- 通过固定 GameObject `AndroidTutorialBridge` 的字符串协议控制播放。

本阶段不接后端、不做内容更新、不做 ASR/TTS/问答、不拆仓、不改游戏 JSON / animation / compiled 语义。

## 目录

```text
clients/android/
  settings.gradle       include :app + 外部 :unityLibrary
  build.gradle          Android / Kotlin / Compose 插件版本
  gradle.properties     AndroidX、Kotlin、Compose 通用配置
  gradlew / gradlew.bat Gradle 入口；路径不写死在 wrapper 中
  build-uaal.bat        唯一的机器路径配置块 + 固定构建顺序
  app/
    build.gradle
    src/main/AndroidManifest.xml
    src/main/java/com/boardai/tutorial/uaal/MainActivity.kt
    src/main/java/com/boardai/tutorial/uaal/UnityBridgeCallback.kt
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

`local.properties` 由 `build-uaal.bat` 在缺失时根据 `ANDROID_HOME` 生成，不入 Git。手工维护时：

```properties
sdk.dir=D:/Unity/Hub/Editor/6000.5.8f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK
```

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
- 避免 Material ripple：Unity Activity 的 `android:hardwareAccelerated=false` 在 Android 10 上会让 `RippleDrawable` 在软件绘制路径中崩溃，因此控制按钮使用无 ripple 的 Compose `clickable(indication = null)`。

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
| `SetContentRoot` | path | 预留；只保存 `tutorialRoot`，本次不改变默认加载路径 |
| `SetUnityTouchControlsEnabled` | `"true"` / `"false"` | 启用/禁用旧 Unity IMGUI 触控层 |

`TutorialCuePlayer` 新增：

```csharp
public void Pause()
public void Resume()
public void SetVolume(float value)
public void SetUnityTouchControlsEnabled(bool enabled)
public float Position
public float Duration
public bool UnityTouchControlsEnabled
```

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
isPlaying, isPaused, volume, cueId, cueText, cueIndex, cueTotal,
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

## 默认内容加载

本阶段保持 POC 默认路径不变：

```text
Application.persistentDataPath/splendor
```

Android 上对应：

```text
/sdcard/Android/data/com.boardai.tutorial.uaal/files/splendor
```

推送：

```bat
set ADB=D:\Unity\Hub\Editor\6000.5.8f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe
%ADB% shell mkdir -p /sdcard/Android/data/com.boardai.tutorial.uaal/files
%ADB% push D:\workspace\board\content\games\splendor /sdcard/Android/data/com.boardai.tutorial.uaal/files/
```

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
- 用 `SetContentRoot` 接入下一阶段的内容 manifest / 增量下载；
- 将 Unity 状态回传节流策略改为事件驱动，降低每 0.5 秒的 JNI 调用频率；
- 后续如果移除 `hardwareAccelerated=false`，可以恢复 Material ripple。
