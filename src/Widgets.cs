using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SMX
{
    // One slider row: master volume, an app in the mixer, or a monitor's brightness.
    public class VolItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        void Raise(string p) { var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(p)); }

        double vol; bool muted, silent;
        public string Key; public object Tag;
        public Action<double> OnVol; public Action<bool> OnMute;

        // properties, not fields: WPF bindings only see properties
        public string Name { get; set; }
        public string Badge { get; set; }
        public string Info { get; set; }
        public ImageSource Icon { get; set; }
        public double Volume
        {
            get { return vol; }
            set { if (vol == value) return; vol = value; Raise("Volume"); if (!silent && OnVol != null) OnVol(value); }
        }
        public bool Muted
        {
            get { return muted; }
            set { if (muted == value) return; muted = value; Raise("Muted"); if (!silent && OnMute != null) OnMute(value); }
        }
        // Values read back from the system must not be written back to it.
        public void SetFromSystem(double v, bool m) { silent = true; Volume = v; Muted = m; silent = false; }
    }

    public static class UiUtil
    {
        // A view that becomes visible fades in and rises a little.
        public static void Enter(FrameworkElement el, double dy)
        {
            var tf = el.RenderTransform as TranslateTransform;
            if (tf == null) el.RenderTransform = tf = new TranslateTransform();
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            tf.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        // A dialog overlay fades in while its card grows from 94 % with a small overshoot.
        // ... and leaves the same way: fades while the card shrinks a little, then collapses (unless it was opened again meanwhile).
        public static void PopOut(Grid overlay, Action done = null)
        {
            if (overlay.Visibility != Visibility.Visible || !overlay.IsHitTestVisible) return;
            overlay.IsHitTestVisible = false;
            var card = overlay.Children.Count > 0 ? overlay.Children[0] as FrameworkElement : null;
            var sc = card == null ? null : card.RenderTransform as ScaleTransform;
            if (sc != null)
            {
                var s = new DoubleAnimation(0.96, TimeSpan.FromMilliseconds(110));
                sc.BeginAnimation(ScaleTransform.ScaleXProperty, s); sc.BeginAnimation(ScaleTransform.ScaleYProperty, s);
            }
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
            fade.Completed += delegate
            {
                if (overlay.IsHitTestVisible) return; // reopened during the fade
                overlay.BeginAnimation(UIElement.OpacityProperty, null); overlay.Visibility = Visibility.Collapsed;
                if (done != null) done();
            };
            overlay.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        public static void PopIn(Grid overlay)
        {
            overlay.IsHitTestVisible = true;
            overlay.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
            var card = overlay.Children.Count > 0 ? overlay.Children[0] as FrameworkElement : null; if (card == null) return;
            var sc = card.RenderTransform as ScaleTransform;
            if (sc == null) { card.RenderTransformOrigin = new Point(0.5, 0.5); card.RenderTransform = sc = new ScaleTransform(1, 1); }
            var a = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } };
            sc.BeginAnimation(ScaleTransform.ScaleXProperty, a); sc.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        // Counts open context menus so the main window does not close itself under them.
        public static int MenusOpen;

        // Menus built in code are outside the window's tree and cannot see its theme brushes, so they get a copy.
        public static ContextMenu NewMenu(Window win, FrameworkElement target)
        {
            var cm = new ContextMenu { Style = (Style)win.Resources["Ctx"], PlacementTarget = target };
            foreach (var k in win.Resources.Keys) cm.Resources[k] = win.Resources[k];
            cm.Opened += delegate { MenusOpen++; };
            cm.Closed += delegate { win.Dispatcher.BeginInvoke(new Action(delegate { MenusOpen--; })); };
            return cm;
        }

        public static void Add(Window win, ContextMenu cm, string header, Action a)
        {
            var mi = new MenuItem { Header = header, Style = (Style)win.Resources["Mi"] };
            mi.Click += delegate { a(); };
            cm.Items.Add(mi);
        }
    }
    // ---------- Core Audio ----------

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCls { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator { void EnumAudioEndpoints(int f, int m, out IntPtr d); void GetDefaultAudioEndpoint(int flow, int role, out IMMDevice dev); }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice { void Activate(ref Guid iid, uint ctx, IntPtr p, [MarshalAs(UnmanagedType.Interface)] out object o); }
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2 { void A(); void B(); void GetSessionEnumerator(out IAudioSessionEnumerator e); }
    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator { void GetCount(out int n); void GetSession(int i, out IAudioSessionControl2 s); }
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        void GetState(out int s); void A(); void B(); void C(); void D(); void E(); void F(); void G(); void H();
        void I(); void J(); void GetProcessId(out uint pid); [PreserveSig] int IsSystemSoundsSession(); void K();
    }
    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume { void SetMasterVolume(float v, ref Guid ctx); void GetMasterVolume(out float v); void SetMute(bool m, ref Guid ctx); void GetMute(out bool m); }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        void A(); void B(); void C(); void D();
        void SetMasterVolumeLevelScalar(float v, ref Guid ctx); void GetMasterVolumeLevel(out float db); void GetMasterVolumeLevelScalar(out float v);
        void E(); void F(); void G(); void H();
        void SetMute(bool m, ref Guid ctx); void GetMute(out bool m);
    }

    class AudioCtl
    {
        IAudioEndpointVolume ep; IAudioSessionManager2 mgr;
        public readonly VolItem Master = new VolItem { Name = "Volumen" };
        class ProcInfo { public string Key, Name; public ImageSource Icon; }
        static readonly Dictionary<uint, ProcInfo> procs = new Dictionary<uint, ProcInfo>();

        public AudioCtl()
        {
            Master.OnVol = v => { Guid g = Guid.Empty; try { ep.SetMasterVolumeLevelScalar((float)(v / 100), ref g); } catch { ep = null; } };
            Master.OnMute = m => { Guid g = Guid.Empty; try { ep.SetMute(m, ref g); } catch { ep = null; } };
            Open();
        }

        // The default output can change (headphones, HDMI): re-open when a call fails.
        bool Open()
        {
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCls();
                IMMDevice dev; en.GetDefaultAudioEndpoint(0, 1, out dev);
                object o; Guid g1 = typeof(IAudioEndpointVolume).GUID, g2 = typeof(IAudioSessionManager2).GUID;
                dev.Activate(ref g1, 23, IntPtr.Zero, out o); ep = (IAudioEndpointVolume)o;
                dev.Activate(ref g2, 23, IntPtr.Zero, out o); mgr = (IAudioSessionManager2)o;
                return true;
            }
            catch { ep = null; mgr = null; return false; }
        }

        public void PollMaster()
        {
            try
            {
                if (ep == null && !Open()) return;
                float v; bool m; ep.GetMasterVolumeLevelScalar(out v); ep.GetMute(out m);
                Master.SetFromSystem(Math.Round(v * 100), m);
            }
            catch { ep = null; }
        }

        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, System.Text.StringBuilder sb, ref int size);

        static string PathOf(uint pid)
        {
            IntPtr h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return null;
            try { var sb = new System.Text.StringBuilder(520); int n = sb.Capacity; return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null; }
            finally { CloseHandle(h); }
        }

        static ProcInfo Info(uint pid, bool system)
        {
            ProcInfo pi;
            if (procs.TryGetValue(pid, out pi)) return pi;
            pi = new ProcInfo { Key = "pid" + pid, Name = "Programa " + pid };
            if (system) { pi.Key = "#sys"; pi.Name = "Sonidos del sistema"; }
            else
                try
                {
                    var p = Process.GetProcessById((int)pid);
                    pi.Key = p.ProcessName.ToLowerInvariant(); pi.Name = p.ProcessName;
                    string path = null; try { path = p.MainModule.FileName; } catch { }
                    if (path == null) path = PathOf(pid); // elevated / protected processes refuse MainModule
                    if (path != null)
                    {
                        var d = FileVersionInfo.GetVersionInfo(path).FileDescription;
                        if (!string.IsNullOrWhiteSpace(d)) pi.Name = d.Trim();
                        using (var ic = System.Drawing.Icon.ExtractAssociatedIcon(path))
                        {
                            var bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(ic.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            bs.Freeze(); pi.Icon = bs;
                        }
                    }
                }
                catch { }
            procs[pid] = pi;
            return pi;
        }

        // Mixer rows are keyed by program (a browser can own several sessions: they move together).
        public void SyncSessions(ObservableCollection<VolItem> list, bool dragging)
        {
            if (mgr == null && !Open()) return;
            var seen = new List<string>();
            try
            {
                IAudioSessionEnumerator se; mgr.GetSessionEnumerator(out se);
                int n; se.GetCount(out n);
                var groups = new Dictionary<string, List<ISimpleAudioVolume>>();
                var infos = new Dictionary<string, ProcInfo>();
                for (int i = 0; i < n; i++)
                {
                    IAudioSessionControl2 c; se.GetSession(i, out c);
                    int state; c.GetState(out state);
                    bool sys = c.IsSystemSoundsSession() == 0;
                    if (state == 2 || (sys && state != 1)) continue; // expired, or idle system sounds
                    uint pid; c.GetProcessId(out pid);
                    var pi = Info(pid, sys);
                    List<ISimpleAudioVolume> l;
                    if (!groups.TryGetValue(pi.Key, out l)) { groups[pi.Key] = l = new List<ISimpleAudioVolume>(); infos[pi.Key] = pi; seen.Add(pi.Key); }
                    l.Add((ISimpleAudioVolume)c);
                }
                foreach (var key in seen)
                {
                    var vols = groups[key]; float v; bool m; vols[0].GetMasterVolume(out v); vols[0].GetMute(out m);
                    var item = list.FirstOrDefault(x => x.Key == key);
                    if (item == null)
                    {
                        var pi = infos[key];
                        item = new VolItem { Key = key, Name = pi.Name, Icon = pi.Icon };
                        var it = item;
                        item.OnVol = nv => { Guid g = Guid.Empty; foreach (var s in (List<ISimpleAudioVolume>)it.Tag) try { s.SetMasterVolume((float)(nv / 100), ref g); } catch { } };
                        item.OnMute = nm => { Guid g = Guid.Empty; foreach (var s in (List<ISimpleAudioVolume>)it.Tag) try { s.SetMute(nm, ref g); } catch { } };
                        item.Tag = vols; item.SetFromSystem(Math.Round(v * 100), m);
                        list.Add(item);
                    }
                    else
                    {
                        item.Tag = vols;
                        if (!dragging) item.SetFromSystem(Math.Round(v * 100), m);
                    }
                }
            }
            catch { mgr = null; }
            foreach (var gone in list.Where(x => !seen.Contains(x.Key)).ToList()) list.Remove(gone);
        }
    }

    // ---------- system media session (Windows' own "now playing": browsers, Twitch, Spotify...) ----------
    // WinRT types are reached by reflection so the project still builds with just the .NET Framework compiler.

    class MediaCtl
    {
        public class State { public bool Has, Playing; public string Title = "", Artist = ""; public byte[] Thumb; }
        class Cand { public object S, Props; public string Key, Title, Artist; public bool Playing; }

        const string Ns = "Windows.Media.Control.GlobalSystemMediaTransportControlsSession";
        static Type T(string n, string asm) { return Type.GetType(n + ", " + asm + ", ContentType=WindowsRuntime"); }
        static readonly Type tMgr = T(Ns + "Manager", "Windows.Media"), tProps = T(Ns + "MediaProperties", "Windows.Media");
        static readonly Type tRef = T("Windows.Storage.Streams.IRandomAccessStreamReference", "Windows.Storage");
        static readonly Type tRas = T("Windows.Storage.Streams.IRandomAccessStreamWithContentType", "Windows.Storage");
        static readonly Type tIn = T("Windows.Storage.Streams.IInputStream", "Windows.Storage");

        object mgr, selected; string selectedKey, lastKey; byte[] lastThumb; int thumbTries;
        readonly Dictionary<string, DateTime> since = new Dictionary<string, DateTime>(); // when each media started playing

        static object Call(object o, string name) { return o.GetType().GetMethod(name).Invoke(o, null); }
        static object Get(object o, string prop) { return o.GetType().GetProperty(prop).GetValue(o, null); }
        static object Await(object op, Type result)
        {
            var m = typeof(System.WindowsRuntimeSystemExtensions).GetMethods().First(x => x.Name == "AsTask" && x.IsGenericMethodDefinition
                        && x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType.Name == "IAsyncOperation`1");
            var task = m.MakeGenericMethod(result).Invoke(null, new object[] { op });
            return task.GetType().GetProperty("Result").GetValue(task, null);
        }

        // Blocks on WinRT calls: run off the UI thread.
        // Windows' "current" session can lag behind what you are watching, so look at all of them
        // and show whichever started playing most recently.
        public State Poll()
        {
            var st = new State();
            try
            {
                if (mgr == null) mgr = Await(tMgr.GetMethod("RequestAsync").Invoke(null, null), tMgr);
                var cands = new List<Cand>();
                var sessions = ((System.Collections.IEnumerable)Call(mgr, "GetSessions")).Cast<object>().ToList();
                // start every properties request first so they run in parallel, then collect them
                var ops = sessions.Select(s => { try { return Call(s, "TryGetMediaPropertiesAsync"); } catch { return null; } }).ToList();
                for (int i = 0; i < sessions.Count; i++)
                    try
                    {
                        var s = sessions[i];
                        var p = Await(ops[i], tProps);
                        var c = new Cand { S = s, Props = p, Title = (string)Get(p, "Title") ?? "", Artist = (string)Get(p, "Artist") ?? "" };
                        c.Playing = Get(Call(s, "GetPlaybackInfo"), "PlaybackStatus").ToString() == "Playing";
                        c.Key = Get(s, "SourceAppUserModelId") + "|" + c.Title + "|" + c.Artist;
                        cands.Add(c);
                    }
                    catch { }

                var now = DateTime.UtcNow;
                foreach (var c in cands) if (c.Playing && !since.ContainsKey(c.Key)) since[c.Key] = now;
                foreach (var k in since.Keys.ToList()) if (!cands.Any(c => c.Playing && c.Key == k)) since.Remove(k);

                var pick = cands.Where(c => c.Playing).OrderByDescending(c => since[c.Key]).FirstOrDefault()
                        ?? cands.FirstOrDefault(c => c.Key == selectedKey) ?? cands.FirstOrDefault();
                if (pick == null) { selected = null; selectedKey = lastKey = null; return st; }

                selected = pick.S; selectedKey = pick.Key;
                st.Has = true; st.Title = pick.Title; st.Artist = pick.Artist; st.Playing = pick.Playing;
                if (pick.Key != lastKey) { lastKey = pick.Key; lastThumb = null; thumbTries = 0; }
                // Browsers publish the new title first and the art later (sometimes the previous track's art in between):
                // keep re-reading for a while after a change and take whatever differs.
                if (thumbTries++ < 12 || (lastThumb == null && thumbTries < 40))
                {
                    var t = ReadThumb(Get(pick.Props, "Thumbnail"));
                    if (t != null && (lastThumb == null || !t.SequenceEqual(lastThumb))) lastThumb = t;
                }
                st.Thumb = lastThumb;
            }
            catch { mgr = null; }
            return st;
        }

        static byte[] ReadThumb(object thumb)
        {
            if (thumb == null) return null;
            try
            {
                var ras = Await(tRef.GetMethod("OpenReadAsync").Invoke(thumb, null), tRas);
                var read = typeof(System.IO.WindowsRuntimeStreamExtensions).GetMethod("AsStreamForRead", new[] { tIn });
                using (var s = (Stream)read.Invoke(null, new[] { ras }))
                using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.Length > 0 ? ms.ToArray() : null; }
            }
            catch { return null; }
        }

        public void Send(string method)
        {
            try { var s = selected; if (s != null) Call(s, method); } catch { mgr = null; }
        }
    }

    // ---------- monitor brightness over DDC/CI ----------

    class DisplayCtl
    {
        // Number = the n in \\.\DISPLAYn (Windows can hand out 5, 6... after drivers change); Rank = 1, 2... in that order.
        public class Mon { public IntPtr H; public uint Max; public double Cur; public int Number, Rank; public string Info; }

        delegate bool EnumProc(IntPtr mon, IntPtr hdc, IntPtr rect, IntPtr data);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int l, t, r, b; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFOEX { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }
        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, EnumProc cb, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFOEX mi);
        [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr h, out uint n);
        [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr h, uint n, [Out] PM[] arr);
        [DllImport("dxva2.dll")] static extern bool GetMonitorBrightness(IntPtr h, out uint min, out uint cur, out uint max);
        [DllImport("dxva2.dll")] static extern bool SetMonitorBrightness(IntPtr h, uint v);
        [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitors(uint n, [In] PM[] arr);
        [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
        struct PM { public IntPtr h; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string desc; }

        readonly object gate = new object();
        List<PM[]> groups = new List<PM[]>();
        List<Mon> mons = new List<Mon>();
        readonly Dictionary<int, double> pending = new Dictionary<int, double>();
        readonly AutoResetEvent wake = new AutoResetEvent(false);

        public DisplayCtl()
        {
            // Each DDC write takes ~60 ms: a worker applies only the latest slider value per monitor.
            var t = new Thread(Work) { IsBackground = true }; t.Start();
        }

        // Monitors that answer DDC/CI, ordered by their Windows display number (DISPLAY1, DISPLAY2...).
        public List<Mon> Scan()
        {
            var found = new List<PM[]>(); var list = new List<Mon>(); var allNumbers = new List<int>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr mon, IntPtr hdc, IntPtr rc, IntPtr d)
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) };
                int number = 0; string info = "";
                if (GetMonitorInfo(mon, ref mi))
                {
                    var digits = new string(mi.szDevice.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray()); // "\\.\DISPLAY2" -> 2
                    int.TryParse(digits, out number);
                    info = "DISPLAY" + number + " · " + (mi.rcMonitor.r - mi.rcMonitor.l) + "×" + (mi.rcMonitor.b - mi.rcMonitor.t) + ((mi.dwFlags & 1) != 0 ? " · principal" : "");
                }
                allNumbers.Add(number); // counts monitors that don't answer DDC/CI too, so ranks match the real monitor order

                uint n; if (!GetNumberOfPhysicalMonitorsFromHMONITOR(mon, out n) || n == 0) return true;
                var arr = new PM[n]; if (!GetPhysicalMonitorsFromHMONITOR(mon, n, arr)) return true;
                found.Add(arr);
                foreach (var m in arr)
                {
                    uint min, cur, max;
                    if (GetMonitorBrightness(m.h, out min, out cur, out max) && max > min)
                        list.Add(new Mon { H = m.h, Max = max, Cur = Math.Round(100.0 * (cur - min) / (max - min)), Number = number, Info = info });
                }
                return true;
            }, IntPtr.Zero);
            allNumbers.Sort();
            foreach (var m in list) m.Rank = allNumbers.IndexOf(m.Number) + 1;
            list = list.OrderBy(m => m.Number).ToList();
            lock (gate)
            {
                foreach (var g in groups) DestroyPhysicalMonitors((uint)g.Length, g);
                groups = found; mons = list;
            }
            return list;
        }

        public void Set(int index, double pct)
        {
            lock (pending) pending[index] = pct;
            wake.Set();
        }

        void Work()
        {
            while (true)
            {
                wake.WaitOne(); Thread.Sleep(40);
                KeyValuePair<int, double>[] todo;
                lock (pending) { todo = pending.ToArray(); pending.Clear(); }
                foreach (var kv in todo)
                    lock (gate)
                        if (kv.Key < mons.Count) SetMonitorBrightness(mons[kv.Key].H, (uint)Math.Round(mons[kv.Key].Max * kv.Value / 100));
            }
        }
    }

    // ---------- the widget row ----------

    class WidgetsCtl
    {
        readonly Window win; readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
        readonly AudioCtl audio = new AudioCtl(); readonly MediaCtl media = new MediaCtl(); readonly DisplayCtl display = new DisplayCtl();
        readonly ObservableCollection<VolItem> mix = new ObservableCollection<VolItem>(), bright = new ObservableCollection<VolItem>();
        readonly DispatcherTimer tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        readonly Grid mixerOverlay, musicOn; readonly ScrollViewer scroll;
        readonly TextBlock mixNone, brightNone, mTitle, mArtist, musicOff;
        readonly Button btnPlay, btnWidgets, btnTab; readonly Border mThumb;
        readonly Action<bool> onHide;

        // Settings panel "Restablecer": widgets visible again.
        public void ResetAll() { SetHidden(false); onHide(false); }
        bool polling, pollSoon, running; byte[] shownThumb;

        T F<T>(string name) where T : class { return (T)win.FindName(name); }

        public bool MixerOpen { get { return mixerOverlay.Visibility == Visibility.Visible && mixerOverlay.IsHitTestVisible; } }

        public WidgetsCtl(Window w, bool hidden, Action<bool> onHide)
        {
            win = w; this.onHide = onHide;
            mixerOverlay = F<Grid>("MixerOverlay"); musicOn = F<Grid>("MusicOn"); scroll = F<ScrollViewer>("WidgetsScroll");
            mixNone = F<TextBlock>("MixNone"); brightNone = F<TextBlock>("BrightNone"); mTitle = F<TextBlock>("MTitle");
            mArtist = F<TextBlock>("MArtist"); musicOff = F<TextBlock>("MusicOff");
            btnPlay = F<Button>("BtnPlay"); btnWidgets = F<Button>("BtnWidgets"); btnTab = F<Button>("BtnWidgetTab");
            mThumb = F<Border>("MThumb");

            F<Border>("CardVol").DataContext = audio.Master;
            F<Border>("CardBright").Resources["PctVis"] = Visibility.Visible;
            F<ItemsControl>("MixList").ItemsSource = mix; F<ItemsControl>("BrightList").ItemsSource = bright;

            F<Button>("BtnMixer").Click += delegate { OpenMixer(); };
            F<Button>("BtnCloseMixer").Click += delegate { CloseMixer(true); };
            mixerOverlay.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { if (e.OriginalSource == mixerOverlay) CloseMixer(true); };

            F<Button>("BtnPrev").Click += delegate { Send("TrySkipPreviousAsync"); };
            btnPlay.Click += delegate { Send("TryTogglePlayPauseAsync"); };
            F<Button>("BtnNext").Click += delegate { Send("TrySkipNextAsync"); };

            // compact music card: cover on the left, one-line title, no artist line
            mArtist.Visibility = Visibility.Collapsed; mTitle.TextWrapping = TextWrapping.NoWrap; mTitle.MaxHeight = 18;

            SetHidden(hidden);
            EventHandler toggle = delegate { bool hide = scroll.Visibility == Visibility.Visible; SetHidden(hide); onHide(hide); };
            btnWidgets.Click += delegate { toggle(null, null); }; btnTab.Click += delegate { toggle(null, null); };

            tick.Tick += delegate { Tick(); };
        }

        void SetHidden(bool hide)
        {
            scroll.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
            btnWidgets.Content = hide ? "Mostrar widgets" : "Ocultar widgets";
            btnTab.Content = hide ? "\uE70E" : "\uE70D"; // chevron up / down
            btnTab.ToolTip = hide ? "Mostrar widgets" : "Ocultar widgets";
        }

        // NEXO_DEMO=1 (screenshots for the docs): fixed, harmless widget content instead of the real system state.
        void ShowDemo()
        {
            audio.Master.SetFromSystem(64, false);
            ApplyBrightness(new List<DisplayCtl.Mon> { new DisplayCtl.Mon { Rank = 1, Cur = 70, Info = "Demo display" }, new DisplayCtl.Mon { Rank = 2, Cur = 45, Info = "Demo display" } });
            byte[] png;
            using (var bmp = new System.Drawing.Bitmap(200, 200))
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            using (var ms = new MemoryStream())
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(System.Drawing.Color.FromArgb(36, 59, 107));
                using (var sun = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(242, 166, 90))) g.FillEllipse(sun, 50, 46, 100, 100);
                using (var sea = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(20, 33, 61))) g.FillRectangle(sea, 0, 110, 200, 90);
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png); png = ms.ToArray();
            }
            running = true;
            ApplyMedia(new MediaCtl.State { Has = true, Playing = true, Title = "Midnight Drive", Artist = "Aurora Lane", Thumb = png });
        }

        public void Shown()
        {
            if (Environment.GetEnvironmentVariable("NEXO_DEMO") == "1") { CloseMixer(); ShowDemo(); return; }
            CloseMixer(); running = true;
            audio.PollMaster(); tick.Start();
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<DisplayCtl.Mon> found = null;
                try { found = display.Scan(); } catch { }
                ui.BeginInvoke(new Action(delegate { if (found != null) ApplyBrightness(found); }));
            });
            PollMedia();
        }

        public void Hidden() { running = false; tick.Stop(); CloseMixer(); }

        void Tick()
        {
            if (Mouse.LeftButton != MouseButtonState.Pressed) // don't fight a slider being dragged
            {
                audio.PollMaster();
                if (MixerOpen) { audio.SyncSessions(mix, false); mixNone.Visibility = mix.Count == 0 ? Visibility.Visible : Visibility.Collapsed; }
            }
            pollSoon = false; PollMedia(); // PollMedia skips itself while a poll is still running
        }

        // ----- mixer -----

        void OpenMixer()
        {
            audio.SyncSessions(mix, false);
            mixNone.Visibility = mix.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            mixerOverlay.Visibility = Visibility.Visible; UiUtil.PopIn(mixerOverlay);
        }

        public bool CloseMixer(bool fade = false)
        {
            if (!MixerOpen) return false;
            if (fade) UiUtil.PopOut(mixerOverlay, delegate { mix.Clear(); });
            else { mixerOverlay.BeginAnimation(UIElement.OpacityProperty, null); mixerOverlay.Visibility = Visibility.Collapsed; mix.Clear(); }
            return true;
        }

        public void OpenMixerForTest() { OpenMixer(); }

        // ----- brightness -----

        void ApplyBrightness(List<DisplayCtl.Mon> found)
        {
            brightNone.Visibility = found.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!bright.Select(b => b.Info).SequenceEqual(found.Select(m => "Monitor " + m.Rank + " · " + m.Info)))
            {
                bright.Clear();
                for (int i = 0; i < found.Count; i++)
                {
                    int idx = i;
                    bright.Add(new VolItem { Name = "Monitor " + found[i].Rank, Badge = found[i].Rank.ToString(), Info = "Monitor " + found[i].Rank + " · " + found[i].Info, OnVol = v => display.Set(idx, v) });
                }
            }
            if (Mouse.LeftButton != MouseButtonState.Pressed)
                for (int i = 0; i < found.Count; i++) bright[i].SetFromSystem(found[i].Cur, false);
        }

        // ----- music -----

        void Send(string method)
        {
            ThreadPool.QueueUserWorkItem(delegate { media.Send(method); });
            pollSoon = true;
        }

        void PollMedia()
        {
            if (polling) return; polling = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                var st = media.Poll();
                ui.BeginInvoke(new Action(delegate { polling = false; if (running) ApplyMedia(st); }));
            });
        }

        void ApplyMedia(MediaCtl.State s)
        {
            musicOn.Visibility = s.Has ? Visibility.Visible : Visibility.Collapsed;
            musicOff.Visibility = s.Has ? Visibility.Collapsed : Visibility.Visible;
            if (!s.Has) return;
            mTitle.Text = s.Title; mArtist.Text = s.Artist;
            btnPlay.Content = s.Playing ? "\uE769" : "\uE768"; // pause / play
            if (!ReferenceEquals(s.Thumb, shownThumb))
            {
                shownThumb = s.Thumb;
                if (s.Thumb == null) { mThumb.ClearValue(Border.BackgroundProperty); mThumb.SetResourceReference(Border.BackgroundProperty, "cField"); return; }
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit(); bi.StreamSource = new MemoryStream(s.Thumb); bi.DecodePixelWidth = 168; bi.CacheOption = BitmapCacheOption.OnLoad; bi.EndInit(); bi.Freeze();
                    mThumb.Background = new ImageBrush(bi) { Stretch = Stretch.UniformToFill };
                }
                catch { }
            }
        }
    }
}
