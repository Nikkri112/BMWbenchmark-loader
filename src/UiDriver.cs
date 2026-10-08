using System;
using System.Runtime.InteropServices;

namespace BMWBenchmarkLoader
{
    public static class UiDriver
    {
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);

        struct RECT { public int L, T, R, B; }

        // Базовые координаты UI в макете 1920x1080
        public const int BaseMenuBenchX = 150, BaseMenuBenchY = 485;   // "Тест быстродействия"
        public const int BaseConfirmX = 755, BaseConfirmY = 634;       // "Подтвердить"
        public const int BaseMenuExitX = 100, BaseMenuExitY = 653;     // "Выход" (главное меню)
        public const int BaseResExitX = 1786, BaseResExitY = 1029;     // "Выход" (экран результатов)

        public static IntPtr GetGameWindow(string procName)
        {
            var procs = System.Diagnostics.Process.GetProcessesByName(procName);
            foreach (var p in procs)
            {
                if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
            }
            return IntPtr.Zero;
        }

        public static void Click(IntPtr hwnd, int baseX, int baseY)
        {
            RECT r;
            if (!GetWindowRect(hwnd, out r)) throw new Exception("GetWindowRect failed");
            int w = r.R - r.L, h = r.B - r.T;
            int ax = r.L + baseX * w / 1920;
            int ay = r.T + baseY * h / 1080;
            SetForegroundWindow(hwnd);
            System.Threading.Thread.Sleep(250);
            SetCursorPos(ax, ay);
            System.Threading.Thread.Sleep(150);
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(60);
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            Console.WriteLine("    [ui] click " + baseX + "," + baseY + " -> screen " + ax + "," + ay);
        }
    }
}
