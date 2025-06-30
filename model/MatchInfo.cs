using League.model;

namespace League
{
    public class MatchInfo
    {
        private string _resultText;
        public string ResultText
        {
            get => _resultText;
            set
            {
                _resultText = value;
                // 自动设置颜色
                ResultColor = value == "失败" ? Color.DodgerBlue : Color.Red;
            }
        }
        public Color ResultColor { get; set; }

        public string DurationText { get; set; }    //比赛时长
        public string Mode { get; set; }    //游戏模式
        public string GameTime { get; set; }    //游戏时间

        public Image HeroIcon { get; set; } //玩家头像
        public int Kills { get; set; }  //击杀数
        public int Deaths { get; set; } //死亡数
        public int Assists { get; set; }    //助攻

        public Image[] SummonerSpells { get; set; } = new Image[2]; //召唤师技能

        public Item[] Items { get; set; } // 用于保存图标+名称+描述

        // 新增核心数据
        public string GoldText { get; set; }      // 金钱（格式化为 "12k"）
        public string DamageText { get; set; }    // 英雄伤害（格式化为 "32k"）
        public string DamageTaken { get; set; }    // 承受伤害（如 "33.3k"）

        public string KPPercentage {  get; set; }   //参团率

        public List<PlayerInfo> BlueTeam { get; set; } = new List<PlayerInfo>(); // teamId=100
        public List<PlayerInfo> RedTeam { get; set; } = new List<PlayerInfo>();  // teamId=200

        // 符文信息
        public RuneInfo[] PrimaryRunes { get; set; }    // 主系符文 (0是基石)
        public RuneInfo[] SecondaryRunes { get; set; }// 副系符文

        public PlayerInfo SelfPlayer { get; set; } // 当前玩家

        public string ChampionName { get; set; }             // 英雄名
        public string ChampionDescription { get; set; }      // 英雄描述

        public string[] SpellNames { get; set; }             // 召唤师技能名称
        public string[] SpellDescriptions { get; set; }      // 召唤师技能描述

        public string[] ItemNames { get; set; }              // 装备名称
        public string[] ItemDescriptions { get; set; }       // 装备描述

        public string[] PrimaryRuneNames { get; set; }              // 主系符文名称
        public string[] PrimaryRuneDescriptions { get; set; }       // 主系符文描述

        public string[] SecondaryRuneNames { get; set; }              // 副系符文名称
        public string[] SecondaryRuneDescriptions { get; set; }       // 副系符文描述
    }
}
