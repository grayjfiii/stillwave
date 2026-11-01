using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Stillwave
{
    public class Station : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        string nowPlaying = "";
        [System.Text.Json.Serialization.JsonIgnore] public string NowPlaying
        {
            get => nowPlaying;
            set { nowPlaying = value ?? ""; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(NowPlaying))); }
        }

        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string Country { get; set; } = "";
        public string State { get; set; } = "";
        public string Tags { get; set; } = "";
        public string Codec { get; set; } = "";
        public int Bitrate { get; set; }
        public string Uuid { get; set; } = "";
        public string Favicon { get; set; } = "";
        public string Kind { get; set; } = "radio";   // radio, garden, place, archive, podcast, episode
        public string Show { get; set; } = "";        // for an episode: the podcast or collection it belongs to
        [System.Text.Json.Serialization.JsonIgnore] public bool AutoNext => Kind == "episode" && Codec == "Archive";

        // set when a list is shown, so saved stations are marked with a star
        [System.Text.Json.Serialization.JsonIgnore] public bool IsFav { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public string DisplayName => (IsFav ? "* " : "") + Name;
        [System.Text.Json.Serialization.JsonIgnore] public string CountryShort => Country.StartsWith("The ") ? Country.Substring(4) : Country;
        [System.Text.Json.Serialization.JsonIgnore] public string TagsShort => string.Join(", ", Tags.Split(',').Take(3));
        [System.Text.Json.Serialization.JsonIgnore] public string Info => Bitrate > 0 ? $"{Codec} {Bitrate}k" : Codec;
    }

    public class CountryItem
    {
        public string Name { get; set; } = "";
        public string Code { get; set; } = "";
        public string Display { get; set; } = "";
    }

    // radio-browser.info: the community directory behind Home Assistant's Radio Browser
    static class Directory
    {
        static readonly string[] Mirrors =
        {
            "https://de1.api.radio-browser.info", "https://de2.api.radio-browser.info",
            "https://fi1.api.radio-browser.info", "https://nl1.api.radio-browser.info", "https://at1.api.radio-browser.info",
        };
        static int mirror;
        static readonly HttpClient Http = MakeClient();

        static HttpClient MakeClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Stillwave/1.0");
            return c;
        }

        public static async Task<JsonElement> Get(string path)
        {
            Exception last = null;
            for (int k = 0; k < Mirrors.Length; k++)
            {
                int m = (mirror + k) % Mirrors.Length;
                try
                {
                    string text = await Http.GetStringAsync(Mirrors[m] + path);
                    mirror = m;
                    return JsonDocument.Parse(text).RootElement.Clone();
                }
                catch (Exception e) { last = e; }
            }
            throw new Exception("Can't reach the radio directory: " + last?.Message);
        }

        static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

        public static Station ToStation(JsonElement e)
        {
            string url = Str(e, "url_resolved");
            if (url.Length == 0) url = Str(e, "url");
            return new Station
            {
                Name = Str(e, "name").Trim(),
                Url = url,
                Country = Str(e, "country"),
                State = Str(e, "state"),
                Tags = Str(e, "tags"),
                Codec = Str(e, "codec"),
                Bitrate = e.TryGetProperty("bitrate", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : 0,
                Uuid = Str(e, "stationuuid"),
                Favicon = Str(e, "favicon"),
            };
        }

        public static async Task<List<Station>> Search(string name, string countryCode, string state, string tag)
        {
            var q = new List<string> { "hidebroken=true", "order=clickcount", "reverse=true", "limit=300" };
            if (name.Length > 0) q.Add("name=" + Uri.EscapeDataString(name));
            if (countryCode.Length > 0) q.Add("countrycode=" + countryCode);
            if (state.Length > 0) q.Add("state=" + Uri.EscapeDataString(state));
            if (tag.Length > 0) q.Add("tag=" + Uri.EscapeDataString(tag));
            var arr = await Get("/json/stations/search?" + string.Join("&", q));
            return arr.EnumerateArray().Select(ToStation).Where(s => s.Url.Length > 0 && s.Name.Length > 0).ToList();
        }

        public static async Task<List<CountryItem>> Countries()
        {
            var arr = await Get("/json/countries");
            return arr.EnumerateArray()
                .Where(c => Str(c, "name").Length > 0 && Str(c, "iso_3166_1").Length > 0)
                .Select(c =>
                {
                    string n = Str(c, "name");
                    string shown = n.StartsWith("The ") ? n.Substring(4) : n;
                    int count = c.TryGetProperty("stationcount", out var v) ? v.GetInt32() : 0;
                    return new CountryItem { Name = n, Code = Str(c, "iso_3166_1"), Display = $"{shown} ({count})" };
                })
                .OrderBy(c => c.Display, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static async Task<List<string>> Tags()
        {
            var arr = await Get("/json/tags?order=stationcount&reverse=true&limit=250&hidebroken=true");
            return arr.EnumerateArray().Select(t => Str(t, "name")).Where(t => t.Length > 1).OrderBy(t => t).ToList();
        }

        public static async Task<List<(string Country, string State)>> States()
        {
            var arr = await Get("/json/states?hidebroken=true&limit=100000");
            return arr.EnumerateArray().Select(s => (Str(s, "country"), Str(s, "name"))).Where(s => s.Item2.Length > 0).ToList();
        }

        static readonly HttpClient IcyHttp = MakeClient();

        // Shoutcast/Icecast streams carry the song title inline; ask for it, read up to the first
        // metadata block, and hang up. Returns null when the stream has none.
        public class TrackInfo
        {
            public string Artist = "", Song = "", Album = "", Year = "", Genre = "", ArtUrl = "";
        }

        static string Norm(string s) => Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]", "");

        // Apple's free iTunes Search API: album, year and cover art for a song, when the match is convincing
        public static async Task<TrackInfo> Lookup(string artist, string song, CancellationToken ct)
        {
            string term = (artist + " " + song).Trim();
            if (term.Length < 3) return null;
            string text = await IcyHttp.GetStringAsync("https://itunes.apple.com/search?media=music&entity=song&limit=6&term=" + Uri.EscapeDataString(term), ct);
            var root = JsonDocument.Parse(text).RootElement;
            foreach (var r in root.GetProperty("results").EnumerateArray())
            {
                string tn = Norm(Str(r, "trackName")), an = Norm(Str(r, "artistName"));
                string ns = Norm(song), na = Norm(artist);
                bool songOk = ns.Length > 0 && tn.Length > 0 && (tn.Contains(ns) || ns.Contains(tn));
                bool artistOk = na.Length == 0 || (an.Length > 0 && (an.Contains(na) || na.Contains(an)));
                if (!songOk || !artistOk) continue;
                string date = Str(r, "releaseDate");
                return new TrackInfo
                {
                    Artist = Str(r, "artistName"),
                    Song = Str(r, "trackName"),
                    Album = Str(r, "collectionName"),
                    Year = date.Length >= 4 ? date.Substring(0, 4) : "",
                    Genre = Str(r, "primaryGenreName"),
                    ArtUrl = Str(r, "artworkUrl100").Replace("100x100bb", "300x300bb"),
                };
            }
            return null;
        }

        // ---------- artist profile: Wikipedia's short summary, plus genre/origin/formed from TheAudioDB ----------
        public class ArtistInfo { public string Name = "", Meta = "", Bio = "", Url = ""; }
        static readonly Dictionary<string, ArtistInfo> artistCache = new Dictionary<string, ArtistInfo>();
        static readonly Regex MusicWords = new Regex("music|singer|band|rapper|composer|DJ\\b|producer|duo|songwriter|group|vocalist|guitarist|recording artist|pianist|orchestra|ensemble", RegexOptions.IgnoreCase);

        // "Artist feat. Someone" / "A & B" / "A, B" -> the first name
        public static string PrimaryArtist(string a)
        {
            a = Regex.Split(a ?? "", "\\s+(?:feat\\.?|ft\\.?|featuring|with|x)\\s+|\\s*[,;/]\\s*|\\s+&\\s+|\\s+and\\s+", RegexOptions.IgnoreCase)[0];
            return a.Trim();
        }

        public static async Task<ArtistInfo> Artist(string artist, CancellationToken ct)
        {
            string name = PrimaryArtist(artist);
            if (name.Length < 2) return null;
            string key = name.ToLowerInvariant();
            lock (artistCache) if (artistCache.TryGetValue(key, out var hit)) return hit;

            ArtistInfo found = null;
            foreach (string suffix in new[] { "", " (band)", " (musician)", " (singer)", " (rapper)", " (producer)", " (duo)" })
            {
                try
                {
                    string page = Uri.EscapeDataString((name + suffix).Replace(' ', '_'));
                    string text = await IcyHttp.GetStringAsync("https://en.wikipedia.org/api/rest_v1/page/summary/" + page + "?redirect=true", ct);
                    var j = JsonDocument.Parse(text).RootElement;
                    if (Str(j, "type") == "disambiguation") continue;
                    string title = Str(j, "title"), desc = Str(j, "description"), extract = Str(j, "extract");
                    string baseTitle = Regex.Replace(title, "\\s*\\(.*?\\)\\s*$", "");
                    string nt = Norm(baseTitle), na = Norm(name);
                    if (nt.Length == 0 || !(nt == na || nt.Contains(na) || na.Contains(nt))) continue;      // must be the same name
                    if (!MusicWords.IsMatch(desc + " " + extract.Substring(0, Math.Min(300, extract.Length)))) continue;   // and a musical act
                    string bio = extract;
                    if (bio.Length > 330)
                    {
                        int cut = bio.LastIndexOf(". ", 330, StringComparison.Ordinal);
                        bio = cut > 120 ? bio.Substring(0, cut + 1) : bio.Substring(0, 327).TrimEnd() + "...";
                    }
                    string url = "";
                    if (j.TryGetProperty("content_urls", out var cu) && cu.TryGetProperty("desktop", out var dk)) url = Str(dk, "page");
                    found = new ArtistInfo { Name = baseTitle, Meta = desc, Bio = bio, Url = url };
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }

            // genre / origin / year formed (the free tier has no biography text)
            try
            {
                string text = await IcyHttp.GetStringAsync("https://www.theaudiodb.com/api/v1/json/123/search.php?s=" + Uri.EscapeDataString(name), ct);
                var arr = JsonDocument.Parse(text).RootElement.GetProperty("artists");
                if (arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                {
                    var a = arr[0];
                    string formed = Str(a, "intFormedYear");
                    string meta = string.Join("  -  ", new[] { Str(a, "strGenre"), Str(a, "strCountry"), formed.Length > 0 && formed != "0" ? "since " + formed : "" }.Where(x => x.Length > 0));
                    if (meta.Length > 0)
                    {
                        if (found == null) found = new ArtistInfo { Name = Str(a, "strArtist").Length > 0 ? Str(a, "strArtist") : name };
                        found.Meta = meta;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            lock (artistCache) artistCache[key] = found;
            return found;
        }

        public static async Task<(string Title, string Art)> IcyTitle(string url, CancellationToken ct, int timeoutSeconds = 12)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Icy-MetaData", "1");
            using var resp = await IcyHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            if (!resp.Headers.TryGetValues("icy-metaint", out var v) || !int.TryParse(v.First(), out int interval) || interval <= 0) return (null, null);
            using var s = await resp.Content.ReadAsStreamAsync(limit.Token);
            var skip = new byte[interval];
            if (!await ReadFully(s, skip, limit.Token)) return (null, null);
            var lenByte = new byte[1];
            if (!await ReadFully(s, lenByte, limit.Token) || lenByte[0] == 0) return (null, null);
            var meta = new byte[lenByte[0] * 16];
            if (!await ReadFully(s, meta, limit.Token)) return (null, null);
            string text = Encoding.UTF8.GetString(meta).TrimEnd('\0');
            var m = Regex.Match(text, "StreamTitle='(.*?)';", RegexOptions.Singleline);
            string title = m.Success ? m.Groups[1].Value.Trim() : "";
            // iHeart stations pack extra key="value" fields after the song: Artist - text="Song" song_spot=...
            var ih = Regex.Match(title, "^(.*?)\\s*-\\s*text=\"([^\"]*)\"");
            if (ih.Success) title = ih.Groups[2].Value.Length > 0 ? (ih.Groups[1].Value + " - " + ih.Groups[2].Value).Trim(' ', '-') : ih.Groups[1].Value;
            else title = Regex.Replace(title, "\\s+\\w+=\".*$", "").Trim();
            string art = Regex.Match(text, "amgArtworkURL=\"([^\"]*)\"").Groups[1].Value;
            return (title.Length > 0 ? title : null, art.Length > 0 ? art : null);
        }

        static async Task<bool> ReadFully(Stream s, byte[] buf, CancellationToken ct)
        {
            int got = 0;
            while (got < buf.Length)
            {
                int n = await s.ReadAsync(buf, got, buf.Length - got, ct);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        // radio-browser asks players to report a play so popular stations rank higher
        public static async void Click(string uuid)
        {
            if (uuid.Length == 0) return;
            try { await Get("/json/url/" + uuid); } catch { }
        }
    }

    public partial class MainWindow : Window
    {
        static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stillwave");
        // before the rename the settings lived in "DeskRadioPC": bring them along once
        static readonly bool migrated = MigrateOldData();
        static bool MigrateOldData()
        {
            try
            {
                string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskRadioPC");
                if (!System.IO.Directory.Exists(old) || System.IO.Directory.Exists(DataDir)) return false;
                System.IO.Directory.CreateDirectory(DataDir);
                foreach (string f in System.IO.Directory.GetFiles(old)) File.Copy(f, Path.Combine(DataDir, Path.GetFileName(f)));
                return true;
            }
            catch { return false; }
        }
        static readonly string FavFile = Path.Combine(DataDir, "favorites.json");
        static readonly string SettingsFile = Path.Combine(DataDir, "settings.json");

        readonly MediaPlayer player = new MediaPlayer();
        Spectrum spectrum;
        Process ffplay;               // fallback for streams Windows' own player rejects
        Station current;
        List<Station> favorites = new List<Station>();
        List<Station> shown = new List<Station>();
        List<(string Country, string State)> allStates = new List<(string, string)>();
        bool showingFavs, loading = true, playing;
        Station lastStation;            // the station that was on when the app last closed
        bool resumePlaying;
        CancellationTokenSource titleCts;
        int searchSeq;
        readonly DispatcherTimer debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        readonly DispatcherTimer volRestart = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };

        public MainWindow()
        {
            InitializeComponent();
            LoadSettings();
            LoadFavorites();
            debounce.Tick += (s, e) => { debounce.Stop(); _ = RunSearch(); };
            volRestart.Tick += (s, e) => { volRestart.Stop(); if (ffplay != null && current != null) StartFfplay(current); };
            player.MediaOpened += (s, e) => Dispatcher.Invoke(() => { if (ffplay == null) NowStatus.Text = "Playing"; });
            player.MediaFailed += (s, e) => Dispatcher.Invoke(() => OnFailed(e.ErrorMessage));
            player.MediaEnded += (s, e) => Dispatcher.Invoke(() => { if (playing && current != null && current.AutoNext) Step(1); });  // next recording in a collection
            spectrum = new Spectrum(SpecCanvas);
            if (savedSpectrum != null) spectrum.Load(savedSpectrum);
            SpectrumChanged();
            spectrum.Start();
            BuildJumpList();
            _ = ListenForCommands();
            Closed += (s, e) => { SaveSettings(); StopAll(); spectrum.Stop(); };  // save first, so "was playing" is still true
            Loaded += async (s, e) => await Init();   // always opens in full mode
        }

        // ---------- startup ----------
        async Task Init()
        {
            try
            {
                var countries = await Directory.Countries();
                countries.Insert(0, new CountryItem { Name = "", Code = "", Display = "Any country" });
                CountryBox.ItemsSource = countries;
                var tags = await Directory.Tags();
                tags.Insert(0, "");
                tagList = tags;
                GenreBox.ItemsSource = tags;
                CountryBox.SelectedItem = countries.FirstOrDefault(c => c.Code == savedCountry) ?? countries.FirstOrDefault(c => c.Code == "US");
                _ = LoadStates();
            }
            catch (Exception e) { ListStatus.Text = e.Message; }
            loading = false;

            string[] args = Environment.GetCommandLineArgs();
            int p = Array.IndexOf(args, "--play");
            if (p >= 0 && p + 1 < args.Length) Play(new Station { Name = "Test stream", Url = args[p + 1] });
            else if (lastStation != null && lastStation.Url.Length > 0)  // pick up where we left off
            {
                if (resumePlaying) Play(lastStation);
                else ShowCurrent(lastStation, "Stopped");
            }

            if (favorites.Count > 0) ShowFavorites();
            else await RunSearch();
        }

        async Task LoadStates()
        {
            try { allStates = await Directory.States(); FillStates(); } catch { }
        }

        void FillStates()
        {
            var c = CountryBox.SelectedItem as CountryItem;
            loading = true;
            string keep = StateBox.Text;
            var list = new List<string> { "" };
            if (c != null && c.Code.Length > 0)
                list.AddRange(allStates.Where(s => string.Equals(s.Country, c.Name, StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.State).Distinct().OrderBy(s => s));
            StateBox.ItemsSource = list;
            StateBox.Text = list.Contains(keep) ? keep : "";
            loading = false;
        }

        // ---------- settings and favorites ----------
        string savedCountry = "US";
        string savedSpectrum;

        // ---------- taskbar right-click menu ----------
        void BuildJumpList()
        {
            try
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                var list = new System.Windows.Shell.JumpList { ShowFrequentCategory = false, ShowRecentCategory = false };
                void Add(string title, string cmd, string tip) => list.JumpItems.Add(new System.Windows.Shell.JumpTask
                { Title = title, Arguments = "--cmd " + cmd, ApplicationPath = exe, IconResourcePath = exe, IconResourceIndex = 0, Description = tip, WorkingDirectory = Path.GetDirectoryName(exe) });
                Add("Play / Pause", "toggle", "Start or stop the current station");
                Add("Next station", "next", "Next station in the list");
                Add("Previous station", "prev", "Previous station in the list");
                Add("Random station", "random", "Play a random station from the list");
                Add("Mini / full window", "mini", "Switch between the small always-on-top player and the full window");
                System.Windows.Shell.JumpList.SetJumpList(Application.Current, list);
                list.Apply();
            }
            catch { }
        }

        async Task ListenForCommands()
        {
            while (true)
            {
                try
                {
                    using var server = new System.IO.Pipes.NamedPipeServerStream(App.PipeName, System.IO.Pipes.PipeDirection.In, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    using var r = new StreamReader(server);
                    string cmd = (await r.ReadLineAsync())?.Trim();
                    RunCommand(cmd);
                }
                catch { await Task.Delay(500); }
            }
        }

        void RunCommand(string cmd)
        {
            switch (cmd)
            {
                case "toggle": PlayStop_Click(null, null); break;
                case "next": Step(1); break;
                case "prev": Step(-1); break;
                case "random": Random_Click(null, null); break;
                case "mini": SetMini(!mini); break;
                default:
                    if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                    Activate();
                    break;
            }
        }

        // ---------- mini mode: small always-on-top player ----------
        bool mini;
        Rect normalBounds;

        void Mini_Click(object s, RoutedEventArgs e) => SetMini(!mini);

        void SetMini(bool on)
        {
            if (on == mini) return;
            mini = on;
            if (on)
            {
                if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
                normalBounds = new Rect(Left, Top, Width, Height);
                FilterGrid.Visibility = StatusGrid.Visibility = Results.Visibility = Visibility.Collapsed;
                ResultsRow.Height = GridLength.Auto;
                HeaderGrid.MinHeight = 0;
                // buttons drop under the song text so the title gets the full width
                Grid.SetColumnSpan(NowText, 2);
                Grid.SetColumn(Transport, 1); Grid.SetRow(Transport, 1); Grid.SetColumnSpan(Transport, 2);
                Transport.Margin = new Thickness(0, 8, 0, 0);
                MinWidth = 560; MinHeight = 0;
                SizeToContent = SizeToContent.Height;
                Width = 740;
                Topmost = true;
                MiniBtn.Content = "Full";
                ArtistBox.Visibility = Visibility.Collapsed;
                scanCts?.Cancel();   // no table on screen, so stop checking other stations' titles
            }
            else
            {
                FilterGrid.Visibility = StatusGrid.Visibility = Results.Visibility = Visibility.Visible;
                SaveBtn.Visibility = Visibility.Visible;
                ResultsRow.Height = new GridLength(1, GridUnitType.Star);
                HeaderGrid.MinHeight = 114;   // room for the artist profile, so the table never shifts when it appears
                Grid.SetColumnSpan(NowText, 1);
                Grid.SetColumn(Transport, 2); Grid.SetRow(Transport, 0); Grid.SetColumnSpan(Transport, 1);
                Transport.Margin = new Thickness(0);
                SizeToContent = SizeToContent.Manual;
                MinWidth = 640; MinHeight = 420;
                if (normalBounds.Width > 0) { Width = normalBounds.Width; Height = normalBounds.Height; Left = normalBounds.X; Top = normalBounds.Y; }
                Topmost = false;
                MiniBtn.Content = "Mini";
                if (artistHas) ArtistBox.Visibility = Visibility.Visible;
                StartNowPlayingScan();
            }
        }

        void Spec_Click(object s, MouseButtonEventArgs e)
        {
            spectrum.Style = (spectrum.Style + 1) % Spectrum.Styles.Length;
            spectrum.Visible = true;
            SpectrumChanged();
        }

        void SpectrumChanged()
        {
            spectrum.Changed();
            SpecBox.Height = spectrum.Visible ? 54 : 10;
            BuildSpectrumMenu();
        }

        void BuildSpectrumMenu()
        {
            var menu = new ContextMenu();
            MenuItem Group(string title, string[] names, int selected, Action<int> set)
            {
                var g = new MenuItem { Header = title };
                for (int i = 0; i < names.Length; i++)
                {
                    int idx = i;
                    var m = new MenuItem { Header = names[i], IsCheckable = true, IsChecked = i == selected, StaysOpenOnClick = false };
                    m.Click += (s, e) => { set(idx); spectrum.Visible = true; SpectrumChanged(); };
                    g.Items.Add(m);
                }
                return g;
            }
            menu.Items.Add(Group("Style", Spectrum.Styles, spectrum.Style, i => spectrum.Style = i));
            menu.Items.Add(Group("Colors", Spectrum.Themes, spectrum.Theme, i => spectrum.Theme = i));
            menu.Items.Add(Group("Bars", Array.ConvertAll(Spectrum.BandChoices, b => b.ToString()), spectrum.BandsIdx, i => spectrum.BandsIdx = i));
            menu.Items.Add(Group("Sensitivity", Spectrum.GainNames, spectrum.Gain, i => spectrum.Gain = i));
            menu.Items.Add(Group("Motion", Spectrum.SmoothNames, spectrum.Smooth, i => spectrum.Smooth = i));
            var peaks = new MenuItem { Header = "Peak markers", IsCheckable = true, IsChecked = spectrum.Peaks };
            peaks.Click += (s, e) => { spectrum.Peaks = !spectrum.Peaks; SpectrumChanged(); };
            menu.Items.Add(peaks);
            menu.Items.Add(new Separator());
            var vis = new MenuItem { Header = spectrum.Visible ? "Hide spectrum" : "Show spectrum" };
            vis.Click += (s, e) => { spectrum.Visible = !spectrum.Visible; SpectrumChanged(); };
            menu.Items.Add(vis);
            SpecBox.ContextMenu = menu;
        }
        void LoadSettings()
        {
            try
            {
                var j = JsonDocument.Parse(File.ReadAllText(SettingsFile)).RootElement;
                if (j.TryGetProperty("volume", out var v)) VolSlider.Value = v.GetDouble();
                if (j.TryGetProperty("country", out var c)) savedCountry = c.GetString();
                if (j.TryGetProperty("last", out var ls) && ls.ValueKind == JsonValueKind.Object) lastStation = ls.Deserialize<Station>();
                if (j.TryGetProperty("wasPlaying", out var wp)) resumePlaying = wp.GetBoolean();
                if (j.TryGetProperty("spectrum", out var sp)) savedSpectrum = sp.GetString();
            }
            catch { }
            player.Volume = VolSlider.Value;
        }

        void SaveSettings()
        {
            try
            {
                Directory_Create();
                string code = (CountryBox.SelectedItem as CountryItem)?.Code ?? savedCountry;
                File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new { volume = VolSlider.Value, country = code, last = current ?? lastStation, wasPlaying = playing, spectrum = spectrum.Serialize() }));
            }
            catch { }
        }

        static void Directory_Create() => System.IO.Directory.CreateDirectory(DataDir);

        void LoadFavorites()
        {
            try { favorites = JsonSerializer.Deserialize<List<Station>>(File.ReadAllText(FavFile)) ?? new List<Station>(); }
            catch { favorites = new List<Station>(); }
        }

        void SaveFavorites()
        {
            try { Directory_Create(); File.WriteAllText(FavFile, JsonSerializer.Serialize(favorites)); } catch { }
        }

        bool IsFav(Station s) => favorites.Any(f => f.Url == s.Url);

        // ---------- lists ----------
        void ShowList(List<Station> list, string status)
        {
            shown = list;
            foreach (var s in shown) s.IsFav = IsFav(s);
            Results.ItemsSource = null;
            Results.ItemsSource = shown;
            ListStatus.Text = status;
            listStatusBase = status;
            StartNowPlayingScan();
        }

        // ---------- "now playing" column: peek at each listed station's stream for its current song ----------
        string listStatusBase = "";
        CancellationTokenSource scanCts;

        void StartNowPlayingScan()
        {
            scanCts?.Cancel();
            if (mini) return;
            scanCts = new CancellationTokenSource();
            _ = ScanNowPlaying(shown.Where(s => s.Kind == "radio" || s.Kind == "garden").Take(300).ToList(), scanCts.Token);
        }

        async Task ScanNowPlaying(List<Station> list, CancellationToken ct)
        {
            var gate = new SemaphoreSlim(10);
            while (!ct.IsCancellationRequested)
            {
                var clock = Stopwatch.StartNew();
                int done = 0, found = 0;
                var tasks = list.Select(async st =>
                {
                    try
                    {
                        await gate.WaitAsync(ct);
                        try
                        {
                            string t = null;
                            try { (t, _) = await Directory.IcyTitle(st.Url, ct, 6); } catch { }
                            if (ct.IsCancellationRequested) return;
                            if (t != null) { st.NowPlaying = t; Interlocked.Increment(ref found); }
                        }
                        finally { gate.Release(); }
                        int d = Interlocked.Increment(ref done);
                        if (!ct.IsCancellationRequested)
                            ListStatus.Text = $"{listStatusBase}  |  titles: {found} found, {d}/{list.Count} checked ({clock.Elapsed.TotalSeconds:0}s)";
                    }
                    catch { }
                }).ToList();
                await Task.WhenAll(tasks);
                try { await Task.Delay(60000, ct); } catch { return; }
            }
        }

        void ShowFavorites()
        {
            showingFavs = true;
            ResetBack();
            FavBtn.Content = "Back to search";
            ShowList(favorites.ToList(), favorites.Count == 0 ? "No favorites yet. Search, then press Save while a station plays." : $"{favorites.Count} favorites. Double-click to play.");
        }

        async Task RunSearch()
        {
            showingFavs = false;
            FavBtn.Content = "My favorites";
            int mine = ++searchSeq;
            ResetBack();
            if (source != 0) { await RunOtherSearch(mine); return; }
            // the blank entry at the top of each list means "any", whatever text the box still shows
            string name = NameBox.Text.Trim();
            string state = StateBox.SelectedIndex == 0 ? "" : StateBox.Text.Trim();
            string tag = GenreBox.SelectedIndex == 0 ? "" : GenreBox.Text.Trim();
            string code = (CountryBox.SelectedItem as CountryItem)?.Code ?? "";
            ListStatus.Text = "Searching...";
            try
            {
                var list = await Directory.Search(name, code, state, tag);
                if (mine != searchSeq) return;  // a newer search superseded this one
                ShowList(list, list.Count == 0 ? "No stations match." : $"{list.Count} stations, most popular first" + (list.Count >= 300 ? " (showing the top 300 - narrow the search to see more)" : "") + ". Double-click to play.");
            }
            catch (Exception e) { if (mine == searchSeq) ListStatus.Text = e.Message; }
        }

        void QueueSearch()
        {
            if (loading) return;
            debounce.Stop();
            debounce.Start();
        }

        // ---------- playback ----------
        void ShowCurrent(Station s, string status)
        {
            current = s;
            NowName.Text = s.Name;
            NowTitle.Text = "";
            NowArtist.Text = "";
            ClearArtist();
            SetArt(s.Favicon);  // the station's logo until a song's cover is found
            NowSub.Text = string.Join("  -  ", new[] { s.CountryShort, s.State, s.Info }.Where(x => !string.IsNullOrWhiteSpace(x)));
            NowStatus.Text = status;
            PlayBtn.Content = playing ? "Stop" : "Play";
            SaveBtn.Content = IsFav(s) ? "★ Favorite" : "☆ Favorite";
        }

        void SetArt(string url)
        {
            try
            {
                if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Scheme.StartsWith("http")) { ArtImage.Source = null; return; }
                var b = new BitmapImage();
                b.BeginInit();
                b.UriSource = uri;
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.EndInit();
                b.DownloadFailed += (s, e) => Dispatcher.Invoke(() => { if (ArtImage.Source == b) ArtImage.Source = null; });
                b.DecodeFailed += (s, e) => Dispatcher.Invoke(() => { if (ArtImage.Source == b) ArtImage.Source = null; });
                ArtImage.Source = b;
            }
            catch { ArtImage.Source = null; }
        }

        string artistUrl = "";

        bool artistHas;

        void ClearArtist()
        {
            artistHas = false;
            ArtistBox.Visibility = Visibility.Collapsed;
            artistUrl = "";
        }

        void Artist_Click(object s, MouseButtonEventArgs e)
        {
            if (artistUrl.Length == 0) return;
            try { Process.Start(new ProcessStartInfo(artistUrl) { UseShellExecute = true }); } catch { }
        }

        async Task ShowArtist(string artist, CancellationToken ct)
        {
            try
            {
                var a = await Directory.Artist(artist, ct);
                if (ct.IsCancellationRequested) return;
                if (a == null || (a.Bio.Length == 0 && a.Meta.Length == 0)) { ClearArtist(); return; }
                ArtistName.Text = a.Name;
                ArtistMeta.Text = a.Meta.Length > 0 ? "   " + a.Meta : "";
                ArtistBio.Text = a.Bio;
                ArtistBio.Visibility = a.Bio.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                artistUrl = a.Url;
                ArtistBox.Cursor = a.Url.Length > 0 ? Cursors.Hand : Cursors.Arrow;
                artistHas = true;
                ArtistBox.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
            }
            catch { }
        }

        // "Artist - Song" from the stream, then album, year and cover from the iTunes catalogue
        async Task ShowTrack(string title, string streamArt, CancellationToken ct)
        {
            string artist = "", song = title;
            int dash = title.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0) { artist = title.Substring(0, dash).Trim(); song = title.Substring(dash + 3).Trim(); }
            NowTitle.Text = song;
            NowArtist.Text = artist;
            if (artist.Length == 0) ClearArtist();
            if (!string.IsNullOrEmpty(streamArt)) SetArt(streamArt);
            if (artist.Length > 0) _ = ShowArtist(artist, ct);
            try
            {
                var info = await Directory.Lookup(artist, song, ct);
                if (ct.IsCancellationRequested) return;
                if (info != null)
                {
                    NowArtist.Text = string.Join("  -  ", new[] { info.Artist, info.Album, info.Year }.Where(x => !string.IsNullOrWhiteSpace(x)));
                    if (string.IsNullOrEmpty(streamArt)) SetArt(info.ArtUrl);
                }
                else if (string.IsNullOrEmpty(streamArt)) SetArt(current?.Favicon);
            }
            catch { }
        }

        async Task TitleLoop(string url, CancellationToken ct)
        {
            string lastTitle = null;
            while (!ct.IsCancellationRequested)
            {
                string title = null, art = null;
                try { (title, art) = await Directory.IcyTitle(url, ct); } catch { }
                if (ct.IsCancellationRequested) return;
                if (title != null && title != lastTitle) { lastTitle = title; await ShowTrack(title, art, ct); }
                try { await Task.Delay(title == null ? 60000 : 15000, ct); } catch { return; }
            }
        }

        void Play(Station s)
        {
            if (Sources.IsContainer(s)) { Open(s); return; }   // a podcast, collection or place: show what's inside
            StopAll();
            playing = true;
            ShowCurrent(s, "Connecting...");
            bool recorded = s.Kind == "episode";
            if (recorded) { NowTitle.Text = s.Show; }       // episode: the show's name under the episode title
            else
            {
                titleCts = new CancellationTokenSource();
                _ = TitleLoop(s.Url, titleCts.Token);
            }
            try
            {
                player.Source = MediaSource.CreateFromUri(new Uri(s.Url));
                player.Volume = VolSlider.Value;
                player.Play();
            }
            catch (Exception e) { OnFailed(e.Message); }
            if (s.Kind == "radio") Directory.Click(s.Uuid);
        }

        void OnFailed(string why)
        {
            if (!playing || current == null) return;
            if (ffplay == null && FindFfplay() != null) { StartFfplay(current); return; }
            NowStatus.Text = "Can't play this station" + (string.IsNullOrEmpty(why) ? "" : " (" + why + ")")
                + (ffplay == null && FindFfplay() == null ? ". Some stations need ffmpeg: install it with  winget install ffmpeg  and restart the app." : "");
        }

        static string FindFfplay()
        {
            string beside = Path.Combine(AppContext.BaseDirectory, "ffplay.exe");   // a copy dropped next to the app works too
            if (File.Exists(beside)) return beside;
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try { string p = Path.Combine(dir.Trim(), "ffplay.exe"); if (File.Exists(p)) return p; } catch { }
            }
            string link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "ffplay.exe");
            return File.Exists(link) ? link : null;
        }

        void StartFfplay(Station s)
        {
            KillFfplay();
            string exe = FindFfplay();
            if (exe == null) return;
            var psi = new ProcessStartInfo(exe, $"-nodisp -loglevel quiet -volume {(int)(VolSlider.Value * 100)} \"{s.Url}\"")
            { CreateNoWindow = true, UseShellExecute = false };
            try { ffplay = Process.Start(psi); NowStatus.Text = "Playing (compatibility mode)"; }
            catch (Exception e) { NowStatus.Text = "Can't play this station (" + e.Message + ")"; }
        }

        void KillFfplay()
        {
            try { if (ffplay != null && !ffplay.HasExited) ffplay.Kill(); } catch { }
            ffplay = null;
        }

        void StopAll()
        {
            playing = false;
            titleCts?.Cancel();
            NowTitle.Text = "";
            NowArtist.Text = "";
            ClearArtist();
            KillFfplay();
            try { player.Pause(); player.Source = null; } catch { }
        }

        void Step(int d)
        {
            if (shown.Count == 0 || shown.All(Sources.IsContainer)) return;
            int i = current == null ? -1 : shown.FindIndex(s => s.Url == current.Url);
            i = i < 0 ? (d > 0 ? 0 : shown.Count - 1) : (i + d + shown.Count) % shown.Count;
            while (Sources.IsContainer(shown[i])) i = (i + d + shown.Count) % shown.Count;   // skip shows and places
            Results.SelectedIndex = i;
            Results.ScrollIntoView(shown[i]);
            Play(shown[i]);
        }

        // ---------- events ----------
        void Prev_Click(object s, RoutedEventArgs e) => Step(-1);
        void Next_Click(object s, RoutedEventArgs e) => Step(1);

        readonly Random rng = new Random();
        void Random_Click(object s, RoutedEventArgs e)
        {
            if (shown.Count == 0 || shown.All(Sources.IsContainer)) return;
            int i;
            do i = rng.Next(shown.Count); while (Sources.IsContainer(shown[i]) || (shown.Count > 1 && current != null && shown[i].Url == current.Url));
            Results.SelectedIndex = i;
            Results.ScrollIntoView(shown[i]);
            Play(shown[i]);
        }

        void PlayStop_Click(object s, RoutedEventArgs e)
        {
            if (playing) { StopAll(); NowStatus.Text = "Stopped"; PlayBtn.Content = "Play"; }
            else if (current != null) Play(current);
        }

        void Save_Click(object s, RoutedEventArgs e) => ToggleFav(current);

        void ToggleFav(Station st)
        {
            if (st == null) return;
            var existing = favorites.FirstOrDefault(f => f.Url == st.Url);
            if (existing != null) favorites.Remove(existing); else favorites.Add(st);
            SaveFavorites();
            if (current != null) SaveBtn.Content = IsFav(current) ? "★ Favorite" : "☆ Favorite";
            if (showingFavs) ShowFavorites();
            else { foreach (var r in shown) r.IsFav = IsFav(r); Results.Items.Refresh(); }
        }

        // right-click menu on a table row
        void Results_RightDown(object s, MouseButtonEventArgs e)
        {
            var item = ItemsControl.ContainerFromElement(Results, e.OriginalSource as DependencyObject) as ListViewItem;
            if (item == null) { e.Handled = true; return; }   // not on a row: no menu
            item.IsSelected = true;
            if (item.Content is Station st) CtxFav.Header = IsFav(st) ? "Remove from favorites" : "Add to favorites";
        }
        void Ctx_Play(object s, RoutedEventArgs e) { if (Results.SelectedItem is Station st) Play(st); }
        void Ctx_Fav(object s, RoutedEventArgs e) => ToggleFav(Results.SelectedItem as Station);

        void Favs_Click(object s, RoutedEventArgs e)
        {
            if (showingFavs) _ = RunSearch(); else ShowFavorites();
        }

        void Search_Click(object s, RoutedEventArgs e) => _ = RunSearch();

        void Clear_Click(object s, RoutedEventArgs e)
        {
            debounce.Stop();
            loading = true;  // resetting the boxes must not trigger a search of its own
            NameBox.Text = "";
            StateBox.SelectedIndex = -1;
            StateBox.Text = "";
            GenreBox.SelectedIndex = -1;
            GenreBox.Text = "";
            loading = false;
            _ = RunSearch();
        }

        void NameBox_KeyDown(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { debounce.Stop(); _ = RunSearch(); }
        }

        void Country_Changed(object s, SelectionChangedEventArgs e)
        {
            if (loading) return;
            FillStates();
            QueueSearch();
        }

        void Filter_Changed(object s, SelectionChangedEventArgs e) => QueueSearch();

        void Results_DoubleClick(object s, MouseButtonEventArgs e)
        {
            if (Results.SelectedItem is Station st) Play(st);
        }

        void Results_KeyDown(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Back && BackBtn.Visibility == Visibility.Visible) Back_Click(null, null);
            else if (e.Key == Key.Enter && Results.SelectedItem is Station st) Play(st);
        }

        void Vol_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (player == null) return;
            player.Volume = e.NewValue;
            if (ffplay != null) { volRestart.Stop(); volRestart.Start(); }  // ffplay can't change volume live
        }
    }
}
