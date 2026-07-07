// ============================================================
//  Claude Quota Monitor  (Project #23)
//  Single-exe WinForms app that:
//    * stores multiple Claude accounts' OAuth tokens (DPAPI encrypted)
//    * polls https://api.anthropic.com/api/oauth/usage per account (>=180s)
//    * serves a mobile-friendly dashboard over LAN (HttpListener)
//    * shows a system-tray icon + a WebView2 desktop window
//  ALL user-facing Chinese text lives in dashboard.html (served utf-8),
//  so this .cs stays pure ASCII to avoid csc codepage mojibake.
// ============================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClaudeQuotaMonitor
{
    // ---- One monitored account ----
    public class Account
    {
        public string Id;
        public string Name;
        public string Email;        // which Claude account this token belongs to (plain, shown on card)
        public string Plan;         // subscription plan shown as a gray subtitle: "Max" / "Pro"
        public string EncToken;     // base64( DPAPI-protected UTF8 access token )
        public string EncRefresh;   // base64( DPAPI-protected UTF8 refresh token ), if OAuth login
        public long ExpiresAtMs;    // unix ms when the access token expires (0 = unknown / long-lived paste)
        public string LocalPath;    // if imported from a local Claude Code login, path to its .credentials.json (re-read each poll)

        // runtime-only (not persisted)
        [System.Xml.Serialization.XmlIgnore] public string LastRaw;      // raw JSON from usage API
        [System.Xml.Serialization.XmlIgnore] public string LastError;
        [System.Xml.Serialization.XmlIgnore] public long LastUpdatedMs;  // unix ms, 0 = never
        [System.Xml.Serialization.XmlIgnore] public int LastStatus;
        [System.Xml.Serialization.XmlIgnore] public long LastPollMs;     // when we last hit the API
    }

    static class Program
    {
        public const string VERSION = "v1.2.6";
        public const int PORT = 45900;
        public const string USAGE_URL = "https://api.anthropic.com/api/oauth/usage";
        public const string PROFILE_URL = "https://api.anthropic.com/api/oauth/profile";  // returns account.email/display_name
        public const int MIN_POLL_MS = 185000;   // >=180s per account to dodge the 429 bucket

        // ---- Claude Code's public OAuth client (PKCE). Lets the app do its own browser login,
        //      so users never touch the CLI or local credential files. ----
        // Claude Code's official public OAuth client (Pro/Max SUBSCRIPTION login). Exact constants
        // verified against multiple working implementations (login page = claude.ai; token backend =
        // platform.claude.com). Scope must NOT include org:create_api_key (that's the Console side and
        // makes claude.ai reject the request). This flow yields access_token + refresh_token.
        public const string OAUTH_CLIENT_ID = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
        public const string OAUTH_AUTH_URL  = "https://claude.ai/oauth/authorize";
        public const string OAUTH_TOKEN_URL = "https://platform.claude.com/v1/oauth/token";
        // Manual-code flow (gist-verified): with code=true, claude.ai shows a "CODE#STATE" string on the
        // platform.claude.com callback page after the user authorizes IN A REAL BROWSER (Arkose passes).
        // The user copies that code back into the app. Loopback + this client_id gets rejected.
        public const string OAUTH_REDIRECT  = "https://platform.claude.com/oauth/code/callback";
        public const string OAUTH_SCOPE     = "user:inference user:profile user:sessions:claude_code user:mcp_servers";

        // one pending login at a time (verifier + state), set by /api/oauth/start
        static string PendingVerifier;
        static string PendingState;

        static volatile string CliEmail;   // email of the account the local Claude CLI is currently logged into (null = none/no CLI)
        static string _claudeExe;           // cached path to claude.exe

        // ---- optional cloud sync (Firebase): push state to the user's own dashboard for phone viewing ----
        public const string FIREBASE_API_KEY = "AIzaSyBTeuTwtfS5mnzc7lekpuX7ueChZPQArZs";
        public const string FIREBASE_PROJECT = "claude-quota";
        // Google desktop OAuth client. Loaded at startup from an embedded "gauth.txt" resource (2 lines:
        // client id, client secret) so the values aren't committed to the public repo. Desktop client
        // secrets are not confidential per Google, but this keeps GitHub push-protection happy.
        static string GOOGLE_CLIENT_ID = "";
        static string GOOGLE_CLIENT_SECRET = "";
        public const string GOOGLE_REDIRECT = "http://localhost:45900/gauth";
        static string GPendingVerifier;

        static void LoadGoogleAuth()
        {
            try
            {
                string txt = null;
                byte[] b = Res("gauth.txt");
                if (b != null) txt = Encoding.UTF8.GetString(b);
                else { string f = Path.Combine(Application.StartupPath, "gauth.txt"); if (File.Exists(f)) txt = File.ReadAllText(f); }
                if (txt == null) return;
                var lines = txt.Replace("\r", "").Split('\n');
                if (lines.Length >= 1) GOOGLE_CLIENT_ID = lines[0].Trim();
                if (lines.Length >= 2) GOOGLE_CLIENT_SECRET = lines[1].Trim();
            }
            catch (Exception ex) { Log("LoadGoogleAuth: " + ex.Message); }
        }
        static string CloudUid;             // the paired user's Firebase uid
        static string CloudRefresh;         // Firebase refresh token (plaintext in memory; DPAPI on disk)
        static string CloudIdToken;         // current Firebase id token
        static long CloudIdExpMs;           // when CloudIdToken expires
        static string CloudPath { get { return Path.Combine(DataDir, "cloud.json"); } }

        static readonly string DataDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeQuotaMonitor");
        static string AccountsPath { get { return Path.Combine(DataDir, "accounts.json"); } }
        static string LogPath { get { return Path.Combine(DataDir, "monitor.log"); } }

        static readonly List<Account> Accounts = new List<Account>();
        static readonly object Gate = new object();
        static readonly JavaScriptSerializer JS = new JavaScriptSerializer();
        static readonly HttpClient Http = new HttpClient();

        public static NotifyIcon Tray;
        public static MainForm Window;

        static void Log(string s)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + s + "\r\n"); }
            catch { }
        }
        static long NowMs() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds; }

        // ---- single-exe support: managed WebView2 dlls load from embedded resources; the native
        //      WebView2Loader.dll is extracted to LOCALAPPDATA and put on the dll search path. ----
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetDllDirectory(string lpPathName);

        static byte[] Res(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) return null;
                var b = new byte[s.Length]; int off = 0, n;
                while (off < b.Length && (n = s.Read(b, off, b.Length - off)) > 0) off += n;
                return b;
            }
        }

        static void SetupSingleExe()
        {
            AppDomain.CurrentDomain.AssemblyResolve += delegate (object sender, ResolveEventArgs e)
            {
                try
                {
                    string sn = new AssemblyName(e.Name).Name;
                    string res = sn == "Microsoft.Web.WebView2.Core" ? "Microsoft.Web.WebView2.Core.dll"
                               : sn == "Microsoft.Web.WebView2.WinForms" ? "Microsoft.Web.WebView2.WinForms.dll" : null;
                    if (res == null) return null;
                    var bytes = Res(res);
                    return bytes == null ? null : Assembly.Load(bytes);
                }
                catch { return null; }
            };
            try
            {
                // native loader -> %LOCALAPPDATA%\ClaudeQuotaMonitor\bin (machine-specific, must not roam)
                string binDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeQuotaMonitor", "bin");
                Directory.CreateDirectory(binDir);
                string loaderPath = Path.Combine(binDir, "WebView2Loader.dll");
                var native = Res("WebView2Loader.dll");
                if (native != null && (!File.Exists(loaderPath) || new FileInfo(loaderPath).Length != native.Length))
                    File.WriteAllBytes(loaderPath, native);
                SetDllDirectory(binDir);
            }
            catch (Exception ex) { Log("SetupSingleExe native: " + ex.Message); }
        }

        [STAThread]
        static void Main()
        {
            SetupSingleExe();
            LoadGoogleAuth();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { Directory.CreateDirectory(DataDir); } catch { }
            try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; } catch { }
            Log("=== Claude Quota Monitor " + VERSION + " starting, port " + PORT + " ===");

            LoadAccounts();
            LoadCloud();

            // one shared UA that the endpoint demands, else it 429s hard
            Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "claude-code/2.0.1");
            Http.DefaultRequestHeaders.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            Http.Timeout = TimeSpan.FromSeconds(30);

            CleanupOldLoginProfiles();
            StartHttpServer();
            StartPoller();
            SetupTray();

            Application.ApplicationExit += delegate
            {
                try { if (Tray != null) { Tray.Visible = false; Tray.Dispose(); } } catch { }
            };

            // only a boot launch carrying --tray stays in the tray; a manual launch always opens the window
            bool trayStart = false;
            try { foreach (var a in Environment.GetCommandLineArgs()) if (a == "--tray") trayStart = true; } catch { }
            if (!trayStart) OpenWindow();
            Application.Run();
        }

        // -------------------- persistence --------------------
        static void LoadAccounts()
        {
            lock (Gate)
            {
                Accounts.Clear();
                try
                {
                    if (File.Exists(AccountsPath))
                    {
                        var arr = JS.Deserialize<List<Account>>(File.ReadAllText(AccountsPath, Encoding.UTF8));
                        if (arr != null) Accounts.AddRange(arr);
                    }
                }
                catch (Exception ex) { Log("LoadAccounts error: " + ex.Message); }
            }
        }
        static void SaveAccounts()
        {
            lock (Gate)
            {
                try
                {
                    var slim = new List<Dictionary<string, object>>();
                    foreach (var a in Accounts)
                        slim.Add(new Dictionary<string, object> {
                            { "Id", a.Id }, { "Name", a.Name }, { "Email", a.Email }, { "Plan", a.Plan },
                            { "EncToken", a.EncToken }, { "EncRefresh", a.EncRefresh }, { "ExpiresAtMs", a.ExpiresAtMs }, { "LocalPath", a.LocalPath } });
                    File.WriteAllText(AccountsPath, JS.Serialize(slim), new UTF8Encoding(false));
                }
                catch (Exception ex) { Log("SaveAccounts error: " + ex.Message); }
            }
        }

        static string Protect(string token)
        {
            byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(enc);
        }
        static string Unprotect(string enc)
        {
            byte[] dec = ProtectedData.Unprotect(Convert.FromBase64String(enc), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(dec);
        }

        // -------------------- poller --------------------
        static void StartPoller()
        {
            var t = new System.Threading.Thread(() =>
            {
                long lastCli = 0, lastPush = 0;
                while (true)
                {
                    try { if (NowMs() - lastCli >= 30000) { RefreshCliStatus(); lastCli = NowMs(); } } catch { }
                    try { PollDue(); } catch (Exception ex) { Log("poll loop error: " + ex.Message); }
                    try { if (NowMs() - lastPush >= 30000) { PushCloud(); lastPush = NowMs(); } } catch { }
                    Thread.Sleep(5000);
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        static void PollDue()
        {
            List<Account> due = new List<Account>();
            lock (Gate)
                foreach (var a in Accounts)
                    if (NowMs() - a.LastPollMs >= MIN_POLL_MS) due.Add(a);

            foreach (var a in due)
            {
                a.LastPollMs = NowMs();   // reserve slot immediately so we never double-hit
                PollOne(a).Wait();
                Thread.Sleep(1500);       // small stagger between accounts
            }
        }

        static bool AsBool(object o) { try { return o != null && Convert.ToBoolean(o); } catch { return false; } }

        // Pull email + subscription plan from the profile endpoint (same token) and set them on the account.
        static async Task FetchProfile(Account a, string token)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, PROFILE_URL);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                var resp = await Http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return;
                string body = await resp.Content.ReadAsStringAsync();
                var d = JS.Deserialize<Dictionary<string, object>>(body);
                var acc = (d != null && d.ContainsKey("account")) ? d["account"] as Dictionary<string, object> : null;
                if (acc == null) return;
                string email = GetStr(acc, "email"); if (string.IsNullOrEmpty(email)) email = GetStr(acc, "display_name");
                string plan = AsBool(acc.ContainsKey("has_claude_max") ? acc["has_claude_max"] : null) ? "Max"
                            : (AsBool(acc.ContainsKey("has_claude_pro") ? acc["has_claude_pro"] : null) ? "Pro" : null);
                bool changed = false;
                if (!string.IsNullOrEmpty(email) && a.Email != email) { a.Email = email; changed = true; }
                if (!string.IsNullOrEmpty(plan) && a.Plan != plan) { a.Plan = plan; changed = true; }
                if (changed) SaveAccounts();
            }
            catch (Exception ex) { Log("FetchProfile ex: " + ex.Message); }
        }

        static async Task<HttpResponseMessage> GetUsage(string token)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, USAGE_URL);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            return await Http.SendAsync(req);
        }

        // Read a Claude .credentials.json (claudeAiOauth.{accessToken,refreshToken,expiresAt}).
        static bool ReadCreds(string path, out string access, out string refresh, out long expMs)
        {
            access = null; refresh = null; expMs = 0;
            try
            {
                if (!File.Exists(path)) return false;
                var d = JS.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
                var o = (d != null && d.ContainsKey("claudeAiOauth")) ? d["claudeAiOauth"] as Dictionary<string, object> : null;
                if (o == null) return false;
                access = GetStr(o, "accessToken");
                refresh = GetStr(o, "refreshToken");
                if (o.ContainsKey("expiresAt") && o["expiresAt"] != null) { try { expMs = Convert.ToInt64(o["expiresAt"]); } catch { } }
                return !string.IsNullOrEmpty(access);
            }
            catch (Exception ex) { Log("ReadCreds: " + ex.Message); return false; }
        }

        // Approach A: run `claude auth login` into an ISOLATED config dir (doesn't touch the user's main
        // ~/.claude login), read the resulting subscription token, delete the dir, then self-manage/refresh.
        static void CliLoginAdd(HttpListenerContext ctx) { StartCliLogin(ctx, null); }
        static void CliLoginRelogin(HttpListenerContext ctx)
        {
            try
            {
                var d = JS.Deserialize<Dictionary<string, string>>(ReadBody(ctx));
                string id = d.ContainsKey("id") ? d["id"] : null;
                if (string.IsNullOrEmpty(id)) { WriteJson(ctx, "{\"ok\":false,\"error\":\"no id\"}"); return; }
                StartCliLogin(ctx, id);
            }
            catch (Exception ex) { WriteJson(ctx, "{\"ok\":false,\"error\":" + JS.Serialize(ex.Message) + "}"); }
        }

        // existingId == null -> add a new account; else re-login (replace token on that account)
        static void StartCliLogin(HttpListenerContext ctx, string existingId)
        {
            try
            {
                string claude = FindClaudeExe();
                if (claude == null) { WriteJson(ctx, "{\"ok\":false,\"error\":" + JS.Serialize("這台找不到 claude 指令，請先安裝 Claude Code。") + "}"); return; }
                string dir = Path.Combine(DataDir, "accts", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var psi = new System.Diagnostics.ProcessStartInfo(claude, "auth login --claudeai")
                { UseShellExecute = false, CreateNoWindow = true };   // hidden console; the browser is the real UI
                psi.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = dir;
                var proc = System.Diagnostics.Process.Start(psi);
                WatchCliLogin(dir, proc, existingId);
                WriteJson(ctx, "{\"ok\":true}");
            }
            catch (Exception ex) { WriteJson(ctx, "{\"ok\":false,\"error\":" + JS.Serialize(ex.Message) + "}"); }
        }

        static void WatchCliLogin(string dir, System.Diagnostics.Process proc, string existingId)
        {
            Task.Run(delegate
            {
                string cred = Path.Combine(dir, ".credentials.json");
                bool done = false;
                try
                {
                    for (int i = 0; i < 240; i++)   // wait up to ~4 minutes for the user to authorize
                    {
                        Thread.Sleep(1000);
                        string access, refresh; long exp;
                        if (ReadCreds(cred, out access, out refresh, out exp) && !string.IsNullOrEmpty(refresh))
                        {
                            Account a;
                            if (existingId != null)
                            {
                                lock (Gate) a = Accounts.Find(x => x.Id == existingId);
                                if (a == null) { Log("relogin: account " + existingId + " gone"); break; }
                                a.EncToken = Protect(access); a.EncRefresh = Protect(refresh); a.ExpiresAtMs = exp;
                                a.LastError = null;
                                Log("cli-login: account re-logged in");
                            }
                            else
                            {
                                a = new Account { Id = Guid.NewGuid().ToString("N"), Name = "", Email = "",
                                    EncToken = Protect(access), EncRefresh = Protect(refresh), ExpiresAtMs = exp };
                                lock (Gate) Accounts.Add(a);
                                bool refreshed = RefreshAccount(a);   // verify self-refresh works on this isolated token
                                Log("cli-login: account added; self-refresh test = " + refreshed);
                            }
                            SaveAccounts();
                            a.LastPollMs = NowMs();
                            Task.Run(() => PollOne(a));
                            done = true;
                            break;
                        }
                    }
                    if (!done) Log("cli-login: timed out (no credentials written)");
                }
                catch (Exception ex) { Log("WatchCliLogin: " + ex.Message); }
                finally
                {
                    try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
                    // approach A: delete the isolated dir immediately (plaintext token must not linger)
                    try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception ex) { Log("cli-login dir cleanup: " + ex.Message); }
                }
            });
        }

        static async Task PollOne(Account a)
        {
            try
            {
                // refresh the access token if it's near/past expiry (OAuth logins carry a refresh token)
                if (a.ExpiresAtMs > 0 && !string.IsNullOrEmpty(a.EncRefresh) && NowMs() >= a.ExpiresAtMs - 60000)
                    RefreshAccount(a);

                string token = Unprotect(a.EncToken);
                // auto-fill email + plan once (only when missing)
                if (string.IsNullOrEmpty(a.Email) || string.IsNullOrEmpty(a.Plan))
                    await FetchProfile(a, token);
                var resp = await GetUsage(token);
                // token died mid-life? refresh once and retry
                if ((int)resp.StatusCode == 401 && !string.IsNullOrEmpty(a.EncRefresh) && RefreshAccount(a))
                {
                    token = Unprotect(a.EncToken);
                    resp = await GetUsage(token);
                }
                string body = await resp.Content.ReadAsStringAsync();
                a.LastStatus = (int)resp.StatusCode;
                a.LastUpdatedMs = NowMs();
                if (resp.IsSuccessStatusCode) { a.LastRaw = body; a.LastError = null; }
                else { a.LastError = "HTTP " + (int)resp.StatusCode; Log("poll " + a.Name + " -> " + (int)resp.StatusCode + " " + Trunc(body, 160)); }
            }
            catch (Exception ex)
            {
                a.LastError = ex.Message; a.LastUpdatedMs = NowMs();
                Log("poll " + (a != null ? a.Name : "?") + " ex: " + ex.Message);
            }
        }
        static string Trunc(string s, int n) { if (s == null) return ""; return s.Length <= n ? s : s.Substring(0, n); }

        // -------------------- http server --------------------
        static void StartHttpServer()
        {
            var t = new System.Threading.Thread(() =>
            {
                HttpListener l = new HttpListener();
                // localhost only: serves the embedded WebView2 dashboard. No admin, no firewall, no LAN.
                l.Prefixes.Add("http://localhost:" + PORT + "/");
                try { l.Start(); } catch (Exception ex) { Log("localhost bind failed: " + ex.Message); return; }
                Log("HTTP server listening on localhost:" + PORT);
                while (true)
                {
                    try { var ctx = l.GetContext(); ThreadPool.QueueUserWorkItem(_ => Handle(ctx)); }
                    catch (Exception ex) { Log("GetContext error: " + ex.Message); Thread.Sleep(500); }
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        static void Handle(HttpListenerContext ctx)
        {
            try
            {
                ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
                ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET,POST,OPTIONS";
                ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
                string path = ctx.Request.Url.AbsolutePath.TrimEnd('/');
                if (path == "") path = "/";

                if (ctx.Request.HttpMethod == "OPTIONS") { ctx.Response.StatusCode = 204; ctx.Response.Close(); return; }

                if (path == "/callback") { OAuthCallback(ctx); return; }   // system-browser OAuth redirect lands here
                if (path == "/gauth") { GoogleCallback(ctx); return; }      // Google sign-in loopback lands here
                if (path == "/" || path == "/index.html") { ServeDashboard(ctx); return; }
                if (path == "/api/state") { WriteJson(ctx, BuildState()); return; }
                if (path == "/api/accounts" && ctx.Request.HttpMethod == "POST") { AddAccount(ctx); return; }
                if (path == "/api/accounts/delete" && ctx.Request.HttpMethod == "POST") { DeleteAccount(ctx); return; }
                if (path == "/api/accounts/email" && ctx.Request.HttpMethod == "POST") { SetEmail(ctx); return; }
                if (path == "/api/refresh" && ctx.Request.HttpMethod == "POST") { ForceRefresh(ctx); return; }
                if (path == "/api/oauth/start" && ctx.Request.HttpMethod == "POST") { OAuthStart(ctx); return; }
                if (path == "/api/oauth/login" && ctx.Request.HttpMethod == "POST") { OAuthLogin(ctx); return; }
                if (path == "/api/setup-token/launch" && ctx.Request.HttpMethod == "POST") { LaunchSetupToken(ctx); return; }
                if (path == "/api/accounts/cli-login" && ctx.Request.HttpMethod == "POST") { CliLoginAdd(ctx); return; }
                if (path == "/api/accounts/relogin" && ctx.Request.HttpMethod == "POST") { CliLoginRelogin(ctx); return; }
                if (path == "/api/cloud/pair" && ctx.Request.HttpMethod == "POST") { CloudPair(ctx); return; }
                if (path == "/api/cloud/google-login" && ctx.Request.HttpMethod == "POST") { CloudGoogleLogin(ctx); return; }
                if (path == "/api/cloud/status") { CloudStatus(ctx); return; }
                if (path == "/api/cloud/unpair" && ctx.Request.HttpMethod == "POST") { CloudUnpair(ctx); return; }
                if (path == "/api/oauth/complete" && ctx.Request.HttpMethod == "POST") { OAuthComplete(ctx); return; }

                ctx.Response.StatusCode = 404; WriteBytes(ctx, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("404"));
            }
            catch (Exception ex) { Log("Handle error: " + ex.Message); try { ctx.Response.Abort(); } catch { } }
        }

        static void ServeDashboard(HttpListenerContext ctx)
        {
            byte[] html = null;
            try { string file = Path.Combine(Application.StartupPath, "dashboard.html"); if (File.Exists(file)) html = File.ReadAllBytes(file); } catch { }
            if (html == null) html = Res("dashboard.html");   // single-exe: from embedded resource
            if (html == null) html = Encoding.UTF8.GetBytes("<h1>dashboard.html missing</h1>");
            WriteBytes(ctx, "text/html; charset=utf-8", html);
        }

        // Build the state JSON. Never exposes tokens; raw is the Anthropic response embedded verbatim.
        static string BuildState()
        {
            var sb = new StringBuilder();
            sb.Append("{\"version\":\"").Append(VERSION).Append("\",\"port\":").Append(PORT)
              .Append(",\"now\":").Append(NowMs())
              .Append(",\"cliEmail\":").Append(JS.Serialize(CliEmail))
              .Append(",\"accounts\":[");
            lock (Gate)
            {
                for (int i = 0; i < Accounts.Count; i++)
                {
                    var a = Accounts[i];
                    if (i > 0) sb.Append(",");
                    sb.Append("{\"id\":").Append(JS.Serialize(a.Id))
                      .Append(",\"name\":").Append(JS.Serialize(a.Name))
                      .Append(",\"email\":").Append(JS.Serialize(a.Email))
                      .Append(",\"plan\":").Append(JS.Serialize(a.Plan))
                      .Append(",\"updatedMs\":").Append(a.LastUpdatedMs)
                      .Append(",\"status\":").Append(a.LastStatus)
                      .Append(",\"error\":").Append(JS.Serialize(a.LastError))
                      .Append(",\"raw\":").Append(string.IsNullOrEmpty(a.LastRaw) ? "null" : a.LastRaw)
                      .Append("}");
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        static string ReadBody(HttpListenerContext ctx)
        {
            // all our clients send UTF-8 JSON; force it rather than trusting a charset header
            using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                return r.ReadToEnd();
        }

        // -------------------- in-app OAuth (PKCE) --------------------
        static string B64Url(byte[] b) { return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }

        static string BuildAuthUrl(out string verifier, out string state)
        {
            var rng = RandomNumberGenerator.Create();
            byte[] vb = new byte[32]; rng.GetBytes(vb);
            byte[] sb = new byte[16]; rng.GetBytes(sb);
            verifier = B64Url(vb);
            state = B64Url(sb);
            string challenge;
            using (var sha = SHA256.Create()) challenge = B64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            // code=true => manual flow: the callback page shows a CODE#STATE string to copy back.
            return OAUTH_AUTH_URL
                + "?code=true"
                + "&client_id=" + Uri.EscapeDataString(OAUTH_CLIENT_ID)
                + "&response_type=code"
                + "&redirect_uri=" + Uri.EscapeDataString(OAUTH_REDIRECT)
                + "&scope=" + Uri.EscapeDataString(OAUTH_SCOPE)
                + "&code_challenge=" + challenge
                + "&code_challenge_method=S256"
                + "&state=" + state;
        }

        // Exchange code -> access token -> long-lived key, then add the account. Returns null on success, else an error message.
        public static string OAuthExchange(string code, string state, string verifier)
        {
            try
            {
                if (string.IsNullOrEmpty(verifier)) return "no pending login; click login again";
                // token exchange must be form-encoded (JSON times out / is rejected)
                var post = new HttpRequestMessage(HttpMethod.Post, OAUTH_TOKEN_URL);
                post.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
                    { "grant_type", "authorization_code" },
                    { "code", code },
                    { "state", state },
                    { "client_id", OAUTH_CLIENT_ID },
                    { "redirect_uri", OAUTH_REDIRECT },
                    { "code_verifier", verifier }
                });
                var tr = Http.SendAsync(post).Result;
                string tbody = tr.Content.ReadAsStringAsync().Result;
                if (!tr.IsSuccessStatusCode) { Log("oauth token exchange " + (int)tr.StatusCode + " " + Trunc(tbody, 300)); return "token exchange HTTP " + (int)tr.StatusCode; }
                var td = JS.Deserialize<Dictionary<string, object>>(tbody);
                string access = GetStr(td, "access_token");
                if (string.IsNullOrEmpty(access)) { Log("oauth token: no access_token in " + Trunc(tbody, 300)); return "no access_token"; }
                string refresh = GetStr(td, "refresh_token");
                long expMs = ExpiryMs(td);

                var a = new Account {
                    Id = Guid.NewGuid().ToString("N"), Name = "", Email = "",
                    EncToken = Protect(access),
                    EncRefresh = string.IsNullOrEmpty(refresh) ? null : Protect(refresh),
                    ExpiresAtMs = expMs
                };
                lock (Gate) { Accounts.Add(a); PendingVerifier = null; PendingState = null; }
                SaveAccounts();
                Task.Run(() => PollOne(a));
                a.LastPollMs = NowMs();
                return null;
            }
            catch (Exception ex) { Log("OAuthExchange ex: " + ex.Message); return ex.Message; }
        }

        static string GetStr(Dictionary<string, object> d, string k)
        {
            return (d != null && d.ContainsKey(k) && d[k] != null) ? d[k].ToString() : null;
        }
        static long ExpiryMs(Dictionary<string, object> d)
        {
            try
            {
                if (d != null && d.ContainsKey("expires_in") && d["expires_in"] != null)
                {
                    long secs = Convert.ToInt64(d["expires_in"]);
                    if (secs > 0) return NowMs() + secs * 1000;
                }
            }
            catch { }
            return 0;
        }

        // Use the refresh token to get a fresh access token. Returns true on success (fields updated + saved).
        static bool RefreshAccount(Account a)
        {
            try
            {
                if (string.IsNullOrEmpty(a.EncRefresh)) return false;
                string refresh = Unprotect(a.EncRefresh);
                var post = new HttpRequestMessage(HttpMethod.Post, OAUTH_TOKEN_URL);
                post.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
                    { "grant_type", "refresh_token" },
                    { "refresh_token", refresh },
                    { "client_id", OAUTH_CLIENT_ID }
                });
                var tr = Http.SendAsync(post).Result;
                string tbody = tr.Content.ReadAsStringAsync().Result;
                if (!tr.IsSuccessStatusCode) { Log("token refresh " + (int)tr.StatusCode + " " + Trunc(tbody, 200)); return false; }
                var td = JS.Deserialize<Dictionary<string, object>>(tbody);
                string access = GetStr(td, "access_token");
                if (string.IsNullOrEmpty(access)) return false;
                string newRefresh = GetStr(td, "refresh_token");
                a.EncToken = Protect(access);
                if (!string.IsNullOrEmpty(newRefresh)) a.EncRefresh = Protect(newRefresh);
                a.ExpiresAtMs = ExpiryMs(td);
                SaveAccounts();
                return true;
            }
            catch (Exception ex) { Log("RefreshAccount ex: " + ex.Message); return false; }
        }

        static void OAuthStart(HttpListenerContext ctx)   // kept for the manual/browser fallback path
        {
            try
            {
                string verifier, state;
                string url = BuildAuthUrl(out verifier, out state);
                lock (Gate) { PendingVerifier = verifier; PendingState = state; }
                WriteJson(ctx, "{\"ok\":true,\"url\":" + JS.Serialize(url) + "}");
            }
            catch (Exception ex) { WriteJson(ctx, "{\"ok\":false,\"error\":" + JS.Serialize(ex.Message) + "}"); }
        }

        // Open the login in the user's REAL default browser (not an embedded WebView). claude.ai gates
        // the authorize grant behind Arkose anti-bot; an embedded WebView is flagged as automation and
        // rejected, but a real browser + real user passes — same as the official `claude` CLI does.
        static void OAuthLogin(HttpListenerContext ctx)
        {
            try
            {
                string verifier, state;
                string url = BuildAuthUrl(out verifier, out state);
                lock (Gate) { PendingVerifier = verifier; PendingState = state; }
                try { System.Diagnostics.Process.Start(url); }
                catch (Exception ex) { Log("open browser ex: " + ex.Message); WriteJson(ctx, "{\"ok\":false,\"error\":\"cannot open browser\"}"); return; }
                WriteJson(ctx, "{\"ok\":true}");
            }
            catch (Exception ex) { WriteJson(ctx, "{\"ok\":false,\"error\":" + JS.Serialize(ex.Message) + "}"); }
        }

        // The browser redirects here (http://localhost:45900/callback?code=..&state=..) after authorizing.
        static void OAuthCallback(HttpListenerContext ctx)
        {
            string msg;
            try
            {
                string code = ctx.Request.QueryString["code"];
                string state = ctx.Request.QueryString["state"];
                string error = ctx.Request.QueryString["error"];
                if (!string.IsNullOrEmpty(error)) msg = "登入未完成：" + error + "，請關閉此分頁後再試一次。";
                else if (string.IsNullOrEmpty(code)) msg = "沒有收到授權碼，請關閉此分頁後再試一次。";
                else
                {
                    string verifier; string pend; lock (Gate) { verifier = PendingVerifier; pend = PendingState; }
                    string err = OAuthExchange(code, string.IsNullOrEmpty(state) ? pend : state, verifier);
                    msg = err == null
                        ? "✅ 登入成功！已加入監控，請關閉此分頁、回到「Claude 額度監控」。"
                        : "登入失敗：" + err + "（詳見 monitor.log）";
                }
            }
            catch (Exception ex) { Log("OAuthCallback ex: " + ex.Message); msg = "登入處理發生錯誤：" + ex.Message; }
            byte[] b = Encoding.UTF8.GetBytes("<!doctype html><html lang=zh-Hant><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><body style=\"font-family:'Microsoft JhengHei',sans-serif;background:#f4efe6;color:#2c2622;text-align:center;padding:80px 24px\"><div style=\"font-size:20px;line-height:1.8\">" + msg + "</div></body></html>");
            WriteBytes(ctx, "text/html; charset=utf-8", b);
        }

        // -------------------- local Claude CLI helpers --------------------
        static string FindClaudeExe()
        {
            if (!string.IsNullOrEmpty(_claudeExe) && File.Exists(_claudeExe)) return _claudeExe;
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string[] cands = {
                Path.Combine(home, ".local", "bin", "claude.exe"),
                Path.Combine(appdata, "npm", "claude.cmd"),
                Path.Combine(appdata, "npm", "claude.exe"),
                Path.Combine(home, "AppData", "Local", "Programs", "claude", "claude.exe")
            };
            foreach (var c in cands) if (File.Exists(c)) { _claudeExe = c; return c; }
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("where", "claude") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                var p = System.Diagnostics.Process.Start(psi);
                string outp = p.StandardOutput.ReadToEnd(); p.WaitForExit(3000);
                foreach (var line in outp.Split('\n')) { string t = line.Trim(); if (t.Length > 0 && File.Exists(t)) { _claudeExe = t; return t; } }
            }
            catch { }
            return null;
        }

        // Ask the CLI who it's logged in as (official command; does NOT read the token file). Cache in CliEmail.
        static void RefreshCliStatus()
        {
            try
            {
                string claude = FindClaudeExe();
                if (claude == null) { CliEmail = null; return; }
                var psi = new System.Diagnostics.ProcessStartInfo(claude, "auth status --json")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                var p = System.Diagnostics.Process.Start(psi);
                string outp = p.StandardOutput.ReadToEnd(); p.WaitForExit(5000);
                var d = JS.Deserialize<Dictionary<string, object>>(outp);
                bool logged = d != null && d.ContainsKey("loggedIn") && d["loggedIn"] != null && Convert.ToBoolean(d["loggedIn"]);
                CliEmail = logged ? GetStr(d, "email") : null;
            }
            catch (Exception ex) { Log("RefreshCliStatus: " + ex.Message); CliEmail = null; }
        }

        // Launch the official `claude setup-token` in a console window the user can copy the token from.
        static void LaunchSetupToken(HttpListenerContext ctx)
        {
            try
            {
                string claude = FindClaudeExe();
                if (claude == null) { WriteJson(ctx, "{\"ok\":false,\"error\":" + JS.Serialize("這台找不到 claude 指令，請先安裝 Claude Code，或改用手動貼權杖。") + "}"); return; }
                // /s /k => run the quoted command and keep the window open so the printed token stays visible
                string args = "/s /k \"\"" + claude + "\" setup-token\"";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", args) { UseShellExecute = true });
                WriteJson(ctx, "{\"ok\":true}");
            }
            catch (Exception ex) { WriteJson(ctx, "{\"ok\":false,\"error\":" + JS.Serialize(ex.Message) + "}"); }
        }

        // -------------------- cloud sync (Firebase) --------------------
        static void LoadCloud()
        {
            try
            {
                if (!File.Exists(CloudPath)) return;
                var d = JS.Deserialize<Dictionary<string, string>>(File.ReadAllText(CloudPath, Encoding.UTF8));
                if (d == null) return;
                CloudUid = d.ContainsKey("Uid") ? d["Uid"] : null;
                if (d.ContainsKey("EncRt") && !string.IsNullOrEmpty(d["EncRt"])) CloudRefresh = Unprotect(d["EncRt"]);
            }
            catch (Exception ex) { Log("LoadCloud: " + ex.Message); }
        }
        static void SaveCloud()
        {
            try
            {
                var d = new Dictionary<string, string> {
                    { "Uid", CloudUid },
                    { "EncRt", string.IsNullOrEmpty(CloudRefresh) ? null : Protect(CloudRefresh) }
                };
                File.WriteAllText(CloudPath, JS.Serialize(d), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log("SaveCloud: " + ex.Message); }
        }

        // Mint/refresh a Firebase id token from the stored refresh token.
        static bool EnsureFirebaseToken()
        {
            if (string.IsNullOrEmpty(CloudRefresh)) return false;
            if (!string.IsNullOrEmpty(CloudIdToken) && NowMs() < CloudIdExpMs - 60000) return true;
            try
            {
                var post = new HttpRequestMessage(HttpMethod.Post, "https://securetoken.googleapis.com/v1/token?key=" + FIREBASE_API_KEY);
                post.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
                    { "grant_type", "refresh_token" }, { "refresh_token", CloudRefresh } });
                var r = Http.SendAsync(post).Result;
                string body = r.Content.ReadAsStringAsync().Result;
                if (!r.IsSuccessStatusCode) { Log("firebase token " + (int)r.StatusCode + " " + Trunc(body, 200)); return false; }
                var d = JS.Deserialize<Dictionary<string, object>>(body);
                CloudIdToken = GetStr(d, "id_token");
                long secs = 3600; try { secs = Convert.ToInt64(GetStr(d, "expires_in")); } catch { }
                CloudIdExpMs = NowMs() + secs * 1000;
                string newRt = GetStr(d, "refresh_token");
                if (!string.IsNullOrEmpty(newRt) && newRt != CloudRefresh) { CloudRefresh = newRt; SaveCloud(); }
                return !string.IsNullOrEmpty(CloudIdToken);
            }
            catch (Exception ex) { Log("EnsureFirebaseToken: " + ex.Message); return false; }
        }

        // Write the current state to Firestore /states/{uid} as a single JSON string field.
        static void PushCloud()
        {
            try
            {
                if (string.IsNullOrEmpty(CloudUid) || string.IsNullOrEmpty(CloudRefresh)) return;
                if (!EnsureFirebaseToken()) return;
                string stateJson = BuildState();
                string docJson = "{\"fields\":{\"json\":{\"stringValue\":" + JS.Serialize(stateJson)
                    + "},\"updatedAt\":{\"integerValue\":\"" + NowMs() + "\"}}}";
                string url = "https://firestore.googleapis.com/v1/projects/" + FIREBASE_PROJECT
                    + "/databases/(default)/documents/states/" + CloudUid;
                var req = new HttpRequestMessage(new HttpMethod("PATCH"), url);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + CloudIdToken);
                req.Content = new StringContent(docJson, Encoding.UTF8, "application/json");
                var r = Http.SendAsync(req).Result;
                if (!r.IsSuccessStatusCode) { string b = r.Content.ReadAsStringAsync().Result; Log("cloud push " + (int)r.StatusCode + " " + Trunc(b, 200)); }
            }
            catch (Exception ex) { Log("PushCloud: " + ex.Message); }
        }

        static void CloudPair(HttpListenerContext ctx)
        {
            try
            {
                var d = JS.Deserialize<Dictionary<string, string>>(ReadBody(ctx));
                string code = d.ContainsKey("code") ? (d["code"] ?? "").Trim() : "";
                string json = Encoding.UTF8.GetString(Convert.FromBase64String(code));
                var p = JS.Deserialize<Dictionary<string, string>>(json);
                string uid = p.ContainsKey("uid") ? p["uid"] : null;
                string rt = p.ContainsKey("rt") ? p["rt"] : null;
                if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(rt)) { WriteJson(ctx, "{\"ok\":false,\"error\":\"配對碼格式不對\"}"); return; }
                CloudUid = uid; CloudRefresh = rt; CloudIdToken = null; CloudIdExpMs = 0;
                SaveCloud();
                Task.Run(() => PushCloud());
                WriteJson(ctx, "{\"ok\":true}");
            }
            catch (Exception ex) { WriteJson(ctx, "{\"ok\":false,\"error\":" + JS.Serialize("配對碼無效：" + ex.Message) + "}"); }
        }
        static void CloudStatus(HttpListenerContext ctx)
        {
            WriteJson(ctx, "{\"paired\":" + (!string.IsNullOrEmpty(CloudUid) && !string.IsNullOrEmpty(CloudRefresh) ? "true" : "false") + "}");
        }
        static void CloudUnpair(HttpListenerContext ctx)
        {
            CloudUid = null; CloudRefresh = null; CloudIdToken = null; CloudIdExpMs = 0;
            try { if (File.Exists(CloudPath)) File.Delete(CloudPath); } catch { }
            WriteJson(ctx, "{\"ok\":true}");
        }

        // Open the user's default browser to Google sign-in; the loopback redirect lands on /gauth.
        static void CloudGoogleLogin(HttpListenerContext ctx)
        {
            try
            {
                if (string.IsNullOrEmpty(GOOGLE_CLIENT_ID)) { WriteJson(ctx, "{\"ok\":false,\"error\":\"這個版本未內建 Google 登入設定，請改用手動配對碼。\"}"); return; }
                var rng = RandomNumberGenerator.Create();
                byte[] vb = new byte[32]; rng.GetBytes(vb);
                string verifier = B64Url(vb), challenge;
                using (var sha = SHA256.Create()) challenge = B64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
                lock (Gate) GPendingVerifier = verifier;
                string url = "https://accounts.google.com/o/oauth2/v2/auth"
                    + "?client_id=" + Uri.EscapeDataString(GOOGLE_CLIENT_ID)
                    + "&redirect_uri=" + Uri.EscapeDataString(GOOGLE_REDIRECT)
                    + "&response_type=code&scope=" + Uri.EscapeDataString("openid email profile")
                    + "&code_challenge=" + challenge + "&code_challenge_method=S256"
                    + "&access_type=offline&prompt=select_account";
                System.Diagnostics.Process.Start(url);
                WriteJson(ctx, "{\"ok\":true}");
            }
            catch (Exception ex) { WriteJson(ctx, "{\"ok\":false,\"error\":" + JS.Serialize(ex.Message) + "}"); }
        }

        // Loopback landing: exchange Google code -> Google id_token -> Firebase (signInWithIdp) -> pair.
        static void GoogleCallback(HttpListenerContext ctx)
        {
            string msg;
            try
            {
                string code = ctx.Request.QueryString["code"];
                string error = ctx.Request.QueryString["error"];
                if (!string.IsNullOrEmpty(error)) msg = "登入未完成：" + error;
                else if (string.IsNullOrEmpty(code)) msg = "沒有收到授權碼。";
                else
                {
                    string verifier; lock (Gate) verifier = GPendingVerifier;
                    // 1) code -> Google tokens
                    var post = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token");
                    post.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
                        { "grant_type", "authorization_code" }, { "code", code },
                        { "client_id", GOOGLE_CLIENT_ID }, { "client_secret", GOOGLE_CLIENT_SECRET },
                        { "redirect_uri", GOOGLE_REDIRECT }, { "code_verifier", verifier ?? "" } });
                    var tr = Http.SendAsync(post).Result;
                    string tbody = tr.Content.ReadAsStringAsync().Result;
                    if (!tr.IsSuccessStatusCode) { Log("google token " + (int)tr.StatusCode + " " + Trunc(tbody, 300)); msg = "Google 換權杖失敗（HTTP " + (int)tr.StatusCode + "）"; }
                    else
                    {
                        string gIdToken = GetStr(JS.Deserialize<Dictionary<string, object>>(tbody), "id_token");
                        // 2) Google id_token -> Firebase idToken/refreshToken/uid
                        var fb = new HttpRequestMessage(HttpMethod.Post, "https://identitytoolkit.googleapis.com/v1/accounts:signInWithIdp?key=" + FIREBASE_API_KEY);
                        string body = "{\"postBody\":" + JS.Serialize("id_token=" + gIdToken + "&providerId=google.com")
                            + ",\"requestUri\":\"http://localhost\",\"returnSecureToken\":true}";
                        fb.Content = new StringContent(body, Encoding.UTF8, "application/json");
                        var fr = Http.SendAsync(fb).Result;
                        string fbody = fr.Content.ReadAsStringAsync().Result;
                        if (!fr.IsSuccessStatusCode) { Log("firebase signInWithIdp " + (int)fr.StatusCode + " " + Trunc(fbody, 300)); msg = "連結 Firebase 失敗（HTTP " + (int)fr.StatusCode + "）"; }
                        else
                        {
                            var fd = JS.Deserialize<Dictionary<string, object>>(fbody);
                            string uid = GetStr(fd, "localId"), rt = GetStr(fd, "refreshToken");
                            if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(rt)) msg = "連結失敗：回傳缺 uid/refreshToken";
                            else
                            {
                                lock (Gate) { CloudUid = uid; CloudRefresh = rt; CloudIdToken = null; CloudIdExpMs = 0; GPendingVerifier = null; }
                                SaveCloud();
                                Task.Run(() => PushCloud());
                                msg = "✅ 已連結成功！用量會開始同步到你的手機儀表板（claude-quota.web.app），請關閉此分頁。";
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Log("GoogleCallback ex: " + ex.Message); msg = "連結發生錯誤：" + ex.Message; }
            byte[] b = Encoding.UTF8.GetBytes("<!doctype html><html lang=zh-Hant><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><body style=\"font-family:'Microsoft JhengHei',sans-serif;background:#f4efe6;color:#2c2622;text-align:center;padding:80px 24px\"><div style=\"font-size:20px;line-height:1.8\">" + msg + "</div></body></html>");
            WriteBytes(ctx, "text/html; charset=utf-8", b);
        }

        public static void LogLine(string s) { Log(s); }
        public static string PubTrunc(string s, int n) { return Trunc(s, n); }
        // EXCEPTION to the "always recycle" rule: our throwaway WebView2 login profiles are permanently
        // deleted (user-approved) so they don't pile up in the Recycle Bin. This is ONLY for cqm-login-*
        // temp folders. Every other file/folder must still go to the Recycle Bin.
        public static void PurgeTempDir(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); }
            catch (Exception ex) { Log("PurgeTempDir: " + ex.Message); }
        }

        // Sweep leftover throwaway login profiles from previous runs (unlocked now => silent).
        static void CleanupOldLoginProfiles()
        {
            try
            {
                foreach (var d in Directory.GetDirectories(Path.GetTempPath(), "cqm-login-*"))
                    try { PurgeTempDir(d); } catch { }
            }
            catch (Exception ex) { Log("CleanupOldLoginProfiles: " + ex.Message); }
        }

        static void OAuthComplete(HttpListenerContext ctx)
        {
            try
            {
                var d = JS.Deserialize<Dictionary<string, string>>(ReadBody(ctx));
                string pasted = d.ContainsKey("code") ? (d["code"] ?? "").Trim() : "";
                if (pasted == "") { WriteJson(ctx, "{\"ok\":false,\"error\":\"no code\"}"); return; }
                // the copy page often gives  <code>#<state>
                string code = pasted, state;
                lock (Gate) state = PendingState;
                int hash = pasted.IndexOf('#');
                if (hash >= 0) { code = pasted.Substring(0, hash); state = pasted.Substring(hash + 1); }
                string verifier; lock (Gate) verifier = PendingVerifier;
                string err = OAuthExchange(code, state, verifier);
                if (err == null) WriteJson(ctx, "{\"ok\":true}");
                else WriteJson(ctx, "{\"ok\":false,\"error\":" + JS.Serialize(err) + "}");
            }
            catch (Exception ex) { Log("OAuthComplete ex: " + ex.Message); WriteJson(ctx, "{\"ok\":false,\"error\":" + JS.Serialize(ex.Message) + "}"); }
        }

        static void AddAccount(HttpListenerContext ctx)
        {
            try
            {
                var d = JS.Deserialize<Dictionary<string, string>>(ReadBody(ctx));
                string name = d.ContainsKey("name") ? (d["name"] ?? "").Trim() : "";
                string email = d.ContainsKey("email") ? (d["email"] ?? "").Trim() : "";
                string token = d.ContainsKey("token") ? (d["token"] ?? "").Trim() : "";
                if (name == "") name = "Account";
                if (!token.StartsWith("sk-ant-oat")) { WriteJson(ctx, "{\"ok\":false,\"error\":\"bad token\"}"); return; }
                var a = new Account { Id = Guid.NewGuid().ToString("N"), Name = name, Email = email, EncToken = Protect(token) };
                lock (Gate) Accounts.Add(a);
                SaveAccounts();
                Task.Run(() => PollOne(a));   // poll immediately (this counts as its first slot)
                a.LastPollMs = NowMs();
                WriteJson(ctx, "{\"ok\":true,\"id\":" + JS.Serialize(a.Id) + "}");
            }
            catch (Exception ex) { WriteJson(ctx, "{\"ok\":false,\"error\":" + JS.Serialize(ex.Message) + "}"); }
        }

        static void DeleteAccount(HttpListenerContext ctx)
        {
            try
            {
                var d = JS.Deserialize<Dictionary<string, string>>(ReadBody(ctx));
                string id = d.ContainsKey("id") ? d["id"] : "";
                lock (Gate) Accounts.RemoveAll(x => x.Id == id);
                SaveAccounts();
                WriteJson(ctx, "{\"ok\":true}");
            }
            catch (Exception ex) { WriteJson(ctx, "{\"ok\":false,\"error\":" + JS.Serialize(ex.Message) + "}"); }
        }

        static void SetEmail(HttpListenerContext ctx)
        {
            try
            {
                var d = JS.Deserialize<Dictionary<string, string>>(ReadBody(ctx));
                string id = d.ContainsKey("id") ? d["id"] : "";
                string email = d.ContainsKey("email") ? (d["email"] ?? "").Trim() : "";
                lock (Gate)
                    foreach (var a in Accounts)
                        if (a.Id == id) a.Email = email;
                SaveAccounts();
                WriteJson(ctx, "{\"ok\":true}");
            }
            catch (Exception ex) { WriteJson(ctx, "{\"ok\":false,\"error\":" + JS.Serialize(ex.Message) + "}"); }
        }

        static void ForceRefresh(HttpListenerContext ctx)
        {
            // allow a manual refresh but keep the per-account floor so we never trip 429
            lock (Gate)
                foreach (var a in Accounts)
                    if (NowMs() - a.LastPollMs >= MIN_POLL_MS) a.LastPollMs = 0;
            WriteJson(ctx, "{\"ok\":true}");
        }

        static void WriteJson(HttpListenerContext ctx, string json) { WriteBytes(ctx, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json)); }
        static void WriteBytes(HttpListenerContext ctx, string ctype, byte[] data)
        {
            try { ctx.Response.ContentType = ctype; ctx.Response.ContentLength64 = data.Length; ctx.Response.OutputStream.Write(data, 0, data.Length); ctx.Response.OutputStream.Close(); }
            catch (Exception ex) { Log("write error: " + ex.Message); }
        }

        // Load the app icon from disk (dev) or the embedded resource (single-exe). null on failure.
        public static Icon LoadAppIcon()
        {
            try { string ico = Path.Combine(Application.StartupPath, "icon.ico"); if (File.Exists(ico)) return new Icon(ico); } catch { }
            try { using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("icon.ico")) if (s != null) return new Icon(s); } catch { }
            return null;
        }

        // -------------------- auto-start on boot (HKCU Run key) + start-minimized setting --------------------
        const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RUN_NAME = "ClaudeQuotaMonitor";
        const string APP_KEY = @"Software\ClaudeQuotaMonitor";
        static bool IsAutoStart()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RUN_KEY)) return k != null && k.GetValue(RUN_NAME) != null; }
            catch { return false; }
        }
        static void SetAutoStart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RUN_KEY, true) ?? Registry.CurrentUser.CreateSubKey(RUN_KEY))
                {
                    // boot launch adds --tray only when "start minimized" is on, so boot stays silently in the tray
                    if (on) k.SetValue(RUN_NAME, "\"" + Application.ExecutablePath + "\"" + (IsStartMinimized() ? " --tray" : ""));
                    else if (k.GetValue(RUN_NAME) != null) k.DeleteValue(RUN_NAME, false);
                }
            }
            catch (Exception ex) { Log("SetAutoStart: " + ex.Message); }
        }
        // Start minimized: only affects the boot auto-start (a manual launch always opens the window).
        // It is stored as a preference and applied by (re)writing the Run key with/without --tray.
        static bool IsStartMinimized()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(APP_KEY)) return k != null && "1".Equals(Convert.ToString(k.GetValue("StartMinimized"))); }
            catch { return false; }
        }
        static void SetStartMinimized(bool on)
        {
            try { using (var k = Registry.CurrentUser.CreateSubKey(APP_KEY)) k.SetValue("StartMinimized", on ? "1" : "0"); }
            catch (Exception ex) { Log("SetStartMinimized: " + ex.Message); }
            if (IsAutoStart()) SetAutoStart(true);   // re-write the Run key so the --tray flag matches the new setting
        }

        // -------------------- tray + window --------------------
        static void SetupTray()
        {
            Tray = new NotifyIcon();
            try { Icon ic = LoadAppIcon(); Tray.Icon = ic != null ? ic : SystemIcons.Information; } catch { Tray.Icon = SystemIcons.Information; }
            Tray.Text = "Claude Quota Monitor";
            Tray.Visible = true;

            var menu = new ContextMenuStrip();
            // (double-click the tray icon opens the dashboard, so no explicit "open" item is needed)
            var min = new ToolStripMenuItem("最小化啟動") { CheckOnClick = true, Checked = IsStartMinimized() }; // 最小化啟動(啟動時直接待在匣)
            min.CheckedChanged += delegate { SetStartMinimized(min.Checked); };
            menu.Items.Add(min);
            var auto = new ToolStripMenuItem("開機自動啟動") { CheckOnClick = true, Checked = IsAutoStart() }; // 開機自動啟動
            auto.CheckedChanged += delegate { SetAutoStart(auto.Checked); };
            menu.Items.Add(auto);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("結束", null, delegate { Application.Exit(); });                                // 結束
            Tray.ContextMenuStrip = menu;
            Tray.DoubleClick += delegate { OpenWindow(); };
        }

        static void OpenWindow()
        {
            if (Window == null || Window.IsDisposed) Window = new MainForm();
            Window.Show(); Window.WindowState = FormWindowState.Normal; Window.BringToFront(); Window.Activate();
        }
    }

    // -------------------- WebView2 window (loads the served dashboard) --------------------
    public class MainForm : Form
    {
        WebView2 wv;
        public MainForm()
        {
            Text = "Claude Quota Monitor " + Program.VERSION;
            Width = 460; Height = 720; StartPosition = FormStartPosition.CenterScreen;
            try { Icon ic = Program.LoadAppIcon(); if (ic != null) Icon = ic; } catch { }
            wv = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(wv);
            Init();
        }
        async void Init()
        {
            try
            {
                string udf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeQuotaMonitor", "wv2");
                var env = await CoreWebView2Environment.CreateAsync(null, udf);
                await wv.EnsureCoreWebView2Async(env);
                wv.CoreWebView2.Navigate("http://localhost:" + Program.PORT + "/");
            }
            catch (Exception) { }
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // hide to tray instead of quitting
            e.Cancel = true; Hide();
            base.OnFormClosing(e);
        }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // minimize also hides to the tray (out of the taskbar); cloud push keeps running in the background
            if (WindowState == FormWindowState.Minimized) { Hide(); WindowState = FormWindowState.Normal; }
        }
    }

    // -------------------- private OAuth login window --------------------
    // Fixed 1280x720, its own throwaway WebView2 profile => no session is kept, so every login
    // is fresh and you can switch accounts without hunting for a logout button. Auto-captures the code.
    public class LoginForm : Form
    {
        WebView2 wv;
        readonly string authUrl, verifier, state;
        string udf;
        bool handled;

        public LoginForm(string authUrl, string verifier, string state)
        {
            this.authUrl = authUrl; this.verifier = verifier; this.state = state;
            Text = "登入 Claude 帳號";
            ClientSize = new Size(1280, 720);
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            ShowInTaskbar = true;
            try { Icon ic = Program.LoadAppIcon(); if (ic != null) Icon = ic; } catch { }
            wv = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(wv);
            Init();
        }

        async void Init()
        {
            try
            {
                // fresh throwaway profile in %TEMP% => no cookies persist between logins
                udf = Path.Combine(Path.GetTempPath(), "cqm-login-" + Guid.NewGuid().ToString("N"));
                var env = await CoreWebView2Environment.CreateAsync(null, udf);
                await wv.EnsureCoreWebView2Async(env);
                wv.CoreWebView2.NavigationStarting += OnNav;
                wv.CoreWebView2.WebResourceResponseReceived += OnResp;   // capture the background XHR error body
                wv.CoreWebView2.AddWebResourceRequestedFilter("https://claude.ai/v1/oauth/*", CoreWebView2WebResourceContext.All);
                wv.CoreWebView2.WebResourceRequested += OnReq;           // capture the grant POST body
                wv.CoreWebView2.Navigate(authUrl);
            }
            catch (Exception ex) { Program.LogLine("LoginForm init: " + ex.Message); }
        }

        // Log the POST body the SPA sends to the grant endpoint, then restore it so the request proceeds.
        void OnReq(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                var r = e.Request;
                if (r.Method == "POST" && r.Content != null)
                {
                    byte[] bytes;
                    using (var ms = new MemoryStream()) { r.Content.CopyTo(ms); bytes = ms.ToArray(); }
                    r.Content = new MemoryStream(bytes);   // restore so the real request still has its body
                    Program.LogLine("login req POST " + Program.PubTrunc(r.Uri, 120) + " body=" + Program.PubTrunc(Encoding.UTF8.GetString(bytes), 500));
                }
            }
            catch (Exception ex) { Program.LogLine("OnReq: " + ex.Message); }
        }

        // Log the response body of any failing oauth/authorize API call — that's where claude.ai hides
        // the real reason ("Invalid request format" is only the generic UI text).
        async void OnResp(object sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            try
            {
                string uri = e.Request.Uri;
                int status = e.Response.StatusCode;
                bool relevant = uri.IndexOf("oauth", StringComparison.OrdinalIgnoreCase) >= 0
                             || uri.IndexOf("authorize", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!relevant) return;
                if (status >= 400)
                {
                    string body = "";
                    try { using (var st = await e.Response.GetContentAsync()) if (st != null) body = new StreamReader(st).ReadToEnd(); }
                    catch { }
                    Program.LogLine("login resp " + status + " " + Program.PubTrunc(uri, 160) + " :: " + Program.PubTrunc(body, 400));
                }
                else Program.LogLine("login resp " + status + " " + Program.PubTrunc(uri, 160));
            }
            catch { }
        }

        void OnNav(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            try
            {
                string uri = e.Uri;
                // diagnostics: log EVERY navigation so a failed login leaves a full trail in monitor.log
                string er = GetQ(uri, "error"); string erd = GetQ(uri, "error_description");
                Program.LogLine("login nav: " + Program.PubTrunc(uri, 400)
                    + (er != null ? "  [error=" + er + " desc=" + erd + "]" : ""));
                if (!handled && uri.StartsWith(Program.OAUTH_REDIRECT, StringComparison.OrdinalIgnoreCase) && uri.IndexOf("code=", StringComparison.Ordinal) >= 0)
                {
                    handled = true;
                    e.Cancel = true;   // don't actually load the callback page; we have what we need
                    string code = GetQ(uri, "code");
                    string st = GetQ(uri, "state");
                    Task.Run(delegate
                    {
                        string err = Program.OAuthExchange(code, string.IsNullOrEmpty(st) ? state : st, verifier);
                        try
                        {
                            BeginInvoke((Action)delegate
                            {
                                if (err != null) MessageBox.Show(this, "登入失敗：" + err + "\n（詳見 monitor.log）", "Claude 額度監控");
                                Close();
                            });
                        }
                        catch { }
                    });
                }
            }
            catch { }
        }

        static string GetQ(string uri, string key)
        {
            int q = uri.IndexOf('?'); if (q < 0) return null;
            string qs = uri.Substring(q + 1);
            int frag = qs.IndexOf('#'); if (frag >= 0) qs = qs.Substring(0, frag);
            foreach (var kv in qs.Split('&'))
            {
                int eq = kv.IndexOf('=');
                if (eq > 0 && Uri.UnescapeDataString(kv.Substring(0, eq)) == key)
                    return Uri.UnescapeDataString(kv.Substring(eq + 1));
            }
            return null;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // Don't delete the throwaway profile here: WebView2 still holds a lock right after close,
            // which would pop a "folder in use" dialog. It's swept on next startup instead (unlocked then).
            try { if (wv != null) wv.Dispose(); } catch { }
            base.OnFormClosed(e);
        }
    }
}
