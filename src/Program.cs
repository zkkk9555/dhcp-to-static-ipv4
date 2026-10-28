using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Windows.Forms;

namespace DhcpStaticIp
{
    public class AdapterInfo
    {
        public string Name = "";
        public string Description = "";
        public string Mac = "";
        public bool Dhcp;
        public string Ip = "";
        public string Mask = "";
        public string Gateway = "";
        public string DhcpServer = "";
        public List<string> Dns = new List<string>();
        public bool HasIPv4 { get { return Ip != ""; } }
    }

    public static class NetUtil
    {
        public static List<NetworkInterface> ActiveAdapters()
        {
            List<NetworkInterface> list = new List<NetworkInterface>();
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                bool hasV4 = false;
                try
                {
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork) { hasV4 = true; break; }
                    }
                }
                catch { }
                if (hasV4) list.Add(ni);
            }
            return list;
        }

        public static string FormatMac(PhysicalAddress pa)
        {
            if (pa == null) return "";
            byte[] b = pa.GetAddressBytes();
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < b.Length; i++)
            {
                if (i > 0) sb.Append("-");
                sb.Append(b[i].ToString("X2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public static AdapterInfo Read(string name)
        {
            AdapterInfo info = new AdapterInfo();
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.Name != name) continue;
                info.Name = ni.Name;
                info.Description = ni.Description;
                info.Mac = FormatMac(ni.GetPhysicalAddress());
                IPInterfaceProperties p = ni.GetIPProperties();
                try
                {
                    IPv4InterfaceProperties v4 = p.GetIPv4Properties();
                    if (v4 != null) info.Dhcp = v4.IsDhcpEnabled;
                }
                catch { }
                foreach (UnicastIPAddressInformation ua in p.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                    {
                        info.Ip = ua.Address.ToString();
                        if (ua.IPv4Mask != null) info.Mask = ua.IPv4Mask.ToString();
                    }
                }
                foreach (GatewayIPAddressInformation g in p.GatewayAddresses)
                {
                    if (g.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        info.Gateway = g.Address.ToString();
                        break;
                    }
                }
                foreach (IPAddress d in p.DnsAddresses)
                {
                    if (d.AddressFamily == AddressFamily.InterNetwork) info.Dns.Add(d.ToString());
                }
                foreach (IPAddress d in p.DhcpServerAddresses)
                {
                    if (d.AddressFamily == AddressFamily.InterNetwork)
                    {
                        info.DhcpServer = d.ToString();
                        break;
                    }
                }
                break;
            }
            if (info.Mask == "") info.Mask = "255.255.255.0";
            return info;
        }

        public static string Run(string file, string args, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    byte[] ob = ReadAllBytes(p.StandardOutput.BaseStream);
                    byte[] eb = ReadAllBytes(p.StandardError.BaseStream);
                    string o = Decode(ob);
                    string e = Decode(eb);
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return "[超时] " + (o + e).Trim();
                    }
                    return (o + e).Trim();
                }
            }
            catch (Exception ex)
            {
                return "[错误] " + ex.Message;
            }
        }

        private static byte[] ReadAllBytes(Stream stream)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] buf = new byte[4096];
                int n;
                while ((n = stream.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                return ms.ToArray();
            }
        }

        private static string Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch { return Encoding.Default.GetString(bytes); }
        }

        public static uint ToUint(string ip)
        {
            byte[] b = IPAddress.Parse(ip).GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        public static string DefaultAdapter()
        {
            List<NetworkInterface> list = ActiveAdapters();
            foreach (NetworkInterface ni in list)
            {
                AdapterInfo a = Read(ni.Name);
                if (a.Gateway != "") return ni.Name;
            }
            if (list.Count > 0) return list[0].Name;
            return "";
        }

        public static AdapterInfo Pick(string adapter)
        {
            string name = adapter;
            if (name == null || name == "") name = DefaultAdapter();
            return Read(name);
        }

        public static bool PingOk(string ip)
        {
            try
            {
                using (Ping ping = new Ping())
                {
                    PingReply reply = ping.Send(ip, 800);
                    return reply != null && reply.Status == IPStatus.Success;
                }
            }
            catch { return false; }
        }

        public static string SetStatic(string adapter, string targetIp, out bool verified)
        {
            verified = false;
            AdapterInfo cur = Read(adapter);
            StringBuilder log = new StringBuilder();
            if (!cur.HasIPv4) return "该网卡当前没有 IPv4 地址，无法推断子网掩码 / 网关，请先连接网络。";
            IPAddress ip;
            if (!IPAddress.TryParse(targetIp, out ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            {
                return "IP 地址格式不正确：" + targetIp;
            }
            string mask = cur.Mask == "" ? "255.255.255.0" : cur.Mask;
            string gateway = cur.Gateway;
            if (gateway == "")
            {
                string[] parts = cur.Ip.Split('.');
                if (parts.Length == 4) gateway = parts[0] + "." + parts[1] + "." + parts[2] + ".1";
            }
            if (gateway == "") return "找不到默认网关，无法设置静态 IP。";
            if (targetIp == gateway) return "IP 地址不能和默认网关相同。";
            string[] t = targetIp.Split('.');
            if (t.Length == 4 && (t[3] == "0" || t[3] == "255")) return "IP 地址的最后一节不能是 0 或 255。";
            uint ipU = ToUint(targetIp);
            uint maskU = ToUint(mask);
            uint gwU = ToUint(gateway);
            if ((ipU & maskU) != (gwU & maskU))
            {
                return "IP 地址 " + targetIp + " 与网关 " + gateway + "（掩码 " + mask + "）不在同一网段，无法使用。";
            }
            if (targetIp == cur.Ip && !cur.Dhcp)
            {
                verified = true;
                return "当前已经是静态 IP " + cur.Ip + "，无需修改。";
            }
            if (targetIp != cur.Ip && PingOk(targetIp))
            {
                log.AppendLine("警告：目标地址 " + targetIp + " 当前 ping 有响应，可能已被其他设备占用。");
                log.AppendLine("");
            }
            log.AppendLine("复用当前网络信息");
            log.AppendLine("  子网掩码 : " + mask);
            log.AppendLine("  默认网关 : " + gateway);
            log.AppendLine("  DNS      : " + (cur.Dns.Count == 0 ? "（无，将使用 223.5.5.5 / 119.29.29.29）" : string.Join("，", cur.Dns.ToArray())));
            log.AppendLine("");
            log.AppendLine("netsh interface ipv4 set address name=\"" + adapter + "\" static " + targetIp + " " + mask + " " + gateway);
            string r1 = Run("netsh", "interface ipv4 set address name=\"" + adapter + "\" static " + targetIp + " " + mask + " " + gateway, 30000);
            if (r1 != "") log.AppendLine("  " + r1);

            List<string> dns = new List<string>(cur.Dns);
            if (dns.Count == 0) { dns.Add("223.5.5.5"); dns.Add("119.29.29.29"); }
            string r2 = Run("netsh", "interface ipv4 set dnsservers name=\"" + adapter + "\" static " + dns[0] + " primary", 30000);
            if (r2 != "") log.AppendLine("  " + r2);
            for (int i = 1; i < dns.Count; i++)
            {
                string ri = Run("netsh", "interface ipv4 add dnsservers name=\"" + adapter + "\" " + dns[i] + " index=" + (i + 1), 30000);
                if (ri != "") log.AppendLine("  " + ri);
            }
            Run("ipconfig", "/flushdns", 10000);

            for (int i = 0; i < 10; i++)
            {
                System.Threading.Thread.Sleep(500);
                AdapterInfo now = Read(adapter);
                if (now.Ip == targetIp && !now.Dhcp)
                {
                    verified = true;
                    log.AppendLine("");
                    log.AppendLine("已生效：IPv4 = " + now.Ip + " / " + now.Mask + "，网关 = " + now.Gateway);
                    log.AppendLine("当前 DNS  : " + (now.Dns.Count == 0 ? "（无）" : string.Join("，", now.Dns.ToArray())));
                    break;
                }
            }
            if (!verified)
            {
                AdapterInfo now = Read(adapter);
                log.AppendLine("");
                log.AppendLine("警告：命令已执行，但读取到的当前地址是 " + (now.Ip == "" ? "（无）" : now.Ip) + "，请稍后点「刷新」确认。");
            }
            return log.ToString();
        }

        public static string SetDhcp(string adapter, out bool verified)
        {
            verified = false;
            StringBuilder log = new StringBuilder();
            log.AppendLine("netsh interface ipv4 set address name=\"" + adapter + "\" source=dhcp");
            string r1 = Run("netsh", "interface ipv4 set address name=\"" + adapter + "\" source=dhcp", 30000);
            if (r1 != "") log.AppendLine("  " + r1);
            string r2 = Run("netsh", "interface ipv4 set dnsservers name=\"" + adapter + "\" source=dhcp", 30000);
            if (r2 != "") log.AppendLine("  " + r2);
            log.AppendLine("ipconfig /renew（最多等 60 秒）");
            string r3 = Run("ipconfig", "/renew \"" + adapter + "\"", 60000);
            if (r3 != "") log.AppendLine("  " + r3.Replace("\r\n", " ").Replace("\n", " "));
            Run("ipconfig", "/flushdns", 10000);

            for (int i = 0; i < 40; i++)
            {
                System.Threading.Thread.Sleep(500);
                AdapterInfo now = Read(adapter);
                if (now.Dhcp && now.Ip != "")
                {
                    verified = true;
                    log.AppendLine("");
                    log.AppendLine("已恢复 DHCP：IPv4 = " + now.Ip + " / " + now.Mask + "，网关 = " + now.Gateway);
                    break;
                }
            }
            if (!verified)
            {
                AdapterInfo now = Read(adapter);
                log.AppendLine("");
                log.AppendLine("警告：已执行恢复命令，但当前仍显示 DHCP=" + (now.Dhcp ? "开" : "关") + "、IPv4=" + (now.Ip == "" ? "（无）" : now.Ip) + "，请检查路由器 DHCP 是否开启。");
            }
            return log.ToString();
        }
    }

    public static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(244, 245, 247);
        public static readonly Color Card = Color.White;
        public static readonly Color Border = Color.FromArgb(229, 231, 235);
        public static readonly Color Text = Color.FromArgb(15, 23, 42);
        public static readonly Color Muted = Color.FromArgb(100, 116, 139);
        public static readonly Color Faint = Color.FromArgb(148, 163, 184);
        public static readonly Color Accent = Color.FromArgb(37, 99, 235);
        public static readonly Color AccentHover = Color.FromArgb(29, 78, 216);
        public static readonly Color AccentPress = Color.FromArgb(30, 64, 175);
        public static readonly Color Success = Color.FromArgb(21, 128, 61);
        public static readonly Color SuccessBg = Color.FromArgb(220, 252, 231);
        public static readonly Color Warn = Color.FromArgb(180, 83, 9);
        public static readonly Color WarnBg = Color.FromArgb(254, 243, 199);
        public static readonly Color Danger = Color.FromArgb(185, 28, 28);
        public static readonly Color DangerBg = Color.FromArgb(254, 226, 226);
        public static readonly Color Inset = Color.FromArgb(248, 249, 251);

        public static Font Title()
        {
            return new Font("Microsoft YaHei UI", 16F, FontStyle.Bold);
        }
        public static Font Heading()
        {
            return new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold);
        }
        public static Font Body()
        {
            return new Font("Microsoft YaHei UI", 9F);
        }
        public static Font Small()
        {
            return new Font("Microsoft YaHei UI", 8.5F);
        }
        public static Font ButtonFont()
        {
            return new Font("Microsoft YaHei UI", 9.5F);
        }
        public static Font Value()
        {
            return new Font("Consolas", 9.5F);
        }
        public static Font Mono()
        {
            return new Font("Consolas", 8.5F);
        }
    }

    public class RoundedPanel : Panel
    {
        private int _radius = 12;
        private Color _border = Theme.Border;
        private Color _fill = Theme.Card;

        public int Radius { get { return _radius; } set { _radius = value; Invalidate(); } }
        public Color BorderColor { get { return _border; } set { _border = value; Invalidate(); } }
        public Color FillColor { get { return _fill; } set { _fill = value; Invalidate(); } }

        public RoundedPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = RoundedRect(r, _radius))
            {
                using (SolidBrush b = new SolidBrush(_fill)) g.FillPath(b, path);
                using (Pen p = new Pen(_border, 1f)) g.DrawPath(p, path);
            }
        }

        public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            if (d <= 0) { path.AddRectangle(bounds); return path; }
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    public class FlatButton : Button
    {
        public enum Kind { Primary, Secondary, Ghost }
        private Kind _kind = Kind.Primary;
        private bool _hover;
        private bool _pressed;

        public Kind Style { get { return _kind; } set { _kind = value; Invalidate(); } }

        public FlatButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Text = "";
            Cursor = Cursors.Hand;
            Font = Theme.ButtonFont();
            TabStop = true;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            Graphics g = pevent.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill;
            Color border = Color.Empty;
            Color text;
            if (_kind == Kind.Primary)
            {
                fill = !Enabled ? Color.FromArgb(203, 213, 225) : (_pressed ? Theme.AccentPress : (_hover ? Theme.AccentHover : Theme.Accent));
                text = Color.White;
            }
            else if (_kind == Kind.Secondary)
            {
                fill = !Enabled ? Theme.Inset : (_pressed ? Color.FromArgb(237, 239, 243) : (_hover ? Color.FromArgb(245, 246, 248) : Color.White));
                border = Theme.Border;
                text = Enabled ? Theme.Text : Theme.Faint;
            }
            else
            {
                fill = !Enabled ? Theme.Card : (_pressed ? Color.FromArgb(235, 237, 241) : (_hover ? Color.FromArgb(242, 244, 247) : Theme.Card));
                text = Enabled ? Theme.Muted : Theme.Faint;
            }
            using (GraphicsPath path = RoundedPanel.RoundedRect(r, Math.Max(6, Height / 5)))
            {
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
                if (border != Color.Empty)
                {
                    using (Pen p = new Pen(border, 1f)) g.DrawPath(p, path);
                }
            }
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            if (Focused)
            {
                Rectangle focus = new Rectangle(3, 3, Width - 7, Height - 7);
                using (Pen p = new Pen(Color.FromArgb(120, Theme.Accent), 1f))
                {
                    p.DashStyle = DashStyle.Dot;
                    g.DrawRectangle(p, focus);
                }
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { _pressed = true; Invalidate(); }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            _pressed = false; Invalidate();
            base.OnKeyUp(e);
        }
    }

    public class InputBox : RoundedPanel
    {
        public TextBox Box;
        private Label _placeholder;
        private bool _focused;

        public override string Text
        {
            get { return Box.Text; }
            set { Box.Text = value; UpdatePlaceholder(); }
        }

        public string Placeholder
        {
            get { return _placeholder.Text; }
            set { _placeholder.Text = value; UpdatePlaceholder(); }
        }

        public InputBox()
        {
            Radius = 8;
            FillColor = Color.White;
            BorderColor = Theme.Border;
            Height = 44;
            Padding = new Padding(0);

            Box = new TextBox();
            Box.BorderStyle = BorderStyle.None;
            Box.Font = new Font("Consolas", 11F);
            Box.BackColor = Color.White;
            Box.ForeColor = Theme.Text;
            Box.Location = new Point(14, 11);
            Box.Width = 200;
            Box.GotFocus += new EventHandler(BoxFocus);
            Box.LostFocus += new EventHandler(BoxBlur);
            Box.TextChanged += new EventHandler(BoxChanged);
            Controls.Add(Box);

            _placeholder = new Label();
            _placeholder.AutoSize = true;
            _placeholder.ForeColor = Theme.Faint;
            _placeholder.Font = Theme.Value();
            _placeholder.BackColor = Color.White;
            _placeholder.Location = new Point(15, 12);
            _placeholder.Visible = false;
            Controls.Add(_placeholder);
            _placeholder.BringToFront();
        }

        private void BoxFocus(object sender, EventArgs e) { _focused = true; BorderColor = Theme.Accent; UpdatePlaceholder(); }
        private void BoxBlur(object sender, EventArgs e) { _focused = false; BorderColor = Theme.Border; UpdatePlaceholder(); }
        private void BoxChanged(object sender, EventArgs e) { UpdatePlaceholder(); }

        private void UpdatePlaceholder()
        {
            _placeholder.Visible = !_focused && Box.Text.Length == 0;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Box != null) Box.Width = Width - 28;
        }
    }

    public class Pill : Control
    {
        private string _text = "";
        private Color _bg = Theme.SuccessBg;
        private Color _fg = Theme.Success;
        public override string Text { get { return _text; } set { _text = value; Invalidate(); } }
        public Color PillBack { get { return _bg; } set { _bg = value; Invalidate(); } }
        public Color PillFore { get { return _fg; } set { _fg = value; Invalidate(); } }

        public Pill()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Font = Theme.Small();
            Height = 24;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = RoundedPanel.RoundedRect(r, Math.Max(2, Height / 2)))
            {
                using (SolidBrush b = new SolidBrush(_bg)) g.FillPath(b, path);
            }
            TextRenderer.DrawText(g, _text, Font, r, _fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }

    public class Logo : Control
    {
        public Logo()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Width = 40;
            Height = 40;
            BackColor = Theme.Bg;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = RoundedPanel.RoundedRect(r, 11))
            {
                using (LinearGradientBrush b = new LinearGradientBrush(r, Color.FromArgb(59, 130, 246), Color.FromArgb(29, 78, 216), 55f))
                {
                    g.FillPath(b, path);
                }
            }
            TextRenderer.DrawText(g, "IP", new Font("Segoe UI", 13F, FontStyle.Bold), r, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    public class StatusDot : Control
    {
        private Color _color = Theme.Faint;
        public Color DotColor { get { return _color; } set { _color = value; Invalidate(); } }
        public StatusDot()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Width = 14;
            Height = 14;
            BackColor = Theme.Bg;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(_color)) e.Graphics.FillEllipse(b, 2, 2, Width - 4, Height - 4);
        }
    }

    public class MainForm : Form
    {
        private ComboBox cbAdapter;
        private FlatButton btnRefresh;
        private Pill pillState;
        private Label lblAdapter, lblMac, lblIp, lblGw, lblDns, lblDhcpSrv, lblDesc;
        private Label lblKeyAdapter, lblKeyMac, lblKeyIp, lblKeyGw, lblKeyDns, lblKeyDhcpSrv, lblKeyDesc;
        private InputBox input;
        private FlatButton btnApply;
        private FlatButton btnDhcp;
        private Label lblInputHint;
        private RichTextBox log;
        private FlatButton btnClearLog;
        private StatusDot dot;
        private Label lblStatus;
        private Label lblFooterHint;
        private Label lblHeadingIp;
        private Label lblLogEmpty;

        private const int MarginX = 28;
        private const int CardWidth = 824;
        private bool _busy;
        private bool _scaled;

        public MainForm()
        {
            Text = "DHCP 改静态 IPv4";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(880, 636);
            MinimumSize = new Size(880, 636);
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Bg;
            Font = Theme.Body();
            DoubleBuffered = true;

            BuildHeader();
            BuildNetworkCard();
            BuildActionCard();
            BuildLogCard();
            BuildFooter();

            Load += new EventHandler(FormLoad);
        }

        private void BuildHeader()
        {
            Logo logo = new Logo();
            logo.Location = new Point(MarginX, 18);
            Controls.Add(logo);

            Label title = new Label();
            title.Text = "DHCP 改静态 IPv4";
            title.Font = Theme.Title();
            title.ForeColor = Theme.Text;
            title.AutoSize = true;
            title.BackColor = Theme.Bg;
            title.Location = new Point(MarginX + 52, 20);
            Controls.Add(title);

            Label subtitle = new Label();
            subtitle.Text = "复用当前网络的掩码、网关与 DNS，只修改 IP 地址";
            subtitle.Font = Theme.Body();
            subtitle.ForeColor = Theme.Muted;
            subtitle.AutoSize = true;
            subtitle.BackColor = Theme.Bg;
            subtitle.Location = new Point(MarginX + 53, 58);
            Controls.Add(subtitle);

            Label lblKey = new Label();
            lblKey.Text = "网卡";
            lblKey.Font = Theme.Small();
            lblKey.ForeColor = Theme.Faint;
            lblKey.AutoSize = true;
            lblKey.BackColor = Theme.Bg;
            lblKey.Location = new Point(600, 14);
            Controls.Add(lblKey);

            cbAdapter = new ComboBox();
            cbAdapter.DropDownStyle = ComboBoxStyle.DropDownList;
            cbAdapter.FlatStyle = FlatStyle.Flat;
            cbAdapter.Font = Theme.Body();
            cbAdapter.Location = new Point(600, 34);
            cbAdapter.Size = new Size(196, 28);
            cbAdapter.SelectedIndexChanged += new EventHandler(AdapterChanged);
            Controls.Add(cbAdapter);

            btnRefresh = new FlatButton();
            btnRefresh.Style = FlatButton.Kind.Ghost;
            btnRefresh.Text = "刷新";
            btnRefresh.Font = Theme.Small();
            btnRefresh.Location = new Point(804, 34);
            btnRefresh.Size = new Size(48, 28);
            btnRefresh.Click += new EventHandler(RefreshClick);
            Controls.Add(btnRefresh);
        }

        private RoundedPanel NewCard(int y, int h, string heading)
        {
            RoundedPanel card = new RoundedPanel();
            card.Location = new Point(MarginX, y);
            card.Size = new Size(CardWidth, h);
            card.BackColor = Theme.Bg;
            Controls.Add(card);

            Label hd = new Label();
            hd.Text = heading;
            hd.Font = Theme.Heading();
            hd.ForeColor = Theme.Text;
            hd.AutoSize = true;
            hd.BackColor = Theme.Card;
            hd.Location = new Point(20, 16);
            card.Controls.Add(hd);
            return card;
        }

        private Label KeyLabel(Control parent, string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Theme.Small();
            l.ForeColor = Theme.Faint;
            l.AutoSize = false;
            l.UseMnemonic = false;
            l.BackColor = Theme.Card;
            l.Location = new Point(x, y);
            l.Size = new Size(w, 18);
            parent.Controls.Add(l);
            return l;
        }

        private Label ValueLabel(Control parent, int x, int y, int w)
        {
            Label l = new Label();
            l.Font = Theme.Value();
            l.ForeColor = Theme.Text;
            l.AutoSize = false;
            l.AutoEllipsis = true;
            l.UseMnemonic = false;
            l.BackColor = Theme.Card;
            l.Location = new Point(x, y);
            l.Size = new Size(w, 20);
            parent.Controls.Add(l);
            return l;
        }

        private void BuildNetworkCard()
        {
            RoundedPanel card = NewCard(88, 158, "当前网络");
            pillState = new Pill();
            pillState.Location = new Point(card.Width - 92, 11);
            pillState.Size = new Size(72, 24);
            card.Controls.Add(pillState);

            int row1 = 42, row2 = 68, row3 = 94, row4 = 120;
            lblKeyAdapter = KeyLabel(card, "网卡", 20, row1, 64);
            lblAdapter = ValueLabel(card, 92, row1 - 2, 300);
            lblKeyMac = KeyLabel(card, "MAC", 430, row1, 64);
            lblMac = ValueLabel(card, 502, row1 - 2, 300);

            lblKeyIp = KeyLabel(card, "IP 地址", 20, row2, 64);
            lblIp = ValueLabel(card, 92, row2 - 2, 310);
            lblKeyGw = KeyLabel(card, "网关", 430, row2, 64);
            lblGw = ValueLabel(card, 502, row2 - 2, 300);

            lblKeyDns = KeyLabel(card, "DNS", 20, row3, 64);
            lblDns = ValueLabel(card, 92, row3 - 2, 320);
            lblKeyDhcpSrv = KeyLabel(card, "DHCP 服务器", 430, row3, 90);
            lblDhcpSrv = ValueLabel(card, 528, row3 - 2, 274);

            lblKeyDesc = KeyLabel(card, "网卡描述", 20, row4, 64);
            lblDesc = ValueLabel(card, 92, row4 - 2, 700);

        }

        private void BuildActionCard()
        {
            RoundedPanel card = NewCard(260, 170, "设置静态 IP");

            lblHeadingIp = new Label();
            lblHeadingIp.Text = "目标 IP 地址";
            lblHeadingIp.Font = Theme.Small();
            lblHeadingIp.ForeColor = Theme.Muted;
            lblHeadingIp.AutoSize = true;
            lblHeadingIp.BackColor = Theme.Card;
            lblHeadingIp.Location = new Point(20, 40);
            card.Controls.Add(lblHeadingIp);

            input = new InputBox();
            input.Location = new Point(20, 60);
            input.Size = new Size(320, 44);
            input.Placeholder = "例如 192.168.0.241";
            card.Controls.Add(input);

            lblInputHint = new Label();
            lblInputHint.Text = "须与网关同网段，且不要落在路由器 DHCP 池内";
            lblInputHint.Font = Theme.Small();
            lblInputHint.ForeColor = Theme.Faint;
            lblInputHint.AutoSize = true;
            lblInputHint.BackColor = Theme.Card;
            lblInputHint.Location = new Point(356, 74);
            card.Controls.Add(lblInputHint);

            btnApply = new FlatButton();
            btnApply.Style = FlatButton.Kind.Primary;
            btnApply.Text = "应用为固定 IP";
            btnApply.Location = new Point(20, 110);
            btnApply.Size = new Size(180, 44);
            btnApply.Click += new EventHandler(ApplyClick);
            card.Controls.Add(btnApply);

            btnDhcp = new FlatButton();
            btnDhcp.Style = FlatButton.Kind.Secondary;
            btnDhcp.Text = "恢复为 DHCP";
            btnDhcp.Location = new Point(212, 110);
            btnDhcp.Size = new Size(168, 44);
            btnDhcp.Click += new EventHandler(DhcpClick);
            card.Controls.Add(btnDhcp);
        }

        private void BuildLogCard()
        {
            RoundedPanel card = NewCard(444, 140, "运行日志");
            btnClearLog = new FlatButton();
            btnClearLog.Style = FlatButton.Kind.Ghost;
            btnClearLog.Text = "清空";
            btnClearLog.Font = Theme.Small();
            btnClearLog.Location = new Point(card.Width - 76, 10);
            btnClearLog.Size = new Size(56, 28);
            btnClearLog.Click += new EventHandler(ClearLogClick);
            card.Controls.Add(btnClearLog);

            log = new RichTextBox();
            log.Location = new Point(18, 36);
            log.Size = new Size(card.Width - 36, 88);
            log.BorderStyle = BorderStyle.None;
            log.BackColor = Color.FromArgb(250, 251, 253);
            log.ForeColor = Color.FromArgb(71, 85, 105);
            log.Font = Theme.Mono();
            log.ReadOnly = true;
            log.DetectUrls = false;
            log.HideSelection = false;
            log.ScrollBars = RichTextBoxScrollBars.Vertical;
            card.Controls.Add(log);

            lblLogEmpty = new Label();
            lblLogEmpty.Text = "暂无日志 — 操作后会在这里显示完整过程";
            lblLogEmpty.Font = Theme.Small();
            lblLogEmpty.ForeColor = Theme.Faint;
            lblLogEmpty.BackColor = Color.FromArgb(250, 251, 253);
            lblLogEmpty.AutoSize = true;
            lblLogEmpty.Location = new Point(22, 40);
            card.Controls.Add(lblLogEmpty);
            lblLogEmpty.BringToFront();
        }

        private void BuildFooter()
        {
            dot = new StatusDot();
            dot.Location = new Point(MarginX + 4, 600);
            dot.DotColor = Theme.Faint;
            Controls.Add(dot);

            lblStatus = new Label();
            lblStatus.Text = "就绪";
            lblStatus.Font = Theme.Small();
            lblStatus.ForeColor = Theme.Muted;
            lblStatus.BackColor = Theme.Bg;
            lblStatus.AutoSize = true;
            lblStatus.Location = new Point(MarginX + 24, 598);
            Controls.Add(lblStatus);

            lblFooterHint = new Label();
            lblFooterHint.Text = "只修改 IPv4 与 IPv4 DNS，不改 IPv6";
            lblFooterHint.Font = Theme.Small();
            lblFooterHint.ForeColor = Theme.Faint;
            lblFooterHint.BackColor = Theme.Bg;
            lblFooterHint.AutoSize = true;
            lblFooterHint.Location = new Point(880 - MarginX - 300, 598);
            lblFooterHint.Size = new Size(300, 18);
            lblFooterHint.TextAlign = ContentAlignment.MiddleRight;
            Controls.Add(lblFooterHint);
        }

        private void FormLoad(object sender, EventArgs e)
        {
            ScaleUI();
            LoadAdapters();
            AppendLog("提示：会自动复用当前 DHCP 的掩码、网关与 DNS；恢复 DHCP 将重新申请租约。");
            ShowStatus("就绪", Theme.Faint);
        }

        private void ScaleUI()
        {
            if (_scaled) return;
            _scaled = true;
            float s = DeviceDpi / 96f;
            if (s <= 1.01f) return;
            ScaleTree(Controls, s);
            ClientSize = new Size((int)Math.Round(880 * s), (int)Math.Round(636 * s));
            MinimumSize = new Size((int)Math.Round(880 * s), (int)Math.Round(636 * s));
        }

        private static void ScaleTree(Control.ControlCollection children, float s)
        {
            foreach (Control c in children)
            {
                c.Bounds = new Rectangle(
                    (int)Math.Round(c.Left * s),
                    (int)Math.Round(c.Top * s),
                    (int)Math.Round(c.Width * s),
                    (int)Math.Round(c.Height * s));
                RoundedPanel rp = c as RoundedPanel;
                if (rp != null) rp.Radius = (int)Math.Round(rp.Radius * s);
                if (c.Controls.Count > 0) ScaleTree(c.Controls, s);
            }
        }

        private void LoadAdapters()
        {
            string keep = cbAdapter.SelectedItem == null ? "" : cbAdapter.SelectedItem.ToString();
            cbAdapter.Items.Clear();
            foreach (NetworkInterface ni in NetUtil.ActiveAdapters()) cbAdapter.Items.Add(ni.Name);
            if (cbAdapter.Items.Count == 0)
            {
                ShowStatus("没有找到已连接的网络适配器", Theme.Danger);
                return;
            }
            int idx = 0;
            for (int i = 0; i < cbAdapter.Items.Count; i++)
            {
                if (cbAdapter.Items[i].ToString() == keep) { idx = i; break; }
            }
            cbAdapter.SelectedIndex = idx;
        }

        private string CurrentAdapter()
        {
            if (cbAdapter.SelectedItem == null) return "";
            return cbAdapter.SelectedItem.ToString();
        }

        private void RefreshInfo()
        {
            string name = CurrentAdapter();
            if (name == "")
            {
                ShowStatus("没有可用网卡", Theme.Danger);
                return;
            }
            AdapterInfo a = NetUtil.Read(name);
            lblAdapter.Text = a.Name;
            lblMac.Text = a.Mac;
            lblIp.Text = a.HasIPv4 ? (a.Ip + " / " + a.Mask) : "（无 IPv4 地址）";
            lblGw.Text = a.Gateway == "" ? "（无）" : a.Gateway;
            lblDns.Text = a.Dns.Count == 0 ? "（无）" : string.Join("，", a.Dns.ToArray());
            lblDhcpSrv.Text = a.DhcpServer == "" ? "（无）" : a.DhcpServer;
            lblDesc.Text = a.Description;
            if (a.Dhcp)
            {
                pillState.Text = "自动获取";
                pillState.PillBack = Theme.SuccessBg;
                pillState.PillFore = Theme.Success;
            }
            else
            {
                pillState.Text = "静态";
                pillState.PillBack = Color.FromArgb(219, 234, 254);
                pillState.PillFore = Color.FromArgb(29, 78, 216);
            }
            if (!input.Box.Focused)
            {
                input.Text = a.HasIPv4 ? " " + a.Ip : "";
            }
        }

        private void AdapterChanged(object sender, EventArgs e)
        {
            RefreshInfo();
        }

        private void RefreshClick(object sender, EventArgs e)
        {
            LoadAdapters();
            RefreshInfo();
            ShowStatus("已刷新", Theme.Muted);
        }

        private void ApplyClick(object sender, EventArgs e)
        {
            if (_busy) return;
            string name = CurrentAdapter();
            if (name == "") { ShowStatus("请先选择网卡", Theme.Danger); return; }
            string ip = input.Text.Trim();
            if (ip == "") { ShowStatus("请输入目标 IPv4 地址", Theme.Danger); input.Box.Focus(); return; }

            SetBusy(true);
            ShowStatus("正在应用静态 IP…", Theme.Muted);
            AppendLog("──── 应用为固定 IP ────");
            try
            {
                bool verified;
                string text = NetUtil.SetStatic(name, ip, out verified);
                AppendLog(text);
                RefreshInfo();
                if (verified) ShowStatus("已应用为 " + ip, Theme.Success);
                else ShowStatus("命令已执行，请刷新确认", Theme.Warn);
            }
            catch (Exception ex)
            {
                AppendLog("出错：" + ex.Message);
                ShowStatus("操作失败：" + ex.Message, Theme.Danger);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void DhcpClick(object sender, EventArgs e)
        {
            if (_busy) return;
            string name = CurrentAdapter();
            if (name == "") { ShowStatus("请先选择网卡", Theme.Danger); return; }

            SetBusy(true);
            ShowStatus("正在恢复 DHCP…", Theme.Muted);
            AppendLog("──── 恢复为 DHCP ────");
            try
            {
                bool verified;
                string text = NetUtil.SetDhcp(name, out verified);
                AppendLog(text);
                RefreshInfo();
                if (verified) ShowStatus("已恢复为 DHCP", Theme.Success);
                else ShowStatus("已执行恢复，请检查路由器 DHCP", Theme.Warn);
            }
            catch (Exception ex)
            {
                AppendLog("出错：" + ex.Message);
                ShowStatus("操作失败：" + ex.Message, Theme.Danger);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void ClearLogClick(object sender, EventArgs e)
        {
            log.Clear();
            if (lblLogEmpty != null) lblLogEmpty.Visible = true;
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            btnApply.Enabled = !busy;
            btnApply.Text = busy ? "正在处理…" : "应用为固定 IP";
            btnDhcp.Enabled = !busy;
            btnRefresh.Enabled = !busy;
            cbAdapter.Enabled = !busy;
            input.Box.Enabled = !busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }

        private void ShowStatus(string text, Color color)
        {
            lblStatus.Text = text;
            lblStatus.ForeColor = color == Theme.Faint ? Theme.Muted : color;
            dot.DotColor = color;
            lblStatus.Refresh();
        }

        private void AppendLog(string text)
        {
            if (lblLogEmpty != null) lblLogEmpty.Visible = false;
            log.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text + Environment.NewLine);
            log.ScrollToCaret();
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            if (args != null && args.Length > 1 && args[0] == "--shot")
            {
                RunShot(args[1]);
                return;
            }
            if (args != null && args.Length > 0 && args[0].StartsWith("--"))
            {
                Environment.Exit(RunCli(args));
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        private static void RunShot(string path)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            MainForm f = new MainForm();
            f.Show();
            Application.DoEvents();
            System.Threading.Thread.Sleep(700);
            Application.DoEvents();
            try
            {
                using (Bitmap bmp = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(path + ".err.txt", ex.ToString()); } catch { }
            }
            f.Close();
        }

        private static void Out(string path, string text)
        {
            if (path != null && path != "")
            {
                try { File.WriteAllText(path, text, new UTF8Encoding(true)); } catch { }
            }
        }

        private static int RunCli(string[] args)
        {
            string cmd = args[0].ToLowerInvariant();
            string adapter = "";
            string logPath = "";
            string ip = "";
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--adapter" && i + 1 < args.Length) { adapter = args[++i]; }
                else if (a == "--log" && i + 1 < args.Length) { logPath = args[++i]; }
                else if (!a.StartsWith("--") && ip == "") { ip = a; }
            }
            AdapterInfo info = NetUtil.Pick(adapter);
            if (info.Name == "")
            {
                Out(logPath, "没有找到可用的网络适配器。");
                return 2;
            }
            if (cmd == "--info")
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("网卡        : " + info.Name);
                sb.AppendLine("描述        : " + info.Description);
                sb.AppendLine("MAC 地址    : " + info.Mac);
                sb.AppendLine("DHCP        : " + (info.Dhcp ? "已启用（自动获取）" : "未启用（静态/手动）"));
                sb.AppendLine("IPv4 地址   : " + (info.Ip == "" ? "（无）" : info.Ip + "  /  " + info.Mask));
                sb.AppendLine("默认网关    : " + (info.Gateway == "" ? "（无）" : info.Gateway));
                sb.AppendLine("DNS 服务器  : " + (info.Dns.Count == 0 ? "（无）" : string.Join("，", info.Dns.ToArray())));
                sb.AppendLine("DHCP 服务器 : " + (info.DhcpServer == "" ? "（无）" : info.DhcpServer));
                Out(logPath != "" ? logPath : ip, sb.ToString());
                return 0;
            }
            if (cmd == "--set")
            {
                if (ip == "") { Out(logPath, "缺少目标 IP：--set 192.168.0.241"); return 2; }
                bool verified;
                string text = NetUtil.SetStatic(info.Name, ip, out verified);
                Out(logPath, text);
                return verified ? 0 : 1;
            }
            if (cmd == "--dhcp")
            {
                bool verified;
                string text = NetUtil.SetDhcp(info.Name, out verified);
                Out(logPath, text);
                return verified ? 0 : 1;
            }
            Out(logPath, "未知参数：" + string.Join(" ", args));
            return 2;
        }
    }
}
