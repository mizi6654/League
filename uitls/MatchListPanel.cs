using System.Diagnostics;
using League.model;

namespace League
{
    public class MatchListPanel : Panel
    {
        //用来设置鼠标移动时的文字提示
        private ToolTip _tooltip = new ToolTip();
        private string _lastTooltip = "";

        private MatchInfo _matchInfo;
        public MatchInfo MatchInfo
        {
            get => _matchInfo;
            set
            {
                _matchInfo = value;
                Invalidate();
            }
        }

        // ===== 布局常量 =====
        private const int Padding = 10;           // 整体内边距
        private const int IconSize = 64;          // 英雄头像尺寸
        private const int SpellSize = 30;         // 召唤师技能尺寸
        private const int SpellSpacing = 4;       // 技能间距
        private const int ItemSize = 30;           // 装备图标尺寸
        private const int ItemSpacing = 4;         // 装备间距

        // 新增固定布局常量
        private const int ModeWidth = 120;        // 模式区域固定宽度
        private const int KdaX = 280;             // KDA固定X坐标
        private const int SpellStartX = 395;       // 召唤师技能固定起始X
        private const int ItemsStartX = 260;       // 装备区域起始X（与技能对齐）
        private const int emptyItemX = 190;       // 符文技能区域X（与装备对齐）
        private const int ItemsYOffset = 30;       // 装备相对顶部的固定Y偏移

        // 队伍成员显示相关
        private const int TeamIconSize = 30;
        private const int TeamIconSpacing = 2;
        private const int TeamRowSpacing = 8;

        public MatchListPanel(MatchInfo match)
        {
            _matchInfo = match;
            SetStyle(ControlStyles.UserPaint |
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.ResizeRedraw, true);
            BorderStyle = BorderStyle.None;
            BackColor = Color.FloralWhite;
            //BackColor = Color.White;
            DoubleBuffered = true;
            //Size = new Size(730, 80);
            Size = new Size(950, 80);

            //设置图片提示，让它变的更灵敏
            _tooltip = new ToolTip
            {
                InitialDelay = 100,
                ReshowDelay = 100,
                AutoPopDelay = 15000,
                ShowAlways = true
            };
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 空实现禁止默认背景绘制
        }

        #region 绘制列表详情
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (MatchInfo == null) return;

            var g = e.Graphics;

            // 背景
            using (var backgroundBrush = new SolidBrush(BackColor))
            {
                g.FillRectangle(backgroundBrush, ClientRectangle);
            }

            var primaryFont = new Font("微软雅黑", 9);
            var boldFont = new Font("微软雅黑", 11, FontStyle.Bold);

            var headerY = Padding + 4;
            var iconX = Padding + 60;

            using (var resultBrush = new SolidBrush(MatchInfo.ResultColor))
            {
                g.DrawString(MatchInfo.ResultText, boldFont, resultBrush,
                    new RectangleF(Padding, headerY, 90, boldFont.Height));
            }

            var durationY = headerY + boldFont.Height + 8;
            g.DrawString(MatchInfo.DurationText, primaryFont, Brushes.DimGray, Padding, durationY);
            g.DrawString(MatchInfo.GameTime, primaryFont, Brushes.DimGray, Padding + 130, durationY);

            // 绘制英雄头像
            g.DrawImage(MatchInfo.HeroIcon, iconX, Padding, IconSize, IconSize);

            var modeX = iconX + IconSize + Padding;
            g.DrawString(MatchInfo.Mode, primaryFont, Brushes.DarkSlateBlue,
                new RectangleF(modeX, headerY - 5, ModeWidth, 20));

            // ===== 召唤师技能：移到 KDA 上方 =====
            int spellY = headerY - 6;
            for (int i = 0; i < 2; i++)
            {
                if (MatchInfo.SummonerSpells[i] != null)
                {
                    g.DrawImage(MatchInfo.SummonerSpells[i],
                        SpellStartX + i * (SpellSize + SpellSpacing),
                        spellY,
                        SpellSize,
                        SpellSize);
                }
            }

            // ===== KDA：保留原位置 =====
            var kdaText = $"{MatchInfo.Kills} / {MatchInfo.Deaths} / {MatchInfo.Assists}";
            g.DrawString(kdaText, primaryFont, Brushes.DarkSlateGray, KdaX, headerY - 2);

            // ===== 绘制符文 =====
            int runeY = headerY + ItemsYOffset - (ItemSize + 4);
            for (int i = 0; i < 6; i++)
            {
                int x = emptyItemX + KdaX + i * (ItemSize + ItemSpacing);
                var rect = new Rectangle(x, runeY, ItemSize, ItemSize);

                // 先绘制背景框
                using (var pen = new Pen(Color.Gray))
                {
                    g.DrawRectangle(pen, rect);
                }

                // 在绘制符文图片前，填充黑色背景（让透明PNG更清晰）
                using (var bgBrush = new SolidBrush(Color.Black))  // 或用 Color.FromArgb(30, 30, 30) 深灰
                {
                    g.FillRectangle(bgBrush, rect);
                }

                // 根据索引决定绘制主系还是副系符文
                Image runeImage = null;

                if (i < 4
                    && MatchInfo.PrimaryRunes != null
                    && i < MatchInfo.PrimaryRunes.Length
                    && MatchInfo.PrimaryRunes[i] != null)
                {
                    runeImage = MatchInfo.PrimaryRunes[i].Icon;
                }
                else if (i >= 4
                    && MatchInfo.SecondaryRunes != null
                    && (i - 4) < MatchInfo.SecondaryRunes.Length
                    && MatchInfo.SecondaryRunes[i - 4] != null)
                {
                    runeImage = MatchInfo.SecondaryRunes[i - 4].Icon;
                }


                // 如果有符文图片则绘制
                if (runeImage != null)
                {
                    g.DrawImage(runeImage, rect);

                    // 如果是基石符文（主系第一个），加特殊边框
                    if (i == 0 && MatchInfo.PrimaryRunes != null && MatchInfo.PrimaryRunes.Length > 0)
                    {
                        using (var pen = new Pen(Color.Goldenrod, 2))
                        {
                            g.DrawRectangle(pen, rect);
                        }
                    }
                }
            }

            // ===== 原装备区域：右移后绘制 =====
            int itemsY = headerY + ItemsYOffset;
            for (int i = 0; i < 6; i++)
            {
                if (MatchInfo.Items[i] != null)
                {
                    g.DrawImage(MatchInfo.Items[i].Icon,
                        ItemsStartX + i * (ItemSize + ItemSpacing),
                        itemsY,
                        ItemSize,
                        ItemSize);
                }
            }

            // ===== 右侧数据列 =====
            var statsX = Width + emptyItemX - Padding - 450;
            g.DrawString($"伤害：{MatchInfo.DamageText}", primaryFont, Brushes.OrangeRed, statsX, headerY);
            g.DrawString($"经济：{MatchInfo.GoldText}", primaryFont, Brushes.Goldenrod, statsX, headerY + 20);
            g.DrawString($"参团：{MatchInfo.KPPercentage}", primaryFont, Brushes.DodgerBlue, statsX, headerY + 40);

            // ===== 队伍成员显示 =====
            DrawTeamMembers(g, statsX + 100, headerY - 2);

            // ===== 边框 =====
            using (var borderPen = new Pen(MatchInfo.ResultColor, 1))
            {
                g.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);
            }
        }
        #endregion

        #region 绘制队伍头像及点击事件

        private void DrawTeamMembers(Graphics g, int startX, int baseY)
        {
            // 红队（第一行）
            for (int i = 0; i < Math.Min(5, MatchInfo.RedTeam.Count); i++)
            {
                int x = startX + i * (TeamIconSize + TeamIconSpacing);
                DrawPlayerIcon(g, MatchInfo.RedTeam[i], x, baseY - 4, TeamIconSize);
            }

            // 蓝队（第二行）
            int blueY = baseY + TeamIconSize + TeamRowSpacing - 6;
            for (int i = 0; i < Math.Min(5, MatchInfo.BlueTeam.Count); i++)
            {
                int x = startX + i * (TeamIconSize + TeamIconSpacing);
                DrawPlayerIcon(g, MatchInfo.BlueTeam[i], x, blueY, TeamIconSize);
            }
        }

        private void DrawPlayerIcon(Graphics g, PlayerInfo player, int x, int y, int size)
        {
            if (player?.Avatar == null) return;

            // 绘制头像
            g.DrawImage(player.Avatar, x, y, size, size);

            // 高亮当前玩家
            if (player.IsSelf)
            {
                using (var pen = new Pen(Color.Lime, 3)) // 或 Color.Lime 等醒目色
                {
                    g.DrawRectangle(pen, x, y, size, size);
                }
            }
        }

        private IEnumerable<Tuple<Rectangle, PlayerInfo>> GetPlayerIconRegions()
        {
            if (MatchInfo == null) yield break;

            int statsX = Width - Padding - 250;
            int teamStartX = statsX + 100;
            int baseY = (Padding + 4) - 2 - 2;

            // 红队区域
            for (int i = 0; i < MatchInfo.RedTeam.Count; i++)
            {
                yield return Tuple.Create(
                    new Rectangle(
                        teamStartX + i * (TeamIconSize + TeamIconSpacing),
                        baseY,
                        TeamIconSize,
                        TeamIconSize),
                    MatchInfo.RedTeam[i]);
            }

            // 蓝队区域
            int blueY = baseY + TeamIconSize + TeamRowSpacing - 4;
            for (int i = 0; i < MatchInfo.BlueTeam.Count; i++)
            {
                yield return Tuple.Create(
                    new Rectangle(
                        teamStartX + i * (TeamIconSize + TeamIconSpacing),
                        blueY,
                        TeamIconSize,
                        TeamIconSize),
                    MatchInfo.BlueTeam[i]);
            }
        }

        // 事件定义（携带 FullName 参数）
        public event Action<string> PlayerIconClicked;

        // 鼠标点击处理（保持不变）
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (MatchInfo == null) return;

            foreach (var tuple in GetPlayerIconRegions())
            {
                if (tuple.Item1.Contains(e.Location) && tuple.Item2 != null)
                {
                    try
                    {
                        // 调用事件，通知主窗口（如果有人订阅）
                        PlayerIconClicked?.Invoke(tuple.Item2.FullName);

                        Clipboard.SetText(tuple.Item2.FullName);
                        new ToolTip().Show($"已复制: {tuple.Item2.FullName}", this, e.Location, 1500);
                    }
                    catch { /* 错误处理 */ }
                    return;
                }
            }
        }
        #endregion

        #region 增加鼠标悬停提示

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (MatchInfo == null) return;

            string tooltipText = null;

            // 英雄头像提示
            var heroRect = new Rectangle(Padding + 60, Padding, IconSize, IconSize);
            if (heroRect.Contains(e.Location))
            {
                tooltipText = MatchInfo.ChampionName; // 你需要给 MatchInfo 添加 HeroName 属性
            }

            // 召唤师技能提示
            int spellY = Padding + 4 - 6;
            for (int i = 0; i < 2; i++)
            {
                var rect = new Rectangle(
                    SpellStartX + i * (SpellSize + SpellSpacing),
                    spellY,
                    SpellSize,
                    SpellSize);

                if (rect.Contains(e.Location))
                {
                    if (MatchInfo.SpellNames != null && MatchInfo.SpellDescriptions != null &&
                        i < MatchInfo.SpellNames.Length && i < MatchInfo.SpellDescriptions.Length)
                    {
                        tooltipText = $"{MatchInfo.SpellNames[i]}\n{StripHtmlTags(MatchInfo.SpellDescriptions[i])}";
                    }
                    break;
                }
            }


            // 装备图片提示
            int itemY = Padding + 4 + ItemsYOffset;
            for (int i = 0; i < 6; i++)
            {
                var rect = new Rectangle(
                    ItemsStartX + i * (ItemSize + ItemSpacing),
                    itemY,
                    ItemSize,
                    ItemSize);

                if (rect.Contains(e.Location))
                {
                    var item = MatchInfo.Items?[i];
                    if (item != null)
                    {
                        tooltipText = $"{item.Name}\n{StripHtmlTags(item.Description)}";
                    }
                    break;
                }
            }

            // 符文技能提示
            int runeY = Padding + 4 + ItemsYOffset - (ItemSize + 4);
            for (int i = 0; i < 6; i++)
            {
                int x = emptyItemX + KdaX + i * (ItemSize + ItemSpacing);
                var rect = new Rectangle(x, runeY, ItemSize, ItemSize);

                if (rect.Contains(e.Location))
                {
                    RuneInfo rune = null;
                    if (i < 4 && MatchInfo.PrimaryRunes != null && i < MatchInfo.PrimaryRunes.Length)
                    {
                        rune = MatchInfo.PrimaryRunes[i];
                    }
                    else if (i >= 4 && MatchInfo.SecondaryRunes != null && (i - 4) < MatchInfo.SecondaryRunes.Length)
                    {
                        rune = MatchInfo.SecondaryRunes[i - 4];
                    }

                    if (rune != null)
                    {
                        tooltipText = $"{rune.Name}\n{StripHtmlTags(rune.Description)}";
                    }

                    break;
                }
            }

            // 玩家头像提示
            foreach (var (rect, player) in GetPlayerIconRegions())
            {
                if (rect.Contains(e.Location) && player != null)
                {
                    tooltipText = player.FullName;
                    break;
                }
            }

            // 设置 Tooltip
            if (tooltipText != null && tooltipText != _lastTooltip)
            {
                _tooltip.SetToolTip(this, tooltipText);
                _lastTooltip = tooltipText;
            }
            else if (tooltipText == null)
            {
                _tooltip.SetToolTip(this, null);
                _lastTooltip = null;
            }
        }

        //过滤描述中的html标签
        public static string StripHtmlTags(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            input = input.Replace("<br>", "\n").Replace("<br/>", "\n");
            return System.Text.RegularExpressions.Regex.Replace(input, "<.*?>", "").Trim();
        }
        #endregion

    }
}