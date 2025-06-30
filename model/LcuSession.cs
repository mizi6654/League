using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace League.model
{
    public class LcuSession
    {
        #region 字段和属性
        private HttpClient _client;
        private string _port;
        private string _token;

        private readonly Queue<long> _responseTimeHistory = new Queue<long>();
        private const int MaxHistorySize = 5;

        // 调整并发数为 6（根据服务端承载能力调整）
        private readonly SemaphoreSlim _concurrencySemaphore = new SemaphoreSlim(6);

        public HttpClient Client => _client;    //公开 HttpClient，供外部访问

        public List<Champion> Champions = new List<Champion>(); //存储英雄名称
        public List<Item> Items = new List<Item>(); //存储装备名称
        public List<SummonerSpell> SummonerSpells = new List<SummonerSpell>();  //存储召唤师名称
        public List<RuneInfo> Runes = new List<RuneInfo>(); //存储符文技能名称
        public List<ProfileIcon> ProfileIcons = new List<ProfileIcon>();   //存储玩家头像信息
        #endregion


        #region 接口连接处理
        /// <summary>
        /// 初始化 LCU 客户端连接，获取进程中的端口号和授权 Token，并构建 HttpClient 实例。
        /// </summary>
        /// <returns>初始化成功返回 true，失败返回 false。</returns>
        public async Task<bool> InitializeAsync()
        {
            // 获取 LeagueClientUx 进程
            var process = Process.GetProcessesByName("LeagueClientUx").FirstOrDefault();
            if (process == null)
            {
                Debug.WriteLine("未找到 LeagueClientUx 进程");
                return false;
            }

            // 获取命令行参数
            string cmdLine = GetCommandLine(process);
            _port = ExtractArgument(cmdLine, "--app-port=");
            _token = ExtractArgument(cmdLine, "--remoting-auth-token=");

            // 清理隐藏字符
            _port = _port?.Trim().Trim('"', '\'', ' ', '\r', '\n');
            _token = _token?.Trim().Trim('"', '\'', ' ', '\r', '\n');

            if (string.IsNullOrEmpty(_port) || !int.TryParse(_port, out int port) || port < 10000 || port > 65535)
            {
                Debug.WriteLine($"无效的端口号：{_port}");
                return false;
            }

            if (string.IsNullOrEmpty(_token))
            {
                Debug.WriteLine("未能提取Token");
                return false;
            }

            // 调试输出关键信息
            Debug.WriteLine($"Port: {port}, Token: {_token}");
            Debug.WriteLine($"Authorization: Basic {Convert.ToBase64String(Encoding.ASCII.GetBytes($"riot:{_token}"))}");

            // 配置 HttpClientHandler 和代理
            // 使用 SocketsHttpHandler 替代 HttpClientHandler
            var handler = new SocketsHttpHandler
            {
                // 连接池配置
                UseProxy = false,                    // 禁用代理（除非必须）

                // 调整TCP参数
                PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                MaxConnectionsPerServer = 6,

                // 启用HTTP/2 (需服务端支持)
                EnableMultipleHttp2Connections = true,
                ConnectTimeout = TimeSpan.FromSeconds(1),

                // 关键管道化配置
                UseCookies = false,
                AllowAutoRedirect = false,

                // SSL/TLS 配置（替代HttpClientHandler的ServerCertificateCustomValidationCallback）
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (sender, cert, chain, errors) => true
                }
            };

            // 创建 HttpClient 实例
            _client?.Dispose();  // 确保旧的 HttpClient 被释放
            _client = new HttpClient(handler)
            {
                // 强制使用 HTTP/1.1 长连接（LCU 实测不支持 HTTP/2）
                DefaultRequestVersion = HttpVersion.Version11,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                //Timeout = TimeSpan.FromSeconds(6)
                Timeout = Timeout.InfiniteTimeSpan // 控制超时用 cts
            };

            //设置 Authorization Header
            _client.DefaultRequestHeaders.Clear();
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes($"riot:{_token}"))
            );
            //模拟客户端的请求头15.9.677.0562
            _client.DefaultRequestHeaders.Add("User-Agent", "LeagueOfLegendsClient/15.9.677.0562 (CEF 91)");
            _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true
            };
            _client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            _client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
            _client.DefaultRequestHeaders.ExpectContinue = false;

            _client.BaseAddress = new Uri($"https://127.0.0.1:{port}/");

            try
            {
                // 测试连接获取当前用户
                var response = await _client.GetAsync("lol-summoner/v1/current-summoner",
                    HttpCompletionOption.ResponseHeadersRead);

                //var response = await _client.GetAsync($"lol-match-history/v1/products/lol/{puuid}/matches?begIndex=0&endIndex=1",
                //    HttpCompletionOption.ResponseHeadersRead);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                /* 忽略错误 */
                Debug.WriteLine($"心跳异常：{ex.ToString()}");
            }
            // 启动心跳保持
            //_ = Task.Run(KeepAliveAsync);

            return false;
        }

        private string GetCommandLine(Process process)
        {
            using var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {process.Id}");
            foreach (ManagementObject obj in searcher.Get())
                return obj["CommandLine"]?.ToString() ?? "";
            return "";
        }

        private string ExtractArgument(string cmdLine, string key)
        {
            var match = Regex.Match(cmdLine, key + "([^ ]+)");
            return match.Success ? match.Groups[1].Value : null;
        }

        #endregion

        #region 数据查询
        /// <summary>
        /// 通过玩家召唤师名称获取其 PUUID（游戏唯一身份标识）。
        /// 通过本地客户端（LCU）API 获取。
        /// </summary>
        /// <param name="summonerName">召唤师名称</param>
        /// <returns>召唤师的 PUUID</returns>
        // 修改后 → 返回完整数据对象
        public async Task<JObject> GetSummonerByNameAsync(string summonerName)
        {
            try
            {
                var response = await _client.GetAsync($"/lol-summoner/v1/summoners?name={Uri.EscapeDataString(summonerName)}");
                if (!response.IsSuccessStatusCode) return null;

                var content = await response.Content.ReadAsStringAsync();
                return JObject.Parse(content); // 返回完整 JObject
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"查询异常：{ex}");
                return null;
            }
        }

        public async Task<JObject> GetGameNameBySummonerId(string summonerId)
        {
            try
            {
                // 注意使用路径参数而非查询参数
                var response = await _client.GetAsync($"/lol-summoner/v1/summoners/{summonerId}");

                if (!response.IsSuccessStatusCode) return null;

                var content = await response.Content.ReadAsStringAsync();
                return JObject.Parse(content);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ID查询异常：{ex}");
                return null;
            }
        }

        /// <summary>
        /// 自动获取当前登录用户信息
        /// </summary>
        public async Task<JObject> GetCurrentSummoner()
        {
            try
            {
                var response = await _client.GetAsync("/lol-summoner/v1/current-summoner");
                if (!response.IsSuccessStatusCode) return null;
                    var content = await response.Content.ReadAsStringAsync();

                // 调试输出
                //Debug.WriteLine("段位API返回数据：");
                //Debug.WriteLine(content);

                return JObject.Parse(content);
            }
            catch (Exception ex)
            {
                if (ex is TaskCanceledException)
                {
                    // 请求被取消或超时，不输出警告（可以选择静默处理）
                    return new JObject(); // 返回空对象
                }
                Debug.WriteLine($"获取段位失败: {ex}");
                return new JObject();
            }
        }


        /// <summary>
        /// 带分页的请求
        /// </summary>
        /// <param name="puuid"></param>
        /// <param name="begIndex"></param>
        /// <param name="endIndex"></param>
        /// <param name="isPreheat"></param>
        /// <returns></returns>
        public async Task<JArray> FetchMatchesWithRetry(string puuid,int begIndex,int endIndex, bool isPreheat = false)
        {
            int maxRetries = isPreheat ? 1 : 3;
            int attempt = 0;

            while (attempt < maxRetries)
            {
                attempt++;
                CancellationTokenSource cts = null;

                Debug.WriteLine($"[{puuid}] 第{attempt}次尝试，等待信号量...");
                await _concurrencySemaphore.WaitAsync();
                Debug.WriteLine($"[{puuid}] 获取到信号量，开始请求");

                try
                {
                    int timeoutSeconds = attempt == 1 ? 2 : 4;

                    cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

                    // 使用分页参数请求缓存接口
                    string path = $"lol-match-history/v1/products/lol/{puuid}/matches?begIndex={begIndex}&endIndex={endIndex}";

                    var watch = Stopwatch.StartNew();
                    var response = await _client.GetAsync(path, cts.Token);
                    watch.Stop();

                    if (response.IsSuccessStatusCode)
                    {
                        string content = await response.Content.ReadAsStringAsync();
                        if (!isPreheat)
                        {
                            UpdateResponseTimeHistory(watch.ElapsedMilliseconds);
                            Debug.WriteLine($"[正常请求] （默认返回{endIndex}场）耗时: {watch.ElapsedMilliseconds}ms");
                        }

                        var json = JObject.Parse(content);
                        var allGames = json["games"]?["games"] as JArray;

                        return allGames; // 默认返回全部
                    }
                    else
                    {
                        Debug.WriteLine($"[响应失败] {response.StatusCode}，已使用缓存接口（忽略分页）");
                    }
                }
                catch (TaskCanceledException)
                {
                    Debug.WriteLine($"第{attempt}次请求超时（使用缓存接口）");
                    if (isPreheat) break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"请求异常（使用缓存接口）: {ex.Message}");
                }
                finally
                {
                    _concurrencySemaphore.Release();
                    cts?.Dispose();
                }
            }

            return null;
        }

        /// <summary>
        /// 使用缓存接口快速拉取最近 20~50 场战绩（不支持分页）
        /// </summary>
        //public async Task<JArray> FetchLatestMatches(string puuid, bool isPreheat = false)
        //{
        //    int maxRetries = isPreheat ? 1 : 3;
        //    int attempt = 0;

        //    while (attempt < maxRetries)
        //    {
        //        attempt++;
        //        CancellationTokenSource cts = null;

        //        Debug.WriteLine($"[{puuid}] [Fast] 第{attempt}次尝试，等待信号量...");
        //        await _concurrencySemaphore.WaitAsync();
        //        Debug.WriteLine($"[{puuid}] [Fast] 获取到信号量，开始请求");

        //        try
        //        {
        //            int timeoutSeconds = attempt == 1 ? 2 : 4;

        //            cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

        //            // 使用缓存接口（无分页参数）
        //            string path = $"lol-match-history/v1/products/lol/{puuid}/matches";

        //            var watch = Stopwatch.StartNew();
        //            var response = await _client.GetAsync(path, cts.Token);
        //            watch.Stop();

        //            if (response.IsSuccessStatusCode)
        //            {
        //                string content = await response.Content.ReadAsStringAsync();
        //                if (!isPreheat)
        //                {
        //                    UpdateResponseTimeHistory(watch.ElapsedMilliseconds);
        //                    Debug.WriteLine($"[Fast请求成功] 默认返回 20~50 场，耗时: {watch.ElapsedMilliseconds}ms");
        //                }

        //                var json = JObject.Parse(content);
        //                return json["games"]?["games"] as JArray;
        //            }
        //            else
        //            {
        //                Debug.WriteLine($"[Fast请求失败] {response.StatusCode}，接口为缓存接口");
        //            }
        //        }
        //        catch (TaskCanceledException)
        //        {
        //            Debug.WriteLine($"[Fast请求超时] 第{attempt}次尝试（缓存接口）");
        //            if (isPreheat) break;
        //        }
        //        catch (Exception ex)
        //        {
        //            Debug.WriteLine($"[Fast请求异常] {ex.Message}");
        //        }
        //        finally
        //        {
        //            _concurrencySemaphore.Release();
        //            cts?.Dispose();
        //        }
        //    }

        //    return null;
        //}

        /// <summary>
        /// 使用缓存接口快速拉取最近 20~50 场战绩（不支持分页）
        ///— 一次请求，失败就返回 null，不重试
        /// </summary>
        public async Task<JArray> FetchLatestMatches(string puuid, bool isPreheat = false)
        {
            int[] retryDelays = { 10, 15 };
            for (int i = 0; i < retryDelays.Length; i++)
            {
                var timeout = TimeSpan.FromSeconds(retryDelays[i]);
                using (var cts = new CancellationTokenSource(timeout))
                {
                    try
                    {
                        using (var response = await _client.GetAsync(
                            $"lol-match-history/v1/products/lol/{puuid}/matches",
                            HttpCompletionOption.ResponseHeadersRead,
                            cts.Token).ConfigureAwait(false))
                        {
                            response.EnsureSuccessStatusCode(); // 若非 2xx 会抛异常

                            var json = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                            return json["games"]?["games"] as JArray;
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        Debug.WriteLine($"[Fetch超时] {puuid} 超过 {retryDelays[i]} 秒未响应");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[第{i + 1}次尝试失败] 异常: {ex.Message}");
                    }
                }

                if (i < retryDelays.Length - 1)
                {
                    Debug.WriteLine($"[等待重试] 即将进行第{i + 2}次尝试");
                    await Task.Delay(1000); // 可改为指数退避
                }
            }

            return null;
        }


        private void UpdateResponseTimeHistory(long elapsedMs)
        {
            _responseTimeHistory.Enqueue(elapsedMs);
            if (_responseTimeHistory.Count > MaxHistorySize)
            {
                _responseTimeHistory.Dequeue();
            }
        }


        /// <summary>
        /// 根据游戏ID获取每一局的对战数据，如出装、玩家列表
        /// </summary>
        /// <param name="gameId"></param>
        /// <returns></returns>
        public async Task<JObject> GetFullMatchByGameIdAsync(long gameId)
        {
            try
            {
                var response = await _client.GetAsync($"/lol-match-history/v1/games/{gameId}");

                if (!response.IsSuccessStatusCode) return null;

                var content = await response.Content.ReadAsStringAsync();
                return JObject.Parse(content);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取完整对战信息失败：{ex.Message}");
                return null;
            }
        }

        #endregion

        #region 排位信息查询
        /// <summary>
        /// 根据PUUID获取当前赛季的排位段位信息（单排/灵活组排）
        /// </summary>
        public async Task<JObject> GetCurrentRankedStatsAsync(string puuid)
        {
            try
            {
                var response = await _client.GetAsync($"/lol-ranked/v1/ranked-stats/{puuid}");
                var content = await response.Content.ReadAsStringAsync();

                // 调试输出
                //Debug.WriteLine($"段位API返回数据：{content}");

                return JObject.Parse(content);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取段位失败: {ex}");
                return new JObject();
            }
        }
        #endregion

        #region 监控玩家状态及游戏大厅
        // 获取当前游戏阶段
        public async Task<string> GetGameflowPhase()
        {
            try
            {
                var response = await _client.GetAsync("/lol-gameflow/v1/gameflow-phase");
                if (!response.IsSuccessStatusCode) return null;

                // 原始响应内容可能为 "\"ChampSelect\""（包含引号）
                var rawContent = await response.Content.ReadAsStringAsync();

                // 移除所有引号和空格
                return rawContent.Trim().Trim('"');
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取游戏阶段失败: {ex}");
                return null;
            }
        }

        public async Task<JObject> GetTryShowLobbyGroupInfo()
        {
            try
            {
                var response = await _client.GetAsync("/lol-lobby/v2/lobby");
                if (!response.IsSuccessStatusCode) return null;

                var content = await response.Content.ReadAsStringAsync();
                Debug.WriteLine($"lobby阶段原始数据：{content}");

                return JObject.Parse(content);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"lobby阶段数据失败: {ex}");
                return new JObject();
            }
        }


        // 获取选人阶段会话详情
        public async Task<JObject> GetChampSelectSession()
        {
            try
            {
                var response = await _client.GetAsync("/lol-champ-select/v1/session");
                if (!response.IsSuccessStatusCode) return null;

                var content = await response.Content.ReadAsStringAsync();
                //Debug.WriteLine($"选人阶段原始数据：{content}");

                return JObject.Parse(content);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取选人阶段数据失败: {ex}");
                return new JObject();
            }
        }

        // 获取游戏会话信息（含游戏模式）
        public async Task<JObject> GetGameSession()
        {
            try
            {
                var response = await _client.GetAsync("/lol-gameflow/v1/session");
                if (!response.IsSuccessStatusCode) return null;

                var content = await response.Content.ReadAsStringAsync();
                //Debug.WriteLine($"游戏会话信息数据：{content}");

                return JObject.Parse(content);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取游戏会话失败: {ex}");
                return new JObject();
            }
        }
        #endregion

        #region 加载所有英雄

        /// <summary>
        /// 初始化所有英雄数据
        /// </summary>
        /// <returns></returns>
        public async Task LoadChampionsAsync()
        {
            string json = await _client.GetStringAsync("/lol-game-data/assets/v1/champion-summary.json");
            var list = JsonConvert.DeserializeObject<List<dynamic>>(json);
            Champions.Clear();
            foreach (var c in list)
            {
                Champions.Add(new Champion
                {
                    Id = c.id,
                    Alias = c.alias,
                    Name = c.name,
                    Description = c.description
                });
            }
        }

        /// <summary>
        /// 根据Id查找英雄映射
        /// </summary>
        /// <param name="champId"></param>
        /// <returns></returns>
        public Champion GetChampionById(int champId)
        {
            return Champions.FirstOrDefault(c => c.Id == champId);
        }

        /// <summary>
        /// 根据英雄Id获取完整信息（图片 + 名称 + 描述）
        /// </summary>
        /// <param name="champId"></param>
        /// <returns></returns>
        public async Task<(Image image, string name, string description)> GetChampionInfoAsync(int champId)
        {
            var champion = GetChampionById(champId);
            if (champion != null)
            {
                var image = await GetChampionIconAsync(champId);
                return (image, champion.Name, champion.Description);
            }

            return (null, "未知英雄", "未找到该英雄的描述");
        }

        /// <summary>
        /// 根据ID获取图片并缓存本地
        /// </summary>
        /// <param name="champId"></param>
        /// <param name="champion"></param>
        /// <returns></returns>
        public async Task<Image> GetChampionIconAsync(int champId)
        {
            // 确保缓存目录存在
            string cacheDir = Path.Combine(Application.StartupPath, "Assets", "champion");
            Directory.CreateDirectory(cacheDir); // 如果目录不存在则创建

            // 获取英雄信息用于构建文件名
            var champion = GetChampionById(champId);
            if (champion == null)
            {
                Debug.WriteLine($"未找到ID为 {champId} 的英雄");
                return null;
            }

            // 构建本地缓存路径
            string safeName = champion.Name.Replace(" ", "").Replace("'", "");
            string cachePath = Path.Combine(cacheDir, $"{safeName}.png");

            // 1. 首先尝试从本地缓存加载
            if (File.Exists(cachePath))
            {
                try
                {
                    using (var stream = File.OpenRead(cachePath))
                    {
                        var image = Image.FromStream(stream);
                        //Debug.WriteLine($"从本地缓存加载英雄图标: {champion.Name}");
                        return (Image)image.Clone(); // 返回克隆对象以避免资源释放问题
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"本地缓存加载失败: {ex.Message}");
                    // 如果缓存文件损坏，继续从网络获取
                }
            }

            // 2. 本地没有则从LCU API获取
            string path = $"/lol-game-data/assets/v1/champion-icons/{champId}.png";
            try
            {
                var response = await _client.GetAsync(path);
                if (!response.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"图片获取失败，状态码: {response.StatusCode}");
                    return null;
                }

                using (var stream = await response.Content.ReadAsStreamAsync())
                {
                    var image = Image.FromStream(stream);
                    Debug.WriteLine($"成功从API读取图片: {champion.Name}");

                    // 3. 将获取的图片保存到本地缓存
                    try
                    {
                        await SaveImageToCacheAsync(image, cachePath);
                        Debug.WriteLine($"已缓存英雄图标: {champion.Name}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"缓存保存失败: {ex.Message}");
                        // 即使缓存失败也返回图片
                    }

                    return image;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"图片加载异常: {ex}");
                return null;
            }
        }

        /// <summary>
        /// 将图片保存到本地缓存
        /// </summary>
        /// <param name="image"></param>
        /// <param name="cachePath"></param>
        /// <returns></returns>
        private async Task SaveImageToCacheAsync(Image image, string cachePath)
        {
            // 确保目录存在
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath));

            // 使用带随机后缀的临时文件，避免多线程冲突
            string tempPath = $"{cachePath}.{Guid.NewGuid().ToString("N").Substring(0, 8)}.tmp";

            try
            {
                // 尝试多次写入（应对可能的临时冲突）
                int retryCount = 0;
                const int maxRetries = 3;

                while (retryCount < maxRetries)
                {
                    try
                    {
                        // 使用FileShare.None确保独占访问
                        using (var fs = new FileStream(
                            tempPath,
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.None,
                            bufferSize: 4096,
                            useAsync: true))
                        {
                            await Task.Run(() =>
                            {
                                image.Save(fs, System.Drawing.Imaging.ImageFormat.Png);
                                fs.Flush(); // 确保所有数据写入磁盘
                            });
                        }
                        break; // 成功则退出重试循环
                    }
                    catch (IOException) when (retryCount < maxRetries - 1)
                    {
                        retryCount++;
                        await Task.Delay(100 * retryCount); // 指数退避
                        continue;
                    }
                }

                // 原子性替换文件
                if (File.Exists(cachePath))
                {
                    File.Delete(cachePath);
                }
                File.Move(tempPath, cachePath);
            }
            finally
            {
                // 确保临时文件被清理
                if (File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (IOException)
                    {
                        // 如果删除失败，可以记录日志但不影响主流程
                        Debug.WriteLine($"无法删除临时文件: {tempPath}");
                    }
                }
            }
        }
        #endregion

        #region 加载所有装备

        /// <summary>
        /// 初始化所有装备信息
        /// </summary>
        /// <returns></returns>
        public async Task LoadItemsAsync()
        {
            string json = await _client.GetStringAsync("/lol-game-data/assets/v1/items.json");
            var list = JsonConvert.DeserializeObject<List<dynamic>>(json);
            Items.Clear();

            foreach (var i in list)
            {
                int id = i.id;
                string name = i.name;
                string description = i.description;
                string iconPath = (string)i.iconPath;

                if (string.IsNullOrEmpty(iconPath)) continue;

                Items.Add(new Item
                {
                    Id = id,
                    Name = name,
                    Description = description,
                    IconFileName = iconPath // 注意：这里是完整路径
                });
            }
        }

        /// <summary>
        /// 根据装备ID查找装备映射
        /// </summary>
        /// <param name="itemId"></param>
        /// <returns></returns>
        public Item GetItemById(int itemId)
        {
            return Items.FirstOrDefault(i => i.Id == itemId);
        }

        /// <summary>
        /// 根据装备ID查找（图片、名称、描述）
        /// </summary>
        /// <param name="itemId"></param>
        /// <returns></returns>
        public async Task<(Image image, string name, string description)> GetItemInfoAsync(int itemId)
        {
            var item = GetItemById(itemId);
            if (item != null)
            {
                var image = await GetItemIconAsync(item.IconFileName);
                return (image, item.Name, item.Description);
            }

            return (null, "未知装备", "未找到该装备的描述");
        }

        /// <summary>
        /// 根据装备路径获取图片并缓存本地
        /// </summary>
        /// <param name="iconPath"></param>
        /// <returns></returns>
        public async Task<Image> GetItemIconAsync(string iconPath)
        {
            string cacheDir = Path.Combine(Application.StartupPath, "Assets", "item");
            Directory.CreateDirectory(cacheDir);

            string fileName = Path.GetFileName(iconPath);
            if (string.IsNullOrEmpty(fileName))
            {
                Debug.WriteLine($"无效的装备图标路径: {iconPath}");
                return null;
            }

            string cachePath = Path.Combine(cacheDir, fileName);

            if (File.Exists(cachePath))
            {
                try
                {
                    using (var stream = File.OpenRead(cachePath))
                    {
                        var image = Image.FromStream(stream);
                        return (Image)image.Clone(); // clone避免流关闭后图片失效
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"本地缓存加载失败: {ex.Message}");
                }
            }

            try
            {
                var response = await _client.GetAsync(iconPath).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"装备图标获取失败: {iconPath} 状态码: {response.StatusCode}");
                    return null;
                }

                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                {
                    Image image;
                    try
                    {
                        image = Image.FromStream(stream);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"图片流转换失败: {ex.Message}");
                        return null;
                    }

                    try
                    {
                        await SaveItemImageToCacheAsync(image, cachePath).ConfigureAwait(false);
                        Debug.WriteLine($"已缓存装备图标: {fileName}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"缓存图片失败: {ex.Message}");
                    }

                    return image;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"装备图标加载异常: {ex}");
                return null;
            }
        }

        //public async Task<Image> GetItemIconAsync(string iconPath)
        //{
        //    // 确保缓存目录存在
        //    string cacheDir = Path.Combine(Application.StartupPath, "Assets", "item");
        //    Directory.CreateDirectory(cacheDir);

        //    // 从路径中提取文件名
        //    string fileName = Path.GetFileName(iconPath);
        //    if (string.IsNullOrEmpty(fileName))
        //    {
        //        Debug.WriteLine($"无效的装备图标路径: {iconPath}");
        //        return null;
        //    }

        //    // 构建本地缓存路径
        //    string cachePath = Path.Combine(cacheDir, fileName);

        //    // 1. 首先尝试从本地缓存加载
        //    if (File.Exists(cachePath))
        //    {
        //        try
        //        {
        //            using (var stream = File.OpenRead(cachePath))
        //            {
        //                var image = Image.FromStream(stream);
        //                //Debug.WriteLine($"从本地缓存加载装备图标: {fileName}");
        //                return (Image)image.Clone();
        //            }
        //        }
        //        catch (Exception ex)
        //        {
        //            Debug.WriteLine($"本地缓存加载失败: {ex.Message}");
        //            // 如果缓存文件损坏，继续从网络获取
        //        }
        //    }

        //    // 2. 本地没有则从LCU API获取
        //    try
        //    {
        //        var response = await _client.GetAsync(iconPath);
        //        if (!response.IsSuccessStatusCode)
        //        {
        //            Debug.WriteLine($"装备图标获取失败: {iconPath} 状态码: {response.StatusCode}");
        //            return null;
        //        }

        //        using (var stream = await response.Content.ReadAsStreamAsync())
        //        {
        //            var image = Image.FromStream(stream);
        //            Debug.WriteLine($"成功从API读取装备图标: {fileName}");

        //            // 3. 将获取的图片保存到本地缓存
        //            try
        //            {
        //                await SaveItemImageToCacheAsync(image, cachePath);
        //                Debug.WriteLine($"已缓存装备图标: {fileName}");
        //            }
        //            catch (Exception ex)
        //            {
        //                Debug.WriteLine($"装备图标缓存保存失败: {ex.Message}");
        //                // 即使缓存失败也返回图片
        //            }

        //            return image;
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        Debug.WriteLine($"装备图标加载异常: {ex}");
        //        return null;
        //    }
        //}

        private async Task SaveItemImageToCacheAsync(Image image, string cachePath)
        {
            // 确保目录存在
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath));

            // 使用唯一临时文件名
            string tempPath = $"{cachePath}.{Guid.NewGuid().ToString("N").Substring(0, 8)}.tmp";

            try
            {
                // 重试机制
                int retryCount = 0;
                const int maxRetries = 3;

                while (retryCount < maxRetries)
                {
                    try
                    {
                        using (var fs = new FileStream(
                            tempPath,
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.None,
                            bufferSize: 4096,
                            useAsync: true))
                        {
                            await Task.Run(() =>
                            {
                                image.Save(fs, System.Drawing.Imaging.ImageFormat.Png);
                                fs.Flush();
                            });
                        }
                        break;
                    }
                    catch (IOException) when (retryCount < maxRetries - 1)
                    {
                        retryCount++;
                        await Task.Delay(100 * retryCount);
                        continue;
                    }
                }

                // 原子性替换文件
                if (File.Exists(cachePath))
                {
                    File.Delete(cachePath);
                }
                File.Move(tempPath, cachePath);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); }
                    catch (IOException ex)
                    {
                        Debug.WriteLine($"无法删除临时文件: {ex.Message}");
                    }
                }
            }
        }
        #endregion

        #region 加载所有召唤师技能
        public async Task LoadSpellsAsync()
        {
            string json = await _client.GetStringAsync("/lol-game-data/assets/v1/summoner-spells.json");
            var list = JsonConvert.DeserializeObject<List<dynamic>>(json);
            SummonerSpells.Clear();

            foreach (var s in list)
            {
                long id = Convert.ToInt64(s.id); // 改为 long
                string name = s.name.ToString();
                string description = s.description?.ToString();
                string iconPath = s.iconPath.ToString();
                string iconFile = Path.GetFileName(iconPath);

                SummonerSpells.Add(new SummonerSpell
                {
                    Id = id,
                    Name = name,
                    Description = description,
                    IconPath = iconPath,
                    IconFileName = iconFile
                });
            }
        }

        //查找方法
        public SummonerSpell GetSpellById(int spellId)
        {
            return SummonerSpells.FirstOrDefault(s => s.Id == spellId);
        }

        //获取图片+描述方法
        public async Task<(Image image, string name, string description)> GetSpellInfoAsync(int spellId)
        {
            var spell = GetSpellById(spellId);
            if (spell != null)
            {
                var image = await GetSummonerSpellIconAsync(spell.IconPath);
                return (image, spell.Name, spell.Description);
            }

            return (null, "未知召唤师技能", "未找到该技能的描述");
        }

        public async Task<Image> GetSummonerSpellIconAsync(string iconPath)
        {
            // 统一路径格式处理
            if (!iconPath.StartsWith("/"))
                iconPath = "/" + iconPath;

            // 确保缓存目录存在
            string cacheDir = Path.Combine(Application.StartupPath, "Assets", "spell");
            Directory.CreateDirectory(cacheDir);

            // 从路径中提取文件名
            string fileName = Path.GetFileName(iconPath);
            if (string.IsNullOrEmpty(fileName))
            {
                Debug.WriteLine($"无效的召唤师技能图标路径: {iconPath}");
                return null;
            }

            // 构建本地缓存路径
            string cachePath = Path.Combine(cacheDir, fileName);

            // 1. 首先尝试从本地缓存加载
            if (File.Exists(cachePath))
            {
                try
                {
                    using (var stream = File.OpenRead(cachePath))
                    {
                        var image = Image.FromStream(stream);
                        //Debug.WriteLine($"从本地缓存加载召唤师技能图标: {fileName}");
                        return (Image)image.Clone();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"本地缓存加载失败: {ex.Message}");
                    // 如果缓存文件损坏，继续从网络获取
                }
            }

            // 2. 本地没有则从LCU API获取
            try
            {
                var response = await _client.GetAsync(iconPath);
                if (!response.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"召唤师技能图标获取失败: {iconPath} 状态码: {response.StatusCode}");
                    return null;
                }

                using (var stream = await response.Content.ReadAsStreamAsync())
                {
                    var image = Image.FromStream(stream);
                    Debug.WriteLine($"成功从API读取召唤师技能图标: {fileName}");

                    // 3. 将获取的图片保存到本地缓存
                    try
                    {
                        await SaveSpellImageToCacheAsync(image, cachePath);
                        Debug.WriteLine($"已缓存召唤师技能图标: {fileName}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"召唤师技能图标缓存保存失败: {ex.Message}");
                        // 即使缓存失败也返回图片
                    }

                    return image;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"召唤师技能图标加载异常: {ex}");
                return null;
            }
        }

        private async Task SaveSpellImageToCacheAsync(Image image, string cachePath)
        {
            // 确保目录存在
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath));

            // 使用唯一临时文件名
            string tempPath = $"{cachePath}.{Guid.NewGuid().ToString("N").Substring(0, 8)}.tmp";

            try
            {
                // 重试机制
                int retryCount = 0;
                const int maxRetries = 3;

                while (retryCount < maxRetries)
                {
                    try
                    {
                        using (var fs = new FileStream(
                            tempPath,
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.None,
                            bufferSize: 4096,
                            useAsync: true))
                        {
                            await Task.Run(() =>
                            {
                                image.Save(fs, System.Drawing.Imaging.ImageFormat.Png);
                                fs.Flush();
                            });
                        }
                        break;
                    }
                    catch (IOException) when (retryCount < maxRetries - 1)
                    {
                        retryCount++;
                        await Task.Delay(100 * retryCount);
                        continue;
                    }
                }

                // 原子性替换文件
                if (File.Exists(cachePath))
                {
                    File.Delete(cachePath);
                }
                File.Move(tempPath, cachePath);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); }
                    catch (IOException ex)
                    {
                        Debug.WriteLine($"无法删除临时文件: {ex.Message}");
                    }
                }
            }
        }
        #endregion

        #region 加载所有符文
        public async Task LoadRunesAsync()
        {
            var path = "/lol-game-data/assets/v1/perks.json";
            try
            {
                var response = await _client.GetAsync(path);
                if (!response.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"符文数据获取失败: {response.StatusCode}");
                    return;
                }

                string json = await response.Content.ReadAsStringAsync();
                Runes = JsonConvert.DeserializeObject<List<RuneInfo>>(json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"符文加载异常: {ex.Message}");
            }
        }

        //查找方法
        public RuneInfo GetRuneById(int runeId)
        {
            return Runes.FirstOrDefault(r => r.id == runeId);
        }

        //获取符文图片与描述
        public async Task<(Image image, string name, string description)> GetRuneInfoAsync(int runeId)
        {
            var rune = GetRuneById(runeId);
            if (rune != null)
            {
                //Debug.WriteLine($"找到符文: {runeId} => {rune.name}");
                var image = await GetRuneIconAsync(rune.iconPath);
                return (image, rune.name, rune.longDesc);
            }
            Debug.WriteLine($"未找到符文: {runeId}");
            return (null, "未知符文", "未找到该符文描述");
        }

        public async Task<Image> GetRuneIconAsync(string iconPath)
        {
            // 确保缓存目录存在
            string cacheDir = Path.Combine(Application.StartupPath, "Assets", "runes");
            Directory.CreateDirectory(cacheDir);

            // 从路径中提取文件名
            string fileName = Path.GetFileName(iconPath);
            if (string.IsNullOrEmpty(fileName))
            {
                Debug.WriteLine($"无效的符文图标路径: {iconPath}");
                return null;
            }

            // 构建本地缓存路径
            string cachePath = Path.Combine(cacheDir, fileName);

            // 1. 首先尝试从本地缓存加载
            if (File.Exists(cachePath))
            {
                try
                {
                    using (var stream = File.OpenRead(cachePath))
                    {
                        var image = Image.FromStream(stream);
                        //Debug.WriteLine($"从本地缓存加载符文图标: {fileName}");
                        return (Image)image.Clone();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"本地缓存加载失败: {ex.Message}");
                    // 如果缓存文件损坏，继续从网络获取
                }
            }

            // 2. 本地没有则从LCU API获取
            try
            {
                var response = await _client.GetAsync(iconPath);
                if (!response.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"符文图标获取失败: {iconPath} 状态码: {response.StatusCode}");
                    return null;
                }

                using (var stream = await response.Content.ReadAsStreamAsync())
                {
                    var image = Image.FromStream(stream);
                    Debug.WriteLine($"成功从API读取符文图标: {fileName}");

                    // 3. 将获取的图片保存到本地缓存
                    try
                    {
                        await SaveRuneImageToCacheAsync(image, cachePath);
                        Debug.WriteLine($"已缓存符文图标: {fileName}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"符文图标缓存保存失败: {ex.Message}");
                        // 即使缓存失败也返回图片
                    }

                    return image;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"符文图标加载异常: {ex}");
                return null;
            }
        }

        private async Task SaveRuneImageToCacheAsync(Image image, string cachePath)
        {
            // 确保目录存在
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath));

            // 使用唯一临时文件名
            string tempPath = $"{cachePath}.{Guid.NewGuid().ToString("N").Substring(0, 8)}.tmp";

            try
            {
                // 重试机制
                int retryCount = 0;
                const int maxRetries = 3;

                while (retryCount < maxRetries)
                {
                    try
                    {
                        using (var fs = new FileStream(
                            tempPath,
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.None,
                            bufferSize: 4096,
                            useAsync: true))
                        {
                            await Task.Run(() =>
                            {
                                image.Save(fs, System.Drawing.Imaging.ImageFormat.Png);
                                fs.Flush();
                            });
                        }
                        break;
                    }
                    catch (IOException) when (retryCount < maxRetries - 1)
                    {
                        retryCount++;
                        await Task.Delay(100 * retryCount);
                        continue;
                    }
                }

                // 原子性替换文件
                if (File.Exists(cachePath))
                {
                    File.Delete(cachePath);
                }
                File.Move(tempPath, cachePath);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); }
                    catch (IOException ex)
                    {
                        Debug.WriteLine($"无法删除临时文件: {ex.Message}");
                    }
                }
            }
        }
        #endregion
    }
}
