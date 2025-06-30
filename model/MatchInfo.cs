//using League.model;

//namespace League
//{
//    public class MatchInfo
//    {
//        private string _resultText;
//        public string ResultText
//        {
//            get => _resultText;
//            set
//            {
//                _resultText = value;
//                // 自动设置颜色
//                ResultColor = value == "失败" ? Color.DodgerBlue : Color.Red;
//            }
//        }
//        public Color ResultColor { get; set; }

//        public string DurationText { get; set; }    //比赛时长
//        public string Mode { get; set; }    //游戏模式
//        public string GameTime { get; set; }    //游戏时间

//        public Image HeroIcon { get; set; } //玩家头像
//        public int Kills { get; set; }  //击杀数
//        public int Deaths { get; set; } //死亡数
//        public int Assists { get; set; }    //助攻

//        public Image[] SummonerSpells { get; set; } = new Image[2]; //召唤师技能

//        public Item[] Items { get; set; } // 用于保存图标+名称+描述

//        // 新增核心数据
//        public string GoldText { get; set; }      // 金钱（格式化为 "12k"）
//        public string DamageText { get; set; }    // 英雄伤害（格式化为 "32k"）
//        public string DamageTaken { get; set; }    // 承受伤害（如 "33.3k"）

//        public string KPPercentage {  get; set; }   //参团率

//        public List<PlayerInfo> BlueTeam { get; set; } = new List<PlayerInfo>(); // teamId=100
//        public List<PlayerInfo> RedTeam { get; set; } = new List<PlayerInfo>();  // teamId=200

//        // 符文信息
//        public RuneInfo[] PrimaryRunes { get; set; }    // 主系符文 (0是基石)
//        public RuneInfo[] SecondaryRunes { get; set; }// 副系符文

//        public PlayerInfo SelfPlayer { get; set; } // 当前玩家

//        public string ChampionName { get; set; }             // 英雄名
//        public string ChampionDescription { get; set; }      // 英雄描述

//        public string[] SpellNames { get; set; }             // 召唤师技能名称
//        public string[] SpellDescriptions { get; set; }      // 召唤师技能描述

//        public string[] ItemNames { get; set; }              // 装备名称
//        public string[] ItemDescriptions { get; set; }       // 装备描述

//        public string[] PrimaryRuneNames { get; set; }              // 主系符文名称
//        public string[] PrimaryRuneDescriptions { get; set; }       // 主系符文描述

//        public string[] SecondaryRuneNames { get; set; }              // 副系符文名称
//        public string[] SecondaryRuneDescriptions { get; set; }       // 副系符文描述
//    }
//}

using League.model;

public class MatchInfo
{
    public MatchInfo()
    {
        PrimaryRunes = Enumerable.Range(0, 4).Select(_ => new RuneInfo()).ToArray();
        SecondaryRunes = Enumerable.Range(0, 2).Select(_ => new RuneInfo()).ToArray();
        Items = Enumerable.Range(0, 6).Select(_ => new Item()).ToArray();
        SpellNames = new string[2];
        SpellDescriptions = new string[2];
        ItemNames = new string[6];
        ItemDescriptions = new string[6];
        PrimaryRuneNames = new string[4];
        PrimaryRuneDescriptions = new string[4];
        SecondaryRuneNames = new string[2];
        SecondaryRuneDescriptions = new string[2];
    }

    private string _resultText;
    public string ResultText
    {
        get => _resultText;
        set
        {
            _resultText = value;
            ResultColor = value == "失败" ? Color.DodgerBlue : Color.Red;
        }
    }
    public Color ResultColor { get; set; } = Color.Red;

    public string DurationText { get; set; } = "";
    public string Mode { get; set; } = "";
    public string GameTime { get; set; } = "";

    public Image HeroIcon { get; set; } = new Bitmap(1, 1);

    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }

    public Image[] SummonerSpells { get; set; } = new Image[2];

    public Item[] Items { get; set; } =
        Enumerable.Range(0, 6).Select(_ => new Item()).ToArray();

    public string GoldText { get; set; } = "";
    public string DamageText { get; set; } = "";
    public string DamageTaken { get; set; } = "";
    public string KPPercentage { get; set; } = "";

    public List<PlayerInfo> BlueTeam { get; set; } = new List<PlayerInfo>();
    public List<PlayerInfo> RedTeam { get; set; } = new List<PlayerInfo>();

    public RuneInfo[] PrimaryRunes { get; set; } =
        Enumerable.Range(0, 4).Select(_ => new RuneInfo()).ToArray();

    public RuneInfo[] SecondaryRunes { get; set; } =
        Enumerable.Range(0, 2).Select(_ => new RuneInfo()).ToArray();

    public PlayerInfo SelfPlayer { get; set; } = new PlayerInfo();

    public string ChampionName { get; set; } = "";
    public string ChampionDescription { get; set; } = "";

    public string[] SpellNames { get; set; } = new string[2];
    public string[] SpellDescriptions { get; set; } = new string[2];

    public string[] ItemNames { get; set; } = new string[6];
    public string[] ItemDescriptions { get; set; } = new string[6];

    public string[] PrimaryRuneNames { get; set; } = new string[4];
    public string[] PrimaryRuneDescriptions { get; set; } = new string[4];

    public string[] SecondaryRuneNames { get; set; } = new string[2];
    public string[] SecondaryRuneDescriptions { get; set; } = new string[2];
}
