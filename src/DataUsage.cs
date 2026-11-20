// Data Usage - small window showing the current internet connection's data usage and limit.
// Build: see build.cmd (uses the C# compiler that ships with Windows, no installs needed).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Automation;
using Microsoft.Win32;
using Windows.Networking.Connectivity;

class UsageInfo
{
    public string Error;
    public string Name = "", Kind = "", Status = "";
    public bool Metered, HasLimit, Recurring;
    public double LimitBytes;
    public long Sent, Recv;
    public DateTime Start;
    public DateTime End;
    public string LimitType = "", RangeLabel = "";
    public List<KeyValuePair<string, long>> Apps = new List<KeyValuePair<string, long>>();
    public DateTime Updated;
    public DateTime PrevStart;
    public bool ResetPending; // the period is over but Windows has not zeroed its counter yet
    public long PlanUsed; // Windows' own running total for the current limit period, when available
    public long Used { get { return PlanUsed > 0 ? PlanUsed : Sent + Recv; } }
}

static class Reader
{
    static T Wait<T>(Windows.Foundation.IAsyncOperation<T> op)
    {
        while (op.Status == Windows.Foundation.AsyncStatus.Started) System.Threading.Thread.Sleep(15);
        return op.GetResults();
    }

    public static readonly string[] Ranges = { "Last 24 hours", "Last 7 days", "Last month" };

    public static UsageInfo Load(int range, bool forceNoLimit)
    {
        var info = new UsageInfo { Updated = DateTime.Now };
        try
        {
            ConnectionProfile p = NetworkInformation.GetInternetConnectionProfile();
            if (p == null) { info.Error = "No internet connection found."; return info; }

            info.Name = p.ProfileName;
            uint iana = p.NetworkAdapter != null ? p.NetworkAdapter.IanaInterfaceType : 0;
            info.Kind = iana == 6 ? "Ethernet" : iana == 71 ? "Wi-Fi" : (iana == 243 || iana == 244) ? "Cellular" : "Network";

            ConnectionCost cost = p.GetConnectionCost();
            info.Metered = cost.NetworkCostType != NetworkCostType.Unrestricted;
            info.Status = cost.OverDataLimit ? "Over data limit" : cost.ApproachingDataLimit ? "Approaching data limit" : "";

            DateTime now = DateTime.Now;
            ReadLimit(p, info, now);
            if (forceNoLimit) { info.HasLimit = false; info.Recurring = false; info.PlanUsed = 0; }
            if (!info.HasLimit)
            {
                if (range < 0 || range >= Ranges.Length) range = 2;
                info.RangeLabel = Ranges[range];
                info.Start = range == 0 ? now.AddHours(-24) : range == 1 ? now.AddDays(-7) : now.AddMonths(-1);
                info.End = now;
                info.LimitType = "No limit set";
            }

            var states = new NetworkUsageStates { Roaming = TriStates.DoNotCare, Shared = TriStates.DoNotCare };
            for (int pass = 0; pass < 2; pass++)
            {
            info.Sent = 0; info.Recv = 0;
            var start = new DateTimeOffset(info.Start);
            var end = new DateTimeOffset(now);

            var usage = Wait(p.GetNetworkUsageAsync(start, end, DataUsageGranularity.Total, states));
            foreach (NetworkUsage u in usage) { info.Sent += (long)u.BytesSent; info.Recv += (long)u.BytesReceived; }

            try
            {
                var apps = Wait(p.GetAttributedNetworkUsageAsync(start, end, states));
                var map = new Dictionary<string, long>();
                foreach (AttributedNetworkUsage a in apps)
                {
                    string n = string.IsNullOrEmpty(a.AttributionName) ? a.AttributionId : a.AttributionName;
                    n = Friendly(n);
                    long b = (long)(a.BytesSent + a.BytesReceived);
                    long cur; map.TryGetValue(n, out cur); map[n] = cur + b;
                }
                info.Apps = map.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).ToList();
            }
            catch { /* per-app breakdown is optional */ }

            // Windows rolls its counter over a little after the period ends. Until it does, keep showing the
            // period Windows is still counting so every number here matches the Settings page.
            if (pass == 0 && info.HasLimit && info.Recurring && info.PlanUsed > 0
                && info.PlanUsed - (info.Sent + info.Recv) > Math.Max(50L << 20, info.PlanUsed / 10))
            {
                info.End = info.Start; info.Start = info.PrevStart; info.ResetPending = true;
                continue;
            }
            break;
            }
        }
        catch (Exception ex) { info.Error = ex.Message; }
        return info;
    }

    [DllImport("wcmapi.dll", CharSet = CharSet.Unicode)]
    static extern uint WcmQueryProperty(ref Guid itf, string profile, int property, IntPtr reserved, out uint size, out IntPtr data);
    [DllImport("wcmapi.dll")]
    static extern void WcmFreeMemory(IntPtr p);

    static byte[] QueryWcm(Guid adapter, string profile)
    {
        try
        {
            uint size; IntPtr data;
            if (WcmQueryProperty(ref adapter, profile, 5 /* dataplan status */, IntPtr.Zero, out size, out data) != 0 || data == IntPtr.Zero) return null;
            var b = new byte[size];
            Marshal.Copy(data, b, 0, (int)size);
            WcmFreeMemory(data);
            return b;
        }
        catch { return null; }
    }

    static string Friendly(string n)
    {
        if (string.IsNullOrEmpty(n)) return "Unknown";
        if (n.IndexOf('\\') >= 0)
        {
            string leaf = n.Substring(n.LastIndexOf('\\') + 1);
            if (leaf.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) leaf = leaf.Substring(0, leaf.Length - 4);
            return leaf.Length > 0 ? leaf : n;
        }
        int us = n.LastIndexOf('_');
        if (us > 0 && n.Length - us == 14) // package family name: Name_publisherid
        {
            n = n.Substring(0, us);
            int dot = n.LastIndexOf('.');
            if (dot >= 0 && dot < n.Length - 1) n = n.Substring(dot + 1);
        }
        return n;
    }

    // The limit you set in Settings > Data usage is stored by the Data Usage service here.
    static void ReadLimit(ConnectionProfile p, UsageInfo info, DateTime now)
    {
        try
        {
            if (p.NetworkAdapter == null) return;
            string guid = p.NetworkAdapter.NetworkAdapterId.ToString("B").ToUpperInvariant();
            using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey k = hklm.OpenSubKey(@"SOFTWARE\Microsoft\DusmSvc\Profiles\" + guid))
            {
                // Preferred source: Windows Connection Manager's live data plan status (includes usage so far).
                byte[] plan = QueryWcm(p.NetworkAdapter.NetworkAdapterId, p.ProfileName);
                bool live = plan != null && plan.Length >= 52 && BitConverter.ToUInt32(plan, 12) != 0;
                if (!live)
                {
                    plan = null;
                    if (k == null) return;
                    var names = new List<string> { p.ProfileName, "*" };
                    names.AddRange(k.GetSubKeyNames());
                    foreach (string n in names)
                    {
                        using (RegistryKey sk = k.OpenSubKey(n))
                        {
                            if (sk == null) continue;
                            plan = sk.GetValue("UserDataPlan") as byte[];
                            if (plan != null && plan.Length >= 44) break;
                            plan = null;
                        }
                    }
                }
                if (plan == null) return;

                uint limitMb = BitConverter.ToUInt32(plan, 12);
                if (limitMb == 0) return;
                if (live) info.PlanUsed = BitConverter.ToUInt32(plan, 0) * 1048576L;
                long ft = BitConverter.ToInt64(plan, 24);
                DateTime start = ft > 0 ? DateTime.FromFileTime(ft) : now.Date;
                int y = BitConverter.ToUInt16(plan, 32), mo = BitConverter.ToUInt16(plan, 34), d = BitConverter.ToUInt16(plan, 36);
                int h = BitConverter.ToUInt16(plan, 38), mi = BitConverter.ToUInt16(plan, 40), s = BitConverter.ToUInt16(plan, 42);

                info.HasLimit = true;
                info.LimitBytes = limitMb * 1024.0 * 1024.0;
                info.Recurring = (y | mo | d | h | mi | s) != 0;
                if (!info.Recurring)
                {
                    info.Start = start; info.End = now; info.LimitType = "One time";
                    return;
                }
                Func<int, DateTime> at = i => start.AddYears(y * i).AddMonths(mo * i).AddDays(d * i).AddHours(h * i).AddMinutes(mi * i).AddSeconds(s * i);
                int idx = 0;
                while (idx < 100000 && at(idx + 1) <= now) idx++;
                info.Start = at(idx);
                info.End = at(idx + 1);
                info.PrevStart = at(idx - 1);

                if (y == 0 && mo == 1 && d == 0) info.LimitType = "Monthly";
                else if (y == 0 && mo == 0 && d == 1) info.LimitType = "Daily";
                else if (y == 0 && mo == 0 && d == 7) info.LimitType = "Weekly";
                else
                {
                    var parts = new List<string>();
                    if (y > 0) parts.Add(y + " yr"); if (mo > 0) parts.Add(mo + " mo"); if (d > 0) parts.Add(d + " days"); if (h > 0) parts.Add(h + " hr");
                    info.LimitType = "Every " + string.Join(" ", parts);
                }
            }
        }
        catch { }
    }
}

class MainForm : Form
{
    static readonly Color Bg = Color.FromArgb(24, 25, 29), Card = Color.FromArgb(36, 38, 45), Track = Color.FromArgb(54, 57, 66);
    static readonly Color Fg = Color.FromArgb(240, 242, 245), Muted = Color.FromArgb(150, 156, 168);
    static readonly Color Blue = Color.FromArgb(76, 194, 255), Amber = Color.FromArgb(245, 176, 65), Red = Color.FromArgb(239, 83, 80);

    UsageInfo info;
    bool loading, reloadAgain;
    public static bool ForceNoLimit;
    int rangeIndex = 2;
    bool rangeHover, autoStart = GetAutoStart();
    Rectangle rangeRect, autoRect;
    ContextMenuStrip rangeMenu;
    ToolStripMenuItem autoItem;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string PrefKey = @"Software\DataUsageWindow";
    readonly float s;
    readonly Button btnRefresh, btnSettings;
    readonly Timer refreshTimer = new Timer { Interval = 60000 }, tick = new Timer { Interval = 15000 };
    readonly Font fTitle, fBig, fBody, fSmall, fBold;
    public string ShotPath;
    const int MaxApps = 8;

    public MainForm()
    {
        Text = "Data Usage";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        Opacity = 0; // shown once the real size is known and the window is placed fully on screen
        BackColor = Bg;
        DoubleBuffered = true;
        using (Graphics g = CreateGraphics()) s = g.DpiX / 96f;
        fTitle = new Font("Segoe UI Semibold", 12f);
        fBig = new Font("Segoe UI Semibold", 26f);
        fBody = new Font("Segoe UI", 9.5f);
        fBold = new Font("Segoe UI Semibold", 9.5f);
        fSmall = new Font("Segoe UI", 8.5f);
        contentW = S(380); titleH = S(34);
        Size = new Size(contentW, S(120));
        try { using (Stream st = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico")) Icon = new Icon(st); } catch { }
        tray = new NotifyIcon { Icon = Icon ?? SystemIcons.Application, Text = "Data Usage", Visible = false };
        var menu = new ContextMenuStrip { ShowImageMargin = false, BackColor = Card, ForeColor = Fg, Font = fBody, Renderer = new ToolStripProfessionalRenderer(new DarkColors()) };
        menu.Items.Add("Open Data Usage", null, (o, e) => FromTray());
        menu.Items.Add("Refresh now", null, (o, e) => Reload());
        menu.Items.Add("Open Windows data usage settings", null, (o, e) => OpenSettings());
        autoItem = new ToolStripMenuItem(AutoText(), null, (o, e) => ToggleAutoStart());
        menu.Items.Add(autoItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (o, e) => Close());
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (o, e) => { if (e.Button == MouseButtons.Left) FromTray(); };
        FormClosed += (o, e) => { tray.Visible = false; tray.Dispose(); };

        btnRefresh = MakeButton("Refresh");
        btnSettings = MakeButton("Open in Settings");
        try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(PrefKey)) if (k != null) rangeIndex = (int)k.GetValue("Range", 2); } catch { }
        rangeIndex = Math.Max(0, Math.Min(Reader.Ranges.Length - 1, rangeIndex));
        rangeMenu = new ContextMenuStrip { ShowImageMargin = false, BackColor = Card, ForeColor = Fg, Font = fBody, Renderer = new ToolStripProfessionalRenderer(new DarkColors()) };
        for (int i = 0; i < Reader.Ranges.Length; i++)
        {
            int idx = i;
            var it = new ToolStripMenuItem(Reader.Ranges[i]) { Padding = new Padding(0, S(3), 0, S(3)) };
            it.Click += (o, e) =>
            {
                rangeIndex = idx;
                try { using (RegistryKey k = Registry.CurrentUser.CreateSubKey(PrefKey)) k.SetValue("Range", idx); } catch { }
                Reload();
            };
            rangeMenu.Items.Add(it);
        }
        rangeMenu.Opening += (o, e) => { for (int i = 0; i < rangeMenu.Items.Count; i++) rangeMenu.Items[i].ForeColor = i == rangeIndex ? Blue : Fg; };
        btnRefresh.Click += (o, e) => Reload();
        btnSettings.Click += (o, e) => OpenSettings();
        Resize += (o, e) => { if (WindowState == FormWindowState.Minimized) { ToTray(); return; } if (WindowState == FormWindowState.Maximized) { WindowState = FormWindowState.Normal; return; } ApplyLayout(); Invalidate(); };
        refreshTimer.Tick += (o, e) => Reload();
        tick.Tick += (o, e) => Invalidate();
        refreshTimer.Start(); tick.Start();
        Shown += (o, e) => Reload();
        KeyPreview = true;
        KeyDown += (o, e) => { if (e.KeyCode == Keys.F5) Reload(); else if (e.KeyCode == Keys.Escape) Close(); };
    }

    int S(float v) { return (int)Math.Round(v * s); }

    Button MakeButton(string text)
    {
        var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = Card, ForeColor = Fg, Font = fBody, Cursor = Cursors.Hand, TabStop = false, Visible = false };
        b.FlatAppearance.BorderColor = Track;
        b.FlatAppearance.MouseOverBackColor = Track;
        Controls.Add(b);
        return b;
    }

    async void Reload()
    {
        if (loading) { reloadAgain = true; return; }
        loading = true;
        int range = rangeIndex;
        bool force = ForceNoLimit;
        UsageInfo r = await Task.Run(() => Reader.Load(range, force));
        loading = false;
        if (IsDisposed) return;
        if (reloadAgain) { reloadAgain = false; Reload(); return; }
        info = r;
        try { tray.Text = TipText(r); } catch { }
        int h;
        using (Graphics g = CreateGraphics()) h = Render(g, false);
        btnY = h;
        contentH = h + S(30) + S(16);
        ApplyLayout();
        Invalidate();
        if (ShotPath != null)
        {
            Refresh();
            using (var bmp = new Bitmap(ClientSize.Width, ClientSize.Height))
            {
                DrawToBitmap(bmp, new Rectangle(Point.Empty, ClientSize));
                bmp.Save(ShotPath);
            }
            File.WriteAllText(ShotPath + ".txt", Dump(r));
            Close();
        }
    }

    // ----- window chrome: our own themed title bar instead of the Windows one -----
    int contentW, titleH, contentH, btnY, cardY, hover = -1;
    int TbW { get { return S(44); } }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.Style |= 0x20000 | 0x80000;   // WS_MINIMIZEBOX | WS_SYSMENU: taskbar click minimizes / restores
            cp.ClassStyle |= 0x20000;        // CS_DROPSHADOW
            return cp;
        }
    }

    readonly NotifyIcon tray;
    bool placed;
    public bool StartInTray;

    class DarkColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected { get { return Track; } }
        public override Color MenuItemBorder { get { return Track; } }
        public override Color MenuBorder { get { return Track; } }
        public override Color ToolStripDropDownBackground { get { return Card; } }
        public override Color ImageMarginGradientBegin { get { return Card; } }
        public override Color ImageMarginGradientMiddle { get { return Card; } }
        public override Color ImageMarginGradientEnd { get { return Card; } }
        public override Color SeparatorDark { get { return Track; } }
        public override Color SeparatorLight { get { return Track; } }
    }

    // Minimizing hides the window and leaves only the tray icon; left-click on it brings the window back.
    void ToTray()
    {
        tray.Visible = true;
        Hide();
    }

    void FromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        tray.Visible = false;
        ApplyLayout();
        Activate();
        Invalidate();
    }

    static string TipText(UsageInfo i)
    {
        string t;
        if (i.Error != null) t = "Data Usage: unavailable";
        else if (i.HasLimit)
        {
            int pct = i.LimitBytes > 0 ? (int)Math.Floor(i.Used / i.LimitBytes * 100) : 0;
            long left = (long)i.LimitBytes - i.Used;
            t = pct + "% used - " + Fmt(i.Used) + " of " + Fmt((long)i.LimitBytes) + "\n"
                + (i.ResetPending ? "Reset due" : i.Recurring ? Span(i.End - DateTime.Now) + " until reset" : Fmt(Math.Max(0, left)) + " remaining");
        }
        else t = Fmt(i.Used) + " used, " + i.RangeLabel.ToLower();
        return t.Length > 63 ? t.Substring(0, 63) : t; // Windows caps tray tooltips at 63 characters
    }

    // First layout: centre the full-height window on its screen. Later: never let the bottom run off the screen.
    void PlaceOnScreen()
    {
        Rectangle wa = Screen.FromPoint(new Point(Left + Width / 2, Top + S(10))).WorkingArea;
        if (!placed)
        {
            placed = true;
            Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y + Math.Max(0, (wa.Height - Height) / 2));
            Opacity = 1;
            if (StartInTray) { StartInTray = false; ToTray(); }
            return;
        }
        if (Bottom > wa.Bottom) Top = Math.Max(wa.Top, wa.Bottom - Height);
    }

    // "Start with Windows" = an entry in this user's Run key that launches the app hidden in the tray. Off unless ticked.
    static bool GetAutoStart()
    {
        try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("DataUsage") != null; }
        catch { return false; }
    }

    string AutoText() { return (autoStart ? "\u2713  " : "     ") + "Start with Windows (in tray)"; }

    void ToggleAutoStart()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (autoStart) k.DeleteValue("DataUsage", false);
                else k.SetValue("DataUsage", "\"" + Application.ExecutablePath + "\" --tray");
            }
            autoStart = !autoStart;
        }
        catch { }
        autoItem.Text = AutoText();
        Invalidate();
    }

    void SetMaxBounds()
    {
        Screen sc = Screen.FromPoint(new Point(Left + Width / 2, Top + Height / 2));
        Rectangle wa = sc.WorkingArea, b = sc.Bounds;
        MaximizedBounds = new Rectangle(wa.X - b.X, wa.Y - b.Y, wa.Width, wa.Height);
    }

    // Keeps the window at its proper size (never the collapsed size seen after a minimize) and places the controls.
    void ApplyLayout()
    {
        if (WindowState == FormWindowState.Minimized || contentH == 0) return;
        if (WindowState == FormWindowState.Normal)
        {
            var want = new Size(contentW, contentH);
            if (Size != want) { Size = want; return; } // Resize fires again and lands below
            PlaceOnScreen();
        }
        int ox = Math.Max(0, (ClientSize.Width - contentW) / 2);
        int bh = S(30), pad = S(16), gap = S(8), bw = (contentW - pad * 2 - gap) / 2, cw = S(124);
        btnRefresh.SetBounds(ox + pad, btnY, bw, bh);
        btnSettings.SetBounds(ox + pad + bw + gap, btnY, bw, bh);
        btnRefresh.Visible = btnSettings.Visible = true;
    }

    Rectangle TitleBtn(int i) { return new Rectangle(ClientSize.Width - (2 - i) * TbW, 0, TbW, titleH); }

    int HitBtn(Point p)
    {
        for (int i = 0; i < 2; i++) if (TitleBtn(i).Contains(p)) return i;
        return -1;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0xA3) return; // double-click on the title strip: no maximize, the size is fixed
        if (m.Msg == 0x112 && ((int)m.WParam.ToInt64() & 0xFFF0) == 0xF030) return; // SC_MAXIMIZE
        if (m.Msg == 0x84) // WM_NCHITTEST: the title strip drags the window like a normal caption
        {
            long lp = m.LParam.ToInt64();
            Point p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            if (p.Y >= 0 && p.Y < titleH && p.X < ClientSize.Width - 2 * TbW) { m.Result = (IntPtr)2; return; }
        }
        base.WndProc(ref m);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = HitBtn(e.Location);
        if (h != hover) { hover = h; Invalidate(new Rectangle(0, 0, ClientSize.Width, titleH)); }
        bool rh = rangeRect.Contains(e.Location);
        if (rh != rangeHover) { rangeHover = rh; Invalidate(); }
        Cursor = rh || autoRect.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hover != -1) { hover = -1; Invalidate(new Rectangle(0, 0, ClientSize.Width, titleH)); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        if (rangeRect.Contains(e.Location)) { rangeMenu.MinimumSize = new Size(rangeRect.Width, 0); rangeMenu.Show(this, new Point(rangeRect.Left, rangeRect.Bottom + S(3))); return; }
        if (autoRect.Contains(e.Location)) { ToggleAutoStart(); return; }
        int h = HitBtn(e.Location);
        hover = -1;
        if (h == 0) ToTray();
        else if (h == 1) Close();
    }

    void DrawTitle(Graphics g)
    {
        int W = ClientSize.Width;
        using (var bBar = new SolidBrush(Color.FromArgb(17, 18, 21)))
        using (var bMuted = new SolidBrush(Muted))
        using (var pen = new Pen(Fg, Math.Max(1f, (float)Math.Round(s))))
        using (var border = new Pen(Track, 1f))
        {
            g.SmoothingMode = SmoothingMode.None;
            g.FillRectangle(bBar, 0, 0, W, titleH);
            int isz = S(16);
            if (Icon != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                using (var ic = new Icon(Icon, new Size(isz, isz))) g.DrawIcon(ic, new Rectangle(S(11), (titleH - isz) / 2, isz, isz));
            }
            SizeF ts = g.MeasureString("Data Usage", fSmall);
            g.DrawString("Data Usage", fSmall, bMuted, S(34), (titleH - ts.Height) / 2f);
            for (int i = 0; i < 2; i++)
            {
                Rectangle r = TitleBtn(i);
                if (hover == i) using (var hb = new SolidBrush(i == 1 ? Color.FromArgb(196, 43, 28) : Track)) g.FillRectangle(hb, r);
                int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, q = S(5);
                if (i == 0) g.DrawLine(pen, cx - q, cy, cx + q, cy);
                else
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.DrawLine(pen, cx - q, cy - q, cx + q, cy + q);
                    g.DrawLine(pen, cx + q, cy - q, cx - q, cy + q);
                    g.SmoothingMode = SmoothingMode.None;
                }
            }
            if (WindowState == FormWindowState.Normal) g.DrawRectangle(border, 0, 0, W - 1, ClientSize.Height - 1);
        }
    }

    // Opens Settings, then steps into the Data usage page itself (the link alone stops at Network & internet on Windows 11).
    void OpenSettings()
    {
        try { System.Diagnostics.Process.Start("ms-settings:datausage"); } catch { return; }
        Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < 25; i++)
                {
                    System.Threading.Thread.Sleep(400);
                    AutomationElement win = AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.NameProperty, "Settings"));
                    if (win == null) continue;
                    if (win.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemSettings_DataUsage_AppList_Page_ComboBox")) != null) return;
                    foreach (AutomationElement b in win.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                    {
                        string n = b.Current.Name;
                        if (n != null && n.StartsWith("Data usage for", StringComparison.OrdinalIgnoreCase))
                        {
                            ((InvokePattern)b.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                            return;
                        }
                    }
                }
            }
            catch { }
        });
    }

    static string Dump(UsageInfo i)
    {
        var sb = new StringBuilder();
        sb.AppendLine("error=" + i.Error);
        sb.AppendLine("name=" + i.Name + " kind=" + i.Kind + " metered=" + i.Metered + " limitType=" + i.LimitType);
        sb.AppendLine("used=" + Fmt(i.Used) + " limit=" + Fmt((long)i.LimitBytes) + " sent=" + Fmt(i.Sent) + " recv=" + Fmt(i.Recv));
        sb.AppendLine("start=" + i.Start + " end=" + i.End);
        foreach (var a in i.Apps) sb.AppendLine(a.Key + ": " + Fmt(a.Value));
        return sb.ToString();
    }

    public static string Fmt(long bytes)
    {
        double mb = bytes / 1048576.0;
        if (mb >= 1024) return (mb / 1024).ToString("0.00") + " GB";
        if (mb >= 1) return mb.ToString("0") + " MB";
        return bytes <= 0 ? "0 MB" : "< 1 MB";
    }

    static string Span(TimeSpan t)
    {
        if (t.TotalMinutes < 1) return "less than a minute";
        if (t.TotalHours < 1) return t.Minutes + " min";
        if (t.TotalDays < 1) return t.Hours + " hr " + t.Minutes + " min";
        return (int)t.TotalDays + (t.TotalDays < 2 ? " day " : " days ") + t.Hours + " hr";
    }

    protected override void OnPaint(PaintEventArgs e) { Render(e.Graphics, true); DrawTitle(e.Graphics); }

    static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // Draws everything (or just measures when draw == false). Returns the y where the buttons go.
    int Render(Graphics g, bool draw)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        int ox = Math.Max(0, (ClientSize.Width - contentW) / 2);
        int pad = ox + S(16), w = contentW - S(32), y = titleH + S(10);
        rangeRect = Rectangle.Empty; autoRect = Rectangle.Empty;
        var right = new StringFormat { Alignment = StringAlignment.Far, FormatFlags = StringFormatFlags.NoWrap };
        var ellip = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using (var bFg = new SolidBrush(Fg))
        using (var bMuted = new SolidBrush(Muted))
        using (var bCard = new SolidBrush(Card))
        using (var bTrack = new SolidBrush(Track))
        {
            if (info == null)
            {
                if (draw) g.DrawString("Loading…", fBody, bMuted, pad, y);
                return y + S(40);
            }
            if (info.Error != null)
            {
                if (draw) g.DrawString(info.Error, fBody, bFg, new RectangleF(pad, y, w, S(60)));
                return y + S(70);
            }

            // Header: connection name + type / metered
            if (draw)
            {
                g.DrawString(info.Name, fTitle, bFg, new RectangleF(pad - S(2), y, w, S(26)), ellip);
                string sub = info.Kind + "  ·  " + (info.Metered ? "Metered" : "Not metered") + (info.Status != "" ? "  ·  " + info.Status : "");
                g.DrawString(sub, fSmall, bMuted, pad, y + S(25));
            }
            y += S(52);

            DateTime now = DateTime.Now;
            long used = info.Used;

            if (info.HasLimit)
            {
                double frac = info.LimitBytes > 0 ? used / info.LimitBytes : 0;
                int pct = (int)Math.Floor(frac * 100);
                Color c = frac >= 0.9 ? Red : frac >= 0.75 ? Amber : Blue;
                int cardH = S(132);
                if (draw)
                {
                    using (var path = Round(new RectangleF(pad, y, w, cardH), S(8))) g.FillPath(bCard, path);
                    int ix = pad + S(14), iw = w - S(28);
                    using (var bc = new SolidBrush(c))
                    {
                        string big = pct + "%";
                        SizeF bs = g.MeasureString(big, fBig);
                        g.DrawString(big, fBig, bc, ix - S(6), y + S(6));
                        g.DrawString("used", fBody, bMuted, ix - S(6) + bs.Width - S(6), y + S(30));
                        g.DrawString(info.LimitType + " limit", fSmall, bMuted, new RectangleF(ix, y + S(16), iw, S(18)), right);
                        g.DrawString(Fmt((long)info.LimitBytes), fBold, bFg, new RectangleF(ix, y + S(32), iw, S(20)), right);

                        // percentage bar
                        var bar = new RectangleF(ix, y + S(64), iw, S(14));
                        using (var tp = Round(bar, S(7))) g.FillPath(bTrack, tp);
                        float fw = (float)(bar.Width * Math.Max(0, Math.Min(1, frac)));
                        if (fw > 0)
                        {
                            fw = Math.Max(fw, bar.Height);
                            using (var fp = Round(new RectangleF(bar.X, bar.Y, fw, bar.Height), S(7))) g.FillPath(bc, fp);
                        }
                        // time-elapsed marker for the usage period
                        if (info.Recurring)
                        {
                            double tf = (now - info.Start).TotalSeconds / Math.Max(1, (info.End - info.Start).TotalSeconds);
                            float mx = bar.X + (float)(bar.Width * Math.Max(0, Math.Min(1, tf)));
                            using (var pen = new Pen(Fg, Math.Max(1f, s * 1.5f))) g.DrawLine(pen, mx, bar.Y - S(3), mx, bar.Bottom + S(3));
                        }
                    }
                    long left = (long)info.LimitBytes - used;
                    g.DrawString(Fmt(used) + " used", fBody, bFg, ix - S(2), y + S(88));
                    g.DrawString(left >= 0 ? Fmt(left) + " remaining" : Fmt(-left) + " over limit", fBody, bFg, new RectangleF(ix, y + S(88), iw, S(20)), right);
                    string reset = info.ResetPending ? "Reset due, waiting for Windows to roll over" : info.Recurring ? Span(info.End - now) + " until reset" : "No automatic reset";
                    g.DrawString(reset, fSmall, bMuted, ix - S(1), y + S(108));
                }
                y += cardH + S(12);
            }
            else
            {
                cardY = y;
                int cardH = S(74);
                rangeRect = new Rectangle(pad + w - S(12) - S(128), y + S(12), S(128), S(28));
                if (draw)
                {
                    using (var path = Round(new RectangleF(pad, y, w, cardH), S(8))) g.FillPath(bCard, path);
                    using (var bc = new SolidBrush(Blue)) g.DrawString(Fmt(used), fBig, bc, pad + S(8), y + S(4));
                    // range picker (opens a themed menu)
                    using (var pb = new SolidBrush(rangeHover ? Color.FromArgb(70, 74, 86) : Track))
                    using (var pp = Round(rangeRect, S(6))) g.FillPath(pb, pp);
                    SizeF rs = g.MeasureString(info.RangeLabel, fBody);
                    g.DrawString(info.RangeLabel, fBody, bFg, rangeRect.X + S(9), rangeRect.Y + (rangeRect.Height - rs.Height) / 2f);
                    using (var cp = new Pen(Muted, Math.Max(1.5f, s * 1.5f)))
                    {
                        float cx = rangeRect.Right - S(15), cy = rangeRect.Y + rangeRect.Height / 2f, q = S(4);
                        g.DrawLines(cp, new[] { new PointF(cx - q, cy - q / 2), new PointF(cx, cy + q / 2), new PointF(cx + q, cy - q / 2) });
                    }
                    g.DrawString("used in the " + info.RangeLabel.ToLower() + "  ·  no data limit set", fSmall, bMuted, pad + S(13), y + S(50));
                }
                y += cardH + S(12);
            }

            // Detail rows
            var rows = new List<KeyValuePair<string, string>>();
            if (info.HasLimit)
            {
                rows.Add(new KeyValuePair<string, string>("Data limit", Fmt((long)info.LimitBytes) + "  (" + info.LimitType.ToLower() + ")"));
                rows.Add(new KeyValuePair<string, string>("Period started", info.Start.ToString("ddd d MMM, h:mm tt")));
                if (info.Recurring) rows.Add(new KeyValuePair<string, string>("Resets", info.ResetPending ? "Due now" : info.End.ToString("ddd d MMM, h:mm tt")));
            }
            rows.Add(new KeyValuePair<string, string>("Downloaded", Fmt(info.Recv)));
            rows.Add(new KeyValuePair<string, string>("Uploaded", Fmt(info.Sent)));
            foreach (var r in rows)
            {
                if (draw)
                {
                    g.DrawString(r.Key, fBody, bMuted, pad, y);
                    g.DrawString(r.Value, fBody, bFg, new RectangleF(pad, y, w, S(20)), right);
                }
                y += S(22);
            }

            // Per-app usage
            if (info.Apps.Count > 0)
            {
                y += S(8);
                if (draw) g.DrawString(info.HasLimit ? "Top apps this period" : "Top apps, " + info.RangeLabel.ToLower(), fBold, bFg, pad - S(1), y);
                y += S(24);
                long max = Math.Max(1, info.Apps[0].Value);
                foreach (var a in info.Apps.Take(MaxApps))
                {
                    if (draw)
                    {
                        g.DrawString(a.Key, fSmall, bFg, new RectangleF(pad, y, w - S(80), S(18)), ellip);
                        g.DrawString(Fmt(a.Value), fSmall, bMuted, new RectangleF(pad, y, w, S(18)), right);
                        var tr = new RectangleF(pad + S(2), y + S(18), w - S(4), S(3));
                        g.FillRectangle(bTrack, tr);
                        using (var bb = new SolidBrush(Blue)) g.FillRectangle(bb, tr.X, tr.Y, (float)(tr.Width * a.Value / (double)max), tr.Height);
                    }
                    y += S(27);
                }
                if (info.Apps.Count > MaxApps)
                {
                    if (draw) g.DrawString("+ " + (info.Apps.Count - MaxApps) + " more", fSmall, bMuted, pad, y);
                    y += S(20);
                }
            }

            y += S(4);
            if (draw) g.DrawString("Updated " + info.Updated.ToString("h:mm:ss tt") + "  ·  refreshes every minute", fSmall, bMuted, pad, y);
            y += S(24);
            {
                string lbl = "Start with Windows (in tray)";
                SizeF ls = g.MeasureString(lbl, fSmall);
                int bx = pad + S(1), bs = S(14);
                autoRect = new Rectangle(pad, y - S(3), bs + S(10) + (int)ls.Width, S(22));
                if (draw)
                {
                    var box = new RectangleF(bx, y + S(1), bs, bs);
                    using (var bp = Round(box, S(3)))
                    {
                        if (autoStart)
                        {
                            using (var bb = new SolidBrush(Blue)) g.FillPath(bb, bp);
                            using (var ck = new Pen(Bg, Math.Max(1.5f, s * 1.6f)))
                                g.DrawLines(ck, new[] { new PointF(box.X + bs * 0.24f, box.Y + bs * 0.52f), new PointF(box.X + bs * 0.43f, box.Y + bs * 0.70f), new PointF(box.X + bs * 0.77f, box.Y + bs * 0.30f) });
                        }
                        else using (var op = new Pen(Muted, Math.Max(1f, s))) g.DrawPath(op, bp);
                    }
                    g.DrawString(lbl, fSmall, autoStart ? bFg : bMuted, bx + bs + S(6), y);
                }
            }
            y += S(30);
        }
        return y;
    }
}

static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main(string[] args)
    {
        try { SetProcessDPIAware(); } catch { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var f = new MainForm();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--shot" && i + 1 < args.Length) f.ShotPath = args[++i];
            else if (args[i] == "--tray") f.StartInTray = true; // start hidden in the tray
            else if (args[i] == "--nolimit") MainForm.ForceNoLimit = true; // preview the no-limit view
        }
        Application.Run(f);
    }
}
