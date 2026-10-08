using System;
using System.IO;
using System.Text;

namespace BMWBenchmarkLoader
{
    public class Program
    {
        public static int Main(string[] args)
        {
            var opts = Args.Parse(args);
            Console.OutputEncoding = Encoding.UTF8;
            var workDir = Path.Combine(Path.GetTempPath(), "bmwbench_loader");
            Directory.CreateDirectory(workDir);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

            if (opts.ApplyProfile != null)
            {
                var c2 = new GameConfig();
                c2.Locate();
                c2.Load();
                var runner2 = new GameRunner(c2, workDir);
                runner2.KillGame();
                if (opts.ApplyProfile == "cpu") c2.ApplyCpuProfile(); else c2.ApplyGpuProfile();
                c2.Save();
                Console.WriteLine("Профиль применён: " + opts.ApplyProfile);
                Console.WriteLine(c2.DescribeProfile(opts.ApplyProfile));
                return 0;
            }

            if (opts.OcrTestFile != null)
            {
                int tw, th; ScreenCaptor.GetSize(opts.OcrTestFile, out tw, out th);
                var r = BenchResult.Parse(OcrRunner.RunWords(opts.OcrTestFile, workDir), tw, th, opts.OcrTestFile, workDir);
                Console.WriteLine(r.RawOcr);
                Console.WriteLine();
                Console.WriteLine("--- parsed ---");
                Console.WriteLine(r.Format("OCR-тест"));
                Console.WriteLine("IsValid: " + r.IsValid);
                return 0;
            }

            Console.WriteLine("==========================================================");
            Console.WriteLine("  BMW Benchmark Loader  -  Black Myth: Wukong Benchmark");
            Console.WriteLine("  Автоматический прогон CPU- и GPU-тестов");
            Console.WriteLine("==========================================================");
            Console.WriteLine();

            GameConfig cfg = null;
            try
            {
                Console.WriteLine("[1/6] Поиск Steam и Benchmark Tool...");
                cfg = new GameConfig();
                cfg.Locate();
                cfg.Load();
                Console.WriteLine("      Steam:     " + cfg.SteamPath);
                Console.WriteLine("      Игра:      " + cfg.GameInstallDir);
                Console.WriteLine();

                Console.WriteLine("[2/6] Сбор характеристик ПК...");
                var specs = SystemSpecs.Collect();
                Console.WriteLine(specs.Format());
                Console.WriteLine();

                BenchResult cpuResult = null;
                BenchResult gpuResult = null;
                string cpuDesc = null;
                string gpuDesc = null;
                string cpuError = null;
                string gpuError = null;

                var runner = new GameRunner(cfg, workDir);

                if (!opts.GpuOnly)
                {
                    Console.WriteLine("[3/6] CPU-тест: применение настроек (нагрузка на CPU, минимум GPU)...");
                    try
                    {
                        runner.KillGame();
                        cfg.ApplyCpuProfile();
                        cfg.Save();
                        cpuDesc = cfg.DescribeProfile("CPU-тест");
                        Console.WriteLine(cpuDesc);
                        runner.RunBenchmarkOnce("CPU", "cpu");
                        var lastPng = Path.Combine(workDir, "cpu_results.png");
                        if (File.Exists(lastPng))
                        {
                            int cw, ch; ScreenCaptor.GetSize(lastPng, out cw, out ch);
                            cpuResult = BenchResult.Parse(OcrRunner.RunWords(lastPng, workDir), cw, ch, lastPng, workDir);
                            Console.WriteLine(cpuResult.Format("CPU-тест — результат"));
                            if (!cpuResult.IsValid)
                            {
                                Console.WriteLine("  [!] CPU: не все поля распознаны. Сырой OCR:");
                                Console.WriteLine(cpuResult.RawOcr);
                            }
                        }
                        else
                        {
                            cpuError = "файл cpu_results.png не создан";
                            Console.WriteLine("  [!] " + cpuError);
                        }
                    }
                    catch (Exception ex)
                    {
                        cpuError = ex.Message;
                        Console.WriteLine("  [!] CPU-тест ошибка: " + ex.Message);
                    }
                    Console.WriteLine();
                }

                if (!opts.CpuOnly)
                {
                    Console.WriteLine("[4/6] GPU-тест: применение настроек (максимальная нагрузка на GPU)...");
                    try
                    {
                        runner.KillGame();
                        cfg.ApplyGpuProfile();
                        cfg.Save();
                        gpuDesc = cfg.DescribeProfile("GPU-тест");
                        Console.WriteLine(gpuDesc);
                        runner.RunBenchmarkOnce("GPU", "gpu");
                        var lastPng = Path.Combine(workDir, "gpu_results.png");
                        if (File.Exists(lastPng))
                        {
                            int gw, gh; ScreenCaptor.GetSize(lastPng, out gw, out gh);
                            gpuResult = BenchResult.Parse(OcrRunner.RunWords(lastPng, workDir), gw, gh, lastPng, workDir);
                            Console.WriteLine(gpuResult.Format("GPU-тест — результат"));
                            if (!gpuResult.IsValid)
                            {
                                Console.WriteLine("  [!] GPU: не все поля распознаны. Сырой OCR:");
                                Console.WriteLine(gpuResult.RawOcr);
                            }
                        }
                        else
                        {
                            gpuError = "файл gpu_results.png не создан";
                            Console.WriteLine("  [!] " + gpuError);
                        }
                    }
                    catch (Exception ex)
                    {
                        gpuError = ex.Message;
                        Console.WriteLine("  [!] GPU-тест ошибка: " + ex.Message);
                    }
                    Console.WriteLine();
                }

                Console.WriteLine("[5/6] Восстановление исходных настроек...");
                try { runner.KillGame(); } catch { }
                try { cfg.RestoreOriginal(); } catch { }
                Console.WriteLine();

                Console.WriteLine("[6/6] Отчёт...");
                var reportPath = opts.OutPath ?? Path.Combine(Directory.GetCurrentDirectory(), "BMWbenchmark_report_" + stamp + ".txt");
                var report = BuildReport(specs, cpuResult, gpuResult, cpuDesc, gpuDesc, stamp, cpuError, gpuError);
                File.WriteAllText(reportPath, report, new UTF8Encoding(false));
                Console.WriteLine(report);
                Console.WriteLine("Отчёт сохранён: " + reportPath);
                Console.WriteLine("Скриншоты: " + workDir);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("ОШИБКА: " + ex.Message);
                if (cfg != null)
                {
                    try { cfg.RestoreOriginal(); } catch { }
                }
                return 1;
            }
        }

        static string BuildReport(SystemSpecs specs, BenchResult cpu, BenchResult gpu,
            string cpuDesc, string gpuDesc, string stamp, string cpuError, string gpuError)
        {
            var sb = new StringBuilder();
            sb.AppendLine("======================================================================");
            sb.AppendLine("  BLACK MYTH: WUKONG BENCHMARK TOOL — ОТЧЁТ АВТОМАТИЧЕСКОГО ПРОГОНА");
            sb.AppendLine("  Дата: " + stamp);
            sb.AppendLine("======================================================================");
            sb.AppendLine();
            sb.AppendLine("--- ХАРАКТЕРИСТИКИ КОМПЬЮТЕРА ----------------------------------------");
            sb.AppendLine(specs.Format());
            sb.AppendLine();
            sb.AppendLine("--- РЕЗУЛЬТАТ CPU-ТЕСТА (нагрузка на процессор) ----------------------");
            if (cpu != null && cpu.IsValid) sb.AppendLine(cpu.Format("CPU"));
            else if (cpu != null)
            {
                sb.AppendLine(cpu.Format("CPU (частично)"));
                if (!string.IsNullOrEmpty(cpu.RawOcr))
                {
                    sb.AppendLine("  --- сырой OCR ---");
                    sb.AppendLine(cpu.RawOcr);
                }
            }
            else sb.AppendLine("  нет данных" + (cpuError != null ? " (" + cpuError + ")" : ""));
            sb.AppendLine();
            sb.AppendLine("--- РЕЗУЛЬТАТ GPU-ТЕСТА (максимальная нагрузка на видеокарту) --------");
            if (gpu != null && gpu.IsValid) sb.AppendLine(gpu.Format("GPU"));
            else if (gpu != null)
            {
                sb.AppendLine(gpu.Format("GPU (частично)"));
                if (!string.IsNullOrEmpty(gpu.RawOcr))
                {
                    sb.AppendLine("  --- сырой OCR ---");
                    sb.AppendLine(gpu.RawOcr);
                }
            }
            else sb.AppendLine("  нет данных" + (gpuError != null ? " (" + gpuError + ")" : ""));
            sb.AppendLine();
            sb.AppendLine("--- НАСТРОЙКИ CPU-ТЕСТА ----------------------------------------------");
            sb.AppendLine("  (минимальный рендер-скейл и GPU-эффекты, максимальная дальность");
            sb.AppendLine("   прорисовки и растительность — упор на CPU)");
            if (cpuDesc != null) sb.AppendLine(cpuDesc);
            sb.AppendLine();
            sb.AppendLine("--- НАСТРОЙКИ GPU-ТЕСТА ----------------------------------------------");
            sb.AppendLine("  (нативное разрешение, все уровни качества на максимум — упор на GPU)");
            if (gpuDesc != null) sb.AppendLine(gpuDesc);
            sb.AppendLine();
            sb.AppendLine("--- ПРИМЕЧАНИЕ -------------------------------------------------------");
            sb.AppendLine("  Показатели FPS получены распознаванием экрана результатов");
            sb.AppendLine("  Benchmark Tool (среднее/максимум/минимум/5-й перцентиль/VRAM).");
            sb.AppendLine("  Скриншоты сохранены во временной папке инструмента.");
            return sb.ToString();
        }
    }

    public class Args
    {
        public bool CpuOnly;
        public bool GpuOnly;
        public string OutPath;
        public string OcrTestFile;
        public string ApplyProfile;

        public static Args Parse(string[] args)
        {
            var a = new Args();
            foreach (var s in args)
            {
                var l = s.ToLowerInvariant();
                if (l == "--cpu-only") a.CpuOnly = true;
                else if (l == "--gpu-only") a.GpuOnly = true;
                else if (l.StartsWith("--out=")) a.OutPath = s.Substring(6);
                else if (l.StartsWith("--ocr-test=")) a.OcrTestFile = s.Substring(11);
                else if (l.StartsWith("--apply-profile=")) a.ApplyProfile = s.Substring(16);
            }
            return a;
        }
    }
}