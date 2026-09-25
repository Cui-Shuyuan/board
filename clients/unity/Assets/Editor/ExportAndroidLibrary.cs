// Unity as a Library (UaaL) export entry point for the native Android POC.
//
// Batch mode:
//   Unity.exe -batchmode -quit -projectPath <project> ^
//     -executeMethod ExportAndroidLibrary.Export ^
//     -logFile <project>\Builds\AndroidLibrary\export.log
//
// Output:
//   <project>\Builds\AndroidLibrary\unityLibrary\
//   <project>\Builds\AndroidLibrary\launcher\
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class ExportAndroidLibrary
{
    private const string ApplicationIdentifier = "com.boardai.tutorial.uaal";
    private const string OutputFolderName = "AndroidLibrary";
    private const string SummaryFileName = "export-summary.log";

    public static void Export()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string outputDirectory = Path.Combine(projectRoot, "Builds", OutputFolderName);
        string summaryPath = Path.Combine(outputDirectory, SummaryFileName);
        Directory.CreateDirectory(outputDirectory);

        string originalApplicationIdentifier = PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android);
        ScriptingImplementation originalScriptingBackend =
            PlayerSettings.GetScriptingBackend(BuildTargetGroup.Android);
        AndroidArchitecture originalTargetArchitectures = PlayerSettings.Android.targetArchitectures;
        bool originalExportAsGoogleAndroidProject = EditorUserBuildSettings.exportAsGoogleAndroidProject;
        bool originalBuildAppBundle = EditorUserBuildSettings.buildAppBundle;

        BuildReport report = null;
        string error = null;
        string startMessage = null;

        try
        {
            string[] scenes = CollectEnabledScenes();
            if (scenes.Length == 0)
            {
                error = "EditorBuildSettings has no enabled scenes.";
            }
            else if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android))
            {
                error = "Failed to switch active build target to Android. Android Build Support may be missing.";
            }
            else
            {
                // Keep the POC deterministic: IL2CPP + ARM64 matches the Redmi K30 Pro.
                EditorUserBuildSettings.buildAppBundle = false;
                EditorUserBuildSettings.development = true;
                EditorUserBuildSettings.allowDebugging = true;
                EditorUserBuildSettings.exportAsGoogleAndroidProject = true;

                PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, ApplicationIdentifier);
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
                PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

                var options = new BuildPlayerOptions
                {
                    scenes = scenes,
                    // With exportAsGoogleAndroidProject=true this is an output directory,
                    // not an .apk path.
                    locationPathName = outputDirectory,
                    target = BuildTarget.Android,
                    options = BuildOptions.Development | BuildOptions.AllowDebugging,
                };

                startMessage =
                    "[ExportAndroidLibrary] export started\n" +
                    "  target: " + BuildTarget.Android + "\n" +
                    "  applicationIdentifier: " + ApplicationIdentifier + "\n" +
                    "  scriptingBackend: IL2CPP\n" +
                    "  targetArchitectures: ARM64\n" +
                    "  scenes: " + string.Join(", ", scenes) + "\n" +
                    "  output: " + outputDirectory + "\n";
                Debug.Log(startMessage);

                report = BuildPipeline.BuildPlayer(options);
            }
        }
        catch (Exception ex)
        {
            error = ex.ToString();
        }
        finally
        {
            RestoreSettings(
                originalApplicationIdentifier,
                originalScriptingBackend,
                originalTargetArchitectures,
                originalExportAsGoogleAndroidProject,
                originalBuildAppBundle);
        }

        if (report != null && report.summary.result == BuildResult.Succeeded)
        {
            string successMessage =
                (startMessage ?? "[ExportAndroidLibrary] export started\n") +
                "[ExportAndroidLibrary] export succeeded\n" +
                "  unityLibrary: " + Path.Combine(outputDirectory, "unityLibrary") + "\n" +
                "  launcher: " + Path.Combine(outputDirectory, "launcher") + "\n" +
                "  totalSize: " + report.summary.totalSize + "\n";
            WriteSummary(summaryPath, successMessage);
            Debug.Log(successMessage);
            EditorApplication.Exit(0);
            return;
        }

        string failureMessage =
            (startMessage ?? "[ExportAndroidLibrary] export started\n") +
            "[ExportAndroidLibrary] export failed\n" +
            "  result: " + (report == null ? "no BuildReport" : report.summary.result.ToString()) + "\n" +
            "  error: " + (error ?? "unknown") + "\n";
        WriteSummary(summaryPath, failureMessage);
        Debug.LogError(failureMessage);
        EditorApplication.Exit(1);
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

    private static void RestoreSettings(
        string applicationIdentifier,
        ScriptingImplementation scriptingBackend,
        AndroidArchitecture targetArchitectures,
        bool exportAsGoogleAndroidProject,
        bool buildAppBundle)
    {
        try
        {
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, applicationIdentifier);
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, scriptingBackend);
            PlayerSettings.Android.targetArchitectures = targetArchitectures;
            EditorUserBuildSettings.exportAsGoogleAndroidProject = exportAsGoogleAndroidProject;
            EditorUserBuildSettings.buildAppBundle = buildAppBundle;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[ExportAndroidLibrary] failed to restore editor settings: " + ex.Message);
        }
    }

    private static void WriteSummary(string summaryPath, string text)
    {
        try
        {
            File.WriteAllText(summaryPath, text);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[ExportAndroidLibrary] could not write summary: " + ex.Message);
        }
    }
}
