﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Blindspot.Commands;
using Blindspot.Core;
using Blindspot.Core.Models;
using Blindspot.Helpers;
using Blindspot.Playback;
using Blindspot.ViewModels;

namespace Blindspot
{
    public partial class BuffersWindow : Form, IBufferHolder
    {
        public BufferHotkeyManager KeyManager { get; set; }
        public Dictionary<string, HotkeyCommandBase> Commands { get; set; }
        public BufferListCollection Buffers { get; set; }
        private PlaybackManager playbackManager;
        private ISpotifyClient spotify;
        private UserSettings settings = UserSettings.Instance;
        private UpdateManager updater = UpdateManager.Instance;
        private IOutputManager output = OutputManager.Instance;
        private bool downloadedUpdate = false; // for checks for updates
        protected NotifyIcon _trayIcon;
        private BufferList _playQueueBuffer;
        private TrayIconMenuManager _trayIconMenuManager;

        #region user32 functions for moving away from window
        // need a bit of pinvoke here to move away from the window if the user manages to reach the window
        [DllImport("user32.dll", EntryPoint = "FindWindow", SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool BringWindowToTop(IntPtr hWnd);
        #endregion

        public BuffersWindow()
        {
            // language stuff needs to happen before InitializeComponent
            if (!settings.DontShowFirstTimeWizard)
            {
                // find out what language was used in the installer and use it for the first time wizard and everything else
                SetThreadToInstallationLanguage();
                DisplayFirstTimeWizard();
            }
            else
            {
                // if a language code is set in settings, set it on the thread
                if (settings.UILanguageCode != 0)
                    Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo(settings.UILanguageCode);
            }
            InitializeComponent();
            InitializeTrayIcon();
            Commands = new Dictionary<string, HotkeyCommandBase>();
            playbackManager = new PlaybackManager(settings.OutputDeviceID, settings.UseDirectSound);
            playbackManager.OnError += new PlaybackManager.PlaybackManagerErrorHandler(StreamingError);

            SetupFormEventHandlers();
            Buffers = new BufferListCollection();
            _playQueueBuffer = new BufferList("Play Queue", false);
            Buffers.Add(_playQueueBuffer);
            Buffers.Add(new PlaylistContainerBufferList("Playlists", false));
            spotify = SpotifyClient.Instance;
            _trayIconMenuManager = new TrayIconMenuManager(Buffers, Commands, _trayIcon);
            settings.PropertyChanged += new PropertyChangedEventHandler(OnUserSettingChange);
        }

        private void SetupFormEventHandlers()
        {
            Session.OnAudioDataArrived += new Action<byte[]>(bytes =>
            {
                playbackManager.AddBytesToPlayingStream(bytes);
            });
            Session.OnAudioStreamComplete += new Action<object>(obj =>
            {
                playbackManager.fullyDownloaded = true;
                Session.UnloadPlayer();
            });
            playbackManager.OnEndOfTrack += new Action(HandleEndOfCurrentTrack);
            playbackManager.OnPlayingTrackChanged += new Action(HandleChangeOfTrack);
            updater.NewVersionDetected += new EventHandler((sender, e) =>
            {
                Version newVersion = sender as Version;
                string updateMessage = StringStore.AnUpdateToBlindspotIsAvailableQuestionNoVersion;
                if (newVersion != null)
                    updateMessage = String.Format(StringStore.AnUpDateToBlindspotIsAvailableQuestionWithVersion, newVersion.Major, newVersion.Minor, newVersion.Build);
                if (MessageBox.Show(updateMessage, StringStore.NewVersionAvailable, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    updater.DownloadNewVersion();
                }
            });
            updater.UpdateDownloaded += new EventHandler((sender, e) =>
            {
                MessageBox.Show(StringStore.NewVersionDownloadedSuccessfully, StringStore.ReadyToInstall, MessageBoxButtons.OK, MessageBoxIcon.Information);
                downloadedUpdate = true;
                updater.RunInstaller();
            });
            updater.UpdateFailed += new EventHandler((sender, e) =>
            {
                var exception = sender as Exception;
                if (exception != null)
                    MessageBox.Show(String.Format("{0}. {1}: {2}", StringStore.AnUnexpectedErrorOccurred, exception.GetType().ToString(), exception.Message), StringStore.ErrorDuringUpdate, MessageBoxButtons.OK, MessageBoxIcon.Error);
            });
            // comment this out for debugging, so that exceptions appear naturally
            Application.ThreadException += new System.Threading.ThreadExceptionEventHandler((sender, e) =>
            {
                if (e.Exception is OutOfMemoryException)
                {
                    MessageBox.Show(StringStore.CriticalError + "\r\n" + String.Format("{0}: {1}", e.Exception.GetType().ToString(), e.Exception.Message), "Out of cheese error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    this.Close(); // on OutOfMemory exceptions, we should close immediately
                    return;
                }
                else
                {
                    MessageBox.Show(StringStore.AnUnexpectedErrorOccurred + "\r\n" + String.Format("{0}: {1}", e.Exception.GetType().ToString(), e.Exception.Message), StringStore.Oops, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });
        }

        private void InitializeTrayIcon()
        {
            _trayIcon = new NotifyIcon
            {
                ContextMenuStrip = new ContextMenuStrip() { AccessibleName = "Blindspot" },
                Icon = new Icon("blindspot.ico"),
                Text = "Blindspot",
                Visible = true
            };
            _trayIcon.ContextMenuStrip.Opening += _trayIcon_ContextMenuStrip_Opening;
            _trayIcon.MouseUp += _trayIcon_MouseUp;
        }

        protected override void OnLoad(EventArgs e)
        {
            if (settings.UpdatesInterestedIn != UserSettings.UpdateType.None)
                updater.CheckForNewVersion();

            if (downloadedUpdate)
            {
                this.Close();
                return;
            }
            SpotifyController.Initialize();
            var appKeyBytes = Properties.Resources.spotify_appkey;
            LoadBufferWindowCommands();
            KeyManager = BufferHotkeyManager.LoadFromTextFile(this);
            string username = "", password = "";
            // the first one controls the loop, the second is the response from Libspotify's login call
            bool isLoggedIn = false, logInResponse = false;
            while (!isLoggedIn)
            {
                using (LoginWindow logon = new LoginWindow())
                {
                    var response = logon.ShowDialog();
                    if (response != DialogResult.OK)
                    {
                        this.Close();
                        output.OutputMessage(StringStore.ExitingProgram);
                        return;
                    }
                    username = logon.Username;
                    password = logon.Password;
                }
                try
                {
                    output.OutputMessage(StringStore.LoggingIn);
                    logInResponse = SpotifyController.Login(appKeyBytes, username, password);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(StringStore.ErrorDuringLoad + "\\r\\nInitialization: " + ex.Message, StringStore.Oops, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    continue;
                }
                if (logInResponse)
                {
                    output.OutputMessage(StringStore.LoggedInToSpotify);
                    UserSettings.Instance.Username = username;
                    UserSettings.Save();
                    spotify.SetPrivateSession(UserSettings.Instance.StartInPrivateSession);
                    isLoggedIn = true;
                }
                else
                {
                    var reason = spotify.GetLoginError().Message;
                    MessageBox.Show(reason, StringStore.LogInFailure, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            output.OutputMessage(StringStore.LoadingPlaylists, false);
            var playlists = LoadUserPlaylists();
            if (playlists == null) return;

            var playlistsBuffer = Buffers[1] as PlaylistContainerBufferList;
            if (playlistsBuffer == null)
                throw new NullReferenceException("PlaylistsBuffer is null");

            playlistsBuffer.Clear();
            playlists.ForEach(p =>
            {
                playlistsBuffer.Add(new PlaylistBufferItem(p));
            });
            // we put a reference to the session container on the playlists buffer
            // so that it can subscribe to playlist added and removed events and in future maybe other things
            playlistsBuffer.Model = PlaylistContainer.GetSessionContainer();

            output.OutputMessage(String.Format("{0} {1}", playlists.Count, StringStore.PlaylistsLoaded), false);
            Buffers.CurrentListIndex = 1; // start on the playllists list
            output.OutputBufferListState(Buffers, NavigationDirection.None, false);
        }

        private List<PlaylistContainer.PlaylistInfo> LoadUserPlaylists()
        {
            string exceptionMessage = "";
            try
            {
                return SpotifyController.GetAllSessionPlaylists();
            }
            // done this way so that we can recursively send this method on if the user chooses to retry
            catch (Exception ex)
            {
                exceptionMessage = ex.Message;
            }
            // handle problems - display dialog to user giving them a chance to retry this operation
            var dialog = MessageBox.Show(StringStore.ErrorDuringLoad + exceptionMessage + "\r\n\r\n" + StringStore.SelectRetryToTryAgainOrCancelToQuit, StringStore.ErrorDuringLoad, MessageBoxButtons.RetryCancel, MessageBoxIcon.Error);
            if (dialog == DialogResult.Retry)
            {
                return LoadUserPlaylists();
            }
            else
            {
                // user has cancelled loading playlists and chosen to exit
                this.Close();
                return null;
            }
        }

        // here be the various buffer controlling commands
        public void LoadBufferWindowCommands()
        {
            // first we make a list of all the commands, so we can slot them into a dictionary with LINQ later
            var hotkeyCommands = new List<HotkeyCommandBase>()
            {
                new CloseCommand(this),
                new NextBufferCommand(Buffers),
                new PreviousBufferCommand(Buffers),
                new NextBufferItemCommand(Buffers),
                new PreviousBufferItemCommand(Buffers),
                new FirstBufferItemCommand(Buffers),
                new LastBufferItemCommand(Buffers),
                new NextBufferItemJumpCommand(Buffers),
                new PreviousBufferItemJumpCommand(Buffers),
                new ActivateBufferItemCommand(Buffers, playbackManager),
                new PlaybackVolumeUpCommand(playbackManager),
                new PlaybackVolumeDownCommand(playbackManager),
                new DismissBufferCommand(Buffers),
                new AnnounceNowPlayingCommand(playbackManager),
                new NewSearchCommand(Buffers),
                new ShowAboutDialogCommand(),
                new ShowOptionsDialogCommand(this),
                new ShowItemDetailsCommand(Buffers),
                new AddToQueueCommand(Buffers),
                new AddNextInQueueCommand(Buffers),
                new MediaPlayPauseCommand(playbackManager),
                new NextTrackCommand(Buffers, playbackManager),
                new PreviousTrackCommand(Buffers, playbackManager),
                new ShowContextMenuCommand(_trayIcon),
                new BrowseGettingStartedCommand(),
                new BrowseHotkeyListCommand(),
                new AddToPlaylistCommand(Buffers),
                new RemoveFromPlaylistCommand(Buffers),
                new ToggleShuffleCommand()
            };

            // the hotkeys use the key to know which command to execute
            hotkeyCommands.ForEach(command =>
            {
                Commands.Add(command.Key, command);
            });
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SpotifyController.ShutDown();
            playbackManager.Dispose();
            _trayIcon.Visible = false; // remove tray icon
            base.OnFormClosing(e);
        }

        private void StreamingError(string message)
        {
            if (InvokeRequired)
            {
                this.Invoke(new PlaybackManager.PlaybackManagerErrorHandler(StreamingError), message);
            }
            else
            {
                MessageBox.Show(message, StringStore.StreamingError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                Session.Pause();
            }
        }

        private void BuffersWindow_Activated(object sender, EventArgs e)
        {
            // cheeky little way of forceably moving user off of this invisible window in rare events that it's reached
            IntPtr desktopHwnd = FindWindow("Shell_TrayWnd", null);
            BringWindowToTop(desktopHwnd);
        }

        private void SetThreadToInstallationLanguage()
        {
            RegistryReader regReader = new RegistryReader();
            var installedLang = regReader.Read("Installer Language");
            if (!String.IsNullOrEmpty(installedLang))
            {
                var culture = new System.Globalization.CultureInfo(Convert.ToInt32(installedLang));
                // get the neutral culture (as opposed to area specific ones), we're only interested in language for now
                if (!culture.IsNeutralCulture) culture = culture.Parent;
                Thread.CurrentThread.CurrentUICulture = culture;
            }
        }

        private void DisplayFirstTimeWizard()
        {
            FirstTimeWizard wizard = new FirstTimeWizard();
            wizard.ShowDialog();
            if (wizard.DialogResult == DialogResult.OK)
            {
                // language may have changed, will be saved to settings object
                // if settings is different from current (which came from installer or fallback of OS default), change to new one
                if (settings.UILanguageCode != Thread.CurrentThread.CurrentUICulture.LCID)
                {
                    Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo(settings.UILanguageCode);
                }
            }
        }

        private void ClearCurrentlyPlayingTrack()
        {
            if (playbackManager.PlayingTrack != null)
            {
                playbackManager.Stop();
                Session.UnloadPlayer();
            }
        }

        private void PlayTrackBufferItem(TrackBufferItem item)
        {
            var response = Session.LoadPlayer(item.Model.TrackPtr);
            if (response.IsError && !settings.SkipUnplayableTracks)
            {
                output.OutputMessage(StringStore.UnableToPlayTrack + response.Message, false);
                return;
            }
            if (response.IsError && settings.SkipUnplayableTracks)
            {
                HandleEndOfCurrentTrack();
                return; // don't carry on with this, as it got handled in a recursive call
            }
            Session.Play();
            playbackManager.PlayingTrack = item.Model;
            playbackManager.fullyDownloaded = false;
            playbackManager.Play();
            output.OutputTrackModel(playbackManager.PlayingTrack,
                    settings.OutputTrackChangesGraphically, settings.OutputTrackChangesWithSpeech);
        }

        private void HandleEndOfCurrentTrack()
        {
            playbackManager.AddCurrentTrackToPreviousTracks();
            playbackManager.PlayingTrack = null;
            _playQueueBuffer.RemoveAt(0);
            PlayNextQueuedTrack();
        }

        private void PlayNextQueuedTrack()
        {
            if (_playQueueBuffer.Count > 0)
            {
                var nextBufferItem = _playQueueBuffer[0] as TrackBufferItem;
                PlayTrackBufferItem(nextBufferItem);
                _playQueueBuffer.CurrentItemIndex = 0;
            }
        }

        private void _trayIcon_MouseUp(object sender, MouseEventArgs e)
        {
            // making left clicks launch the context menu as well
            if (e.Button == MouseButtons.Left)
            {
                MethodInfo mi = typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
                mi.Invoke(_trayIcon, null);
            }
        }

        private void _trayIcon_ContextMenuStrip_Opening(object sender, CancelEventArgs e)
        {
            e.Cancel = false; // we haven't got any items in there yet, but please don't cancel!
            _trayIcon.ContextMenuStrip.Items.Clear(); // rebuild the context menu dynamically

            _trayIconMenuManager.BuildContextMenu();
        }


        private void HandleChangeOfTrack()
        {
            if (playbackManager.PlayingTrack == null)
            {
                _trayIcon.Text = "Blindspot";
            }
            else
            {
                var trackItem = new TrackBufferItem(playbackManager.PlayingTrack);
                _trayIcon.Text = String.Format("Blindspot - {0}", trackItem.ToTruncatedString());
            }
        }

        public void ReRegisterHotkeys()
        {
            KeyManager.Hotkeys.ForEach(hotkey =>
            {
                if (hotkey.Registered)
                    hotkey.Unregister();
            });
            KeyManager = BufferHotkeyManager.LoadFromTextFile(this);
        }

        private void OnUserSettingChange(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case "OutputDeviceID":
                    playbackManager.PlaybackDeviceID = settings.OutputDeviceID;
                    break;
                default:
                    break;
            }
        }

    }
}