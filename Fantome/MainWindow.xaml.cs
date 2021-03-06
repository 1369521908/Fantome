﻿using Fantome.ModManagement;
using Fantome.ModManagement.IO;
using Fantome.MVVM.Commands;
using Fantome.MVVM.ViewModels;
using Fantome.MVVM.ModelViews.Dialogs;
using Fantome.Utilities;
using LoLCustomSharp;
using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Dialogs;
using Serilog;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Octokit;
using Application = System.Windows.Application;
using DataFormats = System.Windows.DataFormats;
using MessageBox = System.Windows.MessageBox;
using System.Reflection;
using System.Windows.Threading;
using Fantome.MVVM.ViewModels.CreateMod;
using System.Windows.Forms;
using Fantome.Properties;

namespace Fantome
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public const string LOGS_FOLDER = "Logs";

        public ModListViewModel ModList => this.ModsListBox.DataContext as ModListViewModel;

        public bool IsModListTypeCard
        {
            get => Config.Get<int>("ModListType") == 0;
            set
            {
                if (value)
                {
                    Config.Set("ModListType", 0);
                }

                NotifyPropertyChanged();
            }
        }
        public bool IsModListTypeRow
        {
            get => Config.Get<int>("ModListType") == 1;
            set
            {
                if (value)
                {
                    Config.Set("ModListType", 1);
                }

                NotifyPropertyChanged();
            }
        }
        public bool IsUpdateAvailable
        {
            get => _isUpdateAvailable;
            set
            {
                this._isUpdateAvailable = value;
                NotifyPropertyChanged();
            }
        }

        private bool _isUpdateAvailable;

        private ModManager _modManager;
        private OverlayPatcher _patcher;
        private NotifyIcon _notifyIcon;

        public event PropertyChangedEventHandler PropertyChanged;

        public ICommand RunSettingsDialogCommand => new RelayCommand(RunSettingsDialog);
        public ICommand RunCreateModDialogCommand => new RelayCommand(RunCreateModDialog);
        public ICommand OpenGithubCommand => new RelayCommand(OpenGithub);
        public ICommand OpenGithubReleasesCommand => new RelayCommand(OpenGithubReleases);

        public MainWindow()
        {
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            //Initial checks to see if we can run Fantome
            CheckEnvironment();
            CheckForExistingProcess();
            CheckForPathUnicodeCharacters();

            //Main loading sequence
            Config.Load();
            InitializeLogger();
            CreateWorkFolders();
            StartPatcher();
            InitializeComponent();
            InitializeModManager();
            BindMVVM();
            InitializeTrayIcon();
            ThemeHelper.LoadTheme();
            CheckForUpdate();
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            string message = "A Fatal Error has occurred, Fantome will now terminate.\n";
            message += "Please delete the FILE_INDEX.json, MOD_DATABSE.json files and Overlay folder if the error happened during Installation or Uninstallation.\n\n";
            message += ((Exception) e.ExceptionObject).GetType() + ": ";
            message += ((Exception)e.ExceptionObject).Message + '\n';

            Log.Fatal(((Exception)e.ExceptionObject).ToString());
            MessageBox.Show(message, "Fantome - Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void InitializeModManager()
        {
            Log.Information("Initializing Mod Manager");
            this._modManager = new ModManager();
        }
        private void StartPatcher()
        {
            Log.Information("Starting League Patcher");
            string overlayDirectory = (Directory.GetCurrentDirectory() + @"\" + ModManager.OVERLAY_FOLDER + @"\").Replace('\\', '/');
            this._patcher = new OverlayPatcher();
            this._patcher.Start(overlayDirectory, OnPatcherMessage, OnPatcherError);
        }
        private void CreateWorkFolders()
        {
            Log.Information("Creating Work folders");
            Directory.CreateDirectory(ModManager.MOD_FOLDER);
            Directory.CreateDirectory(ModManager.OVERLAY_FOLDER);
            Directory.CreateDirectory(LOGS_FOLDER);
        }
        private void BindMVVM()
        {
            Log.Information("Binding View Models");
            this.DataContext = this;
            this.ModsListBox.DataContext = new ModListViewModel(this._modManager);

            DialogHelper.MessageDialog = this.MessageDialog;
            DialogHelper.OperationDialog = this.OperationDialog;
            DialogHelper.RootDialog = this.RootDialog;
        }
        private void InitializeTrayIcon()
        {
            Log.Information("Initializing Tray Icon");

            string[] arguments = Environment.GetCommandLineArgs();
            ContextMenuStrip contextMenuStrip = new ContextMenuStrip();

            //Add Close button
            contextMenuStrip.Items.Add(new ToolStripMenuItem("Close", null, delegate (object sender, EventArgs args)
            {
                Application.Current.Shutdown();
            }));

            //Create Icon 
            this._notifyIcon = new NotifyIcon()
            {
                Visible = false,
                Icon = Properties.Resources.icon,
                ContextMenuStrip = contextMenuStrip
            };

            //Add DoubleClick event handler
            this._notifyIcon.DoubleClick += delegate (object sender, EventArgs args)
            {
                this.Topmost = true;
                Show();
                this.WindowState = WindowState.Normal;
                this.Topmost = false;
            };

            //If we are starting with -tray flag then we minimize window to tray
            if (arguments.Length > 1 && arguments.Any(x => x == "-tray"))
            {
                this.WindowState = WindowState.Minimized;
                OnStateChanged(new EventArgs());
            }
        }
        private void CheckEnvironment()
        {
            //First we check if Fantome is running in Wine and if it isn't then we can check windows version
            if (!WineDetector.IsRunningInWine())
            {   // AS OF VERSION 1.1, FANTOME SUPPORTS ALL WINDOWS VERSIONS
                //OperatingSystem operatingSystem = Environment.OSVersion;
                //if (operatingSystem.Version.Major != 10)
                //{
                //    MessageBox.Show("You need to be running Windows 10 in order to properly use Fantome\n"
                //        + @"By clicking the ""OK"" button you acknowledge that Fantome may not work correctly on your Windows version",
                //        "", MessageBoxButton.OK, MessageBoxImage.Error);
                //}
            }
        }
        private void RemoveExecutableAdminPrivilages()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers\", true))
            {
                if (key != null)
                {
                    List<string> toRemove = key.GetValueNames().Where(x => x.StartsWith(this._modManager.LeagueFolder.Replace(@"\Game", ""))).ToList();
                    foreach (string value in toRemove)
                    {
                        Log.Information("Removing Admin privilige from: " + value);
                        key.DeleteValue(value);
                    }
                }
            }

            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\WindowsNT\CurrentVersion\AppCompatFlags\Layers\", true))
            {
                if (key != null)
                {
                    List<string> toRemove = key.GetValueNames().Where(x => x.StartsWith(this._modManager.LeagueFolder.Replace(@"\Game", ""))).ToList();
                    foreach (string value in toRemove)
                    {
                        Log.Information("Removing Admin privilige from: " + value);
                        key.DeleteValue(value);
                    }
                }
            }
        }
        private void InitializeLogger()
        {
            string logPath = string.Format(@"{0}\FantomeLog - {1}.txt", LOGS_FOLDER, DateTime.Now.ToString("dd.MM.yyyy - HH-mm-ss"));
            string loggingPattern = Config.Get<string>("LoggingPattern");

            Log.Logger = new LoggerConfiguration()
                .WriteTo.File(logPath, outputTemplate: loggingPattern)
                .CreateLogger();
        }
        private void CheckForExistingProcess()
        {
            foreach (Process process in Process.GetProcessesByName("Fantome").Where(x => x.Id != Process.GetCurrentProcess().Id))
            {
                if (process.MainModule.ModuleName == "Fantome.exe")
                {
                    if (MessageBox.Show("There is already a running instance of Fantome.\nPlease check your tray.", "", MessageBoxButton.OK, MessageBoxImage.Error) == MessageBoxResult.OK)
                    {
                        Application.Current.Shutdown();
                    }
                }
            }
        }
        private void CheckForPathUnicodeCharacters()
        {
            string currentDirectory = Environment.CurrentDirectory;
            if (currentDirectory.Any(c => c > 255))
            {
                string message = currentDirectory + '\n';
                message += "The path to Fantome contains Unicode characters, please remove them from the path or move Fantome to a different directory\n";
                message += "Unicode characters are letters from languages such as Russian, Chinese etc....";

                if (MessageBox.Show(message, "", MessageBoxButton.OK, MessageBoxImage.Error) == MessageBoxResult.OK)
                {
                    Application.Current.Shutdown();
                }
            }
        }
        private async void CheckForUpdate()
        {
            try
            {
                GitHubClient gitClient = new GitHubClient(new ProductHeaderValue("Fantome"));

                IReadOnlyList<Release> releases = await gitClient.Repository.Release.GetAll("LoL-Fantome", "Fantome");
                Release newestRelease = releases[0];

                if(Version.TryParse(newestRelease.TagName, out Version newestVersion))
                {
                    if (!newestRelease.Prerelease && newestVersion > Assembly.GetExecutingAssembly().GetName().Version)
                    {
                        this.IsUpdateAvailable = true;

                        await DialogHelper.ShowMessageDialog("A new version of Fantome is available." + '\n' + @"Click the ""Update"" button to download it.");
                    }
                }
            }
            catch (Exception)
            {
                Log.Information("Unable to check for updates");
            }
        }

        private async void OnAddMod(object sender, RoutedEventArgs e)
        {
            CommonOpenFileDialog dialog = new CommonOpenFileDialog("Choose the ZIP file of your mod")
            {
                Multiselect = false
            };

            dialog.Filters.Add(new CommonFileDialogFilter("ZIP Files", "zip"));

            if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
            {
                await AddMod(dialog.FileName);
            }
        }
        private async Task AddMod(string modLocation)
        {
            string modPath = "";

            using (ModFile originalMod = new ModFile(modLocation))
            {
                modPath = string.Format(@"{0}\{1}.zip", ModManager.MOD_FOLDER, originalMod.GetID());
            }

            //If mod is not in Mods folder then we copy it
            if (!File.Exists(modPath))
            {
                Log.Information("Copying Mod: {0} to {1}", modLocation, modPath);
                File.Copy(modLocation, modPath, true);
            }

            Log.Information("Loading Mod: {0}", modPath);
            bool install = Config.Get<bool>("InstallAddedMods");
            ModFile mod = new ModFile(modPath);
            await this.ModList.AddMod(mod, install);
        }

        private async void RunSettingsDialog(object o)
        {
            SettingsDialog dialog = new SettingsDialog
            {
                DataContext = new SettingsViewModel()
            };


            object result = await DialogHost.Show(dialog, "RootDialog", (dialog.DataContext as SettingsViewModel).OnSaveSettings);
        }
        private async void RunCreateModDialog(object o)
        {
            ModFile createdMod = await DialogHelper.ShowCreateModDialog(this._modManager.Index);

            if(createdMod != null)
            {
                await this.ModList.AddMod(createdMod, false);
            }
        }
        private async void OnRootDialogLoad(object sender, EventArgs e)
        {
            string leagueLocation = Config.Get<string>("LeagueLocation");
            if (string.IsNullOrEmpty(leagueLocation) || !LeagueLocationValidator.Validate(leagueLocation))
            {
                await GetLeagueLocation();
                Config.Set("LeagueLocation", leagueLocation);
            }

            this._modManager.AssignLeague(leagueLocation);
            this.ModList.SyncWithModManager();

            RemoveExecutableAdminPrivilages();

            async Task GetLeagueLocation()
            {
                LeagueLocationDialog dialog = new LeagueLocationDialog()
                {
                    DataContext = new LeagueLocationDialogViewModel()
                };

                object result = await DialogHost.Show(dialog, "RootDialog");

                if ((bool)result == true)
                {
                    leagueLocation = dialog.ViewModel.LeagueLocation;
                }
            }
        }

        private void OpenGithub(object o)
        {
            Process.Start("cmd", "/C start https://github.com/LoL-Fantome/Fantome");
        }
        private void OpenGithubReleases(object o)
        {
            Process.Start("cmd", "/C start https://github.com/LoL-Fantome/Fantome/releases");
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (this.WindowState == WindowState.Minimized)
            {
                if(Config.Get<bool>("MinimizeToTray"))
                {
                    this._notifyIcon.Visible = true;
                    Hide();
                }
            }
            else if (this.WindowState == WindowState.Normal)
            {
                this._notifyIcon.Visible = false;
            }

            base.OnStateChanged(e);
        }
        private void OnClosing(object sender, CancelEventArgs e)
        {
            this._notifyIcon.Dispose();
        }
        private async void OnDrop(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                for (int i = 0; i < files.Length; i++)
                {
                    await AddMod(files[i]);
                }
            }
        }

        private void OnPatcherError(Exception exception)
        {
            Log.Error("PATCHER: {0}", exception);
        }
        private void OnPatcherMessage(string message)
        {
            Log.Information("PATCHER: {0}", message);
        }

        private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
