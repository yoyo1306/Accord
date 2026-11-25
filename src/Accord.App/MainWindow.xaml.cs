using System.Diagnostics;
using System.Runtime.InteropServices;
using Accord.Core;
using Accord.Core.Audio;
using Accord.Core.Hardware;
using Accord.Core.Ipc;
using Accord.Core.Models;
using Accord.Core.Storage;
using Accord.Core.Switching;
using Accord.Core.SystemIntegration;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;
using Path = System.IO.Path;

namespace Accord.App;

public sealed partial class MainWindow : Window
{
    private readonly AdminClient _adminClient = new();
    private readonly AudioSwitcherEngine _switcherEngine = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _statusTimer;

    private AppSettings _settings = new();
    private List<AudioDevice> _allDevices = new();
    private string _activeFlow = "Render";
    private string? _selectedDeviceKey;
    private string? _pressedDeviceKey;
    private bool _suppressUiEvents;
    private bool _isBusy;
    private bool _isRefreshingLive;

    private WatcherStatus? _latestLiveStatus;
    private HeadsetPresence? _latestPresence;
    private IntPtr _hwnd;
    private WndProcDelegate? _newWndProc;
    private IntPtr _oldWndProc;

    public MainWindow()
    {
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        ConfigureWindowAndTitleBar();
        InstallMinSizeHook();

        _settings = SettingsStore.Load();
        ApplySavedTheme(_settings.Theme);

        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromMilliseconds(2500);
        _statusTimer.Tick += (_, _) => _ = RefreshLiveBannerAsync();

        RootGrid.Loaded += RootGrid_Loaded;
        RootGrid.ActualThemeChanged += (_, _) => ApplyThemeBrushes();
        TitleBarGrid.SizeChanged += (_, _) => UpdateTitleBarRegions();
        TitleBarControlsPanel.SizeChanged += (_, _) => UpdateTitleBarRegions();
        Closed += MainWindow_Closed;
    }

    private void ConfigureWindowAndTitleBar()
    {
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }
        else
        {
            ExtendsContentIntoTitleBar = true;
        }

        string icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "accord.ico");
        if (File.Exists(icoPath))
        {
            AppWindow.SetIcon(icoPath);
        }

        string pngPath = Path.Combine(AppContext.BaseDirectory, "Assets", "accord.png");
        if (File.Exists(pngPath))
        {
            TitleBarIcon.Source = new BitmapImage(new Uri(pngPath));
        }

        // Size & center cleanly on the current monitor work area
        DisplayArea displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        uint dpi = GetDpiForWindow(_hwnd);
        double scale = dpi > 0 ? dpi / 96.0 : 1.0;

        int targetW = (int)Math.Round(1140 * scale);
        int targetH = (int)Math.Round(740 * scale);
        int maxW = Math.Max(800, displayArea.WorkArea.Width - 48);
        int maxH = Math.Max(600, displayArea.WorkArea.Height - 48);
        int w = Math.Min(targetW, maxW);
        int h = Math.Min(targetH, maxH);

        int x = displayArea.WorkArea.X + (displayArea.WorkArea.Width - w) / 2;
        int y = displayArea.WorkArea.Y + (displayArea.WorkArea.Height - h) / 2;
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (RootGrid.XamlRoot is not null)
        {
            RootGrid.XamlRoot.Changed += (_, _) => UpdateTitleBarRegions();
        }

        _suppressUiEvents = true;
        try
        {
            FilterActiveButton.IsChecked = _settings.ShowActive;
            FilterDisabledButton.IsChecked = _settings.ShowDisabled;
            FilterDisconnectedButton.IsChecked = _settings.ShowDisconnected;

            SelectComboByTag(ThemeComboBox, _settings.Theme);
            SelectComboByTag(PollIntervalComboBox, _settings.PollIntervalSeconds.ToString());

            bool autoSwitchOn = StartupManager.IsAutoSwitchEnabled();
            BannerAutoSwitchToggle.IsOn = autoSwitchOn;
            OptionsAutoSwitchToggle.IsOn = autoSwitchOn;

            bool bootRestoreOn = ScheduledTaskManager.IsBootRestoreEnabled();
            BootRestoreToggle.IsOn = bootRestoreOn;

            StorageModeTitle.Text = AppPaths.IsPortable ? "Stockage portable" : "Stockage partagé (ProgramData)";
            StoragePathText.Text = AppPaths.DataDir;
            ToolTipService.SetToolTip(StoragePathText, AppPaths.DataDir);
        }
        finally
        {
            _suppressUiEvents = false;
        }

        ApplyThemeBrushes();
        UpdateTitleBarRegions();
        ReloadDevices(keepSelection: false);
        if (ScheduledTaskManager.HasLegacyBootRestoreTask())
        {
            SetStatus("Ancienne tâche de démarrage v1 détectée : activez « Restaurer au démarrage » dans Options pour la migrer.", StatusKind.Warning);
        }
        _ = RefreshLiveBannerAsync();
        _statusTimer.Start();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _statusTimer.Stop();
        _adminClient.Dispose();
    }

    public void BringToForegroundFromBackground()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ShowWindow(_hwnd, SW_RESTORE);
            SetForegroundWindow(_hwnd);
            Activate();
        });
    }

    private void UpdateTitleBarRegions()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported() || RootGrid.XamlRoot is null) return;

        double scale = RootGrid.XamlRoot.RasterizationScale;
        if (scale <= 0) scale = 1.0;

        double rightInsetDip = AppWindow.TitleBar.RightInset / scale;
        if (rightInsetDip < 136) rightInsetDip = 140;
        double targetRightMargin = Math.Ceiling(rightInsetDip) + 8;
        if (Math.Abs(TitleBarControlsPanel.Margin.Right - targetRightMargin) > 0.5)
        {
            TitleBarControlsPanel.Margin = new Thickness(0, 0, targetRightMargin, 0);
        }

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (RootGrid.XamlRoot is null) return;
            double s = RootGrid.XamlRoot.RasterizationScale;
            if (s <= 0) s = 1.0;

            try
            {
                InputNonClientPointerSource nonClient = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);

                GeneralTransform dragTransform = TitleBarDragArea.TransformToVisual(null);
                Windows.Foundation.Rect dragBounds = dragTransform.TransformBounds(
                    new Windows.Foundation.Rect(0, 0, TitleBarDragArea.ActualWidth, TitleBarDragArea.ActualHeight));

                if (dragBounds.Width > 0 && dragBounds.Height > 0)
                {
                    var dragRect = new RectInt32(
                        (int)Math.Floor(dragBounds.X * s),
                        (int)Math.Floor(dragBounds.Y * s),
                        (int)Math.Ceiling(dragBounds.Width * s),
                        (int)Math.Ceiling(dragBounds.Height * s));
                    AppWindow.TitleBar.SetDragRectangles([dragRect]);
                    nonClient.SetRegionRects(NonClientRegionKind.Caption, [dragRect]);
                }

                GeneralTransform ctrlTransform = TitleBarControlsPanel.TransformToVisual(null);
                Windows.Foundation.Rect ctrlBounds = ctrlTransform.TransformBounds(
                    new Windows.Foundation.Rect(0, 0, TitleBarControlsPanel.ActualWidth, TitleBarControlsPanel.ActualHeight));

                if (ctrlBounds.Width > 0 && ctrlBounds.Height > 0)
                {
                    var ctrlRect = new RectInt32(
                        (int)Math.Floor(ctrlBounds.X * s),
                        (int)Math.Floor(ctrlBounds.Y * s),
                        (int)Math.Ceiling(ctrlBounds.Width * s),
                        (int)Math.Ceiling(ctrlBounds.Height * s));
                    nonClient.SetRegionRects(NonClientRegionKind.Passthrough, [ctrlRect]);
                }
            }
            catch
            {
                // Layout not finalized yet; subsequent SizeChanged will refresh regions.
            }
        });
    }

    // ==================== THEME MANAGEMENT ====================

    private void ApplySavedTheme(string theme)
    {
        RootGrid.RequestedTheme = theme switch
        {
            "Dark" => ElementTheme.Dark,
            "Light" => ElementTheme.Light,
            _ => ElementTheme.Default
        };
    }

    private void ApplyThemeBrushes()
    {
        ThemePalette p = ThemePalette.For(RootGrid.ActualTheme);

        // Title bar caption colors
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            AppWindow.TitleBar.ButtonForegroundColor = p.TitleBarButtonFg;
            AppWindow.TitleBar.ButtonHoverForegroundColor = p.TitleBarButtonHoverFg;
            AppWindow.TitleBar.ButtonHoverBackgroundColor = p.TitleBarButtonHoverBg;
            AppWindow.TitleBar.ButtonPressedBackgroundColor = p.TitleBarButtonPressedBg;
            AppWindow.TitleBar.ButtonInactiveForegroundColor = p.TitleBarButtonInactiveFg;
        }

        // Theme toggle glyph (Moon in light mode -> switch to Dark; Sun in dark mode -> switch to Light)
        ThemeToggleIcon.Glyph = p.IsDark ? "\uE706" : "\uE708";
        ToolTipService.SetToolTip(ThemeToggleButton, p.IsDark ? "Passer en mode clair" : "Passer en mode sombre");

        // Header pills & buttons
        VersionPill.Background = p.NeutralPillBackground;
        VersionPillText.Foreground = p.TextMuted;
        OptionsHeaderButton.Background = p.CardBackground;
        OptionsHeaderButton.BorderBrush = p.CardBorder;

        // Main cards
        SwitcherBannerCard.Background = p.CardBackground;
        SwitcherBannerCard.BorderBrush = p.CardBorder;
        DeviceCatalogCard.Background = p.CardBackground;
        DeviceCatalogCard.BorderBrush = p.CardBorder;
        InspectorCard.Background = p.CardBackground;
        InspectorCard.BorderBrush = p.CardBorder;

        FlowTabsContainer.Background = p.SubtleCardBackground;
        FlowTabsContainer.BorderBrush = p.CardBorder;

        // Secondary subtitles & muted text
        DefaultOutputSubtitle.Foreground = p.TextSecondary;
        FilteredCountHint.Foreground = p.TextMuted;
        InspectorEmptyHint.Foreground = p.TextSecondary;
        PreviewBox.Background = p.PreviewBackground;
        PreviewBox.BorderBrush = p.CardBorder;
        PreviewCaption.Foreground = p.TextMuted;
        DeviceMetaCard.Background = p.SubtleCardBackground;
        DeviceMetaCard.BorderBrush = p.CardBorder;
        DeviceGuidText.Foreground = p.TextMuted;

        // Filter dots
        FilterActiveDot.Fill = p.SuccessDot;
        FilterDisconnectedDot.Fill = p.WarningDot;
        FilterDisabledDot.Fill = p.NeutralDot;

        // Status bar
        StatusBarGrid.BorderBrush = p.CardBorder;
        StatusMessageText.Foreground = p.TextSecondary;
        TotalDevicesStatusText.Foreground = p.TextMuted;
        AdminSessionBadge.Background = p.AccentSoftBackground;

        // Options drawer
        OptionsBackdrop.Background = p.DrawerBackdrop;
        OptionsDrawerPanel.Background = p.DrawerSurface;
        OptionsDrawerPanel.BorderBrush = p.DrawerBorder;
        foreach (Border card in new[] { OptAppearanceCard, OptSwitcherCard, OptRestoreCard, OptLogsCard })
        {
            card.Background = p.CardBackground;
            card.BorderBrush = p.CardBorder;
        }
        OptProfileDivider.Background = p.CardBorder;
        LogsPreviewBox.Background = p.PreviewBackground;
        LogsPreviewBox.BorderBrush = p.CardBorder;
        LogsPreviewText.Foreground = p.TextSecondary;

        foreach (TextBlock lbl in new[] { OptSecAppearance, OptSecSwitcher, OptSecRestore, OptSecLogs })
        {
            lbl.Foreground = p.TextSecondary;
        }
        foreach (TextBlock desc in new[] { OptThemeDesc, StoragePathText, OptWatcherDesc, OptPollDesc, OptProfileSummaryDesc, OptBootRestoreDesc, BackupSummaryText })
        {
            desc.Foreground = p.TextMuted;
        }

        StyleFlowTabs(p);
        RenderLiveBannerUi(p);
        RenderDeviceList();
        UpdateInspectorTheme(p);
    }

    private void StyleFlowTabs(ThemePalette p)
    {
        bool playActive = _activeFlow == "Render";
        PlaybackTabButton.Background = playActive ? p.CardBackground : new SolidColorBrush(Colors.Transparent);
        RecordingTabButton.Background = !playActive ? p.CardBackground : new SolidColorBrush(Colors.Transparent);

        PlaybackCountBadge.Background = playActive ? p.AccentSoftBackground : p.NeutralPillBackground;
        PlaybackCountText.Foreground = playActive ? p.AccentText : p.NeutralPillForeground;

        RecordingCountBadge.Background = !playActive ? p.AccentSoftBackground : p.NeutralPillBackground;
        RecordingCountText.Foreground = !playActive ? p.AccentText : p.NeutralPillForeground;
    }

    private void UpdateInspectorTheme(ThemePalette p)
    {
        AudioDevice? selected = GetSelectedDevice();
        if (selected is null) return;

        InspectorIconTile.Background = selected.IsDefault ? p.IconTileSelectedBackground : p.IconTileBackground;
        InspectorDeviceIcon.Foreground = selected.IsDefault ? p.AccentText : p.TextPrimary;

        InspectorFlowPill.Background = p.NeutralPillBackground;
        InspectorFlowText.Foreground = p.NeutralPillForeground;

        ApplyStatePillStyle(InspectorStatePill, InspectorStateText, selected.State, p);

        BackupStatusIcon.Foreground = selected.HasBackup ? p.SuccessForeground : p.TextMuted;
        BackupStatusText.Foreground = selected.HasBackup ? p.TextPrimary : p.TextSecondary;
    }

    private static void ApplyStatePillStyle(Border pill, TextBlock text, int state, ThemePalette p)
    {
        switch (state)
        {
            case AudioDevice.StateActive:
                pill.Background = p.SuccessBackground;
                text.Foreground = p.SuccessForeground;
                break;
            case AudioDevice.StateUnplugged:
                pill.Background = p.WarningBackground;
                text.Foreground = p.WarningForeground;
                break;
            default:
                pill.Background = p.NeutralPillBackground;
                text.Foreground = p.NeutralPillForeground;
                break;
        }
    }

    // ==================== DEVICE LOADING & LIST RENDERING ====================

    private void ReloadDevices(bool keepSelection = true)
    {
        string? prevKey = keepSelection ? _selectedDeviceKey : null;
        try
        {
            _allDevices = AudioRegistryService.LoadAllDevices();
        }
        catch (Exception ex)
        {
            SetStatus($"Erreur de lecture audio : {ex.Message}", StatusKind.Error);
            return;
        }

        int playCount = _allDevices.Count(d => d.Flow == "Render");
        int recCount = _allDevices.Count(d => d.Flow == "Capture");
        PlaybackCountText.Text = playCount.ToString();
        RecordingCountText.Text = recCount.ToString();

        TotalDevicesStatusText.Text = $"{_allDevices.Count} appareils lus";
        UpdateBackupSummary();
        PopulateProfileTargetCombos();

        _selectedDeviceKey = prevKey;
        RenderDeviceList();
        LoadSelectedDeviceIntoInspector();
        UpdateAdminSessionBadge();
    }

    private List<AudioDevice> GetFilteredDevices()
    {
        bool showActive = FilterActiveButton.IsChecked == true;
        bool showDisabled = FilterDisabledButton.IsChecked == true;
        bool showDisconnected = FilterDisconnectedButton.IsChecked == true;
        string query = SearchBox.Text?.Trim() ?? string.Empty;

        var filtered = new List<AudioDevice>();
        foreach (AudioDevice d in _allDevices)
        {
            if (d.Flow != _activeFlow) continue;
            bool stateMatch =
                (d.IsActive && showActive) ||
                (d.IsDisabled && showDisabled) ||
                (d.IsDisconnected && showDisconnected);
            if (!stateMatch) continue;

            if (!string.IsNullOrEmpty(query) &&
                !d.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !d.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            filtered.Add(d);
        }
        return filtered;
    }

    private void RenderDeviceList()
    {
        ThemePalette p = ThemePalette.For(RootGrid.ActualTheme);
        List<AudioDevice> items = GetFilteredDevices();

        FilteredCountHint.Text = items.Count <= 1 ? $"{items.Count} affiché" : $"{items.Count} affichés";
        DeviceListPanel.Children.Clear();

        if (items.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "Aucun périphérique ne correspond aux filtres actifs.",
                Foreground = p.TextMuted,
                FontSize = 13,
                Margin = new Thickness(8, 24, 8, 24),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            DeviceListPanel.Children.Add(empty);
            return;
        }

        // Keep selection valid within active view if possible
        if (string.IsNullOrEmpty(_selectedDeviceKey) || !items.Any(d => DeviceKey(d) == _selectedDeviceKey))
        {
            AudioDevice firstPreferred = items.FirstOrDefault(d => d.IsDefault) ?? items[0];
            _selectedDeviceKey = DeviceKey(firstPreferred);
        }

        foreach (AudioDevice device in items)
        {
            string key = DeviceKey(device);
            bool isSelected = key == _selectedDeviceKey;

            var card = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10, 12, 10),
                Background = isSelected ? p.ItemSelectedBackground : p.ItemBackground,
                BorderBrush = isSelected ? p.ItemSelectedBorder : p.CardBorder,
                Tag = key
            };

            card.PointerEntered += (_, _) =>
            {
                if (_selectedDeviceKey != key)
                {
                    card.Background = ThemePalette.For(RootGrid.ActualTheme).ItemHoverBackground;
                }
            };
            card.PointerExited += (_, _) =>
            {
                ThemePalette cur = ThemePalette.For(RootGrid.ActualTheme);
                card.Background = _selectedDeviceKey == key ? cur.ItemSelectedBackground : cur.ItemBackground;
            };
            card.PointerPressed += (_, args) =>
            {
                if (args.GetCurrentPoint(card).Properties.IsLeftButtonPressed)
                {
                    _pressedDeviceKey = key;
                }
            };
            card.PointerReleased += (_, _) =>
            {
                if (_pressedDeviceKey == key)
                {
                    _pressedDeviceKey = null;
                    if (_selectedDeviceKey != key)
                    {
                        _selectedDeviceKey = key;
                        RenderDeviceList();
                        LoadSelectedDeviceIntoInspector();
                    }
                }
            };

            var rowGrid = new Grid { ColumnSpacing = 12 };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Left icon tile
            var iconTile = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(6),
                Background = isSelected || device.IsDefault ? p.IconTileSelectedBackground : p.IconTileBackground,
                VerticalAlignment = VerticalAlignment.Center
            };
            iconTile.Child = new FontIcon
            {
                Glyph = GetDeviceGlyph(device),
                FontSize = 15,
                Foreground = isSelected || device.IsDefault ? p.AccentText : p.TextPrimary
            };
            Grid.SetColumn(iconTile, 0);
            rowGrid.Children.Add(iconTile);

            // Center title + description
            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            textStack.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(device.Name) ? "(Sans nom)" : device.Name,
                FontSize = 13.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = p.TextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            textStack.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(device.Description) ? "Aucune description" : device.Description,
                FontSize = 12,
                Foreground = p.TextSecondary,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(textStack, 1);
            rowGrid.Children.Add(textStack);

            // Right badges
            var badges = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (device.IsDefault)
            {
                var defPill = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(7, 2, 7, 2),
                    Background = p.AccentSoftBackground,
                    BorderBrush = p.AccentSoftBorder,
                    BorderThickness = new Thickness(1)
                };
                defPill.Child = new TextBlock
                {
                    Text = "Par défaut",
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = p.AccentText
                };
                badges.Children.Add(defPill);
            }

            if (device.HasBackup)
            {
                var bkpPill = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    Background = p.NeutralPillBackground
                };
                var bkpStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                bkpStack.Children.Add(new FontIcon { Glyph = "\uE74E", FontSize = 10, Foreground = p.TextMuted });
                bkpStack.Children.Add(new TextBlock { Text = "Sauvé", FontSize = 11, Foreground = p.NeutralPillForeground });
                bkpPill.Child = bkpStack;
                badges.Children.Add(bkpPill);
            }

            var statePill = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 2, 8, 2),
                MinWidth = 68
            };
            var stateText = new TextBlock
            {
                Text = device.StateLabel,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            ApplyStatePillStyle(statePill, stateText, device.State, p);
            statePill.Child = stateText;
            badges.Children.Add(statePill);

            Grid.SetColumn(badges, 2);
            rowGrid.Children.Add(badges);

            card.Child = rowGrid;
            DeviceListPanel.Children.Add(card);
        }
    }

    private AudioDevice? GetSelectedDevice() =>
        string.IsNullOrEmpty(_selectedDeviceKey)
            ? null
            : _allDevices.FirstOrDefault(d => DeviceKey(d) == _selectedDeviceKey);

    private void LoadSelectedDeviceIntoInspector()
    {
        AudioDevice? device = GetSelectedDevice();
        if (device is null)
        {
            InspectorEmptyState.Visibility = Visibility.Visible;
            InspectorContentScroll.Visibility = Visibility.Collapsed;
            return;
        }

        InspectorEmptyState.Visibility = Visibility.Collapsed;
        InspectorContentScroll.Visibility = Visibility.Visible;

        _suppressUiEvents = true;
        try
        {
            InspectorDeviceIcon.Glyph = GetDeviceGlyph(device);
            InspectorHeaderTitle.Text = string.IsNullOrWhiteSpace(device.Name) ? "(Sans nom)" : device.Name;
            InspectorFlowText.Text = device.FlowLabel;
            InspectorStateText.Text = device.StateLabel;

            NameTextBox.Text = device.Name;
            DescriptionTextBox.Text = device.Description;
            UpdateLivePreviewText();

            SetDefaultButton.IsEnabled = device.IsActive && !device.IsDefault && !_isBusy;
            SetDefaultButtonText.Text = device.IsDefault
                ? "Déjà défini par défaut"
                : "Définir comme périphérique par défaut";

            DeviceBackup? bkp = BackupStore.Find(device.Flow, device.Id, device.WaveId);
            device.HasBackup = bkp is not null;
            RestoreButton.IsEnabled = bkp is not null && !_isBusy;
            BackupButton.IsEnabled = !_isBusy;
            RenameButton.IsEnabled = !_isBusy;

            if (bkp is not null)
            {
                BackupStatusIcon.Glyph = "\uE73E";
                BackupStatusText.Text = $"Sauvegarde : {bkp.SavedDisplay}";
            }
            else
            {
                BackupStatusIcon.Glyph = "\uE946";
                BackupStatusText.Text = "Aucune sauvegarde enregistrée pour cet appareil.";
            }

            DeviceGuidText.Text = device.Id;
            ToolTipService.SetToolTip(DeviceGuidText, device.EndpointId);
            RenameShieldIcon.Visibility = _adminClient.IsSessionActive ? Visibility.Collapsed : Visibility.Visible;
        }
        finally
        {
            _suppressUiEvents = false;
        }

        UpdateInspectorTheme(ThemePalette.For(RootGrid.ActualTheme));
    }

    private void UpdateLivePreviewText()
    {
        string n = NameTextBox.Text?.Trim() ?? string.Empty;
        string d = DescriptionTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(n) && string.IsNullOrEmpty(d))
        {
            PreviewFullText.Text = "Indiquez un nom ci-dessus";
        }
        else if (string.IsNullOrEmpty(d))
        {
            PreviewFullText.Text = n;
        }
        else
        {
            PreviewFullText.Text = $"{n} ({d})";
        }
    }

    // ==================== LIVE BASCULE BANNER ====================

    private async Task RefreshLiveBannerAsync()
    {
        if (_isRefreshingLive) return;
        _isRefreshingLive = true;
        try
        {
            AppSettings snapshotSettings = SettingsStore.Load();
            (HeadsetPresence presence, WatcherStatus? fileStatus, bool watcherRunning) = await Task.Run(() =>
            {
                WatcherStatus? ws = SettingsStore.LoadWatcherStatus();
                bool running = StartupManager.IsWatcherRunning();
                HeadsetPresence pres = _switcherEngine.EvaluatePresence(snapshotSettings);
                return (pres, ws, running);
            });

            _latestPresence = presence;

            bool manual = !string.IsNullOrWhiteSpace(snapshotSettings.ManualProfileOverride);
            string activeProfile = manual
                ? snapshotSettings.ManualProfileOverride
                : (watcherRunning && fileStatus is not null ? fileStatus.ActiveProfile : DetectActiveProfileFromDefault(presence));

            LiveAudioEndpoint? defOut = null;
            try { defOut = AudioCoreCom.GetDefaultEndpoint(0, 1); } catch { }

            _latestLiveStatus = new WatcherStatus
            {
                ActiveProfile = activeProfile,
                ManualOverride = manual,
                PixelConnected = presence.PixelOn,
                RazerConnected = presence.RazerOn,
                RazerStateLabel = presence.RazerRawState switch
                {
                    RazerHeadsetState.Connected => "Connecté (2,4 GHz)",
                    RazerHeadsetState.Disconnected => "Éteint / déconnecté",
                    _ => "Dongle occupé"
                },
                PixelPlaybackName = presence.PixelPlay?.Name ?? string.Empty,
                RazerPlaybackName = presence.RazerPlay?.Name ?? string.Empty,
                ScreenPlaybackName = presence.Screen?.Name ?? string.Empty,
                DefaultPlaybackName = defOut?.Name ?? fileStatus?.DefaultPlaybackName ?? string.Empty,
                WatcherPid = watcherRunning ? (fileStatus?.WatcherPid ?? 1) : 0
            };

            RenderLiveBannerUi(ThemePalette.For(RootGrid.ActualTheme));
        }
        catch
        {
            // Keep last valid state on transient audio restart.
        }
        finally
        {
            _isRefreshingLive = false;
        }
    }

    private static string DetectActiveProfileFromDefault(HeadsetPresence p)
    {
        try
        {
            string? defId = AudioCoreCom.GetDefaultEndpoint(0, 1)?.Id;
            if (!string.IsNullOrEmpty(defId))
            {
                if (p.PixelPlay is not null && string.Equals(p.PixelPlay.Id, defId, StringComparison.OrdinalIgnoreCase)) return "Pixel";
                if (p.RazerPlay is not null && string.Equals(p.RazerPlay.Id, defId, StringComparison.OrdinalIgnoreCase)) return "Razer";
                if (p.Screen is not null && string.Equals(p.Screen.Id, defId, StringComparison.OrdinalIgnoreCase)) return "Screen";
            }
        }
        catch { }
        return AudioSwitcherEngine.ResolveStartupProfile(p);
    }

    private void RenderLiveBannerUi(ThemePalette p)
    {
        bool watcherRunning = StartupManager.IsWatcherRunning();
        WatcherStatus? s = _latestLiveStatus;

        if (watcherRunning)
        {
            WatcherStatePill.Background = p.SuccessBackground;
            WatcherStateDot.Fill = p.SuccessDot;
            WatcherStateText.Foreground = p.SuccessForeground;
            WatcherStateText.Text = s?.ManualOverride == true ? "Active (manuel)" : "Active";
        }
        else
        {
            WatcherStatePill.Background = p.NeutralPillBackground;
            WatcherStateDot.Fill = p.NeutralDot;
            WatcherStateText.Foreground = p.NeutralPillForeground;
            WatcherStateText.Text = "En pause";
        }

        string outName = string.IsNullOrWhiteSpace(s?.DefaultPlaybackName)
            ? "Sortie active : non détectée"
            : $"Sortie : {s.DefaultPlaybackName}";
        DefaultOutputSubtitle.Text = outName;
        ToolTipService.SetToolTip(DefaultOutputSubtitle, outName);

        ClearManualOverrideButton.Visibility = s?.ManualOverride == true ? Visibility.Visible : Visibility.Collapsed;

        string active = s?.ActiveProfile ?? "Screen";
        bool pixelOn = s?.PixelConnected == true;
        bool razerOn = s?.RazerConnected == true;

        PixelProfileSub.Text = pixelOn ? "Connecté" : (string.IsNullOrEmpty(s?.PixelPlaybackName) ? "Absent" : "Veille");
        RazerProfileSub.Text = razerOn ? "Connecté (2,4 GHz)" : (string.IsNullOrEmpty(s?.RazerPlaybackName) ? "Absent" : "Éteint");
        ScreenProfileSub.Text = string.IsNullOrEmpty(s?.ScreenPlaybackName) ? "Non trouvé" : "Disponible";

        PixelProfileDot.Fill = pixelOn ? p.SuccessDot : p.NeutralDot;
        RazerProfileDot.Fill = razerOn ? p.SuccessDot : p.NeutralDot;
        ScreenProfileDot.Fill = !string.IsNullOrEmpty(s?.ScreenPlaybackName) ? p.SuccessDot : p.NeutralDot;

        StyleProfileButton(PixelProfileButton, PixelProfileSub, active == "Pixel", p);
        StyleProfileButton(RazerProfileButton, RazerProfileSub, active == "Razer", p);
        StyleProfileButton(ScreenProfileButton, ScreenProfileSub, active == "Screen", p);
    }

    private static void StyleProfileButton(Button btn, TextBlock sub, bool isActive, ThemePalette p)
    {
        btn.Background = isActive ? p.AccentSoftBackground : p.SubtleCardBackground;
        btn.BorderBrush = isActive ? p.AccentSoftBorder : p.CardBorder;
        sub.Foreground = isActive ? p.AccentText : p.TextMuted;
    }

    // ==================== USER ACTIONS ====================

    private void FlowTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string flow && flow != _activeFlow)
        {
            _activeFlow = flow;
            _selectedDeviceKey = null;
            StyleFlowTabs(ThemePalette.For(RootGrid.ActualTheme));
            RenderDeviceList();
            LoadSelectedDeviceIntoInspector();
        }
    }

    private void FilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        _settings.ShowActive = FilterActiveButton.IsChecked == true;
        _settings.ShowDisabled = FilterDisabledButton.IsChecked == true;
        _settings.ShowDisconnected = FilterDisconnectedButton.IsChecked == true;
        SettingsStore.Save(_settings);

        RenderDeviceList();
        LoadSelectedDeviceIntoInspector();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RenderDeviceList();
        LoadSelectedDeviceIntoInspector();
    }

    private void EditorTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressUiEvents) return;
        UpdateLivePreviewText();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        ReloadDevices(keepSelection: true);
        _ = RefreshLiveBannerAsync();
        SetStatus("Liste des périphériques actualisée.", StatusKind.Info);
    }

    private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        bool currentlyDark = RootGrid.ActualTheme == ElementTheme.Dark;
        string next = currentlyDark ? "Light" : "Dark";
        _settings.Theme = next;
        SettingsStore.Save(_settings);

        ApplySavedTheme(next);
        _suppressUiEvents = true;
        SelectComboByTag(ThemeComboBox, next);
        _suppressUiEvents = false;
        ApplyThemeBrushes();
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents) return;
        if (ThemeComboBox.SelectedItem is ComboBoxItem item && item.Tag is string theme)
        {
            _settings.Theme = theme;
            SettingsStore.Save(_settings);
            ApplySavedTheme(theme);
            ApplyThemeBrushes();
        }
    }

    private async void ProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string profile) return;

        try
        {
            HeadsetPresence presence = _latestPresence ?? await Task.Run(() => _switcherEngine.EvaluatePresence(_settings));
            bool applied = await Task.Run(() => _switcherEngine.ApplyProfile(profile, presence));
            if (!applied)
            {
                SetStatus($"Impossible d'activer le profil {profile} : périphérique audio absent ou inactif.", StatusKind.Warning);
                return;
            }

            _settings.ManualProfileOverride = profile;
            _settings.ManualOverrideStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            SettingsStore.Save(_settings);

            SetStatus($"Profil audio basculé sur {FormatProfileLabel(profile)}.", StatusKind.Success);
            ReloadDevices(keepSelection: true);
            await RefreshLiveBannerAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Échec de bascule : {ex.Message}", StatusKind.Error);
        }
    }

    private async void ClearManualOverrideButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.ManualProfileOverride = string.Empty;
        _settings.ManualOverrideStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SettingsStore.Save(_settings);

        try
        {
            HeadsetPresence presence = await Task.Run(() => _switcherEngine.EvaluatePresence(_settings));
            string autoProfile = AudioSwitcherEngine.ResolveStartupProfile(presence);
            await Task.Run(() => _switcherEngine.ApplyProfile(autoProfile, presence));
            SetStatus($"Retour en mode automatique ({FormatProfileLabel(autoProfile)}).", StatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"Erreur retour auto : {ex.Message}", StatusKind.Error);
        }

        ReloadDevices(keepSelection: true);
        await RefreshLiveBannerAsync();
    }

    private async void SetDefaultButton_Click(object sender, RoutedEventArgs e)
    {
        AudioDevice? device = GetSelectedDevice();
        if (device is null || !device.IsActive) return;

        try
        {
            await Task.Run(() => AudioCoreCom.SetDefaultAllRoles(device.EndpointId));
            SetStatus($"Défini par défaut : {device.FullDisplayName}.", StatusKind.Success);
            ReloadDevices(keepSelection: true);
            await RefreshLiveBannerAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Impossible de définir par défaut : {ex.Message}", StatusKind.Error);
        }
    }

    private async void RenameButton_Click(object sender, RoutedEventArgs e)
    {
        AudioDevice? device = GetSelectedDevice();
        if (device is null) return;

        string newName = NameTextBox.Text?.Trim() ?? string.Empty;
        string newDesc = DescriptionTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(newName))
        {
            SetStatus("Indiquez un nom avant de renommer.", StatusKind.Warning);
            NameTextBox.Focus(FocusState.Programmatic);
            return;
        }

        SetBusy(true, $"Renommage de {device.Name} en cours (le son se coupe une seconde)...");
        try
        {
            AdminResponse resp = await _adminClient.ExecuteAsync(new AdminRequest
            {
                Action = "Rename",
                Flow = device.Flow,
                DeviceId = device.Id,
                Name = newName,
                Description = newDesc
            });

            if (resp.Success)
            {
                SetStatus($"{resp.Message}. Le son a été redémarré une seconde.", StatusKind.Success);
                ReloadDevices(keepSelection: true);
                await RefreshLiveBannerAsync();
            }
            else
            {
                SetStatus($"Échec du renommage : {resp.Message}", StatusKind.Error);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Échec du renommage : {ex.Message}", StatusKind.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void BackupButton_Click(object sender, RoutedEventArgs e)
    {
        AudioDevice? device = GetSelectedDevice();
        if (device is null) return;

        try
        {
            DeviceBackup snapshot = AudioRegistryService.ReadSnapshot(device.Flow, device.Id);
            BackupStore.Save(snapshot);
            SetStatus($"Sauvegarde enregistrée : {snapshot.SavedDisplay}.", StatusKind.Success);
            ReloadDevices(keepSelection: true);
        }
        catch (Exception ex)
        {
            SetStatus($"Échec de la sauvegarde : {ex.Message}", StatusKind.Error);
        }
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        AudioDevice? device = GetSelectedDevice();
        if (device is null) return;

        DeviceBackup? backup = BackupStore.Find(device.Flow, device.Id, device.WaveId);
        if (backup is null)
        {
            SetStatus("Aucune sauvegarde pour cet appareil.", StatusKind.Warning);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Restaurer le nom sauvegardé",
            Content = $"Restaurer {backup.SavedDisplay} pour ce périphérique ? Le service audio Windows redémarrera une seconde.",
            PrimaryButtonText = "Restaurer",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        SetBusy(true, $"Restauration de {backup.SavedDisplay}...");
        try
        {
            AdminResponse resp = await _adminClient.ExecuteAsync(new AdminRequest
            {
                Action = "RestoreSingle",
                Backup = backup
            });

            if (resp.Success)
            {
                SetStatus(resp.Message, StatusKind.Success);
                ReloadDevices(keepSelection: true);
                await RefreshLiveBannerAsync();
            }
            else
            {
                SetStatus($"Échec de la restauration : {resp.Message}", StatusKind.Error);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Échec de la restauration : {ex.Message}", StatusKind.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void CopyGuidButton_Click(object sender, RoutedEventArgs e)
    {
        AudioDevice? device = GetSelectedDevice();
        if (device is null) return;

        var dp = new DataPackage();
        dp.SetText(device.EndpointId);
        Clipboard.SetContent(dp);
        SetStatus($"Identifiant copié : {device.Id}", StatusKind.Info);
    }

    // ==================== OPTIONS DRAWER & SYSTEM TOGGLES ====================

    private void OptionsHeaderButton_Click(object sender, RoutedEventArgs e)
    {
        if (OptionsOverlay.Visibility == Visibility.Visible)
        {
            CloseOptionsDrawer();
        }
        else
        {
            OpenOptionsDrawer();
        }
    }

    private void CloseOptionsButton_Click(object sender, RoutedEventArgs e) => CloseOptionsDrawer();

    private void OptionsBackdrop_PointerReleased(object sender, PointerRoutedEventArgs e) => CloseOptionsDrawer();

    private void OpenOptionsDrawer()
    {
        RefreshLogsPreview();
        UpdateBackupSummary();
        OptionsOverlay.Visibility = Visibility.Visible;
    }

    private void CloseOptionsDrawer()
    {
        OptionsOverlay.Visibility = Visibility.Collapsed;
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && OptionsOverlay.Visibility == Visibility.Visible)
        {
            CloseOptionsDrawer();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.F5 && !_isBusy)
        {
            ReloadDevices(keepSelection: true);
            _ = RefreshLiveBannerAsync();
            e.Handled = true;
        }
    }

    private async void AutoSwitchToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        if (sender is not ToggleSwitch ts) return;

        bool enable = ts.IsOn;
        _suppressUiEvents = true;
        BannerAutoSwitchToggle.IsOn = enable;
        OptionsAutoSwitchToggle.IsOn = enable;
        _suppressUiEvents = false;

        try
        {
            await Task.Run(() =>
            {
                if (enable) StartupManager.EnableAutoSwitch();
                else StartupManager.DisableAutoSwitch();
            });

            SetStatus(
                enable ? "Bascule automatique activée (surveillance en cours)." : "Bascule automatique désactivée.",
                StatusKind.Success);
            await RefreshLiveBannerAsync();
        }
        catch (Exception ex)
        {
            _suppressUiEvents = true;
            BannerAutoSwitchToggle.IsOn = !enable;
            OptionsAutoSwitchToggle.IsOn = !enable;
            _suppressUiEvents = false;
            SetStatus($"La bascule automatique n'a pas pu être modifiée : {ex.Message}", StatusKind.Error);
        }
    }

    private async void BootRestoreToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        bool enable = BootRestoreToggle.IsOn;

        SetBusy(true, enable ? "Activation de la tâche AccordRestauration..." : "Suppression de la tâche AccordRestauration...");
        try
        {
            AdminResponse resp = await _adminClient.ExecuteAsync(new AdminRequest
            {
                Action = "SetBootRestore",
                Enabled = enable
            });

            if (resp.Success)
            {
                string extra = enable && BackupStore.Load().Count == 0
                    ? " Aucune sauvegarde pour l'instant : elle s'appliquera dès qu'il y en aura une."
                    : string.Empty;
                SetStatus(resp.Message + extra, StatusKind.Success);
            }
            else
            {
                _suppressUiEvents = true;
                BootRestoreToggle.IsOn = !enable;
                _suppressUiEvents = false;
                SetStatus(resp.Message, StatusKind.Error);
            }
        }
        catch (Exception ex)
        {
            _suppressUiEvents = true;
            BootRestoreToggle.IsOn = !enable;
            _suppressUiEvents = false;
            SetStatus($"Impossible de modifier la tâche de démarrage : {ex.Message}", StatusKind.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RestoreAllNowButton_Click(object sender, RoutedEventArgs e)
    {
        List<DeviceBackup> all = BackupStore.Load();
        if (all.Count == 0)
        {
            SetStatus("Aucune sauvegarde disponible à restaurer.", StatusKind.Warning);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Restaurer toutes les sauvegardes",
            Content = $"Restaurer les {all.Count} périphérique(s) sauvegardé(s) maintenant ? Le service audio Windows redémarrera une seconde.",
            PrimaryButtonText = "Tout restaurer",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        SetBusy(true, "Restauration complète des noms sauvegardés...");
        try
        {
            AdminResponse resp = await _adminClient.ExecuteAsync(new AdminRequest { Action = "RestoreAll" });
            SetStatus(resp.Message, resp.Success ? StatusKind.Success : StatusKind.Warning);
            ReloadDevices(keepSelection: true);
            RefreshLogsPreview();
            await RefreshLiveBannerAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Échec de la restauration globale : {ex.Message}", StatusKind.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void PollIntervalComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents) return;
        if (PollIntervalComboBox.SelectedItem is ComboBoxItem item &&
            item.Tag is string s &&
            int.TryParse(s, out int sec))
        {
            _settings.PollIntervalSeconds = Math.Clamp(sec, 1, 15);
            SettingsStore.Save(_settings);
            SetStatus($"Fréquence de vérification réglée sur {_settings.PollIntervalSeconds} s.", StatusKind.Info);
        }
    }

    private void PopulateProfileTargetCombos()
    {
        _suppressUiEvents = true;
        try
        {
            var playbackOptions = _allDevices.Where(d => d.Flow == "Render").ToList();
            var recordingOptions = _allDevices.Where(d => d.Flow == "Capture").ToList();

            FillTargetCombo(PixelPlayCombo, playbackOptions, _settings.Profiles.PixelPlaybackId);
            FillTargetCombo(PixelRecCombo, recordingOptions, _settings.Profiles.PixelRecordingId);
            FillTargetCombo(RazerPlayCombo, playbackOptions, _settings.Profiles.RazerPlaybackId);
            FillTargetCombo(RazerRecCombo, recordingOptions, _settings.Profiles.RazerRecordingId);
            FillTargetCombo(ScreenPlayCombo, playbackOptions, _settings.Profiles.ScreenPlaybackId);
        }
        finally
        {
            _suppressUiEvents = false;
        }
    }

    private static void FillTargetCombo(ComboBox combo, List<AudioDevice> candidates, string selectedEndpointId)
    {
        combo.Items.Clear();
        var autoItem = new ComboBoxItem { Content = "Auto (détection)", Tag = string.Empty };
        combo.Items.Add(autoItem);

        ComboBoxItem toSelect = autoItem;
        foreach (AudioDevice d in candidates)
        {
            var item = new ComboBoxItem
            {
                Content = d.FullDisplayName,
                Tag = d.EndpointId
            };
            combo.Items.Add(item);
            if (!string.IsNullOrEmpty(selectedEndpointId) &&
                string.Equals(d.EndpointId, selectedEndpointId, StringComparison.OrdinalIgnoreCase))
            {
                toSelect = item;
            }
        }

        combo.SelectedItem = toSelect;
    }

    private void ProfileTargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents) return;

        _settings.Profiles.PixelPlaybackId = GetSelectedComboTag(PixelPlayCombo);
        _settings.Profiles.PixelRecordingId = GetSelectedComboTag(PixelRecCombo);
        _settings.Profiles.RazerPlaybackId = GetSelectedComboTag(RazerPlayCombo);
        _settings.Profiles.RazerRecordingId = GetSelectedComboTag(RazerRecCombo);
        _settings.Profiles.ScreenPlaybackId = GetSelectedComboTag(ScreenPlayCombo);
        SettingsStore.Save(_settings);

        SetStatus("Cibles des profils enregistrées.", StatusKind.Info);
        _ = RefreshLiveBannerAsync();
    }

    private void ResetProfilesButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.Profiles = new ProfileTargets();
        SettingsStore.Save(_settings);
        PopulateProfileTargetCombos();
        SetStatus("Profils réinitialisés en détection automatique.", StatusKind.Info);
        _ = RefreshLiveBannerAsync();
    }

    private void OpenDataDirButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFolder(AppPaths.DataDir);
    }

    private void OpenLogsDirButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFolder(AppPaths.LogsDir);
    }

    private void RefreshLogsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshLogsPreview();
    }

    private void RefreshLogsPreview()
    {
        var lines = new List<string>();
        AppendTail(lines, "watcher.log", AppPaths.WatcherLogPath, 8);
        AppendTail(lines, "autorestore.log", AppPaths.AutoRestoreLogPath, 6);

        LogsPreviewText.Text = lines.Count == 0
            ? "Aucune entrée de journal pour le moment."
            : string.Join(Environment.NewLine, lines);
    }

    private static void AppendTail(List<string> dest, string label, string path, int count)
    {
        if (!File.Exists(path)) return;
        try
        {
            string[] all = File.ReadAllLines(path);
            if (all.Length == 0) return;
            dest.Add($"--- {label} ---");
            foreach (string line in all.TakeLast(count))
            {
                dest.Add(line);
            }
        }
        catch { }
    }

    private void UpdateBackupSummary()
    {
        int count = BackupStore.Load().Count;
        BackupSummaryText.Text = count <= 1
            ? $"{count} appareil sauvegardé"
            : $"{count} appareils sauvegardés";
    }

    private void UpdateAdminSessionBadge()
    {
        bool active = _adminClient.IsSessionActive;
        AdminSessionBadge.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        RenameShieldIcon.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
    }

    // ==================== HELPERS ====================

    private enum StatusKind { Info, Success, Warning, Error }

    private void SetBusy(bool busy, string? message = null)
    {
        _isBusy = busy;
        RenameProgressRing.IsActive = busy;
        RenameProgressRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RenameButton.IsEnabled = !busy;
        BackupButton.IsEnabled = !busy;
        RestoreButton.IsEnabled = !busy && (GetSelectedDevice()?.HasBackup == true);
        RestoreAllNowButton.IsEnabled = !busy;
        BootRestoreToggle.IsEnabled = !busy;
        UpdateAdminSessionBadge();

        if (!string.IsNullOrEmpty(message))
        {
            SetStatus(message, StatusKind.Info);
        }
    }

    private void SetStatus(string message, StatusKind kind)
    {
        ThemePalette p = ThemePalette.For(RootGrid.ActualTheme);
        StatusMessageText.Text = message;
        StatusDot.Fill = kind switch
        {
            StatusKind.Success => p.SuccessDot,
            StatusKind.Warning => p.WarningDot,
            StatusKind.Error => p.ErrorForeground,
            _ => p.AccentFill
        };
    }

    private static string DeviceKey(AudioDevice d) => $"{d.Flow}|{d.Id}";

    private static string GetDeviceGlyph(AudioDevice d)
    {
        if (d.Flow == "Capture") return "\uE720"; // Microphone
        if (d.Name.Contains("Razer", StringComparison.OrdinalIgnoreCase) ||
            d.Description.Contains("BlackShark", StringComparison.OrdinalIgnoreCase) ||
            d.Description.Contains("Headset", StringComparison.OrdinalIgnoreCase))
            return "\uE95B"; // Headset
        if (d.Name.Contains("Pixel", StringComparison.OrdinalIgnoreCase) ||
            d.Description.Contains("Buds", StringComparison.OrdinalIgnoreCase))
            return "\uE7F6"; // Earbuds / Headphones
        if (d.Name.Contains("Philips", StringComparison.OrdinalIgnoreCase) ||
            d.Description.Contains("Evnia", StringComparison.OrdinalIgnoreCase))
            return "\uE7F4"; // Monitor
        return "\uE767"; // Speaker
    }

    private static string FormatProfileLabel(string profile) => profile switch
    {
        "Pixel" => "Pixel Buds",
        "Razer" => "Razer",
        _ => "Écran"
    };

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (object item in combo.Items)
        {
            if (item is ComboBoxItem cbi && string.Equals(cbi.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = cbi;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static string GetSelectedComboTag(ComboBox combo) =>
        combo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string tag ? tag : string.Empty;

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }

    // ==================== WIN32 MIN-SIZE SUBCLASS & ACTIVATION ====================

    private void InstallMinSizeHook()
    {
        _newWndProc = CustomWndProc;
        IntPtr fnPtr = Marshal.GetFunctionPointerForDelegate(_newWndProc);
        _oldWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, fnPtr);
    }

    private IntPtr CustomWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            uint dpi = GetDpiForWindow(hWnd);
            double scale = dpi > 0 ? dpi / 96.0 : 1.0;
            MINMAXINFO info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            info.ptMinTrackSize.X = (int)Math.Round(940 * scale);
            info.ptMinTrackSize.Y = (int)Math.Round(620 * scale);
            Marshal.StructureToPtr(info, lParam, fDeleteOld: false);
            return IntPtr.Zero;
        }
        return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    private const int GWLP_WNDPROC = -4;
    private const uint WM_GETMINMAXINFO = 0x0024;
    private const int SW_RESTORE = 9;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
