﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric.Health;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FabricObserver.Observers.Utilities;
using Microsoft.Extensions.PlatformAbstractions;

namespace FabricObserver.Observers
{
    // This observer monitors logical disk behavior and signals Service Fabric Warning or Error events based on user-supplied thresholds
    // in Settings.xml.
    // The output (a local file) is used by the API service and the HTML frontend (http://localhost:5000/api/ObserverManager).
    // Health Report processor will also emit ETW telemetry if configured in Settings.xml.
    public class DiskObserver : ObserverBase
    {
        // Data storage containers for post run analysis.
        private readonly List<FabricResourceUsageData<float>> diskAverageQueueLengthData;
        private readonly List<FabricResourceUsageData<double>> diskSpacePercentageUsageData;
        private readonly List<FabricResourceUsageData<double>> diskSpaceUsageData;
        private readonly List<FabricResourceUsageData<double>> diskSpaceAvailableData;
        private readonly List<FabricResourceUsageData<double>> diskSpaceTotalData;
        private readonly Stopwatch stopWatch;
        private StringBuilder diskInfo = new StringBuilder();

        public int DiskSpacePercentErrorThreshold { get; set; }

        public int DiskSpacePercentWarningThreshold { get; set; }

        public int AverageQueueLengthWarningThreshold { get; set; }

        public int AverageQueueLengthErrorThreshold { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="DiskObserver"/> class.
        /// </summary>
        public DiskObserver()
        {
            this.diskSpacePercentageUsageData = new List<FabricResourceUsageData<double>>();
            this.diskSpaceUsageData = new List<FabricResourceUsageData<double>>();
            this.diskSpaceAvailableData = new List<FabricResourceUsageData<double>>();
            this.diskSpaceTotalData = new List<FabricResourceUsageData<double>>();
            this.diskAverageQueueLengthData = new List<FabricResourceUsageData<float>>();
            this.stopWatch = new Stopwatch();
        }

        
        public override async Task ObserveAsync(CancellationToken token)
        {
            // If set, this observer will only run during the supplied interval.
            // See Settings.xml, CertificateObserverConfiguration section, RunInterval parameter for an example.
            if (this.RunInterval > TimeSpan.MinValue
                && DateTime.Now.Subtract(this.LastRunDateTime) < this.RunInterval)
            {
                return;
            }

            this.SetErrorWarningThresholds();

            DriveInfo[] allDrives = DriveInfo.GetDrives();

            if (ObserverManager.ObserverWebAppDeployed)
            {
                this.diskInfo = new StringBuilder();
            }

            foreach (var d in allDrives)
            {
                token.ThrowIfCancellationRequested();

                if (!DiskUsage.ShouldCheckDrive(d))
                {
                    continue;
                }

                // This section only needs to run if you have the FabricObserverWebApi app installed.
                if (ObserverManager.ObserverWebAppDeployed)
                {
                    _ = this.diskInfo.AppendFormat("\n\nDrive Name: {0}\n", d.Name);

                    // Logging.
                    _ = this.diskInfo.AppendFormat("Drive Type: {0}\n", d.DriveType);
                    _ = this.diskInfo.AppendFormat("  Volume Label   : {0}\n", d.VolumeLabel);
                    _ = this.diskInfo.AppendFormat("  Filesystem     : {0}\n", d.DriveFormat);
                    _ = this.diskInfo.AppendFormat("  Total Disk Size: {0} GB\n", d.TotalSize / 1024 / 1024 / 1024);
                    _ = this.diskInfo.AppendFormat("  Root Directory : {0}\n", d.RootDirectory);
                    _ = this.diskInfo.AppendFormat("  Free User : {0} GB\n", d.AvailableFreeSpace / 1024 / 1024 / 1024);
                    _ = this.diskInfo.AppendFormat("  Free Total: {0} GB\n", d.TotalFreeSpace / 1024 / 1024 / 1024);
                    _ = this.diskInfo.AppendFormat("  % Used    : {0}%\n", DiskUsage.GetCurrentDiskSpaceUsedPercent(d.Name));
                }

                // Setup monitoring data structures.
                string id = d.Name;

                // Since these live across iterations, do not duplicate them in the containing list.
                // Disk space %.
                if (this.diskSpacePercentageUsageData.All(data => data.Id != id))
                {
                    this.diskSpacePercentageUsageData.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.DiskSpaceUsagePercentage, id, this.DataCapacity));
                }

                if (this.diskSpaceUsageData.All(data => data.Id != id))
                {
                    this.diskSpaceUsageData.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.DiskSpaceUsageMb, id, this.DataCapacity));
                }

                if (this.diskSpaceAvailableData.All(data => data.Id != id))
                {
                    this.diskSpaceAvailableData.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.DiskSpaceAvailableMb, id, this.DataCapacity));
                }

                if (this.diskSpaceTotalData.All(data => data.Id != id))
                {
                    this.diskSpaceTotalData.Add(new FabricResourceUsageData<double>(ErrorWarningProperty.DiskSpaceTotalMb, id, this.DataCapacity));
                }

                // Current disk queue length.
                if (this.diskAverageQueueLengthData.All(data => data.Id != id))
                {
                    this.diskAverageQueueLengthData.Add(new FabricResourceUsageData<float>(ErrorWarningProperty.DiskAverageQueueLength, id, this.DataCapacity));
                }

                // Generate data over time (monitorDuration.) for use in ReportAsync health analysis.
                this.stopWatch.Start();

                TimeSpan duration = TimeSpan.FromSeconds(10);

                if (this.MonitorDuration > TimeSpan.MinValue)
                {
                    duration = this.MonitorDuration;
                }

                // Warm up the counters.
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // It is import to check if code is running on Windows,
                    // since d.Name.Substring(0, 2) will fail on Linux for / (root) mount point.
                    _ = DiskUsage.GetAverageDiskQueueLength(d.Name.Substring(0, 2));
                }

                while (this.stopWatch.Elapsed <= duration)
                {
                    token.ThrowIfCancellationRequested();

                    this.diskSpacePercentageUsageData.Single(
                            x => x.Id == id)
                        .Data.Add(DiskUsage.GetCurrentDiskSpaceUsedPercent(id));

                    this.diskSpaceUsageData.Single(
                            x => x.Id == id)
                        .Data.Add(DiskUsage.GetUsedDiskSpace(id, SizeUnit.Megabytes));

                    this.diskSpaceAvailableData.Single(
                            x => x.Id == id)
                        .Data.Add(DiskUsage.GetAvailableDiskSpace(id, SizeUnit.Megabytes));

                    this.diskSpaceTotalData.Single(
                            x => x.Id == id)
                        .Data.Add(DiskUsage.GetTotalDiskSpace(id, SizeUnit.Megabytes));

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        this.diskAverageQueueLengthData.Single(
                            x => x.Id == id)
                        .Data.Add(DiskUsage.GetAverageDiskQueueLength(d.Name.Substring(0, 2)));
                    }

                    await Task.Delay(250).ConfigureAwait(true);
                }

                // This section only needs to run if you have the FabricObserverWebApi app installed.
                if (ObserverManager.ObserverWebAppDeployed && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    _ = this.diskInfo.AppendFormat(
                        "{0}",
                        this.GetWindowsPerfCounterDetailsText(
                            this.diskAverageQueueLengthData.FirstOrDefault(
                                x => x.Id == d.Name.Substring(0, 1))
                                ?.Data,
                            "Avg. Disk Queue Length"));
                }

                this.RunDuration = this.stopWatch.Elapsed;
                this.stopWatch.Reset();
            }

            await this.ReportAsync(token).ConfigureAwait(true);
            this.LastRunDateTime = DateTime.Now;
        }

        public override Task ReportAsync(CancellationToken token)
        {
            try
            {
                var timeToLiveWarning = this.SetHealthReportTimeToLive();

                // User-supplied Disk Space Usage % thresholds from Settings.xml.
                foreach (var data in this.diskSpacePercentageUsageData)
                {
                    token.ThrowIfCancellationRequested();
                    this.ProcessResourceDataReportHealth(
                        data,
                        this.DiskSpacePercentErrorThreshold,
                        this.DiskSpacePercentWarningThreshold,
                        timeToLiveWarning);
                }

                // User-supplied Average disk queue length thresholds from Settings.xml.
                foreach (var data in this.diskAverageQueueLengthData)
                {
                    token.ThrowIfCancellationRequested();
                    this.ProcessResourceDataReportHealth(
                        data,
                        this.AverageQueueLengthErrorThreshold,
                        this.AverageQueueLengthWarningThreshold,
                        timeToLiveWarning);
                }

                /* For ETW Only */
                if (this.IsEtwEnabled)
                {
                    // Disk Space Usage
                    foreach (var data in this.diskSpaceUsageData)
                    {
                        token.ThrowIfCancellationRequested();
                        this.ProcessResourceDataReportHealth(
                            data,
                            0,
                            0,
                            timeToLiveWarning);
                    }

                    // Disk Space Available
                    foreach (var data in this.diskSpaceAvailableData)
                    {
                        token.ThrowIfCancellationRequested();
                        this.ProcessResourceDataReportHealth(
                            data,
                            0,
                            0,
                            timeToLiveWarning);
                    }

                    // Disk Space Total
                    foreach (var data in this.diskSpaceTotalData)
                    {
                        token.ThrowIfCancellationRequested();
                        this.ProcessResourceDataReportHealth(
                            data,
                            0,
                            0,
                            timeToLiveWarning);
                    }
                }

                token.ThrowIfCancellationRequested();

                // This section only needs to run if you have the FabricObserverWebApi app installed.
                if (!ObserverManager.ObserverWebAppDeployed)
                {
                    return Task.CompletedTask;
                }

                var diskInfoPath = Path.Combine(this.ObserverLogger.LogFolderBasePath, "disks.txt");

                _ = this.ObserverLogger.TryWriteLogFile(diskInfoPath, this.diskInfo.ToString());

                _ = this.diskInfo.Clear();

                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                if (!(e is OperationCanceledException))
                {
                    this.HealthReporter.ReportFabricObserverServiceHealth(
                            this.FabricServiceContext.ServiceName.OriginalString,
                            this.ObserverName,
                            HealthState.Error,
                            $"Unhandled exception processing Disk information:{Environment.NewLine}{e}");
                }

                throw;
            }
        }

        private void SetErrorWarningThresholds()
        {
            try
            {
                if (int.TryParse(
                    this.GetSettingParameterValue(
                    this.ConfigurationSectionName,
                    ObserverConstants.DiskObserverDiskSpacePercentError), out int diskUsedError))
                {
                    this.DiskSpacePercentErrorThreshold = diskUsedError;
                }

                if (int.TryParse(
                    this.GetSettingParameterValue(
                    this.ConfigurationSectionName,
                    ObserverConstants.DiskObserverDiskSpacePercentWarning), out int diskUsedWarning))
                {
                    this.DiskSpacePercentWarningThreshold = diskUsedWarning;
                }

                if (int.TryParse(
                    this.GetSettingParameterValue(
                    this.ConfigurationSectionName,
                    ObserverConstants.DiskObserverAverageQueueLengthError), out int diskCurrentQueueLengthError))
                {
                    this.AverageQueueLengthErrorThreshold = diskCurrentQueueLengthError;
                }

                if (int.TryParse(
                    this.GetSettingParameterValue(
                    this.ConfigurationSectionName,
                    ObserverConstants.DiskObserverAverageQueueLengthWarning), out int diskCurrentQueueLengthWarning))
                {
                    this.AverageQueueLengthWarningThreshold = diskCurrentQueueLengthWarning;
                }
            }
            catch (ArgumentNullException)
            {
            }
            catch (FormatException)
            {
            }
        }

        private string GetWindowsPerfCounterDetailsText(
            ICollection<float> data,
            string counter)
        {
            if (data == null || data.Count == 0)
            {
                return null;
            }

            var sb = new StringBuilder();
            string ret;
            string unit = "ms";

            if (counter.Contains("Queue"))
            {
                unit = string.Empty;
            }

            _ = sb.AppendFormat("  {0}: {1}", counter, Math.Round(data.Average(), 3) + $" {unit}" + Environment.NewLine);

            ret = sb.ToString();
            _ = sb.Clear();

            return ret;
        }
    }
}