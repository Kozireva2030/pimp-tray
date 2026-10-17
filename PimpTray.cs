// PMP Tray for Windows: a tray icon with a small panel next to the clock — temperature, CPU, GPU and memory load,
// Claude Code token usage per day (read from %USERPROFILE%\.claude\projects\**\*.jsonl) and, optionally,
// Workflow progress from status.md files in a projects folder you choose. Nothing is sent anywhere; the app only
// reads local files and Windows performance counters (PDH, English counter names, no admin rights needed).
// The tray icon shows CPU load or today's tokens.
//
// Unofficial. Not affiliated with Anthropic.
//
// Компилятор — csc из .NET Framework 4.8, поэтому C# 5: без $"" и ?.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PimpWinTray
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32 entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32 entry);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

        const string RelaunchVar = "PIMPWIN_RELAUNCHED";

        // Запущен из чата Claude — значит, внутри пакета Claude (MSIX): запись автозапуска уходит в скрытый реестр
        // пакета, Windows при входе её не видит, а сам значок закрывается вместе с Claude. Своего имени пакета у
        // такого процесса нет, поэтому узнаём по предкам: среди них claude.exe. Такой запуск перезапускаем через
        // проводник — он стартует программу обычным образом, вне пакета.
        static bool StartedFromClaude()
        {
            var snap = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return false;
            try
            {
                var parent = new Dictionary<uint, uint>();
                var name = new Dictionary<uint, string>();
                var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                {
                    parent[e.th32ProcessID] = e.th32ParentProcessID;
                    name[e.th32ProcessID] = e.szExeFile ?? "";
                }
                uint pid = (uint)Process.GetCurrentProcess().Id;
                for (int depth = 0; depth < 12 && parent.TryGetValue(pid, out pid) && pid != 0; depth++)
                {
                    string exe;
                    if (!name.TryGetValue(pid, out exe)) break;
                    if (exe.Equals("claude.exe", StringComparison.OrdinalIgnoreCase)) return true;
                    if (exe.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) return false;
                }
                return false;
            }
            catch { return false; }
            finally { CloseHandle(snap); }
        }

        [STAThread]
        static void Main()
        {
            if (StartedFromClaude())
            {
                if (Environment.GetEnvironmentVariable(RelaunchVar) == null)
                {
                    try
                    {
                        Environment.SetEnvironmentVariable(RelaunchVar, "1");
                        Process.Start("explorer.exe", "\"" + Application.ExecutablePath + "\"");
                        return;
                    }
                    catch (Exception e) { Log.Write("relaunch: " + e.Message); }
                }
                else Log.Write("проводник не вывел из пакета Claude: автозапуск может не сохраниться");
            }
            bool created;
            using (var mutex = new Mutex(true, "Local\\" + Log.AppName, out created))
            {
                if (!created) return;
                try { SetProcessDPIAware(); } catch { }
                // Ошибка в панели — в журнал, без окна «Необработанное исключение» поверх работы.
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => Log.Write("ошибка: " + e.Exception);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
                GC.KeepAlive(mutex);
            }
        }
    }

    static class Log
    {
        // Имя для мьютекса, папки настроек и автозапуска: публичная сборка живёт отдельно и не мешает полной.
        public const string AppName = "PimpTray";
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
        static readonly object gate = new object();

        // Пишут и главный поток, и фоновые — по одному.
        public static void Write(string text)
        {
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    var path = Path.Combine(Dir, "log.txt");
                    if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + text + Environment.NewLine);
                }
                catch { }
            }
        }
    }

    enum Level { Normal, Low, Critical }

    // ---------------------------------------------------------------- датчики

    static class Pdh
    {
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhOpenQuery(string src, IntPtr user, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
        [DllImport("pdh.dll")] public static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint count, IntPtr buffer);

        const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000, PDH_MORE_DATA = 0x800007D2;

        // Все экземпляры счётчика: имя -> значение.
        public static List<KeyValuePair<string, double>> Read(IntPtr counter)
        {
            var list = new List<KeyValuePair<string, double>>();
            if (counter == IntPtr.Zero) return list;
            uint size = 0, count;
            uint rc = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, IntPtr.Zero);
            if (rc != PDH_MORE_DATA || size == 0) return list;
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                rc = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, buf);
                if (rc != 0) return list;
                int stride = IntPtr.Size == 8 ? 24 : 16;
                int valueOffset = IntPtr.Size == 8 ? 16 : 8;
                int statusOffset = IntPtr.Size;
                for (int i = 0; i < count; i++)
                {
                    var item = buf + i * stride;
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                    uint status = (uint)Marshal.ReadInt32(item + statusOffset);
                    if (status != 0 && status != 1) continue;   // PDH_CSTATUS_VALID_DATA / NEW_DATA
                    double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + valueOffset));
                    list.Add(new KeyValuePair<string, double>(name, v));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return list;
        }
    }

    class Sample
    {
        public DateTime At;
        public double? Temp, Cpu, Gpu, MemUsedGb;
    }

    class Sensors
    {
        [StructLayout(LayoutKind.Sequential)]
        class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX m);

        IntPtr query, temp, cpu, gpu;
        public double MemTotalGb;
        public Sample Now = new Sample();
        public readonly List<Sample> History = new List<Sample>();
        public const int HistoryEverySeconds = 30;
        public const double HistoryHours = 3;
        DateTime lastHistory = DateTime.MinValue;
        public string GpuName = "";
        // Встроенная видеокарта (AMD Radeon Graphics без номера, Intel): градусы у неё общие с процессором.
        public bool IntegratedGpu { get { return Regex.IsMatch(GpuName, @"^AMD Radeon\(TM\) Graphics$|Intel", RegexOptions.IgnoreCase); } }

        public Sensors()
        {
            try
            {
                if (Pdh.PdhOpenQuery(null, IntPtr.Zero, out query) != 0) { query = IntPtr.Zero; return; }
                Pdh.PdhAddEnglishCounter(query, @"\Thermal Zone Information(*)\High Precision Temperature", IntPtr.Zero, out temp);
                if (Pdh.PdhAddEnglishCounter(query, @"\Processor Information(_Total)\% Processor Utility", IntPtr.Zero, out cpu) != 0)
                    Pdh.PdhAddEnglishCounter(query, @"\Processor(_Total)\% Processor Time", IntPtr.Zero, out cpu);
                Pdh.PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out gpu);
                Pdh.PdhCollectQueryData(query);
            }
            catch (Exception e) { Log.Write("pdh: " + e.Message); }
            try
            {
                foreach (var key in new[] { @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000" })
                    using (var k = Registry.LocalMachine.OpenSubKey(key))
                        if (k != null) GpuName = (k.GetValue("DriverDesc") as string) ?? "";
            }
            catch { }
        }

        public void Tick()
        {
            var s = new Sample { At = DateTime.Now };
            try
            {
                if (query != IntPtr.Zero && Pdh.PdhCollectQueryData(query) == 0)
                {
                    // Тепловые зоны ACPI: десятые доли кельвина; берём самую горячую. 0 и мусор — прочь.
                    double best = double.NaN;
                    foreach (var kv in Pdh.Read(temp))
                    {
                        var c = kv.Value / 10.0 - 273.15;
                        if (c > 5 && c < 125 && (double.IsNaN(best) || c > best)) best = c;
                    }
                    if (!double.IsNaN(best)) s.Temp = best;
                    var cpuList = Pdh.Read(cpu);
                    if (cpuList.Count > 0) s.Cpu = Math.Max(0, Math.Min(100, cpuList[0].Value));
                    // Видеокарта: как в Диспетчере задач — самый загруженный тип движка, сумма по процессам.
                    var byEngine = new Dictionary<string, double>();
                    foreach (var kv in Pdh.Read(gpu))
                    {
                        var m = Regex.Match(kv.Key, @"luid_(0x\w+_0x\w+)_phys_(\d+)_eng_(\d+)_engtype_");
                        var key = m.Success ? m.Groups[1].Value + ":" + m.Groups[2].Value + ":" + m.Groups[3].Value : kv.Key;
                        double sum;
                        byEngine.TryGetValue(key, out sum);
                        byEngine[key] = sum + kv.Value;
                    }
                    if (byEngine.Count > 0) s.Gpu = Math.Max(0, Math.Min(100, byEngine.Values.Max()));
                }
            }
            catch (Exception e) { Log.Write("sensors: " + e.Message); }
            try
            {
                var m = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(m))
                {
                    // ГБ как в Диспетчере задач (по 1024³).
                    MemTotalGb = m.ullTotalPhys / 1073741824.0;
                    s.MemUsedGb = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
                }
            }
            catch { }
            Now = s;
            if ((s.At - lastHistory).TotalSeconds >= HistoryEverySeconds - 1)
            {
                lastHistory = s.At;
                History.Add(s);
                var edge = s.At.AddHours(-HistoryHours);
                History.RemoveAll(x => x.At < edge);
            }
        }
    }

    // ---------------------------------------------------------------- слова

    // ---------------------------------------------------------------- язык интерфейса

    // Язык PMP (пожелание 17.10.2026: «перевод всего интерфейса PMP на английский — переключатель языка RU/EN»): все
    // строки интерфейса — в одной таблице ниже, ключ → русский и английский рядом. Выбор помнится в settings.json
    // («language»: ru / en); не выбран — язык интерфейса Windows (русский → RU, иначе EN). Смена — сразу, без перезапуска:
    // панель, меню и подсказки берут строки при каждом рисовании. Данные (status.md проектов, имена чатов, проектов,
    // своих тем, моделей) не переводятся.
    static class L
    {
        public const string Key = "language";

        public static bool En
        {
            get
            {
                var v = Settings.GetString(Key, null);
                if (v == "en") return true;
                if (v == "ru") return false;
                return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ru";
            }
        }

        public static void Set(bool en) { Settings.SetValue(Key, en ? "en" : "ru"); }

        public static CultureInfo Culture { get { return CultureInfo.GetCultureInfo(En ? "en-US" : "ru-RU"); } }

        // Ключа нет в таблице — виден сам ключ (ошибку видно сразу, панель не падает).
        public static string T(string key)
        {
            string[] pair;
            return Table.TryGetValue(key, out pair) ? pair[En ? 1 : 0] : key;
        }

        public static string F(string key, params object[] args) { return string.Format(T(key), args); }

        // Ярлык блока мелкими прописными — тем же словом, что в меню: «Память» → «ПАМЯТЬ», «Memory» → «MEMORY».
        public static string Caps(string key) { return T(key).ToUpper(Culture); }

        // Число со словом: по-русски три формы («день|дня|дней»), по-английски две («day|days»).
        public static string P(int n, string key)
        {
            var forms = T(key).Split('|');
            if (En) return n == 1 ? forms[0] : forms[forms.Length - 1];
            return forms.Length < 3 ? forms[0] : Words.Plural(n, forms[0], forms[1], forms[2]);
        }

        // Дробная часть: по-русски запятая, по-английски точка.
        public static string Dec(string s) { return En ? s : s.Replace('.', ','); }

        // Таблица: ключ, русский, английский — по три в строке. Rows объявлен раньше Table: статические поля
        // заполняются по порядку.
        static readonly string[] Rows =
        {
            // язык
            "lang.title", "Язык", "Language",
            // единицы, числа, дни
            "fmt.pct", "{0} %", "{0}%",
            "fmt.dayMonth", "d MMMM", "MMMM d",
            "pl.step", "шаг|шага|шагов", "step|steps",
            "dur.m", "{0} мин", "{0}m",
            "dur.h", "{0} ч", "{0}h",
            "span.3h", "за 3 ч", "past 3h",
            "span.hm", "за {0} ч {1} мин", "past {0}h {1}m",
            "span.m", "за {0} мин", "past {0}m",
            "span.m1", "за 1 мин", "past minute",
            "num.thousand", "{0} тыс", "{0}K",
            "num.million", "{0} млн", "{0}M",
            "num.billion", "{0} млрд", "{0}B",
            // цвет PMP
            "accent.red", "Красный", "Red",
            "accent.orange", "Оранжевый", "Orange",
            "accent.amber", "Янтарь", "Amber",
            "accent.green", "Зелёный", "Green",
            "accent.teal", "Бирюзовый", "Teal",
            "accent.blue", "Синий", "Blue",
            "accent.violet", "Фиолетовый", "Violet",
            "accent.pink", "Розовый", "Pink",
            "accent.graphite", "Графит", "Graphite",
            "accent.tip", "Цвет PMP", "PMP color",
            "accent.caps", "ЦВЕТ PMP", "PMP COLOR",
            "accent.note", "значок у часов и эта панель", "tray icon and this panel",
            // блоки панели
            "widget.temperature", "Температура", "Temperature",
            "widget.memory", "Память", "Memory",
            "widget.cpu", "Процессор", "CPU",
            "widget.gpu", "Видеокарта", "GPU",
            "widget.tokens", "Токены", "Tokens",
            "widget.workflow", "Workflow", "Workflows",
            "header.settingsTip", "Настройки (Ctrl+,)", "Settings (Ctrl+,)",
            "fold.collapseAll", "Свернуть всё", "Collapse all",
            "fold.expandAll", "Развернуть всё", "Expand all",
            "fold.collapseAllTip", "Свернуть все блоки панели", "Collapse all panel blocks",
            "fold.expandAllTip", "Развернуть все блоки панели", "Expand all panel blocks",
            "fold.collapse", "Свернуть", "Collapse",
            "fold.expand", "Развернуть", "Expand",
            "fold.collapseLc", "свернуть", "Click to collapse",
            "fold.expandLc", "развернуть", "Click to expand",
            "hidden.title", "Скрытые", "Hidden",
            "hidden.restoreTip", "Вернуть блок «{0}»", "Show {0} again",
            "menu.half", "Половина — рядом с соседним", "Half width",
            "menu.full", "Во всю ширину", "Full width",
            "menu.hide", "Скрыть блок", "Hide",
            "menu.keepOpen", "Оставлять раскрытым", "Keep expanded",
            "menu.restore", "Вернуть как было", "Reset layout",
            // железо
            "mem.gb", "{0} из {1} ГБ", "{0} of {1} GB",
            "mem.tip", "Зелёный — до 75 %, жёлтый — 75–90 %, красный — выше", "Green under 75%, yellow 75–90%, red above",
            "mem.none", "Память не читается", "Memory unavailable",
            "temp.none", "нет датчика", "no sensor",
            "temp.tip", "Зелёный — до 80°, жёлтый — 80–90°, красный — выше", "Green under 80°, yellow 80–90°, red above",
            "temp.now", "сейчас {0}°", "now {0}°",
            "temp.silent", "Датчик не отвечает", "Sensor not responding",
            "temp.igpuShort", "видеокарта — в процессоре", "GPU shares this sensor",
            "temp.igpu", "Видеокарта встроена в процессор — градусы общие", "Integrated GPU — shares the CPU temperature",
            "load.tip", "Зелёный — до 70 %, жёлтый — 70–90 %, красный — выше", "Green under 70%, yellow 70–90%, red above",
            // Workflow
            "flow.reading", "Читаю проекты…", "Reading projects…",
            "flow.nothingRunning", "Сейчас ничего не идёт", "Nothing running right now",
            "flow.aheadCollapseTip", "Свернуть запланированные", "Collapse planned",
            "flow.aheadExpandTip", "Запланированные Workflow — по одному на строку", "Planned workflows, one per line",
            "flow.ahead", "Впереди · {0}", "Ahead · {0}",
            "flow.pageOf", "{0}–{1} из {2}", "{0}–{1} of {2}",
            "flow.prevPage", "Предыдущие {0}", "Previous {0}",
            "flow.nextPage", "Следующие {0}", "Next {0}",
            "count.done", "готово", "done",
            "count.run", "идёт|идут|идут", "running|running",
            "count.wait", "ждёт|ждут|ждут", "waiting|waiting",
            "count.fail", "упал|упали|упали", "failed|failed",
            "count.plan", "впереди", "ahead",
            "count.noneDone", "Готовых за сутки нет", "Nothing finished in the last 24 h",
            "count.nonePlan", "Запланированных нет", "Nothing planned",
            "legend.nothing", "Пока нечего показать", "Nothing to show yet",
            "legend.again", "Нажми выбранное ещё раз — снова идущие", "Click again to show running",
            "legend.pickOrBack", "Нажми число — покажу только эти; выбранное — снова идущие", "Click a number to filter · click it again to clear",
            "legend.pick", "Нажми число — покажу только эти", "Click a number to filter",
            "legend.done", "готово — зелёная полоска", "done — green bar",
            "legend.runPulse", "идёт — рамочка, мерцает", "running — pulsing outline",
            "legend.run", "идёт — рамочка", "running — outline",
            "legend.wait", "ждёт тебя — жёлтая", "waiting for you — yellow",
            "legend.fail", "упал — красная", "failed — red",
            "legend.plan", "впереди — серая", "ahead — gray",
            "stage.plan", "план", "plan",
            "stage.review", "критик", "review",
            "stage.test", "тест", "test",
            "stage.code", "кодинг", "coding",
            "stage.research", "исследование", "research",
            "who.me", "я", "me",
            "row.waitsYou", "ждёт тебя", "waiting for you",
            "row.failed", "упал", "failed",
            "row.finishedAt", "в {0}", "at {0}",
            "row.stepsOf", "{0} из {1}", "{0} of {1}",
            "row.tip", "Подробнее: этапы, кто и на чём, чат", "Details: stages, who's on what, chat",
            "when.notStarted", "Ещё не начат", "Not started yet",
            "when.finished", "Завершился в {0}", "Finished at {0}",
            "when.took", " · занял {0}", " · took {0}",
            "when.startedAt", "Начался в {0}", "Started at {0}",
            "when.justStarted", "Только начат, в {0}", "Just started, at {0}",
            "when.startedAgo", "Начался {0} назад, в {1}", "Started {0} ago, at {1}",
            "when.ends", " · закончит в {0}", " · ends at {0}",
            "when.started", "только начат", "just started",
            "card.noRoles", "Роли не записаны в status.md", "No roles recorded in status.md",
            "card.waitsYou", "Ждёт тебя: {0}", "Waiting for you: {0}",
            "card.prevTip", "Предыдущий Workflow (←)", "Previous workflow (←)",
            "card.nextTip", "Следующий Workflow (→)", "Next workflow (→)",
            "card.closeTip", "К списку Workflow (Esc)", "Back to workflows (Esc)",
            // токены
            "tok.counting", "считаю…", "counting…",
            "tok.noData", "нет данных", "no data",
            "tok.countingTip", "Считаю токены по файлам Claude Code", "Counting tokens from Claude Code files",
            "tok.cardTip", "Токены по дням за 7 дней, справа — сегодня · посчитано в {0}", "Tokens per day for 7 days, today on the right · counted at {0}",
            "tok.today", "сегодня", "today",
            "tok.week", "7 дней", "7 days",
            "period.today", "Сегодня", "Today",
            "period.yesterday", "Вчера", "Yesterday",
            "period.week", "7 дней", "7 days",
            "tok.countingLong", "Считаю токены…", "Counting tokens…",
            "tok.firstRun", "Первый раз — по файлам Claude Code за 14 дней", "First run — reading 14 days of Claude Code files",
            "tok.models", "Модели", "Models",
            "tok.projects", "Проекты", "Projects",
            "tok.footer", "по файлам Claude Code · посчитано в {0}", "from Claude Code files · counted at {0}",
            "tok.unit", "токенов", "tokens",
            "tok.yesterdayFull", "вчера за весь день — {0}", "yesterday, full day — {0}",
            "tok.same", "столько же", "no change",
            "tok.compare", "{0} против {1} · было {2}", "{0} vs {1} · was {2}",
            "tok.dayBefore", "позавчера", "the day before",
            "tok.prevWeek", "прошлых 7 дней", "previous 7 days",
            "tok.barTip", "{0} — {1} токенов", "{0} — {1} tokens",
            "tok.empty", "за этот период токенов нет", "no tokens in this period",
            "tok.more", "ещё {0}: {1}", "{0} more: {1}",
            "tok.other", "другие", "other",
            "tok.claudeChats", "Чаты Claude", "Claude chats",
            // настройки
            "settings.title", "Настройки", "Settings",
            "settings.more", "Дополнительно", "Advanced",
            "settings.about", "О программе", "About",
            "settings.quit", "Выйти", "Quit",
            "more.autostart", "Запускать вместе с Windows", "Start with Windows",
            "back.tip", "Назад (Esc)", "Back (Esc)",
            // значок у часов, меню, окна
            "tray.settings", "Настройки…", "Settings…",
            "tray.tipCpu", "PMP — процессор: {0} %", "PMP — CPU: {0}%",
            "tray.tipTokens", "PMP — токены за сегодня: {0}", "PMP — tokens today: {0}",
            "tray.counting", "PMP — считаю…", "PMP — counting…",
            // значок и Workflow в настройках
            "icon.title", "Значок у часов", "Tray icon",
            "icon.note", "Число на значке: нагрузка процессора или токены Claude Code за сегодня", "The number on the tray icon: CPU load or today's Claude Code tokens",
            "flow.folder", "Папка проектов", "Projects folder",
            "flow.off", "выключено", "off",
            "flow.choose", "Выбрать папку…", "Choose folder…",
            "flow.change", "Другая папка…", "Change folder…",
            "flow.disable", "Выключить Workflow", "Turn off Workflows",
            "flow.note", "Блок Workflow читает status.md из папки docs, audit или work каждого проекта в этой папке. Формат — в README.", "The Workflows block reads status.md from the docs, audit or work folder of each project in this folder. The format is in the README.",
            "flow.pick", "Папка, в которой лежат проекты (по папке на проект)", "The folder that holds your projects (one subfolder per project)",
            "about.title", "О программе", "About PMP",
            "about.text", "PMP Tray для Windows\n\nНеофициальная программа, не связана с Anthropic.\n\nЗначок у часов: нагрузка процессора или токены Claude Code за сегодня. Панель — температура, память, процессор, видеокарта, токены по дням и, если выбрана папка проектов, Workflow из status.md; правой кнопкой по блоку — ширина, «Скрыть» и язык, долгим нажатием — перетащить. Программа только читает файлы на этом компьютере и счётчики Windows и ничего никуда не отправляет.\n\nАвтор — Kozireva2030. Лицензия MIT.",
                "PMP Tray for Windows\n\nUnofficial. Not affiliated with Anthropic.\n\nThe tray icon shows CPU load or today's Claude Code tokens. The panel has temperature, memory, CPU, GPU, tokens per day and, if you pick a projects folder, Workflows from status.md; right-click a block for width, Hide and language, press and hold to drag it. The app only reads files on this computer and Windows performance counters; nothing is sent anywhere.\n\nBy Kozireva2030. MIT License.",
        };

        static readonly Dictionary<string, string[]> Table = Build();

        static Dictionary<string, string[]> Build()
        {
            var d = new Dictionary<string, string[]>();
            for (int i = 0; i + 2 < Rows.Length; i += 3) d[Rows[i]] = new[] { Rows[i + 1], Rows[i + 2] };
            return d;
        }
    }

    static class Words
    {

        public static string Plural(int n, string one, string few, string many)
        {
            int tens = n % 100, ones = n % 10;
            if (tens >= 11 && tens <= 14) return many;
            return ones == 1 ? one : (ones >= 2 && ones <= 4 ? few : many);
        }

        public static string Gb(double gb)
        {
            var s = gb.ToString("0.0", CultureInfo.InvariantCulture);
            return L.Dec(s.EndsWith(".0") ? s.Substring(0, s.Length - 2) : s);
        }

        // «за 3 ч» / «за 1 ч 30 мин» / «за 19 мин».
        public static string Span(TimeSpan span)
        {
            int minutes = (int)Math.Round(span.TotalMinutes);
            if (minutes >= 175) return L.T("span.3h");
            if (minutes >= 60) return L.F("span.hm", minutes / 60, minutes % 60);
            return minutes <= 1 ? L.T("span.m1") : L.F("span.m", minutes);
        }
    }

    // ---------------------------------------------------------------- цвета

    static class Palette
    {
        // Цвет PMP (StatusIcon.swift, PMPAccent): девять по кругу, графит последним; id, ключ имени (L), цвет, цвет на светлой
        // панели задач. Красит плашку PMP у часов и в панели, переключатели и полоски.
        public static readonly string[][] Accents =
        {
            new[] { "red", "accent.red", "F0524F", "F0524F" }, new[] { "orange", "accent.orange", "FB7B30", "FB7B30" },
            new[] { "amber", "accent.amber", "F59E0B", "F59E0B" }, new[] { "green", "accent.green", "5FC48F", "5FC48F" },
            new[] { "teal", "accent.teal", "5BC8C0", "3AAFA7" }, new[] { "blue", "accent.blue", "7AA2F7", "5B86EC" },
            new[] { "violet", "accent.violet", "A98BFA", "8E6CF2" }, new[] { "pink", "accent.pink", "F06BB4", "F06BB4" },
            new[] { "graphite", "accent.graphite", "ECECEE", "3A3A3C" },
        };
        // Наведение на цвет в карточке «Цвет PMP»: панель примеряет его, значок у часов — нет.
        public static string PreviewAccent;

        public static string AccentId
        {
            get
            {
                var id = Settings.GetString(Settings.Accent, "violet");
                return Accents.Any(a => a[0] == id) ? id : "violet";
            }
        }

        static string[] AccentRow(string id) { return Accents.FirstOrDefault(a => a[0] == id) ?? Accents[6]; }
        public static Color AccentOf(string id, bool lightBar) { return Css("#" + AccentRow(id)[lightBar ? 3 : 2]); }
        public static Color Accent { get { return AccentOf(PreviewAccent ?? AccentId, false); } }

        public static Color Css(string hex)
        {
            var t = (hex ?? "").Trim().TrimStart('#');
            int value;
            if (t.Length != 6 || !int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) return Color.Gray;
            return Color.FromArgb((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
        }

        public static readonly Color Yellow = Color.FromArgb(0xF5, 0xC5, 0x18);
        public static readonly Color Red = Color.FromArgb(0xF0, 0x52, 0x4F);
        public static readonly Color Green = Color.FromArgb(0x5F, 0xC4, 0x8F);
        public static readonly Color Bg = Color.FromArgb(0x1E, 0x1E, 0x20);
        public static readonly Color Tile = Color.FromArgb(0x27, 0x27, 0x2A);
        public static readonly Color TileLine = Color.FromArgb(0x38, 0x38, 0x3C);
        public static readonly Color Text = Color.FromArgb(0xEC, 0xEC, 0xEE);
        public static readonly Color Dim = Color.FromArgb(0x9A, 0x9A, 0xA0);
        public static readonly Color Track = Color.FromArgb(0x3A, 0x3A, 0x3E);

        public static Color ForLevel(Level level, Color normal)
        {
            if (level == Level.Critical) return Red;
            if (level == Level.Low) return Yellow;
            return normal;
        }

        // Железо цветом состояния (пожелание 17.10.2026: «зелёный — нормально, жёлтый — много, красный — критично»):
        // число в заголовке и график блока — одним цветом. Пороги — README, «Железо».
        public static Color ForTemp(double c)
        {
            if (c >= 90) return Red;
            if (c >= 80) return Yellow;
            return Green;
        }

        public static Color ForLoad(double percent)
        {
            if (percent >= 90) return Red;
            if (percent >= 70) return Yellow;
            return Green;
        }

        public static Color ForMemory(double percent)
        {
            if (percent >= 90) return Red;
            if (percent >= 75) return Yellow;
            return Green;
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // ---------------------------------------------------------------- значок у часов

    static class TrayIcon
    {
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string name);

        // Размер значка в пикселях — по масштабу монитора с панелью задач, а не по масштабу на момент входа
        // в Windows: иначе при другом мониторе Windows растягивает значок и он мылится.
        public static int Size()
        {
            try
            {
                var taskbar = FindWindow("Shell_TrayWnd", null);
                uint dpi = taskbar == IntPtr.Zero ? 0 : GetDpiForWindow(taskbar);
                if (dpi > 0) return Math.Max(16, GetSystemMetricsForDpi(49 /* SM_CXSMICON */, dpi));
            }
            catch { }
            return Math.Max(16, GetSystemMetrics(49));
        }

        public static bool TaskbarLight()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    return v is int && (int)v == 1;
                }
            }
            catch { return false; }
        }

        // Плашка цвета PMP с числом «израсходовано за неделю» тёмной цифрой; мало осталось — плашка жёлтая,
        // совсем мало — красная. Цифр нет — контур и «–». Рисуется сразу в размер значка при текущем масштабе
        // экрана (SM_CXSMICON для масштаба панели задач: 16 при 100 %, 20 при 125 %, 24 при 150 %) и упаковывается в ICO с 32-битной
        // прозрачностью: Bitmap.GetHicon её терял, края цифры и углов выходили рублеными.
        public static Icon Make(string text, Level level, bool outlined)
        {
            string label = string.IsNullOrEmpty(text) ? "–" : text;
            int size = Size();
            using (var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);
                    bool light = TaskbarLight();
                    var badge = Palette.ForLevel(level, Palette.AccentOf(Palette.AccentId, light));
                    float radius = size * 0.24f;
                    if (outlined)
                    {
                        var fg = light ? Color.FromArgb(220, 20, 20, 22) : Color.FromArgb(235, 236, 236, 238);
                        float w = Math.Max(1f, size / 14f);
                        using (var path = Palette.Round(new RectangleF(w / 2, w / 2, size - w, size - w), radius))
                        using (var pen = new Pen(fg, w)) g.DrawPath(pen, path);
                        DrawCentered(g, label, size, fg);
                    }
                    else
                    {
                        using (var path = Palette.Round(new RectangleF(0, 0, size, size), radius))
                        using (var brush = new SolidBrush(badge)) g.FillPath(brush, path);
                        DrawCentered(g, label, size, Color.FromArgb(0x1B, 0x16, 0x2E));
                    }
                }
                return ToIcon(bmp);
            }
        }

        // Токены на значке — не длиннее четырёх знаков: 850, 12K, 3.2M, 41M, 1.2B.
        public static string Compact(long v)
        {
            Func<double, string, string> one = (x, unit) => (x < 10 ? x.ToString("0.#", CultureInfo.InvariantCulture) : x.ToString("0", CultureInfo.InvariantCulture)) + unit;
            if (v >= 1000000000) return one(v / 1e9, "B");
            if (v >= 999500) return one(v / 1e6, "M");
            if (v >= 1000) return (v / 1000.0).ToString("0", CultureInfo.InvariantCulture) + "K";
            return v.ToString(CultureInfo.InvariantCulture);
        }

        // Цифры вписываются в плашку: высота — 70 % значка, ширина — до краёв без двух пикселей с каждой стороны.
        static void DrawCentered(Graphics g, string label, int size, Color color)
        {
            using (var family = new FontFamily("Segoe UI"))
            using (var path = new GraphicsPath())
            {
                path.AddString(label, family, (int)FontStyle.Bold, 100f, PointF.Empty, StringFormat.GenericTypographic);
                var b = path.GetBounds();
                if (b.Width <= 0 || b.Height <= 0) return;
                float maxH = label == "–" ? size * 0.12f : size * 0.70f;
                float scale = Math.Min((size - 4f) / b.Width, (label == "–" ? size * 0.5f : maxH) / b.Height);
                if (label == "–") scale = (size * 0.5f) / b.Width;
                using (var m = new Matrix())
                {
                    float w = b.Width * scale, h = b.Height * scale;
                    // Целые пиксели слева и сверху — ровнее штрихи цифры.
                    float x = (float)Math.Round((size - w) / 2), y = (float)Math.Round((size - h) / 2);
                    m.Translate(x, y);
                    m.Scale(scale, scale);
                    m.Translate(-b.X, -b.Y);
                    path.Transform(m);
                }
                using (var brush = new SolidBrush(color)) g.FillPath(brush, path);
            }
        }

        // ICO с одним 32-битным BGRA-кадром и пустой маской: прозрачность краёв сохраняется как есть.
        static Icon ToIcon(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            int maskStride = ((w + 31) / 32) * 4;
            int pixels = w * h * 4, mask = maskStride * h;
            using (var ms = new MemoryStream())
            using (var wr = new BinaryWriter(ms))
            {
                wr.Write((ushort)0); wr.Write((ushort)1); wr.Write((ushort)1);
                wr.Write((byte)(w >= 256 ? 0 : w)); wr.Write((byte)(h >= 256 ? 0 : h));
                wr.Write((byte)0); wr.Write((byte)0); wr.Write((ushort)1); wr.Write((ushort)32);
                wr.Write((uint)(40 + pixels + mask)); wr.Write((uint)22);
                wr.Write((uint)40); wr.Write(w); wr.Write(h * 2); wr.Write((ushort)1); wr.Write((ushort)32);
                wr.Write((uint)0); wr.Write((uint)(pixels + mask)); wr.Write(0); wr.Write(0); wr.Write((uint)0); wr.Write((uint)0);
                for (int y = h - 1; y >= 0; y--)
                    for (int x = 0; x < w; x++)
                    {
                        var c = bmp.GetPixel(x, y);
                        wr.Write(c.B); wr.Write(c.G); wr.Write(c.R); wr.Write(c.A);
                    }
                wr.Write(new byte[mask]);
                wr.Flush();
                ms.Position = 0;
                return new Icon(ms, w, h);
            }
        }
    }

    // ---------------------------------------------------------------- настройки и команды

    // Настройки PMP для Windows: %LOCALAPPDATA%\PimpTray\settings.json — да/нет, числа и строки.
    // Ключ есть — человек выбирал сам; нет — умолчание.
    static class Settings
    {
        public const string ShowTemp = "showTemp", ShowMemory = "showMemory", ShowLoad = "showLoad";
        public const string Accent = "pmpAccent", ShowWorkflow = "showWorkflow", ShowTokens = "showTokens", WorkflowOpen = "panelWorkflowOpen",
            // «Впереди · N» в блоке Workflow: свернула — свернулось и помнится (пожелание 17.10.2026), по умолчанию свёрнуто
            WorkflowAheadOpen = "panelWorkflowAheadOpen";
        // Что показывает значок у часов: cpu (по умолчанию) или tokens; корень проектов для блока Workflow (нет — выключен).
        public const string TrayShows = "trayShows", WorkflowRoot = "workflowRoot";
        // Блоки панели развёрнуты: пара «Процессор + Видеокарта» — одним ключом, остальные — каждый своим.
        public const string TempOpen = "panelTempOpen", LoadOpen = "panelLoadOpen", MemoryOpen = "panelMemoryOpen";
        // Раскладка блоков панели: [{id, wide, hidden?}] по порядку; совпала с умолчанием — ключа нет.
        // Прежние выключатели show* — только для переезда в «hidden» при первом запуске.
        public const string PanelLayout = "panelLayout";
        static readonly string FilePath = Path.Combine(Log.Dir, "settings.json");
        static Dictionary<string, object> map;

        static Dictionary<string, object> Map()
        {
            if (map != null) return map;
            map = new Dictionary<string, object>();
            try
            {
                if (File.Exists(FilePath))
                {
                    var raw = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(FilePath)) as IDictionary<string, object>;
                    if (raw != null) map = new Dictionary<string, object>(raw);
                }
            }
            catch (Exception e) { Log.Write("settings read: " + e.Message); }
            return map;
        }

        public static bool Get(string key, bool fallback)
        {
            object v;
            return Map().TryGetValue(key, out v) && v is bool ? (bool)v : fallback;
        }

        public static void Set(string key, bool value) { SetValue(key, value); }

        public static bool Has(string key) { return Map().ContainsKey(key); }

        public static int GetInt(string key, int fallback)
        {
            object v;
            if (!Map().TryGetValue(key, out v) || v == null || v is bool || v is string) return fallback;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { return fallback; }
        }

        public static string GetString(string key, string fallback)
        {
            object v;
            return Map().TryGetValue(key, out v) && v is string ? (string)v : fallback;
        }

        public static object GetRaw(string key)
        {
            object v;
            return Map().TryGetValue(key, out v) ? v : null;
        }

        // null — ключ убрать (выбора больше нет).
        public static void SetValue(string key, object value)
        {
            if (value == null) Map().Remove(key); else Map()[key] = value;
            try
            {
                Directory.CreateDirectory(Log.Dir);
                File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(map), new UTF8Encoding(false));
            }
            catch (Exception e) { Log.Write("settings write: " + e.Message); }
        }
    }

    // Объект JSON с порядком полей, в каком его собрали: у команд порядок — контракт (scope, title, match,
    // preview, затем слои theme, font, size, frame, look). Значение C#-null — поля нет вовсе, Json.Null — null.
    class JObj : IEnumerable<KeyValuePair<string, object>>
    {
        readonly List<KeyValuePair<string, object>> fields = new List<KeyValuePair<string, object>>();

        public JObj Add(string key, object value)
        {
            if (value != null) fields.Add(new KeyValuePair<string, object>(key, value));
            return this;
        }

        public int Count { get { return fields.Count; } }
        public IEnumerator<KeyValuePair<string, object>> GetEnumerator() { return fields.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() { return fields.GetEnumerator(); }
        public override string ToString() { return Json.Write(this); }
    }

    static class Json
    {
        public static readonly object Null = DBNull.Value;

        // Экранирование: кавычка, обратная черта и управляющие — \u00xx.
        public static string Str(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < 0x20 || c == 0x7F) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        public static string Write(object v)
        {
            if (v == null || v is DBNull) return "null";
            if (v is string) return Str((string)v);
            if (v is bool) return (bool)v ? "true" : "false";
            if (v is int || v is long) return Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture);
            if (v is double || v is float || v is decimal)
            {
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                return double.IsNaN(d) || double.IsInfinity(d) ? "null" : d.ToString("0.######", CultureInfo.InvariantCulture);
            }
            var obj = v as JObj;
            if (obj != null) return "{" + string.Join(",", obj.Select(kv => Str(kv.Key) + ":" + Write(kv.Value))) + "}";
            var dict = v as IDictionary<string, object>;
            if (dict != null) return "{" + string.Join(",", dict.Select(kv => Str(kv.Key) + ":" + Write(kv.Value))) + "}";
            var list = v as IEnumerable;
            if (list != null) return "[" + string.Join(",", list.Cast<object>().Select(Write)) + "]";
            return Str(v.ToString());
        }

        public static IDictionary<string, object> Parse(string text)
        {
            try { return new JavaScriptSerializer().DeserializeObject(text) as IDictionary<string, object>; }
            catch { return null; }
        }

        // Поле разобранного объекта: строка, целое, да/нет; нет поля или другой тип — null (false).
        public static string GetStr(IDictionary<string, object> m, string key)
        {
            object v;
            return m != null && m.TryGetValue(key, out v) ? v as string : null;
        }

        public static int? GetInt(IDictionary<string, object> m, string key)
        {
            object v;
            if (m == null || !m.TryGetValue(key, out v) || v == null || v is bool || v is string) return null;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { return null; }
        }

        public static bool GetBool(IDictionary<string, object> m, string key)
        {
            object v;
            return m != null && m.TryGetValue(key, out v) && v is bool && (bool)v;
        }
    }

    static class Files
    {
        public static void WriteAtomic(string path, string text)
        {
            var tmp = path + ".pimpwin.tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        public static string Read(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null; } catch { return null; }
        }
    }

    // Меню у значка в цветах панели: стандартное меню Windows светлое и рядом с тёмной панелью выглядит чужим.
    class DarkMenuColors : ProfessionalColorTable
    {
        static readonly Color Hover = Color.FromArgb(0x3A, 0x3A, 0x40);
        public override Color ToolStripDropDownBackground { get { return Palette.Tile; } }
        public override Color ImageMarginGradientBegin { get { return Palette.Tile; } }
        public override Color ImageMarginGradientMiddle { get { return Palette.Tile; } }
        public override Color ImageMarginGradientEnd { get { return Palette.Tile; } }
        public override Color MenuBorder { get { return Palette.TileLine; } }
        public override Color MenuItemBorder { get { return Hover; } }
        public override Color MenuItemSelected { get { return Hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
        public override Color SeparatorDark { get { return Palette.TileLine; } }
        public override Color SeparatorLight { get { return Palette.Tile; } }
        public override Color CheckBackground { get { return Palette.Tile; } }
        public override Color CheckSelectedBackground { get { return Hover; } }
        public override Color CheckPressedBackground { get { return Hover; } }
    }

    class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Palette.Text : Palette.Dim;
            base.OnRenderItemText(e);
        }

        // Галка выбранного пункта — значком в цвет текста, без светлой системной рамки.
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            using (var font = new Font("Segoe MDL2 Assets", Math.Max(8f, r.Height * 0.62f), FontStyle.Regular, GraphicsUnit.Pixel))
                TextRenderer.DrawText(e.Graphics, "", font, r, Palette.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    // ---------------------------------------------------------------- панель

    class Panel : Form
    {
        readonly TrayApp app;
        float k = 1f;
        public DateTime HiddenAt = DateTime.MinValue;
        // Листы панели: «Настройки» за шестерёнкой, «Токены» — из своей карточки.
        public enum Sheet { Panel, Settings, Tokens }
        Sheet sheet = Sheet.Panel;
        // Нажимаемые места нарисованной панели: прямоугольник в пикселях, действие, подсказка, наведение, перетаскивание.
        class Hit { public RectangleF R; public Action Act; public string Tip; public Action Hover; public Action<float> Drag; }
        readonly List<Hit> zones = new List<Hit>();
        // Где мышь держит примерку: ушла отсюда — примерка снимается (у полос цветов промежутки между образцами — внутри).
        readonly List<RectangleF> previewAreas = new List<RectangleF>();
        Point? mouse;
        int hot = -1;
        Hit drag;
        Screen screen;
        readonly ToolTip tip = new ToolTip();
        string tipText;
        // Карточка «Цвет PMP» под шапкой; открытая группа настроек (открыта всегда одна).
        bool accentOpen;
        string settingsGroup;
        // Период листа «Токены»: 0 — сегодня, 1 — вчера, 2 — 7 дней.
        int tokensPeriod;
        // Открыт выпадающий список: панель не прячется, пока он на экране.
        bool menuOpen;
        // Лист выше экрана (ноутбук, раскрытый «Шрифт»): панель — во всю высоту, содержимое крутится колесом.
        float scroll, contentH, viewH;
        const TextFormatFlags Keep = TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.NoPrefix;

        const int W = 340;
        const float Pad = 14, Inner = W - Pad * 2, CX = Pad + 14, CW = Inner - 28;

        public Panel(TrayApp app)
        {
            this.app = app;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Palette.Bg;
            DoubleBuffered = true;
            KeyPreview = true;
            Text = "PMP";
            using (var g = CreateGraphics()) k = g.DpiX / 96f;
            Width = S(W);
            Height = S(500);
            // Подсказки — в цветах панели (имена цветов и видов отсчёта живут только в них).
            anim.Tick += delegate { AnimTick(); };
            tip.OwnerDraw = true;
            tip.InitialDelay = 400;
            tip.ReshowDelay = 80;
            tip.AutoPopDelay = 8000;
            tip.Popup += (s, e) =>
            {
                using (var font = new System.Drawing.Font("Segoe UI", 9f * k, FontStyle.Regular, GraphicsUnit.Pixel))
                {
                    var sz = TextRenderer.MeasureText(tipText ?? "", font, Size.Empty, TextFormatFlags.NoPrefix);
                    e.ToolTipSize = new Size(sz.Width + S(14), sz.Height + S(8));
                }
            };
            tip.Draw += (s, e) =>
            {
                using (var br = new SolidBrush(Palette.Tile)) e.Graphics.FillRectangle(br, e.Bounds);
                using (var pen = new Pen(Palette.TileLine)) e.Graphics.DrawRectangle(pen, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
                using (var font = new System.Drawing.Font("Segoe UI", 9f * k, FontStyle.Regular, GraphicsUnit.Pixel))
                    TextRenderer.DrawText(e.Graphics, e.ToolTipText, font, e.Bounds, Palette.Text,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;   // CS_DROPSHADOW
                cp.ExStyle |= 0x00000080;      // WS_EX_TOOLWINDOW — без кнопки в панели задач и Alt+Tab
                return cp;
            }
        }

        int S(float v) { return (int)Math.Round(v * k); }
        float F(float v) { return v * k; }

        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); if (!menuOpen) HidePanel(); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Escape();
            // открытая карточка Workflow листается стрелками, как точками
            else if ((e.KeyCode == Keys.Left || e.KeyCode == Keys.Right) && sheet == Sheet.Panel && flowCard != null) Turn(e.KeyCode == Keys.Right ? 1 : -1);
            // фильтр Workflow с клавиатуры: ← → — соседний счёт (Tab, Enter и пробел — в ProcessCmdKey)
            else if ((e.KeyCode == Keys.Left || e.KeyCode == Keys.Right) && sheet == Sheet.Panel && flowFocus != null) MoveFlowFocus(e.KeyCode == Keys.Right ? 1 : -1);
            else if (e.Control && e.KeyCode == Keys.Oemcomma) ShowSheet(sheet == Sheet.Settings ? Sheet.Panel : Sheet.Settings);
            base.OnKeyDown(e);
        }

        // Tab и Shift+Tab — по счёту Workflow в шапке блока (фильтры), Enter и пробел — нажать выбранный. Tab у формы
        // без полей ввода съедает навигация WinForms, поэтому ловим здесь, до неё.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            if (sheet == Sheet.Panel && flowCard == null && flowFocusable.Count > 0)
            {
                if (key == Keys.Tab) { MoveFlowFocus((keyData & Keys.Shift) != 0 ? -1 : 1); return true; }
                if ((key == Keys.Enter || key == Keys.Space) && flowFocus != null && flowFocusable.Contains(flowFocus)) { PickFlowFilter(flowFocus); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // Esc по шагам: лист — примерка, потом назад; панель — примерка, карточка Workflow, карточка «Цвет PMP»,
        // и только потом панель закрывается.
        void Escape()
        {
            // Esc при поднятом блоке: всё как было, а отпускание кнопки после этого — не нажатие (без флага
            // отпускание раскрывало «Температуру», красило окна и т. д.).
            if (dragId != null) { swallowUp = (Control.MouseButtons & MouseButtons.Left) != 0; DragEnd(true); return; }
            if (Palette.PreviewAccent != null) { EndHover(); return; }
            if (sheet != Sheet.Panel) { ShowSheet(Sheet.Panel); return; }
            if (flowCard != null) { CloseFlowCard(); return; }
            if (flowFocus != null) { flowFocus = null; Invalidate(); return; }
            if (accentOpen) { accentOpen = false; Refit(); return; }
            HidePanel();
        }

        void EndHover()
        {
            if (Palette.PreviewAccent != null) { Palette.PreviewAccent = null; Invalidate(); }
        }

        // Подсказка у нарисованного места: одна подсказка на всю панель, поэтому показываем её сами — после паузы
        // под курсором, со сменой места прячем.
        readonly System.Windows.Forms.Timer tipTimer = new System.Windows.Forms.Timer { Interval = 450 };

        // Смена языка: открытая подсказка — на старом языке; прячем, при следующем наведении покажется новая.
        public void ResetTip()
        {
            tipText = null;
            tipTimer.Stop();
            tip.Hide(this);
        }

        void SetTip(string text)
        {
            if (text == tipText) return;
            tipText = text;
            tipTimer.Stop();
            tip.Hide(this);
            if (text == null) return;
            if (tipTimer.Tag == null)
            {
                tipTimer.Tag = true;
                tipTimer.Tick += delegate
                {
                    tipTimer.Stop();
                    if (tipText != null && mouse.HasValue && Visible) tip.Show(tipText, this, mouse.Value.X + S(12), mouse.Value.Y + S(20), 6000);
                };
            }
            tipTimer.Start();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            mouse = e.Location;
            if (drag != null) { drag.Drag(e.X / k); return; }
            if (dragId != null) { DragMove(e.Location); return; }
            if (pressId != null && (Math.Abs(e.X - pressAt.X) > S(4) || Math.Abs(e.Y - pressAt.Y) > S(4))) { press.Stop(); pressId = null; }
            int now = zones.FindIndex(z => z.R.Contains(e.Location));
            // Рука — только над нажимаемым; у места с одной подсказкой (кольцо проекта) — обычная стрелка.
            Cursor = now >= 0 && (zones[now].Act != null || zones[now].Drag != null) ? Cursors.Hand : Cursors.Default;
            if (now != hot)
            {
                hot = now;
                var h = now >= 0 ? zones[now] : null;
                SetTip(h == null ? null : h.Tip);
                if (h != null && h.Hover != null) h.Hover();
                Invalidate();
            }
            if (!previewAreas.Any(r => r.Contains(e.Location))) EndHover();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (drag != null || dragId != null) return;
            mouse = null;
            SetTip(null);
            EndHover();
            if (hot != -1) { hot = -1; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            swallowUp = false;
            // свайп по карточке Workflow: нажал, повёл вбок, отпустил — соседняя страница
            swipeFrom = sheet == Sheet.Panel && flowCard != null && flowCardRect.Contains(e.Location) ? e.Location : (Point?)null;
            var h = zones.FirstOrDefault(z => z.Drag != null && z.R.Contains(e.Location));
            if (h == null) { if (sheet == Sheet.Panel) PressStart(e.Location); return; }
            drag = h;
            Capture = true;
            h.Drag(e.X / k);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (drag != null) { drag = null; Capture = false; Invalidate(); return; }
            // поднятый блок отпустили — раскладка сохраняется, нажатия нет
            if (dragId != null) { DragEnd(false); return; }
            press.Stop();
            pressId = null;
            // перетаскивание отменили Esc, кнопка ещё была нажата — это её отпускание, не нажатие
            if (swallowUp && e.Button == MouseButtons.Left) { swallowUp = false; return; }
            if (e.Button == MouseButtons.Left && swipeFrom.HasValue)
            {
                int sx = e.X - swipeFrom.Value.X, sy = e.Y - swipeFrom.Value.Y;
                swipeFrom = null;
                if (Math.Abs(sx) > S(40) && Math.Abs(sx) > Math.Abs(sy) * 1.5) { Turn(sx < 0 ? 1 : -1); return; }
            }
            // Правая кнопка по блоку панели — его меню (ширина, «Скрыть блок», «Вернуть как было»).
            if (e.Button == MouseButtons.Right && sheet == Sheet.Panel)
            {
                var id = BlockAt(e.Location);
                if (id != null) BlockMenu(id, e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            // Нажали — подсказка уходит и до следующего места не возвращается.
            tipTimer.Stop();
            tip.Hide(this);
            int i = zones.FindIndex(z => z.R.Contains(e.Location));
            if (i >= 0 && zones[i].Act != null) zones[i].Act();
        }

        public void ShowSheet(Sheet next)
        {
            if (next == Sheet.Settings && sheet != Sheet.Settings) settingsGroup = null;
            if (next != Sheet.Panel) { accentOpen = false; Palette.PreviewAccent = null; }
            sheet = next;
            hot = -1;
            scroll = 0;
            SetTip(null);
            Refit();
        }

        // Жест тачпада вбок и наклон колеса (WM_MOUSEHWHEEL) — WinForms их не разбирает: над карточкой Workflow
        // листают её (вправо — следующий).
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x020E && sheet == Sheet.Panel && flowCard != null)
            {
                int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                long lp = m.LParam.ToInt64();
                var at = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                if (FlowWheel(at, delta)) { m.Result = IntPtr.Zero; return; }
            }
            base.WndProc(ref m);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            // колесо над карточкой Workflow листает её (вниз — следующий), а не прокручивает панель
            if (sheet == Sheet.Panel && FlowWheel(e.Location, -e.Delta)) return;
            float max = Math.Max(0, contentH - viewH);
            float next = Math.Min(Math.Max(scroll - e.Delta / 120f * 48, 0), max);
            if (next == scroll) return;
            scroll = next;
            EndHover();
            hot = -1;
            SetTip(null);
            Invalidate();
        }

        public void HidePanel()
        {
            if (!Visible) return;
            press.Stop();
            pressId = null;
            DragEnd(true);
            swallowUp = false;
            flowCard = null;
            flowSlidePrev = null;
            swipeFrom = null;
            flowFilter = "run";
            flowFocus = null;
            flowPage = aheadPage = 0;
            Palette.PreviewAccent = null;
            SetTip(null);
            Hide();
            HiddenAt = DateTime.Now;
            sheet = Sheet.Panel;
            accentOpen = false;
        }

        public void ShowNearTray()
        {
            screen = Screen.FromPoint(Cursor.Position);
            scroll = 0;
            // панель открылась — блоки сразу на местах, без проезда; вид — компактный
            shown.Clear();
            CompactView();
            Fit();
            Place();
            Show();
            Activate();
            Invalidate();
        }

        // Высота — по содержимому, но не выше рабочей области экрана; лишнее — прокруткой.
        void Fit()
        {
            contentH = Measure();
            var area = (screen ?? Screen.FromPoint(Cursor.Position)).WorkingArea;
            int h = Math.Min(S(contentH), area.Height - S(20));
            viewH = h / k;
            scroll = Math.Min(Math.Max(scroll, 0), Math.Max(0, contentH - viewH));
            if (h != Height) Height = h;
        }

        // Высота — по содержимому (открылись настройки); край у панели задач остаётся на месте.
        public void Refit()
        {
            if (!Visible) return;
            int old = Height;
            Fit();
            if (Height != old) Place();
            Invalidate();
        }

        void Place()
        {
            var sc = screen ?? Screen.FromPoint(Cursor.Position);
            var area = sc.WorkingArea;
            var bounds = sc.Bounds;
            int gap = S(10);
            int x = area.Right - Width - gap, y = area.Bottom - Height - gap;
            if (area.Top > bounds.Top) y = area.Top + gap;                       // панель задач сверху
            if (area.Left > bounds.Left) x = area.Left + gap;                   // слева
            Location = new Point(Math.Max(area.Left, x), Math.Max(area.Top, y));
        }

        float Measure()
        {
            switch (sheet)
            {
                case Sheet.Settings: return DrawSettings(null);
                case Sheet.Tokens: return DrawTokens(null);
                default: return Draw(null);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            zones.Clear();
            previewAreas.Clear();
            flowFocusable.Clear();
            motionNow = Motion;
            var state = g.Save();
            g.TranslateTransform(0, -F(scroll));
            switch (sheet)
            {
                case Sheet.Settings: DrawSettings(g); break;
                case Sheet.Tokens: DrawTokens(g); break;
                default: Draw(g); break;
            }
            g.Restore(state);
            // Прокрутка: тонкая полоса справа — сколько видно и где.
            if (contentH > viewH + 0.5f)
            {
                float track = viewH - 16, bar = Math.Max(24, track * viewH / contentH);
                float top = 8 + (track - bar) * scroll / (contentH - viewH);
                using (var path = Palette.Round(new RectangleF(F(W - 6), F(top), F(3), F(bar)), F(1.5f)))
                using (var br = new SolidBrush(Color.FromArgb(0x50, 255, 255, 255))) g.FillPath(br, path);
            }
            using (var pen = new Pen(Palette.TileLine)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        // Нажимаемое место; true — над ним сейчас мышь (подсветка). Координаты — в точках листа, с учётом прокрутки.
        bool Zone(Graphics g, float x, float y, float w, float h, Action act, string tipText = null, Action hover = null, Action<float> drag = null)
        {
            if (ghost) return false;
            var r = new RectangleF(F(x), F(y - scroll), F(w), F(h));
            if (g != null) zones.Add(new Hit { R = r, Act = act, Tip = tipText, Hover = hover, Drag = drag });
            return g != null && mouse.HasValue && r.Contains(mouse.Value);
        }

        void PreviewArea(Graphics g, float x, float y, float w, float h)
        {
            if (g != null) previewAreas.Add(new RectangleF(F(x), F(y - scroll), F(w), F(h)));
        }

        void Hover(Graphics g, float x, float y, float w, float h)
        {
            using (var path = Palette.Round(new RectangleF(F(x), F(y), F(w), F(h)), F(8)))
            using (var br = new SolidBrush(Color.FromArgb(0x12, 255, 255, 255))) g.FillPath(br, path);
        }

        // Шапка: плашка PMP (нажатие — «Цвет PMP»), справа шестерёнка настроек.
        void Header(Graphics g, float pad, float y, float inner)
        {
            bool badgeOver = Zone(g, pad - 4, y, 42, 24, delegate { ToggleAccent(); }, L.T("accent.tip"));
            if (g == null) return;
            if (badgeOver || accentOpen)
                using (var path = Palette.Round(new RectangleF(F(pad - 4), F(y), F(42), F(24)), F(6)))
                using (var br = new SolidBrush(Color.FromArgb(accentOpen ? 0x1F : 0x14, 255, 255, 255))) g.FillPath(br, path);
            var badge = new RectangleF(F(pad), F(y + 2), F(34), F(19));
            using (var path = Palette.Round(badge, F(5)))
            using (var br = new SolidBrush(Palette.Accent)) g.FillPath(br, path);
            // Буквы — ровно по центру плашки по их контуру: строка текста с запасом под хвосты букв садила «PMP» вверх.
            using (var family = new FontFamily("Segoe UI"))
            using (var text = new GraphicsPath())
            {
                text.AddString("PMP", family, (int)FontStyle.Bold, F(9.5f), PointF.Empty, StringFormat.GenericTypographic);
                var b = text.GetBounds();
                using (var m = new Matrix())
                {
                    m.Translate(badge.X + (badge.Width - b.Width) / 2 - b.X, badge.Y + (badge.Height - b.Height) / 2 - b.Y);
                    text.Transform(m);
                }
                using (var br = new SolidBrush(Palette.Bg)) g.FillPath(br, text);
            }
            // Точка «нажми» на правом верхнем углу плашки: не все догадываются, что на неё можно нажать.
            float dx = badge.Right - F(1), dy = badge.Top + F(1);
            using (var br = new SolidBrush(Palette.Bg)) g.FillEllipse(br, dx - F(5), dy - F(5), F(10), F(10));
            using (var br = new SolidBrush(badgeOver || accentOpen ? Palette.Text : Palette.Dim)) g.FillEllipse(br, dx - F(3.5f), dy - F(3.5f), F(7), F(7));
            // Имя программы в её же панели не пишем — место отдано кнопке «Свернуть всё».
            var parts = new List<string>();
            float gx = pad + inner - 22, gy = y + 1, textRight = gx - 6;
            if (sheet == Sheet.Panel) textRight = FoldAllButton(g, gx - 4, y + 1) - 8;
            CapsLabel(g, string.Join(" · ", parts), pad + 44, y + 6, textRight - pad - 44);
            bool settings = sheet == Sheet.Settings;
            bool over = Zone(g, gx, gy, 22, 22, delegate { ShowSheet(settings ? Sheet.Panel : Sheet.Settings); }, L.T("header.settingsTip"));
            if (over || settings)
                using (var br = new SolidBrush(Color.FromArgb(settings ? 0x1F : 0x14, 255, 255, 255)))
                    g.FillEllipse(br, F(gx), F(gy), F(22), F(22));
            using (var font = new System.Drawing.Font("Segoe MDL2 Assets", F(12), FontStyle.Regular, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, "", font, new Rectangle(S(gx), S(gy), S(22), S(22)),
                    over || settings ? Palette.Text : Palette.Dim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | Keep);
        }

        // Плашка PMP: карточка «Цвет PMP» под шапкой; с листа — сперва назад на панель.
        void ToggleAccent()
        {
            Palette.PreviewAccent = null;
            if (sheet != Sheet.Panel) { ShowSheet(Sheet.Panel); accentOpen = true; }
            else accentOpen = !accentOpen;
            Refit();
        }

        // ---------------------------------------------------------------- сворачивание блоков (макет 17.10.2026, ★1.1)

        // Блоки, которые сейчас есть на панели и сворачиваются; скрытые правой
        // кнопкой — мимо. «Токены» — уже строка, ведёт на свой лист.
        List<string> FoldKeys()
        {
            var keys = new List<string>();
            foreach (var w in Widgets())
            {
                if (w.Hidden) continue;
                var key = FoldKeyOf(w.Id);
                if (key == null || keys.Contains(key)) continue;
                keys.Add(key);
            }
            return keys;
        }

        static bool IsOpen(string key) { return Settings.Get(key, true); }

        // Компактный вид при каждом открытии панели (пожелание 17.10.2026): раскрыты блоки с «Оставлять раскрытым»
        // (правая кнопка), остальное свёрнуто строками, даже если в прошлый раз раскрывала. Сосед по ряду — вместе (SyncRows).
        static string KeepKey(string key) { return key + "Keep"; }

        static bool KeepOpen(string key) { return Settings.Get(KeepKey(key), KeepByDefault(key)); }

        // Раскрыт при открытии панели, пока не сняли «Оставлять раскрытым»: железо (температура с памятью, процессор с видеокартой).
        static bool KeepByDefault(string key) { return key == Settings.TempOpen || key == Settings.MemoryOpen || key == Settings.LoadOpen; }

        void CompactView()
        {
            foreach (var key in WidgetNames.Select(n => FoldKeyOf(n[0])).Where(x => x != null).Distinct())
                if (IsOpen(key) != KeepOpen(key)) Settings.Set(key, KeepOpen(key));
            SyncRows();
        }

        // «Оставлять раскрытым» — блоку и его соседу по ряду (ряд сворачивается вместе); включили — раскрыть сразу.
        void SetKeepOpen(string id, bool keep)
        {
            var keys = new List<string> { FoldKeyOf(id) };
            var row = Rows(VisibleWidgets()).FirstOrDefault(r => r.Any(w => w.Id == id));
            if (row != null) keys.AddRange(row.Select(w => FoldKeyOf(w.Id)).Where(x => x != null));
            foreach (var key in keys.Distinct())
            {
                // как по умолчанию — ключа нет
                Settings.SetValue(KeepKey(key), keep == KeepByDefault(key) ? null : (object)keep);
                if (keep) Settings.Set(key, true);
            }
            Refit();
        }

        // Сосед по ряду сворачивается вместе (ряд остаётся ровным, свёрнутый — строкой 36): ключ соседа ставит
        // рисование ряда перед каждым блоком, FoldHead запоминает его в своём нажатии.
        string foldAlso;

        void Fold(string key, bool open, string also = null)
        {
            Settings.Set(key, open);
            if (also != null && also != key) Settings.Set(also, open);
            Refit();
        }

        // Кнопка «Свернуть всё / Развернуть всё» в шапке у шестерёнки, контуром; значок — две стрелки к середине
        // (свернуть) или врозь (развернуть). Хоть один блок развёрнут — «Свернуть всё». Возвращает левый край.
        float FoldAllButton(Graphics g, float right, float y)
        {
            var keys = FoldKeys();
            bool any = keys.Any(IsOpen);
            string label = any ? L.T("fold.collapseAll") : L.T("fold.expandAll");
            var lsz = Txt(null, label, 10f, FontStyle.Regular, Palette.Dim, 0, 0, 200, false);
            float bw = 5 + 12 + 4 + lsz.Width / k + 8, bx = right - bw;
            bool over = Zone(g, bx, y, bw, 22, delegate
            {
                foreach (var key in keys) Settings.Set(key, !any);
                Refit();
            }, any ? L.T("fold.collapseAllTip") : L.T("fold.expandAllTip"));
            if (g == null) return bx;
            using (var path = Palette.Round(new RectangleF(F(bx), F(y), F(bw), F(22)), F(6)))
            {
                using (var br = new SolidBrush(Color.FromArgb(over ? 0x17 : 0x09, 255, 255, 255))) g.FillPath(br, path);
                using (var pen = new Pen(over ? Color.FromArgb(0x33, 255, 255, 255) : Palette.TileLine, 1)) g.DrawPath(pen, path);
            }
            var color = over ? Palette.Text : Palette.Dim;
            float ix = bx + 5, iy = y + 4;
            Func<float, float, PointF> p = (px, py) => new PointF(F(ix + px), F(iy + py));
            using (var pen = new Pen(color, F(1.1f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                if (any)
                {
                    g.DrawLines(pen, new[] { p(3, 2.5f), p(6, 5.5f), p(9, 2.5f) });
                    g.DrawLines(pen, new[] { p(3, 11.5f), p(6, 8.5f), p(9, 11.5f) });
                }
                else
                {
                    g.DrawLines(pen, new[] { p(3, 5.5f), p(6, 2.5f), p(9, 5.5f) });
                    g.DrawLines(pen, new[] { p(3, 8.5f), p(6, 11.5f), p(9, 8.5f) });
                }
            }
            Txt(g, label, 10f, FontStyle.Regular, color, bx + 5 + 12 + 4, y + 3.5f, lsz.Width / k + 4, false);
            return bx;
        }

        // Ярлык мелкими прописными:
        // один шрифт и цвет у заголовков блоков, групп настроек и строки версии Claude в шапке.
        SizeF CapsLabel(Graphics g, string text, float x, float y, float w)
        {
            return Txt(g, text.ToUpper(L.Culture), 9f, FontStyle.Bold, Palette.Dim, x, y, w, false);
        }

        // Заголовок сворачиваемого блока — одним шрифтом у всех: ярлык мелкими прописными; справа —
        // главное число (always — и развёрнутым, иначе только свёрнутым), перед ним — пояснение свёрнутого (note) и
        // стрелка, как в Windows: ⌃ свернуть, ⌄ развернуть. Развёрнут — строка 30 над содержимым, свёрнут — весь блок 36.
        bool FoldHead(Graphics g, float x, float y, float w, string key, string cap, string value, Color? valueColor, bool always, string note = null, string fallback = null, string tipText = null, bool quiet = false)
        {
            bool open = IsOpen(key);
            float hh = open ? 30 : 36;
            var also = foldAlso;
            bool over = Zone(g, x, y, w, hh, delegate { Fold(key, !open, also); }, tipText != null ? tipText + " · " + (open ? L.T("fold.collapseLc") : L.T("fold.expandLc")) : open ? L.T("fold.collapse") : L.T("fold.expand"));
            if (g == null) return open;
            if (over && !quiet) Hover(g, x + 3, y + 3, w - 6, hh - 6);
            float chevX = x + w - 14 - 12, capY = open ? y + 10 : y + 12.5f, valY = open ? y + 7.5f : y + 10;
            var csz = CapsLabel(g, cap, x + 14, capY, w - 28 - 18);
            float left = x + 14 + csz.Width / k + 8, right = chevX - 6;
            if (value != null && (always || !open))
            {
                // не влезает — короче (fallback), а не обрезанное «23 часа 5…»
                if (fallback != null && Txt(null, value, 11.5f, FontStyle.Bold, Palette.Text, 0, 0, 400, false).Width / k > right - left)
                    value = fallback;
                var vsz = Txt(g, value, 11.5f, FontStyle.Bold, valueColor ?? Palette.Text, left, valY, right - left, false, true);
                right -= vsz.Width / k + 6;
            }
            // пояснение — только целиком: обрезанное «14,8 из 3…» хуже, чем никакого
            if (!open && !string.IsNullOrEmpty(note) && Txt(null, note, 9.5f, FontStyle.Regular, Palette.Dim, 0, 0, 400, false).Width / k <= right - left)
                Txt(g, note, 9.5f, FontStyle.Regular, Palette.Dim, left, y + 12, right - left, false, true);
            Glyph(g, open ? "" : "", 9f, over && !quiet ? Palette.Text : Palette.Dim, chevX, open ? y + 6 : y + 9, 12, 18);
            return open;
        }

        // Шапка панели стоит на месте при прокрутке: рисуется поверх содержимого, её места нажатия — первыми,
        // под ней содержимое не нажимается.
        void StickyHeader(Graphics g, float pad, float inner)
        {
            int first = zones.Count;
            float top = scroll, band = pad + 24 + 9;
            using (var br = new SolidBrush(Palette.Bg)) g.FillRectangle(br, 0, F(top), Width, F(band));
            if (scroll > 0)
                using (var pen = new Pen(Palette.TileLine, 1)) g.DrawLine(pen, 0, F(top + band), Width, F(top + band));
            Header(g, pad, top + pad, inner);
            Zone(g, 0, top, W, band, null);
            var mine = zones.GetRange(first, zones.Count - first);
            zones.RemoveRange(first, mine.Count);
            zones.InsertRange(0, mine);
        }

        // ---------------------------------------------------------------- раскладка блоков

        // Блок панели: порядок — местом в списке, «широкий» — во всю ширину, «скрыт» — в строке «Скрытые» внизу.
        class Widget { public string Id; public bool Wide, Hidden; }

        // Блоки и их имена в меню и в строке «Скрытые». Порядок — умолчание («Вернуть как было»: парами слева-справа,
        // Workflow — длинной полоской внизу): Температура + Память · Процессор + Видеокарта · Токены · Workflow.
        static readonly string[][] WidgetNames =
        {
            new[] { "temperature", "widget.temperature" }, new[] { "memory", "widget.memory" },
            new[] { "cpu", "widget.cpu" }, new[] { "gpu", "widget.gpu" }, new[] { "tokens", "widget.tokens" },
            new[] { "workflow", "widget.workflow" },
        };
        static readonly string[] WideByDefault = { "workflow" };

        // Раскладка из settings.json старше этого порядка — один раз переставляется в новый порядок (скрытые остаются
        // скрытыми), дальше перестановки пользователя уважаются.
        const int LayoutOrder = 6;
        const string LayoutOrderKey = "panelLayoutOrder";

        // Workflow сузить нельзя.
        static bool CanNarrow(string id) { return id != "workflow"; }

        static string WidgetName(string id)
        {
            var n = WidgetNames.FirstOrDefault(x => x[0] == id);
            return n == null ? id : L.T(n[1]);
        }

        // Ключ «развёрнут» блока; у пары нагрузки — общий; у «Токенов» — нет (строка-ссылка).
        static string FoldKeyOf(string id)
        {
            switch (id)
            {
                case "workflow": return Settings.WorkflowOpen;
                case "temperature": return Settings.TempOpen;
                case "memory": return Settings.MemoryOpen;
                case "cpu": case "gpu": return Settings.LoadOpen;
                default: return null;
            }
        }

        static List<Widget> StandardLayout()
        {
            return WidgetNames.Select(n => new Widget { Id = n[0], Wide = WideByDefault.Contains(n[0]) }).ToList();
        }

        static bool IsStandard(List<Widget> list)
        {
            var std = StandardLayout();
            return list.Count == std.Count && list.Select((w, i) => w.Id == std[i].Id && w.Wide == std[i].Wide && !w.Hidden).All(b => b);
        }

        List<Widget> layout;

        // Раскладка из settings.json: чужие и повторные id — прочь, недостающий — за своим соседом
        // по умолчанию. Ключа нет — умолчание; прежние выключатели «… в панели» переезжают в «скрыт» один раз.
        List<Widget> Widgets()
        {
            if (layout != null) return layout;
            var list = new List<Widget>();
            var raw = Settings.GetRaw(Settings.PanelLayout) as object[];
            if (raw != null)
            {
                foreach (var o in raw)
                {
                    var d = o as IDictionary<string, object>;
                    var id = d == null ? null : Json.GetStr(d, "id");
                    if (id == null || !WidgetNames.Any(n => n[0] == id) || list.Any(w => w.Id == id)) continue;
                    object wide;
                    list.Add(new Widget { Id = id, Wide = !d.TryGetValue("wide", out wide) || !(wide is bool) || (bool)wide, Hidden = Json.GetBool(d, "hidden") });
                }
            }
            var std = StandardLayout();
            for (int i = 0; i < std.Count; i++)
            {
                if (list.Any(w => w.Id == std[i].Id)) continue;
                int at = 0;
                for (int j = i - 1; j >= 0; j--)
                {
                    int found = list.FindIndex(w => w.Id == std[j].Id);
                    if (found >= 0) { at = found + 1; break; }
                }
                list.Insert(at, std[i]);
            }
            foreach (var w in list) if (!CanNarrow(w.Id)) w.Wide = true;
            // своя раскладка прежнего порядка — один раз в новый порядок и ширины умолчания, скрытые — скрытыми
            if (raw != null && Settings.GetInt(LayoutOrderKey, 0) < LayoutOrder)
            {
                var hidden = list.Where(w => w.Hidden).Select(w => w.Id).ToList();
                list = StandardLayout();
                foreach (var w in list) w.Hidden = hidden.Contains(w.Id);
                layout = list;
                SaveLayout();
            }
            if (Settings.GetInt(LayoutOrderKey, 0) < LayoutOrder) Settings.SetValue(LayoutOrderKey, LayoutOrder);
            layout = list;
            if (raw == null)
            {
                var moved = new[]
                {
                    new[] { Settings.ShowWorkflow, "workflow" }, new[] { Settings.ShowTokens, "tokens" }, new[] { Settings.ShowTemp, "temperature" },
                    new[] { Settings.ShowMemory, "memory" }, new[] { Settings.ShowLoad, "cpu" }, new[] { Settings.ShowLoad, "gpu" },
                };
                bool any = false;
                foreach (var m in moved)
                    if (Settings.Has(m[0]))
                    {
                        any = true;
                        if (!Settings.Get(m[0], true)) list.First(w => w.Id == m[1]).Hidden = true;
                    }
                if (any)
                {
                    SaveLayout();
                    foreach (var key in moved.Select(m => m[0]).Distinct()) Settings.SetValue(key, null);
                }
            }
            return list;
        }

        // Соседи по ряду — в одном виде: после перестановки свёрнутый рядом с раскрытым тянулся бы пустой плиткой до его
        // высоты. Разные — раскрываются оба (содержимое не прячем без спроса).
        void SyncRows()
        {
            for (int pass = 0; pass < 4; pass++)
            {
                bool changed = false;
                foreach (var row in Rows(VisibleWidgets()).Where(r => r.Count == 2))
                {
                    string a = FoldKeyOf(row[0].Id), b = FoldKeyOf(row[1].Id);
                    if (a == null || b == null || a == b || IsOpen(a) == IsOpen(b)) continue;
                    Settings.Set(a, true);
                    Settings.Set(b, true);
                    changed = true;
                }
                if (!changed) return;
            }
        }

        void SaveLayout()
        {
            SyncRows();
            var list = Widgets();
            if (IsStandard(list)) { Settings.SetValue(Settings.PanelLayout, null); return; }
            Settings.SetValue(Settings.PanelLayout, list.Select(w =>
            {
                var d = new Dictionary<string, object> { { "id", w.Id }, { "wide", w.Wide } };
                if (w.Hidden) d["hidden"] = true;
                return d;
            }).ToList());
        }

        // Ряды: широкий — свой ряд; узкие — по двое подряд; одинокая половинка — во всю ширину (пустая
        // половина «выглядела дырой»). Блоки на панели сейчас: не скрытые; Workflow — только когда выбрана папка проектов.
        List<Widget> VisibleWidgets()
        {
            bool flows = StatusScan.Root() != null;
            return Widgets().Where(w => !w.Hidden && (flows || w.Id != "workflow")).ToList();
        }

        List<List<Widget>> Rows(List<Widget> visible)
        {
            Func<Widget, bool> wide = w => w.Wide || !CanNarrow(w.Id);
            var rows = new List<List<Widget>>();
            for (int i = 0; i < visible.Count; i++)
            {
                var w = visible[i];
                if (!wide(w) && i + 1 < visible.Count && !wide(visible[i + 1])) { rows.Add(new List<Widget> { w, visible[i + 1] }); i++; }
                else rows.Add(new List<Widget> { w });
            }
            return rows;
        }

        // Где блоки стоят сейчас (логические точки листа) и куда едут: смена раскладки, ширины, скрытие и возврат —
        // «всё остальное плавно переставляется» пружиной ≈0,3 с без отскока.
        readonly Dictionary<string, PointF> shown = new Dictionary<string, PointF>();
        readonly Dictionary<string, RectangleF> targets = new Dictionary<string, RectangleF>();
        readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer { Interval = 15 };
        // Рамки блоков на экране (пиксели, с прокруткой) — для правой кнопки и долгого нажатия.
        readonly List<KeyValuePair<string, RectangleF>> blockHits = new List<KeyValuePair<string, RectangleF>>();
        // Сколько блоков в ряду у каждого (одинокой половинке «Во всю ширину» — серым).
        readonly Dictionary<string, int> rowSize = new Dictionary<string, int>();

        int animSkip;

        void AnimTick()
        {
            bool moving = false;
            foreach (var kv in targets)
            {
                PointF at;
                if (!shown.TryGetValue(kv.Key, out at)) continue;
                float dx = kv.Value.X - at.X, dy = kv.Value.Y - at.Y;
                if (Math.Abs(dx) < 0.4f && Math.Abs(dy) < 0.4f) { shown[kv.Key] = kv.Value.Location; continue; }
                shown[kv.Key] = new PointF(at.X + dx * 0.24f, at.Y + dy * 0.24f);
                moving = true;
            }
            // листание карточки Workflow — последний кадр дорисовывает её на месте
            bool sliding = flowSlidePrev != null;
            // легенда счёта ждёт своей паузы; идущий Workflow на экране мерцает (Pulse)
            bool legendWait = legendAt != null && !LegendShown;
            bool pulsing = pulseSeen && Visible && sheet == Sheet.Panel;
            bool counting = false;
            if (!moving && !sliding && !counting && !legendWait && !pulsing) { anim.Stop(); Invalidate(); return; }
            // играет только плитка отсчёта или пульс — кадр раз в ~45 мс, а не на каждом тике
            if (moving || sliding) { Invalidate(); return; }
            if (++animSkip % 3 != 0) return;
            // только пульс — перерисовать один блок Workflow (с легендой поверх, если она видна), не всю панель
            var flow = blockHits.FirstOrDefault(b => b.Key == "workflow");
            if (pulsing && !counting && !legendWait && !LegendShown && flow.Key != null)
                Invalidate(Rectangle.Inflate(Rectangle.Round(flow.Value), 2, 2));
            else Invalidate();
        }

        // Рисует (g != null) или только меряет высоту, логические пиксели.
        float Draw(Graphics g)
        {
            float pad = 14, gap = 10, y = pad;
            float inner = W - pad * 2, half = (inner - gap) / 2;

            // Шапка — в конце, поверх содержимого (StickyHeader): при прокрутке стоит на месте.
            y += 24 + gap;
            if (accentOpen) y += AccentCard(g, y) + gap;

            var visible = VisibleWidgets();
            var placed = new List<KeyValuePair<Widget, RectangleF>>();
            if (g != null) { targets.Clear(); blockHits.Clear(); rowSize.Clear(); pulseSeen = false; legendAt = null; }
            foreach (var row in Rows(visible))
            {
                float bw = row.Count == 2 ? half : inner, rh = 0;
                foreach (var w in row) rh = Math.Max(rh, BlockHeight(w.Id, bw));
                for (int i = 0; i < row.Count; i++)
                {
                    placed.Add(new KeyValuePair<Widget, RectangleF>(row[i], new RectangleF(pad + i * (half + gap), y, bw, rh)));
                    if (g != null) rowSize[row[i].Id] = row.Count;
                }
                y += rh + gap;
            }
            if (g != null)
            {
                bool moving = false;
                foreach (var p in placed)
                {
                    targets[p.Key.Id] = p.Value;
                    PointF at;
                    if (!shown.TryGetValue(p.Key.Id, out at)) shown[p.Key.Id] = at = p.Value.Location;
                    else if (Math.Abs(at.X - p.Value.X) > 0.4f || Math.Abs(at.Y - p.Value.Y) > 0.4f) moving = true;
                }
                if (moving && !anim.Enabled) anim.Start();
                // Сперва все на своих местах, поднятый (долгое нажатие) — последним, поверх остальных, у указателя.
                foreach (var p in placed)
                {
                    if (p.Key.Id == dragId) continue;
                    var at = shown[p.Key.Id];
                    var partner = placed.Where(o => o.Value.Y == p.Value.Y && o.Key != p.Key).Select(o => FoldKeyOf(o.Key.Id)).FirstOrDefault();
                    foldAlso = partner;
                    DrawBlock(g, p.Key.Id, at.X, at.Y, p.Value.Width, p.Value.Height);
                    foldAlso = null;
                    blockHits.Add(new KeyValuePair<string, RectangleF>(p.Key.Id, new RectangleF(F(at.X), F(at.Y - scroll), F(p.Value.Width), F(p.Value.Height))));
                }
                var lifted = placed.FirstOrDefault(p => p.Key.Id == dragId);
                if (lifted.Key != null && mouse.HasValue)
                {
                    // Workflow всегда во всю ширину — едут только по вертикали
                    float lx = CanNarrow(dragId) ? mouse.Value.X / k - grab.X : pad, ly = mouse.Value.Y / k + scroll - grab.Y;
                    var r = lifted.Value;
                    for (int i = 3; i >= 1; i--)
                        using (var path = Palette.Round(new RectangleF(F(lx - i), F(ly + 6 - i + 2), F(r.Width + 2 * i), F(r.Height + 2 * i)), F(12 + i)))
                        using (var br = new SolidBrush(Color.FromArgb(0x26, 0, 0, 0))) g.FillPath(br, path);
                    DrawBlock(g, lifted.Key.Id, lx, ly, r.Width, r.Height);
                    using (var path = Palette.Round(new RectangleF(F(lx), F(ly), F(r.Width), F(r.Height)), F(12)))
                    using (var pen = new Pen(Color.FromArgb(0xB3, Palette.Accent), F(1.5f))) g.DrawPath(pen, path);
                }
            }
            y += HiddenRow(g, y);
            if (g != null)
            {
                if (legendAt == null) legendSince = null; else DrawLegend(g);
            }
            if (g != null) StickyHeader(g, pad, inner);
            return y - gap + pad;
        }

        // Высота блока по ширине и раскрытости (ряд — по самому высокому, сосед тянется до него).
        float BlockHeight(string id, float w)
        {
            switch (id)
            {
                case "workflow": return WorkflowBody(null, 0);
                case "temperature": return !IsOpen(Settings.TempOpen) ? 36 : app.Sensors.IntegratedGpu ? 150 : 132;
                case "memory": return IsOpen(Settings.MemoryOpen) ? 132 : 36;
                case "cpu": case "gpu": return IsOpen(Settings.LoadOpen) ? 92 : 36;
                case "tokens": return 36;
                default: return 36;
            }
        }

        void DrawBlock(Graphics g, string id, float x, float y, float w, float h)
        {
            var now = app.Sensors.Now;
            var hist = app.Sensors.History;
            switch (id)
            {
                case "workflow":
                    // широкий всегда: рисуется от края панели (поднятый едет только по вертикали)
                    TileBg(g, Pad, y, Inner, h);
                    WorkflowBody(g, y);
                    break;
                case "temperature": TempTile(g, x, y, w, h); break;
                case "memory":
                {
                    // Память — процентом занятости (пожелание 17.10.2026), гигабайты — мельче: свёрнутым — слева от
                    // процента, если влезают; развёрнутым — подписью под графиком, как у температуры.
                    TileBg(g, x, y, w, h);
                    double total = Math.Max(1, app.Sensors.MemTotalGb);
                    double? used = now.MemUsedGb.HasValue ? now.MemUsedGb.Value / total * 100 : (double?)null;
                    string gb = now.MemUsedGb.HasValue ? L.F("mem.gb", Words.Gb(now.MemUsedGb.Value), Words.Gb(total)) : null;
                    if (FoldHead(g, x, y, w, Settings.MemoryOpen, L.Caps("widget.memory"), used.HasValue ? L.F("fmt.pct", Math.Round(used.Value)) : "—",
                        used.HasValue ? Palette.ForMemory(used.Value) : (Color?)null, true, gb, null, L.T("mem.tip")))
                    {
                        var pts = Series(hist, now, s => s.MemUsedGb.HasValue ? s.MemUsedGb.Value / total * 100 : (double?)null);
                        Chart(g, x + 14, y + 38, w - 28, 66, pts, 0, 100, v => Palette.ForMemory(v), null, null);
                        Txt(g, gb ?? L.T("mem.none"), 9.5f, FontStyle.Regular, Palette.Dim, x + 14, y + 110, w - 28, false);
                    }
                    break;
                }
                // нагрузка — процессор и видеокарта сворачиваются вместе
                case "cpu": LoadTile(g, x, y, w, h, L.Caps("widget.cpu"), now.Cpu, Series(hist, now, s => s.Cpu)); break;
                case "gpu": LoadTile(g, x, y, w, h, L.Caps("widget.gpu"), now.Gpu, Series(hist, now, s => s.Gpu)); break;
                case "tokens": TokensCard(g, x, y, w); break;
            }
        }

        // Температура (график ступенями по 20°) — во всю ширину или половинкой.
        void TempTile(Graphics g, float x, float y, float w, float h)
        {
            var now = app.Sensors.Now;
            TileBg(g, x, y, w, h);
            if (!FoldHead(g, x, y, w, Settings.TempOpen, L.Caps("widget.temperature"), now.Temp.HasValue ? Math.Round(now.Temp.Value) + "°" : L.T("temp.none"),
                now.Temp.HasValue ? Palette.ForTemp(now.Temp.Value) : Palette.Dim, true, null, now.Temp.HasValue ? null : "—",
                L.T("temp.tip")))
                return;
            var pts = Series(app.Sensors.History, now, s => s.Temp);
            double lo = 40, hi = 100;
            if (pts.Count > 0)
            {
                double mn = pts.Min(p => p.Value), mx = pts.Max(p => p.Value);
                lo = Math.Max(0, Math.Floor(mn / 20) * 20);
                hi = Math.Max(Math.Ceiling(mx / 20) * 20, lo + 20);
            }
            Chart(g, x + 14, y + 38, w - 28, 66, pts, lo, hi, v => Palette.ForTemp(v), Ticks(lo, hi, 20), v => ((int)v).ToString() + "°");
            string cap;
            if (pts.Count > 1)
            {
                long low = (long)Math.Round(pts.Min(p => p.Value)), high = (long)Math.Round(pts.Max(p => p.Value));
                string when = Words.Span(pts[pts.Count - 1].Key - pts[0].Key);
                cap = low == high ? when + ": " + low + "°" : when + ": " + low + "–" + high + "°";
            }
            else if (pts.Count == 1) cap = L.F("temp.now", Math.Round(pts[0].Value));
            else cap = L.T("temp.silent");
            Txt(g, cap, 9.5f, FontStyle.Regular, Palette.Dim, x + 14, y + 110, w - 28, false);
            if (app.Sensors.IntegratedGpu)
                Txt(g, w < Inner - 1 ? L.T("temp.igpuShort") : L.T("temp.igpu"), 8.5f, FontStyle.Regular,
                    Palette.Dim, x + 14, y + 128, w - 28, false);
        }

        // «Скрытые»: под всеми блоками подпись и «+ Имя»; нажатие — блок на прежнее место.
        float HiddenRow(Graphics g, float y)
        {
            var hidden = Widgets().Where(w => w.Hidden).ToList();
            if (hidden.Count == 0) return 0;
            float top = y, left = Pad + 4;
            var lsz = Txt(g, L.T("hidden.title"), 10f, FontStyle.Regular, Palette.Dim, left, y + 4, 80, false);
            float start = left + lsz.Width / k + 10, x = start;
            foreach (var w in hidden)
            {
                var item = w;
                string label = "+ " + WidgetName(w.Id);
                float bw = Txt(null, label, 10f, FontStyle.Regular, Palette.Text, 0, 0, 200, false).Width / k + 16;
                if (x + bw > Pad + Inner && x > start) { x = start; y += 28; }
                bool over = Zone(g, x, y, bw, 24, delegate
                {
                    item.Hidden = false;
                    SaveLayout();
                    Refit();
                }, L.F("hidden.restoreTip", WidgetName(w.Id)));
                if (g != null)
                {
                    using (var path = Palette.Round(new RectangleF(F(x), F(y), F(bw), F(24)), F(7)))
                    {
                        using (var br = new SolidBrush(Color.FromArgb(over ? 0x22 : 0x0F, 255, 255, 255))) g.FillPath(br, path);
                        using (var pen = new Pen(over ? Color.FromArgb(0x33, 255, 255, 255) : Palette.TileLine, 1)) g.DrawPath(pen, path);
                    }
                    Txt(g, label, 10f, FontStyle.Regular, over ? Palette.Text : Palette.Dim, x + 8, y + 4, bw - 10, false);
                }
                x += bw + 6;
            }
            return y + 24 + 10 - top;
        }

        // ---------------------------------------------------------------- правая кнопка и долгое нажатие по блоку

        string BlockAt(Point p)
        {
            var hit = blockHits.LastOrDefault(b => b.Value.Contains(p));
            return hit.Key;
        }

        // Меню блока: ширина, «Скрыть блок», «Вернуть как было».
        void BlockMenu(string id, Point at)
        {
            var w = Widgets().FirstOrDefault(x => x.Id == id);
            if (w == null) return;
            int inRow;
            bool alone = !rowSize.TryGetValue(id, out inRow) || inRow < 2;
            var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = true };
            var size = new ToolStripMenuItem(w.Wide ? L.T("menu.half") : L.T("menu.full"))
            {
                Enabled = w.Wide ? CanNarrow(id) : !alone,
            };
            size.Click += delegate { w.Wide = !w.Wide; SaveLayout(); Refit(); };
            menu.Items.Add(size);
            menu.Items.Add(L.T("menu.hide"), null, delegate { w.Hidden = true; SaveLayout(); Refit(); });
            // открытие панели сворачивает всё, кроме блоков с этой галочкой (пожелание 17.10.2026)
            var fold = FoldKeyOf(id);
            if (fold != null)
            {
                bool keep = KeepOpen(fold);
                menu.Items.Add(new ToolStripMenuItem(L.T("menu.keepOpen"), null, delegate { SetKeepOpen(id, !keep); }) { Checked = keep });
            }
            menu.Items.Add(new ToolStripSeparator());
            var standard = new ToolStripMenuItem(L.T("menu.restore")) { Enabled = !IsStandard(Widgets()) };
            standard.Click += delegate { layout = StandardLayout(); SaveLayout(); Refit(); };
            menu.Items.Add(standard);
            // язык интерфейса — здесь же, под рукой (пожелание 17.10.2026); смена — сразу
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(TrayApp.LanguageMenu(app.SetLanguage));
            menuOpen = true;
            tipTimer.Stop();
            tip.Hide(this);
            menu.Closed += delegate
            {
                menuOpen = false;
                BeginInvoke(new Action(delegate
                {
                    menu.Dispose();
                    if (Visible && Form.ActiveForm != this) Activate();
                }));
            };
            menu.Show(this, at);
        }

        // Долгое нажатие (пожелание 17.10.2026): левая кнопка на блоке и 0,4 с без сдвига больше 4 px —
        // блок поднимается и едет за указателем; над чужим блоком встаёт на его место, отпустил —
        // раскладка в settings.json; Esc — как было. Сдвинулся раньше или отпустил — обычное нажатие.
        readonly System.Windows.Forms.Timer press = new System.Windows.Forms.Timer { Interval = 400 };
        string pressId, dragId, lastSwap;
        bool swallowUp;
        Point pressAt;
        PointF grab;
        List<Widget> dragBefore;

        void PressStart(Point at)
        {
            pressId = BlockAt(at);
            if (pressId == null) return;
            pressAt = at;
            if (press.Tag == null)
            {
                press.Tag = true;
                press.Tick += delegate
                {
                    press.Stop();
                    // кнопку отпустили — OnMouseUp уже снял pressId (WinForms держит захват мыши от нажатия до отпускания)
                    if (pressId == null) return;
                    PointF from;
                    if (!shown.TryGetValue(pressId, out from)) { pressId = null; return; }
                    dragId = pressId;
                    pressId = null;
                    dragBefore = Widgets().Select(w => new Widget { Id = w.Id, Wide = w.Wide, Hidden = w.Hidden }).ToList();
                    grab = new PointF(pressAt.X / k - from.X, pressAt.Y / k + scroll - from.Y);
                    lastSwap = null;
                    Capture = true;
                    Cursor = Cursors.SizeAll;
                    SetTip(null);
                    Invalidate();
                };
            }
            press.Stop();
            press.Start();
        }

        void DragMove(Point at)
        {
            var target = blockHits.Where(b => b.Key != dragId && b.Value.Contains(at)).Select(b => b.Key).FirstOrDefault();
            if (target == null) { lastSwap = null; Invalidate(); return; }
            if (target == lastSwap) { Invalidate(); return; }
            lastSwap = target;
            var list = Widgets();
            int from = list.FindIndex(w => w.Id == dragId), to = list.FindIndex(w => w.Id == target);
            if (from < 0 || to < 0) return;
            var item = list[from];
            list.RemoveAt(from);
            list.Insert(to, item);
            SyncRows();
            Refit();
        }

        void DragEnd(bool cancel)
        {
            if (dragId == null) return;
            if (cancel && dragBefore != null) layout = dragBefore;
            // отпущенный блок доезжает на место от указателя, а не от прежнего места
            if (mouse.HasValue) shown[dragId] = new PointF(CanNarrow(dragId) ? mouse.Value.X / k - grab.X : Pad, mouse.Value.Y / k + scroll - grab.Y);
            dragId = null;
            dragBefore = null;
            Capture = false;
            Cursor = Cursors.Default;
            SaveLayout();
            Refit();
        }

        // ---------------------------------------------------------------- «Цвет PMP» на панели

        // «Цвет PMP»: девять цветов одним рядом по ширине блока, имя — подсказкой; наведение
        // примеряет цвет на панели, нажатие красит и значок у часов.
        float AccentCard(Graphics g, float y)
        {
            const float h = 82, tile = 28;
            if (g != null)
            {
                TileBg(g, Pad, y, Inner, h);
                var sz = Txt(g, L.T("accent.caps"), 9f, FontStyle.Bold, Palette.Dim, CX, y + 12.5f, 120, false);
                Txt(g, L.T("accent.note"), 9.5f, FontStyle.Regular, Palette.Dim, CX + sz.Width / k + 8, y + 12, CW - sz.Width / k - 8, false);
            }
            float ty = y + 40, step = (CW - tile) / (Palette.Accents.Length - 1);
            PreviewArea(g, CX - 4, ty - 4, CW + 8, tile + 8);
            var current = Palette.AccentId;
            foreach (var a in Palette.Accents)
            {
                string id = a[0];
                float ax = CX + Array.IndexOf(Palette.Accents, a) * step;
                bool over = Zone(g, ax, ty, tile, tile, delegate { app.SetAccent(id); },
                    L.T(a[1]), delegate { Palette.PreviewAccent = id; Invalidate(); });
                if (g == null) continue;
                var color = Palette.AccentOf(id, false);
                TileFace(g, ax, ty, tile, tile, 8, over ? 1.08f : 1f, id == current, 2f, (gg, r) =>
                {
                    using (var path = Palette.Round(r, F(8) * (r.Width / F(tile))))
                    using (var br = new SolidBrush(color)) gg.FillPath(br, path);
                });
            }
            return h;
        }

        // ---------------------------------------------------------------- «Идёт Workflow сейчас», «Токены»

        // Карточка по содержимому: сперва меряем, потом фон и содержимое.
        float Card(Graphics g, float y, Func<Graphics, float, float> body)
        {
            float h = body(null, y);
            if (g != null) { TileBg(g, Pad, y, Inner, h); body(g, y); }
            return h;
        }

        // Заголовок группы в карточке — мелкими прописными.
        float Caps(Graphics g, float y, string title, string right = null)
        {
            if (g != null)
            {
                CapsLabel(g, title, CX, y, CW * 0.6f);
                if (right != null) Txt(g, right, 9.5f, FontStyle.Regular, Palette.Dim, CX + CW * 0.4f, y - 1, CW * 0.6f, false, true);
            }
            return 22;
        }

        // «Workflow» (заголовок без «Идёт» — пожелание 17.10.2026; пожелание 17.10.2026: «сразу понятно, сколько сейчас идёт, сколько запланировано; инфографика
        // сверху; у каждого короткое название; кликабельные — раскрыть подробнее; каждая ячейка — живая»). Шапка — счёт
        // сразу за подписью, вплотную: «8 готово · 4 идут · 13 впереди» (серым и тихо — пожелание 17.10.2026 08:45; ждёт —
        // жёлтым, упал — красным; знаки — единой схемой, легенда при наведении на счёт;
        // пожелание 17.10.2026: «цифры ближе… без длинных пробелов»). Каждое число с подписью — кнопка-фильтр («это как
        // фильтр: нажимаю 8 готово — показывает готовые»): выбранный подсвечен, повторное нажатие — снова «идут»
        // (по умолчанию); ноль — бледный и не нажимается. Под шапкой — полоса по отрезку на Workflow, выбранная группа
        // в ней ярко, остальные бледно. Ниже — строки выбранной группы порциями по 5 с «‹ ›», по проектам с заголовком:
        // «идут» — каждый идущий, ждущий или упавший (сводка свежее 12 ч), под ними «Впереди · N» (свёрнуто или
        // развёрнуто — помнится в settings.json); «готово» и «впереди» — своими строками, одной строкой каждая, без шагов.
        // Строка: номер ячейкой, короткое имя, справа этап и модель идущей роли; шаги отрезками и «1 из 3 · 25 мин».
        // Нажатие — карточка на месте блока (FlowPager, с 17.10.2026 ~06:50 — «всплывающих окошек не надо»), листается
        // внутри выбранной группы: этапы — только записанные ролью, кто и на чём, когда, «Открыть чат». Внизу —
        // «Запустить Workflow». Строку «Сейчас: …» из шапки status.md под Workflow не ставим: она про весь проект.
        // Открытая карточка («папка#номер»; null — список), её соседи по листанию и проезд прошлой страницы.
        string flowCard, flowSlidePrev;
        List<string> flowPages = new List<string>();
        DateTime flowSlideAt;
        int flowSlideDir, flowWheel;
        DateTime flowWheelAt;
        RectangleF flowCardRect;
        Point? swipeFrom;
        // Прошлая страница во время проезда рисуется без мест нажатия (Zone их не заводит).
        bool ghost;
        // Фильтр по счёту: run (идущие с ждущими и упавшими — по умолчанию), wait, fail, done, plan. Не помнится:
        // панель закрылась — снова «идут», как открытая карточка. Фокус клавиатуры — на счёте (Tab, ← →, Enter, пробел).
        string flowFilter = "run", flowFocus;
        readonly List<string> flowFocusable = new List<string>();

        static bool AheadOpen { get { return Settings.Get(Settings.WorkflowAheadOpen, false); } }

        // Группы строк блока по состоянию: идущие — по плиткам (сводка свежее 12 ч, свежие первыми), остальные — по
        // сводкам за сутки. Счёт в шапке и полоса считаются по этим же строкам — число всегда равно списку.
        Dictionary<string, List<KeyValuePair<StatusProject, int>>> FlowGroups(List<StatusProject> list, DateTime now)
        {
            List<StatusScan.Tile> running, waiting;
            StatusScan.Tiles(list, now, out running, out waiting);
            var active = new List<KeyValuePair<StatusProject, int>>();
            foreach (var t in running)
                for (int i = 0; i < t.P.Flows.Count; i++)
                {
                    var st = t.P.Flows[i].State;
                    if (st == "run" || st == "wait" || st == "fail") active.Add(new KeyValuePair<StatusProject, int>(t.P, i));
                }
            var fresh = list.Where(p => (now - p.Updated).TotalHours <= 24).ToList();
            Func<string, List<KeyValuePair<StatusProject, int>>> of = state =>
            {
                var res = new List<KeyValuePair<StatusProject, int>>();
                foreach (var p in fresh)
                    for (int i = 0; i < p.Flows.Count; i++)
                        if (p.Flows[i].State == state) res.Add(new KeyValuePair<StatusProject, int>(p, i));
                return res;
            };
            // строки одного проекта — подряд (порядок проектов — по первой строке), чтобы заголовок шёл один раз
            Func<List<KeyValuePair<StatusProject, int>>, List<KeyValuePair<StatusProject, int>>> byProject =
                rows => rows.GroupBy(r => r.Key.Folder + "|" + FlowProject(r.Key, r.Value)).SelectMany(x => x).ToList();
            active = byProject(active);
            return new Dictionary<string, List<KeyValuePair<StatusProject, int>>>
            {
                { "run", active },
                { "wait", active.Where(r => r.Key.Flows[r.Value].State == "wait").ToList() },
                { "fail", active.Where(r => r.Key.Flows[r.Value].State == "fail").ToList() },
                // готовые — свежие сверху, по времени окончания (проект — по своему свежему)
                { "done", byProject(of("done").OrderByDescending(r => FinishedAt(r.Key, r.Key.Flows[r.Value], now)).ToList()) },
                { "plan", byProject(of("plan")) },
            };
        }

        // Проект строки Workflow. В status.md поля «проект» у Workflow нет: Workflow других проектов ведутся в одном
        // run-файле с приставкой в «о чём» — «PMP: …». Приставка до «:» — проект,
        // если начинается с латинской заглавной или стоит у двух и больше Workflow файла («разрезка 4:», «сбор:» —
        // разовые, не проекты). Иначе — проект самого status.md.
        static string FlowPrefix(StatusProject p, int index)
        {
            var about = p.Flows[index].About;
            int colon = about.IndexOf(':');
            if (colon <= 0 || colon > 24) return null;
            var pre = about.Substring(0, colon).Trim();
            if (pre.Length == 0 || pre == p.Name) return null;
            if (pre[0] >= 'A' && pre[0] <= 'Z') return pre;
            int same = p.Flows.Count(f => f.About.StartsWith(pre + ":", StringComparison.Ordinal));
            return same >= 2 ? pre : null;
        }

        static string FlowProject(StatusProject p, int index) { return FlowPrefix(p, index) ?? p.Name; }

        // Когда кончился готовый Workflow: второе время строки («13:05 → 13:50»), нет его — первое; часы — того дня,
        // когда записан status.md (впереди записи больше чем на 3 ч — вчера). Времени нет — в самый низ.
        static DateTime FinishedAt(StatusProject p, StatusFlow f, DateTime now)
        {
            var times = Regex.Matches(f.Time ?? "", @"(\d{1,2})\s*:\s*(\d{2})").Cast<Match>().ToList();
            if (times.Count == 0) return DateTime.MinValue;
            var m = times.Count > 1 ? times[1] : times[0];
            int hh = int.Parse(m.Groups[1].Value), mm = int.Parse(m.Groups[2].Value);
            if (hh > 23 || mm > 59) return DateTime.MinValue;
            var written = Written(p, now);
            var at = written.Date.AddHours(hh).AddMinutes(mm);
            at = at > written.AddHours(3) ? at.AddDays(-1) : at;
            // записано одно время и сколько занял («13:05 · 0,5 ч», «13:05 · 45 мин») — конец = начало + длительность
            if (times.Count == 1) at = at.Add(Took(f.Time));
            return at;
        }

        // Сколько занял по хвосту строки времени: «· 1,7 ч», «· 45 мин», «· 1 ч 20 мин»; нет — ноль.
        static TimeSpan Took(string time)
        {
            var tail = Regex.Match(time ?? "", @"·\s*([^·]+)$");
            if (!tail.Success) return TimeSpan.Zero;
            var t = tail.Groups[1].Value;
            double minutes = 0;
            var h = Regex.Match(t, @"(\d+(?:[.,]\d+)?)\s*ч");
            if (h.Success) minutes += 60 * double.Parse(h.Groups[1].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
            var mi = Regex.Match(t, @"(\d+)\s*мин");
            if (mi.Success) minutes += int.Parse(mi.Groups[1].Value);
            return TimeSpan.FromMinutes(Math.Min(minutes, 24 * 60));
        }

        float WorkflowBody(Graphics g, float y)
        {
            var list = StatusScan.Current;
            var groups = FlowGroups(list ?? new List<StatusProject>(), DateTime.Now);
            if (!groups.ContainsKey(flowFilter) || groups[flowFilter].Count == 0) flowFilter = "run";
            var shown = groups[flowFilter];
            var planned = groups["plan"];
            float top = y;
            if (flowCard != null && IsOpen(Settings.WorkflowOpen))
            {
                // листают внутри выбранной группы; у «идут» запланированные под «Впереди» — между запланированными
                var group = shown.Any(r => FlowKey(r.Key, r.Value) == flowCard) ? shown
                    : flowFilter == "run" && planned.Any(r => FlowKey(r.Key, r.Value) == flowCard) ? planned : null;
                if (group != null) return FlowPager(g, y, group);
            }
            flowCard = null;
            flowSlidePrev = null;
            flowPages.Clear();
            bool open = IsOpen(Settings.WorkflowOpen);
            // счёт — места нажатия раньше шапки (первое место под курсором главнее), рисуется после её подсветки
            CountY = y;
            var counts = list == null ? null : FlowCountItems(groups, y, open);
            bool overCount = counts != null && FlowCountZones(g, counts, open);
            FoldHead(g, Pad, y, Inner, Settings.WorkflowOpen, L.Caps("widget.workflow"), null, null, true, quiet: overCount);
            if (g != null && counts != null) FlowCountDraw(g, counts, open);
            if (!open) return 36;
            y += 30;
            var strip = groups["done"].Concat(groups["run"]).Concat(planned).Select(r => r.Key.Flows[r.Value]).ToList();
            if (strip.Count > 0)
            {
                if (g != null) FlowStrip(g, CX, y + 1, CW, strip, flowFilter);
                y += FlowStripH + 9;
            }
            if (shown.Count == 0)
            {
                if (g != null) Txt(g, list == null ? L.T("flow.reading") : L.T("flow.nothingRunning"), 10f, FontStyle.Regular, Palette.Dim, CX, y + 2, CW, false);
                y += 24;
            }
            else
            {
                y += 2;
                y += FlowRows(g, y, shown, false);
            }
            if (flowFilter == "run" && planned.Count > 0)
            {
                bool ahead = AheadOpen;
                bool over = Zone(g, Pad + 7, y, Inner - 14, 26, delegate { Settings.Set(Settings.WorkflowAheadOpen, !ahead); Refit(); },
                    ahead ? L.T("flow.aheadCollapseTip") : L.T("flow.aheadExpandTip"));
                if (g != null)
                {
                    if (over) Hover(g, Pad + 7, y, Inner - 14, 26);
                    var sz = Txt(g, L.F("flow.ahead", planned.Count), 10f, FontStyle.Regular, over ? Palette.Text : Palette.Dim, CX, y + 5, CW - 30, false);
                    Glyph(g, ahead ? "\uE70E" : "\uE70D", 8f, over ? Palette.Text : Palette.Dim, CX + sz.Width / k + 6, y + 4, 12, 18);
                }
                y += 26;
                if (ahead) y += FlowRows(g, y, planned, true);
            }
            return y + 4 - top;
        }

        // Строки группы порциями по FlowPageSize (пожелание 17.10.2026: «не списком… порциями, компактно, и по
        // проектам»): над строками каждого проекта — его имя заголовком со счётом, строки имя не повторяют
        //; больше порции — внизу «‹ 1–5 из 13 ›». ahead — список под «Впереди · N» (своя порция).
        const int FlowPageSize = 5;
        int flowPage, aheadPage;

        float FlowRows(Graphics g, float y, List<KeyValuePair<StatusProject, int>> rows, bool ahead)
        {
            float top = y;
            int pages = Math.Max(1, (rows.Count + FlowPageSize - 1) / FlowPageSize);
            int page = Math.Max(0, Math.Min(ahead ? aheadPage : flowPage, pages - 1));
            if (ahead) aheadPage = page; else flowPage = page;
            float h = PortionRows(g, y, rows, page);
            if (pages > 1)
            {
                // высота — по самой высокой порции: листаешь — «‹ ›» и всё ниже стоят на месте, панель не прыгает
                for (int i = 0; i < pages; i++) h = Math.Max(h, PortionRows(null, 0, rows, i));
                y += h;
                y += PageRow(g, y, page, pages, rows.Count, ahead);
            }
            else y += h;
            return y - top;
        }

        float PortionRows(Graphics g, float y, List<KeyValuePair<StatusProject, int>> rows, int page)
        {
            float top = y;
            string prev = null;
            foreach (var r in rows.Skip(page * FlowPageSize).Take(FlowPageSize))
            {
                string name = FlowProject(r.Key, r.Value), id = r.Key.Folder + "|" + name;
                // счёт проекта — только там, где группа начинается; на следующей порции — одно имя
                bool first = rows.FindIndex(o => o.Key.Folder + "|" + FlowProject(o.Key, o.Value) == id) >= page * FlowPageSize;
                if (id != prev) y += ProjectHead(g, y, name, first ? rows.Count(o => o.Key.Folder + "|" + FlowProject(o.Key, o.Value) == id) : 0);
                y += FlowRow(g, Pad + 7, y, Inner - 14, r.Key, r.Value, false);
                prev = id;
            }
            return y - top;
        }

        // Заголовок проекта над его строками: имя жирным бледно, счёт — обычным; по левой грани ячеек номера.
        float ProjectHead(Graphics g, float y, string name, int count)
        {
            if (g != null)
            {
                float x = Pad + 7 + 6;
                var sz = Txt(g, name, 9.5f, FontStyle.Bold, Palette.Dim, x, y + 6, CW - 40, false);
                if (count > 1) Txt(g, "· " + count, 9.5f, FontStyle.Regular, Palette.Dim, x + sz.Width / k + 4, y + 6, 40, false);
            }
            return 22;
        }

        // Листание порций: ‹ и › по краям подписи «6–10 из 13», посередине блока; на крайней порции стрелка бледная.
        float PageRow(Graphics g, float y, int page, int pages, int count, bool ahead)
        {
            string label = L.F("flow.pageOf", page * FlowPageSize + 1, Math.Min(count, (page + 1) * FlowPageSize), count);
            float lw = Measure(label, 9.5f, FontStyle.Regular), bw = 26, cx = Pad + Inner / 2;
            float lx = cx - lw / 2, h = 24;
            Action<int> turn = d =>
            {
                if (ahead) aheadPage += d; else flowPage += d;
                Refit();
            };
            for (int side = -1; side <= 1; side += 2)
            {
                int d = side;
                bool can = side < 0 ? page > 0 : page < pages - 1;
                float bx = side < 0 ? lx - 6 - bw : lx + lw + 6;
                bool over = Zone(g, bx, y, bw, h, can ? (Action)delegate { turn(d); } : null,
                    can ? L.F(side < 0 ? "flow.prevPage" : "flow.nextPage", FlowPageSize) : null);
                if (g == null) continue;
                if (over && can) Hover(g, bx, y, bw, h);
                Glyph(g, side < 0 ? "" : "", 8f, !can ? Color.FromArgb(0x50, Palette.Dim) : over ? Palette.Text : Palette.Dim, bx + (bw - 12) / 2, y + 3, 12, 18);
            }
            if (g != null) Txt(g, label, 9.5f, FontStyle.Regular, Palette.Dim, lx, y + 4, lw + 4, false);
            return h + 2;
        }

        class FlowCount { public string Key, Num, Word, Tip; public Color NumColor, WordColor; public bool On; public float X, W; }

        // Счёт в шапке слева направо сразу за «WORKFLOW»: готово · идут (ждут, упали) · впереди. Число жирным, подпись —
        // обычным; серым (выбранный — цветом текста), ждёт — жёлтым, упал — красным, ноль — бледнее. Ширины — по настоящему
        // холсту (Measure): мерка без холста прибавляет к каждому слову ~7 точек, отсюда были «длинные пробелы».
        List<FlowCount> FlowCountItems(Dictionary<string, List<KeyValuePair<StatusProject, int>>> groups, float y, bool open)
        {
            int done = groups["done"].Count, plan = groups["plan"].Count, wait = groups["wait"].Count, fail = groups["fail"].Count;
            int run = groups["run"].Count - wait - fail;
            Color faint = Color.FromArgb(0x90, Palette.Dim);
            Func<string, int, string, Color, string, FlowCount> item = (key, n, word, color, tip) => new FlowCount
            {
                Key = key, Num = n.ToString(), Word = word, On = n > 0,
                NumColor = n == 0 ? faint : color, WordColor = n == 0 ? faint : color,
                Tip = n == 0 ? tip : null,
            };
            // серым и тихо (пожелание 17.10.2026 08:45: «сейчас много внимания отвлекает»); цвет — у знаков полосы и строк,
            // их смысл — в легенде при наведении на счёт. Ждёт тебя и упал — своим цветом: это зов, их почти не бывает.
            var items = new List<FlowCount>
            {
                item("done", done, L.T("count.done"), Palette.Dim, L.T("count.noneDone")),
                item("run", run, L.P(run, "count.run"), Palette.Dim, L.T("flow.nothingRunning")),
            };
            if (wait > 0) items.Add(item("wait", wait, L.P(wait, "count.wait"), Palette.Yellow, null));
            if (fail > 0) items.Add(item("fail", fail, L.P(fail, "count.fail"), Palette.Red, null));
            items.Add(item("plan", plan, L.T("count.plan"), Palette.Dim, L.T("count.nonePlan")));
            float x = CX + Measure(L.Caps("widget.workflow"), 9f, FontStyle.Bold) + 16;
            float right = Pad + Inner - 14 - 12 - 6;
            foreach (var c in items)
            {
                c.X = x;
                c.W = Measure(c.Num, 10.5f, FontStyle.Bold) + 4 + Measure(c.Word, 9.5f, FontStyle.Regular);
                x += c.W + FlowCountGap;
            }
            // не влезает — с конца долой (выбранный остаётся)
            while (items.Count > 1 && items[items.Count - 1].X + items[items.Count - 1].W + FlowCountPad > right)
            {
                int drop = items.FindLastIndex(c => c.Key != flowFilter);
                if (drop < 0) break;
                items.RemoveAt(drop);
                x = items[0].X;
                foreach (var c in items) { c.X = x; x += c.W + FlowCountGap; }
            }
            return items;
        }

        // Поля кнопки счёта вокруг текста и шаг между кнопками (в нём — бледная точка).
        const float FlowCountPad = 6, FlowCountGap = 2 * FlowCountPad + 8;

        float FlowCountTop(bool open) { return open ? 3 : 5.5f; }

        // Места нажатия счёта; true — курсор над одним из них (подсветку шапки тогда не рисуем).
        bool FlowCountZones(Graphics g, List<FlowCount> items, bool open)
        {
            if (g == null) return false;
            bool any = false;
            foreach (var c in items)
            {
                var key = c.Key;
                if (c.On) flowFocusable.Add(key);
                // обычной подсказки нет: над счётом — легенда знаков с тем же «нажми — покажу» (DrawLegend)
                any |= Zone(g, c.X - FlowCountPad, CountY + FlowCountTop(open), c.W + 2 * FlowCountPad, 24,
                    c.On ? (Action)delegate { PickFlowFilter(key); } : null, null);
            }
            return any;
        }

        // Шапка рисуется на y блока — запомненный здесь, чтобы места и рисунок счёта стояли на одной строке.
        float CountY;

        void FlowCountDraw(Graphics g, List<FlowCount> items, bool open)
        {
            float py = CountY + FlowCountTop(open), ty = py + 4.5f;
            for (int i = 0; i < items.Count; i++)
            {
                var c = items[i];
                var r = new RectangleF(F(c.X - FlowCountPad), F(py - scroll), F(c.W + 2 * FlowCountPad), F(24));
                bool over = c.On && mouse.HasValue && r.Contains(mouse.Value);
                bool picked = open && c.Key == flowFilter && c.On;
                if (picked || over)
                {
                    Color tint = c.Key == "wait" || c.Key == "fail" ? c.NumColor : Color.White;
                    using (var path = Palette.Round(new RectangleF(F(c.X - FlowCountPad), F(py), F(c.W + 2 * FlowCountPad), F(24)), F(12)))
                    using (var br = new SolidBrush(Color.FromArgb(picked ? (over ? 0x34 : 0x26) : 0x14, tint))) g.FillPath(br, path);
                }
                if (flowFocus == c.Key && c.On)
                    using (var path = Palette.Round(new RectangleF(F(c.X - FlowCountPad + 0.5f), F(py + 0.5f), F(c.W + 2 * FlowCountPad - 1), F(23)), F(11.5f)))
                    using (var pen = new Pen(Palette.Accent, F(1.2f))) g.DrawPath(pen, path);
                if (i > 0) Txt(g, "·", 9.5f, FontStyle.Regular, Palette.Dim, c.X - FlowCountGap / 2 - 2, ty + 1.5f, 6, false);
                // выбранный фильтр — цветом текста, остальные серым
                Color num = picked && c.Key != "wait" && c.Key != "fail" ? Palette.Text : c.NumColor;
                Txt(g, c.Num, 10.5f, FontStyle.Bold, num, c.X, ty, 40, false);
                Txt(g, c.Word, 9.5f, FontStyle.Regular, picked && c.Key != "wait" && c.Key != "fail" ? Palette.Text : c.WordColor, c.X + Measure(c.Num, 10.5f, FontStyle.Bold) + 4, ty + 1.5f, 90, false);
            }
            // легенда — при наведении на счёт, через паузу как у подсказок; рисуется поверх блоков в Draw
            var all = items.Count == 0 ? RectangleF.Empty : new RectangleF(F(items[0].X - FlowCountPad), F(py - scroll),
                F(items[items.Count - 1].X + items[items.Count - 1].W + FlowCountPad - (items[0].X - FlowCountPad)), F(24));
            bool hover = mouse.HasValue && all.Contains(mouse.Value) && dragId == null;
            if (!hover) { legendSince = null; legendAt = null; return; }
            if (legendSince == null) legendSince = DateTime.Now;
            legendKinds = items.Select(c => c.Key).ToList();
            // ноль не нажимается — вместо «нажми» его пояснение; «ещё раз — снова идущие» — только когда выбранное видно
            // подсвеченным (блок раскрыт и фильтр не «идут»): свёрнутый блок нажатием раскроется с тем, на что нажали
            var under = items.FirstOrDefault(c => mouse.Value.X >= F(c.X - FlowCountPad) && mouse.Value.X <= F(c.X + c.W + FlowCountPad));
            bool pickedShown = open && flowFilter != "run" && items.Any(c => c.On && c.Key == flowFilter);
            legendHint = under != null && !under.On && under.Tip != null ? under.Tip
                : !items.Any(c => c.On) ? L.T("legend.nothing")
                : pickedShown ? (under != null && under.Key == flowFilter ? L.T("legend.again") : L.T("legend.pickOrBack"))
                : L.T("legend.pick");
            legendAt = new PointF(items[0].X - FlowCountPad, py);
            if (!anim.Enabled) anim.Start();
        }

        // Легенда счёта: с какого мига курсор на счёте, где счёт (точки листа) и какие группы в нём есть.
        DateTime? legendSince;
        PointF? legendAt;
        List<string> legendKinds = new List<string>();
        // Подпись внизу легенды: курсор на нуле — его пояснение («Готовых за сутки нет»), иначе — что делает нажатие.
        string legendHint;

        bool LegendShown { get { return legendAt.HasValue && legendSince.HasValue && (DateTime.Now - legendSince.Value).TotalSeconds >= 0.45; } }

        // Подсказка-легенда (пожелание 17.10.2026 08:50: «нужно условное кодирование… чтобы интуитивно понятно»): те же
        // знаки, что в полосе, строках и карточке, — и что делает нажатие на число. Над счётом; не влезает — под ним.
        void DrawLegend(Graphics g)
        {
            if (!LegendShown) return;
            var lines = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("done", L.T("legend.done")),
                new KeyValuePair<string, string>("run", motionNow ? L.T("legend.runPulse") : L.T("legend.run")),
            };
            if (legendKinds.Contains("wait")) lines.Add(new KeyValuePair<string, string>("wait", L.T("legend.wait")));
            if (legendKinds.Contains("fail")) lines.Add(new KeyValuePair<string, string>("fail", L.T("legend.fail")));
            lines.Add(new KeyValuePair<string, string>("plan", L.T("legend.plan")));
            const float row = 20, padX = 12, padY = 9, mark = 22;
            string hint = legendHint ?? L.T("legend.pick");
            float w = Math.Max(lines.Max(l => Measure(l.Value, 9.5f, FontStyle.Regular)) + mark + 10, Measure(hint, 9f, FontStyle.Regular)) + 2 * padX;
            float h = padY + lines.Count * row + 4 + 16 + padY;
            float x = Math.Min(legendAt.Value.X, Pad + Inner - w), y = legendAt.Value.Y - h - 6;
            if (y - scroll < 14 + 24 + 9 + 4) y = legendAt.Value.Y + 24 + 6;
            for (int i = 3; i >= 1; i--)
                using (var path = Palette.Round(new RectangleF(F(x - i), F(y + 3 - i), F(w + 2 * i), F(h + 2 * i)), F(10 + i)))
                using (var br = new SolidBrush(Color.FromArgb(0x22, 0, 0, 0))) g.FillPath(br, path);
            using (var path = Palette.Round(new RectangleF(F(x), F(y), F(w), F(h)), F(10)))
            {
                using (var br = new SolidBrush(Palette.Tile)) g.FillPath(br, path);
                using (var pen = new Pen(Color.FromArgb(0x30, 255, 255, 255), 1)) g.DrawPath(pen, path);
            }
            float ly = y + padY;
            foreach (var l in lines)
            {
                FlowMark(g, x + padX, ly + 7, mark, 6, l.Key, true);
                Txt(g, l.Value, 9.5f, FontStyle.Regular, Palette.Text, x + padX + mark + 10, ly + 2, w - mark - 10 - 2 * padX, false);
                ly += row;
            }
            Txt(g, hint, 9f, FontStyle.Regular, Palette.Dim, x + padX, ly + 4, w - 2 * padX, false);
        }

        // ---- единая схема знаков Workflow (пожелание 17.10.2026 08:50) ----
        // Готово — залитая зелёная полоска; идёт — рамочка цветом PMP с лёгким пульсом (в Windows выключены анимации —
        // без пульса); впереди — серая тусклая; ждёт тебя — жёлтая, упал — красная. Одна схема у полосы сверху,
        // ячейки номера и шагов в строке, точек и кружков карточки, легенды.
        [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, ref bool value, uint ini);

        // «Показывать анимацию в Windows» (Параметры → Специальные возможности → Визуальные эффекты).
        static bool Motion
        {
            get
            {
                bool on = true;
                try { if (!SystemParametersInfo(0x1042, 0, ref on, 0)) on = true; } catch { on = true; }
                return on;
            }
        }

        // Кадр пульса 0..1 (≈1,6 с на вдох-выдох); рисование пульса держит таймер кадров, пока панель на экране.
        bool pulseSeen;
        // Настройка анимаций Windows — читается раз за кадр (OnPaint), а не на каждый знак.
        bool motionNow = true;

        float Pulse()
        {
            if (!motionNow) return 1;
            pulseSeen = true;
            if (!anim.Enabled) anim.Start();
            double t = (DateTime.Now - DateTime.Today).TotalSeconds;
            return (float)(0.5 + 0.5 * Math.Sin(t * 2 * Math.PI / 1.6));
        }

        // Цвет знака по состоянию; dim — знак не из выбранной фильтром группы (бледнее).
        static Color MarkColor(string state)
        {
            return state == "done" ? Palette.Green : state == "run" ? Palette.Accent : state == "wait" ? Palette.Yellow : state == "fail" ? Palette.Red
                : Color.FromArgb(0x55, Palette.Dim);
        }

        // Полоска-знак: готово, ждёт, упал — залитая; идёт — рамочка с пульсом; впереди — серая тусклая.
        // lit — серая выбрана фильтром «впереди»: ярче обычной тусклой.
        void FlowMark(Graphics g, float x, float y, float w, float h, string state, bool strong, bool lit = false)
        {
            var rect = new RectangleF(F(x), F(y), F(w), F(h));
            float rad = F(Math.Min(h / 2, w / 2));
            var c = lit ? Color.FromArgb(0xB0, Palette.Dim) : MarkColor(state);
            if (!strong && state != "plan") c = Color.FromArgb(0x48, c);
            if (state == "run")
            {
                float p = strong ? Pulse() : 0, pw = F(Math.Max(1.2f, Math.Min(1.6f, h / 4)));
                var inset = new RectangleF(rect.X + pw / 2, rect.Y + pw / 2, Math.Max(1, rect.Width - pw), Math.Max(1, rect.Height - pw));
                using (var path = Palette.Round(inset, Math.Max(0.5f, rad - pw / 2)))
                {
                    if (strong) using (var br = new SolidBrush(Color.FromArgb((int)(0x10 + 0x30 * p), Palette.Accent))) g.FillPath(br, path);
                    int a = strong ? (int)(0x70 + 0x8F * p) : 0x60;
                    using (var pen = new Pen(Color.FromArgb(a, Palette.Accent), pw)) g.DrawPath(pen, path);
                }
                return;
            }
            using (var path = Palette.Round(rect, rad))
            using (var br = new SolidBrush(c)) g.FillPath(br, path);
        }

        // Нажатие счёта: другой — показать его группу, тот же ещё раз — снова «идут». Блок свёрнут — раскрыть с этой группой.
        void PickFlowFilter(string key)
        {
            flowFocus = flowFocus != null ? key : null;
            if (!IsOpen(Settings.WorkflowOpen)) { flowFilter = key; flowPage = 0; Fold(Settings.WorkflowOpen, true); return; }
            flowFilter = flowFilter == key ? "run" : key;
            flowPage = 0;
            Refit();
        }

        void MoveFlowFocus(int dir)
        {
            if (flowFocusable.Count == 0) return;
            int at = flowFocus == null ? (dir > 0 ? -1 : flowFocusable.Count) : flowFocusable.IndexOf(flowFocus);
            at = Math.Max(0, Math.Min(flowFocusable.Count - 1, at + dir));
            flowFocus = flowFocusable[at];
            Invalidate();
        }

        // Ширина строки по настоящему холсту — та же, что выйдет при рисовании.
        static readonly Graphics MeasureCanvas = Graphics.FromImage(new Bitmap(1, 1));

        float Measure(string text, float pt, FontStyle style)
        {
            using (var font = new Font("Segoe UI", pt * k, style, GraphicsUnit.Pixel))
                return TextRenderer.MeasureText(MeasureCanvas, text, font, new Size(4000, 200), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width / k;
        }

        // Полоса — по отрезку на Workflow, знаками единой схемы (FlowMark; пожелание 17.10.2026: «зелёным принято выделять
        // выполненное»): готовые — зелёные, идущие — рамочкой с пульсом, ждёт — жёлтым, упал — красным, впереди — серые. Выбранная фильтром
        // группа — в полный цвет, остальные бледно: переключила фильтр — полоса показывает его («они же могут выделяться»).
        void FlowStrip(Graphics g, float x, float y, float w, List<StatusFlow> all, string filter)
        {
            Func<string, bool> picked = st => filter == "run" ? st == "run" || st == "wait" || st == "fail" : st == filter;
            var order = new[] { "done", "run", "wait", "fail", "plan" };
            var list = all.OrderBy(f => Array.IndexOf(order, f.State)).ToList();
            int n = list.Count;
            float segGap = n > 30 ? 1.5f : 3, segW = (w - segGap * (n - 1)) / n;
            for (int i = 0; i < n; i++)
            {
                var st = list[i].State;
                // знаки — единой схемой (FlowMark); не из выбранной группы — бледнее
                FlowMark(g, x + i * (segW + segGap), y, segW, FlowStripH, st, picked(st), filter == "plan" && st == "plan");
            }
        }

        // Полоса сверху — жирными полосками (пожелание 17.10.2026 08:50: «выполненные — зелёные жирненькие полоски»).
        const float FlowStripH = 8;

        // Короткое имя: до «:», «;», «,» или « — », не длиннее 28 знаков — по целому слову, без «…».
        static string ShortName(string about)
        {
            var s = about.Trim();
            int cut = s.Length;
            foreach (var mark in new[] { ":", ";", ",", " — ", " – ", " (" })
            {
                int i = s.IndexOf(mark, StringComparison.Ordinal);
                if (i > 0 && i < cut) cut = i;
            }
            s = s.Substring(0, cut).Trim();
            if (s.Length <= 28) return s;
            int space = s.LastIndexOf(' ', 28);
            s = space > 8 ? s.Substring(0, space) : s.Substring(0, 28);
            // предлог или союз в хвосте («охота за ошибками в») — долой
            var words = s.Split(' ').ToList();
            var small = new[] { "в", "во", "и", "на", "по", "за", "с", "со", "к", "о", "об", "от", "до", "для", "из", "у", "а", "но", "или", "—", "–" };
            while (words.Count > 1 && small.Contains(words[words.Count - 1].ToLowerInvariant())) words.RemoveAt(words.Count - 1);
            return string.Join(" ", words);
        }

        // Идущая роль: с 💭, иначе последняя записанная (формат status.md: без пометки идёт последняя).
        static FlowRole CurrentRole(StatusFlow f)
        {
            return f.Roles.FirstOrDefault(r => r.Running && !r.Past) ?? f.Roles.LastOrDefault(r => !r.Past);
        }

        // Этап по роли: «планирование» — план, «критика/ревизия/ревью» — критик, «проверка/тест/гейт» — тест;
        // кодинг и исследование — тоже словом таблицы (по-английски — coding, research); прочие роли — как записаны.
        static string StageName(string role)
        {
            var r = role.ToLowerInvariant();
            if (r.StartsWith("план")) return L.T("stage.plan");
            if (r.StartsWith("критик") || r.StartsWith("ревизи") || r.StartsWith("ревью")) return L.T("stage.review");
            if (r.StartsWith("провер") || r.StartsWith("тест") || r.StartsWith("гейт")) return L.T("stage.test");
            if (r.StartsWith("кодинг")) return L.T("stage.code");
            if (r.StartsWith("исследов")) return L.T("stage.research");
            return r;
        }

        // Кто: «я · Opus 5.5 high», «Opus 5.5 high ×2»; не записано — «—».
        static string Who(FlowRole r)
        {
            var s = (r.Me ? L.T("who.me") + (r.Model.Length > 0 ? " · " : "") : "") + r.Model + (r.Agents > 1 ? " ×" + r.Agents : "");
            return s.Length == 0 ? "—" : s;
        }

        // Fable и max — с пометкой 🔴 в status.md: красным и жирным.
        static bool Hot(FlowRole r)
        {
            var m = r.Model.ToLowerInvariant();
            return r.Red || m.Contains("fable") || Regex.IsMatch(m, @"\bmax\b") || m.Contains("extra");
        }

        static string FlowKey(StatusProject p, int index) { return p.Folder + "#" + index; }

        // Строка Workflow 38: [N] Проект · короткое имя ……… этап · модель / шаги отрезками ……… «2 из 6 · 42 мин».
        // Нажатие — карточка на месте блока (FlowPager). Раскрытое не помнится. В карточке (inCard) —
        // шапка страницы: без нажатия и без короткого имени (под ней «о чём» целиком).
        float FlowRow(Graphics g, float x, float y, float w, StatusProject p, int index, bool showName, bool inCard = false)
        {
            var f = p.Flows[index];
            string key = FlowKey(p, index);
            bool wait = f.State == "wait", fail = f.State == "fail", plan = f.State == "plan", finished = f.State == "done";
            var cur = CurrentRole(f);
            // готовые и запланированные — одной строкой без шагов (пожелание 17.10.2026: «компактно»): шаги у них все
            // пройдены или все впереди, отрезки ничего не говорят; справа — когда кончился или сколько шагов
            bool slim = !inCard && (plan || finished);
            int steps = f.Total ?? 0;
            string role = wait ? L.T("row.waitsYou") : fail ? L.T("row.failed") : finished ? When(p, f, true)
                : plan ? (steps > 0 ? steps + " " + L.P(steps, "pl.step") : "")
                : cur == null ? "" : StageName(cur.Name) + (cur.Model.Length > 0 ? " · " + cur.Model : "");
            bool hot = !wait && !fail && !plan && !finished && cur != null && Hot(cur);
            // ждёт, упал и готов — не идут, время у них не растёт
            string time = wait || fail || plan || finished ? "" : Elapsed(f.Time, Written(p, DateTime.Now), DateTime.Now);
            // «2 из 3» — как в status.md и /digest: пройдено шагов из всех (текущий отрезок — бледнее)
            int total = f.Total ?? 0, done = Math.Max(0, Math.Min(f.Done ?? 0, total));
            var tail = new List<string>();
            if (total > 0) tail.Add(L.F("row.stepsOf", done, total));
            if (time.Length > 0) tail.Add(time);
            // приставка-проект («PMP: …») стоит заголовком над строками — в имени строки её нет
            var prefix = FlowPrefix(p, index);
            string text = prefix == null ? f.About : f.About.Substring(f.About.IndexOf(':') + 1).Trim();
            string about = inCard ? "" : text.Length > 0 ? ShortName(text) : "Workflow " + (index + 1);
            float rh = slim ? 24 : 38;
            bool over = !inCard && Zone(g, x, y, w, rh, delegate { OpenFlowCard(key); }, L.T("row.tip"));
            if (g == null) return rh + 2;
            if (over) Hover(g, x, y, w, rh);
            float lx = x + 6, rx = x + w - 6;
            Color ink = plan ? Palette.Dim : Palette.Text;
            // Номер — ячейкой, как в полосе Workflow; двузначный — ячейка шире, цифры не вылезают.
            // Ячейка — знаком единой схемы: готово — залитая зелёная, идёт — рамочка с пульсом, впереди — серая.
            float cellW = index + 1 >= 10 ? 21 : 16;
            Color numInk = CellMark(g, lx, y + 4, cellW, 16, f.State);
            using (var font = new Font("Segoe UI", F(9.5f), FontStyle.Bold, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, (index + 1).ToString(), font, new Rectangle(S(lx), S(y + 4), S(cellW), S(16)), numInk,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | Keep);
            float roleW = role.Length == 0 ? 0 : Math.Min(Txt(null, role, 9.5f, hot ? FontStyle.Bold : FontStyle.Regular, Palette.Dim, 0, 0, 220, false).Width / k + 2, w * 0.5f);
            float textRight = rx - (roleW > 0 ? roleW + 8 : 0), nx = lx + cellW + 6;
            float ax = nx;
            if (showName)
            {
                var nsz = Txt(g, FlowProject(p, index), 10.5f, FontStyle.Bold, ink, nx, y + 5, Math.Min(textRight - nx, w * 0.4f), false);
                ax = nx + nsz.Width / k + 3;
            }
            if (about.Length > 0 && textRight - ax > 16) Txt(g, (showName ? "· " : "") + about, 10.5f, FontStyle.Regular, plan ? Palette.Dim : Palette.Text, ax, y + 5, textRight - ax, false);
            if (roleW > 0)
                Txt(g, role, 9.5f, hot ? FontStyle.Bold : FontStyle.Regular, wait ? Palette.Yellow : fail || hot ? Palette.Red : Palette.Dim, rx - roleW, y + 6, roleW, false, true);
            if (slim) return rh + 2;
            // Шаги отрезками единой схемой: пройденные — зелёные, текущий — рамочка с пульсом (ждёт — жёлтый, упал —
            // красный), впереди — серые; у готового все зелёные.
            string right = string.Join(" · ", tail);
            float tw = right.Length == 0 ? 0 : Txt(g, right, 9.5f, FontStyle.Regular, Palette.Dim, rx - 120, y + 23, 120, false, true).Width / k;
            float sx = nx, sw = rx - (tw > 0 ? tw + 10 : 0) - sx;
            if (total > 0 && sw >= 20)
            {
                float segGap = 3, segW = (sw - segGap * (total - 1)) / total;
                if (segW < 2) { segGap = 0; segW = sw / total; }
                for (int i = 0; i < total; i++)
                {
                    string st = finished || i < done ? "done" : i == done && !plan ? (wait ? "wait" : fail ? "fail" : "run") : "plan";
                    FlowMark(g, sx + i * (segW + segGap), y + 26.5f, segW, 6, st, true);
                }
            }
            return 38 + 2;
        }

        // Ячейка номера по состоянию Workflow; возвращает цвет цифры.
        Color CellMark(Graphics g, float x, float y, float w, float h, string state)
        {
            var rect = new RectangleF(F(x), F(y), F(w), F(h));
            if (state == "run")
            {
                float p = Pulse(), pw = F(1.4f);
                var inset = new RectangleF(rect.X + pw / 2, rect.Y + pw / 2, rect.Width - pw, rect.Height - pw);
                using (var path = Palette.Round(inset, F(4) - pw / 2))
                {
                    using (var br = new SolidBrush(Color.FromArgb((int)(0x0C + 0x28 * p), Palette.Accent))) g.FillPath(br, path);
                    using (var pen = new Pen(Color.FromArgb((int)(0x80 + 0x7F * p), Palette.Accent), pw)) g.DrawPath(pen, path);
                }
                return Palette.Text;
            }
            using (var path = Palette.Round(rect, F(4)))
            using (var br = new SolidBrush(MarkColor(state))) g.FillPath(br, path);
            return state == "plan" ? Palette.Dim : Palette.Bg;
        }

        class Stage { public string Title, Who, State; public bool Hot; }

        // Этапы карточки — только записанные ролью (и строкой «прошлые шаги»): прошлые и стоящие до идущей — пройдены,
        // идущая — идёт (ждёт, упал), дальше — впереди; готовый — все пройдены, запланированный — все впереди.
        static List<Stage> Stages(StatusFlow f)
        {
            var list = new List<Stage>();
            var cur = CurrentRole(f);
            int ci = cur == null ? -1 : f.Roles.IndexOf(cur);
            for (int i = 0; i < f.Roles.Count; i++)
            {
                var r = f.Roles[i];
                string state;
                if (f.State == "done") state = "done";
                else if (f.State == "plan") state = "ahead";
                else if (r.Past || i < ci) state = "done";
                else if (i == ci) state = f.State == "wait" ? "wait" : f.State == "fail" ? "fail" : "run";
                else state = "ahead";
                list.Add(new Stage { Title = StageName(r.Name), Who = Who(r), State = state, Hot = Hot(r) });
            }
            return list;
        }

        // Кружок этапа — единой схемой: ✓ зелёный — пройден, рамочка цвета PMP с пульсом — идёт,
        // «!» жёлтый — ждёт, «✕» красный — упал, залитый серый тусклый — впереди (рамочка — только у идущего).
        void StageDot(Graphics g, float cx, float cy, string state)
        {
            var r = new RectangleF(F(cx - 6), F(cy - 6), F(12), F(12));
            Color c = state == "done" ? Palette.Green : state == "wait" ? Palette.Yellow : state == "fail" ? Palette.Red : state == "run" ? Palette.Accent : MarkColor("plan");
            if (state == "done" || state == "wait" || state == "fail")
            {
                using (var br = new SolidBrush(c)) g.FillEllipse(br, r);
                using (var pen = new Pen(Palette.Tile, F(1.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    if (state == "done") g.DrawLines(pen, new[] { new PointF(F(cx - 3), F(cy)), new PointF(F(cx - 0.8f), F(cy + 2.4f)), new PointF(F(cx + 3.2f), F(cy - 2.4f)) });
                    else if (state == "fail") { g.DrawLine(pen, F(cx - 2.5f), F(cy - 2.5f), F(cx + 2.5f), F(cy + 2.5f)); g.DrawLine(pen, F(cx + 2.5f), F(cy - 2.5f), F(cx - 2.5f), F(cy + 2.5f)); }
                    else { g.DrawLine(pen, F(cx), F(cy - 3), F(cx), F(cy + 0.6f)); g.DrawLine(pen, F(cx), F(cy + 2.8f), F(cx), F(cy + 3)); }
                }
                return;
            }
            // идёт — рамочка цветом PMP с пульсом (единая схема), впереди — залитый серый тусклый, как точки и ячейки
            if (state == "run")
            {
                float p = Pulse();
                using (var br = new SolidBrush(Color.FromArgb((int)(0x10 + 0x30 * p), c))) g.FillEllipse(br, r);
                using (var pen = new Pen(Color.FromArgb((int)(0x80 + 0x7F * p), c), F(1.6f))) g.DrawEllipse(pen, r);
                return;
            }
            using (var br = new SolidBrush(c)) g.FillEllipse(br, r);
        }

        // Когда: «Начался 25 мин назад, в 13:05 · закончит в 13:50», «Завершился в 01:20 · занял 1 ч»,
        // «Ещё не начат».
        // brief — для строки готового: «в 01:20» без «Завершился» и без «занял».
        static string When(StatusProject p, StatusFlow f, bool brief = false)
        {
            if (f.State == "plan") return L.T("when.notStarted");
            var times = Regex.Matches(f.Time ?? "", @"(\d{1,2}\s*:\s*\d{2})").Cast<Match>().Select(m => m.Groups[1].Value.Replace(" ", "")).ToList();
            if (times.Count == 0) return "";
            if (f.State == "done")
            {
                var end = times.Count > 1 ? times[1] : times[0];
                if (brief) return L.F("row.finishedAt", end);
                var took = Regex.Match(f.Time, @"·\s*([^·]+)$");
                var span = Took(f.Time);
                // «занял» — единицами языка интерфейса; не разобралось — как записано в status.md
                string tookText = !took.Success || Clock(took.Groups[1].Value) ? "" : span > TimeSpan.Zero ? Duration((int)Math.Round(span.TotalMinutes)) : took.Groups[1].Value.Trim();
                return L.F("when.finished", end) + (tookText.Length > 0 ? L.F("when.took", tookText) : "");
            }
            var finish = Regex.Match(f.Time, @"закончит\s+в\s+(\d{1,2}\s*:\s*\d{2})");
            var going = Elapsed(f.Time, Written(p, DateTime.Now), DateTime.Now);
            string start = going.Length == 0 ? L.F("when.startedAt", times[0]) : going == Started ? L.F("when.justStarted", times[0]) : L.F("when.startedAgo", going, times[0]);
            return start + (finish.Success ? L.F("when.ends", finish.Groups[1].Value.Replace(" ", "")) : "");
        }

        static bool Clock(string s) { return Regex.IsMatch(s, @"\d\s*:\s*\d"); }

        // Тело карточки под шапкой страницы: о чём целиком, этапы с кружками и «кто», когда, «Ждёт тебя: …», «Открыть чат».
        float FlowCard(Graphics g, float x, float y, float w, StatusProject p, int index)
        {
            var f = p.Flows[index];
            float top = y, ix = x + 10, iw = w - 20;
            y += 2;
            y += TxtWrap(g, f.About.Length > 0 ? f.About : "Workflow " + (index + 1), 10f, Palette.Text, ix, y, iw) + 8;
            var stages = Stages(f);
            if (stages.Count == 0)
            {
                if (g != null) Txt(g, L.T("card.noRoles"), 9.5f, FontStyle.Regular, Palette.Dim, ix, y, iw, false);
                y += 20;
            }
            for (int i = 0; i < stages.Count; i++)
            {
                var s = stages[i];
                if (g != null)
                {
                    float cx = ix + 6, cy = y + 9;
                    if (i < stages.Count - 1)
                        using (var pen = new Pen(Palette.Track, F(1.5f))) g.DrawLine(pen, F(cx), F(cy + 7), F(cx), F(cy + 22 - 7));
                    StageDot(g, cx, cy, s.State);
                    Txt(g, s.Title, 10f, s.State == "run" ? FontStyle.Bold : FontStyle.Regular, s.State == "ahead" ? Palette.Dim : Palette.Text, ix + 20, y + 1, 64, false);
                    Txt(g, s.Who, 9.5f, s.Hot ? FontStyle.Bold : FontStyle.Regular, s.Hot ? Palette.Red : Palette.Dim, ix + 84, y + 2, iw - 84, false, true);
                }
                y += 22;
            }
            var when = When(p, f);
            if (when.Length > 0)
            {
                if (g != null) Txt(g, when, 9.5f, FontStyle.Regular, Palette.Dim, ix, y + 3, iw, false);
                y += 22;
            }
            if (f.State == "wait" && !StatusScan.Nothing(p.Wait)) y += TxtWrap(g, L.F("card.waitsYou", p.Wait), 9.5f, Palette.Yellow, ix, y + 2, iw) + 6;
            y += 4;
            return y - top;
        }

        // ---- карточка Workflow на месте блока (пожелание 17.10.2026 ~06:50) ----
        // «Всплывающих окошек не надо — такая же панелька прямо на месте, листать влево-вправо между Workflow, сверху
        // точки-лампочки (на каком ты), дошла до последнего — дальше не идёт». Сверху — ‹, точки (знаками единой схемы,
        // на каком ты — крупнее, в светлом кольце), ›; справа ✕ — назад к списку. Листают стрелки, точки, колесо и свайп по карточке, ← →; на крайних —
        // стоп, без кольца. Высота — по самой высокой странице: при листании блок не прыгает.
        const float FlowSlideSeconds = 0.24f;

        bool FlowSliding { get { return flowSlidePrev != null && (DateTime.Now - flowSlideAt).TotalSeconds < FlowSlideSeconds; } }

        void OpenFlowCard(string key)
        {
            flowCard = key;
            flowSlidePrev = null;
            flowWheel = 0;
            Refit();
        }

        void CloseFlowCard()
        {
            flowCard = null;
            flowSlidePrev = null;
            Refit();
        }

        // На соседнюю страницу (dir −1 / +1) или на точку (TurnTo); за край — никуда.
        void Turn(int dir)
        {
            int at = flowPages.IndexOf(flowCard);
            if (at >= 0) TurnTo(at + dir);
        }

        void TurnTo(int index)
        {
            int at = flowPages.IndexOf(flowCard);
            if (at < 0 || index < 0 || index >= flowPages.Count || index == at) return;
            flowSlidePrev = flowCard;
            flowSlideDir = index > at ? 1 : -1;
            flowSlideAt = DateTime.Now;
            flowCard = flowPages[index];
            SetTip(null);
            if (!anim.Enabled) anim.Start();
            Refit();
        }

        float FlowPager(Graphics g, float y, List<KeyValuePair<StatusProject, int>> group)
        {
            float top = y, x = Pad + 7, w = Inner - 14;
            var keys = group.Select(r => FlowKey(r.Key, r.Value)).ToList();
            flowPages = keys;
            int at = keys.IndexOf(flowCard), n = keys.Count;
            float bodyH = group.Max(r => FlowPage(null, x, 0, w, r.Key, r.Value));
            float height = 36 + bodyH + 4;
            if (g == null) return height;
            flowCardRect = new RectangleF(F(Pad), F(top - scroll), F(Inner), F(height));

            // Верх: ‹ точки › по середине, ✕ справа (на месте стрелки «свернуть»).
            const float btn = 26;
            float dot = 7, dotGap = 8, room = Inner - 2 * 44 - 2 * (btn + 6);
            if (n * dot + (n - 1) * dotGap > room) dotGap = Math.Max(3, (room - n * dot) / Math.Max(1, n - 1));
            if (n * dot + (n - 1) * dotGap > room) { dot = 5; dotGap = Math.Max(2, (room - n * dot) / Math.Max(1, n - 1)); }
            float dotsW = n * dot + (n - 1) * dotGap, groupW = btn + 6 + dotsW + 6 + btn;
            float gx = Pad + (Inner - groupW) / 2, ty = y + 5;
            bool canBack = at > 0, canNext = at < n - 1;
            bool backOver = Zone(g, gx, ty, btn, btn, canBack ? (Action)delegate { Turn(-1); } : null, canBack ? L.T("card.prevTip") : null);
            if (backOver && canBack) g.FillEllipse(HoverBrush, F(gx), F(ty), F(btn), F(btn));
            Glyph(g, "\uE76B", 9f, !canBack ? Color.FromArgb(0x48, Palette.Dim) : backOver ? Palette.Text : Palette.Dim, gx, ty, btn, btn);
            float dx = gx + btn + 6;
            for (int i = 0; i < n; i++)
            {
                int index = i;
                var r = group[i];
                var f = r.Key.Flows[r.Value];
                string name = r.Key.Name + " · " + (f.About.Length > 0 ? ShortName(f.About) : "Workflow " + (r.Value + 1));
                bool over = Zone(g, dx - dotGap / 2, ty, dot + dotGap, btn, i == at ? null : (Action)delegate { TurnTo(index); }, name);
                // точка — знаком единой схемы по состоянию Workflow; на каком ты — крупнее, в светлом кольце
                float d = i == at ? dot + 3 : dot, ox = dx - (d - dot) / 2, oy = ty + (btn - d) / 2;
                if (i == at)
                    using (var pen = new Pen(Color.FromArgb(0xC0, Palette.Text), F(1.2f))) g.DrawEllipse(pen, F(ox - 2.5f), F(oy - 2.5f), F(d + 5), F(d + 5));
                DotMark(g, ox, oy, d, f.State, over);
                dx += dot + dotGap;
            }
            float nx = gx + groupW - btn;
            bool nextOver = Zone(g, nx, ty, btn, btn, canNext ? (Action)delegate { Turn(1); } : null, canNext ? L.T("card.nextTip") : null);
            if (nextOver && canNext) g.FillEllipse(HoverBrush, F(nx), F(ty), F(btn), F(btn));
            Glyph(g, "\uE76C", 9f, !canNext ? Color.FromArgb(0x48, Palette.Dim) : nextOver ? Palette.Text : Palette.Dim, nx, ty, btn, btn);
            float cx = Pad + Inner - 14 - 12 - 7;
            bool closeOver = Zone(g, cx, ty, btn, btn, delegate { CloseFlowCard(); }, L.T("card.closeTip"));
            if (closeOver) g.FillEllipse(HoverBrush, F(cx), F(ty), F(btn), F(btn));
            Glyph(g, "\uE711", 8.5f, closeOver ? Palette.Text : Palette.Dim, cx, ty, btn, btn);

            // Страница; при листании прошлая уезжает, новая въезжает (≈0,24 с, к концу медленнее).
            float bodyY = y + 36;
            // Обрезка и перенос — сохраняются руками, не g.Save(): TextRenderer (PreserveGraphicsClipping) сводит обрезку
            // по стеку Save со сдвигами, и в прокрученной панели обрезка текста выходила пустой — в карточке рисовались
            // только полоски и кружки, ни одной надписи.
            var savedClip = g.Clip;
            var savedTransform = g.Transform;
            g.SetClip(new RectangleF(F(Pad + 1), F(bodyY), F(Inner - 2), F(bodyH)));
            double t = FlowSliding ? (DateTime.Now - flowSlideAt).TotalSeconds / FlowSlideSeconds : 1;
            float e = 1 - (float)Math.Pow(1 - Math.Min(1, t), 3);
            if (t < 1)
            {
                var prev = group.FirstOrDefault(r => FlowKey(r.Key, r.Value) == flowSlidePrev);
                if (prev.Key != null)
                {
                    g.TranslateTransform(F(-flowSlideDir * e * Inner), 0);
                    ghost = true;
                    FlowPage(g, x, bodyY, w, prev.Key, prev.Value);
                    ghost = false;
                    g.Transform = savedTransform;
                }
                g.TranslateTransform(F(flowSlideDir * (1 - e) * Inner), 0);
            }
            else flowSlidePrev = null;
            var cur = group[Math.Max(0, at)];
            FlowPage(g, x, bodyY, w, cur.Key, cur.Value);
            g.Transform = savedTransform;
            g.Clip = savedClip;
            savedTransform.Dispose();
            savedClip.Dispose();
            return height;
        }

        static readonly Brush HoverBrush = new SolidBrush(Color.FromArgb(0x14, 255, 255, 255));

        // Точка листания: готово — залитая зелёная, идёт — колечко с пульсом, впереди — серая (под курсором — ярче).
        void DotMark(Graphics g, float x, float y, float d, string state, bool over)
        {
            var r = new RectangleF(F(x), F(y), F(d), F(d));
            if (state == "run")
            {
                float p = Pulse(), pw = F(1.4f);
                var inset = new RectangleF(r.X + pw / 2, r.Y + pw / 2, r.Width - pw, r.Height - pw);
                using (var pen = new Pen(Color.FromArgb((int)(0x90 + 0x6F * p), Palette.Accent), pw)) g.DrawEllipse(pen, inset);
                return;
            }
            var c = state == "plan" && over ? Palette.Dim : MarkColor(state);
            using (var br = new SolidBrush(c)) g.FillEllipse(br, r);
        }

        // Страница карточки: шапка (номер, проект, этап и модель, шаги) и тело (FlowCard).
        float FlowPage(Graphics g, float x, float y, float w, StatusProject p, int index)
        {
            float top = y;
            y += FlowRow(g, x, y, w, p, index, true, true);
            // тело — по краям шапки: номер и «о чём», этап и «кто» — на одних вертикалях
            y += FlowCard(g, x - 4, y, w + 8, p, index);
            return y - top;
        }

        // Колесо и свайп по карточке: щелчок колеса или жест тачпада — одна страница; пока жест идёт (события чаще
        // 0,2 с), следующая не листается.
        bool FlowWheel(Point at, int delta)
        {
            if (flowCard == null || flowPages.Count == 0 || !flowCardRect.Contains(at)) return false;
            var now = DateTime.Now;
            bool burst = (now - flowWheelAt).TotalSeconds < 0.2;
            flowWheelAt = now;
            if (flowWheel == int.MaxValue) { if (burst) return true; flowWheel = 0; }
            flowWheel += delta;
            if (Math.Abs(flowWheel) >= 120)
            {
                Turn(flowWheel > 0 ? 1 : -1);
                flowWheel = int.MaxValue;
            }
            return true;
        }

        static string Started { get { return L.T("when.started"); } }

        // Когда записано «идёт N»: по строке «обновлено 06:10» (те же часы, что у «идёт»; впереди не дальше 3 ч —
        // сегодня, иначе вчера), без неё — по времени файла.
        static DateTime Written(StatusProject p, DateTime now)
        {
            var m = Regex.Match(p.Stamp ?? "", @"(\d{1,2})\s*:\s*(\d{2})");
            int hh, mm;
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out hh) || !int.TryParse(m.Groups[2].Value, out mm) || hh > 23 || mm > 59)
                return p.Updated;
            var at = now.Date.AddHours(hh).AddMinutes(mm);
            return at > now.AddHours(3) ? at.AddDays(-1) : at;
        }

        // Сколько Workflow идёт. Есть в строке «13:05 → … · идёт 40 мин» — берём его (то же число, что в status.md
        // и /digest) и прибавляем, сколько прошло с «обновлено» (Written; впереди — ничего не прибавляем). Нет — от первого времени строки до сейчас; часы
        // в status.md бывают впереди часов компьютера, поэтому начало впереди не дальше 3 ч — «только начат», а не
        // «со вчера». Минуты до часа, дальше часы с одним знаком, как в status.md.
        static string Elapsed(string time, DateTime written, DateTime now)
        {
            int minutes;
            var going = Regex.Match(time ?? "", @"идёт\s+(\d+(?:[.,]\d+)?)\s*(мин|ч)", RegexOptions.IgnoreCase);
            if (going.Success)
            {
                double n = double.Parse(going.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                double since = written > now ? 0 : (now - written).TotalMinutes;
                minutes = (int)Math.Round((going.Groups[2].Value.ToLowerInvariant() == "ч" ? n * 60 : n) + since);
            }
            else
            {
                var m = Regex.Match(time ?? "", @"(\d{1,2})\s*:\s*(\d{2})");
                if (!m.Success) return "";
                int hh = int.Parse(m.Groups[1].Value), mm = int.Parse(m.Groups[2].Value);
                if (hh > 23 || mm > 59) return "";
                var start = now.Date.AddHours(hh).AddMinutes(mm);
                if (start > now.AddMinutes(1))
                {
                    if (start <= now.AddHours(3)) return Started;
                    start = start.AddDays(-1);
                }
                minutes = (int)(now - start).TotalMinutes;
            }
            return Duration(Math.Max(1, minutes));
        }

        // «25 мин», «1,7 ч» — минуты до часа, дальше часы с одним знаком, как в status.md.
        static string Duration(int minutes)
        {
            if (minutes < 60) return L.F("dur.m", minutes);
            return L.F("dur.h", L.Dec((minutes / 60.0).ToString("0.#", CultureInfo.InvariantCulture)));
        }

        // «Токены»: плавная линия расхода за 7 дней (справа — сегодня) и число за сегодня; нажатие —
        // лист «Токены» с днями, моделями и проектами.
        float TokensCard(Graphics g, float x, float y, float w)
        {
            const float h = 36;
            var values = Enumerable.Range(0, 7).Select(i => Tokens.Total(DateTime.Today.AddDays(i - 6))).ToList();
            bool counted = Tokens.ScannedAt != null, empty = !values.Any(v => v.HasValue && v.Value > 0);
            string today = !counted ? L.T("tok.counting") : empty ? L.T("tok.noData") : Tokens.Human(values[6] ?? 0);
            string tipText = !counted ? L.T("tok.countingTip") : L.F("tok.cardTip", Tokens.ScannedAt.Value.ToString("HH:mm"));
            bool over = Zone(g, x, y, w, h, delegate { tokensPeriod = 0; ShowSheet(Sheet.Tokens); app.RefreshTokens(true); }, tipText);
            if (g == null) return h;
            TileBg(g, x, y, w, h);
            if (over) Hover(g, x + 3, y + 3, w - 6, h - 6);
            var csz = Txt(g, L.Caps("widget.tokens"), 9f, FontStyle.Bold, Palette.Dim, x + 14, y + 12.5f, 100, false);
            float chevX = x + w - 14 - 12, right = chevX - 6, capRight = x + 14 + csz.Width / k + 8;
            bool number = counted && !empty;
            // тихо (пожелание 17.10.2026 08:45: «расход токенов мне неважен»): число обычным и бледным, не жирным
            var nsz = Txt(g, today, 10.5f, FontStyle.Regular, Palette.Dim, right - 110, y + 11, 110, false, true);
            float left = right - nsz.Width / k;
            if (number)
            {
                // половинкой — только число; «сегодня» и линия недели — если влезают
                var ssz = Txt(null, L.T("tok.today"), 9.5f, FontStyle.Regular, Palette.Dim, 0, 0, 80, false);
                if (left - 5 - ssz.Width / k > capRight)
                {
                    left -= 5 + ssz.Width / k;
                    Txt(g, L.T("tok.today"), 9.5f, FontStyle.Regular, Palette.Dim, left, y + 12, ssz.Width / k + 2, false);
                }
                if (left - 14 - 56 > capRight)
                {
                    Spark(g, left - 14 - 56, y + 4, 56, 18, values);
                    Txt(g, L.T("tok.week"), 8f, FontStyle.Regular, Palette.Dim, left - 14 - 56, y + 21, 56, true);
                }
            }
            Glyph(g, "", 8.5f, Palette.Dim, chevX, y + 9, 12, 18);
            return h;
        }

        // Плавная линия с заливкой цветом PMP, шкала от нуля по максимуму недели; сегодня — точкой с ореолом.
        void Spark(Graphics g, float x, float y, float w, float h, List<long?> values)
        {
            long top = values.Where(v => v.HasValue).Select(v => v.Value).DefaultIfEmpty(0).Max();
            if (top <= 0) return;
            float step = (w - 8) / (values.Count - 1);
            var pts = new List<PointF>();
            for (int i = 0; i < values.Count; i++)
                if (values[i].HasValue) pts.Add(new PointF(F(x + 4 + i * step), F(y + h - 4 - (float)values[i].Value / top * (h - 8))));
            if (pts.Count < 2) return;
            using (var line = new GraphicsPath())
            {
                for (int i = 1; i < pts.Count; i++)
                {
                    float mid = (pts[i - 1].X + pts[i].X) / 2;
                    line.AddBezier(pts[i - 1], new PointF(mid, pts[i - 1].Y), new PointF(mid, pts[i].Y), pts[i]);
                }
                using (var area = (GraphicsPath)line.Clone())
                {
                    var last = pts[pts.Count - 1];
                    area.AddLine(last, new PointF(last.X, F(y + h)));
                    area.AddLine(new PointF(last.X, F(y + h)), new PointF(pts[0].X, F(y + h)));
                    area.CloseFigure();
                    using (var br = new LinearGradientBrush(new PointF(0, F(y) - 1), new PointF(0, F(y + h) + 1),
                        Color.FromArgb(0x59, Palette.Accent), Color.FromArgb(0, Palette.Accent))) g.FillPath(br, area);
                }
                using (var pen = new Pen(Palette.Accent, F(1.5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawPath(pen, line);
            }
            if (values[values.Count - 1].HasValue)
            {
                var end = pts[pts.Count - 1];
                using (var br = new SolidBrush(Color.FromArgb(0x47, Palette.Accent))) g.FillEllipse(br, end.X - F(4), end.Y - F(4), F(8), F(8));
                using (var br = new SolidBrush(Palette.Accent)) g.FillEllipse(br, end.X - F(2.5f), end.Y - F(2.5f), F(5), F(5));
            }
        }

        // ---------------------------------------------------------------- лист «Токены»

        // Окно «Токены»: период сверху, итог крупно и сравнение с прошлым таким же периодом,
        // столбики 7 дней, модели и проекты.
        float DrawTokens(Graphics g)
        {
            float gap = 10, y = Pad;
            Header(g, Pad, y, Inner);
            y += 24 + gap;
            y += BackRow(g, y, L.T("widget.tokens"), delegate { ShowSheet(Sheet.Panel); });
            Segments(g, Pad, y, Inner, new[] { L.T("period.today"), L.T("period.yesterday"), L.T("period.week") }, tokensPeriod, i => { tokensPeriod = i; Refit(); });
            y += 24 + gap;
            if (Tokens.ScannedAt == null)
            {
                const float h = 58;
                if (g != null)
                {
                    TileBg(g, Pad, y, Inner, h);
                    Txt(g, L.T("tok.countingLong"), 10.5f, FontStyle.Regular, Palette.Text, CX, y + 11, CW, false);
                    Txt(g, L.T("tok.firstRun"), 9f, FontStyle.Regular, Palette.Dim, CX, y + 33, CW, false);
                }
                return y + h + Pad;
            }
            var days = PeriodDays(tokensPeriod, 0);
            y += Card(g, y, TokensSummary) + gap;
            y += Card(g, y, (gg, yy) => TokensList(gg, yy, L.T("tok.models"), Sum(days, d => d.Models), 6)) + gap;
            y += Card(g, y, (gg, yy) => TokensList(gg, yy, L.T("tok.projects"), Sum(days, d => d.Projects), 6)) + gap;
            if (g != null) Txt(g, L.F("tok.footer", Tokens.ScannedAt.Value.ToString("HH:mm")), 9f, FontStyle.Regular, Palette.Dim, Pad + 4, y, Inner - 8, false);
            y += 18;
            return y + Pad;
        }

        // Дни периода: 0 — сегодня, 1 — вчера, 2 — 7 дней; back — на столько периодов назад (для сравнения).
        static List<DateTime> PeriodDays(int period, int back)
        {
            var today = DateTime.Today;
            if (period == 2) return Enumerable.Range(0, 7).Select(i => today.AddDays(i - 6 - 7 * back)).ToList();
            return new List<DateTime> { today.AddDays(-(period + back)) };
        }

        static Dictionary<string, long> Sum(List<DateTime> days, Func<TokenDay, Dictionary<string, long>> pick)
        {
            var map = new Dictionary<string, long>();
            foreach (var d in days)
            {
                var t = Tokens.Day(d);
                if (t == null) continue;
                foreach (var kv in pick(t)) { long old; map.TryGetValue(kv.Key, out old); map[kv.Key] = old + kv.Value; }
            }
            return map;
        }

        static long Total(List<DateTime> days) { return days.Sum(d => Tokens.Total(d) ?? 0); }

        float TokensSummary(Graphics g, float y)
        {
            float top = y;
            y += 10;
            y += Caps(g, y, L.Caps(new[] { "period.today", "period.yesterday", "period.week" }[tokensPeriod]));
            long total = Total(PeriodDays(tokensPeriod, 0)), before = Total(PeriodDays(tokensPeriod, 1));
            if (g != null)
            {
                var sz = Txt(g, Tokens.Human(total), 24f, FontStyle.Regular, Palette.Text, CX - 1, y - 4, CW, false);
                Txt(g, L.T("tok.unit"), 11f, FontStyle.Regular, Palette.Dim, CX + sz.Width / k + 6, y + 10, 100, false);
            }
            y += 38;
            // Сравнение с прошлым таким же периодом; «Сегодня» день не кончился — без процента.
            string compare = "";
            if (before > 0)
            {
                if (tokensPeriod == 0) compare = L.F("tok.yesterdayFull", Tokens.Human(before));
                else
                {
                    long change = (long)Math.Round((total - before) * 100.0 / before);
                    compare = L.F("tok.compare", change > 0 ? "▲ " + L.F("fmt.pct", change) : change < 0 ? "▼ " + L.F("fmt.pct", -change) : L.T("tok.same"),
                        tokensPeriod == 1 ? L.T("tok.dayBefore") : L.T("tok.prevWeek"), Tokens.Human(before));
                }
            }
            if (compare.Length > 0)
            {
                if (g != null) Txt(g, compare, 9.5f, FontStyle.Regular, Palette.Dim, CX, y, CW, false);
                y += 22;
            }
            // Столбики 7 дней: дни периода — цветом PMP, число — над столбиком.
            var week = PeriodDays(2, 0);
            var chosen = new HashSet<DateTime>(PeriodDays(tokensPeriod, 0));
            long max = Math.Max(1, week.Max(d => Tokens.Total(d) ?? 0));
            float barGap = 6, bw = (CW - barGap * 6) / 7, chartH = 64;
            y += 14;
            for (int i = 0; i < week.Count; i++)
            {
                var d = week[i];
                long v = Tokens.Total(d) ?? 0;
                float bx = CX + i * (bw + barGap), bh = v <= 0 ? 0 : Math.Max(2, chartH * v / (float)max), by = y + chartH - bh;
                Zone(g, bx, y - 14, bw, chartH + 44, null, L.F("tok.barTip", d.ToString(L.T("fmt.dayMonth"), L.Culture), v.ToString("N0", L.Culture)));
                if (g == null) continue;
                using (var path = Palette.Round(new RectangleF(F(bx), F(y), F(bw), F(chartH)), F(4)))
                using (var br = new SolidBrush(Color.FromArgb(0x0B, 255, 255, 255))) g.FillPath(br, path);
                if (bh > 0)
                    using (var path = Palette.Round(new RectangleF(F(bx), F(by), F(bw), F(bh)), F(Math.Min(4, bh / 2))))
                    using (var br = new SolidBrush(chosen.Contains(d) ? Palette.Accent : Color.FromArgb(0x55, 255, 255, 255))) g.FillPath(br, path);
                if (v > 0) Txt(g, Tokens.Human(v), 8f, FontStyle.Regular, Palette.Dim, bx - 6, by - 14, bw + 12, true);
                bool today = d == DateTime.Today;
                Txt(g, d.Day.ToString(), 9f, today ? FontStyle.Bold : FontStyle.Regular, today ? Palette.Text : Palette.Dim, bx - 4, y + chartH + 4, bw + 8, true);
                Txt(g, d.ToString("ddd", L.Culture), 8.5f, FontStyle.Regular, Palette.Dim, bx - 4, y + chartH + 18, bw + 8, true);
            }
            y += chartH + 34;
            return y + 6 - top;
        }

        // Список «имя · число · доля» с тонкой полосой доли; длинный — первые limit, остальные одной строкой.
        float TokensList(Graphics g, float y, string title, Dictionary<string, long> map, int limit)
        {
            float top = y;
            y += 10;
            if (g != null) Txt(g, title, 11f, FontStyle.Bold, Palette.Text, CX, y, CW, false);
            y += 24;
            long total = map.Values.Sum();
            if (total <= 0)
            {
                if (g != null) Txt(g, L.T("tok.empty"), 9.5f, FontStyle.Regular, Palette.Dim, CX, y, CW, false);
                return y + 22 - top;
            }
            var rows = map.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).ToList();
            foreach (var kv in rows.Take(limit))
            {
                double share = kv.Value / (double)total;
                string percent = share > 0 && Math.Round(share * 100) == 0 ? "<" + L.F("fmt.pct", 1) : L.F("fmt.pct", Math.Round(share * 100));
                if (g != null)
                {
                    // «другие» и «Чаты Claude» — слова программы в кеше токенов, на экране — языком интерфейса
                    string name = kv.Key == "другие" ? L.T("tok.other") : kv.Key == "Чаты Claude" ? L.T("tok.claudeChats") : kv.Key;
                    Txt(g, name, 10f, FontStyle.Regular, Palette.Text, CX, y, CW - 120, false);
                    Txt(g, Tokens.Human(kv.Value), 10f, FontStyle.Regular, Palette.Text, CX + CW - 120, y, 80, false, true);
                    Txt(g, percent, 9f, FontStyle.Regular, Palette.Dim, CX + CW - 36, y + 1, 36, false, true);
                    using (var path = Palette.Round(new RectangleF(F(CX), F(y + 19), F(CW), F(3)), F(1.5f)))
                    using (var br = new SolidBrush(Palette.Track)) g.FillPath(br, path);
                    using (var path = Palette.Round(new RectangleF(F(CX), F(y + 19), F((float)Math.Max(3, CW * share)), F(3)), F(1.5f)))
                    using (var br = new SolidBrush(Color.FromArgb(0xB0, Palette.Accent))) g.FillPath(br, path);
                }
                y += 30;
            }
            if (rows.Count > limit)
            {
                var rest = rows.Skip(limit).ToList();
                if (g != null) Txt(g, L.F("tok.more", rest.Count, Tokens.Human(rest.Sum(kv => kv.Value))), 9.5f, FontStyle.Regular, Palette.Dim, CX, y, CW, false);
                y += 20;
            }
            return y + 4 - top;
        }

        // ---------------------------------------------------------------- лист «Настройки»

        // Группы-карточки: открыта всегда одна, справа — что выбрано; чего на Windows нет (жёлтая кнопка,
        // ⌘Q) — не переносится.
        float DrawSettings(Graphics g)
        {
            float gap = 10, y = Pad;
            Header(g, Pad, y, Inner);
            y += 24 + gap;
            y += BackRow(g, y, L.T("settings.title"), delegate { ShowSheet(Sheet.Panel); });

            y += Group(g, y, false, "icon", L.T("icon.title"), TrayApp.TrayShowsTokens ? L.T("widget.tokens") : L.T("widget.cpu"), IconGroup) + gap;
            var root = StatusScan.Root();
            y += Group(g, y, false, "workflow", L.T("widget.workflow"), root == null ? L.T("flow.off") : Path.GetFileName(root.TrimEnd('\\')), WorkflowGroup) + gap;
            y += Group(g, y, false, "more", L.T("settings.more"), "", MoreGroup) + gap;

            // «О программе · Выйти» — подписи-ссылки одной строкой.
            // ширины — по подписям: у «About» и «О программе» они разные
            string about = L.T("settings.about"), quit = L.T("settings.quit");
            float wAbout = Measure(about, 9.5f, FontStyle.Regular) + 12, wQuit = Measure(quit, 9.5f, FontStyle.Regular) + 8;
            bool aboutOver = Zone(g, Pad, y, wAbout, 22, delegate { app.About(); });
            bool quitOver = Zone(g, Pad + wAbout + 6, y, wQuit, 22, delegate { app.Quit(); });
            if (g != null)
            {
                Txt(g, about, 9.5f, FontStyle.Regular, aboutOver ? Palette.Text : Palette.Dim, Pad + 4, y + 4, wAbout, false);
                Txt(g, "·", 9.5f, FontStyle.Regular, Palette.Dim, Pad + wAbout - 2, y + 4, 10, false);
                Txt(g, quit, 9.5f, FontStyle.Regular, quitOver ? Palette.Text : Palette.Dim, Pad + wAbout + 10, y + 4, wQuit, false);
            }
            y += 22;
            return y + Pad;
        }

        // «Значок у часов»: число на значке — нагрузка процессора или токены за сегодня.
        float IconGroup(Graphics g, float y)
        {
            float top = y;
            y += 6;
            Segments(g, CX, y, CW, new[] { L.T("widget.cpu"), L.T("widget.tokens") }, TrayApp.TrayShowsTokens ? 1 : 0,
                i => app.SetTrayShows(i == 1));
            y += 24 + 6;
            y += TxtWrap(g, L.T("icon.note"), 9f, Palette.Dim, CX, y, CW) + 10;
            return y - top;
        }

        // «Workflow»: папка проектов (по умолчанию не выбрана — блока нет), выбрать другую, выключить.
        float WorkflowGroup(Graphics g, float y)
        {
            float top = y;
            var root = StatusScan.Root();
            y += 4;
            y += Label(g, y, L.T("flow.folder"), null);
            if (g != null) Txt(g, root ?? L.T("flow.off"), 9.5f, FontStyle.Regular, root == null ? Palette.Dim : Palette.Text, CX, y, CW, false);
            y += 22;
            y += ActionRow(g, y, root == null ? L.T("flow.choose") : L.T("flow.change"), PickProjectsFolder, true);
            if (root != null) y += ActionRow(g, y, L.T("flow.disable"), delegate { app.SetWorkflowRoot(null); }, false);
            y += 4;
            y += TxtWrap(g, L.T("flow.note"), 9f, Palette.Dim, CX, y, CW) + 10;
            return y - top;
        }

        // Выбор папки проектов: панель не прячется, пока открыт диалог.
        void PickProjectsFolder()
        {
            menuOpen = true;
            try
            {
                using (var dialog = new FolderBrowserDialog { Description = L.T("flow.pick"), ShowNewFolderButton = false })
                {
                    var root = StatusScan.Root();
                    if (root != null) dialog.SelectedPath = root;
                    if (dialog.ShowDialog(this) == DialogResult.OK) app.SetWorkflowRoot(dialog.SelectedPath);
                }
            }
            finally
            {
                menuOpen = false;
                if (Visible) Activate();
            }
        }
        // «Дополнительно»: автозапуск и язык.
        float MoreGroup(Graphics g, float y)
        {
            float top = y;
            y += 4;
            // Автозапуск — сюда из убранной группы «Окна и Windows» (пожелание 17.10.2026: «не терять»); блоки панели
            // прячутся и возвращаются правой кнопкой на самой панели.
            bool auto = TrayApp.AutostartOn();
            Switch(g, Pad, y, Inner, L.T("more.autostart"), auto, delegate { TrayApp.SetAutostart(!auto); Invalidate(); });
            y += 34;
            // язык интерфейса (то же, что в меню правой кнопки); названия языков — каждое на своём языке
            y += Label(g, y, L.T("lang.title"), null);
            Segments(g, CX, y, CW, new[] { "Русский", "English" }, L.En ? 1 : 0, i => app.SetLanguage(i == 1));
            y += 24 + 8;
            return y - top;
        }

        // ---------------------------------------------------------------- элементы листов

        // «‹ Заголовок» — назад (Esc).
        float BackRow(Graphics g, float y, string title, Action back)
        {
            if (Zone(g, Pad - 4, y, Inner + 8, 28, back, L.T("back.tip"))) Hover(g, Pad - 4, y, Inner + 8, 28);
            if (g != null)
            {
                Txt(g, "‹", 13f, FontStyle.Bold, Palette.Dim, Pad + 2, y + 3, 14, false);
                Txt(g, title, 11.5f, FontStyle.Bold, Palette.Text, Pad + 16, y + 5, Inner - 20, false);
            }
            return 28 + 6;
        }

        // Группа-аккордеон: заголовок 36 (имя · сводка · шеврон), под ним — содержимое, когда открыта; открыта одна.
        // Без содержимого — просто строка со сводкой.
        float Group(Graphics g, float y, bool win, string key, string title, string summary, Func<Graphics, float, float> content)
        {
            bool open = content != null && settingsGroup == key;
            float body = open ? content(null, y + 36) : 0;
            float h = open ? 36 + body + 8 : 36;
            if (g != null) TileBg(g, Pad, y, Inner, h);
            bool over = content != null && Zone(g, Pad, y, Inner, 36, delegate
            {
                settingsGroup = open ? null : key;
                Refit();
            });
            if (g == null) return h;
            if (over) Hover(g, Pad + 3, y + 3, Inner - 6, 30);
            var sz = CapsLabel(g, title, CX, y + 12.5f, Inner * 0.6f);
            float right = Pad + Inner - 14 - (content != null ? 18 : 0), left = CX + sz.Width / k + 12;
            if (!string.IsNullOrEmpty(summary)) Txt(g, summary, 9.5f, FontStyle.Regular, Palette.Dim, left, y + 11, right - left, false, true);
            if (content != null) Glyph(g, open ? "" : "", 9f, over ? Palette.Text : Palette.Dim, Pad + Inner - 14 - 12, y + 9, 12, 18);
            if (open)
            {
                using (var pen = new Pen(Palette.TileLine, 1)) g.DrawLine(pen, F(CX), F(y + 35), F(CX + CW), F(y + 35));
                content(g, y + 36);
            }
            return h;
        }

        // Строка-выключатель 34: подпись слева, переключатель справа; нажимается вся строка.
        void Switch(Graphics g, float x, float y, float w, string title, bool on, Action toggle, string tipText = null)
        {
            bool over = Zone(g, x + 4, y + 2, w - 8, 30, toggle, tipText);
            if (g == null) return;
            if (over) Hover(g, x + 4, y + 2, w - 8, 30);
            Txt(g, title, 10.5f, FontStyle.Regular, Palette.Text, x + 14, y + 9, w - 28 - 44, false);
            SwitchKnob(g, x + w - 14 - 32, y + 8, on, true);
        }

        void SwitchKnob(Graphics g, float sx, float sy, bool on, bool enabled)
        {
            float sw = 32, sh = 18;
            int alpha = enabled ? 255 : 0x60;
            using (var path = Palette.Round(new RectangleF(F(sx), F(sy), F(sw), F(sh)), F(sh / 2)))
            using (var br = new SolidBrush(Color.FromArgb(alpha, on ? Palette.Accent : Palette.Track))) g.FillPath(br, path);
            float knob = sh - 4, kx = on ? sx + sw - 2 - knob : sx + 2;
            using (var br = new SolidBrush(Color.FromArgb(alpha, on ? Color.White : Palette.Dim))) g.FillEllipse(br, F(kx), F(sy + 2), F(knob), F(knob));
        }

        // Строка-кнопка 28: наведение — фон светлее, шеврон — уводит на лист.
        float ActionRow(Graphics g, float y, string title, Action act, bool chevron, Color? color = null, string tipText = null)
        {
            bool over = Zone(g, CX - 6, y, CW + 12, 28, act, tipText);
            if (g != null)
            {
                if (over) Hover(g, CX - 6, y, CW + 12, 28);
                Txt(g, title, 10.5f, FontStyle.Regular, color ?? Palette.Text, CX, y + 6, CW - 20, false);
                if (chevron) Glyph(g, "", 8.5f, Palette.Dim, CX + CW - 12, y + 5, 12, 18);
            }
            return 28;
        }

        float Label(Graphics g, float y, string title, string hint)
        {
            if (g != null)
            {
                var sz = Txt(g, title, 10.5f, FontStyle.Regular, Palette.Text, CX, y + 4, CW, false);
                if (hint != null) Txt(g, hint, 9f, FontStyle.Regular, Palette.Dim, CX + sz.Width / k + 6, y + 6, CW - sz.Width / k - 6, false);
            }
            return 26;
        }

        // Сегменты 24: выбранный — заливка цветом PMP; наведение — светлее; weights — доли ширины.
        void Segments(Graphics g, float x, float y, float w, string[] items, int selected, Action<int> pick, Action<int> hover = null, float[] weights = null)
        {
            float gapS = 4, total = weights == null ? items.Length : weights.Sum(), unit = (w - gapS * (items.Length - 1)) / total, sx = x;
            for (int i = 0; i < items.Length; i++)
            {
                int index = i;
                float sw = unit * (weights == null ? 1 : weights[i]);
                bool chosen = i == selected;
                bool over = Zone(g, sx, y, sw, 24, chosen ? (Action)null : delegate { pick(index); }, null,
                    hover == null ? null : (Action)delegate { hover(index); });
                if (g != null)
                {
                    using (var path = Palette.Round(new RectangleF(F(sx), F(y), F(sw), F(24)), F(7)))
                    using (var br = new SolidBrush(chosen ? Palette.Accent : Color.FromArgb(over ? 0x24 : 0x14, 255, 255, 255))) g.FillPath(br, path);
                    Txt(g, items[i], 9.5f, chosen ? FontStyle.Bold : FontStyle.Regular, chosen ? Palette.Bg : Palette.Text, sx, y + 4, sw, true);
                }
                sx += sw + gapS;
            }
        }

        // Плитка с наведением (увеличение от центра) и рамкой выбранного через зазор 2 pt.
        void TileFace(Graphics g, float x, float y, float w, float h, float radius, float scale, bool selected, float ring,
            Action<Graphics, RectangleF> face, bool border = false)
        {
            float cx = x + w / 2, cy = y + h / 2, sw = w * scale, sh = h * scale;
            var r = new RectangleF(F(cx - sw / 2), F(cy - sh / 2), F(sw), F(sh));
            face(g, r);
            if (border)
                using (var path = Palette.Round(r, F(radius)))
                using (var pen = new Pen(Color.FromArgb(0x2E, 255, 255, 255), 1)) g.DrawPath(pen, path);
            if (selected)
            {
                var o = RectangleF.Inflate(r, F(2.5f), F(2.5f));
                using (var path = Palette.Round(o, F(radius + 2.5f)))
                using (var pen = new Pen(Palette.Text, F(ring))) g.DrawPath(pen, path);
            }
        }

        // Значок из Segoe MDL2 Assets по центру прямоугольника.
        void Glyph(Graphics g, string glyph, float pt, Color color, float x, float y, float w, float h)
        {
            using (var font = new System.Drawing.Font("Segoe MDL2 Assets", F(pt), FontStyle.Regular, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, glyph, font, new Rectangle(S(x), S(y), S(w), S(h)), color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | Keep);
        }

        // Текст с переносом по словам; возвращает высоту в логических точках.
        float TxtWrap(Graphics g, string text, float pt, Color color, float x, float y, float w)
        {
            using (var font = new System.Drawing.Font("Segoe UI", pt * k, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                var flags = TextFormatFlags.NoPadding | Keep | TextFormatFlags.WordBreak;
                var size = TextRenderer.MeasureText(text, font, new Size(S(w), int.MaxValue), flags);
                if (g != null) TextRenderer.DrawText(g, text, font, new Rectangle(S(x), S(y), S(w), size.Height + 2), color, flags);
                return size.Height / k;
            }
        }

        static double[] Ticks(double lo, double hi, double step)
        {
            var list = new List<double>();
            for (double t = lo; t <= hi + 0.01; t += step) list.Add(t);
            return list.ToArray();
        }

        static List<KeyValuePair<DateTime, double>> Series(List<Sample> hist, Sample now, Func<Sample, double?> pick)
        {
            var list = new List<KeyValuePair<DateTime, double>>();
            foreach (var s in hist) { var v = pick(s); if (v.HasValue) list.Add(new KeyValuePair<DateTime, double>(s.At, v.Value)); }
            var n = pick(now);
            if (n.HasValue && (list.Count == 0 || list[list.Count - 1].Key < now.At))
                list.Add(new KeyValuePair<DateTime, double>(now.At, n.Value));
            return list;
        }

        void TileBg(Graphics g, float x, float y, float w, float h)
        {
            using (var path = Palette.Round(new RectangleF(F(x), F(y), F(w), F(h)), F(12)))
            {
                using (var br = new SolidBrush(Palette.Tile)) g.FillPath(br, path);
                using (var pen = new Pen(Palette.TileLine, 1)) g.DrawPath(pen, path);
            }
        }

        void LoadTile(Graphics g, float x, float y, float w, float h, string title, double? value, List<KeyValuePair<DateTime, double>> pts)
        {
            TileBg(g, x, y, w, h);
            if (!FoldHead(g, x, y, w, Settings.LoadOpen, title, value.HasValue ? L.F("fmt.pct", Math.Round(value.Value)) : "—",
                value.HasValue ? Palette.ForLoad(value.Value) : (Color?)null, true, null, null, L.T("load.tip"))) return;
            Chart(g, x + 12, y + 36, w - 24, 44, pts, 0, 100, v => Palette.ForLoad(v), null, null);
        }

        void Chart(Graphics g, float x, float y, float w, float h, List<KeyValuePair<DateTime, double>> pts, double lo, double hi,
            Func<double, Color> color, double[] ticks, Func<double, string> tickText)
        {
            float axis = ticks == null ? 0 : 30;
            float cx = x + axis, cw = w - axis;
            using (var pen = new Pen(Palette.TileLine, 1))
            {
                if (ticks != null)
                    foreach (var t in ticks)
                    {
                        float ty = y + h - (float)((t - lo) / (hi - lo)) * h;
                        g.DrawLine(pen, F(cx), F(ty), F(cx + cw), F(ty));
                        Txt(g, tickText(t), 8f, FontStyle.Regular, Palette.Dim, x - 2, ty - 7, axis - 4, false, true);
                    }
                else g.DrawLine(pen, F(cx), F(y + h), F(cx + cw), F(y + h));
            }
            if (pts.Count == 0) return;
            var t1 = pts[pts.Count - 1].Key;
            var t0 = t1.AddHours(-Sensors.HistoryHours);
            var first = pts[0].Key;
            // Короткая история растягивается на всю ширину («за 19 мин»).
            if ((t1 - first).TotalMinutes < 180) t0 = first;
            double span = Math.Max(1, (t1 - t0).TotalSeconds);
            var points = new List<PointF>();
            foreach (var p in pts)
            {
                float px = cx + (float)((p.Key - t0).TotalSeconds / span) * cw;
                double v = Math.Max(lo, Math.Min(hi, p.Value));
                float py = y + h - (float)((v - lo) / (hi - lo)) * h;
                points.Add(new PointF(F(px), F(py)));
            }
            if (points.Count == 1) points.Insert(0, new PointF(F(cx), points[0].Y));
            var last = pts[pts.Count - 1].Value;
            var c = color(last);
            using (var fill = new GraphicsPath())
            {
                fill.AddLines(points.ToArray());
                fill.AddLine(points[points.Count - 1], new PointF(points[points.Count - 1].X, F(y + h)));
                fill.AddLine(new PointF(points[points.Count - 1].X, F(y + h)), new PointF(points[0].X, F(y + h)));
                fill.CloseFigure();
                using (var br = new SolidBrush(Color.FromArgb(60, c))) g.FillPath(br, fill);
            }
            using (var pen = new Pen(c, F(2)) { LineJoin = LineJoin.Round }) g.DrawLines(pen, points.ToArray());
            var end = points[points.Count - 1];
            using (var br = new SolidBrush(c)) g.FillEllipse(br, end.X - F(3), end.Y - F(3), F(6), F(6));
        }

        SizeF Txt(Graphics g, string text, float pt, FontStyle style, Color color, float x, float y, float w, bool center, bool right = false)
        {
            using (var font = new Font("Segoe UI", pt * k, style, GraphicsUnit.Pixel))
            {
                var flags = TextFormatFlags.NoPadding | Keep | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
                if (center) flags |= TextFormatFlags.HorizontalCenter;
                if (right) flags |= TextFormatFlags.Right;
                var rect = new Rectangle(S(x), S(y), S(w), (int)Math.Ceiling(font.GetHeight() * 1.3f));
                if (g == null) return TextRenderer.MeasureText(text, font, rect.Size, flags);
                TextRenderer.DrawText(g, text, font, rect, color, flags);
                return TextRenderer.MeasureText(g, text, font, rect.Size, flags);
            }
        }

    }

    // ---------------------------------------------------------------- проекты: status.md, Workflow, /digest

    class StatusFlow
    {
        // run / wait / fail / done / plan
        public string State = "plan", About = "", Time = "", Phase = "", Role;
        public int? Done, Total;
        // Роли целиком: «кодинг · 2 агента · Opus 5.5 high 💭»; прошлые шаги — Past.
        public List<FlowRole> Roles = new List<FlowRole>();
    }

    class FlowRole { public string Name = "", Model = ""; public int Agents; public bool Me, Running, Past, Red; }

    class StatusProject
    {
        // Stamp — строка «обновлено 06:10» под заголовком (часы того, кто писал; бывают впереди часов компьютера).
        public string Folder, Name, Marker = "", Now = "", Wait = "", Stamp = "";
        public DateTime Updated, Changed;
        public List<StatusFlow> Flows = new List<StatusFlow>();
    }

    // Сводки проектов (формат — README): раз в 60 с и при открытии панели обходим
    // <папка проектов>\*\{docs,audit,work}\status.md (до 128 КБ), кроме рабочих копий; из них — блок панели «Workflow».
    static class StatusScan
    {
        const int Limit = 6 * 1024, TotalLimit = 128 * 1024, FileLimit = 128 * 1024, AboutLimit = 60, TailLimit = 200;
        static readonly string[] Folders = { "docs", "audit", "work" };
        static readonly Regex WorkingCopy = new Regex("-(wf[0-9]+|rev[0-9]*|inst[0-9]+)$", RegexOptions.IgnoreCase);
        static readonly Regex Clock = new Regex(@"\d\s*:\s*\d");
        static readonly object gate = new object();
        static List<StatusProject> last;
        static DateTime lastAt = DateTime.MinValue;
        static bool scanning;

        // Последний обход; ещё не обходили — null.
        public static List<StatusProject> Current { get { lock (gate) return last; } }

        // Корень проектов — папка, выбранная в «Настройки → Workflow» (settings.json, workflowRoot); не выбрана или
        // пропала — null: блока Workflow нет, status.md никто не читает.
        public static string Root()
        {
            var raw = Settings.GetString(Settings.WorkflowRoot, null);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                var full = Path.GetFullPath(raw.Trim());
                return Directory.Exists(full) ? full : null;
            }
            catch { return null; }
        }

        // Рабочая копия — не проект: хвост -wf<N>/-rev/-inst<N> или git worktree (.git — файл).
        public static bool IsWorkingCopy(string dir)
        {
            return WorkingCopy.IsMatch(Path.GetFileName(dir)) || File.Exists(Path.Combine(dir, ".git"));
        }

        public static List<StatusProject> Scan()
        {
            var list = new List<StatusProject>();
            var root = Root();
            if (root == null) return list;
            string[] dirs;
            try { dirs = Directory.GetDirectories(root); } catch { return list; }
            foreach (var dir in dirs.OrderBy(d => d, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith(".") || IsWorkingCopy(dir)) continue;
                var file = Folders.Select(f => Path.Combine(dir, f, "status.md")).FirstOrDefault(File.Exists);
                if (file == null) continue;
                string text;
                try
                {
                    using (var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        var buf = new byte[Math.Min(s.Length, FileLimit)];
                        int n = 0, r;
                        while (n < buf.Length && (r = s.Read(buf, n, buf.Length - n)) > 0) n += r;
                        text = Encoding.UTF8.GetString(buf, 0, n);
                    }
                }
                catch { continue; }
                if (text.Trim().Length == 0) continue;
                var p = Parse(text);
                p.Folder = name;
                if (p.Name.Length == 0) p.Name = name;
                p.Updated = File.GetLastWriteTime(file);
                p.Changed = p.Updated;
                // Когда проект менялся: свежее из сводки и run-файлов рядом.
                try
                {
                    foreach (var run in Directory.GetFiles(Path.GetDirectoryName(file), "run-*.json"))
                    {
                        var t = File.GetLastWriteTime(run);
                        if (t > p.Changed) p.Changed = t;
                    }
                }
                catch { }
                list.Add(p);
            }
            return list.OrderByDescending(p => p.Updated).ThenBy(p => p.Folder, StringComparer.Ordinal).ToList();
        }

        // Начало блока: «3️⃣ Workflow …» — цифра-клавиша (или 🔟) с начала строки.
        static bool IsHeading(string line)
        {
            var t = line.Trim();
            if (t.StartsWith("\U0001F51F")) return true;
            return t.Length > 1 && t[0] >= '0' && t[0] <= '9' && (t[1] == '️' || t[1] == '⃣');
        }

        // Значок сильнее слова; ни значка, ни слова — впереди.
        static readonly string[][] Marks =
        {
            new[] { "fail", "🛑", "❌" }, new[] { "wait", "✋", "⚠", "❓", "🙋", "⏸" }, new[] { "plan", "⬜" },
            new[] { "done", "✅", "🟢" }, new[] { "run", "💭", "⏳" },
        };
        static readonly string[][] StateWords =
        {
            new[] { "fail", "упал", "стоп", "сломал", "failed", "stopped", "broken" },
            new[] { "plan", "заплан", "впереди", "в очереди", "planned", "queued", "ahead" },
            new[] { "wait", "ждёт", "жду", "пауз", "waiting", "paused" }, new[] { "done", "готов", "выложен", "done", "shipped" },
            new[] { "run", "идёт", "кодинг", "критик", "проверк", "исследов", "планирован", "ревизи", "running", "coding", "review", "testing" },
        };

        static string BlockState(string heading)
        {
            foreach (var rule in Marks) if (rule.Skip(1).Any(m => heading.IndexOf(m, StringComparison.Ordinal) >= 0)) return rule[0];
            var text = heading.ToLowerInvariant();
            foreach (var rule in StateWords) if (rule.Skip(1).Any(w => text.Contains(w))) return rule[0];
            return "plan";
        }

        // Хвостовые строки сводки: «Сейчас: …» — что идёт, «Ждёт …: …» — чего ждут от человека.
        static readonly string[] NowHeads = { "Сейчас:", "Now:" }, WaitHeads = { "Ждёт пользователя:", "Waiting:" };

        // «Сейчас: …» / «Ждёт …: …»: now — первое; null — не такая строка.
        static string Tail(string line, out bool now)
        {
            now = false;
            var body = line;
            if (body.StartsWith("- ") || body.StartsWith("* ")) body = body.Substring(2).TrimStart(' ');
            if (body.StartsWith("**")) body = body.Substring(2);
            string rest = null;
            foreach (var head in NowHeads.Concat(WaitHeads))
                if (body.StartsWith(head, StringComparison.Ordinal)) { rest = body.Substring(head.Length); now = NowHeads.Contains(head); break; }
            return rest == null ? null : rest.Replace("**", "").Trim();
        }

        // Роль «роль · агенты · модель усилие [💭]».
        class Role
        {
            public string Name = "", Agents = "", Line, Model = "";
            public bool Running, Red;
            public Role(string item)
            {
                Line = item;
                var text = item.Trim();
                Running = text.EndsWith("💭", StringComparison.Ordinal);
                if (Running) text = text.Substring(0, text.Length - "💭".Length).Trim();
                var parts = text.Split('·', '•').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                if (parts.Count == 0) return;
                Name = parts[0];
                var agents = parts.Skip(1).FirstOrDefault(s => s.ToLowerInvariant().Contains("аген") || s.ToLowerInvariant().Contains("agent") || s == "я" || s.StartsWith("я "));
                Agents = agents ?? "";
                // Модель с эффортом — первая часть после имени, что не «агенты»; 🔴 и ** — пометки Fable и max.
                var model = parts.Skip(1).FirstOrDefault(s => s != agents) ?? "";
                Red = model.Contains("🔴");
                Model = Regex.Replace(model.Replace("🔴", "").Replace("**", ""), @"\s+", " ").Trim();
            }

            public FlowRole ToFlow(bool past)
            {
                var m = Regex.Match(Agents, @"\d+");
                return new FlowRole
                {
                    Name = Name, Model = Model, Running = Running, Past = past, Red = Red,
                    Me = Agents == "я" || Agents.StartsWith("я "), Agents = m.Success ? int.Parse(m.Value) : 0,
                };
            }
        }

        class Block
        {
            public readonly StatusFlow Flow = new StatusFlow();
            public readonly List<string> Past = new List<string>();
            public readonly List<Role> PastRoles = new List<Role>();
            public readonly List<Role> Roles = new List<Role>();

            public void Take(string item)
            {
                var lower = item.ToLowerInvariant();
                if (Flow.Total == null)
                {
                    var parts = lower.Split(' ');
                    int done, total;
                    if (parts.Length >= 4 && (parts[0] == "шаги" || parts[0] == "steps") && (parts[2] == "из" || parts[2] == "of") && int.TryParse(parts[1], out done)
                        && int.TryParse(new string(parts[3].TakeWhile(char.IsDigit).ToArray()), out total)) { Flow.Done = done; Flow.Total = total; }
                }
                if ((lower.StartsWith("прошл") && lower.Contains("шаг")) || (lower.StartsWith("past") && lower.Contains("step")))
                {
                    int colon = item.IndexOf(':');
                    var body = colon < 0 ? "" : item.Substring(colon + 1);
                    Past.Clear();
                    PastRoles.Clear();
                    foreach (var chunk in body.Split(';'))
                    {
                        var head = chunk.Split('—', '·', ',', ':')[0].Trim();
                        var word = head.Split(' ').FirstOrDefault(s => s.Length > 0);
                        if (word == null) continue;
                        Past.Add(word);
                        // «планирование · 1 агент · Opus 5.5 high — итог»: роль целиком, имя — первое слово
                        var was = new Role(chunk.Split('—')[0]);
                        was.Name = word;
                        PastRoles.Add(was);
                    }
                    return;
                }
                bool clock = Clock.IsMatch(item);
                if (Flow.About.Length == 0 && !lower.StartsWith("шаги") && !lower.StartsWith("steps") && !clock)
                {
                    Flow.About = Regex.Replace(item, @"^(о\s+чём|about)\s*:\s*", "", RegexOptions.IgnoreCase);
                    return;
                }
                if (Flow.Time.Length == 0 && clock) { Flow.Time = item; return; }
                var role = item.Split('·', '•')[0].Trim();
                if (item.Contains("·") && !item.Contains("→") && role.Length > 0 && !role.Any(char.IsDigit)) Roles.Add(new Role(item));
            }

            // Текущая фаза: первая роль с 💭, иначе последняя; только у идущих, ждущих и упавших.
            public StatusFlow Finish()
            {
                Flow.Roles = PastRoles.Where(r => !Roles.Any(x => x.Name.ToLowerInvariant() == r.Name.ToLowerInvariant()))
                    .Select(r => r.ToFlow(true)).Concat(Roles.Select(r => r.ToFlow(false))).ToList();
                var names = new List<string>();
                foreach (var name in Past.Concat(Roles.Select(r => r.Name)))
                    if (name.Length > 0 && !names.Any(n => n.ToLowerInvariant() == name.ToLowerInvariant())) names.Add(name);
                var current = Roles.FirstOrDefault(r => r.Running) ?? Roles.LastOrDefault();
                int index = current == null ? -1 : names.FindIndex(n => n.ToLowerInvariant() == current.Name.ToLowerInvariant());
                if ((Flow.State == "run" || Flow.State == "wait" || Flow.State == "fail") && index >= 0 && index < 5)
                {
                    var name = names[index];
                    Flow.Phase = name.ToLowerInvariant() == "планирование" ? "план" : (name.Length > 9 ? name.Substring(0, 8) + "…" : name);
                    var role = string.Join(" · ", new[] { current.Name, current.Agents }.Where(s => s.Length > 0));
                    Flow.Role = role.Length == 0 ? null : role;
                }
                return Flow;
            }
        }

        // Разбор status.md: заголовок «# 🔰Проект», блоки «N️⃣ Workflow …», хвосты «Сейчас:»
        // и «Ждёт …:» (берётся последняя).
        public static StatusProject Parse(string text)
        {
            var p = new StatusProject { Name = "" };
            bool sawHeading = false;
            Block current = null;
            var blocks = new List<Block>();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (!sawHeading && line.StartsWith("# "))
                {
                    sawHeading = true;
                    var head = line.Substring(2).Trim();
                    int i = 0;
                    while (i < head.Length && !char.IsLetterOrDigit(head[i])) i++;
                    p.Marker = head.Substring(0, i).Trim();
                    p.Name = head.Substring(i).Trim();
                    continue;
                }
                if (current == null && p.Stamp.Length == 0 && (line.StartsWith("обновлено", StringComparison.OrdinalIgnoreCase) || line.StartsWith("updated", StringComparison.OrdinalIgnoreCase)))
                {
                    p.Stamp = line;
                    continue;
                }
                if (IsHeading(line))
                {
                    if (current != null) blocks.Add(current);
                    current = new Block();
                    current.Flow.State = BlockState(line);
                    continue;
                }
                bool now;
                var tail = Tail(line, out now);
                if (tail != null)
                {
                    if (tail.Length > 0) { if (now) p.Now = tail; else p.Wait = tail; }
                    continue;
                }
                if (current == null || !(line.StartsWith("- ") || line.StartsWith("* "))) continue;
                var item = line.Substring(2).Trim();
                if (item.Length > 0) current.Take(item);
            }
            if (current != null) blocks.Add(current);
            p.Flows = blocks.Select(b => b.Finish()).ToList();
            return p;
        }

        // «Ждёт …: ничего» — не ждёт.
        public static bool Nothing(string value)
        {
            var clean = (value ?? "").Trim().TrimEnd('.').ToLowerInvariant();
            return clean == "" || clean == "ничего" || clean == "нет" || clean == "—" || clean == "-";
        }

        // ---- блок панели

        public class Tile
        {
            public StatusProject P;
            public int Done, Total, Running;
            public bool Failed, Waits;
            public string Wait = "", Help = "";
        }

        // Плитки «Идут» (Workflow идёт или упал, сводка свежее 12 ч) и «Ждут тебя» (свежее 7 суток), свежие — первыми.
        public static void Tiles(List<StatusProject> list, DateTime now, out List<Tile> running, out List<Tile> waiting)
        {
            running = new List<Tile>();
            waiting = new List<Tile>();
            foreach (var p in list.OrderByDescending(x => x.Updated))
            {
                double age = (now - p.Updated).TotalSeconds;
                bool waitsLine = !Nothing(p.Wait);
                var waitFlow = p.Flows.FirstOrDefault(f => f.State == "wait");
                bool waits = age <= 7 * 86400 && (waitsLine || waitFlow != null);
                var live = age <= 12 * 3600 ? p.Flows.Where(f => f.State == "run" || f.State == "fail").ToList() : new List<StatusFlow>();
                if (live.Count == 0 && !waits) continue;
                string what = waitsLine ? p.Wait : (waitFlow != null ? "Workflow «" + waitFlow.About + "» ждёт тебя" : "");
                var t = new Tile
                {
                    P = p, Done = p.Flows.Count(f => f.State == "done"), Total = p.Flows.Count,
                    Running = p.Flows.Count(f => f.State == "run"), Failed = p.Flows.Any(f => f.State == "fail"),
                };
                var lines = new List<string>();
                for (int i = 0; i < p.Flows.Count; i++)
                {
                    var f = p.Flows[i];
                    if (f.State != "run" && f.State != "wait" && f.State != "fail") continue;
                    var mark = f.State == "fail" ? "упал" : f.State == "wait" ? "ждёт" : (f.Total.HasValue ? f.Done + "/" + f.Total : "");
                    lines.Add(string.Join(" · ", new[] { (i + 1).ToString(), f.About, mark }.Where(s => s.Length > 0)));
                }
                if (p.Now.Length > 0) lines.Add(p.Now);
                if (live.Count > 0)
                {
                    t.Waits = waits;
                    t.Wait = waits ? what : "";
                    running.Add(t);
                }
                else
                {
                    t.Waits = true;
                    var about = p.Flows.Where(f => f.State == "wait").Select(f => f.About).FirstOrDefault() ?? "";
                    t.Wait = what.Length > 0 ? what : about;
                    waiting.Add(t);
                }
                if (t.Waits && t.Wait.Length > 0) lines.Add("Ждёт тебя: " + t.Wait);
                t.Help = string.Join("\n", lines.Where(s => s.Length > 0));
            }
        }

        // ---- обход по таймеру и команда status

        // Обход в фоне (не чаще раза в 10 с); готово — changed на потоке пула.
        public static void Tick(Action<List<StatusProject>> changed)
        {
            lock (gate)
            {
                if (scanning || (DateTime.Now - lastAt).TotalSeconds < 10) return;
                scanning = true;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<StatusProject> list = null;
                try { list = Scan(); }
                catch (Exception e) { Log.Write("сводки: " + e.Message); }
                lock (gate)
                {
                    scanning = false;
                    if (list != null) { last = list; lastAt = DateTime.Now; }
                }
                if (list != null) changed(list);
            });
        }

    }

    // ---------------------------------------------------------------- токены по дням

    class TokenDay
    {
        public long In, CacheWrite, CacheRead, Out;
        public readonly Dictionary<string, long> Models = new Dictionary<string, long>(), Projects = new Dictionary<string, long>();
        public long Total { get { return In + CacheWrite + CacheRead + Out; } }

        public TokenDay Copy()
        {
            var d = new TokenDay { In = In, CacheWrite = CacheWrite, CacheRead = CacheRead, Out = Out };
            foreach (var kv in Models) d.Models[kv.Key] = kv.Value;
            foreach (var kv in Projects) d.Projects[kv.Key] = kv.Value;
            return d;
        }
    }

    // Токены Claude Code по дням: реплики модели
    // в %USERPROFILE%\.claude\projects\**\*.jsonl, поле message.usage; одна реплика пишется несколькими строками с тем
    // же message.id и requestId — считается один раз. Токены — вход + запись кеша + чтение кеша + выход («оборот»).
    // Считаем в фоне: у каждого файла помним, докуда прочли, и читаем только дописанное; дни, кончившиеся больше
    // 10 минут назад, — в %LOCALAPPDATA%\PimpTray\tokens.json: после перезапуска их не перечитываем.
    static class Tokens
    {
        public const int Days = 14;
        static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        static readonly string CachePath = Path.Combine(Log.Dir, "tokens.json");
        static readonly object gate = new object();
        // Всё ниже — только поток счёта; панели — готовая копия view.
        static readonly Dictionary<string, TokenDay> days = new Dictionary<string, TokenDay>();
        static readonly HashSet<string> closed = new HashSet<string>();
        static readonly Dictionary<string, string> seen = new Dictionary<string, string>();
        static readonly Dictionary<string, string> projects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static Dictionary<string, long> offsets;
        static DateTime scanStarted = DateTime.MinValue;
        static Dictionary<string, TokenDay> view = new Dictionary<string, TokenDay>();
        static DateTime lastScan = DateTime.MinValue;
        static bool busy;
        // Когда досчитали последний раз; ещё не считали — null.
        public static DateTime? ScannedAt;

        public static string Key(DateTime d) { return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }

        public static TokenDay Day(DateTime d)
        {
            TokenDay t;
            return view.TryGetValue(Key(d), out t) ? t : null;
        }

        // Сумма дня; ещё не считали — null.
        public static long? Total(DateTime d)
        {
            if (ScannedAt == null) return null;
            var t = Day(d);
            return t == null ? 0 : t.Total;
        }

        // Освежить в фоне: не чаще раза в 60 с, а по открытию листа «Токены» — раз в 15 с. done — на потоке пула.
        public static void Refresh(bool soon, Action done)
        {
            lock (gate)
            {
                if (busy || (DateTime.Now - lastScan).TotalSeconds < (soon ? 15 : 60)) return;
                busy = true;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                    Scan();
                }
                catch (Exception e) { Log.Write("токены: " + e.Message); }
                finally
                {
                    Thread.CurrentThread.Priority = ThreadPriority.Normal;
                    lock (gate) { busy = false; lastScan = DateTime.Now; }
                }
                if (done != null) done();
            });
        }

        static void Scan()
        {
            var now = DateTime.Now;
            var first = now.Date.AddDays(-(Days - 1));
            var firstKey = Key(first);
            bool initial = offsets == null;
            if (initial) { Load(firstKey); offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase); }
            // Первый раз — файлы, тронутые с самого раннего незакрытого дня; дальше — тронутые после прошлого счёта.
            var from = first;
            while (from < now.Date && closed.Contains(Key(from))) from = from.AddDays(1);
            var since = initial ? from : scanStarted.AddMinutes(-2);
            scanStarted = now;
            if (Directory.Exists(Root))
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(Root, "*.jsonl", SearchOption.AllDirectories).ToList(); }
                catch (Exception e) { Log.Write("токены: " + e.Message); files = new string[0]; }
                foreach (var path in files)
                {
                    try
                    {
                        var info = new FileInfo(path);
                        if (info.LastWriteTime < since) continue;
                        long offset;
                        offsets.TryGetValue(path, out offset);
                        if (info.Length < offset) offset = 0;
                        if (info.Length == offset) continue;
                        offsets[path] = ReadFrom(path, offset, firstKey);
                    }
                    catch (Exception e) { Log.Write("токены " + Path.GetFileName(path) + ": " + e.Message); }
                }
            }
            // Закрыть дни, кончившиеся больше 10 минут назад: строк за них больше не будет.
            var closeBefore = now.AddMinutes(-10).Date;
            bool changed = false;
            for (var d = first; d < closeBefore; d = d.AddDays(1))
            {
                var key = Key(d);
                if (closed.Contains(key)) continue;
                if (!days.ContainsKey(key)) days[key] = new TokenDay();
                closed.Add(key);
                changed = true;
            }
            foreach (var old in days.Keys.Where(k => string.CompareOrdinal(k, firstKey) < 0).ToList()) { days.Remove(old); closed.Remove(old); }
            foreach (var gone in seen.Where(kv => closed.Contains(kv.Value) || string.CompareOrdinal(kv.Value, firstKey) < 0).Select(kv => kv.Key).ToList()) seen.Remove(gone);
            if (changed || initial) Save();
            view = days.ToDictionary(kv => kv.Key, kv => kv.Value.Copy());
            ScannedAt = now;
        }

        // Читает дописанное с offset до последнего перевода строки; возвращает, докуда прочёл.
        static long ReadFrom(string path, long offset, string firstKey)
        {
            using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
            {
                s.Seek(offset, SeekOrigin.Begin);
                var buf = new byte[1 << 20];
                var line = new MemoryStream();
                long pos = offset, done = offset;
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    int start = 0;
                    for (int i = 0; i < n; i++)
                    {
                        if (buf[i] != (byte)'\n') continue;
                        line.Write(buf, start, i - start);
                        if (line.Length > 0) Count(Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length), firstKey);
                        line.SetLength(0);
                        start = i + 1;
                        done = pos + i + 1;
                    }
                    line.Write(buf, start, n - start);
                    pos += n;
                }
                return done;
            }
        }

        static void Count(string line, string firstKey)
        {
            if (line.IndexOf("\"type\":\"assistant\"", StringComparison.Ordinal) < 0) return;
            int u = line.LastIndexOf("\"usage\":{", StringComparison.Ordinal), m = line.IndexOf("\"message\":{", StringComparison.Ordinal);
            if (u < 0 || m < 0 || m > u) return;
            var stamp = Value(line, "\"timestamp\":\"", -1);
            DateTime at;
            if (stamp == null || !DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at)) return;
            var day = Key(at.Kind == DateTimeKind.Utc ? at.ToLocalTime() : at);
            if (string.CompareOrdinal(day, firstKey) < 0 || closed.Contains(day)) return;
            var key = (Value(line, "\"id\":\"", m) ?? Value(line, "\"uuid\":\"", 0)) + "|" + Value(line, "\"requestId\":\"", -1);
            if (seen.ContainsKey(key)) return;
            seen[key] = day;
            long input = Number(line, "\"input_tokens\":", u), write = Number(line, "\"cache_creation_input_tokens\":", u),
                read = Number(line, "\"cache_read_input_tokens\":", u), output = Number(line, "\"output_tokens\":", u);
            long sum = input + write + read + output;
            if (sum <= 0) return;
            TokenDay t;
            if (!days.TryGetValue(day, out t)) days[day] = t = new TokenDay();
            t.In += input;
            t.CacheWrite += write;
            t.CacheRead += read;
            t.Out += output;
            Add(t.Models, ModelName(Value(line, "\"model\":\"", m)), sum);
            Add(t.Projects, ProjectOf(Value(line, "\"cwd\":\"", 0)), sum);
        }

        static void Add(Dictionary<string, long> map, string key, long value)
        {
            long old;
            map.TryGetValue(key, out old);
            map[key] = old + value;
        }

        // Строковое значение после prefix: from < 0 — последнее вхождение в строке, иначе первое не раньше from.
        // Кавычка, не экранированная в тексте, — только у настоящих полей JSON: внутри текста реплик она \".
        static string Value(string line, string prefix, int from)
        {
            int i = from < 0 ? line.LastIndexOf(prefix, StringComparison.Ordinal) : line.IndexOf(prefix, from, StringComparison.Ordinal);
            if (i < 0) return null;
            var sb = new StringBuilder();
            for (i += prefix.Length; i < line.Length && line[i] != '"'; i++)
            {
                if (line[i] == '\\' && i + 1 < line.Length) i++;
                sb.Append(line[i]);
            }
            return sb.ToString();
        }

        static long Number(string line, string prefix, int from)
        {
            int i = line.IndexOf(prefix, from, StringComparison.Ordinal);
            if (i < 0) return 0;
            long v = 0;
            for (i += prefix.Length; i < line.Length && line[i] >= '0' && line[i] <= '9'; i++) v = v * 10 + (line[i] - '0');
            return v;
        }

        // «claude-opus-4-1-20250805» → «Opus 4.1», «claude-fable-5-1» → «Fable 5.1».
        public static string ModelName(string id)
        {
            if (string.IsNullOrEmpty(id) || id.StartsWith("<")) return "другие";
            var s = Regex.Replace(Regex.Replace(id.ToLowerInvariant(), "^claude-", ""), @"-\d{8}$", "");
            var parts = s.Split('-');
            var family = parts.FirstOrDefault(x => x.Length > 0 && !x.All(char.IsDigit));
            if (family == null) return id;
            var version = string.Join(".", parts.Where(x => x.Length > 0 && x.All(char.IsDigit)));
            return char.ToUpperInvariant(family[0]) + family.Substring(1) + (version.Length > 0 ? " " + version : "");
        }

        // Проект — папка под корнем проектов (рабочая копия git — её основной репозиторий); чаты Claude без папки
        // проекта (scratch) — «Чаты Claude».
        static string ProjectOf(string cwd)
        {
            if (string.IsNullOrEmpty(cwd)) return "другие";
            string name;
            if (projects.TryGetValue(cwd, out name)) return name;
            var top = StatusScan.Root();
            var root = top == null ? null : top.TrimEnd('\\') + "\\";
            var path = cwd.TrimEnd('\\');
            if (root != null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                var folder = path.Substring(root.Length).Split('\\')[0];
                name = folder;
                try
                {
                    var git = Path.Combine(root, folder, ".git");
                    if (File.Exists(git))
                    {
                        var line = File.ReadAllText(git).Trim();
                        int at = line.IndexOf(".git", StringComparison.OrdinalIgnoreCase);
                        if (line.StartsWith("gitdir:") && at > 0) name = Path.GetFileName(line.Substring(7, at - 7).Trim().TrimEnd('/', '\\'));
                    }
                }
                catch { }
            }
            else if (path.IndexOf("\\scratch", StringComparison.OrdinalIgnoreCase) >= 0) name = "Чаты Claude";
            else name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) name = "другие";
            projects[cwd] = name;
            return name;
        }

        static void Load(string firstKey)
        {
            try
            {
                if (!File.Exists(CachePath)) return;
                var raw = Json.Parse(File.ReadAllText(CachePath));
                if (raw == null || Json.GetInt(raw, "v") != 1) return;
                var map = raw.ContainsKey("days") ? raw["days"] as IDictionary<string, object> : null;
                if (map == null) return;
                foreach (var kv in map)
                {
                    var d = kv.Value as IDictionary<string, object>;
                    if (d == null || string.CompareOrdinal(kv.Key, firstKey) < 0) continue;
                    var t = new TokenDay
                    {
                        In = Long(d, "in"), CacheWrite = Long(d, "cw"), CacheRead = Long(d, "cr"), Out = Long(d, "out"),
                    };
                    foreach (var pair in new[] { new { K = "m", M = t.Models }, new { K = "p", M = t.Projects } })
                    {
                        var sub = d.ContainsKey(pair.K) ? d[pair.K] as IDictionary<string, object> : null;
                        if (sub != null) foreach (var x in sub) pair.M[x.Key] = Long(sub, x.Key);
                    }
                    days[kv.Key] = t;
                    closed.Add(kv.Key);
                }
            }
            catch (Exception e) { Log.Write("токены, кеш: " + e.Message); }
        }

        static long Long(IDictionary<string, object> m, string key)
        {
            object v;
            if (!m.TryGetValue(key, out v) || v == null || v is string || v is bool) return 0;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        static void Save()
        {
            var map = new JObj();
            foreach (var key in closed.OrderBy(k => k, StringComparer.Ordinal))
            {
                TokenDay t;
                if (!days.TryGetValue(key, out t)) continue;
                var m = new JObj();
                foreach (var kv in t.Models) m.Add(kv.Key, kv.Value);
                var p = new JObj();
                foreach (var kv in t.Projects) p.Add(kv.Key, kv.Value);
                map.Add(key, new JObj().Add("in", t.In).Add("cw", t.CacheWrite).Add("cr", t.CacheRead).Add("out", t.Out).Add("m", m).Add("p", p));
            }
            try
            {
                Directory.CreateDirectory(Log.Dir);
                Files.WriteAtomic(CachePath, Json.Write(new JObj().Add("v", 1).Add("days", map)));
            }
            catch (Exception e) { Log.Write("токены, кеш: " + e.Message); }
        }

        // «1,2 млн», «340 млн», «2,4 млрд», «12 тыс».
        public static string Human(long value)
        {
            double v = value;
            Func<double, string> one = x => L.Dec(x.ToString("0.0", CultureInfo.InvariantCulture));
            if (v >= 1e9) return L.F("num.billion", one(v / 1e9));
            if (v >= 1e8) return L.F("num.million", (v / 1e6).ToString("0", CultureInfo.InvariantCulture));
            if (v >= 1e6) return L.F("num.million", one(v / 1e6));
            if (v >= 1e3) return L.F("num.thousand", (v / 1e3).ToString("0", CultureInfo.InvariantCulture));
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }

    // ---------------------------------------------------------------- программа

    class TrayApp : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = Log.AppName;
        // Автозапуск включён, пока его не выключили в настройках: установка из чата Claude пишет его в реестр пакета
        // Claude, а не в настоящий, поэтому программа сама проверяет его при каждом старте.
        static readonly string AutostartOffPath = Path.Combine(Log.Dir, "autostart-off");

        public readonly Sensors Sensors = new Sensors();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly Panel panel;
        readonly System.Windows.Forms.Timer fast = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer minute = new System.Windows.Forms.Timer();
        // Через него фоновые потоки (обход status.md, токены) зовут главный поток.
        readonly Control marshal = new Control();
        string iconKey = "";
        Icon icon;

        public TrayApp()
        {
            marshal.CreateControl();
            GC.KeepAlive(marshal.Handle);
            panel = new Panel(this);
            // Правый клик — короткое меню: всё остальное — в панели и её настройках.
            // Подписи — при каждом открытии: язык мог смениться, пока меню было закрыто.
            var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = true };
            // Пустое меню WinForms открывать не даёт (Cancel уже стоит) — собрали пункты, снимаем отмену.
            menu.Opening += (s, e) =>
            {
                panel.HidePanel();
                FillMenu(menu);
                e.Cancel = false;
            };
            FillMenu(menu);

            tray.ContextMenuStrip = menu;
            tray.MouseUp += (s, e) => { if (e.Button == MouseButtons.Left) TogglePanel(false); };
            tray.Text = "PMP";
            UpdateIcon(true);
            tray.Visible = true;

            Sensors.Tick();
            fast.Interval = 2000;
            fast.Tick += delegate
            {
                // Датчики раз в 2 с, пока панель открыта или значок показывает процессор; иначе — раз в 30 с (график за 3 ч).
                if (panel.Visible || !TrayShowsTokens || (DateTime.Now - Sensors.Now.At).TotalSeconds >= Sensors.HistoryEverySeconds - 1) Sensors.Tick();
                UpdateIcon(false);
                if (panel.Visible) panel.Refit();
            };
            fast.Start();
            SystemEvents.UserPreferenceChanged += OnPrefs;
            SystemEvents.DisplaySettingsChanged += OnPrefs;
            if (!File.Exists(AutostartOffPath) && !AutostartOn()) SetAutostart(true);
            // Сводки проектов и токены — раз в минуту, в фоне.
            minute.Interval = 60000;
            minute.Tick += delegate { RefreshProjects(); RefreshTokens(false); };
            minute.Start();
            RefreshProjects();
            RefreshTokens(false);
        }

        // Обход status.md в фоне; готово — панель.
        public void RefreshProjects()
        {
            if (StatusScan.Root() == null) return;
            StatusScan.Tick(list =>
            {
                try { marshal.BeginInvoke(new Action(panel.Refit)); } catch { }
            });
        }

        public void RefreshTokens(bool soon)
        {
            Tokens.Refresh(soon, delegate { try { marshal.BeginInvoke(new Action(delegate { UpdateIcon(false); panel.Refit(); })); } catch { } });
        }

        void OnPrefs(object sender, EventArgs e) { UpdateIcon(true); }

        void TogglePanel(bool forceOpen)
        {
            if (panel.Visible && !forceOpen) { panel.HidePanel(); return; }
            // Клик по значку при открытой панели сперва снимает фокус (панель прячется) — не открывать снова.
            if (!forceOpen && (DateTime.Now - panel.HiddenAt).TotalMilliseconds < 300) return;
            if ((DateTime.Now - Sensors.Now.At).TotalSeconds > 1.5) Sensors.Tick();
            RefreshProjects();
            RefreshTokens(true);
            panel.ShowNearTray();
        }

        void OpenSettings()
        {
            if (!panel.Visible) TogglePanel(true);
            panel.ShowSheet(Panel.Sheet.Settings);
        }

        public void About()
        {
            panel.HidePanel();
            MessageBox.Show(L.T("about.text") + "\n\n" + Application.ExecutablePath, L.T("about.title"),
                MessageBoxButtons.OK, MessageBoxIcon.None);
        }

        // Язык интерфейса: запомнить и сразу перерисовать — панель, подсказку у значка, меню (оно собирается при открытии).
        public void SetLanguage(bool en)
        {
            if (L.En == en && Settings.Has(L.Key)) return;
            L.Set(en);
            panel.ResetTip();
            UpdateIcon(true);
            panel.Refit();
        }

        void FillMenu(ContextMenuStrip menu)
        {
            menu.Items.Clear();
            menu.Items.Add(new ToolStripMenuItem(L.T("tray.settings"), null, delegate { OpenSettings(); }) { ShortcutKeyDisplayString = "Ctrl+," });
            menu.Items.Add(LanguageMenu(SetLanguage));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(L.T("settings.about"), null, delegate { About(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(L.T("settings.quit"), null, delegate { Quit(); });
        }

        // Пункт «Язык ▸ Русский / English» — в меню правой кнопки у значка и у блоков панели; галка — у текущего.
        public static ToolStripMenuItem LanguageMenu(Action<bool> pick)
        {
            var item = new ToolStripMenuItem(L.T("lang.title"));
            bool en = L.En;
            item.DropDownItems.Add(new ToolStripMenuItem("Русский", null, delegate { pick(false); }) { Checked = !en });
            item.DropDownItems.Add(new ToolStripMenuItem("English", null, delegate { pick(true); }) { Checked = en });
            var drop = item.DropDown as ToolStripDropDownMenu;
            if (drop != null) { drop.ShowImageMargin = false; drop.ShowCheckMargin = true; }
            item.DropDown.Renderer = new DarkMenuRenderer();
            return item;
        }

        public void Quit() { ExitThread(); }

        // Что показывает значок у часов: true — токены за сегодня, false — нагрузка процессора (по умолчанию).
        public static bool TrayShowsTokens { get { return Settings.GetString(Settings.TrayShows, "cpu") == "tokens"; } }

        public void SetTrayShows(bool tokens)
        {
            Settings.SetValue(Settings.TrayShows, tokens ? "tokens" : null);
            if (tokens) RefreshTokens(true); else Sensors.Tick();
            UpdateIcon(true);
            panel.Refit();
        }

        // Папка проектов для блока Workflow; null — выключить.
        public void SetWorkflowRoot(string path)
        {
            Settings.SetValue(Settings.WorkflowRoot, string.IsNullOrWhiteSpace(path) ? null : path);
            RefreshProjects();
            panel.Refit();
        }

        // Цвет PMP: плашка у часов и панель.
        public void SetAccent(string id)
        {
            Settings.SetValue(Settings.Accent, id);
            Palette.PreviewAccent = null;
            UpdateIcon(true);
            panel.Refit();
        }

        void UpdateIcon(bool force)
        {
            // Значок: нагрузка процессора (жёлтая плашка — от 70 %, красная — от 90 %) или токены за сегодня.
            int? number = null;
            string label = null, tip;
            var level = Level.Normal;
            if (TrayShowsTokens)
            {
                var today = Tokens.Total(DateTime.Today);
                if (today.HasValue) { label = TrayIcon.Compact(today.Value); tip = L.F("tray.tipTokens", Tokens.Human(today.Value)); }
                else tip = L.T("tray.counting");
            }
            else
            {
                var cpu = Sensors.Now.Cpu;
                if (cpu.HasValue)
                {
                    number = (int)Math.Round(cpu.Value);
                    label = number.Value.ToString(CultureInfo.InvariantCulture);
                    level = cpu.Value >= 90 ? Level.Critical : cpu.Value >= 70 ? Level.Low : Level.Normal;
                    tip = L.F("tray.tipCpu", number.Value);
                }
                else tip = L.T("tray.counting");
            }
            bool outlined = label == null;
            string key = label + "|" + level + "|" + TrayIcon.TaskbarLight() + "|" + TrayIcon.Size() + "|" + Palette.AccentId;
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            if (tray.Text != tip) tray.Text = tip;
            if (!force && key == iconKey) return;
            iconKey = key;
            var old = icon;
            icon = TrayIcon.Make(label, level, outlined);
            tray.Icon = icon;
            if (old != null) old.Dispose();
        }

        public static bool AutostartOn()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null; }
            catch { return false; }
        }

        public static void SetAutostart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunName, false);
                }
                Directory.CreateDirectory(Log.Dir);
                if (on) File.Delete(AutostartOffPath);
                else File.WriteAllText(AutostartOffPath, DateTime.Now.ToString("s"));
            }
            catch (Exception e) { Log.Write("autostart: " + e.Message); }
        }

        protected override void ExitThreadCore()
        {
            minute.Stop();
            SystemEvents.UserPreferenceChanged -= OnPrefs;
            SystemEvents.DisplaySettingsChanged -= OnPrefs;
            fast.Stop();
            tray.Visible = false;
            tray.Dispose();
            base.ExitThreadCore();
        }
    }
}
