using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace BMWBenchmarkLoader
{
    public class GameRunner
    {
        readonly GameConfig _cfg;
        readonly string _workDir;

        public GameRunner(GameConfig cfg, string workDir)
        {
            _cfg = cfg;
            _workDir = workDir;
        }

        public void KillGame()
        {
            foreach (var name in new[] { "b1-Win64-Shipping", "b1_benchmark" })
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { p.Kill(); p.WaitForExit(20000); } catch { }
                }
            }
            Thread.Sleep(3000);
            // убрать лок одиночного инстанса, оставшийся после убийства
            try
            {
                var lockFile = Path.Combine(_cfg.GameInstallDir, "b1", "Saved", "PersistentDownloadDir", "GameSingleton.lock");
                if (File.Exists(lockFile)) File.Delete(lockFile);
            }
            catch { }
        }

        public void RunBenchmarkOnce(string passName, string shotPrefix)
        {
            while (true)
            {
                Console.WriteLine("[" + passName + "] Запуск бенчмарка...");
                LaunchGame(passName == "CPU");
                Console.WriteLine("[" + passName + "] Ожидание главного меню...");
                WaitMainMenu();
                Console.WriteLine("[" + passName + "] Открытие теста быстродействия...");
                ClickMenuBench();
                Console.WriteLine("[" + passName + "] Подтверждение запуска...");
                WaitConfirmDialogAndClick();

                Console.WriteLine("[" + passName + "] Бенчмарк выполняется, ожидание экрана результатов...");
                var shot = WaitResultsScreen(shotPrefix);
                if (shot != null)
                {
                    Console.WriteLine("[" + passName + "] Экран результатов получен: " + Path.GetFileName(shot));
                    return;
                }
                Console.WriteLine("[" + passName + "] Игра завершилась досрочно, повторный запуск...");
                KillGame();
            }
        }

        void LaunchGame(bool highPriority)
        {
            KillGame();
            // Steam должен работать; если нет — запускаем и ждём
            if (Process.GetProcessesByName("steam").Length == 0)
            {
                Process.Start(new ProcessStartInfo { FileName = Path.Combine(_cfg.SteamPath, "steam.exe"), UseShellExecute = true });
                var dl = DateTime.UtcNow.AddSeconds(60);
                while (DateTime.UtcNow < dl && Process.GetProcessesByName("steam").Length == 0) Thread.Sleep(2000);
            }
            var steamExe = Path.Combine(_cfg.SteamPath, "steam.exe");
            // -novid пропускает вступительную катсцену, -useallavailablecores задаействует все ядра,
            // -high повышает приоритет процесса (усиливает CPU-нагрузку в CPU-тесте)
            var gameArgs = "-applaunch 3132990 -novid -useallavailablecores" + (highPriority ? " -high" : "");
            var deadline = DateTime.UtcNow.AddMinutes(5);
            bool retried = false;
            while (DateTime.UtcNow < deadline)
            {
                Process.Start(new ProcessStartInfo { FileName = steamExe, Arguments = gameArgs, UseShellExecute = true });
                var until = DateTime.UtcNow.AddSeconds(retried ? 90 : 45);
                while (DateTime.UtcNow < until)
                {
                    Thread.Sleep(3000);
                    if (Process.GetProcessesByName("b1-Win64-Shipping").Length > 0) return;
                }
                if (!retried) retried = true;
            }
            throw new Exception("Процесс b1-Win64-Shipping не запустился за 5 минут");
        }

        void WaitMainMenu()
        {
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(5000);
                var hwnd = UiDriver.GetGameWindow("b1-Win64-Shipping");
                if (hwnd == IntPtr.Zero) continue;
                try
                {
                    var png = ScreenCaptor.Capture(hwnd, Path.Combine(_workDir, "probe_menu.png"));
                    var text = OcrRunner.WordsToText(OcrRunner.RunWords(png, _workDir));
                    if (text.Contains("Тест быстродействия") || text.Contains("быстродействия"))
                        return;
                }
                catch { }
            }
            throw new Exception("Главное меню не обнаружено за 3 минуты");
        }

        void ClickMenuBench()
        {
            var hwnd = UiDriver.GetGameWindow("b1-Win64-Shipping");
            if (hwnd == IntPtr.Zero) throw new Exception("Окно игры не найдено");
            UiDriver.Click(hwnd, UiDriver.BaseMenuBenchX, UiDriver.BaseMenuBenchY);
        }

        void WaitConfirmDialogAndClick()
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            bool clicked = false;
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(1500);
                var hwnd = UiDriver.GetGameWindow("b1-Win64-Shipping");
                if (hwnd == IntPtr.Zero) continue;
                try
                {
                    var png = ScreenCaptor.Capture(hwnd, Path.Combine(_workDir, "probe_confirm.png"));
                    var text = OcrRunner.WordsToText(OcrRunner.RunWords(png, _workDir));
                    if (text.Contains("Хотите запустить"))
                    {
                        UiDriver.Click(hwnd, UiDriver.BaseConfirmX, UiDriver.BaseConfirmY);
                        clicked = true;
                        break;
                    }
                }
                catch { }
            }
            if (!clicked)
            {
                // возможно диалог не появился (уже запускается) — кликнем всё равно по центру кнопки
                var hwnd = UiDriver.GetGameWindow("b1-Win64-Shipping");
                if (hwnd != IntPtr.Zero)
                    UiDriver.Click(hwnd, UiDriver.BaseConfirmX, UiDriver.BaseConfirmY);
            }
            Thread.Sleep(2000);
        }

string WaitResultsScreen(string shotPrefix)
        {
            // первые 60 секунд — загрузка уровня/катсцена, результаты не могут появиться
            Thread.Sleep(60000);
            var deadline = DateTime.UtcNow.AddMinutes(20);
            int stableHits = 0;
            int probeCount = 0;
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(8000);
                var hwnd = UiDriver.GetGameWindow("b1-Win64-Shipping");
                if (hwnd == IntPtr.Zero) continue;
                try
                {
                    probeCount++;
                    var png = ScreenCaptor.Capture(hwnd, Path.Combine(_workDir, "probe_result.png"));
                    var words = OcrRunner.RunWords(png, _workDir);
                    var text = OcrRunner.WordsToText(words);
                    Console.WriteLine("  [probe" + probeCount + "] OCR: " + text);
                    // Логируем прогресс каждые 5 проб
                    if (probeCount % 5 == 0)
                    {
                        var preview = text.Replace("\n", " | ").Substring(0, Math.Min(200, text.Length));
                        Console.WriteLine("  [probe " + probeCount + "] OCR: " + preview);
                    }
                    
                    if (text.Contains("Результаты теста") || text.Contains("В среднем") || text.Contains("FPS") || text.Contains("фпс"))
                    {
                        stableHits++;
                        Console.WriteLine("  [probe " + probeCount + "] Результаты обнаружены (hit " + stableHits + ")");
                        if (stableHits >= 2)
                        {
                            // финальный скриншот результатов
                            return ScreenCaptor.Capture(hwnd, Path.Combine(_workDir, shotPrefix + "_results.png"));
                        }
                    }
                    else stableHits = 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  [probe " + probeCount + "] Ошибка OCR: " + ex.Message);
                }
            }
            throw new Exception("Экран результатов не обнаружен за 20 минут");
        }
                    }
                    else stableHits = 0;
                }
                catch { }
            }
            throw new Exception("Экран результатов не обнаружен за 10 минут");
        }
    }
}
