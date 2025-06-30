using League.model;

namespace League.uitls
{
    public partial class MatchTabContent : UserControl
    {
        private Dictionary<TabPage, MatchTabPageContent> _tabPageContents = new Dictionary<TabPage, MatchTabPageContent>();
        public ClosableTabControl MainTabControl => closableTabControl1;
        public MatchTabContent()
        {
            InitializeComponent();
        }

        public void CreateNewTab(
            string gameName, 
            string tagLine, 
            string puuid, 
            string profileIconId,
            string summonerLevel,
            string privacy,Dictionary<string, RankedStats> rankedStats)
        {

            // 检查是否已存在
            foreach (TabPage page in MainTabControl.TabPages)
            {
                if (page.Tag as string == puuid)
                {
                    MainTabControl.SelectedTab = page;

                    // 刷新已有 Tab 内容
                    if (_tabPageContents.TryGetValue(page, out var existingContent))
                    {
                        string fullGameName = gameName + "#" + tagLine;
                        existingContent.InitiaRank(fullGameName, profileIconId, summonerLevel, privacy, rankedStats);
                        existingContent.Initialize(puuid);
                    }

                    return;
                }
            }

            // 创建新标签页
            var newTab = new TabPage($"{gameName}#{tagLine}")
            {
                Tag = puuid
            };

            
            // 创建内容控件
            var tabContent = new MatchTabPageContent();

            // 绑定数据加载方法
            tabContent.LoadDataRequested += async (p, beg, end, q) =>
            {
                //return await ((FormMain)this.ParentForm).LoadMatchDataAsync(p, beg, end, q);
                return await ((FormMain)this.ParentForm).LoadFullMatchDataAsync(p, beg, end, q);
            };

            // 绑定解析事件（带puuid参数）
            tabContent.ParsePanelRequested += async (match, puuidParam) =>
            {
                return await ((FormMain)this.ParentForm).ParseGameToPanelAsync(match, gameName,tagLine);
            };

            // 初始化控件
            tabContent.Initialize(puuid);
            string fullName = gameName + "#" + tagLine;
            tabContent.InitiaRank(fullName,profileIconId, summonerLevel, privacy,rankedStats);

            // 添加控件
            newTab.Controls.Add(tabContent);
            MainTabControl.TabPages.Add(newTab);
            MainTabControl.SelectedTab = newTab;
            _tabPageContents[newTab] = tabContent; // ❗你没有加这个，导致后续刷新失败

        }
    }
}
