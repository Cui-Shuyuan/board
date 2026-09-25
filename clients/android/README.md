# UaaL 最小验证工程

原生 Android 壳 + Unity 导出的 `unityLibrary`，用于验证 Unity as a Library（UaaL）。

## 目标与边界

已验证：

- Unity 工程通过 `Assets/Editor/ExportAndroidLibrary.cs` 导出 Android Library；
- 原生 `app` 模块嵌入 Unity 渲染视图；
- 原生 `Button` 叠加在 Unity SurfaceView 之上；
- 按钮通过固定 GameObject bridge 调用 Unity 的 `TutorialCuePlayer.TogglePause()`；
- Splendor 数据从新包名的 `persistentDataPath` 加载。

边界：

- 本目录只依赖 `clients/unity/Builds/AndroidLibrary/unityLibrary` 生成物，不复制 Unity C# 源码；
- Unity 工程不复制本目录的 Java/Android 源码；
- 交互只通过 `UnityPlayer.UnitySendMessage("AndroidTutorialBridge", "TogglePause", "")` 和固定常量 GameObject 名称完成。

## 目录

```text
clients/android/
  settings.gradle       include :app + 外部 :unityLibrary
  build.gradle          加载 Unity 生成的 gradle.properties，设置 AGP 版本
  gradle.properties     通用 Gradle 配置
  gradlew / gradlew.bat Gradle 入口；优先复用 Unity 自带 Gradle
  build-uaal.bat        固定构建顺序：导出 Unity -> 校验 -> assembleDebug -> 输出 APK
  app/
    build.gradle
    src/main/AndroidManifest.xml
    src/main/java/com/boardai/tutorial/uaal/MainActivity.java
```

## 构建

Windows 环境默认使用：

- Unity：`D:\Unity\Hub\Editor\6000.5.8f1\Editor\Unity.exe`
- SDK：`D:\Unity\Hub\Editor\6000.5.8f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK`
- JDK：`D:\Unity\Hub\Editor\6000.5.8f1\Editor\Data\PlaybackEngines\AndroidPlayer\OpenJDK`

一条命令构建：

```bat
cd D:\workspace\board\clients\android
build-uaal.bat
```

也可以分步执行：

```bat
cd D:\workspace\board\clients\android
set JAVA_HOME=D:\Unity\Hub\Editor\6000.5.8f1\Editor\Data\PlaybackEngines\AndroidPlayer\OpenJDK
gradlew.bat assembleDebug
```

`gradlew.bat` 会优先使用 Unity 自带的 Gradle 发行版，并把 Gradle User Home 默认放到 `D:\gradle-home-boardai`。这是为了避开中文用户名路径在 Windows cmd 下的编码问题，以及 Windows 260 字符路径限制。也可通过环境变量覆盖：

```bat
set UNITY_GRADLE_HOME=...
set GRADLE_USER_HOME=D:\some\short\ascii\path
set JAVA_HOME=...
gradlew.bat assembleDebug
```

`local.properties` 是本地文件，不入 Git。没有时 `build-uaal.bat` 会按默认 SDK 路径生成；也可手工写：

```properties
sdk.dir=D:/Unity/Hub/Editor/6000.5.8f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK
```

APK 输出：

```text
clients/android/app/build/outputs/apk/debug/app-debug.apk
```

## Unity 导出

`clients/unity/Assets/Editor/ExportAndroidLibrary.cs` 使用 `BuildPipeline.BuildPlayer` 和
`EditorUserBuildSettings.exportAsGoogleAndroidProject = true` 导出到：

```text
clients/unity/Builds/AndroidLibrary/unityLibrary
clients/unity/Builds/AndroidLibrary/launcher
```

POC 只使用 `unityLibrary`。构建前可单独执行：

```bat
D:\Unity\Hub\Editor\6000.5.8f1\Editor\Unity.exe ^
  -batchmode -quit ^
  -projectPath "D:\workspace\board\clients\unity" ^
  -executeMethod ExportAndroidLibrary.Export ^
  -logFile "D:\workspace\board\clients\unity\Builds\AndroidLibrary\export.log"
```

## 原生层与 bridge

`MainActivity` 继承 Unity 6 导出的 `com.unity3d.player.UnityPlayerGameActivity`（不是旧版的
`UnityPlayerActivity`），在 `super.onCreate` 后把 Android 原生 `Button` 加到
`android.R.id.content` 上。按钮点击执行：

```java
UnityPlayer.UnitySendMessage("AndroidTutorialBridge", "TogglePause", "");
```

对应的 Unity bridge 在：

```text
clients/unity/Assets/Scripts/Tutorial/AndroidTutorialBridge.cs
```

固定 GameObject 名称常量：

```csharp
public const string GameObjectName = "AndroidTutorialBridge";
```

它转发到现有的 `TutorialCuePlayer.TogglePause()`。

## Splendor 数据

新包名的 `persistentDataPath`：

```text
/sdcard/Android/data/com.boardai.tutorial.uaal/files/splendor
```

推送：

```bat
set ADB=D:\Unity\Hub\Editor\6000.5.8f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe
%ADB% shell mkdir -p /sdcard/Android/data/com.boardai.tutorial.uaal/files
%ADB% push D:\workspace\board\content\games\splendor /sdcard/Android/data/com.boardai.tutorial.uaal/files/
```

## 真机安装

```bat
%ADB% install -r D:\workspace\board\clients\android\app\build\outputs\apk\debug\app-debug.apk
```

如果 MIUI 报 `INSTALL_FAILED_USER_RESTRICTED`，改用：

```bat
%ADB% push D:\workspace\board\clients\android\app\build\outputs\apk\debug\app-debug.apk /sdcard/Download/BoardAI-uaal.apk
```

然后手机上手动安装。

## 不提交

- `build/`
- `.gradle/`
- `.gradle-user-home/`
- `local.properties`
- `clients/unity/Builds/`
- APK、logcat、`unityLibrary` 生成物
