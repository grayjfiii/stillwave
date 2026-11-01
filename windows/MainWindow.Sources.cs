using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Stillwave
{
    // Radio Garden, Internet Archive, podcasts and "add your own station"
    public partial class MainWindow
    {
        int source;                                   // 0 radio directory, 1 Radio Garden, 2 Internet Archive, 3 podcasts
        List<string> tagList = new List<string>();
        readonly Stack<(List<Station> List, string Status)> backStack = new Stack<(List<Station>, string)>();

        void ResetBack()
        {
            backStack.Clear();
            BackBtn.Visibility = Visibility.Collapsed;
        }

        void Source_Changed(object s, SelectionChangedEventArgs e)
        {
            if (loading || !IsLoaded) return;
            source = SourceBox.SelectedIndex;
            debounce.Stop();
            showingFavs = false;
            FavBtn.Content = "My favorites";

            bool radio = source == 0;
            CountryBox.IsEnabled = StateBox.IsEnabled = radio;
            GenreBox.IsEnabled = radio || source == 2;
            GenreLabel.Text = source == 2 ? "Collection" : "Genre";
            PlaceBtn.Visibility = source == 1 ? Visibility.Visible : Visibility.Collapsed;
            NameBox.ToolTip = source switch
            {
                1 => "Search stations or places. Leave it empty for a random place.",
                2 => "Search titles or artists. Leave it empty for the most popular.",
                3 => "Search podcasts. Leave it empty for the top charts.",
                _ => null,
            };

            loading = true;   // refilling the boxes must not trigger a search of its own
            if (source == 2)
            {
                GenreBox.IsEditable = false;
                GenreBox.ItemsSource = Sources.ArchiveSets.Select(a => a.Name).ToList();
                GenreBox.SelectedIndex = 0;
            }
            else
            {
                GenreBox.IsEditable = true;
                GenreBox.ItemsSource = tagList;
                GenreBox.SelectedIndex = -1;
                GenreBox.Text = "";
            }
            loading = false;
            _ = RunSearch();
        }

        // search for anything that isn't the radio directory
        async Task RunOtherSearch(int mine)
        {
            string name = NameBox.Text.Trim();
            ListStatus.Text = "Searching...";
            try
            {
                List<Station> list; string status;
                switch (source)
                {
                    case 1:
                        if (name.Length == 0) { await ShowRandomPlace(mine); return; }
                        list = await Sources.GardenSearch(name);
                        status = $"{list.Count} results from Radio Garden. Double-click a station to play it, or a place to see its stations.";
                        break;
                    case 2:
                        var set = Sources.ArchiveSets[Math.Max(0, GenreBox.SelectedIndex)];
                        list = await Sources.ArchiveSearch(set.Id, name);
                        status = $"{list.Count} from the Internet Archive ({set.Name}, most popular first). Double-click one to see what's in it.";
                        break;
                    default:
                        list = name.Length == 0 ? await Sources.PodcastTop() : await Sources.PodcastSearch(name);
                        status = (name.Length == 0 ? "Top podcasts. " : $"{list.Count} podcasts. ") + "Double-click one to see its episodes.";
                        break;
                }
                if (mine != searchSeq) return;
                ShowList(list, list.Count == 0 ? "Nothing found." : status);
            }
            catch (Exception e) { if (mine == searchSeq) ListStatus.Text = "Search failed: " + e.Message; }
        }

        async Task ShowRandomPlace(int mine)
        {
            for (int tries = 0; tries < 6; tries++)
            {
                var p = await Sources.RandomPlace();
                var list = await Sources.PlaceChannels(p.Id, p.Title, p.Country);
                if (mine != searchSeq) return;
                if (list.Count == 0) continue;
                ShowList(list, $"Random place: {p.Title}, {p.Country}  -  {list.Count} stations. Press Random place again to hop somewhere else.");
                return;
            }
            ListStatus.Text = "Couldn't find a place with stations. Try again.";
        }

        void Place_Click(object s, RoutedEventArgs e)
        {
            ResetBack();
            _ = ShowRandomPlace(++searchSeq);
        }

        // open a podcast, collection or place and list what's inside
        async void Open(Station s)
        {
            ListStatus.Text = "Loading " + s.Name + "...";
            int mine = ++searchSeq;
            try
            {
                List<Station> kids;
                string what;
                if (s.Kind == "podcast") { kids = await Sources.PodcastEpisodes(s); what = "episodes, newest first"; }
                else if (s.Kind == "archive") { kids = await Sources.ArchiveFiles(s); what = "recordings"; }
                else { kids = await Sources.PlaceChannels(s.Url.Substring("garden-place:".Length), s.State, s.Country); what = "stations"; }
                if (mine != searchSeq) return;
                if (kids.Count == 0) { ListStatus.Text = "Nothing playable in " + s.Name + "."; return; }
                backStack.Push((shown, listStatusBase));
                BackBtn.Visibility = Visibility.Visible;
                ShowList(kids, $"{s.Name}  -  {kids.Count} {what}. Double-click to play; Back returns to the list.");
            }
            catch (Exception e) { if (mine == searchSeq) ListStatus.Text = "Couldn't open it: " + e.Message; }
        }

        void Back_Click(object s, RoutedEventArgs e)
        {
            if (backStack.Count == 0) return;
            var prev = backStack.Pop();
            if (backStack.Count == 0) BackBtn.Visibility = Visibility.Collapsed;
            ShowList(prev.List, prev.Status);
        }

        // ---------- add your own station ----------
        void AddStation_Click(object sender, RoutedEventArgs ev)
        {
            var dlg = new Window
            {
                Title = "Add a station", Width = 520, SizeToContent = SizeToContent.Height, Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Resources = Resources,
            };
            var url = new TextBox(); var name = new TextBox();
            var msg = new TextBlock { Foreground = System.Windows.Media.Brushes.LightSalmon, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            TextBlock Label(string t) => new TextBlock { Text = t, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9f, 0xb3, 0xc8)), FontSize = 12, Margin = new Thickness(0, 8, 0, 2) };
            var import = new Button { Content = "Import playlist file..." };
            var add = new Button { Content = "Add", IsDefault = true, MinWidth = 70 };
            var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0), MinWidth = 70 };
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            row.Children.Add(import); row.Children.Add(add); row.Children.Add(cancel);
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(Label("Stream link (or a .pls / .m3u playlist link)"));
            panel.Children.Add(url);
            panel.Children.Add(Label("Name (optional - the stream's own name is used if you leave it empty)"));
            panel.Children.Add(name);
            panel.Children.Add(msg);
            panel.Children.Add(row);
            dlg.Content = panel;

            async Task AddEntries(List<(string Name, string Url)> entries, string typedName)
            {
                int added = 0;
                foreach (var en in entries)
                {
                    if (favorites.Any(f => f.Url == en.Url)) continue;
                    string nm = entries.Count == 1 && typedName.Length > 0 ? typedName : System.Text.RegularExpressions.Regex.Replace(en.Name ?? "", "\\s*\\(#?\\d+\\).*$", "");   // "Name (#1): blurb" -> "Name"
                    if (string.IsNullOrWhiteSpace(nm)) nm = await Sources.StreamName(en.Url);
                    if (string.IsNullOrWhiteSpace(nm)) { try { nm = new Uri(en.Url).Host; } catch { nm = en.Url; } }
                    favorites.Add(new Station { Name = nm.Trim(), Url = en.Url, Codec = "Custom" });
                    added++;
                }
                SaveFavorites();
                dlg.DialogResult = true;
                showingFavs = false;
                ShowFavorites();
                ListStatus.Text = added == 0 ? "Already in your favorites." : $"Added {added} station{(added == 1 ? "" : "s")} to My favorites.";
            }

            add.Click += async (s, e) =>
            {
                string u = url.Text.Trim();
                if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || !uri.Scheme.StartsWith("http")) { msg.Text = "Paste a full link that starts with http:// or https://"; return; }
                add.IsEnabled = false; msg.Text = "Checking the link...";
                try
                {
                    var entries = await Sources.ResolveLink(u);
                    if (entries.Count == 0) { msg.Text = "That playlist has no stream links in it."; add.IsEnabled = true; return; }
                    // a station's own playlist link just lists mirrors of the same stream, so keep the first
                    await AddEntries(entries.Take(1).ToList(), name.Text.Trim());
                }
                catch (Exception ex) { msg.Text = "Couldn't read that link: " + ex.Message; add.IsEnabled = true; }
            };
            import.Click += async (s, e) =>
            {
                var pick = new Microsoft.Win32.OpenFileDialog { Filter = "Playlists (*.m3u;*.m3u8;*.pls)|*.m3u;*.m3u8;*.pls|All files|*.*" };
                if (pick.ShowDialog(dlg) != true) return;
                try
                {
                    var entries = Sources.ParsePlaylist(File.ReadAllText(pick.FileName));
                    if (entries.Count == 0) { msg.Text = "No stream links found in that file."; return; }
                    await AddEntries(entries, "");
                }
                catch (Exception ex) { msg.Text = "Couldn't read that file: " + ex.Message; }
            };
            dlg.Loaded += (s, e) => url.Focus();
            dlg.ShowDialog();
        }
    }
}
