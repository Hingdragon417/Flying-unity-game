using System;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

public static class BuildDedicatedServer
{
    private const string ServerScene = "Assets/Scenes/ServerScene.unity";
    private const string DefaultOutputPath = "glideServer.x86_64";

    public static void PerformBuild()
    {
        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneLinux64);
        EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Server;
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneLinux64, new[] { GraphicsDeviceType.Vulkan });

        BuildPlayerOptions options = new()
        {
            scenes = new[] { ServerScene },
            locationPathName = GetCommandLineValue("-serverOutput", DefaultOutputPath),
            target = BuildTarget.StandaloneLinux64,
            subtarget = (int)StandaloneBuildSubtarget.Server,
            options = BuildOptions.EnableHeadlessMode
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);

        if (report.summary.result != BuildResult.Succeeded)
        {
            throw new Exception($"Dedicated server build failed: {report.summary.result}");
        }

        Debug.Log($"Dedicated server build completed: {options.locationPathName}");
    }

    private static string GetCommandLineValue(string argumentName, string fallbackValue)
    {
        string[] args = Environment.GetCommandLineArgs();

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == argumentName && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                return args[i + 1];
            }
        }

        return fallbackValue;
    }
}
