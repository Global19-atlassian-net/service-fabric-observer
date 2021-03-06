﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace FabricObserver.Observers.Utilities
{
    public abstract class CpuUtilizationProvider : IDisposable
    {
        protected CpuUtilizationProvider()
        {
        }

        public abstract Task<float> NextValueAsync();

        public void Dispose()
        {
            Dispose(disposing: true);
        }

        public static CpuUtilizationProvider Create()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return new WindowsCpuUtilizationProvider();
            }
            else
            {
                return new LinuxCpuUtilizationProvider();
            }
        }

        protected abstract void Dispose(bool disposing);
    }
}
