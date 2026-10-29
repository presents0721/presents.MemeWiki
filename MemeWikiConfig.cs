using System.ComponentModel;

namespace presents.MemeWiki;

/// <summary>插件配置。Alife 会根据这些属性自动生成模块设置表单。</summary>
public sealed class MemeWikiConfig
{
    [DisplayName("联网补充")]
    [Description("本地词库没收录时联网查；收录了但内容太短也补一次。关闭后完全离线，只认内置 6668 条和自定义词条。")]
    public bool EnableOnline { get; set; } = true;

    [DisplayName("查 cnmeme.wiki")]
    [Description("梗百科原始数据源，网络梗最全。关掉后只查百度百科 / 维基。")]
    public bool UseCnmeme { get; set; } = true;

    [DisplayName("查百度百科")]
    [Description("泛知识、流行语、影视人物类的最好用；有「含义 / 流行时间」这类成对信息。")]
    public bool UseBaiduBaike { get; set; } = true;

    [DisplayName("查中文维基")]
    [Description("补充背景知识。个别词会命中消歧义页（一堆并列条目），那种情况会自动忽略。")]
    public bool UseWikipedia { get; set; } = true;

    [DisplayName("联网超时（秒）")]
    [Description("单个数据源的超时。3–20；查梗是聊天里临时用，超过 8 秒基本等于没查到。")]
    public int OnlineTimeoutSeconds { get; set; } = 8;

    [DisplayName("回答长度上限")]
    [Description("投给对话的文本上限（字）。600–4000；太长会把上下文顶满，默认 1500 够说清一个梗。")]
    public int MaxAnswerChars { get; set; } = 1500;

    [DisplayName("模糊匹配")]
    [Description("允许一个字之差 / 近似的写法（「赛博朋克」↔「赛博庞克」）。关闭后只认完整标题。")]
    public bool FuzzyMatch { get; set; } = true;

    [DisplayName("查「最新收录」")]
    [Description("搜索没命中时，再翻一遍 cnmeme 按更新时间倒序的前 120 条。刚出两天的梗常常还没进搜索索引，但一定在列表最前面。")]
    public bool UseLatestIndex { get; set; } = true;

    [DisplayName("自动同步新梗")]
    [Description("插件启动时（间隔超过 12 小时）自动按 updated_at 增量同步一次，几秒钟完事。关掉后只能手动同步。")]
    public bool AutoSyncDaily { get; set; } = true;

    [DisplayName("走系统代理")]
    [Description("默认直连。只有在「不开代理就完全连不上」时才打开；开着代理却规则没覆盖 cnmeme/维基时，请求会一直挂到超时，比直连更慢。")]
    public bool UseSystemProxy { get; set; } = false;

    [DisplayName("注入接梗提示")]
    [Description("在系统提示里加一段：遇到看不懂的梗先查再回、别硬解释、查到后用自己的话说。关掉后模型只靠函数说明自己判断。")]
    public bool InjectGuidance { get; set; } = true;
}
