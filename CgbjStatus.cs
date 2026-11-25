using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static class CgbjStatus
{
    internal const string Unavailable = "Consulta no disponible";
    internal const string RepositoryAnnounced = "Repositorio anunciado en PortWatch";
    private const string TrackerUrl = "https://portwatch-vi.xyz/data/tracker.json";
    private const int MaximumCharacters = 2 * 1024 * 1024;

    internal static async Task<string> CheckAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                if (ServicePointManager.SecurityProtocol != 0)
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var request = (HttpWebRequest)WebRequest.Create(TrackerUrl);
                request.UserAgent = "Leonida-Launcher/1.0";
                request.Accept = "application/json";
                request.AllowAutoRedirect = false;
                request.Timeout = 15000;
                request.ReadWriteTimeout = 15000;
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode != HttpStatusCode.OK) return Unavailable;
                    using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    {
                        var body = new StringBuilder();
                        var buffer = new char[8192];
                        int count;
                        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (body.Length + count > MaximumCharacters) return Unavailable;
                            body.Append(buffer, 0, count);
                        }
                        return ReadStatus(body.ToString());
                    }
                }
            }
            catch (WebException ex)
            {
                if (ex.Response != null) ex.Response.Close();
                return Unavailable;
            }
            catch (IOException) { return Unavailable; }
        }).ConfigureAwait(false);
    }

    internal static string ReadStatus(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumCharacters) return Unavailable;
        try
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = MaximumCharacters };
            var tracker = serializer.DeserializeObject(json) as Dictionary<string, object>;
            object efforts;
            if (tracker == null || !tracker.TryGetValue("efforts", out efforts) || !(efforts is object[]))
                return Unavailable;

            string status = null;
            foreach (object entry in (object[])efforts)
            {
                var project = entry as Dictionary<string, object>;
                object id, repo;
                if (project == null || !project.TryGetValue("id", out id) || !object.Equals(id, "cgbj"))
                    continue;
                if (status != null || !project.TryGetValue("repo", out repo)) return Unavailable;
                if (repo == null) status = "Sin repositorio público";
                else if (repo is string && Regex.IsMatch((string)repo,
                    @"\A(?:https://github\.com/)?[A-Za-z0-9][A-Za-z0-9-]{0,38}/(?!\.{1,2}\z)[A-Za-z0-9_.-]{1,100}\z"))
                    status = RepositoryAnnounced;
                else return Unavailable;
            }
            return status ?? Unavailable;
        }
        catch (ArgumentException) { return Unavailable; }
        catch (InvalidOperationException) { return Unavailable; }
    }
}
