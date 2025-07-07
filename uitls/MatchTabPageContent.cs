using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Forms;
using League.model;
using Newtonsoft.Json.Linq;

namespace League.uitls
{
    public partial class MatchTabPageContent : UserControl
    {
        // 分页状态
        private int _currentPage = 1;
        private int _pageSize = 8;
        private string _selectedQueueId = "";
        private string _puuid;

        // 数据加载事件
        public event Func<string, int, int, string, Task<JArray>> LoadDataRequested;
        public event Func<JObject, string, Task<Panel>> ParsePanelRequested;

        private SemaphoreSlim _loadSemaphore = new SemaphoreSlim(1, 1);
        private SemaphoreSlim _updateLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _updateCts;

        public MatchTabPageContent()
        {
            InitializeComponent();

            this.Dock = DockStyle.Fill; // 这将使控件填充父容器
            this.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            this.AutoScroll = true;

            //初始化数据绑定
            InitComboBoxes();
            WireEvents();
        }

        // 初始化筛选器
        private void InitComboBoxes()
        {
            comboPage.Items.AddRange(new object[] { 8, 20, 50, 100 });
            comboPage.SelectedItem = 8; // 默认值
            _pageSize = 8;

            comboFilter.DisplayMember = "Text";
            comboFilter.ValueMember = "Value";
            comboFilter.Items.AddRange(new[]
            {
                new QueueFilterItem { Text = "全部", Value = "" },
                new QueueFilterItem { Text = "单双排位", Value = "420" },
                new QueueFilterItem { Text = "灵活排位", Value = "440" },
                new QueueFilterItem { Text = "匹配模式", Value = "430" },
                new QueueFilterItem { Text = "大乱斗", Value = "450" }
            });
            comboFilter.SelectedIndex = 0;
        }

        //下拉框事件绑定
        private void WireEvents()
        {
            btnPrev.Click += async (s, e) => await OnPrevPage();
            btnNext.Click += async (s, e) => await OnNextPage();

            // 将事件处理程序保存到成员变量
            asyncComboPageChangedHandler = async (s, e) => await OnPageSizeChanged();
            comboPage.SelectedIndexChanged += asyncComboPageChangedHandler;

            comboFilter.SelectedIndexChanged += async (s, e) => await OnFilterChanged();
        }

        public void Initialize(string puuid)
        {
            if (string.IsNullOrEmpty(puuid))
                throw new ArgumentException("PUUID不能为空");

            _puuid = puuid;
            _ = LoadDataAsync(); // 异步初始化加载
        }

        private async Task LoadDataAsync(bool resetPage = true)
        {
            if (resetPage) _currentPage = 1;
            await UpdateMatchesDisplay();
        }

        private async Task UpdateMatchesDisplay()
        {
            await _loadSemaphore.WaitAsync();
            const int maxFetchLimit = 200;

            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] 开始加载数据 - PUUID: {_puuid.Substring(0, 5)}...");

            if (LoadDataRequested == null || ParsePanelRequested == null)
            {
                Debug.WriteLine("[严重错误] 未绑定数据加载事件");
                ShowErrorMessage("系统错误：功能未初始化");
                return;
            }

            try
            {
                SetLoadingState(true);

                int begIndex = (_currentPage - 1) * _pageSize;
                int endIndex = begIndex + (_pageSize - 1);

                if (endIndex > maxFetchLimit)
                    endIndex = maxFetchLimit;

                // 重新计算最大页数
                TotalPages = (int)Math.Ceiling((double)maxFetchLimit / _pageSize);

                var matches = await LoadDataRequested(_puuid, begIndex, endIndex, _selectedQueueId);

                Debug.WriteLine($"收到 {matches?.Count ?? 0} 条数据");
                if (matches == null || matches.Count == 0)
                {
                    ShowEmptyMessage();
                    return; // 直接退出，不再执行后续逻辑
                }

                Debug.WriteLine("开始更新界面...");
                await UpdateMatchList(matches);

                // 更新按钮状态
                btnPrev.Enabled = _currentPage > 1;
                btnNext.Enabled = _currentPage < TotalPages;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[异常] {ex.GetType().Name}: {ex.Message}");
                Debug.WriteLine(ex.StackTrace);
                ShowErrorMessage($"加载失败: {ex.Message}");
            }
            finally
            {
                SetLoadingState(false);
                Debug.WriteLine("界面更新结束");
                _loadSemaphore.Release();
            }
        }


        private async Task UpdateMatchList(JArray matches)
        {
            await _updateLock.WaitAsync();
            try
            {
                _updateCts?.Cancel();
                _updateCts = new CancellationTokenSource();
                var token = _updateCts.Token;

                // 统一线程切换逻辑
                if (flowLayoutPanelRight.InvokeRequired)
                {
                    await flowLayoutPanelRight.Invoke(async () =>
                    {
                        if (!token.IsCancellationRequested)
                            await UpdateMatchListInternal(matches, token);
                    });
                }
                else
                {
                    if (!token.IsCancellationRequested)
                        await UpdateMatchListInternal(matches, token);
                }
            }
            finally
            {
                _updateLock.Release();
            }
        }

        private async Task UpdateMatchListInternal(JArray matches, CancellationToken token)
        {
            // step 1: 在后台线程处理耗时工作
            var panels = await Task.Run(async () =>
            {
                var panelList = new List<Panel>();

                var tasks = matches.Cast<JObject>()
                    .Select(match => ParsePanelRequested?.Invoke(match, _puuid))
                    .ToList();

                foreach (var t in tasks)
                {
                    if (token.IsCancellationRequested) break;

                    var panel = await t.ConfigureAwait(false);
                    if (panel != null) panelList.Add(panel);
                }

                return panelList;
            });

            // step 2: 回到 UI 线程
            if (!token.IsCancellationRequested)
            {
                if (flowLayoutPanelRight.InvokeRequired)
                {
                    flowLayoutPanelRight.Invoke(new Action(() =>
                    {
                        flowLayoutPanelRight.SuspendLayout();
                        flowLayoutPanelRight.Controls.Clear();
                        flowLayoutPanelRight.Controls.AddRange(panels.ToArray());
                        flowLayoutPanelRight.ResumeLayout(true);
                    }));
                }
                else
                {
                    flowLayoutPanelRight.SuspendLayout();
                    flowLayoutPanelRight.Controls.Clear();
                    flowLayoutPanelRight.Controls.AddRange(panels.ToArray());
                    flowLayoutPanelRight.ResumeLayout(true);
                }
            }
        }

        public async Task InitiaRank(string fullName, string profileIconId, string summonerLevel, string privacy, Dictionary<string, RankedStats> rankedStats)
        {
            linkGameName.Text = fullName;
            lblLevel.Text = $"玩家等级：【{summonerLevel}】";
            lblPrivacy.Text = $"身份状态：【{privacy}】";
            picChampionId.Image = await Profileicon.GetProfileIconAsync(int.Parse(profileIconId));

            // 检查数据是否存在
            if (rankedStats == null) return;

            // 访问单双排数据（安全访问）
            if (rankedStats.TryGetValue("单双排", out var soloStats))
            {
                lblSoloTier.Text = soloStats.FormattedTier;      // 段位
                lblSoloGames.Text = $"{soloStats.TotalGames} 场";    //场次
                lblSoloWins.Text = $"{soloStats.Wins}场";  //胜场
                lblSoloLosses.Text = $"{soloStats.Losses}场";  //负场
                lblSoloWinRate.Text = $"{soloStats.WinRateDisplay}%";  //胜率
                //lblSoloWinRate.Text = $"{soloStats.WinRate}%";  //胜率
                lblSoloLeaguePoints.Text = $"{soloStats.LeaguePoints}点";  //胜点
            }

            // 访问灵活组排数据
            if (rankedStats.TryGetValue("灵活组排", out var flexStats))
            {
                lblFlexTier.Text = flexStats.FormattedTier;      // 段位
                lblFlexGames.Text = $"{flexStats.TotalGames} 场";    //场次
                lblFlexWins.Text = $"{flexStats.Wins}场";  //胜场
                lblFlexLosses.Text = $"{flexStats.Losses}场";  //负场
                lblFlexWinRate.Text = $"{flexStats.WinRateDisplay}%";  //胜率胜率
                //lblFlexWinRate.Text = $"{flexStats.WinRate}%";  //胜率胜率
                lblFlexLeaguePoints.Text = $"{flexStats.LeaguePoints}点";  //胜点
            }
        }

        private async Task OnPrevPage()
        {
            if (_currentPage > 1)
            {
                _currentPage--;
                await UpdateMatchesDisplay();
            }
        }

        private async Task OnNextPage()
        {
            if (_currentPage < TotalPages)
            {
                _currentPage++;
                await UpdateMatchesDisplay();
            }
        }

        private async Task OnPageSizeChanged()
        {
            if (int.TryParse(comboPage.SelectedItem?.ToString(), out int newPageSize))
            {
                if (_pageSize != newPageSize)
                {
                    _pageSize = newPageSize;
                    _currentPage = 1;
                    await UpdateMatchesDisplay(); // 关键！重新加载
                }
            }
        }


        private async Task OnFilterChanged()
        {
            var item = comboFilter.SelectedItem as QueueFilterItem;
            _selectedQueueId = item?.Value;

            int desiredPageSize = string.IsNullOrEmpty(_selectedQueueId) ? 8 : 50;

            if (_pageSize != desiredPageSize)
            {
                // 移除事件处理程序以防止重复触发
                comboPage.SelectedIndexChanged -= asyncComboPageChangedHandler;
                // 使用整数设置SelectedItem
                comboPage.SelectedItem = desiredPageSize;
                // 重新绑定事件
                comboPage.SelectedIndexChanged += asyncComboPageChangedHandler;

                _pageSize = desiredPageSize;
            }

            _currentPage = 1;
            await UpdateMatchesDisplay();
        }

        // 你需要把这个事件委托保存为成员
        private EventHandler asyncComboPageChangedHandler;

        private void SetLoadingState(bool isLoading)
        {
            if (IsDisposed || Disposing) return;

            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke((Action)(() => SetLoadingState(isLoading)));
                    return;
                }

                // 添加控件存在性检查
                if (btnPrev != null) btnPrev.Enabled = !isLoading;
                if (btnNext != null) btnNext.Enabled = !isLoading;
                if (comboPage != null) comboPage.Enabled = !isLoading;
                if (comboFilter != null) comboFilter.Enabled = !isLoading;
            }
            catch (ObjectDisposedException ex)
            {
                Debug.WriteLine($"控件已释放: {ex.Message}");
            }
        }

        private void ShowErrorMessage(string message)
        {
            Invoke((Action)(() =>
            {
                flowLayoutPanelRight.Controls.Clear();
                flowLayoutPanelRight.Controls.Add(new Label
                {
                    Text = message,
                    ForeColor = Color.Red,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter
                });
            }));
        }

        private void ShowEmptyMessage()
        {
            if (flowLayoutPanelRight.InvokeRequired)
            {
                flowLayoutPanelRight.Invoke((Action)ShowEmptyMessage);
                return;
            }

            flowLayoutPanelRight.SuspendLayout();
            try
            {
                flowLayoutPanelRight.Controls.Clear();

                // 创建标签并显式设置属性
                var label = new Label
                {
                    Text = "近50场比赛没有找到匹配的数据！",
                    TextAlign = ContentAlignment.MiddleCenter,
                    AutoSize = false,
                    Size = new Size(flowLayoutPanelRight.ClientSize.Width, 50),
                    BackColor = Color.White,
                    ForeColor = Color.Red,
                    Dock = DockStyle.Top // 确保标签占据顶部
                };

                flowLayoutPanelRight.Controls.Add(label);
                label.BringToFront(); // 防止被其他控件覆盖

                // 调试输出
                Debug.WriteLine($"Label尺寸: {label.Width}x{label.Height}, 父容器尺寸: {flowLayoutPanelRight.Size}");
            }
            finally
            {
                flowLayoutPanelRight.ResumeLayout(true);
                flowLayoutPanelRight.PerformLayout();
                flowLayoutPanelRight.Update();
                flowLayoutPanelRight.Parent?.Refresh();
            }
        }

        private void linkGameName_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            // 获取LinkLabel的文本
            string textToCopy = linkGameName.Text;

            // 复制文本到剪贴板
            Clipboard.SetText(textToCopy);

            // 将屏幕坐标转成控件坐标
            Point clientPos = linkGameName.PointToClient(Cursor.Position);

            // 在鼠标附近显示 ToolTip
            new ToolTip().Show($"已复制: {textToCopy}", linkGameName, clientPos.X + 10, clientPos.Y + 10, 1500);
        }

        #region 公共属性
        [Browsable(false)]
        public int CurrentPage => _currentPage;

        [Browsable(false)]
        public int TotalPages { get; private set; }

        [Browsable(false)]
        public string CurrentFilter => _selectedQueueId;
        #endregion
    }
    public class QueueFilterItem
    {
        public string Text { get; set; }
        public string Value { get; set; }
    }
}
