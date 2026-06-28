// HeadlessTester — WPF UI smoke tester for Master's FM tray.
//
// Why this exists: in v14.2.0 + v14.2.1 I shipped a "Audio source" tray menu
// that LOOKED right in source but didn't actually function (parent MenuItem
// had sub-items but no Command, so clicking did nothing visible). A cold
// build + manual click would have caught it. This harness DOES that as code:
//
//   1. Loads each window XAML from disk via XamlReader (no project-reference
//      DataContext needed; we just see the structure)
//   2. Walks the visual tree of MainWindow's ContextMenu, dumps each MenuItem
//      with its Header + Command binding. FLAGS the anomaly shape that hit us
//      (parent with children but null Command — UX trap)
//   3. For each Dialog window: forces layout (Measure/Arrange/UpdateLayout)
//      and renders to PNG via RenderTargetBitmap — entirely OFFSCREEN. No
//      window ever pops up on the operator's display, no mouse, no keyboard.
//   4. Dumps the visual tree as text per window so changes show in diff.
//
// Outputs to tests/HeadlessTester/snapshots/<timestamp>/:
//   - report.md (overall pass/fail per window + anomaly list)
//   - ctx_menu_tree.txt (full tray ContextMenu walk)
//   - <WindowName>.png (offscreen render)
//   - <WindowName>.tree.txt (visual tree dump)
//
// Run: dotnet run --project tests/HeadlessTester
// Or after build: tests/HeadlessTester/bin/Debug/net8.0-windows/HeadlessTester.exe
//
// Future expansion (not in MVP): boot the tray's full DI container, resolve
// real ViewModels as DataContext so Bindings render real values; programmatic
// command invocation; wizard navigation through every step.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using XamlReader = System.Windows.Markup.XamlReader;

namespace MastersFM.HeadlessTester;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // --smtc subcommand: dump every live SMTC session's metadata
        // (Title/Artist/PlaybackType/Status). Used to confirm runtime bugs
        // like "Master's FM shows wrong info while Prime Video is playing"
        // without having to attach a debugger to the tray.
        if (args.Length > 0 && args[0] == "--smtc")
        {
            return SmtcSnapshot.Run();
        }

        // Default source dir = repo's src/tray_csharp (relative to the test exe).
        // Allow override: HeadlessTester.exe <path-to-tray_csharp>
        var trayDir = ResolveTrayDir(args);
        var outDir  = Path.Combine(AppContext.BaseDirectory, "snapshots",
            DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(outDir);

        Console.WriteLine($"tray src dir : {trayDir}");
        Console.WriteLine($"snapshot dir : {outDir}");
        Console.WriteLine();

        var report = new StringBuilder();
        report.AppendLine($"# Headless tester run {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine();
        report.AppendLine($"- tray source: `{trayDir}`");
        report.AppendLine($"- snapshot   : `{outDir}`");
        report.AppendLine();

        int totalAnomalies = 0;
        int totalFailures  = 0;

        try
        {
            // WPF needs an Application instance to look up FrameworkElement
            // dispatcher + resource lookups, even for XamlReader-loaded trees.
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            // Merge the tray's theme dictionary so {StaticResource ...} refs
            // in dialog XAML resolve. Each theme file is loadable as a raw XAML
            // resource dictionary.
            LoadThemeResources(app, trayDir, report);

            // ── Test 1: MainWindow ContextMenu structure ────────────────────
            report.AppendLine("## Test 1: Tray ContextMenu");
            report.AppendLine();
            var mwXaml = Path.Combine(trayDir, "MainWindow.xaml");
            if (File.Exists(mwXaml))
            {
                var (anomalies, treeText) = AnalyzeMainWindowMenu(mwXaml);
                File.WriteAllText(Path.Combine(outDir, "ctx_menu_tree.txt"), treeText);
                report.AppendLine($"- Walked MainWindow.xaml ContextMenu, dumped to `ctx_menu_tree.txt`");
                if (anomalies.Count == 0)
                {
                    report.AppendLine($"- ✓ no command/structure anomalies");
                }
                else
                {
                    totalAnomalies += anomalies.Count;
                    report.AppendLine();
                    report.AppendLine($"### ⚠️ {anomalies.Count} anomaly(ies)");
                    report.AppendLine();
                    foreach (var a in anomalies) report.AppendLine($"- {a}");
                }
            }
            else
            {
                report.AppendLine($"- ✗ MainWindow.xaml not found at {mwXaml}");
                totalFailures++;
            }
            report.AppendLine();

            // ── Test 2: Each Dialog window — load, layout, render ──────────
            report.AppendLine("## Test 2: Dialog windows (offscreen render + tree dump)");
            report.AppendLine();
            var dialogsDir = Path.Combine(trayDir, "Dialogs");
            if (Directory.Exists(dialogsDir))
            {
                foreach (var xamlFile in Directory.GetFiles(dialogsDir, "*.xaml").OrderBy(p => p))
                {
                    var name = Path.GetFileNameWithoutExtension(xamlFile);
                    var result = RenderDialog(xamlFile, name, outDir);
                    report.AppendLine($"- {result.Mark} **{name}** — {result.Message}");
                    if (!result.Ok) totalFailures++;
                }
            }
            else
            {
                report.AppendLine($"- ✗ Dialogs dir not found at {dialogsDir}");
                totalFailures++;
            }
            report.AppendLine();

            // ── Summary ──────────────────────────────────────────────────────
            report.AppendLine("## Summary");
            report.AppendLine();
            report.AppendLine($"- Anomalies (UX traps): **{totalAnomalies}**");
            report.AppendLine($"- Failures (couldn't render/parse): **{totalFailures}**");
            report.AppendLine();
            if (totalAnomalies == 0 && totalFailures == 0)
            {
                report.AppendLine("✅ All checks passed.");
            }
            else
            {
                report.AppendLine("❌ Issues found — fix before shipping.");
            }

            var reportPath = Path.Combine(outDir, "report.md");
            File.WriteAllText(reportPath, report.ToString());

            Console.WriteLine();
            Console.WriteLine($"Report: {reportPath}");
            Console.WriteLine($"Anomalies: {totalAnomalies}   Failures: {totalFailures}");

            return totalAnomalies + totalFailures == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex}");
            File.WriteAllText(Path.Combine(outDir, "FATAL.txt"), ex.ToString());
            return 2;
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string ResolveTrayDir(string[] args)
    {
        if (args.Length > 0) return Path.GetFullPath(args[0]);
        // Default: walk up from the test exe to repo root, then src/tray_csharp
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, "src", "tray_csharp");
            if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
            dir = Path.GetDirectoryName(dir);
        }
        // Last-ditch hard-coded fallback
        return @"G:\Project Folder\Master FM\src\tray_csharp";
    }

    private static void LoadThemeResources(Application app, string trayDir, StringBuilder report)
    {
        // The tray's App.xaml typically merges Theme/Index.xaml which itself
        // chains the rest. Load Index.xaml from disk; if it can't be loaded
        // (typically because it has compiled-only references), fall back to
        // each leaf theme file.
        var indexPath = Path.Combine(trayDir, "Theme", "Index.xaml");
        var leafTheme = new[] {
            "Colors.xaml", "Typography.xaml", "Buttons.xaml", "Cards.xaml",
            "Inputs.xaml", "ScrollBars.xaml", "Animations.xaml", "Icons.xaml",
            "BrandIcons.xaml", "AppDialogStyle.xaml", "ContextMenu.xaml"
        };

        int loaded = 0, failed = 0;
        TryLoadDict(app, indexPath, ref loaded, ref failed);
        if (loaded == 0)
        {
            foreach (var leaf in leafTheme)
            {
                TryLoadDict(app, Path.Combine(trayDir, "Theme", leaf), ref loaded, ref failed);
            }
        }
        report.AppendLine($"- Theme resources merged: {loaded} ok, {failed} failed.");
        report.AppendLine();
    }

    private static void TryLoadDict(Application app, string path, ref int loaded, ref int failed)
    {
        if (!File.Exists(path)) return;
        try
        {
            // Strip compile-only attributes that XamlReader rejects at runtime
            // (x:Shared, MergedDictionaries with Source="" to other Theme files
            // that load fine on their own once we strip x:Shared).
            var text = File.ReadAllText(path);
            text = System.Text.RegularExpressions.Regex.Replace(text,
                @"\s+x:Shared\s*=\s*""(True|False)""", "");
            using var s = new MemoryStream(Encoding.UTF8.GetBytes(text));
            if (XamlReader.Load(s) is ResourceDictionary rd)
            {
                app.Resources.MergedDictionaries.Add(rd);
                loaded++;
            }
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine($"  theme dict load failed for {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    // ── Test 1: MainWindow ContextMenu structure ─────────────────────────────

    private static (List<string> anomalies, string tree) AnalyzeMainWindowMenu(string mwXaml)
    {
        var anomalies = new List<string>();
        var tree      = new StringBuilder();

        // Read MainWindow.xaml as XML and walk it. We DON'T XamlReader.Load it
        // because MainWindow has compiled code-behind references that fail to
        // resolve under XamlReader. Walking as XML gives us the structural
        // shape we care about for menu anomaly detection.
        var doc = new XmlDocument();
        doc.Load(mwXaml);
        var ns = new XmlNamespaceManager(doc.NameTable);
        // The default namespace in WPF XAML
        ns.AddNamespace("p", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");

        var menuItems = doc.SelectNodes("//p:MenuItem", ns);
        tree.AppendLine($"MainWindow.xaml MenuItems found: {menuItems?.Count ?? 0}");
        tree.AppendLine();

        if (menuItems == null) return (anomalies, tree.ToString());

        foreach (XmlNode mi in menuItems)
        {
            int depth = ParentMenuItemDepth(mi);
            var pad   = new string(' ', depth * 2);
            var header  = mi.Attributes?["Header"]?.Value ?? "(no header)";
            var command = mi.Attributes?["Command"]?.Value;
            var isCheckable = mi.Attributes?["IsCheckable"]?.Value;
            var styleHint   = mi.Attributes?["Style"]?.Value ?? "";

            // Count child MenuItems that are direct nested items (the submenu).
            int childCount = 0;
            foreach (XmlNode c in mi.ChildNodes)
                if (c.NodeType == XmlNodeType.Element && c.LocalName == "MenuItem") childCount++;

            tree.AppendLine($"{pad}- Header=\"{header}\"  Command={(command ?? "(none)")}  IsCheckable={(isCheckable ?? "false")}  children={childCount}");

            // ── THE ANOMALY DETECTION ────────────────────────────────────────
            // A MenuItem that:
            //   - has child MenuItems (so it's acting as a submenu container),
            //   - has NO Command (so clicking its OWN header does nothing),
            //   - is NOT styled as a sub-header (read-only label),
            //   - is NOT a separator-like spacer
            // …is a UX trap: users click it expecting an action, nothing happens.
            // This is exactly the v14.2.x "Audio source" submenu shape that
            // shipped broken.
            bool isSubheader = styleHint.Contains("SubHeader", StringComparison.OrdinalIgnoreCase);
            if (childCount > 0
                && string.IsNullOrEmpty(command)
                && !isSubheader
                && depth == 0  // only flag top-level — nested submenus are expected to behave this way
                && !string.Equals(header, "(no header)", StringComparison.Ordinal))
            {
                anomalies.Add(
                    $"Top-level MenuItem `\"{header}\"` has {childCount} sub-item(s) but NO Command. " +
                    $"Clicking the header itself does nothing — users may not realize they need to hover for the submenu. " +
                    $"Add a Command (so it has its own click action) or move the children to siblings.");
            }
        }

        return (anomalies, tree.ToString());
    }

    private static int ParentMenuItemDepth(XmlNode node)
    {
        int depth = 0;
        var p = node.ParentNode;
        while (p != null)
        {
            if (p.NodeType == XmlNodeType.Element && p.LocalName == "MenuItem") depth++;
            p = p.ParentNode;
        }
        return depth;
    }

    // ── Test 2: Render each Dialog window offscreen ──────────────────────────

    private record RenderResult(bool Ok, string Mark, string Message);

    private static RenderResult RenderDialog(string xamlFile, string name, string outDir)
    {
        FrameworkElement? root = null;
        try
        {
            // XamlReader.Load fails when the root has x:Class because that
            // references a compiled code-behind type we don't have. Strip the
            // x:Class attribute and any x:Name="<something>" on the root, and
            // also drop event-handler attributes (PreviewMouseDown="...", etc.)
            // which point to methods on the code-behind. What remains is a
            // pure-WPF tree that XamlReader can instantiate generically.
            var xamlText = File.ReadAllText(xamlFile);
            xamlText = System.Text.RegularExpressions.Regex.Replace(xamlText,
                @"\s+x:Class\s*=\s*""[^""]*""", "");
            xamlText = System.Text.RegularExpressions.Regex.Replace(xamlText,
                @"\s+(MouseDown|MouseLeftButtonDown|MouseRightButtonDown|PreviewMouseDown|PreviewKeyDown|KeyDown|Loaded|Closing|Closed|DataContextChanged|SelectionChanged|Click)\s*=\s*""[^""]*""", "");
            using var s = new MemoryStream(Encoding.UTF8.GetBytes(xamlText));
            var loaded = XamlReader.Load(s);
            root = loaded as FrameworkElement;
            if (root == null)
            {
                return new RenderResult(false, "✗",
                    $"loaded root was {loaded?.GetType().Name ?? "null"}, not a FrameworkElement");
            }
        }
        catch (Exception ex)
        {
            // Fall back: walk as XML and dump structure even though we can't render.
            try
            {
                var fallbackTree = DumpXamlAsXml(xamlFile);
                File.WriteAllText(Path.Combine(outDir, name + ".tree.txt"), fallbackTree);
                return new RenderResult(false, "⚠️",
                    $"XamlReader threw `{ex.GetType().Name}: {Truncate(ex.Message, 100)}` — dumped XML structure to {name}.tree.txt instead.");
            }
            catch
            {
                return new RenderResult(false, "✗",
                    $"both XamlReader AND XML fallback failed: {Truncate(ex.Message, 120)}");
            }
        }

        // Force layout offscreen. Window's layout subsystem needs explicit
        // Width/Height — without a Show() to trigger HWND creation, ActualWidth
        // stays at the layout-system default (0 → renders as 1×1px). Force the
        // dimensions and use them directly for the bitmap size.
        double w = 720, h = 560;
        if (root is Window winRoot)
        {
            if (!double.IsNaN(winRoot.Width)  && winRoot.Width  > 0) w = winRoot.Width;
            if (!double.IsNaN(winRoot.Height) && winRoot.Height > 0) h = winRoot.Height;
            winRoot.Width  = w;
            winRoot.Height = h;
        }

        try
        {
            root.Measure(new Size(w, h));
            root.Arrange(new Rect(0, 0, w, h));
            root.UpdateLayout();
            // Pump dispatcher one frame so any deferred Invokes settle.
            PumpDispatcherOnce();

            // Render to PNG — use forced w/h directly. ActualWidth comes back
            // as 0 because no HWND was created; use the dimensions we forced.
            var actualW = (int)Math.Ceiling(w);
            var actualH = (int)Math.Ceiling(h);
            var bmp = new RenderTargetBitmap(actualW, actualH, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(root);
            using (var fs = File.Create(Path.Combine(outDir, name + ".png")))
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                enc.Save(fs);
            }

            // Dump tree
            var sb = new StringBuilder();
            DumpVisualTree(root, sb, 0);
            File.WriteAllText(Path.Combine(outDir, name + ".tree.txt"), sb.ToString());

            return new RenderResult(true, "✓",
                $"rendered to `{name}.png` ({actualW}×{actualH}px), tree → `{name}.tree.txt`");
        }
        catch (Exception ex)
        {
            return new RenderResult(false, "✗",
                $"layout/render threw `{ex.GetType().Name}: {Truncate(ex.Message, 120)}`");
        }
    }

    private static void PumpDispatcherOnce()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void DumpVisualTree(DependencyObject node, StringBuilder sb, int depth)
    {
        var pad = new string(' ', depth * 2);
        var fe  = node as FrameworkElement;
        var name = fe?.Name;
        var t    = node.GetType().Name;

        // Common interesting properties to surface
        string content = "";
        if (node is ContentControl cc && cc.Content is string s && s.Length > 0)
            content = $" Content=\"{Truncate(s, 50)}\"";
        else if (node is TextBlock tb && !string.IsNullOrEmpty(tb.Text))
            content = $" Text=\"{Truncate(tb.Text, 50)}\"";
        else if (node is MenuItem mi && mi.Header is string mh)
            content = $" Header=\"{Truncate(mh, 50)}\"";

        sb.AppendLine($"{pad}{t}{(string.IsNullOrEmpty(name) ? "" : $" Name={name}")}{content}");

        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            DumpVisualTree(VisualTreeHelper.GetChild(node, i), sb, depth + 1);
        }
    }

    private static string DumpXamlAsXml(string path)
    {
        var doc = new XmlDocument();
        doc.Load(path);
        var sb = new StringBuilder();
        WalkXml(doc.DocumentElement!, sb, 0);
        return sb.ToString();
    }

    private static void WalkXml(XmlNode node, StringBuilder sb, int depth)
    {
        var pad = new string(' ', depth * 2);
        sb.Append($"{pad}<{node.LocalName}");
        if (node.Attributes != null)
        {
            foreach (XmlAttribute attr in node.Attributes)
            {
                if (attr.LocalName is "xmlns" or "x" || attr.Name.StartsWith("xmlns:")) continue;
                sb.Append($" {attr.LocalName}=\"{Truncate(attr.Value, 60)}\"");
            }
        }
        sb.AppendLine(">");

        foreach (XmlNode c in node.ChildNodes)
        {
            if (c.NodeType == XmlNodeType.Element) WalkXml(c, sb, depth + 1);
        }
    }

    private static string Truncate(string s, int n)
        => s.Length <= n ? s : s.Substring(0, n) + "…";
}
