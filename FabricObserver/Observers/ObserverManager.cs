﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Health;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Interfaces;
using FabricObserver.Observers.Utilities;
using FabricObserver.Observers.Utilities.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ServiceFabric.TelemetryLib;

namespace FabricObserver.Observers
{
    // This class manages the lifetime of all observers from instantiation to "destruction",
    // and sequentially runs all observer instances in a never-ending while loop,
    // with optional sleeps, and reliable shutdown event handling.
    public class ObserverManager : IDisposable
    {
        private static bool etwEnabled;
        private readonly string nodeName;
        private readonly TelemetryEvents telemetryEvents;
        private List<IObserver> observers;
        private EventWaitHandle globalShutdownEventHandle;
        private volatile bool shutdownSignaled;
        private TimeSpan observerExecTimeout = TimeSpan.FromMinutes(30);
        private CancellationToken token;
        private CancellationTokenSource cts;
        private CancellationTokenSource linkedSFRuntimeObserverTokenSource;
        private bool disposedValue;
        private IEnumerable<IObserver> serviceCollection;
        private bool isConfigurationUpdateInProgess;

        /// <summary>
        /// Initializes a new instance of the <see cref="ObserverManager"/> class.
        /// This is used for unit testing.
        /// </summary>
        /// <param name="observer">Observer instance.</param>
        public ObserverManager(IObserver observer)
        {
            this.cts = new CancellationTokenSource();
            this.token = this.cts.Token;
            this.Logger = new Logger("ObserverManagerSingleObserverRun");
            this.HealthReporter = new ObserverHealthReporter(this.Logger);

            // The unit tests expect file output from some observers.
            ObserverWebAppDeployed = true;

            this.observers = new List<IObserver>(new[]
            {
                observer,
            });
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ObserverManager"/> class.
        /// </summary>
        /// <param name="context">service context.</param>
        /// <param name="token">cancellation token.</param>
        public ObserverManager(IServiceProvider serviceProvider, CancellationToken token)
        {
            this.token = token;
            this.cts = new CancellationTokenSource();
            this.linkedSFRuntimeObserverTokenSource = CancellationTokenSource.CreateLinkedTokenSource(this.cts.Token, this.token);
            FabricClientInstance = new FabricClient();
            FabricServiceContext = serviceProvider.GetRequiredService<StatelessServiceContext>();
            this.nodeName = FabricServiceContext?.NodeContext.NodeName;
            FabricServiceContext.CodePackageActivationContext.ConfigurationPackageModifiedEvent += CodePackageActivationContext_ConfigurationPackageModifiedEvent;
            
            // Observer Logger setup.
            string logFolderBasePath;
            string observerLogPath = GetConfigSettingValue(
                ObserverConstants.ObserverLogPathParameter);

            if (!string.IsNullOrEmpty(observerLogPath))
            {
                logFolderBasePath = observerLogPath;
            }
            else
            {
                string logFolderBase = Path.Combine(Environment.CurrentDirectory, "observer_logs");
                logFolderBasePath = logFolderBase;
            }

            // this logs error/warning/info messages for ObserverManager.
            this.Logger = new Logger("ObserverManager", logFolderBasePath);
            this.HealthReporter = new ObserverHealthReporter(this.Logger);
            this.SetPropertieSFromConfigurationParameters();
            this.serviceCollection = serviceProvider.GetServices<IObserver>();
            
            // Populate the Observer list for the sequential run loop.
            this.observers = this.serviceCollection.Where(o => o.IsEnabled).ToList();

            // FabricObserver Internal Diagnostic Telemetry (Non-PII).
            // Internally, TelemetryEvents determines current Cluster Id as a unique identifier for transmitted events.
            if (!FabricObserverInternalTelemetryEnabled)
            {
                return;
            }

            if (FabricServiceContext == null)
            {
                return;
            }

            string codePkgVersion = FabricServiceContext.CodePackageActivationContext.CodePackageVersion;
            string serviceManifestVersion = FabricServiceContext.CodePackageActivationContext.GetConfigurationPackageObject("Config").Description.ServiceManifestVersion;
            string filepath = Path.Combine(logFolderBasePath, $"fo_telemetry_sent_{codePkgVersion.Replace(".", string.Empty)}_{serviceManifestVersion.Replace(".", string.Empty)}_{FabricServiceContext.NodeContext.NodeType}.txt");
#if !DEBUG
            // If this has already been sent for this activated version (code/config) of nodetype x
            if (File.Exists(filepath))
            {
                return;
            }
#endif
            this.telemetryEvents = new TelemetryEvents(
                FabricClientInstance,
                FabricServiceContext,
                ServiceEventSource.Current,
                this.token);

            if (this.telemetryEvents.FabricObserverRuntimeNodeEvent(
                codePkgVersion,
                this.GetFabricObserverInternalConfiguration(),
                "HealthState.Initialized"))
            {
                // Log a file to prevent re-sending this in case of process restart(s).
                // This non-PII FO/Cluster info is versioned and should only be sent once per deployment (config or code updates.).
                this.Logger.TryWriteLogFile(filepath, "_");
            }
        }

        public static FabricClient FabricClientInstance
        {
            get; set;
        }

        public static int ObserverExecutionLoopSleepSeconds { get; private set; } = ObserverConstants.ObserverRunLoopSleepTimeSeconds;

        public static StatelessServiceContext FabricServiceContext
        {
            get; set;
        }

        public static ITelemetryProvider TelemetryClient
        {
            get; set;
        }

        public static bool TelemetryEnabled
        {
            get; set;
        }

        public static bool FabricObserverInternalTelemetryEnabled { get; set; } = true;

        public static bool ObserverWebAppDeployed
        {
            get; set;
        }

        public static bool EtwEnabled
        {
            get => bool.TryParse(GetConfigSettingValue(ObserverConstants.EnableEventSourceProvider), out etwEnabled) && etwEnabled;

            set => etwEnabled = value;
        }

        public static string EtwProviderName
        {
            get
            {
                if (!EtwEnabled)
                {
                    return null;
                }

                string key = GetConfigSettingValue(ObserverConstants.EventSourceProviderName);

                return !string.IsNullOrEmpty(key) ? key : null;
            }
        }

        public string ApplicationName
        {
            get; set;
        }

        public bool IsObserverRunning
        {
            get; set;
        }

        private ObserverHealthReporter HealthReporter
        {
            get; set;
        }

        private string Fqdn
        {
            get; set;
        }

        private Logger Logger
        {
            get; set;
        }

        public async Task StartObserversAsync()
        {
            try
            {
                // Nothing to do here.
                if (this.observers.Count == 0)
                {
                    return;
                }

                // Create Global Shutdown event handler
                this.globalShutdownEventHandle = new EventWaitHandle(false, EventResetMode.ManualReset);

                // Continue running until a shutdown signal is sent
                this.Logger.LogInfo("Starting Observers loop.");

                // Observers run sequentially. See RunObservers impl.
                while (true)
                {
                    if (this.shutdownSignaled || this.token.IsCancellationRequested)
                    {
                        _ = this.globalShutdownEventHandle.Set();
                        this.Logger.LogWarning("Shutdown signaled. Stopping.");
                        break;
                    }

                    if (!await this.RunObserversAsync().ConfigureAwait(false))
                    {
                        continue;
                    }

                    if (ObserverExecutionLoopSleepSeconds > 0)
                    {
                        this.Logger.LogInfo($"Sleeping for {ObserverExecutionLoopSleepSeconds} seconds before running again.");
                        this.ThreadSleep(this.globalShutdownEventHandle, TimeSpan.FromSeconds(ObserverExecutionLoopSleepSeconds));
                    }
                }
            }
            catch (Exception ex)
            {
                var message =
                    $"Unhanded Exception in {ObserverConstants.ObserverManagerName} on node " +
                    $"{this.nodeName}. Taking down FO process. " +
                    $"Error info:{Environment.NewLine}{ex}";

                this.Logger.LogError(message);

                // Telemetry.
                if (TelemetryEnabled)
                {
                    await (TelemetryClient?.ReportHealthAsync(
                            HealthScope.Application,
                            "FabricObserverServiceHealth",
                            HealthState.Warning,
                            message,
                            ObserverConstants.ObserverManagerName,
                            this.token)).ConfigureAwait(false);
                }

                // ETW.
                if (EtwEnabled)
                {
                    Logger.EtwLogger?.Write(
                        $"FabricObserverServiceCriticalHealthEvent",
                        new
                        {
                            Level = 2, // Error
                            Node = this.nodeName,
                            Observer = ObserverConstants.ObserverManagerName,
                            Value = message,
                        });
                }

                // Don't swallow the exception.
                // Take down FO process. Fix the bugs in OM that this identifies.
                throw;
            }

            this.ShutDown();
        }

        public void StopObservers(bool shutdownSignaled = true)
        {
            try
            {
                this.shutdownSignaled = shutdownSignaled;
                this.SignalAbortToRunningObserver();
                this.IsObserverRunning = false;
            }
            catch (Exception e)
            {
                this.Logger.LogWarning($"Unhandled Exception thrown during ObserverManager.Stop(): {e}");

                throw;
            }
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            this.Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!this.disposedValue)
            {
                if (disposing)
                {
                    if (FabricClientInstance != null)
                    {
                        FabricClientInstance.Dispose();
                        FabricClientInstance = null;
                    }
                }

                FabricServiceContext.CodePackageActivationContext.ConfigurationPackageModifiedEvent -= this.CodePackageActivationContext_ConfigurationPackageModifiedEvent;

                this.disposedValue = true;
            }
        }

        private static bool IsObserverWebApiAppInstalled()
        {
            try
            {
                var deployedObsWebApps = FabricClientInstance.QueryManager.GetApplicationListAsync(new Uri("fabric:/FabricObserverWebApi")).GetAwaiter().GetResult();
                return deployedObsWebApps?.Count > 0;
            }
            catch (Exception e) when (e is FabricException || e is TimeoutException)
            {
            }

            return false;
        }

        private static string GetConfigSettingValue(string parameterName)
        {
            try
            {
                var configSettings = FabricServiceContext.CodePackageActivationContext?.GetConfigurationPackageObject("Config")?.Settings;
                var section = configSettings?.Sections[ObserverConstants.ObserverManagerConfigurationSectionName];
                var parameter = section?.Parameters[parameterName];

                return parameter?.Value;
            }
            catch (Exception e) when (e is KeyNotFoundException || e is FabricElementNotFoundException)
            {
            }

            return null;
        }

        private void ShutDown()
        {
            if (this.IsObserverRunning)
            {
                this.StopObservers();
            }

            _ = this.globalShutdownEventHandle?.Set();

            if (this.observers?.Count > 0)
            {
                foreach (IObserver obs in this.observers)
                {
                    obs?.Dispose();
                }

                this.observers.Clear();
            }

            if (this.cts != null)
            {
                this.cts.Dispose();
                this.cts = null;
            }

            this.globalShutdownEventHandle?.Dispose();

            // Flush and Dispose all NLog targets. No more logging.
            Logger.Flush();
            DataTableFileLogger.Flush();
            Logger.ShutDown();
            DataTableFileLogger.ShutDown();
        }

        /// <summary>
        /// This function gets FabricObserver's internal configuration for telemetry for Charles and company.
        /// No PII. As you can see, this is generic information - number of enabled observer, observer names.
        /// </summary>
        private string GetFabricObserverInternalConfiguration()
        {
            int enabledObserverCount = this.observers.Count(obs => obs.IsEnabled);
            string ret;
            string observerList = this.observers.Aggregate("{ ", (current, obs) => current + $"{obs.ObserverName} ");

            observerList += "}";
            ret = $"EnabledObserverCount: {enabledObserverCount}, EnabledObservers: {observerList}";

            return ret;
        }

        // This impl is to ensure FO exits if shutdown is requested while the over loop is sleeping
        // So, instead of blocking with a Thread.Sleep, for example, ThreadSleep is used to ensure
        // we can receive signals and act accordingly during thread sleep state.
        private void ThreadSleep(WaitHandle ewh, TimeSpan timeout)
        {
            // if timeout is <= 0, return. 0 is infinite, and negative is not valid
            if (timeout.TotalMilliseconds <= 0)
            {
                return;
            }

            var elapsedTime = new TimeSpan(0, 0, 0);
            var stopwatch = new Stopwatch();

            while (!this.shutdownSignaled &&
                   !this.token.IsCancellationRequested &&
                   timeout > elapsedTime)
            {
                stopwatch.Start();

                // the event can be signaled by CtrlC,
                // Exit ASAP when the program terminates (i.e., shutdown/abort is signalled.)
                _ = ewh.WaitOne(timeout.Subtract(elapsedTime));
                stopwatch.Stop();

                elapsedTime = stopwatch.Elapsed;
            }

            if (stopwatch.IsRunning)
            {
                stopwatch.Stop();
            }
        }

        /// <summary>
        /// Event handler for application parameter updates (Application Upgrades).
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e">Contains the information necessary for setting new config params from updated package.</param>
        private async void CodePackageActivationContext_ConfigurationPackageModifiedEvent(object sender, PackageModifiedEventArgs<ConfigurationPackage> e)
        {
            this.isConfigurationUpdateInProgess = true;
            this.StopObservers(false);

            await Task.Delay(
                TimeSpan.FromSeconds(1),
                this.linkedSFRuntimeObserverTokenSource != null ? this.linkedSFRuntimeObserverTokenSource.Token : this.token).ConfigureAwait(false);
            
            try
            {
                foreach (var observer in this.serviceCollection)
                {
                    observer.ConfigurationSettings.UpdateConfigSettings(e.NewPackage.Settings);
                }

                this.observers = this.serviceCollection.Where(o => o.IsEnabled).ToList();
            }
            catch (Exception err)
            {
                var healthReport = new Utilities.HealthReport
                {
                    AppName = new Uri(FabricServiceContext.CodePackageActivationContext.ApplicationName),
                    Code = FoErrorWarningCodes.Ok,
                    ReportType = HealthReportType.Application,
                    HealthMessage = $"Error updating observer with new configuration:{Environment.NewLine}{err}",
                    NodeName = FabricServiceContext.NodeContext.NodeName,
                    State = HealthState.Ok,
                    Property = $"Configuration_Upate_Error",
                };

                this.HealthReporter.ReportHealthToServiceFabric(healthReport);
            }

            this.isConfigurationUpdateInProgess = false;
        }

        /// <summary>
        /// Sets ObserverManager's related properties/fields to their corresponding Settings.xml 
        /// configuration settings (parameter values).
        /// </summary>
        private void SetPropertieSFromConfigurationParameters()
        {
            this.ApplicationName = FabricRuntime.GetActivationContext().ApplicationName;

            // Observers
            if (int.TryParse(
                GetConfigSettingValue(ObserverConstants.ObserverExecutionTimeout),
                out int result))
            {
                this.observerExecTimeout = TimeSpan.FromSeconds(result);
            }

            // Logger
            if (bool.TryParse(
                GetConfigSettingValue(ObserverConstants.EnableVerboseLoggingParameter),
                out bool enableVerboseLogging))
            {
                this.Logger.EnableVerboseLogging = enableVerboseLogging;
            }

            if (int.TryParse(
                GetConfigSettingValue(ObserverConstants.ObserverLoopSleepTimeSeconds),
                out int execFrequency))
            {
                ObserverExecutionLoopSleepSeconds = execFrequency;

                this.Logger.LogInfo($"ExecutionFrequency is {ObserverExecutionLoopSleepSeconds} Seconds");
            }

            // FQDN for use in warning or error hyperlinks in HTML output
            // This only makes sense when you have the FabricObserverWebApi app installed.
            string fqdn = GetConfigSettingValue(ObserverConstants.Fqdn);
            if (!string.IsNullOrEmpty(fqdn))
            {
                this.Fqdn = fqdn;
            }

            // FabricObserver runtime telemetry (Non-PII)
            if (bool.TryParse(GetConfigSettingValue(ObserverConstants.FabricObserverTelemetryEnabled), out bool foTelemEnabled))
            {
                FabricObserverInternalTelemetryEnabled = foTelemEnabled;
            }

            // ObserverWebApi.
            ObserverWebAppDeployed = bool.TryParse(GetConfigSettingValue(ObserverConstants.ObserverWebApiAppDeployed), out bool obsWeb) ? obsWeb : IsObserverWebApiAppInstalled();

            // (Assuming Diagnostics/Analytics cloud service implemented) Telemetry.
            if (bool.TryParse(GetConfigSettingValue(ObserverConstants.TelemetryEnabled), out bool telemEnabled))
            {
                TelemetryEnabled = telemEnabled;
            }

            if (TelemetryEnabled)
            {
                string telemetryProviderType = GetConfigSettingValue(ObserverConstants.TelemetryProviderType);

                if (string.IsNullOrEmpty(telemetryProviderType))
                {
                    TelemetryEnabled = false;

                    return;
                }

                if (!Enum.TryParse(telemetryProviderType, out TelemetryProviderType telemetryProvider))
                {
                    TelemetryEnabled = false;

                    return;
                }

                switch (telemetryProvider)
                {
                    case TelemetryProviderType.AzureLogAnalytics:
                    {
                        var logAnalyticsLogType =
                            GetConfigSettingValue(ObserverConstants.LogAnalyticsLogTypeParameter);

                        var logAnalyticsSharedKey =
                            GetConfigSettingValue(ObserverConstants.LogAnalyticsSharedKeyParameter);

                        var logAnalyticsWorkspaceId =
                            GetConfigSettingValue(ObserverConstants.LogAnalyticsWorkspaceIdParameter);

                        if (string.IsNullOrEmpty(logAnalyticsWorkspaceId)
                            || string.IsNullOrEmpty(logAnalyticsSharedKey))
                        {
                            TelemetryEnabled = false;

                            return;
                        }

                        TelemetryClient = new LogAnalyticsTelemetry(
                            logAnalyticsWorkspaceId,
                            logAnalyticsSharedKey,
                            logAnalyticsLogType,
                            FabricClientInstance,
                            this.token);

                        break;
                    }

                    case TelemetryProviderType.AzureApplicationInsights:
                    {
                        string aiKey = GetConfigSettingValue(ObserverConstants.AiKey);

                        if (string.IsNullOrEmpty(aiKey))
                        {
                            TelemetryEnabled = false;

                            return;
                        }

                        TelemetryClient = new AppInsightsTelemetry(aiKey);

                        break;
                    }

                    default:

                        TelemetryEnabled = false;

                        break;
                }
            }
        }

        /// <summary>
        /// This function will signal cancellation on the token passed to an observer's ObserveAsync. 
        /// This will eventually cause the observer to stop processing as this will throw an OperationCancelledExeption 
        /// in one of the observer's executing code paths.
        /// </summary>
        private void SignalAbortToRunningObserver()
        {
            this.Logger.LogInfo("Signalling task cancellation to currently running Observer.");
            
            if (this.linkedSFRuntimeObserverTokenSource != null)
            {
                this.linkedSFRuntimeObserverTokenSource.Cancel();
            }
            else 
            {
                this.cts?.Cancel();
            }

            this.Logger.LogInfo("Successfully signaled cancellation to currently running Observer.");
        }

        /// <summary>
        /// Runs all observers in a sequential loop.
        /// </summary>
        /// <returns>A boolean value indicating success of a complete observer loop run.</returns>
        private async Task<bool> RunObserversAsync()
        {
            var exceptionBuilder = new StringBuilder();
            bool allExecuted = true;

            foreach (var observer in this.observers)
            {
                try
                {
                    // Shutdown/cancellation signaled, so stop.
                    bool taskCancelled = this.linkedSFRuntimeObserverTokenSource != null ? this.linkedSFRuntimeObserverTokenSource.Token.IsCancellationRequested : this.token.IsCancellationRequested;
                    
                    if (taskCancelled || this.shutdownSignaled)
                    {
                        return false;
                    }

                    // Is it healthy?
                    if (observer.IsUnhealthy)
                    {
                        continue;
                    }

                    this.Logger.LogInfo($"Starting {observer.ObserverName}");

                    this.IsObserverRunning = true;

                    // Synchronous call.
                    var isCompleted = observer.ObserveAsync(
                        this.linkedSFRuntimeObserverTokenSource != null ? this.linkedSFRuntimeObserverTokenSource.Token : this.token).Wait(this.observerExecTimeout);

                    // The observer is taking too long (hung?), move on to next observer.
                    // Currently, this observer will not run again for the lifetime of this FO service instance.
                    if (!isCompleted)
                    {
                        string observerHealthWarning = observer.ObserverName + $" has exceeded its specified run time of {this.observerExecTimeout.TotalSeconds} seconds. " +
                                                       $"This means something is wrong with {observer.ObserverName}. It will not be run again. Look into it.";

                        this.Logger.LogError(observerHealthWarning);
                        observer.IsUnhealthy = true;

                        if (TelemetryEnabled)
                        {
                            await (TelemetryClient?.ReportHealthAsync(
                                    HealthScope.Application,
                                    $"{observer.ObserverName}HealthError",
                                    HealthState.Error,
                                    observerHealthWarning,
                                    ObserverConstants.ObserverManagerName,
                                    this.token)).ConfigureAwait(false);
                        }

                        continue;
                    }

                    this.Logger.LogInfo($"Successfully ran {observer.ObserverName}.");

                    if (!ObserverWebAppDeployed)
                    {
                        continue;
                    }

                    if (observer.HasActiveFabricErrorOrWarning)
                    {
                        var errWarnMsg = !string.IsNullOrEmpty(this.Fqdn) ? $"<a style=\"font-weight: bold; color: red;\" href=\"http://{this.Fqdn}/api/ObserverLog/{observer.ObserverName}/{observer.NodeName}/json\">One or more errors or warnings detected</a>." : $"One or more errors or warnings detected. Check {observer.ObserverName} logs for details.";

                        this.Logger.LogWarning($"{observer.ObserverName}: " + errWarnMsg);
                    }
                    else
                    {
                        // Delete the observer's instance log (local file with Warn/Error details per run)..
                        _ = observer.ObserverLogger.TryDeleteInstanceLog();

                        try
                        {
                            if (File.Exists(this.Logger.FilePath))
                            {
                                // Replace the ObserverManager.log text that doesn't contain the observer Warn/Error line(s).
                                File.WriteAllLines(
                                    this.Logger.FilePath,
                                    File.ReadLines(this.Logger.FilePath)
                                        .Where(line => !line.Contains(observer.ObserverName)).ToList());
                            }
                        }
                        catch (IOException)
                        {
                        }
                    }
                }
                catch (AggregateException ex)
                {
                    this.IsObserverRunning = false;

                    if (ex.InnerException is OperationCanceledException ||
                        ex.InnerException is TaskCanceledException)
                    {
                        if (this.isConfigurationUpdateInProgess)
                        {
                            this.IsObserverRunning = false;
                            return true;
                        }

                        continue;
                    }

                    _ = exceptionBuilder.AppendLine($"Exception from {observer.ObserverName}:\r\n{ex.InnerException}");
                    allExecuted = false;
                }

                this.IsObserverRunning = false;
            }

            if (allExecuted)
            {
                this.Logger.LogInfo(ObserverConstants.AllObserversExecutedMessage);
            }
            else
            {
                this.HealthReporter.ReportFabricObserverServiceHealth(
                    ObserverConstants.ObserverManagerName,
                    this.ApplicationName,
                    HealthState.Error,
                    exceptionBuilder.ToString());

                _ = exceptionBuilder.Clear();
            }

            return allExecuted;
        }
    }
}
