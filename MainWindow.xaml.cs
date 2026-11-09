using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace Pano;

public partial class MainWindow : Window
{
    readonly ClipStore _store = new();
    ICollectionView? _view;
    IntPtr _hwnd, _target;
    WF.NotifyIcon? _tray;
    WF.ToolStripMenuItem? _openItem;
    bool _paused;
    long _ignoreUntil;
    string _hotkeyText = "";
    ClipKind? _kindFilter;
    bool _pinnedFilter;
    readonly DispatcherTimer _captureTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    int _captureAttempts;

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public MainWindow()
    {
        InitializeComponent();
        BuildHints();
        Deactivated += (_, _) => HidePopup();
        PreviewKeyDown += OnKey;
        _captureTimer.Tick += (_, _) => { _captureTimer.Stop(); Capture(); };
    }

    // ================= Startup =================
    public void Init(bool startHidden)
    {
        _store.Load();
        _view = CollectionViewSource.GetDefaultView(_store.Items);
        _view.Filter = o => o is Clip c && Accept(c);
        List.ItemsSource = _view;
        _store.Items.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(Renumber, DispatcherPriority.Background);

        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
        Native.AddClipboardFormatListener(_hwnd);
        RegisterHotkey();
        CreateTray();
        Renumber();

        if (!startHidden)
        {
            ShowPopup(fromTray: true);
            _tray!.ShowBalloonTip(5000, "Pano çalışıyor",
                $"Kopyaladığın her şey artık kaydediliyor. {_hotkeyText} ile istediğin yerde aç.", WF.ToolTipIcon.None);
        }
    }

    void RegisterHotkey()
    {
        (uint mods, uint vk, string text)[] options =
        {
            (Native.MOD_CONTROL | Native.MOD_ALT, 0x56, "Ctrl+Alt+V"),
            (Native.MOD_CONTROL | Native.MOD_SHIFT, 0x20, "Ctrl+Shift+Boşluk"),
            (Native.MOD_ALT | Native.MOD_SHIFT, 0x56, "Alt+Shift+V"),
        };
        foreach (var o in options)
            if (Native.RegisterHotKey(_hwnd, 1, o.mods | Native.MOD_NOREPEAT, o.vk)) { _hotkeyText = o.text; return; }
        _hotkeyText = "";
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_CLIPBOARDUPDATE)
        {
            if (!_paused && Environment.TickCount64 >= _ignoreUntil)
            {
                _captureAttempts = 0;
                _captureTimer.Stop(); _captureTimer.Start(); // debounce: apps often write several formats in a row
            }
        }
        else if (msg == Native.WM_HOTKEY && wParam == (IntPtr)1)
        {
            if (IsVisible && IsActive) HidePopup(); else ShowPopup(fromTray: false);
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ================= Capture =================
    void Capture()
    {
        try
        {
            var data = WF.Clipboard.GetDataObject();
            if (data == null) return;

            // Respect the conventions password managers use to keep secrets out of clipboard history.
            if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") || data.GetDataPresent("Clipboard Viewer Ignore")) return;
            if (data.GetDataPresent("CanIncludeInClipboardHistory") && data.GetData("CanIncludeInClipboardHistory") is MemoryStream ms
                && ms.Length >= 4 && BitConverter.ToInt32(ms.ToArray(), 0) == 0) return;

            if (data.GetDataPresent(WF.DataFormats.FileDrop) && data.GetData(WF.DataFormats.FileDrop) is string[] { Length: > 0 } files)
                _store.AddFiles(files);
            else if (data.GetDataPresent(WF.DataFormats.UnicodeText) && data.GetData(WF.DataFormats.UnicodeText) is string text)
                _store.AddText(text);
            else if (data.GetDataPresent(WF.DataFormats.Bitmap) && WF.Clipboard.GetImage() is SD.Image img)
            {
                using (img)
                {
                    if ((long)img.Width * img.Height > 50_000_000) return;
                    using var outMs = new MemoryStream();
                    img.Save(outMs, SD.Imaging.ImageFormat.Png);
                    _store.AddImage(outMs.ToArray(), img.Width, img.Height);
                }
            }
        }
        catch (ExternalException)
        {
            // Clipboard is locked by another process; try again shortly.
            if (++_captureAttempts < 4) _captureTimer.Start();
        }
        catch (Exception ex) { App.Log(ex); }
    }

    // ================= Popup =================
    public void ShowPopup(bool fromTray)
    {
        var fg = Native.GetForegroundWindow();
        _target = fromTray || fg == _hwnd ? IntPtr.Zero : fg;

        Search.Text = "";
        FAll.IsChecked = true;
        SetFilter(null, false);
        foreach (var c in _store.Items) c.RefreshMeta();
        UpdateHints();

        PositionWindow(centered: fromTray);
        Show();
        Activate();
        Native.SetForegroundWindow(_hwnd);
        Search.Focus();
        Keyboard.Focus(Search);
        SelectIndex(0);
    }

    void PositionWindow(bool centered)
    {
        Native.GetCursorPos(out var p);
        var wa = Native.WorkAreaAt(p);
        Native.GetWindowRect(_hwnd, out var wr);
        int w = wr.R - wr.L, h = wr.B - wr.T;
        int x, y;
        if (centered)
        {
            x = wa.L + (wa.R - wa.L - w) / 2;
            y = wa.T + (wa.B - wa.T - h) / 2;
        }
        else
        {
            x = p.X - 40;
            y = p.Y - 10;
            if (y + h > wa.B) y = p.Y - h + 10;
        }
        x = Math.Max(wa.L, Math.Min(x, wa.R - w));
        y = Math.Max(wa.T, Math.Min(y, wa.B - h));
        Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, 0x1 | 0x4 | 0x10);
    }

    void HidePopup() { if (IsVisible) Hide(); }

    // ================= List / filter =================
    bool Accept(Clip c)
    {
        if (_pinnedFilter && !c.Pinned) return false;
        if (_kindFilter is ClipKind k && c.Kind != k) return false;
        return c.Matches(_query);
    }

    void SetFilter(ClipKind? kind, bool pinned)
    {
        _kindFilter = kind; _pinnedFilter = pinned;
        _view?.Refresh();
        Renumber();
        SelectIndex(0);
    }

    void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (_view == null) return;
        if (sender == FText) SetFilter(ClipKind.Text, false);
        else if (sender == FImage) SetFilter(ClipKind.Image, false);
        else if (sender == FFiles) SetFilter(ClipKind.Files, false);
        else if (sender == FPinned) SetFilter(null, true);
        else SetFilter(null, false);
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        _query = Pano.Clip.Fold(Search.Text.Trim());
        if (_view == null) return;
        _view.Refresh();
        Renumber();
        SelectIndex(0);
    }

    string _query = "";

    void Renumber()
    {
        if (_view == null) return;
        int i = 0;
        foreach (Clip c in _view) c.Number = ++i;
        CountTxt.Text = $"{_store.Items.Count} kayıt";

        bool empty = i == 0;
        Empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty)
        {
            if (_store.Items.Count == 0)
            {
                EmptyTitle.Text = "Pano henüz boş";
                EmptyBody.Text = "Bir şey kopyala (Ctrl+C). Kopyaladığın metinler, görseller ve dosyalar burada birikir.";
            }
            else if (Search.Text.Trim().Length > 0)
            {
                EmptyTitle.Text = $"“{Search.Text.Trim()}” bulunamadı";
                EmptyBody.Text = "Farklı bir kelime dene ya da filtreyi “Tümü” yap.";
            }
            else if (_pinnedFilter)
            {
                EmptyTitle.Text = "Sabitlenmiş kayıt yok";
                EmptyBody.Text = "Sık kullandığın bir kaydı Ctrl+P ile sabitle; temizlemede silinmez ve hep burada durur.";
            }
            else
            {
                EmptyTitle.Text = "Bu türde kayıt yok";
                EmptyBody.Text = "Filtreyi “Tümü” yaparak bütün geçmişi görebilirsin.";
            }
        }
    }

    void SelectIndex(int i)
    {
        int n = List.Items.Count;
        if (n == 0) return;
        i = Math.Clamp(i, 0, n - 1);
        List.SelectedIndex = i;
        List.ScrollIntoView(List.SelectedItem);
    }

    Clip? Selected => List.SelectedItem as Clip;

    // ================= Keyboard & mouse =================
    void OnKey(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        switch (key)
        {
            case Key.Escape:
                if (Search.Text.Length > 0) Search.Text = ""; else HidePopup();
                break;
            case Key.Down: SelectIndex(List.SelectedIndex + 1); break;
            case Key.Up: SelectIndex(List.SelectedIndex - 1); break;
            case Key.PageDown: SelectIndex(List.SelectedIndex + 6); break;
            case Key.PageUp: SelectIndex(List.SelectedIndex - 6); break;
            case Key.Enter: if (Selected is Clip c) _ = Paste(c); break;
            case Key.Tab: CycleFilter(shift ? -1 : 1); break;
            case Key.P when ctrl: if (Selected is Clip p) TogglePin(p); break;
            case Key.Delete when shift || Search.Text.Length == 0: if (Selected is Clip d) Delete(d); break;
            case >= Key.D1 and <= Key.D9 when ctrl: PasteNth(key - Key.D1); break;
            case >= Key.NumPad1 and <= Key.NumPad9 when ctrl: PasteNth(key - Key.NumPad1); break;
            default: return;
        }
        e.Handled = true;
    }

    void CycleFilter(int d)
    {
        var chips = new[] { FAll, FText, FImage, FFiles, FPinned };
        int i = Array.FindIndex(chips, c => c.IsChecked == true);
        chips[(i + d + chips.Length) % chips.Length].IsChecked = true;
    }

    void PasteNth(int idx)
    {
        if (idx < List.Items.Count && List.Items[idx] is Clip c) _ = Paste(c);
    }

    void List_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(List, d) is ListBoxItem { DataContext: Clip c })
            _ = Paste(c);
    }

    void Pin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Clip c) TogglePin(c);
        e.Handled = true;
    }

    void Del_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Clip c) Delete(c);
        e.Handled = true;
    }

    void TogglePin(Clip c)
    {
        c.Pinned = !c.Pinned;
        c.RefreshMeta();
        _store.Save();
        if (_pinnedFilter) { _view?.Refresh(); Renumber(); }
    }

    void Delete(Clip c)
    {
        int i = List.SelectedIndex;
        _store.Remove(c);
        Renumber();
        SelectIndex(i);
        Search.Focus();
    }

    // ================= Paste =================
    async Task Paste(Clip c)
    {
        _ignoreUntil = Environment.TickCount64 + 600;
        try
        {
            var obj = new WF.DataObject();
            switch (c.Kind)
            {
                case ClipKind.Text:
                    obj.SetData(WF.DataFormats.UnicodeText, true, c.Text ?? "");
                    break;
                case ClipKind.Image:
                    var bytes = File.ReadAllBytes(Path.Combine(ClipStore.ImageDir, c.ImageFile!));
                    using (var ms = new MemoryStream(bytes))
                    using (var bmp = new SD.Bitmap(ms))
                        obj.SetData(WF.DataFormats.Bitmap, true, new SD.Bitmap(bmp));
                    obj.SetData("PNG", false, new MemoryStream(bytes));
                    break;
                case ClipKind.Files:
                    var col = new System.Collections.Specialized.StringCollection();
                    foreach (var f in (c.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        if (File.Exists(f) || Directory.Exists(f)) col.Add(f);
                    if (col.Count == 0) { Flash("Bu dosyalar artık mevcut değil."); return; }
                    obj.SetFileDropList(col);
                    break;
            }
            WF.Clipboard.SetDataObject(obj, true, 10, 50);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Flash("Pano şu an meşgul, tekrar dene.");
            return;
        }

        _store.Bump(c);
        var target = _target;
        HidePopup();
        if (target != IntPtr.Zero && Native.IsWindow(target))
        {
            Native.SetForegroundWindow(target);
            await Task.Delay(90);
            Native.SendPaste();
        }
        else
        {
            _tray?.ShowBalloonTip(1500, "Panoya kopyalandı", "İstediğin yere Ctrl+V ile yapıştırabilirsin.", WF.ToolTipIcon.None);
        }
    }

    void Flash(string msg)
    {
        EmptyTitle.Text = msg;
        CountTxt.Text = msg;
    }

    // ================= Footer hints =================
    void BuildHints() => UpdateHints();

    void UpdateHints()
    {
        Hints.Children.Clear();
        void Add(string key, string label)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 14, 2) };
            sp.Children.Add(new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("LineBrush"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 5, 1),
                Child = new TextBlock { Text = key, Foreground = (System.Windows.Media.Brush)FindResource("FgBrush"), FontSize = 11 },
            });
            sp.Children.Add(new TextBlock { Text = label, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            Hints.Children.Add(sp);
        }
        Add("Enter", _target == IntPtr.Zero ? "kopyala" : "yapıştır");
        Add("Ctrl+1–9", "hızlı seçim");
        Add("Ctrl+P", "sabitle");
        Add("Shift+Del", "sil");
        Add("Tab", "filtre");
        Add("Esc", "kapat");
    }

    // ================= Tray =================
    void CreateTray()
    {
        var res = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
        var icon = new SD.Icon(res!.Stream, WF.SystemInformation.SmallIconSize);

        var menu = new WF.ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Font = new SD.Font("Segoe UI", 9.5f),
            Padding = new WF.Padding(2, 4, 2, 4),
        };
        _openItem = new WF.ToolStripMenuItem("Panoyu aç", null, (_, _) => ShowPopup(fromTray: true))
        { ShortcutKeyDisplayString = _hotkeyText, Font = new SD.Font("Segoe UI", 9.5f, SD.FontStyle.Bold) };
        var pause = new WF.ToolStripMenuItem("Kaydı duraklat") { CheckOnClick = true };
        pause.CheckedChanged += (_, _) =>
        {
            _paused = pause.Checked;
            PausedBadge.Visibility = _paused ? Visibility.Visible : Visibility.Collapsed;
            _tray!.Text = _paused ? "Pano (duraklatıldı)" : "Pano";
        };
        var auto = new WF.ToolStripMenuItem("Windows ile başlat") { Checked = IsAutoStart(), CheckOnClick = true };
        auto.CheckedChanged += (_, _) => SetAutoStart(auto.Checked);
        var clear = new WF.ToolStripMenuItem("Sabit olmayanları temizle", null, (_, _) =>
        {
            int n = _store.ClearUnpinned();
            Renumber();
            _tray!.ShowBalloonTip(2000, "Geçmiş temizlendi", $"{n} kayıt silindi. Sabitlenenler duruyor.", WF.ToolTipIcon.None);
        });
        var folder = new WF.ToolStripMenuItem("Veri klasörünü aç", null, (_, _) =>
        {
            try { System.Diagnostics.Process.Start("explorer.exe", $"\"{ClipStore.Dir}\""); } catch { }
        });
        var exit = new WF.ToolStripMenuItem("Çıkış", null, (_, _) => ExitApp());

        menu.Items.AddRange(new WF.ToolStripItem[]
        {
            _openItem, new WF.ToolStripSeparator(), pause, auto, new WF.ToolStripSeparator(), clear, folder, new WF.ToolStripSeparator(), exit,
        });

        _tray = new WF.NotifyIcon
        {
            Icon = icon,
            Text = "Pano",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == WF.MouseButtons.Left) ShowPopup(fromTray: true); };
        if (_hotkeyText == "")
            _tray.ShowBalloonTip(6000, "Kısayol ayarlanamadı",
                "Ctrl+Alt+V başka bir program tarafından kullanılıyor. Pano'yu tepsi simgesine tıklayarak açabilirsin.", WF.ToolTipIcon.Warning);
    }

    static bool IsAutoStart()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("Pano") is string; }
        catch { return false; }
    }

    static void SetAutoStart(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue("Pano", $"\"{Environment.ProcessPath}\" --hidden");
            else k.DeleteValue("Pano", false);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    void ExitApp()
    {
        _store.SaveNow();
        Native.UnregisterHotKey(_hwnd, 1);
        Native.RemoveClipboardFormatListener(_hwnd);
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Application.Current.Shutdown();
    }
}

sealed class DarkMenuRenderer : WF.ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

    protected override void OnRenderItemText(WF.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? SD.Color.FromArgb(236, 238, 244) : SD.Color.FromArgb(120, 128, 145);
        if (e.Text == (e.Item as WF.ToolStripMenuItem)?.ShortcutKeyDisplayString) e.TextColor = SD.Color.FromArgb(141, 149, 168);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(WF.ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        var r = e.ImageRectangle;
        using var pen = new SD.Pen(SD.Color.FromArgb(52, 211, 153), 2f);
        g.DrawLines(pen, new[]
        {
            new SD.PointF(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.52f),
            new SD.PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.74f),
            new SD.PointF(r.Left + r.Width * 0.82f, r.Top + r.Height * 0.28f),
        });
    }

    sealed class DarkColors : WF.ProfessionalColorTable
    {
        static readonly SD.Color Bg = SD.Color.FromArgb(24, 29, 41);
        static readonly SD.Color Sel = SD.Color.FromArgb(32, 42, 58);
        static readonly SD.Color Line = SD.Color.FromArgb(38, 45, 61);
        public override SD.Color ToolStripDropDownBackground => Bg;
        public override SD.Color ImageMarginGradientBegin => Bg;
        public override SD.Color ImageMarginGradientMiddle => Bg;
        public override SD.Color ImageMarginGradientEnd => Bg;
        public override SD.Color MenuBorder => Line;
        public override SD.Color MenuItemBorder => Sel;
        public override SD.Color MenuItemSelected => Sel;
        public override SD.Color SeparatorDark => Line;
        public override SD.Color SeparatorLight => Line;
        public override SD.Color CheckBackground => Bg;
        public override SD.Color CheckSelectedBackground => Sel;
        public override SD.Color CheckPressedBackground => Sel;
    }
}
