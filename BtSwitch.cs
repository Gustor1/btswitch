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

            timer.Interval = 2000; timer.Tick += (s, e) => Refresh_();
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
            try { devices = BtAudio.List(); }
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
            foreach (var d in devices) list.Controls.Add(MakeRow(d));
            list.ResumeLayout();
        }

        Control MakeRow(BtDevice d)
        {
            var row = new Panel { Width = 340, Height = 64, BackColor = Card, Margin = new Padding(0, 4, 0, 4) };
            var name = new Label { Text = d.Name, AutoEllipsis = true, Font = new Font("Segoe UI Semibold", 10f) };
            name.SetBounds(12, 10, 200, 22);
            var where = new Label { Text = d.Connected ? "Son sur le PC" : "Libre → son sur le téléphone",
                ForeColor = d.Connected ? PcColor : PhoneColor, Font = new Font("Segoe UI", 8.5f) };
            where.SetBounds(12, 34, 200, 20);
            var btn = new Button { FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Cursor = Cursors.Hand,
                Text = d.Connected ? "Vers le tél." : "Vers le PC",
                BackColor = d.Connected ? PhoneColor : PcColor, Font = new Font("Segoe UI Semibold", 9f) };
            btn.FlatAppearance.BorderSize = 0;
            btn.SetBounds(218, 14, 110, 36);
            btn.AccessibleName = d.Connected ? "Envoyer le son de " + d.Name + " vers le téléphone" : "Ramener le son de " + d.Name + " sur le PC";
            btn.Click += (s, e) => Toggle(d);
            row.Controls.Add(name); row.Controls.Add(where); row.Controls.Add(btn);
            return row;
        }

        void BuildTrayMenu()
        {
            var m = tray.ContextMenuStrip; m.Items.Clear();
            try { devices = BtAudio.List(); } catch { }
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
            try { devices = BtAudio.List(); } catch { return; }
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
