﻿using Newtonsoft.Json.Linq;

namespace League.model
{
    public class RankedStats
    {
        private const string SoloQueueKey = "RANKED_SOLO_5x5";  //单双排
        private const string FlexQueueKey = "RANKED_FLEX_SR";   //灵活排位

        public string QueueType { get; private set; }   // 队列类型
        public string Tier { get; private set; }    // 段位（如：黄金）
        public string Division { get; private set; }    // 小段（如：IV）
        public int Wins { get; private set; }   // 胜场
        public int Losses { get; private set; } // 负场
        public int LeaguePoints { get; private set; }   // 胜点
        public int TotalGames => Wins + Losses; // 总场次
        public double WinRate => TotalGames > 0 ? Math.Round(Wins * 100.0 / TotalGames, 2) : 0; // 胜率

        // 格式化后的段位显示
        public string FormattedTier => FormatTierDisplay();

        /// <summary>
        /// 从JSON直接创建双队列数据字典
        /// </summary>
        public static Dictionary<string, RankedStats> FromJson(JObject rankedJson)
        {
            var result = new Dictionary<string, RankedStats>
            {
                { "单双排", CreateFromQueueData(rankedJson?.SelectToken("$.queueMap.RANKED_SOLO_5x5")) },
                { "灵活组排", CreateFromQueueData(rankedJson?.SelectToken("$.queueMap.RANKED_FLEX_SR")) }
            };

            // 确保至少返回空数据
            result["单双排"] ??= new RankedStats { Tier = "未定级" };
            result["灵活组排"] ??= new RankedStats { Tier = "未定级" };

            return result;
        }

        /// <summary>
        /// 创建单个队列实例
        /// </summary>
        private static RankedStats CreateFromQueueData(JToken queueData)
        {
            if (queueData == null || !queueData.HasValues)
                return new RankedStats { Tier = "未定级" };

            return new RankedStats
            {
                QueueType = queueData["queueType"]?.ToString() ?? string.Empty,
                Tier = NormalizeTier(queueData["tier"]?.ToString()),
                Division = NormalizeDivision(queueData["division"]?.ToString()),
                Wins = queueData["wins"]?.ToObject<int>() ?? 0,
                Losses = queueData["losses"]?.ToObject<int>() ?? 0,
                LeaguePoints = queueData["leaguePoints"]?.ToObject<int>() ?? 0
            };
        }

        /// <summary>
        /// 处理段位显示逻辑
        /// </summary>
        private string FormatTierDisplay()
        {
            if (string.IsNullOrEmpty(Tier) || Tier == "NONE")
                return "未定级";

            // 段位中英对照表
            var tierMap = new Dictionary<string, string>
            {
                {"IRON", "黑铁"},
                {"BRONZE", "青铜"},
                {"SILVER", "白银"},
                {"GOLD", "黄金"},
                {"PLATINUM", "铂金"},
                {"EMERALD", "翡翠"},
                {"DIAMOND", "钻石"},
                {"MASTER", "大师"},
                {"GRANDMASTER", "宗师"},
                {"CHALLENGER", "最强王者"}
            };

            // 获取中文段位名称
            if (!tierMap.TryGetValue(Tier, out var chineseTier))
                chineseTier = "未知";  // 处理意外值

            // 处理特殊段位（无小段）
            var isHighTier = Tier is "MASTER" or "GRANDMASTER" or "CHALLENGER";

            return isHighTier ?
                chineseTier :
                $"{chineseTier} {ToChineseDivision(Division)}";
        }

        /// <summary>
        /// 将罗马数字转换为中文显示
        /// </summary>
        private static string ToChineseDivision(string division)
        {
            return division switch
            {
                "I" => "I",
                "II" => "II",
                "III" => "III",
                "IV" => "IV",
                _ => ""
            };
        }

        /// <summary>
        /// 标准化段位数据
        /// </summary>
        private static string NormalizeTier(string rawTier)
        {
            return string.IsNullOrWhiteSpace(rawTier) || rawTier == "NONE"
                ? "未定级"
                : rawTier;
        }

        /// <summary>
        /// 处理小段位显示
        /// </summary>
        private static string NormalizeDivision(string rawDivision)
        {
            return rawDivision == "NA" || string.IsNullOrEmpty(rawDivision)
                ? string.Empty
                : rawDivision;
        }
    }
}
