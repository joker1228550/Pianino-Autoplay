using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Utilities;
//This is how to tell the form application to use the core
//You will need to use this if you want to make your own GUI
//vvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvv
using Tower_Unite_Instrument_Autoplayer.Core;

namespace Tower_Unite_Instrument_Autoplayer.GUI
{
    public partial class GraphicalUserInterface : Form
    {
        //Here we create a thread that will be used to play the song
        //This way the program will not freeze while playing
        List<Thread> songThreads = new List<Thread>();

        //This is the hook for the key bindings, until I find a better solution
        GlobalKeyboardHook gkh = new GlobalKeyboardHook();

        //The key bind to start playing
        Keys startKey;
        //The key bind to stop playing
        Keys stopKey;

        //This variable is used to ignore changes to the note box when loading a saved file
        bool isLoading = false;

        //Tracks which characters in NoteTextBox are currently painted red for the "now playing"
        //highlight, so the previous highlight can be reset back to normal before a new one is
        //applied. -1/0 means nothing is currently highlighted.
        int highlightedStart = -1;
        int highlightedLength = 0;

        //True while a playlist sequence (RunPlaylist) is actively running - including during
        //the ~5 second gap between songs, not just while a song is actually sounding. While
        //true, PlayButton/F2 is repurposed to mean "skip to the next track".
        bool isPlaylistPlaying = false;
        //True while "play selection" has temporarily swapped in just the highlighted text for
        //playback - checked by IsAnythingPlaying so a regular Play click can't start a second,
        //conflicting playback while this is going on.
        bool isSelectionPlaying = false;
        //Set by PlayButton_Click while a playlist is playing, to tell RunPlaylist's between-
        //songs wait to end early rather than waiting out the rest of the ~5 seconds.
        volatile bool playlistSkipRequested = false;
        //Where playlist entries are saved/loaded from - a "playlist" folder created next to the
        //executable the first time it's needed.
        static readonly string PlaylistFolder = System.IO.Path.Combine(Application.StartupPath, "playlist");
        //The title of whatever song is currently loaded, if it's known (set from a Virtual
        //Piano import's title, or a regular loaded file's name) - used so "Save to Playlist"
        //can name the file after the actual song instead of only ever using a timestamp.
        //Cleared when the notes are cleared, since it no longer refers to anything meaningful.
        string lastLoadedSongTitle = null;
        //The most recently auto-applied normal delay (from a song import), if any hasn't
        //already been "consumed" by a manual correction - used so a manual edit to
        //NormalDelayBox right after an import can be recognised as a correction to that
        //specific suggestion, rather than an unrelated edit.
        int? lastSuggestedNormalDelayMs = null;

        /// <summary>
        /// This is the constructor for the GUI
        /// It is called when the GUI starts
        /// </summary>
        public GraphicalUserInterface()
        {
            InitializeComponent();
            ErrorLabel.Hide();
            //Autoplayer.Version is also written as the first line of saved files and checked
            //against SupportedVersionsSave when loading, so it must stay "Version: x.x.x" in
            //English. Only the on-screen label is translated, derived from that same string.
            VersionLabel.Text = Autoplayer.Version.Replace("Version:", "Версия:");
            Autoplayer.AddingNoteFinished += EnablePlayButton;
            Autoplayer.SongFinishedPlaying += EnablePlayButton;
            Autoplayer.SongFinishedPlaying += EnableClearButton;
            Autoplayer.SongFinishedPlaying += ClearNoteHighlight;
            Autoplayer.SongWasStopped += EnablePlayButton;
            Autoplayer.SongWasStopped += EnableClearButton;
            Autoplayer.SongWasStopped += SongStopped;
            Autoplayer.SongWasStopped += ClearNoteHighlight;
            Autoplayer.SongWasInteruptedByException += ExceptionHandler;
            Autoplayer.NotePlaying += OnNotePlaying;

            //Subscribe the method "GKS_KeyDown" to the KeyDown event of the GlobalKeyboardHook
            gkh.KeyDown += new KeyEventHandler(GKS_KeyDown);

            //This converts the text from the keybind settings window to actual keys
            //Then we add them to the global hook (This is done so the keypresses will be detected when the application is not in focus)
            KeysConverter keysConverter = new KeysConverter();
            startKey = (Keys)keysConverter.ConvertFromString(StartKeyTextBox.Text.ToString());
            stopKey = (Keys)keysConverter.ConvertFromString(StopKeyTextBox.Text.ToString());
            gkh.HookedKeys.Add(startKey);
            gkh.HookedKeys.Add(stopKey);

            //Populate the Song Selection tab's genre list.
            foreach (VirtualPianoGenre genre in VirtualPianoImporter.GetKnownGenres())
            {
                GenreComboBox.Items.Add(genre);
            }
            if (GenreComboBox.Items.Count > 0)
            {
                GenreComboBox.SelectedIndex = 0;
            }

            VPPasteDifficultyBox.Items.AddRange(new object[] { "Super Easy", "Easy", "Intermediate", "Expert" });
            VPPasteDifficultyBox.SelectedIndex = 1;

            GetSongBpmApiKeyBox.Text = Properties.Settings.Default.GetSongBpmApiKey;

            RefreshPlaylistSongsList();

            //These pairs sit side by side and should each grow to fill their share of the
            //available width as the window resizes - plain Anchor properties can pin a control
            //to an edge or stretch it across its whole parent, but there's no Anchor combination
            //that splits newly-available space evenly between two separate controls, so this is
            //done by hand on every resize instead. Each pair's own Anchor is set to Top|Bottom
            //only (no Left/Right) in the Designer, leaving X position and width entirely to this.
            groupBox2.Anchor = AnchorStyles.Top | AnchorStyles.Bottom;
            groupBox3.Anchor = AnchorStyles.Top | AnchorStyles.Bottom;
            CustomizeTabButton.Resize += (s, e) => SplitPanelsEvenly(CustomizeTabButton, groupBox2, groupBox3, 6, 0);
            SplitPanelsEvenly(CustomizeTabButton, groupBox2, groupBox3, 6, 0);

            genreArtistGroup.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            genreSongGroup.Anchor = AnchorStyles.Top | AnchorStyles.Bottom;
            GenreTabPage.Resize += (s, e) => SplitPanelsEvenly(GenreTabPage, genreArtistGroup, genreSongGroup, 8, 8);
            SplitPanelsEvenly(GenreTabPage, genreArtistGroup, genreSongGroup, 8, 8);

            PlaylistSongsListBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            PlaylistStatusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Bottom;
            groupBox1.Resize += (s, e) => SplitPanelsEvenly(groupBox1, PlaylistSongsListBox, PlaylistStatusLabel, 10, 3);
            SplitPanelsEvenly(groupBox1, PlaylistSongsListBox, PlaylistStatusLabel, 10, 3);
        }

        /// <summary>
        /// Resizes two side-by-side controls to each fill half of the container's available
        /// width (minus margin on each outer edge and gap between them), keeping each control's
        /// existing Y position and height untouched. Called once up front and again on every
        /// resize of the given container.
        /// </summary>
        private void SplitPanelsEvenly(Control container, Control left, Control right, int gap, int margin)
        {
            int totalWidth = container.ClientSize.Width - margin * 2 - gap;
            if (totalWidth < 100)
                return; //Avoid nonsensical sizes while minimized or mid-layout.

            int leftWidth = totalWidth / 2;
            int rightWidth = totalWidth - leftWidth;

            left.Location = new System.Drawing.Point(margin, left.Location.Y);
            left.Width = leftWidth;
            right.Location = new System.Drawing.Point(left.Right + gap, right.Location.Y);
            right.Width = rightWidth;
        }

        /// <summary>
        /// This method handles the key bind presses
        /// NOTE: This might trigger anti-virus software
        /// as this is a popular method used in keyloggers
        /// </summary>
        private void GKS_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == startKey)
            {
                PlayButton.PerformClick();
            }
            else if (e.KeyCode == stopKey)
            {
                StopButton.PerformClick();
            }
            e.Handled = true;
        }

        /// <summary>
        /// This method will run all the update methods.
        /// </summary>
        public void UpdateEverything()
        {
            UpdateNoteBox();
            UpdateDelayListBox();
            NormalDelayBox.Value = Autoplayer.DelayAtNormalSpeed;
            FastDelayBox.Value = Autoplayer.DelayAtFastSpeed;
            isLoading = false;
        }
        /// <summary>
        /// This method updates the DelayListBox with all delays from the Delays list in the main program
        /// Each custom delay has its own line
        /// </summary>
        public void UpdateDelayListBox()
        {
            DelayListBox.Items.Clear();
            foreach (Delay delay in Autoplayer.Delays)
            {
                //Adding the Delay object itself (rather than a formatted string) is what makes
                //clicking an entry meaningful - DelayListBox_SelectedIndexChanged can read the
                //selected item's own Character/Time straight back out, instead of the list being
                //just an unselectable block of text with no link back to the underlying data.
                DelayListBox.Items.Add(delay);
            }
        }
        /// <summary>
        /// This method updates the CustomNotesListBox with all notes from the CustomNotes list in the main program
        /// Each custom note has its own line
        /// </summary>
        public void UpdateCustomNoteListBox()
        {
            CustomNoteListBox.Items.Clear();
            foreach (Note note in Autoplayer.CustomNotes.Keys)
            {
                Note newNote;
                Autoplayer.CustomNotes.TryGetValue(note, out newNote);
                CustomNoteListBox.Items.Add(new CustomNoteMapping { FromChar = note.Character, ToChar = newNote.Character });
            }
        }
        /// <summary>
        /// This method updates the NoteTextBox with all notes from the Song list in the main program
        /// </summary>
        public void UpdateNoteBox()
        {
            NoteTextBox.Clear();
            //Whatever was highlighted no longer means anything once the box has been rebuilt.
            highlightedStart = -1;
            highlightedLength = 0;
            foreach (INote note in Autoplayer.Song)
            {
                if (note is DelayNote)
                {
                    NoteTextBox.Text += (((DelayNote)note).Character);
                }
                else if (note is SpeedChangeNote)
                {
                    if (((SpeedChangeNote)note).TurnOnFast)
                    {
                        NoteTextBox.Text += "{";
                    }
                    else
                    {
                        NoteTextBox.Text += "}";
                    }
                }
                else if (note is Note)
                {
                    NoteTextBox.Text += ((Note)note).Character;
                }
                else if (note is MultiNote)
                {
                    NoteTextBox.Text += "[";
                    foreach (Note multiNote in ((MultiNote)note).Notes)
                    {
                        NoteTextBox.Text += multiNote.Character;
                    }
                    NoteTextBox.Text += "]";
                }
            }
        }
        /// <summary>
        /// This method makes or remakes the song by clearing all notes and adding the ones from NoteTextBox
        /// </summary>
        private void MakeSong()
        {
            try
            {
                ErrorLabel.Hide();
                DisablePlayButton();
                Autoplayer.ClearAllNotes();
                Autoplayer.AddNotesFromString(NoteTextBox.Text);
            }
            catch (AutoplayerNoteCreationFailedException e)
            {
                ErrorLabel.Text = $"ОШИБКА: {e.Message}";
                ErrorLabel.Show();
            }
        }
        
        /// <summary>
        /// This method disables the play button so the user cannot press it
        /// </summary>
        private void DisablePlayButton()
        {
            PlayButton.Enabled = false;
        }
        /// <summary>
        /// This method enables the play button and makes it interactable.
        /// This can be called from the background playback thread (via SongFinishedPlaying/
        /// SongWasStopped) as well as the UI thread (via AddingNoteFinished), so it needs the
        /// same cross-thread marshaling as EnableClearButton below.
        /// </summary>
        private void EnablePlayButton()
        {
            if (PlayButton.InvokeRequired)
            {
                MethodInvoker methodInvokerDelegate = delegate () { PlayButton.Enabled = true; };
                PlayButton.Invoke(methodInvokerDelegate);
            }
            else
            {
                PlayButton.Enabled = true;
            }
        }

        /// <summary>
        /// Unlocks the notes textbox and clears any red "now playing" highlight left over from
        /// the song that just finished or was stopped. Called once per song (not per note), so
        /// unlike OnNotePlaying below, a normal synchronous Invoke here is fine.
        /// </summary>
        private void ClearNoteHighlight()
        {
            if (NoteTextBox.InvokeRequired)
            {
                MethodInvoker methodInvokerDelegate = delegate () { ClearNoteHighlightOnUiThread(); };
                NoteTextBox.Invoke(methodInvokerDelegate);
            }
            else
            {
                ClearNoteHighlightOnUiThread();
            }
        }

        private void ClearNoteHighlightOnUiThread()
        {
            NoteTextBox.ReadOnly = false;
            if (highlightedLength > 0 && highlightedStart + highlightedLength <= NoteTextBox.TextLength)
            {
                NoteTextBox.Select(highlightedStart, highlightedLength);
                NoteTextBox.SelectionColor = NoteTextBox.ForeColor;
                NoteTextBox.Select(0, 0);
            }
            highlightedStart = -1;
            highlightedLength = 0;
        }

        /// <summary>
        /// This is called from Autoplayer's background playback thread, once for every note,
        /// right before it plays - so it deliberately uses BeginInvoke (fire-and-forget) rather
        /// than Invoke. Invoke would block the playback thread until the UI thread gets round to
        /// processing it, and with this firing on every single note that delay would accumulate
        /// across the whole song, drifting the actual playback timing away from what's configured.
        /// </summary>
        private void OnNotePlaying(int songIndex)
        {
            if (!NoteTextBox.IsHandleCreated || NoteTextBox.IsDisposed)
                return;

            NoteTextBox.BeginInvoke((MethodInvoker)delegate ()
            {
                HighlightPlayingNote(songIndex);
            });
        }

        private void HighlightPlayingNote(int songIndex)
        {
            if (songIndex < 0 || songIndex >= Autoplayer.SongEntryStart.Count || songIndex >= Autoplayer.SongEntryLength.Count)
                return;

            //Reset the previously highlighted range back to the box's normal text colour.
            if (highlightedLength > 0 && highlightedStart + highlightedLength <= NoteTextBox.TextLength)
            {
                NoteTextBox.Select(highlightedStart, highlightedLength);
                NoteTextBox.SelectionColor = NoteTextBox.ForeColor;
            }

            int start = Autoplayer.SongEntryStart[songIndex];
            int length = Autoplayer.SongEntryLength[songIndex];
            int collapseTo = 0;

            //The text can only have changed since this song was parsed if something went wrong
            //elsewhere, but check bounds defensively anyway rather than risk an exception on the
            //playback thread's UI callback.
            if (start >= 0 && length > 0 && start + length <= NoteTextBox.TextLength)
            {
                NoteTextBox.Select(start, length);
                NoteTextBox.SelectionColor = System.Drawing.Color.Red;
                NoteTextBox.ScrollToCaret();
                highlightedStart = start;
                highlightedLength = length;
                collapseTo = start + length;
            }
            else
            {
                highlightedStart = -1;
                highlightedLength = 0;
            }

            //Collapse the selection so only the text colour changes - it shouldn't look like a
            //dragged/selected block of text.
            NoteTextBox.Select(collapseTo, 0);
            NoteTextBox.SelectionColor = NoteTextBox.ForeColor;
        }

        /// <summary>
        /// This method disables the clear button so the user cannot press it
        /// </summary>
        private void DisableClearButton()
        {
            ClearNotesButton.Enabled = false;
        }
        /// <summary>
        /// This method enables the clear button and makes it interactable
        /// </summary>
        private void EnableClearButton()
        {
            if (ClearNotesButton.InvokeRequired)
            {
                MethodInvoker methodInvokerDelegate = delegate () { ClearNotesButton.Enabled = true; };
                ClearNotesButton.Invoke(methodInvokerDelegate);
            }
            else
            {
                ClearNotesButton.Enabled = true;
            }
        }
        
        /// <summary>
        /// This method will handle exceptions thrown from other threads than the current one
        /// This was added because I had some problems with exceptions from other threads not being catched
        /// </summary>
        private void ExceptionHandler(AutoplayerException exception)
        {
            ErrorLabel.Text = exception.Message;
            ErrorLabel.Show();
        }

        #region Custom delay buttons
        /// <summary>
        /// This is called when the selected item in DelayListBox changes - populates the
        /// Символ/Задержка fields with the clicked entry's own values, so editing an existing
        /// delay is just: click it, adjust the number, click "Добавить задержку" again (which
        /// already updates rather than duplicates an existing character).
        /// </summary>
        private void DelayListBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            Delay selected = DelayListBox.SelectedItem as Delay;
            if (selected == null)
                return;

            CustomDelayCharacterBox.Text = selected.Character.ToString();
            CustomDelayTimeBox.Value = ClampToNumericRange(selected.Time, CustomDelayTimeBox);
        }
        /// <summary>
        /// This is called when we click the AddDelayButton
        /// </summary>
        private void AddDelayButton_Click(object sender, EventArgs e)
        {
            try
            {
                if(Autoplayer.CheckDelayExists(CustomDelayCharacterBox.Text.ToCharArray()[0]))
                {
                    //If the delay already exists, just update the time value
                    Autoplayer.ChangeDelay(CustomDelayCharacterBox.Text.ToCharArray()[0], (int)CustomDelayTimeBox.Value);
                }
                else
                {
                    //If the delay does not exist, add a new entry
                    Autoplayer.AddDelay(CustomDelayCharacterBox.Text.ToCharArray()[0], (int)CustomDelayTimeBox.Value);
                }
                //Update the GUI element to show the delays in the GUI
                UpdateDelayListBox();

                //Update the current notes to with the new rules
                MakeSong();
            }
            catch (AutoplayerCustomDelayException error)
            {
                MessageBox.Show(error.Message);
            }
        }
        /// <summary>
        /// This is called when we click the RemoveDelayButton
        /// </summary>
        private void RemoveDelayButton_Click(object sender, EventArgs e)
        {
            try
            {
                //Prefer the selected entry in the list - this is what makes "remove just this
                //one" actually usable, rather than needing to already know and retype the exact
                //character. Falls back to whatever's typed in Символ if nothing is selected, so
                //typing a character directly still works too.
                Delay selected = DelayListBox.SelectedItem as Delay;
                char character = selected != null ? selected.Character : CustomDelayCharacterBox.Text.ToCharArray()[0];

                //Remove the delay from the list
                Autoplayer.RemoveDelay(character);
                //Update the GUI
                UpdateDelayListBox();

                //Update the current notes wtih the new rules
                MakeSong();
            }
            catch (AutoplayerCustomDelayException error)
            {
                MessageBox.Show(error.Message);
            }
        }
        /// <summary>
        /// This is called when we click the RemoveAllDelayButton
        /// </summary>
        private void RemoveAllDelayButton_Click(object sender, EventArgs e)
        {
            //Clear the list of delays
            Autoplayer.ResetDelays();

            //Update the GUI
            UpdateDelayListBox();

            //Update the current notes wtih the new rules
            MakeSong();
        }
        #endregion

        #region Custom note buttons
        /// <summary>
        /// This is called when we click the AddNoteButton
        /// </summary>
        /// <summary>
        /// A single custom note remapping, shown as an item in CustomNoteListBox. Note itself
        /// isn't reused here since a mapping needs both the original and replacement character
        /// together, not just one Note's own identity.
        /// </summary>
        private class CustomNoteMapping
        {
            public char FromChar { get; set; }
            public char ToChar { get; set; }

            public override string ToString()
            {
                return $"Изменено с '{FromChar}' на '{ToChar}'";
            }
        }

        /// <summary>
        /// This is called when the selected item in CustomNoteListBox changes - populates both
        /// character fields with the clicked entry's own values, the same way
        /// DelayListBox_SelectedIndexChanged does for delays.
        /// </summary>
        private void CustomNoteListBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            CustomNoteMapping selected = CustomNoteListBox.SelectedItem as CustomNoteMapping;
            if (selected == null)
                return;

            CustomNoteCharacterBox.Text = selected.FromChar.ToString();
            CustomNoteNewCharacterBox.Text = selected.ToChar.ToString();
        }

        private void AddNoteButton_Click(object sender, EventArgs e)
        {
            try
            {
                char character = CustomNoteCharacterBox.Text.ToCharArray()[0];
                char newCharacter = CustomNoteNewCharacterBox.Text.ToCharArray()[0];

                WindowsInput.Native.VirtualKeyCode vkOld;
                WindowsInput.Native.VirtualKeyCode vkNew;
                try
                {
                    Autoplayer.VirtualDictionary.TryGetValue(character, out vkOld);
                    Autoplayer.VirtualDictionary.TryGetValue(newCharacter, out vkNew);

                    if (vkOld == 0 || vkNew == 0)
                    {
                        return;
                    }
                }
                catch (ArgumentNullException)
                {
                    return;
                }

                //This will check if the note is an uppercase letter, or if the note is in the list of high notes
                bool isOldHighNote = char.IsUpper(character) || Autoplayer.AlwaysHighNotes.Contains(character);
                bool isNewHighNote = char.IsUpper(newCharacter) || Autoplayer.AlwaysHighNotes.Contains(newCharacter);

                Note note = new Note(character, vkOld, isOldHighNote);
                Note newNote = new Note(newCharacter, vkNew, isNewHighNote);

                if (Autoplayer.CheckNoteExists(note))
                {
                    //If the note already exists, just update it
                    Autoplayer.ChangeNote(note, newNote);
                }
                else
                {
                    //If the note does not exist, add a new entry
                    Autoplayer.AddNote(note, newNote);
                }
                //Update the GUI element to show the delays in the GUI
                UpdateCustomNoteListBox();

                //Update the current notes to with the new rules
                MakeSong();
            }
            catch (AutoplayerCustomNoteException error)
            {
                MessageBox.Show(error.Message);
            }
        }
        /// <summary>
        /// This is called when we click the RemoveNoteButton
        /// </summary>
        private void RemoveNoteButton_Click(object sender, EventArgs e)
        {
            try
            {
                //Prefer the selected entry in the list, the same way RemoveDelayButton_Click
                //does - falls back to whatever's typed in Символ if nothing is selected.
                CustomNoteMapping selected = CustomNoteListBox.SelectedItem as CustomNoteMapping;
                char character = selected != null ? selected.FromChar : CustomNoteCharacterBox.Text.ToCharArray()[0];

                WindowsInput.Native.VirtualKeyCode vk;
                try
                {
                    Autoplayer.VirtualDictionary.TryGetValue(character, out vk);

                    if (vk == 0)
                    {
                        return;
                    }
                }
                catch (ArgumentNullException)
                {
                    return;
                }

                //This will check if the note is an uppercase letter, or if the note is in the list of high notes
                bool isHighNote = char.IsUpper(character) || Autoplayer.AlwaysHighNotes.Contains(character);

                Note note = new Note(character, vk, isHighNote);

                //Remove the note from the dictonary
                Autoplayer.RemoveNote(note);
                //Update the GUI
                UpdateCustomNoteListBox();

                //Update the current notes wtih the new rules
                MakeSong();
            }
            catch (AutoplayerCustomNoteException error)
            {
                MessageBox.Show(error.Message);
            }
        }
        /// <summary>
        /// This is called when we click the RemoveAllNotesButton
        /// </summary>
        private void RemoveAllNotesButton_Click(object sender, EventArgs e)
        {
            //Clear the dictonary of custom notes
            Autoplayer.ResetNotes();

            //Update the GUI
            UpdateCustomNoteListBox();

            //Update the current notes wtih the new rules
            MakeSong();
        }
        #endregion

        /// <summary>
        /// This is called when we click the ClearNotesButton
        /// </summary>
        private void ClearNotesButton_Click(object sender, EventArgs e)
        {
            Autoplayer.ClearAllNotes();
            //Updates the note box GUI element to show the notes in the GUI
            UpdateNoteBox();
            lastLoadedSongTitle = null;
        }
        /// <summary>
        /// True if a regular playback thread, a playlist, or "play selection" is currently
        /// active. Used everywhere that needs to guard against starting a second, conflicting
        /// playback - see the comment on PlayButton_Click for why that matters.
        /// </summary>
        private bool IsAnythingPlaying()
        {
            return songThreads.Any(t => t.IsAlive) || isPlaylistPlaying || isSelectionPlaying;
        }

        /// <summary>
        /// This is called when we click the PlayButton
        /// </summary>
        private void PlayButton_Click(object sender, EventArgs e)
        {
            //While a playlist is actively running, this same button/hotkey means "skip to the
            //next track" instead of "start a new playback" - stopping the current song lets
            //RunPlaylist's loop (which is blocked waiting on Autoplayer.PlaySong) regain control
            //and move on immediately, without waiting out the rest of the gap between songs.
            if (isPlaylistPlaying)
            {
                playlistSkipRequested = true;
                Autoplayer.StopSong();
                return;
            }

            //Guard against ever having two PlaySong threads running at once: if that happens,
            //both threads independently press/release keys with their own timing, so the game
            //receives an unpredictable, overlapping mix of keypresses - notes that should be
            //sequential can arrive bunched together, or with extra delay, depending on how the
            //threads happen to interleave. This is very likely what "sometimes normal, sometimes
            //delayed and all at once" playback was caused by: nothing stopped a second click (or
            //a second hotkey press) from starting another thread while one was already playing.
            if (IsAnythingPlaying())
            {
                return;
            }

            //Disable the clear notes button so we don't clear the notes
            //while trying to play them
            DisableClearButton();
            //Disable the play button so we don't create another thread
            //while one is running
            DisablePlayButton();
            //Make the notes read-only while playing: edits wouldn't affect the already-parsed
            //Song being played back anyway, and would desync the highlight-tracking indices
            //below from what's actually on screen. This is playback-specific, unlike
            //DisablePlayButton/EnablePlayButton above which are also toggled on every keystroke
            //by MakeSong while reparsing, so it's handled separately rather than folded in there.
            NoteTextBox.ReadOnly = true;
            //Start the song in a new thread so we can do other things in
            //the program when the song is playing. Stopping the song for example
            songThreads.Insert(0, new Thread(Autoplayer.PlaySong));
            songThreads[0].Start();
        }
        /// <summary>
        /// This is called when we click the StopButton
        /// </summary>
        private void StopButton_Click(object sender, EventArgs e)
        {
            //Ensure a running playlist sequence exits entirely rather than just stopping the
            //current song and moving on to the next one.
            isPlaylistPlaying = false;
            Autoplayer.StopSong();
        }
        private void SongStopped()
        {
            //PlaySong() already exits promptly and gracefully on its own once Stop is set (it
            //returns immediately after releasing every held key, rather than looping through
            //the rest of the song) - by the time this event fires, that return is already
            //either complete or one line away. Forcefully aborting the thread on top of that
            //used to happen here, but Thread.Abort() injects its exception at a point in the
            //thread's execution that can't be predicted or controlled - if that happens to land
            //between a key's KeyDown and its matching KeyUp (inside the native SendInput call
            //itself, or anywhere in between), that key is left stuck held down as far as the
            //game is concerned, which would then interfere with whatever note tries to use that
            //same key next. Since the thread is already finishing up on its own, all that's
            //actually needed here is to stop tracking it and restore the UI.
            songThreads.Clear();
            EnableClearButton();
        }
        /// <summary>
        /// This is called when we click the LoadButton
        /// </summary>
        private void LoadButton_Click(object sender, EventArgs e)
        {
            OpenFileDialog fileDialog = new OpenFileDialog();

            //This sets the load dialog to filter on .txt files.
            fileDialog.Filter = "Текстовый файл | *.txt";

            if (fileDialog.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    isLoading = true;
                    Autoplayer.ResetDelays();
                    Autoplayer.LoadSong(fileDialog.FileName);
                    //Update everything when we are done loading
                    UpdateEverything();
                    lastLoadedSongTitle = System.IO.Path.GetFileNameWithoutExtension(fileDialog.FileName);
                    MessageBox.Show("Загрузка завершена");
                }
                catch (AutoplayerLoadFailedException error)
                {
                    isLoading = false;
                    MessageBox.Show($"Ошибка загрузки: {error.Message}");
                }
            }
        }
        /// <summary>
        /// This is called when we click the SaveButton
        /// </summary>
        private void SaveButton_Click(object sender, EventArgs e)
        {
            SaveFileDialog fileDialog = new SaveFileDialog();

            //This sets the save dialog to filter on .txt files.
            fileDialog.Filter = "Текстовый файл | *.txt";

            if (fileDialog.ShowDialog() == DialogResult.OK)
            {
                Autoplayer.SaveSong(fileDialog.FileName);
                MessageBox.Show($"Ноты сохранены в {fileDialog.FileName}");
            }
        }

        /// <summary>
        /// This is called when we change the state of the loop checkbox
        /// </summary>
        private void LoopCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            Autoplayer.Loop = LoopCheckBox.Checked;
        }

        /// <summary>
        /// This is called when the text in StartKeyTextBox is changed
        /// </summary>
        private void StartKeyTextBox_TextChanged(object sender, EventArgs e)
        {
            //Using a try catch here in case the user inputs wrong key binds.
            KeysConverter keysConverter = new KeysConverter();
            try
            {
                //Remember to reset the hooked key to the new one.
                gkh.HookedKeys.Remove(startKey);
                startKey = (Keys)keysConverter.ConvertFromString(StartKeyTextBox.Text.ToString());
                if (startKey == stopKey)
                    MessageBox.Show("Эта клавиша уже привязана к другому действию!");
                gkh.HookedKeys.Add(startKey);
                PlayButton.Text = $"Играть ({StartKeyTextBox.Text.ToString()})";
            }
            catch (ArgumentException)
            {
                MessageBox.Show($"ОШИБКА: Неверная клавиша {StartKeyTextBox.Text.ToString()}");
            }
        }
        /// <summary>
        /// This is called when the text in StopKeyTextBox is changed
        /// </summary>
        private void StopKeyTextBox_TextChanged(object sender, EventArgs e)
        {
            //Using a try catch here in case the user inputs wrong key binds.
            KeysConverter keysConverter = new KeysConverter();
            try
            {
                //Remember to reset the hooked key to the new one.
                gkh.HookedKeys.Remove(stopKey);
                stopKey = (Keys)keysConverter.ConvertFromString(StopKeyTextBox.Text.ToString());
                if (stopKey == startKey)
                    MessageBox.Show("Эта клавиша уже привязана к другому действию!");
                gkh.HookedKeys.Add(stopKey);
                StopButton.Text = $"Стоп ({StopKeyTextBox.Text.ToString()})";
            }
            catch (ArgumentException)
            {
                MessageBox.Show($"ОШИБКА: Неверная клавиша {StopKeyTextBox.Text.ToString()}");
            }
        }

        /// <summary>
        /// This is called when the value of NormalDelayBox is changed
        /// </summary>
        private void GetSongBpmApiKeyBox_TextChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.GetSongBpmApiKey = GetSongBpmApiKeyBox.Text.Trim();
            Properties.Settings.Default.Save();
        }
        private void NormalDelayBox_ValueChanged(object sender, EventArgs e)
        {
            Autoplayer.DelayAtNormalSpeed = (int)NormalDelayBox.Value;

            //If this edit happened right after a song import applied a suggested delay (not
            //during the import itself, and not some unrelated edit with no suggestion to
            //compare against), treat it as the user correcting that specific suggestion, and
            //fold the correction into the speed multiplier - so the next import starts closer
            //to what was actually wanted, instead of needing the exact same manual correction
            //applied all over again on every single song.
            if (!isLoading && lastSuggestedNormalDelayMs.HasValue && lastSuggestedNormalDelayMs.Value > 0)
            {
                double observedTotalMultiplier = (double)SpeedMultiplierBox.Value
                    * ((double)NormalDelayBox.Value / lastSuggestedNormalDelayMs.Value);

                //Blended (50/50) with the existing multiplier rather than replacing it outright,
                //so one unusually fast or slow song doesn't overcorrect everything that follows -
                //it still takes real, visible effect within just a couple of corrections, not
                //dozens.
                decimal blended = Math.Round((SpeedMultiplierBox.Value + (decimal)observedTotalMultiplier) / 2m, 2);
                if (blended < SpeedMultiplierBox.Minimum) blended = SpeedMultiplierBox.Minimum;
                if (blended > SpeedMultiplierBox.Maximum) blended = SpeedMultiplierBox.Maximum;

                if (blended != SpeedMultiplierBox.Value)
                {
                    SpeedMultiplierBox.Value = blended;
                    SetPlaylistStatus($"Заметил вашу правку скорости — множитель скорости обновлён на {blended:0.00} для следующих песен.", false);
                }

                //This correction has now been "consumed" - an unrelated later edit shouldn't be
                //treated as a second correction of the same import.
                lastSuggestedNormalDelayMs = null;
            }
        }
        /// <summary>
        /// This is called when the value of FastDelayBox is changed
        /// </summary>
        private void FastDelayBox_ValueChanged(object sender, EventArgs e)
        {
            Autoplayer.DelayAtFastSpeed = (int)FastDelayBox.Value;
        }

        private void NoteTextBox_TextChanged(object sender, EventArgs e)
        {
            if(!isLoading)
            {
                MakeSong();
            }
        }

        /// <summary>
        /// This is called when we click the AuthorSiteButton. Opens the Discord server of the
        /// author who put together this fork/set of improvements, in the user's default browser.
        /// </summary>
        private void AuthorSiteButton_Click(object sender, EventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://discord.gg/MW2Z9TdrY") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось открыть ссылку: {ex.Message}");
            }
        }

        /// <summary>
        /// This is called when we click any of the three OpenXSiteButton buttons on the Notes tab
        /// </summary>
        private void OpenVpSiteButton_Click(object sender, EventArgs e)
        {
            OpenLink("https://virtualpiano.net/");
        }

        private void OpenForumSiteButton_Click(object sender, EventArgs e)
        {
            OpenLink("https://forums.pixeltailgames.com/t/piano-lessons-sharing-thread/1941");
        }

        private void OpenTrelloSiteButton_Click(object sender, EventArgs e)
        {
            OpenLink("https://trello.com/b/mpmwXlhA/a-certain-vp-trello");
        }

        private void OpenLink(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось открыть ссылку: {ex.Message}");
            }
        }

        #region Virtual Piano tab
        //Holds the most recent search results so the list box only needs to store the
        //display text while we keep the full result (including the URL) here.
        private List<VirtualPianoSearchResult> vpLastResults = new List<VirtualPianoSearchResult>();

        /// <summary>
        /// Shows a status message on the Virtual Piano tab, in red for errors.
        /// </summary>
        private void SetVpStatus(string text, bool isError)
        {
            VPStatusLabel.ForeColor = isError ? System.Drawing.Color.Red : System.Drawing.Color.Black;
            VPStatusLabel.Text = text;
        }

        /// <summary>
        /// This is called when we click the VPSearchButton
        /// </summary>
        private async void VPSearchButton_Click(object sender, EventArgs e)
        {
            string query = VPSearchTextBox.Text.Trim();
            if (query.Length == 0)
            {
                SetVpStatus("Введите название песни или исполнителя для поиска.", true);
                return;
            }

            VPSearchButton.Enabled = false;
            VPResultsListBox.Items.Clear();
            SetVpStatus("Идёт поиск на virtualpiano.net...", false);

            try
            {
                List<VirtualPianoSearchResult> results = await VirtualPianoImporter.SearchAsync(query);
                vpLastResults = results;

                if (results.Count == 0)
                {
                    SetVpStatus("Ничего не найдено. Можно вставить прямую ссылку на virtualpiano.net ниже.", true);
                }
                else
                {
                    foreach (VirtualPianoSearchResult result in results)
                    {
                        VPResultsListBox.Items.Add(result.ToString());
                    }
                    SetVpStatus($"Найдено результатов: {results.Count}. Выберите один и нажмите «Загрузить выбранную песню» (или дважды кликните по нему).", false);
                }
            }
            catch (Exception ex)
            {
                SetVpStatus($"Ошибка поиска: {ex.Message}", true);
            }
            finally
            {
                VPSearchButton.Enabled = true;
            }
        }

        /// <summary>
        /// This is called when the user presses a key while focused on VPSearchTextBox,
        /// so that pressing Enter triggers a search.
        /// </summary>
        private void VPSearchTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                VPSearchButton.PerformClick();
            }
        }

        /// <summary>
        /// This is called when we click the VPLoadSelectedButton
        /// </summary>
        private async void VPLoadSelectedButton_Click(object sender, EventArgs e)
        {
            int index = VPResultsListBox.SelectedIndex;
            if (index < 0 || index >= vpLastResults.Count)
            {
                SetVpStatus("Сначала выберите результат из списка.", true);
                return;
            }

            VPLoadUrlButton.Enabled = false;
            VPLoadSelectedButton.Enabled = false;
            try
            {
                await LoadVirtualPianoUrlAsync(vpLastResults[index].Url, VPAppendCheckBox.Checked, SetVpStatus);
            }
            catch (AutoplayerNoteCreationFailedException ex)
            {
                isLoading = false;
                SetVpStatus($"Преобразованные ноты были отклонены: {ex.Message}", true);
            }
            catch (Exception ex)
            {
                isLoading = false;
                SetVpStatus($"Не удалось загрузить песню: {ex.Message}", true);
            }
            finally
            {
                VPLoadUrlButton.Enabled = true;
                VPLoadSelectedButton.Enabled = true;
            }
        }

        /// <summary>
        /// This is called when we double-click an entry in VPResultsListBox
        /// </summary>
        private async void VPResultsListBox_DoubleClick(object sender, EventArgs e)
        {
            int index = VPResultsListBox.SelectedIndex;
            if (index < 0 || index >= vpLastResults.Count)
                return;

            VPLoadUrlButton.Enabled = false;
            VPLoadSelectedButton.Enabled = false;
            try
            {
                await LoadVirtualPianoUrlAsync(vpLastResults[index].Url, VPAppendCheckBox.Checked, SetVpStatus);
            }
            catch (AutoplayerNoteCreationFailedException ex)
            {
                isLoading = false;
                SetVpStatus($"Преобразованные ноты были отклонены: {ex.Message}", true);
            }
            catch (Exception ex)
            {
                isLoading = false;
                SetVpStatus($"Не удалось загрузить песню: {ex.Message}", true);
            }
            finally
            {
                VPLoadUrlButton.Enabled = true;
                VPLoadSelectedButton.Enabled = true;
            }
        }

        /// <summary>
        /// This is called when we click the VPImportMidiButton. Reads an actual Standard MIDI
        /// File's note/timing data (via MidiImporter, which uses the DryWetMidi library) rather
        /// than attempting to read notation from a picture of sheet music, which would need
        /// genuine optical music recognition this program has no reliable way to do.
        /// </summary>
        private void VPImportMidiButton_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Filter = "MIDI файлы (*.mid;*.midi)|*.mid;*.midi|Все файлы (*.*)|*.*";
                dialog.Title = "Выберите MIDI-файл";

                if (dialog.ShowDialog() != DialogResult.OK)
                    return;

                VPImportMidiButton.Enabled = false;
                try
                {
                    MidiImportResult midiResult = MidiImporter.ConvertMidiFile(
                        dialog.FileName, (double)SpeedMultiplierBox.Value, (int)MidiOctaveOffsetBox.Value);

                    VirtualPianoImportResult result = new VirtualPianoImportResult
                    {
                        Title = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName),
                        Artist = "",
                        ConvertedNotation = midiResult.ConvertedNotation,
                        ChordCount = midiResult.ChordCount,
                        FastGroupCount = 0,
                        PauseCount = 0,
                        SkippedCharacterCount = midiResult.SkippedOutOfRangeCount,
                        SpeedSuggestionApplied = true,
                        SuggestedNormalDelayMs = midiResult.SuggestedNormalDelayMs,
                        SuggestedFastDelayMs = midiResult.SuggestedFastDelayMs,
                        SuggestedBpm = midiResult.SuggestedBpm,
                        SuggestedDelaySource = "по темпу и плотности нот этого MIDI-файла"
                    };

                    ApplyImportResult(result, VPAppendCheckBox.Checked, SetVpStatus);

                    if (midiResult.SkippedOutOfRangeCount > 0)
                    {
                        SetVpStatus(
                            $"Загружено из MIDI: {midiResult.NoteCount} нот, {midiResult.ChordCount} аккордов. " +
                            $"Пропущено вне диапазона клавиш (36 белых/25 чёрных от C2): {midiResult.SkippedOutOfRangeCount} — " +
                            "если это много, попробуйте транспонировать MIDI-файл ближе к середине клавиатуры.",
                            true);
                    }
                }
                catch (AutoplayerNoteCreationFailedException ex)
                {
                    isLoading = false;
                    SetVpStatus($"Преобразованные ноты были отклонены: {ex.Message}", true);
                }
                catch (Exception ex)
                {
                    isLoading = false;
                    SetVpStatus($"Не удалось прочитать MIDI-файл: {ex.Message}", true);
                }
                finally
                {
                    VPImportMidiButton.Enabled = true;
                }
            }
        }

        /// <summary>
        /// This is called when we click the VPPasteLoadButton. Converts whatever notation text
        /// the user has pasted in (copied by hand from wherever they found it) using the
        /// selected difficulty tier for calibration, since there's no page here to scrape one
        /// from.
        /// </summary>
        private void VPPasteLoadButton_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(VPPasteTextBox.Text))
            {
                SetVpStatus("Сначала вставьте текст нот в поле слева.", true);
                return;
            }

            string difficulty = VPPasteDifficultyBox.SelectedItem as string;

            VPPasteLoadButton.Enabled = false;
            try
            {
                VirtualPianoImportResult result = VirtualPianoImporter.ImportFromPastedText(
                    VPPasteTextBox.Text, difficulty, (double)SpeedMultiplierBox.Value);
                ApplyImportResult(result, VPAppendCheckBox.Checked, SetVpStatus);
                VPPasteTextBox.Clear();
            }
            catch (AutoplayerNoteCreationFailedException ex)
            {
                isLoading = false;
                SetVpStatus($"Преобразованные ноты были отклонены: {ex.Message}", true);
            }
            catch (Exception ex)
            {
                isLoading = false;
                SetVpStatus($"Не удалось преобразовать текст: {ex.Message}", true);
            }
            finally
            {
                VPPasteLoadButton.Enabled = true;
            }
        }

        /// <summary>
        /// This is called when we click the VPLoadUrlButton
        /// </summary>
        private async void VPLoadUrlButton_Click(object sender, EventArgs e)
        {
            VPLoadUrlButton.Enabled = false;
            VPLoadSelectedButton.Enabled = false;
            try
            {
                await LoadVirtualPianoUrlAsync(VPUrlTextBox.Text, VPAppendCheckBox.Checked, SetVpStatus);
            }
            catch (AutoplayerNoteCreationFailedException ex)
            {
                isLoading = false;
                SetVpStatus($"Преобразованные ноты были отклонены: {ex.Message}", true);
            }
            catch (Exception ex)
            {
                isLoading = false;
                SetVpStatus($"Не удалось загрузить песню: {ex.Message}", true);
            }
            finally
            {
                VPLoadUrlButton.Enabled = true;
                VPLoadSelectedButton.Enabled = true;
            }
        }

        /// <summary>
        /// Downloads and converts the music sheet at the given virtualpiano.net URL, then applies
        /// the result via ApplyImportResult. Shared between the Virtual Piano tab and the Song
        /// Selection tab - each supplies its own append flag and status callback, and is
        /// responsible for managing its own buttons' enabled state and catching exceptions
        /// around this call (so each can phrase its own error message through its own status
        /// label, without this shared method needing to know which tab called it).
        /// </summary>
        private async Task LoadVirtualPianoUrlAsync(string url, bool append, Action<string, bool> setStatus)
        {
            setStatus("Загружаю ноты...", false);

            VirtualPianoImportResult result = await VirtualPianoImporter.ImportFromUrlAsync(url, (double)SpeedMultiplierBox.Value);

            ApplyImportResult(result, append, setStatus);
        }

        /// <summary>
        /// Inserts a converted song's notes into the song (replacing or appending, per the append
        /// parameter), updates the Main tab's note box, sets suggested playback speeds in the
        /// Settings tab, switches to the Main tab, and reports what happened both on the calling
        /// tab (via setStatus) and on the Main tab itself. Shared by every way a song can get
        /// into this program from outside (a virtualpiano.net URL, a Song Selection pick, or
        /// manually pasted notation) - each of those only needs to produce a
        /// VirtualPianoImportResult and hand it to this, rather than repeating this whole block.
        /// </summary>
        private void ApplyImportResult(VirtualPianoImportResult result, bool append, Action<string, bool> setStatus)
        {
            isLoading = true;
            if (!append)
            {
                Autoplayer.ClearAllNotes();
            }
            Autoplayer.AddNotesFromString(result.ConvertedNotation);
            UpdateNoteBox();
            //ImportFromUrlAsync/ImportFromPastedText register/update the short/long/extended
            //pause delay characters every time - refresh the Customize tab's delay list so the
            //current values are actually visible there.
            UpdateDelayListBox();

            //The normal/fast delay suggestion was already calculated - just apply and report it
            //here, rather than recomputing it. This only ever sets a starting point - it's the
            //same NormalDelayBox/FastDelayBox the user can retune freely afterwards, and doing
            //so is exactly what feeds the self-adjusting correction in NormalDelayBox_ValueChanged.
            if (result.SpeedSuggestionApplied)
            {
                NormalDelayBox.Value = result.SuggestedNormalDelayMs;
                FastDelayBox.Value = result.SuggestedFastDelayMs;
                lastSuggestedNormalDelayMs = result.SuggestedNormalDelayMs;
            }
            isLoading = false;

            string name = string.IsNullOrEmpty(result.Artist) ? result.Title : $"{result.Title} — {result.Artist}";
            string skippedNote = result.SkippedCharacterCount > 0
                ? $", пропущено неподдерживаемых символов: {result.SkippedCharacterCount}"
                : "";
            string speedNote = result.SpeedSuggestionApplied
                ? $" Скорость в «Настройках» подобрана {result.SuggestedDelaySource}: ~{result.SuggestedBpm} BPM ({result.SuggestedNormalDelayMs} мс / {result.SuggestedFastDelayMs} мс)."
                : " Не удалось подобрать скорость для этой песни — оставил текущие настройки.";
            setStatus(
                $"Загружено «{name}»: аккордов — {result.ChordCount}, быстрых групп — {result.FastGroupCount}, " +
                $"пауз — {result.PauseCount}{skippedNote}. Вставлено во вкладку «Главная».{speedNote}",
                false);

            tabControl1.SelectedTab = MainTabPage;

            //So "Save to Playlist" can name the file after this song instead of only ever using
            //a timestamp.
            lastLoadedSongTitle = string.IsNullOrEmpty(result.Artist) ? result.Title : $"{result.Title} - {result.Artist}";

            //The message above is shown on the tab the user is leaving, right before switching
            //away from it - easy to miss. Repeat a short version of it here on the Main tab
            //itself (PlaylistStatusLabel), where the user actually lands.
            if (result.SpeedSuggestionApplied)
            {
                SetPlaylistStatus($"Скорость подобрана {result.SuggestedDelaySource}: ~{result.SuggestedBpm} BPM.", false);
            }
            else
            {
                SetPlaylistStatus("Не удалось определить сложность песни — скорость не менялась.", false);
            }
        }
        #endregion

        #region Genre (Song Selection) tab

        //Holds every song fetched for the currently loaded genre, so switching the artist
        //filter is just a client-side re-filter rather than a new network request.
        private List<VirtualPianoCategorySong> genreSongs = new List<VirtualPianoCategorySong>();
        private const string AllArtistsEntry = "— Все исполнители —";

        /// <summary>
        /// Shows a status message on the Song Selection tab, in red for errors.
        /// </summary>
        private void SetGenreStatus(string text, bool isError)
        {
            GenreStatusLabel.ForeColor = isError ? System.Drawing.Color.Red : System.Drawing.Color.Black;
            GenreStatusLabel.Text = text;
        }

        /// <summary>
        /// This is called when we click the LoadGenreButton. Fetches the selected genre's songs
        /// (across a few pages) from virtualpiano.net, then fills in the artist and song lists.
        /// </summary>
        private async void LoadGenreButton_Click(object sender, EventArgs e)
        {
            VirtualPianoGenre genre = GenreComboBox.SelectedItem as VirtualPianoGenre;
            if (genre == null)
            {
                SetGenreStatus("Сначала выберите жанр.", true);
                return;
            }

            LoadGenreButton.Enabled = false;
            ArtistListBox.Items.Clear();
            GenreSongListBox.Items.Clear();
            genreSongs = new List<VirtualPianoCategorySong>();
            SetGenreStatus($"Загружаю список песен для «{genre.DisplayName}»...", false);

            try
            {
                genreSongs = await VirtualPianoImporter.GetSongsInGenreAsync(genre.UrlPath);

                if (genreSongs.Count == 0)
                {
                    SetGenreStatus(
                        "Не удалось получить список песен для этого жанра. Разметка страницы могла " +
                        "измениться - можно попробовать вкладку Virtual Piano и вставить ссылку напрямую.",
                        true);
                    return;
                }

                ArtistListBox.Items.Add(AllArtistsEntry);
                foreach (string artist in VirtualPianoImporter.GetDistinctArtists(genreSongs))
                {
                    ArtistListBox.Items.Add(artist);
                }
                ArtistListBox.SelectedIndex = 0;

                SetGenreStatus(
                    $"Найдено песен: {genreSongs.Count}. Выберите исполнителя слева, чтобы отфильтровать список, " +
                    "или сразу выберите песню справа (двойной клик или кнопка снизу).",
                    false);
            }
            catch (Exception ex)
            {
                SetGenreStatus($"Ошибка загрузки списка: {ex.Message}", true);
            }
            finally
            {
                LoadGenreButton.Enabled = true;
            }
        }

        /// <summary>
        /// This is called when the selected artist changes - re-filters GenreSongListBox from
        /// the already-fetched genreSongs, without any new network request.
        /// </summary>
        private void ArtistListBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            string selectedArtist = ArtistListBox.SelectedItem as string;
            GenreSongListBox.Items.Clear();

            IEnumerable<VirtualPianoCategorySong> songsToShow;
            if (selectedArtist == null || selectedArtist == AllArtistsEntry)
            {
                songsToShow = genreSongs;
            }
            else
            {
                songsToShow = genreSongs.Where(s =>
                    string.Equals(string.IsNullOrWhiteSpace(s.Artist) ? "(автор не указан)" : s.Artist.Trim(),
                        selectedArtist, StringComparison.OrdinalIgnoreCase));
            }

            foreach (VirtualPianoCategorySong song in songsToShow)
            {
                GenreSongListBox.Items.Add(song);
            }
        }

        /// <summary>
        /// This is called when we click the LoadGenreSongButton
        /// </summary>
        private async void LoadGenreSongButton_Click(object sender, EventArgs e)
        {
            await LoadSelectedGenreSongAsync();
        }

        /// <summary>
        /// This is called when we double-click an entry in GenreSongListBox
        /// </summary>
        private async void GenreSongListBox_DoubleClick(object sender, EventArgs e)
        {
            await LoadSelectedGenreSongAsync();
        }

        private async Task LoadSelectedGenreSongAsync()
        {
            VirtualPianoCategorySong song = GenreSongListBox.SelectedItem as VirtualPianoCategorySong;
            if (song == null)
            {
                SetGenreStatus("Сначала выберите песню из списка.", true);
                return;
            }

            LoadGenreButton.Enabled = false;
            LoadGenreSongButton.Enabled = false;
            try
            {
                await LoadVirtualPianoUrlAsync(song.Url, GenreAppendCheckBox.Checked, SetGenreStatus);
            }
            catch (AutoplayerNoteCreationFailedException ex)
            {
                isLoading = false;
                SetGenreStatus($"Преобразованные ноты были отклонены: {ex.Message}", true);
            }
            catch (Exception ex)
            {
                isLoading = false;
                SetGenreStatus($"Не удалось загрузить песню: {ex.Message}", true);
            }
            finally
            {
                LoadGenreButton.Enabled = true;
                LoadGenreSongButton.Enabled = true;
            }
        }

        #endregion

        #region Segment playback and playlist

        /// <summary>
        /// This is called when we click the PlaySelectionButton. Temporarily swaps in just the
        /// highlighted text as the song to play (so a specific excerpt can be practiced/tested
        /// without deleting everything around it), then restores the full song from
        /// NoteTextBox.Text - which is never itself modified by this - once done.
        /// </summary>
        private async void PlaySelectionButton_Click(object sender, EventArgs e)
        {
            if (IsAnythingPlaying())
            {
                return;
            }

            string selectedText = NoteTextBox.SelectedText;
            if (string.IsNullOrEmpty(selectedText))
            {
                MessageBox.Show("Сначала выделите часть нот, которую нужно проиграть.");
                return;
            }

            int selectionStart = NoteTextBox.SelectionStart;

            isSelectionPlaying = true;
            DisableClearButton();
            DisablePlayButton();
            NoteTextBox.ReadOnly = true;

            try
            {
                Autoplayer.ClearAllNotes();
                Autoplayer.AddNotesFromString(selectedText);
                //AddNotesFromString only knows about the substring it was given, so its
                //character-range tracking is 0-based against that substring - offset it to
                //match where the selection actually sits in the full text, so the red "now
                //playing" highlight points at the right place instead of the start of the box.
                for (int idx = 0; idx < Autoplayer.SongEntryStart.Count; idx++)
                {
                    Autoplayer.SongEntryStart[idx] += selectionStart;
                }

                await Task.Run(() => Autoplayer.PlaySong());
            }
            catch (AutoplayerNoteCreationFailedException ex)
            {
                ErrorLabel.Text = $"ОШИБКА: {ex.Message}";
                ErrorLabel.Show();
            }
            finally
            {
                //NoteTextBox.Text itself was never touched, so re-parsing it exactly reproduces
                //the full Song that was in place before the selection was swapped in.
                isLoading = true;
                Autoplayer.ClearAllNotes();
                Autoplayer.AddNotesFromString(NoteTextBox.Text);
                isLoading = false;
                NoteTextBox.ReadOnly = false;
                isSelectionPlaying = false;
                EnablePlayButton();
                EnableClearButton();
            }
        }

        /// <summary>
        /// This is called when we click the SaveToPlaylistButton. Appends the current song (with
        /// its own current delay settings) as a new entry in the playlist folder, reusing the
        /// exact same file format Autoplayer.SaveSong/LoadSong already use for single songs - so
        /// loading a playlist entry back later is just an ordinary LoadSong call.
        /// </summary>
        private void SaveToPlaylistButton_Click(object sender, EventArgs e)
        {
            try
            {
                System.IO.Directory.CreateDirectory(PlaylistFolder);
                string fileName = BuildPlaylistFileName();
                string fullPath = System.IO.Path.Combine(PlaylistFolder, fileName);
                Autoplayer.SaveSong(fullPath);
                RefreshPlaylistSongsList();
                SetPlaylistStatus($"Добавлено в плейлист: {fileName}. Всего песен в плейлисте: {GetPlaylistFiles().Length}.", false);
            }
            catch (Exception ex)
            {
                SetPlaylistStatus($"Не удалось сохранить в плейлист: {ex.Message}", true);
            }
        }

        /// <summary>
        /// Names a new playlist entry after the currently loaded song's title when one is known
        /// (from a Virtual Piano import or a regular loaded file), falling back to a timestamp
        /// for hand-typed notes that were never associated with a named song. Either way, if the
        /// resulting name is already taken in the playlist folder, a " (2)", " (3)" etc. suffix
        /// is added rather than silently overwriting an earlier save of the same song.
        /// </summary>
        private string BuildPlaylistFileName()
        {
            string baseName = string.IsNullOrWhiteSpace(lastLoadedSongTitle)
                ? $"song_{DateTime.Now:yyyyMMdd_HHmmss_fff}"
                : SanitizeFileName(lastLoadedSongTitle);

            if (string.IsNullOrEmpty(baseName))
            {
                baseName = $"song_{DateTime.Now:yyyyMMdd_HHmmss_fff}";
            }

            string candidate = baseName + ".txt";
            int suffix = 2;
            while (System.IO.File.Exists(System.IO.Path.Combine(PlaylistFolder, candidate)))
            {
                candidate = $"{baseName} ({suffix}).txt";
                suffix++;
            }
            return candidate;
        }

        /// <summary>
        /// Replaces characters that aren't valid in a Windows filename (and trims the result)
        /// so a song title can be used directly as one.
        /// </summary>
        private string SanitizeFileName(string name)
        {
            char[] invalidChars = System.IO.Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                sb.Append(Array.IndexOf(invalidChars, c) >= 0 ? ' ' : c);
            }
            //Collapse any runs of spaces left behind by replaced characters.
            string cleaned = Regex.Replace(sb.ToString(), "\\s{2,}", " ").Trim();
            //Windows filenames have a practical length limit and a very long song/artist name
            //could otherwise produce an unwieldy file - keep it reasonable.
            const int maxLength = 80;
            if (cleaned.Length > maxLength)
            {
                cleaned = cleaned.Substring(0, maxLength).Trim();
            }
            return cleaned;
        }

        /// <summary>
        /// This is called when we click the PlayPlaylistButton. Starts RunPlaylist on its own
        /// background thread so the UI stays responsive; RunPlaylist itself calls
        /// Autoplayer.PlaySong synchronously for each entry in turn.
        /// </summary>
        private void PlayPlaylistButton_Click(object sender, EventArgs e)
        {
            if (IsAnythingPlaying())
            {
                return;
            }

            string[] files = GetPlaylistFiles();
            if (files.Length == 0)
            {
                SetPlaylistStatus(
                    $"Плейлист пуст. Сначала сохраните хотя бы одну песню кнопкой «В плейлист» (папка: {PlaylistFolder}).",
                    true);
                return;
            }

            isPlaylistPlaying = true;
            DisableClearButton();
            NoteTextBox.ReadOnly = true;
            //PlayButton is deliberately left enabled here - while the playlist is playing, F2/
            //Play is repurposed by PlayButton_Click to mean "skip to next track" instead of
            //"start a new playback".

            Thread playlistThread = new Thread(() => RunPlaylist(files));
            playlistThread.Start();
        }

        /// <summary>
        /// Plays each file in the playlist in turn, on its own background thread: loads that
        /// entry's notes and its own saved delay settings (via the ordinary LoadSong, so no
        /// separate playlist file format is needed), reflects it in the UI, plays it, then waits
        /// ~5 seconds (checked in small steps so Stop/skip take effect promptly) before moving
        /// on to the next one.
        /// </summary>
        private void RunPlaylist(string[] files)
        {
            try
            {
                for (int i = 0; i < files.Length && isPlaylistPlaying; i++)
                {
                    playlistSkipRequested = false;

                    try
                    {
                        Autoplayer.ResetDelays();
                        Autoplayer.CustomNotes.Clear();
                        Autoplayer.LoadSong(files[i]);
                    }
                    catch (Exception)
                    {
                        //Skip a corrupted/unreadable entry rather than aborting the whole playlist.
                        continue;
                    }

                    string fileName = System.IO.Path.GetFileNameWithoutExtension(files[i]);
                    int songNumber = i + 1;
                    int totalSongs = files.Length;
                    Invoke((MethodInvoker)delegate ()
                    {
                        isLoading = true;
                        UpdateNoteBox();
                        isLoading = false;
                        UpdateDelayListBox();
                        NormalDelayBox.Value = ClampToNumericRange(Autoplayer.DelayAtNormalSpeed, NormalDelayBox);
                        FastDelayBox.Value = ClampToNumericRange(Autoplayer.DelayAtFastSpeed, FastDelayBox);
                        SetPlaylistStatus($"Играю {songNumber} из {totalSongs}: {fileName}", false);
                    });

                    Autoplayer.PlaySong();

                    if (!isPlaylistPlaying)
                        break;

                    if (i < files.Length - 1)
                    {
                        for (int waited = 0; waited < 5000 && isPlaylistPlaying && !playlistSkipRequested; waited += 100)
                        {
                            Thread.Sleep(100);
                        }
                    }
                }
            }
            finally
            {
                isPlaylistPlaying = false;
                Invoke((MethodInvoker)delegate ()
                {
                    NoteTextBox.ReadOnly = false;
                    EnableClearButton();
                    SetPlaylistStatus("Плейлист остановлен.", false);
                });
            }
        }

        /// <summary>
        /// Every playlist entry file, sorted (their timestamp-based filenames sort chronologically,
        /// i.e. in save order).
        /// </summary>
        private string[] GetPlaylistFiles()
        {
            if (!System.IO.Directory.Exists(PlaylistFolder))
                return new string[0];

            string[] files = System.IO.Directory.GetFiles(PlaylistFolder, "*.txt");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            return files;
        }

        /// <summary>
        /// Repopulates PlaylistSongsListBox from the playlist folder's current contents, showing
        /// each entry's title (the filename without its .txt extension, which is the song's
        /// actual title whenever it was saved from a Virtual Piano import or a named file -
        /// see BuildPlaylistFileName). Called at startup and after a successful save, so newly
        /// added entries show up without needing to restart the program.
        /// </summary>
        private void RefreshPlaylistSongsList()
        {
            PlaylistSongsListBox.Items.Clear();
            foreach (string file in GetPlaylistFiles())
            {
                PlaylistSongsListBox.Items.Add(System.IO.Path.GetFileNameWithoutExtension(file));
            }
        }

        /// <summary>
        /// This is called when we double-click an entry in PlaylistSongsListBox - loads that
        /// specific playlist entry (its notes and its own saved delay settings, exactly as
        /// LoadButton_Click does for any other saved file) into the Main tab, replacing whatever
        /// is currently there.
        /// </summary>
        private void PlaylistSongsListBox_DoubleClick(object sender, EventArgs e)
        {
            string selectedName = PlaylistSongsListBox.SelectedItem as string;
            if (selectedName == null)
                return;

            if (IsAnythingPlaying())
            {
                SetPlaylistStatus("Сначала остановите текущее воспроизведение.", true);
                return;
            }

            string fullPath = System.IO.Path.Combine(PlaylistFolder, selectedName + ".txt");
            try
            {
                isLoading = true;
                Autoplayer.ResetDelays();
                Autoplayer.CustomNotes.Clear();
                Autoplayer.LoadSong(fullPath);
                UpdateEverything();
                lastLoadedSongTitle = selectedName;
                SetPlaylistStatus($"Загружено из плейлиста: {selectedName}.", false);
            }
            catch (AutoplayerLoadFailedException ex)
            {
                isLoading = false;
                SetPlaylistStatus($"Не удалось загрузить «{selectedName}»: {ex.Message}", true);
            }
        }

        /// <summary>
        /// Keeps a value saved in a playlist entry from throwing if it's outside a NumericUpDown's
        /// configured Minimum/Maximum (this can't normally happen from the Settings tab itself,
        /// but a hand-edited or older save file could contain anything).
        /// </summary>
        private decimal ClampToNumericRange(int value, NumericUpDown box)
        {
            if (value < box.Minimum) return box.Minimum;
            if (value > box.Maximum) return box.Maximum;
            return value;
        }

        /// <summary>
        /// Shows a status message about the playlist on the Main tab, in red for errors.
        /// </summary>
        private void SetPlaylistStatus(string text, bool isError)
        {
            PlaylistStatusLabel.ForeColor = isError ? System.Drawing.Color.Red : System.Drawing.Color.Black;
            PlaylistStatusLabel.Text = text;
        }

        #endregion
    }
}
