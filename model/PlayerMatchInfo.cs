namespace League.model
{
    public class PlayerMatchInfo
    {
        public PlayerInfo Player { get; set; }
        public List<ListViewItem> MatchItems { get; set; } = new List<ListViewItem>();  
        public ImageList HeroIcons { get; set; } = new ImageList { ImageSize = new Size(20, 20) };

        //用于表示该对象是否来自缓存
        public bool IsFromCache { get; set; }

        // 新增：用于存储比赛的唯一标识（gameId + teamId）
        public List<string> MatchKeys { get; set; } = new List<string>();

        // 读取 MatchKeys 作为 Matches
        public IEnumerable<string> Matches => MatchKeys;
        //public List<string> Matches { get; set; } = new List<string>();

        public string PartyId { get; set; } // 加上这个字段用于判断组队身份
    }

}
