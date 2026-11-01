using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Stillwave
{
    // Extra places to find things to play: Radio Garden, the Internet Archive and podcasts,
    // plus helpers for playlists and for naming a stream someone pasted in.
    public static class Sources
    {
        static readonly HttpClient Http = Make();
        static HttpClient Make()
        {
            var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(25) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Stillwave/1.0");
            return c;
        }

        static string S(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

        static async Task<JsonElement> Json(string url, CancellationToken ct = default)
        {
            string text = await Http.GetStringAsync(url, ct);
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        public static bool IsContainer(Station s) => s.Kind == "podcast" || s.Kind == "archive" || s.Kind == "place";

        // ---------- Radio Garden ----------
        const string Garden = "https://radio.garden/api";
        static List<(string Id, string Title, string Country)> places;

        static string LastSegment(string path) => (path ?? "").TrimEnd('/').Split('/').LastOrDefault() ?? "";

        static Station GardenChannel(string id, string title, string place, string country) => new Station
        {
            Kind = "garden", Name = title, Url = $"{Garden}/ara/content/listen/{id}/channel.mp3",
            Country = country, State = place, Codec = "Garden",
        };

        public static async Task<List<Station>> GardenSearch(string q, CancellationToken ct = default)
        {
            var root = await Json($"{Garden}/search?q=" + Uri.EscapeDataString(q), ct);
            var list = new List<Station>();
            foreach (var hit in root.GetProperty("hits").GetProperty("hits").EnumerateArray())
            {
                if (!hit.TryGetProperty("_source", out var src) || !src.TryGetProperty("page", out var page)) continue;
                string type = S(src, "type"), id = LastSegment(S(page, "url")), title = S(page, "title");
                if (id.Length == 0 || title.Length == 0) continue;
                if (type == "channel")
                {
                    string place = page.TryGetProperty("place", out var pl) ? S(pl, "title") : "";
                    string country = page.TryGetProperty("country", out var co) ? S(co, "title") : "";
                    list.Add(GardenChannel(id, title, place, country));
                }
                else if (type == "place")
                    list.Add(new Station { Kind = "place", Name = title + "  (place)", Url = "garden-place:" + id, Country = S(page, "subtitle"), State = title, Codec = "Place" });
            }
            return list;
        }

        public static async Task<List<Station>> PlaceChannels(string placeId, string placeTitle, string country, CancellationToken ct = default)
        {
            var root = await Json($"{Garden}/ara/content/page/{placeId}/channels", ct);
            var list = new List<Station>();
            foreach (var block in root.GetProperty("data").GetProperty("content").EnumerateArray())
            {
                if (!block.TryGetProperty("items", out var items)) continue;
                foreach (var it in items.EnumerateArray())
                {
                    if (!it.TryGetProperty("page", out var page)) continue;
                    string id = LastSegment(S(page, "url")), title = S(page, "title");
                    if (id.Length > 0 && title.Length > 0 && !list.Any(x => x.Name == title)) list.Add(GardenChannel(id, title, placeTitle, country));
                }
            }
            return list;
        }

        // a random place on the map that has at least one station
        public static async Task<(string Id, string Title, string Country)> RandomPlace(CancellationToken ct = default)
        {
            if (places == null)
            {
                var root = await Json($"{Garden}/ara/content/places", ct);
                places = root.GetProperty("data").GetProperty("list").EnumerateArray()
                    .Where(p => p.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number && sz.GetInt32() > 0)
                    .Select(p => (S(p, "id"), S(p, "title"), S(p, "country"))).ToList();
            }
            return places[Random.Shared.Next(places.Count)];
        }

        // ---------- Internet Archive ----------
        public static readonly (string Name, string Id)[] ArchiveSets =
        {
            ("Old-time radio shows", "oldtimeradio"),
            ("Live concerts", "etree"),
            ("Audiobooks (LibriVox)", "librivoxaudio"),
            ("78 rpm records", "78rpm"),
        };

        public static async Task<List<Station>> ArchiveSearch(string collection, string q, CancellationToken ct = default)
        {
            string query = $"collection:({collection}) AND mediatype:(audio)";
            if (q.Length > 0) query += $" AND (title:({q.Replace("\"", " ")}) OR creator:({q.Replace("\"", " ")}))";
            string url = "https://archive.org/advancedsearch.php?q=" + Uri.EscapeDataString(query)
                + "&fl[]=identifier&fl[]=title&fl[]=creator&sort[]=downloads+desc&rows=100&output=json";
            var root = await Json(url, ct);
            var list = new List<Station>();
            foreach (var d in root.GetProperty("response").GetProperty("docs").EnumerateArray())
            {
                string id = S(d, "identifier");
                if (id.Length == 0) continue;
                string creator = d.TryGetProperty("creator", out var c) ? (c.ValueKind == JsonValueKind.Array ? string.Join(", ", c.EnumerateArray().Select(x => x.GetString())) : c.GetString()) : "";
                list.Add(new Station
                {
                    Kind = "archive", Name = S(d, "title").Length > 0 ? S(d, "title") : id, Url = "archive:" + id, Tags = creator ?? "", Codec = "Archive",
                    Favicon = "https://archive.org/services/img/" + id,
                });
            }
            return list;
        }

        public static async Task<List<Station>> ArchiveFiles(Station item, CancellationToken ct = default)
        {
            string id = item.Url.Substring("archive:".Length);
            var root = await Json("https://archive.org/metadata/" + Uri.EscapeDataString(id), ct);
            var list = new List<Station>();
            var seen = new HashSet<string>();
            if (!root.TryGetProperty("files", out var files)) return list;
            foreach (var f in files.EnumerateArray())
            {
                string name = S(f, "name"), fmt = S(f, "format");
                if (!name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || !fmt.Contains("MP3", StringComparison.OrdinalIgnoreCase)) continue;
                string baseName = System.IO.Path.GetFileNameWithoutExtension(name);
                if (!seen.Add(baseName)) continue;   // the same recording at a second bitrate
                string title = S(f, "title");
                list.Add(new Station
                {
                    Kind = "episode", Name = title.Length > 0 ? title : baseName.Replace('_', ' '),
                    Url = $"https://archive.org/download/{Uri.EscapeDataString(id)}/" + string.Join("/", name.Split('/').Select(Uri.EscapeDataString)),
                    Show = item.Name, Codec = "Archive", Favicon = item.Favicon,
                });
                if (list.Count >= 500) break;
            }
            return list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ---------- podcasts (Apple's free directory + each show's own feed) ----------
        static Station PodcastRow(JsonElement r) => new Station
        {
            Kind = "podcast", Name = S(r, "collectionName"), Url = S(r, "feedUrl"), State = S(r, "artistName"),
            Tags = S(r, "primaryGenreName"), Country = "", Codec = "Podcast",
            Uuid = r.TryGetProperty("collectionId", out var cid) && cid.ValueKind == JsonValueKind.Number ? cid.GetInt64().ToString() : "",
            Favicon = S(r, "artworkUrl600").Length > 0 ? S(r, "artworkUrl600") : S(r, "artworkUrl100"),
        };

        public static async Task<List<Station>> PodcastSearch(string q, CancellationToken ct = default)
        {
            var root = await Json("https://itunes.apple.com/search?media=podcast&limit=50&term=" + Uri.EscapeDataString(q), ct);
            return root.GetProperty("results").EnumerateArray().Select(PodcastRow).Where(p => p.Url.Length > 0).ToList();
        }

        public static async Task<List<Station>> PodcastTop(CancellationToken ct = default)
        {
            var top = await Json("https://itunes.apple.com/us/rss/toppodcasts/limit=50/json", ct);
            var ids = top.GetProperty("feed").GetProperty("entry").EnumerateArray()
                .Select(e => e.GetProperty("id").GetProperty("attributes").GetProperty("im:id").GetString()).ToList();
            var root = await Json("https://itunes.apple.com/lookup?entity=podcast&id=" + string.Join(",", ids), ct);
            var rows = root.GetProperty("results").EnumerateArray().Select(PodcastRow).Where(p => p.Url.Length > 0).ToList();
            return rows.OrderBy(r => ids.IndexOf(r.Uuid)).ToList();   // keep the chart order
        }

        public static async Task<List<Station>> PodcastEpisodes(Station show, CancellationToken ct = default)
        {
            string xml = await Http.GetStringAsync(show.Url, ct);
            var doc = XDocument.Parse(xml);
            var list = new List<Station>();
            foreach (var item in doc.Descendants("item"))
            {
                var enc = item.Element("enclosure");
                string url = (string)enc?.Attribute("url");
                if (string.IsNullOrEmpty(url)) continue;
                string date = "";
                if (DateTimeOffset.TryParse((string)item.Element("pubDate"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) date = dt.ToString("yyyy-MM-dd");
                list.Add(new Station
                {
                    Kind = "episode", Name = ((string)item.Element("title") ?? "Episode").Trim(), Url = url, State = date,
                    Show = show.Name, Codec = "Episode", Favicon = show.Favicon,
                });
                if (list.Count >= 200) break;
            }
            return list;
        }

        // ---------- your own stations ----------
        public static List<(string Name, string Url)> ParsePlaylist(string text)
        {
            var list = new List<(string, string)>();
            var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Any(l => l.StartsWith("[playlist]", StringComparison.OrdinalIgnoreCase)))   // PLS
            {
                var urls = new SortedDictionary<int, string>(); var names = new Dictionary<int, string>();
                foreach (var l in lines)
                {
                    var m = Regex.Match(l, "^(File|Title)(\\d+)=(.*)$", RegexOptions.IgnoreCase);
                    if (!m.Success) continue;
                    int n = int.Parse(m.Groups[2].Value);
                    if (m.Groups[1].Value.Equals("File", StringComparison.OrdinalIgnoreCase)) urls[n] = m.Groups[3].Value.Trim(); else names[n] = m.Groups[3].Value.Trim();
                }
                foreach (var kv in urls) list.Add((names.TryGetValue(kv.Key, out var nm) ? nm : "", kv.Value));
            }
            else                                                                                     // M3U
            {
                string pending = "";
                foreach (var l in lines)
                {
                    if (l.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase)) { int c = l.IndexOf(','); pending = c >= 0 ? l.Substring(c + 1).Trim() : ""; }
                    else if (!l.StartsWith("#")) { list.Add((pending, l)); pending = ""; }
                }
            }
            return list.Where(x => x.Item2.StartsWith("http", StringComparison.OrdinalIgnoreCase)).Take(200).ToList();
        }

        // a playlist link points at a list of streams, not at a stream
        public static async Task<List<(string Name, string Url)>> ResolveLink(string url, CancellationToken ct = default)
        {
            string path = url.Split('?')[0].ToLowerInvariant();
            if (path.EndsWith(".pls") || path.EndsWith(".m3u"))
                return ParsePlaylist(await Http.GetStringAsync(url, ct));
            return new List<(string, string)> { ("", url) };
        }

        // what the stream calls itself, when it says
        public static async Task<string> StreamName(string url, CancellationToken ct = default)
        {
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(TimeSpan.FromSeconds(6));
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("Icy-MetaData", "1");
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, limit.Token);
                if (resp.Headers.TryGetValues("icy-name", out var v)) { string n = v.FirstOrDefault()?.Trim(); if (!string.IsNullOrEmpty(n)) return n; }
            }
            catch { }
            return null;
        }
    }
}
