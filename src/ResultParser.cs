using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BMWBenchmarkLoader
{
    public class BenchResult
    {
        public double? AvgFps;
        public double? MaxFps;
        public double? MinFps;
        public double? Low95Fps;
        public double? VramUsedGb;
        public string RawOcr;
        public string DateText;

        public bool IsValid { get { return AvgFps.HasValue && MaxFps.HasValue; } }

        static readonly Regex NumRx = new Regex("^(\\d+(?:[.,]\\d+)?)");

        // Базовые зоны (макет 1920x1080), скриншот масштабируется
        static readonly int[][] Regions = new int[][]
        {
            new[] { 60, 340, 560, 445 },    // 0: среднее (большая цифра)
            new[] { 60, 445, 330, 520 },    // 1: максимум
            new[] { 330, 445, 620, 520 },   // 2: минимум
            new[] { 60, 520, 620, 600 },    // 3: 5-й перцентиль
            new[] { 60, 600, 540, 690 }     // 4: используемая видеопамять
        };

        public static BenchResult Parse(List<OcrWord> words, int imgW, int imgH)
        {
            var r = new BenchResult();
            if (words == null || words.Count == 0) return r;
            var raw = new System.Text.StringBuilder();
            foreach (var w in words) raw.AppendLine(w.Text);
            r.RawOcr = raw.ToString();

            // масштаб скриншота относительно базового макета 1920x1080
            double sx = imgW / 1920.0, sy = imgH / 1080.0;
            if (sx <= 0 || sx > 4) sx = 1;
            if (sy <= 0 || sy > 4) sy = 1;

            // дата — сразу под заголовком "Результаты теста"
            foreach (var w in words)
            {
                if (w.Text.Contains("окт.") || w.Text.Contains("янв.") || w.Text.Contains("февр.") ||
                    w.Text.Contains("мар.") || w.Text.Contains("апр.") || w.Text.Contains("мая") ||
                    w.Text.Contains("июн") || w.Text.Contains("июл") || w.Text.Contains("авг.") ||
                    w.Text.Contains("сент.") || w.Text.Contains("нояб.") || w.Text.Contains("дек."))
                {
                    r.DateText = w.Text;
                    break;
                }
            }

            // зоны: 0=среднее (большая цифра), 1=максимум, 2=минимум, 3=5-й перцентиль, 4=VRAM
            for (int i = 0; i < Regions.Length; i++)
            {
                var reg = Regions[i];
                double x1 = reg[0] * sx, y1 = reg[1] * sy, x2 = reg[2] * sx, y2 = reg[3] * sy;
                double? best = null;
                double bestH = 0;
                bool bestWordsComma = false;
                foreach (var w in words)
                {
                    var cx = w.X + w.W / 2;
                    var cy = w.Y + w.H / 2;
                    if (cx < x1 || cx > x2 || cy < y1 || cy > y2) continue;
                    var m = NumRx.Match(w.Text);
                    if (!m.Success) continue;
                    var tail = w.Text.Substring(m.Groups[1].Value.Length).ToLowerInvariant();
                    if (i == 0)
                    {
                        // большая цифра — стилизованный шрифт, OCR может прилипить мусор ("126ws"):
                        // допускаем любой хвост, не начинающийся с кириллицы
                        if (tail.Length > 0 && tail[0] >= 'а' && tail[0] <= 'я') continue;
                    }
                    else
                    {
                        // отсекаем слова-подписи ("5-й", "перцентиль") — только числа/число+FPS/число+Гб
                        if (tail.Length > 0 && tail != "fps" && tail != "гб" && tail != "гб." && tail != "gb") continue;
                    }
                    var v = ParseNum(m.Groups[1].Value);
                    if (!v.HasValue) continue;
                    if (i == 0)
                    {
                        if (w.H > bestH) { best = v; bestH = w.H; }
                    }
                    else if (i == 4)
                    {
                        // VRAM: предпочитаем слово с десятичной запятой ("4,2 Гб") вместо оси графика
                        bool hasComma = w.Text.Contains(",");
                        bool curComma = best.HasValue && bestH > 0 && false;
                        if (!best.HasValue || (hasComma && !bestWordsComma)) { best = v; bestH = w.H; bestWordsComma = hasComma; }
                    }
                    else if (!best.HasValue) { best = v; bestH = w.H; }
                }
                switch (i)
                {
                    case 0: r.AvgFps = best; break;
                    case 1: r.MaxFps = best; break;
                    case 2: r.MinFps = best; break;
                    case 3: r.Low95Fps = best; break;
                    case 4: r.VramUsedGb = best; break;
                }
            }
            return r;
        }

        static double? ParseNum(string s)
        {
            if (s == null) return null;
            double d;
            s = s.Replace(',', '.');
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return null;
        }

        public string Format(string title)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("  [" + title + "]");
            sb.AppendLine("  В среднем (FPS):          " + F(AvgFps));
            sb.AppendLine("  Максимум (FPS):           " + F(MaxFps));
            sb.AppendLine("  Минимум (FPS):            " + F(MinFps));
            sb.AppendLine("  5-й перцентиль (FPS):     " + F(Low95Fps));
            sb.AppendLine("  Исп. видеопамять (ГБ):    " + F(VramUsedGb));
            return sb.ToString();
        }

        static string F(double? v) { return v.HasValue ? v.Value.ToString("0.##") : "n/a"; }
    }
}
