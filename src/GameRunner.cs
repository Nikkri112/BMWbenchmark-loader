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
            // -novid пропускает вступительную катсцену, -useallavailablecores задействует все ядра,
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
                    if (IsMainMenu(text))
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
                    if (Norm(text).Contains("хотитезапустить") || Norm(text).Contains("подтвердить") ||
                        Norm(text).Contains("wanttostart") || Norm(text).Contains("confirm"))
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
            int noWindowStreak = 0;

            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(8000);

                // игра упала / закрылась — выходим, чтобы RunBenchmarkOnce перезапустил
                if (Process.GetProcessesByName("b1-Win64-Shipping").Length == 0)
                {
                    noWindowStreak++;
                    Console.WriteLine("  [probe] процесс игры не найден (streak " + noWindowStreak + ")");
                    if (noWindowStreak >= 2)
                    {
                        Console.WriteLine("  [probe] игра завершилась досрочно");
                        return null;
                    }
                    continue;
                }
                noWindowStreak = 0;

                var hwnd = UiDriver.GetGameWindow("b1-Win64-Shipping");
                if (hwnd == IntPtr.Zero)
                {
                    Console.WriteLine("  [probe] окно игры не найдено");
                    continue;
                }

                try
                {
                    probeCount++;
                    var png = ScreenCaptor.Capture(hwnd, Path.Combine(_workDir, "probe_result.png"));
                    // сохраняем копию каждой пробы для отладки
                    try
                    {
                        File.Copy(png, Path.Combine(_workDir, "probe_result_" + probeCount + ".png"), true);
                    }
                    catch { }

                    var words = OcrRunner.RunWords(png, _workDir);
                    var text = OcrRunner.WordsToText(words);
                    var norm = Norm(text);

                    // короткий лог каждый раз
                    var oneLine = text.Replace("\r", " ").Replace("\n", " | ");
                    if (oneLine.Length > 180) oneLine = oneLine.Substring(0, 180) + "...";
                    Console.WriteLine("  [probe " + probeCount + "] OCR: " + oneLine);

                    if (IsResultsScreen(norm, text))
                    {
                        stableHits++;
                        Console.WriteLine("  [probe " + probeCount + "] Экран результатов обнаружен (hit " + stableHits + ")");
                        // одного уверенного попадания достаточно
                        if (stableHits >= 1)
                        {
                            return ScreenCaptor.Capture(hwnd, Path.Combine(_workDir, shotPrefix + "_results.png"));
                        }
                    }
                    else
                    {
                        stableHits = 0;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  [probe " + probeCount + "] Ошибка OCR: " + ex.Message);
                }
            }
            throw new Exception("Экран результатов не обнаружен за 20 минут");
        }

        // --- эвристики распознавания UI ---

        static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.ToLowerInvariant();
            // убираем пробелы, пунктуацию, типичные OCR-мусорные символы
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var c in s)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'а' && c <= 'я') || (c >= '0' && c <= '9') || c == 'ё')
                    sb.Append(c);
            }
            return sb.ToString();
        }

        static bool IsMainMenu(string text)
        {
            var n = Norm(text);
            return n.Contains("тестбыстродействия")
                || n.Contains("быстродействия")
                || n.Contains("performancetest")
                || n.Contains("benchmark")
                || n.Contains("настройки")
                || n.Contains("settings");
        }

        static bool IsResultsScreen(string norm, string raw)
        {
            // сильные маркеры заголовка / блоков результатов
            if (norm.Contains("результатытеста") || norm.Contains("результаттеста"))
                return true;
            if (norm.Contains("testresults") || norm.Contains("benchmarkresults"))
                return true;

            // «В среднем» / average
            if (norm.Contains("всреднем") || norm.Contains("average") || norm.Contains("avgfps"))
                return true;

            // несколько ключевых подписей сразу
            int hits = 0;
            if (norm.Contains("максимум") || norm.Contains("maximum") || norm.Contains("maxfps")) hits++;
            if (norm.Contains("минимум") || norm.Contains("minimum") || norm.Contains("minfps")) hits++;
            if (norm.Contains("перцентиль") || norm.Contains("percentile") || norm.Contains("5й")) hits++;
            if (norm.Contains("видеопамят") || norm.Contains("vram")) hits++;
            if (norm.Contains("fps") || norm.Contains("фпс")) hits++;
            if (hits >= 2) return true;

            // fallback: в сыром тексте явно есть FPS и хотя бы одна типичная подпись
            var lower = (raw ?? "").ToLowerInvariant();
            if ((lower.Contains("fps") || lower.Contains("фпс")) &&
                (lower.Contains("средн") || lower.Contains("макс") || lower.Contains("мин") || lower.Contains("avg")))
                return true;

            return false;
        }
    }
}
