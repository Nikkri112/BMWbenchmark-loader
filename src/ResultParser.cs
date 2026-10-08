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

        public bool IsValid
        {
            get
            {
                if (!MaxFps.HasValue) return false;
                return AvgFps.HasValue || MinFps.HasValue || Low95Fps.HasValue;
            }
        }

        static readonly Regex AnyNumRx = new Regex("(\\d+(?:[.,]\\d+)?)", RegexOptions.Compiled);

        // Зоны макета 1920x1080
        static readonly int[][] Regions = new int[][]
        {
            new[] { 50, 320, 280, 430 },
            new[] { 50, 420, 280, 505 },
            new[] { 290, 420, 500, 505 },
            new[] { 50, 505, 300, 575 },
            new[] { 50, 600, 280, 690 }
        };

        public static BenchResult Parse(List<OcrWord> words, int imgW, int imgH)
        {
            return Parse(words, imgW, imgH, null, null);
        }

        public static BenchResult Parse(List<OcrWord> words, int imgW, int imgH, string pngPath, string workDir)
        {
            var r = new BenchResult();
            if (words == null) words = new List<OcrWord>();

            var raw = new System.Text.StringBuilder();
            foreach (var w in words) raw.AppendLine(w.Text);
            r.RawOcr = raw.ToString();

            double sx = imgW / 1920.0, sy = imgH / 1080.0;
            if (sx <= 0 || sx > 4) sx = 1;
            if (sy <= 0 || sy > 4) sy = 1;

            foreach (var w in words)
            {
                var t = w.Text ?? "";
                if (t.Contains("окт") || t.Contains("янв") || t.Contains("февр") ||
                    t.Contains("мар") || t.Contains("апр") || t.Contains("мая") ||
                    t.Contains("июн") || t.Contains("июл") || t.Contains("авг") ||
                    t.Contains("сент") || t.Contains("нояб") || t.Contains("дек"))
                {
                    r.DateText = t;
                    break;
                }
            }

            var sorted = new List<OcrWord>(words);
            sorted.Sort((a, b) =>
            {
                int rowA = (int)(a.Y / 20), rowB = (int)(b.Y / 20);
                if (rowA != rowB) return rowA.CompareTo(rowB);
                return a.X.CompareTo(b.X);
            });

            ParseRegions(r, words, sx, sy);
            ParseByKeywords(r, sorted);

            if (pngPath != null && workDir != null && FileExists(pngPath))
            {
                try { SupplementalCropOcr(r, pngPath, workDir, sx, sy); }
                catch (Exception ex) { Console.WriteLine("  [ocr-crop] " + ex.Message); }
            }

            ParseUsedVram(r, sorted);
            Sanitize(r);
            return r;
        }

        static bool FileExists(string p)
        {
            try { return System.IO.File.Exists(p); } catch { return false; }
        }

        /// <summary>
        /// OCR: З→3, О/о→0, Б→6 и т.п. Только для коротких токенов (числа).
        /// </summary>
        static string FixOcrDigits(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            // не трогаем длинные слова
            if (s.Length > 8) return s;
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var c in s)
            {
                switch (c)
                {
                    case 'З': case 'з': sb.Append('3'); break;
                    case 'О': case 'о': sb.Append('0'); break;
                    case 'Б': case 'б': sb.Append('6'); break;
                    case 'I': case 'l': case '|': sb.Append('1'); break;
                    case 'S': sb.Append('5'); break;
                    case 'B': sb.Append('8'); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Достаёт число из OCR-токена, с починкой ЗО→30.
        /// </summary>
        static double? TryParseOcrNum(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var m = AnyNumRx.Match(text);
            if (m.Success)
            {
                var v = ParseNum(m.Groups[1].Value);
                if (v.HasValue) return v;
            }
            var fixedText = FixOcrDigits(text);
            if (fixedText == text) return null;
            m = AnyNumRx.Match(fixedText);
            if (m.Success) return ParseNum(m.Groups[1].Value);
            return null;
        }

        static bool LooksLikeFps(double v)
        {
            if (v < 1 || v > 500) return false;
            if (v >= 2000 && v <= 2099) return false;
            double frac = Math.Abs(v - Math.Truncate(v));
            if (frac > 0.05 && v < 10) return false; // 2,6 → VRAM
            return true;
        }

        static bool LooksLikeVram(double v)
        {
            return v >= 0.3 && v <= 32;
        }

        static void Sanitize(BenchResult r)
        {
            if (r.MinFps.HasValue && !LooksLikeFps(r.MinFps.Value))
            {
                if (!r.VramUsedGb.HasValue && LooksLikeVram(r.MinFps.Value))
                    r.VramUsedGb = r.MinFps;
                r.MinFps = null;
            }
            if (r.MaxFps.HasValue && !LooksLikeFps(r.MaxFps.Value)) r.MaxFps = null;
            if (r.AvgFps.HasValue && !LooksLikeFps(r.AvgFps.Value)) r.AvgFps = null;
            if (r.Low95Fps.HasValue && !LooksLikeFps(r.Low95Fps.Value)) r.Low95Fps = null;

            if (r.MinFps.HasValue && r.MaxFps.HasValue && r.MinFps > r.MaxFps)
                r.MinFps = null;

            // голый 4/6/8/12/16 — total VRAM карты
            if (r.VramUsedGb.HasValue)
            {
                double v = r.VramUsedGb.Value;
                double frac = Math.Abs(v - Math.Truncate(v));
                if (frac < 0.05 && (v == 4 || v == 6 || v == 8 || v == 12 || v == 16 || v == 24))
                    r.VramUsedGb = null;
            }
        }

        static void SupplementalCropOcr(BenchResult r, string pngPath, string workDir, double sx, double sy)
        {
            if (!r.AvgFps.HasValue)
            {
                int x = (int)(50 * sx), y = (int)(330 * sy), w = (int)(220 * sx), h = (int)(100 * sy);
                var cropWords = OcrRunner.RunWordsRegion(pngPath, workDir, x, y, w, h, 3, "avg");
                Console.WriteLine("  [ocr-crop avg] " + OcrRunner.WordsToText(cropWords).Replace("\n", " | ").Trim());
                double? best = null;
                double bestH = 0;
                foreach (var word in cropWords)
                {
                    var v = TryParseOcrNum(word.Text);
                    if (!v.HasValue || !LooksLikeFps(v.Value)) continue;
                    if (word.H > bestH) { best = v; bestH = word.H; }
                }
                if (best.HasValue) r.AvgFps = best;
            }

            if (!r.VramUsedGb.HasValue)
            {
                // несколько кропов/масштабов — цифра used VRAM часто стилизованная
                var attempts = new int[][]
                {
                    new[] { 40, 580, 280, 120, 3 }, // x,y,w,h,scale
                    new[] { 40, 580, 280, 120, 4 },
                    new[] { 60, 610, 200, 80, 4 },
                };
                double? withDec = null;
                for (int ai = 0; ai < attempts.Length; ai++)
                {
                    var a = attempts[ai];
                    int x = (int)(a[0] * sx), y = (int)(a[1] * sy), w = (int)(a[2] * sx), h = (int)(a[3] * sy);
                    string tag = "vram" + a[4] + (ai > 0 ? "b" + ai : "");
                    var cropWords = OcrRunner.RunWordsRegion(pngPath, workDir, x, y, w, h, a[4], tag);
                    Console.WriteLine("  [ocr-crop " + tag + "] " + OcrRunner.WordsToText(cropWords).Replace("\n", " | ").Trim());

                    foreach (var word in cropWords)
                    {
                        var text = word.Text ?? "";
                        var low = text.ToLowerInvariant();
                        var v = TryParseOcrNum(text);
                        if (!v.HasValue || !LooksLikeVram(v.Value)) continue;

                        bool hasDec = text.Contains(",") || text.Contains(".");
                        bool hasUnit = low.Contains("гб") || low.Contains("gb");
                        double frac = Math.Abs(v.Value - Math.Truncate(v.Value));
                        // оси графика 20/40/60/80
                        if (!hasDec && !hasUnit && frac < 0.05 &&
                            (v.Value == 10 || v.Value == 20 || v.Value == 30 || v.Value == 40 ||
                             v.Value == 60 || v.Value == 80 || v.Value == 100))
                            continue;

                        if (hasDec || hasUnit || frac > 0.05)
                        {
                            withDec = v;
                            break;
                        }
                    }
                    if (withDec.HasValue) break;

                    string joined = "";
                    foreach (var word in cropWords) joined += FixOcrDigits(word.Text ?? "") + " ";
                    var mx = Regex.Match(joined, @"(\d+[.,]\d+)\s*(гб|gb)?", RegexOptions.IgnoreCase);
                    if (mx.Success)
                    {
                        var v = ParseNum(mx.Groups[1].Value);
                        if (v.HasValue && LooksLikeVram(v.Value)) { withDec = v; break; }
                    }
                }
                if (withDec.HasValue) r.VramUsedGb = withDec;
            }
        }

        static void ParseRegions(BenchResult r, List<OcrWord> words, double sx, double sy)
        {
            for (int i = 0; i < 4; i++)
            {
                var reg = Regions[i];
                double x1 = reg[0] * sx, y1 = reg[1] * sy, x2 = reg[2] * sx, y2 = reg[3] * sy;
                double? best = null;
                double bestH = 0;

                foreach (var w in words)
                {
                    var cx = w.X + w.W / 2;
                    var cy = w.Y + w.H / 2;
                    if (cx < x1 || cx > x2 || cy < y1 || cy > y2) continue;

                    var text = w.Text ?? "";
                    var low = text.ToLowerInvariant();
                    if (low.Contains("гб") || low.Contains("gb")) continue;

                    var v = TryParseOcrNum(text);
                    if (!v.HasValue || !LooksLikeFps(v.Value)) continue;

                    // отсекаем подписи «5-й»
                    var tail = text;
                    var m0 = AnyNumRx.Match(FixOcrDigits(text));
                    if (m0.Success)
                        tail = text.Length > m0.Length ? text.Substring(Math.Min(text.Length, m0.Length)) : "";
                    tail = tail.ToLowerInvariant();
                    if (i != 0 && tail.Contains("й") && !tail.Contains("fps") && !tail.Contains("фпс"))
                        continue;

                    if (i == 0)
                    {
                        if (w.H > bestH) { best = v; bestH = w.H; }
                    }
                    else if (!best.HasValue || w.H > bestH)
                    {
                        best = v;
                        bestH = w.H;
                    }
                }

                switch (i)
                {
                    case 0: if (!r.AvgFps.HasValue) r.AvgFps = best; break;
                    case 1: if (!r.MaxFps.HasValue) r.MaxFps = best; break;
                    case 2: if (!r.MinFps.HasValue) r.MinFps = best; break;
                    case 3: if (!r.Low95Fps.HasValue) r.Low95Fps = best; break;
                }
            }
        }

        static void ParseByKeywords(BenchResult r, List<OcrWord> sorted)
        {
            if (!r.AvgFps.HasValue)
                r.AvgFps = NumberAfterKeyword(sorted, new[] { "среднем", "среднее", "average", "avg" }, true);
            if (!r.MaxFps.HasValue)
                r.MaxFps = NumberAfterKeyword(sorted, new[] { "максимум", "максим", "maximum" }, false);
            if (!r.MinFps.HasValue)
                r.MinFps = NumberAfterKeyword(sorted, new[] { "минимум", "миним", "minimum" }, false);
            if (!r.Low95Fps.HasValue)
                r.Low95Fps = NumberAfterKeyword(sorted, new[] { "перцентил", "percentile", "5-й", "5й" }, false);
        }

        static double? NumberAfterKeyword(List<OcrWord> sorted, string[] keys, bool preferLarge)
        {
            for (int i = 0; i < sorted.Count; i++)
            {
                var t = (sorted[i].Text ?? "").ToLowerInvariant();
                bool hit = false;
                foreach (var k in keys)
                    if (t.Contains(k)) { hit = true; break; }
                if (!hit) continue;

                double? best = null;
                double bestH = 0;

                for (int j = i + 1; j < sorted.Count && j <= i + 6; j++)
                {
                    var w = sorted[j];
                    if (w.Y > sorted[i].Y + 100) break;

                    var text = w.Text ?? "";
                    var low = text.ToLowerInvariant();
                    if (low.Contains("гб") || low.Contains("gb")) continue;

                    var v = TryParseOcrNum(text);
                    if (!v.HasValue || !LooksLikeFps(v.Value)) continue;

                    if (preferLarge)
                    {
                        if (w.H > bestH) { best = v; bestH = w.H; }
                    }
                    else return v;
                }
                if (best.HasValue) return best;
            }
            return null;
        }

        static void ParseUsedVram(BenchResult r, List<OcrWord> sorted)
        {
            if (r.VramUsedGb.HasValue) return;

            for (int i = 0; i < sorted.Count; i++)
            {
                var t = (sorted[i].Text ?? "").ToLowerInvariant();
                if (!t.Contains("используем") && !t.Contains("used")) continue;

                for (int j = i + 1; j < sorted.Count && j <= i + 10; j++)
                {
                    var w = sorted[j];
                    if (w.Y > sorted[i].Y + 150) break;

                    var text = w.Text ?? "";
                    var low = text.ToLowerInvariant();
                    var v = TryParseOcrNum(text);
                    if (!v.HasValue || !LooksLikeVram(v.Value)) continue;

                    bool hasDec = text.Contains(",") || text.Contains(".");
                    bool hasUnit = low.Contains("гб") || low.Contains("gb");
                    double frac = Math.Abs(v.Value - Math.Truncate(v.Value));
                    if (hasDec || hasUnit || frac > 0.05)
                    {
                        r.VramUsedGb = v;
                        return;
                    }
                }
            }
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