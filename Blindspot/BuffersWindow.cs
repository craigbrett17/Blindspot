﻿using ScreenReaderAPIWrapper;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Forms;
using System.Resources;
using Blindspot.Helpers;
using Blindspot.ViewModels;
using Blindspot.Controllers;
using System.IO;
using libspotifydotnet;
using System.Runtime.InteropServices;
using System.Text;

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
        
        #region user32 functions for moving away from window
        // need a bit of pinvoke here to move away from the window if the user manages to reach the window
        [DllImport("user32.dll", EntryPoint = "FindWindow", SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool BringWindowToTop(IntPtr hWnd);
        #endregion

        public BuffersWindow()
        {
            InitializeComponent();
            Commands = new Dictionary<string, HandledEventHandler>();
            playbackManager = new PlaybackManager();
            playbackManager.OnError += new PlaybackManager.PlaybackManagerErrorHandler(StreamingError);
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
            });
            Buffers = new BufferListCollection();
            Buffers.Add(new BufferList("Playlists", false));
            spotify = SpotifyClient.Instance;
            // We want this in for debugging, uncomment it for better UI experience
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

        protected override void OnLoad(EventArgs e)
        {
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
                    UserSettings.Instance.Password = password;
                    UserSettings.Save();
                    spotify.SetPrivateSession(true);
                }
                else
                {
                    var reason = libspotify.sp_error_message(Session.LoginError);
                    ScreenReader.SayString(StringStore.LogInFailure + reason);
                    // TODO: Make login window reappear until success or exit
                    this.Close();
                    return;
                }
                ScreenReader.SayString(StringStore.LoadingPlaylists, false);
                var playlists = SpotifyController.GetAllSessionPlaylists();
                Buffers[0].Clear();
                playlists.ForEach(p =>
                {
                    Buffers[0].Add(new PlaylistBufferItem(p));
                });
                ScreenReader.SayString(String.Format("{0} {1}", playlists.Count, StringStore.PlaylistsLoaded), false);
            }
            catch (Exception ex)
            {
                MessageBox.Show(StringStore.ErrorDuringLoad + ex.Message, StringStore.Oops, MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.Dispose();
            }
            ScreenReader.SayString(Buffers.CurrentList.ToString(), false);
        }

        // here be the various buffer controlling commands
        public Dictionary<string, HandledEventHandler> LoadBufferWindowCommands()
        {
            var commands = new Dictionary<string, HandledEventHandler>();
            commands.Add("close_blindspot", new HandledEventHandler((sender, e) =>
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
            }));
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
            commands.Add("show_about_window", new HandledEventHandler(ShowAboutDialog));
            return commands;
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
                if (playingTrack != null)
                {
                    Session.UnloadPlayer();
                    playbackManager.Stop(); 
                }
                var response = Session.LoadPlayer(tbi.Model.TrackPtr);
                if (response != libspotify.sp_error.OK)
                {
                    var reason = libspotify.sp_error_message(response);
                    ScreenReader.SayString(StringStore.UnableToPlayTrack + reason, false);
                    return;
                }
                Session.Play();
                playingTrack = tbi.Model;
                playbackManager.fullyDownloaded = false;
                playbackManager.Play();
                isPaused = false;
            }
            else if (item is PlaylistBufferItem)
            {
                PlaylistBufferItem pbi = item as PlaylistBufferItem;
                ScreenReader.SayString(StringStore.LoadingPlaylist, false);
                Buffers.Add(new BufferList(pbi.Model.Name));
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
            SearchWindow search = new SearchWindow();
            search.ShowDialog();
            if (search.DialogResult != DialogResult.OK)
            {
                return; // cancelled search
            }
            string searchText = search.SearchText;
            var searchType = search.Type;
            search.Dispose();
            if (searchType == SearchType.Track)
            {
                ScreenReader.SayString(StringStore.Searching, false);
                Buffers.Add(new BufferList("Search for: " + searchText));
                Buffers.CurrentListIndex = Buffers.Count - 1;
                var searchBuffer = Buffers.CurrentList;
                ScreenReader.SayString(searchBuffer.ToString(), false);
                var tracks = spotify.SearchTracks(searchText);
                if (tracks == null || tracks.Count == 0)
                {
                    if (spotify.LastSearch != null && !String.IsNullOrEmpty(spotify.LastSearch.DidYouMean))
	                {
                        searchBuffer.Add(new BufferItem("No search results. Did you mean: " + spotify.LastSearch.DidYouMean)); 
	                }
                    else
	                {
	                    searchBuffer.Add(new BufferItem(StringStore.NoSearchResults)); 
	                }
                }
                else
                {
                    ScreenReader.SayString(tracks.Count + " " + StringStore.SearchResults, false);
                    foreach (Track t in tracks)
                    {
                        searchBuffer.Add(new TrackBufferItem(t));
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

        private void ShowAboutDialog(object sender, HandledEventArgs e)
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
    }
}