using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace League.model
{
    public class VersionInfo
    {
        public string version { get; set; }
        public string date { get; set; }
        public List<string> changelog { get; set; }
        public string updateUrl { get; set; }

        public static VersionInfo GetLocalVersion()
        {
            var versionFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "version.txt");

            if (!File.Exists(versionFile))
                return null;

            var version = File.ReadAllText(versionFile).Trim();

            return new VersionInfo
            {
                version = version
            };
        }

        public static async Task<VersionInfo> GetRemoteVersion()
        {
            try
            {
                HttpClient httpClient = new HttpClient();

                // raw.gitee.com 或其他真实地址
                var response = await httpClient.GetAsync("http://sz18x2eyh.hn-bkt.clouddn.com/version.json");
                if (!response.IsSuccessStatusCode)
                    return null;

                var content = await response.Content.ReadAsStringAsync();
                var remoteVersion = JsonConvert.DeserializeObject<VersionInfo>(content);
                return remoteVersion;
            }
            catch (TaskCanceledException ex)
            {
                Debug.WriteLine($"[检测更新] 请求超时: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取更新失败: {ex}");
                return null;
            }
        }
    }
}
