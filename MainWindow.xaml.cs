using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Media.Control;
using Windows.Storage.Streams;
using WMedia = Windows.Media;
using System.IO;

namespace TaskbarPlayer;

/// <summary>
/// Виджет плеера для панели задач. Берёт данные из системных медиа-сессий Windows (SMTC),
/// поэтому работает с чем угодно, что там публикуется: Яндекс Музыка, Spotify, YouTube в браузере и т.д.
/// Ничего не опрашивает постоянно: всё на событиях, таймер прогресса работает только в
/// расширенном режиме и пока играет музыка.
/// </summary>
public partial class MainWindow : Window
{
    // ---------- Win32 ----------
    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr h, ref WindowCompositionAttributeData data);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [StructLayout(LayoutKind.Sequential)]
    struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AccentPolicy { public int AccentState; public int AccentFlags; public int GradientColor; public int AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfData; }

    /// <summary>
    /// Размытие фона под виджетом (эффект «жидкого стекла»). Если на вашей версии Windows
    /// выглядит плохо (рваные края, нет размытия), поставьте false: останется полупрозрачное стекло без блюра.
    /// </summary>
    const bool UseBlur = true;

    // ---------- Кисти ----------
    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
    static readonly Brush Accent = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));            // активно: чисто белый
    static readonly Brush Normal = Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)));      // неактивно: 60% белого

    // ---------- Состояние ----------
    readonly IntPtr _hwnd;
    readonly UIElement[] _expandedOnly;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer _zOrder = new() { Interval = TimeSpan.FromSeconds(2) };

    GlobalSystemMediaTransportControlsSessionManager? _mgr;
    GlobalSystemMediaTransportControlsSession? _session;

    bool _expanded, _playing, _shuffleOk, _repeatOk;
    double _rate = 1, _top;
    TimeSpan _pos, _dur;
    DateTimeOffset _stamp = DateTimeOffset.UtcNow;
    string _lastKey = "";
    DateTime _lastWheel = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();

        // Окно без фокуса и не в Alt+Tab. Handle создаём заранее, но окно не показываем,
        // пока нет ни одной медиа-сессии.
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

        if (UseBlur)
        {
            EnableBlur();
            int preference = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(_hwnd, 33, ref preference, sizeof(int));
        }

        _expandedOnly = new UIElement[] { ShuffleBtn, RepeatBtn, ProgressHost, TimeText };

        _tick.Tick += (_, _) => UpdateProgress();
        // Панель задач Windows 11 иногда «перебивает» topmost: раз в пару секунд возвращаем окно наверх.
        _zOrder.Tick += (_, _) => SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);

        LocationChanged += (_, _) => { if (Math.Abs(Top - _top) > 0.5) Top = _top; }; // двигаем только по горизонтали
        ProgressTrack.SizeChanged += (_, _) => UpdateProgress();
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.InvokeAsync(PlaceWindow);

        _ = InitAsync();
    }

    // =====================================================================
    //  Подключение к системным медиа-сессиям
    // =====================================================================
    async Task InitAsync()
    {
        GlobalSystemMediaTransportControlsSessionManager m;
        try { m = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
        catch { return; }
        _mgr = m;

        m.CurrentSessionChanged += (_, _) =>
            Dispatcher.InvokeAsync(() => Attach(m.GetCurrentSession()));

        m.SessionsChanged += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            // если наш источник закрылся: переключаемся на текущий системный
            var alive = _session is not null &&
                        m.GetSessions().Any(x => x.SourceAppUserModelId == _session.SourceAppUserModelId);
            if (!alive) Attach(m.GetCurrentSession());
        });

        Attach(m.GetCurrentSession());
    }

    void Attach(GlobalSystemMediaTransportControlsSession? s)
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMedia;
            _session.PlaybackInfoChanged -= OnPlayback;
            _session.TimelinePropertiesChanged -= OnTimeline;
        }

        _session = s;
        _lastKey = "";

        if (s is null)
        {
            _tick.Stop();
            _zOrder.Stop();
            Hide();
            return;
        }

        s.MediaPropertiesChanged += OnMedia;
        s.PlaybackInfoChanged += OnPlayback;
        s.TimelinePropertiesChanged += OnTimeline;

        if (!IsVisible)
        {
            PlaceWindow();
            Show();
            _zOrder.Start();
        }

        _ = RefreshMediaAsync();
        RefreshPlayback();
    }

    void OnMedia(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) =>
        Dispatcher.InvokeAsync(() => RefreshMediaAsync());

    void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) =>
        Dispatcher.InvokeAsync(RefreshPlayback);

    void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) =>
        Dispatcher.InvokeAsync(RefreshTimeline);

    // =====================================================================
    //  Обновление интерфейса
    // =====================================================================
    async Task RefreshMediaAsync()
    {
        var s = _session;
        if (s is null) return;

        GlobalSystemMediaTransportControlsSessionMediaProperties p;
        try { p = await s.TryGetMediaPropertiesAsync(); }
        catch { return; }
        if (!ReferenceEquals(s, _session)) return;

        TitleText.Text = string.IsNullOrWhiteSpace(p.Title) ? "Без названия" : p.Title;
        var artist = string.IsNullOrWhiteSpace(p.Artist) ? p.AlbumArtist : p.Artist;
        ArtistText.Text = artist ?? "";
        ArtistText.Visibility = string.IsNullOrWhiteSpace(artist) ? Visibility.Collapsed : Visibility.Visible;

        var key = $"{p.Title}|{p.Artist}|{p.AlbumTitle}";
        if (key != _lastKey || CoverBrush.ImageSource is null)
        {
            _lastKey = key;
            await LoadCoverAsync(p.Thumbnail);
        }

        RefreshTimeline(); // при смене трека позиция сбрасывается
    }

    async Task LoadCoverAsync(IRandomAccessStreamReference? thumb)
    {
        if (thumb is null) { CoverBrush.ImageSource = null; return; }
        try
        {
            using var rs = await thumb.OpenReadAsync();
            using var ms = new MemoryStream();
            await rs.AsStreamForRead().CopyToAsync(ms);
            ms.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 96; // обложка 28 px: нет смысла держать большую в памяти
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            CoverBrush.ImageSource = bmp;
        }
        catch { CoverBrush.ImageSource = null; }
    }

    void RefreshPlayback()
    {
        var s = _session;
        if (s is null) return;

        GlobalSystemMediaTransportControlsSessionPlaybackInfo info;
        try { info = s.GetPlaybackInfo(); }
        catch { return; }

        var c = info.Controls;
        _playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        _rate = info.PlaybackRate ?? 1.0;

        PlayBtn.Content = _playing ? "\uE769" : "\uE768";
        PlayBtn.IsEnabled = c.IsPlayPauseToggleEnabled || c.IsPlayEnabled || c.IsPauseEnabled;
        PrevBtn.IsEnabled = c.IsPreviousEnabled;
        NextBtn.IsEnabled = c.IsNextEnabled;

        // Шафл и повтор есть не у всех источников: если плеер не отдаёт их Windows, кнопки скрываем
        _shuffleOk = c.IsShuffleEnabled;
        _repeatOk = c.IsRepeatEnabled;
        ShuffleBtn.Visibility = _expanded && _shuffleOk ? Visibility.Visible : Visibility.Collapsed;
        RepeatBtn.Visibility = _expanded && _repeatOk ? Visibility.Visible : Visibility.Collapsed;

        ShuffleBtn.Foreground = info.IsShuffleActive == true ? Accent : Normal;

        RepeatBtn.IsEnabled = c.IsRepeatEnabled;
        var rm = info.AutoRepeatMode;
        RepeatBtn.Content = rm == WMedia.MediaPlaybackAutoRepeatMode.Track ? "\uE8ED" : "\uE8EE";
        RepeatBtn.Foreground = rm is null or WMedia.MediaPlaybackAutoRepeatMode.None ? Normal : Accent;

        Cover.ToolTip = FriendlyName(s.SourceAppUserModelId);

        RefreshTimeline();
        UpdateTickState();
    }

    void RefreshTimeline()
    {
        var s = _session;
        if (s is null) return;
        try
        {
            var t = s.GetTimelineProperties();
            _dur = t.EndTime - t.StartTime;
            _pos = t.Position;
            // некоторые источники не заполняют LastUpdatedTime: тогда считаем «от сейчас»
            _stamp = t.LastUpdatedTime.Year < 2000 ? DateTimeOffset.UtcNow : t.LastUpdatedTime;
        }
        catch { }
        UpdateProgress();
    }

    void UpdateProgress()
    {
        if (!_expanded) return;

        var dur = _dur.TotalSeconds;
        var pos = _pos.TotalSeconds;
        if (_playing) pos += (DateTimeOffset.UtcNow - _stamp).TotalSeconds * _rate;
        pos = dur > 0 ? Math.Clamp(pos, 0, dur) : Math.Max(0, pos);

        var ratio = dur > 0 ? pos / dur : 0;
        var w = ProgressTrack.ActualWidth;
        ProgressFill.Width = w * ratio;
        TimeText.Text = dur > 0 ? $"{Fmt(pos)} / {Fmt(dur)}" : Fmt(pos);
    }

    void UpdateTickState()
    {
        if (_expanded && _playing && IsVisible) _tick.Start();
        else _tick.Stop();
    }

    static string Fmt(double sec) =>
        TimeSpan.FromSeconds(sec).ToString(sec >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    static string FriendlyName(string id)
    {
        var l = id.ToLowerInvariant();
        if (l.Contains("spotify")) return "Spotify";
        if (l.Contains("yandex") && l.Contains("music")) return "Яндекс Музыка";
        if (l.Contains("yandex")) return "Яндекс Браузер";
        if (l.Contains("msedge")) return "Microsoft Edge";
        if (l.Contains("chrome")) return "Chrome";
        if (l.Contains("firefox")) return "Firefox";
        if (l.Contains("opera")) return "Opera";
        return id.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
    }

    // =====================================================================
    //  Управление
    // =====================================================================
    async void Do(Func<GlobalSystemMediaTransportControlsSession, Task> act)
    {
        var s = _session;
        if (s is null) return;
        try { await act(s); } catch { }
    }

    void Prev_Click(object sender, RoutedEventArgs e) => Do(async s => { await s.TrySkipPreviousAsync(); });
    void Next_Click(object sender, RoutedEventArgs e) => Do(async s => { await s.TrySkipNextAsync(); });
    void Play_Click(object sender, RoutedEventArgs e) => Do(async s => { await s.TryTogglePlayPauseAsync(); });

    void Shuffle_Click(object sender, RoutedEventArgs e) => Do(async s =>
    {
        var on = s.GetPlaybackInfo().IsShuffleActive ?? false;
        await s.TryChangeShuffleActiveAsync(!on);
    });

    void Repeat_Click(object sender, RoutedEventArgs e) => Do(async s =>
    {
        var cur = s.GetPlaybackInfo().AutoRepeatMode ?? WMedia.MediaPlaybackAutoRepeatMode.None;
        var next = cur switch
        {
            WMedia.MediaPlaybackAutoRepeatMode.None => WMedia.MediaPlaybackAutoRepeatMode.List,
            WMedia.MediaPlaybackAutoRepeatMode.List => WMedia.MediaPlaybackAutoRepeatMode.Track,
            _ => WMedia.MediaPlaybackAutoRepeatMode.None,
        };
        await s.TryChangeAutoRepeatModeAsync(next);
    });

    // Колесо мыши над виджетом: вниз = следующий трек, вверх = предыдущий
    void Window_Wheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if ((DateTime.UtcNow - _lastWheel).TotalMilliseconds < 350) return;
        _lastWheel = DateTime.UtcNow;

        if (e.Delta < 0) Do(async s => { await s.TrySkipNextAsync(); });
        else Do(async s => { await s.TrySkipPreviousAsync(); });
    }

    // Клик по полосе прогресса = перемотка
    void Progress_Down(object sender, MouseButtonEventArgs e)
    {
        if (_dur <= TimeSpan.Zero || ProgressTrack.ActualWidth <= 0) return;
        var r = Math.Clamp(e.GetPosition(ProgressTrack).X / ProgressTrack.ActualWidth, 0, 1);
        var target = TimeSpan.FromSeconds(_dur.TotalSeconds * r);

        _pos = target; _stamp = DateTimeOffset.UtcNow; // сразу двигаем полосу, не ждём ответа источника
        UpdateProgress();
        Do(async s => { await s.TryChangePlaybackPositionAsync(target.Ticks); });
    }

    // =====================================================================
    //  Свернуть / развернуть, подсветка обложки
    // =====================================================================
    void Cover_Enter(object sender, MouseEventArgs e) => CoverOverlay.Opacity = 1;
    void Cover_Leave(object sender, MouseEventArgs e) => CoverOverlay.Opacity = 0;

    void Cover_Click(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control) return;
        SetExpanded(!_expanded);
    }

    void SetExpanded(bool on)
    {
        _expanded = on;
        foreach (var el in _expandedOnly)
        {
            var show = on
                && !(ReferenceEquals(el, ShuffleBtn) && !_shuffleOk)
                && !(ReferenceEquals(el, RepeatBtn) && !_repeatOk);
            el.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show)
            {
                // плавное появление дополнительных элементов
                el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }
        }
        UpdateTickState();
        RefreshTimeline();
        Dispatcher.InvokeAsync(UpdateProgress, DispatcherPriority.Loaded);
    }

    void EnableBlur()
    {
        var accent = new AccentPolicy
        {
            AccentState = 3,
            AccentFlags = 2,
            GradientColor = 0
        };
    }

        static string PosFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TaskbarPlayer", "left.txt");

    static double LoadLeft()
    {
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(PosFile);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }
        }
        catch { }
        return 16;
    }

    void SaveLeft()
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PosFile)!);
                System.IO.File.WriteAllText(PosFile, Left.ToString(CultureInfo.InvariantCulture));
            }
            catch { }
        }

        void PlaceWindow()
        {
            var wa = SystemParameters.WorkArea;
            var sh = SystemParameters.PrimaryScreenHeight;

            if (wa.Top > 1) _top = (wa.Top - Height) / 2;
            else if (wa.Bottom < sh - 1) _top = wa.Bottom + (sh - wa.Bottom - Height) / 2;
            else _top = sh - Height - 8;

            Top = _top;
            Left = Math.Clamp(LoadLeft(), 0, Math.Max(0, SystemParameters.PrimaryScreenWidth - 200));
        }

        void Root_Down(object sender, MouseButtonEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
                SaveLeft();
            }
        }

        // =====================================================================
        //  Контекстное меню
        // =====================================================================
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static bool IsAutostart()
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("TaskbarPlayer") is not null;
        }

        static void SetAutostart(bool on)
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k is null) return;
            if (on) k.SetValue("TaskbarPlayer", $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue("TaskbarPlayer", false);
        }

        void Root_RightUp(object sender, MouseButtonEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = Root, Placement = PlacementMode.MousePoint };

            var sessions = _mgr is null
                ? Array.Empty<GlobalSystemMediaTransportControlsSession>()
                : _mgr.GetSessions().ToArray();

            if (sessions.Length == 0)
            {
                menu.Items.Add(new MenuItem { Header = "Нет активных источников", IsEnabled = false });
            }
            else
            {
                foreach (var sess in sessions)
                {
                    var item = new MenuItem
                    {
                        Header = FriendlyName(sess.SourceAppUserModelId),
                        IsChecked = sess.SourceAppUserModelId == _session?.SourceAppUserModelId,
                    };
                    var captured = sess;
                    item.Click += (_, _) => Attach(captured);
                    menu.Items.Add(item);
                }
            }

            menu.Items.Add(new Separator());

            var auto = new MenuItem { Header = "Запускать вместе с Windows", IsCheckable = true, IsChecked = IsAutostart() };
            auto.Click += (_, _) => SetAutostart(auto.IsChecked);
            menu.Items.Add(auto);

            var exit = new MenuItem { Header = "Выход" };
            exit.Click += (_, _) => Application.Current.Shutdown();
            menu.Items.Add(exit);

            menu.IsOpen = true;
        }
}