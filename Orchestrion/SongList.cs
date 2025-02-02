﻿using ImGuiNET;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Text;
using Dalamud.Logging;

namespace Orchestrion
{
    struct Song
    {
        public int Id;
        public string Name;
        public string Locations;
        public string AdditionalInfo;
    }
    
    public struct SongReplacement
    {
        /// <summary>
        /// The ID of the song to replace.
        /// </summary>
        public int TargetSongId;
        
        /// <summary>
        /// The ID of the replacement track to play. -1 means "ignore this song" and continue playing
        /// what was previously playing.
        /// </summary>
        public int ReplacementId;
    }

    public struct SongHistoryEntry
    {
        public int Id;
        public DateTime TimePlayed;
    }

    class SongList : IDisposable
    {
        private static float Scale => ImGui.GetIO().FontGlobalScale;
        
        private const string SheetPath = @"https://docs.google.com/spreadsheets/d/1gGNCu85sjd-4CDgqw-K5tefTe4HYuDK38LkRyvx_fEc/gviz/tq?tqx=out:csv&sheet=main";
        private Dictionary<int, Song> songs = new Dictionary<int, Song>();
        private Configuration configuration;
        private IPlaybackController controller;
        private IResourceLoader loader;
        private int selectedSong;
        private int selectedHistoryEntry;
        private string searchText = string.Empty;
        private string songListFile = string.Empty;
        private ImGuiScene.TextureWrap favoriteIcon = null;
        private ImGuiScene.TextureWrap settingsIcon = null;
        private bool showDebugOptions = false;

        private List<SongHistoryEntry> SongHistory = new List<SongHistoryEntry>();
        
        private SongReplacement tmpReplacement;
        private List<int> removalList = new();
        private const string NoChange = "Do not change BGM"; 

        private bool visible = false;
        public bool Visible
        {
            get { return visible; }
            set { visible = value; }
        }

        private bool settingsVisible = false;
        public bool SettingsVisible
        {
            get { return settingsVisible; }
            set { settingsVisible = value; }
        }

        public bool AllowDebug { get; set; } = false;

        public SongList(string songListFile, Configuration configuration, IPlaybackController controller, IResourceLoader loader)
        {
            this.songListFile = songListFile;
            this.configuration = configuration;
            this.controller = controller;
            this.loader = loader;

            UpdateSheet();
            ResetReplacement();
        }

        public void Dispose()
        {
            Stop();
            songs = null;
            favoriteIcon?.Dispose();
            settingsIcon?.Dispose();
        }
        
        // Attempts to load supplemental bgm data from the csv file
        // This throws all internal errors
        private void LoadSheet(string sheetText)
        {
            songs = new Dictionary<int, Song>();

            var sheetLines = sheetText.Split('\n'); // gdocs provides \n
            for (int i = 1; i < sheetLines.Length; i++)
            {
                // The formatting is odd here because gdocs adds quotes around columns and doubles each single quote
                var elements = sheetLines[i].Split(new[] {"\","}, StringSplitOptions.None);
                var id = int.Parse(elements[0].Substring(1));
                var name = elements[1].Substring(1).Replace("\"\"", "\"");;

                // Any track without an official name is "???"
                // While Null BGM tracks and None are also pretty invalid
                if (string.IsNullOrEmpty(name) || name == "Null BGM") continue;
                    
                var location = elements[2].Substring(1).Replace("\"\"", "\"");
                var additionalInfo = elements[3].Substring(1, elements[3].Substring(1).Length - 1).Replace("\"\"", "\"");
                    
                var song = new Song
                {
                    Id = id,
                    Name = name,
                    Locations = location,
                    AdditionalInfo = additionalInfo
                };
                    
                songs[id] = song;
            }
        }

        private void UpdateSheet()
        {
            var existingText = File.ReadAllText(songListFile);

            using var client = new WebClient();
            try
            {
                PluginLog.Log("Checking for updated bgm sheet");
                var newText = client.DownloadString(SheetPath);
                LoadSheet(newText);

                // would really prefer some kind of proper versioning here
                if (newText != existingText)
                {
                    File.WriteAllText(songListFile, newText);
                    PluginLog.Log("Updated bgm sheet to new version");
                }
            }
            catch (Exception e)
            {
                PluginLog.Error(e, "Orchestrion failed to update bgm sheet; using previous version");
                // if this throws, something went horribly wrong and we should just break completely
                LoadSheet(existingText);
            }
        }

        private void ResetReplacement()
        {
            var id = songs.Keys.First(x => !configuration.SongReplacements.ContainsKey(x));
            tmpReplacement = new SongReplacement
            {
                TargetSongId = id,
                ReplacementId = -1,
            };
        }

        private void Play(int songId)
        {
            controller.PlaySong(songId);
        }

        private void Stop()
        {
            controller.StopSong();
        }

        private bool IsFavorite(int songId) => configuration.FavoriteSongs.Contains(songId);

        private void AddFavorite(int songId)
        {
            configuration.FavoriteSongs.Add(songId);
            configuration.Save();
        }

        private void RemoveFavorite(int songId)
        {
            configuration.FavoriteSongs.Remove(songId);
            configuration.Save();
        }

        public string GetSongTitle(int id)
        {
            try
            {
                return songs.ContainsKey(id) ? songs[id].Name : "";
            }
            catch (Exception e)
            {
                PluginLog.Error(e, "GetSongTitle");
            }

            return "";
        }

        public void AddSongToHistory(int id)
        {
            // Don't add silence
            if (id == 1)
                return;

            SongHistoryEntry newEntry = new SongHistoryEntry();
            newEntry.Id = id;
            newEntry.TimePlayed = DateTime.Now;
            int CurrentIndex = SongHistory.Count - 1;
            // Check if we have history, if yes, then check if ID is the same as previous, if not, add to history
            if (CurrentIndex < 0 ||
                (CurrentIndex >= 0 && SongHistory[CurrentIndex].Id != id))
            {
                SongHistory.Add(newEntry);

                PluginLog.Debug($"Added {id} to history. There are now {CurrentIndex+1} songs in history.");
            }
        }

        public void Draw()
        {
            DrawMainWindow();
            DrawSettings();
        }

        private void DrawMainWindow()
        {
            // temporary bugfix for a race condition where it was possible that
            // we would attempt to load the icon before the ImGuiScene was created in dalamud
            // which would fail and lead to this icon being null
            // Hopefully later the UIBuilder API can add an event to notify when it is ready
            if (favoriteIcon == null)
            {
                favoriteIcon = loader.LoadUIImage("favoriteIcon.png");
                settingsIcon = loader.LoadUIImage("settings.png");
            }

            if (!Visible) return;
            if (songs == null) return;
            
            var windowTitle = new StringBuilder("Orchestrion");
            if (configuration.ShowSongInTitleBar)
            {
                // TODO: subscribe to the event so this only has to be constructed on change?
                var song = controller.CurrentSong;
                if (songs.ContainsKey(song))
                    windowTitle.Append($" - [{songs[song].Id}] {songs[song].Name}");
            }
            windowTitle.Append("###Orchestrion");

            ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, ScaledVector2(370, 150));
            ImGui.SetNextWindowSize(ScaledVector2(370, 440), ImGuiCond.FirstUseEver);
            // these flags prevent the entire window from getting a secondary scrollbar sometimes, and also keep it from randomly moving slightly with the scrollwheel
            if (ImGui.Begin(windowTitle.ToString(), ref visible, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                ImGui.AlignTextToFramePadding();
                ImGui.Text("Search: ");
                ImGui.SameLine();
                ImGui.InputText("##searchbox", ref searchText, 32);

                ImGui.SameLine();
                ImGui.SetCursorPosX(ImGui.GetWindowSize().X - 32);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 1);
                if (ImGui.ImageButton(settingsIcon.ImGuiHandle, new Vector2(16, 16)))
                    settingsVisible = true;

                ImGui.Separator();

                if (ImGui.BeginTabBar("##songlist tabs"))
                {
                    if (ImGui.BeginTabItem("All songs"))
                    {
                        DrawSonglist(false);
                        ImGui.EndTabItem();
                    }
                    if (ImGui.BeginTabItem("Favorites"))
                    {
                        DrawSonglist(true);
                        ImGui.EndTabItem();
                    }
                    if (ImGui.BeginTabItem("History"))
                    {
                        DrawSongHistory();
                        ImGui.EndTabItem();
                    }
                    if (ImGui.BeginTabItem("Replacements"))
                    {
                        DrawReplacements();
                        ImGui.EndTabItem();
                    }
                    ImGui.EndTabBar();
                }
            }
            ImGui.End();

            ImGui.PopStyleVar();
        }

        private void DrawFooter(bool isHistory = false)
        {
            ImGui.Separator();
            ImGui.Columns(2, "footer columns", false);
            ImGui.SetColumnWidth(-1, ImGui.GetWindowSize().X - 100);
            if (isHistory)
            {
                ImGui.TextWrapped(selectedHistoryEntry > 0 ? songs[SongHistory[selectedHistoryEntry].Id].Locations : string.Empty);
                ImGui.TextWrapped(selectedHistoryEntry > 0 ? songs[SongHistory[selectedHistoryEntry].Id].AdditionalInfo : string.Empty);
            }
            else
            {
                ImGui.TextWrapped(selectedSong > 0 ? songs[selectedSong].Locations : string.Empty);
                ImGui.TextWrapped(selectedSong > 0 ? songs[selectedSong].AdditionalInfo : string.Empty);
            }
            ImGui.NextColumn();
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetWindowSize().X - 100);
            ImGui.SetCursorPosY(ImGui.GetWindowSize().Y - 30);
            if (ImGui.Button("Stop"))
                Stop();
            ImGui.SameLine();
            if (ImGui.Button("Play"))
                Play(selectedSong);
            ImGui.Columns(1);
        }
        
        private void DrawReplacements()
        {
            ImGui.BeginChild("##replacementlist");
            DrawCurrentReplacement();
            DrawReplacementList();
            ImGui.EndChild();
        }

        private void DrawReplacementList()
        {
            foreach (var replacement in configuration.SongReplacements.Values)
            {
                ImGui.Spacing();
                var target = songs[replacement.TargetSongId];
                
                var targetText = $"{replacement.TargetSongId} - {target.Name}";
                string replText;
                if (replacement.ReplacementId == -1)
                    replText = NoChange;
                else
                    replText = $"{replacement.ReplacementId} - {songs[replacement.ReplacementId].Name}";

                ImGui.TextWrapped($"{targetText}");
                if (ImGui.IsItemHovered())
                    DrawBgmTooltip(target);

                ImGui.Text($"will be replaced with");
                ImGui.TextWrapped($"{replText}");
                if (ImGui.IsItemHovered() && replacement.ReplacementId != -1)
                    DrawBgmTooltip(songs[replacement.ReplacementId]);
                
                // Delete button in top right of area
                RightAlignButton(ImGui.GetCursorPosY(), "Delete");
                if (ImGui.Button($"Delete##{replacement.TargetSongId}"))
                    removalList.Add(replacement.TargetSongId);
                
                ImGui.Separator();
            }

            if (removalList.Count > 0)
            {
                foreach (var toRemove in removalList)
                    configuration.SongReplacements.Remove(toRemove);
                removalList.Clear();    
                configuration.Save();
            }
        }

        private void RightAlignButton(float y, string text)
        {
            var style = ImGui.GetStyle();
            var padding = style.WindowPadding.X + style.FramePadding.X * 2 + style.ScrollbarSize;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetWindowWidth() - ImGui.CalcTextSize(text).X - padding);
            ImGui.SetCursorPosY(y);
        }

        private void DrawCurrentReplacement()
        {
            ImGui.Spacing();
            
            var targetText = $"{songs[tmpReplacement.TargetSongId].Id} - {songs[tmpReplacement.TargetSongId].Name}";
            string replacementText;
            if (tmpReplacement.ReplacementId == -1)
                replacementText = NoChange;
            else
                replacementText = $"{songs[tmpReplacement.ReplacementId].Id} - {songs[tmpReplacement.ReplacementId].Name}";
            
            // This fixes the ultra-wide combo boxes, I guess
            var width = ImGui.GetWindowWidth() * 0.60f;
            
            if (ImGui.BeginCombo("Target Song", targetText))
            {
                foreach (var song in songs.Values)
                {
                    if (!SearchMatches(song)) continue;
                    if (configuration.SongReplacements.ContainsKey(song.Id)) continue;
                    var tmpText = $"{song.Id} - {song.Name}";
                    var tmpTextSize = ImGui.CalcTextSize(tmpText);
                    var isSelected = tmpReplacement.TargetSongId == song.Id;
                    if (ImGui.Selectable(tmpText, isSelected, ImGuiSelectableFlags.None, new Vector2(width, tmpTextSize.Y)))
                        tmpReplacement.TargetSongId = song.Id;
                    if (ImGui.IsItemHovered())
                        DrawBgmTooltip(song);
                }
                ImGui.EndCombo();
            }
            
            ImGui.Spacing();
            
            if (ImGui.BeginCombo("Replacement Song", replacementText))
            {
                var tmpText = NoChange;
                var tmpTextSize = ImGui.CalcTextSize(tmpText);
                var isSelected = tmpReplacement.TargetSongId == -1;
                if (ImGui.Selectable(NoChange))
                    tmpReplacement.ReplacementId = -1;

                foreach (var song in songs.Values)
                {
                    if (!SearchMatches(song)) continue;
                    tmpText = $"{song.Id} - {song.Name}";
                    tmpTextSize = ImGui.CalcTextSize(tmpText);
                    isSelected = tmpReplacement.ReplacementId == song.Id;
                    if (ImGui.Selectable(tmpText, isSelected, ImGuiSelectableFlags.None, new Vector2(width, tmpTextSize.Y)))
                        tmpReplacement.ReplacementId = song.Id;
                    if (ImGui.IsItemHovered())
                        DrawBgmTooltip(song);
                }
                ImGui.EndCombo();
            }
            
            ImGui.Spacing();
            ImGui.Spacing();
            
            RightAlignButton(ImGui.GetCursorPosY(), "Add as song replacement");
            if (ImGui.Button("Add as song replacement"))
            {
                configuration.SongReplacements.Add(tmpReplacement.TargetSongId, tmpReplacement);
                configuration.Save();
                ResetReplacement();
            }

            ImGui.Separator();
        }

        private bool SearchMatches(Song song)
        {
            var matchesSearch = searchText.Length != 0
                   && (song.Name.ToLower().Contains(searchText.ToLower())
                    || song.Locations.ToLower().Contains(searchText.ToLower())
                    || song.AdditionalInfo.ToLower().Contains(searchText.ToLower())
                    || song.Id.ToString().Contains(searchText));
            var searchEmpty = searchText.Length == 0;
            return matchesSearch || searchEmpty;
        }

        private void DrawSonglist(bool favoritesOnly)
        {
            // to keep the tab bar always visible and not have it get scrolled out
            ImGui.BeginChild("##songlist_internal", new Vector2(-1f, -60f));

            ImGuiTableFlags flags = ImGuiTableFlags.SizingFixedFit;

            if (ImGui.BeginTable("songlist table", 4, flags))
            {
                ImGui.TableSetupColumn("fav", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("id", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("title", ImGuiTableColumnFlags.WidthStretch);

                //ImGui.Columns(2, "songlist columns", false);

                //ImGui.SetColumnWidth(-1, 13);
                //ImGui.SetColumnOffset(1, 12);

                foreach (var s in songs)
                {
                    var song = s.Value;
                    if (!SearchMatches(song))
                        continue;

                    bool isFavorite = IsFavorite(song.Id);

                    if (favoritesOnly && !isFavorite)
                        continue;

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();

                    //ImGui.SetCursorPosX(-1);

                    if (isFavorite)
                    {
                        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 3);
                        ImGui.Image(favoriteIcon.ImGuiHandle, new Vector2(13, 13));
                    }

                    ImGui.TableNextColumn();

                    ImGui.Text(song.Id.ToString());

                    ImGui.TableNextColumn();

                    if (ImGui.Selectable($"{song.Name}##{song.Id}", selectedSong == song.Id, ImGuiSelectableFlags.AllowDoubleClick))
                    {
                        selectedSong = song.Id;
                        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                            Play(selectedSong);
                    }
                    if (ImGui.BeginPopupContextItem())
                    {
                        if (!isFavorite)
                        {
                            if (ImGui.Selectable("Add to favorites"))
                                AddFavorite(song.Id);
                        }
                        else
                        {
                            if (ImGui.Selectable("Remove from favorites"))
                                RemoveFavorite(song.Id);
                        }
                        ImGui.EndPopup();
                    }
                    ImGui.NextColumn();
                }

                ImGui.EndTable();
            }
            ImGui.EndChild();
            DrawFooter();
        }

        private void DrawSongHistory()
        {
            // to keep the tab bar always visible and not have it get scrolled out
            ImGui.BeginChild("##songlist_internal", new Vector2(-1f, -60f));

            ImGuiTableFlags flags = ImGuiTableFlags.SizingFixedFit; //ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersV | ImGuiTableFlags.ContextMenuInBody;

            if (ImGui.BeginTable("songlist table", 4, flags))
            {
                ImGui.TableSetupColumn("fav", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("id", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("title", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("time", ImGuiTableColumnFlags.WidthFixed);
                //ImGui.Columns(2, "songlist columns", false);

                //ImGui.SetColumnWidth(-1, 13);
                //ImGui.SetColumnOffset(1, 12);

                //ImGui.SetColumnWidth(2, 90);

                DateTime now = DateTime.Now;

                // going from the end of the list
                for (int i = SongHistory.Count - 1; i >= 0; i--)
                {
                    var songHistoryEntry = SongHistory[i];
                    var song = songs[songHistoryEntry.Id];

                    if (!SearchMatches(song))
                        continue;

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();

                    bool isFavorite = IsFavorite(song.Id);
                    //ImGui.SetCursorPosX(-1);

                    if (isFavorite)
                    {
                        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 3);
                        ImGui.Image(favoriteIcon.ImGuiHandle, new Vector2(13, 13));
                        //ImGui.SameLine();
                    }
                    ImGui.TableNextColumn();

                    ImGui.Text(song.Id.ToString());

                    ImGui.TableNextColumn();
                    if (ImGui.Selectable($"{song.Name}##{i}", selectedHistoryEntry == i, ImGuiSelectableFlags.AllowDoubleClick))
                    {
                        selectedSong = song.Id;
                        selectedHistoryEntry = i;
                        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                            Play(selectedSong);
                    }
                    if (ImGui.BeginPopupContextItem())
                    {
                        if (!isFavorite)
                        {
                            if (ImGui.Selectable("Add to favorites"))
                                AddFavorite(song.Id);
                        }
                        else
                        {
                            if (ImGui.Selectable("Remove from favorites"))
                                RemoveFavorite(song.Id);
                        }
                        ImGui.EndPopup();
                    }
                    ImGui.TableNextColumn();

                    ImGui.SameLine(8);

                    TimeSpan deltaTime = (now - songHistoryEntry.TimePlayed);

                    if (deltaTime.TotalMinutes >= 1)
                    {
                        ImGui.Text($"{(int)deltaTime.TotalMinutes}m ago");
                    }
                    else
                    {
                        ImGui.Text($"{(int)deltaTime.TotalSeconds}s ago");
                    }
                }
                ImGui.EndTable();
            }
            ImGui.EndChild();
            DrawFooter(true);
        }

        public void DrawSettings()
        {
            if (!settingsVisible)
                return;

            var settingsSize = AllowDebug ? ScaledVector2(490, 325) : ScaledVector2(490, 175);

            ImGui.SetNextWindowSize(settingsSize, ImGuiCond.Always);
            if (ImGui.Begin("Orchestrion Settings", ref settingsVisible, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse))
            {
                if (ImGui.IsWindowAppearing())
                    showDebugOptions = false;

                ImGui.SetNextItemOpen(true, ImGuiCond.Appearing);
                if (ImGui.CollapsingHeader("Display##orch options"))
                {
                    ImGui.Spacing();

                    var showSongInTitlebar = configuration.ShowSongInTitleBar;
                    if (ImGui.Checkbox("Show current song in player title bar", ref showSongInTitlebar))
                    {
                        configuration.ShowSongInTitleBar = showSongInTitlebar;
                        configuration.Save();
                    }

                    var showSongInChat = configuration.ShowSongInChat;
                    if (ImGui.Checkbox("Show \"Now playing\" messages in game chat when the current song changes", ref showSongInChat))
                    {
                        configuration.ShowSongInChat = showSongInChat;
                        configuration.Save();
                    }

                    var showNative = configuration.ShowSongInNative;
                    if (ImGui.Checkbox("Show current song in the \"server info\" UI element in-game", ref showNative))
                    {
                        configuration.ShowSongInNative = showNative;
                        configuration.Save();
                    }

                    if (!showNative)
                        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.5f);
                    
                    var showIdNative = configuration.ShowIdInNative;
                    if (ImGui.Checkbox("Show song ID in the \"server info\" UI element in-game", ref showIdNative) && showNative)
                    {
                        configuration.ShowIdInNative = showIdNative;
                        configuration.Save();
                    }
                    
                    if (!showNative)
                        ImGui.PopStyleVar();

                    if (AllowDebug)
                        ImGui.Checkbox("Show debug options (Only if you have issues!)", ref showDebugOptions);

                    ImGui.TreePop();
                }

                // I'm sure there are better ways to do this, but I didn't want to change global spacing
                ImGui.Spacing();
                ImGui.Spacing();
                ImGui.Spacing();

                if (showDebugOptions && AllowDebug)
                {
                    ImGui.SetNextItemOpen(true, ImGuiCond.Appearing);
                    if (ImGui.CollapsingHeader("Debug##orch options"))
                    {
                        ImGui.Spacing();

                        int targetPriority = configuration.TargetPriority;

                        ImGui.SetNextItemWidth(100.0f);
                        if (ImGui.SliderInt("BGM priority", ref targetPriority, 0, 11))
                        {
                            // stop the current song so it doesn't get 'stuck' on in case we switch to a lower priority
                            Stop();

                            configuration.TargetPriority = targetPriority;
                            configuration.Save();

                            // don't (re)start a song here for now
                        }
                        ImGui.SameLine();
                        HelpMarker("Songs play at various priority levels, from 0 to 11.\n" +
                            "Songs at lower numbers will override anything playing at a higher number, with 0 winning out over everything else.\n" +
                            "You can experiment with changing this value if you want the game to be able to play certain music even when Orchestrion is active.\n" +
                            "(Usually) zone music is 10-11, mount music is 6, GATEs are 4.  There is a lot of variety in this, however.\n" +
                            "The old Orchestrion used to play at level 3 (it now uses 0 by default).");

                        ImGui.Spacing();
                        if (ImGui.Button("Dump priority info"))
                            controller.DumpDebugInformation();

                        ImGui.TreePop();
                    }
                }
            }
            ImGui.End();
        }
        
        private void DrawBgmTooltip(Song bgm)
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(400f);
            ImGui.TextColored(new Vector4(0, 1, 0, 1), "Song Info");
            ImGui.TextWrapped($"Title: {bgm.Name}");
            ImGui.TextWrapped(string.IsNullOrEmpty(bgm.Locations) ? "Location: Unknown" : $"Location: {bgm.Locations}");
            if (!string.IsNullOrEmpty(bgm.AdditionalInfo))
                ImGui.TextWrapped($"Info: {bgm.AdditionalInfo}");
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        static void HelpMarker(string desc)
        {
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35.0f);
                ImGui.TextUnformatted(desc);
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }
        }

        private static Vector2 ScaledVector2(float x, float y)
        {
            return new Vector2(x * Scale, y * Scale);
        }
    }
}
