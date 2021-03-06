﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FabricObserver.Observers.Utilities
{
    public class LinuxInfoProvider : OperatingSystemInfoProvider
    {
        public override (long TotalMemory, int PercentInUse) TupleGetTotalPhysicalMemorySizeAndPercentInUse()
        {
            Dictionary<string, ulong> memInfo = LinuxProcFS.ReadMemInfo();

            long totalMemory = (long)memInfo[MemInfoConstants.MemTotal];
            long freeMem = (long)memInfo[MemInfoConstants.MemFree];
            long availableMem = (long)memInfo[MemInfoConstants.MemAvailable];

            // Divide by 1048576 to convert total memory from KB to GB.
            return (totalMemory / 1048576, (int)(((double)(totalMemory - availableMem - freeMem)) / totalMemory * 100));
        }

        public override int GetActivePortCount(int processId = -1, ServiceContext context = null)
        {
            int count = GetPortCount(processId, predicate: (line) => true, context);
            return count;
        }

        public override int GetActiveEphemeralPortCount(int processId = -1, ServiceContext context = null)
        {
            (int lowPort, int highPort) = TupleGetDynamicPortRange();

            int count = GetPortCount(processId, (line) =>
                        {
                            int port = GetPortFromNetstatOutput(line);
                            return port >= lowPort && port <= highPort;
                        }, context);

            return count;
        }

        public override (int LowPort, int HighPort) TupleGetDynamicPortRange()
        {
            string text = File.ReadAllText("/proc/sys/net/ipv4/ip_local_port_range");
            int tabIndex = text.IndexOf('\t');
            return (LowPort: int.Parse(text.Substring(0, tabIndex)), HighPort: int.Parse(text.Substring(tabIndex + 1)));
        }

        public override async Task<OSInfo> GetOSInfoAsync(CancellationToken cancellationToken)
        {
            OSInfo osInfo = default(OSInfo);
            (int exitCode, List<string> outputLines) = await ExecuteProcessAsync("lsb_release", "-d");

            if (exitCode == 0 && outputLines.Count == 1)
            {
                /*
                ** Example:
                ** Description:\tUbuntu 18.04.2 LTS
                */
                osInfo.Name = outputLines[0].Split(new char[] { ':' }, 2)[1].Trim();
            }

            osInfo.Version = File.ReadAllText("/proc/version");

            osInfo.Language = string.Empty;
            osInfo.Status = "OK";
            osInfo.NumberOfProcesses = Process.GetProcesses().Length;

            // Source code: https://git.kernel.org/pub/scm/linux/kernel/git/torvalds/linux.git/tree/fs/proc/meminfo.c
            Dictionary<string, ulong> memInfo = LinuxProcFS.ReadMemInfo();
            osInfo.TotalVisibleMemorySizeKB = memInfo[MemInfoConstants.MemTotal];
            osInfo.FreePhysicalMemoryKB = memInfo[MemInfoConstants.MemFree];
            osInfo.AvailableMemoryKB = memInfo[MemInfoConstants.MemAvailable];

            // On Windows, TotalVirtualMemorySize = TotalVisibleMemorySize + SizeStoredInPagingFiles.
            // SizeStoredInPagingFiles - Total number of kilobytes that can be stored in the operating system paging files—0 (zero)
            // indicates that there are no paging files. Be aware that this number does not represent the actual physical size of the paging file on disk.
            // https://docs.microsoft.com/en-us/windows/win32/cimwin32prov/win32-operatingsystem
            osInfo.TotalVirtualMemorySizeKB = osInfo.TotalVisibleMemorySizeKB + memInfo[MemInfoConstants.SwapTotal];

            osInfo.FreeVirtualMemoryKB = osInfo.FreePhysicalMemoryKB + memInfo[MemInfoConstants.SwapFree];

            (float uptime, float idleTime) = await LinuxProcFS.ReadUptimeAsync();

            osInfo.LastBootUpTime = DateTime.UtcNow.AddSeconds(-uptime).ToString("o");

            try
            {
                osInfo.InstallDate = new DirectoryInfo("/var/log/installer").CreationTimeUtc.ToString("o");
            }
            catch (IOException)
            {
                osInfo.InstallDate = "N/A";
            }

            return osInfo;
        }

        public static int GetPortCount(int processId, Predicate<string> predicate, ServiceContext context = null)
        {
            string processIdStr = processId == -1 ? string.Empty : " " + processId.ToString() + "/";

            /*
            ** -t - tcp
            ** -p - display PID/Program name for sockets
            ** -n - don't resolve names
            ** -a - display all sockets (default: connected)
            */
            string arg = "-tna";
            string bin = "netstat";

            if (processId > -1)
            {
                if (context == null)
                {
                    return -1;
                }

                // We need the full path to the currently deployed FO CodePackage, which is were our 
                // proxy binary lives, which is used for elevated netstat call.
                string path = context.CodePackageActivationContext.GetCodePackageObject("Code").Path;
                arg = string.Empty;

                // This is a proxy binary that uses Capabilites to run netstat -tpna with elevated privilege.
                // FO runs as sfappsuser (SF default, Linux normal user), which can't run netstat -tpna. 
                // During deployment, a setup script is run (as root user)
                // that adds capabilities to elevated_netstat program, which will *only* run (execv) "netstat -tpna".
                bin = $"{path}/elevated_netstat";
            }

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                Arguments = arg,
                FileName = bin,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
            };

            int count = 0;
            string line;
            using (Process process = Process.Start(startInfo))
            {
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    if (!line.StartsWith("tcp ", StringComparison.Ordinal | StringComparison.OrdinalIgnoreCase))
                    {
                        // skip headers
                        continue;
                    }

                    if (processId != -1 && !line.Contains(processIdStr))
                    {
                        continue;
                    }

                    if (!predicate(line))
                    {
                        continue;
                    }

                    ++count;
                }

                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    return -1;
                }
            }

            return count;
        }

        public static int GetPortFromNetstatOutput(string line)
        {
            /*
            ** Example:
            ** tcp        0      0 0.0.0.0:19080           0.0.0.0:*               LISTEN      13422/FabricGateway
            */

            int colonIndex = line.IndexOf(':');

            if (colonIndex >= 0)
            {
                int spaceIndex = line.IndexOf(' ', startIndex: colonIndex + 1);

                if (spaceIndex >= 0)
                {
                    return int.Parse(line.Substring(colonIndex + 1, spaceIndex - colonIndex - 1));
                }
            }

            return -1;
        }

        public async Task<(int ExitCode, List<string> Output)> ExecuteProcessAsync(string fileName, string arguments)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                Arguments = arguments,
                FileName = fileName,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
            };

            List<string> output = new List<string>();

            using (Process process = Process.Start(startInfo))
            {
                string line;

                while ((line = await process.StandardOutput.ReadLineAsync()) != null)
                {
                    output.Add(line);
                }

                process.WaitForExit();

                return (process.ExitCode, output);
            }
        }
    }
}
