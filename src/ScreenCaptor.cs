using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace BMWBenchmarkLoader
{
    public static class ScreenCaptor
    {
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        struct RECT { public int L, T, R, B; }

        public static string Capture(IntPtr hwnd, string file)
        {
            SetForegroundWindow(hwnd);
            System.Threading.Thread.Sleep(300);
            RECT r;
            if (!GetWindowRect(hwnd, out r)) throw new Exception("GetWindowRect failed");
            int w = r.R - r.L, h = r.B - r.T;
            if (w <= 0 || h <= 0) throw new Exception("пустой прямоугольник окна");
            using (var bmp = new Bitmap(w, h))
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(r.L, r.T, 0, 0, new Size(w, h));
                bmp.Save(file, ImageFormat.Png);
            }
            return file;
        }

        public static void GetSize(string pngPath, out int w, out int h)
        {
            using (var bmp = new Bitmap(pngPath)) { w = bmp.Width; h = bmp.Height; }
        }
    }
}
