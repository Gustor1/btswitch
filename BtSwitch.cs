// BtSwitch - bascule le son d'un appareil audio Bluetooth entre le PC et le telephone
// sans le desappairer. Utilise la propriete KS "BtAudio" (ONESHOT_DISCONNECT / RECONNECT)
// exposee par la pile audio Bluetooth de Windows.
// Compilation : csc /target:winexe /out:BtSwitch.exe BtSwitch.cs
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BtSwitch
{
    // ---------------- COM interop ----------------
    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY { public Guid fmtid; public uint pid; public PROPERTYKEY(Guid g, uint p) { fmtid = g; pid = p; } }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT { public ushort vt; public ushort r1, r2, r3; public IntPtr p; public IntPtr p2; }

    [StructLayout(LayoutKind.Sequential)]
    struct KSPROPERTY { public Guid Set; public uint Id; public uint Flags; }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCo { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        void RegisterEndpointNotificationCallback(IntPtr client);
        void UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        void GetCount(out uint count);
        void Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        void OpenPropertyStore(int access, out IPropertyStore store);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PROPERTYKEY key);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        void Commit();
    }

    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDeviceTopology
    {
        void GetConnectorCount(out uint count);
        void GetConnector(uint index, out IConnector connector);
        void GetSubunitCount(out uint count);
        void GetSubunit(uint index, out IntPtr subunit);
        void GetPartById(uint id, out IntPtr part);
        void GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetSignalPath(IntPtr from, IntPtr to, bool rejectMixedPaths, out IntPtr parts);
    }

    [ComImport, Guid("9c2c4058-23f5-41de-877a-df3af236a09e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IConnector
    {
        void GetType(out int type);
        void GetDataFlow(out int flow);
        void ConnectTo(IConnector other);
        void Disconnect();
        void IsConnected(out bool connected);
        void GetConnectedTo(out IConnector other);
        void GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IKsControl
    {
        void KsProperty(ref KSPROPERTY property, uint propertyLength, IntPtr data, uint dataLength, out uint bytesReturned);
        void KsMethod(IntPtr method, uint methodLength, IntPtr data, uint dataLength, out uint bytesReturned);
        void KsEvent(IntPtr evt, uint eventLength, IntPtr data, uint dataLength, out uint bytesReturned);
    }

    // ---------------- Logique Bluetooth ----------------
    class BtDevice
    {
        public string Name;
        public bool Connected;
        public List<string> FilterIds = new List<string>();
    }

    static class BtAudio
    {
        static readonly Guid KSPROPSETID_BtAudio = new Guid("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
        const uint ONESHOT_RECONNECT = 0, ONESHOT_DISCONNECT = 1;
        const int CLSCTX_ALL = 23, DEVICE_STATE_ACTIVE = 1, DEVICE_STATEMASK_ALL = 0xF, eAll = 2;
        static readonly PROPERTYKEY PKEY_DeviceInterface_FriendlyName = new PROPERTYKEY(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);
        static readonly PROPERTYKEY PKEY_Device_FriendlyName = new PROPERTYKEY(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

        static IMMDeviceEnumerator NewEnum() { return (IMMDeviceEnumerator)new MMDeviceEnumeratorCo(); }

        static string ReadString(IMMDevice dev, PROPERTYKEY key)
        {
            try
            {
                IPropertyStore ps; dev.OpenPropertyStore(0, out ps);
                PROPVARIANT pv; ps.GetValue(ref key, out pv);
                if (pv.vt == 31 && pv.p != IntPtr.Zero) return Marshal.PtrToStringUni(pv.p);
            }
            catch { }
            return null;
        }

        public static List<BtDevice> List()
        {
            var byName = new Dictionary<string, BtDevice>(StringComparer.OrdinalIgnoreCase);
            var result = new List<BtDevice>();
            var en = NewEnum();
            IMMDeviceCollection col; en.EnumAudioEndpoints(eAll, DEVICE_STATEMASK_ALL, out col);
            uint n; col.GetCount(out n);
            Guid iidTopo = typeof(IDeviceTopology).GUID;
            for (uint i = 0; i < n; i++)
            {
                try
                {
                    IMMDevice ep; col.Item(i, out ep);
                    object o; ep.Activate(ref iidTopo, CLSCTX_ALL, IntPtr.Zero, out o);
                    var topo = (IDeviceTopology)o;
                    IConnector con; topo.GetConnector(0, out con);
                    string filterId; con.GetDeviceIdConnectedTo(out filterId);
                    if (filterId == null || filterId.IndexOf("bth", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    string name = ReadString(ep, PKEY_DeviceInterface_FriendlyName) ?? ReadString(ep, PKEY_Device_FriendlyName) ?? "Appareil Bluetooth";
                    // le profil "mains libres" (micro) apparait comme un 2e appareil : on le regroupe
                    foreach (var suffix in new[] { " Hands-Free AG Audio", " Hands-Free", " Mains libres" })
                        if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { name = name.Substring(0, name.Length - suffix.Length); break; }
                    int state; ep.GetState(out state);
                    BtDevice d;
                    if (!byName.TryGetValue(name, out d)) { d = new BtDevice { Name = name }; byName[name] = d; result.Add(d); }
                    if (state == DEVICE_STATE_ACTIVE) d.Connected = true;
                    if (!d.FilterIds.Contains(filterId)) d.FilterIds.Add(filterId);
                }
                catch { /* endpoint sans topologie : ignore */ }
            }
            return result;
        }

        public static void SetConnected(BtDevice d, bool connect)
        {
            var en = NewEnum();
            Guid iidKs = typeof(IKsControl).GUID;
            Exception last = null; int ok = 0;
            foreach (var id in d.FilterIds)
            {
                try
                {
                    IMMDevice f; en.GetDevice(id, out f);
                    object o; f.Activate(ref iidKs, CLSCTX_ALL, IntPtr.Zero, out o);
                    var ks = (IKsControl)o;
                    var prop = new KSPROPERTY { Set = KSPROPSETID_BtAudio, Id = connect ? ONESHOT_RECONNECT : ONESHOT_DISCONNECT, Flags = 1 /*GET*/ };
                    uint ret; ks.KsProperty(ref prop, (uint)Marshal.SizeOf(typeof(KSPROPERTY)), IntPtr.Zero, 0, out ret);
                    ok++;
                }
                catch (Exception ex) { last = ex; }
            }
            if (ok == 0 && last != null) throw last;
        }
    }

    // ---------------- Interface ----------------
    class MainForm : Form
    {
        const string AppName = "BtSwitch";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const int HOTKEY_ID = 1, MOD_ALT = 1, MOD_CONTROL = 2, VK_B = 0x42, WM_HOTKEY = 0x0312;
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, int mods, int vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);

        readonly NotifyIcon tray = new NotifyIcon();
        readonly FlowLayoutPanel list = new FlowLayoutPanel();
        readonly Label status = new Label();
        readonly CheckBox startup = new CheckBox();
        readonly Timer timer = new Timer();
        List<BtDevice> devices = new List<BtDevice>();
        string lastSignature = "";
        string favorite;
        bool quitting;
        DateTime holdUntil = DateTime.MinValue; // evite que le rafraichissement remette le switch en place pendant la bascule

        static readonly Color Bg = Color.FromArgb(24, 24, 28), Card = Color.FromArgb(38, 38, 46),
            Fg = Color.FromArgb(235, 235, 240), Muted = Color.FromArgb(150, 150, 165),
            PcColor = Color.FromArgb(70, 130, 255), PhoneColor = Color.FromArgb(46, 170, 110);

        public MainForm()
        {
            Text = "BtSwitch - son PC / téléphone";
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Bg; ForeColor = Fg; Font = new Font("Segoe UI", 10f);
            ClientSize = new Size(380, 400);
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 12, wa.Bottom - Height - 12);
            Icon = MakeIcon(PcColor);

            var title = new Label { Text = "Où va le son de tes écouteurs ?", Dock = DockStyle.Top, Height = 34,
                Font = new Font("Segoe UI Semibold", 11f), Padding = new Padding(12, 10, 0, 0) };
            list.Dock = DockStyle.Fill; list.FlowDirection = FlowDirection.TopDown; list.WrapContents = false;
            list.AutoScroll = true; list.Padding = new Padding(10, 4, 10, 4);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 58 };
            status.SetBounds(12, 4, 360, 20); status.ForeColor = Muted; status.Font = new Font("Segoe UI", 8.5f);
            status.Text = "Raccourci : Ctrl+Alt+B bascule le dernier appareil utilisé";
            startup.SetBounds(12, 28, 250, 24); startup.Text = "Lancer au démarrage de Windows";
            startup.ForeColor = Muted; startup.Checked = IsStartupEnabled();
            startup.CheckedChanged += (s, e) => SetStartup(startup.Checked);
            bottom.Controls.Add(status); bottom.Controls.Add(startup);

            Controls.Add(list); Controls.Add(bottom); Controls.Add(title);

            tray.Icon = Icon; tray.Text = "BtSwitch"; tray.Visible = true;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleWindow(); };
            tray.ContextMenuStrip = new ContextMenuStrip();
            tray.ContextMenuStrip.Opening += (s, e) => BuildTrayMenu();

            timer.Interval = 2000; timer.Tick += (s, e) => { if (DateTime.Now >= holdUntil) Refresh_(); };
            timer.Start();
            favorite = ReadSetting("Favorite");
            Refresh_();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterHotKey(Handle, HOTKEY_ID, MOD_CONTROL | MOD_ALT, VK_B);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID) ToggleFavorite();
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            UnregisterHotKey(Handle, HOTKEY_ID);
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        void ToggleWindow()
        {
            if (Visible) Hide(); else { Show(); Activate(); Refresh_(); }
        }

        void Refresh_()
        {
            try { devices = Sorted(BtAudio.List()); }
            catch (Exception ex) { status.Text = "Erreur de lecture : " + ex.Message; return; }
            string sig = "";
            foreach (var d in devices) sig += d.Name + (d.Connected ? "1" : "0") + "|";
            bool anyPc = false;
            foreach (var d in devices) if (d.Connected) anyPc = true;
            tray.Icon = MakeIcon(anyPc ? PcColor : PhoneColor);
            if (sig == lastSignature) return;
            lastSignature = sig;
            Rebuild();
        }

        void Rebuild()
        {
            list.SuspendLayout();
            list.Controls.Clear();
            if (devices.Count == 0)
            {
                list.Controls.Add(new Label { AutoSize = false, Width = 340, Height = 80, ForeColor = Muted,
                    Text = "Aucun appareil audio Bluetooth appairé.\nAppaire d'abord tes écouteurs dans\nParamètres > Bluetooth et appareils." });
            }
            for (int i = 0; i < devices.Count; i++) list.Controls.Add(MakeRow(devices[i], i));
            list.ResumeLayout();
        }

        Control MakeRow(BtDevice d, int index)
        {
            var row = new Panel { Width = 340, Height = 64, BackColor = Card, Margin = new Padding(0, 4, 0, 4) };

            var up = new ArrowButton(true) { BackColor = Card, Enabled = index > 0 };
            up.SetBounds(6, 9, 22, 22); up.AccessibleName = "Monter " + d.Name;
            up.Click += (s, e) => MoveDevice(index, -1);
            var down = new ArrowButton(false) { BackColor = Card, Enabled = index < devices.Count - 1 };
            down.SetBounds(6, 33, 22, 22); down.AccessibleName = "Descendre " + d.Name;
            down.Click += (s, e) => MoveDevice(index, +1);

            var name = new Label { Text = d.Name, AutoEllipsis = true, Font = new Font("Segoe UI Semibold", 10f) };
            name.SetBounds(34, 11, 174, 22);
            var where = new Label { Text = d.Connected ? "Son sur le PC" : "Libre pour le téléphone",
                ForeColor = d.Connected ? PcColor : PhoneColor, Font = new Font("Segoe UI", 8.5f) };
            where.SetBounds(34, 34, 174, 20);

            var sw = new ToggleSwitch(!d.Connected, PcColor, PhoneColor, Muted) { BackColor = Card };
            sw.SetBounds(212, 15, 118, 34);
            sw.AccessibleName = "Son de " + d.Name + " : " + (d.Connected ? "PC" : "téléphone");
            sw.Toggled += (s, e) => Toggle(d);

            row.Controls.Add(up); row.Controls.Add(down);
            row.Controls.Add(name); row.Controls.Add(where); row.Controls.Add(sw);
            return row;
        }

        // ----- ordre personnalise -----
        List<BtDevice> Sorted(List<BtDevice> list)
        {
            var order = new List<string>((ReadSetting("Order") ?? "").Split('\n'));
            var res = new List<BtDevice>(list);
            res.Sort((a, b) =>
            {
                int ia = order.IndexOf(a.Name), ib = order.IndexOf(b.Name);
                if (ia < 0) ia = int.MaxValue - list.Count + list.IndexOf(a);
                if (ib < 0) ib = int.MaxValue - list.Count + list.IndexOf(b);
                return ia.CompareTo(ib);
            });
            return res;
        }

        void MoveDevice(int index, int delta)
        {
            int j = index + delta;
            if (j < 0 || j >= devices.Count) return;
            var tmp = devices[index]; devices[index] = devices[j]; devices[j] = tmp;
            var names = new List<string>();
            foreach (var d in devices) names.Add(d.Name);
            WriteSetting("Order", string.Join("\n", names.ToArray()));
            lastSignature = "";
            Refresh_();
        }

        void BuildTrayMenu()
        {
            var m = tray.ContextMenuStrip; m.Items.Clear();
            try { devices = Sorted(BtAudio.List()); } catch { }
            if (devices.Count == 0) m.Items.Add(new ToolStripMenuItem("Aucun appareil Bluetooth audio") { Enabled = false });
            foreach (var d in devices)
            {
                var dev = d;
                var it = new ToolStripMenuItem(d.Name + (d.Connected ? "  —  sur PC (cliquer → tél.)" : "  —  libre (cliquer → PC)"));
                it.Checked = d.Connected;
                it.Click += (s, e) => Toggle(dev);
                m.Items.Add(it);
            }
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Ouvrir la fenêtre", null, (s, e) => { Show(); Activate(); });
            m.Items.Add("Quitter", null, (s, e) => { quitting = true; Close(); Application.Exit(); });
        }

        void Toggle(BtDevice d)
        {
            bool connect = !d.Connected;
            holdUntil = DateTime.Now.AddSeconds(2.4);
            favorite = d.Name; WriteSetting("Favorite", favorite);
            status.Text = connect ? "Connexion de " + d.Name + " au PC…" : "Libération de " + d.Name + " pour le téléphone…";
            Cursor = Cursors.WaitCursor;
            try
            {
                BtAudio.SetConnected(d, connect);
                tray.ShowBalloonTip(1500, "BtSwitch", connect ? d.Name + " → PC" : d.Name + " → téléphone", ToolTipIcon.None);
            }
            catch (Exception ex)
            {
                status.Text = "Échec : " + ex.Message;
                tray.ShowBalloonTip(3000, "BtSwitch", "Échec : " + ex.Message, ToolTipIcon.Warning);
            }
            finally { Cursor = Cursors.Default; }
            // la pile Bluetooth met 1-3 s a changer d'etat
            var t = new Timer { Interval = 2500 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); lastSignature = ""; Refresh_(); status.Text = "Raccourci : Ctrl+Alt+B bascule le dernier appareil utilisé"; };
            t.Start();
        }

        void ToggleFavorite()
        {
            try { devices = Sorted(BtAudio.List()); } catch { return; }
            BtDevice target = null;
            foreach (var d in devices) if (favorite != null && d.Name == favorite) target = d;
            if (target == null && devices.Count > 0) target = devices[0];
            if (target != null) Toggle(target);
        }

        static Icon MakeIcon(Color c)
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var b = new SolidBrush(c)) g.FillEllipse(b, 1, 1, 30, 30);
                using (var p = new Pen(Color.White, 3f))
                {
                    // casque stylise
                    g.DrawArc(p, 8, 7, 16, 16, 180, 180);
                    g.FillRectangle(Brushes.White, 6, 15, 5, 9);
                    g.FillRectangle(Brushes.White, 21, 15, 5, 9);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        static string ReadSetting(string k)
        {
            try { using (var key = Registry.CurrentUser.OpenSubKey(@"Software\" + AppName)) return key == null ? null : key.GetValue(k) as string; }
            catch { return null; }
        }
        static void WriteSetting(string k, string v)
        {
            try { using (var key = Registry.CurrentUser.CreateSubKey(@"Software\" + AppName)) key.SetValue(k, v); } catch { }
        }
        static bool IsStartupEnabled()
        {
            try { using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) return key != null && key.GetValue(AppName) != null; }
            catch { return false; }
        }
        static void SetStartup(bool on)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (on) key.SetValue(AppName, "\"" + Application.ExecutablePath + "\" --tray");
                    else key.DeleteValue(AppName, false);
                }
            }
            catch { }
        }
    }

    // ---------------- Controles dessines ----------------
    static class Shapes
    {
        public static System.Drawing.Drawing2D.GraphicsPath Pill(RectangleF r)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            float d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 90, 180);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
            p.CloseFigure();
            return p;
        }
        public static Color Lerp(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
    }

    // Interrupteur "PC | Tel." : la pastille indique ou va le son
    class ToggleSwitch : Control
    {
        public bool OnPhone { get; private set; }
        public event EventHandler Toggled;
        float pos;
        bool hover;
        readonly Timer anim = new Timer { Interval = 15 };
        readonly Color pc, phone, muted;

        public ToggleSwitch(bool onPhone, Color pc, Color phone, Color muted)
        {
            this.pc = pc; this.phone = phone; this.muted = muted;
            OnPhone = onPhone; pos = onPhone ? 1f : 0f;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true; Cursor = Cursors.Hand;
            Font = new Font("Segoe UI Semibold", 9f);
            AccessibleRole = AccessibleRole.CheckButton;
            anim.Tick += (s, e) =>
            {
                float target = OnPhone ? 1f : 0f;
                pos += (target - pos) * 0.3f;
                if (Math.Abs(target - pos) < 0.01f) { pos = target; anim.Stop(); }
                Invalidate();
            };
        }

        void Flip()
        {
            OnPhone = !OnPhone;
            if (SystemInformation.UIEffectsEnabled) anim.Start(); else { pos = OnPhone ? 1f : 0f; Invalidate(); }
            if (Toggled != null) Toggled(this, EventArgs.Empty);
        }

        protected override void OnClick(EventArgs e) { base.OnClick(e); Focus(); Flip(); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) Flip();
            else if (e.KeyCode == Keys.Left && OnPhone) Flip();
            else if (e.KeyCode == Keys.Right && !OnPhone) Flip();
        }
        protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var track = new RectangleF(1, 1, Width - 3, Height - 3);
            using (var path = Shapes.Pill(track))
            using (var b = new SolidBrush(hover ? Color.FromArgb(62, 62, 74) : Color.FromArgb(52, 52, 62)))
                g.FillPath(b, path);

            float half = (track.Width - 6) / 2f;
            var thumb = new RectangleF(track.X + 3 + pos * half, track.Y + 3, half, track.Height - 6);
            using (var path = Shapes.Pill(thumb))
            using (var b = new SolidBrush(Shapes.Lerp(pc, phone, pos)))
                g.FillPath(b, path);

            var left = new RectangleF(track.X + 3, track.Y, half, track.Height);
            var right = new RectangleF(track.X + 3 + half, track.Y, half, track.Height);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using (var on = new SolidBrush(Color.White))
            using (var off = new SolidBrush(muted))
            {
                g.DrawString("PC", Font, pos < 0.5f ? on : off, left, sf);
                g.DrawString("Tél.", Font, pos >= 0.5f ? on : off, right, sf);
            }
            if (Focused)
                using (var path = Shapes.Pill(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2)))
                using (var p = new Pen(Color.White, 1.5f))
                    g.DrawPath(p, path);
        }
    }

    // Petite fleche pour reordonner
    class ArrowButton : Control
    {
        readonly bool up; bool hover;
        public ArrowButton(bool up)
        {
            this.up = up;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            TabStop = true; Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.PushButton;
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            if (Enabled && (hover || Focused))
                using (var b = new SolidBrush(Color.FromArgb(60, 60, 72))) g.FillEllipse(b, 0, 0, Width - 1, Height - 1);
            float cx = Width / 2f, cy = Height / 2f;
            var pts = up
                ? new[] { new PointF(cx - 5, cy + 2.5f), new PointF(cx, cy - 3), new PointF(cx + 5, cy + 2.5f) }
                : new[] { new PointF(cx - 5, cy - 2.5f), new PointF(cx, cy + 3), new PointF(cx + 5, cy - 2.5f) };
            var col = !Enabled ? Color.FromArgb(70, 70, 82) : hover ? Color.White : Color.FromArgb(160, 160, 175);
            using (var p = new Pen(col, 2f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
                g.DrawLines(p, pts);
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (var mutex = new System.Threading.Mutex(true, "BtSwitch_single_instance", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                var f = new MainForm();
                bool trayOnly = args.Length > 0 && args[0] == "--tray";
                if (trayOnly) { var h = f.Handle; Application.Run(); }
                else Application.Run(f);
            }
        }
    }
}
