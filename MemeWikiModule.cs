using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;

namespace presents.MemeWiki;

/// <summary>
/// 梗百科：把「一个梗到底是什么意思」这件事交给插件，而不是让模型现编。
///
/// 数据分四层，优先级从高到低：
///   1. 用户自定义词条（自己圈子的黑话，最准）
///   2. 内置词库（6668 条，离线、毫秒级）
///   3. 增量同步来的新梗标题 + 摘要（cnmeme 列表接口）
///   4. 在线三个源（cnmeme 详情 / 百度百科 / 中文维基，并发 + 缓存）
/// </summary>
[Module(
    "梗百科",
    "查网络梗 / 缩写 / 黑话的含义、用法与出处。内置 6668 条离线词库，没收录时并发查 cnmeme.wiki、百度百科、中文维基；AI 遇到不懂的梗会自己查，也支持自定义词条。",
    defaultCategory: "presents/梗与黑话")]
public sealed class MemeWikiModule(
    PluginSystem pluginSystem,
    XmlFunctionCaller functionCaller,
    ILogger<MemeWikiModule> logger,
    Interactor<MemeWikiModule> interactor) : ChatBehaviour, IConfigurable<MemeWikiConfig>
{
    public MemeWikiConfig Configuration { get; set; } = null!;

    readonly Random random = new();

    MemeStore store = new();
    UserTerms terms = null!;
    MemeSync? sync;
    MemeOnline? online;
    XmlHandler? handler;
    string pluginDirectory = "";

    long hitCount;
    long missCount;
    bool syncing;

    // ---------------- 生命周期 ----------------

    protected override Task OnAwake()
    {
        try
        {
            pluginDirectory = pluginSystem.PluginContext.GetPluginDirectoryPath("presents.MemeWiki");

            store = MemeStore.Load(Path.Combine(pluginDirectory, "Resources", "memes.json.gz"),
                message => logger.LogWarning("[MemeWiki] {Message}", message));

            terms = new UserTerms(Path.Combine(pluginDirectory, "user-terms.json"));
            terms.Load(message => logger.LogWarning("[MemeWiki] {Message}", message));

            sync = new MemeSync(Path.Combine(pluginDirectory, "synced-memes.json"), logger, Configuration.UseSystemProxy);
            int merged = store.Merge(sync.LoadOverlay(message => logger.LogWarning("[MemeWiki] {Message}", message)));

            online = new MemeOnline(Configuration.UseSystemProxy);

            handler = new XmlHandler(this)
            {
                Description = "查网络梗、拼音缩写、圈内黑话的含义 / 用法 / 出处。遇到自己不确定的梗先查再回答；也能记住或忘掉自定义词条。"
            };
            functionCaller.RegisterHandler(handler, DocumentMode.Implicit, cancellationToken: DestroyCancellationToken);

            logger.LogInformation("[MemeWiki] 词库 {Local} 条（增量 {Merged} 条，构建于 {BuiltAt}）+ 自定义 {Terms} 条",
                store.Count, merged, string.IsNullOrWhiteSpace(store.BuiltAt) ? "未知" : store.BuiltAt, terms.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[MemeWiki] 初始化失败");
        }

        return Task.CompletedTask;
    }

    protected override Task OnStart()
    {
        UpdatePrompt();

        if (Configuration.AutoSyncDaily)
        {
            // 增量同步只翻「最近更新」的前几页，几秒钟完事，所以不用挑时间：
            // 超过 12 小时就补一次，新梗最多半天就能进本地库。
            DateTime last = sync?.LastSyncAt() ?? DateTime.MinValue;
            if (DateTime.Now - last > TimeSpan.FromHours(12))
                _ = SyncInBackgroundAsync();
        }

        return Task.CompletedTask;
    }

    protected override Task OnDestroy()
    {
        online?.Dispose();
        sync?.Dispose();
        return Task.CompletedTask;
    }

    // ---------------- AI 可调用函数 ----------------

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查网络梗 / 拼音缩写 / 圈内黑话的含义、用法和出处。query 可以是词（yyds、awsl、你说的对），也可以是含梗的短句（你这句是什么梗）。查不到就直说不知道，不要硬编。")]
    public async Task SearchMeme([Description("要查的梗：词或含梗的短句")] string query = "")
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            interactor.Poke("【梗百科】要给一个词或短句才能查。");
            return;
        }

        try
        {
            string text = await BuildAnswerAsync(query.Trim());
            interactor.Poke(text);
        }
        catch (OperationCanceledException)
        {
            // 插件在销毁：不投递
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[MemeWiki] 查梗失败：{Query}", query);
            interactor.Poke($"【梗百科】查「{query}」时出错了：{ex.Message}");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("记住一条自定义词条（自己圈子的黑话 / 内部梗）。之后查这个词会优先用你给的含义。")]
    public Task RememberMeme(
        [Description("词条名，比如「开黑」")] string name,
        [Description("含义 / 怎么用")] string meaning)
    {
        (bool ok, string message) = terms.Add(name, meaning);
        interactor.Poke("【梗百科】" + message);
        logger.LogInformation("[MemeWiki] 自定义词条 {Result}：{Name}", ok ? "写入" : "失败", name);
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("忘掉一条自定义词条（只删自己加的，不会动内置词库）。")]
    public Task ForgetMeme([Description("词条名")] string name)
    {
        (_, string message) = terms.Remove(name);
        interactor.Poke("【梗百科】" + message);
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("随机抽一条梗，用来接话或整活。")]
    public Task RandomMeme()
    {
        MemeEntry? entry = store.Random(random);
        if (entry == null)
        {
            interactor.Poke("【梗百科】词库是空的。");
            return Task.CompletedTask;
        }

        MemeAnswer answer = new()
        {
            Query = entry.Title,
            Title = entry.Title,
            Tags = entry.Tags,
            Usage = entry.Usage,
            Origin = entry.Origin,
            Other = entry.Other,
            How = "随机一条",
            Url = string.IsNullOrWhiteSpace(entry.Slug) ? "" : $"https://cnmeme.wiki/词条/{Uri.EscapeDataString(entry.Slug)}"
        };

        interactor.Poke(MemeRender.Format(answer, Configuration.MaxAnswerChars));
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查看梗百科状态：词库多少条、自定义多少条、开了哪些在线源、本次运行命中率。")]
    public Task MemeWikiStatus()
    {
        List<string> sources = new();
        if (Configuration.EnableOnline)
        {
            if (Configuration.UseCnmeme) sources.Add("cnmeme.wiki");
            if (Configuration.UseBaiduBaike) sources.Add("百度百科");
            if (Configuration.UseWikipedia) sources.Add("中文维基");
        }

        interactor.Poke(MemeRender.FormatStatus(store.Count, store.BuiltAt, terms.Count, sources,
            Interlocked.Read(ref hitCount), Interlocked.Read(ref missCount), store.LatestDate));

        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("从 cnmeme.wiki 同步最近更新的词条（按更新时间倒序，几秒钟）。本地老是查不到新梗时可以手动跑一次。")]
    public async Task SyncMemeLibrary()
    {
        if (syncing)
        {
            interactor.Poke("【梗百科】正在同步，别急。");
            return;
        }

        if (sync == null)
        {
            interactor.Poke("【梗百科】同步器没初始化好。");
            return;
        }

        syncing = true;
        try
        {
            (int added, int total, string message) = await sync.SyncRecentAsync(6, DestroyCancellationToken);

            if (added > 0)
            {
                int merged = store.Merge(sync.LoadOverlay());
                message += $"，已并入内存（{merged} 条）";
            }

            interactor.Poke("【梗百科】" + message);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            syncing = false;
        }
    }

    // ---------------- 查询主流程 ----------------

    async Task<string> BuildAnswerAsync(string query)
    {
        int limit = Math.Clamp(Configuration.MaxAnswerChars, 300, 8000);

        // ① 自定义词条：自己人定的说法最贴切
        if (terms.TryMatch(query, out UserTerm term))
        {
            Interlocked.Increment(ref hitCount);

            return MemeRender.Format(new MemeAnswer
            {
                Query = query,
                Title = term.Name,
                How = "自定义词条",
                Custom = true,
                Usage = term.Meaning,
                Tags = new List<string> { "自定义" }
            }, limit);
        }

        // ② 本地词库
        List<MemeHit> local = store.Search(query, 5, Configuration.FuzzyMatch);
        MemeHit? best = local.FirstOrDefault();

        bool thin = best == null ||
                    best.Entry.Usage.Length + best.Entry.Origin.Length + best.Entry.Other.Length < 150;

        // ③ 在线补充：本地没收录、或者收录了但内容太薄时才联网
        List<OnlineSnippet> extras = new();
        bool onlineTried = false;

        if (Configuration.EnableOnline && thin && online != null)
        {
            onlineTried = true;
            extras = await online.LookupAsync(query, Configuration, logger, DestroyCancellationToken, preferCnmeme: best == null);
        }

        if (best != null)
        {
            Interlocked.Increment(ref hitCount);

            string url = string.IsNullOrWhiteSpace(best.Entry.Slug)
                ? ""
                : $"https://cnmeme.wiki/词条/{Uri.EscapeDataString(best.Entry.Slug)}";

            List<OnlineSnippet> others = extras
                .Where(x => !string.Equals(x.Title, best.Entry.Title, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return MemeRender.Format(new MemeAnswer
            {
                Query = query,
                Title = best.Entry.Title,
                Tags = best.Entry.Tags,
                Usage = best.Entry.Usage,
                Origin = best.Entry.Origin,
                Other = best.Entry.Other,
                How = best.How,
                Url = url,
                Extras = others,
                Related = local.Skip(1).Select(x => x.Entry.Title).ToList()
            }, limit);
        }

        // ④ 本地没命中：用在线结果拼
        if (extras.Count > 0)
        {
            Interlocked.Increment(ref hitCount);

            OnlineSnippet main = extras.FirstOrDefault(x => x.Source == "cnmeme.wiki") ?? extras[0];

            string other = main.Other;
            if (main.Facts.Count > 0)
            {
                string facts = string.Join(" · ", main.Facts.Select(x => $"{x.Key}：{x.Value}"));
                other = string.IsNullOrWhiteSpace(other) ? facts : facts + "\n" + other;
            }

            return MemeRender.Format(new MemeAnswer
            {
                Query = query,
                Title = string.IsNullOrWhiteSpace(main.Title) ? query : main.Title,
                Usage = main.Usage,
                Origin = main.Origin,
                Other = other,
                How = main.Source,
                Url = main.Url,
                Extras = extras.Where(x => !ReferenceEquals(x, main)).ToList(),
                Related = extras.Where(x => !ReferenceEquals(x, main))
                                .Select(x => x.Title)
                                .Where(x => !string.IsNullOrWhiteSpace(x) && x != query)
                                .ToList()
            }, limit);
        }

        // ⑤ 都没有
        Interlocked.Increment(ref missCount);
        logger.LogInformation("[MemeWiki] 未命中：{Query}（在线={Online}）", query, onlineTried);

        return MemeRender.FormatMiss(query, onlineTried, store.Count > 0);
    }

    // ---------------- 内部 ----------------

    async Task SyncInBackgroundAsync()
    {
        if (sync == null || syncing) return;

        syncing = true;
        try
        {
            (int added, int total, string message) = await sync.SyncRecentAsync(6, DestroyCancellationToken);
            if (added > 0) store.Merge(sync.LoadOverlay());

            logger.LogInformation("[MemeWiki] 每日同步：{Message}", message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[MemeWiki] 每日同步失败");
        }
        finally
        {
            syncing = false;
        }
    }

    void UpdatePrompt()
    {
        if (!Configuration.InjectGuidance) return;

        interactor.Prompt("""
            遇到看不懂的网络梗、拼音缩写（yyds / awsl / xswl）、圈内黑话、名场面台词，先调用 SearchMeme 查一下再回复，不要凭空猜。
            查到资料后用自己的话说，别把「含义 / 出处」整段念一遍；标注「仅用于理解」的出处尤其不要照搬。
            查不到就直说不知道，可以追问对方是什么意思，或者用 RememberMeme 把对方给的解释记下来。
            """);
    }
}
