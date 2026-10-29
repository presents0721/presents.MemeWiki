using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace presents.MemeWiki;

/// <summary>
/// 内置词库的一条梗。字段名用单字母是为了压体积：
/// 6668 条 × 每个键省 5~8 字节，压缩包里能省下几十 KB，解压后的对象也少一层。
/// </summary>
internal sealed class MemeEntry
{
    /// <summary>标题（唯一）。</summary>
    [JsonPropertyName("t")] public string Title { get; set; } = "";

    /// <summary>cnmeme 的 slug，用于拼来源链接和在线取详情。</summary>
    [JsonPropertyName("s")] public string Slug { get; set; } = "";

    /// <summary>标签（分类，不是别名）。</summary>
    [JsonPropertyName("g")] public List<string> Tags { get; set; } = new();

    /// <summary>怎么用 / 怎么接。</summary>
    [JsonPropertyName("u")] public string Usage { get; set; } = "";

    /// <summary>出处 / 来源。</summary>
    [JsonPropertyName("o")] public string Origin { get; set; } = "";

    /// <summary>其它补充。</summary>
    [JsonPropertyName("x")] public string Other { get; set; } = "";

    /// <summary>条目在 cnmeme 上的最后更新时间（增量同步时用来判断新旧）。</summary>
    [JsonPropertyName("d")] public string UpdatedAt { get; set; } = "";
}

internal sealed class MemeData
{
    [JsonPropertyName("built_at")] public string BuiltAt { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("entries")] public List<MemeEntry> Entries { get; set; } = new();
}

/// <summary>本地检索的一条命中。</summary>
internal sealed class MemeHit
{
    public required MemeEntry Entry { get; init; }

    /// <summary>越大越可信：同名 1000，包含 700–900，模糊 400–500。</summary>
    public int Score { get; init; }

    /// <summary>命中方式的中文说明，给回答里的「命中方式」用。</summary>
    public string How { get; init; } = "";
}

/// <summary>在线源返回的一段资料。</summary>
internal sealed class OnlineSnippet
{
    public string Source { get; init; } = "";
    public string Title { get; init; } = "";
    public string Usage { get; init; } = "";
    public string Origin { get; init; } = "";
    public string Other { get; init; } = "";
    public string Url { get; init; } = "";

    /// <summary>结构化补充信息（百度百科的「含义 / 流行时间」这类）。</summary>
    public Dictionary<string, string> Facts { get; init; } = new();

    public bool IsEmpty => string.IsNullOrWhiteSpace(Usage) && string.IsNullOrWhiteSpace(Origin) && string.IsNullOrWhiteSpace(Other);
}

/// <summary>自定义词条（用户自己定义的梗 / 黑话），优先级最高。</summary>
internal sealed class UserTerm
{
    public string Name { get; set; } = "";
    public string Meaning { get; set; } = "";
    public string At { get; set; } = "";
}

/// <summary>一次查询的完整结果，供渲染层拼文本。</summary>
internal sealed class MemeAnswer
{
    public string Query { get; init; } = "";
    public string Title { get; init; } = "";
    public List<string> Tags { get; init; } = new();
    public string Usage { get; init; } = "";
    public string Origin { get; init; } = "";
    public string Other { get; init; } = "";

    /// <summary>命中方式（同名 / 标题包含 / 模糊 / 自定义 / 在线）。</summary>
    public string How { get; init; } = "";

    public string Url { get; init; } = "";

    /// <summary>在线补充（可能为空）。</summary>
    public List<OnlineSnippet> Extras { get; init; } = new();

    /// <summary>同批检索到的其它候选标题。</summary>
    public List<string> Related { get; init; } = new();

    /// <summary>true = 来自用户自定义词条。</summary>
    public bool Custom { get; init; }
}
