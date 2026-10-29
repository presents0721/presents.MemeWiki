using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace presents.MemeWiki;

/// <summary>
/// 在线补充：cnmeme.wiki（梗库本体）+ 百度百科（泛知识）+ 中文维基（背景）。
///
/// 三条硬约束（都是踩过的坑）：
///   1. **强制 IPv4**：国内很多网络 IPv6 是黑洞，Cloudflare 的 AAAA 连 0.01 秒就失败，
///      而 .NET 默认会先试 IPv6，一路耗到超时。这里用 ConnectCallback 只连 A 记录。
///   2. **不等最慢的那个**：三个源并发，谁在预算内回来就先算谁；两个够了就不再等第三个。
///   3. **列表先于详情**：cnmeme 的列表接口已经带摘要，先拿摘要兜底，剩余预算再去取详情正文。
/// </summary>
internal sealed class MemeOnline : IDisposable
{
    const string CnmemeApi = "https://cnmeme.wiki/api/v1/public/memes";
    const string BaiduApi = "https://baike.baidu.com/api/openapi/BaikeLemmaCardApi";

    static readonly TimeSpan HitTtl = TimeSpan.FromHours(6);
    static readonly TimeSpan MissTtl = TimeSpan.FromMinutes(10);

    readonly HttpClient http;
    readonly ConcurrentDictionary<string, CacheItem> cache = new(StringComparer.Ordinal);

    sealed record CacheItem(List<OnlineSnippet> Snippets, DateTime At, bool Hit);

    sealed record LatestItem(string Title, string Slug, string Summary, string Date);

    // 「最新收录」列表缓存：刚出的梗常常还没进搜索索引，但一定在按更新时间排序的列表最前面
    List<LatestItem>? latestCache;
    DateTime latestCacheAt = DateTime.MinValue;

    public MemeOnline(bool useSystemProxy = false)
    {
        SocketsHttpHandler handler = new()
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(6),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),

            // 默认直连。国内的系统代理经常只对部分域名放行：
            // 代理规则一旦挡住 cnmeme / 维基，请求会一直挂着直到超时，比直连慢一个数量级。
            UseProxy = useSystemProxy,
            ConnectCallback = Ipv4Connect.ConnectAsync
        };

        http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json,text/html;q=0.9,*/*;q=0.8");
    }


    /// <summary>
    /// 并发查所有启用的源。
    /// <paramref name="preferCnmeme"/> = true 时（本地没命中，说明查的是新梗），会等 cnmeme 到预算用完为止，
    /// 别的源先回来只当补充；否则（本地已命中，只是补资料）拿够两条就收工。
    /// </summary>
    public async Task<List<OnlineSnippet>> LookupAsync(string query, MemeWikiConfig config, ILogger logger,
        CancellationToken token, bool preferCnmeme = false)
    {
        string key = MemeText.Normalize(query);
        if (key.Length == 0) return new List<OnlineSnippet>();

        if (cache.TryGetValue(key, out CacheItem? cached))
        {
            TimeSpan age = DateTime.UtcNow - cached.At;
            if (age < (cached.Hit ? HitTtl : MissTtl))
                return cached.Snippets;
        }

        int budget = Math.Clamp(config.OnlineTimeoutSeconds, 3, 20);

        using CancellationTokenSource scope = CancellationTokenSource.CreateLinkedTokenSource(token);
        scope.CancelAfter(TimeSpan.FromSeconds(budget + 1));
        CancellationToken scoped = scope.Token;

        List<Task<OnlineSnippet?>> tasks = new();

        if (config.UseCnmeme)
            tasks.Add(Guard("cnmeme", () => CnmemeAsync(query, budget, scoped, config.UseLatestIndex), logger));

        // 百度百科 / 维基更适合「一个词」而不是一整句话
        bool looksLikeWord = key.Length <= 12;

        if (config.UseBaiduBaike && looksLikeWord)
            tasks.Add(Guard("百度百科", () => BaiduAsync(query, Math.Min(budget, 6), scoped), logger));

        if (config.UseWikipedia && looksLikeWord)
            tasks.Add(Guard("维基百科", () => WikipediaAsync(query, Math.Min(budget, 5), scoped), logger));

        if (tasks.Count == 0) return new List<OnlineSnippet>();

        Task<OnlineSnippet?>? cnmemeTask = tasks.Count > 0 && config.UseCnmeme ? tasks[0] : null;

        List<OnlineSnippet> snippets = new();

        // 竞速：总预算 budget 秒；一旦拿到第一条，再给其余源 2 秒宽限就收工。
        // 这样「百度秒回、cnmeme 慢、维基被墙」的常见组合不会把用户按在 8 秒上。
        DateTime hardDeadline = DateTime.UtcNow.AddSeconds(budget);
        DateTime? graceDeadline = null;

        // 就算这次指望 cnmeme，也不给它吃满预算：聊天里等 6 秒已经是忍耐极限了
        DateTime cnmemeDeadline = DateTime.UtcNow.AddSeconds(Math.Min(budget, 6));

        while (tasks.Count > 0)
        {
            // 还没拿到 cnmeme 且这次就指望它（本地没命中）时，不吃宽限：等满预算
            bool waitingCnmeme = preferCnmeme && cnmemeTask != null && tasks.Contains(cnmemeTask);

            DateTime until = waitingCnmeme && cnmemeDeadline < hardDeadline ? cnmemeDeadline : hardDeadline;
            if (!waitingCnmeme && graceDeadline.HasValue && graceDeadline.Value < until)
                until = graceDeadline.Value;

            TimeSpan remain = until - DateTime.UtcNow;
            if (remain <= TimeSpan.Zero) break;

            Task delay = Task.Delay(remain, CancellationToken.None);
            Task finished = await Task.WhenAny(tasks.Cast<Task>().Append(delay));

            if (finished == delay) break;

            Task<OnlineSnippet?> task = (Task<OnlineSnippet?>)finished;
            tasks.Remove(task);

            OnlineSnippet? snippet = await task;

            bool gotCnmeme = false;
            if (snippet != null && !snippet.IsEmpty)
            {
                snippets.Add(snippet);
                gotCnmeme = snippet.Source == "cnmeme.wiki";
            }

            if (preferCnmeme && gotCnmeme) break;                                    // 主源到手，够了
            if (snippets.Count == 1) graceDeadline = DateTime.UtcNow.AddSeconds(2);   // 已经有一条能用了
            if (snippets.Count >= 2) break;                                           // 两个源够了
        }

        // 没赶上预算的任务别丢着跑：取消掉，避免它们在后台继续发请求
        if (tasks.Count > 0)
        {
            try { scope.Cancel(); } catch { }
        }

        cache[key] = new CacheItem(snippets, DateTime.UtcNow, snippets.Count > 0);

        if (cache.Count > 500)
        {
            DateTime edge = DateTime.UtcNow - HitTtl;
            foreach (var pair in cache.Where(x => x.Value.At < edge).Take(100).ToList())
                cache.TryRemove(pair.Key, out _);
        }

        return snippets;
    }

    static async Task<OnlineSnippet?> Guard(string name, Func<Task<OnlineSnippet?>> action, ILogger logger)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("[MemeWiki] {Source} 超时", name);
        }
        catch (Exception ex)
        {
            logger.LogDebug("[MemeWiki] {Source} 查询失败：{Message}", name, ex.Message);
        }

        return null;
    }

    static CancellationTokenSource WithTimeout(int seconds, CancellationToken token)
    {
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        return cts;
    }

    // ---------------- cnmeme.wiki ----------------

    async Task<OnlineSnippet?> CnmemeAsync(string query, int budget, CancellationToken token, bool useLatest)
    {
        string listUrl = $"{CnmemeApi}?q={Uri.EscapeDataString(query)}&limit=5";

        // cnmeme 走 Cloudflare，国内直连时好时坏：给一次重试，单次超时砍半，
        // 两次加起来仍在预算内——比一次等满 6 秒然后彻底失败划算。
        JsonDocument? list = null;

        for (int attempt = 0; attempt < 2 && list == null; attempt++)
        {
            try
            {
                using CancellationTokenSource listCts = WithTimeout(Math.Max(2, Math.Min(3, budget / 2)), token);
                list = await GetJsonAsync(listUrl, listCts.Token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                if (attempt == 0) await Task.Delay(250, token);
            }
        }

        if (list == null) return null;

        using JsonDocument listDoc = list;

        if (!listDoc.RootElement.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
            return null;

        string normalized = MemeText.Normalize(query);
        string? slug = null;
        string title = query;
        string summary = "";

        // ① 同名优先
        foreach (JsonElement item in items.EnumerateArray())
        {
            string t = item.TryGetProperty("title", out JsonElement te) ? te.GetString() ?? "" : "";
            string s = item.TryGetProperty("slug", out JsonElement se) ? se.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(s)) continue;

            if (MemeText.Normalize(t) == normalized)
            {
                slug = s;
                title = t;
                summary = item.TryGetProperty("summary", out JsonElement me) ? me.GetString() ?? "" : "";
                break;
            }
        }

        // ② 完整标题出现在查询里
        if (slug == null)
        {
            foreach (JsonElement item in items.EnumerateArray())
            {
                string t = item.TryGetProperty("title", out JsonElement te) ? te.GetString() ?? "" : "";
                string s = item.TryGetProperty("slug", out JsonElement se) ? se.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(s)) continue;

                string nt = MemeText.Normalize(t);
                if (nt.Length >= 2 && normalized.Contains(nt, StringComparison.Ordinal))
                {
                    slug = s;
                    title = t;
                    summary = item.TryGetProperty("summary", out JsonElement me) ? me.GetString() ?? "" : "";
                    break;
                }
            }
        }

        // ③ 查询是标题的一部分（yyds → YYDS的完整写法）
        if (slug == null)
        {
            foreach (JsonElement item in items.EnumerateArray())
            {
                string t = item.TryGetProperty("title", out JsonElement te) ? te.GetString() ?? "" : "";
                string s = item.TryGetProperty("slug", out JsonElement se) ? se.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(s)) continue;

                string nt = MemeText.Normalize(t);
                if (nt.Length >= 2 && nt.Contains(normalized, StringComparison.Ordinal))
                {
                    slug = s;
                    title = t;
                    summary = item.TryGetProperty("summary", out JsonElement me) ? me.GetString() ?? "" : "";
                    break;
                }
            }
        }

        // ④ 最新列表兜底：刚出两天的梗常常还没进搜索索引，但一定在「按更新时间倒序」的最前面
        if (string.IsNullOrWhiteSpace(slug) && useLatest)
        {
            foreach (LatestItem item in await GetLatestAsync(token))
            {
                string nt = MemeText.Normalize(item.Title);
                if (nt.Length < 2) continue;

                if (nt == normalized ||
                    normalized.Contains(nt, StringComparison.Ordinal) ||
                    nt.Contains(normalized, StringComparison.Ordinal))
                {
                    slug = item.Slug;
                    title = item.Title;
                    summary = item.Summary;
                    break;
                }
            }
        }

        // 到这里还没对上就认输。**不能拿列表第一条凑数**：
        // 搜 yyds 时 cnmeme 会返回一堆热门梗，随手取第一条就会答成「我们不说XX」。
        if (string.IsNullOrWhiteSpace(slug)) return null;

        string url = $"https://cnmeme.wiki/词条/{Uri.EscapeDataString(slug)}";
        string body = "";

        // ④ 剩余预算够才去取正文（详情比列表慢一倍）
        try
        {
            using CancellationTokenSource detailCts = WithTimeout(Math.Max(2, budget - 3), token);
            using JsonDocument detail = await GetJsonAsync($"{CnmemeApi}/{Uri.EscapeDataString(slug)}", detailCts.Token);

            if (detail.RootElement.TryGetProperty("item", out JsonElement item) && item.ValueKind == JsonValueKind.Object)
            {
                if (item.TryGetProperty("title", out JsonElement t2) && t2.GetString() is { Length: > 0 } dt)
                    title = dt;

                body = MemeText.HtmlToText(item.TryGetProperty("content_html", out JsonElement html) ? html.GetString() : null);
            }
        }
        catch (Exception)
        {
            // 详情失败就用列表摘要，不算整条失败
        }

        if (body.Length == 0) body = MemeText.Tidy(summary);
        if (body.Length == 0) return null;

        (string usage, string origin, string other) = MemeText.SplitSections(body);

        return new OnlineSnippet
        {
            Source = "cnmeme.wiki",
            Title = title,
            Usage = usage,
            Origin = origin,
            Other = other,
            Url = url
        };
    }

    /// <summary>取「最近更新」的前 120 条（缓存 30 分钟）。失败时返回上一次的缓存或空表。</summary>
    async Task<List<LatestItem>> GetLatestAsync(CancellationToken token)
    {
        if (latestCache != null && DateTime.UtcNow - latestCacheAt < TimeSpan.FromMinutes(30))
            return latestCache;

        string url = $"{CnmemeApi}?limit=120&sort=updated_at&order=desc";

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using CancellationTokenSource cts = WithTimeout(4, token);
                using JsonDocument doc = await GetJsonAsync(url, cts.Token);

                if (!doc.RootElement.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
                    break;

                List<LatestItem> list = new();

                foreach (JsonElement item in items.EnumerateArray())
                {
                    string title = item.TryGetProperty("title", out JsonElement te) ? te.GetString() ?? "" : "";
                    string slug = item.TryGetProperty("slug", out JsonElement se) ? se.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(slug)) continue;

                    string summary = item.TryGetProperty("summary", out JsonElement me) ? me.GetString() ?? "" : "";
                    list.Add(new LatestItem(title, slug, summary, FirstDate(item)));
                }

                if (list.Count > 0)
                {
                    latestCache = list;
                    latestCacheAt = DateTime.UtcNow;
                    return list;
                }
            }
            catch (Exception)
            {
                // 兜底失败就当作没有，不影响主流程
            }
        }

        return latestCache ?? new List<LatestItem>();
    }

    static string FirstDate(JsonElement item)
    {
        foreach (string key in new[] { "updated_at", "created_at" })
        {
            if (!item.TryGetProperty(key, out JsonElement value)) continue;

            string text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            if (text.Length >= 10) return text[..10];
        }

        return "";
    }

    // ---------------- 百度百科 ----------------

    async Task<OnlineSnippet?> BaiduAsync(string query, int budget, CancellationToken token)
    {
        using CancellationTokenSource cts = WithTimeout(budget, token);

        string url = $"{BaiduApi}?scope=103&format=json&appid=379020&bk_length=600&bk_key={Uri.EscapeDataString(query)}";
        using JsonDocument doc = await GetJsonAsync(url, cts.Token);

        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        string abstractText = MemeText.HtmlToText(root.TryGetProperty("abstract", out JsonElement ab) ? ab.GetString() : null);
        string desc = MemeText.Tidy(root.TryGetProperty("desc", out JsonElement de) ? de.GetString() : null);
        string title = root.TryGetProperty("title", out JsonElement ti) ? ti.GetString() ?? query : query;
        string pageUrl = root.TryGetProperty("url", out JsonElement u) ? u.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(abstractText) && string.IsNullOrWhiteSpace(desc)) return null;

        Dictionary<string, string> facts = new();
        if (root.TryGetProperty("card", out JsonElement card) && card.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in card.EnumerateArray())
            {
                if (facts.Count >= 4) break;

                string name = item.TryGetProperty("name", out JsonElement ne) ? ne.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (name is "外文名" or "中文名" or "别名") continue;

                string value = "";
                if (item.TryGetProperty("value", out JsonElement ve))
                {
                    if (ve.ValueKind == JsonValueKind.Array)
                        value = string.Join("、", ve.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)));
                    else if (ve.ValueKind == JsonValueKind.String)
                        value = ve.GetString() ?? "";
                }

                // 卡片值里常常混着 <a href=...>：剥干净再用
                value = MemeText.Tidy(MemeText.HtmlToText(value));
                if (value.Length == 0) continue;

                facts[name] = MemeText.Clip(value, 60);
            }
        }

        return new OnlineSnippet
        {
            Source = "百度百科",
            Title = title,
            Usage = abstractText,
            Other = desc,
            Url = string.IsNullOrWhiteSpace(pageUrl) ? "" : pageUrl,
            Facts = facts
        };
    }

    // ---------------- 中文维基 ----------------

    async Task<OnlineSnippet?> WikipediaAsync(string query, int budget, CancellationToken token)
    {
        using CancellationTokenSource cts = WithTimeout(budget, token);

        string url = "https://zh.wikipedia.org/api/rest_v1/page/summary/" + Uri.EscapeDataString(query);
        using JsonDocument doc = await GetJsonAsync(url, cts.Token);

        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        string type = root.TryGetProperty("type", out JsonElement te) ? te.GetString() ?? "" : "";
        if (type == "disambiguation") return null;     // 消歧义页是一串并列条目，对解释梗没用

        string extract = root.TryGetProperty("extract", out JsonElement ee) ? ee.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(extract)) return null;

        string title = root.TryGetProperty("title", out JsonElement ti) ? ti.GetString() ?? query : query;
        string pageUrl = "";
        if (root.TryGetProperty("content_urls", out JsonElement cu) &&
            cu.TryGetProperty("desktop", out JsonElement desktop) &&
            desktop.TryGetProperty("page", out JsonElement page))
            pageUrl = page.GetString() ?? "";

        return new OnlineSnippet
        {
            Source = "维基百科",
            Title = title,
            Usage = MemeText.Tidy(extract),
            Url = pageUrl
        };
    }

    // ---------------- 基础 ----------------

    async Task<JsonDocument> GetJsonAsync(string url, CancellationToken token)
    {
        using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}");

        await using Stream stream = await response.Content.ReadAsStreamAsync(token);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token);
    }

    public void Dispose() => http.Dispose();
}
