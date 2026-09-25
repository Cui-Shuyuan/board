// Android debug APK builder for the tutorial verification round.
//
// Unity batchmode entry point:
//   Unity.exe -batchmode -quit -projectPath <project> \
//     -executeMethod AndroidDebugBuild.BuildApk \
//     -logFile <project>\Builds\Android\build.log
//
// Output:
//   <project>\Builds\Android\BoardAI.apk
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class AndroidDebugBuild
{
    private const string ApplicationIdentifier = "com.boardai.tutorial";
    private const string SummaryFileName = "build-summary.log";

    public static void BuildApk()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string outputDirectory = Path.Combine(projectRoot, "Builds", "Android");
        string apkPath = Path.Combine(outputDirectory, "BoardAI.apk");
        string summaryPath = Path.Combine(outputDirectory, SummaryFileName);

        Directory.CreateDirectory(outputDirectory);

        try
        {
            string[] scenes = CollectEnabledScenes();
            if (scenes.Length == 0)
            {
                Fail(summaryPath, "EditorBuildSettings has no enabled scenes.");
                return;
            }

            if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android))
            {
                Fail(summaryPath, "Failed to switch active build target to Android. Android Build Support may be missing.");
                return;
            }

            // Output path is BoardAI.apk, so do not let the editor switch to an AAB.
            EditorUserBuildSettings.buildAppBundle = false;
            EditorUserBuildSettings.development = true;
            EditorUserBuildSettings.allowDebugging = true;

            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, ApplicationIdentifier);
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = apkPath,
                target = BuildTarget.Android,
                options = BuildOptions.Development | BuildOptions.AllowDebugging,
            };

            string startMessage =
                "[AndroidDebugBuild] build started\n" +
                "  target: " + BuildTarget.Android + "\n" +
                "  applicationIdentifier: " + ApplicationIdentifier + "\n" +
                "  scriptingBackend: IL2CPP\n" +
                "  targetArchitectures: ARM64\n" +
                "  scenes: " + string.Join(", ", scenes) + "\n" +
                "  output: " + apkPath + "\n";
            File.WriteAllText(summaryPath, startMessage);
            Debug.Log(startMessage);

            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report != null && report.summary.result == BuildResult.Succeeded)
            {
                string successMessage =
                    startMessage +
                    "[AndroidDebugBuild] build succeeded\n" +
                    "  totalSize: " + report.summary.totalSize + "\n" +
                    "  apk: " + apkPath + "\n";
                File.WriteAllText(summaryPath, successMessage);
                Debug.Log(successMessage);
                EditorApplication.Exit(0);
            }
            else
            {
                string result = report == null ? "no BuildReport" : report.summary.result.ToString();
                Fail(summaryPath, startMessage + "[AndroidDebugBuild] build failed: " + result);
            }
        }
        catch (Exception ex)
        {
            Fail(summaryPath, "[AndroidDebugBuild] exception:\n" + ex);
        }
    }

    private static string[] CollectEnabledScenes()
    {
        var scenes = new List<string>();
        foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
        {
            if (scene != null && scene.enabled && !string.IsNullOrEmpty(scene.path))
                scenes.Add(scene.path);
        }
        return scenes.ToArray();
    }

    private static void Fail(string summaryPath, string message)
    {
        string text = message + "\n";
        try { File.WriteAllText(summaryPath, text); }
        catch (Exception logEx) { Debug.LogWarning("[AndroidDebugBuild] could not write summary: " + logEx.Message); }
        Debug.LogError(text);
        EditorApplication.Exit(1);
    }
}
