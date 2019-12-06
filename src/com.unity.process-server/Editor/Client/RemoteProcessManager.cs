﻿namespace Unity.Editor.ProcessServer
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Interfaces;
    using Tasks;
    using Unity.Editor.ProcessServer.Internal.IO;

    public interface IRemoteProcessManager : IProcessManager
    {
        T Configure<T>(T processTask, ProcessOptions options = default, string workingDirectory = null)
            where T : IProcessTask;
        T Configure<T>(T processTask, ProcessStartInfo startInfo, ProcessOptions options = default)
            where T : IProcessTask;

        event EventHandler<IpcProcessRestartEventArgs> OnProcessRestart;
    }


    class RemoteProcessEnvironment : IProcessEnvironment
    {
        private readonly IProcessEnvironment localProcessEnvironment;

        public RemoteProcessEnvironment(IProcessEnvironment localProcessEnvironment)
        {
            this.localProcessEnvironment = localProcessEnvironment;
        }

        public void Configure(ProcessStartInfo psi)
        {
            localProcessEnvironment.Configure(psi);
        }

        public IEnvironment Environment => localProcessEnvironment.Environment;
    }

    class RemoteProcessManager : IRemoteProcessManager
    {
        private readonly Dictionary<string, SynchronizationContextTaskScheduler> processes = new Dictionary<string, SynchronizationContextTaskScheduler>();
        private readonly Dictionary<string, RemoteProcessWrapper> wrappers = new Dictionary<string, RemoteProcessWrapper>();
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private IProcessRunner runner;
        public event EventHandler<IpcProcessRestartEventArgs> OnProcessRestart;

        public RemoteProcessManager(IProcessEnvironment environment, CancellationToken token)
        {
            token.Register(cts.Cancel);
            DefaultProcessEnvironment = environment;
            ProcessNotifications = new Notifications(this);
        }

        public void ConnectToServer(IProcessRunner processRunner)
        {
            this.runner = processRunner;
        }

        public T Configure<T>(T processTask, ProcessStartInfo startInfo, ProcessOptions options = default) where T : IProcessTask
        {
            processTask.Configure(this, startInfo);
            if (processTask.Wrapper is RemoteProcessWrapper wrapper)
            {
                wrapper.Configure(options);
                wrapper.OnProcessPrepared += (processWrapper, ipcProcess) => {
                    wrappers.Add(ipcProcess.Id, processWrapper);
                    processes.Add(ipcProcess.Id, new SynchronizationContextTaskScheduler(new ThreadSynchronizationContext(cts.Token)));
                };
            }
            return processTask;
        }

        public T Configure<T>(T processTask, ProcessOptions options = default, string workingDirectory = null) where T : IProcessTask
        {
            var startInfo = new ProcessStartInfo {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            startInfo.FileName = processTask.ProcessName.ToSPath().ToString();
            startInfo.Arguments = processTask.ProcessArguments;
            startInfo.WorkingDirectory = workingDirectory;

            return Configure(processTask, startInfo, options);
        }

        public T Configure<T>(T processTask, string workingDirectory = null) where T : IProcessTask => Configure(processTask, workingDirectory);

        public T Configure<T>(T processTask, ProcessStartInfo startInfo) where T : IProcessTask => Configure(processTask, startInfo, default);

        public BaseProcessWrapper WrapProcess(string taskName, ProcessStartInfo startInfo, IOutputProcessor outputProcessor,
            Action onStart, Action onEnd, Action<Exception, string> onError,
            CancellationToken token)
        {
            if (runner == null)
            {
                throw new InvalidOperationException("runner is not set on RemoteProcessManager. Did you forget to call ConnectToServer(runner)?");
            }
            return new RemoteProcessWrapper(runner, startInfo, outputProcessor, onStart, onEnd, onError, token);
        }

        public void Stop()
        {
            Dispose();
        }

        private void RaiseProcessOnStart(IpcProcess process)
        {
            if (!wrappers.TryGetValue(process.Id, out var wrapper) || !processes.TryGetValue(process.Id, out var scheduler))
                throw new InvalidOperationException($"OnStart for process {process.Id} was called but there's no record of it in the process list.");

            scheduler.Schedule(s => wrapper.OnProcessStart(), null, cts.Token);
        }

        private void RaiseOnProcessEnd(IpcProcess process, bool success, Exception ex, string errors)
        {
            if (!wrappers.TryGetValue(process.Id, out var wrapper) || !processes.TryGetValue(process.Id, out var scheduler))
                throw new InvalidOperationException($"OnEnd for process {process.Id} was called but there's no record of it in the process list.");

            var task = new Task(s => wrapper.OnProcessEnd((IpcProcessEndEventArgs)s), new IpcProcessEndEventArgs(process, success, ex, errors),
                cts.Token, TaskCreationOptions.None);

            task.ContinueWith((_, __) => {
                // process is done and it won't be restarted, cleanup
                if (process.ProcessOptions.MonitorOptions != MonitorOptions.KeepAlive)
                {
                    lock (processes)
                    {
                        if (wrappers.ContainsKey(process.Id))
                        {
                            processes.Remove(process.Id);
                            wrappers.Remove(process.Id);
                            scheduler.Dispose();
                            ((ThreadSynchronizationContext)scheduler.Context).Dispose();
                        }
                    }
                }
            }, null, cts.Token, TaskContinuationOptions.None, TaskScheduler.Default);
            task.Start(scheduler);
        }

        private void RaiseProcessOnError(IpcProcess process, string errors)
        {
            if (!wrappers.TryGetValue(process.Id, out var wrapper) || !processes.TryGetValue(process.Id, out var scheduler))
                throw new InvalidOperationException($"OnError for process {process.Id} was called but there's no record of it in the process list.");

            scheduler.Schedule(s => wrapper.OnProcessError((IpcProcessErrorEventArgs)s), new IpcProcessErrorEventArgs(process, errors), cts.Token);
        }

        private void RaiseProcessOnOutput(IpcProcess process, string line)
        {
            if (!wrappers.TryGetValue(process.Id, out var wrapper) || !processes.TryGetValue(process.Id, out var scheduler))
                throw new InvalidOperationException($"OnError for process {process.Id} but there's no record of it in the process list.");

            scheduler.Schedule(s => wrapper.OnProcessOutput((IpcProcessOutputEventArgs)s), new IpcProcessOutputEventArgs(process, line), cts.Token);
        }

        internal void RaiseProcessRestart(IpcProcess process, ProcessRestartReason reason)
        {
            if (!wrappers.TryGetValue(process.Id, out var wrapper) || !processes.TryGetValue(process.Id, out var scheduler))
                throw new InvalidOperationException($"OnRestart for process {process.Id} but there's no record of it in the process list.");

            scheduler.Schedule(s => OnProcessRestart?.Invoke(this, (IpcProcessRestartEventArgs)s), new IpcProcessRestartEventArgs(process, reason), cts.Token);
        }

        private bool disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (disposed) return;
            disposed = true;

            if (disposing)
            {
                RemoteProcessWrapper[] wraps;
                SynchronizationContextTaskScheduler[] procs;
                lock (processes)
                {
                    wraps = wrappers.Values.ToArray();
                    procs = processes.Values.ToArray();
                    wrappers.Clear();
                    processes.Clear();
                }

                foreach (var w in wraps) w.Detach();
                foreach (var p in procs)
                {
                    p.Dispose();
                    ((IDisposable)p.Context).Dispose();
                }
                cts.Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        public IProcessEnvironment DefaultProcessEnvironment { get; }
        public IProcessNotifications ProcessNotifications { get; }

        class Notifications : IProcessNotifications
        {
            private readonly RemoteProcessManager manager;

            public Notifications(RemoteProcessManager manager)
            {
                this.manager = manager;
            }

            public async Task ProcessOnEnd(IpcProcess process, bool success, Exception ex, string errors) =>
                manager.RaiseOnProcessEnd(process, success, ex, errors);

            public async Task ProcessOnError(IpcProcess process, string errors) => manager.RaiseProcessOnError(process, errors);

            public async Task ProcessOnOutput(IpcProcess process, string line) =>
                manager.RaiseProcessOnOutput(process, line);

            public async Task ProcessOnStart(IpcProcess process) => manager.RaiseProcessOnStart(process);
        }
    }
}