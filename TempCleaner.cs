using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

class TempCleaner : Form
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHQueryRecycleBin(string root, ref SHQUERYRBINFO info);
    [StructLayout(LayoutKind.Sequential, Pack = 4)] struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    class Category
    {
        public string Title, Hint;
        public string[] Folders;
        public string Pattern = "*";
        public bool Default, RecycleBin;
        public long Size;
        public int Files;
    }

    readonly List<Category> categories = new List<Category>();
    ListView list;
    Label totalLabel, statusLabel;
    Button scanButton, cleanButton;
    CheckBox oldOnlyBox;

    TempCleaner()
    {
        Text = "TempCleaner";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new Size(600, 470);
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterScreen;

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Add("Временные файлы пользователя", "%TEMP%", true, Path.GetTempPath());
        Add("Временные файлы Windows", "нужны права админа", true, Path.Combine(windows, "Temp"));
        Add("Кэш Google Chrome", "пароли и вкладки останутся", true, BrowserCaches(Path.Combine(local, @"Google\Chrome\User Data")));
        Add("Кэш Microsoft Edge", "пароли и вкладки останутся", true, BrowserCaches(Path.Combine(local, @"Microsoft\Edge\User Data")));
        Add("Кэш Яндекс Браузера", "пароли и вкладки останутся", true, BrowserCaches(Path.Combine(local, @"Yandex\YandexBrowser\User Data")));
        Add("Кэш Firefox", "пароли и вкладки останутся", true, FirefoxCaches(Path.Combine(local, @"Mozilla\Firefox\Profiles")));
        Add("Дампы и отчёты об ошибках", "CrashDumps, WER", true, Path.Combine(local, "CrashDumps"), Path.Combine(local, @"Microsoft\Windows\WER"));
        Add("Кэш эскизов", "превью пересоздадутся", false, Path.Combine(local, @"Microsoft\Windows\Explorer")).Pattern = "thumbcache_*.db";
        Add("Загрузки обновлений Windows", "нужны права админа", false, Path.Combine(windows, @"SoftwareDistribution\Download"));
        var bin = Add("Корзина", "удаляется навсегда", false);
        bin.RecycleBin = true;

        list = new ListView { Location = new Point(12, 12), Size = new Size(576, 318), View = View.Details, CheckBoxes = true, FullRowSelect = true };
        list.Columns.Add("Что очистить", 260);
        list.Columns.Add("Размер", 90, HorizontalAlignment.Right);
        list.Columns.Add("", 200);
        foreach (var c in categories)
            list.Items.Add(new ListViewItem(new[] { c.Title, "…", c.Hint }) { Tag = c, Checked = c.Default });
        list.ItemChecked += (s, e) => UpdateTotal();
        Controls.Add(list);

        oldOnlyBox = new CheckBox { Text = "Не трогать файлы моложе суток (безопаснее)", Checked = true, Location = new Point(12, 340), AutoSize = true };
        Controls.Add(oldOnlyBox);
        oldOnlyBox.CheckedChanged += (s, e) => Scan();

        totalLabel = new Label { Location = new Point(12, 372), Size = new Size(576, 30), Font = new Font("Segoe UI", 13, FontStyle.Bold), ForeColor = Color.FromArgb(40, 90, 160) };
        Controls.Add(totalLabel);
        statusLabel = new Label { Location = new Point(12, 404), Size = new Size(340, 50), ForeColor = Color.DimGray, Font = new Font("Segoe UI", 9) };
        statusLabel.Text = "Занятые программами файлы пропускаются. Закрой браузеры, чтобы очистить их кэш полностью";
        Controls.Add(statusLabel);
        scanButton = new Button { Text = "Пересчитать", Location = new Point(362, 414), Size = new Size(110, 38) };
        scanButton.Click += (s, e) => Scan();
        Controls.Add(scanButton);
        cleanButton = new Button { Text = "Очистить", Location = new Point(478, 414), Size = new Size(110, 38), Font = new Font("Segoe UI", 10, FontStyle.Bold) };
        cleanButton.Click += (s, e) => Clean();
        Controls.Add(cleanButton);

        Shown += (s, e) => Scan();
    }

    Category Add(string title, string hint, bool def, params string[] folders)
    {
        var c = new Category { Title = title, Hint = hint, Default = def, Folders = folders };
        categories.Add(c);
        return c;
    }

    static string[] BrowserCaches(string userData)
    {
        var result = new List<string>();
        if (!Directory.Exists(userData)) return result.ToArray();
        foreach (var profile in Directory.GetDirectories(userData))
        {
            string name = Path.GetFileName(profile);
            if (name != "Default" && !name.StartsWith("Profile")) continue;
            result.Add(Path.Combine(profile, "Cache"));
            result.Add(Path.Combine(profile, "Code Cache"));
            result.Add(Path.Combine(profile, "GPUCache"));
            result.Add(Path.Combine(profile, @"Service Worker\CacheStorage"));
        }
        return result.ToArray();
    }

    static string[] FirefoxCaches(string profiles)
    {
        if (!Directory.Exists(profiles)) return new string[0];
        return Directory.GetDirectories(profiles).Select(p => Path.Combine(p, "cache2")).ToArray();
    }

    IEnumerable<FileInfo> Files(Category c, bool oldOnly)
    {
        var cutoff = DateTime.Now.AddDays(-1);
        foreach (var folder in c.Folders)
        {
            if (!Directory.Exists(folder)) continue;
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(folder));
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                FileInfo[] files;
                try
                {
                    files = dir.GetFiles(c.Pattern);
                    if (c.Pattern == "*") foreach (var d in dir.GetDirectories()) if ((d.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(d);
                }
                catch { continue; }
                foreach (var f in files)
                    if (!oldOnly || f.LastWriteTime < cutoff) yield return f;
            }
        }
    }

    void Scan()
    {
        scanButton.Enabled = cleanButton.Enabled = false;
        totalLabel.Text = "Считаю…";
        bool oldOnly = oldOnlyBox.Checked;
        new Thread(() =>
        {
            foreach (var c in categories)
            {
                c.Size = 0; c.Files = 0;
                if (c.RecycleBin)
                {
                    var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO)) };
                    if (SHQueryRecycleBin(null, ref info) == 0) { c.Size = info.i64Size; c.Files = (int)info.i64NumItems; }
                }
                else foreach (var f in Files(c, oldOnly)) { try { c.Size += f.Length; c.Files++; } catch { } }
                var cat = c;
                BeginInvoke(new Action(() => ShowSize(cat)));
            }
            BeginInvoke(new Action(() => { scanButton.Enabled = cleanButton.Enabled = true; UpdateTotal(); }));
        }) { IsBackground = true }.Start();
    }

    void ShowSize(Category c)
    {
        foreach (ListViewItem item in list.Items)
            if (item.Tag == c) item.SubItems[1].Text = FormatSize(c.Size);
    }

    void UpdateTotal()
    {
        long total = list.CheckedItems.Cast<ListViewItem>().Sum(i => ((Category)i.Tag).Size);
        totalLabel.Text = "Можно освободить: " + FormatSize(total);
    }

    void Clean()
    {
        var selected = list.CheckedItems.Cast<ListViewItem>().Select(i => (Category)i.Tag).ToList();
        if (selected.Count == 0) return;
        if (selected.Any(c => c.RecycleBin) &&
            MessageBox.Show(this, "Корзина будет очищена навсегда. Продолжить?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        scanButton.Enabled = cleanButton.Enabled = false;
        totalLabel.Text = "Чищу…";
        bool oldOnly = oldOnlyBox.Checked;
        new Thread(() =>
        {
            long freed = 0;
            int skipped = 0;
            foreach (var c in selected)
            {
                if (c.RecycleBin)
                {
                    if (SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4) == 0) freed += c.Size;
                    continue;
                }
                foreach (var f in Files(c, oldOnly).ToList())
                {
                    try { long len = f.Length; f.Attributes = FileAttributes.Normal; f.Delete(); freed += len; }
                    catch { skipped++; }
                }
                if (c.Pattern == "*")
                    foreach (var folder in c.Folders) RemoveEmptyDirs(folder, true);
            }
            BeginInvoke(new Action(() =>
            {
                statusLabel.Text = "Освобождено " + FormatSize(freed) + (skipped > 0 ? ". Пропущено занятых файлов: " + skipped : "");
                Scan();
            }));
        }) { IsBackground = true }.Start();
    }

    static void RemoveEmptyDirs(string dir, bool isRoot)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var d in Directory.GetDirectories(dir))
                if ((new DirectoryInfo(d).Attributes & FileAttributes.ReparsePoint) == 0) RemoveEmptyDirs(d, false);
            if (!isRoot && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch { }
    }

    static string FormatSize(long bytes)
    {
        string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return (u == 0 ? v.ToString("0") : v.ToString("0.0")) + " " + units[u];
    }

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.Run(new TempCleaner());
    }
}
