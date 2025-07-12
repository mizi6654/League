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
        //������ѯ����Ƿ��Ѿ�����lcu api�ͻ���
        private AsyncPoller _lcuPoller = new AsyncPoller();
        private bool lcuReady = false; // ��ʾ�Ƿ��Ѿ���ʼ������

        private MatchTabContent _matchTabContent;

        private CancellationTokenSource _watcherCts;

        //OnChampSelectStart() ������һ�� �ڲ���ѯ����
        private CancellationTokenSource _champSelectCts;

        private int myTeamId = 0;
        //�洢�ҷ�����ѡ���Ӣ��״̬
        private List<string> lastChampSelectSnapshot = new List<string>();

        //�ж��Ƿ����л��棬�����ֱ�ӷ��ػ��棬���������ѯ
        private readonly Dictionary<long, PlayerMatchInfo> _cachedPlayerMatchInfos = new Dictionary<long, PlayerMatchInfo>();

        //��ʾ��Ƭ�����浱ǰ summoner �� championId
        private Dictionary<long, int> _currentChampBySummoner = new Dictionary<long, int>();
        private Dictionary<long, int> _summonerToColMap = new Dictionary<long, int>(); // optional: �ṩһ���λӳ��

        // ���ͷ��ȫ�ֻ���
        private static readonly ConcurrentDictionary<string, Image> _imageCache = new();

        // key: summonerId, value: ����ĶԾ���Ϣ
        private Dictionary<long, PlayerMatchInfo> playerMatchCache = new Dictionary<long, PlayerMatchInfo>();
        private Dictionary<(int row, int column), (long summonerId, int championId)> playerCache = new Dictionary<(int, int), (long, int)>();

        // summonerId �� PlayerCardControl ӳ�䣬����Ⱦɫʱ���ٻ�ȡ�ؼ�
        private readonly ConcurrentDictionary<long, PlayerCardControl> _cardBySummonerId = new();

        private bool _gameEndHandled = false;

        // ���Ա������ȷ��ֻ����һ�Σ�������ʾ�����tabControl��ʾ�ı�
        private ToolTip tip = new ToolTip();
        private int _lastIndex = -1;

        private readonly Poller _tab1Poller = new Poller();
        private Panel _waitingPanel;

        bool _isGame = false;

        private bool _champSelectMessageSent = false;   //����ѡ�˽׶η�����Ϣ��־

        //���������ֶΣ������洢�ҷ�������з����������
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
                    AutoPopDelay = 5000,  // ��ʾ��ʾ 5 ��
                    InitialDelay = 100,   // �����ͣ 0.5 ������ʾ
                    ReshowDelay = 100,    // �ٴ���ʾ���ӳ�
                    ShowAlways = true    // ��ʹ�ؼ����ڻ״̬Ҳ��ʾ
                };

                // ��ȡ���ذ汾
                var localVersion = VersionInfo.GetLocalVersion();

                // ��ȡԶ�̰汾
                var remoteVersion = await VersionInfo.GetRemoteVersion();

                if (remoteVersion != null)
                {
                    if (localVersion != null && remoteVersion.version == localVersion.version)
                    {
                        Debug.WriteLine($"��ǰ�������°汾��{remoteVersion.version}");
                    }
                    else
                    {
                        var changelogStr = string.Join("\n", remoteVersion.changelog);

                        Debug.WriteLine($"�������ݣ�\n�汾��{remoteVersion.version}\n���ڣ�{remoteVersion.date}\n{changelogStr}");

                        var msg = $"��⵽�°汾 {remoteVersion.version} ({remoteVersion.date})\n\n" + changelogStr;

                        var result = MessageBox.Show(msg, "�汾����", MessageBoxButtons.OKCancel);
                        if (result == DialogResult.OK)
                        {
                            // ���� Updater.exe
                            Process.Start(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "update", "Update.exe"));
                            Environment.Exit(0);
                        }
                    }
                }
                else
                {
                    Debug.WriteLine("δ��⵽�°汾��");
                }


                // �� MouseMove �¼�����̬��ʾ��Ӧ��ǩ����ʾ
                imageTabControl1.MouseMove += ImageTabControl1_MouseMove;

                // ������ѯ LCU ���
                StartLcuConnectPolling();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ȫ�ֳ�ʼ���쳣] {ex.Message}");
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

                        // ����Ļ����ת�ɿؼ�����
                        Point clientPos = imageTabControl1.PointToClient(Cursor.Position);
                        // ����긽����ʾ ToolTip
                        tip.Show(imageTabControl1.TabPages[i].Text, imageTabControl1, clientPos.X + 10, clientPos.Y + 10, 1500);
                    }
                    return;
                }
            }
            // ��겻���κα�ǩ�ϣ������ʾ
            tip.SetToolTip(imageTabControl1, null);
            _lastIndex = -1;
        }

        private async void btn_search_Click(object sender, EventArgs e)
        {
            if (!lcuReady)
            {
                MessageBox.Show("LCU �ͻ���δ���ӣ����ȵ�¼��Ϸ���Ժ����ԣ�");
                return;
            }

            string input = txtGameName.Text.Trim();
            if (!input.Contains("#"))
            {
                MessageBox.Show("�������������ƣ��磺�����#����");
                return;
            }

            var summoner = await Globals.lcuClient.GetSummonerByNameAsync(input);
            if (summoner == null)
            {
                MessageBox.Show("��Ҳ�����,�����ֻ�ܲ�ѯ��ͬ�������!");
                return;
            }

            // ����puuid��ȡԭʼ����
            var rankedJson = await Globals.lcuClient.GetCurrentRankedStatsAsync(summoner["puuid"].ToString());

            // ֱ��ͨ���෽������
            var rankedStats = RankedStats.FromJson(rankedJson);

            string privacyStatus = "����";
            if (summoner["privacy"]?.ToString().Equals("PUBLIC", StringComparison.OrdinalIgnoreCase) == true)
                privacyStatus = "����";
            // ֱ�Ӵ�����ǩҳ��������Ҫ����������Ϣ��
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

        //��ʼ����Դ
        private async Task InitializeLcuResources()
        {
            await Globals.lcuClient.LoadChampionsAsync();
            await Globals.lcuClient.LoadItemsAsync();
            await Globals.lcuClient.LoadSpellsAsync();
            await Globals.lcuClient.LoadRunesAsync();
        }

        #region �����������ѯ��� LCU ����
        /// <summary>
        /// �������ڣ���ѯ�����Ƿ��¼��lcu�ͻ���
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
                        Debug.WriteLine("[LCU���ӳɹ�]");

                        //LCU API ���ӳɹ�֮���ʼ��Ӣ����Դ
                        await InitializeLcuResources();

                        SafeInvoke(panelMatchList, () =>
                        {
                            //���û��б�ؼ���ӵ�penal��
                            panelMatchList.Controls.Clear();
                            _matchTabContent = new MatchTabContent();
                            _matchTabContent.Dock = DockStyle.Fill;
                            panelMatchList.Controls.Add(_matchTabContent);
                        });

                        this.InvokeIfRequired(async () =>
                        {
                            //��ʼ����Դ���֮��������ѯ��ǰ�û�ս��
                            await InitializeDefaultTab();

                            // ��������⵱ǰ phase��ȷ���ɻָ���Ϸ�г���
                            string currentPhase = await Globals.lcuClient.GetGameflowPhase();

                            if (!string.IsNullOrEmpty(currentPhase))
                            {
                                Debug.WriteLine($"[LCU���] ��ǰ phase = {currentPhase}");
                                await HandleGameflowPhase(currentPhase, previousPhase: null);
                            }

                            // ��ʽ������ѯ
                            StartGameflowWatcher();
                        });

                        //ˢ����ʾUI
                        SetLcuUiState(lcuReady, _isGame);
                    }
                    else
                    {
                        Debug.WriteLine("[LCU�����] δ�ҵ� LCU �ͻ���");
                    }
                }
            }, 5000);
        }

        /// <summary>
        /// Ĭ�ϲ�ѯ��ǰ�ͻ�����Ҷ�ս����
        /// </summary>
        /// <returns></returns>
        private async Task InitializeDefaultTab()
        {
            var summoner = await Globals.lcuClient.GetCurrentSummoner();
            if (summoner == null) return;

            Globals.CurrentPuuid = summoner["puuid"].ToString();

            // ����puuid��ȡԭʼ����
            var rankedJson = await Globals.lcuClient.GetCurrentRankedStatsAsync(summoner["puuid"].ToString());

            // ֱ��ͨ���෽������
            var rankedStats = RankedStats.FromJson(rankedJson);

            string privacyStatus = "����";
            if (summoner["privacy"]?.ToString().Equals("PUBLIC", StringComparison.OrdinalIgnoreCase) == true)
                privacyStatus = "����";
            // ֱ�Ӵ�����ǩҳ��������Ҫ����������Ϣ��
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
        /// ������ҽ�����Ϸ����״̬����ʵʱ��ȡ�����Ϣ
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
                            //���ؿգ�����Ϊ����
                            OnLcuDisconnected();
                            break;
                        }

                        if (phase != lastPhase)
                        {
                            Debug.WriteLine($"[Gameflow] ״̬�ı�: {lastPhase} �� {phase}");

                            //��������ؿգ������״̬�ж�
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
                Debug.WriteLine($"�����쳣��{ex}");
            }
        }

        //��װ phase ����
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

                    //�뿪ѡ��ʱ���������Ϣ��־
                    _champSelectMessageSent = false;
                    break;

                case "ChampSelect":
                    _isGame = true;

                    SafeInvoke(penalGameMatchData, () =>
                    {
                        //������Ϸѡ�˷��䣬�����ǰ���UI��ʾ�ؼ�
                        if (_waitingPanel != null && penalGameMatchData.Controls.Contains(_waitingPanel))
                        {
                            penalGameMatchData.Controls.Remove(_waitingPanel);
                            _waitingPanel.Dispose();
                            _waitingPanel = null;
                        }

                        //�ж��Ƿ����tableLayoutPanel1������������ӣ�����������ʾ���ս���Ŀؼ�
                        if (!penalGameMatchData.Controls.Contains(tableLayoutPanel1))
                        {
                            tableLayoutPanel1.Dock = DockStyle.Fill;
                            penalGameMatchData.Controls.Add(tableLayoutPanel1);
                        }

                        tableLayoutPanel1.Visible = true;
                        tableLayoutPanel1.Controls.Clear();
                    });

                    _gameEndHandled = false;

                    //��ʼ��ȡ�ҷ������������
                    await OnChampSelectStart();
                    break;

                case "InProgress":
                    //ֹͣ�ҷ�Ӣ�ۻ�ȡ��ѯ
                    _champSelectCts?.Cancel();

                    //�뿪ѡ��ʱ���������Ϣ��־
                    _champSelectMessageSent = false;

                    //��ʼ��ȡ�з������������
                    await ShowEnemyTeamCards();
                    break;

                case "EndOfGame":
                case "PreEndOfGame":
                case "WaitingForStats":
                case "Lobby":
                case "None":
                    //�뿪ѡ��ʱ���������Ϣ��־
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
        /// ��������״̬Ϊ��ChampSelect����ʾ�ҷ�Ӣ���б�Ƭ
        /// </summary>
        /// <returns></returns>
        private async Task OnChampSelectStart()
        {
            _champSelectCts?.Cancel(); // ��֮ǰ������ѯ����ȡ��
            _champSelectCts = new CancellationTokenSource();
            var token = _champSelectCts.Token;

            Debug.WriteLine("����ѡ�˽׶�");

            await Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var phase = await Globals.lcuClient.GetGameflowPhase();
                        if (phase != "ChampSelect") break;

                        await ShowMyTeamCards(); // ˢ��ѡ����Ϣ

                        await Task.Delay(2000, token); // ÿ2��ˢ��һ��
                    }
                    catch (TaskCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("ѡ�˽׶���ѯ�쳣��" + ex.Message);
                    }
                }
            }, token);
        }

        //��װ��Ϸ״̬����
        private void ClearGameState()
        {
            lastChampSelectSnapshot.Clear();
            _currentChampBySummoner.Clear();
            _summonerToColMap.Clear();
            _cachedPlayerMatchInfos.Clear();
            playerMatchCache.Clear();
            _cardBySummonerId.Clear();
        }

        //��װ���ߴ���
        private void OnLcuDisconnected()
        {
            lcuReady = false;
            _isGame = false;

            _watcherCts?.Cancel();
            SetLcuUiState(false, false);
            StartLcuConnectPolling();
        }

        //��Ϸ����
        private async Task OnGameEnd()
        {
            Debug.WriteLine("��Ϸ�ѽ�����������ջ��漰����洢��Ϣ������UI");

            // key: summonerId, value: ����ĶԾ���Ϣ
            playerMatchCache.Clear();
            playerCache.Clear();

            _champSelectCts?.Cancel();  //��շ���״̬�ڲ���ѯ����
            lastChampSelectSnapshot.Clear();    //��մ洢�ҷ�����ѡ���Ӣ��״̬


            this.InvokeIfRequired(async () =>
            {
                Debug.WriteLine("���������� Tab ҳ����...");
                await InitializeDefaultTab();
            });

        }

        private void StopGameflowWatcher()
        {
            _watcherCts?.Cancel();
        }
        #endregion

        #region �������ز�ѯս��������ʾͷ�񣬺���ʾս��
        private async Task CreateBasicCardsOnly(JArray team, bool isMyTeam, int row)
        {
            Debug.WriteLine($"[CreateBasicCardsOnly] ��ʼ���� {(isMyTeam ? "�ҷ�" : "�з�")} ��Ƭ���к�: {row}");
            int col = 0;

            foreach (var p in team)
            {
                long summonerId = (long)p["summonerId"];
                int championId = (int)p["championId"];

                // Ӣ��û������������
                if (_currentChampBySummoner.TryGetValue(summonerId, out int prevChampId) && prevChampId == championId)
                {
                    _summonerToColMap[summonerId] = col++;
                    //Debug.WriteLine($"[CreateBasicCardsOnly] summonerId={summonerId} Ӣ��δ�䣬����ͷ����أ�col={col - 1}");
                    continue;
                }

                // ���¿����ֵ�
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
                    GameName = "������...",
                    IsPublic = "[������]",
                    SoloRank = "������...",
                    FlexRank = "������..."
                };

                var matchInfo = new PlayerMatchInfo
                {
                    Player = player,
                    MatchItems = new List<ListViewItem>(),
                    HeroIcons = new ImageList()
                };

                //Debug.WriteLine($"[CreateBasicCardsOnly] ������Ƭ summonerId={summonerId}, championId={championId}, col={col}");
                UpdateOrCreateLoadingPlayerMatch(matchInfo, isMyTeam, row, col);

                col++;
            }

            Debug.WriteLine($"[CreateBasicCardsOnly] ��� {(isMyTeam ? "�ҷ�" : "�з�")} ��Ƭ�������� {col} �����");
        }

        private async Task FillPlayerMatchInfoAsync(JArray team, bool isMyTeam, int row)
        {
            Debug.WriteLine($"[FillPlayerMatchInfoAsync] ��ʼ�첽ս����ѯ {(isMyTeam ? "�ҷ�" : "�з�")}���к�: {row}");

            var fetchedInfos = await RunWithLimitedConcurrency(
                team,
                async p =>
                {
                    long sid = p["summonerId"]?.Value<long>() ?? 0;
                    int cid = p["championId"]?.Value<int>() ?? 0;

                    PlayerMatchInfo info;

                    // �ȿ������Ƿ���
                    lock (_cachedPlayerMatchInfos)
                    {
                        if (_cachedPlayerMatchInfos.TryGetValue(sid, out info))
                        {
                            //Debug.WriteLine($"[ʹ�û���] summonerId={sid}");

                            if (_currentChampBySummoner.TryGetValue(sid, out int current) && current == cid)
                            {
                                int col = _summonerToColMap.TryGetValue(sid, out int c) ? c : 0;

                                // �жϿ�Ƭ�Ƿ���Ϊ�������С�
                                var panel = tableLayoutPanel1.GetControlFromPosition(col, row) as BorderPanel;
                                var card = panel?.Controls.Count > 0 ? panel.Controls[0] as PlayerCardControl : null;

                                if (card != null && card.IsLoading)
                                {
                                    Debug.WriteLine($"[ˢ�¼����п�Ƭ] summonerId={sid}");
                                    CreateLoadingPlayerMatch(info, isMyTeam, row, col);
                                }
                            }

                            return info;
                        }
                    }

                    // �ǻ������У�ִ������
                    Debug.WriteLine($"[ս������] ��ѯ��ʼ summonerId={sid}, championId={cid}");
                    info = await SafeFetchPlayerMatchInfoAsync(p);
                    if (info == null)
                    {
                        Debug.WriteLine($"[����] summonerId={sid} ��ȡʧ�ܣ�info Ϊ null");

                        // ����һ��ʧ�ܿ�Ƭ����ʾ
                        var failedInfo = new PlayerMatchInfo
                        {
                            Player = new PlayerInfo
                            {
                                SummonerId = sid,
                                ChampionId = cid,
                                ChampionName = "��ѯʧ��",
                                GameName = "ʧ��",
                                IsPublic = "[ʧ��]",
                                SoloRank = "ʧ��",
                                FlexRank = "ʧ��",
                                Avatar = LoadErrorImage() // �滻Ϊ���Լ��Ĵ���ͼ
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

                    // ȷ��������ǵ�ǰӢ��
                    if (_currentChampBySummoner.TryGetValue(sid, out int curCid) && curCid == cid)
                    {
                        int col = _summonerToColMap.TryGetValue(sid, out int c) ? c : 0;
                        CreateLoadingPlayerMatch(info, isMyTeam, row, col);
                    }
                    else
                    {
                        Debug.WriteLine($"[����ս������] summonerId={sid} �Ѹ���Ӣ��");
                    }

                    return info;
                },
                maxConcurrency: 3
            );

            Debug.WriteLine($"[FillPlayerMatchInfoAsync] �첽ս����ѯ��ɣ�����ȡ {fetchedInfos.Count} ��");

            // ������ӹ�ϵ�����Է� null �� info ��Ч
            var detector = new PartyDetector();
            detector.Detect(fetchedInfos.Where(f => f != null).ToList());

            // ������ɫ
            foreach (var info in fetchedInfos)
            {
                if (info?.Player == null) continue;
                UpdatePlayerNameColor(info.Player.SummonerId, info.Player.NameColor);
            }
        }


        private async Task ShowMyTeamCards()
        {
            //Debug.WriteLine("[ShowMyTeamCards] ��ȡѡ�˻Ự��...");
            var session = await Globals.lcuClient.GetChampSelectSession();
            if (session == null)
            {
                Debug.WriteLine("[ShowMyTeamCards] ��ȡʧ��: session == null");
                return;
            }

            var myTeam = session["myTeam"] as JArray;
            if (myTeam == null || myTeam.Count == 0)
            {
                Debug.WriteLine("[ShowMyTeamCards] ��ȡʧ��: myTeam ����Ϊ��");
                return;
            }

            myTeamId = (int)myTeam[0]["team"];
            int row = myTeamId - 1;
            //Debug.WriteLine($"[ShowMyTeamCards] �ҵĶ��� teamId={myTeamId}, row={row}");

            // ���ɵ�ǰ����
            var currentSnapshot = new List<string>();
            foreach (var player in myTeam)
            {
                long summonerId = (long)player["summonerId"];
                int championId = (int)player["championId"];
                currentSnapshot.Add($"{summonerId}:{championId}");
            }

            // �ȽϿ��գ���һ���򲻸���
            if (lastChampSelectSnapshot.SequenceEqual(currentSnapshot))
            {
                //Debug.WriteLine("[ShowMyTeamCards] ����δ�仯������ˢ��");
                return;
            }

            // �������
            lastChampSelectSnapshot = currentSnapshot;

            //Debug.WriteLine("[ShowMyTeamCards] ����仯����ʼˢ��");

            //���ҷ����ݴ洢
            _cachedMyTeam = myTeam;

            // ˢ�� UI ��ս��
            await CreateBasicCardsOnly(myTeam, isMyTeam: true, row: row);
            //_ = FillPlayerMatchInfoAsync(myTeam, isMyTeam: true, row: row);
            await FillPlayerMatchInfoAsync(myTeam, isMyTeam: true, row: row);

            // �����ȼ�����
            if (!_champSelectMessageSent)
            {
                ListenAndSendMessageWhenHotkeyPressed(myTeam);
                _champSelectMessageSent = true;
            }
        }

        // ���� Win32 API
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(Keys vKey);

        private void ListenAndSendMessageWhenHotkeyPressed(JArray myTeam)
        {
            Task.Run(() =>
            {
                Debug.WriteLine("[HotKey] �ȴ��û����¿�ݼ� F9...");

                while (true)
                {
                    if ((GetAsyncKeyState(Keys.F9) & 0x8000) != 0)
                    {
                        Debug.WriteLine("[HotKey] ��⵽ F9 �����£�");

                        // �л� UI �̣߳���֤ STA��
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
        /// �����ҷ��������ݵ�ѡ�����촰��
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

                //��ȡ��ǰ��puuid
                string puuid = (string)p["puuid"];
                //�ж��Ƿ��봰�ڼ���ʱ��puuidһ����һ�������Լ��ģ�·��������
                if (!string.IsNullOrEmpty(puuid) && string.Equals(puuid, Globals.CurrentPuuid, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine($"[��������] ��ǰ���:{p["gameName"].ToString()}");
                    continue;
                }

                string name = info.Player.GameName ?? "δ֪";
                string solo = info.Player.SoloRank ?? "δ֪";
                string flex = info.Player.FlexRank ?? "δ֪";

                var wins = info.WinHistory.Count(w => w);
                var total = info.WinHistory.Count;
                double winRate = total > 0 ? (wins * 100.0 / total) : 0;

                //sb.AppendLine($"{name}: ��˫�� {solo} | ��� {flex} | ��20��ʤ��: {winRate:F1}%");

                // ƴ�ӽ�10�� KDA
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
                    kdaString = "�޼�¼";
                }

                sb.AppendLine($"{name}: ��˫�� {solo} | ��� {flex} | ��20��ʤ��: {winRate:F1}% | ��10��KDA: {kdaString}");
            }

            string allMessage = sb.ToString().Trim();
            var lines = allMessage.Split(new[] { Environment.NewLine }, StringSplitOptions.None);

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                Clipboard.SetText(line);

                // �������
                SendKeys.SendWait("{ENTER}");
                Thread.Sleep(50);

                // ճ��
                SendKeys.SendWait("^v");
                Thread.Sleep(50);

                // �س�����
                SendKeys.SendWait("{ENTER}");
                Thread.Sleep(100);
            }

            Debug.WriteLine("[ս����Ϣ] SendKeys ������� (���з���)");
        }

        private async Task ShowEnemyTeamCards()
        {
            try
            {
                Debug.WriteLine("��ʼִ�� ShowEnemyTeamCards");

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

                //���з����ݴ洢
                _cachedEnemyTeam = enemyTeam;

                // �ȴ���ͷ��ռλ��Ƭ
                await CreateBasicCardsOnly(enemyTeam, isMyTeam: false, row: enemyRow);

                // ���첽�����λ/ս��
                //_ = FillPlayerMatchInfoAsync(enemyTeam, isMyTeam: false, row: enemyRow);
                await FillPlayerMatchInfoAsync(enemyTeam, isMyTeam: false, row: enemyRow);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ShowEnemyTeamCards �쳣: " + ex.ToString());
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
                        Debug.WriteLine($"[�����쳣] Index {item.index}: {ex.Message}");
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                tasks.Add(task);
            }

            await Task.WhenAll(tasks);
            return results.ToList(); // ˳��������һ��
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
                    Debug.WriteLine($"[Fetchʧ��] �� {attempt} �γ���ʧ��: {ex.Message}");
                    if (attempt <= retryTimes)
                        await Task.Delay(1000);
                }
            }

            Debug.WriteLine("[Fetchʧ��] ��������ʧ�ܣ����� null");
            return null; // �ص㣺��Ҫ������Ч����
        }

        //ͷ���ȡʧ����ʾĬ��ͼ
        private Image LoadErrorImage()
        {
            return Image.FromFile(AppDomain.CurrentDomain.BaseDirectory + "Assets\\Defaults\\Profile.png");
        }
        #endregion

        #region ������ʷս������������
        /// <summary>
        /// ����summoner��Ϣ��ȡ��ҵ�puuid����λ����ʷս��
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
            Image iconChamp = await Globals.lcuClient.GetChampionIconAsync(championId); //��ȡͷ��.ConfigureAwait(false)
            // �ȳ��Դӻ����ȡ
            if (playerMatchCache.TryGetValue(summonerId, out var cachedMatch))
            {
                cachedMatch.Player.ChampionId = championId;
                cachedMatch.Player.ChampionName = championName;
                cachedMatch.Player.Avatar = iconChamp;
                cachedMatch.IsFromCache = true;  // ����ǻ���
                return cachedMatch;
            }

            // ����û���У�����API��ȡ��ϸ��Ϣ
            string puuid = "";
            string gameName = "δ֪���";
            string privacyStatus = "[����]";
            string soloRank = "δ֪";
            string flexRank = "δ֪";

            var summoner = await Globals.lcuClient.GetGameNameBySummonerId(summonerId.ToString());
            if (summoner != null)
            {
                puuid = summoner["puuid"]?.ToString() ?? "";
                gameName = summoner["gameName"]?.ToString() ?? "δ֪���";

                if (summoner["privacy"]?.ToString().Equals("PUBLIC", StringComparison.OrdinalIgnoreCase) == true)
                    privacyStatus = "[����]";

                var rankedJson = await Globals.lcuClient.GetCurrentRankedStatsAsync(puuid);
                var rankedStats = RankedStats.FromJson(rankedJson);

                if (rankedStats != null)
                {
                    if (rankedStats.TryGetValue("��˫��", out var soloStats))
                        soloRank = $"{soloStats.FormattedTier}({soloStats.LeaguePoints})";

                    if (rankedStats.TryGetValue("�������", out var flexStats))
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

            // ��ȡ�����б�20����
            var matches = await Globals.lcuClient.FetchLatestMatches(puuid);

            // ����PlayerMatchInfo��������������
            var matchInfo = GetPlayerMatchInfo(puuid, matches);
            matchInfo.Player = playerInfo;
            matchInfo.IsFromCache = false;

            // ���»���
            playerMatchCache[summonerId] = matchInfo;

            return matchInfo;
        }


        /// <summary>
        /// ������Ϸ���䣬����puuid��ȡ���ݣ�������������ҵ���ʷս������
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
                return result; // ֱ�ӷ��ؿյģ���������쳣
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
                    // �첽����ͼƬ��������Ҫ�������ʵ���������
                    // ������Ҫ��Ϊ�첽������Ԥ�ȼ�������ͼƬ
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

                // ���浽 RecentMatches ��������kda��Ϣ
                result.RecentMatches.Add(new MatchStat
                {
                    Kills = kills,
                    Deaths = deaths,
                    Assists = assists
                });

                // ����
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

                // ����ƥ���õ�key��gameId_teamId ����ͬһ����ͬһ���顱
                result.MatchKeys.Add($"{gameId}_{teamId}");
            }

            result.HeroIcons = heroIcons;
            return result;
        }

        #endregion

        #region UI���´���

        /// <summary>
        /// ֻ�����������ͬ�������ɫ
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
        /// �����ж��Ƿ񷿼������л���Ӣ�ۣ����ֻ�л�Ӣ�������Ӣ��ͷ��
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
                    // �� UI ��ǰ�Ǽ����У���ˢ��
                    var panel = tableLayoutPanel1.GetControlFromPosition(column, row) as BorderPanel;
                    var card = panel?.Controls.Count > 0 ? panel.Controls[0] as PlayerCardControl : null;

                    if (card != null && card.IsLoading)
                    {
                        Debug.WriteLine($"[ˢ�¼����п�Ƭ] summonerId={summonerId}");
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

            // ��һ�Ӣ����ȫ�����ֱ���ؽ���Ƭ
            CreateLoadingPlayerMatch(matchInfo, isMyTeam, row, column);
            playerCache[key] = (summonerId, championId);
        }

        /// <summary>
        /// ����Ӣ��ͷ�񷽷�
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
                        card.SetAvatarOnly(player.Avatar); // ֻ����ͷ�񣬲����б�
                    }
                }
            });
        }


        /// <summary>
        /// ���ݻ�ȡ���ĵ���˫�����ݴ�����Ƭ��������UI��ʾ
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

            // ע��ӳ�䣬����֮��ֻ������ɫ
            _cardBySummonerId[matchInfo.Player.SummonerId] = card;

            string name = player.GameName ?? "δ֪";
            string soloRank = string.IsNullOrEmpty(player.SoloRank) ? "δ֪" : player.SoloRank;
            string flexRank = string.IsNullOrEmpty(player.FlexRank) ? "δ֪" : player.FlexRank;

            Color nameColor = matchInfo.Player.NameColor;
            card.SetPlayerInfo(name, soloRank, flexRank, player.Avatar, player.IsPublic, matchItems, nameColor);
            card.ListViewControl.SmallImageList = heroIcons;
            card.ListViewControl.View = View.Details;

            panel.Controls.Add(card);

            // ����ؼ�ǰ���Ƴ��ɵ�
            SafeInvoke(tableLayoutPanel1, () =>
            {
                var oldControl = tableLayoutPanel1.GetControlFromPosition(column, row);
                if (oldControl != null)
                {
                    tableLayoutPanel1.Controls.Remove(oldControl);
                    oldControl.Dispose(); // �ͷžɿؼ���Դ
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
                    control.BeginInvoke(action); // �첽�������������߳�
                }
                catch
                {
                    // �ؼ�������ʱ����
                }
            }
            else
            {
                action();
            }
        }
        #endregion

        #region ��ѯս�������
        /// <summary>
        /// ��ѯ����
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

            // ��ȡ��ɸѡ gameId
            var gameIds = matches
                .Where(m => string.IsNullOrEmpty(queueId) ||
                           m["queueId"]?.ToString() == queueId)
                .Select(m => m["gameId"]?.Value<long>())
                .Where(id => id != null)
                .Distinct()
                .ToList();

            // ��ȡÿ��������ս��Ϣ
            var tasks = gameIds.Select(id => Globals.lcuClient.GetFullMatchByGameIdAsync(id.Value));
            var results = await Task.WhenAll(tasks);

            // תΪ JArray ����
            var fullMatches = results.Where(r => r != null);
            return new JArray(fullMatches);
        }

        /// <summary>
        /// ���ݽ���
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
        /// �󶨰�ť�¼�
        /// </summary>
        /// <param name="fullName"></param>
        private async void Panel_PlayerIconClicked(string fullName)
        {
            try
            {
                txtGameName.Text = fullName;    //���ͷ��ʱ���û�������������Դ������ò�ѯ����
                var parts = fullName.Split('#');
                if (parts.Length != 2) return;

                var summoner = await Globals.lcuClient.GetSummonerByNameAsync(fullName);
                if (summoner == null) return;

                // ����puuid��ȡԭʼ����
                var rankedJson = await Globals.lcuClient.GetCurrentRankedStatsAsync(summoner["puuid"].ToString());

                // ֱ��ͨ���෽������
                var rankedStats = RankedStats.FromJson(rankedJson);

                string privacyStatus = "����";
                if (summoner["privacy"]?.ToString().Equals("PUBLIC", StringComparison.OrdinalIgnoreCase) == true)
                    privacyStatus = "����";
                // ֱ�Ӵ�����ǩҳ��������Ҫ����������Ϣ��
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
                MessageBox.Show($"��ѯʧ��: {ex.Message}");
            }
        }
        #endregion

        #region LCU ���������ʾ
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
                Font = new Font("΢���ź�", 12, FontStyle.Bold)
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
                    Text = string.IsNullOrEmpty(exePath) ? "δ��⵽ LOL ��¼����" : exePath,
                    Font = new Font("΢���ź�", 10, FontStyle.Regular)
                };

                var btnStartLol = new Button
                {
                    Text = "����LOL��¼����",
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
                            Debug.WriteLine("�ҵ� LOL ��¼����" + exePath);
                            helper.StartLOLLoginProgram(exePath);
                        }
                        else
                        {
                            Debug.WriteLine("δ��⵽ LOL ��¼����");
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
                "���ڼ��LCU���ӣ���ȷ����¼����Ϸ...",
                showLolLauncher: true
            );

            panel.Left = (parentControl.Width - panel.Width) / 2;
            panel.Top = (parentControl.Height - panel.Height) / 2;
            panel.Anchor = AnchorStyles.None;

            parentControl.Controls.Add(panel);
        }


        private void ShowWaitingForGameMessage(Control parentControl)
        {
            parentControl.Controls.Clear();   // �������������оɿؼ�

            tableLayoutPanel1.Visible = false;

            _waitingPanel = CreateStatusPanel("���ڵȴ�������Ϸ�����Ժ�...", showLolLauncher: false);

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
                    Debug.WriteLine("�Զ�������");
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
                    // ����
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Tab1Poller��ѯ�쳣: {ex}");
                }
            }, 3000);
        }

        #endregion
    }
}