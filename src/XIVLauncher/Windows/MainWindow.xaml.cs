using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheapLoc;
using MaterialDesignThemes.Wpf;
using Serilog;
using XIVLauncher.Accounts;
using XIVLauncher.Addon;
using XIVLauncher.Cache;
using XIVLauncher.Dalamud;
using XIVLauncher.Game;
using XIVLauncher.Game.Patch;
using XIVLauncher.Game.Patch.Acquisition;
using XIVLauncher.PatchInstaller;
using XIVLauncher.PatchInstaller.PatcherIpcMessages;
using XIVLauncher.Settings;
using XIVLauncher.Windows.ViewModel;
using Timer = System.Timers.Timer;

namespace XIVLauncher.Windows
{
    /// <summary>
    ///     Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private Timer _bannerChangeTimer;
        private Headlines _headlines;
        private BitmapImage[] _bannerBitmaps;
        private int _currentBannerIndex;

        private Timer _maintenanceQueueTimer;

        private readonly Launcher _launcher = new Launcher();
        private readonly Game.Patch.PatchInstaller _installer = new Game.Patch.PatchInstaller();

        private AccountManager _accountManager;

        private bool _isLoggingIn;

        public MainWindow()
        {
            InitializeComponent();

            this.DataContext = new MainWindowViewModel();

            NewsListView.ItemsSource = new List<News>
            {
                new News
                {
                    Title = Loc.Localize("NewsLoading", "Loading..."),
                    Tag = "DlError"
                }
            };

#if !XL_NOAUTOUPDATE
            Title += " v" + Util.GetAssemblyVersion();
#else
            Title += " " + Util.GetGitHash();
#endif

#if !XL_NOAUTOUPDATE
            if (EnvironmentSettings.IsDisableUpdates)
#endif
            {
                Title += " - UNSUPPORTED VERSION - NO UPDATES - COULD DO BAD THINGS";
            }

#if DEBUG
            Title += " - Debugging";
#endif
            
            if (EnvironmentSettings.IsWine)
                Title += " - Wine on Linux";
        }

        private void SetupHeadlines()
        {
            try
            {
                _bannerChangeTimer?.Stop();

                _headlines = Headlines.Get(_launcher, App.Settings.Language.GetValueOrDefault(ClientLanguage.English));

                _bannerBitmaps = new BitmapImage[_headlines.Banner.Length];
                for (var i = 0; i < _headlines.Banner.Length; i++)
                {
                    var imageBytes = _launcher.DownloadAsLauncher(_headlines.Banner[i].LsbBanner.ToString(), App.Settings.Language.GetValueOrDefault(ClientLanguage.English));

                    using var stream = new MemoryStream(imageBytes);

                    var bitmapImage = new BitmapImage();
                    bitmapImage.BeginInit();
                    bitmapImage.StreamSource = stream;
                    bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                    bitmapImage.EndInit();
                    bitmapImage.Freeze();

                    _bannerBitmaps[i] = bitmapImage;
                }

                Dispatcher.BeginInvoke(new Action(() => { BannerImage.Source = _bannerBitmaps[0]; }));

                _bannerChangeTimer = new Timer {Interval = 5000};

                _bannerChangeTimer.Elapsed += (o, args) =>
                {
                    if (_currentBannerIndex + 1 > _headlines.Banner.Length - 1)
                        _currentBannerIndex = 0;
                    else
                        _currentBannerIndex++;

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        BannerImage.Source = _bannerBitmaps[_currentBannerIndex];
                    }));
                };

                _bannerChangeTimer.AutoReset = true;
                _bannerChangeTimer.Start();

                Dispatcher.BeginInvoke(new Action(() => { NewsListView.ItemsSource = _headlines.News; }));
            }
            catch (Exception)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    NewsListView.ItemsSource = new List<News> {new News {Title = Loc.Localize("NewsDlFailed", "Could not download news data."), Tag = "DlError"}};
                }));
            }
        }

        public void Initialize()
        {
#if DEBUG
            var fakeStartMenuItem = new MenuItem
            {
                Header = "Fake start"
            };
            fakeStartMenuItem.Click += FakeStart_OnClick;

            LoginContextMenu.Items.Add(fakeStartMenuItem);
#endif
            
            // Set the default patch acquisition method
            App.Settings.PatchAcquisitionMethod ??=
                EnvironmentSettings.IsWine ? AcquisitionMethod.NetDownloader : AcquisitionMethod.Aria;

            // Clean up invalid addons
            if (App.Settings.AddonList != null)
                App.Settings.AddonList = App.Settings.AddonList.Where(x => !string.IsNullOrEmpty(x.Addon.Path)).ToList();

            App.Settings.EncryptArguments ??= true;
            App.Settings.AskBeforePatchInstall ??= true;

            var gateStatus = false;
            try
            {
                gateStatus = _launcher.GetGateStatus();
            }
            catch
            {
                // ignored
            }

            if (!gateStatus) WorldStatusPackIcon.Foreground = new SolidColorBrush(Color.FromRgb(242, 24, 24));

            var version = Util.GetAssemblyVersion();
            if (App.Settings.LastVersion != version)
            {
                new ChangelogWindow().ShowDialog();

                App.Settings.LastVersion = version;
            }

            _accountManager = new AccountManager(App.Settings);

            var savedAccount = _accountManager.CurrentAccount;

            if (savedAccount != null)
                SwitchAccount(savedAccount, false);

            AutoLoginCheckBox.IsChecked = App.Settings.AutologinEnabled;

            if (App.Settings.UniqueIdCacheEnabled && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                UniqueIdCache.Instance.Reset();
                Console.Beep(523, 150); // Feedback without popup
            }

            if (App.GlobalIsDisableAutologin)
            {
                Log.Information("Autologin was disabled globally, saving into settings...");
                App.Settings.AutologinEnabled = false;
            }

            if (App.Settings.AutologinEnabled && savedAccount != null && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                Log.Information("Engaging Autologin...");

                try
                {
                    this.Kickoff(true);
                    return;
                }
                catch (Exception ex)
                {
                    new ErrorWindow(ex, Loc.Localize("CheckLoginInfo", "Additionally, please check your login information or try again."), "AutoLogin")
                        .ShowDialog();
                    App.Settings.AutologinEnabled = false;
                    _isLoggingIn = false;
                }
            }
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || bool.Parse(Environment.GetEnvironmentVariable("XL_NOAUTOLOGIN") ?? "false"))
            {
                App.Settings.AutologinEnabled = false;
                AutoLoginCheckBox.IsChecked = false;
            }

            if (App.Settings.GamePath?.Exists != true)
            {
                var setup = new FirstTimeSetup();
                setup.ShowDialog();

                // If the user didn't reach the end of the setup, we should quit
                if (!setup.WasCompleted)
                {
                    Environment.Exit(0);
                    return;
                }
                
                SettingsControl.ReloadSettings();
            }

            Task.Run(SetupHeadlines);

            Log.Information("MainWindow initialized.");

            Show();
            Activate();
        }

        private void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            this.Kickoff(false, true);
        }

        private void LoginNoStart_Click(object sender, RoutedEventArgs e)
        {
            this.Kickoff(false, false);
        }

        private void HandleBootCheck(Action whenFinishAction)
        {
            try
            {
                App.Settings.PatchPath ??= new DirectoryInfo(Path.Combine(Paths.RoamingPath, "patches"));

                Game.Patch.PatchList.PatchListEntry[] bootPatches = null;
                try
                {
                    bootPatches = _launcher.CheckBootVersion(App.Settings.GamePath);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Unable to check boot version.");
                    MessageBox.Show(Loc.Localize("CheckBootVersionError", "XIVLauncher was not able to check the boot version for the select game installation. This can happen if a maintenance is currently in progress or if your connection to the version check server is not available. Please report this error if you are able to login with the official launcher, but not XIVLauncher."), "XIVLauncher", MessageBoxButton.OK, MessageBoxImage.Error);

                    _isLoggingIn = false;

                    Task.Run(SetupHeadlines);
                    Show();
                    Activate();
                    return;
                }
                
                if (bootPatches != null)
                {
                    var mutex = new Mutex(false, "XivLauncherIsPatching");
                    if (mutex.WaitOne(0, false))
                    {
                        if (Util.CheckIsGameOpen())
                        {
                            MessageBox.Show(
                                Loc.Localize("GameIsOpenError",
                                    "The game and/or the official launcher are open. XIVLauncher cannot patch the game if this is the case.\nPlease close the official launcher and try again."),
                                "XIVLauncher", MessageBoxButton.OK, MessageBoxImage.Exclamation);

                            Environment.Exit(0);
                            return;
                        }

                        var patcher = new PatchManager(bootPatches, App.Settings.GamePath,
                            App.Settings.PatchPath, _installer);

                        var progressDialog = new PatchDownloadDialog(patcher);
                        progressDialog.Show();
                        this.Hide();

                        patcher.OnFinish += async (sender, args) =>
                        {
                            progressDialog.Dispatcher.Invoke(() =>
                            {
                                progressDialog.Hide();
                                progressDialog.Close();
                            });

                            if (args)
                            {
                                whenFinishAction?.Invoke();
                            }
                            else
                            {
                                this.Dispatcher.Invoke(() =>
                                {
                                    this.Show();
                                    _isLoggingIn = false;
                                });
                            }

                            // This is a good indicator that we should clear the UID cache
                            UniqueIdCache.Instance.Reset();

                            await patcher.UnInitializeAcquisition();

                            mutex.Close();
                            mutex = null;
                        };

                        patcher.Start();
                    }
                    else
                    {
                        MessageBox.Show(Loc.Localize("PatcherAlreadyInProgress", "XIVLauncher is already patching your game in another instance. Please check if XIVLauncher is still open."), "XIVLauncher", MessageBoxButton.OK, MessageBoxImage.Error);
                        Environment.Exit(0);
                    }
                }
                else
                {
                    whenFinishAction?.Invoke();
                }
            }
            catch (Exception ex)
            {
                new ErrorWindow(ex, "Could not patch boot.", nameof(HandleBootCheck)).ShowDialog();
                Environment.Exit(0);
            }
        }

        private void Kickoff(bool autoLogin, bool startGame = true)
        {
            ProblemCheck.RunCheck();

            HandleBootCheck(() => this.Dispatcher.Invoke(() => this.PrepareLogin(autoLogin, startGame)));
        }

        private void Reactivate()
        {
            _isLoggingIn = false;

            _ = Task.Run(SetupHeadlines);
            Show();
            Activate();
        }

        private void PrepareLogin(bool autoLogin, bool startGame = true)
        {
            if (string.IsNullOrEmpty(LoginUsername.Text))
            {
                CustomMessageBox.Show(
                    Loc.Localize("EmptyUsernameError", "Please enter an username."),
                    "XIVLauncher", MessageBoxButton.OK, MessageBoxImage.Error);

                this.Reactivate();
                return;
            }

            if (LoginUsername.Text.Contains("@"))
            {
                CustomMessageBox.Show(
                    Loc.Localize("EmailUsernameError", "Please enter your SE account name, not your email address."),
                    "XIVLauncher", MessageBoxButton.OK, MessageBoxImage.Error);

                this.Reactivate();
                return;
            }

            if (string.IsNullOrEmpty(LoginPassword.Password))
            {
                CustomMessageBox.Show(
                    Loc.Localize("EmptyPasswordError", "Please enter a password."),
                    "XIVLauncher", MessageBoxButton.OK, MessageBoxImage.Error);

                App.Settings.AutologinEnabled = false;
                AutoLoginCheckBox.IsChecked = false;
                
                this.Reactivate();
                return;
            }

            if (_isLoggingIn)
                return;

            if (Repository.Ffxiv.GetVer(App.Settings.GamePath) == PatcherMain.BASE_GAME_VERSION && App.Settings.UniqueIdCacheEnabled)
            {
                CustomMessageBox.Show(
                    Loc.Localize("UidCacheInstallError",
                        "You enabled the UID cache in the patcher settings.\nThis setting does not allow you to reinstall FFXIV.\n\nIf you want to reinstall FFXIV, please take care to disable it first."),
                    "XIVLauncher Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _isLoggingIn = true;

            var hasValidCache = UniqueIdCache.Instance.HasValidCache(LoginUsername.Text) && App.Settings.UniqueIdCacheEnabled;

            Log.Information("CurrentAccount: {0}", _accountManager.CurrentAccount == null ? "null" : _accountManager.CurrentAccount.ToString());

            if (_accountManager.CurrentAccount != null && _accountManager.CurrentAccount.UserName.Equals(LoginUsername.Text) && _accountManager.CurrentAccount.Password != LoginPassword.Password && _accountManager.CurrentAccount.SavePassword)
            {
                _accountManager.UpdatePassword(_accountManager.CurrentAccount, LoginPassword.Password);
            }

            if (_accountManager.CurrentAccount == null || _accountManager.CurrentAccount.Id != $"{LoginUsername.Text}-{OtpCheckBox.IsChecked == true}-{SteamCheckBox.IsChecked == true}")
            {
                var accountToSave = new XivAccount(LoginUsername.Text)
                {
                    Password = LoginPassword.Password,
                    SavePassword = true,
                    UseOtp = OtpCheckBox.IsChecked == true,
                    UseSteamServiceAccount = SteamCheckBox.IsChecked == true
                };

                _accountManager.AddAccount(accountToSave);

                _accountManager.CurrentAccount = accountToSave;
            }

            if (!autoLogin)
            {
                App.Settings.AutologinEnabled = AutoLoginCheckBox.IsChecked == true;
            }

            var otp = "";
            if (OtpCheckBox.IsChecked == true && !hasValidCache)
            {
                var otpDialog = new OtpInputDialog();
                otpDialog.ShowDialog();

                if (otpDialog.Result == null)
                {
                    _isLoggingIn = false;

                    if (autoLogin)
                    {
                        CleanUp();
                        Environment.Exit(0);
                    }

                    return;
                }

                otp = otpDialog.Result;
            }

            StartLogin(otp, startGame);
        }

        private void InstallGamePatch(Launcher.LoginResult loginResult, bool gateStatus, bool startGame)
        {
            var mutex = new Mutex(false, "XivLauncherIsPatching");
            if (mutex.WaitOne(0, false))
            {
                if (Util.CheckIsGameOpen())
                {
                    CustomMessageBox.Show(
                        Loc.Localize("GameIsOpenError", "The game and/or the official launcher are open. XIVLauncher cannot patch the game if this is the case.\nPlease close the official launcher and try again."),
                        "XIVLauncher", MessageBoxButton.OK, MessageBoxImage.Exclamation);

                    this.Dispatcher.Invoke(() =>
                    {
                        this.Show();
                        _isLoggingIn = false;
                    });
                }
                else
                {
                    Debug.Assert(loginResult.State == Launcher.LoginState.NeedsPatchGame,
                        "loginResult.State == Launcher.LoginState.NeedsPatchGame ASSERTION FAILED");

                    Debug.Assert(loginResult.PendingPatches != null, "loginResult.PendingPatches != null ASSERTION FAILED");

                    var patcher = new PatchManager(loginResult.PendingPatches, App.Settings.GamePath, App.Settings.PatchPath, _installer);

                    var progressDialog = new PatchDownloadDialog(patcher);
                    progressDialog.Show();
                    this.Hide();

                    patcher.OnFinish += async (sender, success) =>
                    {
                        progressDialog.Dispatcher.Invoke(() =>
                        {
                            progressDialog.Hide();
                            progressDialog.Close();
                        });

                        if (success)
                        {
                            if (!startGame)
                            {
                                this.Dispatcher.Invoke(() =>
                                {
                                    CustomMessageBox.Show(
                                        Loc.Localize("LoginNoStartOk",
                                            "An update check was executed and any pending updates were installed."), "XIVLauncher",
                                        MessageBoxButton.OK, MessageBoxImage.Information, false);

                                    _isLoggingIn = false;
                                    Show();
                                    Activate();
                                });
                            }
                            else
                            {
                                await this.Dispatcher.Invoke(() => StartGameAndAddon(loginResult, gateStatus));
                            }
                            _installer.Stop();
                        }
                        else
                        {
                            this.Dispatcher.Invoke(() =>
                            {
                                this.Show();
                                _isLoggingIn = false;
                            });
                        }

                        await patcher.UnInitializeAcquisition();

                        mutex.Close();
                        mutex = null;
                    };

                    patcher.Start();
                }
            }
            else
            {
                CustomMessageBox.Show(Loc.Localize("PatcherAlreadyInProgress", "XIVLauncher is already patching your game in another instance. Please check if XIVLauncher is still open."), "XIVLauncher", MessageBoxButton.OK, MessageBoxImage.Error);
                Environment.Exit(0);
            }
        }

        private async void StartLogin(string otp, bool startGame)
        {
            Log.Information("StartLogin() called");

            var gateStatus = false;
            try
            {
                gateStatus = await Task.Run(() => _launcher.GetGateStatus());
            }
            catch
            {
                // ignored
            }

            try
            {
                var loginResult = _launcher.Login(LoginUsername.Text, LoginPassword.Password, otp, SteamCheckBox.IsChecked == true, App.Settings.UniqueIdCacheEnabled, App.Settings.GamePath);

                Debug.Assert(loginResult != null, "ASSERTION FAILED loginResult != null!");

                if (loginResult.State != Launcher.LoginState.Ok)
                {
                    Log.Verbose($"[LR] {loginResult.State} {loginResult.PendingPatches != null} {loginResult.OauthLogin?.Playable}");

                    if (loginResult.State == Launcher.LoginState.NoOAuth)
                    {
                        var failedOauthMessage = Loc.Localize("LoginNoOauthMessage", "Could not login into your Square Enix account.\nThis could be caused by bad credentials or OTPs.\n\nPlease also check your email inbox for any messages from Square Enix - they might want you to reset your password due to \"suspicious activity\".\nThis is NOT caused by a security issue in XIVLauncher, it is merely a safety measure by Square Enix to prevent logins from new locations, in case your account is getting stolen.\nXIVLauncher and the official launcher will work fine again after resetting your password.");
                        if (App.Settings.AutologinEnabled)
                        {
                            failedOauthMessage += Loc.Localize("LoginNoOauthAutologinHint", "\n\nAuto-Login has been disabled.");
                            App.Settings.AutologinEnabled = false;
                        }

                        CustomMessageBox.Show(failedOauthMessage, Loc.Localize("LoginNoOauthTitle", "Login issue"), MessageBoxButton.OK, MessageBoxImage.Error);
                        
                        _isLoggingIn = false;
                        Show();
                        Activate();
                        return;
                    }

                    if (loginResult.State == Launcher.LoginState.NoService)
                    {
                        CustomMessageBox.Show(
                            Loc.Localize("LoginNoServiceMessage",
                                "This Square Enix account cannot play FINAL FANTASY XIV.\n\nIf you bought FINAL FANTASY XIV on Steam, make sure to check the \"Use Steam service account\" checkbox while logging in.\nIf Auto-Login is enabled, hold shift while starting to access settings."),
                            "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error, false);

                        _isLoggingIn = false;
                        Show();
                        Activate();
                        return;
                    }

                    if (loginResult.State == Launcher.LoginState.NoTerms)
                    {
                        CustomMessageBox.Show(Loc.Localize("LoginAcceptTermsMessage", "Please accept the FINAL FANTASY XIV Terms of Use in the official launcher."),
                            "Error", MessageBoxButton.OK, MessageBoxImage.Error);

                        _isLoggingIn = false;
                        Show();
                        Activate();
                        return;
                    }

                    /*
                     * The server requested us to patch Boot, even though in order to get to this code, we just checked for boot patches.
                     *
                     * This means that something or someone modified boot binaries without our involvement.
                     * We have no way to go back to a "known" good state other than to do a full reinstall.
                     *
                     * This has happened multiple times with users that have viruses that infect other EXEs and change their hashes, causing the update
                     * server to reject our boot hashes.
                     *
                     * In the future we may be able to just delete /boot and run boot patches again, but this doesn't happen often enough to warrant the
                     * complexity and if boot is fucked game probably is too.
                     */
                    if (loginResult.State == Launcher.LoginState.NeedsPatchBoot)
                    {
                        CustomMessageBox.Show(Loc.Localize("EverythingIsFuckedMessage", "Certain essential game files were modified/broken by a third party and the game can neither update nor start.\nYou have to reinstall the game to continue.\n\nIf this keeps happening, please contact us via Discord."),
                            "Error", MessageBoxButton.OK, MessageBoxImage.Error);

                        _isLoggingIn = false;
                        Show();
                        Activate();
                        return;
                    }

                    if (App.Settings.AskBeforePatchInstall.HasValue && App.Settings.AskBeforePatchInstall.Value)
                    {
                        var selfPatchAsk = MessageBox.Show(
                            Loc.Localize("PatchInstallDisclaimer", "A new patch has been found that needs to be installed before you can play.\nDo you wish for XIVLauncher to install it?"),
                            "Out of date", MessageBoxButton.YesNo, MessageBoxImage.Information);

                        if (selfPatchAsk == MessageBoxResult.Yes)
                        {
                            InstallGamePatch(loginResult, gateStatus, startGame);
                        }
                        else
                        {
                            _isLoggingIn = false;
                            Show();
                            Activate();
                            return;
                        }
                    }
                    else
                    {
                        InstallGamePatch(loginResult, gateStatus, startGame);
                    }

                    return;
                }

                if (startGame)
                {
                    await StartGameAndAddon(loginResult, gateStatus);
                }
                else
                {
                    CustomMessageBox.Show(
                        Loc.Localize("LoginNoStartOk",
                            "An update check was executed and any pending updates were installed."), "XIVLauncher",
                        MessageBoxButton.OK, MessageBoxImage.Information, false);

                    _isLoggingIn = false;
                    Show();
                    Activate();
                }
                
            }
            catch (Exception ex)
            {
                Log.Error(ex, "StartGame failed...");

                if (!gateStatus)
                {
                    Log.Information("GateStatus is false.");
                    CustomMessageBox.Show(
                        Loc.Localize("MaintenanceNotice", "Maintenance seems to be in progress. The game shouldn't be launched."),
                        "XIVLauncher", MessageBoxButton.OK, MessageBoxImage.Exclamation, false);

                    _isLoggingIn = false;

                    _ = Task.Run(SetupHeadlines);
                    Show();
                    Activate();
                    return;
                }

                new ErrorWindow(ex, "Please also check your login information or try again.", "Login").ShowDialog();
            }
        }

        private async Task StartGameAndAddon(Launcher.LoginResult loginResult, bool gateStatus)
        {
            if (!gateStatus)
            {
                Log.Information("GateStatus is false.");
                CustomMessageBox.Show(
                    Loc.Localize("MaintenanceNotice",
                        "Maintenance seems to be in progress. The game shouldn't be launched."),
                    "XIVLauncher", MessageBoxButton.OK, MessageBoxImage.Exclamation, false);

                _isLoggingIn = false;

                _ = Task.Run(SetupHeadlines);
                Show();
                Activate();
                return;
            }

            // We won't do any sanity checks here anymore, since that should be handled in StartLogin

            var gameProcess = Launcher.LaunchGame(loginResult.UniqueId, loginResult.OauthLogin.Region,
                    loginResult.OauthLogin.MaxExpansion, App.Settings.SteamIntegrationEnabled,
                    SteamCheckBox.IsChecked == true, App.Settings.AdditionalLaunchArgs, App.Settings.GamePath, App.Settings.IsDx11, App.Settings.Language.GetValueOrDefault(ClientLanguage.English), App.Settings.EncryptArguments.GetValueOrDefault(false));

            if (gameProcess == null)
            {
                Log.Information("GameProcess was null...");
                _isLoggingIn = false;
                return;
            }

            CleanUp();

            this.Hide();

            var addonMgr = new AddonManager();

            try
            {
                if (App.Settings.AddonList == null)
                    App.Settings.AddonList = new List<AddonEntry>();

                var addons = App.Settings.AddonList.Where(x => x.IsEnabled).Select(x => x.Addon).Cast<IAddon>().ToList();

                if (App.Settings.InGameAddonEnabled && App.Settings.IsDx11)
                {
                    var overlay = new DalamudLoadingOverlay();
                    overlay.Hide();
                    addons.Add(new DalamudLauncher(overlay));
                }
                else
                {
                    Log.Warning("In-Game addon was not enabled.");
                }

                await Task.Run(() => addonMgr.RunAddons(gameProcess, App.Settings, addons));
            }
            catch (Exception ex)
            {
                new ErrorWindow(ex,
                    "This could be caused by your antivirus, please check its logs and add any needed exclusions.",
                    "Addons").ShowDialog();
                _isLoggingIn = false;

                addonMgr.StopAddons();
            }

            var watchThread = new Thread(() =>
            {
                while (!gameProcess.HasExited)
                {
                    gameProcess.Refresh();
                    Thread.Sleep(1);
                }

                Log.Information("Game has exited.");
                addonMgr.StopAddons();
                
                CleanUp();

                Environment.Exit(0);
            });
            watchThread.Start();

            Log.Debug("Started WatchThread");
        }

        private void CleanUp()
        {
            _installer.Stop();
        }

        private void BannerCard_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            if (_headlines != null) Process.Start(_headlines.Banner[_currentBannerIndex].Link.ToString());
        }

        private void SaveLoginCheckBox_OnChecked(object sender, RoutedEventArgs e)
        {
            AutoLoginCheckBox.IsEnabled = true;
        }

        private void SaveLoginCheckBox_OnUnchecked(object sender, RoutedEventArgs e)
        {
            AutoLoginCheckBox.IsChecked = false;
            AutoLoginCheckBox.IsEnabled = false;
        }

        private void NewsListView_OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            if (_headlines == null)
                return;

            if (!(NewsListView.SelectedItem is News item)) 
                return;

            if (!string.IsNullOrEmpty(item.Url))
            {
                Process.Start(item.Url);
            }
            else
            {
                string url;
                switch (App.Settings.Language)
                {
                    case ClientLanguage.Japanese:
                        url = "https://jp.finalfantasyxiv.com/lodestone/news/detail/";
                        break;

                    case ClientLanguage.English when Util.IsRegionNorthAmerica():
                        url = "https://na.finalfantasyxiv.com/lodestone/news/detail/";
                        break;

                    case ClientLanguage.English:
                        url = "https://eu.finalfantasyxiv.com/lodestone/news/detail/";
                        break;

                    case ClientLanguage.German:
                        url = "https://de.finalfantasyxiv.com/lodestone/news/detail/";
                        break;

                    case ClientLanguage.French:
                        url = "https://fr.finalfantasyxiv.com/lodestone/news/detail/";
                        break;

                    default:
                        url = "https://eu.finalfantasyxiv.com/lodestone/news/detail/";
                        break;
                }

                Process.Start(url + item.Id);
            }
        }

        private void WorldStatusButton_Click(object sender, RoutedEventArgs e)
        {
            Process.Start("https://is.xivup.com/");
        }

        private void QueueButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (_maintenanceQueueTimer == null)
                SetupMaintenanceQueueTimer();

            DialogHost.OpenDialogCommand.Execute(null, MaintenanceQueueDialogHost);
            _maintenanceQueueTimer.Start();

            // Manually fire the first event, avoid waiting the first timer interval
            Task.Run(() =>
            {
                OnMaintenanceQueueTimerEvent(null, null);
            });
        }

        private void SetupMaintenanceQueueTimer()
        {
            // This is a good indicator that we should clear the UID cache
            UniqueIdCache.Instance.Reset();

            _maintenanceQueueTimer = new Timer
            {
                Interval = 15000
            };

            _maintenanceQueueTimer.Elapsed += OnMaintenanceQueueTimerEvent;
        }

        private void OnMaintenanceQueueTimerEvent(Object source, System.Timers.ElapsedEventArgs e)
        {
            var bootPatches = _launcher.CheckBootVersion(App.Settings.GamePath);

            var gateStatus = false;
            try
            {
                gateStatus = _launcher.GetGateStatus();
            }
            catch
            {
                // ignored
            }

            if (gateStatus || bootPatches != null)
            {
                if (bootPatches != null)
                    CustomMessageBox.Show(Loc.Localize("MaintenanceQueueBootPatch",
                        "A patch for the FFXIV launcher was detected.\nThis usually means that there is a patch for the game as well.\n\nYou will now be logged in."), "XIVLauncher");

                Dispatcher.BeginInvoke(new Action(() => {
                    QuitMaintenanceQueueButton_OnClick(null, null);
                    LoginButton_Click(null, null);
                }));

                Console.Beep(523, 150);
                Thread.Sleep(25);
                Console.Beep(523, 150);
                Thread.Sleep(25);
                Console.Beep(523, 150);
                Thread.Sleep(25);
                Console.Beep(523, 300);
                Thread.Sleep(150);
                Console.Beep(415, 300);
                Thread.Sleep(150);
                Console.Beep(466, 300);
                Thread.Sleep(150);
                Console.Beep(523, 300);
                Thread.Sleep(25);
                Console.Beep(466, 150);
                Thread.Sleep(25);
                Console.Beep(523, 900);
            }
        }

        private void QuitMaintenanceQueueButton_OnClick(object sender, RoutedEventArgs e)
        {
            _maintenanceQueueTimer.Stop();
            DialogHost.CloseDialogCommand.Execute(null, MaintenanceQueueDialogHost);
        }

        private void Card_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Return || _isLoggingIn)
                return;

            this.Kickoff(false);
            _isLoggingIn = true;
        }

        private void MainWindow_OnClosed(object sender, EventArgs e)
        {
            CleanUp();
            Application.Current.Shutdown();
        }

        private void AccountSwitcherButton_OnClick(object sender, RoutedEventArgs e)
        {
            var switcher = new AccountSwitcher(_accountManager);

            switcher.WindowStartupLocation = WindowStartupLocation.Manual;
            var location = AccountSwitcherButton.PointToScreen(new Point(0,0));
            switcher.Left = location.X + 15;
            switcher.Top = location.Y + 15;

            switcher.OnAccountSwitchedEventHandler += OnAccountSwitchedEventHandler;

            switcher.Show();
        }

        private void OnAccountSwitchedEventHandler(object sender, XivAccount e)
        {
            SwitchAccount(e, true);
        }

        private void SwitchAccount(XivAccount account, bool saveAsCurrent)
        {
            LoginUsername.Text = account.UserName;
            OtpCheckBox.IsChecked = account.UseOtp;
            SteamCheckBox.IsChecked = account.UseSteamServiceAccount;
            AutoLoginCheckBox.IsChecked = App.Settings.AutologinEnabled;

            if (account.SavePassword)
                LoginPassword.Password = account.Password;

            if (saveAsCurrent)
            {
                _accountManager.CurrentAccount = account;
            }
        }

        private void SettingsControl_OnSettingsDismissed(object sender, EventArgs e)
        {
            Task.Run(SetupHeadlines);
        }

        private async void FakeStart_OnClick(object sender, RoutedEventArgs e)
        {
            await StartGameAndAddon(new Launcher.LoginResult
            {
                OauthLogin = new Launcher.OauthLoginResult
                {
                    MaxExpansion = 3,
                    Playable = true,
                    Region = 0,
                    SessionId = "0",
                    TermsAccepted = true
                },
                State = Launcher.LoginState.Ok,
                UniqueId = "0"
            }, true);
        }
    }
}