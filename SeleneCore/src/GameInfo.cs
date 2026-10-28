using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Threading;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace Selene
{
    // Completes what games are missing. Playnite downloads metadata only for games a store add-on imports, so games added by
    // hand or by a script stay bare (no description, genres, cover...). A while after the start, every game missing its main
    // details gets each empty field filled from the metadata add-ons, in the order Playnite's own metadata settings give for
    // that field (IGDB for the words, SteamGridDB for the pictures, say). Nothing already there is replaced. A game is tried
    // again two weeks later at the soonest (metadata-tried-2.txt). What the add-ons leave empty (IGDB finds nothing for a game
    // it can't match on its own) comes from the Steam store when a game there has exactly the same name.
    internal static class GameInfo
    {
        private const int PerStart = 120;   // games looked up per start at most (the rest next time)
        private static string TriedFile { get { return Path.Combine(SelenePaths.DataFolder, "metadata-tried-2.txt"); } }   // (2: every game once more, with the Steam store as well)

        public static void CompleteLater()
        {
            if (!DownloadConsent.Allowed("metadatacomplete")) return;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(75) };
            t.Tick += (s, e) => { t.Stop(); Start(); };
            t.Start();
        }

        // a game missing its main details
        private static bool Bare(Game g)
        {
            return string.IsNullOrWhiteSpace(g.Description) || string.IsNullOrEmpty(g.CoverImage) || string.IsNullOrEmpty(g.BackgroundImage)
                || g.GenreIds == null || g.GenreIds.Count == 0 || !g.ReleaseDate.HasValue || g.DeveloperIds == null || g.DeveloperIds.Count == 0;
        }

        private static void Start()
        {
            if (!DownloadConsent.Allowed("metadatacomplete")) return;
            try
            {
                var api = HomeNav.Api;
                if (api == null) return;
                var plugins = api.Addons.Plugins.OfType<MetadataPlugin>().ToList();
                if (plugins.Count == 0) { HomeNav.Log("game info: no metadata add-ons"); return; }
                var tried = ReadTried();
                var due = api.Database.Games.Where(g => g != null && !string.IsNullOrEmpty(g.Name) && Bare(g))
                    .Where(g => { DateTime when; return !tried.TryGetValue(g.Id, out when) || (DateTime.UtcNow - when).TotalDays >= 14; })
                    .OrderByDescending(g => g.IsInstalled).ThenByDescending(g => g.LastActivity ?? DateTime.MinValue)
                    .Take(PerStart).Select(g => g.Id).ToList();
                HomeNav.Log("game info: " + due.Count + " games missing details");
                if (due.Count == 0) return;
                var order = SourceOrder(api, plugins);
                var ui = Dispatcher.CurrentDispatcher;
                Task.Factory.StartNew(() =>
                {
                    int filled = 0;
                    foreach (var id in due)
                    {
                        if (!DownloadConsent.Allowed("metadatacomplete")) break;
                        Game game = null;
                        ui.Invoke(new Action(() => game = api.Database.Games.Get(id)));
                        if (game == null) continue;
                        try { if (Fill(api, game, order, ui)) filled++; }
                        catch (Exception ex) { HomeNav.Log("game info: " + game.Name + ": " + ex.Message); }
                        tried[id] = DateTime.UtcNow;
                    }
                    WriteTried(tried);
                    int done = filled;
                    ui.BeginInvoke(new Action(() =>
                    {
                        HomeNav.Log("game info: filled in " + done + " of " + due.Count);
                    }));
                });
            }
            catch (Exception ex) { HomeNav.Log("game info: " + ex.Message); }
        }

        // each field's metadata add-ons, in Playnite's order for it (its metadata settings); the store's own ("none") left out
        private static Dictionary<MetadataField, List<MetadataPlugin>> SourceOrder(Playnite.SDK.IPlayniteAPI api, List<MetadataPlugin> plugins)
        {
            var keys = new Dictionary<string, MetadataField>
            {
                { "Genre", MetadataField.Genres }, { "ReleaseDate", MetadataField.ReleaseDate }, { "Developer", MetadataField.Developers },
                { "Publisher", MetadataField.Publishers }, { "Tag", MetadataField.Tags }, { "Feature", MetadataField.Features },
                { "Description", MetadataField.Description }, { "CoverImage", MetadataField.CoverImage }, { "BackgroundImage", MetadataField.BackgroundImage },
                { "Icon", MetadataField.Icon }, { "Links", MetadataField.Links }, { "CriticScore", MetadataField.CriticScore },
                { "CommunityScore", MetadataField.CommunityScore }, { "AgeRating", MetadataField.AgeRating }, { "Series", MetadataField.Series }
            };
            var order = new Dictionary<MetadataField, List<MetadataPlugin>>();
            try
            {
                var config = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                    .DeserializeObject(File.ReadAllText(Path.Combine(api.Paths.ConfigurationPath, "config.json"))) as Dictionary<string, object>;
                object m; config.TryGetValue("MetadataSettings", out m);
                var settings = m as Dictionary<string, object>;
                foreach (var k in keys)
                {
                    object f; if (settings == null || !settings.TryGetValue(k.Key, out f)) continue;
                    var field = f as Dictionary<string, object>; object src;
                    if (field == null || !field.TryGetValue("Sources", out src)) continue;
                    var list = new List<MetadataPlugin>();
                    foreach (var o in (src as IEnumerable) ?? new object[0])
                    {
                        Guid gid; if (!Guid.TryParse(Convert.ToString(o), out gid) || gid == Guid.Empty) continue;
                        var p = plugins.FirstOrDefault(x => x.Id == gid);
                        if (p != null) list.Add(p);
                    }
                    order[k.Value] = list;
                }
            }
            catch (Exception ex) { HomeNav.Log("game info: metadata settings: " + ex.Message); }
            // a field Playnite's settings don't name: every metadata add-on that offers it
            foreach (var k in keys.Values) if (!order.ContainsKey(k)) order[k] = plugins.Where(p => p.SupportedFields != null && p.SupportedFields.Contains(k)).ToList();
            return order;
        }

        // one game: each empty field from the first add-on that has it; true when anything was filled
        private static bool Fill(Playnite.SDK.IPlayniteAPI api, Game game, Dictionary<MetadataField, List<MetadataPlugin>> order, Dispatcher ui)
        {
            var providers = new Dictionary<Guid, OnDemandMetadataProvider>();
            var args = new GetMetadataFieldArgs();
            Func<MetadataPlugin, OnDemandMetadataProvider> provider = p =>
            {
                OnDemandMetadataProvider got;
                if (!providers.TryGetValue(p.Id, out got)) { try { got = p.GetMetadataProvider(new MetadataRequestOptions(game, true)); } catch { got = null; } providers[p.Id] = got; }
                return got;
            };
            Func<MetadataField, Func<OnDemandMetadataProvider, object>, object> first = (field, get) =>
            {
                foreach (var p in order[field])
                {
                    var pr = provider(p);
                    if (pr == null || (pr.AvailableFields != null && !pr.AvailableFields.Contains(field))) continue;
                    object v = null;
                    try { v = get(pr); } catch { }
                    if (v is string && string.IsNullOrWhiteSpace((string)v)) v = null;
                    if (v is IEnumerable && !(v is string) && !((IEnumerable)v).Cast<object>().Any()) v = null;
                    if (v != null) return v;
                }
                return null;
            };
            Func<List<Guid>, bool> none = ids => ids == null || ids.Count == 0;

            // looked up off the UI thread (web requests), pictures downloaded here too
            var wants = new Dictionary<MetadataField, object>();
            if (string.IsNullOrWhiteSpace(game.Description)) wants[MetadataField.Description] = first(MetadataField.Description, p => p.GetDescription(args));
            if (none(game.GenreIds)) wants[MetadataField.Genres] = first(MetadataField.Genres, p => p.GetGenres(args));
            if (none(game.DeveloperIds)) wants[MetadataField.Developers] = first(MetadataField.Developers, p => p.GetDevelopers(args));
            if (none(game.PublisherIds)) wants[MetadataField.Publishers] = first(MetadataField.Publishers, p => p.GetPublishers(args));
            if (none(game.FeatureIds)) wants[MetadataField.Features] = first(MetadataField.Features, p => p.GetFeatures(args));
            if (none(game.TagIds)) wants[MetadataField.Tags] = first(MetadataField.Tags, p => p.GetTags(args));
            if (none(game.SeriesIds)) wants[MetadataField.Series] = first(MetadataField.Series, p => p.GetSeries(args));
            if (none(game.AgeRatingIds)) wants[MetadataField.AgeRating] = first(MetadataField.AgeRating, p => p.GetAgeRatings(args));
            if (!game.ReleaseDate.HasValue) wants[MetadataField.ReleaseDate] = first(MetadataField.ReleaseDate, p => p.GetReleaseDate(args));
            if (!game.CriticScore.HasValue) wants[MetadataField.CriticScore] = first(MetadataField.CriticScore, p => p.GetCriticScore(args));
            if (!game.CommunityScore.HasValue) wants[MetadataField.CommunityScore] = first(MetadataField.CommunityScore, p => p.GetCommunityScore(args));
            if (game.Links == null || game.Links.Count == 0) wants[MetadataField.Links] = first(MetadataField.Links, p => p.GetLinks(args));
            if (string.IsNullOrEmpty(game.CoverImage)) wants[MetadataField.CoverImage] = Local(first(MetadataField.CoverImage, p => p.GetCoverImage(args)) as MetadataFile);
            if (string.IsNullOrEmpty(game.BackgroundImage)) wants[MetadataField.BackgroundImage] = Local(first(MetadataField.BackgroundImage, p => p.GetBackgroundImage(args)) as MetadataFile);
            if (string.IsNullOrEmpty(game.Icon)) wants[MetadataField.Icon] = Local(first(MetadataField.Icon, p => p.GetIcon(args)) as MetadataFile);
            foreach (var pr in providers.Values) if (pr != null) try { pr.Dispose(); } catch { }
            if (wants.Any(w => w.Value == null)) FromSteamStore(game.Name, wants);

            var found = wants.Where(w => w.Value != null).ToDictionary(w => w.Key, w => w.Value);
            if (found.Count == 0) return false;
            bool changed = false;
            ui.Invoke(new Action(() =>
            {
                var g = api.Database.Games.Get(game.Id);
                if (g == null) return;
                var db = api.Database;
                object v;
                if (found.TryGetValue(MetadataField.Description, out v) && string.IsNullOrWhiteSpace(g.Description)) { g.Description = (string)v; changed = true; }
                if (found.TryGetValue(MetadataField.Genres, out v) && none(g.GenreIds)) { g.GenreIds = db.Genres.Add((IEnumerable<MetadataProperty>)v).Select(x => x.Id).ToList(); changed = true; }
                if (found.TryGetValue(MetadataField.Developers, out v) && none(g.DeveloperIds)) { g.DeveloperIds = db.Companies.Add((IEnumerable<MetadataProperty>)v).Select(x => x.Id).ToList(); changed = true; }
                if (found.TryGetValue(MetadataField.Publishers, out v) && none(g.PublisherIds)) { g.PublisherIds = db.Companies.Add((IEnumerable<MetadataProperty>)v).Select(x => x.Id).ToList(); changed = true; }
                if (found.TryGetValue(MetadataField.Features, out v) && none(g.FeatureIds)) { g.FeatureIds = db.Features.Add((IEnumerable<MetadataProperty>)v).Select(x => x.Id).ToList(); changed = true; }
                if (found.TryGetValue(MetadataField.Tags, out v) && none(g.TagIds)) { g.TagIds = db.Tags.Add((IEnumerable<MetadataProperty>)v).Select(x => x.Id).ToList(); changed = true; }
                if (found.TryGetValue(MetadataField.Series, out v) && none(g.SeriesIds)) { g.SeriesIds = db.Series.Add((IEnumerable<MetadataProperty>)v).Select(x => x.Id).ToList(); changed = true; }
                if (found.TryGetValue(MetadataField.AgeRating, out v) && none(g.AgeRatingIds)) { g.AgeRatingIds = db.AgeRatings.Add((IEnumerable<MetadataProperty>)v).Select(x => x.Id).ToList(); changed = true; }
                if (found.TryGetValue(MetadataField.ReleaseDate, out v) && !g.ReleaseDate.HasValue) { g.ReleaseDate = (ReleaseDate?)v; changed = true; }
                if (found.TryGetValue(MetadataField.CriticScore, out v) && !g.CriticScore.HasValue) { g.CriticScore = (int?)v; changed = true; }
                if (found.TryGetValue(MetadataField.CommunityScore, out v) && !g.CommunityScore.HasValue) { g.CommunityScore = (int?)v; changed = true; }
                if (found.TryGetValue(MetadataField.Links, out v) && (g.Links == null || g.Links.Count == 0)) { g.Links = new System.Collections.ObjectModel.ObservableCollection<Link>((IEnumerable<Link>)v); changed = true; }
                if (found.TryGetValue(MetadataField.CoverImage, out v) && string.IsNullOrEmpty(g.CoverImage)) { g.CoverImage = db.AddFile((string)v, g.Id); changed = true; }
                if (found.TryGetValue(MetadataField.BackgroundImage, out v) && string.IsNullOrEmpty(g.BackgroundImage)) { g.BackgroundImage = db.AddFile((string)v, g.Id); changed = true; }
                if (found.TryGetValue(MetadataField.Icon, out v) && string.IsNullOrEmpty(g.Icon)) { g.Icon = db.AddFile((string)v, g.Id); changed = true; }
                if (changed) db.Games.Update(g);
            }));
            if (changed) HomeNav.Log("game info: " + game.Name + ": " + string.Join(", ", found.Keys));
            foreach (var w in found) if (w.Value is string && ((string)w.Value).StartsWith(Path.GetTempPath())) try { File.Delete((string)w.Value); } catch { }
            return changed;
        }

        // ---- the Steam store, for what the add-ons left empty ----
        private static string Plain(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in (name ?? "").ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        private static object Json(string url)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var web = new WebClient { Encoding = System.Text.Encoding.UTF8 })
            {
                web.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0";
                return new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(web.DownloadString(url));
            }
        }

        private static object At(object o, params string[] path)
        {
            foreach (var k in path) { var d = o as Dictionary<string, object>; if (d == null || !d.TryGetValue(k, out o)) return null; }
            return o;
        }

        private static List<MetadataProperty> Names(object list, string key)
        {
            var names = new List<MetadataProperty>();
            foreach (var o in (list as IEnumerable) ?? new object[0])
            {
                var n = key == null ? Convert.ToString(o) : Convert.ToString(At(o, key));
                if (!string.IsNullOrWhiteSpace(n)) names.Add(new MetadataNameProperty(n.Trim()));
            }
            return names.Count > 0 ? names : null;
        }

        // the empty ones of `wants` from the store page of the game with exactly this name (nothing when there's none)
        private static void FromSteamStore(string name, Dictionary<MetadataField, object> wants)
        {
            try
            {
                var plain = Plain(name);
                if (plain.Length == 0) return;
                var found = At(Json("https://store.steampowered.com/api/storesearch/?term=" + Uri.EscapeDataString(name) + "&l=english&cc=US"), "items") as IEnumerable;
                string appId = null;
                foreach (var item in found ?? new object[0]) if (Plain(Convert.ToString(At(item, "name"))) == plain) { appId = Convert.ToString(At(item, "id")); break; }
                if (appId == null) { HomeNav.Log("game info: " + name + ": not on the Steam store by that name"); return; }
                var data = At(Json("https://store.steampowered.com/api/appdetails?appids=" + appId + "&l=english"), appId, "data");
                if (data == null) return;
                Action<MetadataField, object> put = (field, value) => { object had; if (wants.TryGetValue(field, out had) && had == null && value != null) wants[field] = value; };
                var about = Convert.ToString(At(data, "detailed_description") ?? At(data, "about_the_game"));
                put(MetadataField.Description, string.IsNullOrWhiteSpace(about) ? null : about);
                put(MetadataField.Genres, Names(At(data, "genres"), "description"));
                put(MetadataField.Developers, Names(At(data, "developers"), null));
                put(MetadataField.Publishers, Names(At(data, "publishers"), null));
                put(MetadataField.Features, Names(At(data, "categories"), "description"));
                DateTime when;
                var date = Convert.ToString(At(data, "release_date", "date"));
                if (DateTime.TryParseExact(date, new[] { "d MMM, yyyy", "MMM d, yyyy", "d MMMM, yyyy", "MMMM d, yyyy" }, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out when) || DateTime.TryParse(date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out when)) put(MetadataField.ReleaseDate, (ReleaseDate?)new ReleaseDate(when));
                var score = At(data, "metacritic", "score");
                if (score != null) put(MetadataField.CriticScore, (int?)Convert.ToInt32(score));
                var cdn = "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/" + appId + "/";
                object had2;
                if (wants.TryGetValue(MetadataField.CoverImage, out had2) && had2 == null) wants[MetadataField.CoverImage] = Local(new MetadataFile(cdn + "library_600x900_2x.jpg")) ?? Local(new MetadataFile(cdn + "library_600x900.jpg"));
                if (wants.TryGetValue(MetadataField.BackgroundImage, out had2) && had2 == null) wants[MetadataField.BackgroundImage] = Local(new MetadataFile(cdn + "library_hero.jpg")) ?? Local(new MetadataFile(Convert.ToString(At(data, "background_raw"))));
                HomeNav.Log("game info: " + name + ": Steam store page " + appId);
            }
            catch (Exception ex) { HomeNav.Log("game info: " + name + ": Steam store: " + ex.Message); }
        }

        // a picture from an add-on as a file on disk (its bytes, a web address downloaded, or a path as it is); null without one
        private static string Local(MetadataFile file)
        {
            if (file == null) return null;
            try
            {
                var name = string.IsNullOrEmpty(file.FileName) ? Guid.NewGuid() + ".jpg" : Guid.NewGuid() + "_" + Path.GetFileName(file.FileName);
                var temp = Path.Combine(Path.GetTempPath(), "selene-meta-" + name);
                if (file.HasContent && file.Content != null) { File.WriteAllBytes(temp, file.Content); return temp; }
                if (string.IsNullOrEmpty(file.Path)) return null;
                if (file.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    using (var web = new WebClient()) web.DownloadFile(file.Path, temp);
                    return temp;
                }
                return File.Exists(file.Path) ? file.Path : null;
            }
            catch (Exception ex) { HomeNav.Log("game info: picture: " + ex.Message); return null; }
        }

        private static Dictionary<Guid, DateTime> ReadTried()
        {
            var tried = new Dictionary<Guid, DateTime>();
            try
            {
                if (File.Exists(TriedFile))
                    foreach (var l in File.ReadAllLines(TriedFile))
                    {
                        var parts = l.Split(' ');
                        Guid id; long ticks;
                        if (parts.Length == 2 && Guid.TryParse(parts[0], out id) && long.TryParse(parts[1], out ticks)) tried[id] = new DateTime(ticks, DateTimeKind.Utc);
                    }
            }
            catch { }
            return tried;
        }

        private static void WriteTried(Dictionary<Guid, DateTime> tried)
        {
            try { File.WriteAllLines(TriedFile, tried.Select(t => t.Key + " " + t.Value.Ticks)); } catch { }
        }
    }
}
