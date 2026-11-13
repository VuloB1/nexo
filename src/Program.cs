using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WF = System.Windows.Forms;
using SD = System.Drawing;

[assembly: AssemblyTitle("Nexo")]
[assembly: AssemblyProduct("Nexo")]
[assembly: AssemblyDescription("Menú de inicio personalizado para Windows")]
[assembly: AssemblyCompany("Nexo")]
[assembly: AssemblyCopyright("Copyright © 2026 Nexo")]
[assembly: AssemblyVersion("1.0.3.0")]
[assembly: AssemblyFileVersion("1.0.3.0")]

namespace SMX
{
    // ---------- model ----------

    [DataContract]
    public class Node : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        void Raise(string p) { var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(p)); }

        string name; ObservableCollection<Node> items; ImageSource icon; int dropMode;
        public string Key = "", Initials = "";

        [DataMember]
        public string Name
        {
            get { return name; }
            set { name = value; Key = Util.Norm(value); Initials = Util.Initials(Key); Raise("Name"); Raise("LabelWrap"); }
        }
        // A single long word (Configuración) must not break mid-word at large text: it gets an ellipsis instead.
        public TextWrapping LabelWrap { get { return name != null && name.IndexOf(' ') >= 0 ? TextWrapping.Wrap : TextWrapping.NoWrap; } }
        [DataMember] public string Id { get; set; }
        [DataMember(EmitDefaultValue = false)]
        public ObservableCollection<Node> Items
        {
            get { return items; }
            set { items = value; if (items != null) items.CollectionChanged += delegate { Raise("Preview"); }; }
        }
        public bool IsFolder { get { return items != null; } }
        public bool IsHeader { get; set; }
        public string Uri; // set for search results that are not apps (a Windows setting or a file): what to open
        bool selected;
        public bool Selected { get { return selected; } set { if (selected != value) { selected = value; Raise("Selected"); } } } // keyboard highlight
        public int DropMode { get { return dropMode; } set { if (dropMode != value) { dropMode = value; Raise("DropMode"); } } }
        public IEnumerable<Node> Preview { get { return items == null ? null : items.Take(4).ToList(); } }
        public ImageSource Icon
        {
            get { if (icon == null && !IsHeader && items == null && Id != null) Icons.Request(this); return icon; }
            set { icon = value; Raise("Icon"); }
        }
        public void SetIconQuiet(ImageSource i) { icon = i; }
    }

    // A floating copy of the tile being dragged: tilted a little, slightly bigger, with a shadow. It never takes focus or mouse input.
    class DragGhost
    {
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }

        readonly Window w; readonly Matrix toDip; readonly double halfW, halfH;

        public DragGhost(FrameworkElement tile, Matrix toDip)
        {
            this.toDip = toDip; double sx = 1 / toDip.M11, pad = 18;
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(tile.ActualWidth * sx), (int)Math.Ceiling(tile.ActualHeight * sx), 96 * sx, 96 * sx, PixelFormats.Pbgra32);
            rtb.Render(tile);
            var img = new Image { Source = rtb, Width = tile.ActualWidth, Height = tile.ActualHeight, Opacity = 0.94, RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new TransformGroup { Children = { new ScaleTransform(1.06, 1.06), new RotateTransform(3) } },
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 6, Opacity = 0.5 } };
            halfW = tile.ActualWidth / 2 + pad; halfH = tile.ActualHeight / 2 + pad;
            w = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, Topmost = true, ShowActivated = false,
                Focusable = false, IsHitTestVisible = false, SizeToContent = SizeToContent.WidthAndHeight, Content = new Border { Padding = new Thickness(pad), Child = img }, Opacity = 0 };
            w.SourceInitialized += delegate
            {
                var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x08000000 | 0x80 | 0x20); // no-activate, tool window, click-through
            };
            Move(); w.Show();
            w.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        }

        public void Move()
        {
            POINT p; if (!GetCursorPos(out p)) return;
            var d = toDip.Transform(new Point(p.x, p.y));
            w.Left = d.X - halfW; w.Top = d.Y - halfH;
        }

        public void Close() { w.Close(); }
    }

    static class Util
    {
        public static string Norm(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            foreach (char c in s.Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }
        public static string Initials(string key)
        {
            var sb = new StringBuilder();
            foreach (var w in key.Split(' ', '-', '.')) if (w.Length > 0) sb.Append(w[0]);
            return sb.ToString();
        }
    }

    // ---------- shell interop: app list + icons ----------

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attribs);
        void Compare(IShellItem psi, uint hint, out int order);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumShellItems
    {
        [PreserveSig] int Next(uint celt, out IShellItem item, out uint fetched);
        void Skip(uint celt);
        void Reset();
        void Clone(out IEnumShellItems e);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        void GetImage(SIZE size, int flags, out IntPtr hbm);
    }

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

    static class Shell
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

        static IShellItem Item(string path)
        {
            Guid g = typeof(IShellItem).GUID; IShellItem si;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref g, out si);
            return si;
        }

        // Every app Windows itself lists in "All apps" (Win32 + Store), flat.
        public static List<Node> ScanApps()
        {
            var res = new List<Node>();
            var folder = Item("shell:::{4234d49b-0245-4df3-b780-3893943456e1}");
            Guid bhid = new Guid("94f60519-2850-4924-aa5a-d15e84868039"), riid = typeof(IEnumShellItems).GUID;
            IntPtr p; folder.BindToHandler(IntPtr.Zero, ref bhid, ref riid, out p);
            var en = (IEnumShellItems)Marshal.GetObjectForIUnknown(p); Marshal.Release(p);
            IShellItem it; uint n;
            while (en.Next(1, out it, out n) == 0 && n == 1)
            {
                string name, id;
                it.GetDisplayName(0, out name);
                it.GetDisplayName(0x80018001, out id); // PARENTRELATIVEPARSING = AppUserModelID / path
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(id)) res.Add(new Node { Name = name, Id = id });
            }
            return res;
        }

        public static ImageSource LoadIcon(string id)
        {
            var f = (IShellItemImageFactory)Item(id.StartsWith("file:") ? id.Substring(5) : "shell:AppsFolder\\" + id);
            IntPtr hbm; f.GetImage(new SIZE { cx = 64, cy = 64 }, 0x4 /*ICONONLY*/, out hbm);
            try
            {
                var bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bs.Freeze(); return bs;
            }
            finally { DeleteObject(hbm); }
        }
    }

    // Icons load on one low-priority STA thread, only for items that actually get shown.
    static class Icons
    {
        static readonly Dictionary<string, ImageSource> cache = new Dictionary<string, ImageSource>();
        static readonly Dictionary<string, List<Node>> waiting = new Dictionary<string, List<Node>>();
        static readonly HashSet<string> failed = new HashSet<string>();
        static readonly BlockingCollection<string> q = new BlockingCollection<string>();
        static Dispatcher ui;

        public static void Start(Dispatcher d)
        {
            ui = d;
            var t = new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
            t.SetApartmentState(ApartmentState.STA); t.Start();
        }

        // Disk cache: one PNG per app, shown at once on the next start. It is only a first paint: each app's icon is re-read from the shell
        // in the background (at most every 6 hours) and replaced on screen and on disk when it changed, so an updated app never keeps its old icon.
        static readonly string cacheDir = Path.Combine(AppDir.Path, "icons");
        static readonly Dictionary<string, DateTime> checkedAt = new Dictionary<string, DateTime>(); // UI thread only
        public static Action<string, ImageSource> Changed; // an icon was replaced: the menu updates the nodes that show it

        static void Revalidate(string id)
        {
            DateTime t;
            if (id.StartsWith("file:") || (checkedAt.TryGetValue(id, out t) && DateTime.UtcNow - t < TimeSpan.FromHours(6))) return;
            checkedAt[id] = DateTime.UtcNow;
            idle.Enqueue("!" + id); q.Add(""); // "!" = only compare with a fresh copy
        }

        // "Actualizar iconos" in the tray menu: forget every cached icon so all of them are read again.
        public static void Reset()
        {
            cache.Clear(); failed.Clear(); checkedAt.Clear();
            try { foreach (var f in Directory.GetFiles(cacheDir, "*.png")) File.Delete(f); } catch { }
        }

        static byte[] Png(ImageSource img)
        {
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create((BitmapSource)img));
            using (var ms = new MemoryStream()) { enc.Save(ms); return ms.ToArray(); }
        }

        static string DiskFile(string id)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
                return Path.Combine(cacheDir, BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(id))).Replace("-", "") + ".png");
        }

        static ImageSource Disk(string id)
        {
            string f = DiskFile(id);
            if (!File.Exists(f)) return null;
            var bi = new BitmapImage();
            bi.BeginInit(); bi.UriSource = new Uri(f); bi.CacheOption = BitmapCacheOption.OnLoad; bi.EndInit(); bi.Freeze(); // OnLoad: don't keep the file locked
            return bi;
        }

        static void SaveDisk(string id, ImageSource img)
        {
            Directory.CreateDirectory(cacheDir);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create((BitmapSource)img));
            using (var fs = File.Create(DiskFile(id))) enc.Save(fs);
        }

        public static void Request(Node n)
        {
            ImageSource c;
            if (cache.TryGetValue(n.Id, out c)) { n.SetIconQuiet(c); Revalidate(n.Id); return; }
            if (failed.Contains(n.Id)) return;
            List<Node> l;
            if (!waiting.TryGetValue(n.Id, out l)) { l = new List<Node>(); waiting[n.Id] = l; q.Add(n.Id); }
            if (!l.Contains(n)) l.Add(n);
        }

        // Idle-time preload: every app's icon is decoded into memory ahead of time, so search results and "All apps" never wait for icons.
        // Requests from the screen always go first; preloading only runs when none are waiting.
        static readonly ConcurrentQueue<string> idle = new ConcurrentQueue<string>();

        public static void Prefetch(string id)
        {
            if (id == null || cache.ContainsKey(id) || waiting.ContainsKey(id) || failed.Contains(id)) return;
            idle.Enqueue(id); q.Add(""); // the empty id only wakes the worker up
        }

        // Worker thread: read the icon again; if it differs from the cached picture, replace the file and tell the menu.
        static void Refresh(string id)
        {
            try
            {
                var fresh = Shell.LoadIcon(id); byte[] now = Png(fresh);
                string f = DiskFile(id);
                if (File.Exists(f) && File.ReadAllBytes(f).SequenceEqual(now)) return;
                Directory.CreateDirectory(cacheDir); File.WriteAllBytes(f, now);
                ui.BeginInvoke(new Action(delegate { cache[id] = fresh; var c = Changed; if (c != null) c(id, fresh); }));
            }
            catch { } // shortcut: an app that cannot be read now keeps its cached icon, retried on the next check
        }

        static void Work()
        {
            for (;;)
            {
                string id;
                if (!q.TryTake(out id, idle.IsEmpty ? -1 : 0)) { if (!idle.TryDequeue(out id)) continue; }
                if (id.Length == 0) continue;
                if (id[0] == '!') { Refresh(id.Substring(1)); continue; }
                ImageSource img = null;
                bool app = !id.StartsWith("file:"); // file icons are not worth caching on disk
                try { if (app) img = Disk(id); } catch { }
                bool fromDisk = img != null;
                if (img == null) { try { img = Shell.LoadIcon(id); if (app) SaveDisk(id, img); } catch { } }
                string i = id; ImageSource im = img;
                ui.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    List<Node> l; waiting.TryGetValue(i, out l); waiting.Remove(i);
                    if (im == null) { failed.Add(i); return; }
                    cache[i] = im; checkedAt[i] = fromDisk ? DateTime.MinValue : DateTime.UtcNow;
                    if (l != null) foreach (var n in l) n.Icon = im;
                    if (fromDisk) Revalidate(i); // painted from disk: check it against the shell next
                }));
            }
        }
    }

    // ---------- Win key hook ----------

    static class Kbd
    {
        [StructLayout(LayoutKind.Sequential)] struct KBD { public uint vk, scan, flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] struct KI { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
        // x64 INPUT is 40 bytes: type + pad + 32-byte union (MOUSEINPUT is the biggest member)
        [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public KI ki; public long pad; }

        delegate IntPtr Proc(int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, Proc p, IntPtr mod, uint tid);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

        static readonly Proc proc = Hook; // kept in a field so the GC never frees the callback
        static IntPtr hook; static bool winDown, combo;
        public static Action OnWin;

        public static void Install()
        {
            // Both hooks live on their own thread with its own message loop: a busy or slow menu window can then never stall
            // keyboard / mouse input system-wide (Windows drops hooks that answer too slowly).
            var ready = new ManualResetEvent(false);
            var t = new Thread(delegate()
            {
                hook = SetWindowsHookEx(13, proc, GetModuleHandle(null), 0);
                mouseHook = SetWindowsHookEx(14, mproc, GetModuleHandle(null), 0);
                ready.Set();
                MSG m;
                while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessage(ref m); }
            }) { IsBackground = true, Name = "Nexo hooks" };
            t.Start(); ready.WaitOne(3000);
        }

        [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
        [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);

        // The mouse hook sees every click system-wide: it only swallows the taskbar Start button (see OnStartClick),
        // and reports outside clicks only while the menu is open (wantClicks).
        public static void MouseOn() { wantClicks = true; }
        public static void MouseOff() { wantClicks = false; }
        static bool wantClicks, eat;
        // True when the click landed on the taskbar Start button: it is swallowed so Windows' own menu never opens.
        public static Func<int, int, bool> OnStartClick;

        // Any mouse button press, reported with screen coordinates; the menu uses it to close on outside clicks.
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        public static Action<int, int> OnClick;
        static readonly Proc mproc = MouseHook;
        static IntPtr mouseHook;

        // An exception escaping a hook callback kills the whole process (STATUS_FATAL_USER_CALLBACK_EXCEPTION), so nothing may escape.
        static IntPtr MouseHook(int code, IntPtr w, IntPtr l)
        {
            try
            {
                int m = (int)w;
                if (code >= 0)
                {
                    if (m == 0x202 && eat) { eat = false; return (IntPtr)1; }
                    if (m == 0x201 || m == 0x204 || m == 0x207)
                    {
                        int x = Marshal.ReadInt32(l, 0), y = Marshal.ReadInt32(l, 4);
                        var s = OnStartClick;
                        if (m == 0x201 && s != null && s(x, y)) { eat = true; return (IntPtr)1; }
                        var a = OnClick; if (wantClicks && a != null) a(x, y);
                    }
                }
            }
            catch { }
            return CallNextHookEx(mouseHook, code, w, l);
        }

        static INPUT Key(ushort vk, bool up)
        {
            return new INPUT { type = 1, ki = new KI { vk = vk, flags = up ? 2u : 0u } };
        }

        static IntPtr Hook(int code, IntPtr w, IntPtr l)
        {
            try
            {
                if (code >= 0)
                {
                    var k = (KBD)Marshal.PtrToStructure(l, typeof(KBD));
                    if ((k.flags & 0x10) == 0) // ignore our own injected keys
                    {
                        int m = (int)w;
                        bool down = m == 0x100 || m == 0x104, up = m == 0x101 || m == 0x105;
                        if (k.vk == 0x5B || k.vk == 0x5C)
                        {
                            if (down) { if (!winDown) { winDown = true; combo = false; } }
                            else if (up)
                            {
                                bool solo = winDown && !combo; winDown = false;
                                if (solo)
                                {
                                    // Swallow the release and replay it behind a dummy key: Windows then
                                    // sees "Win + other key" and does not open its own Start menu.
                                    SendInput(3, new[] { Key(0xE8, false), Key(0xE8, true), Key((ushort)k.vk, true) }, Marshal.SizeOf(typeof(INPUT)));
                                    var a = OnWin; if (a != null) a();
                                    return (IntPtr)1;
                                }
                            }
                        }
                        else if (down && winDown) combo = true; // Win+E, Win+D... pass through untouched
                    }
                }
            }
            catch { }
            return CallNextHookEx(hook, code, w, l);
        }
    }

    // Per-user data folder. Earlier builds called the app StartMenuX: its folder is moved over once so nothing is lost.
    static class AppDir
    {
        public static readonly string Path = Init();
        // NEXO_USER / NEXO_DATA: only for screenshots and tests (a neutral name and an empty data folder).
        public static readonly string User = Environment.GetEnvironmentVariable("NEXO_USER") ?? Environment.UserName;
        static string Init()
        {
            string over = Environment.GetEnvironmentVariable("NEXO_DATA");
            if (!string.IsNullOrEmpty(over)) return over;
            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string now = System.IO.Path.Combine(root, "Nexo"), old = System.IO.Path.Combine(root, "StartMenuX");
            try { if (Directory.Exists(old)) { Merge(old, now); Directory.Move(old, old + ".migrated"); } } catch { } // kept, never deleted
            return now;
        }

        // Copies every file the new folder lacks (or has an older copy of); safe to repeat if a previous run was cut short.
        static void Merge(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from))
            {
                string t = System.IO.Path.Combine(to, System.IO.Path.GetFileName(f));
                if (!File.Exists(t) || File.GetLastWriteTimeUtc(f) > File.GetLastWriteTimeUtc(t)) File.Copy(f, t, true);
            }
            foreach (var d in Directory.GetDirectories(from)) Merge(d, System.IO.Path.Combine(to, System.IO.Path.GetFileName(d)));
        }
    }

    // Where the taskbar Start button is, found with UI Automation once a second (it moves as taskbar icons come and go).
    static class StartButton
    {
        static Rect[] rects = new Rect[0];
        static int busy;
        static System.Threading.Timer timer;

        [StructLayout(LayoutKind.Sequential)] struct PT { public int x, y; }
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(PT p);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);

        // The cached rectangle alone is not enough: over a full-screen game the same pixels belong to the game, and must keep their clicks.
        public static bool Hit(int x, int y)
        {
            bool inRect = false;
            foreach (var r in rects) if (r.Contains(x, y)) { inRect = true; break; }
            if (!inRect) return false;
            var top = GetAncestor(WindowFromPoint(new PT { x = x, y = y }), 2); // GA_ROOT
            var cls = new StringBuilder(64); GetClassName(top, cls, 64);
            return cls.ToString().StartsWith("Shell_", StringComparison.Ordinal) && cls.ToString().EndsWith("TrayWnd", StringComparison.Ordinal);
        }

        public static void Watch() { timer = new System.Threading.Timer(delegate { Refresh(); }, null, 0, 1000); }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

        static void Refresh()
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            try
            {
                // No polling of the taskbar while a full-screen game is in front: it is not visible, and the PC is busy enough.
                if (MenuApp.FullScreen(GetForegroundWindow())) return;
                var found = new List<Rect>();
                var A = System.Windows.Automation.AutomationElement.RootElement;
                var byId = new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.AutomationIdProperty, "StartButton");
                foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
                    foreach (System.Windows.Automation.AutomationElement bar in A.FindAll(System.Windows.Automation.TreeScope.Children,
                        new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ClassNameProperty, cls)))
                    {
                        var b = bar.FindFirst(System.Windows.Automation.TreeScope.Descendants, byId);
                        if (b != null) found.Add(b.Current.BoundingRectangle);
                    }
                rects = found.ToArray();
            }
            catch { rects = new Rect[0]; } // shortcut: if the taskbar can't be read the native Start button simply keeps working
            finally { busy = 0; }
        }
    }

    // ---------- the menu ----------

    [DataContract]
    class Prefs
    {
        [DataMember] public int Size = 2;   // icon size level 0..4
        [DataMember] public string Avatar;  // file name inside the app folder
        [DataMember] public bool HideWidgets;
        [DataMember] public string BgFile;      // copy of the chosen picture inside the app folder; null = no background
        [DataMember] public int BgBlur;         // 0..40, baked into bg.jpg
        [DataMember] public int BgDim;          // 0..80 %, black veil over the picture
        [DataMember] public int BgLum;          // 0..100 average brightness of bg.jpg: picks light or dark text
        [DataMember] public int Corners;       // 0 normal, 1 square, 2 round
        [DataMember] public int Density;        // 0 normal, 1 compact, 2 airy (gap between tiles)
        [DataMember] public int TextSz;         // 0 normal, 1 small, 2 large
        [DataMember] public int Theme;         // 0 follow Windows, 1 light, 2 dark
        [DataMember] public string Accent;      // "#RRGGBB"; null = Windows accent
        [DataMember] public int Tint;           // window tint opacity 10..100; 0 = default
        [DataMember] public int CardMul;        // card/field opacity as % of default 25..250; 0 = default
        [DataMember] public int WinSize;        // window height: 0 normal, 1 compact, 2 tall
        [DataMember] public Dictionary<string, int> Uses; // app id -> times launched; ranks search results
    }

    class MenuApp
    {
        const double W = 880;
        static readonly double[] Heights = { 840, 680, 980 };
        static readonly string[] HeightNames = { "Normal", "Compacta", "Alta" };
        // per level: tile width, tile height, icon size. Level 2 (default) fits 8 tiles per row in W.
        // Width is the same at every level (8 tiles per row, everywhere); the level changes icon size and height.
        static readonly double[][] Sizes = {
            new[] { 94.0, 70.0, 26.0 }, new[] { 94.0, 78.0, 31.0 }, new[] { 94.0, 88.0, 38.0 },
            new[] { 94.0, 100.0, 46.0 }, new[] { 94.0, 114.0, 56.0 } };
        static readonly double[] Margins = { 3, 2, 4 };                         // tile margin per density
        static readonly double[][] Radii = { new[] { 12.0, 20, 8 }, new[] { 4.0, 8, 3 }, new[] { 18.0, 28, 12 } }; // card, big surface, small per corner style
        static readonly double[] FontSizes = { 12, 11, 13 };
        static readonly string[] CornerNames = { "Normales", "Rectas", "Redondas" }, DensityNames = { "Normal", "Compacto", "Amplio" }, TextNames = { "Normal", "Pequeño", "Grande" };
        static readonly string[] SizeNames = { "Muy pequeño", "Pequeño", "Normal", "Grande", "Muy grande" };

        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int l, t, r, b; }

        [StructLayout(LayoutKind.Sequential)] struct ACCENT { public int state, flags; public uint gradient; public int animation; }
        [StructLayout(LayoutKind.Sequential)] struct WCAD { public int attr; public IntPtr data; public int size; }
        [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr h, ref WCAD d);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
        [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int l, r, t, b; }
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        // Over a full-screen game Windows keeps the taskbar hidden, and our menu swallows the Win key that would have brought it up:
        // so while the menu is open the taskbar(s) go topmost again, and are released when it closes.
        readonly List<IntPtr> raisedBars = new List<IntPtr>();

        internal static bool FullScreen(IntPtr w)
        {
            RECT r;
            if (w == IntPtr.Zero || !GetWindowRect(w, out r)) return false;
            var scr = WF.Screen.FromHandle(w).Bounds;
            return r.l <= scr.Left && r.t <= scr.Top && r.r >= scr.Right && r.b >= scr.Bottom;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int n);

        // One line per event in %AppData%\StartMenuX\debug.log: the only way to see what happens over a game.
        static readonly object logLock = new object();
        // Written from the thread pool: a slow disk (a game loading) must never delay opening the menu or a hook callback.
        void Log(string s)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff ") + s + "\r\n", f = Path.Combine(dir, "debug.log");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    lock (logLock)
                    {
                        if (File.Exists(f) && new FileInfo(f).Length > 100000) File.Delete(f);
                        File.AppendAllText(f, line);
                    }
                }
                catch { }
            });
        }

        void RaiseTaskbar()
        {
            raisedBars.Clear();
            IntPtr fg = GetForegroundWindow(); var cls = new System.Text.StringBuilder(64); GetClassName(fg, cls, 64);
            bool full = FullScreen(fg);
            Log("open: foreground=" + cls + " fullscreen=" + full);
            // The shell only re-reads a window's style (and taskbar button) while it is hidden: reopening mid-fade must hide it first,
            // otherwise a button could stay on the taskbar or the style fall out of step with the state.
            if (appWindow != full && win.IsVisible) win.Hide();
            // The shell only reveals the taskbar when a regular window (one with a taskbar button) is activated; a tool window does not count.
            SetAppWindow(full);
            if (!full) return;
            foreach (var c in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
                for (IntPtr h = FindWindowEx(IntPtr.Zero, IntPtr.Zero, c, null); h != IntPtr.Zero; h = FindWindowEx(IntPtr.Zero, h, c, null)) raisedBars.Add(h);
            PlaceBars();
            // the shell may put the bar back right after our window takes focus: assert it again a moment later
            int n = 0; var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            t.Tick += delegate { PlaceBars(); if (++n >= 3 || !shown) t.Stop(); };
            t.Start();
        }

        bool appWindow;

        void SetAppWindow(bool on)
        {
            if (appWindow == on) return;
            appWindow = on;
            int ex = GetWindowLong(hwnd, -20);
            SetWindowLong(hwnd, -20, on ? (ex & ~0x80) | 0x40000 : (ex & ~0x40000) | 0x80); // TOOLWINDOW <-> APPWINDOW
        }

        void PlaceBars()
        {
            if (!shown) return;
            foreach (var h in raisedBars) SetWindowPos(h, (IntPtr)(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040); // topmost; no move / size / activate; show
        }

        // Only goes back behind the game if one is still in front once focus has settled (otherwise a normal window would cover the taskbar).
        void ReleaseTaskbar()
        {
            if (raisedBars.Count == 0) return;
            var bars = raisedBars.ToArray(); raisedBars.Clear();
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            t.Tick += delegate
            {
                t.Stop();
                if (FullScreen(GetForegroundWindow())) foreach (var h in bars) SetWindowPos(h, (IntPtr)(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            };
            t.Start();
        }

        readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
        readonly string dir = AppDir.Path;
        string LayoutFile { get { return Path.Combine(dir, "layout.json"); } }

        Window win; FrameworkElement root; IntPtr hwnd; TranslateTransform contentTf;
        TextBox search, folderTitle; TextBlock hint, noRes;
        ScrollViewer pinScroll; ItemsControl pinList, folderList; ListBox resList; ScrollViewer allScroll; StackPanel allPanel; Popup letterPop; UniformGrid letterGrid;
        FrameworkElement calcCard; TextBlock calcText;
        string calcValue; int fileGen; ImageSource gear; List<Node> settings;
        readonly Dictionary<char, FrameworkElement> letterAt = new Dictionary<char, FrameworkElement>();
        Grid pinnedView, allView, resView, folderOverlay, cfgOverlay;
        Button btnPower; TextBlock greeting, userInitial; Border avatar;
        Prefs prefs = new Prefs();
        WidgetsCtl widgets;
        bool busy; // a modal dialog is up: don't treat focus loss / outside clicks as "close"

        ObservableCollection<Node> pins;
        List<Node> apps = new List<Node>();
        string appSig = "";
        bool shown, scanning, needSeed; int baseView, shownAt; // baseView: 0 pinned, 1 all apps; shownAt: TickCount of the last open
        DateTime lastScan = DateTime.MinValue;
        Node openFolder, downNode, lastTarget; Point downPos; int lastMode;

        public static string TestLook;
        readonly string forceTheme; // "light" | "dark" | null = follow Windows (used by --shot to test both)
        bool isLight, testMode;

        public MenuApp(string forceTheme)
        {
            this.forceTheme = forceTheme;
            using (var s = typeof(MenuApp).Assembly.GetManifestResourceStream("Menu.xaml")) win = (Window)XamlReader.Load(s);
            root = (FrameworkElement)win.FindName("Root");
            ((Border)root).Child.RenderTransform = contentTf = new TranslateTransform();
            search = F<TextBox>("Search"); folderTitle = F<TextBox>("FolderTitle"); hint = F<TextBlock>("Hint"); noRes = F<TextBlock>("NoRes");
            pinScroll = F<ScrollViewer>("PinScroll"); pinList = F<ItemsControl>("PinList"); folderList = F<ItemsControl>("FolderList");
            allScroll = F<ScrollViewer>("AllScroll"); allPanel = F<StackPanel>("AllPanel"); letterPop = F<Popup>("LetterPop"); letterGrid = F<UniformGrid>("LetterGrid");
            letterPop.Opened += delegate { UiUtil.MenusOpen++; };
            letterPop.Closed += delegate
            {
                UiUtil.MenusOpen--;
                var dim = F<Border>("LetterDim"); dim.BeginAnimation(UIElement.OpacityProperty, null); dim.Visibility = Visibility.Collapsed;
            };
            resList = F<ListBox>("ResList");
            calcCard = F<FrameworkElement>("CalcCard"); calcText = F<TextBlock>("CalcText");
            pinnedView = F<Grid>("PinnedView"); allView = F<Grid>("AllView"); resView = F<Grid>("ResView"); folderOverlay = F<Grid>("FolderOverlay"); cfgOverlay = F<Grid>("CfgOverlay");
            btnPower = F<Button>("BtnPower");
            greeting = F<TextBlock>("Greeting"); userInitial = F<TextBlock>("UserInitial"); avatar = F<Border>("Avatar");
            userInitial.Text = AppDir.User.Substring(0, 1).ToUpper();
            LoadPrefs();
            if (TestLook != null) { var p = TestLook.Split(','); prefs.Accent = p[0]; prefs.Tint = int.Parse(p[1]); prefs.CardMul = int.Parse(p[2]); if (p.Length > 5) { prefs.Corners = int.Parse(p[3]); prefs.Density = int.Parse(p[4]); prefs.TextSz = int.Parse(p[5]); } } // --look "#E74856,30,200", in memory only
            ApplyAvatar(); // was swallowed by the comment above: the picture was never loaded at start, only right after choosing it
            F<Button>("BtnWin").Content = HeightNames[prefs.WinSize % Heights.Length];
            widgets = new WidgetsCtl(win, prefs.HideWidgets, h => { prefs.HideWidgets = h; SavePrefs(); });

            pins = LoadPins();
            if (pins == null) { pins = new ObservableCollection<Node>(); needSeed = true; }
            pinList.ItemsSource = pins;

            hwnd = new WindowInteropHelper(win).EnsureHandle();
            InitChrome();
            ApplyTheme();
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += delegate { ui.BeginInvoke(new Action(ApplyTheme)); };
            Icons.Start(ui);
            Icons.Changed = (id, img) => { foreach (var n in IconNodes()) if (n.Id == id) n.Icon = img; };
            Wire();
            InitLook();
            NameButtons(win);
            Rescan();
        }

        // ----- appearance panel: theme, accent, opacity (applied live, saved after a short pause) -----

        static readonly string[] Palette = { "#0078D4", "#60CDFF", "#7C5CFF", "#C239B3", "#E74856", "#FF8C00", "#FFB900", "#10893E", "#00B7C3", "#8E8E93" };
        static readonly string[] ThemeNames = { "Automático", "Claro", "Oscuro" };
        readonly List<KeyValuePair<Border, string>> dots = new List<KeyValuePair<Border, string>>();
        Slider tintSlider, cardSlider; TextBox accentHex; DispatcherTimer saveTimer; bool loadingLook;

        void InitLook()
        {
            InitBackground();
            tintSlider = F<Slider>("TintSlider"); cardSlider = F<Slider>("CardSlider"); accentHex = F<TextBox>("AccentHex");
            var row = F<WrapPanel>("AccentRow");
            Action<Border, string> add = (dot, hex) =>
            {
                dot.Cursor = Cursors.Hand; dot.BorderThickness = new Thickness(2); dot.Margin = new Thickness(3, 0, 3, 0);
                dot.MouseLeftButtonUp += delegate { prefs.Accent = hex; LookChanged(true); };
                dots.Add(new KeyValuePair<Border, string>(dot, hex)); row.Children.Add(dot);
            };
            var auto = new Border { CornerRadius = new CornerRadius(11), Height = 22, Padding = new Thickness(6, 0, 6, 0), ToolTip = "El acento de Windows" };
            auto.SetResourceReference(Border.BackgroundProperty, "cField");
            var autoText = new TextBlock { Text = "Windows", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            autoText.SetResourceReference(TextBlock.ForegroundProperty, "cText");
            auto.Child = autoText; add(auto, null);
            foreach (var hex in Palette)
                add(new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), ToolTip = hex,
                                 Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)) }, hex);

            F<Button>("BtnTheme").Click += delegate { prefs.Theme = (prefs.Theme + 1) % ThemeNames.Length; LookChanged(true); };
            tintSlider.ValueChanged += delegate { if (!loadingLook) { prefs.Tint = (int)Math.Round(tintSlider.Value); LookChanged(false); } };
            cardSlider.ValueChanged += delegate { if (!loadingLook) { prefs.CardMul = (int)Math.Round(cardSlider.Value); LookChanged(false); } };
            accentHex.TextChanged += delegate
            {
                var t = accentHex.Text.Trim();
                if (!loadingLook && System.Text.RegularExpressions.Regex.IsMatch(t, "^#[0-9A-Fa-f]{6}$")) { prefs.Accent = t; LookChanged(false); }
            };
            F<Button>("BtnCorners").Click += delegate { prefs.Corners = (prefs.Corners + 1) % CornerNames.Length; LookChanged(true); };
            F<Button>("BtnSpacing").Click += delegate { prefs.Density = (prefs.Density + 1) % DensityNames.Length; LookChanged(true); };
            F<Button>("BtnText").Click += delegate { prefs.TextSz = (prefs.TextSz + 1) % TextNames.Length; LookChanged(true); };
            // one reset per section
            F<Button>("BtnResetLook").Click += delegate { prefs.Theme = 0; prefs.Accent = null; prefs.Tint = 0; prefs.CardMul = 0; LookChanged(true); };
            F<Button>("BtnResetShape").Click += delegate { prefs.Corners = 0; prefs.Density = 0; prefs.TextSz = 0; prefs.Size = 2; LookChanged(true); };
            F<Button>("BtnResetWin").Click += delegate { prefs.WinSize = 0; F<Button>("BtnWin").Content = HeightNames[0]; widgets.ResetAll(); SavePrefs(); };
            ApplyShape();
            SyncLook();
        }

        // ----- custom background: the picture is blurred once (at pick time / slider release) and stored as bg.jpg -----

        int opens;
        public static string TestType;
        public static bool TestScroll;
        public static double TestHold; // --hold secs: keep the test window open that long (for CPU sampling)
        public static string TestBg; // --bg "file,blur,dim": in memory only, rendered to a temp file
        Image bgImg; Border bgDimBox; Slider blurSlider, dimSlider; DispatcherTimer blurTimer;
        const int BgW = 880, BgH = 1000; // covers every window height; UniformToFill crops the rest

        string BgOut { get { return TestBg != null ? Path.Combine(Path.GetTempPath(), "smx-bg-test.jpg") : Path.Combine(dir, "bg.jpg"); } }

        void RenderBackground()
        {
            var src = new BitmapImage();
            src.BeginInit(); src.UriSource = new Uri(Path.Combine(dir, prefs.BgFile)); src.DecodePixelWidth = 1280; src.CacheOption = BitmapCacheOption.OnLoad; src.EndInit(); src.Freeze();
            double r = prefs.BgBlur, m = Math.Ceiling(r * 2); // oversize the picture so the blur does not fade out at the edges
            var img = new Image { Source = src, Stretch = Stretch.UniformToFill, Width = BgW + 2 * m, Height = BgH + 2 * m,
                                  HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (r > 0) img.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = r, RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality };
            var host = new Grid { Width = BgW, Height = BgH, ClipToBounds = true }; host.Children.Add(img);
            host.Measure(new Size(BgW, BgH)); host.Arrange(new Rect(0, 0, BgW, BgH)); host.UpdateLayout();
            var rtb = new RenderTargetBitmap(BgW, BgH, 96, 96, PixelFormats.Pbgra32); rtb.Render(host); rtb.Freeze();

            var enc = new JpegBitmapEncoder { QualityLevel = 92 }; enc.Frames.Add(BitmapFrame.Create(rtb));
            Directory.CreateDirectory(Path.GetDirectoryName(BgOut));
            using (var fs = File.Create(BgOut)) enc.Save(fs);

            var small = new TransformedBitmap(rtb, new ScaleTransform(16.0 / BgW, 16.0 / BgH)); // 16x16 is enough for an average
            var px = new byte[small.PixelWidth * small.PixelHeight * 4]; small.CopyPixels(px, small.PixelWidth * 4, 0);
            double sum = 0; for (int i = 0; i < px.Length; i += 4) sum += 0.114 * px[i] + 0.587 * px[i + 1] + 0.299 * px[i + 2];
            prefs.BgLum = (int)Math.Round(sum / (px.Length / 4) / 255 * 100);
        }

        // With a picture, the "Opacidad del fondo" slider (Tint) fades it so the glass behind shows through; untouched = fully opaque.
        void BgOpacity() { if (bgImg != null) bgImg.Opacity = prefs.Tint > 0 ? Math.Max(0.3, prefs.Tint / 100.0) : 1; }

        void ApplyBackground()
        {
            BgOpacity();
            bool on = prefs.BgFile != null && File.Exists(BgOut);
            bgImg.Visibility = bgDimBox.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            bgDimBox.Opacity = prefs.BgDim / 100.0;
            if (!on) { bgImg.Source = null; return; }
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit(); bi.UriSource = new Uri(BgOut); bi.CacheOption = BitmapCacheOption.OnLoad; bi.EndInit(); bi.Freeze();
                bgImg.Source = bi;
            }
            catch { }
        }

        void PickBackground()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Elige la imagen de fondo", Filter = "Imágenes|*.png;*.jpg;*.jpeg;*.bmp;*.webp" };
            busy = true; bool ok = dlg.ShowDialog(win) == true; busy = false;
            if (shown) ForceForeground();
            if (!ok) return;
            try
            {
                Directory.CreateDirectory(dir);
                foreach (var old in Directory.GetFiles(dir, "bg-src.*")) File.Delete(old);
                string name = "bg-src" + Path.GetExtension(dlg.FileName).ToLowerInvariant();
                File.Copy(dlg.FileName, Path.Combine(dir, name), true);
                prefs.BgFile = name; prefs.BgBlur = 12; prefs.BgDim = 25;
                RenderBackground(); ApplyBackground(); LookChanged(true);
            }
            catch { prefs.BgFile = null; ApplyBackground(); LookChanged(true); }
        }

        // ----- backup: pins, look, avatar and background in one .zip -----

        static bool BackupFile(string n) { return n == "layout.json" || n == "prefs.json" || n == "bg.jpg" || n.StartsWith("avatar.") || n.StartsWith("bg-src."); }

        void Flash(Button b, string text)
        {
            var old = b.Content; b.Content = text;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            t.Tick += delegate { t.Stop(); b.Content = old; };
            t.Start();
        }

        void SaveBackup()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Guardar copia de seguridad", Filter = "Copia del menú (*.zip)|*.zip", FileName = "Nexo-copia-" +DateTime.Now.ToString("yyyy-MM-dd") + ".zip" };
            busy = true; bool ok = dlg.ShowDialog(win) == true; busy = false;
            if (shown) ForceForeground();
            if (!ok) return;
            string msg = "Copia guardada ✓";
            try
            {
                Save(); SavePrefs();
                if (File.Exists(dlg.FileName)) File.Delete(dlg.FileName);
                using (var z = ZipFile.Open(dlg.FileName, ZipArchiveMode.Create))
                    foreach (var f in Directory.GetFiles(dir).Where(x => BackupFile(Path.GetFileName(x)))) z.CreateEntryFromFile(f, Path.GetFileName(f));
            }
            catch { msg = "No se pudo guardar"; }
            Flash(F<Button>("BtnBackup"), msg);
        }

        void RestoreBackup()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Elige una copia de seguridad", Filter = "Copia del menú (*.zip)|*.zip" };
            busy = true; bool ok = dlg.ShowDialog(win) == true; busy = false;
            if (shown) ForceForeground();
            if (!ok) return;
            var btn = F<Button>("BtnRestore");
            try
            {
                var files = new List<KeyValuePair<string, byte[]>>(); // read everything first: a bad file must not wipe the current setup
                using (var z = ZipFile.OpenRead(dlg.FileName))
                    foreach (var e in z.Entries)
                    {
                        if (e.FullName != e.Name || !BackupFile(e.Name) || e.Length > 64 * 1024 * 1024) continue; // flat, known names only
                        using (var s = e.Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); files.Add(new KeyValuePair<string, byte[]>(e.Name, ms.ToArray())); }
                    }
                if (!files.Any(x => x.Key == "prefs.json" || x.Key == "layout.json")) { Flash(btn, "Archivo no válido"); return; }
                Directory.CreateDirectory(dir);
                foreach (var f in Directory.GetFiles(dir).Where(x => BackupFile(Path.GetFileName(x)))) File.Delete(f);
                foreach (var kv in files) File.WriteAllBytes(Path.Combine(dir, kv.Key), kv.Value);
            }
            catch { Flash(btn, "No se pudo restaurar"); return; }
            // restart so everything loads from the restored files (a second instance waits for this one to exit)
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            Process.Start(exe, "--after " + Process.GetCurrentProcess().Id);
            Application.Current.Shutdown();
        }

        void ClearBackground()
        {
            prefs.BgFile = null; ApplyBackground();
            try { foreach (var f in Directory.GetFiles(dir, "bg*")) File.Delete(f); } catch { }
            LookChanged(true);
        }

        void InitBackground()
        {
            bgImg = F<Image>("BgImg"); bgDimBox = F<Border>("BgDim"); blurSlider = F<Slider>("BlurSlider"); dimSlider = F<Slider>("DimSlider");
            if (TestBg != null) { var p = TestBg.Split(','); prefs.BgFile = p[0]; prefs.BgBlur = int.Parse(p[1]); prefs.BgDim = int.Parse(p[2]); RenderBackground(); }
            F<Button>("BtnBgPick").Click += delegate { PickBackground(); };
            F<Button>("BtnBgClear").Click += delegate { ClearBackground(); };
            dimSlider.ValueChanged += delegate { if (!loadingLook) { prefs.BgDim = (int)Math.Round(dimSlider.Value); bgDimBox.Opacity = prefs.BgDim / 100.0; LookChanged(false); } };
            blurSlider.ValueChanged += delegate
            {
                if (loadingLook) return;
                prefs.BgBlur = (int)Math.Round(blurSlider.Value); UpdateLookLabels();
                if (blurTimer == null)
                {
                    blurTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
                    blurTimer.Tick += delegate { blurTimer.Stop(); try { RenderBackground(); ApplyBackground(); LookChanged(false); } catch { } };
                }
                blurTimer.Stop(); blurTimer.Start(); // render once the slider rests
            };
            ApplyBackground();
            if (TestBg != null) ApplyTheme(); // the luminance is only known after the first render
        }

        void ApplyShape()
        {
            var r = Radii[prefs.Corners % Radii.Length];
            win.Resources["RCard"] = new CornerRadius(r[0]); win.Resources["RBig"] = new CornerRadius(r[1]); win.Resources["RSmall"] = new CornerRadius(r[2]);
            win.Resources["TileMg"] = new Thickness(Margins[prefs.Density % Margins.Length]);
            double fs = FontSizes[prefs.TextSz % FontSizes.Length];
            win.FontSize = fs; win.Resources["TileTextH"] = Math.Round(fs * 2.7); // two lines of tile label
            SetSize(prefs.Size, false);
        }

        void LookChanged(bool sync)
        {
            ApplyTheme(); ApplyShape();
            if (sync) SyncLook(); else UpdateLookLabels();
            if (saveTimer == null) { saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) }; saveTimer.Tick += delegate { saveTimer.Stop(); SavePrefs(); }; }
            saveTimer.Stop(); saveTimer.Start();
        }

        void SyncLook()
        {
            loadingLook = true;
            tintSlider.Value = prefs.Tint > 0 ? prefs.Tint : prefs.BgFile != null ? 100 : (isLight ? 70 : 55); // the built-in defaults
            cardSlider.Value = prefs.CardMul > 0 ? prefs.CardMul : 100;
            blurSlider.Value = prefs.BgBlur; dimSlider.Value = prefs.BgDim;
            blurSlider.IsEnabled = dimSlider.IsEnabled = prefs.BgFile != null;
            var cur = ((SolidColorBrush)win.Resources["cAccent"]).Color;
            accentHex.Text = prefs.Accent ?? string.Format("#{0:X2}{1:X2}{2:X2}", cur.R, cur.G, cur.B); // shows the colour in use
            F<Button>("BtnTheme").Content = ThemeNames[prefs.Theme % ThemeNames.Length];
            F<Button>("BtnCorners").Content = CornerNames[prefs.Corners % CornerNames.Length];
            F<Button>("BtnSpacing").Content = DensityNames[prefs.Density % DensityNames.Length];
            F<Button>("BtnText").Content = TextNames[prefs.TextSz % TextNames.Length];
            foreach (var d in dots)
            {
                if (string.Equals(d.Value, prefs.Accent, StringComparison.OrdinalIgnoreCase)) d.Key.SetResourceReference(Border.BorderBrushProperty, "cText");
                else d.Key.BorderBrush = Brushes.Transparent;
            }
            loadingLook = false;
            UpdateLookLabels();
        }

        void UpdateLookLabels()
        {
            F<TextBlock>("TintLabel").Text = (int)Math.Round(tintSlider.Value) + "%";
            F<TextBlock>("CardLabel").Text = (int)Math.Round(cardSlider.Value) + "%";
            F<TextBlock>("BlurLabel").Text = (int)Math.Round(blurSlider.Value) + " px";
            F<TextBlock>("DimLabel").Text = (int)Math.Round(dimSlider.Value) + "%";
        }

        // Icon-only buttons get their tooltip as accessible name (screen readers would otherwise read the glyph character).
        static void NameButtons(DependencyObject d)
        {
            foreach (object o in LogicalTreeHelper.GetChildren(d))
            {
                var b = o as Button;
                if (b != null && b.ToolTip is string && string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(b)))
                    System.Windows.Automation.AutomationProperties.SetName(b, (string)b.ToolTip);
                var c = o as DependencyObject; if (c != null) NameButtons(c);
            }
        }

        T F<T>(string n) where T : class { return (T)win.FindName(n); }

        // ----- theme: light/dark + accent follow Windows -----

        static bool ReadLight()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k != null && Convert.ToInt32(k.GetValue("SystemUsesLightTheme", 0)) == 1;
            }
            catch { return false; }
        }

        static Color ReadAccent()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    object o = k == null ? null : k.GetValue("AccentColorMenu");
                    if (o != null) { uint v = unchecked((uint)Convert.ToInt32(o)); return Color.FromRgb((byte)v, (byte)(v >> 8), (byte)(v >> 16)); } // stored as ABGR
                }
            }
            catch { }
            return Color.FromRgb(0x00, 0x78, 0xD4);
        }

        void ApplyTheme()
        {
            isLight = forceTheme != null ? forceTheme == "light" : prefs.BgFile != null ? prefs.BgLum * (100 - prefs.BgDim) / 10000.0 > 0.55 // text colour follows the picture
                    : prefs.Theme == 1 ? true : prefs.Theme == 2 ? false : ReadLight();
            Action<string, string, string> t = (key, dark, light) =>
                win.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isLight ? light : dark));
            t("cTint", "#8C1E1E1E", "#B3F3F3F3");
            t("cText", "#FFFFFF", "#1A1A1A");
            t("cDim", "#A6FFFFFF", "#99000000");
            t("cCard", "#12FFFFFF", "#99FFFFFF");
            t("cCardHover", "#22FFFFFF", "#CCFFFFFF");
            t("cStroke", "#14FFFFFF", "#1A000000");
            t("cField", "#1FFFFFFF", "#B3FFFFFF");
            t("cDock", "#1AFFFFFF", "#99FFFFFF");
            t("cRowHover", "#1AFFFFFF", "#12000000");
            t("cRowSel", "#2EFFFFFF", "#1F000000");
            t("cFooter", "#26000000", "#0F000000");
            t("cMenu", "#F2242424", "#F5F8F8F8");
            t("cOverlay", "#99000000", "#66000000");
            t("cFolder", "#F0262626", "#F7F6F6F6");
            t("cThumb", "#55FFFFFF", "#55000000");

            // user opacity: the window tint is set outright, cards/fields are scaled relative to their defaults
            Action<string, Func<byte, byte>> alpha = (key, f) =>
            {
                var c = ((SolidColorBrush)win.Resources[key]).Color; c.A = f(c.A); win.Resources[key] = new SolidColorBrush(c);
            };
            if (prefs.Tint > 0) alpha("cTint", o => (byte)(255 * prefs.Tint / 100));
            BgOpacity();
            if (prefs.CardMul > 0)
                foreach (var k in new[] { "cCard", "cCardHover", "cField", "cDock" }) alpha(k, o => (byte)Math.Min(255, o * prefs.CardMul / 100));

            Color a = ReadAccent();
            if (prefs.Accent != null) { try { a = (Color)ColorConverter.ConvertFromString(prefs.Accent); } catch { } }
            win.Resources["cAccent"] = new SolidColorBrush(a);
            win.Resources["cAccentSoft"] = new SolidColorBrush(Color.FromArgb(0x99, a.R, a.G, a.B));
            win.Resources["cOnAccent"] = new SolidColorBrush((0.299 * a.R + 0.587 * a.G + 0.114 * a.B) / 255 > 0.6 ? Colors.Black : Colors.White);
            int v = isLight ? 0 : 1; DwmSetWindowAttribute(hwnd, 20, ref v, 4);
            gear = null; if (settings != null) foreach (var s in settings) s.Icon = GearIcon(); // icon colour follows the accent
        }

        // Test helper: show the menu, wait for icons, save a PNG of it (over a flat stand-in for the acrylic), then call done.
        // menu = "tile" (right-click menu of the first pinned app) or "size" (music widget's size menu): also grabs the real screen,
        // because popups are separate windows and the PNG render below cannot see them.
        public void Shot(string path, string query, bool all, bool mixer, string menu, bool click, Action done)
        {
            testMode = !click; Show();
            if (menu == "cfg") { cfgOverlay.Visibility = Visibility.Visible; menu = null; }
            if (mixer) ui.BeginInvoke(DispatcherPriority.ContextIdle, new Action(delegate { widgets.OpenMixerForTest(); })); // after the deferred widgets.Shown()
            if (all) { baseView = 1; Apply(); }
            if (query != null) search.Text = query;
            if (menu != null)
            {
                var m = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
                m.Tick += delegate
                {
                    m.Stop();
                    if (menu == "drop") { pins[1].DropMode = 2; pins[3].DropMode = 1; pins[5].DropMode = 3; }
                    else if (menu == "nav") { foreach (var k in new[] { Key.Right, Key.Right, Key.Down, Key.Down }) NavMove(k); }
                    else if (menu == "letters") ShowLetters(letterAt.Values.ElementAt(3));
                    else
                    {
                        var cp = pinList.ItemContainerGenerator.ContainerFromIndex(0);
                        var cm = cp == null ? null : ShowTileMenu(TileOf(VisualTreeHelper.GetChild(cp, 0)), PlacementMode.Bottom);
                        if (click && cm != null)
                        {
                            var c = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.7) };
                            c.Tick += delegate
                            {
                                c.Stop();
                                var item = (MenuItem)cm.Items[0]; // "Abrir"
                                var pt = item.PointToScreen(new Point(item.ActualWidth / 2, item.ActualHeight / 2));
                                SetCursorPos((int)pt.X, (int)pt.Y); mouse_event(0x2, 0, 0, 0, UIntPtr.Zero); mouse_event(0x4, 0, 0, 0, UIntPtr.Zero);
                            };
                            c.Start();
                        }
                    }
                };
                m.Start();
            }
            if (TestType != null) // test-only: type the text one letter at a time, time each keystroke including layout, count icons still missing
            {
                var tt = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                tt.Tick += delegate
                {
                    tt.Stop(); var res = new List<string>();
                    for (int n = 1; n <= TestType.Length; n++)
                    {
                        var sw = Stopwatch.StartNew(); search.Text = TestType.Substring(0, n); searchTimer.Stop(); DoSearch(); win.UpdateLayout();
                        res.Add(TestType.Substring(0, n) + "=" + sw.ElapsedMilliseconds + "ms/" + resList.Items.Count + "items");
                    }
                    var w2 = Stopwatch.StartNew(); baseView = 1; search.Text = ""; Apply(); win.UpdateLayout(); res.Add("allApps(first paint)=" + w2.ElapsedMilliseconds + "ms");
                    int missing = 0; foreach (var a in apps) if (a.Icon == null) missing++; // asking for the icon also queues it
                    res.Add("appsWithoutIcon=" + missing + "/" + apps.Count);
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "smx-type.txt"), string.Join("  ", res));
                };
                tt.Start();
            }
            if (TestScroll) // test-only: scroll "All apps" steadily for 3 s (after 2 s) and log frame gaps
            {
                var st = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                st.Tick += delegate
                {
                    st.Stop(); var sw = Stopwatch.StartNew(); var log = new List<long>(); EventHandler h = null; double off = 0;
                    h = delegate
                    {
                        log.Add(sw.ElapsedMilliseconds); off += 14; allScroll.ScrollToVerticalOffset(off);
                        if (sw.ElapsedMilliseconds > 3000) { CompositionTarget.Rendering -= h; File.WriteAllText(Path.Combine(Path.GetTempPath(), "smx-scroll.txt"), string.Join(",", log)); }
                    };
                    CompositionTarget.Rendering += h;
                };
                st.Start();
            }
            if (TestHold > 0) // test-only: close and reopen once to measure a warm open (the first one pays one-off start-up costs)
            {
                var re = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                re.Tick += delegate { re.Stop(); Hide(); var again = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; again.Tick += delegate { again.Stop(); Show(); }; again.Start(); };
                re.Start();
            }
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(TestHold > 0 ? TestHold : 4.5) };
            t.Tick += delegate
            {
                t.Stop();
                RECT r;
                if (menu != null && GetWindowRect(hwnd, out r))
                    using (var bmp = new System.Drawing.Bitmap(r.r - r.l, r.b - r.t))
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(r.l, r.t, 0, 0, bmp.Size);
                        bmp.Save(path + ".screen.png");
                    }
                double s = 1.5, w = root.ActualWidth, h = root.ActualHeight;
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(new SolidColorBrush(isLight ? Color.FromRgb(0xE4, 0xE4, 0xE8) : Color.FromRgb(0x2B, 0x2B, 0x30)), null, new Rect(0, 0, w, h));
                    dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, w, h));
                }
                var rtb = new RenderTargetBitmap((int)(w * s), (int)(h * s), 96 * s, 96 * s, PixelFormats.Pbgra32);
                rtb.Render(dv);
                var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(path)) enc.Save(fs);
                done();
            };
            t.Start();
        }

        void InitChrome()
        {
            HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
            int v = 1; DwmSetWindowAttribute(hwnd, 20, ref v, 4);  // dark mode
            v = 2; DwmSetWindowAttribute(hwnd, 33, ref v, 4);       // rounded corners
            // Acrylic through the window accent API rather than DWMWA_SYSTEMBACKDROP_TYPE: the backdrop type turns into a flat
            // colour whenever the window is not the active one, the accent keeps blurring what is behind. The tint is drawn by Root (cTint).
            v = 1; DwmSetWindowAttribute(hwnd, 38, ref v, 4);
            var ap = new ACCENT { state = 4, gradient = 0x01000000 };
            int sz = Marshal.SizeOf(typeof(ACCENT)); IntPtr pa = Marshal.AllocHGlobal(sz); Marshal.StructureToPtr(ap, pa, false);
            var data = new WCAD { attr = 19, data = pa, size = sz };
            SetWindowCompositionAttribute(hwnd, ref data); Marshal.FreeHGlobal(pa);
            var m = new MARGINS { l = -1, r = -1, t = -1, b = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref m);
            SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x80); // tool window: hidden from Alt+Tab
        }

        // Render once offscreen so the first real open is instant.
        public void Warm()
        {
            win.Left = -10000; win.Top = -10000; win.Show();
            ui.BeginInvoke(DispatcherPriority.ContextIdle, new Action(delegate { win.Hide(); win.ShowActivated = true; TrimLater(); }));
        }

        [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr p, IntPtr min, IntPtr max);
        DispatcherTimer trimTimer;

        // While hidden the menu has no use for its pages: collect garbage and give the working set back to Windows (pages fault back in on demand).
        void Trim()
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
        }

        void TrimLater()
        {
            // Handing the working set back makes the next open page everything in again (a visible hitch), so only do it after a long idle.
            if (trimTimer == null) trimTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            trimTimer.Tick -= TrimTick; trimTimer.Tick += TrimTick; trimTimer.Stop(); trimTimer.Start();
        }
        void TrimTick(object s, EventArgs e) { trimTimer.Stop(); if (!shown && !testMode) Trim(); }

        void Wire()
        {
            win.Deactivated += delegate
            {
                if (!shown || busy || testMode || UiUtil.MenusOpen != 0) return;
                int since = Environment.TickCount - shownAt;
                Log("deactivated " + since + " ms after opening");
                // Right after opening, focus is often grabbed back (a borderless game does this): take it again instead of closing.
                if (since < 800) ui.BeginInvoke(new Action(delegate { if (shown) ForceForeground(); }));
                else Hide();
            };
            // Focus-loss events are unreliable (taskbar, other always-on-top windows), so also watch raw clicks.
            Kbd.OnClick = (x, y) =>
            {
                RECT r;
                if (!shown || busy || UiUtil.MenusOpen > 0 || !GetWindowRect(hwnd, out r)) return;
                if (x < r.l || x >= r.r || y < r.t || y >= r.b) { Log("outside click at " + x + "," + y + " " + (Environment.TickCount - shownAt) + " ms after opening"); ui.BeginInvoke(new Action(Hide)); }
            };
            win.PreviewKeyDown += OnKey;
            win.PreviewTextInput += delegate(object s, TextCompositionEventArgs e)
            {
                if (openFolder != null || widgets.MixerOpen || cfgOverlay.Visibility == Visibility.Visible || Keyboard.FocusedElement is TextBox) return;
                search.Focus(); search.Text += e.Text; search.CaretIndex = search.Text.Length; e.Handled = true;
            };
            win.PreviewMouseRightButtonUp += OnRightClick;
            // results are rebuilt once typing pauses for a moment (a burst of letters costs one rebuild, not one per letter); clearing is immediate
            searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            searchTimer.Tick += delegate { searchTimer.Stop(); DoSearch(); };
            search.TextChanged += delegate
            {
                hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                searchTimer.Stop();
                if (search.Text.Length == 0) DoSearch(); else searchTimer.Start();
            };

            F<Button>("BtnAll").Click += delegate { baseView = 1; Apply(); };
            F<Button>("BtnBack").Click += delegate { baseView = 0; Apply(); };
            F<Button>("BtnSettings").Click += delegate { Hide(); Run("ms-settings:"); };
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            DockLink("BtnHome", home);
            DockLink("BtnDocs", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            DockLink("BtnDown", Path.Combine(home, "Downloads"));
            DockLink("BtnPics", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
            btnPower.Click += delegate { PowerMenu(); };
            F<Button>("BtnCfg").Click += delegate { cfgOverlay.Visibility = Visibility.Visible; UiUtil.PopIn(cfgOverlay); };
            F<Button>("BtnBackup").Click += delegate { SaveBackup(); };
            F<Button>("BtnRestore").Click += delegate { RestoreBackup(); };
            F<Button>("BtnCloseCfg").Click += delegate { UiUtil.PopOut(cfgOverlay); };
            cfgOverlay.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { if (e.OriginalSource == cfgOverlay) UiUtil.PopOut(cfgOverlay); };
            F<Button>("BtnWin").Click += delegate
            {
                prefs.WinSize = (prefs.WinSize + 1) % Heights.Length; SavePrefs();
                F<Button>("BtnWin").Content = HeightNames[prefs.WinSize];
            };
            calcCard.MouseLeftButtonUp += delegate
            {
                if (calcValue == null) return;
                try { Clipboard.SetText(calcValue); calcText.Text = "= " + calcValue + "   ✓ copiado"; } catch { }
            };
            F<Button>("BtnBigger").Click += delegate { SetSize(prefs.Size + 1, true); };
            F<Button>("BtnSmaller").Click += delegate { SetSize(prefs.Size - 1, true); };
            avatar.MouseLeftButtonUp += delegate { PickAvatar(); };

            foreach (UIElement e in new UIElement[] { pinScroll, folderList })
            {
                e.PreviewMouseLeftButtonDown += PinDown; e.PreviewMouseMove += PinMove; e.PreviewMouseLeftButtonUp += PinUp;
            }
            pinScroll.DragOver += PinDragOver; pinScroll.Drop += PinDrop; pinScroll.DragLeave += delegate { SetTarget(null, 0); };

            foreach (UIElement lb in new UIElement[] { allScroll, resList })
                lb.PreviewMouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
                {
                    var n = NodeAt(e.OriginalSource); if (n != null) Activate(n);
                };

            folderOverlay.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { if (e.OriginalSource == folderOverlay) CloseFolder(); };
        }

        // ----- show / hide -----

        public void Toggle() { Log("toggle (shown=" + shown + ")"); if (shown) Hide(); else Show(); }

        void Show()
        {
            int hour = DateTime.Now.Hour;
            greeting.Text = (hour < 6 ? "Buenas noches" : hour < 13 ? "Buenos días" : hour < 20 ? "Buenas tardes" : "Buenas noches") + ", " + AppDir.User;
            if (trimTimer != null) trimTimer.Stop();
            search.Clear(); baseView = 0; CloseFolder(); cfgOverlay.BeginAnimation(UIElement.OpacityProperty, null); cfgOverlay.Visibility = Visibility.Collapsed; Apply();
            pinScroll.ScrollToTop(); allScroll.ScrollToTop();

            // open on the monitor under the mouse (pixels -> WPF units)
            var scr = WF.Screen.FromPoint(WF.Cursor.Position).WorkingArea;
            var toDip = HwndSource.FromHwnd(hwnd).CompositionTarget.TransformFromDevice;
            var wa = new Rect(toDip.Transform(new Point(scr.Left, scr.Top)), toDip.Transform(new Point(scr.Right, scr.Bottom)));
            win.Width = W; win.Height = Math.Min(Heights[prefs.WinSize % Heights.Length], wa.Height - 24);
            win.Left = wa.Left + (wa.Width - win.Width) / 2;
            double top = wa.Bottom - win.Height - 12;
            win.Top = top; root.Opacity = 0; shown = true; shownAt = Environment.TickCount;
            // The window itself never moves: moving a window that has acrylic behind it makes DWM re-blur every frame. Only the content slides.
            contentTf.Y = 20;
            RaiseTaskbar();
            win.Show();
            ForceForeground();
            if (!testMode) Kbd.MouseOn();
            // audio / brightness / media polling can wait until the first frames are on screen
            ui.BeginInvoke(DispatcherPriority.Background, new Action(delegate { if (shown) widgets.Shown(); }));

            var slide = new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            if (TestHold > 0) // test-only: log frame times while the open animation runs
            {
                var sw = Stopwatch.StartNew(); var log = new List<long>(); EventHandler h = null; int openNo = ++opens;
                h = delegate { log.Add(sw.ElapsedMilliseconds); if (sw.ElapsedMilliseconds > 600) { CompositionTarget.Rendering -= h; File.WriteAllText(Path.Combine(Path.GetTempPath(), "smx-frames" + openNo + ".txt"), string.Join(",", log)); } };
                CompositionTarget.Rendering += h;
            }
            contentTf.BeginAnimation(TranslateTransform.YProperty, slide);
            root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));
            search.Focus();
        }

        void Hide() { Hide(90); }

        void Hide(int ms)
        {
            if (!shown) return;
            shown = false; Kbd.MouseOff(); widgets.Hidden();
            var fade = new DoubleAnimation(root.Opacity, 0, TimeSpan.FromMilliseconds(ms));
            fade.Completed += delegate
            {
                if (shown) return;
                win.Hide(); SetAppWindow(false); ReleaseTaskbar(); Save(); // disk writes and the app rescan happen once the menu is gone, not while it animates
                if (DateTime.UtcNow - lastScan > TimeSpan.FromSeconds(60)) Rescan();
                TrimLater();
            };
            root.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        // Windows refuses SetForegroundWindow from a background process; sharing the foreground thread's input queue allows it.
        void ForceForeground()
        {
            uint pid, me = GetCurrentThreadId(), fg = GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            AttachThreadInput(me, fg, true); SetForegroundWindow(hwnd); win.Activate(); AttachThreadInput(me, fg, false);
        }

        // ----- views -----

        // ----- arrow-key navigation over the pinned / all-apps tiles (search keeps the focus, so typing still searches) -----

        Node navNode;

        void NavSet(Node n) { if (navNode != null) navNode.Selected = false; navNode = n; if (n != null) n.Selected = true; }

        static IEnumerable<FrameworkElement> Tiles(DependencyObject d)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                var fe = c as FrameworkElement;
                if (fe != null && (fe.Tag as string) == "tile" && fe.DataContext is Node) { if (!((Node)fe.DataContext).IsHeader) yield return fe; }
                else foreach (var t in Tiles(c)) yield return t;
            }
        }

        void NavMove(Key k)
        {
            bool res = resView.Visibility == Visibility.Visible;
            var host = res ? resList : baseView == 1 ? (FrameworkElement)allPanel : pinList;
            Node current = res ? resList.SelectedItem as Node : navNode;
            var items = Tiles(host).Select(t => new { t, c = t.TransformToAncestor(host).Transform(new Point(t.ActualWidth / 2, t.ActualHeight / 2)) })
                                   .OrderBy(x => Math.Round(x.c.Y)).ThenBy(x => x.c.X).ToList();
            if (items.Count == 0) return;
            int cur = items.FindIndex(x => x.t.DataContext == current), next = 0;
            if (cur >= 0)
            {
                var c = items[cur].c; next = cur;
                if (k == Key.Left) next = Math.Max(0, cur - 1);
                else if (k == Key.Right) next = Math.Min(items.Count - 1, cur + 1);
                else
                {   // up/down: nearest tile in the adjacent row, by horizontal distance
                    bool down = k == Key.Down;
                    var rows = items.Where(x => down ? x.c.Y > c.Y + 10 : x.c.Y < c.Y - 10).ToList();
                    if (rows.Count > 0)
                    {
                        double y = down ? rows.Min(x => x.c.Y) : rows.Max(x => x.c.Y);
                        next = items.IndexOf(rows.Where(x => Math.Abs(x.c.Y - y) < 10).OrderBy(x => Math.Abs(x.c.X - c.X)).First());
                    }
                }
            }
            var pick = (Node)items[next].t.DataContext;
            if (res) resList.SelectedItem = pick; else NavSet(pick);
            items[next].t.BringIntoView();
        }

        bool NavActive()
        {
            return openFolder == null && !widgets.MixerOpen && cfgOverlay.Visibility != Visibility.Visible
                && UiUtil.MenusOpen == 0 && (Keyboard.FocusedElement == search || !(Keyboard.FocusedElement is TextBox));
        }

        void Apply()
        {
            NavSet(null);
            int v = search.Text.Trim().Length > 0 ? 2 : baseView;
            if (v == 1) EnsureAll();
            Action<FrameworkElement, bool> show = (el, on) =>
            {
                bool was = el.Visibility == Visibility.Visible;
                el.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (on && !was && shown) UiUtil.Enter(el, 10); // switching views glides; the first one on open is covered by the window's own fade
            };
            show(pinnedView, v == 0); show(allView, v == 1); show(resView, v == 2);
        }

        void OnKey(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (widgets.CloseMixer(true)) { }
                else if (cfgOverlay.Visibility == Visibility.Visible && cfgOverlay.IsHitTestVisible) UiUtil.PopOut(cfgOverlay);
                else if (openFolder != null) CloseFolder();
                else if (search.Text.Length > 0) search.Clear();
                else if (baseView == 1) { baseView = 0; Apply(); }
                else Hide();
            }
            else if ((e.Key == Key.Down || e.Key == Key.Up || e.Key == Key.Left || e.Key == Key.Right) && NavActive()) { NavMove(e.Key); e.Handled = true; }
            else if (e.Key == Key.Enter && navNode != null && NavActive()) { var n = navNode; e.Handled = true; Activate(n); }
            else if (e.Key == Key.Enter && resView.Visibility == Visibility.Visible)
            {
                var n = resList.SelectedItem as Node; if (n != null) Activate(n); e.Handled = true;
            }
        }

        // ----- apps + search -----

        void Rescan()
        {
            if (scanning) return; scanning = true;
            var t = new Thread(delegate()
            {
                List<Node> r = null;
                try { r = Shell.ScanApps(); } catch { }
                ui.BeginInvoke(new Action(delegate { scanning = false; lastScan = DateTime.UtcNow; if (r != null && r.Count > 0) SetApps(r); }));
            });
            t.SetApartmentState(ApartmentState.STA); t.IsBackground = true; t.Start();
        }

        void SetApps(List<Node> list)
        {
            // LTSC keeps the old Notepad/Calculator next to the Store ones: when a Store app (id has '!') shares a name with a classic one, show only the Store app.
            var modern = new HashSet<string>(list.Where(a => a.Id.Contains("!")).Select(a => a.Key));
            list.RemoveAll(a => !a.Id.Contains("!") && modern.Contains(a.Key));
            list.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            string sig = string.Join("|", list.Select(a => a.Id));
            if (sig == appSig) return;
            appSig = sig; apps = list;

            foreach (var a in list) Icons.Prefetch(a.Id);
            allDirty = true; if (baseView == 1) EnsureAll(); // ~300 tiles are only built when "All apps" is actually opened
            if (needSeed) { needSeed = false; Seed(); }
            DoSearch();
        }

        // "All apps": one section per initial letter; clicking a letter opens a grid of the others to jump to.
        DispatcherTimer searchTimer;
        bool allDirty;
        void EnsureAll() { if (allDirty) { allDirty = false; BuildAll(apps); } }

        // The view opens at once with the first few sections; the rest is built a section at a time while the UI is idle
        // (creating ~260 tiles in one go took ~240 ms).
        List<IGrouping<char, Node>> pendingSections = new List<IGrouping<char, Node>>(); int allGen;

        void BuildAll(List<Node> list)
        {
            allPanel.Children.Clear(); letterAt.Clear(); int gen = ++allGen;
            pendingSections = list.GroupBy(a => a.Key.Length > 0 && char.IsLetter(a.Key[0]) ? char.ToUpperInvariant(a.Key[0]) : '#').ToList();
            AddSections(30);
            if (pendingSections.Count > 0) ui.BeginInvoke(DispatcherPriority.Background, new Action(delegate { ContinueAll(gen); }));
        }

        void ContinueAll(int gen)
        {
            if (gen != allGen || pendingSections.Count == 0) return;
            AddSections(1);
            if (pendingSections.Count > 0) ui.BeginInvoke(DispatcherPriority.Background, new Action(delegate { ContinueAll(gen); }));
        }

        // adds sections until at least `tiles` tiles were created
        void AddSections(int tiles)
        {
            while (pendingSections.Count > 0 && tiles > 0)
            {
                var g = pendingSections[0]; pendingSections.RemoveAt(0);
                var head = new Button { Style = (Style)win.Resources["Letter"], Content = g.Key.ToString(), Margin = new Thickness(3, 8, 3, 2) };
                head.Click += delegate { ShowLetters(head); };
                allPanel.Children.Add(head); letterAt[g.Key] = head;
                var items = g.ToList(); tiles -= items.Count;
                allPanel.Children.Add(new ItemsControl { ItemTemplate = (DataTemplate)win.Resources["Tile"], ItemsPanel = (ItemsPanelTemplate)win.Resources["Wrap"], ItemsSource = items });
            }
        }

        void ShowLetters(FrameworkElement from)
        {
            AddSections(int.MaxValue); // the letter grid and the jump need every section to exist
            letterGrid.Children.Clear();
            // every letter is listed so the grid keeps its shape; the ones without apps are dimmed
            var keys = new SortedSet<char>(letterAt.Keys);
            for (char c = 'A'; c <= 'Z'; c++) keys.Add(c);
            foreach (var k in keys)
            {
                FrameworkElement target; bool has = letterAt.TryGetValue(k, out target);
                var b = new Button { Style = (Style)win.Resources["LetterKey"], Content = k.ToString(), Height = 54, Margin = new Thickness(3), IsEnabled = has };
                if (has) b.Click += delegate
                {
                    letterPop.IsOpen = false; allPanel.UpdateLayout();
                    allScroll.ScrollToVerticalOffset(target.TransformToAncestor(allPanel).Transform(new Point(0, 0)).Y);
                };
                letterGrid.Children.Add(b);
            }
            letterPop.PlacementTarget = allScroll; letterPop.IsOpen = true;
            var dim = F<Border>("LetterDim"); dim.Visibility = Visibility.Visible;
            dim.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            var card = F<Border>("LetterCard"); var sc = (ScaleTransform)card.RenderTransform; var ms = TimeSpan.FromMilliseconds(170);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            sc.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1, ms) { EasingFunction = ease });
            sc.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1, ms) { EasingFunction = ease });
            card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, ms));
        }

        static readonly string[] SeedNames = {
            "Microsoft Edge", "Google Chrome", "Firefox", "Explorador de archivos", "File Explorer", "Configuración", "Settings",
            "Calculadora", "Calculator", "Bloc de notas", "Notepad", "Terminal", "Paint", "Microsoft Store", "Reloj", "Fotos", "Cámara" };

        void Seed()
        {
            foreach (var s in SeedNames)
            {
                var a = apps.FirstOrDefault(x => string.Equals(x.Name, s, StringComparison.OrdinalIgnoreCase));
                if (a != null) Pin(a);
            }
            Save();
        }

        static int Score(Node a, string q)
        {
            if (a.Key.StartsWith(q, StringComparison.Ordinal)) return 0;
            if (a.Key.Contains(" " + q) || a.Initials.StartsWith(q, StringComparison.Ordinal)) return 1;
            return a.Key.Contains(q) ? 2 : 9;
        }

        void DoSearch()
        {
            Apply(); fileGen++;
            string raw = search.Text.Trim(), q = Util.Norm(raw);
            if (q.Length == 0) return;
            var r = apps.Select(a => new { a, s = Score(a, q) }).Where(x => x.s < 9)
                        .OrderByDescending(x => Uses(x.a)).ThenBy(x => x.s).ThenBy(x => x.a.Key, StringComparer.Ordinal).Select(x => x.a).Take(36).ToList();
            var sets = Settings().Where(s => s.Key.Contains(q)).Take(6).ToList();
            resList.ItemsSource = Sections(r, sets, null);
            SelectFirst();
            ShowCalc(raw);
            noRes.Visibility = r.Count + sets.Count == 0 && calcValue == null ? Visibility.Visible : Visibility.Collapsed;
            FindFiles(raw, r, sets);
        }

        // Results are grouped under full-row headings so programs and files don't mix.
        static List<Node> Sections(List<Node> progs, List<Node> sets, List<Node> files)
        {
            var r = new List<Node>();
            Action<string, List<Node>> add = (title, l) =>
            {
                if (l == null || l.Count == 0) return;
                r.Add(new Node { Name = title, IsHeader = true }); r.AddRange(l);
            };
            add("Programas", progs); add("Ajustes de Windows", sets); add("Archivos", files);
            return r;
        }

        void SelectFirst()
        {
            var l = resList.ItemsSource as List<Node>;
            int i = l == null ? -1 : l.FindIndex(n => !n.IsHeader);
            resList.SelectedIndex = i;
        }

        // Quick calculator: "12*(3+4)" shows 84; click copies it.
        void ShowCalc(string raw)
        {
            calcValue = null;
            Func<string, string, bool> re = System.Text.RegularExpressions.Regex.IsMatch;
            if (re(raw, @"^[0-9+\-*/%().,\s]+$") && re(raw, @"\d\s*[+\-*/%]"))
                try
                {
                    double v = Convert.ToDouble(new System.Data.DataTable().Compute(raw.Replace(',', '.'), null), CultureInfo.InvariantCulture);
                    if (!double.IsInfinity(v) && !double.IsNaN(v)) calcValue = v.ToString("0.##########", CultureInfo.CurrentCulture);
                }
                catch { }
            calcCard.Visibility = calcValue == null ? Visibility.Collapsed : Visibility.Visible;
            if (calcValue != null) calcText.Text = "= " + calcValue;
        }

        // Windows settings pages, matched by name like apps; shown with a gear icon.
        static readonly string[] SettingList = {
            "Pantalla|display", "Sonido|sound", "Bluetooth y dispositivos|bluetooth", "Red e Internet|network", "Wi-Fi|network-wifi",
            "Personalización|personalization", "Fondo de pantalla|personalization-background", "Colores y modo oscuro|colors",
            "Aplicaciones instaladas|appsfeatures", "Aplicaciones predeterminadas|defaultapps", "Aplicaciones de inicio|startupapps",
            "Cuentas|yourinfo", "Hora e idioma|dateandtime", "Juegos|gaming-gamebar", "Accesibilidad|easeofaccess", "Privacidad|privacy",
            "Windows Update|windowsupdate", "Energía y batería|powersleep", "Almacenamiento|storagesense", "Notificaciones|notifications",
            "Impresoras y escáneres|printers", "Mouse|mousetouchpad", "Teclado|typing", "Acerca de|about" };

        List<Node> Settings()
        {
            if (settings == null)
                settings = SettingList.Select(s => { var p = s.Split('|'); return new Node { Name = "Ajustes: " + p[0], Uri = "ms-settings:" + p[1] }; }).ToList();
            foreach (var s in settings) if (s.Icon == null) s.SetIconQuiet(GearIcon());
            return settings;
        }

        ImageSource GearIcon()
        {
            if (gear != null) return gear;
            var ft = new FormattedText("", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 44, (Brush)win.Resources["cAccent"]);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawText(ft, new Point((64 - ft.Width) / 2, (64 - ft.Height) / 2));
            var rtb = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32); rtb.Render(dv); rtb.Freeze();
            return gear = rtb;
        }

        // User files come from the Windows search index, off the UI thread; they are appended when ready.
        void FindFiles(string raw, List<Node> progs, List<Node> sets)
        {
            if (raw.Length < 2 || raw.IndexOfAny(new[] { '%', '[', '_' }) >= 0 || Environment.GetEnvironmentVariable("NEXO_DEMO") == "1") return; // demo (docs screenshots): never show the real file index
            int gen = fileGen; string like = raw.Replace("'", "''"), home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            ThreadPool.QueueUserWorkItem(delegate
            {
                var found = new List<Node>();
                Thread.Sleep(250); if (gen != fileGen) return; // typing on: skip the query for stale text
                try
                {
                    using (var c = new System.Data.OleDb.OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows'"))
                    {
                        c.Open();
                        var cmd = new System.Data.OleDb.OleDbCommand("SELECT TOP 8 System.ItemUrl FROM SystemIndex WHERE SCOPE='file:" + home
                            + "' AND System.FileName LIKE '" + like + "%' ORDER BY System.DateModified DESC", c);
                        using (var rd = cmd.ExecuteReader())
                            while (rd.Read()) { string p = rd.GetString(0).Replace("file:", "").Replace('/', '\\'); // ItemUrl is "file:C:/Users/..." (ItemPathDisplay is localised, not a real path)
                                found.Add(new Node { Name = Path.GetFileName(p), Id = "file:" + p, Uri = p }); }
                    }
                }
                catch { }
                if (found.Count == 0) return;
                ui.BeginInvoke(new Action(delegate
                {
                    if (gen != fileGen) return; // the query changed meanwhile
                    var sel = resList.SelectedItem as Node;
                    resList.ItemsSource = Sections(progs, sets, found);
                    if (sel != null) resList.SelectedItem = sel; else SelectFirst();
                    noRes.Visibility = Visibility.Collapsed;
                }));
            });
        }

        // ----- launching + context menu -----

        static void Run(string target, string args) { try { Process.Start(target, args); } catch { } }

        static readonly Dictionary<string, string> KnownFolders = new Dictionary<string, string> {
            { "{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}", Environment.GetEnvironmentVariable("ProgramFiles(x86)") },
            { "{6D809377-6AF0-444B-8957-A3773F02200E}", Environment.GetEnvironmentVariable("ProgramW6432") },
            { "{905E63B6-C1BF-494E-B29C-65B732D3D21A}", Environment.GetEnvironmentVariable("ProgramFiles") },
            { "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}", Environment.SystemDirectory },
            { "{F38BF404-1D43-42F2-9305-67DE0B28FC23}", Environment.GetEnvironmentVariable("SystemRoot") } };

        // Where an item lives, or null if there is nothing to show (Store apps, Windows settings...).
        string Locate(Node n)
        {
            if (n.Uri != null) return File.Exists(n.Uri) || Directory.Exists(n.Uri) ? n.Uri : null;
            string id = n.Id ?? "";
            if (id.Contains("!")) return null; // Store app: no folder to open
            int end = id.IndexOf("}\\");
            string dir;
            if (id.StartsWith("{") && end > 0 && KnownFolders.TryGetValue(id.Substring(0, end + 1).ToUpperInvariant(), out dir) && dir != null)
                id = Path.Combine(dir, id.Substring(end + 2));
            if (File.Exists(id)) return id;
            // otherwise the Start Menu shortcut with the app's name -> its target
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
                try
                {
                    foreach (var lnk in Directory.EnumerateFiles(root, n.Name + ".lnk", SearchOption.AllDirectories))
                    {
                        string t = LinkTarget(lnk);
                        return t != null && File.Exists(t) ? t : lnk;
                    }
                }
                catch { }
            return null;
        }

        static string LinkTarget(string lnk)
        {
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell"); object sh = Activator.CreateInstance(t);
                object sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
                return (string)sc.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, sc, null);
            }
            catch { return null; }
        }

        static void Run(string target, bool admin = false)
        {
            try
            {
                var psi = new ProcessStartInfo(target) { UseShellExecute = true };
                if (admin) psi.Verb = "runas";
                Process.Start(psi);
            }
            catch { }
        }

        void Activate(Node n)
        {
            if (n.IsHeader) return;
            if (n.IsFolder) { OpenFolder(n, false); return; }
            if (n.Uri != null) { Launch(n); Run(n.Uri); return; }
            CountUse(n);
            Launch(n); Run("shell:AppsFolder\\" + n.Id);
        }

        // The pressed tile swells and fades while the menu sinks and fades out behind it.
        void Launch(Node n)
        {
            FrameworkElement t = null;
            foreach (var x in Tiles(root)) if (x.DataContext == n && x.IsVisible) { t = x; break; }
            if (t == null || !shown) { Hide(); return; }
            var oldTf = t.RenderTransform; var oldOrigin = t.RenderTransformOrigin;
            var st = new ScaleTransform(1, 1); t.RenderTransformOrigin = new Point(0.5, 0.5); t.RenderTransform = st;
            var ms = TimeSpan.FromMilliseconds(260); var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var grow = new DoubleAnimation(1, 1.28, ms) { EasingFunction = ease };
            grow.Completed += delegate { t.BeginAnimation(UIElement.OpacityProperty, null); t.RenderTransform = oldTf; t.RenderTransformOrigin = oldOrigin; };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, grow); st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            t.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, ms) { BeginTime = TimeSpan.FromMilliseconds(60), EasingFunction = ease });
            contentTf.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 14, ms) { EasingFunction = ease });
            Hide(240);
        }

        void CountUse(Node n)
        {
            if (n.Id == null) return;
            if (prefs.Uses == null) prefs.Uses = new Dictionary<string, int>();
            int c; prefs.Uses.TryGetValue(n.Id, out c); prefs.Uses[n.Id] = c + 1;
            SavePrefs();
        }

        int Uses(Node n) { int c = 0; if (prefs.Uses != null && n.Id != null) prefs.Uses.TryGetValue(n.Id, out c); return c; }

        static FrameworkElement TileOf(object src)
        {
            var d = src as DependencyObject;
            while (d != null)
            {
                var fe = d as FrameworkElement;
                if (fe != null && (fe.Tag as string) == "tile") return fe;
                d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }
        static Node NodeAt(object src) { var t = TileOf(src); return t == null ? null : t.DataContext as Node; }

        ContextMenu NewMenu(FrameworkElement target) { return UiUtil.NewMenu(win, target); }
        void Add(ContextMenu cm, string header, Action a) { UiUtil.Add(win, cm, header, a); }

        void OnRightClick(object s, MouseButtonEventArgs e)
        {
            var tile = TileOf(e.OriginalSource);
            if (tile == null || !(tile.DataContext is Node)) return;
            ShowTileMenu(tile, PlacementMode.MousePoint);
            e.Handled = true;
        }

        ContextMenu ShowTileMenu(FrameworkElement tile, PlacementMode where)
        {
            var n = (Node)tile.DataContext;
            if (n.IsHeader) return null;
            var cm = NewMenu(tile); cm.Placement = where;
            var pinned = n.IsFolder ? n : FindPinned(n.Id);
            Add(cm, "Abrir", delegate { Activate(n); });
            string loc = n.IsFolder ? null : Locate(n);
            if (loc != null) Add(cm, "Abrir ubicación", delegate { Hide(); Run("explorer.exe", "/select,\"" + loc + "\""); });
            if (n.IsFolder)
            {
                Add(cm, "Cambiar nombre", delegate { OpenFolder(n, true); });
                Add(cm, "Deshacer carpeta", delegate
                {
                    int i = pins.IndexOf(n); pins.RemoveAt(i);
                    foreach (var c in n.Items.ToList()) pins.Insert(i++, c);
                    Tidy();
                });
            }
            else if (n.Uri == null)
            {
                Add(cm, "Ejecutar como administrador", delegate { Hide(); Run("shell:AppsFolder\\" + n.Id, true); });
                if (pinned == null) Add(cm, "Fijar a Inicio", delegate { Pin(n); Tidy(); });
                else
                {
                    Add(cm, "Desanclar de Inicio", delegate { ParentOf(pinned).Remove(pinned); Tidy(); });
                    if (ParentOf(pinned) != pins) Add(cm, "Quitar de la carpeta", delegate { ParentOf(pinned).Remove(pinned); pins.Add(pinned); Tidy(); });
                }
            }
            cm.IsOpen = true;
            return cm;
        }

        void PowerMenu()
        {
            var cm = NewMenu(btnPower); cm.Placement = PlacementMode.Bottom;
            Add(cm, "Reiniciar", delegate { Hide(); Run("shutdown.exe /r /t 0"); });
            Add(cm, "Apagar", delegate { Hide(); Run("shutdown.exe /s /t 0"); });
            cm.IsOpen = true;
        }

        // ----- preferences: icon size + profile picture -----

        void DockLink(string button, string path) { F<Button>(button).Click += delegate { Hide(); Run(path); }; }

        string PrefsFile { get { return Path.Combine(dir, "prefs.json"); } }

        void LoadPrefs()
        {
            try
            {
                using (var fs = File.OpenRead(PrefsFile)) prefs = (Prefs)new DataContractJsonSerializer(typeof(Prefs)).ReadObject(fs);
            }
            catch { prefs = new Prefs(); }
        }

        void SavePrefs()
        {
            try
            {
                Directory.CreateDirectory(dir);
                using (var fs = File.Create(PrefsFile)) new DataContractJsonSerializer(typeof(Prefs)).WriteObject(fs, prefs);
            }
            catch { }
        }

        void SetSize(int level, bool save)
        {
            level = Math.Max(0, Math.Min(Sizes.Length - 1, level));
            prefs.Size = level;
            // tile pitch stays 100 px whatever the spacing, so 8 tiles per row always line up
            win.Resources["TileW"] = 100 - 2 * Margins[prefs.Density % Margins.Length]; win.Resources["TileH"] = Sizes[level][1];
            win.Resources["IconS"] = Sizes[level][2]; win.Resources["MiniS"] = Math.Round(Sizes[level][2] * 0.36);
            F<TextBlock>("SizeLabel").Text = SizeNames[level];
            if (save) SavePrefs();
        }

        void PickAvatar()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Elige tu imagen de perfil", Filter = "Imágenes|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp" };
            busy = true;
            bool ok = dlg.ShowDialog(win) == true;
            busy = false;
            if (shown) ForceForeground();
            if (!ok) return;
            try
            {
                Directory.CreateDirectory(dir);
                foreach (var old in Directory.GetFiles(dir, "avatar.*")) File.Delete(old);
                prefs.Avatar = "avatar" + Path.GetExtension(dlg.FileName).ToLowerInvariant();
                File.Copy(dlg.FileName, Path.Combine(dir, prefs.Avatar), true);
                SavePrefs(); ApplyAvatar();
            }
            catch { }
        }

        void ApplyAvatar()
        {
            try
            {
                if (string.IsNullOrEmpty(prefs.Avatar)) return;
                var bi = new BitmapImage();
                bi.BeginInit(); bi.UriSource = new Uri(Path.Combine(dir, prefs.Avatar)); bi.DecodePixelWidth = 120;
                bi.CacheOption = BitmapCacheOption.OnLoad; bi.EndInit(); bi.Freeze(); // OnLoad: don't keep the file locked
                avatar.Background = new ImageBrush(bi) { Stretch = Stretch.UniformToFill };
                userInitial.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        // ----- pins: model + persistence -----

        // Every node that can show an app icon: pins, the items inside folders, and the scanned app list.
        IEnumerable<Node> IconNodes()
        {
            foreach (var p in pins) { yield return p; if (p.IsFolder) foreach (var c in p.Items) yield return c; }
            foreach (var a in apps) yield return a;
        }

        // Tray menu "Actualizar iconos": drop all cached icons and read them again.
        public void ResetIcons()
        {
            Icons.Reset();
            foreach (var n in IconNodes()) if (!n.IsFolder) n.Icon = null; // the next read of Icon asks for a fresh one
        }

        Node FindPinned(string id)
        {
            foreach (var p in pins)
            {
                if (!p.IsFolder) { if (p.Id == id) return p; }
                else foreach (var c in p.Items) if (c.Id == id) return c;
            }
            return null;
        }

        ObservableCollection<Node> ParentOf(Node n)
        {
            if (pins.Contains(n)) return pins;
            foreach (var f in pins) if (f.IsFolder && f.Items.Contains(n)) return f.Items;
            return null;
        }

        void Pin(Node app) { if (FindPinned(app.Id) == null) pins.Add(new Node { Name = app.Name, Id = app.Id }); }

        void Tidy()
        {
            foreach (var f in pins.Where(p => p.IsFolder && p.Items.Count == 0).ToList()) pins.Remove(f);
            if (openFolder != null && !pins.Contains(openFolder)) CloseFolder();
            Save();
        }

        ObservableCollection<Node> LoadPins()
        {
            try
            {
                using (var fs = File.OpenRead(LayoutFile))
                    return (ObservableCollection<Node>)new DataContractJsonSerializer(typeof(ObservableCollection<Node>)).ReadObject(fs);
            }
            catch { return null; }
        }

        void Save()
        {
            try
            {
                Directory.CreateDirectory(dir);
                using (var fs = File.Create(LayoutFile)) new DataContractJsonSerializer(typeof(ObservableCollection<Node>)).WriteObject(fs, pins);
            }
            catch { }
        }

        // ----- folders -----

        void OpenFolder(Node f, bool rename)
        {
            openFolder = f; folderTitle.DataContext = f; folderList.ItemsSource = f.Items;
            folderOverlay.Visibility = Visibility.Visible; UiUtil.PopIn(folderOverlay);
            folderOverlay.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)));
            if (rename) { folderTitle.Focus(); folderTitle.SelectAll(); } else search.Focus();
        }

        void CloseFolder()
        {
            if (openFolder == null) return;
            var be = folderTitle.GetBindingExpression(TextBox.TextProperty); if (be != null) be.UpdateSource();
            openFolder = null; Save(); search.Focus();
            if (shown) UiUtil.PopOut(folderOverlay);
            else { folderOverlay.BeginAnimation(UIElement.OpacityProperty, null); folderOverlay.Visibility = Visibility.Collapsed; }
        }

        // ----- drag & drop on the pinned grid -----

        void PinDown(object s, MouseButtonEventArgs e) { downNode = NodeAt(e.OriginalSource); downPos = e.GetPosition(win); }

        void PinMove(object s, MouseEventArgs e)
        {
            if (downNode == null || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(win);
            if (Math.Abs(p.X - downPos.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - downPos.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var n = downNode; downNode = null;
            CloseFolder(); // so the pinned grid is reachable as a drop target
            FrameworkElement tile = null; DragGhost ghost = null;
            foreach (var t in Tiles(root)) if (t.DataContext == n && t.IsVisible) { tile = t; break; }
            if (tile != null)
            {
                try { ghost = new DragGhost(tile, HwndSource.FromHwnd(hwnd).CompositionTarget.TransformFromDevice); } catch { }
                tile.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(150))); // the tile "leaves" its slot
            }
            GiveFeedbackEventHandler fb = delegate(object o, GiveFeedbackEventArgs ev)
            {
                if (ghost == null) return;
                ev.UseDefaultCursors = false; Mouse.SetCursor(Cursors.Arrow); ev.Handled = true; ghost.Move();
            };
            QueryContinueDragEventHandler qc = delegate { if (ghost != null) ghost.Move(); };
            pinScroll.GiveFeedback += fb; pinScroll.QueryContinueDrag += qc;
            try { DragDrop.DoDragDrop(pinScroll, new DataObject("SMX", n), DragDropEffects.Move); }
            finally
            {
                pinScroll.GiveFeedback -= fb; pinScroll.QueryContinueDrag -= qc;
                if (ghost != null) ghost.Close();
                if (tile != null) tile.BeginAnimation(UIElement.OpacityProperty, null);
            }
            SetTarget(null, 0);
        }

        void PinUp(object s, MouseButtonEventArgs e)
        {
            var n = NodeAt(e.OriginalSource);
            if (n != null && n == downNode) { downNode = null; Activate(n); } else downNode = null;
        }

        void SetTarget(Node n, int mode)
        {
            if (lastTarget != null && lastTarget != n) lastTarget.DropMode = 0;
            if (n != null) n.DropMode = mode;
            lastTarget = n; lastMode = mode;
        }

        // 1 = insert before, 2 = insert after, 3 = merge into folder (edges reorder, middle merges)
        int Zone(FrameworkElement tile, DragEventArgs e, Node src)
        {
            double x = e.GetPosition(tile).X / tile.ActualWidth;
            if (!src.IsFolder && x >= 0.28 && x <= 0.72) return 3;
            return x < 0.5 ? 1 : 2;
        }

        void PinDragOver(object s, DragEventArgs e)
        {
            e.Handled = true; e.Effects = DragDropEffects.None;
            var src = e.Data.GetData("SMX") as Node; if (src == null) return;
            e.Effects = DragDropEffects.Move;
            var t = TileOf(e.OriginalSource); var tn = t == null ? null : t.DataContext as Node;
            if (tn == null || tn == src) SetTarget(null, 0); else SetTarget(tn, Zone(t, e, src));
        }

        void PinDrop(object s, DragEventArgs e)
        {
            var src = e.Data.GetData("SMX") as Node; var tn = lastTarget; int mode = lastMode;
            SetTarget(null, 0);
            e.Handled = true;
            if (src == null) return;
            var from = ParentOf(src); if (from == null) return;

            if (tn == null) { from.Remove(src); pins.Add(src); }
            else if (mode == 3 && tn.IsFolder) { from.Remove(src); tn.Items.Add(src); }
            else if (mode == 3)
            {
                from.Remove(src);
                var f = new Node { Name = "Carpeta", Items = new ObservableCollection<Node>() };
                f.Items.Add(tn); f.Items.Add(src);
                pins[pins.IndexOf(tn)] = f;
            }
            else
            {
                from.Remove(src);
                int i = pins.IndexOf(tn);
                pins.Insert(mode == 2 ? i + 1 : i, src);
            }
            Tidy();
        }
    }

    // ---------- entry point + tray ----------

    static class Program
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        [STAThread]
        static void Main(string[] args)
        {
            // Test mode: StartMenuX.exe --shot out.png [--theme light|dark] [--search text] [--all]
            string shot = null, theme = null, query = null, popup = null; bool all = false, mixer = false, click = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--shot" && i + 1 < args.Length) shot = args[++i];
                else if (args[i] == "--theme" && i + 1 < args.Length) theme = args[++i];
                else if (args[i] == "--search" && i + 1 < args.Length) query = args[++i];
                else if (args[i] == "--all") all = true;
                else if (args[i] == "--mixer") mixer = true;
                else if (args[i] == "--click") click = true;
                else if (args[i] == "--hold" && i + 1 < args.Length) MenuApp.TestHold = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                else if (args[i] == "--scroll") MenuApp.TestScroll = true;
                else if (args[i] == "--type" && i + 1 < args.Length) MenuApp.TestType = args[++i];
                else if (args[i] == "--bg" && i + 1 < args.Length) MenuApp.TestBg = args[++i];
                else if (args[i] == "--look" && i + 1 < args.Length) MenuApp.TestLook = args[++i];
                else if (args[i] == "--menu" && i + 1 < args.Length) popup = args[++i];
                else if (args[i] == "--after" && i + 1 < args.Length) // relaunch: wait for the previous instance to exit
                    try { Process.GetProcessById(int.Parse(args[++i])).WaitForExit(8000); } catch { }
            }
            if (shot != null)
            {
                var a = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                new MenuApp(theme).Shot(shot, query, all, mixer, popup, click, () => a.Shutdown());
                a.Run();
                return;
            }

            bool first;
            using (var mutex = new Mutex(true, "Nexo_single_instance", out first))
            {
                if (!first) return;
                MigrateAutostart();
                var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                Action<object> fail = ex => { try { File.AppendAllText(Path.Combine(AppDir.Path, "debug.log"), DateTime.Now.ToString("HH:mm:ss.fff ") + "ERROR " + ex + "\r\n"); } catch { } };
                app.DispatcherUnhandledException += (s, e) => { fail(e.Exception); e.Handled = true; }; // a UI hiccup must not take the whole menu down
                AppDomain.CurrentDomain.UnhandledException += (s, e) => fail(e.ExceptionObject);
                var menu = new MenuApp(null);
                var ui = Dispatcher.CurrentDispatcher;
                Kbd.OnWin = () => ui.BeginInvoke(new Action(menu.Toggle)); // return from the hook fast
                Kbd.Install();
                Kbd.OnStartClick = (x, y) => { if (!StartButton.Hit(x, y)) return false; ui.BeginInvoke(new Action(menu.Toggle)); return true; };
                StartButton.Watch();
                menu.Warm();

                var tray = new WF.NotifyIcon { Icon = SD.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName), Text = "Nexo", Visible = true };
                var autostart = new WF.ToolStripMenuItem("Iniciar con Windows") { Checked = IsAutostart() };
                autostart.Click += delegate { SetAutostart(!autostart.Checked); autostart.Checked = IsAutostart(); };
                var cm = new WF.ContextMenuStrip();
                cm.Items.Add("Abrir", null, delegate { menu.Toggle(); });
                cm.Items.Add(autostart);
                cm.Items.Add("Actualizar iconos", null, delegate { menu.ResetIcons(); });
                cm.Items.Add("Salir", null, delegate { tray.Visible = false; app.Shutdown(); });
                tray.ContextMenuStrip = cm;
                tray.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) menu.Toggle(); };

                app.Run();
                tray.Dispose();
            }
        }

        static bool IsAutostart()
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("Nexo") != null;
        }

        // The StartMenuX autostart entry points at the old exe: replace it with one for this exe.
        static void MigrateAutostart()
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                if (k != null && k.GetValue("StartMenuX") != null) { k.DeleteValue("StartMenuX", false); SetAutostart(true); }
        }

        static void SetAutostart(bool on)
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (on) k.SetValue("Nexo", "\"" + Process.GetCurrentProcess().MainModule.FileName + "\"");
                else k.DeleteValue("Nexo", false);
            }
        }
    }
}
