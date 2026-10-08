using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BMWBenchmarkLoader
{
    public class GameConfig
    {
        public string SteamPath;
        public string GameInstallDir;
        public string ExePath;
        public string IniPath;
        public string OriginalIni;

        // Top-level scalar keys we care about
        public int ResolutionSizeX = 1920;
        public int ResolutionSizeY = 1080;
        public int FullscreenMode = 1;
        public double FrameRateLimit = 0;

        // UISettingData entries (ordered). Index map:
        //  0 MainDisplay, 1 PrivacyAgreement, 2 Min, 3 FrameRateQualityFirst, 4 ScreenBrightness,
        //  5 ScreenMode, 6 ScreenRatio, 7 ScreenResolution, 8 WindowFullImageQuality, 9 LockFrameRate,
        // 10 Vsync, 11 MotionBlur, 12 Dlss, 13 Dx12, 14 SoundVolume, 15 RecommendQualityLevel,
        // 16 ImageQuality, 17 SuperResolutionSampling, 18 InsertFrame, 19 Rtx, 20 QualityLevel,
        // 21 ViewDistance, 22 AntiAliasing, 23 PostProcessing, 24 ShadowQuality, 25 TextureQuality,
        // 26 FxQuality, 27 MaterialQuality, 28 VegetationQuality, 29 GlobalIllumination, 30 ReflectionQuality,
        // 31 RtxLevel
        public List<KeyValuePair<string, string>> UiSettings = new List<KeyValuePair<string, string>>();

        public Dictionary<string, string> Scalability = new Dictionary<string, string>();

        public void Locate()
        {
            SteamPath = ReadSteamPath();
            if (SteamPath == null) throw new Exception("Steam не найден (HKCU\\Software\\Valve\\Steam)");
            GameInstallDir = FindGameDir();
            if (GameInstallDir == null) throw new Exception("Black Myth: Wukong Benchmark Tool (appid 3132990) не найден в библиотеках Steam");
            ExePath = Path.Combine(GameInstallDir, "b1_benchmark.exe");
            if (!File.Exists(ExePath)) throw new Exception("b1_benchmark.exe не найден: " + ExePath);
            IniPath = Path.Combine(GameInstallDir, "b1", "Saved", "Config", "Windows", "GameUserSettings.ini");
            if (!File.Exists(IniPath)) throw new Exception("GameUserSettings.ini не найден: " + IniPath);
        }

        static string ReadSteamPath()
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software\\Valve\\Steam"))
            {
                if (k == null) return null;
                var v = "" + k.GetValue("SteamPath");
                return v.Length > 0 ? v : null;
            }
        }

        string FindGameDir()
        {
            var vdf = Path.Combine(SteamPath, "steamapps", "libraryfolders.vdf");
            var libs = new List<string> { SteamPath };
            if (File.Exists(vdf))
            {
                var rx = new Regex("\"path\"\\s+\"(.+?)\"");
                foreach (Match m in rx.Matches(File.ReadAllText(vdf)))
                {
                    var p = m.Groups[1].Value.Replace("\\\\", "\\");
                    if (!libs.Contains(p)) libs.Add(p);
                }
            }
            foreach (var lib in libs)
            {
                var acf = Path.Combine(lib, "steamapps", "appmanifest_3132990.acf");
                if (File.Exists(acf))
                {
                    var txt = File.ReadAllText(acf);
                    var m = Regex.Match(txt, "\"installdir\"\\s+\"(.+?)\"");
                    if (m.Success)
                    {
                        var dir = Path.Combine(lib, "steamapps", "common", m.Groups[1].Value);
                        if (Directory.Exists(dir)) return dir;
                    }
                }
            }
            return null;
        }

        public void Load()
        {
            OriginalIni = File.ReadAllText(IniPath);
            ParseIni(OriginalIni);
        }

        void ParseIni(string text)
        {
            string section = "";
            var uiRx = new Regex("\\(\\s*\"([^\"]*)\"\\s*,\\s*\"([^\"]*)\"\\s*\\)");
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]")) { section = line; continue; }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim();
                var val = line.Substring(eq + 1).Trim();
                if (section == "[/Script/GSGameSettings.GSGameUserSettings]")
                {
                    if (key == "UISettingData")
                    {
                        UiSettings.Clear();
                        foreach (Match m in uiRx.Matches(val))
                            UiSettings.Add(new KeyValuePair<string, string>(m.Groups[1].Value, m.Groups[2].Value));
                    }
                    else if (key == "ResolutionSizeX") ResolutionSizeX = ParseInt(val, ResolutionSizeX);
                    else if (key == "ResolutionSizeY") ResolutionSizeY = ParseInt(val, ResolutionSizeY);
                    else if (key == "FullscreenMode") FullscreenMode = ParseInt(val, FullscreenMode);
                    else if (key == "FrameRateLimit") FrameRateLimit = ParseDouble(val, FrameRateLimit);
                }
                else if (section == "[ScalabilityGroups]")
                {
                    Scalability[key] = val;
                }
            }
        }

        public void Save()
        {
            // ВАЖНО: не генерируем ini с нуля (игра игнорирует перегенерированный файл),
            // а модифицируем существующий, сохраняя его структуру
            var text = OriginalIni ?? File.ReadAllText(IniPath);
            text = ApplyToIniText(text);
            File.WriteAllText(IniPath, text, new UTF8Encoding(false));
        }

        string ApplyToIniText(string text)
        {
            // значения UISettingData
            foreach (var kv in UiSettings)
            {
                var rx = new Regex("\\(\"" + Regex.Escape(kv.Key) + "\",\\s*\"[^\"]*\"\\)");
                text = rx.Replace(text, "(\"" + kv.Key + "\", \"" + kv.Value + "\")");
            }
            // скалярные ключи
            text = ReplaceScalar(text, "ResolutionSizeX", ResolutionSizeX.ToString());
            text = ReplaceScalar(text, "ResolutionSizeY", ResolutionSizeY.ToString());
            text = ReplaceScalar(text, "LastUserConfirmedResolutionSizeX", ResolutionSizeX.ToString());
            text = ReplaceScalar(text, "LastUserConfirmedResolutionSizeY", ResolutionSizeY.ToString());
            text = ReplaceScalar(text, "FullscreenMode", FullscreenMode.ToString());
            text = ReplaceScalar(text, "FrameRateLimit", FrameRateLimit.ToString("0.000000", CultureInfo.InvariantCulture));
            // scalability
            foreach (var kv in Scalability)
                text = ReplaceScalar(text, kv.Key, kv.Value);
            return text;
        }

        static string ReplaceScalar(string text, string key, string value)
        {
            var rx = new Regex("(?m)^(" + Regex.Escape(key) + ")=.*$");
            return rx.Replace(text, "$1=" + value);
        }

        string BuildIni()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[/Script/GSGameSettings.GSGameUserSettings]");
            sb.AppendLine("DesiredScreenWidth=" + ResolutionSizeX);
            sb.AppendLine("DesiredScreenHeight=" + ResolutionSizeY);
            sb.AppendLine("StartLevelName=");
            sb.AppendLine("GMCommandList=()");
            sb.AppendLine("bNeverShowStartupUI=False");
            var ui = new StringBuilder();
            ui.Append("(");
            for (int i = 0; i < UiSettings.Count; i++)
            {
                if (i > 0) ui.Append(",");
                ui.Append("(\"" + UiSettings[i].Key + "\", \"" + UiSettings[i].Value + "\")");
            }
            ui.Append(")");
            sb.AppendLine(ui.ToString());
            sb.AppendLine("UISettingCustomData=()");
            sb.AppendLine("SettingpbTag=4");
            sb.AppendLine("PrivacyAgreement=1");
            sb.AppendLine("AgreementReaded=1");
            sb.AppendLine("FirstSettingFinish=True");
            sb.AppendLine("ArchiveMarkFinish=False");
            sb.AppendLine("CrashReportAgreement=0");
            sb.AppendLine("ShowCrashReportUI=0");
            sb.AppendLine("bUseVSync=False");
            sb.AppendLine("bUseDynamicResolution=False");
            sb.AppendLine("ResolutionSizeX=" + ResolutionSizeX);
            sb.AppendLine("ResolutionSizeY=" + ResolutionSizeY);
            sb.AppendLine("LastUserConfirmedResolutionSizeX=" + ResolutionSizeX);
            sb.AppendLine("LastUserConfirmedResolutionSizeY=" + ResolutionSizeY);
            sb.AppendLine("WindowPosX=-1");
            sb.AppendLine("WindowPosY=-1");
            sb.AppendLine("FullscreenMode=" + FullscreenMode);
            sb.AppendLine("LastConfirmedFullscreenMode=" + FullscreenMode);
            sb.AppendLine("PreferredFullscreenMode=" + FullscreenMode);
            sb.AppendLine("Version=5");
            sb.AppendLine("AudioQualityLevel=0");
            sb.AppendLine("LastConfirmedAudioQualityLevel=0");
            sb.AppendLine("FrameRateLimit=" + FrameRateLimit.ToString("0.000000", CultureInfo.InvariantCulture));
            sb.AppendLine("LastUserConfirmedDesiredScreenWidth=" + ResolutionSizeX);
            sb.AppendLine("LastUserConfirmedDesiredScreenHeight=" + ResolutionSizeY);
            sb.AppendLine("LastRecommendedScreenWidth=-1.000000");
            sb.AppendLine("LastRecommendedScreenHeight=-1.000000");
            sb.AppendLine("LastCPUBenchmarkResult=-1.000000");
            sb.AppendLine("LastGPUBenchmarkResult=-1.000000");
            sb.AppendLine("LastGPUBenchmarkMultiplier=1.000000");
            sb.AppendLine("bUseHDRDisplayOutput=False");
            sb.AppendLine("HDRDisplayOutputNits=1000");
            sb.AppendLine();
            sb.AppendLine("[ScalabilityGroups]");
            foreach (var kv in Scalability)
                sb.AppendLine(kv.Key + "=" + kv.Value);
            sb.AppendLine();
            sb.AppendLine("[RayTracing]");
            sb.AppendLine("r.RayTracing.EnableInGame=False");
            sb.AppendLine();
            sb.AppendLine("[GSRenderSetting]");
            sb.AppendLine("GSStreamingPoolSize=512");
            return sb.ToString();
        }

        public void RestoreOriginal()
        {
            if (OriginalIni != null) File.WriteAllText(IniPath, OriginalIni, new UTF8Encoding(false));
        }

        static int ParseInt(string v, int def)
        {
            int r; return int.TryParse(v, out r) ? r : def;
        }

        static double ParseDouble(string v, double def)
        {
            double r; return double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out r) ? r : def;
        }

        public string GetUi(string key, string def)
        {
            foreach (var kv in UiSettings) if (kv.Key == key) return kv.Value;
            return def;
        }

        public void SetUi(string key, string value)
        {
            for (int i = 0; i < UiSettings.Count; i++)
            {
                if (UiSettings[i].Key == key)
                {
                    UiSettings[i] = new KeyValuePair<string, string>(key, value);
                    return;
                }
            }
            UiSettings.Add(new KeyValuePair<string, string>(key, value));
        }

        public void SetScalability(string key, string value)
        {
            Scalability[key] = value;
        }

        /// <summary>
        /// CPU-профиль: максимум нагрузки на процессор, минимум на GPU.
        /// Пресет "Низк." (QualityLevel=3), минимальный рендер-скейл (33%),
        /// выключенные генерация кадров и апскейл, минимальные GPU-эффекты,
        /// при этом максимальные "CPU-тяжёлые" параметры: дальность прорисовки и растительность = Realistic (4).
        /// Шкала индивидуальных значений: 0=низк, 1=средн, 2=высок, 3=Ultra, 4=Realistic.
        /// Пресеты: 3=Низк., 0=Макс (Realistic).
        /// </summary>
        public void ApplyCpuProfile()
        {
            SetUi("Vsync", "0");
            SetUi("MotionBlur", "0");
            SetUi("Dlss", "0");
            SetUi("Dx12", "1");
            SetUi("SuperResolutionSampling", "0");
            SetUi("InsertFrame", "0");
            SetUi("Rtx", "0");
            SetUi("QualityLevel", "3");
            SetUi("ViewDistance", "4");
            SetUi("AntiAliasing", "0");
            SetUi("PostProcessing", "0");
            SetUi("ShadowQuality", "0");
            SetUi("TextureQuality", "0");
            SetUi("FxQuality", "0");
            SetUi("MaterialQuality", "0");
            SetUi("VegetationQuality", "4");
            SetUi("GlobalIllumination", "0");
            SetUi("ReflectionQuality", "0");
            SetUi("RtxLevel", "1");
            SetUi("ImageQuality", "360");
            SetUi("LockFrameRate", "0");
            SetUi("FrameRateQualityFirst", "0");
            Scalability["sg.ResolutionQuality"] = "33";
            Scalability["sg.ViewDistanceQuality"] = "4";
            Scalability["sg.AntiAliasingQuality"] = "0";
            Scalability["sg.ShadowQuality"] = "0";
            Scalability["sg.GlobalIlluminationQuality"] = "0";
            Scalability["sg.RayTracingQuality"] = "0";
            Scalability["sg.ReflectionQuality"] = "0";
            Scalability["sg.PostProcessQuality"] = "0";
            Scalability["sg.TextureQuality"] = "0";
            Scalability["sg.EffectsQuality"] = "0";
            Scalability["sg.FoliageQuality"] = "4";
            Scalability["sg.ShadingQuality"] = "0";
            FrameRateLimit = 0;
        }

        /// <summary>
        /// GPU-профиль: максимальная нагрузка на видеокарту.
        /// Максимальный пресет (QualityLevel=0 = Realistic), все индивидуальные значения на максимум (4),
        /// нативное разрешение (100% рендер-скейл), генерация кадров выключена (не искажает замер),
        /// RT выключен (AMD).
        /// </summary>
        public void ApplyGpuProfile()
        {
            SetUi("Vsync", "0");
            SetUi("MotionBlur", "2");
            SetUi("Dlss", "0");
            SetUi("Dx12", "1");
            SetUi("SuperResolutionSampling", "0");
            SetUi("InsertFrame", "0");
            SetUi("Rtx", "0");
            SetUi("QualityLevel", "0");
            SetUi("ViewDistance", "4");
            SetUi("AntiAliasing", "4");
            SetUi("PostProcessing", "4");
            SetUi("ShadowQuality", "4");
            SetUi("TextureQuality", "4");
            SetUi("FxQuality", "4");
            SetUi("MaterialQuality", "4");
            SetUi("VegetationQuality", "4");
            SetUi("GlobalIllumination", "4");
            SetUi("ReflectionQuality", "4");
            SetUi("RtxLevel", "1");
            SetUi("ImageQuality", "1080");
            SetUi("LockFrameRate", "0");
            SetUi("FrameRateQualityFirst", "0");
            Scalability["sg.ResolutionQuality"] = "100";
            Scalability["sg.ViewDistanceQuality"] = "4";
            Scalability["sg.AntiAliasingQuality"] = "4";
            Scalability["sg.ShadowQuality"] = "4";
            Scalability["sg.GlobalIlluminationQuality"] = "4";
            Scalability["sg.RayTracingQuality"] = "0";
            Scalability["sg.ReflectionQuality"] = "4";
            Scalability["sg.PostProcessQuality"] = "4";
            Scalability["sg.TextureQuality"] = "4";
            Scalability["sg.EffectsQuality"] = "4";
            Scalability["sg.FoliageQuality"] = "4";
            Scalability["sg.ShadingQuality"] = "4";
            FrameRateLimit = 0;
        }

        public string DescribeProfile(string name)
        {
            var sb = new StringBuilder();
            sb.AppendLine("  Рендер-скейл (ImageQuality):  " + GetUi("ImageQuality", "?") + " / 1080  (" + Math.Round(ParseDouble(GetUi("ImageQuality", "1080"), 1080) / 10.8) + "%)");
            sb.AppendLine("  Генерация кадров (InsertFrame): " + (GetUi("InsertFrame", "0") == "0" ? "выкл" : "вкл"));
            sb.AppendLine("  Апскейл (Dlss/FSR):           " + GetUi("Dlss", "?") + " / " + GetUi("SuperResolutionSampling", "?"));
            sb.AppendLine("  DX12: " + GetUi("Dx12", "?") + "   Vsync: " + GetUi("Vsync", "?") + "   MotionBlur: " + GetUi("MotionBlur", "?") + "   RT: " + GetUi("Rtx", "?"));
            sb.AppendLine("  ViewDistance: " + GetUi("ViewDistance", "?") + "   AntiAliasing: " + GetUi("AntiAliasing", "?") + "   PostProcessing: " + GetUi("PostProcessing", "?"));
            sb.AppendLine("  ShadowQuality: " + GetUi("ShadowQuality", "?") + "   TextureQuality: " + GetUi("TextureQuality", "?") + "   FxQuality: " + GetUi("FxQuality", "?"));
            sb.AppendLine("  MaterialQuality: " + GetUi("MaterialQuality", "?") + "   VegetationQuality: " + GetUi("VegetationQuality", "?") + "   GlobalIllumination: " + GetUi("GlobalIllumination", "?"));
            sb.AppendLine("  ReflectionQuality: " + GetUi("ReflectionQuality", "?"));
            return sb.ToString();
        }
    }
}
