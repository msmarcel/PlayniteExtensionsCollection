﻿using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayState.Models;
using PlayState.ViewModels;
using PlayState.Views;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PlayState
{
    public class PlayState : GenericPlugin
    {
        [DllImport("ntdll.dll", PreserveSig = false)]
        public static extern void NtSuspendProcess(IntPtr processHandle);
        [DllImport("ntdll.dll", PreserveSig = false)]
        public static extern void NtResumeProcess(IntPtr processHandle);
        private static readonly ILogger logger = LogManager.GetLogger();
        private Game currentGame;
        private List<string> exclusionList;
        private string gameInstallDir;
        private bool isSuspended = false;
        private Window currentSplashWindow;
        private DispatcherTimer timer;
        private List<ProcessItem> gameProcesses;

        private PlayStateSettingsViewModel settings { get; set; }

        public override Guid Id { get; } = Guid.Parse("26375941-d460-4d32-925f-ad11e2facd8f");
        internal SplashWindowViewModel splashWindowViewModel { get; private set; }

        public PlayState(IPlayniteAPI api) : base(api)
        {
            settings = new PlayStateSettingsViewModel(this);
            Properties = new GenericPluginProperties
            {
                HasSettings = true
            };
            SetExclusionList();

            timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(1000);
            timer.Tick += (src, args) =>
            {
                timer.Stop();
                if (currentSplashWindow != null)
                {
                    currentSplashWindow.Hide();
                    currentSplashWindow.Topmost = false;
                }
            };

            splashWindowViewModel = new SplashWindowViewModel();
            GlobalHotKey.RegisterHotKey(settings.Settings.HotkeyGesture, () => SwitchGameState());
        }

        private void SetExclusionList()
        {
            exclusionList = new List<string>
            {
                "7z.exe",
                "7za.exe",
                "Archive.exe",
                "asset_.exe",
                "anetdrop.exe",
                "Bat_To_Exe_Convertor.exe",
                "BsSndRpt.exe",
                "BootBoost.exe",
                "bootstrap.exe",
                "cabarc.exe",
                "CDKey.exe",
                "Cheat Engine.exe",
                "cheatengine",
                "Civ2Map.exe",
                "config",
                "CLOSEPW.EXE",
                "CrashDump",
                "CrashReport",
                "crc32.exe",
                "CreationKit.exe",
                "CreatureUpload.exe",
                "EasyHook.exe",
                "dgVoodooCpl.exe",
                "dotNet",
                "doc.exe",
                "DXSETUP",
                "dw.exe",
                "ENBInjector.exe",
                "HavokBehaviorPostProcess.exe",
                "help",
                "install",
                "LangSelect.exe",
                "Language.exe",
                "Launch",
                "loader",
                "MapCreator.exe",
                "master_dat_fix_up.exe",
                "md5sum.exe",
                "MGEXEgui.exe",
                "modman.exe",
                "ModOrganizer.exe",
                "notepad++.exe",
                "notification_helper.exe",
                "oalinst.exe",
                "PalettestealerSuspender.exe",
                "pak",
                "patch",
                "planet_mapgen.exe",
                "Papyrus",
                "RADTools.exe",
                "readspr.exe",
                "register.exe",
                "SekiroFPSUnlocker",
                "settings",
                "setup",
                "SCUEx64.exe",
                "synchronicity.exe",
                "syscheck.exe",
                "SystemSurvey.exe",
                "TES Construction Set.exe",
                "Texmod.exe",
                "unins",
                "UnityCrashHandler",
                "x360ce",
                "*Unpack",
                "*UnX_Calibrate",
                "update",
                "UnrealCEFSubProcess.exe",
                "url.exe",
                "versioned_json.exe",
                "vcredist",
                "xtexconv.exe",
                "xwmaencode.exe",
                "Website.exe",
                "wide_on.exe"
            };
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            var game = args.Game;
            if (game.Features != null && game.Features.Any(a => a.Name == "[PlayState] Blacklist"))
            {
                logger.Info($"{game.Name} is in PlayState blacklist. Extension execution stopped");
                return;
            }

            Task.Run(async () =>
            {
                currentGame = game;
                splashWindowViewModel.GameName = currentGame.Name;
                isSuspended = false;
                if (game.GameActions != null && game.GameActions.Count > 0)
                {
                    if (game.GameActions[0].Type == GameActionType.Emulator)
                    {
                        var emulator = PlayniteApi.Database.Emulators[game.GameActions[0].EmulatorId];
                        if (emulator != null)
                        {
                            //TODO Somehow get BuiltinProfiles executables
                            var profile = emulator.CustomProfiles.FirstOrDefault(p => p.Id == game.GameActions[0].EmulatorProfileId);
                            if (profile != null)
                            {
                                gameProcesses = GetProcessesWmiQuery(false, profile.Executable.ToLower());
                            }
                        }
                        return;
                    }
                }

                if (string.IsNullOrEmpty(game.InstallDirectory))
                {
                    return;
                }
                gameInstallDir = game.InstallDirectory.ToLower();

                // Fix for some games that take longer to start, even when already detected as running
                await Task.Delay(5000);
                gameProcesses = GetProcessesWmiQuery(true);
                if (gameProcesses.Count > 0)
                {
                    return;
                }

                // Waiting is useful for games that use a startup launcher, since
                // it can take some time before the user launches the game from it
                await Task.Delay(40000);
                var filterPaths = true;
                for (int i = 0; i < 10; i++)
                {
                    // This is done to stop execution in case a new game was launched
                    // or the launched game was closed
                    if (CurrentGameChanged(game))
                    {
                        return;
                    }

                    // Try a few times with filters.
                    // If nothing is found, try without filters. This helps in cases
                    // where the active process is being filtered out by filters
                    if (i == 5)
                    {
                        filterPaths = false;
                    }
                    gameProcesses = GetProcessesWmiQuery(filterPaths);
                    if (gameProcesses.Count > 0)
                    {
                        return;
                    }
                    else
                    {
                        await Task.Delay(15000);
                    }
                }
            });
        }

        private bool CurrentGameChanged(Game game)
        {
            if (currentGame == null || currentGame.Id != game.Id)
            {
                return true;
            }

            return false;
        }

        private void CreateSplashWindow()
        {
            currentSplashWindow = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Topmost = false,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
                Content = new SplashWindow(),
                DataContext = splashWindowViewModel
            };

            currentSplashWindow.Closed += WindowClosed;
        }

        private void ShowSplashWindow(string status)
        {
            if (currentSplashWindow == null)
            {
                CreateSplashWindow();
            }

            splashWindowViewModel.SuspendStatus = status;
            currentSplashWindow.Topmost = true;
            currentSplashWindow.Show();
            timer.Start();
        }

        private void WindowClosed(object sender, EventArgs e)
        {
            currentSplashWindow.Topmost = false;
            currentSplashWindow.Closed -= WindowClosed;
        }

        private void SwitchGameState()
        {
            if (gameProcesses == null || gameProcesses.Count == 0)
            {
                return;
            }
            
            try
            {
                foreach (var gameProcess in gameProcesses)
                {
                    if (gameProcess == null || gameProcess.Process.Handle == null || gameProcess.Process.Handle == IntPtr.Zero)
                    {
                        return;
                    }
                    if (isSuspended)
                    {
                        NtResumeProcess(gameProcess.Process.Handle);
                    }
                    else
                    {
                        NtSuspendProcess(gameProcess.Process.Handle);
                    }
                }

                if (isSuspended)
                {
                    isSuspended = false;
                    ShowSplashWindow(ResourceProvider.GetString("LOCPlayState_StatusResumedMessage"));
                }
                else
                {
                    isSuspended = true;
                    ShowSplashWindow(ResourceProvider.GetString("LOCPlayState_StatusSuspendedMessage"));
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Error while suspending or resuming game");
                gameProcesses = null;
            }
        }

        private List<ProcessItem> GetProcessesWmiQuery(bool filterPaths, string exactPath = null)
        {
            var wmiQueryString = "SELECT ProcessId, ExecutablePath FROM Win32_Process";
            using (var searcher = new ManagementObjectSearcher(wmiQueryString))
            using (var results = searcher.Get())
            {
                // Unfortunately due to Playnite being a 32 bits process, the GetProcess()
                // method can't access needed values of 64 bits processes, so it's needed
                // to correlate with data obtained from a WMI query that is exponentially slower.
                // It needs to be done this way until #1199 is done
                var query = from p in Process.GetProcesses()
                            join mo in results.Cast<ManagementObject>()
                            on p.Id equals (int)(uint)mo["ProcessId"]
                            select new
                            {
                                Process = p,
                                Path = (string)mo["ExecutablePath"],
                            };

                var gameProcesses = new List<ProcessItem>();
                if (exactPath != null)
                {
                    foreach (var fItem in query.Where(i => i.Path != null && i.Path.ToLower() == exactPath))
                    {
                        gameProcesses.Add(
                           new ProcessItem
                           {
                               ExecutablePath = fItem.Path,
                               Process = fItem.Process
                           }
                       );
                    }
                }
                else
                {
                    foreach (var item in query)
                    {
                        if (item.Path == null)
                        {
                            continue;
                        }

                        var pathLower = item.Path.ToLower();
                        if (!pathLower.StartsWith(gameInstallDir))
                        {
                            continue;
                        }
                        if (filterPaths)
                        {
                            var fileName = Path.GetFileName(pathLower);
                            if (exclusionList.Any(e => fileName.Contains(e)))
                            {
                                continue;
                            }
                        }
                        gameProcesses.Add(
                            new ProcessItem
                            {
                                ExecutablePath = item.Path,
                                Process = item.Process
                            }
                        );
                    }
                }

                return gameProcesses;
            }
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            if (currentGame != null && currentGame.Id == args.Game.Id)
            {
                gameProcesses = null;
            }
            if (currentSplashWindow != null)
            {
                currentSplashWindow.Hide();
                currentSplashWindow.Topmost = false;
            }
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            return new List<MainMenuItem>
            {
                new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayState_MenuItemAddToBlacklistDescription"),
                    MenuSection = "@PlayState",
                    Action = a => {
                        AddSelectedGamesToBlacklist();
                    }
                },
                new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayState_MenuItemRemoveFromBlacklistDescription"),
                    MenuSection = "@PlayState",
                    Action = a => {
                        RemoveSelectedGamesFromBlacklist();
                    }
                },
            };
        }

        private void RemoveSelectedGamesFromBlacklist()
        {
            var feature = PlayniteApi.Database.Features.Add("[PlayState] Blacklist");
            int featureRemovedCount = 0;
            foreach (var game in PlayniteApi.MainView.SelectedGames)
            {
                if (game.FeatureIds != null && game.FeatureIds.Contains(feature.Id))
                {
                    game.FeatureIds.Remove(feature.Id);
                    PlayniteApi.Database.Games.Update(game);
                    featureRemovedCount++;
                    logger.Info(string.Format("Removed blacklist feature from \"{0}\"", game.Name));
                }
            }
            PlayniteApi.Dialogs.ShowMessage(string.Format(ResourceProvider.GetString("LOCPlayState_BlacklistRemovedResultsMessage"), featureRemovedCount), "PlayState");
        }

        public void AddSelectedGamesToBlacklist()
        {
            var feature = PlayniteApi.Database.Features.Add("[PlayState] Blacklist");
            int featureAddedCount = 0;
            foreach (var game in PlayniteApi.MainView.SelectedGames)
            {
                if (game.FeatureIds == null)
                {
                    game.FeatureIds = new List<Guid> { feature.Id };
                    PlayniteApi.Database.Games.Update(game);
                    featureAddedCount++;
                }
                else if (game.FeatureIds.AddMissing(feature.Id))
                {
                    PlayniteApi.Database.Games.Update(game);
                    featureAddedCount++;
                }
                else
                {
                    continue;
                }
                logger.Info(string.Format("Added blacklist feature to \"{0}\"", game.Name));
            }

            PlayniteApi.Dialogs.ShowMessage(string.Format(ResourceProvider.GetString("LOCPlayState_BlacklistAddedResultsMessage"), featureAddedCount), "PlayState");
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settings;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new PlayStateSettingsView();
        }
    }
}