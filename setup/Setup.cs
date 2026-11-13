// Nexo installer + uninstaller in one exe. Nexo.exe is embedded as a resource. Per-user install, no admin needed.
//   Nexo-Setup.exe                 install (window)        /S silent   /dir "path" custom folder   /noautostart
//   Desinstalar.exe (same program) uninstall (window)      /S silent (keeps your settings)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Instalador de Nexo")]
[assembly: AssemblyProduct("Nexo")]
[assembly: AssemblyCompany("Nexo")]
[assembly: AssemblyCopyright("Copyright © 2026 Nexo")]
[assembly: AssemblyVersion("1.0.3.0")]
[assembly: AssemblyFileVersion("1.0.3.0")]

// ---------- the actual work ----------

static class Setup
{
    public const string Version = "1.0.3";
    const string UninstKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Nexo";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool MoveFileEx(string from, string to, int flags);

    public static string DefaultDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Nexo"); } }
    static string Lnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Nexo.lnk"); } }
    public static string DataDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nexo"); } }

    [STAThread]
    static void Main(string[] a)
    {
        bool silent = Array.IndexOf(a, "/S") >= 0, un = Array.IndexOf(a, "/uninstall") >= 0;
        string dir = DefaultDir;
        int i = Array.IndexOf(a, "/dir"); if (i >= 0 && i + 1 < a.Length) dir = a[i + 1];
        try
        {
            if (silent)
            {
                if (un) { StopNexo(); RemoveRun(); RemoveShortcut(); RemoveUninstallKey(); RemoveFiles(Path.GetDirectoryName(Application.ExecutablePath)); }
                else { StopNexo(); string exe = CopyFiles(dir); MakeShortcut(exe, dir); WriteUninstallKey(exe, dir); SetRun(exe, Array.IndexOf(a, "/noautostart") < 0); }
                return;
            }
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) T.K = g.DpiX / 96f;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            int sh = Array.IndexOf(a, "/shot");
            if (sh >= 0 && sh + 1 < a.Length) { new Wizard(un, dir).Preview(a[sh + 1], sh + 2 < a.Length ? a[sh + 2] : "a"); return; }
            Application.Run(new Wizard(un, dir));
        }
        catch (Exception e) { if (!silent) MessageBox.Show(e.Message, "Nexo", MessageBoxButtons.OK, MessageBoxIcon.Error); Environment.ExitCode = 1; }
    }

    public static void StopNexo()
    {
        foreach (var n in new[] { "Nexo", "StartMenuX" })
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(n)) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
    }

    public static bool NexoRunning() { return System.Diagnostics.Process.GetProcessesByName("Nexo").Length > 0; }

    public static string CopyFiles(string dir)
    {
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, "Nexo.exe");
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Nexo.exe"))
        using (var o = File.Create(exe)) s.CopyTo(o);
        string un = Path.Combine(dir, "Desinstalar.exe"), me = Application.ExecutablePath;
        if (!string.Equals(me, un, StringComparison.OrdinalIgnoreCase)) File.Copy(me, un, true);
        if (!File.Exists(exe) || new FileInfo(exe).Length == 0) throw new IOException("No se pudo escribir Nexo.exe (¿lo ha bloqueado el antivirus?)");
        return exe;
    }

    public static void MakeShortcut(string exe, string dir)
    {
        var t = Type.GetTypeFromProgID("WScript.Shell"); object sh = Activator.CreateInstance(t);
        object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { Lnk });
        Action<string, object> set = (n, v) => sc.GetType().InvokeMember(n, BindingFlags.SetProperty, null, sc, new[] { v });
        set("TargetPath", exe); set("WorkingDirectory", dir); set("Description", "Nexo"); set("IconLocation", exe + ",0");
        sc.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        if (!File.Exists(Lnk)) throw new IOException("No se pudo crear el acceso directo");
    }

    public static void WriteUninstallKey(string exe, string dir)
    {
        string un = Path.Combine(dir, "Desinstalar.exe");
        using (var k = Registry.CurrentUser.CreateSubKey(UninstKey))
        {
            k.SetValue("DisplayName", "Nexo"); k.SetValue("DisplayVersion", Version); k.SetValue("Publisher", "Nexo");
            k.SetValue("DisplayIcon", exe); k.SetValue("InstallLocation", dir);
            k.SetValue("UninstallString", "\"" + un + "\" /uninstall"); k.SetValue("NoModify", 1); k.SetValue("NoRepair", 1);
        }
        using (var k = Registry.CurrentUser.OpenSubKey(UninstKey)) if (k == null || k.GetValue("DisplayName") == null) throw new IOException("No se pudo registrar Nexo");
    }

    public static void SetRun(string exe, bool on)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            k.DeleteValue("StartMenuX", false);
            if (on) k.SetValue("Nexo", "\"" + exe + "\""); else k.DeleteValue("Nexo", false);
        }
    }

    public static void RemoveRun() { using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (k != null) k.DeleteValue("Nexo", false); }
    public static void RemoveShortcut() { try { File.Delete(Lnk); } catch { } }
    public static void RemoveUninstallKey() { Registry.CurrentUser.DeleteSubKeyTree(UninstKey, false); }

    // This exe runs from the folder it must delete: everything else goes now, it and the folder at the next restart.
    public static void RemoveFiles(string dir)
    {
        foreach (var f in Directory.GetFiles(dir)) if (!string.Equals(f, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase)) try { File.Delete(f); } catch { }
        MoveFileEx(Application.ExecutablePath, null, 4); MoveFileEx(dir, null, 4); // 4 = MOVEFILE_DELAY_UNTIL_REBOOT; the folder goes once empty
    }

    public static void RemoveData() { try { Directory.Delete(DataDir, true); } catch { } }
}

// ---------- look: one neutral ramp, one accent, text sizes from a short scale ----------

static class T
{
    public static float K = 1; // DPI scale
    public static readonly Color Bg = Color.FromArgb(20, 20, 21), Card = Color.FromArgb(25, 25, 27), Raised = Color.FromArgb(35, 35, 37),
        Hair = Color.FromArgb(46, 46, 49), Text = Color.FromArgb(237, 237, 237), Text2 = Color.FromArgb(161, 161, 166), Muted = Color.FromArgb(133, 133, 139),
        Accent = Color.FromArgb(79, 140, 255), Bad = Color.FromArgb(229, 72, 77);
    static string fam;

    public static int D(int v) { return (int)Math.Round(v * K); }

    public static Font F(float px)
    {
        if (fam == null) { fam = "Segoe UI"; foreach (var f in FontFamily.Families) if (f.Name == "Segoe UI Variable Text") fam = f.Name; }
        return new Font(fam, px * K, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    public static GraphicsPath RR(RectangleF r, float rad)
    {
        float d = rad * 2; var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
        return p;
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
    // dark title bar in the page colour (Windows 11; ignored where unsupported)
    public static void DarkFrame(IntPtr h)
    {
        int on = 1, cap = Bg.R | (Bg.G << 8) | (Bg.B << 16), txt = Text.R | (Text.G << 8) | (Text.B << 16);
        DwmSetWindowAttribute(h, 20, ref on, 4); DwmSetWindowAttribute(h, 35, ref cap, 4); DwmSetWindowAttribute(h, 36, ref txt, 4);
    }
}

class Btn : Control
{
    public bool Primary; bool hot, down;
    public Btn(string text, bool primary)
    {
        Text = text; Primary = primary; Cursor = Cursors.Hand; Font = T.F(14); Size = new Size(T.D(112), T.D(40));
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.StandardClick, true);
    }
    protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hot = down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(T.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
        Color fill = Primary ? (down ? Color.FromArgb(205, 205, 205) : hot ? Color.FromArgb(250, 250, 250) : T.Text) : (down ? Color.FromArgb(30, 30, 32) : hot ? Color.FromArgb(46, 46, 49) : T.Raised);
        Color ink = Primary ? T.Bg : T.Text;
        if (!Enabled) { fill = T.Raised; ink = T.Muted; }
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var p = T.RR(r, T.D(10))) { using (var b = new SolidBrush(fill)) g.FillPath(b, p); if (!Primary) using (var pen = new Pen(T.Hair)) g.DrawPath(pen, p); }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

class Chk : Control
{
    public bool Checked = true;
    public Chk(string text)
    {
        Text = text; Cursor = Cursors.Hand; Font = T.F(14); Height = T.D(24);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.StandardClick, true);
    }
    protected override void OnClick(EventArgs e) { Checked = !Checked; Invalidate(); base.OnClick(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Parent.BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
        int s = T.D(20), y = (Height - s) / 2;
        var r = new RectangleF(0.5f, y + 0.5f, s - 1, s - 1);
        using (var p = T.RR(r, T.D(6)))
        {
            using (var b = new SolidBrush(Checked ? T.Accent : T.Raised)) g.FillPath(b, p);
            if (!Checked) using (var pen = new Pen(T.Hair)) g.DrawPath(pen, p);
        }
        if (Checked)
            using (var pen = new Pen(T.Bg, 2f * T.K) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, new[] { new PointF(s * 0.27f, y + s * 0.52f), new PointF(s * 0.43f, y + s * 0.68f), new PointF(s * 0.74f, y + s * 0.34f) });
        TextRenderer.DrawText(g, Text, Font, new Rectangle(s + T.D(12), 0, Width - s - T.D(12), Height), T.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

class Bar : Control
{
    public float Target, Value; readonly System.Windows.Forms.Timer tm = new System.Windows.Forms.Timer { Interval = 16 };
    public Bar()
    {
        Height = T.D(6); SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        tm.Tick += delegate { if (Math.Abs(Target - Value) < 0.002f) { Value = Target; return; } Value += (Target - Value) * 0.14f; Invalidate(); };
        tm.Start();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Parent.BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
        float rad = Height / 2f;
        using (var p = T.RR(new RectangleF(0, 0, Width, Height), rad)) using (var b = new SolidBrush(T.Raised)) g.FillPath(b, p);
        float w = Math.Max(Height, Width * Math.Min(1f, Value));
        if (Value > 0.001f) using (var p = T.RR(new RectangleF(0, 0, w, Height), rad)) using (var b = new SolidBrush(T.Accent)) g.FillPath(b, p);
    }
}

class Step { public string Label, Done, Detail = ""; public int State; } // State: 0 pending, 1 active, 2 done, 3 failed

class StepList : Control
{
    public readonly List<Step> Items = new List<Step>(); public bool Finished;
    int tick; readonly System.Windows.Forms.Timer tm = new System.Windows.Forms.Timer { Interval = 40 };
    public int RowH { get { return T.D(Finished ? 48 : 36); } }
    public StepList()
    {
        Font = T.F(14); SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        tm.Tick += delegate { tick++; if (Items.Any(i => i.State == 1)) Invalidate(); };
        tm.Start();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Parent.BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
        int rh = RowH, s = T.D(20);
        using (var small = T.F(12))
            for (int i = 0; i < Items.Count; i++)
            {
                var it = Items[i]; int y = i * rh, cy = y + rh / 2, top = cy - s / 2;
                var c = new RectangleF(1, top + 1, s - 2, s - 2);
                if (it.State == 2 || it.State == 3)
                {
                    using (var b = new SolidBrush(it.State == 2 ? T.Accent : T.Bad)) g.FillEllipse(b, c);
                    using (var pen = new Pen(T.Bg, 2f * T.K) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    {
                        if (it.State == 2) g.DrawLines(pen, new[] { new PointF(s * 0.28f, top + s * 0.52f), new PointF(s * 0.44f, top + s * 0.68f), new PointF(s * 0.74f, top + s * 0.34f) });
                        else { g.DrawLine(pen, s * 0.34f, top + s * 0.34f, s * 0.66f, top + s * 0.66f); g.DrawLine(pen, s * 0.66f, top + s * 0.34f, s * 0.34f, top + s * 0.66f); }
                    }
                }
                else
                {
                    using (var pen = new Pen(T.Hair, 2f * T.K)) g.DrawEllipse(pen, c);
                    if (it.State == 1) using (var pen = new Pen(T.Accent, 2f * T.K) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(pen, c, (tick * 14) % 360, 100);
                }
                Color ink = it.State == 0 ? T.Muted : T.Text;
                string label = Finished && it.Done != null && it.State == 2 ? it.Done : it.Label;
                int x = s + T.D(12), w = Width - x;
                if (Finished)
                {
                    TextRenderer.DrawText(g, label, Font, new Rectangle(x, y + T.D(5), w, T.D(22)), ink, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(g, it.Detail, small, new Rectangle(x, y + T.D(25), w, T.D(18)), T.Muted, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.PathEllipsis);
                }
                else TextRenderer.DrawText(g, label, Font, new Rectangle(x, y, w, rh), ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
    }
}

// ---------- the window: welcome page, then progress that turns into the result ----------

class Wizard : Form
{
    readonly bool un; readonly string dir;
    readonly Panel pa = new Panel(), pb = new Panel();
    readonly Chk opt; readonly Label title2, sub2; readonly StepList list = new StepList(); readonly Bar bar = new Bar();
    readonly Btn cancel, main, close;
    bool busy;

    Label L(Control parent, string text, float px, Color c, int x, int y, int w)
    {
        var l = new Label { Text = text, Font = T.F(px), ForeColor = c, BackColor = T.Bg, AutoSize = false, Left = T.D(x), Top = T.D(y), Width = T.D(w), UseMnemonic = false };
        l.Height = TextRenderer.MeasureText(text, l.Font, new Size(l.Width, 2000), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height + T.D(2);
        parent.Controls.Add(l); return l;
    }

    public Wizard(bool uninstall, string dir)
    {
        un = uninstall; this.dir = un ? Path.GetDirectoryName(Application.ExecutablePath) : dir;
        Text = un ? "Desinstalar Nexo" : "Instalar Nexo";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        BackColor = T.Bg; ForeColor = T.Text; Font = T.F(14); KeyPreview = true; DoubleBuffered = true;
        ClientSize = new Size(T.D(560), T.D(500));
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        foreach (var p in new[] { pa, pb }) { p.Dock = DockStyle.Fill; p.BackColor = T.Bg; Controls.Add(p); }

        // page A: what will happen
        var logo = new PictureBox { Left = T.D(40), Top = T.D(40), Width = T.D(72), Height = T.D(72), BackColor = T.Bg };
        try
        {
            // the .ico holds PNG frames: pick the 128 px one and decode it directly
            byte[] ico; using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("nexo.ico")) using (var ms = new MemoryStream()) { s.CopyTo(ms); ico = ms.ToArray(); }
            int n = BitConverter.ToUInt16(ico, 4);
            for (int k = 0; k < n; k++)
            {
                int e0 = 6 + 16 * k;
                if (ico[e0] != 128) continue;
                int len = BitConverter.ToInt32(ico, e0 + 8), off = BitConverter.ToInt32(ico, e0 + 12);
                using (var src = new Bitmap(new MemoryStream(ico, off, len)))
                {
                    var bmp = new Bitmap(logo.Width, logo.Height);
                    using (var g = Graphics.FromImage(bmp)) { g.Clear(T.Bg); g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(src, 0, 0, bmp.Width, bmp.Height); }
                    logo.Image = bmp;
                }
            }
        }
        catch { }
        pa.Controls.Add(logo);
        L(pa, un ? "Desinstalar Nexo" : "Nexo", 28, T.Text, 128, 40, 392);
        L(pa, un ? "Se quitará Nexo de este equipo." : "Menú de inicio personal para Windows", 14, T.Text2, 128, 82, 392);
        L(pa, un ? "Versión " + Setup.Version : "Versión " + Setup.Version + "  ·  solo para tu usuario", 12, T.Muted, 128, 108, 392);

        var card = new Panel { Left = T.D(40), Top = T.D(152), Width = T.D(480), Height = T.D(un ? 120 : 124), BackColor = T.Card };
        card.Paint += (s, e) => { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using (var p = T.RR(new RectangleF(0.5f, 0.5f, card.Width - 1, card.Height - 1), T.D(12))) using (var pen = new Pen(T.Hair)) e.Graphics.DrawPath(pen, p); };
        pa.Controls.Add(card);
        if (un)
        {
            opt = new Chk("Eliminar también mis ajustes y fijados") { Left = T.D(20), Top = T.D(20), Width = T.D(440), Checked = false }; opt.Parent = card;
            card.Controls.Add(opt); opt.BackColor = T.Card;
            L(card, "Si no lo marcas, se conservan por si vuelves a instalar Nexo.", 12, T.Muted, 20, 56, 440).BackColor = T.Card;
            L(card, "Se cerrará Nexo, se quitará su inicio con Windows y su acceso directo.", 12, T.Muted, 20, 84, 440).BackColor = T.Card;
        }
        else
        {
            L(card, "Ubicación", 12, T.Muted, 20, 16, 440).BackColor = T.Card;
            var path = L(card, this.dir, 14, T.Text, 20, 36, 440); path.BackColor = T.Card; path.AutoEllipsis = true; path.Height = T.D(22);
            var line = new Panel { Left = T.D(20), Top = T.D(72), Width = T.D(440), Height = 1, BackColor = T.Hair }; card.Controls.Add(line);
            opt = new Chk("Iniciar Nexo con Windows") { Left = T.D(20), Top = T.D(84), Width = T.D(440), Checked = true, BackColor = T.Card }; card.Controls.Add(opt);
        }
        L(pa, un ? "No hace falta ningún permiso de administrador." : "No hace falta ningún permiso de administrador. Puedes desinstalarlo desde Aplicaciones instaladas.", 12, T.Muted, 40, 292, 480);

        cancel = new Btn("Cancelar", false) { Left = T.D(288), Top = T.D(420) }; cancel.Click += delegate { Close(); }; pa.Controls.Add(cancel);
        main = new Btn(un ? "Desinstalar" : "Instalar", true) { Left = T.D(408), Top = T.D(420) }; main.Click += delegate { Begin(); }; pa.Controls.Add(main);

        // page B: progress, then result
        title2 = L(pb, "", 24, T.Text, 40, 40, 480);
        sub2 = L(pb, "", 14, T.Text2, 40, 76, 480);
        title2.Height = T.D(32); sub2.Height = T.D(40); // their text changes later: fixed boxes, room for two lines
        bar.Left = T.D(40); bar.Top = T.D(124); bar.Width = T.D(480); pb.Controls.Add(bar);
        list.Left = T.D(40); list.Top = T.D(148); list.Width = T.D(480); list.Height = T.D(6 * 48); pb.Controls.Add(list);
        close = new Btn("Cerrar", true) { Left = T.D(408), Top = T.D(420), Visible = false }; close.Click += delegate { Close(); }; pb.Controls.Add(close);

        pb.Visible = false;
        HandleCreated += delegate { T.DarkFrame(Handle); };
        FormClosing += (s, e) => { if (busy) e.Cancel = true; };
        KeyDown += (s, e) =>
        {
            if (busy) return;
            if (e.KeyCode == Keys.Escape) Close();
            else if (e.KeyCode == Keys.Enter) { if (pa.Visible) Begin(); else if (close.Visible) Close(); }
        };
    }

    void Ui(Action a) { try { BeginInvoke(a); } catch { } }

    void Set(int i, int state, string detail)
    {
        Ui(delegate
        {
            list.Items[i].State = state; if (detail != null) list.Items[i].Detail = detail;
            bar.Target = list.Items.Count(x => x.State >= 2) / (float)list.Items.Count; list.Invalidate();
        });
    }

    static void Beat() { System.Threading.Thread.Sleep(280); } // long enough to read each step

    void Begin()
    {
        busy = true; bool autostart = opt.Checked, wipe = opt.Checked;
        string exe = Path.Combine(dir, "Nexo.exe");
        Prepare(autostart);
        var t = new System.Threading.Thread(delegate() { if (un) DoUninstall(wipe); else DoInstall(exe, autostart); }) { IsBackground = true };
        t.Start();
    }

    // test hook: /shot file.png [a|run|done|fail] renders a page to an image without touching the system
    public void Preview(string file, string mode)
    {
        var h = Handle;
        if (mode != "a")
        {
            Prepare(true);
            var details = un ? new[] { "", "", "", "", "El desinstalador y su carpeta se borran al reiniciar", "Siguen en %AppData%\\Nexo" }
                             : new[] { "No había nada abierto que cerrar", dir, "Menú Inicio › Nexo", "Ajustes › Aplicaciones › Aplicaciones instaladas", "Se abrirá al iniciar sesión", "Pulsa la tecla Windows para abrirlo" };
            for (int i = 0; i < list.Items.Count; i++) { list.Items[i].State = mode == "run" ? (i < 3 ? 2 : i == 3 ? 1 : 0) : 2; list.Items[i].Detail = details[i]; }
            if (mode == "fail") { list.Items[5].State = 3; list.Items[5].Detail = "Se cerró nada más abrir; puede que el antivirus lo haya bloqueado"; }
            bar.Value = bar.Target = mode == "run" ? 0.5f : 1;
            if (mode == "done") ApplyFinish(true, un ? "Nexo se ha desinstalado" : "Nexo está instalado", un ? "Ya no queda nada en el equipo salvo lo que ves abajo." : "Todo está en su sitio. Su icono aparece junto al reloj.");
            if (mode == "fail") ApplyFinish(false, "Nexo está copiado, pero no se pudo abrir", "Añade la carpeta de instalación a las exclusiones de tu antivirus y ábrelo desde el Menú Inicio.");
        }
        Show(); Application.DoEvents();
        using (var bmp = new Bitmap(ClientSize.Width, ClientSize.Height)) { DrawToBitmap(bmp, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height)); bmp.Save(file); }
    }

    void Prepare(bool autostart)
    {
        pa.Visible = false; pb.Visible = true;
        title2.Text = un ? "Desinstalando Nexo" : "Instalando Nexo"; sub2.Text = "Solo tarda unos segundos";
        list.Items.Clear();
        if (un)
        {
            list.Items.AddRange(new[] {
                new Step { Label = "Cerrar Nexo", Done = "Nexo cerrado" },
                new Step { Label = "Quitar el inicio con Windows", Done = "Inicio con Windows quitado" },
                new Step { Label = "Quitar el acceso directo", Done = "Acceso directo quitado" },
                new Step { Label = "Quitar de Aplicaciones instaladas", Done = "Quitado de Aplicaciones instaladas" },
                new Step { Label = "Eliminar archivos", Done = "Archivos eliminados" },
                new Step { Label = "Ajustes y fijados", Done = opt.Checked ? "Ajustes y fijados eliminados" : "Ajustes y fijados conservados" } });
        }
        else
        {
            list.Items.AddRange(new[] {
                new Step { Label = "Cerrar Nexo si está abierto", Done = "Listo para instalar" },
                new Step { Label = "Copiar archivos", Done = "Archivos copiados" },
                new Step { Label = "Crear el acceso directo", Done = "Acceso directo creado" },
                new Step { Label = "Registrar en Aplicaciones instaladas", Done = "Registrado en Aplicaciones instaladas" },
                new Step { Label = "Inicio con Windows", Done = autostart ? "Inicio con Windows activado" : "Inicio con Windows desactivado" },
                new Step { Label = "Abrir Nexo", Done = "Nexo en ejecución" } });
        }
    }

    void DoInstall(string exe, bool autostart)
    {
        int i = 0;
        try
        {
            Set(0, 1, null); Setup.StopNexo(); Beat(); Set(0, 2, "No había nada abierto que cerrar");
            i = 1; Set(1, 1, null); Setup.CopyFiles(dir); Beat(); Set(1, 2, dir);
            i = 2; Set(2, 1, null); Setup.MakeShortcut(exe, dir); Beat(); Set(2, 2, "Menú Inicio › Nexo");
            i = 3; Set(3, 1, null); Setup.WriteUninstallKey(exe, dir); Beat(); Set(3, 2, "Ajustes › Aplicaciones › Aplicaciones instaladas");
            i = 4; Set(4, 1, null); Setup.SetRun(exe, autostart); Beat(); Set(4, 2, autostart ? "Se abrirá al iniciar sesión" : "Solo se abrirá cuando tú lo abras");
            i = 5; Set(5, 1, null);
            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = dir });
            System.Threading.Thread.Sleep(2000); // a blocked or crashing program would be gone by now
            if (!Setup.NexoRunning()) { Set(5, 3, "Se cerró nada más abrir; puede que el antivirus lo haya bloqueado"); Finish(false, "Nexo está copiado, pero no se pudo abrir", "Añade la carpeta de instalación a las exclusiones de tu antivirus y ábrelo desde el Menú Inicio."); return; }
            Set(5, 2, "Pulsa la tecla Windows para abrirlo");
            Finish(true, "Nexo está instalado", "Todo está en su sitio. Su icono aparece junto al reloj.");
        }
        catch (Exception e) { Set(i, 3, e.Message); Finish(false, "No se pudo completar la instalación", "Nada más se ha modificado. Puedes volver a intentarlo."); }
    }

    void DoUninstall(bool wipe)
    {
        int i = 0;
        try
        {
            Set(0, 1, null); Setup.StopNexo(); Beat(); Set(0, 2, "");
            i = 1; Set(1, 1, null); Setup.RemoveRun(); Beat(); Set(1, 2, "");
            i = 2; Set(2, 1, null); Setup.RemoveShortcut(); Beat(); Set(2, 2, "");
            i = 3; Set(3, 1, null); Setup.RemoveUninstallKey(); Beat(); Set(3, 2, "");
            i = 4; Set(4, 1, null); Setup.RemoveFiles(dir); Beat(); Set(4, 2, "El desinstalador y su carpeta se borran al reiniciar");
            i = 5; Set(5, 1, null); if (wipe) Setup.RemoveData(); Beat(); Set(5, 2, wipe ? "" : "Siguen en " + Setup.DataDir);
            Finish(true, "Nexo se ha desinstalado", "Ya no queda nada en el equipo salvo lo que ves abajo.");
        }
        catch (Exception e) { Set(i, 3, e.Message); Finish(false, "No se pudo completar la desinstalación", "Puedes volver a intentarlo."); }
    }

    void Finish(bool ok, string title, string sub) { Ui(delegate { ApplyFinish(ok, title, sub); }); }

    void ApplyFinish(bool ok, string title, string sub)
    {
        busy = false; title2.Text = title; sub2.Text = sub; list.Finished = true; bar.Visible = false;
        list.Top = T.D(124); list.Invalidate(); close.Visible = true; close.Focus();
    }
}
