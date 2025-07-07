﻿using System.Diagnostics;
using System.Xml.Linq;

namespace League.uitls
{
    public partial class PlayerCardControl : UserControl
    {
        public bool IsLoading { get; private set; }
        public PlayerCardControl()
        {
            InitializeComponent();
        }

        public ListView ListViewControl
        {
            get { return listViewGames; }
        }

        public void SetAvatarOnly(Image avatar)
        {
            if (avatar != null && this.picHero != null)
            {
                this.picHero.Image = (Image)avatar.Clone();
            }
        }

        public void SetPlayerInfo(string playerName, string soloRank, string flexRank, Image heroImage, string isPublic, List<ListViewItem> recentGames, Color nameColor)
        {
            //lblPlayerName 是一个LinkLabel控件
            lblPlayerName.Text = playerName;

            // 设置同组队玩家颜色
            lblPlayerName.LinkColor = nameColor;
            lblPlayerName.VisitedLinkColor = nameColor;
            lblPlayerName.ActiveLinkColor = nameColor;

            // 设置加粗字体
            // 最好先保存原来的字体信息
            var oldFont = lblPlayerName.Font;

            // 重新创建粗体字体
            lblPlayerName.Font = new Font(
                oldFont.FontFamily,
                oldFont.Size,
                FontStyle.Bold
            );

            lblPlayerName.BorderStyle = BorderStyle.FixedSingle;

            lblSoloRank.Text = $"{soloRank}";
            lblFlexRank.Text = $"{flexRank}";
            lblPrivacyStatus.Text = $"{isPublic}";
            picHero.Image = heroImage;

            IsLoading = playerName.Contains("加载中") || soloRank.Contains("加载中");

            listViewGames.BeginUpdate();
            listViewGames.Items.Clear();

            if (recentGames != null)
            {
                // 使用克隆的 ListViewItem 防止重复引用
                foreach (var item in recentGames)
                {
                    listViewGames.Items.Add((ListViewItem)item.Clone());
                }
            }

            listViewGames.EndUpdate();
            listViewGames.Refresh();

            //Debug.WriteLine($"当前 listViewGames 中共有 {listViewGames.Items.Count} 个项");
        }
    }
}
