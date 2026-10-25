using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Selene
{
    // One game offered in the Store page
    public class StoreItem
    {
        public string Store, StoreName, Id, Title, Image;   // Store: steam, epic, gog, xbox, deals; Image: a wide picture
        public string Backdrop;                              // a screen-sized picture when the store has one
        public string Price, OldPrice;                       // formatted ("₱779.70"); OldPrice only when discounted
        public int Discount;                                 // percent off
        public bool Free;                                    // free to keep right now
        public string Note;                                  // "Free until Oct 29", "Free from Nov 1", "Game Pass"
        public string Link, WebLink;                         // the store app's link and the web page (fallback)
        public string SteamAppId;
        public bool Owned;
    }

    // The Store page's data: free public sources, no account or key. Each list arrives on its own (the page fills in
    // as they come); the last good copy is kept on disk so the page shows something offline.
    public static class StoreData
    {
        private const string Agent = "Selene/1.0 (Playnite theme; github.com/mattxslv/Selene)";
        private static readonly Dictionary<string, List<StoreItem>> lists = new Dictionary<string, List<StoreItem>>();
        private static DateTime loadedAt = DateTime.MinValue;
        private static bool loading;
        private static readonly List<Action<string>> waiting = new List<Action<string>>();

        // ---------------- currency ----------------
        // the stores price by country: picking a currency picks the country whose prices the stores show
        public class Currency { public string Code, Name, Country; }
        public static readonly Currency[] Currencies =
        {
            new Currency { Code = "USD", Name = "US dollar", Country = "US" },
            new Currency { Code = "EUR", Name = "Euro", Country = "DE" },
            new Currency { Code = "GBP", Name = "British pound", Country = "GB" },
            new Currency { Code = "PHP", Name = "Philippine peso", Country = "PH" },
            new Currency { Code = "JPY", Name = "Japanese yen", Country = "JP" },
            new Currency { Code = "CAD", Name = "Canadian dollar", Country = "CA" },
            new Currency { Code = "AUD", Name = "Australian dollar", Country = "AU" },
            new Currency { Code = "BRL", Name = "Brazilian real", Country = "BR" },
            new Currency { Code = "INR", Name = "Indian rupee", Country = "IN" },
            new Currency { Code = "KRW", Name = "Korean won", Country = "KR" },
            new Currency { Code = "SGD", Name = "Singapore dollar", Country = "SG" },
            new Currency { Code = "IDR", Name = "Indonesian rupiah", Country = "ID" },
            new Currency { Code = "MYR", Name = "Malaysian ringgit", Country = "MY" },
            new Currency { Code = "THB", Name = "Thai baht", Country = "TH" },
            new Currency { Code = "MXN", Name = "Mexican peso", Country = "MX" },
        };

        private static string RegionFile { get { return Path.Combine(SelenePaths.DataFolder, "store-region.txt"); } }
        private static string chosen;

        // the currency the prices are in (the picked one, or this PC's region's)
        public static string CurrencyCode
        {
            get
            {
                var c = Currencies.FirstOrDefault(x => x.Country == Country);
                if (c != null) return c.Code;
                try { return new RegionInfo(Country).ISOCurrencySymbol; } catch { return "USD"; }
            }
        }

        // the country picked with the currency (null until someone picks one)
        public static string ChosenCountry
        {
            get
            {
                if (chosen == null) { try { chosen = File.Exists(RegionFile) ? File.ReadAllText(RegionFile).Trim() : ""; } catch { chosen = ""; } }
                return chosen.Length == 2 ? chosen : null;
            }
        }

        public static string Country
        {
            get
            {
                var c = ChosenCountry;
                if (c != null) return c;
                try { return RegionInfo.CurrentRegion.TwoLetterISORegionName; } catch { return "US"; }
            }
        }

        // a new currency: saved, everything loaded again in its prices
        public static void SetCountry(string country)
        {
            try { Directory.CreateDirectory(SelenePaths.DataFolder); File.WriteAllText(RegionFile, country); } catch (Exception ex) { HomeNav.Log("store region: " + ex.Message); }
            if (chosen == country) return;
            if (country == Country) { chosen = country; HomeNav.Log("store: prices stay in " + country); return; }   // the prices already shown
            bool changed = chosen != null && chosen.Length == 2 || country != Country;
            chosen = country;
            HomeNav.Log("store: prices for " + country);
            lists.Clear();
            nextPage.Clear(); exhausted.Clear();
            loadedAt = DateTime.MinValue;
            FromDisk = false;
            try { if (File.Exists(DiskFile)) File.Delete(DiskFile); } catch { }
            if (changed) UpdateCard();
        }

        public static List<StoreItem> List(string key)
        {
            List<StoreItem> l;
            return lists.TryGetValue(key, out l) ? l : new List<StoreItem>();
        }

        public static bool Has(string key) { return lists.ContainsKey(key); }

        // ---------------- more as you scroll ----------------
        // the lists that page: each gives its next batch for page 1, 2, 3... (an empty batch: that's all of it)
        private static readonly Dictionary<string, Func<int, List<StoreItem>>> Pagers = new Dictionary<string, Func<int, List<StoreItem>>>
        {
            { "steam:sale", p => SteamSearch("specials=1", p) },
            { "steam:top", p => SteamSearch("filter=topsellers", p) },
            { "steam:new", p => SteamSearch("filter=popularnew&sort_by=Released_DESC", p) },
            { "steam:soon", p => SteamSearch("filter=popularcomingsoon", p) },
            { "gog:sale", p => Gog("order=desc:discount&discounted=eq:true", p) },
            { "gog:best", p => Gog("order=desc:bestselling", p) },
            { "xbox:gamepass", p => GamePass(p) },
            { "deals:best", p => Deals(p) },
        };
        private static readonly Dictionary<string, int> nextPage = new Dictionary<string, int>();
        private static readonly HashSet<string> paging = new HashSet<string>(), exhausted = new HashSet<string>();

        public static bool CanMore(string key) { return Pagers.ContainsKey(key) && lists.ContainsKey(key) && !exhausted.Contains(key) && !FromDisk; }

        // the list's next batch, added to its end (games it has already left out); `arrived(key)` on the UI thread
        public static void More(string key, Action<string> arrived)
        {
            if (!CanMore(key) || paging.Contains(key)) return;
            paging.Add(key);
            int page;
            if (!nextPage.TryGetValue(key, out page)) page = 1;
            var owned = OwnedIndex();
            var get = Pagers[key];
            var ui = System.Windows.Application.Current.Dispatcher;
            Task.Run(() =>
            {
                List<StoreItem> got = null;
                try { got = get(page); }
                catch (Exception ex) { HomeNav.Log("store more " + key + ": " + ex.Message); }
                ui.BeginInvoke(new Action(() =>
                {
                    paging.Remove(key);
                    List<StoreItem> list;
                    if (got == null || !lists.TryGetValue(key, out list)) return;   // (a failed batch can be tried again)
                    var have = new HashSet<string>(list.Select(i => i.Store + ":" + i.Id));
                    var added = got.Where(i => have.Add(i.Store + ":" + i.Id)).ToList();
                    if (added.Count == 0) { exhausted.Add(key); return; }
                    foreach (var i in added) i.Owned = owned.Has(i);
                    list.AddRange(added);
                    nextPage[key] = page + 1;
                    HomeNav.Log("store: " + key + " +" + added.Count + " (" + list.Count + ")");
                    arrived(key);
                }));
            });
        }

        // the Store card on Spotlight: what it opens, and this week's free games (loaded a little after start)
        public static void AttachCard()
        {
            var res = System.Windows.Application.Current.Resources;
            bool page = Features.On("storepage");
            res["SeleneStoreAbout"] = page ? "Free games, sales and new releases from your stores. You buy in the store's own app." : "Find something new in the store apps on this PC. Selene is here when you come back.";
            StoreShowcase.FillChips();
            if (!page) return;
            var later = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            later.Tick += (s, e) => { later.Stop(); UpdateCard(); };
            later.Start();
        }

        // the Store card's showcase: this week's free games, then the biggest sales, as small pictures with their price
        private static void UpdateCard()
        {
            Load(key => { if (key == "epic:free" || key == "steam:sale" || key == "epic:sale") StoreShowcase.Fill(); });
        }

        // when the lists shown were saved (offline: from the disk copy), MinValue while nothing is loaded
        public static DateTime LoadedAt { get { return loadedAt; } }
        public static bool FromDisk { get; private set; }

        // loads (or refreshes after 30 minutes) every list; `arrived(key)` runs on the UI thread as each one comes in
        public static void Load(Action<string> arrived)
        {
            var ui = System.Windows.Application.Current.Dispatcher;
            if (lists.Count == 0) ReadDisk();
            foreach (var k in lists.Keys.ToList()) arrived(k);
            if (loading) { waiting.Add(arrived); return; }
            if ((DateTime.Now - loadedAt).TotalMinutes < 30 && !FromDisk) return;
            loading = true;
            waiting.Clear();
            waiting.Add(arrived);
            System.Net.ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var owned = OwnedIndex();
            var jobs = new List<Task>();
            Action<string, Func<List<StoreItem>>> fetch = (key, get) => jobs.Add(Task.Run(() =>
            {
                List<StoreItem> got = null;
                try { got = get(); }
                catch (Exception ex) { HomeNav.Log("store data " + key + ": " + ex.Message); }
                if (got == null) return;
                foreach (var i in got) i.Owned = owned.Has(i);
                ui.BeginInvoke(new Action(() =>
                {
                    lists[key] = got;
                    nextPage.Remove(key); exhausted.Remove(key);
                    FromDisk = false;
                    loadedAt = DateTime.Now;
                    foreach (var w in waiting.ToList()) w(key);
                }));
            }));
            fetch("epic", Epic);   // fills epic:* itself (one request for three lists)
            foreach (var pager in Pagers) { var key = pager.Key; var first = pager.Value; fetch(key, () => first(0)); }
            Task.WhenAll(jobs).ContinueWith(t => ui.BeginInvoke(new Action(() => { loading = false; WriteDisk(); })));
        }

        // ---------------- sources ----------------
        private static object Get(string url)
        {
            using (var web = new WebClient { Encoding = Encoding.UTF8 })
            {
                web.Headers[HttpRequestHeader.UserAgent] = Agent;
                var text = web.DownloadString(url);
                return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text);
            }
        }

        private static Dictionary<string, object> D(object o) { return o as Dictionary<string, object>; }
        private static object[] A(object o) { return o as object[] ?? new object[0]; }
        private static string S(object o) { return o == null ? null : Convert.ToString(o, CultureInfo.InvariantCulture); }
        private static object At(object o, params string[] path)
        {
            foreach (var p in path) { var d = D(o); if (d == null || !d.TryGetValue(p, out o)) return null; }
            return o;
        }
        private static double Num(object o) { double v; return o != null && double.TryParse(S(o), NumberStyles.Any, CultureInfo.InvariantCulture, out v) ? v : 0; }

        private static readonly Dictionary<string, string> Symbols = new Dictionary<string, string>
        {
            { "USD", "$" }, { "PHP", "₱" }, { "EUR", "€" }, { "GBP", "£" }, { "JPY", "¥" }, { "INR", "₹" }, { "KRW", "₩" }, { "BRL", "R$" },
            { "AUD", "A$" }, { "CAD", "C$" }, { "SGD", "S$" }, { "MYR", "RM " }, { "IDR", "Rp " }, { "THB", "฿" }, { "VND", "₫" }
        };
        public static string Money(double amount, string currency)
        {
            string sym;
            if (!Symbols.TryGetValue(currency ?? "", out sym)) sym = (currency ?? "") + " ";
            return sym + amount.ToString(amount == Math.Floor(amount) && amount >= 1000 ? "N0" : "N2", CultureInfo.InvariantCulture);
        }

        private static string Open(string protocol, string appLink, string web)
        {
            return StoreScreen.Registered(protocol) ? appLink : web;   // the app when it's there, else the store's page in the browser
        }

        // Steam's lists are its search results (they page: 25 games at a time, as far as you scroll), in this region's prices
        private static List<StoreItem> SteamSearch(string query, int page)
        {
            const int count = 25;
            var data = Get("https://store.steampowered.com/search/results/?query&infinite=1&category1=998&l=english&cc=" + Country + "&count=" + count + "&start=" + (page * count) + "&" + query);
            var html = S(At(data, "results_html")) ?? "";
            var items = new List<StoreItem>();
            foreach (var block in html.Split(new[] { "<a href=" }, StringSplitOptions.None))
            {
                var id = Match(block, "data-ds-appid=\"(\\d+)\"");
                var title = Match(block, "<span class=\"title\">(.*?)</span>");
                if (id == null || title == null) continue;   // bundles and packages have no single app
                var item = new StoreItem { Store = "steam", StoreName = "Steam", Id = id, SteamAppId = id, Title = WebUtility.HtmlDecode(title) };
                int pct;
                if (int.TryParse(Match(block, "data-discount=\"(\\d+)\"") ?? "", out pct)) item.Discount = pct;
                var final = Match(block, "discount_final_price[^\"]*\">([^<]*)<");
                var original = Match(block, "discount_original_price\">([^<]*)<");
                if (!string.IsNullOrEmpty(final)) item.Price = WebUtility.HtmlDecode(final.Trim());
                if (item.Discount > 0 && !string.IsNullOrEmpty(original)) item.OldPrice = WebUtility.HtmlDecode(original.Trim());
                if (item.Price != null && item.Price.IndexOf("free", StringComparison.OrdinalIgnoreCase) >= 0) item.Price = "Free";
                if (item.Price == null)
                {
                    var released = Match(block, "search_released[^>]*>\\s*([^<]*?)\\s*<");
                    item.Note = string.IsNullOrEmpty(released) ? "Coming soon" : WebUtility.HtmlDecode(released);
                }
                item.Link = Open("steam", "steam://store/" + id, "https://store.steampowered.com/app/" + id);
                item.WebLink = "https://store.steampowered.com/app/" + id;
                items.Add(item);
            }
            // sharp pictures from Steam's picture service (the search's own are 231 px)
            var capsules = SteamCapsules(items.Select(i => i.Id).ToList());
            foreach (var i in items)
            {
                string c;
                i.Image = capsules.TryGetValue(i.Id, out c) ? c : "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/" + i.Id + "/header.jpg";
            }
            return items;
        }

        private static string Match(string text, string pattern)
        {
            var m = System.Text.RegularExpressions.Regex.Match(text, pattern, System.Text.RegularExpressions.RegexOptions.Singleline);
            return m.Success ? m.Groups[1].Value : null;
        }

        // Epic's promotions: the free games (now and next) and the games on sale this week
        private static List<StoreItem> Epic()
        {
            var c = Country;
            var data = Get("https://store-site-backend-static.ak.epicgames.com/freeGamesPromotions?locale=en-US&country=" + c + "&allowCountries=" + c);
            List<StoreItem> free = new List<StoreItem>(), next = new List<StoreItem>(), sale = new List<StoreItem>();
            foreach (var e in A(At(data, "data", "Catalog", "searchStore", "elements")))
            {
                var now = A(At(e, "promotions", "promotionalOffers"));
                var soon = A(At(e, "promotions", "upcomingPromotionalOffers"));
                var offer = now.Length > 0 ? A(At(now[0], "promotionalOffers")).FirstOrDefault() : soon.Length > 0 ? A(At(soon[0], "promotionalOffers")).FirstOrDefault() : null;
                if (offer == null) continue;
                var slug = S(At(e, "productSlug"));
                var map = A(At(e, "catalogNs", "mappings")).FirstOrDefault();
                if (map != null && S(At(map, "pageSlug")) != null) slug = S(At(map, "pageSlug"));
                if (string.IsNullOrEmpty(slug) || slug == "[]") continue;
                var item = new StoreItem { Store = "epic", StoreName = "Epic Games", Id = slug, Title = S(At(e, "title")) };
                foreach (var k in A(At(e, "keyImages")))
                    if (S(At(k, "type")) == "OfferImageWide" || (item.Image == null && S(At(k, "type")) == "Thumbnail")) item.Image = S(At(k, "url"));
                var pct = (int)Num(At(offer, "discountSetting", "discountPercentage"));   // 0 = free
                string orig = S(At(e, "price", "totalPrice", "fmtPrice", "originalPrice")), disc = S(At(e, "price", "totalPrice", "fmtPrice", "discountPrice"));
                DateTime end, start;
                bool hasEnd = DateTime.TryParse(S(At(offer, "endDate")), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out end);
                bool hasStart = DateTime.TryParse(S(At(offer, "startDate")), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out start);
                item.Link = Open("com.epicgames.launcher", "com.epicgames.launcher://store/p/" + slug, "https://store.epicgames.com/p/" + slug);
                item.WebLink = "https://store.epicgames.com/p/" + slug;
                if (pct == 0 && now.Length > 0)
                {
                    item.Free = true; item.Price = "Free"; item.OldPrice = orig; item.Discount = 100;
                    if (hasEnd) item.Note = "Free until " + end.ToLocalTime().ToString("MMM d", CultureInfo.InvariantCulture);
                    free.Add(item);
                }
                else if (pct == 0)
                {
                    item.Price = orig;
                    if (hasStart) item.Note = "Free from " + start.ToLocalTime().ToString("MMM d", CultureInfo.InvariantCulture);
                    next.Add(item);
                }
                else if (now.Length > 0)
                {
                    item.Price = disc; item.OldPrice = orig; item.Discount = 100 - pct;   // Epic gives the share you pay
                    if (hasEnd) item.Note = "Sale ends " + end.ToLocalTime().ToString("MMM d", CultureInfo.InvariantCulture);
                    sale.Add(item);
                }
            }
            var ui = System.Windows.Application.Current.Dispatcher;
            var owned = OwnedIndex();
            foreach (var l in new[] { free, next, sale }) foreach (var i in l) i.Owned = owned.Has(i);
            ui.BeginInvoke(new Action(() =>
            {
                lists["epic:free"] = free; lists["epic:next"] = next; lists["epic:sale"] = sale;
                FromDisk = false; loadedAt = DateTime.Now;
                foreach (var w in waiting.ToList()) { w("epic:free"); w("epic:next"); w("epic:sale"); }
            }));
            return null;
        }

        // GOG's catalog (prices in dollars outside its own regions)
        private static List<StoreItem> Gog(string query, int page)
        {
            var data = Get("https://catalog.gog.com/v1/catalog?limit=30&productType=in:game,pack&locale=en-US&countryCode=" + Country + "&page=" + (page + 1) + "&" + query);
            var items = new List<StoreItem>();
            foreach (var p in A(At(data, "products")))
            {
                var slug = S(At(p, "slug"));
                var link = S(At(p, "storeLink")) ?? ("https://www.gog.com/en/game/" + slug);
                var item = new StoreItem { Store = "gog", StoreName = "GOG", Id = S(At(p, "id")), Title = S(At(p, "title")), Image = S(At(p, "coverHorizontal")) };
                var shot = A(At(p, "screenshots")).Select(S).FirstOrDefault(x => x != null);
                if (shot != null) item.Backdrop = shot.Replace("{formatter}", "1600");   // 1600 px wide
                item.Price = S(At(p, "price", "final"));
                var disc = S(At(p, "price", "discount"));
                int pct;
                if (!string.IsNullOrEmpty(disc) && int.TryParse(disc.Trim('-', '%'), out pct) && pct > 0) { item.Discount = pct; item.OldPrice = S(At(p, "price", "base")); }
                if (item.Price == "$0.00") { item.Price = "Free"; }
                item.Link = Open("goggalaxy", "goggalaxy://openStoreUrl/" + link.Replace("https://", ""), link);
                item.WebLink = link;
                items.Add(item);
            }
            return items;
        }

        // PC Game Pass: Microsoft's list, then its catalog for names, pictures and prices
        private static List<string> gamePassIds;

        private static List<StoreItem> GamePass(int page)
        {
            if (page == 0 || gamePassIds == null)
                gamePassIds = A(Get("https://catalog.gamepass.com/sigls/v2?id=fdd9e2a7-0fee-49f6-ad69-4354098401ff&language=en-us&market=" + Country))
                    .Select(o => S(At(o, "id"))).Where(id => id != null).ToList();
            var ids = gamePassIds.Skip(page * 40).Take(40).ToList();
            var items = new List<StoreItem>();
            for (int at = 0; at < ids.Count; at += 20)
            {
                var data = Get("https://displaycatalog.mp.microsoft.com/v7.0/products?languages=en-us&market=" + Country + "&bigIds=" + string.Join(",", ids.Skip(at).Take(20)));
                foreach (var p in A(At(data, "Products")))
                {
                    var lp = A(At(p, "LocalizedProperties")).FirstOrDefault();
                    if (lp == null) continue;
                    var id = S(At(p, "ProductId"));
                    var item = new StoreItem { Store = "xbox", StoreName = "Xbox", Id = id, Title = S(At(lp, "ProductTitle")), Note = "Game Pass" };
                    string hero = null, box = null;
                    foreach (var img in A(At(lp, "Images")))
                    {
                        var purpose = S(At(img, "ImagePurpose"));
                        var uri = S(At(img, "Uri"));
                        if (uri != null && uri.StartsWith("//")) uri = "https:" + uri;
                        if (purpose == "TitledHeroArt" && hero == null) hero = uri;
                        if ((purpose == "SuperHeroArt" || purpose == "BoxArt") && box == null) box = uri;
                    }
                    item.Image = (hero ?? box) != null ? (hero ?? box) + "?w=960" : null;
                    var price = At(A(At(p, "DisplaySkuAvailabilities")).Select(s => A(At(s, "Availabilities")).FirstOrDefault()).FirstOrDefault(a => a != null), "OrderManagementData", "Price");
                    if (price != null)
                    {
                        double list = Num(At(price, "ListPrice")), msrp = Num(At(price, "MSRP"));
                        var cur = S(At(price, "CurrencyCode"));
                        if (list > 0) item.Price = Money(list, cur);
                        if (msrp > list && list > 0) { item.OldPrice = Money(msrp, cur); item.Discount = (int)Math.Round(100 - list * 100 / msrp); }
                    }
                    item.Link = Open("msxbox", "msxbox://game/?productId=" + id, "ms-windows-store://pdp/?productid=" + id);
                    item.WebLink = "https://www.xbox.com/games/store/p/" + id;
                    items.Add(item);
                }
            }
            return items;
        }

        // the best deals from the other stores (CheapShark; dollar prices)
        private static List<StoreItem> Deals(int page)
        {
            var stores = new Dictionary<string, string>();
            foreach (var s in A(Get("https://www.cheapshark.com/api/1.0/stores"))) stores[S(At(s, "storeID"))] = S(At(s, "storeName"));
            var items = new List<StoreItem>();
            var seen = new HashSet<string>();
            var deals = A(Get("https://www.cheapshark.com/api/1.0/deals?pageSize=60&sortBy=Deal%20Rating&onSale=1&pageNumber=" + page));
            var capsules = SteamCapsules(deals.Select(d => S(At(d, "steamAppID"))).Where(id => !string.IsNullOrEmpty(id) && id != "0").Distinct().Take(40).ToList());
            foreach (var d in deals)
            {
                var store = S(At(d, "storeID"));
                if (store == "1" || store == "7" || store == "25") continue;   // Steam, GOG and Epic have their own tabs
                var game = S(At(d, "gameID"));
                if (game == null || !seen.Add(game)) continue;
                var steam = S(At(d, "steamAppID"));
                string name;
                var item = new StoreItem { Store = "deals", StoreName = stores.TryGetValue(store ?? "", out name) ? name : "Store", Id = S(At(d, "dealID")), Title = S(At(d, "title")), SteamAppId = steam };
                // a sharp wide picture or the deal is left out (the stores' own covers are tiny): Steam's 616 px capsule
                // (by its picture service), a Steam package's capsule
                string capsule = null, thumb = S(At(d, "thumb")) ?? "";
                if (!string.IsNullOrEmpty(steam) && steam != "0") capsules.TryGetValue(steam, out capsule);
                if (capsule == null && thumb.Contains("/steam/subs/") && thumb.Contains("capsule_231x87")) capsule = thumb.Replace("capsule_231x87", "capsule_616x353");
                if (capsule == null) continue;
                item.Image = capsule;
                double sale = Num(At(d, "salePrice")), normal = Num(At(d, "normalPrice"));
                item.Price = sale == 0 ? "Free" : Money(sale, "USD");
                if (normal > sale) { item.OldPrice = Money(normal, "USD"); item.Discount = (int)Math.Round(Num(At(d, "savings"))); }
                item.Link = item.WebLink = "https://www.cheapshark.com/redirect?dealID=" + Uri.EscapeDataString(WebUtility.UrlDecode(item.Id));
                items.Add(item);
            }
            return items;
        }

        // the sharpest wide picture for a big card: Steam's capsule at double size (its 616 px one for deals from other
        // stores); Epic's (2560 px) and Microsoft's are big already and are decoded at card size
        public static string Sharp(StoreItem item)
        {
            var url = item.Image;
            if (string.IsNullOrEmpty(url)) return url;
            // Steam's hashed capsules (from its lists and picture service) come at double size too
            if (url.Contains("/capsule_616x353") && url.Contains("store_item_assets") && !url.Contains("_2x") && !url.Contains("/subs/"))
            {
                var slash = url.LastIndexOf('/');
                var folder = url.Substring(0, slash);
                bool hashed = folder.Substring(folder.LastIndexOf('/') + 1).Length >= 32;
                int dot = url.IndexOf(".jpg", slash, StringComparison.Ordinal);
                if (hashed && dot > 0) return url.Insert(dot, "_2x");
            }
            if (!string.IsNullOrEmpty(item.SteamAppId) && url.EndsWith("/header.jpg"))
                return "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/" + item.SteamAppId + "/capsule_616x353.jpg";
            return url;
        }

        // Steam's picture service: each app's 616 px capsule (its file names carry a hash, so they can't be guessed)
        private static Dictionary<string, string> SteamCapsules(List<string> ids)
        {
            var found = new Dictionary<string, string>();
            if (ids.Count == 0) return found;
            try
            {
                var json = "{\"ids\":[" + string.Join(",", ids.Select(id => "{\"appid\":" + id + "}")) + "],\"context\":{\"language\":\"english\",\"country_code\":\"" + Country + "\"},\"data_request\":{\"include_assets\":true}}";
                var data = Get("https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(json));
                foreach (var it in A(At(data, "response", "store_items")))
                {
                    var id = S(At(it, "appid"));
                    var format = S(At(it, "assets", "asset_url_format"));
                    var capsule = S(At(it, "assets", "main_capsule"));
                    if (id == null || format == null || capsule == null) continue;
                    found[id] = "https://shared.akamai.steamstatic.com/store_item_assets/" + format.Replace("${FILENAME}", capsule);
                }
            }
            catch (Exception ex) { HomeNav.Log("steam pictures: " + ex.Message); }
            return found;
        }

        // ---------------- a game's page ----------------
        public class Details
        {
            public string About, Released, Genres, Score, Trailer;   // Trailer: Steam's AV1 stream (DASH)
            public List<string> Shots = new List<string>();
        }

        // more about one game (description, screenshots); `done` on the UI thread (null when there's nothing more)
        private static readonly Dictionary<string, Details> detailsCache = new Dictionary<string, Details>();

        public static void LoadDetails(StoreItem item, Action<Details> done)
        {
            var key = item.Store + ":" + (item.SteamAppId ?? item.Id);
            Details cached;
            if (detailsCache.TryGetValue(key, out cached)) { done(cached); return; }
            var ui = System.Windows.Application.Current.Dispatcher;
            Task.Run(() =>
            {
                Details d = null;
                try { d = FetchDetails(item); }
                catch (Exception ex) { HomeNav.Log("store details " + item.Title + ": " + ex.Message); }
                ui.BeginInvoke(new Action(() => { detailsCache[key] = d; done(d); }));
            });
        }

        // a picture big enough to fill the screen behind a game (null when it only has small ones: better dark than
        // blurry): a Steam game's first 1920 px screenshot, Epic's 2560 px art, Microsoft's hero art at full width
        public static void BigArt(StoreItem item, Action<string> done)
        {
            if (item == null || string.IsNullOrEmpty(item.Image)) { done(null); return; }
            if (!string.IsNullOrEmpty(item.Backdrop)) { done(item.Backdrop); return; }
            if (item.Store == "epic") { done(item.Image); return; }
            if (item.Store == "xbox") { done(item.Image.Replace("?w=960", "?w=1920")); return; }
            if (!string.IsNullOrEmpty(item.SteamAppId) && item.SteamAppId != "0")
            {
                LoadDetails(item, d => done(d != null && d.Shots.Count > 0 ? d.Shots[0] : null));
                return;
            }
            done(null);   // GOG's and the other stores' pictures are card-sized
        }

        private static Details FetchDetails(StoreItem item)
        {
            var d = new Details();
            if (!string.IsNullOrEmpty(item.SteamAppId) && item.SteamAppId != "0")
            {
                var data = At(Get("https://store.steampowered.com/api/appdetails?l=english&cc=" + Country + "&appids=" + item.SteamAppId), item.SteamAppId, "data");
                if (data == null) return null;
                d.About = WebUtility.HtmlDecode(S(At(data, "short_description")));
                d.Released = S(At(data, "release_date", "date"));
                d.Genres = string.Join(" · ", A(At(data, "genres")).Select(g => S(At(g, "description"))).Take(3));
                var score = S(At(data, "metacritic", "score"));
                if (score != null) d.Score = "Metacritic " + score;
                d.Shots = A(At(data, "screenshots")).Select(s => S(At(s, "path_full"))).Where(s => s != null).Take(8).ToList();
                var movie = A(At(data, "movies")).FirstOrDefault(m => S(At(m, "highlight")) == "True") ?? A(At(data, "movies")).FirstOrDefault();
                d.Trailer = S(At(movie, "dash_av1"));
                return d;
            }
            if (item.Store == "xbox")
            {
                var p = A(At(Get("https://displaycatalog.mp.microsoft.com/v7.0/products?languages=en-us&market=" + Country + "&bigIds=" + item.Id), "Products")).FirstOrDefault();
                var lp = A(At(p, "LocalizedProperties")).FirstOrDefault();
                if (lp == null) return null;
                d.About = S(At(lp, "ShortDescription"));
                if (string.IsNullOrEmpty(d.About)) d.About = S(At(lp, "ProductDescription"));
                if (d.About != null && d.About.Length > 420) d.About = d.About.Substring(0, 420).TrimEnd() + "…";
                d.Shots = A(At(lp, "Images")).Where(i => S(At(i, "ImagePurpose")) == "Screenshot").Select(i => { var u = S(At(i, "Uri")); return (u != null && u.StartsWith("//") ? "https:" + u : u) + "?w=1920"; }).Take(8).ToList();
                return d;
            }
            return null;
        }

        // ---------------- owned games ----------------
        private class Owned
        {
            public HashSet<string> Steam = new HashSet<string>(), Names = new HashSet<string>();
            public bool Has(StoreItem i)
            {
                if (!string.IsNullOrEmpty(i.SteamAppId) && Steam.Contains(i.SteamAppId)) return true;
                return i.Title != null && Names.Contains(Key(i.Title));
            }
        }

        private static readonly Guid SteamPlugin = new Guid("cb91dfc9-b977-43bf-8e70-55f46e410fab");

        // the game in the Playnite library this store game is (its Steam app, else its name); null when it isn't there
        public static Playnite.SDK.Models.Game FindOwned(StoreItem item)
        {
            if (HomeNav.Api == null || item == null) return null;
            var key = Key(item.Title ?? "");
            Playnite.SDK.Models.Game byName = null;
            foreach (var g in HomeNav.Api.Database.Games)
            {
                if (!string.IsNullOrEmpty(item.SteamAppId) && g.PluginId == SteamPlugin && g.GameId == item.SteamAppId) return g;
                if (byName == null && key.Length > 0 && !string.IsNullOrEmpty(g.Name) && Key(g.Name) == key) byName = g;
            }
            return byName;
        }

        // the cheapest price for this game in another store (CheapShark, dollars); `done` on the UI thread, null when none
        public class Elsewhere { public string Store, Price, Link; }
        private static Dictionary<string, string> dealStores;

        public static void FindElsewhere(StoreItem item, Action<Elsewhere> done)
        {
            var ui = System.Windows.Application.Current.Dispatcher;
            Task.Run(() =>
            {
                Elsewhere found = null;
                try
                {
                    if (dealStores == null)
                    {
                        var names = new Dictionary<string, string>();
                        foreach (var st in A(Get("https://www.cheapshark.com/api/1.0/stores"))) names[S(At(st, "storeID"))] = S(At(st, "storeName"));
                        dealStores = names;
                    }
                    var lookup = !string.IsNullOrEmpty(item.SteamAppId) && item.SteamAppId != "0"
                        ? Get("https://www.cheapshark.com/api/1.0/games?steamAppID=" + item.SteamAppId)
                        : Get("https://www.cheapshark.com/api/1.0/games?exact=1&limit=1&title=" + Uri.EscapeDataString(item.Title ?? ""));
                    var gameId = S(At(A(lookup).FirstOrDefault(), "gameID"));
                    if (gameId != null)
                    {
                        // the same store as this page's doesn't count (Steam 1, GOG 7, Epic 25)
                        var own = item.Store == "steam" ? "1" : item.Store == "gog" ? "7" : item.Store == "epic" ? "25" : null;
                        var best = A(At(Get("https://www.cheapshark.com/api/1.0/games?id=" + gameId), "deals"))
                            .Where(d => S(At(d, "storeID")) != own && (item.Store != "deals" || S(At(d, "storeID")) != null))
                            .OrderBy(d => Num(At(d, "price"))).FirstOrDefault();
                        if (best != null)
                        {
                            string name;
                            found = new Elsewhere
                            {
                                Store = dealStores.TryGetValue(S(At(best, "storeID")) ?? "", out name) ? name : "another store",
                                Price = Money(Num(At(best, "price")), "USD"),
                                Link = "https://www.cheapshark.com/redirect?dealID=" + Uri.EscapeDataString(WebUtility.UrlDecode(S(At(best, "dealID")) ?? ""))
                            };
                            if (found.Store == item.StoreName) found = null;
                        }
                    }
                }
                catch (Exception ex) { HomeNav.Log("store elsewhere " + item.Title + ": " + ex.Message); }
                ui.BeginInvoke(new Action(() => done(found)));
            });
        }

        // a few more games from the same store (its lists), for the bottom of a game's page
        public static List<StoreItem> MoreFrom(StoreItem item)
        {
            var prefix = item.Store + ":";
            var self = Key(item.Title ?? "");
            var seen = new HashSet<string> { self };
            return lists.Where(p => p.Key.StartsWith(prefix)).SelectMany(p => p.Value)
                .Where(i => !string.IsNullOrEmpty(i.Image) && seen.Add(Key(i.Title ?? ""))).Take(14).ToList();
        }

        public static string Key(string title)
        {
            var sb = new StringBuilder();
            foreach (var ch in title.ToLowerInvariant()) if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            return sb.ToString();
        }

        private static Owned OwnedIndex()
        {
            var o = new Owned();
            try
            {
                if (HomeNav.Api == null) return o;
                foreach (var g in HomeNav.Api.Database.Games)
                {
                    if (g.PluginId == SteamPlugin && !string.IsNullOrEmpty(g.GameId)) o.Steam.Add(g.GameId);
                    if (!string.IsNullOrEmpty(g.Name)) o.Names.Add(Key(g.Name));
                }
            }
            catch (Exception ex) { HomeNav.Log("store owned: " + ex.Message); }
            return o;
        }

        // ---------------- offline copy ----------------
        private static string DiskFile { get { return Path.Combine(SelenePaths.DataFolder, "store-cache.json"); } }

        private class Saved { public DateTime At; public Dictionary<string, List<StoreItem>> Lists; }

        private static void WriteDisk()
        {
            try
            {
                if (lists.Count == 0 || FromDisk) return;
                Directory.CreateDirectory(SelenePaths.DataFolder);
                File.WriteAllText(DiskFile, new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(new Saved { At = loadedAt, Lists = lists }));
            }
            catch (Exception ex) { HomeNav.Log("store cache write: " + ex.Message); }
        }

        private static void ReadDisk()
        {
            try
            {
                if (!File.Exists(DiskFile)) return;
                var saved = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Saved>(File.ReadAllText(DiskFile));
                if (saved == null || saved.Lists == null) return;
                foreach (var pair in saved.Lists) lists[pair.Key] = pair.Value;
                loadedAt = saved.At.ToLocalTime();
                FromDisk = true;
            }
            catch (Exception ex) { HomeNav.Log("store cache read: " + ex.Message); }
        }
    }
}
