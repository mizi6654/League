using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using League.model;
using League.uitls;
using Newtonsoft.Json.Linq;

namespace League
{
    public partial class FormMain : Form
    {
        //用来轮询检测是否已经连接lcu api客户端
        private AsyncPoller _lcuPoller = new AsyncPoller();
        private bool lcuReady = false; // 表示是否已经初始化过了

        private MatchTabContent _matchTabContent;

        private CancellationTokenSource _watcherCts;

        //OnChampSelectStart() 里启动一个 内部轮询任务
        private CancellationTokenSource _champSelectCts;

        private int myTeamId = 0;
        //存储我方队伍选择的英雄状态
        private List<string> lastChampSelectSnapshot = new List<string>();

        //判断是否已有缓存，如果有直接返回缓存，跳过网络查询
        private readonly Dictionary<long, PlayerMatchInfo> _cachedPlayerMatchInfos = new Dictionary<long, PlayerMatchInfo>();

        //显示卡片并缓存当前 summoner → championId
        private Dictionary<long, int> _currentChampBySummoner = new Dictionary<long, int>();
        private Dictionary<long, int> _summonerToColMap = new Dictionary<long, int>(); // optional: 提供一层对位映射

        // 玩家头像全局缓存
        private static readonly ConcurrentDictionary<string, Image> _imageCache = new();

        // key: summonerId, value: 缓存的对局信息
        private Dictionary<long, PlayerMatchInfo> playerMatchCache = new Dictionary<long, PlayerMatchInfo>();
        private Dictionary<(int row, int column), (long summonerId, int championId)> playerCache = new Dictionary<(int, int), (long, int)>();

        // summonerId → PlayerCardControl 映射，便于染色时快速获取控件
        private readonly ConcurrentDictionary<long, PlayerCardControl> _cardBySummonerId = new();

        private bool _gameEndHandled = false;

        // 类成员声明，确保只创建一次，用来显示侧边栏tabControl提示文本
        private ToolTip tip = new ToolTip();
        private int _lastIndex = -1;

        private readonly Poller _tab1Poller = new Poller();
        private Panel _waitingPanel;

        bool _isGame = false;

        private bool _champSelectMessageSent = false;   //队伍选人阶段发送消息标志

        //定义两个字段，用来存储我方队伍与敌方队伍的数据
        private JArray _cachedMyTeam;
        private JArray _cachedEnemyTeam;


        public FormMain()
        {
            InitializeComponent();
        }

        public static class Globals
        {
            public static LcuSession lcuClient = new LcuSession();
            public static string CurrentSummonerId;
            public static string CurrentPuuid;
        }

        private async void FormMain_Load(object sender, EventArgs e)
        {
            try
            {
                var img1 = Image.FromFile(AppDomain.CurrentDomain.BaseDirectory + @"\Assets\Defaults\01.png");
                var img2 = Image.FromFile(AppDomain.CurrentDomain.BaseDirectory + @"\Assets\Defaults\02.png");
                var img3 = Image.FromFile(AppDomain.CurrentDomain.BaseDirectory + @"\Assets\Defaults\03.png");

                imageTabControl1.TabPages[0].Tag = img1;
                imageTabControl1.TabPages[1].Tag = img2;
                imageTabControl1.TabPages[2].Tag = img3;

                tip = new ToolTip
                {
                    AutoPopDelay = 5000,  // 提示显示 5 秒
                    InitialDelay = 100,   // 鼠标悬停 0.5 秒后才显示
                    ReshowDelay = 100,    // 再次显示的延迟
                    ShowAlways = true    // 即使控件不在活动状态也显示
                };

                // 读取本地版本
                var localVersion = VersionInfo.GetLocalVersion();

                // 获取远程版本
                var remoteVersion = await VersionInfo.GetRemoteVersion();

                if (remoteVersion != null)
                {
                    if (localVersion != null && remoteVersion.version == localVersion.version)
                    {
                        Debug.WriteLine($"当前已是最新版本：{remoteVersion.version}");
                    }
                    else
                    {
                        var changelogStr = string.Join("\n", remoteVersion.changelog);

                        Debug.WriteLine($"更新内容：\n版本：{remoteVersion.version}\n日期：{remoteVersion.date}\n{changelogStr}");

                        var msg = $"检测到新版本 {remoteVersion.version} ({remoteVersion.date})\n\n" + changelogStr;

                        var result = MessageBox.Show(msg, "版本更新", MessageBoxButtons.OKCancel);
                        if (result == DialogResult.OK)
                        {
                            // 启动 Updater.exe
                            Process.Start(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "update", "Update.exe"));
                            Environment.Exit(0);
                        }
                    }
                }
                else
                {
                    Debug.WriteLine("未检测到新版本。");
                }


                // 绑定 MouseMove 事件，动态显示对应标签的提示
                imageTabControl1.MouseMove += ImageTabControl1_MouseMove;

                // 启动轮询 LCU 检测
                StartLcuConnectPolling();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[全局初始化异常] {ex.Message}");
            }
        }

        private void ImageTabControl1_MouseMove(object sender, MouseEventArgs e)
        {
            for (int i = 0; i < imageTabControl1.TabPages.Count; i++)
            {
                Rectangle r = imageTabControl1.GetTabRect(i);
                if (r.Contains(e.Location))
                {
                    if (_lastIndex != i)
                    {
                        _lastIndex = i;

                        // 将屏幕坐标转成控件坐标
                        Point clientPos = imageTabControl1.PointToClient(Cursor.Position);
                        // 在鼠标附近显示 ToolTip
                        tip.Show(imageTabControl1.TabPages[i].Text, imageTabControl1, clientPos.X + 10, clientPos.Y + 10, 1500);
                    }
                    return;
                }
            }
            // 鼠标不在任何标签上，清除提示
            tip.SetToolTip(imageTabControl1, null);
            _lastIndex = -1;
        }

        private async void btn_search_Click(object sender, EventArgs e)
        {
            if (!lcuReady)
            {
                MessageBox.Show("LCU 客户端未连接，请先登录游戏并稍后重试！");
                return;
            }

            string input = txtGameName.Text.Trim();
            if (!input.Contains("#"))
            {
                MessageBox.Show("请输入完整名称，如：玩家名#区号");
                return;
            }

            var summoner = await Globals.lcuClient.GetSummonerByNameAsync(input);
            if (summoner == null)
            {
                MessageBox.Show("玩家不存在,本软件只能查询相同大区玩家!");
                return;
            }

            // 根据puuid获取原始数据
            var rankedJson = await Globals.lcuClient.GetCurrentRankedStatsAsync(summoner["puuid"].ToString());

            // 直接通过类方法解析
            var rankedStats = RankedStats.FromJson(rankedJson);

            string privacyStatus = "隐藏";
            if (summoner["privacy"]?.ToString().Equals("PUBLIC", StringComparison.OrdinalIgnoreCase) == true)
                privacyStatus = "公开";
            // 直接创建标签页（不再需要单独设置信息）
            _matchTabContent.CreateNewTab(
                summoner["gameName"].ToString(),
                summoner["tagLine"].ToString(),
                summoner["puuid"].ToString(),
                summoner["profileIconId"].ToString(),
                summoner["summonerLevel"].ToString(),
                privacyStatus,
                rankedStats
            );
        }

        //初始化资源
        private async Task InitializeLcuResources()
        {
            await Globals.lcuClient.LoadChampionsAsync();
            await Globals.lcuClient.LoadItemsAsync();
            await Globals.lcuClient.LoadSpellsAsync();
            await Globals.lcuClient.LoadRunesAsync();
        }

        #region 软件启动，轮询检测 LCU 连接
        /// <summary>
        /// 启动窗口，轮询监听是否登录了lcu客户端
        /// </summary>
        private void StartLcuConnectPolling()
        {
            SetLcuUiState(false, false);

            _lcuPoller.Start(async () =>
            {
                if (!lcuReady)
                {
                    bool isReady = await Globals.lcuClient.InitializeAsync();
                    if (isReady)
                    {
                        lcuReady = true;
                        _lcuPoller.Stop();
                        Debug.WriteLine("[LCU连接成功]");

                        //LCU API 连接成功之后初始化英雄资源
                        await InitializeLcuResources();

                        SafeInvoke(panelMatchList, () =>
                        {
                            //将用户列表控件添加到penal中
                            panelMatchList.Controls.Clear();
                            _matchTabContent = new MatchTabContent();
                            _matchTabContent.Dock = DockStyle.Fill;
                            panelMatchList.Controls.Add(_matchTabContent);
                        });

                        this.InvokeIfRequired(async () =>
                        {
                            //初始化资源完成之后立即查询当前用户战绩
                            await InitializeDefaultTab();

                            // 新增：检测当前 phase，确保可恢复游戏中场景
                            string currentPhase = await Globals.lcuClient.GetGameflowPhase();

                            if (!string.IsNullOrEmpty(currentPhase))
                            {
                                Debug.WriteLine($"[LCU检测] 当前 phase = {currentPhase}");
                                await HandleGameflowPhase(currentPhase, previousPhase: null);
                            }

                            // 正式启动轮询
                            StartGameflowWatcher();
                        });

                        //刷新提示UI
                        SetLcuUiState(lcuReady, _isGame);
                    }
                    else
                    {
                        Debug.WriteLine("[LCU检测中] 未找到 LCU 客户端");
                    }
                }
            }, 5000);
        }

        /// <summary>
        /// 默认查询当前客户端玩家对战数据
        /// </summary>
        /// <returns></returns>
        private async Task InitializeDefaultTab()
        {
            var summoner = await Globals.lcuClient.GetCurrentSummoner();
            if (summoner == null) return;

            Globals.CurrentPuuid = summoner["puuid"].ToString();

            // 根据puuid获取原始数据
            var rankedJson = await Globals.lcuClient.GetCurrentRankedStatsAsync(summoner["puuid"].ToString());

            // 直接通过类方法解析
            var rankedStats = RankedStats.FromJson(rankedJson);

            string privacyStatus = "隐藏";
            if (summoner["privacy"]?.ToString().Equals("PUBLIC", StringComparison.OrdinalIgnoreCase) == true)
                privacyStatus = "公开";
            // 直接创建标签页（不再需要单独设置信息）
            _matchTabContent.CreateNewTab(
                summoner["gameName"].ToString(),
                summoner["tagLine"].ToString(),
                summoner["puuid"].ToString(),
                summoner["profileIconId"].ToString(),
                summoner["summonerLevel"].ToString(),
                privacyStatus,
                rankedStats
            );
        }

        /// <summary>
        /// 监听玩家进入游戏房间状态，并实时获取玩家信息
        /// </summary>
        private async void StartGameflowWatcher()
        {
            if (!lcuReady)
            {
                return;
            }

            try
            {
                _watcherCts = new CancellationTokenSource();
                var token = _watcherCts.Token;
                string lastPhase = null;
                string previousPhase = null;

                await Task.Run(async () =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        string phase = await Globals.lcuClient.GetGameflowPhase();

                        if (string.IsNullOrEmpty(phase))
                        {
                            //返回空，则视为掉线
                            OnLcuDisconnected();
                            break;
                        }

                        if (phase != lastPhase)
                        {
                            Debug.WriteLine($"[Gameflow] 状态改变: {lastPhase} → {phase}");

                            //如果不返回空，则进入状态判断
                            await HandleGameflowPhase(phase, lastPhase);
                            previousPhase = lastPhase;
                            lastPhase = phase;
                        }

                        await Task.Delay(1000);
                    }

                }, token);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"监听异常：{ex}");
            }
        }

        //封装 phase 处理
        private async Task HandleGameflowPhase(string phase, string previousPhase)
        {
            switch (phase)
            {
                case "Matchmaking":
                case "ReadyCheck":
                    _isGame = false;
                    ClearGameState();
                    SafeInvoke(imageTabControl1, () =>
                    {
                        SetLcuUiState(lcuReady, _isGame);
                        imageTabControl1.SelectedIndex = 1;
                    });

                    //离开选人时清掉发送消息标志
                    _champSelectMessageSent = false;
                    break;

                case "ChampSelect":
                    _isGame = true;

                    SafeInvoke(penalGameMatchData, () =>
                    {
                        //进入游戏选人房间，先清空前面的UI提示控件
                        if (_waitingPanel != null && penalGameMatchData.Controls.Contains(_waitingPanel))
                        {
                            penalGameMatchData.Controls.Remove(_waitingPanel);
                            _waitingPanel.Dispose();
                            _waitingPanel = null;
                        }

                        //判断是否存在tableLayoutPanel1，不存在则添加，它是用来显示玩家战绩的控件
                        if (!penalGameMatchData.Controls.Contains(tableLayoutPanel1))
                        {
                            tableLayoutPanel1.Dock = DockStyle.Fill;
                            penalGameMatchData.Controls.Add(tableLayoutPanel1);
                        }

                        tableLayoutPanel1.Visible = true;
                        tableLayoutPanel1.Controls.Clear();
                    });

                    _gameEndHandled = false;

                    //开始获取我方队伍玩家数据
                    await OnChampSelectStart();
                    break;

                case "InProgress":
                    //停止我方英雄获取轮询
                    _champSelectCts?.Cancel();

                    //离开选人时清掉发送消息标志
                    _champSelectMessageSent = false;

                    //开始获取敌方队伍玩家数据
                    await ShowEnemyTeamCards();
                    break;

                case "EndOfGame":
                case "PreEndOfGame":
                case "WaitingForStats":
                case "Lobby":
                case "None":
                    //离开选人时清掉发送消息标志
                    _champSelectMessageSent = false;

                    if (!_gameEndHandled &&
                        (previousPhase == "InProgress" || previousPhase == "WaitingForStats" || previousPhase == "ChampSelect"))
                    {
                        _gameEndHandled = true;
                        await OnGameEnd();
                    }
                    break;
            }
        }

        /// <summary>
        /// 监听房间状态为：ChampSelect，显示我方英雄列表卡片
        /// </summary>
        /// <returns></returns>
        private async Task OnChampSelectStart()
        {
            _champSelectCts?.Cancel(); // 若之前已有轮询，先取消
            _champSelectCts = new CancellationTokenSource();
            var token = _champSelectCts.Token;

            Debug.WriteLine("进入选人阶段");

            await Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var phase = await Globals.lcuClient.GetGameflowPhase();
                        if (phase != "ChampSelect") break;

                        await ShowMyTeamCards(); // 刷新选人信息

                        await Task.Delay(2000, token); // 每2秒刷新一次
                    }
                    catch (TaskCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("选人阶段轮询异常：" + ex.Message);
                    }
                }
            }, token);
        }

        //封装游戏状态清理
        private void ClearGameState()
        {
            lastChampSelectSnapshot.Clear();
            _currentChampBySummoner.Clear();
            _summonerToColMap.Clear();
            _cachedPlayerMatchInfos.Clear();
            playerMatchCache.Clear();
            _cardBySummonerId.Clear();
        }

        //封装断线处理
        private void OnLcuDisconnected()
        {
            lcuReady = false;
            _isGame = false;

            _watcherCts?.Cancel();
            SetLcuUiState(false, false);
            StartLcuConnectPolling();
        }

        //游戏结束
        private async Task OnGameEnd()
        {
            Debug.WriteLine("游戏已结束，正在清空缓存及队伍存储信息，重置UI");

            // key: summonerId, value: 缓存的对局信息
            playerMatchCache.Clear();
            playerCache.Clear();

            _champSelectCts?.Cancel();  //清空房间状态内部轮询任务
            lastChampSelectSnapshot.Clear();    //清空存储我方队伍选择的英雄状态


            this.InvokeIfRequired(async () =>
            {
                Debug.WriteLine("即将重置主 Tab 页内容...");
                await InitializeDefaultTab();
            });

        }

        private void StopGameflowWatcher()
        {
            _watcherCts?.Cancel();
        }
        #endregion

        #region 并行下载查询战绩，先显示头像，后显示战绩
        private async Task CreateBasicCardsOnly(JArray team, bool isMyTeam, int row)
        {
            Debug.WriteLine($"[CreateBasicCardsOnly] 开始创建 {(isMyTeam ? "我方" : "敌方")} 卡片，行号: {row}");
            int col = 0;

            foreach (var p in team)
            {
                long summonerId = (long)p["summonerId"];
                int championId = (int)p["championId"];

                // 英雄没变则跳过加载
                if (_currentChampBySummoner.TryGetValue(summonerId, out int prevChampId) && prevChampId == championId)
                {
                    _summonerToColMap[summonerId] = col++;
                    //Debug.WriteLine($"[CreateBasicCardsOnly] summonerId={summonerId} 英雄未变，跳过头像加载，col={col - 1}");
                    continue;
                }

                // 更新快照字典
                _currentChampBySummoner[summonerId] = championId;
                _summonerToColMap[summonerId] = col;

                string championName = Globals.lcuClient.GetChampionById(championId)?.Name ?? "Unknown";
                Image avatar = await Globals.lcuClient.GetChampionIconAsync(championId);

                var player = new PlayerInfo
                {
                    SummonerId = summonerId,
                    ChampionId = championId,
                    ChampionName = championName,
                    Avatar = avatar,
                    GameName = "加载中...",
                    IsPublic = "[加载中]",
                    SoloRank = "加载中...",
                    FlexRank = "加载中..."
                };

                var matchInfo = new PlayerMatchInfo
                {
                    Player = player,
                    MatchItems = new List<ListViewItem>(),
                    HeroIcons = new ImageList()
                };

                //Debug.WriteLine($"[CreateBasicCardsOnly] 创建卡片 summonerId={summonerId}, championId={championId}, col={col}");
                UpdateOrCreateLoadingPlayerMatch(matchInfo, isMyTeam, row, col);

                col++;
            }

            Debug.WriteLine($"[CreateBasicCardsOnly] 完成 {(isMyTeam ? "我方" : "敌方")} 卡片创建，共 {col} 个玩家");
        }

        private async Task FillPlayerMatchInfoAsync(JArray team, bool isMyTeam, int row)
        {
            Debug.WriteLine($"[FillPlayerMatchInfoAsync] 开始异步战绩查询 {(isMyTeam ? "我方" : "敌方")}，行号: {row}");

            var fetchedInfos = await RunWithLimitedConcurrency(
                team,
                async p =>
                {
                    long sid = p["summonerId"]?.Value<long>() ?? 0;
                    int cid = p["championId"]?.Value<int>() ?? 0;

                    PlayerMatchInfo info;

                    // 先看缓存是否有
                    lock (_cachedPlayerMatchInfos)
                    {
                        if (_cachedPlayerMatchInfos.TryGetValue(sid, out info))
                        {
                            //Debug.WriteLine($"[使用缓存] summonerId={sid}");

                            if (_currentChampBySummoner.TryGetValue(sid, out int current) && current == cid)
                            {
                                int col = _summonerToColMap.TryGetValue(sid, out int c) ? c : 0;

                                // 判断卡片是否仍为“加载中”
                                var panel = tableLayoutPanel1.GetControlFromPosition(col, row) as BorderPanel;
                                var card = panel?.Controls.Count > 0 ? panel.Controls[0] as PlayerCardControl : null;

                                if (card != null && card.IsLoading)
                                {
                                    Debug.WriteLine($"[刷新加载中卡片] summonerId={sid}");
                                    CreateLoadingPlayerMatch(info, isMyTeam, row, col);
                                }
                            }

                            return info;
                        }
                    }

                    // 非缓存命中，执行请求
                    Debug.WriteLine($"[战绩任务] 查询开始 summonerId={sid}, championId={cid}");
                    info = await SafeFetchPlayerMatchInfoAsync(p);
                    if (info == null)
                    {
                        Debug.WriteLine($"[跳过] summonerId={sid} 获取失败，info 为 null");

                        // 构造一个失败卡片并显示
                        var failedInfo = new PlayerMatchInfo
                        {
                            Player = new PlayerInfo
                            {
                                SummonerId = sid,
                                ChampionId = cid,
                                ChampionName = "查询失败",
                                GameName = "失败",
                                IsPublic = "[失败]",
                                SoloRank = "失败",
                                FlexRank = "失败",
                                Avatar = LoadErrorImage() // 替换为你自己的错误图
                            },
                            MatchItems = new List<ListViewItem>(),
                            HeroIcons = new ImageList()
                        };

                        int col = _summonerToColMap.TryGetValue(sid, out int c2) ? c2 : 0;
                        CreateLoadingPlayerMatch(failedInfo, isMyTeam, row, col);

                        return null;
                    }

                    lock (_cachedPlayerMatchInfos)
                        _cachedPlayerMatchInfos[sid] = info;

                    // 确保玩家仍是当前英雄
                    if (_currentChampBySummoner.TryGetValue(sid, out int curCid) && curCid == cid)
                    {
                        int col = _summonerToColMap.TryGetValue(sid, out int c) ? c : 0;
                        CreateLoadingPlayerMatch(info, isMyTeam, row, col);
                    }
                    else
                    {
                        Debug.WriteLine($"[跳过战绩更新] summonerId={sid} 已更换英雄");
                    }

                    return info;
                },
                maxConcurrency: 3
            );

            Debug.WriteLine($"[FillPlayerMatchInfoAsync] 异步战绩查询完成，共获取 {fetchedInfos.Count} 条");

            // 分析组队关系，仅对非 null 的 info 生效
            var detector = new PartyDetector();
            detector.Detect(fetchedInfos.Where(f => f != null).ToList());

            // 更新颜色
            foreach (var info in fetchedInfos)
            {
                if (info?.Player == null) continue;
                UpdatePlayerNameColor(info.Player.SummonerId, info.Player.NameColor);
            }
        }


        private async Task ShowMyTeamCards()
        {
            //Debug.WriteLine("[ShowMyTeamCards] 获取选人会话中...");
            var session = await Globals.lcuClient.GetChampSelectSession();
            if (session == null)
            {
                Debug.WriteLine("[ShowMyTeamCards] 获取失败: session == null");
                return;
            }

            var myTeam = session["myTeam"] as JArray;
            if (myTeam == null || myTeam.Count == 0)
            {
                Debug.WriteLine("[ShowMyTeamCards] 获取失败: myTeam 数据为空");
                return;
            }

            myTeamId = (int)myTeam[0]["team"];
            int row = myTeamId - 1;
            //Debug.WriteLine($"[ShowMyTeamCards] 我的队伍 teamId={myTeamId}, row={row}");

            // 生成当前快照
            var currentSnapshot = new List<string>();
            foreach (var player in myTeam)
            {
                long summonerId = (long)player["summonerId"];
                int championId = (int)player["championId"];
                currentSnapshot.Add($"{summonerId}:{championId}");
            }

            // 比较快照，若一致则不更新
            if (lastChampSelectSnapshot.SequenceEqual(currentSnapshot))
            {
                //Debug.WriteLine("[ShowMyTeamCards] 队伍未变化，跳过刷新");
                return;
            }

            // 保存快照
            lastChampSelectSnapshot = currentSnapshot;

            //Debug.WriteLine("[ShowMyTeamCards] 队伍变化，开始刷新");

            //将我方数据存储
            _cachedMyTeam = myTeam;

            // 刷新 UI 和战绩
            await CreateBasicCardsOnly(myTeam, isMyTeam: true, row: row);
            //_ = FillPlayerMatchInfoAsync(myTeam, isMyTeam: true, row: row);
            await FillPlayerMatchInfoAsync(myTeam, isMyTeam: true, row: row);

            // 启动热键监听
            if (!_champSelectMessageSent)
            {
                ListenAndSendMessageWhenHotkeyPressed(myTeam);
                _champSelectMessageSent = true;
            }
        }

        // 声明 Win32 API
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(Keys vKey);

        private void ListenAndSendMessageWhenHotkeyPressed(JArray myTeam)
        {
            Task.Run(() =>
            {
                Debug.WriteLine("[HotKey] 等待用户按下快捷键 F9...");

                while (true)
                {
                    if ((GetAsyncKeyState(Keys.F9) & 0x8000) != 0)
                    {
                        Debug.WriteLine("[HotKey] 检测到 F9 被按下！");

                        // 切回 UI 线程（保证 STA）
                        this.Invoke((MethodInvoker)(() =>
                        {
                            SendChampSelectSummaryViaSendKeys(myTeam);
                        }));

                        break;
                    }

                    Thread.Sleep(100);
                }
            });
        }


        /// <summary>
        /// 发送我方队伍数据到选人聊天窗口
        /// </summary>
        private void SendChampSelectSummaryViaSendKeys(JArray myTeam)
        {
            var sb = new StringBuilder();

            foreach (var p in myTeam)
            {
                long summonerId = (long)p["summonerId"];
                if (!_cachedPlayerMatchInfos.TryGetValue(summonerId, out var info))
                {
                    continue;
                }

                //获取当前的puuid
                string puuid = (string)p["puuid"];
                //判断是否与窗口加载时的puuid一样，一样则是自己的，路过不发送
                if (!string.IsNullOrEmpty(puuid) && string.Equals(puuid, Globals.CurrentPuuid, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine($"[跳过发送] 当前玩家:{p["gameName"].ToString()}");
                    continue;
                }

                string name = info.Player.GameName ?? "未知";
                string solo = info.Player.SoloRank ?? "未知";
                string flex = info.Player.FlexRank ?? "未知";

                var wins = info.WinHistory.Count(w => w);
                var total = info.WinHistory.Count;
                double winRate = total > 0 ? (wins * 100.0 / total) : 0;

                //sb.AppendLine($"{name}: 单双排 {solo} | 灵活 {flex} | 近20场胜率: {winRate:F1}%");

                // 拼接近10场 KDA
                string kdaString = "";
                if (info.RecentMatches != null && info.RecentMatches.Count > 0)
                {
                    var last10 = info.RecentMatches.Take(10);
                    var kdaList = last10
                        .Select(m => $"{m.Kills}/{m.Deaths}/{m.Assists}");
                    kdaString = string.Join(" ", kdaList);
                }
                else
                {
                    kdaString = "无记录";
                }

                sb.AppendLine($"{name}: 单双排 {solo} | 灵活 {flex} | 近20场胜率: {winRate:F1}% | 近10场KDA: {kdaString}");
            }

            string allMessage = sb.ToString().Trim();
            var lines = allMessage.Split(new[] { Environment.NewLine }, StringSplitOptions.None);

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                Clipboard.SetText(line);

                // 打开聊天框
                SendKeys.SendWait("{ENTER}");
                Thread.Sleep(50);

                // 粘贴
                SendKeys.SendWait("^v");
                Thread.Sleep(50);

                // 回车发送
                SendKeys.SendWait("{ENTER}");
                Thread.Sleep(100);
            }

            Debug.WriteLine("[战绩信息] SendKeys 发送完成 (逐行发送)");
        }

        private async Task ShowEnemyTeamCards()
        {
            try
            {
                Debug.WriteLine("开始执行 ShowEnemyTeamCards");

                JObject currentSummoner = await Globals.lcuClient.GetCurrentSummoner();
                if (currentSummoner?["puuid"] == null) return;

                string myPuuid = (string)currentSummoner["puuid"];
                JObject sessionData = await Globals.lcuClient.GetGameSession();

                var gameData = sessionData?["gameData"];
                var teamOne = gameData?["teamOne"] as JArray;
                var teamTwo = gameData?["teamTwo"] as JArray;
                if (teamOne == null || teamTwo == null) return;

                bool isInTeamOne = teamOne.Any(p => (string)p["puuid"] == myPuuid);
                var enemyTeam = isInTeamOne ? teamTwo : teamOne;
                int enemyRow = isInTeamOne ? 1 : 0;

                //将敌方数据存储
                _cachedEnemyTeam = enemyTeam;

                // 先创建头像占位卡片
                await CreateBasicCardsOnly(enemyTeam, isMyTeam: false, row: enemyRow);

                // 再异步补充段位/战绩
                //_ = FillPlayerMatchInfoAsync(enemyTeam, isMyTeam: false, row: enemyRow);
                await FillPlayerMatchInfoAsync(enemyTeam, isMyTeam: false, row: enemyRow);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ShowEnemyTeamCards 异常: " + ex.ToString());
            }
        }

        public async Task<List<TResult>> RunWithLimitedConcurrency<TInput, TResult>(
        IEnumerable<TInput> inputs,
        Func<TInput, Task<TResult>> taskFunc,
        int maxConcurrency = 3)
        {
            var indexedInputs = inputs.Select((input, index) => new { input, index }).ToList();
            var results = new TResult[indexedInputs.Count];
            var semaphore = new SemaphoreSlim(maxConcurrency);
            var tasks = new List<Task>();

            foreach (var item in indexedInputs)
            {
                await semaphore.WaitAsync();

                var task = Task.Run(async () =>
                {
                    try
                    {
                        var result = await taskFunc(item.input);
                        results[item.index] = result;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[并发异常] Index {item.index}: {ex.Message}");
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                tasks.Add(task);
            }

            await Task.WhenAll(tasks);
            return results.ToList(); // 顺序与输入一致
        }

        private async Task<PlayerMatchInfo> SafeFetchPlayerMatchInfoAsync(JToken p, int retryTimes = 2)
        {
            for (int attempt = 1; attempt <= retryTimes + 1; attempt++)
            {
                try
                {
                    return await FetchPlayerMatchInfoAsync(p);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Fetch失败] 第 {attempt} 次尝试失败: {ex.Message}");
                    if (attempt <= retryTimes)
                        await Task.Delay(1000);
                }
            }

            Debug.WriteLine("[Fetch失败] 所有重试失败，返回 null");
            return null; // 重点：不要返回无效对象
        }

        //头像获取失败显示默认图
        private Image LoadErrorImage()
        {
            return Image.FromFile(AppDomain.CurrentDomain.BaseDirectory + "Assets\\Defaults\\Profile.png");
        }
        #endregion

        #region 下载历史战绩，解析数据
        /// <summary>
        /// 根据summoner信息获取玩家的puuid、段位、历史战绩
        /// </summary>
        /// <param name="p"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="ArgumentException"></exception>
        private async Task<PlayerMatchInfo> FetchPlayerMatchInfoAsync(JToken p)
        {
            if (p == null)
                throw new ArgumentNullException(nameof(p));

            long summonerId = p["summonerId"]?.Value<long>() ?? 0;
            int championId = p["championId"]?.Value<int>() ?? 0;

            if (summonerId == 0)
                throw new ArgumentException("summonerId is missing or invalid.");

            string championName = Globals.lcuClient.GetChampionById(championId)?.Name ?? "Unknown";
            Image iconChamp = await Globals.lcuClient.GetChampionIconAsync(championId); //获取头像.ConfigureAwait(false)
            // 先尝试从缓存获取
            if (playerMatchCache.TryGetValue(summonerId, out var cachedMatch))
            {
                cachedMatch.Player.ChampionId = championId;
                cachedMatch.Player.ChampionName = championName;
                cachedMatch.Player.Avatar = iconChamp;
                cachedMatch.IsFromCache = true;  // 标记是缓存
                return cachedMatch;
            }

            // 缓存没命中，调用API获取详细信息
            string puuid = "";
            string gameName = "未知玩家";
            string privacyStatus = "[隐藏]";
            string soloRank = "未知";
            string flexRank = "未知";

            var summoner = await Globals.lcuClient.GetGameNameBySummonerId(summonerId.ToString());
            if (summoner != null)
            {
                puuid = summoner["puuid"]?.ToString() ?? "";
                gameName = summoner["gameName"]?.ToString() ?? "未知玩家";

                if (summoner["privacy"]?.ToString().Equals("PUBLIC", StringComparison.OrdinalIgnoreCase) == true)
                    privacyStatus = "[公开]";

                var rankedJson = await Globals.lcuClient.GetCurrentRankedStatsAsync(puuid);
                var rankedStats = RankedStats.FromJson(rankedJson);

                if (rankedStats != null)
                {
                    if (rankedStats.TryGetValue("单双排", out var soloStats))
                        soloRank = $"{soloStats.FormattedTier}({soloStats.LeaguePoints})";

                    if (rankedStats.TryGetValue("灵活组排", out var flexStats))
                        flexRank = $"{flexStats.FormattedTier}({flexStats.LeaguePoints})";
                }
            }

            var playerInfo = new PlayerInfo
            {
                Puuid = puuid,
                SummonerId = summonerId,
                ChampionId = championId,
                ChampionName = championName,
                Avatar = iconChamp,
                GameName = gameName,
                IsPublic = privacyStatus,
                SoloRank = soloRank,
                FlexRank = flexRank
            };

            // 拉取比赛列表（20场）
            var matches = await Globals.lcuClient.FetchLatestMatches(puuid);

            // 创建PlayerMatchInfo，包含比赛数据
            var matchInfo = GetPlayerMatchInfo(puuid, matches);
            matchInfo.Player = playerInfo;
            matchInfo.IsFromCache = false;

            // 更新缓存
            playerMatchCache[summonerId] = matchInfo;

            return matchInfo;
        }


        /// <summary>
        /// 监听游戏房间，根据puuid获取数据，并解析房间玩家的历史战绩数据
        /// </summary>
        /// <param name="puuid"></param>
        /// <param name="matches"></param>
        /// <returns></returns>
        public PlayerMatchInfo GetPlayerMatchInfo(string puuid, JArray matches)
        {
            var result = new PlayerMatchInfo();
            var matchItems = result.MatchItems;
            var heroIcons = result.HeroIcons;

            if (matches == null || matches.Count == 0)
            {
                return result; // 直接返回空的，避免后续异常
            }

            foreach (JObject match in matches.Cast<JObject>())
            {
                long gameId = match["gameId"]?.Value<long>() ?? 0;
                if (gameId == 0) continue;

                int currentParticipantId = match["participantIdentities"]
                    ?.FirstOrDefault(id => id["player"]?["puuid"]?.ToString() == puuid)?["participantId"]?.Value<int>() ?? -1;
                if (currentParticipantId == -1) continue;

                var participant = match["participants"]
                    ?.FirstOrDefault(p => p["participantId"]?.Value<int>() == currentParticipantId);
                if (participant == null) continue;

                int teamId = participant["teamId"]?.Value<int>() ?? -1;
                int championId = participant["championId"]?.Value<int>() ?? 0;

                var champion = Globals.lcuClient.GetChampionById(championId);
                string champName = champion.Name.Replace(" ", "").Replace("'", "");

                if (!_imageCache.TryGetValue(champName, out var image))
                {
                    // 异步加载图片，这里需要根据你的实际情况调整
                    // 可能需要改为异步方法或预先加载所有图片
                    image = Globals.lcuClient.GetChampionIconAsync(championId).GetAwaiter().GetResult();
                    if (image != null)
                    {
                        _imageCache.TryAdd(champName, image);
                    }
                }

                if (image != null && !heroIcons.Images.ContainsKey(champName))
                {
                    heroIcons.Images.Add(champName, image);
                }

                var stats = participant["stats"];
                int kills = stats?["kills"]?.Value<int>() ?? 0;
                int deaths = stats?["deaths"]?.Value<int>() ?? 0;
                int assists = stats?["assists"]?.Value<int>() ?? 0;
                bool win = stats?["win"]?.Value<bool>() ?? false;

                // 保存到 RecentMatches 用来发送kda信息
                result.RecentMatches.Add(new MatchStat
                {
                    Kills = kills,
                    Deaths = deaths,
                    Assists = assists
                });

                // 新增
                result.WinHistory.Add(win);

                string gameMode = GameMod.GetModeName(
                        match["queueId"]?.Value<int>() ?? -1,
                        match["gameMode"]?.ToString()
                    );

                var item = new ListViewItem
                {
                    ImageKey = champName,
                    ForeColor = win ? Color.Green : Color.Red,
                    Tag = new MatchMetadata
                    {
                        GameId = gameId,
                        TeamId = teamId
                    }
                };
                item.SubItems.AddRange(new[]
                {
                    gameMode,
                    $"{kills}/{deaths}/{assists}",
                    DateTimeOffset.FromUnixTimeMilliseconds(match["gameCreation"].Value<long>()).ToString("MM-dd")
                });

                matchItems.Add(item);

                // 加入匹配用的key：gameId_teamId 代表“同一场、同一队伍”
                result.MatchKeys.Add($"{gameId}_{teamId}");
            }

            result.HeroIcons = heroIcons;
            return result;
        }

        #endregion

        #region UI更新处理

        /// <summary>
        /// 只更新与队伍相同的玩家颜色
        /// </summary>
        /// <param name="summonerId"></param>
        /// <param name="color"></param>
        private void UpdatePlayerNameColor(long summonerId, Color color)
        {
            if (_cardBySummonerId.TryGetValue(summonerId, out var card))
            {
                card.Invoke((MethodInvoker)(() =>
                {
                    card.lblPlayerName.LinkColor = color;
                    card.lblPlayerName.VisitedLinkColor = color;
                    card.lblPlayerName.ActiveLinkColor = color;
                }));
            }
        }

        /// <summary>
        /// 用来判断是否房间里面切换了英雄，如果只切换英雄则更新英雄头像
        /// </summary>
        /// <param name="matchInfo"></param>
        /// <param name="isMyTeam"></param>
        /// <param name="row"></param>
        /// <param name="column"></param>
        private void UpdateOrCreateLoadingPlayerMatch(PlayerMatchInfo matchInfo, bool isMyTeam, int row, int column)
        {
            var player = matchInfo.Player;
            var key = (row, column);

            long summonerId = player.SummonerId;
            int championId = player.ChampionId;

            if (playerCache.TryGetValue(key, out var cached))
            {
                if (cached.summonerId == summonerId &&
                    cached.championId == championId)
                {
                    // 若 UI 当前是加载中，则刷新
                    var panel = tableLayoutPanel1.GetControlFromPosition(column, row) as BorderPanel;
                    var card = panel?.Controls.Count > 0 ? panel.Controls[0] as PlayerCardControl : null;

                    if (card != null && card.IsLoading)
                    {
                        Debug.WriteLine($"[刷新加载中卡片] summonerId={summonerId}");
                        CreateLoadingPlayerMatch(matchInfo, isMyTeam, row, column);
                    }

                    return;
                }

                if (cached.summonerId == summonerId && cached.championId != championId)
                {
                    UpdatePlayerAvatarOnly(row, column, player);
                    playerCache[key] = (summonerId, championId);
                    return;
                }
            }

            // 玩家或英雄完全变更，直接重建卡片
            CreateLoadingPlayerMatch(matchInfo, isMyTeam, row, column);
            playerCache[key] = (summonerId, championId);
        }

        /// <summary>
        /// 更新英雄头像方法
        /// </summary>
        /// <param name="row"></param>
        /// <param name="column"></param>
        /// <param name="player"></param>
        private void UpdatePlayerAvatarOnly(int row, int column, PlayerInfo player)
        {
            SafeInvoke(tableLayoutPanel1, () =>
            {
                var panel = tableLayoutPanel1.GetControlFromPosition(column, row) as BorderPanel;
                if (panel != null && panel.Controls.Count > 0)
                {
                    var card = panel.Controls[0] as PlayerCardControl;
                    if (card != null)
                    {
                        card.SetAvatarOnly(player.Avatar); // 只更新头像，不碰列表
                    }
                }
            });
        }


        /// <summary>
        /// 根据获取到的敌我双方数据创建卡片，并更新UI显示
        /// </summary>
        /// <param name="matchInfo"></param>
        /// <param name="isMyTeam"></param>
        /// <param name="row"></param>
        /// <param name="column"></param>
        private void CreateLoadingPlayerMatch(PlayerMatchInfo matchInfo, bool isMyTeam, int row, int column)
        {
            var player = matchInfo.Player;
            var heroIcons = matchInfo.HeroIcons;
            var matchItems = matchInfo.MatchItems;

            Color borderColor = row == 0 ? Color.Red :
                                row == 1 ? Color.Blue : Color.Gray;

            var panel = new BorderPanel
            {
                BorderColor = borderColor,
                BorderWidth = 1,
                Padding = new Padding(2),
                Dock = DockStyle.Fill,
                Margin = new Padding(5)
            };

            var card = new PlayerCardControl
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0)
            };

            // 注册映射，便于之后只更新颜色
            _cardBySummonerId[matchInfo.Player.SummonerId] = card;

            string name = player.GameName ?? "未知";
            string soloRank = string.IsNullOrEmpty(player.SoloRank) ? "未知" : player.SoloRank;
            string flexRank = string.IsNullOrEmpty(player.FlexRank) ? "未知" : player.FlexRank;

            Color nameColor = matchInfo.Player.NameColor;
            card.SetPlayerInfo(name, soloRank, flexRank, player.Avatar, player.IsPublic, matchItems, nameColor);
            card.ListViewControl.SmallImageList = heroIcons;
            card.ListViewControl.View = View.Details;

            panel.Controls.Add(card);

            // 加入控件前先移除旧的
            SafeInvoke(tableLayoutPanel1, () =>
            {
                var oldControl = tableLayoutPanel1.GetControlFromPosition(column, row);
                if (oldControl != null)
                {
                    tableLayoutPanel1.Controls.Remove(oldControl);
                    oldControl.Dispose(); // 释放旧控件资源
                }

                tableLayoutPanel1.Controls.Add(panel, column, row);
            });
        }


        public static void SafeInvoke(Control control, Action action)
        {
            if (control.IsDisposed) return;

            if (control.InvokeRequired)
            {
                try
                {
                    control.BeginInvoke(action); // 异步，不阻塞调用线程
                }
                catch
                {
                    // 控件已销毁时忽略
                }
            }
            else
            {
                action();
            }
        }
        #endregion

        #region 查询战绩与解析
        /// <summary>
        /// 查询数据
        /// </summary>
        /// <param name="puuid"></param>
        /// <param name="begIndex"></param>
        /// <param name="endIndex"></param>
        /// <param name="queueId"></param>
        /// <returns></returns>
        public async Task<JArray> LoadFullMatchDataAsync(string puuid, int begIndex, int endIndex, string queueId = null)
        {
            var matches = await Globals.lcuClient.FetchMatchesWithRetry(puuid, begIndex, endIndex);
            if (matches == null) return null;

            // 提取并筛选 gameId
            var gameIds = matches
                .Where(m => string.IsNullOrEmpty(queueId) ||
                           m["queueId"]?.ToString() == queueId)
                .Select(m => m["gameId"]?.Value<long>())
                .Where(id => id != null)
                .Distinct()
                .ToList();

            // 获取每个完整对战信息
            var tasks = gameIds.Select(id => Globals.lcuClient.GetFullMatchByGameIdAsync(id.Value));
            var results = await Task.WhenAll(tasks);

            // 转为 JArray 返回
            var fullMatches = results.Where(r => r != null);
            return new JArray(fullMatches);
        }

        /// <summary>
        /// 数据解析
        /// </summary>
        /// <param name="gameObj"></param>
        /// <param name="gameName"></param>
        /// <param name="tagLine"></param>
        /// <returns></returns>
        public async Task<Panel> ParseGameToPanelAsync(JObject gameObj, string gameName, string tagLine)
        {
            var _parser = new MatchParser();
            _parser.PlayerIconClicked += Panel_PlayerIconClicked;
            var panel = await _parser.ParseGameToPanelAsync(gameObj, gameName, tagLine);
            return panel;
        }

        /// <summary>
        /// 绑定按钮事件
        /// </summary>
        /// <param name="fullName"></param>
        private async void Panel_PlayerIconClicked(string fullName)
        {
            try
            {
                txtGameName.Text = fullName;    //点击头像时将用户名传给输入框以此来设置查询对象
                var parts = fullName.Split('#');
                if (parts.Length != 2) return;

                var summoner = await Globals.lcuClient.GetSummonerByNameAsync(fullName);
                if (summoner == null) return;

                // 根据puuid获取原始数据
                var rankedJson = await Globals.lcuClient.GetCurrentRankedStatsAsync(summoner["puuid"].ToString());

                // 直接通过类方法解析
                var rankedStats = RankedStats.FromJson(rankedJson);

                string privacyStatus = "隐藏";
                if (summoner["privacy"]?.ToString().Equals("PUBLIC", StringComparison.OrdinalIgnoreCase) == true)
                    privacyStatus = "公开";
                // 直接创建标签页（不再需要单独设置信息）
                _matchTabContent.CreateNewTab(
                    summoner["gameName"].ToString(),
                    summoner["tagLine"].ToString(),
                    summoner["puuid"].ToString(),
                    summoner["profileIconId"].ToString(),
                    summoner["summonerLevel"].ToString(),
                    privacyStatus,
                    rankedStats
                );
            }
            catch (Exception ex)
            {
                MessageBox.Show($"查询失败: {ex.Message}");
            }
        }
        #endregion

        #region LCU 检测连接提示
        private void SetLcuUiState(bool connected, bool inGame)
        {
            if (!connected)
            {
                SafeInvoke(panelMatchList, () =>
                {
                    ShowLcuNotConnectedMessage(panelMatchList);
                });
                SafeInvoke(penalGameMatchData, () =>
                {
                    ShowLcuNotConnectedMessage(penalGameMatchData);
                });
            }
            else if (!_isGame)
            {
                SafeInvoke(penalGameMatchData, () =>
                {
                    ShowWaitingForGameMessage(penalGameMatchData);
                });
            }
        }

        private Panel CreateStatusPanel(string message, bool showLolLauncher = false)
        {
            var containerPanel = new Panel
            {
                Width = 500,
                Height = 200,
                BackColor = Color.Transparent
            };

            var label = new Label
            {
                Text = message,
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Top,
                Height = 50,
                Font = new Font("微软雅黑", 12, FontStyle.Bold)
            };

            var progress = new ProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                Width = 200,
                Height = 30,
                MarqueeAnimationSpeed = 30
            };

            progress.Left = (containerPanel.Width - progress.Width) / 2;
            progress.Top = label.Bottom + 10;

            containerPanel.Controls.Add(label);
            containerPanel.Controls.Add(progress);

            if (showLolLauncher)
            {
                LOLHelper helper = new LOLHelper();
                string exePath = helper.GetLOLLoginExePath();

                var linkLolPath = new LinkLabel
                {
                    AutoSize = true,
                    Text = string.IsNullOrEmpty(exePath) ? "未检测到 LOL 登录程序" : exePath,
                    Font = new Font("微软雅黑", 10, FontStyle.Regular)
                };

                var btnStartLol = new Button
                {
                    Text = "启动LOL登录程序",
                    Width = 200,
                    Height = 30
                };

                linkLolPath.Left = (containerPanel.Width - linkLolPath.PreferredWidth) / 2;
                linkLolPath.Top = progress.Bottom + 30;

                btnStartLol.Left = (containerPanel.Width - btnStartLol.Width) / 2;
                btnStartLol.Top = linkLolPath.Bottom + 10;

                containerPanel.Controls.Add(linkLolPath);
                containerPanel.Controls.Add(btnStartLol);

                if (!string.IsNullOrEmpty(exePath))
                {
                    linkLolPath.LinkClicked += (s, e) =>
                    {
                        string folder = Path.GetDirectoryName(exePath);
                        if (Directory.Exists(folder))
                        {
                            Process.Start("explorer.exe", folder);
                        }
                    };

                    btnStartLol.Click += (sender, e) =>
                    {
                        linkLolPath.Text = exePath;
                        if (!string.IsNullOrEmpty(exePath))
                        {
                            Debug.WriteLine("找到 LOL 登录程序：" + exePath);
                            helper.StartLOLLoginProgram(exePath);
                        }
                        else
                        {
                            Debug.WriteLine("未检测到 LOL 登录程序！");
                        }
                    };
                }
            }

            return containerPanel;
        }


        private void ShowLcuNotConnectedMessage(Control parentControl)
        {
            parentControl.Controls.Clear();

            var panel = CreateStatusPanel(
                "正在检测LCU连接，请确保登录了游戏...",
                showLolLauncher: true
            );

            panel.Left = (parentControl.Width - panel.Width) / 2;
            panel.Top = (parentControl.Height - panel.Height) / 2;
            panel.Anchor = AnchorStyles.None;

            parentControl.Controls.Add(panel);
        }


        private void ShowWaitingForGameMessage(Control parentControl)
        {
            parentControl.Controls.Clear();   // 新增：清理所有旧控件

            tableLayoutPanel1.Visible = false;

            _waitingPanel = CreateStatusPanel("正在等待加入游戏，请稍后...", showLolLauncher: false);

            _waitingPanel.Left = (parentControl.Width - _waitingPanel.Width) / 2;
            _waitingPanel.Top = (parentControl.Height - _waitingPanel.Height) / 2;
            _waitingPanel.Anchor = AnchorStyles.None;

            parentControl.Controls.Add(_waitingPanel);
        }

        private void imageTabControl1_SelectedIndexChanged(object sender, EventArgs e)
        {
            int selectedTabIndex = imageTabControl1.SelectedIndex;

            switch (selectedTabIndex)
            {
                case 0:
                    _tab1Poller.Stop();
                    break;

                case 1:
                    StartTab1Polling();
                    break;

                case 2:
                    _tab1Poller.Stop();
                    Debug.WriteLine("自动化设置");
                    break;
            }
        }

        private void StartTab1Polling()
        {
            _tab1Poller.Start(async () =>
            {
                try
                {
                    SetLcuUiState(lcuReady, _isGame);
                }
                catch (TaskCanceledException)
                {
                    // 忽略
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Tab1Poller轮询异常: {ex}");
                }
            }, 3000);
        }

        #endregion
    }
}