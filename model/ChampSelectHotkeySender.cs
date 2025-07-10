using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json.Linq;

namespace League.model
{
    public class ChampSelectHotkeySender
    {
        private readonly JArray _myTeam;
        private readonly Dictionary<long, PlayerMatchInfo> _cachedPlayerMatchInfos;
        private bool _messageSent = false;

        public ChampSelectHotkeySender(JArray myTeam, Dictionary<long, PlayerMatchInfo> cachedInfos)
        {
            _myTeam = myTeam;
            _cachedPlayerMatchInfos = cachedInfos;
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(Keys vKey);

        public void StartListening()
        {
            Task.Run(() =>
            {
                Debug.WriteLine("[HotKey] ChampSelect 等待用户按下 F9...");

                while (!_messageSent)
                {
                    bool f9Down = (GetAsyncKeyState(Keys.F9) & 0x8000) != 0;

                    if (f9Down)
                    {
                        Debug.WriteLine("[HotKey] 检测到 F9 被按下！");
                        SendMyTeamSummary();
                        _messageSent = true;
                    }

                    Thread.Sleep(100);
                }
            });
        }

        private void SendMyTeamSummary()
        {
            var sb = new StringBuilder();

            if (_myTeam == null || _myTeam.Count == 0)
            {
                sb.AppendLine("未检测到我方队伍信息。");
            }
            else
            {
                foreach (var p in _myTeam)
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

            string message = sb.ToString().Trim();

            if (string.IsNullOrEmpty(message))
            {
                Debug.WriteLine("[SendMyTeamSummary] 没有任何可发送的消息");
                return;
            }

            Debug.WriteLine("[SendMyTeamSummary] 开始通过 SendKeys 发送我方队伍数据");

            SendKeys.SendWait("{ENTER}");
            Thread.Sleep(100);
            SendKeys.SendWait(message.Replace(Environment.NewLine, "{ENTER}"));
            Thread.Sleep(100);
            SendKeys.SendWait("{ENTER}");
        }
    }
}
