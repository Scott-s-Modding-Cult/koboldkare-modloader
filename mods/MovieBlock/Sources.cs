using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace MovieBlock {
    internal class Post {
        public long Id;
        public string PreviewUrl;
        public string VideoUrl;
        public string Extension;
        public double Duration;     // seconds, 0 if unknown
        public long Size;           // bytes of VideoUrl, 0 if unknown
        public int Score;
        public int Width;
        public int Height;
        public List<string> Tags = new List<string>();
        public string PageUrl;
    }

    internal class SearchResult {
        public List<Post> Posts = new List<Post>();
        public int Hidden;          // dropped: no playable file or a blocked tag
        public bool LastPage;
        public string Error;
    }

    internal struct TagSuggestion {
        public string Name;
        public int Count;
    }

    /// <summary>A booru that can be searched for videos.</summary>
    internal abstract class Source {
        public const int PageSize = 30;

        // Always excluded, on both sites: content involving minors. Not configurable.
        protected static readonly string[] HardBlocked = { "young", "loli", "shota", "cub", "child", "toddler" };

        public abstract string Key { get; }          // "e621" / "r34"
        public abstract string Title { get; }
        public abstract string[] Sorts { get; }      // labels; index 0 = newest
        public abstract string DefaultTags { get; }

        /// <summary>Null when the source is usable, otherwise what the user has to do first.</summary>
        public virtual string SetupProblem => null;

        public abstract void Search(string tags, int sort, int page, Action<SearchResult> done);
        public abstract void Autocomplete(string prefix, Action<List<TagSuggestion>> done);

        protected static IEnumerable<string> UserBlacklist() =>
            (MovieBlockPlugin.Blacklist.Value ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
                                                   .Select(t => t.Trim().ToLowerInvariant().TrimStart('-'))
                                                   .Where(t => t.Length > 0);

        protected static bool IsBlocked(Post post, HashSet<string> blocked) => post.Tags.Any(blocked.Contains);

        protected static HashSet<string> BlockedSet() {
            var set = new HashSet<string>(HardBlocked);
            foreach (var t in UserBlacklist()) {
                set.Add(t);
            }
            return set;
        }

        /// <summary>The user's tags plus exclusions, de-duplicated, ready for the query string.</summary>
        protected static string BuildQuery(string userTags, IEnumerable<string> extra) {
            var words = new List<string>();
            foreach (var w in (userTags ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) {
                if (!words.Contains(w)) {
                    words.Add(w);
                }
            }
            foreach (var w in extra) {
                if (!words.Contains(w)) {
                    words.Add(w);
                }
            }
            foreach (var t in HardBlocked.Concat(UserBlacklist())) {
                if (!words.Contains("-" + t)) {
                    words.Add("-" + t);
                }
            }
            return Uri.EscapeDataString(string.Join(" ", words.ToArray()));
        }

        protected static string Str(JToken token, params string[] path) {
            foreach (var p in path) {
                if (!(token is JObject obj)) {
                    return null;
                }
                token = obj[p];
            }
            return token == null || token.Type == JTokenType.Null ? null : token.ToString();
        }

        protected static double Num(JToken token, params string[] path) {
            string s = Str(token, path);
            return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0;
        }

        protected static bool IsVideoExt(string ext) => ext == "mp4" || ext == "webm";

        /// <summary>
        /// True for an https URL on <paramref name="domain"/> or one of its subdomains. Downloads and thumbnails only
        /// come from the site itself, so a bad API reply can't send us to some other server or a local file.
        /// </summary>
        public static bool IsTrusted(string url, string domain) {
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) {
                return false;
            }
            string host = uri.Host.ToLowerInvariant();
            return host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
        }

        protected static string ExtOf(string url) {
            if (string.IsNullOrEmpty(url)) {
                return "";
            }
            int q = url.IndexOf('?');
            if (q >= 0) {
                url = url.Substring(0, q);
            }
            int dot = url.LastIndexOf('.');
            return dot >= 0 ? url.Substring(dot + 1).ToLowerInvariant() : "";
        }
    }

    // ---------------------------------------------------------------------------------------- e621

    internal class E621Source : Source {
        public override string Key => "e621";
        public override string Title => "e621";
        public override string[] Sorts => new[] { "Newest", "Top score", "Most faved", "Random" };
        public override string DefaultTags => MovieBlockPlugin.E621DefaultTags.Value;

        private static readonly string[] SortTags = { null, "order:score", "order:favcount", "order:random" };

        private static Dictionary<string, string> Headers() {
            string user = MovieBlockPlugin.E621User.Value?.Trim();
            string key = MovieBlockPlugin.E621ApiKey.Value?.Trim();
            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(key)) {
                return null;
            }
            return new Dictionary<string, string> {
                ["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + key)),
            };
        }

        public override void Search(string tags, int sort, int page, Action<SearchResult> done) {
            var extra = new List<string> { "-type:jpg", "-type:png", "-type:gif", "-type:swf" };
            if (sort > 0 && sort < SortTags.Length) {
                extra.Add(SortTags[sort]);
            }
            string url = $"https://e621.net/posts.json?limit={PageSize}&page={page + 1}&tags={BuildQuery(tags, extra)}";
            string quality = MovieBlockPlugin.Quality.Value;
            var blocked = BlockedSet();
            RateLimit.Run(() => Net.Get(url, bytes => Parse(Net.Text(bytes), quality, blocked), done,
                error => done(new SearchResult { Error = error, LastPage = true }), Headers()));
        }

        private static SearchResult Parse(string json, string quality, HashSet<string> blocked) {
            var result = new SearchResult();
            var posts = JObject.Parse(json)["posts"] as JArray;
            if (posts == null) {
                result.LastPage = true;
                return result;
            }
            result.LastPage = posts.Count < PageSize;
            foreach (var p in posts) {
                var post = new Post {
                    Id = (long)Num(p, "id"),
                    Extension = Str(p, "file", "ext") ?? "",
                    PreviewUrl = Str(p, "preview", "url"),
                    Duration = Num(p, "duration"),
                    Score = (int)Num(p, "score", "total"),
                    Width = (int)Num(p, "file", "width"),
                    Height = (int)Num(p, "file", "height"),
                };
                post.PageUrl = "https://e621.net/posts/" + post.Id;
                if (p["tags"] is JObject tagGroups) {
                    // Most useful first: artist, character, species, copyright, then the rest.
                    foreach (var group in new[] { "artist", "character", "species", "copyright", "general", "lore", "meta" }) {
                        if (tagGroups[group] is JArray list) {
                            post.Tags.AddRange(list.Select(t => t.ToString()));
                        }
                    }
                }
                post.VideoUrl = PickVideo(p, quality);
                post.Size = SizeOf(p, post.VideoUrl);
                if (!IsTrusted(post.VideoUrl, "e621.net")) {
                    post.VideoUrl = null;
                }
                if (!IsTrusted(post.PreviewUrl, "e621.net")) {
                    post.PreviewUrl = null;
                }
                if (!IsVideoExt(post.Extension) || post.VideoUrl == null || IsBlocked(post, blocked)) {
                    result.Hidden++;
                    continue;
                }
                result.Posts.Add(post);
            }
            return result;
        }

        /// <summary>
        /// Unity plays H.264 MP4 and VP8 WebM, not VP9 (most e621 originals). e621 transcodes videos to
        /// 480p/720p MP4 samples and a full-size MP4 variant, so prefer those.
        /// </summary>
        private static string PickVideo(JToken p, string quality) {
            var alt = p["sample"]?["alternates"] as JObject;
            string s480 = Str(alt, "samples", "480p", "url");
            string s720 = Str(alt, "samples", "720p", "url");
            string full = Str(alt, "variants", "mp4", "url");
            string original = Str(p, "file", "url");
            string ext = Str(p, "file", "ext");
            string codec = (Str(alt, "original", "codec") ?? "").ToLowerInvariant();
            string playableOriginal = original != null && (ext == "mp4" || (ext == "webm" && codec.StartsWith("vp8"))) ? original : null;

            // Older API shape: alternates["720p"] = { type, urls: [webm, mp4] }.
            if (s480 == null && s720 == null && full == null && alt != null) {
                s480 = OldShape(alt, "480p");
                s720 = OldShape(alt, "720p");
            }

            IEnumerable<string> order;
            switch ((quality ?? "").ToLowerInvariant()) {
                case "480p":
                    order = new[] { s480, s720, full, playableOriginal };
                    break;
                case "original":
                    order = new[] { playableOriginal, full, s720, s480 };
                    break;
                default:
                    order = new[] { s720, s480, full, playableOriginal };
                    break;
            }
            return order.FirstOrDefault(u => !string.IsNullOrEmpty(u));
        }

        /// <summary>File size e621 lists for whichever URL was picked (for the download percentage).</summary>
        private static long SizeOf(JToken p, string url) {
            if (url == null) {
                return 0;
            }
            var alt = p["sample"]?["alternates"] as JObject;
            foreach (var path in new[] {
                         new[] { "samples", "480p" }, new[] { "samples", "720p" }, new[] { "variants", "mp4" }, new[] { "original" },
                     }) {
                JToken node = alt;
                foreach (var key in path) {
                    node = node is JObject o ? o[key] : null;
                }
                if (Str(node, "url") == url) {
                    return (long)Num(node, "size");
                }
            }
            return Str(p, "file", "url") == url ? (long)Num(p, "file", "size") : 0;
        }

        private static string OldShape(JObject alt, string key) {
            if (alt[key]?["urls"] is JArray urls) {
                return urls.Select(u => u.Type == JTokenType.Null ? null : u.ToString())
                           .FirstOrDefault(u => u != null && u.EndsWith(".mp4"));
            }
            return null;
        }

        public override void Autocomplete(string prefix, Action<List<TagSuggestion>> done) {
            if (prefix.Length < 3) {
                done(new List<TagSuggestion>());
                return;
            }
            string url = "https://e621.net/tags/autocomplete.json?search%5Bname_matches%5D=" + Uri.EscapeDataString(prefix) + "&expiry=7";
            RateLimit.Run(() => Net.Get(url, bytes => {
                var list = new List<TagSuggestion>();
                if (JToken.Parse(Net.Text(bytes)) is JArray array) {
                    foreach (var t in array) {
                        list.Add(new TagSuggestion { Name = Str(t, "name"), Count = (int)Num(t, "post_count") });
                    }
                }
                return list;
            }, done, _ => done(new List<TagSuggestion>()), Headers()));
        }
    }

    // -------------------------------------------------------------------------------- rule34.xxx

    internal class Rule34Source : Source {
        public override string Key => "r34";
        public override string Title => "rule34";
        public override string[] Sorts => new[] { "Newest", "Top score" };
        public override string DefaultTags => MovieBlockPlugin.R34DefaultTags.Value;

        private static readonly string[] SortTags = { null, "sort:score:desc" };

        public override string SetupProblem =>
            string.IsNullOrEmpty(MovieBlockPlugin.R34UserId.Value?.Trim()) || string.IsNullOrEmpty(MovieBlockPlugin.R34ApiKey.Value?.Trim())
                ? "rule34.xxx needs a free API key.\n\n" +
                  "1. Log in at rule34.xxx, open My Account > Options.\n" +
                  "2. Under API Access Credentials, tick Generate New Key and Save.\n" +
                  "3. Copy the &api_key=...&user_id=... text, then in chat type:\n" +
                  "   /movieblock r34key <paste it here>"
                : null;

        public override void Search(string tags, int sort, int page, Action<SearchResult> done) {
            if (SetupProblem != null) {
                done(new SearchResult { Error = SetupProblem, LastPage = true });
                return;
            }
            var extra = new List<string> { "video" };
            if (sort > 0 && sort < SortTags.Length) {
                extra.Add(SortTags[sort]);
            }
            string url = "https://api.rule34.xxx/index.php?page=dapi&s=post&q=index&json=1" +
                         $"&limit={PageSize}&pid={page}&tags={BuildQuery(tags, extra)}" +
                         "&api_key=" + Uri.EscapeDataString(MovieBlockPlugin.R34ApiKey.Value.Trim()) +
                         "&user_id=" + Uri.EscapeDataString(MovieBlockPlugin.R34UserId.Value.Trim());
            var blocked = BlockedSet();
            Net.Get(url, bytes => Parse(Net.Text(bytes), blocked), done,
                error => done(new SearchResult { Error = error, LastPage = true }));
        }

        private static SearchResult Parse(string text, HashSet<string> blocked) {
            var result = new SearchResult();
            text = text.Trim();
            if (text.Length == 0) {
                result.LastPage = true; // no results: the API sends an empty body
                return result;
            }
            if (text.StartsWith("<")) {
                // XML error, e.g. <response success="false" reason="..."/>
                int r = text.IndexOf("reason=\"", StringComparison.Ordinal);
                result.Error = r >= 0 ? text.Substring(r + 8).Split('"')[0] : "rule34 sent an unexpected reply.";
                result.LastPage = true;
                return result;
            }
            var token = JToken.Parse(text);
            if (token.Type == JTokenType.String) {
                result.Error = token.ToString(); // e.g. "Missing authentication..."
                result.LastPage = true;
                return result;
            }
            if (!(token is JArray posts)) {
                result.LastPage = true;
                return result;
            }
            result.LastPage = posts.Count < PageSize;
            foreach (var p in posts) {
                string file = Str(p, "file_url");
                var post = new Post {
                    Id = (long)Num(p, "id"),
                    VideoUrl = file,
                    Extension = ExtOf(file),
                    PreviewUrl = Str(p, "preview_url"),
                    Score = (int)Num(p, "score"),
                    Width = (int)Num(p, "width"),
                    Height = (int)Num(p, "height"),
                };
                post.PageUrl = "https://rule34.xxx/index.php?page=post&s=view&id=" + post.Id;
                post.Tags.AddRange((Str(p, "tags") ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
                if (!IsTrusted(post.PreviewUrl, "rule34.xxx")) {
                    post.PreviewUrl = null;
                }
                if (!IsVideoExt(post.Extension) || !IsTrusted(post.VideoUrl, "rule34.xxx") || IsBlocked(post, blocked)) {
                    result.Hidden++;
                    continue;
                }
                result.Posts.Add(post);
            }
            return result;
        }

        public override void Autocomplete(string prefix, Action<List<TagSuggestion>> done) {
            if (prefix.Length < 2) {
                done(new List<TagSuggestion>());
                return;
            }
            string url = "https://api.rule34.xxx/autocomplete.php?q=" + Uri.EscapeDataString(prefix);
            Net.Get(url, bytes => {
                var list = new List<TagSuggestion>();
                if (JToken.Parse(Net.Text(bytes)) is JArray array) {
                    foreach (var t in array) {
                        // label: "kobold (27254)"
                        string label = Str(t, "label") ?? "";
                        int open = label.LastIndexOf('(');
                        int.TryParse(open >= 0 ? label.Substring(open + 1).TrimEnd(')') : "", out int count);
                        list.Add(new TagSuggestion { Name = Str(t, "value"), Count = count });
                    }
                }
                return list;
            }, done, _ => done(new List<TagSuggestion>()));
        }
    }

    /// <summary>e621 allows about one API request per second; space them out.</summary>
    internal static class RateLimit {
        private static readonly Queue<Action> Pending = new Queue<Action>();
        private static float next;

        public static void Run(Action request) => Pending.Enqueue(request);

        /// <summary>Main thread, every frame.</summary>
        public static void Pump(float now) {
            if (Pending.Count == 0 || now < next) {
                return;
            }
            next = now + 1.05f;
            Pending.Dequeue()();
        }

        public static void Clear() => Pending.Clear();
    }
}
