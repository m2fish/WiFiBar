using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace WiFiBar
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, "WiFiBar.SingleInstance.7C31", out createdNew))
            {
                if (!createdNew)
                {
                    return;
                }

                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write(e.ExceptionObject as Exception);
                Application.ThreadException += (s, e) => Log.Write(e.Exception);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

                try { SetProcessDPIAware(); }
                catch { }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new WiFiBarForm());
            }
        }
    }

    internal static class Log
    {
        public static void Write(Exception exception)
        {
            if (exception == null) return;
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WiFiBar.log");
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + exception + Environment.NewLine);
            }
            catch { }
        }
    }

    internal static class WlanApi
    {
        internal sealed class Network
        {
            public string Ssid;
            public int Signal;
            public bool HasProfile;
            public DateTime LastSeen = DateTime.Now;
        }

        private static readonly Encoding ConsoleEncoding = GetConsoleEncoding();
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private static Encoding GetConsoleEncoding()
        {
            try
            {
                return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch
            {
                return Encoding.Default;
            }
        }

        private static string Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return string.Empty;

            try { return StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException) { }
            catch (ArgumentException) { }

            try { return ConsoleEncoding.GetString(bytes); }
            catch { return Encoding.Default.GetString(bytes); }
        }

        private static string Run(string arguments)
        {
            var startInfo = new ProcessStartInfo("netsh", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                process.Start();
                byte[] buffer;
                using (var memory = new MemoryStream())
                {
                    process.StandardOutput.BaseStream.CopyTo(memory);
                    buffer = memory.ToArray();
                }
                process.WaitForExit(8000);
                return Decode(buffer);
            }
        }

        public static bool TryGetCurrent(out string ssid, out int signal)
        {
            ssid = null;
            signal = 0;

            string output;
            try { output = Run("wlan show interfaces"); }
            catch { return false; }

            string profile = null;
            bool? stateConnected = null;
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim();
                int separator = line.IndexOf(':');
                if (separator <= 0) continue;

                string key = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim();

                if (string.Equals(key, "SSID", StringComparison.OrdinalIgnoreCase))
                {
                    if (ssid == null) ssid = value;
                }
                else if (string.Equals(key, "State", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(key, "状态", StringComparison.OrdinalIgnoreCase))
                {
                    string lower = value.ToLowerInvariant();
                    if (lower.Contains("disconnected") || value.Contains("断开"))
                        stateConnected = false;
                    else if (lower.Contains("connected") || value.Contains("已连接"))
                        stateConnected = true;
                }
                else if (string.Equals(key, "Profile", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(key, "配置文件", StringComparison.OrdinalIgnoreCase))
                {
                    profile = value;
                }
                else if (string.Equals(key, "Signal", StringComparison.OrdinalIgnoreCase) ||
                         key.Contains("信号"))
                {
                    var match = Regex.Match(value, @"(\d+)");
                    if (match.Success) signal = int.Parse(match.Groups[1].Value);
                }
            }

            if (stateConnected == false) return false;

            if (string.IsNullOrWhiteSpace(ssid)) ssid = profile;
            return !string.IsNullOrWhiteSpace(ssid);
        }

        private static HashSet<string> GetProfiles()
        {
            var profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string output;
            try { output = Run("wlan show profiles"); }
            catch { return profiles; }

            var pattern = new Regex(@"^\s*(?:All User Profile|Current User Profile|所有用户配置文件|当前用户配置文件)\s*:\s*(.+?)\s*$");
            foreach (string raw in output.Split('\n'))
            {
                Match match = pattern.Match(raw);
                if (match.Success) profiles.Add(match.Groups[1].Value.Trim());
            }
            return profiles;
        }

        public static List<Network> Scan()
        {
            HashSet<string> profiles;
            try { profiles = GetProfiles(); }
            catch { profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }

            var order = new List<string>();
            var map = new Dictionary<string, Network>(StringComparer.OrdinalIgnoreCase);

            string output;
            try { output = Run("wlan show networks mode=bssid"); }
            catch { output = string.Empty; }

            Network current = null;
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim();

                Match header = Regex.Match(line, @"^SSID\s+\d+\s*:\s*(.*)$", RegexOptions.IgnoreCase);
                if (header.Success)
                {
                    string name = header.Groups[1].Value.Trim();
                    if (name.Length == 0)
                    {
                        current = null;
                        continue;
                    }

                    Network network;
                    if (!map.TryGetValue(name, out network))
                    {
                        network = new Network { Ssid = name, HasProfile = profiles.Contains(name) };
                        map[name] = network;
                        order.Add(name);
                    }
                    current = network;
                    continue;
                }

                if (current != null &&
                    (line.StartsWith("Signal", StringComparison.OrdinalIgnoreCase) || line.Contains("信号")))
                {
                    Match value = Regex.Match(line, @"(\d+)\s*%");
                    if (value.Success)
                    {
                        int strength = int.Parse(value.Groups[1].Value);
                        if (strength > current.Signal) current.Signal = strength;
                    }
                }
            }

            var result = new List<Network>();
            foreach (string key in order) result.Add(map[key]);
            result.Sort((a, b) => a.Signal.CompareTo(b.Signal));
            return result;
        }

        public static bool Connect(string ssid)
        {
            string safe = ssid.Replace("\"", string.Empty);
            string output;
            // 只按配置文件名称连接：中文 SSID 经 ssid= 参数传入时会被按 ANSI 编码比较，
            // netsh 会报“配置文件中不存在 SSID”，导致中文网络无法切换。
            try { output = Run("wlan connect name=\"" + safe + "\""); }
            catch { return false; }

            return output.IndexOf("success", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   output.Contains("成功");
        }

        // ---- Native WiFi API：主动命令系统立即扫描一次 ----
        // netsh 只读服务缓存；WlanScan 是唯一官方触发真实扫描的方式。

        [DllImport("wlanapi.dll")]
        private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

        [DllImport("wlanapi.dll")]
        private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

        [DllImport("wlanapi.dll")]
        private static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

        [DllImport("wlanapi.dll")]
        private static extern uint WlanScan(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr dot11Ssid, IntPtr ieData, IntPtr reserved);

        [DllImport("wlanapi.dll")]
        private static extern void WlanFreeMemory(IntPtr memory);

        /// <summary>对所有无线网卡触发一次真实扫描。立即返回，结果约 2 秒后进入系统缓存。</summary>
        public static void TriggerScan()
        {
            IntPtr client = IntPtr.Zero;
            try
            {
                uint version;
                if (WlanOpenHandle(2, IntPtr.Zero, out version, out client) != 0) return;

                IntPtr listPtr;
                if (WlanEnumInterfaces(client, IntPtr.Zero, out listPtr) != 0) return;

                try
                {
                    int count = Marshal.ReadInt32(listPtr, 0);
                    // 列表结构：count(4) + index(4) + 每项 [Guid 16 + 描述 512 + 状态 4] = 532 字节
                    const int itemSize = 532;
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr item = new IntPtr(listPtr.ToInt64() + 8 + i * itemSize);
                        byte[] guidBytes = new byte[16];
                        Marshal.Copy(item, guidBytes, 0, 16);
                        Guid iface = new Guid(guidBytes);
                        try { WlanScan(client, ref iface, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); }
                        catch { }
                    }
                }
                finally
                {
                    WlanFreeMemory(listPtr);
                }
            }
            catch { }
            finally
            {
                if (client != IntPtr.Zero)
                {
                    try { WlanCloseHandle(client, IntPtr.Zero); } catch { }
                }
            }
        }
    }

    internal sealed class WiFiBarForm : Form
    {
        private const int BarHeight = 30;
        private const int IconWidth = 18;
        private const int Corner = 8;

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;

        private readonly ToolTip toolTip = new ToolTip();
        private readonly System.Windows.Forms.Timer statusTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer scanTimer = new System.Windows.Forms.Timer();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly Font textFont;
        private readonly Font boldFont;

        private List<WlanApi.Network> networks = new List<WlanApi.Network>();
        // 刘海面板专用列表：按信号由强到弱（顶部菜单仍保持由弱到强）
        private List<WlanApi.Network> notchNetworks = new List<WlanApi.Network>();
        private string currentSsid;
        private int currentSignal;
        private bool hovering;
        private int statusBusy;
        private int scanBusy;

        // 拖动与位置记忆
        private const int DragThreshold = 4;
        private const int SnapDistance = 12;
        private Point? customLocation;
        private Point dragOrigin;
        private Point dragOffset;
        private bool dragging;
        // 默认以刘海屏模式启动
        private bool notchMode = true;

        // 刘海屏展开面板
        private const int RowHeight = 30;
        private const int FooterHeight = 66;
        // 页脚上方的两行信息：本机 IP / 网关
        private const int MetaRowHeight = 30;
        private const int MetaBlockHeight = 62;
        // 折叠态只显示信号最强的前 N 个网络
        private const int MaxCollapsedNetworks = 10;
        private const int NotchCollapsedRadius = 15;
        private const int NotchBottomRadius = 16;
        private const int NotchPaddingX = 20;
        private const int NotchListTopPadding = 12;
        private const int NotchListBottomPadding = 8;
        private const int NotchCheckSlot = 20;
        // 底部菜单行：行内留白、图标尺寸与图文间距
        private const int FooterPadX = 12;
        private const int FooterIconSize = 12;
        private const int FooterIconGap = 8;
        private static readonly Color NotchBackground = Color.FromArgb(30, 30, 33);
        private static readonly Color NotchHoverBackground = Color.FromArgb(38, 38, 42);
        private static readonly Color MacAccent = Color.FromArgb(10, 132, 255);
        // 刘海投影：单独的分层窗口绘制，随面板尺寸动画同步
        private const int ShadowSpread = 32;
        private const int ShadowOffsetY = 10;
        private const int ShadowAlpha = 92;
        private ShadowWindow shadowWindow;
        private bool expanded;
        private float expandT;
        private float animFromT;
        private float animTargetT;
        private int animStartTicks;
        private System.Windows.Forms.Timer animTimer;
        private int hoverRow = -1;
        private int hoverFooter = -1;
        private int hoverMeta = -1;
        private bool hoverMore;
        private bool showAllNetworks;
        private int scrollOffset;
        private string localIp;
        private string gatewayIp;
        private bool copyFeedback;
        private readonly System.Windows.Forms.Timer copyTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer ipTimer = new System.Windows.Forms.Timer();
        private Rectangle footerNormalRect;
        private Rectangle footerExitRect;
        // 普通模式：面板锚点（条子原始位置）与展开方向
        private Point panelAnchor;
        private bool hasPanelAnchor;
        private bool panelUp;

        private static readonly string PositionFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WiFiBar", "position.txt");

        public WiFiBarForm()
        {
            textFont = new Font("Microsoft YaHei UI", 9f);
            boldFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            Font = textFont;
            BackColor = Color.FromArgb(33, 33, 33);
            Cursor = Cursors.Hand;
            Size = new Size(120, BarHeight);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            menu.Font = textFont;
            menu.Renderer = new DarkMenuRenderer();
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = false;
            menu.Padding = new Padding(2);

            MouseEnter += (s, e) => { hovering = true; UpdateOpacity(); Invalidate(); };
            MouseLeave += (s, e) => { hovering = false; UpdateOpacity(); Invalidate(); };
            MouseDown += OnBarMouseDown;
            MouseMove += OnBarMouseMove;
            MouseUp += OnBarMouseUp;
            Activated += (s, e) => UpdateOpacity();
            Deactivate += (s, e) =>
            {
                // 展开瞬间焦点常抖动一下；光标还在面板内时不要收起
                bool cursorInside = expanded &&
                    ClientRectangle.Contains(PointToClient(Cursor.Position));
                if (expanded && !cursorInside) CollapsePanel();
                UpdateOpacity();
            };

            customLocation = LoadPosition();

            // 窗口图标与 exe 内嵌图标一致（Alt+Tab、任务管理器中显示）
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }

            toolTip.InitialDelay = 250;
            toolTip.ReshowDelay = 100;
            toolTip.SetToolTip(this, "正在获取 WiFi 状态…");
            // 刘海模式下不显示悬浮提示气泡
            toolTip.Active = !notchMode;

            statusTimer.Interval = 60 * 60 * 1000;
            statusTimer.Tick += (s, e) => RefreshStatus();
            statusTimer.Start();

            scanTimer.Interval = 60 * 60 * 1000;
            scanTimer.Tick += (s, e) => BeginScan(true);
            scanTimer.Start();

            // 面板展开期间定期刷新本机地址，应对 DHCP 变化
            ipTimer.Interval = 5000;
            ipTimer.Tick += (s, e) => RefreshLocalAddress();

            // 「已复制」行内提示，1.2 秒后自动消失
            copyTimer.Interval = 1200;
            copyTimer.Tick += (s, e) =>
            {
                copyTimer.Stop();
                copyFeedback = false;
                Invalidate();
            };

            Shown += (s, e) =>
            {
                FitToText();
                UpdateOpacity();
                RefreshStatus();
                BeginScan(true);
            };
        }

        private void OnBarMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            // 面板展开时不允许拖动
            if (expanded) return;

            dragOrigin = Cursor.Position;
            dragOffset = new Point(Cursor.Position.X - Left, Cursor.Position.Y - Top);
            dragging = false;
            Capture = true;
        }

        private void OnBarMouseMove(object sender, MouseEventArgs e)
        {
            if (notchMode || expanded)
            {
                if (expanded && e.Button == MouseButtons.None)
                {
                    UpdateHoverRow(e.Location);
                    UpdateFooterHover(e.Location);
                }
                return;
            }

            if (e.Button != MouseButtons.Left) return;

            if (!dragging)
            {
                if (Math.Abs(Cursor.Position.X - dragOrigin.X) < DragThreshold &&
                    Math.Abs(Cursor.Position.Y - dragOrigin.Y) < DragThreshold) return;

                dragging = true;
                toolTip.Hide(this);
                if (menu.Visible) menu.Close();
                Cursor = Cursors.SizeAll;
            }

            Location = new Point(Cursor.Position.X - dragOffset.X, Cursor.Position.Y - dragOffset.Y);
            customLocation = Location;
            UpdateShadow();
        }

        private void OnBarMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right) return;

            Capture = false;

            if (dragging)
            {
                dragging = false;
                Cursor = Cursors.Hand;
                Location = SnapToEdge(Location);
                customLocation = Location;
                SavePosition();
                return;
            }

            // 右键：显示桌面（Win+D）
            if (e.Button == MouseButtons.Right)
            {
                if (expanded) CollapsePanel();
                ShowDesktop();
                return;
            }

            if (expanded)
            {
                HandlePanelClick(e.Location);
                return;
            }

            ExpandPanel();
        }

        private void ShowDesktop()
        {
            const byte keyLWin = 0x5B;
            const byte keyD = 0x44;
            const uint keyUp = 0x0002;

            keybd_event(keyLWin, 0, 0, UIntPtr.Zero);
            keybd_event(keyD, 0, 0, UIntPtr.Zero);
            keybd_event(keyD, 0, keyUp, UIntPtr.Zero);
            keybd_event(keyLWin, 0, keyUp, UIntPtr.Zero);
        }

        // 靠近屏幕可用区域边缘时自动吸附，便于和任务栏/屏幕边对齐
        private Point SnapToEdge(Point location)
        {
            Rectangle area = Screen.FromPoint(new Point(location.X + Width / 2, location.Y + Height / 2)).WorkingArea;
            int x = location.X;
            int y = location.Y;

            if (Math.Abs(x - area.Left) <= SnapDistance) x = area.Left;
            if (Math.Abs(area.Right - (x + Width)) <= SnapDistance) x = area.Right - Width;
            if (Math.Abs(y - area.Top) <= SnapDistance) y = area.Top;
            if (Math.Abs(area.Bottom - (y + Height)) <= SnapDistance) y = area.Bottom - Height;

            return new Point(x, y);
        }

        private static Point? LoadPosition()
        {
            try
            {
                if (!File.Exists(PositionFile)) return null;

                string[] parts = File.ReadAllText(PositionFile).Trim().Split(',');
                int x, y;
                if (parts.Length != 2 ||
                    !int.TryParse(parts[0].Trim(), out x) ||
                    !int.TryParse(parts[1].Trim(), out y)) return null;

                var probe = new Rectangle(x, y, 120, BarHeight);
                foreach (Screen screen in Screen.AllScreens)
                {
                    Rectangle visible = Rectangle.Intersect(screen.WorkingArea, probe);
                    if (visible.Width >= 40 && visible.Height >= 12) return new Point(x, y);
                }

                return null;
            }
            catch { return null; }
        }

        private void SavePosition()
        {
            try
            {
                if (!customLocation.HasValue) return;
                string dir = Path.GetDirectoryName(PositionFile);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(PositionFile,
                    customLocation.Value.X + "," + customLocation.Value.Y);
            }
            catch { }
        }

        private void ResetPosition()
        {
            customLocation = null;
            try { if (File.Exists(PositionFile)) File.Delete(PositionFile); } catch { }
            FitToText();
        }

        private void ShowMenu(bool rescan = true)
        {
            toolTip.Hide(this);
            RefreshStatus();
            BuildMenu();
            ShowMenuAboveBar();
            if (rescan) BeginScan(true);
        }

        private void ShowMenuAboveBar()
        {
            Size preferred = menu.GetPreferredSize(Size.Empty);
            Rectangle area = Screen.FromControl(this).WorkingArea;

            int x = Math.Max(area.Left, Math.Min(Left + Width - preferred.Width, area.Right - preferred.Width));
            int y = Top - preferred.Height - 4;
            if (y < area.Top) y = Math.Min(Top + Height + 4, area.Bottom - preferred.Height);

            menu.Show(this, new Point(x - Left, y - Top));
        }

        private void RefreshStatus()
        {
            if (Interlocked.CompareExchange(ref statusBusy, 1, 0) != 0) return;

            ThreadPool.QueueUserWorkItem(delegate
            {
                string ssid = null;
                int signal = 0;
                bool connected = false;

                try { connected = WlanApi.TryGetCurrent(out ssid, out signal); }
                catch { connected = false; }

                try
                {
                    if (!IsHandleCreated || IsDisposed) return;
                    BeginInvoke((Action)(() => ApplyStatus(connected, ssid, signal)));
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
                finally
                {
                    Interlocked.Exchange(ref statusBusy, 0);
                }
            });
        }

        private void ApplyStatus(bool connected, string ssid, int signal)
        {
            currentSsid = connected && !string.IsNullOrEmpty(ssid) ? ssid : null;
            currentSignal = signal;

            FitToText();
            UpdateToolTip();
            Invalidate();
        }

        // 面板展开或刘海模式下不弹气泡，避免遮挡面板
        private void UpdateToolTip()
        {
            if (notchMode || expanded) toolTip.SetToolTip(this, null);
            else toolTip.SetToolTip(this, currentSsid != null
                ? "当前 WiFi：" + currentSsid + "（" + currentSignal + "%）\n左键点击切换网络"
                : "当前未连接 WiFi\n左键点击选择网络");
        }

        private void BeginScan(bool trigger = false)
        {
            if (Interlocked.CompareExchange(ref scanBusy, 1, 0) != 0) return;

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (trigger)
                    {
                        // 先命令系统真实扫描一次，等结果进入服务缓存后再读
                        try { WlanApi.TriggerScan(); } catch { }
                        Thread.Sleep(2500);
                    }

                    List<WlanApi.Network> result = null;
                    try { result = WlanApi.Scan(); }
                    catch { result = null; }

                    if (result == null) return;

                    if (!IsHandleCreated || IsDisposed) return;
                    BeginInvoke((Action)(() =>
                    {
                        networks = Merge(networks, result);
                        RefreshNotchList();
                        if (expanded) ApplyPanelLayout();
                        if (menu.Visible)
                        {
                            menu.Close();
                            BeginInvoke((Action)(() => ShowMenu(false)));
                        }
                    }));
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
                finally
                {
                    Interlocked.Exchange(ref scanBusy, 0);
                }
            });
        }

        // 把新扫描结果并入已有列表：同 SSID 取较高信号，避免某次扫描不完整导致列表变少；
        // 超过 24 小时未再出现的网络视为已消失，从列表中移除。
        private static List<WlanApi.Network> Merge(List<WlanApi.Network> old, List<WlanApi.Network> fresh)
        {
            DateTime now = DateTime.Now;
            var map = new Dictionary<string, WlanApi.Network>(StringComparer.OrdinalIgnoreCase);

            foreach (WlanApi.Network item in old)
            {
                if ((now - item.LastSeen).TotalHours < 24) map[item.Ssid] = item;
            }

            foreach (WlanApi.Network item in fresh)
            {
                WlanApi.Network exist;
                if (map.TryGetValue(item.Ssid, out exist))
                {
                    exist.Signal = Math.Max(exist.Signal, item.Signal);
                    exist.HasProfile = item.HasProfile;
                    exist.LastSeen = now;
                }
                else
                {
                    item.LastSeen = now;
                    map[item.Ssid] = item;
                }
            }

            var result = new List<WlanApi.Network>(map.Values);
            result.Sort((a, b) => a.Signal.CompareTo(b.Signal));
            return result;
        }

        private void BuildMenu()
        {
            menu.Items.Clear();

            string headerText = currentSsid != null
                ? "当前：" + currentSsid + "   " + currentSignal + "%"
                : "当前：未连接";
            menu.Items.Add(new ToolStripMenuItem(headerText) { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());

            if (networks.Count == 0)
            {
                menu.Items.Add(new ToolStripMenuItem("正在搜索网络…") { Enabled = false });
            }
            else
            {
                foreach (WlanApi.Network network in networks)
                {
                    bool isCurrent = currentSsid != null &&
                        string.Equals(network.Ssid, currentSsid, StringComparison.OrdinalIgnoreCase);
                    var item = new ToolStripMenuItem(
                        (isCurrent ? "✓ " : "    ") + network.Ssid + "   " + network.Signal + "%");
                    item.Tag = network.Ssid;
                    if (isCurrent) item.Font = boldFont;
                    if (!network.HasProfile) item.ToolTipText = "该网络未保存过密码，可能需要通过 Windows 快捷设置输入密码";
                    string ssid = network.Ssid;
                    item.Click += (s, e) => ConnectTo(ssid);
                    menu.Items.Add(item);
                }
            }

            menu.Items.Add(new ToolStripSeparator());

            var rescan = new ToolStripMenuItem("刷新网络列表");
            rescan.Click += (s, e) => BeginScan(true);
            menu.Items.Add(rescan);

            var settings = new ToolStripMenuItem("打开 Windows 网络设置");
            settings.Click += (s, e) =>
            {
                try { Process.Start("ms-settings:network-wifi"); } catch { }
            };
            menu.Items.Add(settings);

            var reset = new ToolStripMenuItem("恢复默认位置");
            reset.Click += (s, e) => ResetPosition();
            menu.Items.Add(reset);

            var notch = new ToolStripMenuItem("刘海屏模式") { Checked = notchMode };
            notch.Click += (s, e) => SetNotchMode(!notchMode);
            menu.Items.Add(notch);

            var exit = new ToolStripMenuItem("退出");
            exit.Click += (s, e) =>
            {
                statusTimer.Stop();
                scanTimer.Stop();
                menu.Close();
                Application.Exit();
            };
            menu.Items.Add(exit);
        }

        private void ConnectTo(string ssid)
        {
            menu.Close();

            ThreadPool.QueueUserWorkItem(delegate
            {
                bool requestAccepted = false;
                try { requestAccepted = WlanApi.Connect(ssid); }
                catch { requestAccepted = false; }

                Thread.Sleep(1500);

                string current = null;
                int signal = 0;
                try { WlanApi.TryGetCurrent(out current, out signal); }
                catch { }

                bool connected = current != null &&
                    string.Equals(current, ssid, StringComparison.OrdinalIgnoreCase);

                try
                {
                    if (!IsHandleCreated || IsDisposed) return;
                    BeginInvoke((Action)(() =>
                    {
                        if (connected)
                        {
                            currentSsid = current;
                            currentSignal = signal;
                            FitToText();
                            Invalidate();
                            BeginScan();
                        }
                        else
                        {
                            ShowHint(requestAccepted
                                ? "正在连接 " + ssid + "，若长时间未成功请按 Win+A 手动连接"
                                : "无法直接连接 " + ssid + "\n该网络未保存密码，请按 Win+A 打开快捷设置");
                        }
                    }));
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            });
        }

        private void ShowHint(string message)
        {
            var hint = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                TopMost = true,
                StartPosition = FormStartPosition.Manual,
                BackColor = Color.FromArgb(45, 45, 45),
                Width = 320,
                Height = 58
            };

            using (GraphicsPath path = RoundedRect(new Rectangle(0, 0, hint.Width, hint.Height), Corner))
            {
                hint.Region = new Region(path);
            }

            hint.Controls.Add(new Label
            {
                Text = message,
                ForeColor = Color.Gainsboro,
                Font = textFont,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            });

            hint.Location = new Point(Left + (Width - hint.Width) / 2, Top - hint.Height - 8);
            hint.Show(this);

            var timer = new System.Windows.Forms.Timer { Interval = 4000 };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                timer.Dispose();
                hint.Close();
            };
            timer.Start();
        }

        private void FitToText()
        {
            if (notchMode)
            {
                ApplyPanelLayout();
                return;
            }

            // 面板展开中或收起动画播放中，不要缩回条子
            if (expanded || (animTimer != null && animTimer.Enabled)) return;

            string label = currentSsid ?? "未连接";
            int width = MeasureBarWidth();

            if (Width != width || Height != BarHeight) Size = new Size(width, BarHeight);

            Location = ClampToScreen(customLocation ?? DefaultLocation(width));

            var regionRect = new Rectangle(0, 0, Width, Height);
            using (GraphicsPath path = RoundedRect(regionRect, Corner))
            {
                Region = new Region(path);
            }
        }

        // 收起状态下条子的宽度（不含刘海模式额外的箭头留白）
        private int MeasureBarWidth()
        {
            string label = currentSsid ?? "未连接";
            Size textSize = TextRenderer.MeasureText(label, textFont);
            return 10 + IconWidth + 7 + textSize.Width + 8 + 10 + 10;
        }

        private static Point DefaultLocation(int width)
        {
            Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
            return new Point(workArea.Right - width - 12, workArea.Bottom - BarHeight - 4);
        }

        // ===== 刘海屏展开面板 =====

        private void SetNotchMode(bool on)
        {
            notchMode = on;
            expanded = false;
            expandT = 0f;
            animFromT = 0f;
            animTargetT = 0f;
            hoverRow = -1;
            scrollOffset = 0;
            if (animTimer != null) animTimer.Stop();

            // 刘海模式下不再显示悬浮提示，避免气泡盖住展开面板
            toolTip.Active = !on;
            if (on) toolTip.Hide(this);
            UpdateToolTip();

            if (notchMode) ApplyPanelLayout();
            else
            {
                FitToText();
                Invalidate();
                UpdateShadow();
            }
            UpdateOpacity();
        }

        private void UpdateOpacity()
        {
            if (!notchMode)
            {
                if (Opacity < 1.0) Opacity = 1.0;
                return;
            }
            // 展开中、鼠标悬停、窗口激活时完全不透明
            bool active = expanded || hovering || ContainsFocus;
            double target = active ? 1.0 : 0.72;
            if (Math.Abs(Opacity - target) > 0.001)
            {
                Opacity = target;
                // Opacity 变化会影响阴影合成，需要重绘阴影
                UpdateShadow();
            }
        }

        private void ExpandPanel()
        {
            if (expanded) return;

            RefreshLocalAddress();

            if (!notchMode)
            {
                // 记录条子原位，并按屏幕剩余空间决定向上还是向下展开
                panelAnchor = Location;
                hasPanelAnchor = true;

                int collapsedWidth, openWidth, openHeight;
                MeasureNotchPanel(out collapsedWidth, out openWidth, out openHeight);

                Point center = new Point(panelAnchor.X + Width / 2, panelAnchor.Y + BarHeight / 2);
                Rectangle area = Screen.FromPoint(center).WorkingArea;
                panelUp = panelAnchor.Y + openHeight > area.Bottom;
            }

            expanded = true;
            hoverRow = -1;
            hoverFooter = -1;
            hoverMeta = -1;
            hoverMore = false;
            showAllNetworks = false;
            scrollOffset = 0;
            copyFeedback = false;
            copyTimer.Stop();
            toolTip.Hide(this);
            UpdateToolTip();
            RefreshNotchList();
            ApplyPanelLayout();
            StartPanelAnimation();
            UpdateOpacity();
            Activate();
            RefreshStatus();
            BeginScan(true);
            ipTimer.Start();
        }

        private void CollapsePanel()
        {
            if (!expanded) return;
            expanded = false;
            hoverRow = -1;
            hoverFooter = -1;
            hoverMeta = -1;
            hoverMore = false;
            showAllNetworks = false;
            scrollOffset = 0;
            copyFeedback = false;
            copyTimer.Stop();
            ipTimer.Stop();
            UpdateToolTip();
            StartPanelAnimation();
            UpdateOpacity();
        }

        // 动画目标始终跟随 expanded，避免展开与收起互相覆盖
        private void StartPanelAnimation()
        {
            if (animTimer == null)
            {
                animTimer = new System.Windows.Forms.Timer { Interval = 15 };
                animTimer.Tick += (s, e) => PanelAnimTick();
            }

            animFromT = expandT;
            animTargetT = expanded ? 1f : 0f;
            animStartTicks = Environment.TickCount;
            animTimer.Start();
        }

        private void PanelAnimTick()
        {
            float duration = animTargetT > animFromT ? 180f : 140f;
            float progress = Math.Min(1f, (Environment.TickCount - animStartTicks) / duration);
            float eased = 1f - (float)Math.Pow(1f - progress, 3);
            expandT = animFromT + (animTargetT - animFromT) * eased;

            if (progress >= 1f)
            {
                expandT = animTargetT;
                animTimer.Stop();
            }

            ApplyPanelLayout();
        }

        private void ApplyPanelLayout()
        {
            int collapsedWidth, openWidth, openHeight;
            MeasureNotchPanel(out collapsedWidth, out openWidth, out openHeight);

            int width = collapsedWidth + (int)Math.Round((openWidth - collapsedWidth) * expandT);
            int height = BarHeight + (int)Math.Round((openHeight - BarHeight) * expandT);

            int x, y;
            if (notchMode)
            {
                Rectangle bounds = Screen.PrimaryScreen.Bounds;
                x = bounds.Left + (bounds.Width - width) / 2;
                y = bounds.Top;
            }
            else
            {
                Point anchor = hasPanelAnchor
                    ? panelAnchor
                    : (customLocation ?? DefaultLocation(collapsedWidth));
                Rectangle area = Screen.FromPoint(
                    new Point(anchor.X + collapsedWidth / 2, anchor.Y + BarHeight / 2)).WorkingArea;

                // 屏幕放不下完整列表时压缩高度让列表滚动，而不是把面板顶到屏幕边缘
                int above = Math.Max(0, anchor.Y - area.Top);
                int below = Math.Max(0, area.Bottom - (anchor.Y + BarHeight));
                int available = Math.Max(above, below);
                if (openHeight > available)
                {
                    openHeight = Math.Max(BarHeight + 70, available);
                    height = BarHeight + (int)Math.Round((openHeight - BarHeight) * expandT);
                }

                panelUp = below < openHeight;

                x = anchor.X + (collapsedWidth - width) / 2;
                y = panelUp ? anchor.Y + BarHeight - height : anchor.Y;

                // 保证面板不会横向跑出屏幕
                x = Math.Max(area.Left, Math.Min(x, area.Right - width));
                if (y < area.Top) y = area.Top;
            }

            if (Bounds.X != x || Bounds.Y != y ||
                Bounds.Width != width || Bounds.Height != height)
            {
                SetBounds(x, y, width, height);
            }

            UpdateRegion();
            UpdateShadow();

            // 高度变化后滚动位置可能越界
            int maxScroll = Math.Max(0, TotalListRows - VisibleRowCount);
            if (scrollOffset > maxScroll) scrollOffset = maxScroll;

            Invalidate();
        }

        // 面板投影：用分层窗口在面板下方绘制一层模糊黑色阴影
        private void UpdateShadow()
        {
            // 刘海模式常驻投影；普通模式只在展开时投影（条子本身有边框）
            if (!IsHandleCreated || IsDisposed || !(notchMode || expanded))
            {
                if (shadowWindow != null) shadowWindow.Visible = false;
                return;
            }

            using (Bitmap bitmap = BuildShadowBitmap())
            {
                if (shadowWindow == null) shadowWindow = new ShadowWindow();
                Point location = notchMode
                    ? new Point(Left - ShadowSpread, Top)
                    : new Point(Left - ShadowSpread, Top - ShadowSpread);
                shadowWindow.UpdateLayered(bitmap, location);
                if (!shadowWindow.Visible) shadowWindow.Visible = true;

                // 保证投影始终位于主窗口之下
                SetWindowPos(shadowWindow.Handle, Handle, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
        }

        private Bitmap BuildShadowBitmap()
        {
            bool topFlush = notchMode;   // 刘海贴住屏幕顶边，顶部不需要外扩
            int offsetY = topFlush ? ShadowOffsetY : ShadowSpread;
            int width = Width + ShadowSpread * 2;
            int height = Height + offsetY + ShadowSpread;
            var alpha = new int[width * height];

            float topRadius = notchMode ? 0f : Corner;
            float bottomRadius = notchMode
                ? NotchCollapsedRadius + (NotchBottomRadius - NotchCollapsedRadius) * expandT
                : Corner;

            using (var mask = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(mask))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (GraphicsPath path = PanelPath(
                        new RectangleF(ShadowSpread, offsetY, Width, Height), topRadius, bottomRadius))
                    using (var brush = new SolidBrush(Color.White))
                    {
                        g.FillPath(brush, path);
                    }
                }

                BitmapData data = mask.LockBits(new Rectangle(0, 0, width, height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var pixels = new int[width * height];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                mask.UnlockBits(data);

                for (int i = 0; i < alpha.Length; i++) alpha[i] = (pixels[i] >> 24) & 0xFF;
            }

            // 两次方框模糊得到柔和的投影边缘
            BoxBlur(alpha, width, height, 11);
            BoxBlur(alpha, width, height, 11);

            var result = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            BitmapData target = result.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            var shadowPixels = new int[width * height];
            double opacity = Opacity;
            for (int i = 0; i < shadowPixels.Length; i++)
            {
                int a = (int)(alpha[i] * ShadowAlpha * opacity / 255.0);
                if (a > 255) a = 255;
                shadowPixels[i] = a << 24;
            }
            Marshal.Copy(shadowPixels, 0, target.Scan0, shadowPixels.Length);
            result.UnlockBits(target);
            return result;
        }

        private static void BoxBlur(int[] data, int width, int height, int radius)
        {
            var temp = new int[data.Length];
            int window = radius * 2 + 1;

            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                int sum = 0;
                for (int x = -radius; x <= radius; x++) sum += data[row + Math.Min(width - 1, Math.Max(0, x))];
                for (int x = 0; x < width; x++)
                {
                    temp[row + x] = sum / window;
                    sum += data[row + Math.Min(width - 1, x + radius + 1)] -
                           data[row + Math.Max(0, x - radius)];
                }
            }

            for (int x = 0; x < width; x++)
            {
                int sum = 0;
                for (int y = -radius; y <= radius; y++) sum += temp[Math.Min(height - 1, Math.Max(0, y)) * width + x];
                for (int y = 0; y < height; y++)
                {
                    data[y * width + x] = sum / window;
                    sum += temp[Math.Min(height - 1, y + radius + 1) * width + x] -
                           temp[Math.Max(0, y - radius) * width + x];
                }
            }
        }

        private void UpdateRegion()
        {
            // 灵动岛风格：贴住屏幕顶边，顶部不圆角，只保留底部圆角
            float topRadius = notchMode ? 0f : Corner;
            float bottomRadius = notchMode
                ? NotchCollapsedRadius + (NotchBottomRadius - NotchCollapsedRadius) * expandT
                : Corner;

            using (GraphicsPath path = PanelPath(new Rectangle(0, 0, Width, Height), topRadius, bottomRadius))
            {
                Region = new Region(path);
            }
        }

        private void RefreshNotchList()
        {
            notchNetworks = new List<WlanApi.Network>(networks);
            notchNetworks.Sort((a, b) => b.Signal.CompareTo(a.Signal));
        }

        private void MeasureNotchPanel(out int collapsedWidth, out int openWidth, out int openHeight)
        {
            string label = currentSsid ?? "未连接";
            int textWidth = TextRenderer.MeasureText(label, textFont).Width;
            // 刘海模式多留 16px 给下拉箭头；普通模式沿用条子宽度
            collapsedWidth = notchMode
                ? 10 + IconWidth + 7 + textWidth + 8 + 10 + 10 + 16
                : 10 + IconWidth + 7 + textWidth + 8 + 10 + 10;

            int widestSsid = TextRenderer.MeasureText("正在搜索网络…", textFont).Width;
            int widestSignal = TextRenderer.MeasureText("100%", textFont).Width;
            foreach (WlanApi.Network network in notchNetworks)
            {
                widestSsid = Math.Max(widestSsid, TextRenderer.MeasureText(network.Ssid, boldFont).Width);
                widestSignal = Math.Max(widestSignal,
                    TextRenderer.MeasureText(network.Signal + "%", textFont).Width);
            }
            if (HasMoreNetworks)
            {
                widestSsid = Math.Max(widestSsid, TextRenderer.MeasureText(
                    "更多 (" + (notchNetworks.Count - MaxCollapsedNetworks) + ")", textFont).Width);
            }

            // 布局：勾选槽 + SSID 左对齐 + 信号右对齐；DrawString 比测量略宽，留 12px 余量
            openWidth = Math.Max(collapsedWidth,
                NotchPaddingX * 2 + NotchCheckSlot + widestSsid + 16 + widestSignal + 12);

            // 信息行：左侧标签 + 右侧等宽数值，取两行中较宽的一行；
            // 预留「已复制」提示的宽度，避免提示与数值重叠
            int toastWidth = TextRenderer.MeasureText("已复制", textFont).Width;
            int metaWidth = Math.Max(
                TextRenderer.MeasureText("IP地址", textFont).Width + 16 + toastWidth +
                    TextRenderer.MeasureText(localIp ?? "—", textFont).Width,
                TextRenderer.MeasureText("IP网关", textFont).Width + 24 +
                    TextRenderer.MeasureText(gatewayIp ?? "—", textFont).Width);
            openWidth = Math.Max(openWidth, NotchPaddingX * 2 + metaWidth);

            int rows = Math.Max(1, TotalListRows);
            int naturalHeight = BarHeight + NotchListTopPadding + rows * RowHeight +
                                NotchListBottomPadding + MetaBlockHeight + FooterHeight;
            openHeight = Math.Min(Screen.PrimaryScreen.WorkingArea.Height - 60,
                                  Math.Max(BarHeight + 70, naturalHeight));
        }

        // 当前实际显示的网络数：折叠态只取前 N 个
        private int DisplayedNetworkCount
        {
            get
            {
                if (notchNetworks.Count <= MaxCollapsedNetworks) return notchNetworks.Count;
                return showAllNetworks ? notchNetworks.Count : MaxCollapsedNetworks;
            }
        }

        private bool HasMoreNetworks
        {
            get { return notchNetworks.Count > MaxCollapsedNetworks; }
        }

        // 列表总行数（含「更多 / 收起」行）
        private int TotalListRows
        {
            get { return DisplayedNetworkCount + (HasMoreNetworks ? 1 : 0); }
        }

        private int VisibleRowCount
        {
            get
            {
                return Math.Max(1, (Height - BarHeight - NotchListTopPadding -
                                    NotchListBottomPadding - MetaBlockHeight - FooterHeight) / RowHeight);
            }
        }

        private int MaxScroll
        {
            get { return Math.Max(0, TotalListRows - VisibleRowCount); }
        }

        // 信息行矩形：0 = 本机 IP，1 = 网关
        private Rectangle MetaRowRect(int index)
        {
            int top = BodyRect().Bottom - FooterHeight - MetaBlockHeight + 2 + index * MetaRowHeight;
            return new Rectangle(8, top, Width - 16, MetaRowHeight);
        }

        // 取当前上网网卡的第一个 IPv4 及其网关，避免解析 ipconfig 文本
        private void RefreshLocalAddress()
        {
            string ip = null;
            string gateway = null;

            try
            {
                foreach (NetworkInterface card in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (card.OperationalStatus != OperationalStatus.Up) continue;
                    if (card.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        card.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    IPInterfaceProperties properties = card.GetIPProperties();
                    string candidate = null;
                    foreach (UnicastIPAddressInformation address in properties.UnicastAddresses)
                    {
                        if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        candidate = address.Address.ToString();
                        break;
                    }

                    if (candidate == null) continue;

                    ip = candidate;
                    foreach (GatewayIPAddressInformation item in properties.GatewayAddresses)
                    {
                        if (item.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        gateway = item.Address.ToString();
                        break;
                    }
                    break;
                }
            }
            catch { }

            if (ip == localIp && gateway == gatewayIp) return;

            localIp = ip;
            gatewayIp = gateway;
            if (expanded) Invalidate();
        }

        private void CopyLocalAddress()
        {
            if (string.IsNullOrEmpty(localIp)) return;

            try { Clipboard.SetText(localIp); }
            catch { return; }

            copyFeedback = true;
            copyTimer.Stop();
            copyTimer.Start();
            Invalidate();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape && expanded)
            {
                CollapsePanel();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (expanded && TotalListRows > VisibleRowCount)
            {
                int delta = e.Delta > 0 ? -1 : 1;
                scrollOffset = Math.Max(0, Math.Min(MaxScroll, scrollOffset + delta));
                hoverRow = -1;
                hoverMore = false;
                Invalidate();
                return;
            }
            base.OnMouseWheel(e);
        }

        // 头部（条子）在窗口中的位置：普通模式向上展开时贴在窗口底部
        private Rectangle HeaderRect()
        {
            int top = (!notchMode && panelUp) ? Math.Max(0, Height - BarHeight) : 0;
            return new Rectangle(0, top, Width, Math.Min(BarHeight, Height));
        }

        // 面板内容区（不含头部）
        private Rectangle BodyRect()
        {
            Rectangle header = HeaderRect();
            if (header.Top <= 0) return new Rectangle(0, header.Bottom, Width, Math.Max(0, Height - header.Bottom));
            return new Rectangle(0, 0, Width, Math.Max(0, header.Top));
        }

        private void UpdateHoverRow(Point location)
        {
            int row = -1;
            bool more = false;
            int meta = -1;
            int listTop = BodyRect().Top + NotchListTopPadding;
            int listBottom = listTop + VisibleRowCount * RowHeight;

            if (location.Y >= listTop && location.Y < listBottom)
            {
                int visibleIndex = (location.Y - listTop) / RowHeight;
                int index = scrollOffset + visibleIndex;
                if (index < DisplayedNetworkCount) row = index;
                else if (index == DisplayedNetworkCount && HasMoreNetworks) more = true;
            }
            else
            {
                for (int i = 0; i < 2; i++)
                {
                    if (MetaRowRect(i).Contains(location))
                    {
                        meta = i;
                        break;
                    }
                }
            }

            if (row != hoverRow || more != hoverMore || meta != hoverMeta)
            {
                hoverRow = row;
                hoverMore = more;
                hoverMeta = meta;
                Invalidate();
            }
        }

        private void UpdateFooterHover(Point location)
        {
            int hover = -1;
            if (footerNormalRect.Contains(location)) hover = 0;
            else if (footerExitRect.Contains(location)) hover = 1;

            if (hover != hoverFooter)
            {
                hoverFooter = hover;
                Invalidate();
            }
        }

        private void HandlePanelClick(Point location)
        {
            if (HeaderRect().Contains(location))
            {
                CollapsePanel();
                return;
            }

            if (footerExitRect.Contains(location))
            {
                statusTimer.Stop();
                scanTimer.Stop();
                if (animTimer != null) animTimer.Stop();
                Application.Exit();
                return;
            }

            if (footerNormalRect.Contains(location))
            {
                SetNotchMode(!notchMode);
                return;
            }

            // 信息行：IP 行点击复制，网关行不响应
            if (MetaRowRect(0).Contains(location))
            {
                CopyLocalAddress();
                return;
            }
            if (MetaRowRect(1).Contains(location)) return;

            int listTop = BodyRect().Top + NotchListTopPadding;
            if (location.Y >= listTop && location.Y < listTop + VisibleRowCount * RowHeight)
            {
                int visibleIndex = (location.Y - listTop) / RowHeight;
                if (visibleIndex < VisibleRowCount)
                {
                    int index = scrollOffset + visibleIndex;
                    if (index >= 0 && index < DisplayedNetworkCount)
                    {
                        string ssid = notchNetworks[index].Ssid;
                        CollapsePanel();
                        ConnectTo(ssid);
                    }
                    else if (index == DisplayedNetworkCount && HasMoreNetworks)
                    {
                        ToggleShowAllNetworks();
                    }
                }
            }
        }

        // 展开 / 收起其余网络；关闭面板时由 CollapsePanel 复位
        private void ToggleShowAllNetworks()
        {
            showAllNetworks = !showAllNetworks;
            hoverRow = -1;
            hoverMore = false;
            scrollOffset = 0;
            ApplyPanelLayout();
        }

        private void DrawPanelBody(Graphics g, Rectangle body)
        {
            float alpha = Math.Max(0f, Math.Min(1f, (expandT - 0.3f) / 0.5f));
            if (alpha <= 0f) return;

            int a = (int)(255 * alpha);

            int listTop = body.Top + NotchListTopPadding;
            int visible = VisibleRowCount;
            int start = Math.Max(0, Math.Min(scrollOffset, MaxScroll));

            if (notchNetworks.Count == 0)
            {
                using (var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                })
                using (var brush = new SolidBrush(Color.FromArgb(a * 100 / 255, 255, 255, 255)))
                {
                    g.DrawString("正在搜索网络…", textFont, brush,
                        new RectangleF(0, listTop, Width, RowHeight), format);
                }
            }
            else
            using (var left = new StringFormat
            {
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            })
            using (var right = (StringFormat)left.Clone())
            {
                right.Alignment = StringAlignment.Far;

                int displayed = DisplayedNetworkCount;
                bool moreRow = HasMoreNetworks;
                int totalRows = displayed + (moreRow ? 1 : 0);

                for (int i = 0; i < visible; i++)
                {
                    int index = start + i;
                    if (index >= totalRows) break;

                    int y = listTop + i * RowHeight;
                    int contentLeft = NotchPaddingX + NotchCheckSlot;

                    // 列表末尾的「更多 (N) / 收起」行
                    if (index >= displayed)
                    {
                        DrawMoreRow(g, y, contentLeft, a, left);
                        continue;
                    }

                    WlanApi.Network network = notchNetworks[index];
                    bool isCurrent = currentSsid != null &&
                        string.Equals(network.Ssid, currentSsid, StringComparison.OrdinalIgnoreCase);

                    if (index == hoverRow)
                    {
                        using (GraphicsPath hoverPath = RoundedRect(
                            new RectangleF(8, y + 2, Width - 16, RowHeight - 4), 8f))
                        using (var hoverBrush = new SolidBrush(Color.FromArgb(a * 22 / 255, 255, 255, 255)))
                        {
                            g.FillPath(hoverBrush, hoverPath);
                        }
                    }

                    // 当前网络：macOS 风格蓝色实心圆 + 白色对勾
                    if (isCurrent)
                    {
                        float cy = y + RowHeight / 2f;
                        float cx = NotchPaddingX + NotchCheckSlot / 2f - 1f;
                        using (var dot = new SolidBrush(Color.FromArgb(a, MacAccent)))
                        {
                            g.FillEllipse(dot, cx - 6.5f, cy - 6.5f, 13f, 13f);
                        }
                        using (var check = new Pen(Color.FromArgb(a, 255, 255, 255), 1.6f)
                        { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        {
                            g.DrawLines(check, new[]
                            {
                                new PointF(cx - 2.6f, cy + 0.2f),
                                new PointF(cx - 0.6f, cy + 2.4f),
                                new PointF(cx + 3.2f, cy - 2.2f)
                            });
                        }
                    }

                    using (var brush = new SolidBrush(Color.FromArgb(isCurrent ? a : a * 205 / 255, 255, 255, 255)))
                    {
                        g.DrawString(network.Ssid, isCurrent ? boldFont : textFont, brush,
                            new RectangleF(contentLeft, y, Width - contentLeft - NotchPaddingX, RowHeight), left);
                    }
                    using (var brush = new SolidBrush(Color.FromArgb(a * 115 / 255, 255, 255, 255)))
                    {
                        g.DrawString(network.Signal + "%", textFont, brush,
                            new RectangleF(contentLeft, y, Width - contentLeft - NotchPaddingX, RowHeight), right);
                    }
                }
            }

            DrawMetaBlock(g, a);

            int footerTop = body.Bottom - FooterHeight;

            // 底部：两行菜单项，图标 + 文字，整行响应悬停
            footerNormalRect = new Rectangle(8, footerTop + 4, Width - 16, RowHeight - 2);
            footerExitRect = new Rectangle(8, footerTop + 4 + RowHeight, Width - 16, RowHeight - 2);

            for (int i = 0; i < 2; i++)
            {
                if (hoverFooter != i) continue;

                Rectangle rect = i == 0 ? footerNormalRect : footerExitRect;
                using (GraphicsPath hl = RoundedRect(
                    new RectangleF(rect.X, rect.Y, rect.Width, rect.Height), 7f))
                using (var brush = new SolidBrush(Color.FromArgb(a * 24 / 255, 255, 255, 255)))
                {
                    g.FillPath(brush, hl);
                }
            }

            DrawFooterItem(g, footerNormalRect, notchMode ? "普通模式" : "刘海屏模式", 0, true, a);
            DrawFooterItem(g, footerExitRect, "退出", 1, false, a);
        }

        // 列表末尾的「更多 (N) / 收起」折叠行
        private void DrawMoreRow(Graphics g, int y, int contentLeft, int a, StringFormat left)
        {
            if (hoverMore)
            {
                using (GraphicsPath path = RoundedRect(
                    new RectangleF(8, y + 2, Width - 16, RowHeight - 4), 8f))
                using (var brush = new SolidBrush(Color.FromArgb(a * 22 / 255, 255, 255, 255)))
                {
                    g.FillPath(brush, path);
                }
            }

            float cx = NotchPaddingX + NotchCheckSlot / 2f - 1f;
            float cy = y + RowHeight / 2f;
            using (var pen = new Pen(Color.FromArgb(a * 160 / 255, 255, 255, 255), 1.5f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            })
            {
                if (showAllNetworks)
                {
                    g.DrawLines(pen, new[]
                    {
                        new PointF(cx - 2.6f, cy + 1.2f),
                        new PointF(cx, cy - 1.4f),
                        new PointF(cx + 2.6f, cy + 1.2f)
                    });
                }
                else
                {
                    g.DrawLines(pen, new[]
                    {
                        new PointF(cx - 2.6f, cy - 1.2f),
                        new PointF(cx, cy + 1.4f),
                        new PointF(cx + 2.6f, cy - 1.2f)
                    });
                }
            }

            string label = showAllNetworks
                ? "收起"
                : "更多 (" + (notchNetworks.Count - MaxCollapsedNetworks) + ")";
            using (var brush = new SolidBrush(Color.FromArgb(a * 160 / 255, 255, 255, 255)))
            {
                g.DrawString(label, textFont, brush,
                    new RectangleF(contentLeft, y, Width - contentLeft - NotchPaddingX, RowHeight), left);
            }
        }

        // 页脚上方的两行信息：本机 IP（可点击复制）与网关
        private void DrawMetaBlock(Graphics g, int a)
        {
            for (int i = 0; i < 2; i++)
            {
                if (hoverMeta != i) continue;

                Rectangle rect = MetaRowRect(i);
                using (GraphicsPath hl = RoundedRect(
                    new RectangleF(rect.X, rect.Y, rect.Width, rect.Height), 8f))
                using (var brush = new SolidBrush(Color.FromArgb(a * 22 / 255, 255, 255, 255)))
                {
                    g.FillPath(brush, hl);
                }
            }

            Rectangle ipRect = MetaRowRect(0);
            Rectangle gatewayRect = MetaRowRect(1);
            int labelWidth = TextRenderer.MeasureText("IP地址", textFont).Width;

            // 与网络列表同一套字色：左侧标签为次要色，右侧数值为主色
            using (var labelBrush = new SolidBrush(Color.FromArgb(a * 115 / 255, 255, 255, 255)))
            using (var valueBrush = new SolidBrush(Color.FromArgb(a * 205 / 255, 255, 255, 255)))
            using (var copiedBrush = new SolidBrush(Color.FromArgb(a, MacAccent)))
            using (var left = new StringFormat
            {
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            })
            using (var right = (StringFormat)left.Clone())
            {
                right.Alignment = StringAlignment.Far;

                g.DrawString("IP地址", textFont, labelBrush,
                    new RectangleF(NotchPaddingX, ipRect.Y, Width - NotchPaddingX * 2, ipRect.Height), left);
                if (copyFeedback)
                {
                    g.DrawString("已复制", textFont, copiedBrush,
                        new RectangleF(NotchPaddingX + labelWidth + 8, ipRect.Y,
                            Width - NotchPaddingX * 2, ipRect.Height), left);
                }
                g.DrawString(localIp ?? "—", textFont, valueBrush,
                    new RectangleF(0, ipRect.Y, Width - NotchPaddingX, ipRect.Height), right);

                g.DrawString("IP网关", textFont, labelBrush,
                    new RectangleF(NotchPaddingX, gatewayRect.Y, Width - NotchPaddingX * 2, gatewayRect.Height), left);
                g.DrawString(gatewayIp ?? "—", textFont, valueBrush,
                    new RectangleF(0, gatewayRect.Y, Width - NotchPaddingX, gatewayRect.Height), right);
            }
        }

        // 底部菜单行：图标 + 文字，悬停时整体提亮
        private void DrawFooterItem(Graphics g, Rectangle rect, string caption, int index, bool sliders, int a)
        {
            bool hover = hoverFooter == index;
            int iconAlpha = hover ? a * 236 / 255 : a * 165 / 255;
            int textAlpha = hover ? a * 242 / 255 : a * 192 / 255;

            int iconLeft = rect.X + FooterPadX;
            int cy = rect.Y + rect.Height / 2;

            using (var pen = new Pen(Color.FromArgb(iconAlpha, 255, 255, 255), 1.25f))
            using (var brush = new SolidBrush(Color.FromArgb(iconAlpha, 255, 255, 255)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                if (sliders) DrawSlidersGlyph(g, pen, brush, iconLeft, cy);
                else DrawPowerGlyph(g, pen, iconLeft, cy);
            }

            float textLeft = iconLeft + FooterIconSize + FooterIconGap;
            using (var brush = new SolidBrush(Color.FromArgb(textAlpha, 255, 255, 255)))
            using (var format = new StringFormat
            {
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            })
            {
                g.DrawString(caption, textFont, brush,
                    new RectangleF(textLeft, rect.Y, rect.Right - textLeft - FooterPadX, rect.Height), format);
            }
        }

        // 滑块图标：两条横线各带一个圆点滑块
        private static void DrawSlidersGlyph(Graphics g, Pen pen, Brush brush, int x, int cy)
        {
            const float radius = 2f;
            float[] ys = { cy - 3.4f, cy + 3.4f };
            float[] knobs = { x + 3.8f, x + 8.2f };

            for (int i = 0; i < 2; i++)
            {
                float y = ys[i];
                g.DrawLine(pen, x, y, knobs[i] - radius - 0.7f, y);
                g.DrawLine(pen, knobs[i] + radius + 0.7f, y, x + FooterIconSize, y);
                g.FillEllipse(brush, knobs[i] - radius, y - radius, radius * 2, radius * 2);
            }
        }

        // 电源图标：顶部开口的圆弧 + 竖线
        private static void DrawPowerGlyph(Graphics g, Pen pen, int x, int cy)
        {
            const float radius = 4.1f;
            float cx = x + FooterIconSize / 2f;

            g.DrawArc(pen, cx - radius, cy - radius, radius * 2, radius * 2, -55f, 290f);
            g.DrawLine(pen, cx, cy - radius - 1.5f, cx, cy - 0.7f);
        }

        // 支持上下圆角不同半径；顶部半径为 0 时与屏幕边缘齐平
        private static GraphicsPath PanelPath(RectangleF rect, float topRadius, float bottomRadius)
        {
            var path = new GraphicsPath();
            float topDiameter = topRadius * 2f;
            float bottomDiameter = bottomRadius * 2f;

            if (topRadius > 0.05f)
            {
                path.AddArc(rect.Left, rect.Top, topDiameter, topDiameter, 180, 90);
                path.AddArc(rect.Right - topDiameter, rect.Top, topDiameter, topDiameter, 270, 90);
            }
            else
            {
                path.AddLine(rect.Left, rect.Top, rect.Right, rect.Top);
            }

            path.AddArc(rect.Right - bottomDiameter, rect.Bottom - bottomDiameter,
                bottomDiameter, bottomDiameter, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - bottomDiameter,
                bottomDiameter, bottomDiameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        // 保证条子始终位于某个显示器的可见区域内
        private Point ClampToScreen(Point location)
        {
            Rectangle area = Screen.FromPoint(new Point(location.X + Width / 2, location.Y + Height / 2)).WorkingArea;
            int x = Math.Max(area.Left, Math.Min(location.X, area.Right - Width));
            int y = Math.Max(area.Top, Math.Min(location.Y, area.Bottom - Height));
            return new Point(x, y);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // 刘海模式参考灵动岛：纯黑底、贴住屏幕顶边、只保留底部大圆角、无描边
            float topRadius = notchMode ? 0f : Corner;
            float bottomRadius = notchMode
                ? NotchCollapsedRadius + (NotchBottomRadius - NotchCollapsedRadius) * expandT
                : Corner;

            Color background = hovering ? NotchHoverBackground : NotchBackground;

            using (GraphicsPath fillPath = PanelPath(new RectangleF(0, 0, Width, Height), topRadius, bottomRadius))
            using (var brush = new SolidBrush(background))
            {
                g.FillPath(brush, fillPath);
            }

            if (!notchMode)
            {
                // 边框路径偏移半个像素，1px 线正好压在整行/整列像素上，避免边缘发虚、有颗粒感
                PixelOffsetMode oldOffsetMode = g.PixelOffsetMode;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                using (GraphicsPath borderPath = PanelPath(
                    new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), topRadius, bottomRadius))
                using (var border = new Pen(Color.FromArgb(84, 84, 84), 1f))
                {
                    g.DrawPath(border, borderPath);
                }

                // 内侧一圈极轻的高光，模拟系统材质的受光面
                using (GraphicsPath rimPath = PanelPath(
                    new RectangleF(1.5f, 1.5f, Width - 3f, Height - 3f), topRadius, bottomRadius))
                using (var rim = new Pen(Color.FromArgb(18, 255, 255, 255), 1f))
                {
                    g.DrawPath(rim, rimPath);
                }
                g.PixelOffsetMode = oldOffsetMode;
            }

            // 头部固定为条子高度，展开后内容在剩余区域里绘制
            Rectangle header = HeaderRect();

            DrawWifiGlyph(g, new Rectangle(10, header.Top + (header.Height - 16) / 2, IconWidth, 16),
                currentSsid != null ? SignalColor(currentSignal) : Color.FromArgb(150, 150, 150));

            string label = currentSsid ?? "未连接";
            using (var brush = new SolidBrush(currentSsid != null
                ? Color.FromArgb(245, 245, 245)
                : Color.FromArgb(165, 165, 165)))
            using (var format = new StringFormat
            {
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            })
            {
                var textRect = new RectangleF(
                    10 + IconWidth + 7,
                    header.Top,
                    Width - (10 + IconWidth + 7) - 20,
                    header.Height);
                g.DrawString(label, textFont, brush, textRect, format);
            }

            using (var chevron = new Pen(Color.FromArgb(155, 155, 155), 1.4f))
            {
                float cx = Width - 15;
                float cy = header.Top + header.Height / 2f - 1;
                bool up = expanded && !notchMode && panelUp;
                g.DrawLines(chevron, up
                    ? new[]
                    {
                        new PointF(cx - 3f, cy + 1.5f),
                        new PointF(cx, cy - 2f),
                        new PointF(cx + 3f, cy + 1.5f)
                    }
                    : new[]
                    {
                        new PointF(cx - 3f, cy - 1.5f),
                        new PointF(cx, cy + 2f),
                        new PointF(cx + 3f, cy - 1.5f)
                    });
            }

            if (expandT > 0f) DrawPanelBody(g, BodyRect());
        }

        private static void DrawWifiGlyph(Graphics g, Rectangle box, Color color)
        {
            float cx = box.Left + box.Width / 2f;
            float cy = box.Bottom - 2f;

            using (var pen = new Pen(color, 1.9f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var dot = new SolidBrush(color))
            {
                for (int i = 0; i < 3; i++)
                {
                    float radius = 3.2f + i * 2.9f;
                    g.DrawArc(pen, cx - radius, cy - radius, radius * 2f, radius * 2f, 215f, 110f);
                }
                g.FillEllipse(dot, cx - 1.6f, cy - 1.6f, 3.2f, 3.2f);
            }
        }

        private static Color SignalColor(int signal)
        {
            if (signal >= 70) return Color.FromArgb(76, 217, 100);
            if (signal >= 40) return Color.FromArgb(255, 204, 0);
            return Color.FromArgb(255, 90, 80);
        }

        private static GraphicsPath RoundedRect(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            int diameter = radius * 2;
            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float diameter = radius * 2;
            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        // 只有下方两个圆角，上方与屏幕顶边齐平（刘海屏样式）
        private static GraphicsPath BottomRoundedRect(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float diameter = radius * 2;
            path.AddLine(rect.Left, rect.Top, rect.Right, rect.Top);
            path.AddLine(rect.Right, rect.Top, rect.Right, rect.Bottom - radius);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.AddLine(rect.Left, rect.Bottom - radius, rect.Left, rect.Top);
            path.CloseFigure();
            return path;
        }
    }

    // 逐像素透明的投影窗口：接收预乘 ARGB 位图，鼠标穿透且不抢焦点
    internal sealed class ShadowWindow : Form
    {
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int ULW_ALPHA = 0x00000002;
        private const byte AC_SRC_ALPHA = 0x01;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int CX; public int CY; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll")]
        private static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst,
            ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc,
            int crKey, ref BLENDFUNCTION pblend, int flags);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        public ShadowWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Size = new Size(1, 1);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 内容完全由 UpdateLayeredWindow 提供
        }

        public void UpdateLayered(Bitmap bitmap, Point location)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
                oldBitmap = SelectObject(memDc, hBitmap);

                var size = new SIZE { CX = bitmap.Width, CY = bitmap.Height };
                var src = new POINT { X = 0, Y = 0 };
                var dst = new POINT { X = location.X, Y = location.Y };
                var blend = new BLENDFUNCTION
                {
                    BlendOp = 0,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA
                };

                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    internal sealed class DarkMenuColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Color.FromArgb(38, 38, 38); } }
        public override Color MenuItemSelected { get { return Color.FromArgb(64, 64, 64); } }
        public override Color MenuItemSelectedGradientBegin { get { return Color.FromArgb(64, 64, 64); } }
        public override Color MenuItemSelectedGradientEnd { get { return Color.FromArgb(64, 64, 64); } }
        public override Color MenuItemBorder { get { return Color.FromArgb(38, 38, 38); } }
        public override Color MenuBorder { get { return Color.FromArgb(80, 80, 80); } }
        public override Color ImageMarginGradientBegin { get { return Color.FromArgb(38, 38, 38); } }
        public override Color ImageMarginGradientMiddle { get { return Color.FromArgb(38, 38, 38); } }
        public override Color ImageMarginGradientEnd { get { return Color.FromArgb(38, 38, 38); } }
        public override Color SeparatorDark { get { return Color.FromArgb(64, 64, 64); } }
        public override Color SeparatorLight { get { return Color.FromArgb(64, 64, 64); } }
        public override Color CheckBackground { get { return Color.FromArgb(64, 64, 64); } }
        public override Color CheckSelectedBackground { get { return Color.FromArgb(64, 64, 64); } }
    }

    internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = !e.Item.Enabled
                ? Color.FromArgb(135, 135, 135)
                : (e.Item.Selected ? Color.White : Color.FromArgb(224, 224, 224));
            base.OnRenderItemText(e);
        }
    }
}
