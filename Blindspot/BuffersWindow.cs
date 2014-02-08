﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Blindspot.Core;
using Blindspot.Core.Models;
using Blindspot.Helpers;
using Blindspot.ViewModels;
using ScreenReaderAPIWrapper;

namespace Blindspot
{
    public partial class BuffersWindow : Form, IBufferHolder
    {
        public BufferHotkeyManager KeyManager { get; set; }
        public Dictionary<string, HandledEventHandler> Commands { get; set; }
        public BufferListCollection Buffers { get; set; }
        private Track playingTrack;
        private bool isPaused;
        private PlaybackManager playbackManager;
        private SpotifyClient spotify;
        private UserSettings settings = UserSettings.Instance;
        private UpdateManager updater = UpdateManager.Instance;
        private bool downloadedUpdate = false; // for checks for updates
        protected NotifyIcon _trayIcon;
        private BufferList _playQueueBuffer;
        
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
            Commands = new Dictionary<string, HandledEventHandler>();
            playbackManager = new PlaybackManager();
            playbackManager.OnError += new PlaybackManager.PlaybackManagerErrorHandler(StreamingError);
            SetupFormEventHandlers();
            Buffers = new BufferListCollection();
            _playQueueBuffer = new BufferList("Play Queue", false);
            Buffers.Add(_playQueueBuffer);
            Buffers.Add(new BufferList("Playlists", false));
            spotify = SpotifyClient.Instance;
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
            playbackManager.OnPlaybackStopped += new Action(() =>
            {
                playingTrack = null;
                _trayIcon.Text = "Blindspot";
                _playQueueBuffer.RemoveAt(0);
                if (_playQueueBuffer.Count > 0)
                {
                    var nextBufferItem = _playQueueBuffer[0] as TrackBufferItem;
                    PlayNewTrackBufferItem(nextBufferItem);
                    _playQueueBuffer.CurrentItemIndex = 0;
                }
            });
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
            /*Application.ThreadException += new System.Threading.ThreadExceptionEventHandler((sender, e) =>
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
            });*/
        }

        private void InitializeTrayIcon()
        {
            _trayIcon = new NotifyIcon
            {
                ContextMenuStrip = new ContextMenuStrip(),
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
            string username = "", password = "";
            using (LoginWindow logon = new LoginWindow())
            {
                var response = logon.ShowDialog();
                if (response != DialogResult.OK)
                {
                    this.Close();
                    ScreenReader.SayString(StringStore.ExitingProgram);
                    return;
                }
                username = logon.Username;
                password = logon.Password;
            }
            try
            {
                Commands = LoadBufferWindowCommands();
                KeyManager = BufferHotkeyManager.LoadFromTextFile(this);
                SpotifyController.Initialize();
                var appKeyBytes = Properties.Resources.spotify_appkey;
                ScreenReader.SayString(StringStore.LoggingIn);
                bool loggedIn = SpotifyController.Login(appKeyBytes, username, password);
                if (loggedIn)
                {
                    ScreenReader.SayString(StringStore.LoggedInToSpotify);
                    UserSettings.Instance.Username = username;
                    UserSettings.Save();
                    spotify.SetPrivateSession(true);
                }
                else
                {
                    var reason = spotify.GetLoginError().Message;
                    ScreenReader.SayString(StringStore.LogInFailure + reason);
                    // TODO: Make login window reappear until success or exit
                    this.Close();
                    return;
                }
                ScreenReader.SayString(StringStore.LoadingPlaylists, false);
                var playlists = LoadUserPlaylists();
                if (playlists == null) return;
                Buffers[1].Clear();
                playlists.ForEach(p =>
                {
                    Buffers[1].Add(new PlaylistBufferItem(p));
                });
                ScreenReader.SayString(String.Format("{0} {1}", playlists.Count, StringStore.PlaylistsLoaded), false);
                Buffers.CurrentListIndex = 1; // start on the playllists list
            }
            catch (Exception ex)
            {
                MessageBox.Show(StringStore.ErrorDuringLoad + ex.Message, StringStore.Oops, MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.Close();
            }
            ScreenReader.SayString(Buffers.CurrentList.ToString(), false);
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
        public Dictionary<string, HandledEventHandler> LoadBufferWindowCommands()
        {
            var commands = new Dictionary<string, HandledEventHandler>();
            commands.Add("close_blindspot", new HandledEventHandler((sender, e) => ExitBlindspot()));
            commands.Add("next_buffer", new HandledEventHandler((sender, e) =>
            {
                Buffers.NextList();
                ScreenReader.SayString(Buffers.CurrentList.ToString());
            }));
            commands.Add("previous_buffer", new HandledEventHandler((sender, e) =>
            {
                Buffers.PreviousList();
                ScreenReader.SayString(Buffers.CurrentList.ToString());
            }));
            commands.Add("next_buffer_item", new HandledEventHandler((sender, e) =>
            {
                Buffers.CurrentList.NextItem();
                ScreenReader.SayString(Buffers.CurrentList.CurrentItem.ToString());
            }));
            commands.Add("previous_buffer_item", new HandledEventHandler((sender, e) =>
            {
                Buffers.CurrentList.PreviousItem();
                ScreenReader.SayString(Buffers.CurrentList.CurrentItem.ToString());
            }));
            commands.Add("first_buffer_item", new HandledEventHandler((sender, e) =>
            {
                Buffers.CurrentList.FirstItem();
                ScreenReader.SayString(Buffers.CurrentList.CurrentItem.ToString());
            }));
            commands.Add("last_buffer_item", new HandledEventHandler((sender, e) =>
            {
                Buffers.CurrentList.LastItem();
                ScreenReader.SayString(Buffers.CurrentList.CurrentItem.ToString());
            }));
            commands.Add("next_buffer_item_jump", new HandledEventHandler((sender, e) =>
            {
                Buffers.CurrentList.NextJump();
                ScreenReader.SayString(Buffers.CurrentList.CurrentItem.ToString());
            }));
            commands.Add("previous_buffer_item_jump", new HandledEventHandler((sender, e) =>
            {
                Buffers.CurrentList.PreviousJump();
                ScreenReader.SayString(Buffers.CurrentList.CurrentItem.ToString());
            }));
            commands.Add("activate_buffer_item", new HandledEventHandler(BufferItemActivated));
            commands.Add("playback_volume_up", new HandledEventHandler((sender, e) =>
            {
                ScreenReader.SayString(StringStore.Louder);
                playbackManager.VolumeUp(0.05f);
            }));
            commands.Add("playback_volume_down", new HandledEventHandler((sender, e) =>
            {
                ScreenReader.SayString(StringStore.Quieter);
                playbackManager.VolumeDown(0.05f);
            }));
            commands.Add("dismiss_buffer", new HandledEventHandler((sender, e) =>
            {
                var currentBuffer = Buffers.CurrentList;
                if (!currentBuffer.IsDismissable)
                {
                    ScreenReader.SayString(String.Format("{0} {1}", StringStore.CannotDismissBuffer, currentBuffer.Name));
                }
                else
                {
                    Buffers.PreviousList();
                    Buffers.Remove(currentBuffer);
                    ScreenReader.SayString(Buffers.CurrentList.ToString());
                }
                // if it's a buffer with a search or other unmanaged resources, dispose it
                if (currentBuffer is IDisposable)
                {
                    ((IDisposable)currentBuffer).Dispose();
                }
            }));
            commands.Add("announce_now_playing", new HandledEventHandler((sender, e) =>
            {
                if (playingTrack != null)
                {
                    ScreenReader.SayString(playingTrack.ToString());
                }
                else
                {
                    ScreenReader.SayString(StringStore.NoTrackCurrentlyBeingPlayed);
                }
            }));
            commands.Add("new_search", new HandledEventHandler(ShowSearchWindow));
            commands.Add("show_about_window", new HandledEventHandler((sender, e) => ShowAboutDialog()));
            commands.Add("options_dialog", new HandledEventHandler(ShowOptionsWindow));
            commands.Add("item_details", new HandledEventHandler(ShowItemDetailsDialog));
            commands.Add("add_to_queue", new HandledEventHandler((sender, e) =>
            {
                var item = Buffers.CurrentList.CurrentItem;
                if (item is TrackBufferItem)
                {
                    _playQueueBuffer.Add(item);
                    ScreenReader.SayString(StringStore.AddedToQueue, true);
                }
                else
                {
                    
                }
            }));
            return commands;
        }

        private void ExitBlindspot()
        {
            ScreenReader.SayString(StringStore.ExitingProgram);
            if (this.InvokeRequired)
            {
                Invoke(new Action(() => { this.Close(); }));
            }
            else
            {
                this.Close();
            }
        }
        
        private void BufferItemActivated(object sender, HandledEventArgs e)
        {
            var item = Buffers.CurrentList.CurrentItem;
            if (item is TrackBufferItem)
            {
                var tbi = item as TrackBufferItem;
                if (playingTrack != null && tbi.Model.TrackPtr == playingTrack.TrackPtr)
                {
                    if (!isPaused)
                    {
                        // Session.Pause();
                        playbackManager.Pause();
                        isPaused = true;
                        ScreenReader.SayString(StringStore.Paused);
                    }
                    else
                    {
                        // Session.Play();
                        playbackManager.Play();
                        isPaused = false;
                        ScreenReader.SayString(StringStore.Playing);
                    }
                    return;
                }
                ClearCurrentlyPlayingTrack();
                PlayNewTrackBufferItem(tbi);
                if (Buffers.CurrentListIndex == 0 && _playQueueBuffer.Contains(tbi)) // if they've picked it from the play queue
                {
                    Logger.WriteTrace("Clicked a track queue item");
                    int indexOfChosenTrack = _playQueueBuffer.CurrentItemIndex;
                    Logger.WriteTrace("Index is {0} out of a possible {1}", indexOfChosenTrack, _playQueueBuffer.Count - 1);
                    if (indexOfChosenTrack > 0)
                    {
                        _playQueueBuffer.RemoveRange(0, indexOfChosenTrack);
                        Logger.WriteTrace("Removing from 0 to {0}", indexOfChosenTrack - 1);
                    }
                }
                else
                {
                    _playQueueBuffer.Clear();
                    _playQueueBuffer.Add(tbi);                    
                }
                _playQueueBuffer.CurrentItemIndex = 0;
            }
            else if (item is PlaylistBufferItem)
            {
                PlaylistBufferItem pbi = item as PlaylistBufferItem;
                ScreenReader.SayString(StringStore.LoadingPlaylist, false);
                Buffers.Add(new PlaylistBufferList(pbi.Model.Name));
                Buffers.CurrentListIndex = Buffers.Count - 1;
                var playlistBuffer = Buffers.CurrentList;
                ScreenReader.SayString(playlistBuffer.ToString(), false);
                using (var playlist = SpotifyController.GetPlaylist(pbi.Model.Pointer, true))
                {
                    ScreenReader.SayString(String.Format("{0} {1}", playlist.TrackCount, StringStore.TracksLoaded), false);
                    var tracks = playlist.GetTracks();
                    tracks.ForEach(t =>
                    {
                        playlistBuffer.Add(new TrackBufferItem(t));
                    });
                }
            }
            else
            {
                ScreenReader.SayString(String.Format("{0} {1}", item.ToString(), StringStore.ItemActivated), false);
            }
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

        private void ShowSearchWindow(object sender, HandledEventArgs e)
        {
            SearchWindow searchDialog = new SearchWindow();
            searchDialog.ShowDialog();
            if (searchDialog.DialogResult != DialogResult.OK)
            {
                return; // cancelled search
            }
            string searchText = searchDialog.SearchText;
            var searchType = searchDialog.Type;
            searchDialog.Dispose();
            if (searchType == SearchType.Track)
            {
                ScreenReader.SayString(StringStore.Searching, false);
                var search = spotify.SearchTracks(searchText);
                Buffers.Add(new SearchBufferList(search));
                Buffers.CurrentListIndex = Buffers.Count - 1;
                var searchBuffer = Buffers.CurrentList;
                ScreenReader.SayString(searchBuffer.ToString(), false);
                var tracks = search.Tracks;
                if (tracks == null || tracks.Count == 0)
                {
                    if (search != null && !String.IsNullOrEmpty(search.DidYouMean))
	                {
                        searchBuffer.Add(new BufferItem("No search results. Did you mean: " + search.DidYouMean)); 
	                }
                    else
	                {
	                    searchBuffer.Add(new BufferItem(StringStore.NoSearchResults)); 
	                }
                }
                else
                {
                    ScreenReader.SayString(tracks.Count + " " + StringStore.SearchResults, false);
                    foreach (IntPtr pointer in tracks)
                    {
                        searchBuffer.Add(new TrackBufferItem(new Track(pointer)));
                    }
                }
            }
            else if (searchType == SearchType.Artist)
            {
                MessageBox.Show("Not implemented yet! Boo to the developers!", StringStore.Oops, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else if (searchType == SearchType.Album)
            {
                MessageBox.Show("Not implemented yet! Boo to the developers!", StringStore.Oops, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ShowAboutDialog()
        {
            string aboutText = GetApplicationInfoText();
            MessageBox.Show(aboutText, "About Blindspot", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static string GetApplicationInfoText()
        {
            var version = Application.ProductVersion;
            var productName = Application.ProductName;
            StringBuilder aboutInfo = new StringBuilder();
            aboutInfo.AppendFormat("{0} version {1}", productName, version);
            aboutInfo.AppendLine();
            aboutInfo.AppendFormat("Copyright (c) {0} {1}", DateTime.Now.Year, Application.CompanyName);
            aboutInfo.AppendLine();
            aboutInfo.AppendLine();
            aboutInfo.AppendLine("Powered by SPOTIFY(R) CORE");
            return aboutInfo.ToString();
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

        private void ShowOptionsWindow(object sender, HandledEventArgs e)
        {
            OptionsDialog options = new OptionsDialog();
            options.ShowDialog();
            if (options.DialogResult == DialogResult.OK)
            {
                if (settings.UILanguageCode != Thread.CurrentThread.CurrentUICulture.LCID)
                {
                    Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo(settings.UILanguageCode);
                }
                if (options.KeyboardSettingsChanged)
                {
                    KeyManager.Hotkeys.ForEach(hotkey =>
                    {
                        hotkey.Unregister();
                    });
                    KeyManager = BufferHotkeyManager.LoadFromTextFile(this);
                }
            }
        }

        private void ShowItemDetailsDialog(object sender, HandledEventArgs e)
        {
            BufferItem item = Buffers.CurrentList.CurrentItem;
            object model = null;
            if (item is TrackBufferItem) model = ((TrackBufferItem)item).Model;
            else if (item is PlaylistBufferItem) model = ((PlaylistBufferItem)item).Model;
            if (model != null)
            {
                ItemDetailsWindow detailsView = new ItemDetailsWindow(model);
                detailsView.ShowDialog();
            }
            else
            {
                ScreenReader.SayString("View details is not a valid action for this type of item", true);
            }
        }

        private void ClearCurrentlyPlayingTrack()
        {
            if (playingTrack != null)
            {
                Session.UnloadPlayer();
                playbackManager.Stop();
                _trayIcon.Text = "Blindspot";
            }
        }

        private void PlayNewTrackBufferItem(TrackBufferItem item)
        {
            var response = Session.LoadPlayer(item.Model.TrackPtr);
            if (response.IsError)
            {
                ScreenReader.SayString(StringStore.UnableToPlayTrack + response.Message, false);
                return;
            }
            Session.Play();
            playingTrack = item.Model;
            playbackManager.fullyDownloaded = false;
            playbackManager.Play();
            isPaused = false;
            _trayIcon.Text = String.Format("Blindspot - {0}", item.ToString());
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

            _trayIcon.ContextMenuStrip.Items.AddRange(_globalTrayIconMenuItems);
        }

        private ToolStripItem[] _globalTrayIconMenuItems
        {
            get
            {
                return new ToolStripItem[]
                {
                    new ToolStripSeparator(),
                    new ToolStripMenuItem(StringStore.TrayIconAboutMenuItemText, null, new EventHandler((sender2, e2) => ShowAboutDialog())),
                    new ToolStripMenuItem(StringStore.TrayIconExitMenuItemText, null, new EventHandler((sender2, e2) => ExitBlindspot()))
                };
            }
        }

    }
}