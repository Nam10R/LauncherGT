using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml;
using System.Xml.Linq;

public sealed class UpdateInfo
{
    public string Status = "";
    public string Detail = "";
    public string Url = "";
    public string LocalSha = "";
    public string RemoteSha = "";
    public bool Online;
    public int Ahead = -1;
    public int Behind = -1;
    public string[] Changes = new string[0];
    public bool CanUpdate { get { return Online && Ahead > 0 && Behind == 0 && RemoteSha.Length == 40; } }
}

public static class UpdateChecker
{
    private sealed class CommitMessage { public string message { get; set; } }
    private sealed class CommitEntry
    {
        public string sha { get; set; }
        public CommitMessage commit { get; set; }
    }
    private sealed class Comparison
    {
        public string status { get; set; }
        public int? ahead_by { get; set; }
        public int? behind_by { get; set; }
        public CommitEntry[] commits { get; set; }
    }
    private sealed class CommitPage { public PagePayload payload { get; set; } }
    private sealed class PagePayload { public CommitRoute commitRoute { get; set; } }
    private sealed class CommitRoute { public PublishedCommit commit { get; set; } }
    private sealed class PublishedCommit
    {
        public string oid { get; set; }
        public string[] parents { get; set; }
    }

    private static readonly Regex GitSha = new Regex(@"\A[0-9a-fA-F]{40}\z", RegexOptions.CultureInvariant);

    public static string ProjectUrl(string name)
    {
        if (string.Equals(name, "XCL", StringComparison.OrdinalIgnoreCase)) return "https://github.com/ivall/xcl";
        if (string.Equals(name, "VIctory", StringComparison.OrdinalIgnoreCase)) return "https://github.com/Marvisak/VIctory";
        throw new ArgumentException("Emulador desconocido.", "name");
    }

    public static Task<UpdateInfo> CheckAsync(string name, string localSha)
    {
        return Task.Run(() => Check(name, localSha));
    }

    private static UpdateInfo Check(string name, string localSha)
    {
        var result = new UpdateInfo { Url = ProjectUrl(name), LocalSha = localSha ?? "" };
        string api = result.Url.Replace("https://github.com/", "https://api.github.com/repos/");
        try
        {
            // Without a branch argument, GitHub returns the default branch tip.
            var tips = GetJson<CommitEntry[]>(api + "/commits?per_page=3");
            if (SetTip(result, tips)) return result;
            string remote = result.RemoteSha;

            // Pin both ends. A new push between requests must not change what the install button installs.
            string range = result.LocalSha + "..." + remote;
            var diff = GetJson<Comparison>(api + "/compare/" + range);
            if (diff == null || diff.ahead_by.GetValueOrDefault(-1) < 0 || diff.behind_by.GetValueOrDefault(-1) < 0)
                throw new InvalidDataException();
            int ahead = diff.ahead_by.Value, behind = diff.behind_by.Value;
            string status = ahead > 0 ? (behind > 0 ? "diverged" : "ahead") : (behind > 0 ? "behind" : "identical");
            if (!string.Equals(diff.status, status, StringComparison.Ordinal)) throw new InvalidDataException();
            result.Online = true;
            result.Ahead = ahead;
            result.Behind = behind;
            result.Url += "/compare/" + range;
            if (ahead > 0 && behind == 0)
            {
                result.Status = ahead == 1 ? "1 cambio nuevo" : ahead + " cambios nuevos";
                result.Detail = "Cambios publicados desde tu versión instalada.";
            }
            else if (ahead > 0)
            {
                result.Status = "Versiones divergentes";
                result.Detail = ahead + " cambios exclusivos de GitHub y " + behind + " de tu copia.";
            }
            else if (behind > 0)
            {
                result.Status = "Copia adelantada";
                result.Detail = "Tu copia tiene " + behind + (behind == 1 ? " cambio adicional." : " cambios adicionales.");
            }
            else
            {
                result.Status = "Al día";
                result.Detail = "Las dos revisiones tienen el mismo historial.";
            }
            // The unpaginated comparison ends with the newest commit, even when capped at 250.
            result.Changes = (diff.commits ?? new CommitEntry[0]).Reverse()
                .Where(c => c != null && c.commit != null && !string.IsNullOrWhiteSpace(c.commit.message))
                .Select(c => c.commit.message.Split(new[] { '\r', '\n' }, 2)[0].Trim())
                .Take(3).ToArray();
        }
        catch (WebException ex)
        {
            using (var response = ex.Response as HttpWebResponse)
            {
                if (response == null)
                {
                    result.Status = "Sin conexión";
                    result.Detail = ex.Status == WebExceptionStatus.Timeout
                        ? "GitHub tardó demasiado. Podés volver a consultar." : "No se pudo conectar con GitHub.";
                }
                else
                {
                    int code = (int)response.StatusCode;
                    bool limited = code == 429 || (code == 403 &&
                        (response.Headers["X-RateLimit-Remaining"] == "0" || response.Headers["Retry-After"] != null));
                    result.Status = limited ? "Límite de GitHub" : "Consulta pendiente";
                    result.Detail = limited ? RetryMessage(response) :
                        (code == 404 ? "GitHub no encontró el proyecto o esta revisión." :
                         "GitHub respondió con el código " + code + ".");
                    if (limited) TryPublicHistory(result);
                }
            }
        }
        catch (Exception ex)
        {
            if (!(ex is IOException || ex is InvalidDataException ||
                  ex is ArgumentException || ex is InvalidOperationException)) throw;
            result.Status = "Consulta pendiente";
            result.Detail = "No se pudo verificar la respuesta de GitHub.";
        }
        return result;
    }

    private static bool SetTip(UpdateInfo result, CommitEntry[] tips)
    {
        if (tips == null || tips.Length == 0 || tips[0] == null || !GitSha.IsMatch(tips[0].sha ?? ""))
            throw new InvalidDataException();
        string remote = tips[0].sha.ToLowerInvariant();
        result.RemoteSha = remote;
        result.Changes = tips.Where(c => c != null && c.commit != null && !String.IsNullOrWhiteSpace(c.commit.message))
            .Select(c => c.commit.message.Trim().Split(new[] { '\r', '\n' }, 2)[0].Trim()).Take(3).ToArray();
        if (!GitSha.IsMatch(result.LocalSha))
        {
            result.Online = true;
            result.Status = "Última revisión  " + remote.Substring(0, 7);
            result.Detail = "Cambios recientes del proyecto en GitHub.";
            return true;
        }
        if (!String.Equals(result.LocalSha, remote, StringComparison.OrdinalIgnoreCase)) return false;
        result.Online = true;
        result.Ahead = 0;
        result.Behind = 0;
        result.Status = "Al día · 0 cambios";
        result.Detail = "Tu copia coincide con la última revisión.";
        result.Url += "/commit/" + remote;
        return true;
    }

    private static CommitEntry[] ReadFeed(string repo)
    {
        string xml = GetText(repo + "/commits.atom", "application/atom+xml", false, 1024 * 1024);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 };
        XDocument feed;
        using (var text = new StringReader(xml))
        using (var reader = XmlReader.Create(text, settings)) feed = XDocument.Load(reader);
        XNamespace atom = "http://www.w3.org/2005/Atom";
        if (feed.Root == null || feed.Root.Name != atom + "feed") throw new InvalidDataException();
        return feed.Root.Elements(atom + "entry").Select(entry =>
        {
            const string prefix = "tag:github.com,2008:Grit::Commit/";
            string id = (string)entry.Element(atom + "id") ?? "";
            string sha = id.StartsWith(prefix, StringComparison.Ordinal) ? id.Substring(prefix.Length) : "";
            bool matchesRepo = entry.Elements(atom + "link").Any(link =>
                String.Equals((string)link.Attribute("href"), repo + "/commit/" + sha, StringComparison.OrdinalIgnoreCase));
            if (!GitSha.IsMatch(sha) || !matchesRepo) throw new InvalidDataException();
            return new CommitEntry { sha = sha, commit = new CommitMessage { message = (string)entry.Element(atom + "title") } };
        }).ToArray();
    }

    private static void TryPublicHistory(UpdateInfo result)
    {
        string repo = result.Url;
        try
        {
            var tips = ReadFeed(repo);
            if (SetTip(result, tips)) return;
            result.Online = true;
            result.Url = repo + "/compare/" + result.LocalSha + "..." + result.RemoteSha;
            result.Status = "Revisiones distintas";
            result.Detail = "GitHub publicó otra revisión. Todavía no se pudo verificar cuántos cambios hay entre ambas.";
            // An Atom listing alone does not establish ancestry (merges can interleave histories).
            // For nearby revisions, count only verified single-parent steps up to the installed commit.
            if (!tips.Take(5).Any(c => String.Equals(c.sha, result.LocalSha, StringComparison.OrdinalIgnoreCase))) return;
            var changes = new System.Collections.Generic.List<string>();
            string current = result.RemoteSha;
            for (int count = 1; count <= 4; count++)
            {
                var page = GetJson<CommitPage>(repo + "/commit/" + current + ".json", false);
                var route = page != null && page.payload != null ? page.payload.commitRoute : null;
                var commit = route == null ? null : route.commit;
                if (commit == null || !String.Equals(commit.oid, current, StringComparison.OrdinalIgnoreCase) ||
                    commit.parents == null || commit.parents.Length != 1 || !GitSha.IsMatch(commit.parents[0] ?? "")) return;
                var entry = tips.FirstOrDefault(c => String.Equals(c.sha, current, StringComparison.OrdinalIgnoreCase));
                if (entry != null && !String.IsNullOrWhiteSpace(entry.commit.message)) changes.Add(entry.commit.message.Trim());
                current = commit.parents[0];
                if (!String.Equals(current, result.LocalSha, StringComparison.OrdinalIgnoreCase)) continue;
                result.Ahead = count;
                result.Behind = 0;
                result.Status = count == 1 ? "1 cambio nuevo" : count + " cambios nuevos";
                result.Detail = "Cambios publicados desde tu versión instalada.";
                result.Changes = changes.Take(3).ToArray();
                return;
            }
        }
        catch (WebException ex) { if (ex.Response != null) ex.Response.Close(); }
        catch (IOException) { }
        catch (InvalidDataException) { }
        catch (XmlException) { }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }

    private static string RetryMessage(HttpWebResponse response)
    {
        long epoch;
        if (long.TryParse(response.Headers["X-RateLimit-Reset"], NumberStyles.None,
                          CultureInfo.InvariantCulture, out epoch) && epoch > 0 && epoch < 253402300800L)
        {
            var reset = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(epoch).ToLocalTime();
            return "Podés volver a consultar después de las " + reset.ToString("HH:mm") + ".";
        }
        return "GitHub pidió esperar antes de otra consulta.";
    }

    private static T GetJson<T>(string url, bool api = true)
    {
        const int maximumCharacters = 24 * 1024 * 1024;
        string body = GetText(url, api ? "application/vnd.github+json" : "application/json", api, maximumCharacters);
        return new JavaScriptSerializer { MaxJsonLength = maximumCharacters }.Deserialize<T>(body);
    }

    private static string GetText(string url, string accept, bool api, int maximumCharacters)
    {
        if (ServicePointManager.SecurityProtocol != 0)
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "GET";
        request.UserAgent = "Lanzador-GTA-VI/2.1";
        request.CachePolicy = new System.Net.Cache.HttpRequestCachePolicy(System.Net.Cache.HttpRequestCacheLevel.Revalidate);
        request.Accept = accept;
        if (api) request.Headers["X-GitHub-Api-Version"] = "2022-11-28";
        request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        request.AllowAutoRedirect = false;
        request.Timeout = 15000;
        request.ReadWriteTimeout = 15000;
        using (var response = (HttpWebResponse)request.GetResponse())
        using (var stream = response.GetResponseStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            var body = new StringBuilder();
            var chunk = new char[8192];
            int count;
            while ((count = reader.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (body.Length + count > maximumCharacters) throw new InvalidDataException();
                body.Append(chunk, 0, count);
            }
            return body.ToString();
        }
    }
}
