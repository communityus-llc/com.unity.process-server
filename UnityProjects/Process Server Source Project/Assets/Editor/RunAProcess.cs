﻿using System.Threading;
using UnityEngine;
using UnityEditor;
using Unity.Editor.ProcessServer;
using Unity.Editor.Tasks;
using System.IO;
using Unity.Editor.Tasks.Extensions;

public class RunAProcess : MonoBehaviour
{
    [MenuItem("Process Server/Stop process server")]
    public static void Menu_Stop()
    {
        ProcessServer.Stop();
    }

    [MenuItem("Process Server/Run Process")]
    public static void Menu_RunProcess()
    {
        var processServer = ProcessServer.Get();

        Debug.Log("Running out-of-process");
        var testApp = Path.GetFullPath("Packages/com.unity.process-server.tests/Helpers~/Helper.CommandLine.exe");

        var expectedResult = "result!";

        var process = new DotNetProcessTask(processServer.TaskManager, processServer.ProcessManager,
                testApp, "-d " + expectedResult.InQuotes());

        process.OnOutput += s => Debug.Log(s);

        process.Finally((success, ex, ret) => {
            Debug.Log(ex);
            Debug.Log("Done out-of-process");
            Debug.Log(ret);

            processServer.ShutdownSync();
        }).Start();

    }
}
