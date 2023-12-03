﻿using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StableSwarmUI.Core;
using StableSwarmUI.Utils;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;

namespace StableSwarmUI.Backends;

/// <summary>General utility for backends that self-start or use network APIs.</summary>
public static class NetworkBackendUtils
{
    #region Network
    /// <summary>Create and preconfigure a basic <see cref="HttpClient"/> instance to make web requests with.</summary>
    public static HttpClient MakeHttpClient()
    {
        HttpClient client = new();
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"StableSwarmUI/{Utilities.Version}");
        client.Timeout = TimeSpan.FromMinutes(10);
        return client;
    }

    /// <summary>Parses an <see cref="HttpResponseMessage"/> into a JSON object result.</summary>
    /// <exception cref="InvalidOperationException">Thrown when the server returns invalid data (error code or other non-JSON).</exception>
    /// <exception cref="NotImplementedException">Thrown when an invalid JSON type is requested.</exception>
    public static async Task<JType> Parse<JType>(HttpResponseMessage message) where JType : class
    {
        string content = await message.Content.ReadAsStringAsync();
        if (content.StartsWith("500 Internal Server Error"))
        {
            throw new InvalidOperationException($"Server turned 500 Internal Server Error, something went wrong: {content}");
        }
        try
        {
            if (typeof(JType) == typeof(JObject)) // TODO: Surely C# has syntax for this?
            {
                return JObject.Parse(content) as JType;
            }
            else if (typeof(JType) == typeof(JArray))
            {
                return JArray.Parse(content) as JType;
            }
            else if (typeof(JType) == typeof(string))
            {
                return content as JType;
            }
        }
        catch (JsonReaderException ex)
        {
            throw new InvalidOperationException($"Failed to read JSON '{content}' with message: {ex.Message}");
        }
        throw new NotImplementedException();
    }

    /// <summary>Connects a client websocket to the backend.</summary>
    /// <param name="path">The path to connect on, after the '/', such as 'ws?clientId={uuid}'.</param>
    public static async Task<ClientWebSocket> ConnectWebsocket(string address, string path)
    {
        ClientWebSocket outSocket = new();
        outSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        string scheme = address.BeforeAndAfter("://", out string addr);
        scheme = scheme == "http" ? "ws" : "wss";
        await outSocket.ConnectAsync(new Uri($"{scheme}://{addr}/{path}"), Program.GlobalProgramCancel);
        return outSocket;
    }

    /// <summary>Helper for API-by-URL backends to allow those backends to go idle automatically if connection is lost.</summary>
    public class IdleMonitor
    {
        public Thread IdleMonitorThread;

        public CancellationTokenSource IdleMonitorCancel = new();

        public AbstractT2IBackend Backend;

        public Action ValidateCall;

        public Action<BackendStatus> StatusChangeEvent;

        public void Start()
        {
            Stop();
            if (Backend.Status == BackendStatus.LOADING)
            {
                Backend.Status = BackendStatus.IDLE;
            }
            IdleMonitorThread = new Thread(IdleMonitorLoop);
            IdleMonitorThread.Start();
        }

        void SetStatus(BackendStatus status)
        {
            if (Backend.Status != status)
            {
                Backend.Status = status;
                StatusChangeEvent?.Invoke(status);
            }
        }

        public void IdleMonitorLoop()
        {
            CancellationToken cancel = IdleMonitorCancel.Token;
            while (true)
            {
                try
                {
                    Task.Delay(TimeSpan.FromSeconds(5), cancel).Wait();
                }
                catch (Exception)
                {
                    return;
                }
                if (cancel.IsCancellationRequested || Program.GlobalProgramCancel.IsCancellationRequested)
                {
                    return;
                }
                if (Backend.Status != BackendStatus.RUNNING && Backend.Status != BackendStatus.IDLE)
                {
                    continue;
                }
                try
                {
                    ValidateCall();
                    if (Backend.Status != BackendStatus.RUNNING && Backend.Status != BackendStatus.IDLE)
                    {
                        continue;
                    }
                    SetStatus(BackendStatus.RUNNING);
                }
                catch (Exception)
                {
                    SetStatus(BackendStatus.IDLE);
                }
            }
        }

        public void Stop()
        {
            if (IdleMonitorThread is not null)
            {
                IdleMonitorCancel.Cancel();
                IdleMonitorCancel = new();
                IdleMonitorThread = null;
            }
        }
    }
    #endregion

    #region Self Start
    /// <summary>Returns true if the path given looks valid as a start-script for a backend, or false if not with an error message explaining why.</summary>
    public static bool IsValidStartPath(string backendLabel, string path, string ext)
    {
        if (path.Length < 5)
        {
            return false;
        }
        if (ext != "sh" && ext != "bat" && ext != "py")
        {
            Logs.Error($"Refusing init of {backendLabel} with non-script target. Please verify your start script location. Path was '{path}', which does not end in the expected 'py', 'bat', or 'sh'.");
            return false;
        }
        string subPath = path[1] == ':' ? path[2..] : path;
        if (Utilities.FilePathForbidden.ContainsAnyMatch(subPath))
        {
            Logs.Error($"Failed init of {backendLabel} with script target '{path}' because that file path contains invalid characters ( {Utilities.FilePathForbidden.TrimToMatches(subPath)} ). Please verify your start script location.");
            return false;
        }
        if (!File.Exists(path))
        {
            Logs.Error($"Failed init of {backendLabel} with script target '{path}' because that file does not exist. Please verify your start script location.");
            return false;
        }
        return true;
    }

    /// <summary>Internal tracking value of what port to use next.</summary>
    public static volatile int NextPort = 7820;

    /// <summary>Get the next available port to use, as an incremental value with checks against port usage.</summary>
    public static int GetNextPort()
    {
        int port = Interlocked.Increment(ref NextPort);
        while (Utilities.IsPortTaken(port))
        {
            port = Interlocked.Increment(ref NextPort);
        }
        return port;
    }

    /// <summary>Tries to identify the lib folder that will be used for a given start script.</summary>
    public static string GetProbableLibFolderFor(string script)
    {
        string path = script.Replace('\\', '/');
        string dir = Path.GetDirectoryName(path);
        if (File.Exists($"{dir}/venv/Scripts/python.exe"))
        {
            return Path.GetFullPath($"{dir}/venv/Lib");
        }
        if (File.Exists($"{dir}/../python_embeded/python.exe"))
        {
            return Path.GetFullPath($"{dir}/../python_embeded/Lib");
        }
        if (File.Exists($"{dir}/venv/bin/python3"))
        {
            //return Path.GetFullPath($"{dir}/venv/lib/");
            // sub-folder named like "python3.10"
            string[] subDirs = Directory.GetDirectories($"{dir}/venv/lib/");
            foreach (string subDir in subDirs)
            {
                if (subDir.AfterLast('/').StartsWith("python"))
                {
                    return Path.GetFullPath(subDir);
                }
            }
        }
        return null;
    }

    /// <summary>Configures python execution for a given python start script.</summary>
    public static void ConfigurePythonExeFor(string script, string nameSimple, ProcessStartInfo start, out string preArgs)
    {
        void AddPath(string path)
        {
            start.Environment["PATH"] = PythonLaunchHelper.ReworkPythonPaths(path);
            Logs.Debug($"({nameSimple} launch) Adding path {path}");
        }
        string path = script.Replace('\\', '/');
        string dir = Path.GetDirectoryName(path);
        start.WorkingDirectory = dir;
        preArgs = "-s " + path.AfterLast("/");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (File.Exists($"{dir}/venv/Scripts/python.exe"))
            {
                start.FileName = Path.GetFullPath($"{dir}/venv/Scripts/python.exe");
                AddPath(Path.GetFullPath($"{dir}/venv"));
            }
            else if (File.Exists($"{dir}/../python_embeded/python.exe"))
            {
                start.FileName = Path.GetFullPath($"{dir}/../python_embeded/python.exe");
                start.WorkingDirectory = Path.GetFullPath($"{dir}/..");
                preArgs = "-s " + Path.GetFullPath(path)[(start.WorkingDirectory.Length + 1)..];
                AddPath(Path.GetFullPath($"{dir}/../python_embeded"));
            }
            else
            {
                start.FileName = "python";
            }
        }
        else
        {
            if (File.Exists($"{dir}/venv/bin/python3"))
            {
                start.FileName = Path.GetFullPath($"{dir}/venv/bin/python3");
                AddPath(Path.GetFullPath($"{dir}/venv"));
            }
            else
            {
                start.FileName = "python3";
            }
        }
    }

    /// <summary>Starts a self-start backend based on the user-configuration and backend-specifics provided.</summary>
    public static Task DoSelfStart(string startScript, AbstractT2IBackend backend, string nameSimple, int gpuId, string extraArgs, Func<bool, Task> initInternal, Action<int, Process> takeOutput)
    {
        return DoSelfStart(startScript, nameSimple, gpuId, extraArgs, status => backend.Status = status, async (b) => { await initInternal(b); return backend.Status == BackendStatus.RUNNING; }, takeOutput, () => backend.Status, a => backend.OnShutdown += a);
    }

    /// <summary>Starts a self-start backend based on the user-configuration and backend-specifics provided.</summary>
    public static async Task DoSelfStart(string startScript, string nameSimple, int gpuId, string extraArgs, Action<BackendStatus> reviseStatus, Func<bool, Task<bool>> initInternal, Action<int, Process> takeOutput, Func<BackendStatus> getStatus, Action<Action> addShutdownEvent)
    {
        if (string.IsNullOrWhiteSpace(startScript))
        {
            Logs.Debug($"Cancelling start of {nameSimple} as it has an empty start script.");
            reviseStatus(BackendStatus.DISABLED);
            return;
        }
        Logs.Debug($"Requested generic launch of {startScript} on GPU {gpuId} from {nameSimple}");
        string path = startScript.Replace('\\', '/');
        string ext = path.AfterLast('.');
        if (!IsValidStartPath(nameSimple, path, ext))
        {
            reviseStatus(BackendStatus.ERRORED);
            return;
        }
        int port = GetNextPort();
        string dir = Path.GetDirectoryName(path);
        ProcessStartInfo start = new()
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = dir
        };
        PythonLaunchHelper.CleanEnvironmentOfPythonMess(start, $"({nameSimple} launch) ");
        start.Environment["CUDA_VISIBLE_DEVICES"] = $"{gpuId}";
        string preArgs = "";
        string postArgs = extraArgs.Replace("{PORT}", $"{port}").Trim();
        if (path.EndsWith(".py"))
        {
            ConfigurePythonExeFor(startScript, nameSimple, start, out preArgs);
            Logs.Debug($"({nameSimple} launch) Will use python: {start.FileName}");
        }
        else
        {
            Logs.Debug($"({nameSimple} launch) Will shellexec");
            start.FileName = Path.GetFullPath(path);
        }
        start.Arguments = $"{preArgs} {postArgs}".Trim();
        BackendStatus status = BackendStatus.LOADING;
        reviseStatus(status);
        Process runningProcess = new() { StartInfo = start };
        takeOutput(port, runningProcess);
        runningProcess.Start();
        Logs.Init($"Self-Start {nameSimple} on port {port} is loading...");
        ReportLogsFromProcess(runningProcess, $"{nameSimple} on port {port}", out Action signalShutdownExpected, getStatus, s => { status = s; reviseStatus(s); });
        addShutdownEvent?.Invoke(signalShutdownExpected);
        int checks = 0;
        while (status == BackendStatus.LOADING)
        {
            checks++;
            await Task.Delay(TimeSpan.FromSeconds(1));
            if (checks % 10 == 0)
            {
                Logs.Debug($"{nameSimple} port {port} waiting for server...");
            }
            bool alive = await initInternal(true);
            if (alive)
            {
                Logs.Init($"Self-Start {nameSimple} on port {port} started.");
            }
            status = getStatus();
        }
        Logs.Debug($"{nameSimple} self-start port {port} loop ending (should now be alive)");
    }

    public static void ReportLogsFromProcess(Process process, string nameSimple)
    {
        ReportLogsFromProcess(process, nameSimple, out Action signalShutdownExpected, () => BackendStatus.RUNNING, _ => { });
        signalShutdownExpected();
    }

    public static void ReportLogsFromProcess(Process process, string nameSimple, out Action signalShutdownExpected, Func<BackendStatus> getStatus, Action<BackendStatus> setStatus)
    {
        Logs.LogTracker logTracker = new();
        lock (Logs.OtherTrackers)
        {
            Logs.OtherTrackers[nameSimple] = logTracker;
        }
        BackendStatus status = getStatus();
        bool isShuttingDown = false;
        signalShutdownExpected = () => Volatile.Write(ref isShuttingDown, true);
        void MonitorLoop()
        {
            string line;
            bool keepShowing = false;
            while ((line = process.StandardOutput.ReadLine()) != null)
            {
                if (line.StartsWith("Traceback ("))
                {
                    keepShowing = true;
                    Logs.Warning($"{nameSimple} stdout: {line}");
                }
                else if (keepShowing && line.StartsWith("  "))
                {
                    Logs.Warning($"{nameSimple} stdout: {line}");
                }
                else if (keepShowing)
                {
                    Logs.Warning($"{nameSimple} stdout: {line}");
                    keepShowing = false;
                }
                else
                {
                    Logs.Debug($"{nameSimple} stdout: {line}");
                }
                logTracker.Track($"stdout: {line}");
            }
            status = getStatus();
            Logs.Debug($"Status of {nameSimple} after process end is {status}");
            if (status == BackendStatus.RUNNING || status == BackendStatus.LOADING)
            {
                status = BackendStatus.ERRORED;
                setStatus(status);
            }
            lock (Logs.OtherTrackers)
            {
                if (Logs.OtherTrackers.TryGetValue(nameSimple, out Logs.LogTracker tracker) && tracker == logTracker)
                {
                    Logs.OtherTrackers.Remove(nameSimple);
                }
            }
        }
        new Thread(MonitorLoop) { Name = $"SelfStart{nameSimple.Replace(' ', '_')}_Monitor" }.Start();
        void MonitorErrLoop()
        {
            StringBuilder errorLog = new();
            string line;
            while ((line = process.StandardError.ReadLine()) != null)
            {
                Logs.Debug($"{nameSimple} stderr: {line}");
                errorLog.AppendLine($"{nameSimple} error: {line}");
                logTracker.Track($"stderr: {line}");
                if (errorLog.Length > 1024 * 50)
                {
                    errorLog = new StringBuilder(errorLog.ToString()[(1024 * 10)..]);
                }
            }
            if (getStatus() == BackendStatus.DISABLED)
            {
                Logs.Info($"Self-Start {nameSimple} exited properly from disabling.");
            }
            else if (Volatile.Read(ref isShuttingDown))
            {
                Logs.Info($"Self-Start {nameSimple} exited properly.");
            }
            else
            {
                Logs.Info($"Self-Start {nameSimple} unexpectedly exited (if something failed, change setting `LogLevel` to `Debug` to see why!)");
                if (errorLog.Length > 0)
                {
                    Logs.Info($"Self-Start {nameSimple} had errors before shutdown:\n{errorLog}");
                }
            }
        }
        new Thread(MonitorErrLoop) { Name = $"SelfStart{nameSimple.Replace(' ', '_')}_MonitorErr" }.Start();
    }
    #endregion
}
