using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AdultZone.Core;
using AdultZone.Core.Data;
using AdultZone.Core.Library;
using AdultZone.Core.Providers;

namespace AdultZone.Desktop;

/// <summary>
/// The right-click menu on a performer's photo and wide photo: change it (from
/// this computer, ThePornDB or Babepedia), adjust how it sits in its frame, or remove it.
/// </summary>
public static class PictureTools
{
    /// <summary>Puts the menu on an element. Column is "image" for the photo, "banner" for the wide photo.</summary>
    public static void Attach(FrameworkElement target, MainWindow win, Row actor, string column)
    {
        var id = actor.Long("id") ?? 0;
        var name = actor.Str("name");
        var wide = column == "banner";
        var menu = new ContextMenu();
        MenuItem Item(string text, Action click, bool stay = false)
        {
            var item = new MenuItem { Header = text, StaysOpenOnClick = stay };
            item.Click += (_, _) => click();
            return item;
        }
        void Done(string said)
        {
            Images.Clear();
            win.Toast(said);
            win.Refresh(keepScroll: true);
        }
        void First()
        {
            menu.Items.Clear();
            var has = actor.Str(column).Length > 0;
            menu.Items.Add(Item("Change image", Sources, stay: true));
            if (has)
            {
                menu.Items.Add(Item("Adjust image", () =>
                {
                    if (Adjust(win, actor, column)) Done("Image adjusted");
                }));
                menu.Items.Add(Item("Remove image", () =>
                {
                    Catalog.RemoveActorPicture(id, column);
                    Done("Image removed");
                }));
            }
        }
        // The second part of the menu: where the new picture comes from.
        void Sources()
        {
            menu.Items.Clear();
            menu.Items.Add(Item("From this computer", () =>
            {
                if (Dialogs.PickImage(win) is not { } file) return;
                Catalog.SetActorPicture(id, column, file);
                Done("Image changed");
            }));
            menu.Items.Add(Item("ThePornDB", () => FromSource("ThePornDB", () => Scrape.TpdbPictures(name, wide))));
            menu.Items.Add(Item("Babepedia", () =>
            {
                if (BabepediaWindow.Pick(win, name, wide) is not { } data) return;
                try
                {
                    var saved = Catalog.SavePicture(Config.ActorDir, wide ? $"actor_{id}_banner" : $"actor_{id}", data, Http.ImageExt(data) ?? ".jpg");
                    Catalog.SetPictureColumn("actors", column, Config.ActorDir, id, saved);
                    Done("Image changed");
                }
                catch (Exception ex) { win.Toast(ex.Message, true); }
            }));
        }
        void FromSource(string source, Func<List<string>> load)
        {
            if (Pick(win, $"{name} · {source}", load, wide) is not { } url) return;
            try
            {
                var saved = Scrape.SaveImage(url, Config.ActorDir, wide ? $"actor_{id}_banner" : $"actor_{id}", false);
                Catalog.SetPictureColumn("actors", column, Config.ActorDir, id, saved);
                Done("Image changed");
            }
            catch (Exception ex) { win.Toast(ex.Message, true); }
        }
        menu.Opened += (_, _) => First();
        target.ContextMenu = menu;
    }

    /// <summary>A window of every picture a source has; the one clicked is returned.</summary>
    static string? Pick(Window owner, string title, Func<List<string>> load, bool wide)
    {
        var w = Dialogs.Create(owner, title, 980, 720);
        string? chosen = null;
        var status = Ui.Text("Loading…", 13, Theme.Muted, margin: new Thickness(0, 0, 0, 12));
        var tiles = new WrapPanel();
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Content = tiles };
        var dock = new DockPanel { Margin = new Thickness(22, 18, 14, 18), LastChildFill = true };
        DockPanel.SetDock(status, Dock.Top);
        dock.Children.Add(status);
        dock.Children.Add(scroll);
        w.Content = dock;
        w.Loaded += async (_, _) =>
        {
            List<string> found;
            try { found = await Task.Run(load); }
            catch (Exception ex)
            {
                status.Foreground = Theme.Warn;
                status.Text = ex.Message;
                return;
            }
            status.Text = found.Count == 1 ? "1 picture" : $"{found.Count} pictures";
            foreach (var url in found)
            {
                var address = url;
                var art = new Border { Width = wide ? 290 : 170, Height = wide ? 163 : 235, Background = Theme.Ink, CornerRadius = new CornerRadius(8), ClipToBounds = true };
                Ui.Whole(art, new[] { address }, 520, fitShape: true);
                var frame = new Border { BorderBrush = Theme.LineSoft, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Child = art };
                var b = Ui.Bare(frame, () => { chosen = address; w.Close(); });
                b.Cursor = Cursors.Hand;
                b.Margin = new Thickness(0, 0, 12, 12);
                b.MouseEnter += (_, _) => frame.BorderBrush = Theme.Ember;
                b.MouseLeave += (_, _) => frame.BorderBrush = Theme.LineSoft;
                tiles.Children.Add(b);
            }
        };
        w.ShowDialog();
        return chosen;
    }

    /// <summary>A window with the picture in its frame: drag to move it, the wheel or the slider to zoom. True when saved.</summary>
    static bool Adjust(Window owner, Row actor, string column)
    {
        var wide = column == "banner";
        var file = wide ? Catalog.ActorBanner(actor) : Catalog.ActorPhoto(actor);
        if (file.Length == 0 || !File.Exists(file)) return false;
        BitmapImage bmp;
        try
        {
            bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(file);
            bmp.EndInit();
        }
        catch { return false; }
        if (bmp.PixelWidth == 0 || bmp.PixelHeight == 0) return false;
        var (x, y, zoom) = Catalog.PicturePos(actor.Str(column + "_pos"));
        var saved = false;

        // The frame as it is on the profile: upright for the photo, wide for the wide photo.
        double frameW = wide ? 640 : 330, frameH = wide ? 360 : Math.Round(330 * 600 / 435.0);
        var w = Dialogs.Create(owner, actor.Str("name"), frameW + 60, frameH + 170);
        w.ResizeMode = ResizeMode.NoResize;
        var brush = new ImageBrush(bmp) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.RelativeToBoundingBox };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
        var frame = new Border { Width = frameW, Height = frameH, CornerRadius = new CornerRadius(10), Background = brush, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), Cursor = Cursors.SizeAll };
        var picture = (double)bmp.PixelWidth / bmp.PixelHeight;
        void Draw() => brush.Viewbox = Ui.CoverBox(picture, frameW / frameH, x, y, zoom);
        Draw();

        var slider = new Slider { Minimum = 1, Maximum = 4, Value = zoom, Width = frameW, Margin = new Thickness(0, 16, 0, 0), Focusable = false };
        slider.ValueChanged += (_, _) => { zoom = slider.Value; Draw(); };
        Point? grip = null;
        frame.MouseLeftButtonDown += (_, e) => { grip = e.GetPosition(frame); frame.CaptureMouse(); };
        frame.MouseLeftButtonUp += (_, _) => { grip = null; frame.ReleaseMouseCapture(); };
        frame.MouseMove += (_, e) =>
        {
            if (grip is not { } from) return;
            var now = e.GetPosition(frame);
            var box = brush.Viewbox;
            // The picture follows the pointer: the part in view moves the other way, by as much of the picture as was dragged across.
            if (box.Width < 0.999) x = Math.Clamp(x - (now.X - from.X) / frameW * box.Width / (1 - box.Width), 0, 1);
            if (box.Height < 0.999) y = Math.Clamp(y - (now.Y - from.Y) / frameH * box.Height / (1 - box.Height), 0, 1);
            grip = now;
            Draw();
        };
        frame.MouseWheel += (_, e) => slider.Value = Math.Clamp(slider.Value + (e.Delta > 0 ? 0.1 : -0.1), 1, 4);

        var buttons = Ui.Row(
            Ui.Button("Save", () =>
            {
                Catalog.SetActorPicturePos(actor.Long("id") ?? 0, column, x, y, zoom);
                saved = true;
                w.Close();
            }, Ui.Look.Ember, small: true),
            Ui.Button("Reset", () => { x = 0.5; y = 0.3; slider.Value = 1; zoom = 1; Draw(); }, small: true).Margin(10, 0, 0, 0),
            Ui.Button("Cancel", () => w.Close(), small: true).Margin(10, 0, 0, 0));
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 18, 0, 0);
        var stack = new StackPanel { Margin = new Thickness(30, 22, 30, 20) };
        stack.Children.Add(frame);
        stack.Children.Add(slider);
        stack.Children.Add(buttons);
        w.Content = stack;
        w.ShowDialog();
        return saved;
    }
}
