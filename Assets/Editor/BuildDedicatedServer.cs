using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class BuildDedicatedServer
{
    public static void PerformBuild()
    {
        BuildPlayerOptions options = new()
        {
            scenes = new[] { "Assets/Scenes/ServerScene.unity" },
            locationPathName = "glideServer.x86_64",
            target = BuildTarget.StandaloneLinux64,
            subtarget = (int)StandaloneBuildSubtarget.Server,
            options = BuildOptions.None
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            throw new BuildFailedException($"Dedicated server build failed: {report.summary.result}");
        }

        Debug.Log($"Dedicated server build completed: {options.locationPathName}");
    }
}
