using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json.Linq;
using System.Threading;
using System.Windows.Forms;

namespace League.model
{
    public class InGameSummarySender
    {
        private JArray _cachedMyTeam;
        private JArray _cachedEnemyTeam;
        private Dictionary<long, PlayerMatchInfo> _cachedPlayerMatchInfos;

        public InGameSummarySender(
            JArray cachedMyTeam,
            JArray cachedEnemyTeam,
            Dictionary<long, PlayerMatchInfo> cachedPlayerMatchInfos)
        {
            _cachedMyTeam = cachedMyTeam;
            _cachedEnemyTeam = cachedEnemyTeam;
            _cachedPlayerMatchInfos = cachedPlayerMatchInfos;
        }

        public void StartListening()
        {
            Task.Run(() =>
            {
                Debug.WriteLine("[HotKey] 游戏内，等待用户先按 Enter...");

                bool enterPressed = false;
                var enterTime = DateTime.MinValue;

                while (true)
                {
                    if (!enterPressed)
                    {
                        if ((GetAsyncKeyState(Keys.Enter) & 0x8000) != 0)
                        {
                            enterPressed = true;
                            enterTime = DateTime.Now;
                            Debug.WriteLine("[HotKey] 检测到 Enter 被按下！");
                            Thread.Sleep(5000);
                        }
                    }
                    else
                    {
                        if ((GetAsyncKeyState(Keys.Tab) & 0x8000) != 0)
                        {
                            var delay = (DateTime.Now - enterTime).TotalMilliseconds;
                            Debug.WriteLine($"[HotKey] 检测到 Tab 被按下！（距Enter {delay}ms）");

                            SendInGameSummaryViaClipboard();
                            break;
                        }
                    }

                    Thread.Sleep(50);
                }
            });
        }

        /// <summary>
        /// 将战绩复制到剪贴板，并弹出提示
        /// </summary>
        private void SendInGameSummaryViaClipboard()
        {
            var sb = new StringBuilder();

            sb.AppendLine("【我方】");
            AppendTeamInfo(sb, _cachedMyTeam);

            sb.AppendLine("【敌方】");
            AppendTeamInfo(sb, _cachedEnemyTeam);

            string message = sb.ToString().Trim();

            if (string.IsNullOrEmpty(message))
            {
                Debug.WriteLine("[SendInGameSummaryViaClipboard] 没有任何可发送的消息");
                return;
            }

            Debug.WriteLine("[SendInGameSummaryViaClipboard] 已将10人信息复制到剪贴板");

            CopyTextToClipboard(message);

            Debug.WriteLine($"战绩信息如下：\r\n{message}");

            Debug.WriteLine( "已复制战绩信息到剪贴板！\n\n请切换到游戏聊天框：\n1. 按 Enter 打开聊天\n2. 按 Ctrl+V 粘贴\n3. 再按 Enter 发送");
        }

        private void CopyTextToClipboard(string text)
        {
            Thread thread = new Thread(() =>
            {
                try
                {
                    Clipboard.SetText(text);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[CopyTextToClipboard] 异常：" + ex.Message);
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }

        private void AppendTeamInfo(StringBuilder sb, JArray team)
        {
            if (team == null)
            {
                sb.AppendLine("暂无数据");
                return;
            }

            foreach (var p in team)
            {
                long sid = p["summonerId"]?.Value<long>() ?? 0;

                if (!_cachedPlayerMatchInfos.TryGetValue(sid, out var info))
                {
                    sb.AppendLine("未知玩家数据");
                    continue;
                }

                string name = info.Player.GameName ?? "未知玩家";
                string solo = info.Player.SoloRank ?? "未知";
                string flex = info.Player.FlexRank ?? "未知";

                var wins = info.WinHistory.Count(w => w);
                var total = info.WinHistory.Count;
                double winRate = total > 0 ? (wins * 100.0 / total) : 0;

                sb.AppendLine($"{name}: 单双排 {solo} | 灵活 {flex} | 近20场胜率: {winRate:F1}%");
            }
        }

        #region WinAPI

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(Keys vKey);

        #endregion
    }
}
