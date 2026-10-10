using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using AdultZone.Core;
using AdultZone.Core.Providers;
using Microsoft.Web.WebView2.Wpf;

namespace AdultZone.Desktop;

/// <summary>
/// Babepedia only answers a real browser, so its page is opened in one (Edge's
/// WebView2) and the pictures are read, and fetched, through that page.
/// </summary>
public static class BabepediaWindow
{
    /// <summary>Every picture on the performer's page; the one clicked comes back as its bytes.</summary>
    public static byte[]? Pick(Window owner, string name, bool wide)
    {
        var w = Dialogs.Create(owner, $"{name} · Babepedia", 980, 720);
        byte[]? chosen = null;
        var status = Ui.Text("Loading…", 13, Theme.Muted, margin: new Thickness(0, 0, 0, 12));
        var tiles = new WrapPanel();
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Content = tiles, Visibility = Visibility.Collapsed };
        var web = new WebView2 { CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = Path.Combine(Config.AppHome, "browser") } };
        var body = new Grid();
        body.Children.Add(web);
        body.Children.Add(scroll);
        var dock = new DockPanel { Margin = new Thickness(22, 18, 14, 18), LastChildFill = true };
        DockPanel.SetDock(status, Dock.Top);
        dock.Children.Add(status);
        dock.Children.Add(body);
        w.Content = dock;

        var slots = new Dictionary<int, Border>();
        var listed = false;
        void Fail(string text) { status.Foreground = Theme.Warn; status.Text = text; }

        web.CoreWebView2InitializationCompleted += (_, e) =>
        {
            if (!e.IsSuccess) Fail("Microsoft Edge WebView2 Runtime is needed for Babepedia. " + e.InitializationException?.Message);
        };
        web.NavigationCompleted += async (_, _) =>
        {
            if (listed) return;
            List<string> found;
            try
            {
                var address = JsonSerializer.Deserialize<string>(await web.ExecuteScriptAsync("location.href")) ?? "";
                if (!address.Contains("/babe/", StringComparison.OrdinalIgnoreCase)) return;
                var html = JsonSerializer.Deserialize<string>(await web.ExecuteScriptAsync("document.documentElement.outerHTML")) ?? "";
                found = Scrape.BabepediaParse(html);
            }
            catch (Exception ex) { Fail(ex.Message); return; }
            // Nothing yet: a check page or a missing profile. The page stays up to be dealt with by hand.
            if (found.Count == 0) { status.Text = "No pictures on this page"; return; }
            listed = true;
            status.Text = found.Count == 1 ? "1 picture" : $"{found.Count} pictures";
            web.Visibility = Visibility.Hidden;
            scroll.Visibility = Visibility.Visible;
            for (var i = 0; i < found.Count; i++)
            {
                var art = new Border { Width = wide ? 290 : 170, Height = wide ? 163 : 235, Background = Theme.Ink, CornerRadius = new CornerRadius(8), ClipToBounds = true };
                var frame = new Border { BorderBrush = Theme.LineSoft, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Child = art, Margin = new Thickness(0, 0, 12, 12) };
                slots[i] = art;
                tiles.Children.Add(frame);
                var url = JsonSerializer.Serialize(found[i]);
                // The page fetches its own picture and hands it over as text.
                _ = web.ExecuteScriptAsync(
                    "(async()=>{try{const r=await fetch(" + url + ");if(!r.ok)throw 0;const b=await r.blob();" +
                    "const f=new FileReader();f.onload=()=>chrome.webview.postMessage('" + i + "|'+f.result);f.readAsDataURL(b);}" +
                    "catch(e){chrome.webview.postMessage('" + i + "|');}})()");
            }
        };
        web.WebMessageReceived += (_, e) =>
        {
            try
            {
                var text = e.TryGetWebMessageAsString();
                var bar = text.IndexOf('|');
                if (bar < 1 || !int.TryParse(text[..bar], out var index) || !slots.TryGetValue(index, out var art)) return;
                var frame = (Border)art.Parent;
                var comma = text.IndexOf(',', bar);
                if (comma < 0) { tiles.Children.Remove(frame); return; }
                var data = Convert.FromBase64String(text[(comma + 1)..]);
                if (Http.ImageExt(data) == null) { tiles.Children.Remove(frame); return; }
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 520;
                bmp.StreamSource = new MemoryStream(data);
                bmp.EndInit();
                bmp.Freeze();
                var picture = new Image { Source = bmp, Stretch = System.Windows.Media.Stretch.Uniform };
                Ui.FitShape(art, (double)bmp.PixelWidth / bmp.PixelHeight, picture);
                art.Child = picture;
                frame.Cursor = Cursors.Hand;
                frame.MouseEnter += (_, _) => frame.BorderBrush = Theme.Ember;
                frame.MouseLeave += (_, _) => frame.BorderBrush = Theme.LineSoft;
                frame.MouseLeftButtonUp += (_, _) => { chosen = data; w.Close(); };
            }
            catch { }
        };
        w.Loaded += (_, _) =>
        {
            try { web.Source = new Uri(Scrape.BabepediaPage(name)); }
            catch (Exception ex) { Fail(ex.Message); }
        };
        w.Closed += (_, _) => web.Dispose();
        w.ShowDialog();
        return chosen;
    }
}
