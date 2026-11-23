// Copyright (c) Bili Copilot. All rights reserved.

namespace BiliCopilot.CommentPoll.Tests;

/// <summary>
/// 测试用的响应样本。第一条是 2026-11-23 用匿名请求真实拿到的 vote_info 响应（原样）.
/// </summary>
internal static class VoteJson
{
    /// <summary>
    /// 真实响应：vote_id=20000000，单选，进行中，4 人参与；第二个选项 0 票所以没有 cnt.
    /// </summary>
    public const string RealDetail = """
        {"code":0,"data":{"vote_info":{"vote_id":20000000,"title":"刚学的都还给老师了吗","join_num":4,"choice_cnt":1,"end_time":3929006537,"status":1,"vote_publisher":85642211,"options":[{"opt_idx":1,"opt_desc":"全还回去了","cnt":4},{"opt_idx":2,"opt_desc":"我能全文背诵"}],"name":"燕子不会离开","face":"https://i0.hdslb.com/bfs/face/abc.jpg","options_cnt":2}}}
        """;

    /// <summary>
    /// 已删除的投票：code 还是 0，data 里只剩一个 status.
    /// </summary>
    public const string DeletedDetail = """
        {"code":0,"data":{"vote_info":{"status":4}}}
        """;

    /// <summary>
    /// 多选 + 自己投过：my_votes 在 data 层，choice_cnt=2，第三项 0 票.
    /// </summary>
    public const string MultiChoiceWithMyVotes = """
        {"code":0,"data":{"my_votes":[1,3],"vote_info":{"vote_id":20000001,"title":"三选二","join_num":10,"choice_cnt":2,"end_time":0,"status":1,"vote_publisher":42,"options":[{"opt_idx":1,"opt_desc":"A","cnt":6},{"opt_idx":2,"opt_desc":"B","cnt":5},{"opt_idx":3,"opt_desc":"C"}],"name":"UP","options_cnt":3}}}
        """;

    /// <summary>
    /// my_votes 在 vote_info 里（投过之后再查会是这样），并且是充电专属.
    /// </summary>
    public const string MyVotesInsideInfo = """
        {"code":0,"data":{"vote_info":{"vote_id":20000002,"title":"充电专属","join_num":7,"choice_cnt":1,"end_time":3929006537,"status":1,"only_fans_level":10,"options":[{"opt_idx":1,"opt_desc":"好","cnt":7},{"opt_idx":2,"opt_desc":"不好","cnt":0}],"my_votes":[2]}}}
        """;

    /// <summary>
    /// 已经结束的投票：end_time 在过去，status=4.
    /// </summary>
    public const string EndedDetail = """
        {"code":0,"data":{"vote_info":{"vote_id":20000003,"title":"结束了的","join_num":2,"choice_cnt":1,"end_time":1700000000,"status":4,"options":[{"opt_idx":1,"opt_desc":"是","cnt":1},{"opt_idx":2,"opt_desc":"否","cnt":1}]}}}
        """;

    /// <summary>
    /// end_time 是脏数据（超出 DateTimeOffset 范围）.
    /// </summary>
    public const string FarEndTime = """
        {"code":0,"data":{"vote_info":{"vote_id":20000004,"title":"没有截止时间","join_num":1,"choice_cnt":0,"end_time":999999999999999,"status":1,"options":[{"opt_idx":1,"opt_desc":"唯一","cnt":1}]}}}
        """;

    /// <summary>
    /// 没有 options 的详情（面板该当成不可用）.
    /// </summary>
    public const string NoOptions = """
        {"code":0,"data":{"vote_info":{"vote_id":20000005,"title":"缺选项","join_num":0,"choice_cnt":1}}}
        """;

    /// <summary>
    /// 投票成功、响应带回新票数.
    /// </summary>
    public const string SubmitWithOptions = """
        {"code":0,"data":{"vote_info":{"vote_id":20000000,"options":[{"opt_idx":1,"opt_desc":"全还回去了","cnt":5},{"opt_idx":2,"opt_desc":"我能全文背诵","cnt":1}],"my_votes":[2],"join_num":6}}}
        """;

    /// <summary>
    /// 投票成功但响应没带票数（只回 code），要去重查.
    /// </summary>
    public const string SubmitWithoutOptions = """
        {"code":0,"data":{}}
        """;

    /// <summary>
    /// 重查之后的样子：2 号选项多了一票.
    /// </summary>
    public const string RefreshedDetail = """
        {"code":0,"data":{"vote_info":{"vote_id":20000000,"title":"刚学的都还给老师了吗","join_num":5,"choice_cnt":1,"end_time":3929006537,"status":1,"vote_publisher":85642211,"options":[{"opt_idx":1,"opt_desc":"全还回去了","cnt":4},{"opt_idx":2,"opt_desc":"我能全文背诵","cnt":1}],"name":"燕子不会离开","my_votes":[2]}}}
        """;

    /// <summary>
    /// 服务端拒绝（投票已结束）.
    /// </summary>
    public const string Rejected = """
        {"code":500001,"message":"投票已结束"}
        """;

    /// <summary>
    /// 没登录.
    /// </summary>
    public const string NotSignedIn = """
        {"code":-101,"message":"账号未登录"}
        """;

    /// <summary>
    /// 投票接口自己的没登录码.
    /// </summary>
    public const string VoteNotSignedIn = """
        {"code":100,"message":"未登录"}
        """;

    /// <summary>
    /// 服务端回了非 JSON（票已经投出去，只是结果读不到）.
    /// </summary>
    public const string NotJson = "<html><body>502 Bad Gateway</body></html>";

    /// <summary>
    /// 请求被风控挡下.
    /// </summary>
    public const string RiskControl = "{}";
}
