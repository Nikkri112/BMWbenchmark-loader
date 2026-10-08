using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace BMWBenchmarkLoader
{
    public class OcrWord
    {
        public double X, Y, W, H;
        public string Text;
    }

    /// <summary>
    /// OCR через Windows.Media.Ocr (WinRT).
    /// </summary>
    public static class OcrRunner
    {
        const string Script = @"param([string]$ImageFile, [string]$Lang = 'ru', [string]$OutFile = '')
$ErrorActionPreference = 'Stop'
$null = [Windows.Data.Json.JsonValue,Windows.Data,ContentType=WindowsRuntime]
$null = [Windows.Media.Ocr.OcrEngine,Windows.Media,ContentType=WindowsRuntime]
$null = [Windows.Graphics.Imaging.SoftwareBitmap,Windows.Graphics,ContentType=WindowsRuntime]
$null = [Windows.Globalization.Language,Windows.Globalization,ContentType=WindowsRuntime]

Add-Type -AssemblyName System.Drawing

$asm = [System.Reflection.Assembly]::Load('System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089')
$ext = $asm.GetType('System.WindowsRuntimeSystemExtensions')

$asTaskGeneric = ($ext.GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and
    $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
})[0]

function Await($WinRtTask, $ResultType) {
    $asTask = $asTaskGeneric.MakeGenericMethod($ResultType)
    $netTask = $asTask.Invoke($null, @($WinRtTask))
    $netTask.Wait(-1) | Out-Null
    $netTask.Result
}

$engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage(
    [Windows.Globalization.Language]::new($Lang)
)

if ($null -eq $engine) {
    $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
}

if ($null -eq $engine) {
    throw 'no OCR engine'
}

$img = [System.Drawing.Bitmap]::FromFile($ImageFile)

$ms = New-Object System.IO.MemoryStream
$img.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$img.Dispose()

$ras = [System.IO.WindowsRuntimeStreamExtensions]::AsRandomAccessStream($ms)

$dec = Await (
    [Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($ras)
) ([Windows.Graphics.Imaging.BitmapDecoder])

$swb = Await (
    $dec.GetSoftwareBitmapAsync()
) ([Windows.Graphics.Imaging.SoftwareBitmap])

$res = Await (
    $engine.RecognizeAsync($swb)
) ([Windows.Media.Ocr.OcrResult])

$sb = New-Object System.Text.StringBuilder

foreach ($line in $res.Lines) {
    foreach ($w in $line.Words) {
        $r = $w.BoundingRect

        [void]$sb.AppendLine(
            ('W|' +
             [int]$r.X + '|' +
             [int]$r.Y + '|' +
             [int]$r.Width + '|' +
             [int]$r.Height + '|' +
             $w.Text)
        )
    }
}

if ($OutFile -ne '') {
    Set-Content -LiteralPath $OutFile -Value $sb.ToString() -Encoding UTF8
}
else {
    Write-Output $sb.ToString()
}
";

        public static List<OcrWord> RunWords(
            string pngPath,
            string workDir,
            string lang = "ru")
        {
            var scriptFile = Path.Combine(workDir, "ocr_winrt.ps1");

            File.WriteAllText(
                scriptFile,
                Script,
                new UTF8Encoding(false)
            );

            var outFile = Path.Combine(workDir, "ocr_out.txt");

            if (File.Exists(outFile))
                File.Delete(outFile);

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments =
                    "-NoProfile -ExecutionPolicy Bypass " +
                    "-File \"" + scriptFile + "\" " +
                    "-ImageFile \"" + pngPath + "\" " +
                    "-Lang " + lang + " " +
                    "-OutFile \"" + outFile + "\"",

                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var p = System.Diagnostics.Process.Start(psi))
            {
                string so = p.StandardOutput.ReadToEnd();
                string se = p.StandardError.ReadToEnd();

                p.WaitForExit(120000);

                if (!File.Exists(outFile))
                {
                    throw new Exception(
                        "OCR не выполнен: " +
                        (se.Length > 0 ? se : so)
                    );
                }
            }

            return ParseOutFile(outFile);
        }

        /// <summary>
        /// Обычный OCR вырезанной области.
        /// </summary>
        public static List<OcrWord> RunWordsRegion(
            string pngPath,
            string workDir,
            int x,
            int y,
            int w,
            int h,
            int scale = 3,
            string tag = "crop")
        {
            return RunWordsRegionProcessed(
                pngPath,
                workDir,
                x,
                y,
                w,
                h,
                scale,
                tag,
                0
            );
        }

        /// <summary>
        /// OCR специально для больших числовых значений.
        ///
        /// Запускает несколько вариантов обработки изображения:
        /// 0 = обычный
        /// 1 = grayscale
        /// 2 = повышенный контраст
        /// 3 = бинаризация
        ///
        /// После этого выбирается наиболее информативный OCR-результат.
        /// </summary>
        public static List<OcrWord> RunNumericRegion(
            string pngPath,
            string workDir,
            int x,
            int y,
            int w,
            int h,
            int scale = 4,
            string tag = "number")
        {
            var candidates = new List<List<OcrWord>>();

            for (int mode = 0; mode < 4; mode++)
            {
                try
                {
                    var words = RunWordsRegionProcessed(
                        pngPath,
                        workDir,
                        x,
                        y,
                        w,
                        h,
                        scale,
                        tag + "_m" + mode,
                        mode
                    );

                    if (words != null && words.Count > 0)
                        candidates.Add(words);
                }
                catch
                {
                    // Если один из вариантов обработки не удался,
                    // продолжаем с остальными.
                }
            }

            if (candidates.Count == 0)
                return new List<OcrWord>();

            List<OcrWord> best = null;
            int bestScore = int.MinValue;

            foreach (var words in candidates)
            {
                int score = ScoreNumericResult(words);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = words;
                }
            }

            return best ?? new List<OcrWord>();
        }

        /// <summary>
        /// Создание обработанной картинки и запуск OCR.
        /// </summary>
        static List<OcrWord> RunWordsRegionProcessed(
            string pngPath,
            string workDir,
            int x,
            int y,
            int w,
            int h,
            int scale,
            string tag,
            int processingMode)
        {
            if (w <= 0 || h <= 0)
                return new List<OcrWord>();

            string cropPath = Path.Combine(
                workDir,
                "ocr_crop_" + tag + ".png"
            );

            using (var src = new Bitmap(pngPath))
            {
                // Clamp координат.
                if (x < 0)
                {
                    w += x;
                    x = 0;
                }

                if (y < 0)
                {
                    h += y;
                    y = 0;
                }

                if (x + w > src.Width)
                    w = src.Width - x;

                if (y + h > src.Height)
                    h = src.Height - y;

                if (w <= 2 || h <= 2)
                    return new List<OcrWord>();

                using (var crop = src.Clone(
                    new Rectangle(x, y, w, h),
                    PixelFormat.Format24bppRgb))
                using (var up = new Bitmap(
                    w * scale,
                    h * scale,
                    PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(up))
                    {
                        g.Clear(Color.White);

                        g.InterpolationMode =
                            InterpolationMode.HighQualityBicubic;

                        g.PixelOffsetMode =
                            PixelOffsetMode.HighQuality;

                        g.SmoothingMode =
                            SmoothingMode.HighQuality;

                        g.DrawImage(
                            crop,
                            0,
                            0,
                            up.Width,
                            up.Height
                        );
                    }

                    switch (processingMode)
                    {
                        case 1:
                            ApplyGrayscale(up);
                            break;

                        case 2:
                            ApplyHighContrast(up);
                            break;

                        case 3:
                            ApplyThreshold(up);
                            break;
                    }

                    up.Save(
                        cropPath,
                        ImageFormat.Png
                    );
                }
            }

            var words = RunWords(
                cropPath,
                workDir,
                "ru"
            );

            // Возвращаем координаты в систему исходного изображения.
            foreach (var word in words)
            {
                word.X = word.X / scale + x;
                word.Y = word.Y / scale + y;
                word.W = word.W / scale;
                word.H = word.H / scale;
            }

            return words;
        }

        /// <summary>
        /// Оценка качества OCR результата для числовой области.
        /// </summary>
        static int ScoreNumericResult(List<OcrWord> words)
        {
            int score = 0;

            foreach (var word in words)
            {
                if (word == null || string.IsNullOrWhiteSpace(word.Text))
                    continue;

                string t = word.Text.Trim();

                bool hasDigit = false;
                bool hasDecimal = false;

                foreach (char c in t)
                {
                    if (char.IsDigit(c))
                        hasDigit = true;

                    if (c == ',' || c == '.')
                        hasDecimal = true;
                }

                if (hasDigit)
                    score += 10;

                if (hasDecimal)
                    score += 8;

                // Большие буквы/слова для числового crop обычно мешают.
                if (t.Length > 8)
                    score -= 5;

                if (word.H > 20)
                    score += 2;
            }

            return score;
        }

        /// <summary>
        /// Grayscale.
        /// </summary>
        static void ApplyGrayscale(Bitmap bmp)
        {
            for (int y = 0; y < bmp.Height; y++)
            {
                for (int x = 0; x < bmp.Width; x++)
                {
                    Color c = bmp.GetPixel(x, y);

                    int gray =
                        (int)(
                            0.299 * c.R +
                            0.587 * c.G +
                            0.114 * c.B
                        );

                    if (gray < 0) gray = 0;
                    if (gray > 255) gray = 255;

                    bmp.SetPixel(
                        x,
                        y,
                        Color.FromArgb(gray, gray, gray)
                    );
                }
            }
        }

        /// <summary>
        /// Повышение контраста.
        /// </summary>
        static void ApplyHighContrast(Bitmap bmp)
        {
            for (int y = 0; y < bmp.Height; y++)
            {
                for (int x = 0; x < bmp.Width; x++)
                {
                    Color c = bmp.GetPixel(x, y);

                    int gray =
                        (int)(
                            0.299 * c.R +
                            0.587 * c.G +
                            0.114 * c.B
                        );

                    // Усиливаем светлые цифры.
                    int v;

                    if (gray > 150)
                        v = 255;
                    else if (gray > 100)
                        v = 210;
                    else
                        v = 40;

                    bmp.SetPixel(
                        x,
                        y,
                        Color.FromArgb(v, v, v)
                    );
                }
            }
        }

        /// <summary>
        /// Жёсткая бинаризация.
        /// </summary>
        static void ApplyThreshold(Bitmap bmp)
        {
            for (int y = 0; y < bmp.Height; y++)
            {
                for (int x = 0; x < bmp.Width; x++)
                {
                    Color c = bmp.GetPixel(x, y);

                    int gray =
                        (int)(
                            0.299 * c.R +
                            0.587 * c.G +
                            0.114 * c.B
                        );

                    int v = gray > 135 ? 255 : 0;

                    bmp.SetPixel(
                        x,
                        y,
                        Color.FromArgb(v, v, v)
                    );
                }
            }
        }

        static List<OcrWord> ParseOutFile(string outFile)
        {
            var words = new List<OcrWord>();

            foreach (var raw in File.ReadAllLines(
                outFile,
                Encoding.UTF8))
            {
                if (!raw.StartsWith("W|"))
                    continue;

                var parts = raw.Split('|');

                if (parts.Length < 6)
                    continue;

                var w = new OcrWord
                {
                    X = ParseD(parts[1]),
                    Y = ParseD(parts[2]),
                    W = ParseD(parts[3]),
                    H = ParseD(parts[4]),
                    Text = parts[5]
                };

                words.Add(w);
            }

            return words;
        }

        public static string WordsToText(List<OcrWord> words)
        {
            var sb = new StringBuilder();

            if (words == null)
                return "";

            foreach (var w in words)
            {
                if (w == null)
                    continue;

                sb.AppendLine(w.Text);
            }

            return sb.ToString();
        }

        static double ParseD(string s)
        {
            double d;

            return double.TryParse(
                s,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out d
            )
                ? d
                : 0;
        }
    }
}