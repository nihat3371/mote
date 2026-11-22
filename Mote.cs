// Mote - a tiny, fast Markdown reader for Windows (single file, WinForms + WebBrowser).
// Build: build.ps1  (uses the C# compiler that ships with Windows, no SDK needed)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Mote")]
[assembly: AssemblyProduct("Mote")]
[assembly: AssemblyDescription("A tiny, fast Markdown reader")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

class Mote : Form
{
    // UI language: English by default, Turkish when Windows display language is Turkish
    static readonly bool TR = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr";
    internal static string T(string en, string tr) { return TR ? tr : en; }

    WebBrowser web;
    string path, lastBody, lastBase;
    string tmp = Path.Combine(Path.GetTempPath(), "mote_" + Process.GetCurrentProcess().Id + ".html");
    FileSystemWatcher watcher;
    Timer reloadTimer;
    int pendingScroll = -1;
    bool dark, toc;

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
#if SHOT
        // Test-only build (/define:SHOT): render a file off-screen into a PNG, never shown on the user's screen.
        if (args.Length >= 3 && args[0] == "--shot") { Shot(args); return; }
#endif
        Application.Run(new Mote(args.Length > 0 ? args[0] : null));
    }

#if SHOT
    static int shotTheme = -1;
    [ComImport, Guid("0000010d-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IViewObject { [PreserveSig] int Draw(uint a, int l, IntPtr p, IntPtr t, IntPtr h1, IntPtr hdc, ref Rectangle r, IntPtr w, IntPtr c, int ca); }
    protected override bool ShowWithoutActivation { get { return true; } }
    static void Shot(string[] a)
    {
        shotTheme = a.Length > 3 && a[3] == "dark" ? 1 : 0;
        var f = new Mote(a[1]);
        f.StartPosition = FormStartPosition.Manual; f.ShowInTaskbar = false;
        f.Bounds = new Rectangle(SystemInformation.VirtualScreen.Right + 200, 0, a.Length > 4 ? int.Parse(a[4]) : 932, 960);
        var t = new Timer { Interval = 2500 };
        t.Tick += delegate
        {
            t.Stop();
            var sz = f.web.ClientSize;
            using (var b = new Bitmap(sz.Width, sz.Height))
            {
                using (var g = Graphics.FromImage(b))
                {
                    IntPtr hdc = g.GetHdc();
                    var r = new Rectangle(0, 0, sz.Width, sz.Height);
                    ((IViewObject)f.web.Document.DomDocument).Draw(1, -1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, hdc, ref r, IntPtr.Zero, IntPtr.Zero, 0);
                    g.ReleaseHdc(hdc);
                }
                b.Save(a[2], System.Drawing.Imaging.ImageFormat.Png);
            }
            Environment.Exit(0); // skip SaveSettings
        };
        f.Shown += delegate { t.Start(); };
        Application.Run(f);
    }
#endif

    Mote(string file)
    {
        Text = "Mote";
        Width = 950; Height = 1000;
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        LoadSettings();
        #if SHOT
        if (shotTheme >= 0) dark = shotTheme == 1;
#endif
        BackColor = dark ? Color.FromArgb(31, 30, 29) : Color.FromArgb(240, 238, 230);

        web = new WebBrowser();
        web.Dock = DockStyle.Fill;
        web.ScriptErrorsSuppressed = true;
        web.AllowWebBrowserDrop = true;   // dropped files arrive via Navigating
        web.Navigating += OnNav;
        
        web.DocumentCompleted += delegate
        {
            if (pendingScroll > 0 && web.Document != null)
            {
                var html = web.Document.GetElementsByTagName("HTML");
                if (html.Count > 0) html[0].ScrollTop = pendingScroll;
            }
            pendingScroll = -1;
        };
        Controls.Add(web);

        reloadTimer = new Timer();
        reloadTimer.Interval = 250;
        reloadTimer.Tick += delegate { reloadTimer.Stop(); Render(true); };

        FormClosing += delegate { SaveSettings(); };
        FormClosed += delegate { try { File.Delete(tmp); } catch { } };

        if (file != null && File.Exists(file)) LoadFile(Path.GetFullPath(file));
        else ShowHtml(Md.ToHtml(T(
            "# Mote\n\nDrop a `.md` file onto this window or press **Ctrl+O**.\n\n" +
            "| Shortcut | Action |\n|---|---|\n| `Ctrl+O` | Open file |\n| `Ctrl+D` | Light / dark theme |\n| `Ctrl+T` | Table of contents |\n" +
            "| `F5` | Reload (also automatic when the file changes) |\n| `Ctrl+F` | Find |\n| `Ctrl+wheel` | Zoom |\n| `Ctrl+P` | Print |\n",
            "# Mote\n\nBir `.md` dosyas\u0131n\u0131 pencereye **s\u00fcr\u00fckle-b\u0131rak** veya **Ctrl+O** ile a\u00e7.\n\n" +
            "| K\u0131sayol | \u0130\u015flev |\n|---|---|\n| `Ctrl+O` | Dosya a\u00e7 |\n| `Ctrl+D` | A\u00e7\u0131k / koyu tema |\n| `Ctrl+T` | \u0130\u00e7indekiler |\n" +
            "| `F5` | Yeniden y\u00fckle (de\u011fi\u015fince zaten otomatik) |\n| `Ctrl+F` | Ara |\n| `Ctrl+tekerlek` | Yak\u0131nla\u015ft\u0131r |\n| `Ctrl+P` | Yazd\u0131r |\n")), null);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTitleBar();
    }

    void ApplyTitleBar()
    {
        int v = dark ? 1 : 0;
        try { if (DwmSetWindowAttribute(Handle, 20, ref v, 4) != 0) DwmSetWindowAttribute(Handle, 19, ref v, 4); } catch { }
    }

    // ---- settings (HKCU\Software\Mote) ----
    void LoadSettings()
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Mote"))
            {
                object d = k.GetValue("Dark");
                if (d != null) dark = (int)d == 1;
                else   // first run: follow the Windows app theme
                    using (var w = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                        dark = w != null && w.GetValue("AppsUseLightTheme") is int && (int)w.GetValue("AppsUseLightTheme") == 0;
                toc = (k.GetValue("Toc") as int?) == 1;
                string b = k.GetValue("Bounds") as string;
                if (b != null)
                {
                    string[] p = b.Split(',');
                    var r = new Rectangle(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
                    if (Screen.FromRectangle(r).WorkingArea.IntersectsWith(r))
                    {
                        StartPosition = FormStartPosition.Manual;
                        Bounds = r;
                        if (p[4] == "1") WindowState = FormWindowState.Maximized;
                    }
                }
            }
        }
        catch { }
    }

    void SaveSettings()
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Mote"))
            {
                k.SetValue("Dark", dark ? 1 : 0);
                k.SetValue("Toc", toc ? 1 : 0);
                Rectangle r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                if (WindowState != FormWindowState.Minimized)
                    k.SetValue("Bounds", r.X + "," + r.Y + "," + r.Width + "," + r.Height + "," + (WindowState == FormWindowState.Maximized ? 1 : 0));
            }
        }
        catch { }
    }

    void LoadFile(string p)
    {
        path = p;
        Text = Path.GetFileName(p) + " - Mote";
        if (watcher != null) watcher.Dispose();
        watcher = new FileSystemWatcher(Path.GetDirectoryName(p), Path.GetFileName(p));
        watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;
        watcher.SynchronizingObject = this;
        FileSystemEventHandler h = delegate { reloadTimer.Stop(); reloadTimer.Start(); };
        watcher.Changed += h; watcher.Created += h;
        watcher.EnableRaisingEvents = true;
        Render(false);
    }

    void SaveScroll()
    {
        if (web.Document == null) return;
        var html = web.Document.GetElementsByTagName("HTML");
        if (html.Count > 0) pendingScroll = html[0].ScrollTop;
    }

    void Render(bool keepScroll)
    {
        if (path == null) return;
        string src;
        try { src = File.ReadAllText(path); }
        catch { reloadTimer.Start(); return; } // file locked by the writer: retry
        if (keepScroll) SaveScroll();
        ShowHtml(Md.ToHtml(src), Path.GetDirectoryName(path));
    }

    void Redraw()   // after theme / contents toggle
    {
        SaveScroll();
        ShowHtml(lastBody, lastBase);
    }

    const string SvgHead = "<svg width=\"16\" height=\"16\" viewBox=\"0 0 16 16\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.5\" stroke-linecap=\"round\" stroke-linejoin=\"round\">";
    const string IconMenu = SvgHead + "<path d=\"M2.5 4h11M2.5 8h11M2.5 12h11\"/></svg>";
    const string IconSun = SvgHead + "<circle cx=\"8\" cy=\"8\" r=\"3\"/><path d=\"M8 1.5v1.5M8 13v1.5M1.5 8H3M13 8h1.5M3.4 3.4l1.1 1.1M11.5 11.5l1.1 1.1M3.4 12.6l1.1-1.1M11.5 4.5l1.1-1.1\"/></svg>";
    const string IconMoon = SvgHead + "<path d=\"M13.5 9.6A5.6 5.6 0 0 1 6.4 2.5a5.6 5.6 0 1 0 7.1 7.1z\"/></svg>";

    void ShowHtml(string body, string baseDir)
    {
        lastBody = body; lastBase = baseDir;
        string baseTag = baseDir == null ? "" : "<base href=\"" + new Uri(baseDir.TrimEnd('\\') + "\\").AbsoluteUri + "\">";
        var heads = Regex.Matches(body, @"<h([1-3]) id=""([^""]*)"">(.*?)</h\1>");
        string bar = "<div id=\"bar\">" +
            (heads.Count > 1 ? "<a href=\"mote:toc\" title=\"" + T("Contents", "\u0130\u00e7indekiler") + " (Ctrl+T)\">" + IconMenu + "</a>" : "") +
            "<a href=\"mote:theme\" title=\"" + T("Theme", "Tema") + " (Ctrl+D)\">" + (dark ? IconSun : IconMoon) + "</a></div>";
        var nav = new StringBuilder();
        if (toc && heads.Count > 1)
        {
            nav.Append("<nav id=\"toc\"><b>" + T("CONTENTS", "\u0130\u00c7\u0130NDEK\u0130LER") + "</b>");
            foreach (Match h in heads)
                nav.Append("<a class=\"l" + h.Groups[1].Value + "\" href=\"#" + h.Groups[2].Value + "\">" +
                    Regex.Replace(h.Groups[3].Value, "<[^>]+>", "") + "</a>");
            nav.Append("</nav>");
        }
        string page = "<!DOCTYPE html><html" + (dark ? " class=\"dark\"" : "") + "><head><meta charset=\"utf-8\">" +
            "<meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\">" + baseTag + "<style>" + Css + "</style>" + Js + "</head><body>" +
            bar + nav + "<article>" + body + "</article></body></html>";
        File.WriteAllText(tmp, page, new UTF8Encoding(true));
        web.Navigate(tmp);
    }

    void ToggleTheme()
    {
        dark = !dark;
        #if SHOT
        if (shotTheme >= 0) dark = shotTheme == 1;
#endif
        BackColor = dark ? Color.FromArgb(31, 30, 29) : Color.FromArgb(240, 238, 230);
        ApplyTitleBar();
        Width++; Width--;   // forces Win10 to repaint the title bar
        Redraw();
    }

    void OpenDialog()
    {
        using (var d = new OpenFileDialog())
        {
            d.Filter = "Markdown|*.md;*.markdown;*.mdown;*.txt|" + T("All files", "T\u00fcm dosyalar") + "|*.*";
            if (d.ShowDialog(this) == DialogResult.OK) LoadFile(d.FileName);
        }
    }

    // Shortcuts are caught inside the page and turned into mote: links (the IE control swallows keys)
    const string Js = "<script>document.onkeydown=function(e){e=e||window.event;var k=e.keyCode,c=e.ctrlKey,a=null;" +
        "if(c&&k==68)a='theme';else if(c&&k==84)a='toc';else if(c&&k==79)a='open';else if(k==116)a='reload';" +
        "if(a){try{e.keyCode=0}catch(x){}if(e.preventDefault)e.preventDefault();e.returnValue=false;location.href='mote:'+a;return false;}};" +
        "document.onclick=function(e){e=e||window.event;var t=e.target||e.srcElement;while(t&&t.tagName!='A')t=t.parentNode;if(!t)return;" +
        "var h=t.getAttribute('href',2);if(h&&h.charAt(0)=='#'){var el=document.getElementById(decodeURIComponent(h.substr(1)));" +
        "if(el)el.scrollIntoView();if(e.preventDefault)e.preventDefault();e.returnValue=false;return false;}};</script>";

    static bool IsMd(string p)
    {
        string x = Path.GetExtension(p).ToLowerInvariant();
        return x == ".md" || x == ".markdown" || x == ".mdown" || x == ".txt";
    }

    void OnNav(object s, WebBrowserNavigatingEventArgs e)
    {
        Uri u = e.Url;
        if (u.ToString() == "about:blank") return;
        if (u.IsFile && string.Equals(u.LocalPath, tmp, StringComparison.OrdinalIgnoreCase)) return;
        e.Cancel = true;
        try
        {
            if (u.Scheme == "mote")
            {
                string cmd = u.AbsolutePath;
                if (cmd == "theme") ToggleTheme();
                else if (cmd == "open") BeginInvoke((MethodInvoker)OpenDialog);
                else if (cmd == "reload") Render(true);
                else if (cmd == "toc") { toc = !toc; Redraw(); }
                else if (cmd.StartsWith("copy/"))
                {
                    int n = int.Parse(cmd.Substring(5));
                    Clipboard.SetText(Md.CodeBlocks[n].Length > 0 ? Md.CodeBlocks[n] : " ");
                    var el = web.Document.GetElementById("cp" + n);
                    if (el != null) el.InnerText = T("Copied", "Kopyaland\u0131") + " \u2713";
                }
            }
            else if (u.IsFile)
            {
                string p = u.LocalPath;
                if (u.Fragment.Length > 1 && (Directory.Exists(p) || string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
                {
                    var el = web.Document.GetElementById(Uri.UnescapeDataString(u.Fragment.Substring(1)));
                    if (el != null) el.ScrollIntoView(true);
                }
                else if (File.Exists(p) && IsMd(p)) LoadFile(p);
                else if (File.Exists(p) || Directory.Exists(p)) Process.Start(p);
            }
            else Process.Start(u.ToString()); // http, mailto -> default browser
        }
        catch { }
    }

    const string Css = @"
html{background:#F0EEE6}
body{margin:0;padding:0 20px;color:#3D3D3A;font:17px/1.7 Georgia,'Times New Roman',serif}
article{max-width:902px;margin:64px auto 40px;box-sizing:border-box;word-wrap:break-word;padding:44px 60px 56px;background:#FAF9F5;border:1px solid #E8E6DC;border-radius:14px;box-shadow:0 1px 8px rgba(40,30,20,.05)}
article>:first-child{margin-top:0}
@media (max-width:700px){article{padding:28px 26px 36px}}
h1,h2,h3,h4,h5,h6{margin:1.6em 0 .6em;line-height:1.3;color:#141413;font-family:'Segoe UI',Arial,sans-serif;font-weight:600}
h1{font-size:1.85em;font-weight:700;letter-spacing:-.3px}
h2{font-size:1.45em;padding-bottom:.3em;border-bottom:1px solid #E8E6DC}
h3{font-size:1.18em}
p,ul,ol,table,pre,blockquote{margin:0 0 1.1em}
li{margin:.25em 0}
a{color:#C6613F;text-decoration:none;border-bottom:1px solid #EBCBBB} a:hover{border-bottom-color:#C6613F}
strong{color:#141413}
code{word-wrap:break-word;font:84% Consolas,monospace;background:#F0EEE6;color:#141413;padding:.15em .4em;border-radius:5px}
pre{background:#F5F4EE;color:#141413;border:1px solid #E8E6DC;padding:16px 20px;border-radius:10px;overflow:auto;line-height:1.55}
pre code{background:none;color:inherit;padding:0;font-size:86%;word-wrap:normal}
pre .c{color:#8A877D;font-style:italic} pre .s{color:#5B7F3A} pre .n{color:#B2552F} pre .k{color:#7553A8}
.code{position:relative}
.code .copy{display:none;position:absolute;top:8px;right:10px;font:12px 'Segoe UI',Arial;color:#73726C;background:#FAF9F5;border:1px solid #E8E6DC;border-radius:6px;padding:2px 10px}
.code:hover .copy{display:block} .code .copy:hover{color:#C6613F}
blockquote{padding:.6em 1.2em;color:#5E5D59;background:#F5F4EE;border-left:3px solid #D97757;border-radius:0 8px 8px 0}
blockquote>:last-child{margin-bottom:0}
table{border-collapse:collapse;font:.9em/1.5 'Segoe UI',Arial,sans-serif}
th,td{border:1px solid #E8E6DC;padding:8px 14px} th{background:#F0EEE6;font-weight:600;color:#141413;text-align:left} tr:nth-child(even) td{background:#F7F6F1}
img{max-width:100%;border-radius:8px}
hr{border:0;height:1px;background:#E8E6DC;margin:2.2em 0}
li.task{list-style:none} li.task input{margin:0 .5em 0 -1.4em}
::selection{background:#F3D9CC}
#bar{position:fixed;top:14px;right:18px;z-index:9}
#bar a{float:left;box-sizing:border-box;width:34px;height:34px;margin-left:6px;border:1px solid #E8E6DC;border-radius:10px;background:#FAF9F5;color:#73726C;box-shadow:0 1px 4px rgba(40,30,20,.06)}
#bar svg{display:block;margin:8px}
#bar a:hover{color:#C6613F}
#toc{position:fixed;top:58px;right:18px;width:250px;max-height:75%;overflow:auto;background:#FAF9F5;border:1px solid #E8E6DC;border-radius:12px;box-shadow:0 4px 18px rgba(40,30,20,.10);padding:12px 8px;font:14px/1.4 'Segoe UI',Arial,sans-serif;z-index:9}
#toc b{display:block;padding:2px 10px 8px;font-size:12px;letter-spacing:1px;color:#8A877D}
#toc a{display:block;padding:5px 10px;border:0;border-radius:6px;color:#3D3D3A}
#toc a:hover{background:#F0EEE6;color:#C6613F}
#toc .l2{padding-left:22px} #toc .l3{padding-left:34px;font-size:13px;color:#73726C}
html{scrollbar-face-color:#D9D7CE;scrollbar-track-color:#F0EEE6;scrollbar-arrow-color:#8A877D;scrollbar-shadow-color:#D9D7CE;scrollbar-highlight-color:#D9D7CE;scrollbar-3dlight-color:#F0EEE6;scrollbar-darkshadow-color:#F0EEE6}
html.dark{background:#1F1E1D;scrollbar-face-color:#3D3D3A;scrollbar-track-color:#1F1E1D;scrollbar-arrow-color:#9C9A92;scrollbar-shadow-color:#3D3D3A;scrollbar-highlight-color:#3D3D3A;scrollbar-3dlight-color:#1F1E1D;scrollbar-darkshadow-color:#1F1E1D}
.dark body{color:#D9D7CE}
.dark article{background:#262624;border-color:#3D3D3A;box-shadow:0 2px 14px rgba(0,0,0,.35)}
.dark h1,.dark h2,.dark h3,.dark h4,.dark h5,.dark h6,.dark strong{color:#ECEBE4}
.dark h2{border-bottom-color:#3D3D3A}
.dark a{color:#E39A7E;border-bottom-color:#5A3F33} .dark a:hover{border-bottom-color:#E39A7E}
.dark code{background:#30302E;color:#ECEBE4}
.dark pre{background:#1F1E1D;color:#E4E2DA;border-color:#3D3D3A}
.dark pre code{background:none;color:inherit}
.dark pre .c{color:#8A877D} .dark pre .s{color:#A6C98A} .dark pre .n{color:#E39A7E} .dark pre .k{color:#C3A6E8}
.dark blockquote{background:#30302E;color:#B8B6AD;border-left-color:#D97757}
.dark th,.dark td{border-color:#3D3D3A} .dark th{background:#30302E;color:#ECEBE4} .dark tr:nth-child(even) td{background:#2B2B29}
.dark hr{background:#3D3D3A}
.dark ::selection{background:#5A3A2E}
.dark .code .copy{background:#262624;border-color:#3D3D3A;color:#9C9A92}
.dark #bar a,.dark #toc{background:#30302E;border-color:#3D3D3A;color:#B8B6AD;box-shadow:0 2px 12px rgba(0,0,0,.4)}
.dark #bar a:hover{color:#E39A7E}
.dark #toc a{color:#D9D7CE} .dark #toc a:hover{background:#3D3D3A;color:#E39A7E} .dark #toc .l3{color:#9C9A92}";
}

static class Md
{
    static readonly Regex ListRe = new Regex(@"^( *)([-*+]|\d{1,9}[.)])( +|$)(.*)$");
    static readonly Regex TableSep = new Regex(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$");

    public static List<string> CodeBlocks = new List<string>();
    static Dictionary<string, int> ids = new Dictionary<string, int>();

    public static string ToHtml(string src)
    {
        CodeBlocks.Clear(); ids.Clear();
        var lines = new List<string>(src.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));
        for (int k = 0; k < lines.Count; k++) lines[k] = lines[k].Replace("\t", "    ");
        return Blocks(lines);
    }

    static int Indent(string s) { int n = 0; while (n < s.Length && s[n] == ' ') n++; return n; }

    static string Blocks(List<string> L)
    {
        var sb = new StringBuilder();
        var para = new List<string>();
        Action flush = delegate
        {
            if (para.Count == 0) return;
            sb.Append("<p>").Append(Inline(JoinPara(para))).Append("</p>\n");
            para.Clear();
        };
        int i = 0;
        while (i < L.Count)
        {
            string line = L[i], t = line.Trim();
            Match m;

            if (t.StartsWith("```") || t.StartsWith("~~~"))
            {
                flush();
                string fence = t.Substring(0, 3);
                string lang = t.Substring(3).Trim().Split(' ')[0];
                var code = new StringBuilder();
                for (i++; i < L.Count && !L[i].Trim().StartsWith(fence); i++) code.Append(L[i]).Append('\n');
                i++;
                string raw = code.ToString().TrimEnd('\n');
                int n = CodeBlocks.Count;
                CodeBlocks.Add(raw);
                sb.Append("<div class=\"code\"><a class=\"copy\" id=\"cp" + n + "\" href=\"mote:copy/" + n + "\">" + Mote.T("Copy", "Kopyala") + "</a>")
                  .Append("<pre><code>").Append(Highlight(raw, lang)).Append("</code></pre></div>\n");
                continue;
            }
            if (t.Length == 0) { flush(); i++; continue; }

            if (para.Count > 0 && Regex.IsMatch(line, @"^ {0,3}(=+|-+)\s*$"))
            {   // setext heading
                string txt = JoinPara(para); para.Clear();
                int n = t[0] == '=' ? 1 : 2;
                sb.AppendFormat("<h{0} id=\"{1}\">{2}</h{0}>\n", n, Slug(txt), Inline(txt));
                i++; continue;
            }
            if ((m = Regex.Match(line, @"^ {0,3}(#{1,6})(?:\s+(.*?))?(?:\s+#+)?\s*$")).Success)
            {
                flush();
                string txt = m.Groups[2].Value;
                sb.AppendFormat("<h{0} id=\"{1}\">{2}</h{0}>\n", m.Groups[1].Length, Slug(txt), Inline(txt));
                i++; continue;
            }
            if (Regex.IsMatch(line, @"^ {0,3}([-*_])( *\1){2,} *$"))
            {
                flush(); sb.Append("<hr>\n"); i++; continue;
            }
            if (Regex.IsMatch(line, @"^ {0,3}>"))
            {
                flush();
                var q = new List<string>();
                for (; i < L.Count && Regex.IsMatch(L[i], @"^ {0,3}>"); i++) q.Add(Regex.Replace(L[i], @"^ {0,3}> ?", ""));
                sb.Append("<blockquote>\n").Append(Blocks(q)).Append("</blockquote>\n");
                continue;
            }
            if (line.Contains("|") && i + 1 < L.Count && L[i + 1].Contains("-") && TableSep.IsMatch(L[i + 1]))
            {
                flush();
                i = Table(L, i, sb);
                continue;
            }
            if ((m = ListRe.Match(line)).Success && Indent(line) < 4)
            {
                flush();
                i = List(L, i, sb);
                continue;
            }
            para.Add(line);
            i++;
        }
        flush();
        return sb.ToString();
    }

    static string[] Cells(string row)
    {
        string r = row.Trim();
        if (r.StartsWith("|")) r = r.Substring(1);
        if (r.EndsWith("|")) r = r.Substring(0, r.Length - 1);
        string[] c = r.Split('|');
        for (int k = 0; k < c.Length; k++) c[k] = c[k].Trim();
        return c;
    }

    static int Table(List<string> L, int i, StringBuilder sb)
    {
        string[] head = Cells(L[i]), sep = Cells(L[i + 1]);
        var align = new string[sep.Length];
        for (int k = 0; k < sep.Length; k++)
        {
            bool l = sep[k].StartsWith(":"), r = sep[k].EndsWith(":");
            align[k] = l && r ? "center" : r ? "right" : l ? "left" : null;
        }
        Func<int, string> st = k => k < align.Length && align[k] != null ? " style=\"text-align:" + align[k] + "\"" : "";
        sb.Append("<table><thead><tr>");
        for (int k = 0; k < head.Length; k++) sb.Append("<th" + st(k) + ">" + Inline(head[k]) + "</th>");
        sb.Append("</tr></thead><tbody>\n");
        for (i += 2; i < L.Count && L[i].Trim().Length > 0 && L[i].Contains("|"); i++)
        {
            string[] c = Cells(L[i]);
            sb.Append("<tr>");
            for (int k = 0; k < head.Length; k++) sb.Append("<td" + st(k) + ">" + (k < c.Length ? Inline(c[k]) : "") + "</td>");
            sb.Append("</tr>\n");
        }
        sb.Append("</tbody></table>\n");
        return i;
    }

    static int List(List<string> L, int i, StringBuilder sb)
    {
        Match first = ListRe.Match(L[i]);
        int baseIndent = first.Groups[1].Length;
        bool ordered = char.IsDigit(first.Groups[2].Value[0]);
        string tag = ordered ? "ol" : "ul";
        if (ordered)
        {
            int start = int.Parse(first.Groups[2].Value.TrimEnd('.', ')'));
            sb.Append(start != 1 ? "<ol start=\"" + start + "\">\n" : "<ol>\n");
        }
        else sb.Append("<ul>\n");

        List<string> item = null;
        int contentIndent = 0;
        bool loose = false;
        Action emit = delegate
        {
            if (item == null) return;
            string cls = "", box = "";
            Match tm = Regex.Match(item[0], @"^\[([ xX])\] ");
            if (tm.Success)
            {
                cls = " class=\"task\"";
                box = "<input type=\"checkbox\" disabled" + (tm.Groups[1].Value == " " ? "" : " checked") + "> ";
                item[0] = item[0].Substring(tm.Length);
            }
            string inner = Blocks(item);
            if (!loose) inner = Regex.Replace(inner, @"^<p>([\s\S]*?)</p>\n", "$1\n");
            sb.Append("<li" + cls + ">" + box + inner.TrimEnd('\n') + "</li>\n");
        };

        bool prevBlank = false;
        while (i < L.Count)
        {
            string line = L[i];
            Match m = ListRe.Match(line);
            int ind = Indent(line);
            if (line.Trim().Length == 0)
            {
                int j = i + 1;
                while (j < L.Count && L[j].Trim().Length == 0) j++;
                if (j >= L.Count) break;
                Match nm = ListRe.Match(L[j]);
                bool sameLevelItem = nm.Success && Indent(L[j]) <= baseIndent + 1;
                if (Indent(L[j]) < contentIndent && !sameLevelItem) break;
                if (Indent(L[j]) >= contentIndent) loose = true; // blank line inside an item
                item.Add("");
                prevBlank = true; i++; continue;
            }
            if (m.Success && ind <= baseIndent + 1)
            {
                if (char.IsDigit(m.Groups[2].Value[0]) != ordered) break;
                if (prevBlank) loose = true;
                emit();
                item = new List<string>();
                item.Add(m.Groups[4].Value);
                contentIndent = m.Groups[4].Length > 0 ? m.Groups[4].Index : ind + m.Groups[2].Length + 1;
            }
            else if (ind >= contentIndent || ind > baseIndent)
                item.Add(line.Substring(Math.Min(ind, contentIndent)));
            else if (!prevBlank && ind < 4 && !Regex.IsMatch(line, @"^ {0,3}(#|>|```|~~~)"))
                item.Add(line.Trim()); // lazy continuation line
            else break;
            prevBlank = false;
            i++;
        }
        // drop trailing blank lines from the item
        while (item.Count > 1 && item[item.Count - 1].Length == 0) item.RemoveAt(item.Count - 1);
        emit();
        sb.Append("</" + tag + ">\n");
        return i;
    }

    static string JoinPara(List<string> lines)
    {
        var sb = new StringBuilder();
        for (int k = 0; k < lines.Count; k++)
        {
            string l = lines[k];
            bool br = l.EndsWith("  ") || l.EndsWith("\\");
            l = l.Trim();
            if (l.EndsWith("\\")) l = l.Substring(0, l.Length - 1);
            sb.Append(l);
            if (k < lines.Count - 1) sb.Append(br ? "\u0003\n" : "\n");
        }
        return sb.ToString();
    }

    static string Esc(string s)
    {
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    static string Slug(string s)
    {
        s = Regex.Replace(s.ToLowerInvariant(), @"[^\w\- ]", "");
        s = s.Trim().Replace(' ', '-');
        int c;
        if (ids.TryGetValue(s, out c)) { ids[s] = c + 1; s = s + "-" + c; } else ids[s] = 1;
        return Esc(s);
    }

    const string Keywords = "if|else|elif|for|foreach|while|do|return|function|def|class|struct|enum|interface|import|from|export|" +
        "const|let|var|new|true|false|null|None|True|False|nil|this|self|public|private|protected|static|void|int|string|bool|" +
        "try|catch|finally|throw|raise|except|using|namespace|package|in|of|is|as|async|await|yield|break|continue|switch|case|" +
        "default|param|lambda|with|not|and|or|fn|func|go|pub|impl|use|mut|select|where|order|by|insert|update|delete|create|table|" +
        "echo|then|fi|done|end|begin";

    static string Highlight(string code, string lang)
    {
        lang = lang.ToLowerInvariant();
        if (lang == "" || lang == "text" || lang == "txt" || lang == "md" || lang == "markdown") return Esc(code);
        string com;
        if (" py python ps ps1 powershell pwsh sh bash shell zsh yaml yml toml rb ruby r conf ini dockerfile makefile ".Contains(" " + lang + " ")) com = @"#.*";
        else if (lang == "sql" || lang == "lua") com = @"--.*";
        else if (lang == "html" || lang == "xml" || lang == "svg") com = @"<!--[\s\S]*?-->";
        else com = @"//.*|/\*[\s\S]*?\*/";
        var opt = lang == "sql" || lang.StartsWith("ps") || lang == "powershell" ? RegexOptions.IgnoreCase : RegexOptions.None;
        var re = new Regex(@"(?<c>" + com + @")|(?<s>""(?:\\.|[^""\\\n])*""|'(?:\\.|[^'\\\n])*'|`[^`\n]*`)|(?<n>\b\d+(?:\.\d+)?\b)|(?<k>\b(?:" + Keywords + @")\b)", opt);
        var sb = new StringBuilder();
        int pos = 0;
        foreach (Match m in re.Matches(code))
        {
            sb.Append(Esc(code.Substring(pos, m.Index - pos)));
            string cls = m.Groups["c"].Success ? "c" : m.Groups["s"].Success ? "s" : m.Groups["n"].Success ? "n" : "k";
            sb.Append("<span class=\"" + cls + "\">" + Esc(m.Value) + "</span>");
            pos = m.Index + m.Length;
        }
        sb.Append(Esc(code.Substring(pos)));
        return sb.ToString();
    }

    static string Inline(string s)
    {
        var codes = new List<string>();
        s = Regex.Replace(s, @"(`+)(.+?)\1", m =>
        {
            codes.Add("<code>" + Esc(m.Groups[2].Value.Trim()) + "</code>");
            return "\u0001" + (codes.Count - 1) + "\u0002";
        });
        s = Esc(s);
        s = Regex.Replace(s, @"\\([\\`*_{}\[\]()#+\-.!|~])", m => "&#" + (int)m.Groups[1].Value[0] + ";");
        s = Regex.Replace(s, @"!\[([^\]]*)\]\(([^)\s]+)(?:\s+&quot;.*?&quot;)?\)", "<img src=\"$2\" alt=\"$1\">");
        s = Regex.Replace(s, @"\[([^\]]+)\]\(([^)\s]+)(?:\s+&quot;.*?&quot;)?\)", "<a href=\"$2\">$1</a>");
        s = Regex.Replace(s, @"&lt;(https?://[^\s&]+)&gt;", "<a href=\"$1\">$1</a>");
        s = Regex.Replace(s, @"(?<![""=>;/])\b(https?://[^\s<]+[^\s<.,;:!?)'])", "<a href=\"$1\">$1</a>");
        s = Regex.Replace(s, @"\*\*(?!\s)(.+?)(?<!\s)\*\*", "<strong>$1</strong>");
        s = Regex.Replace(s, @"(?<!\w)__(?!\s)(.+?)(?<!\s)__(?!\w)", "<strong>$1</strong>");
        s = Regex.Replace(s, @"(?<![\w*])\*(?![\s*])(.+?)(?<![\s*])\*(?![\w*])", "<em>$1</em>");
        s = Regex.Replace(s, @"(?<![\w&#])_(?!\s)(.+?)(?<!\s)_(?!\w)", "<em>$1</em>");
        s = Regex.Replace(s, @"~~(.+?)~~", "<del>$1</del>");
        s = s.Replace("\u0003", "<br>");
        s = Regex.Replace(s, "\u0001(\\d+)\u0002", m => codes[int.Parse(m.Groups[1].Value)]);
        return s;
    }
}
