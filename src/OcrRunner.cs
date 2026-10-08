using System;
using System.Collections.Generic;
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
    /// OCR через Windows.Media.Ocr (WinRT). Выполняется системным PowerShell 5.1,
    /// который имеет встроенную поддержку WinRT без Windows SDK.
    /// Возвращает слова с координатами (раскладка экрана результатов фиксирована).
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
$asTaskGeneric = ($ext.GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($WinRtTask, $ResultType) {
  $asTask = $asTaskGeneric.MakeGenericMethod($ResultType)
  $netTask = $asTask.Invoke($null, @($WinRtTask))
  $netTask.Wait(-1) | Out-Null
  $netTask.Result
}
$engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage([Windows.Globalization.Language]::new($Lang))
if ($null -eq $engine) { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages() }
if ($null -eq $engine) { throw 'no OCR engine' }
$img = [System.Drawing.Bitmap]::FromFile($ImageFile)
$ms = New-Object System.IO.MemoryStream
$img.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$img.Dispose()
$ras = [System.IO.WindowsRuntimeStreamExtensions]::AsRandomAccessStream($ms)
$dec = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($ras)) ([Windows.Graphics.Imaging.BitmapDecoder])
$swb = Await ($dec.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
$res = Await ($engine.RecognizeAsync($swb)) ([Windows.Media.Ocr.OcrResult])
$sb = New-Object System.Text.StringBuilder
foreach ($line in $res.Lines) {
  foreach ($w in $line.Words) {
    $r = $w.BoundingRect
    [void]$sb.AppendLine(('W|' + [int]$r.X + '|' + [int]$r.Y + '|' + [int]$r.Width + '|' + [int]$r.Height + '|' + $w.Text))
  }
}
if ($OutFile -ne '') { Set-Content -LiteralPath $OutFile -Value $sb.ToString() -Encoding UTF8 } else { Write-Output $sb.ToString() }
";

        public static List<OcrWord> RunWords(string pngPath, string workDir, string lang = "ru")
        {
            var scriptFile = Path.Combine(workDir, "ocr_winrt.ps1");
            File.WriteAllText(scriptFile, Script, new UTF8Encoding(false));
            var outFile = Path.Combine(workDir, "ocr_out.txt");
            if (File.Exists(outFile)) File.Delete(outFile);

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptFile + "\" -ImageFile \"" + pngPath + "\" -Lang " + lang + " -OutFile \"" + outFile + "\"",
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
                    throw new Exception("OCR не выполнен: " + (se.Length > 0 ? se : so));
            }

            var words = new List<OcrWord>();
            foreach (var raw in File.ReadAllLines(outFile, Encoding.UTF8))
            {
                if (!raw.StartsWith("W|")) continue;
                var parts = raw.Split('|');
                if (parts.Length < 6) continue;
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
            foreach (var w in words) sb.AppendLine(w.Text);
            return sb.ToString();
        }

        static double ParseD(string s)
        {
            double d; return double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : 0;
        }
    }
}
