using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Forms = System.Windows.Forms;

namespace HostsManagerV3
{
    public class HostRecord
    {
        public string Type { get; set; }        // "Active", "Passive", "Comment"
        public string IP { get; set; }          // e.g. "127.0.0.1"
        public string Domain { get; set; }      // e.g. "test.local"
        public string RawLine { get; set; }     // original line
        public string BadgeText { get; set; }   // "● ACTIVE", "○ PASSIVE", "# COMMENT"
        public Brush BadgeBg { get; set; }
        public Brush BadgeFg { get; set; }
        public Brush IpColor { get; set; }
        public bool IsEntry { get; set; }       // true if it has IP and can be toggled
        public string ToggleButtonText { get; set; } // "Deactivate" or "Activate"

        // New Feature Flags
        public bool IsSelected { get; set; }
        public bool IsEditing { get; set; }
        public bool HasConflict { get; set; }
        public bool IsDuplicate { get; set; }
        public string ConflictDetail { get; set; }
        public string PingResult { get; set; }
        public Brush PingColor { get; set; }
    }

    public class AppSettings
    {
        public string Language { get; set; }
        public string Theme { get; set; } // "Light", "Dark", "System"
        public bool MinimizeToTray { get; set; }
        public bool CloseToTray { get; set; }

        public AppSettings()
        {
            Language = "en";
            Theme = "System";
            MinimizeToTray = true;
            CloseToTray = false;
        }
    }

    public class LanguageItem
    {
        public string Code { get; set; }
        public string DisplayName { get; set; }
    }

    public static class I18n
    {
        private static Dictionary<string, string> _strings = new Dictionary<string, string>();
        private static string _currentLanguage = "en";
        public static string CurrentLanguage
        {
            get { return _currentLanguage; }
            set { _currentLanguage = value; }
        }

        private static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string LangDir = Path.Combine(AppDir, "languages");

        public static void Initialize(string lang)
        {
            CurrentLanguage = lang;
            LoadLanguage(CurrentLanguage);
        }

        public static void SetLanguage(string lang)
        {
            CurrentLanguage = lang;
            LoadLanguage(lang);
        }

        private static string GetLanguageSortKey(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsLetter(text, i))
                {
                    return text.Substring(i).Trim();
                }
            }
            return text.Trim();
        }

        public static List<LanguageItem> GetAvailableLanguages()
        {
            var list = new List<LanguageItem>();
            if (!Directory.Exists(LangDir)) return list;

            try
            {
                var files = Directory.GetFiles(LangDir, "*.json");
                foreach (var file in files)
                {
                    string code = Path.GetFileNameWithoutExtension(file);
                    string displayName = code.ToUpper();
                    try
                    {
                        string json = File.ReadAllText(file, Encoding.UTF8);
                        var serializer = new JavaScriptSerializer();
                        var dict = serializer.Deserialize<Dictionary<string, string>>(json);
                        if (dict != null && dict.ContainsKey("LanguageDisplayName"))
                        {
                            displayName = dict["LanguageDisplayName"];
                        }
                    }
                    catch { }

                    list.Add(new LanguageItem { Code = code, DisplayName = displayName });
                }
            }
            catch { }

            return list.OrderBy(l => GetLanguageSortKey(l.DisplayName), StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static void LoadLanguage(string lang)
        {
            string filePath = Path.Combine(LangDir, lang + ".json");
            if (File.Exists(filePath))
            {
                try
                {
                    string json = File.ReadAllText(filePath, Encoding.UTF8);
                    var serializer = new JavaScriptSerializer();
                    _strings = serializer.Deserialize<Dictionary<string, string>>(json);
                }
                catch
                {
                    _strings = new Dictionary<string, string>();
                }
            }
            else
            {
                _strings = new Dictionary<string, string>();
            }
        }

        public static string T(string key, params object[] args)
        {
            string val;
            if (_strings != null && _strings.TryGetValue(key, out val))
            {
                if (args != null && args.Length > 0)
                {
                    try { return string.Format(val, args); } catch { return val; }
                }
                return val;
            }
            return key;
        }
    }

    public class MainWindow : Window
    {
        private static readonly string HostsPath = @"C:\Windows\System32\drivers\etc\hosts";
        private static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string BackupDir = Path.Combine(AppDir, "backups");
        private static readonly string SettingsFile = Path.Combine(AppDir, "settings.json");
        private static readonly string IconFile = Path.Combine(AppDir, "app.png");

        private AppSettings _settings = new AppSettings();
        private Forms.NotifyIcon _notifyIcon;
        private bool _isExiting = false;

        private List<HostRecord> _allRecords = new List<HostRecord>();
        private string _activeFilter = "Tümü";
        private string _searchQuery = "";
        private int _selectedTabIndex = 0;

        // Theme Palette Colors
        public bool IsDark { get; private set; }
        private Brush _colWindowBg;
        private Brush _colCardBg;
        private Brush _colCardBorder;
        private Brush _colCardHover;
        private Brush _colTextPrimary;
        private Brush _colTextSecondary;
        private Brush _colHeaderBg;
        private Brush _colHeaderBorder;
        private Brush _colPillContainer;
        private Brush _colInputBg;
        private Brush _colInputBorder;

        // UI Header Controls
        private Border _headerBorder;
        private TextBlock _lblTitle;
        private TextBlock _lblSubtitle;
        private Border _adminBorder;
        private TextBlock _lblAdminStatus;
        private Button _btnFlush;

        // Navigation Controls
        private Border _navContainer;
        private List<Border> _navPills = new List<Border>();
        private List<TextBlock> _navLabels = new List<TextBlock>();
        private List<Border> _navBadges = new List<Border>();
        private List<TextBlock> _navBadgeTexts = new List<TextBlock>();

        // Views
        private UIElement _viewRecords;
        private UIElement _viewRaw;
        private UIElement _viewBackups;
        private UIElement _viewSettings;

        // Records Controls
        private Border _cardAdd;
        private TextBlock _lblAddTitle;
        private TextBlock _lblIpHeader;
        private TextBlock _lblDomainHeader;
        private TextBlock _lblCommentHeader;
        private Button _btnAddRecord;
        private Button _btnAddComment;
        private TextBox _txtSearch;
        private TextBox _txtIp;
        private TextBox _txtDomain;
        private TextBox _txtComment;
        private TextBlock _lblStats;
        private StackPanel _panelCards;
        private List<Button> _filterButtons = new List<Button>();

        // Bulk Toolbar Controls
        private Border _bulkBar;
        private CheckBox _chkSelectAll;
        private TextBlock _lblBulkCount;
        private Button _btnBulkActivate;
        private Button _btnBulkDeactivate;
        private Button _btnBulkDelete;

        // Raw Editor Controls
        private TextBlock _lblRawInfo;
        private Button _btnRawReload;
        private Button _btnRawSave;
        private Border _borderEditor;
        private TextBox _txtRawEditor;

        // Backups Controls
        private TextBlock _lblBackupsTitle;
        private Button _btnOpenBackupFolder;
        private Button _btnCreateBackup;
        private StackPanel _panelBackups;

        // Settings Controls
        private List<Border> _settingsCards = new List<Border>();
        private TextBlock _lblSettingsTitle;
        private TextBlock _lblSettingsSubtitle;
        private TextBlock _lblVersionHeader;
        private TextBlock _lblVersionTag;
        private Border _vBadgeBorder;
        private TextBlock _lblVersionText;
        private TextBlock _lblGithubTag;
        private Border _ghBadgeBorder;
        private TextBlock _lblGithubText;
        private TextBlock _lblThemeHeader;
        private TextBlock _lblThemeDesc;
        private RadioButton _rbThemeLight;
        private RadioButton _rbThemeDark;
        private RadioButton _rbThemeSystem;
        private TextBlock _lblLangHeader;
        private TextBlock _lblLangDesc;
        private ComboBox _cmbLanguages;
        private bool _isUpdatingLangCombo = false;
        private TextBlock _lblTrayHeader;
        private TextBlock _lblTrayDesc;
        private CheckBox _chkMinimizeToTray;
        private CheckBox _chkCloseToTray;

        // Status & InfoBar
        private Border _statusBorder;
        private TextBlock _lblStatusBar;
        private TextBlock _lblFilePath;
        private Border _infoBar;
        private TextBlock _txtInfoBar;

        public MainWindow()
        {
            LoadSettings();
            I18n.Initialize(_settings.Language);

            Title = I18n.T("AppTitle");
            Width = 1040;
            Height = 780;
            MinWidth = 920;
            MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, sans-serif");

            if (File.Exists(IconFile))
            {
                try
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(IconFile, UriKind.Absolute);
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    Icon = bmp;
                }
                catch { }
            }

            if (!Directory.Exists(BackupDir))
            {
                Directory.CreateDirectory(BackupDir);
            }

            ResolveThemePalette();
            BuildUI();
            ApplyThemeColors();
            ApplyLanguage();
            SetupTrayIcon();

            LoadHostsFile();
            RefreshBackups();

            StateChanged += MainWindow_StateChanged;
            Closing += MainWindow_Closing;
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    string json = File.ReadAllText(SettingsFile, Encoding.UTF8);
                    var serializer = new JavaScriptSerializer();
                    var loaded = serializer.Deserialize<AppSettings>(json);
                    if (loaded != null)
                    {
                        _settings = loaded;
                    }
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                var serializer = new JavaScriptSerializer();
                string json = serializer.Serialize(_settings);
                File.WriteAllText(SettingsFile, json, Encoding.UTF8);
            }
            catch { }
        }

        private static bool IsSystemDarkMode()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object val = key.GetValue("AppsUseLightTheme");
                        if (val != null && (int)val == 0) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private void ResolveThemePalette()
        {
            if (_settings.Theme == "Dark")
            {
                IsDark = true;
            }
            else if (_settings.Theme == "Light")
            {
                IsDark = false;
            }
            else
            {
                IsDark = IsSystemDarkMode();
            }

            if (IsDark)
            {
                _colWindowBg = new SolidColorBrush(Color.FromRgb(26, 29, 33));        // #1A1D21
                _colCardBg = new SolidColorBrush(Color.FromRgb(34, 38, 43));          // #22262B
                _colCardBorder = new SolidColorBrush(Color.FromRgb(48, 54, 62));      // #30363E
                _colCardHover = new SolidColorBrush(Color.FromRgb(42, 47, 54));       // #2A2F36
                _colTextPrimary = new SolidColorBrush(Color.FromRgb(240, 243, 246));  // #F0F3F6
                _colTextSecondary = new SolidColorBrush(Color.FromRgb(154, 162, 174));// #9AA2AE
                _colHeaderBg = new SolidColorBrush(Color.FromRgb(30, 34, 39));        // #1E2227
                _colHeaderBorder = new SolidColorBrush(Color.FromRgb(46, 52, 60));    // #2E343C
                _colPillContainer = new SolidColorBrush(Color.FromRgb(22, 24, 27));   // #16181B
                _colInputBg = new SolidColorBrush(Color.FromRgb(22, 24, 27));         // #16181B
                _colInputBorder = new SolidColorBrush(Color.FromRgb(54, 60, 68));     // #363C44
            }
            else
            {
                _colWindowBg = new SolidColorBrush(Color.FromRgb(244, 246, 249));     // #F4F6F9
                _colCardBg = Brushes.White;
                _colCardBorder = new SolidColorBrush(Color.FromRgb(230, 233, 238));   // #E6E9EE
                _colCardHover = new SolidColorBrush(Color.FromRgb(248, 250, 252));    // #F8FAFC
                _colTextPrimary = new SolidColorBrush(Color.FromRgb(24, 28, 32));     // #181C20
                _colTextSecondary = new SolidColorBrush(Color.FromRgb(115, 120, 130));// #737882
                _colHeaderBg = Brushes.White;
                _colHeaderBorder = new SolidColorBrush(Color.FromRgb(229, 231, 235)); // #E5E7EB
                _colPillContainer = new SolidColorBrush(Color.FromRgb(233, 236, 240));// #E9ECF0
                _colInputBg = Brushes.White;
                _colInputBorder = new SolidColorBrush(Color.FromRgb(215, 220, 228));  // #D7DCE4
            }

            Background = _colWindowBg;
        }

        private void ApplyThemeColors()
        {
            ResolveThemePalette();

            if (_headerBorder != null)
            {
                _headerBorder.Background = _colHeaderBg;
                _headerBorder.BorderBrush = _colHeaderBorder;
                _lblTitle.Foreground = _colTextPrimary;
                _lblSubtitle.Foreground = _colTextSecondary;
            }

            if (_navContainer != null)
            {
                _navContainer.Background = _colPillContainer;
            }

            if (_cardAdd != null)
            {
                _cardAdd.Background = _colCardBg;
                _cardAdd.BorderBrush = _colCardBorder;
                _lblAddTitle.Foreground = _colTextPrimary;
                _lblIpHeader.Foreground = _colTextSecondary;
                _lblDomainHeader.Foreground = _colTextSecondary;
                _lblCommentHeader.Foreground = _colTextSecondary;
                UpdateTextBoxTheme(_txtIp);
                UpdateTextBoxTheme(_txtDomain);
                UpdateTextBoxTheme(_txtComment);
                UpdateTextBoxTheme(_txtSearch);
            }

            if (_bulkBar != null)
            {
                _bulkBar.Background = _colCardBg;
                _bulkBar.BorderBrush = _colCardBorder;
                _chkSelectAll.Foreground = _colTextPrimary;
                _lblBulkCount.Foreground = _colTextSecondary;

                if (_btnBulkActivate != null)
                {
                    _btnBulkActivate.Background = IsDark ? new SolidColorBrush(Color.FromRgb(40, 45, 52)) : new SolidColorBrush(Color.FromRgb(245, 247, 250));
                    _btnBulkActivate.Foreground = _colTextPrimary;
                    _btnBulkActivate.BorderBrush = _colCardBorder;
                }
                if (_btnBulkDeactivate != null)
                {
                    _btnBulkDeactivate.Background = IsDark ? new SolidColorBrush(Color.FromRgb(40, 45, 52)) : new SolidColorBrush(Color.FromRgb(245, 247, 250));
                    _btnBulkDeactivate.Foreground = _colTextPrimary;
                    _btnBulkDeactivate.BorderBrush = _colCardBorder;
                }
                if (_btnBulkDelete != null)
                {
                    _btnBulkDelete.Background = IsDark ? new SolidColorBrush(Color.FromRgb(58, 28, 32)) : new SolidColorBrush(Color.FromRgb(253, 231, 233));
                    _btnBulkDelete.Foreground = IsDark ? new SolidColorBrush(Color.FromRgb(248, 113, 113)) : new SolidColorBrush(Color.FromRgb(164, 38, 44));
                    _btnBulkDelete.BorderBrush = IsDark ? new SolidColorBrush(Color.FromRgb(107, 40, 48)) : new SolidColorBrush(Color.FromRgb(240, 200, 202));
                }
            }

            if (_borderEditor != null)
            {
                _borderEditor.Background = _colCardBg;
                _borderEditor.BorderBrush = _colCardBorder;
                _txtRawEditor.Background = _colInputBg;
                _txtRawEditor.Foreground = _colTextPrimary;
                _lblRawInfo.Foreground = _colTextSecondary;
                _btnRawReload.Background = IsDark ? new SolidColorBrush(Color.FromRgb(40, 45, 52)) : new SolidColorBrush(Color.FromRgb(245, 247, 250));
                _btnRawReload.Foreground = _colTextPrimary;
                _btnRawReload.BorderBrush = _colCardBorder;
            }

            if (_statusBorder != null)
            {
                _statusBorder.Background = _colHeaderBg;
                _statusBorder.BorderBrush = _colHeaderBorder;
                _lblStatusBar.Foreground = _colTextSecondary;
                _lblFilePath.Foreground = _colTextSecondary;
            }

            foreach (var sc in _settingsCards)
            {
                sc.Background = _colCardBg;
                sc.BorderBrush = _colCardBorder;
            }

            if (_vBadgeBorder != null)
            {
                _vBadgeBorder.Background = IsDark ? new SolidColorBrush(Color.FromRgb(30, 58, 95)) : new SolidColorBrush(Color.FromRgb(235, 243, 252));
                _vBadgeBorder.BorderBrush = IsDark ? new SolidColorBrush(Color.FromRgb(37, 99, 235)) : new SolidColorBrush(Color.FromRgb(190, 215, 245));
                if (_lblVersionText != null)
                {
                    _lblVersionText.Foreground = IsDark ? new SolidColorBrush(Color.FromRgb(96, 165, 250)) : new SolidColorBrush(Color.FromRgb(0, 103, 192));
                }
            }

            if (_ghBadgeBorder != null)
            {
                _ghBadgeBorder.Background = IsDark ? new SolidColorBrush(Color.FromRgb(38, 42, 48)) : new SolidColorBrush(Color.FromRgb(240, 243, 246));
                _ghBadgeBorder.BorderBrush = IsDark ? new SolidColorBrush(Color.FromRgb(60, 68, 77)) : new SolidColorBrush(Color.FromRgb(209, 217, 224));
                if (_lblGithubText != null)
                {
                    _lblGithubText.Foreground = IsDark ? new SolidColorBrush(Color.FromRgb(88, 166, 255)) : new SolidColorBrush(Color.FromRgb(9, 105, 218));
                }
            }

            if (_lblSettingsTitle != null)
            {
                _lblSettingsTitle.Foreground = _colTextPrimary;
                _lblSettingsSubtitle.Foreground = _colTextSecondary;
                _lblVersionHeader.Foreground = _colTextPrimary;
                _lblVersionTag.Foreground = _colTextSecondary;
                if (_lblGithubTag != null) _lblGithubTag.Foreground = _colTextSecondary;
                _lblThemeHeader.Foreground = _colTextPrimary;
                _lblThemeDesc.Foreground = _colTextSecondary;
                _rbThemeLight.Foreground = _colTextPrimary;
                _rbThemeDark.Foreground = _colTextPrimary;
                _rbThemeSystem.Foreground = _colTextPrimary;
                _lblLangHeader.Foreground = _colTextPrimary;
                _lblLangDesc.Foreground = _colTextSecondary;
                if (_cmbLanguages != null)
                {
                    _cmbLanguages.Background = _colInputBg;
                    _cmbLanguages.Foreground = _colTextPrimary;
                    _cmbLanguages.BorderBrush = _colInputBorder;
                    foreach (ComboBoxItem item in _cmbLanguages.Items)
                    {
                        item.Foreground = _colTextPrimary;
                        item.Background = _colCardBg;
                    }
                }
                _lblTrayHeader.Foreground = _colTextPrimary;
                _lblTrayDesc.Foreground = _colTextSecondary;
                _chkMinimizeToTray.Foreground = _colTextPrimary;
                _chkCloseToTray.Foreground = _colTextPrimary;
            }

            // Update filter buttons theme
            foreach (Button b in _filterButtons)
            {
                bool isSelected = ((string)b.Tag == "Filter" + _activeFilter || (b.Tag.ToString() == "FilterAll" && _activeFilter == "Tümü"));
                UpdateFilterButtonAppearance(b, isSelected);
            }

            UpdateNavPillsVisual();
            RenderRecords();
            RefreshBackups();
        }

        private void UpdateTextBoxTheme(TextBox tb)
        {
            if (tb == null) return;
            tb.Background = _colInputBg;
            tb.Foreground = _colTextPrimary;
            tb.BorderBrush = _colInputBorder;
        }

        private static bool IsAdministrator()
        {
            try
            {
                var id = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(id);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private static void RestartAsAdmin()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Process.GetCurrentProcess().MainModule.FileName,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Yönetici olarak başlatılamadı: " + ex.Message, "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SetupTrayIcon()
        {
            try
            {
                _notifyIcon = new Forms.NotifyIcon();
                if (File.Exists(IconFile))
                {
                    try
                    {
                        using (var bmp = new System.Drawing.Bitmap(IconFile))
                        {
                            IntPtr hIcon = bmp.GetHicon();
                            _notifyIcon.Icon = System.Drawing.Icon.FromHandle(hIcon);
                        }
                    }
                    catch
                    {
                        _notifyIcon.Icon = SystemIcons.Application;
                    }
                }
                else
                {
                    _notifyIcon.Icon = SystemIcons.Application;
                }

                _notifyIcon.Text = "Hosts Yöneticisi v0.2.0";
                _notifyIcon.Visible = true;

                var menu = new Forms.ContextMenu();
                var mOpen = new Forms.MenuItem(I18n.T("TrayOpen"), (s, e) => RestoreFromTray());
                var mFlush = new Forms.MenuItem(I18n.T("TrayFlushDns"), (s, e) => FlushDns(false));
                var mFolder = new Forms.MenuItem(I18n.T("TrayOpenFolder"), (s, e) =>
                {
                    try { Process.Start("explorer.exe", @"C:\Windows\System32\drivers\etc"); } catch { }
                });
                var mSep = new Forms.MenuItem("-");
                var mExit = new Forms.MenuItem(I18n.T("TrayExit"), (s, e) =>
                {
                    _isExiting = true;
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    Application.Current.Shutdown();
                });

                menu.MenuItems.Add(mOpen);
                menu.MenuItems.Add(mFlush);
                menu.MenuItems.Add(mFolder);
                menu.MenuItems.Add(mSep);
                menu.MenuItems.Add(mExit);

                _notifyIcon.ContextMenu = menu;
                _notifyIcon.DoubleClick += (s, e) => RestoreFromTray();
            }
            catch { }
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void MainWindow_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized && _settings.MinimizeToTray)
            {
                Hide();
                if (_notifyIcon != null)
                {
                    _notifyIcon.ShowBalloonTip(1500, I18n.T("AppTitle"), I18n.T("TrayMinimizedMsg"), Forms.ToolTipIcon.Info);
                }
            }
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isExiting && _settings.CloseToTray)
            {
                e.Cancel = true;
                Hide();
                if (_notifyIcon != null)
                {
                    _notifyIcon.ShowBalloonTip(1500, I18n.T("AppTitle"), I18n.T("TrayMinimizedMsg"), Forms.ToolTipIcon.Info);
                }
            }
            else
            {
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                }
            }
        }

        private void BuildUI()
        {
            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0: Top Header
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 1: InfoBar
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 2: Modern Nav
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 3: Content
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 4: Status Bar

            // -------------------------------------------------------------
            // ROW 0: Header
            // -------------------------------------------------------------
            _headerBorder = new Border
            {
                Background = _colHeaderBg,
                BorderBrush = _colHeaderBorder,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(22, 14, 22, 14)
            };

            var headerDock = new DockPanel();

            // Left: Title & Subtitle
            var titleStack = new StackPanel();
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };

            var iconBorder = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(8),
                Width = 36,
                Height = 36,
                Margin = new Thickness(0, 0, 12, 0)
            };

            if (File.Exists(IconFile))
            {
                try
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(IconFile, UriKind.Absolute);
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.EndInit();

                    var imgLogo = new System.Windows.Controls.Image
                    {
                        Source = bmp,
                        Width = 36,
                        Height = 36
                    };
                    System.Windows.Media.RenderOptions.SetBitmapScalingMode(imgLogo, System.Windows.Media.BitmapScalingMode.HighQuality);
                    iconBorder.Child = imgLogo;
                }
                catch
                {
                    iconBorder.Child = new TextBlock { Text = "🌐", FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                }
            }
            else
            {
                iconBorder.Child = new TextBlock { Text = "🌐", FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            }
            titleRow.Children.Add(iconBorder);

            var titleTextStack = new StackPanel();
            _lblTitle = new TextBlock
            {
                Text = I18n.T("AppTitle"),
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = _colTextPrimary
            };
            _lblSubtitle = new TextBlock
            {
                Text = I18n.T("AppSubtitle"),
                FontSize = 11,
                Foreground = _colTextSecondary,
                Margin = new Thickness(0, 1, 0, 0)
            };
            titleTextStack.Children.Add(_lblTitle);
            titleTextStack.Children.Add(_lblSubtitle);
            titleRow.Children.Add(titleTextStack);

            titleStack.Children.Add(titleRow);
            DockPanel.SetDock(titleStack, Dock.Left);
            headerDock.Children.Add(titleStack);

            // Right: Actions
            var rightActions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            bool isAdmin = IsAdministrator();
            _adminBorder = new Border
            {
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 0),
                Background = isAdmin ? new SolidColorBrush(Color.FromRgb(223, 246, 221)) : new SolidColorBrush(Color.FromRgb(253, 231, 233))
            };
            _lblAdminStatus = new TextBlock
            {
                Text = isAdmin ? I18n.T("AdminActive") : I18n.T("AdminRequired"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = isAdmin ? new SolidColorBrush(Color.FromRgb(16, 124, 65)) : new SolidColorBrush(Color.FromRgb(164, 38, 44)),
                VerticalAlignment = VerticalAlignment.Center
            };
            _adminBorder.Child = _lblAdminStatus;
            if (!isAdmin)
            {
                _adminBorder.Cursor = Cursors.Hand;
                _adminBorder.MouseDown += (s, e) => RestartAsAdmin();
            }
            rightActions.Children.Add(_adminBorder);

            _btnFlush = CreateFluentButton(I18n.T("FlushDns"), false);
            _btnFlush.Margin = new Thickness(0);
            _btnFlush.Click += (s, e) => FlushDns(false);
            rightActions.Children.Add(_btnFlush);

            headerDock.Children.Add(rightActions);
            _headerBorder.Child = headerDock;
            Grid.SetRow(_headerBorder, 0);
            mainGrid.Children.Add(_headerBorder);

            // -------------------------------------------------------------
            // ROW 1: InfoBar
            // -------------------------------------------------------------
            _infoBar = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(240, 248, 255)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 8, 14, 8),
                Margin = new Thickness(22, 10, 22, 0),
                Visibility = Visibility.Collapsed
            };
            var infoGrid = new Grid();
            infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _txtInfoBar = new TextBlock
            {
                Text = "",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0, 90, 158)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_txtInfoBar, 0);
            infoGrid.Children.Add(_txtInfoBar);

            var btnCloseInfo = new Button
            {
                Content = "✕",
                Width = 24,
                Height = 24,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontSize = 11,
                Foreground = Brushes.Gray
            };
            btnCloseInfo.Click += (s, e) => _infoBar.Visibility = Visibility.Collapsed;
            Grid.SetColumn(btnCloseInfo, 1);
            infoGrid.Children.Add(btnCloseInfo);

            _infoBar.Child = infoGrid;
            Grid.SetRow(_infoBar, 1);
            mainGrid.Children.Add(_infoBar);

            // -------------------------------------------------------------
            // ROW 2: Navigation Bar
            // -------------------------------------------------------------
            var navOuter = new Border
            {
                Margin = new Thickness(22, 12, 22, 4),
                HorizontalAlignment = HorizontalAlignment.Left
            };

            _navContainer = new Border
            {
                Background = _colPillContainer,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(3)
            };

            var navStack = new StackPanel { Orientation = Orientation.Horizontal };
            string[] tabKeys = { "TabRecords", "TabRaw", "TabBackups", "TabSettings" };

            _navPills.Clear();
            _navLabels.Clear();
            _navBadges.Clear();
            _navBadgeTexts.Clear();

            for (int i = 0; i < tabKeys.Length; i++)
            {
                int tabIndex = i;
                var pill = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(14, 7, 14, 7),
                    Margin = new Thickness(1, 0, 1, 0),
                    Cursor = Cursors.Hand,
                    Background = Brushes.Transparent
                };

                var itemStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

                var lbl = new TextBlock
                {
                    Text = I18n.T(tabKeys[i]),
                    FontSize = 12.5,
                    Foreground = _colTextSecondary,
                    VerticalAlignment = VerticalAlignment.Center
                };
                itemStack.Children.Add(lbl);

                var badgeBorder = new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(8, 0, 0, 0),
                    Background = new SolidColorBrush(Color.FromRgb(220, 224, 230)),
                    Visibility = (i == 0 || i == 2) ? Visibility.Visible : Visibility.Collapsed
                };
                var badgeTxt = new TextBlock
                {
                    Text = "0",
                    FontSize = 10.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(100, 108, 120))
                };
                badgeBorder.Child = badgeTxt;
                itemStack.Children.Add(badgeBorder);

                pill.Child = itemStack;

                pill.MouseEnter += (s, e) =>
                {
                    if (_selectedTabIndex != tabIndex)
                    {
                        pill.Background = IsDark ? new SolidColorBrush(Color.FromRgb(40, 45, 52)) : new SolidColorBrush(Color.FromRgb(225, 228, 233));
                    }
                };
                pill.MouseLeave += (s, e) =>
                {
                    if (_selectedTabIndex != tabIndex)
                    {
                        pill.Background = Brushes.Transparent;
                    }
                };
                pill.MouseDown += (s, e) => SwitchTab(tabIndex);

                _navPills.Add(pill);
                _navLabels.Add(lbl);
                _navBadges.Add(badgeBorder);
                _navBadgeTexts.Add(badgeTxt);

                navStack.Children.Add(pill);
            }

            _navContainer.Child = navStack;
            navOuter.Child = _navContainer;
            Grid.SetRow(navOuter, 2);
            mainGrid.Children.Add(navOuter);

            // -------------------------------------------------------------
            // ROW 3: Views
            // -------------------------------------------------------------
            var viewsGrid = new Grid { Margin = new Thickness(22, 6, 22, 10) };

            _viewRecords = BuildRecordsTabView();
            _viewRaw = BuildRawTabView();
            _viewBackups = BuildBackupsTabView();
            _viewSettings = BuildSettingsTabView();

            _viewRecords.Visibility = Visibility.Visible;
            _viewRaw.Visibility = Visibility.Collapsed;
            _viewBackups.Visibility = Visibility.Collapsed;
            _viewSettings.Visibility = Visibility.Collapsed;

            viewsGrid.Children.Add(_viewRecords);
            viewsGrid.Children.Add(_viewRaw);
            viewsGrid.Children.Add(_viewBackups);
            viewsGrid.Children.Add(_viewSettings);

            Grid.SetRow(viewsGrid, 3);
            mainGrid.Children.Add(viewsGrid);

            // -------------------------------------------------------------
            // ROW 4: Status Bar
            // -------------------------------------------------------------
            _statusBorder = new Border
            {
                Background = _colHeaderBg,
                BorderBrush = _colHeaderBorder,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(22, 7, 22, 7)
            };
            var statusDock = new DockPanel();

            _lblStatusBar = new TextBlock
            {
                Text = I18n.T("ReadyStatus"),
                FontSize = 11,
                Foreground = _colTextSecondary
            };
            DockPanel.SetDock(_lblStatusBar, Dock.Left);
            statusDock.Children.Add(_lblStatusBar);

            _lblFilePath = new TextBlock
            {
                Text = I18n.T("FileLocation", HostsPath),
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                Foreground = _colTextSecondary,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            statusDock.Children.Add(_lblFilePath);

            _statusBorder.Child = statusDock;
            Grid.SetRow(_statusBorder, 4);
            mainGrid.Children.Add(_statusBorder);

            Content = mainGrid;
            SwitchTab(0);
        }

        private void SwitchTab(int index)
        {
            _selectedTabIndex = index;
            _viewRecords.Visibility = (index == 0) ? Visibility.Visible : Visibility.Collapsed;
            _viewRaw.Visibility = (index == 1) ? Visibility.Visible : Visibility.Collapsed;
            _viewBackups.Visibility = (index == 2) ? Visibility.Visible : Visibility.Collapsed;
            _viewSettings.Visibility = (index == 3) ? Visibility.Visible : Visibility.Collapsed;

            UpdateNavPillsVisual();
        }

        private void UpdateNavPillsVisual()
        {
            for (int i = 0; i < _navPills.Count; i++)
            {
                bool isSelected = (i == _selectedTabIndex);
                if (isSelected)
                {
                    _navPills[i].Background = IsDark ? new SolidColorBrush(Color.FromRgb(46, 52, 60)) : Brushes.White;
                    _navPills[i].Effect = new DropShadowEffect
                    {
                        BlurRadius = 4,
                        ShadowDepth = 1,
                        Opacity = IsDark ? 0.2 : 0.08,
                        Color = Colors.Black
                    };
                    _navLabels[i].Foreground = new SolidColorBrush(Color.FromRgb(0, 120, 215)); // Fluent Blue
                    _navLabels[i].FontWeight = FontWeights.SemiBold;

                    _navBadges[i].Background = IsDark ? new SolidColorBrush(Color.FromRgb(30, 42, 56)) : new SolidColorBrush(Color.FromRgb(235, 243, 252));
                    _navBadgeTexts[i].Foreground = new SolidColorBrush(Color.FromRgb(0, 120, 215));
                }
                else
                {
                    _navPills[i].Background = Brushes.Transparent;
                    _navPills[i].Effect = null;
                    _navLabels[i].Foreground = _colTextSecondary;
                    _navLabels[i].FontWeight = FontWeights.Normal;

                    _navBadges[i].Background = IsDark ? new SolidColorBrush(Color.FromRgb(36, 40, 46)) : new SolidColorBrush(Color.FromRgb(222, 225, 230));
                    _navBadgeTexts[i].Foreground = IsDark ? new SolidColorBrush(Color.FromRgb(160, 168, 178)) : new SolidColorBrush(Color.FromRgb(110, 118, 128));
                }
            }
        }

        // -------------------------------------------------------------
        // TAB 1: Records View
        // -------------------------------------------------------------
        private UIElement BuildRecordsTabView()
        {
            var panel = new Grid();
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0: Add Card
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 1: Search & Filter Toolbar
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 2: Cards Scroll
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 3: Fixed Bottom Bulk Actions Toolbar

            // 0: Add Card Frame
            _cardAdd = new Border
            {
                Background = _colCardBg,
                BorderBrush = _colCardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 14, 16, 14),
                Margin = new Thickness(0, 4, 0, 10)
            };
            _cardAdd.Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = IsDark ? 0.2 : 0.04, Color = Colors.Black };

            var stackAdd = new StackPanel();
            _lblAddTitle = new TextBlock
            {
                Text = I18n.T("AddCardTitle"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = _colTextPrimary,
                Margin = new Thickness(0, 0, 0, 10)
            };
            stackAdd.Children.Add(_lblAddTitle);

            // Row 1: IP + Domain
            var gridRow1 = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            gridRow1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            gridRow1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            gridRow1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            gridRow1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            gridRow1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _lblIpHeader = new TextBlock { Text = I18n.T("IpLabel"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), FontSize = 12, Foreground = _colTextSecondary };
            Grid.SetColumn(_lblIpHeader, 0);
            gridRow1.Children.Add(_lblIpHeader);

            _txtIp = CreateFluentTextBox("127.0.0.1");
            _txtIp.Margin = new Thickness(0, 0, 16, 0);
            Grid.SetColumn(_txtIp, 1);
            gridRow1.Children.Add(_txtIp);

            _lblDomainHeader = new TextBlock { Text = I18n.T("DomainLabel"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), FontSize = 12, Foreground = _colTextSecondary };
            Grid.SetColumn(_lblDomainHeader, 2);
            gridRow1.Children.Add(_lblDomainHeader);

            _txtDomain = CreateFluentTextBox("");
            _txtDomain.Margin = new Thickness(0, 0, 16, 0);
            _txtDomain.KeyDown += (s, e) => { if (e.Key == Key.Enter) AddEntry(); };
            Grid.SetColumn(_txtDomain, 3);
            gridRow1.Children.Add(_txtDomain);

            _btnAddRecord = CreateFluentButton(I18n.T("AddRecordBtn"), true);
            _btnAddRecord.Click += (s, e) => AddEntry();
            Grid.SetColumn(_btnAddRecord, 4);
            gridRow1.Children.Add(_btnAddRecord);

            stackAdd.Children.Add(gridRow1);

            // Row 2: Comment
            var gridRow2 = new Grid();
            gridRow2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            gridRow2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            gridRow2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _lblCommentHeader = new TextBlock { Text = I18n.T("CommentLabel"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0), FontSize = 12, Foreground = _colTextSecondary };
            Grid.SetColumn(_lblCommentHeader, 0);
            gridRow2.Children.Add(_lblCommentHeader);

            _txtComment = CreateFluentTextBox("");
            _txtComment.Margin = new Thickness(0, 0, 16, 0);
            _txtComment.KeyDown += (s, e) => { if (e.Key == Key.Enter) AddComment(); };
            Grid.SetColumn(_txtComment, 1);
            gridRow2.Children.Add(_txtComment);

            _btnAddComment = CreateFluentButton(I18n.T("AddCommentBtn"), false);
            _btnAddComment.Click += (s, e) => AddComment();
            Grid.SetColumn(_btnAddComment, 2);
            gridRow2.Children.Add(_btnAddComment);

            stackAdd.Children.Add(gridRow2);
            _cardAdd.Child = stackAdd;
            Grid.SetRow(_cardAdd, 0);
            panel.Children.Add(_cardAdd);

            // 1: Search & Filter Toolbar
            var toolbarDock = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };

            _txtSearch = CreateFluentTextBox("");
            _txtSearch.Width = 260;
            _txtSearch.Margin = new Thickness(0, 0, 12, 0);
            _txtSearch.TextChanged += (s, e) =>
            {
                _searchQuery = _txtSearch.Text.Trim();
                RenderRecords();
            };
            DockPanel.SetDock(_txtSearch, Dock.Left);
            toolbarDock.Children.Add(_txtSearch);

            var filterStack = new StackPanel { Orientation = Orientation.Horizontal };
            string[] filters = { "Tümü", "Aktif", "Pasif", "Yorumlar" };
            string[] filterKeys = { "FilterAll", "FilterActive", "FilterPassive", "FilterComments" };
            _filterButtons.Clear();

            for (int i = 0; i < filters.Length; i++)
            {
                string filterTag = filters[i];
                var fBtn = CreateFilterButton(I18n.T(filterKeys[i]), filterTag == _activeFilter);
                fBtn.Tag = filterKeys[i];
                fBtn.Click += (s, e) =>
                {
                    _activeFilter = filterTag;
                    foreach (Button b in _filterButtons)
                    {
                        UpdateFilterButtonAppearance(b, (string)b.Tag == "Filter" + _activeFilter || (b.Tag.ToString() == "FilterAll" && _activeFilter == "Tümü"));
                    }
                    RenderRecords();
                };
                _filterButtons.Add(fBtn);
                filterStack.Children.Add(fBtn);
            }
            DockPanel.SetDock(filterStack, Dock.Left);
            toolbarDock.Children.Add(filterStack);

            _lblStats = new TextBlock
            {
                Text = "0 Kayıt",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = _colTextSecondary,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            toolbarDock.Children.Add(_lblStats);

            Grid.SetRow(toolbarDock, 1);
            panel.Children.Add(toolbarDock);

            // 2: Scrollable Records List
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            _panelCards = new StackPanel();
            scroll.Content = _panelCards;

            Grid.SetRow(scroll, 2);
            panel.Children.Add(scroll);

            // 3: Fixed Bottom Bulk Actions Toolbar (Fixed at the bottom of the list)
            _bulkBar = new Border
            {
                Background = _colCardBg,
                BorderBrush = _colCardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 8, 14, 8),
                Margin = new Thickness(0, 8, 0, 2),
                Visibility = Visibility.Visible
            };
            _bulkBar.Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = IsDark ? 0.25 : 0.05, Color = Colors.Black };

            var bulkDock = new DockPanel();

            var bulkLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            _chkSelectAll = new CheckBox
            {
                Content = "  " + I18n.T("BulkSelectAll"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = _colTextPrimary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 0),
                Cursor = Cursors.Hand
            };
            _chkSelectAll.Click += (s, e) =>
            {
                bool isChecked = _chkSelectAll.IsChecked == true;
                foreach (var r in _allRecords)
                {
                    r.IsSelected = isChecked;
                }
                UpdateBulkToolbarState();
                RenderRecords();
            };
            bulkLeft.Children.Add(_chkSelectAll);

            _lblBulkCount = new TextBlock
            {
                Text = I18n.T("BulkSelectedCount", 0),
                FontSize = 12,
                Foreground = _colTextSecondary,
                VerticalAlignment = VerticalAlignment.Center
            };
            bulkLeft.Children.Add(_lblBulkCount);

            DockPanel.SetDock(bulkLeft, Dock.Left);
            bulkDock.Children.Add(bulkLeft);

            var bulkRight = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            _btnBulkActivate = CreateFluentButton(I18n.T("BulkActivate"), false);
            _btnBulkActivate.Padding = new Thickness(12, 5, 12, 5);
            _btnBulkActivate.Margin = new Thickness(0, 0, 6, 0);
            _btnBulkActivate.IsEnabled = false;
            _btnBulkActivate.Click += (s, e) => ExecuteBulkAction("activate");
            bulkRight.Children.Add(_btnBulkActivate);

            _btnBulkDeactivate = CreateFluentButton(I18n.T("BulkDeactivate"), false);
            _btnBulkDeactivate.Padding = new Thickness(12, 5, 12, 5);
            _btnBulkDeactivate.Margin = new Thickness(0, 0, 6, 0);
            _btnBulkDeactivate.IsEnabled = false;
            _btnBulkDeactivate.Click += (s, e) => ExecuteBulkAction("deactivate");
            bulkRight.Children.Add(_btnBulkDeactivate);

            _btnBulkDelete = new Button
            {
                Template = GetFluentButtonTemplate(),
                Content = I18n.T("BulkDelete"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(12, 5, 12, 5),
                Background = IsDark ? new SolidColorBrush(Color.FromRgb(56, 26, 30)) : new SolidColorBrush(Color.FromRgb(253, 231, 233)),
                Foreground = IsDark ? new SolidColorBrush(Color.FromRgb(248, 113, 113)) : new SolidColorBrush(Color.FromRgb(164, 38, 44)),
                BorderBrush = IsDark ? new SolidColorBrush(Color.FromRgb(102, 38, 45)) : new SolidColorBrush(Color.FromRgb(240, 200, 202)),
                BorderThickness = new Thickness(1),
                IsEnabled = false,
                Cursor = Cursors.Hand
            };
            _btnBulkDelete.Click += (s, e) => ExecuteBulkAction("delete");
            bulkRight.Children.Add(_btnBulkDelete);

            bulkDock.Children.Add(bulkRight);
            _bulkBar.Child = bulkDock;

            Grid.SetRow(_bulkBar, 3);
            panel.Children.Add(_bulkBar);

            return panel;
        }

        private void UpdateBulkToolbarState()
        {
            int selectedCount = _allRecords.Count(r => r.IsSelected);
            _lblBulkCount.Text = I18n.T("BulkSelectedCount", selectedCount);

            bool hasSelection = selectedCount > 0;
            _btnBulkActivate.IsEnabled = hasSelection;
            _btnBulkDeactivate.IsEnabled = hasSelection;
            _btnBulkDelete.IsEnabled = hasSelection;

            _chkSelectAll.IsChecked = (_allRecords.Count > 0 && selectedCount == _allRecords.Count);
        }

        private void ExecuteBulkAction(string action)
        {
            var selected = _allRecords.Where(r => r.IsSelected).ToList();
            if (selected.Count == 0) return;

            if (action == "delete")
            {
                var confirm = MessageBox.Show(I18n.T("BulkDeleteConfirm", selected.Count), I18n.T("DeleteConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return;
            }

            try
            {
                var lines = File.ReadAllLines(HostsPath, Encoding.UTF8).ToList();
                var newLines = new List<string>();

                var selectedRawSet = new HashSet<string>(selected.Select(s => s.RawLine.Trim()));

                foreach (var line in lines)
                {
                    string trimmed = line.Trim();
                    if (selectedRawSet.Contains(trimmed))
                    {
                        if (action == "delete")
                        {
                            continue; // Skip line = delete
                        }
                        else if (action == "activate")
                        {
                            if (trimmed.StartsWith("#"))
                            {
                                string un = trimmed.TrimStart('#').Trim();
                                newLines.Add(un);
                            }
                            else
                            {
                                newLines.Add(line);
                            }
                        }
                        else if (action == "deactivate")
                        {
                            if (!trimmed.StartsWith("#"))
                            {
                                newLines.Add("# " + trimmed);
                            }
                            else
                            {
                                newLines.Add(line);
                            }
                        }
                    }
                    else
                    {
                        newLines.Add(line);
                    }
                }

                WriteHostsFile(string.Join("\r\n", newLines.ToArray()) + "\r\n");
                LoadHostsFile();
                ShowInfoBar(I18n.T("BulkSuccess", selected.Count), false);
            }
            catch (UnauthorizedAccessException)
            {
                HandleUnauthorized();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Toplu işlem hatası: " + ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // -------------------------------------------------------------
        // TAB 2: Raw Editor View
        // -------------------------------------------------------------
        private UIElement BuildRawTabView()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var topBar = new DockPanel { Margin = new Thickness(0, 4, 0, 10) };
            _lblRawInfo = new TextBlock
            {
                Text = I18n.T("RawInfoText"),
                FontSize = 12,
                Foreground = _colTextSecondary,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(_lblRawInfo, Dock.Left);
            topBar.Children.Add(_lblRawInfo);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            _btnRawReload = CreateFluentButton(I18n.T("BtnReload"), false);
            _btnRawReload.Click += (s, e) => LoadHostsFile();
            _btnRawReload.Margin = new Thickness(0, 0, 8, 0);
            actions.Children.Add(_btnRawReload);

            _btnRawSave = CreateFluentButton(I18n.T("BtnSave"), true);
            _btnRawSave.Click += (s, e) => SaveRawText();
            actions.Children.Add(_btnRawSave);

            topBar.Children.Add(actions);
            Grid.SetRow(topBar, 0);
            grid.Children.Add(topBar);

            _borderEditor = new Border
            {
                Background = _colCardBg,
                BorderBrush = _colCardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8)
            };

            _txtRawEditor = new TextBox
            {
                FontFamily = new FontFamily("Consolas, Courier New"),
                FontSize = 13,
                AcceptsReturn = true,
                AcceptsTab = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12),
                Background = _colInputBg,
                Foreground = _colTextPrimary
            };
            _borderEditor.Child = _txtRawEditor;

            Grid.SetRow(_borderEditor, 1);
            grid.Children.Add(_borderEditor);

            return grid;
        }

        // -------------------------------------------------------------
        // TAB 3: Backups View
        // -------------------------------------------------------------
        private UIElement BuildBackupsTabView()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var topBar = new DockPanel { Margin = new Thickness(0, 4, 0, 10) };
            _lblBackupsTitle = new TextBlock
            {
                Text = I18n.T("BackupsTitle"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = _colTextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(_lblBackupsTitle, Dock.Left);
            topBar.Children.Add(_lblBackupsTitle);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            _btnOpenBackupFolder = CreateFluentButton(I18n.T("BtnOpenFolder"), false);
            _btnOpenBackupFolder.Click += (s, e) => { try { Process.Start(BackupDir); } catch { } };
            _btnOpenBackupFolder.Margin = new Thickness(0, 0, 8, 0);
            actions.Children.Add(_btnOpenBackupFolder);

            _btnCreateBackup = CreateFluentButton(I18n.T("BtnNewBackup"), true);
            _btnCreateBackup.Click += (s, e) => CreateBackup();
            actions.Children.Add(_btnCreateBackup);

            topBar.Children.Add(actions);
            Grid.SetRow(topBar, 0);
            grid.Children.Add(topBar);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            _panelBackups = new StackPanel();
            scroll.Content = _panelBackups;

            Grid.SetRow(scroll, 1);
            grid.Children.Add(scroll);

            return grid;
        }

        // -------------------------------------------------------------
        // TAB 4: Settings View (Expanded with Theme & Tray)
        // -------------------------------------------------------------
        private UIElement BuildSettingsTabView()
        {
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            var mainStack = new StackPanel { Margin = new Thickness(0, 4, 0, 10) };
            _settingsCards.Clear();

            // 1. Header Card
            var topCard = CreateSettingsCard();
            var topStack = new StackPanel();
            _lblSettingsTitle = new TextBlock
            {
                Text = I18n.T("SettingsTitle"),
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = _colTextPrimary
            };
            _lblSettingsSubtitle = new TextBlock
            {
                Text = I18n.T("SettingsSubtitle"),
                FontSize = 12,
                Foreground = _colTextSecondary,
                Margin = new Thickness(0, 2, 0, 0)
            };
            topStack.Children.Add(_lblSettingsTitle);
            topStack.Children.Add(_lblSettingsSubtitle);
            topCard.Child = topStack;
            mainStack.Children.Add(topCard);

            // 2. 2-Column Grid Layout for Settings Cards (Side-by-side layout)
            var settingsGrid = new Grid();
            settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) }); // Gap
            settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            settingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Row 0: About & System Tray
            settingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Row 1: Appearance & Language Selection

            // --- Row 0, Left: About & Version Card ---
            var verCard = CreateSettingsCard();
            verCard.Margin = new Thickness(0, 0, 0, 12);
            var verStack = new StackPanel();
            _lblVersionHeader = new TextBlock
            {
                Text = I18n.T("VersionCardTitle"),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = _colTextPrimary,
                Margin = new Thickness(0, 0, 0, 12)
            };
            verStack.Children.Add(_lblVersionHeader);

            var vRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            _lblVersionTag = new TextBlock { Text = I18n.T("VersionLabel"), FontSize = 12.5, Foreground = _colTextSecondary, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(_lblVersionTag, Dock.Left);
            vRow.Children.Add(_lblVersionTag);

            _vBadgeBorder = new Border
            {
                Background = IsDark ? new SolidColorBrush(Color.FromRgb(30, 58, 95)) : new SolidColorBrush(Color.FromRgb(235, 243, 252)),
                BorderBrush = IsDark ? new SolidColorBrush(Color.FromRgb(37, 99, 235)) : new SolidColorBrush(Color.FromRgb(190, 215, 245)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 3, 10, 3),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            _lblVersionText = new TextBlock
            {
                Text = I18n.T("VersionValue"),
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = IsDark ? new SolidColorBrush(Color.FromRgb(96, 165, 250)) : new SolidColorBrush(Color.FromRgb(0, 103, 192))
            };
            _vBadgeBorder.Child = _lblVersionText;
            vRow.Margin = new Thickness(0, 8, 0, 8);
            vRow.Children.Add(_vBadgeBorder);
            verStack.Children.Add(vRow);

            var ghRow = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            _lblGithubTag = new TextBlock { Text = I18n.T("GithubLabel"), FontSize = 12.5, Foreground = _colTextSecondary, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(_lblGithubTag, Dock.Left);
            ghRow.Children.Add(_lblGithubTag);

            _ghBadgeBorder = new Border
            {
                Background = IsDark ? new SolidColorBrush(Color.FromRgb(38, 42, 48)) : new SolidColorBrush(Color.FromRgb(240, 243, 246)),
                BorderBrush = IsDark ? new SolidColorBrush(Color.FromRgb(60, 68, 77)) : new SolidColorBrush(Color.FromRgb(209, 217, 224)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 3, 10, 3),
                HorizontalAlignment = HorizontalAlignment.Right,
                Cursor = Cursors.Hand,
                ToolTip = "https://github.com/ahmetbeniz/HostsManager"
            };
            _lblGithubText = new TextBlock
            {
                Text = "🔗 ahmetbeniz/HostsManager",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = IsDark ? new SolidColorBrush(Color.FromRgb(88, 166, 255)) : new SolidColorBrush(Color.FromRgb(9, 105, 218))
            };
            _ghBadgeBorder.Child = _lblGithubText;

            _ghBadgeBorder.MouseEnter += (s, e) =>
            {
                _ghBadgeBorder.Background = IsDark ? new SolidColorBrush(Color.FromRgb(48, 54, 61)) : new SolidColorBrush(Color.FromRgb(225, 232, 240));
            };
            _ghBadgeBorder.MouseLeave += (s, e) =>
            {
                _ghBadgeBorder.Background = IsDark ? new SolidColorBrush(Color.FromRgb(38, 42, 48)) : new SolidColorBrush(Color.FromRgb(240, 243, 246));
            };
            _ghBadgeBorder.MouseLeftButtonUp += (s, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo("https://github.com/ahmetbeniz/HostsManager") { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show("GitHub bağlantısı açılamadı: " + ex.Message, "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            ghRow.Children.Add(_ghBadgeBorder);
            verStack.Children.Add(ghRow);

            verCard.Child = verStack;
            Grid.SetRow(verCard, 0);
            Grid.SetColumn(verCard, 0);
            settingsGrid.Children.Add(verCard);

            // --- Row 0, Right: System Tray Card (To the right of About) ---
            var trayCard = CreateSettingsCard();
            trayCard.Margin = new Thickness(0, 0, 0, 12);
            var trayStack = new StackPanel();

            _lblTrayHeader = new TextBlock
            {
                Text = I18n.T("TrayCardTitle"),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = _colTextPrimary,
                Margin = new Thickness(0, 0, 0, 4)
            };
            _lblTrayDesc = new TextBlock
            {
                Text = I18n.T("TrayCardDesc"),
                FontSize = 12,
                Foreground = _colTextSecondary,
                Margin = new Thickness(0, 0, 0, 12)
            };
            trayStack.Children.Add(_lblTrayHeader);
            trayStack.Children.Add(_lblTrayDesc);

            _chkMinimizeToTray = new CheckBox
            {
                Content = "  " + I18n.T("TrayOptionMinimize"),
                FontSize = 13,
                Foreground = _colTextPrimary,
                IsChecked = _settings.MinimizeToTray,
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = Cursors.Hand
            };
            _chkMinimizeToTray.Click += (s, e) =>
            {
                _settings.MinimizeToTray = _chkMinimizeToTray.IsChecked == true;
                SaveSettings();
            };
            trayStack.Children.Add(_chkMinimizeToTray);

            _chkCloseToTray = new CheckBox
            {
                Content = "  " + I18n.T("TrayOptionClose"),
                FontSize = 13,
                Foreground = _colTextPrimary,
                IsChecked = _settings.CloseToTray,
                Cursor = Cursors.Hand
            };
            _chkCloseToTray.Click += (s, e) =>
            {
                _settings.CloseToTray = _chkCloseToTray.IsChecked == true;
                SaveSettings();
            };
            trayStack.Children.Add(_chkCloseToTray);

            trayCard.Child = trayStack;
            Grid.SetRow(trayCard, 0);
            Grid.SetColumn(trayCard, 2);
            settingsGrid.Children.Add(trayCard);

            // --- Row 1, Left: Appearance & Theme Card ---
            var themeCard = CreateSettingsCard();
            themeCard.Margin = new Thickness(0, 0, 0, 12);
            var themeStack = new StackPanel();

            _lblThemeHeader = new TextBlock
            {
                Text = I18n.T("ThemeCardTitle"),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = _colTextPrimary,
                Margin = new Thickness(0, 0, 0, 4)
            };
            _lblThemeDesc = new TextBlock
            {
                Text = I18n.T("ThemeCardDesc"),
                FontSize = 12,
                Foreground = _colTextSecondary,
                Margin = new Thickness(0, 0, 0, 12)
            };
            themeStack.Children.Add(_lblThemeHeader);
            themeStack.Children.Add(_lblThemeDesc);

            var themeOptions = new StackPanel { Orientation = Orientation.Horizontal };

            _rbThemeLight = new RadioButton
            {
                Content = I18n.T("ThemeLight"),
                FontSize = 13,
                Foreground = _colTextPrimary,
                Margin = new Thickness(0, 0, 16, 0),
                IsChecked = (_settings.Theme == "Light"),
                Cursor = Cursors.Hand
            };
            _rbThemeLight.Checked += (s, e) =>
            {
                _settings.Theme = "Light";
                SaveSettings();
                ApplyThemeColors();
            };
            themeOptions.Children.Add(_rbThemeLight);

            _rbThemeDark = new RadioButton
            {
                Content = I18n.T("ThemeDark"),
                FontSize = 13,
                Foreground = _colTextPrimary,
                Margin = new Thickness(0, 0, 16, 0),
                IsChecked = (_settings.Theme == "Dark"),
                Cursor = Cursors.Hand
            };
            _rbThemeDark.Checked += (s, e) =>
            {
                _settings.Theme = "Dark";
                SaveSettings();
                ApplyThemeColors();
            };
            themeOptions.Children.Add(_rbThemeDark);

            _rbThemeSystem = new RadioButton
            {
                Content = I18n.T("ThemeSystem"),
                FontSize = 13,
                Foreground = _colTextPrimary,
                IsChecked = (_settings.Theme == "System"),
                Cursor = Cursors.Hand
            };
            _rbThemeSystem.Checked += (s, e) =>
            {
                _settings.Theme = "System";
                SaveSettings();
                ApplyThemeColors();
            };
            themeOptions.Children.Add(_rbThemeSystem);

            themeStack.Children.Add(themeOptions);
            themeCard.Child = themeStack;
            Grid.SetRow(themeCard, 1);
            Grid.SetColumn(themeCard, 0);
            settingsGrid.Children.Add(themeCard);

            // --- Row 1, Right: Language Selection Card (To the right of Appearance & Theme) ---
            var langCard = CreateSettingsCard();
            langCard.Margin = new Thickness(0, 0, 0, 12);
            var langStack = new StackPanel();

            _lblLangHeader = new TextBlock
            {
                Text = I18n.T("LanguageCardTitle"),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = _colTextPrimary,
                Margin = new Thickness(0, 0, 0, 4)
            };
            _lblLangDesc = new TextBlock
            {
                Text = I18n.T("LanguageCardDesc"),
                FontSize = 12,
                Foreground = _colTextSecondary,
                Margin = new Thickness(0, 0, 0, 12)
            };
            langStack.Children.Add(_lblLangHeader);
            langStack.Children.Add(_lblLangDesc);

            _cmbLanguages = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FontSize = 13,
                Padding = new Thickness(8, 6, 8, 6),
                Background = _colInputBg,
                Foreground = _colTextPrimary,
                BorderBrush = _colInputBorder,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };

            _cmbLanguages.SelectionChanged += (s, e) =>
            {
                if (_isUpdatingLangCombo) return;
                var selectedItem = _cmbLanguages.SelectedItem as ComboBoxItem;
                if (selectedItem != null && selectedItem.Tag != null)
                {
                    string code = selectedItem.Tag.ToString();
                    if (!string.Equals(_settings.Language, code, StringComparison.OrdinalIgnoreCase))
                    {
                        _settings.Language = code;
                        SaveSettings();
                        I18n.SetLanguage(code);
                        ApplyLanguage();
                    }
                }
            };

            PopulateLanguageComboBox();
            langStack.Children.Add(_cmbLanguages);

            langCard.Child = langStack;
            Grid.SetRow(langCard, 1);
            Grid.SetColumn(langCard, 2);
            settingsGrid.Children.Add(langCard);

            mainStack.Children.Add(settingsGrid);

            scroll.Content = mainStack;
            return scroll;
        }

        private Border CreateSettingsCard()
        {
            var card = new Border
            {
                Background = _colCardBg,
                BorderBrush = _colCardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(20, 16, 20, 16),
                Margin = new Thickness(0, 0, 0, 12)
            };
            card.Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = IsDark ? 0.2 : 0.04, Color = Colors.Black };
            _settingsCards.Add(card);
            return card;
        }

        private void PopulateLanguageComboBox()
        {
            if (_cmbLanguages == null) return;

            _isUpdatingLangCombo = true;
            _cmbLanguages.Items.Clear();

            var availableLanguages = I18n.GetAvailableLanguages();
            int selectedIndex = 0;

            for (int i = 0; i < availableLanguages.Count; i++)
            {
                var lang = availableLanguages[i];
                var cbi = new ComboBoxItem
                {
                    Content = lang.DisplayName,
                    Tag = lang.Code,
                    FontSize = 13,
                    Padding = new Thickness(6, 4, 6, 4),
                    Foreground = _colTextPrimary,
                    Background = _colCardBg
                };

                _cmbLanguages.Items.Add(cbi);

                if (string.Equals(_settings.Language, lang.Code, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = i;
                }
            }

            if (_cmbLanguages.Items.Count > 0)
            {
                _cmbLanguages.SelectedIndex = selectedIndex;
            }

            _isUpdatingLangCombo = false;
        }

        private void ApplyLanguage()
        {
            Title = I18n.T("AppTitle");
            _lblTitle.Text = I18n.T("AppTitle");
            _lblSubtitle.Text = I18n.T("AppSubtitle");
            _lblAdminStatus.Text = IsAdministrator() ? I18n.T("AdminActive") : I18n.T("AdminRequired");
            _btnFlush.Content = I18n.T("FlushDns");

            string[] tabKeys = { "TabRecords", "TabRaw", "TabBackups", "TabSettings" };
            for (int i = 0; i < tabKeys.Length && i < _navLabels.Count; i++)
            {
                _navLabels[i].Text = I18n.T(tabKeys[i]);
            }

            _lblAddTitle.Text = I18n.T("AddCardTitle");
            _lblIpHeader.Text = I18n.T("IpLabel");
            _lblDomainHeader.Text = I18n.T("DomainLabel");
            _lblCommentHeader.Text = I18n.T("CommentLabel");
            _btnAddRecord.Content = I18n.T("AddRecordBtn");
            _btnAddComment.Content = I18n.T("AddCommentBtn");

            string[] filterKeys = { "FilterAll", "FilterActive", "FilterPassive", "FilterComments" };
            for (int i = 0; i < filterKeys.Length && i < _filterButtons.Count; i++)
            {
                _filterButtons[i].Content = I18n.T(filterKeys[i]);
            }

            _chkSelectAll.Content = "  " + I18n.T("BulkSelectAll");
            _btnBulkActivate.Content = I18n.T("BulkActivate");
            _btnBulkDeactivate.Content = I18n.T("BulkDeactivate");
            _btnBulkDelete.Content = I18n.T("BulkDelete");
            UpdateBulkToolbarState();

            _lblRawInfo.Text = I18n.T("RawInfoText");
            _btnRawReload.Content = I18n.T("BtnReload");
            _btnRawSave.Content = I18n.T("BtnSave");

            _lblBackupsTitle.Text = I18n.T("BackupsTitle");
            _btnOpenBackupFolder.Content = I18n.T("BtnOpenFolder");
            _btnCreateBackup.Content = I18n.T("BtnNewBackup");

            if (_lblSettingsTitle != null)
            {
                _lblSettingsTitle.Text = I18n.T("SettingsTitle");
                _lblSettingsSubtitle.Text = I18n.T("SettingsSubtitle");
                _lblVersionHeader.Text = I18n.T("VersionCardTitle");
                _lblVersionTag.Text = I18n.T("VersionLabel");
                _lblVersionText.Text = I18n.T("VersionValue");
                if (_lblGithubTag != null) _lblGithubTag.Text = I18n.T("GithubLabel");
                _lblThemeHeader.Text = I18n.T("ThemeCardTitle");
                _lblThemeDesc.Text = I18n.T("ThemeCardDesc");
                _rbThemeLight.Content = I18n.T("ThemeLight");
                _rbThemeDark.Content = I18n.T("ThemeDark");
                _rbThemeSystem.Content = I18n.T("ThemeSystem");
                _lblLangHeader.Text = I18n.T("LanguageCardTitle");
                _lblLangDesc.Text = I18n.T("LanguageCardDesc");
                PopulateLanguageComboBox();
                _lblTrayHeader.Text = I18n.T("TrayCardTitle");
                _lblTrayDesc.Text = I18n.T("TrayCardDesc");
                _chkMinimizeToTray.Content = "  " + I18n.T("TrayOptionMinimize");
                _chkCloseToTray.Content = "  " + I18n.T("TrayOptionClose");
            }

            _lblStatusBar.Text = I18n.T("ReadyStatus");
            _lblFilePath.Text = I18n.T("FileLocation", HostsPath);

            RenderRecords();
            RefreshBackups();
        }

        // -------------------------------------------------------------
        // HOSTS PARSING & CONFLICT DETECTION
        // -------------------------------------------------------------
        private void LoadHostsFile()
        {
            if (!File.Exists(HostsPath))
            {
                ShowInfoBar("Hata: Hosts dosyası bulunamadı: " + HostsPath, true);
                return;
            }

            try
            {
                string content = File.ReadAllText(HostsPath, Encoding.UTF8);
                _txtRawEditor.Text = content;

                _allRecords.Clear();
                var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

                foreach (var line in lines)
                {
                    string sline = line.Trim();
                    if (string.IsNullOrEmpty(sline)) continue;

                    if (sline.StartsWith("#"))
                    {
                        string uncommented = sline.TrimStart('#').Trim();
                        var parts = uncommented.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && IsIpLike(parts[0]))
                        {
                            _allRecords.Add(new HostRecord
                            {
                                Type = "Pasif",
                                IP = parts[0],
                                Domain = string.Join(" ", parts.Skip(1).ToArray()),
                                RawLine = line,
                                BadgeText = I18n.T("BadgePassive"),
                                BadgeBg = IsDark ? new SolidColorBrush(Color.FromRgb(58, 42, 20)) : new SolidColorBrush(Color.FromRgb(254, 246, 230)),
                                BadgeFg = new SolidColorBrush(Color.FromRgb(216, 59, 1)),
                                IpColor = new SolidColorBrush(Color.FromRgb(140, 145, 155)),
                                IsEntry = true,
                                ToggleButtonText = I18n.T("BtnActivate")
                            });
                        }
                        else
                        {
                            _allRecords.Add(new HostRecord
                            {
                                Type = "Yorum",
                                IP = "",
                                Domain = sline,
                                RawLine = line,
                                BadgeText = I18n.T("BadgeComment"),
                                BadgeBg = IsDark ? new SolidColorBrush(Color.FromRgb(40, 44, 50)) : new SolidColorBrush(Color.FromRgb(238, 240, 244)),
                                BadgeFg = _colTextSecondary,
                                IpColor = Brushes.Gray,
                                IsEntry = false,
                                ToggleButtonText = ""
                            });
                        }
                    }
                    else
                    {
                        var parts = sline.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            _allRecords.Add(new HostRecord
                            {
                                Type = "Aktif",
                                IP = parts[0],
                                Domain = string.Join(" ", parts.Skip(1).ToArray()),
                                RawLine = line,
                                BadgeText = I18n.T("BadgeActive"),
                                BadgeBg = IsDark ? new SolidColorBrush(Color.FromRgb(20, 50, 32)) : new SolidColorBrush(Color.FromRgb(223, 246, 221)),
                                BadgeFg = new SolidColorBrush(Color.FromRgb(16, 124, 65)),
                                IpColor = new SolidColorBrush(Color.FromRgb(0, 120, 215)),
                                IsEntry = true,
                                ToggleButtonText = I18n.T("BtnDeactivate")
                            });
                        }
                        else
                        {
                            _allRecords.Add(new HostRecord
                            {
                                Type = "Yorum",
                                IP = "",
                                Domain = sline,
                                RawLine = line,
                                BadgeText = I18n.T("BadgeComment"),
                                BadgeBg = IsDark ? new SolidColorBrush(Color.FromRgb(40, 44, 50)) : new SolidColorBrush(Color.FromRgb(238, 240, 244)),
                                BadgeFg = _colTextSecondary,
                                IpColor = Brushes.Gray,
                                IsEntry = false,
                                ToggleButtonText = ""
                            });
                        }
                    }
                }

                DetectConflicts();

                if (_navBadgeTexts.Count >= 1)
                {
                    _navBadgeTexts[0].Text = _allRecords.Count.ToString();
                }

                UpdateBulkToolbarState();
                RenderRecords();
                SetStatus(I18n.T("HostsLoadedStatus", _allRecords.Count));
            }
            catch (Exception ex)
            {
                ShowInfoBar("Dosya okuma hatası: " + ex.Message, true);
            }
        }

        private void DetectConflicts()
        {
            var entries = _allRecords.Where(r => r.IsEntry).ToList();
            var domainMap = new Dictionary<string, List<HostRecord>>(StringComparer.OrdinalIgnoreCase);

            foreach (var rec in entries)
            {
                rec.HasConflict = false;
                rec.IsDuplicate = false;
                rec.ConflictDetail = "";

                string cleanDomain = (rec.Domain ?? "").Trim();
                if (string.IsNullOrEmpty(cleanDomain)) continue;

                var subDomains = cleanDomain.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                foreach (var d in subDomains)
                {
                    string key = d.ToLower();
                    if (!domainMap.ContainsKey(key))
                    {
                        domainMap[key] = new List<HostRecord>();
                    }
                    domainMap[key].Add(rec);
                }
            }

            foreach (var kvp in domainMap)
            {
                var list = kvp.Value;
                if (list.Count > 1)
                {
                    var distinctIps = list.Select(r => r.IP).Distinct().ToList();
                    bool hasDifferentIps = distinctIps.Count > 1;

                    foreach (var r in list)
                    {
                        if (hasDifferentIps)
                        {
                            r.HasConflict = true;
                            r.ConflictDetail = string.Join(", ", distinctIps.ToArray());
                        }
                        else
                        {
                            r.IsDuplicate = true;
                        }
                    }
                }
            }
        }

        // -------------------------------------------------------------
        // RECORD CARD RENDERING & ACTIONS
        // -------------------------------------------------------------
        private void RenderRecords()
        {
            _panelCards.Children.Clear();

            var filtered = _allRecords.Where(r =>
            {
                if (_activeFilter == "Aktif" && r.Type != "Aktif") return false;
                if (_activeFilter == "Pasif" && r.Type != "Pasif") return false;
                if (_activeFilter == "Yorumlar" && r.Type != "Yorum") return false;

                if (!string.IsNullOrEmpty(_searchQuery))
                {
                    string q = _searchQuery.ToLower();
                    bool matchIp = (r.IP ?? "").ToLower().Contains(q);
                    bool matchDomain = (r.Domain ?? "").ToLower().Contains(q);
                    bool matchRaw = (r.RawLine ?? "").ToLower().Contains(q);
                    if (!matchIp && !matchDomain && !matchRaw) return false;
                }
                return true;
            }).ToList();

            int totalActive = _allRecords.Count(r => r.Type == "Aktif");
            _lblStats.Text = I18n.T("StatsFormat", filtered.Count, _allRecords.Count, totalActive);

            if (filtered.Count == 0)
            {
                var emptyNotice = new TextBlock
                {
                    Text = I18n.T("NoRecordsFound"),
                    FontSize = 13,
                    FontStyle = FontStyles.Italic,
                    Foreground = Brushes.Gray,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 40, 0, 0)
                };
                _panelCards.Children.Add(emptyNotice);
                return;
            }

            foreach (var rec in filtered)
            {
                if (rec.Type == "Aktif")
                {
                    rec.BadgeText = I18n.T("BadgeActive");
                    rec.ToggleButtonText = I18n.T("BtnDeactivate");
                }
                else if (rec.Type == "Pasif")
                {
                    rec.BadgeText = I18n.T("BadgePassive");
                    rec.ToggleButtonText = I18n.T("BtnActivate");
                }
                else
                {
                    rec.BadgeText = I18n.T("BadgeComment");
                }

                _panelCards.Children.Add(CreateRecordCard(rec));
            }
        }

        private Border CreateRecordCard(HostRecord rec)
        {
            var card = new Border
            {
                Background = _colCardBg,
                BorderBrush = _colCardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 6)
            };

            var mainDock = new DockPanel();

            // 1. Left: CheckBox for Bulk Selection
            var chk = new CheckBox
            {
                IsChecked = rec.IsSelected,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Cursor = Cursors.Hand
            };
            chk.Click += (s, e) =>
            {
                rec.IsSelected = chk.IsChecked == true;
                UpdateBulkToolbarState();
            };
            DockPanel.SetDock(chk, Dock.Left);
            mainDock.Children.Add(chk);

            // 2. Left: Status Badge
            var badgeBorder = new Border
            {
                Background = rec.BadgeBg,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 4, 8, 4),
                Width = 78,
                Margin = new Thickness(0, 0, 10, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var badgeText = new TextBlock
            {
                Text = rec.BadgeText,
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = rec.BadgeFg,
                TextAlignment = TextAlignment.Center
            };
            badgeBorder.Child = badgeText;
            DockPanel.SetDock(badgeBorder, Dock.Left);
            mainDock.Children.Add(badgeBorder);

            // 3. Right: Action Buttons (Toggle, Edit, Delete)
            var rightStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            if (!rec.IsEditing)
            {
                if (rec.IsEntry)
                {
                    bool isActive = rec.Type == "Aktif";
                    var btnToggle = new Button
                    {
                        Template = GetFluentButtonTemplate(),
                        Content = rec.ToggleButtonText,
                        FontSize = 11,
                        Padding = new Thickness(10, 4, 10, 4),
                        Margin = new Thickness(0, 0, 6, 0),
                        Background = isActive
                            ? (IsDark ? new SolidColorBrush(Color.FromRgb(40, 46, 54)) : new SolidColorBrush(Color.FromRgb(245, 247, 250)))
                            : (IsDark ? new SolidColorBrush(Color.FromRgb(22, 52, 34)) : new SolidColorBrush(Color.FromRgb(223, 246, 221))),
                        Foreground = isActive
                            ? (IsDark ? new SolidColorBrush(Color.FromRgb(226, 232, 240)) : new SolidColorBrush(Color.FromRgb(40, 45, 50)))
                            : (IsDark ? new SolidColorBrush(Color.FromRgb(74, 222, 128)) : new SolidColorBrush(Color.FromRgb(16, 124, 65))),
                        BorderBrush = isActive
                            ? (IsDark ? new SolidColorBrush(Color.FromRgb(58, 66, 78)) : new SolidColorBrush(Color.FromRgb(220, 224, 230)))
                            : (IsDark ? new SolidColorBrush(Color.FromRgb(37, 96, 63)) : new SolidColorBrush(Color.FromRgb(189, 235, 194))),
                        BorderThickness = new Thickness(1),
                        Cursor = Cursors.Hand
                    };
                    btnToggle.Click += (s, e) => ToggleRecord(rec);
                    rightStack.Children.Add(btnToggle);

                    // ✏️ In-place Edit Button (To the left of Delete button)
                    var btnEdit = new Button
                    {
                        Template = GetFluentButtonTemplate(),
                        Content = "✏️",
                        ToolTip = I18n.T("BtnEdit"),
                        FontSize = 12,
                        Width = 32,
                        Height = 28,
                        Margin = new Thickness(0, 0, 6, 0),
                        Background = IsDark ? new SolidColorBrush(Color.FromRgb(42, 48, 58)) : new SolidColorBrush(Color.FromRgb(240, 243, 248)),
                        Foreground = IsDark ? new SolidColorBrush(Color.FromRgb(240, 243, 246)) : new SolidColorBrush(Color.FromRgb(30, 32, 36)),
                        BorderBrush = IsDark ? new SolidColorBrush(Color.FromRgb(65, 74, 88)) : new SolidColorBrush(Color.FromRgb(215, 220, 228)),
                        BorderThickness = new Thickness(1),
                        Cursor = Cursors.Hand
                    };
                    btnEdit.Click += (s, e) =>
                    {
                        rec.IsEditing = true;
                        RenderRecords();
                    };
                    rightStack.Children.Add(btnEdit);
                }

                var btnDel = new Button
                {
                    Template = GetFluentButtonTemplate(),
                    Content = "🗑️",
                    ToolTip = I18n.T("BtnDelete"),
                    FontSize = 12,
                    Width = 32,
                    Height = 28,
                    Background = IsDark ? new SolidColorBrush(Color.FromRgb(56, 26, 30)) : new SolidColorBrush(Color.FromRgb(253, 231, 233)),
                    Foreground = IsDark ? new SolidColorBrush(Color.FromRgb(248, 113, 113)) : new SolidColorBrush(Color.FromRgb(164, 38, 44)),
                    BorderBrush = IsDark ? new SolidColorBrush(Color.FromRgb(102, 38, 45)) : new SolidColorBrush(Color.FromRgb(240, 200, 202)),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand
                };
                btnDel.Click += (s, e) => DeleteRecord(rec);
                rightStack.Children.Add(btnDel);
            }
            else
            {
                // In-place Editing Save & Cancel Buttons
                var btnSaveEdit = new Button
                {
                    Template = GetFluentButtonTemplate(),
                    Content = "💾 " + I18n.T("BtnSaveEdit"),
                    FontSize = 11,
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(0, 0, 6, 0),
                    Background = new SolidColorBrush(Color.FromRgb(0, 120, 215)),
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand
                };
                rightStack.Children.Add(btnSaveEdit);

                var btnCancelEdit = new Button
                {
                    Template = GetFluentButtonTemplate(),
                    Content = "✕ " + I18n.T("BtnCancelEdit"),
                    FontSize = 11,
                    Padding = new Thickness(8, 4, 8, 4),
                    Background = IsDark ? new SolidColorBrush(Color.FromRgb(40, 45, 52)) : new SolidColorBrush(Color.FromRgb(240, 243, 248)),
                    Foreground = _colTextPrimary,
                    BorderBrush = _colCardBorder,
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand
                };
                btnCancelEdit.Click += (s, e) =>
                {
                    rec.IsEditing = false;
                    RenderRecords();
                };
                rightStack.Children.Add(btnCancelEdit);

                // Save action handler attached later
                btnSaveEdit.Tag = rec;
            }

            DockPanel.SetDock(rightStack, Dock.Right);
            mainDock.Children.Add(rightStack);

            // 4. Middle: IP & Domain & Quick Action Tools (🌐 Browser & 📶 Ping)
            if (!rec.IsEditing)
            {
                var middleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                if (!string.IsNullOrEmpty(rec.IP))
                {
                    var txtIp = new TextBlock
                    {
                        Text = rec.IP,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 13,
                        FontWeight = FontWeights.Bold,
                        Foreground = rec.IpColor,
                        Width = 140,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    middleStack.Children.Add(txtIp);

                    var txtDomain = new TextBlock
                    {
                        Text = rec.Domain,
                        FontSize = 13,
                        Foreground = rec.Type == "Aktif" ? _colTextPrimary : Brushes.Gray,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 10, 0)
                    };
                    middleStack.Children.Add(txtDomain);

                    // 🌐 Open in Browser Button (Requested!)
                    string primaryDomain = (rec.Domain ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (!string.IsNullOrEmpty(primaryDomain))
                    {
                        var btnWeb = new Button
                        {
                            Template = GetFluentButtonTemplate(),
                            Content = "🌐",
                            ToolTip = I18n.T("BtnOpenBrowser") + " (http://" + primaryDomain + ")",
                            FontSize = 12,
                            Width = 28,
                            Height = 26,
                            Margin = new Thickness(0, 0, 6, 0),
                            Background = IsDark ? new SolidColorBrush(Color.FromRgb(42, 48, 58)) : new SolidColorBrush(Color.FromRgb(240, 244, 250)),
                            Foreground = IsDark ? new SolidColorBrush(Color.FromRgb(96, 165, 250)) : new SolidColorBrush(Color.FromRgb(0, 103, 192)),
                            BorderBrush = IsDark ? new SolidColorBrush(Color.FromRgb(65, 74, 88)) : new SolidColorBrush(Color.FromRgb(215, 225, 238)),
                            BorderThickness = new Thickness(1),
                            Cursor = Cursors.Hand
                        };
                        btnWeb.Click += (s, e) =>
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo("http://" + primaryDomain) { UseShellExecute = true });
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show("Tarayıcı açılamadı: " + ex.Message, "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                            }
                        };
                        middleStack.Children.Add(btnWeb);

                        // 📶 Live Ping Test Button (Requested!)
                        var btnPing = new Button
                        {
                            Template = GetFluentButtonTemplate(),
                            Content = "📶",
                            ToolTip = I18n.T("BtnPing"),
                            FontSize = 12,
                            Width = 28,
                            Height = 26,
                            Margin = new Thickness(0, 0, 6, 0),
                            Background = IsDark ? new SolidColorBrush(Color.FromRgb(42, 48, 58)) : new SolidColorBrush(Color.FromRgb(240, 244, 250)),
                            Foreground = IsDark ? new SolidColorBrush(Color.FromRgb(52, 211, 153)) : new SolidColorBrush(Color.FromRgb(16, 124, 65)),
                            BorderBrush = IsDark ? new SolidColorBrush(Color.FromRgb(65, 74, 88)) : new SolidColorBrush(Color.FromRgb(215, 225, 238)),
                            BorderThickness = new Thickness(1),
                            Cursor = Cursors.Hand
                        };

                        var lblPingStatus = new TextBlock
                        {
                            Text = rec.PingResult ?? "",
                            FontSize = 11,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = rec.PingColor ?? Brushes.Gray,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(2, 0, 6, 0),
                            Visibility = string.IsNullOrEmpty(rec.PingResult) ? Visibility.Collapsed : Visibility.Visible
                        };

                        btnPing.Click += (s, e) =>
                        {
                            lblPingStatus.Text = I18n.T("PingTesting");
                            lblPingStatus.Foreground = Brushes.Gray;
                            lblPingStatus.Visibility = Visibility.Visible;

                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                try
                                {
                                    using (var ping = new Ping())
                                    {
                                        var reply = ping.Send(primaryDomain, 1200);
                                        Dispatcher.Invoke((Action)(() =>
                                        {
                                            if (reply != null && reply.Status == IPStatus.Success)
                                            {
                                                rec.PingResult = string.Format("{0}ms", reply.RoundtripTime);
                                                rec.PingColor = new SolidColorBrush(Color.FromRgb(16, 124, 65));
                                            }
                                            else
                                            {
                                                rec.PingResult = I18n.T("PingFailed");
                                                rec.PingColor = new SolidColorBrush(Color.FromRgb(216, 59, 1));
                                            }
                                            lblPingStatus.Text = rec.PingResult;
                                            lblPingStatus.Foreground = rec.PingColor;
                                        }));
                                    }
                                }
                                catch
                                {
                                    Dispatcher.Invoke((Action)(() =>
                                    {
                                        rec.PingResult = I18n.T("PingFailed");
                                        rec.PingColor = new SolidColorBrush(Color.FromRgb(216, 59, 1));
                                        lblPingStatus.Text = rec.PingResult;
                                        lblPingStatus.Foreground = rec.PingColor;
                                    }));
                                }
                            });
                        };

                        middleStack.Children.Add(btnPing);
                        middleStack.Children.Add(lblPingStatus);
                    }

                    // Conflict / Duplicate Badge (To the right of Ping button)
                    if (rec.HasConflict || rec.IsDuplicate)
                    {
                        var conflictBorder = new Border
                        {
                            Background = rec.HasConflict
                                ? (IsDark ? new SolidColorBrush(Color.FromRgb(64, 40, 15)) : new SolidColorBrush(Color.FromRgb(254, 240, 215)))
                                : (IsDark ? new SolidColorBrush(Color.FromRgb(55, 42, 20)) : new SolidColorBrush(Color.FromRgb(254, 246, 230))),
                            BorderBrush = rec.HasConflict
                                ? (IsDark ? new SolidColorBrush(Color.FromRgb(180, 83, 9)) : new SolidColorBrush(Color.FromRgb(245, 158, 11)))
                                : (IsDark ? new SolidColorBrush(Color.FromRgb(160, 90, 15)) : new SolidColorBrush(Color.FromRgb(217, 119, 6))),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(4),
                            Padding = new Thickness(6, 2, 6, 2),
                            Margin = new Thickness(6, 0, 8, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                            ToolTip = rec.HasConflict ? I18n.T("ConflictTooltip", rec.ConflictDetail) : I18n.T("DuplicateTooltip")
                        };
                        var conflictText = new TextBlock
                        {
                            Text = rec.HasConflict ? I18n.T("BadgeConflict") : I18n.T("BadgeDuplicate"),
                            FontSize = 9.5,
                            FontWeight = FontWeights.Bold,
                            Foreground = IsDark ? new SolidColorBrush(Color.FromRgb(251, 191, 36)) : new SolidColorBrush(Color.FromRgb(180, 83, 9))
                        };
                        conflictBorder.Child = conflictText;
                        middleStack.Children.Add(conflictBorder);
                    }
                }
                else
                {
                    var txtCom = new TextBlock
                    {
                        Text = rec.Domain,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 12,
                        FontStyle = FontStyles.Italic,
                        Foreground = _colTextSecondary,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    middleStack.Children.Add(txtCom);
                }

                mainDock.Children.Add(middleStack);
            }
            else
            {
                // In-place Editing UI (IP & Domain TextBoxes)
                var editStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

                var txtEditIp = CreateFluentTextBox(rec.IP);
                txtEditIp.FontFamily = new FontFamily("Consolas");
                txtEditIp.FontWeight = FontWeights.Bold;
                txtEditIp.Width = 140;
                txtEditIp.Margin = new Thickness(0, 0, 10, 0);
                editStack.Children.Add(txtEditIp);

                var txtEditDomain = CreateFluentTextBox(rec.Domain);
                txtEditDomain.Width = 240;
                txtEditDomain.Loaded += (s, e) =>
                {
                    txtEditDomain.Focus();
                    txtEditDomain.SelectAll();
                };
                editStack.Children.Add(txtEditDomain);

                KeyEventHandler onKey = (s, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        string newIp = txtEditIp.Text.Trim();
                        string newDomain = txtEditDomain.Text.Trim();
                        SaveInPlaceEdit(rec, newIp, newDomain);
                    }
                    else if (e.Key == Key.Escape)
                    {
                        rec.IsEditing = false;
                        RenderRecords();
                    }
                };
                txtEditIp.KeyDown += onKey;
                txtEditDomain.KeyDown += onKey;

                mainDock.Children.Add(editStack);

                // Wire save button
                var btnSave = rightStack.Children.OfType<Button>().FirstOrDefault(b => b.Tag == rec);
                if (btnSave != null)
                {
                    btnSave.Click += (s, e) =>
                    {
                        string newIp = txtEditIp.Text.Trim();
                        string newDomain = txtEditDomain.Text.Trim();
                        SaveInPlaceEdit(rec, newIp, newDomain);
                    };
                }
            }

            card.Child = mainDock;
            return card;
        }

        private void SaveInPlaceEdit(HostRecord rec, string newIp, string newDomain)
        {
            if (string.IsNullOrEmpty(newIp))
            {
                MessageBox.Show(I18n.T("MissingIpMsg"), I18n.T("MissingInfoTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrEmpty(newDomain))
            {
                MessageBox.Show(I18n.T("MissingDomainMsg"), I18n.T("MissingInfoTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string rawOld = rec.RawLine.Trim();
            string newFormatted = string.Format("{0,-16}{1}", newIp, newDomain);
            if (rec.Type == "Pasif")
            {
                newFormatted = "# " + newFormatted;
            }

            try
            {
                var lines = File.ReadAllLines(HostsPath, Encoding.UTF8).ToList();
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i].Trim() == rawOld)
                    {
                        lines[i] = newFormatted;
                        break;
                    }
                }

                WriteHostsFile(string.Join("\r\n", lines.ToArray()) + "\r\n");
                rec.IsEditing = false;
                LoadHostsFile();
                ShowInfoBar(I18n.T("SaveSuccessMsg"), false);
            }
            catch (UnauthorizedAccessException)
            {
                HandleUnauthorized();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Düzenleme hatası: " + ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void AddEntry()
        {
            string ip = _txtIp.Text.Trim();
            string domain = _txtDomain.Text.Trim();

            if (string.IsNullOrEmpty(ip))
            {
                MessageBox.Show(I18n.T("MissingIpMsg"), I18n.T("MissingInfoTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrEmpty(domain))
            {
                MessageBox.Show(I18n.T("MissingDomainMsg"), I18n.T("MissingInfoTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string formattedLine = string.Format("{0,-16}{1}", ip, domain);
            AppendToHosts(formattedLine);
            _txtDomain.Text = "";
        }

        private void AddComment()
        {
            string comment = _txtComment.Text.Trim();
            if (string.IsNullOrEmpty(comment))
            {
                MessageBox.Show(I18n.T("MissingCommentMsg"), I18n.T("MissingInfoTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!comment.StartsWith("#"))
            {
                comment = "# " + comment;
            }

            AppendToHosts(comment);
            _txtComment.Text = "";
        }

        private void AppendToHosts(string line)
        {
            try
            {
                string content = File.ReadAllText(HostsPath, Encoding.UTF8);
                if (!string.IsNullOrEmpty(content) && !content.EndsWith("\n"))
                {
                    content += "\r\n";
                }
                content += line + "\r\n";

                WriteHostsFile(content);
                LoadHostsFile();
                ShowInfoBar(I18n.T("AddRecordBtn") + ": " + line, false);
            }
            catch (UnauthorizedAccessException)
            {
                HandleUnauthorized();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Yazma hatası: " + ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ToggleRecord(HostRecord rec)
        {
            string raw = rec.RawLine.Trim();
            string newTarget;

            if (rec.Type == "Aktif")
            {
                newTarget = "# " + raw;
            }
            else
            {
                newTarget = raw.TrimStart('#').Trim();
            }

            try
            {
                string content = File.ReadAllText(HostsPath, Encoding.UTF8);
                if (content.Contains(raw))
                {
                    int index = content.IndexOf(raw);
                    content = content.Substring(0, index) + newTarget + content.Substring(index + raw.Length);

                    WriteHostsFile(content);
                    LoadHostsFile();
                    ShowInfoBar(I18n.T("ReadyStatus"), false);
                }
            }
            catch (UnauthorizedAccessException)
            {
                HandleUnauthorized();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Güncelleme hatası: " + ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DeleteRecord(HostRecord rec)
        {
            string raw = rec.RawLine.Trim();
            var result = MessageBox.Show(string.Format("{0}\n\n{1}", I18n.T("DeleteConfirmMsg"), raw),
                                         I18n.T("DeleteConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            try
            {
                var lines = File.ReadAllLines(HostsPath, Encoding.UTF8);
                var newLines = new List<string>();
                bool deleted = false;

                foreach (var line in lines)
                {
                    if (!deleted && line.Trim() == raw)
                    {
                        deleted = true;
                        continue;
                    }
                    newLines.Add(line);
                }

                if (deleted)
                {
                    WriteHostsFile(string.Join("\r\n", newLines.ToArray()) + "\r\n");
                    LoadHostsFile();
                    ShowInfoBar(I18n.T("ReadyStatus"), false);
                }
            }
            catch (UnauthorizedAccessException)
            {
                HandleUnauthorized();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Silme hatası: " + ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveRawText()
        {
            try
            {
                WriteHostsFile(_txtRawEditor.Text);
                LoadHostsFile();
                ShowInfoBar(I18n.T("SaveSuccessMsg"), false);
            }
            catch (UnauthorizedAccessException)
            {
                HandleUnauthorized();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Kaydetme hatası: " + ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void WriteHostsFile(string content)
        {
            if (File.Exists(HostsPath))
            {
                try
                {
                    string autoBak = Path.Combine(BackupDir, "hosts_auto_latest.bak");
                    File.Copy(HostsPath, autoBak, true);
                }
                catch { }
            }

            File.WriteAllText(HostsPath, content, Encoding.UTF8);
        }

        // -------------------------------------------------------------
        // BACKUP & RESTORE
        // -------------------------------------------------------------
        private void CreateBackup()
        {
            if (!File.Exists(HostsPath))
            {
                MessageBox.Show("Hosts dosyası bulunamadı.", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string filename = string.Format("hosts_{0}.bak", timestamp);
            string destPath = Path.Combine(BackupDir, filename);

            try
            {
                File.Copy(HostsPath, destPath, true);
                RefreshBackups();
                ShowInfoBar(I18n.T("BackupSuccessMsg", filename), false);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Yedek alınırken hata oluştu: " + ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RefreshBackups()
        {
            _panelBackups.Children.Clear();

            if (!Directory.Exists(BackupDir)) return;

            var dir = new DirectoryInfo(BackupDir);
            var files = dir.GetFiles("*.bak").OrderByDescending(f => f.LastWriteTime).ToList();

            if (_navBadgeTexts.Count >= 3)
            {
                _navBadgeTexts[2].Text = files.Count.ToString();
            }

            if (files.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text = I18n.T("NoBackupsFound"),
                    FontStyle = FontStyles.Italic,
                    Foreground = Brushes.Gray,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 40, 0, 0)
                };
                _panelBackups.Children.Add(empty);
                return;
            }

            foreach (var fi in files)
            {
                _panelBackups.Children.Add(CreateBackupCard(fi));
            }
        }

        private Border CreateBackupCard(FileInfo fi)
        {
            var card = new Border
            {
                Background = _colCardBg,
                BorderBrush = _colCardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 6)
            };

            var dock = new DockPanel();

            var rightStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var btnRestore = CreateFluentButton(I18n.T("BtnRestore"), true);
            btnRestore.Padding = new Thickness(10, 4, 10, 4);
            btnRestore.Margin = new Thickness(0, 0, 6, 0);
            btnRestore.Click += (s, e) => RestoreBackup(fi.FullName, fi.Name);
            rightStack.Children.Add(btnRestore);

            var btnDel = CreateFluentButton(I18n.T("BtnDelete"), false);
            btnDel.Padding = new Thickness(8, 4, 8, 4);
            btnDel.Click += (s, e) => DeleteBackup(fi.FullName, fi.Name);
            rightStack.Children.Add(btnDel);

            DockPanel.SetDock(rightStack, Dock.Right);
            dock.Children.Add(rightStack);

            var leftStack = new StackPanel();
            var txtName = new TextBlock
            {
                Text = fi.Name,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = _colTextPrimary
            };
            leftStack.Children.Add(txtName);

            string sizeStr = fi.Length >= 1024 ? string.Format("{0:F1} KB", fi.Length / 1024.0) : string.Format("{0} B", fi.Length);
            var txtMeta = new TextBlock
            {
                Text = I18n.T("BackupMetaFormat", string.Format("{0:yyyy-MM-dd HH:mm:ss}", fi.LastWriteTime), sizeStr),
                FontSize = 11,
                Foreground = _colTextSecondary,
                Margin = new Thickness(0, 2, 0, 0)
            };
            leftStack.Children.Add(txtMeta);

            dock.Children.Add(leftStack);
            card.Child = dock;
            return card;
        }

        private void RestoreBackup(string fullPath, string name)
        {
            var res = MessageBox.Show(string.Format("{0}\n\n{1}", I18n.T("RestoreConfirmMsg"), name),
                                      I18n.T("RestoreConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;

            try
            {
                string safetyName = string.Format("hosts_before_restore_{0:yyyy-MM-dd_HH-mm-ss}.bak", DateTime.Now);
                File.Copy(HostsPath, Path.Combine(BackupDir, safetyName), true);

                File.Copy(fullPath, HostsPath, true);
                LoadHostsFile();
                RefreshBackups();
                FlushDns(true);
                ShowInfoBar(I18n.T("RestoreSuccessMsg", name), false);
            }
            catch (UnauthorizedAccessException)
            {
                HandleUnauthorized();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Geri yükleme hatası: " + ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DeleteBackup(string fullPath, string name)
        {
            var res = MessageBox.Show(I18n.T("BackupDeleteConfirmMsg", name),
                                      I18n.T("DeleteConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;

            try
            {
                File.Delete(fullPath);
                RefreshBackups();
                SetStatus(I18n.T("ReadyStatus"));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Silme hatası: " + ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // -------------------------------------------------------------
        // HELPERS & STYLES
        // -------------------------------------------------------------
        private void FlushDns(bool silent)
        {
            try
            {
                var psi = new ProcessStartInfo("ipconfig", "/flushdns")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                var p = Process.Start(psi);
                p.WaitForExit();

                if (!silent)
                {
                    ShowInfoBar(I18n.T("FlushDnsSuccess"), false);
                }
                SetStatus(I18n.T("FlushDnsSuccess"));
            }
            catch (Exception ex)
            {
                if (!silent)
                {
                    MessageBox.Show("DNS temizlenirken hata oluştu: " + ex.Message, "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void HandleUnauthorized()
        {
            string msg = "Erişim Engellendi (Yetki Hatası)!\n\n" +
                         "Windows Hosts dosyasını düzenlemek için Yönetici İzni (Administrator) gereklidir.\n\n" +
                         "Uygulama şimdi Yönetici olarak yeniden başlatılsın mı?";
            var res = MessageBox.Show(msg, "Yönetici Yetkisi Gerekli", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res == MessageBoxResult.Yes)
            {
                RestartAsAdmin();
            }
        }

        private void ShowInfoBar(string text, bool isError)
        {
            _txtInfoBar.Text = text;
            _infoBar.Background = isError ? new SolidColorBrush(Color.FromRgb(253, 231, 233)) : new SolidColorBrush(Color.FromRgb(223, 246, 221));
            _infoBar.BorderBrush = isError ? new SolidColorBrush(Color.FromRgb(164, 38, 44)) : new SolidColorBrush(Color.FromRgb(16, 124, 65));
            _txtInfoBar.Foreground = isError ? new SolidColorBrush(Color.FromRgb(164, 38, 44)) : new SolidColorBrush(Color.FromRgb(16, 124, 65));
            _infoBar.Visibility = Visibility.Visible;
        }

        private void SetStatus(string text)
        {
            _lblStatusBar.Text = text;
        }

        private static bool IsIpLike(string val)
        {
            var parts = val.Split('.');
            if (parts.Length == 4 && parts.All(p => p.All(char.IsDigit))) return true;
            if (val.Contains(":")) return true;
            return false;
        }

        private static ControlTemplate _fluentBtnTemplate;
        private static ControlTemplate GetFluentButtonTemplate()
        {
            if (_fluentBtnTemplate != null) return _fluentBtnTemplate;

            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border), "border");
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });

            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(cp);

            template.VisualTree = border;

            var trDisabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            trDisabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
            template.Triggers.Add(trDisabled);

            var trHover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            trHover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.88));
            template.Triggers.Add(trHover);

            _fluentBtnTemplate = template;
            return _fluentBtnTemplate;
        }

        private Button CreateFluentButton(string text, bool isPrimary)
        {
            var btn = new Button
            {
                Template = GetFluentButtonTemplate(),
                Content = text,
                FontSize = 12,
                FontWeight = isPrimary ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = isPrimary ? Brushes.White : _colTextPrimary,
                Background = isPrimary ? new SolidColorBrush(Color.FromRgb(0, 120, 215)) : (IsDark ? new SolidColorBrush(Color.FromRgb(40, 46, 54)) : new SolidColorBrush(Color.FromRgb(245, 247, 250))),
                BorderBrush = isPrimary ? new SolidColorBrush(Color.FromRgb(0, 100, 190)) : _colCardBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 6, 14, 6),
                Cursor = Cursors.Hand
            };
            return btn;
        }

        private Button CreateFilterButton(string text, bool isSelected)
        {
            var btn = new Button
            {
                Template = GetFluentButtonTemplate(),
                Content = text,
                FontSize = 11,
                Margin = new Thickness(0, 0, 4, 0),
                Padding = new Thickness(10, 4, 10, 4),
                Cursor = Cursors.Hand
            };
            UpdateFilterButtonAppearance(btn, isSelected);
            return btn;
        }

        private void UpdateFilterButtonAppearance(Button btn, bool isSelected)
        {
            if (isSelected)
            {
                btn.Background = new SolidColorBrush(Color.FromRgb(0, 120, 215));
                btn.Foreground = Brushes.White;
                btn.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 100, 190));
                btn.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                btn.Background = IsDark ? new SolidColorBrush(Color.FromRgb(36, 42, 50)) : Brushes.White;
                btn.Foreground = IsDark ? new SolidColorBrush(Color.FromRgb(180, 188, 198)) : new SolidColorBrush(Color.FromRgb(60, 65, 75));
                btn.BorderBrush = _colCardBorder;
                btn.FontWeight = FontWeights.Normal;
            }
        }

        private TextBox CreateFluentTextBox(string initialText)
        {
            var tb = new TextBox
            {
                Text = initialText,
                FontSize = 12,
                Padding = new Thickness(8, 5, 8, 5),
                Background = _colInputBg,
                Foreground = _colTextPrimary,
                BorderBrush = _colInputBorder,
                BorderThickness = new Thickness(1)
            };
            return tb;
        }

        // -------------------------------------------------------------
        // WIN32 API FOR SINGLE INSTANCE RESTORE
        // -------------------------------------------------------------
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private static void BringExistingInstanceToFront()
        {
            try
            {
                var current = Process.GetCurrentProcess();
                foreach (var p in Process.GetProcessesByName(current.ProcessName))
                {
                    if (p.Id != current.Id && p.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                        SetForegroundWindow(p.MainWindowHandle);
                        break;
                    }
                }
            }
            catch { }
        }

        [STAThread]
        public static void Main()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, "HostsManager_Global_SingleInstance_Mutex_9872", out createdNew))
            {
                if (!createdNew)
                {
                    BringExistingInstanceToFront();
                    return;
                }

                if (!IsAdministrator())
                {
                    RestartAsAdmin();
                    return;
                }

                var app = new Application();
                app.Run(new MainWindow());
            }
        }
    }
}