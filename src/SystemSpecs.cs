using System;
using System.Collections.Generic;
using System.Management;
using System.Text;

namespace BMWBenchmarkLoader
{
    public class SystemSpecs
    {
        public string CpuName;
        public int CpuCores;
        public int CpuThreads;
        public string GpuName;
        public string GpuDriver;
        public ulong GpuVramMb;
        public ulong RamTotalMb;
        public string RamSpeed;
        public string OsName;
        public string OsVersion;
        public string ScreenResolution;

        public static SystemSpecs Collect()
        {
            var s = new SystemSpecs();
            try
            {
                foreach (var o in new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor").Get())
                {
                    s.CpuName = "" + o["Name"];
                    s.CpuCores = ToInt(o["NumberOfCores"], 0);
                    s.CpuThreads = ToInt(o["NumberOfLogicalProcessors"], 0);
                    break;
                }
            }
            catch { s.CpuName = "n/a"; }

            try
            {
                foreach (var o in new ManagementObjectSearcher("SELECT Name, DriverVersion, AdapterRAM FROM Win32_VideoController WHERE AdapterRAM IS NOT NULL").Get())
                {
                    s.GpuName = "" + o["Name"];
                    s.GpuDriver = "" + o["DriverVersion"];
                    s.GpuVramMb = ToUlong(o["AdapterRAM"], 0) / (1024 * 1024);
                    break;
                }
            }
            catch { s.GpuName = s.GpuName ?? "n/a"; }

            try
            {
                foreach (var o in new ManagementObjectSearcher("SELECT Name, Capacity, Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory").Get())
                {
                    s.RamTotalMb += ToUlong(o["Capacity"], 0) / (1024 * 1024);
                    s.RamSpeed = "" + (o["ConfiguredClockSpeed"] ?? o["Speed"] ?? "n/a");
                }
            }
            catch { }

            try
            {
                foreach (var o in new ManagementObjectSearcher("SELECT Caption, Version, OSArchitecture FROM Win32_OperatingSystem").Get())
                {
                    s.OsName = "" + o["Caption"];
                    s.OsVersion = "" + o["Version"];
                    break;
                }
            }
            catch { s.OsName = "n/a"; }

            try
            {
                foreach (var o in new ManagementObjectSearcher("SELECT CurrentHorizontalResolution, CurrentVerticalResolution FROM Win32_VideoController WHERE CurrentHorizontalResolution IS NOT NULL").Get())
                {
                    s.ScreenResolution = ToInt(o["CurrentHorizontalResolution"], 0) + " x " + ToInt(o["CurrentVerticalResolution"], 0);
                    break;
                }
            }
            catch { }
            return s;
        }

        static int ToInt(object v, int def)
        {
            try { return Convert.ToInt32(v); } catch { return def; }
        }

        static ulong ToUlong(object v, ulong def)
        {
            try { return Convert.ToUInt64(v); } catch { return def; }
        }

        public string Format()
        {
            var sb = new StringBuilder();
            sb.AppendLine("  Процессор:            " + CpuName + (CpuCores > 0 ? "  (" + CpuCores + " ядер / " + CpuThreads + " потоков)" : ""));
            sb.AppendLine("  Видеокарта:           " + GpuName + (GpuVramMb > 0 ? "  (" + GpuVramMb + " МБ)" : ""));
            sb.AppendLine("  Драйвер GPU:          " + (string.IsNullOrEmpty(GpuDriver) ? "n/a" : GpuDriver));
            sb.AppendLine("  Оперативная память:   " + (RamTotalMb > 0 ? (RamTotalMb / 1024) + " ГБ" + (string.IsNullOrEmpty(RamSpeed) ? "" : "  @" + RamSpeed + " МГц") : "n/a"));
            sb.AppendLine("  ОС:                   " + OsName + "  (build " + OsVersion + ")");
            sb.AppendLine("  Разрешение монитора:  " + (string.IsNullOrEmpty(ScreenResolution) ? "n/a" : ScreenResolution));
            return sb.ToString();
        }
    }
}
