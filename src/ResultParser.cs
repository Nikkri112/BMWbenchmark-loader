using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
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

        static readonly Regex AnyNumRx =
            new Regex("(\\d+(?:[.,]\\d+)?)", RegexOptions.Compiled);

        // Зоны макета 1920x1080
        static readonly int[][] Regions = new int[][]
        {
            new[] { 50, 320, 280, 430 },
            new[] { 50, 420, 280, 505 },
            new[] { 290, 420, 500, 505 },
            new[] { 50, 505, 300, 575 },
            new[] { 50, 600, 280, 690 }
        };

        public static BenchResult Parse(
            List<OcrWord> words,
            int imgW,
            int imgH)
        {
            return Parse(words, imgW, imgH, null, null);
        }

        public static BenchResult Parse(
            List<OcrWord> words,
            int imgW,
            int imgH,
            string pngPath,
            string workDir)
        {
            var r = new BenchResult();

            if (words == null)
                words = new List<OcrWord>();

            var raw = new StringBuilder();

            foreach (var w in words)
                raw.AppendLine(w.Text);

            r.RawOcr = raw.ToString();

            double sx = imgW / 1920.0;
            double sy = imgH / 1080.0;

            if (sx <= 0 || sx > 4)
                sx = 1;

            if (sy <= 0 || sy > 4)
                sy = 1;

            // Дата
            foreach (var w in words)
            {
                var t = w.Text ?? "";

                if (t.Contains("окт") ||
                    t.Contains("янв") ||
                    t.Contains("февр") ||
                    t.Contains("мар") ||
                    t.Contains("апр") ||
                    t.Contains("мая") ||
                    t.Contains("июн") ||
                    t.Contains("июл") ||
                    t.Contains("авг") ||
                    t.Contains("сент") ||
                    t.Contains("нояб") ||
                    t.Contains("дек"))
                {
                    r.DateText = t;
                    break;
                }
            }

            var sorted = new List<OcrWord>(words);

            sorted.Sort((a, b) =>
            {
                int rowA = (int)(a.Y / 20);
                int rowB = (int)(b.Y / 20);

                if (rowA != rowB)
                    return rowA.CompareTo(rowB);

                return a.X.CompareTo(b.X);
            });

            // Обычный OCR по зонам.
            ParseRegions(r, words, sx, sy);

            // OCR по ключевым словам.
            ParseByKeywords(r, sorted);

            // Самое важное:
            // повторно анализируем блок "В среднем",
            // даже если OCR уже дал неправильную одиночную цифру.
            //
            // Например:
            //   5
            //   4
            //   FPS
            //
            // превращаем в 54.
            FixAverageFpsFromOcrWords(r, sorted);

            if (pngPath != null &&
                workDir != null &&
                FileExists(pngPath))
            {
                try
                {
                    SupplementalCropOcr(
                        r,
                        pngPath,
                        workDir,
                        sx,
                        sy);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        "  [ocr-crop] " + ex.Message);
                }
            }

            ParseUsedVram(r, sorted);

            Sanitize(r);

            return r;
        }

        static bool FileExists(string p)
        {
            try
            {
                return System.IO.File.Exists(p);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Исправляет типичные ошибки OCR в коротких числовых токенах.
        /// </summary>
        static string FixOcrDigits(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;

            if (s.Length > 8)
                return s;

            var sb = new StringBuilder(s.Length);

            foreach (var c in s)
            {
                switch (c)
                {
                    case 'З':
                    case 'з':
                        sb.Append('3');
                        break;

                    case 'О':
                    case 'о':
                        sb.Append('0');
                        break;

                    case 'Б':
                    case 'б':
                        sb.Append('6');
                        break;

                    case 'I':
                    case 'l':
                    case '|':
                        sb.Append('1');
                        break;

                    case 'S':
                        sb.Append('5');
                        break;

                    case 'B':
                        sb.Append('8');
                        break;

                    default:
                        sb.Append(c);
                        break;
                }
            }

            return sb.ToString();
        }

        static double? TryParseOcrNum(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            var m = AnyNumRx.Match(text);

            if (m.Success)
            {
                var v = ParseNum(m.Groups[1].Value);

                if (v.HasValue)
                    return v;
            }

            var fixedText = FixOcrDigits(text);

            if (fixedText == text)
                return null;

            m = AnyNumRx.Match(fixedText);

            if (m.Success)
                return ParseNum(m.Groups[1].Value);

            return null;
        }

        static bool LooksLikeFps(double v)
        {
            if (v < 1 || v > 500)
                return false;

            if (v >= 2000 && v <= 2099)
                return false;

            double frac =
                Math.Abs(v - Math.Truncate(v));

            // Например 2,6 больше похожее на VRAM.
            if (frac > 0.05 && v < 10)
                return false;

            return true;
        }

        static bool LooksLikeVram(double v)
        {
            return v >= 0.3 && v <= 32;
        }

        static void Sanitize(BenchResult r)
        {
            if (r.MinFps.HasValue &&
                !LooksLikeFps(r.MinFps.Value))
            {
                if (!r.VramUsedGb.HasValue &&
                    LooksLikeVram(r.MinFps.Value))
                {
                    r.VramUsedGb = r.MinFps;
                }

                r.MinFps = null;
            }

            if (r.MaxFps.HasValue &&
                !LooksLikeFps(r.MaxFps.Value))
            {
                r.MaxFps = null;
            }

            if (r.AvgFps.HasValue &&
                !LooksLikeFps(r.AvgFps.Value))
            {
                r.AvgFps = null;
            }

            if (r.Low95Fps.HasValue &&
                !LooksLikeFps(r.Low95Fps.Value))
            {
                r.Low95Fps = null;
            }

            if (r.MinFps.HasValue &&
                r.MaxFps.HasValue &&
                r.MinFps > r.MaxFps)
            {
                r.MinFps = null;
            }

            // Не принимаем обычный 4/6/8/12/16/24
            // за использованную VRAM.
            if (r.VramUsedGb.HasValue)
            {
                double v = r.VramUsedGb.Value;

                double frac =
                    Math.Abs(v - Math.Truncate(v));

                if (frac < 0.05 &&
                    (v == 4 ||
                     v == 6 ||
                     v == 8 ||
                     v == 12 ||
                     v == 16 ||
                     v == 24))
                {
                    r.VramUsedGb = null;
                }
            }
        }

        // ============================================================
        // ИСПРАВЛЕНИЕ AVG FPS
        // ============================================================

        static void FixAverageFpsFromOcrWords(
            BenchResult r,
            List<OcrWord> sorted)
        {
            for (int i = 0; i < sorted.Count; i++)
            {
                string keyword =
                    (sorted[i].Text ?? "").ToLowerInvariant();

                if (!IsAverageKeyword(keyword))
                    continue;

                double? best = FindCombinedFpsAfterKeyword(
                    sorted,
                    i);

                if (best.HasValue)
                {
                    // Очень важно:
                    //
                    // если было:
                    //     AvgFps = 5
                    //
                    // а OCR позволяет собрать:
                    //     5 + 4 = 54
                    //
                    // то 54 должен заменить 5.
                    if (!r.AvgFps.HasValue ||
                        best.Value >= 10 ||
                        r.AvgFps.Value < 10)
                    {
                        r.AvgFps = best;
                    }

                    return;
                }
            }
        }

        static bool IsAverageKeyword(string text)
        {
            return text.Contains("среднем") ||
                   text.Contains("среднее") ||
                   text.Contains("средн") ||
                   text.Contains("average") ||
                   text.Contains("avg");
        }

        static double? FindCombinedFpsAfterKeyword(
            List<OcrWord> sorted,
            int keywordIndex)
        {
            if (keywordIndex < 0 ||
                keywordIndex >= sorted.Count)
            {
                return null;
            }

            var keyword = sorted[keywordIndex];

            var candidates =
                new List<OcrWord>();

            // Берём OCR-слова справа/ниже от "В среднем".
            //
            // Не ограничиваемся только следующими 6 словами,
            // потому что OCR может вставить между ними FPS
            // или другие элементы.
            for (int j = keywordIndex + 1;
                 j < sorted.Count;
                 j++)
            {
                var w = sorted[j];

                // Не уходим далеко вниз страницы.
                if (w.Y > keyword.Y + 180)
                    break;

                // Слишком далеко по горизонтали.
                if (Math.Abs(w.X - keyword.X) > 500)
                    continue;

                string text = w.Text ?? "";
                string low = text.ToLowerInvariant();

                if (low.Contains("гб") ||
                    low.Contains("gb"))
                {
                    continue;
                }

                if (low == "fps" ||
                    low == "фпс")
                {
                    continue;
                }

                var v = TryParseOcrNum(text);

                if (!v.HasValue)
                    continue;

                if (!LooksLikeFps(v.Value))
                    continue;

                candidates.Add(w);
            }

            if (candidates.Count == 0)
                return null;

            // --------------------------------------------------------
            // 1. Сначала ищем уже готовое число 10..500.
            // --------------------------------------------------------

            foreach (var w in candidates)
            {
                var v = TryParseOcrNum(w.Text);

                if (v.HasValue &&
                    v.Value >= 10 &&
                    v.Value <= 500)
                {
                    return v;
                }
            }

            // --------------------------------------------------------
            // 2. Если OCR разделил число:
            //
            //      5
            //      4
            //
            // получаем:
            //
            //      54
            //
            // Аналогично:
            //
            //      2
            //      0
            //
            // -> 20
            // --------------------------------------------------------

            for (int a = 0; a < candidates.Count; a++)
            {
                var first = candidates[a];

                var firstValue =
                    TryParseOcrNum(first.Text);

                if (!firstValue.HasValue)
                    continue;

                if (!IsSingleDigitToken(first.Text))
                    continue;

                int firstDigit =
                    (int)firstValue.Value;

                if (firstDigit < 0 ||
                    firstDigit > 9)
                {
                    continue;
                }

                for (int b = a + 1;
                     b < candidates.Count;
                     b++)
                {
                    var second = candidates[b];

                    var secondValue =
                        TryParseOcrNum(second.Text);

                    if (!secondValue.HasValue)
                        continue;

                    if (!IsSingleDigitToken(second.Text))
                        continue;

                    int secondDigit =
                        (int)secondValue.Value;

                    if (secondDigit < 0 ||
                        secondDigit > 9)
                    {
                        continue;
                    }

                    // OCR должен видеть цифры примерно на одной строке.
                    double centerY1 =
                        first.Y + first.H / 2.0;

                    double centerY2 =
                        second.Y + second.H / 2.0;

                    if (Math.Abs(centerY1 - centerY2) > 35)
                        continue;

                    // Проверяем, что цифры действительно рядом.
                    double gap =
                        second.X -
                        (first.X + first.W);

                    if (gap < -20 || gap > 150)
                        continue;

                    int combined =
                        firstDigit * 10 +
                        secondDigit;

                    if (combined >= 10 &&
                        combined <= 500)
                    {
                        Console.WriteLine(
                            "  [avg combine] " +
                            first.Text +
                            " + " +
                            second.Text +
                            " -> " +
                            combined);

                        return combined;
                    }
                }
            }

            return null;
        }

        static bool IsSingleDigitToken(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string fixedText =
                FixOcrDigits(text).Trim();

            return Regex.IsMatch(
                fixedText,
                @"^\d$");
        }

        // ============================================================
        // CROP OCR
        // ============================================================

        static void SupplementalCropOcr(
            BenchResult r,
            string pngPath,
            string workDir,
            double sx,
            double sy)
        {
            // --------------------------------------------------------
            // AVG
            //
            // Раньше этот блок выполнялся только если AvgFps == null.
            //
            // Это была ошибка.
            //
            // Если обычный OCR дал "5", сюда мы никогда не попадали,
            // хотя настоящее число было "54".
            //
            // Поэтому теперь запускаем дополнительный OCR всегда.
            // --------------------------------------------------------

            {
                int x =
                    (int)(50 * sx);

                int y =
                    (int)(330 * sy);

                int w =
                    (int)(220 * sx);

                int h =
                    (int)(100 * sy);

                var cropWords =
                    OcrRunner.RunWordsRegion(
                        pngPath,
                        workDir,
                        x,
                        y,
                        w,
                        h,
                        3,
                        "avg");

                Console.WriteLine(
                    "  [ocr-crop avg] " +
                    OcrRunner.WordsToText(cropWords)
                        .Replace("\n", " | ")
                        .Trim());

                // Сначала пытаемся собрать 54 / 20 и т.п.
                double? combined =
                    ExtractCombinedFps(
                        cropWords);

                if (combined.HasValue)
                {
                    if (!r.AvgFps.HasValue ||
                        r.AvgFps.Value < 10 ||
                        combined.Value >= 10)
                    {
                        r.AvgFps = combined;
                    }
                }
                else
                {
                    // Обычное одиночное число.
                    double? best = null;
                    double bestH = 0;

                    foreach (var word in cropWords)
                    {
                        var v =
                            TryParseOcrNum(word.Text);

                        if (!v.HasValue ||
                            !LooksLikeFps(v.Value))
                        {
                            continue;
                        }

                        if (word.H > bestH)
                        {
                            best = v;
                            bestH = word.H;
                        }
                    }

                    if (best.HasValue)
                    {
                        if (!r.AvgFps.HasValue ||
                            r.AvgFps.Value < 10)
                        {
                            r.AvgFps = best;
                        }
                    }
                }
            }

            // --------------------------------------------------------
            // VRAM
            // --------------------------------------------------------

            if (!r.VramUsedGb.HasValue)
            {
                var attempts = new int[][]
                {
                    new[] { 40, 580, 280, 120, 3 },
                    new[] { 40, 580, 280, 120, 4 },
                    new[] { 60, 610, 200, 80, 4 },
                };

                double? withDec = null;

                for (int ai = 0;
                     ai < attempts.Length;
                     ai++)
                {
                    var a = attempts[ai];

                    int x =
                        (int)(a[0] * sx);

                    int y =
                        (int)(a[1] * sy);

                    int w =
                        (int)(a[2] * sx);

                    int h =
                        (int)(a[3] * sy);

                    string tag =
                        "vram" +
                        a[4] +
                        (ai > 0 ? "b" + ai : "");

                    var cropWords =
                        OcrRunner.RunWordsRegion(
                            pngPath,
                            workDir,
                            x,
                            y,
                            w,
                            h,
                            a[4],
                            tag);

                    Console.WriteLine(
                        "  [ocr-crop " +
                        tag +
                        "] " +
                        OcrRunner.WordsToText(cropWords)
                            .Replace("\n", " | ")
                            .Trim());

                    foreach (var word in cropWords)
                    {
                        var text =
                            word.Text ?? "";

                        var low =
                            text.ToLowerInvariant();

                        var v =
                            TryParseOcrNum(text);

                        if (!v.HasValue ||
                            !LooksLikeVram(v.Value))
                        {
                            continue;
                        }

                        bool hasDec =
                            text.Contains(",") ||
                            text.Contains(".");

                        bool hasUnit =
                            low.Contains("гб") ||
                            low.Contains("gb");

                        double frac =
                            Math.Abs(
                                v.Value -
                                Math.Truncate(v.Value));

                        // Оси графика.
                        if (!hasDec &&
                            !hasUnit &&
                            frac < 0.05 &&
                            (v.Value == 10 ||
                             v.Value == 20 ||
                             v.Value == 30 ||
                             v.Value == 40 ||
                             v.Value == 60 ||
                             v.Value == 80 ||
                             v.Value == 100))
                        {
                            continue;
                        }

                        if (hasDec ||
                            hasUnit ||
                            frac > 0.05)
                        {
                            withDec = v;
                            break;
                        }
                    }

                    if (withDec.HasValue)
                        break;

                    string joined = "";

                    foreach (var word in cropWords)
                    {
                        joined +=
                            FixOcrDigits(
                                word.Text ?? "") +
                            " ";
                    }

                    var mx =
                        Regex.Match(
                            joined,
                            @"(\d+[.,]\d+)\s*(гб|gb)?",
                            RegexOptions.IgnoreCase);

                    if (mx.Success)
                    {
                        var v =
                            ParseNum(
                                mx.Groups[1].Value);

                        if (v.HasValue &&
                            LooksLikeVram(v.Value))
                        {
                            withDec = v;
                            break;
                        }
                    }
                }

                if (withDec.HasValue)
                    r.VramUsedGb = withDec;
            }
        }

        // Из OCR crop собирает:
        //
        // 5 + 4 -> 54
        // 2 + 0 -> 20
        //
        static double? ExtractCombinedFps(
            List<OcrWord> words)
        {
            if (words == null ||
                words.Count == 0)
            {
                return null;
            }

            var numeric =
                new List<OcrWord>();

            foreach (var word in words)
            {
                string text =
                    word.Text ?? "";

                string low =
                    text.ToLowerInvariant();

                if (low.Contains("fps") ||
                    low.Contains("фпс"))
                {
                    continue;
                }

                var v =
                    TryParseOcrNum(text);

                if (!v.HasValue)
                    continue;

                if (!LooksLikeFps(v.Value))
                    continue;

                numeric.Add(word);
            }

            // Уже готовое число.
            foreach (var word in numeric)
            {
                var v =
                    TryParseOcrNum(word.Text);

                if (v.HasValue &&
                    v.Value >= 10 &&
                    v.Value <= 500)
                {
                    return v;
                }
            }

            // Разделённое число.
            for (int i = 0;
                 i < numeric.Count;
                 i++)
            {
                if (!IsSingleDigitToken(
                        numeric[i].Text))
                {
                    continue;
                }

                int a =
                    (int)TryParseOcrNum(
                        numeric[i].Text).Value;

                for (int j = i + 1;
                     j < numeric.Count;
                     j++)
                {
                    if (!IsSingleDigitToken(
                            numeric[j].Text))
                    {
                        continue;
                    }

                    double y1 =
                        numeric[i].Y +
                        numeric[i].H / 2.0;

                    double y2 =
                        numeric[j].Y +
                        numeric[j].H / 2.0;

                    if (Math.Abs(y1 - y2) > 35)
                        continue;

                    double gap =
                        numeric[j].X -
                        (numeric[i].X +
                         numeric[i].W);

                    if (gap < -20 ||
                        gap > 150)
                    {
                        continue;
                    }

                    int b =
                        (int)TryParseOcrNum(
                            numeric[j].Text).Value;

                    int result =
                        a * 10 + b;

                    if (result >= 10 &&
                        result <= 500)
                    {
                        Console.WriteLine(
                            "  [avg crop combine] " +
                            numeric[i].Text +
                            " + " +
                            numeric[j].Text +
                            " -> " +
                            result);

                        return result;
                    }
                }
            }

            return null;
        }

        // ============================================================
        // Обычные зоны
        // ============================================================

        static void ParseRegions(
            BenchResult r,
            List<OcrWord> words,
            double sx,
            double sy)
        {
            for (int i = 0; i < 4; i++)
            {
                var reg = Regions[i];

                double x1 = reg[0] * sx;
                double y1 = reg[1] * sy;
                double x2 = reg[2] * sx;
                double y2 = reg[3] * sy;

                double? best = null;
                double bestH = 0;

                foreach (var w in words)
                {
                    var cx =
                        w.X + w.W / 2;

                    var cy =
                        w.Y + w.H / 2;

                    if (cx < x1 ||
                        cx > x2 ||
                        cy < y1 ||
                        cy > y2)
                    {
                        continue;
                    }

                    var text =
                        w.Text ?? "";

                    var low =
                        text.ToLowerInvariant();

                    if (low.Contains("гб") ||
                        low.Contains("gb"))
                    {
                        continue;
                    }

                    var v =
                        TryParseOcrNum(text);

                    if (!v.HasValue ||
                        !LooksLikeFps(v.Value))
                    {
                        continue;
                    }

                    if (i != 0)
                    {
                        var fixedText =
                            FixOcrDigits(text);

                        if (fixedText.Contains("5-й") ||
                            fixedText.Contains("5й"))
                        {
                            continue;
                        }
                    }

                    if (i == 0)
                    {
                        if (w.H > bestH)
                        {
                            best = v;
                            bestH = w.H;
                        }
                    }
                    else if (!best.HasValue ||
                             w.H > bestH)
                    {
                        best = v;
                        bestH = w.H;
                    }
                }

                switch (i)
                {
                    case 0:
                        if (!r.AvgFps.HasValue)
                            r.AvgFps = best;
                        break;

                    case 1:
                        if (!r.MaxFps.HasValue)
                            r.MaxFps = best;
                        break;

                    case 2:
                        if (!r.MinFps.HasValue)
                            r.MinFps = best;
                        break;

                    case 3:
                        if (!r.Low95Fps.HasValue)
                            r.Low95Fps = best;
                        break;
                }
            }
        }

        // ============================================================
        // Keywords
        // ============================================================

        static void ParseByKeywords(
            BenchResult r,
            List<OcrWord> sorted)
        {
            if (!r.AvgFps.HasValue)
            {
                r.AvgFps =
                    NumberAfterKeyword(
                        sorted,
                        new[]
                        {
                            "среднем",
                            "среднее",
                            "средн",
                            "average",
                            "avg"
                        },
                        true);
            }

            if (!r.MaxFps.HasValue)
            {
                r.MaxFps =
                    NumberAfterKeyword(
                        sorted,
                        new[]
                        {
                            "максимум",
                            "максим",
                            "maximum"
                        },
                        false);
            }

            if (!r.MinFps.HasValue)
            {
                r.MinFps =
                    NumberAfterKeyword(
                        sorted,
                        new[]
                        {
                            "минимум",
                            "миним",
                            "minimum"
                        },
                        false);
            }

            if (!r.Low95Fps.HasValue)
            {
                r.Low95Fps =
                    NumberAfterKeyword(
                        sorted,
                        new[]
                        {
                            "перцентил",
                            "percentile",
                            "5-й",
                            "5й"
                        },
                        false);
            }
        }

        static double? NumberAfterKeyword(
            List<OcrWord> sorted,
            string[] keys,
            bool preferLarge)
        {
            for (int i = 0;
                 i < sorted.Count;
                 i++)
            {
                var t =
                    (sorted[i].Text ?? "")
                        .ToLowerInvariant();

                bool hit = false;

                foreach (var k in keys)
                {
                    if (t.Contains(k))
                    {
                        hit = true;
                        break;
                    }
                }

                if (!hit)
                    continue;

                double? best = null;
                double bestH = 0;

                for (int j = i + 1;
                     j < sorted.Count &&
                     j <= i + 6;
                     j++)
                {
                    var w = sorted[j];

                    if (w.Y >
                        sorted[i].Y + 100)
                    {
                        break;
                    }

                    var text =
                        w.Text ?? "";

                    var low =
                        text.ToLowerInvariant();

                    if (low.Contains("гб") ||
                        low.Contains("gb"))
                    {
                        continue;
                    }

                    var v =
                        TryParseOcrNum(text);

                    if (!v.HasValue ||
                        !LooksLikeFps(v.Value))
                    {
                        continue;
                    }

                    if (preferLarge)
                    {
                        if (w.H > bestH)
                        {
                            best = v;
                            bestH = w.H;
                        }
                    }
                    else
                    {
                        return v;
                    }
                }

                if (best.HasValue)
                    return best;
            }

            return null;
        }

        // ============================================================
        // VRAM
        // ============================================================

        static void ParseUsedVram(
            BenchResult r,
            List<OcrWord> sorted)
        {
            if (r.VramUsedGb.HasValue)
                return;

            for (int i = 0;
                 i < sorted.Count;
                 i++)
            {
                var t =
                    (sorted[i].Text ?? "")
                        .ToLowerInvariant();

                if (!t.Contains("используем") &&
                    !t.Contains("used"))
                {
                    continue;
                }

                for (int j = i + 1;
                     j < sorted.Count &&
                     j <= i + 10;
                     j++)
                {
                    var w = sorted[j];

                    if (w.Y >
                        sorted[i].Y + 150)
                    {
                        break;
                    }

                    var text =
                        w.Text ?? "";

                    var low =
                        text.ToLowerInvariant();

                    var v =
                        TryParseOcrNum(text);

                    if (!v.HasValue ||
                        !LooksLikeVram(v.Value))
                    {
                        continue;
                    }

                    bool hasDec =
                        text.Contains(",") ||
                        text.Contains(".");

                    bool hasUnit =
                        low.Contains("гб") ||
                        low.Contains("gb");

                    double frac =
                        Math.Abs(
                            v.Value -
                            Math.Truncate(v.Value));

                    if (hasDec ||
                        hasUnit ||
                        frac > 0.05)
                    {
                        r.VramUsedGb = v;
                        return;
                    }
                }
            }
        }

        static double? ParseNum(string s)
        {
            if (s == null)
                return null;

            double d;

            s = s.Replace(',', '.');

            if (double.TryParse(
                    s,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out d))
            {
                return d;
            }

            return null;
        }

        public string Format(string title)
        {
            var sb =
                new StringBuilder();

            sb.AppendLine(
                "  [" + title + "]");

            sb.AppendLine(
                "  В среднем (FPS):          " +
                F(AvgFps));

            sb.AppendLine(
                "  Максимум (FPS):           " +
                F(MaxFps));

            sb.AppendLine(
                "  Минимум (FPS):            " +
                F(MinFps));

            sb.AppendLine(
                "  5-й перцентиль (FPS):     " +
                F(Low95Fps));

            sb.AppendLine(
                "  Исп. видеопамять (ГБ):    " +
                F(VramUsedGb));

            return sb.ToString();
        }

        static string F(double? v)
        {
            return v.HasValue
                ? v.Value.ToString("0.##")
                : "n/a";
        }
    }
}