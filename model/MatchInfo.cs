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
