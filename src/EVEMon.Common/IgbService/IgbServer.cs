using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using EVEMon.Common.Constants;
using EVEMon.Common.Enumerations;
using EVEMon.Common.Enumerations.UISettings;
using EVEMon.Common.Extensions;
using EVEMon.Common.Helpers;
using EVEMon.Common.Models;
using EVEMon.Common.SettingsObjects;

namespace EVEMon.Common.IgbService
{
    /// <summary>
    /// Incomplete HTTP 1.0 web server, to serve out simple pages to
    /// the in-game browser to allow in-game interaction with EVEMon.
    /// </summary>
    public class IgbServer
    {
        private bool m_isPublic;
        private bool m_running;
        private IgbTcpListener m_listener;
        private readonly Dictionary<IgbTcpClient, byte[]> m_clients = new Dictionary<IgbTcpClient, byte[]>();


        #region Construction, Start, Stop and Reset

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="isPublic">Is publicly available</param>
        /// <param name="port">Initial TCP/IP port</param>
        public IgbServer(bool isPublic, int port)
        {
            IgbServerPort = port;
            m_isPublic = isPublic;
            CreateListener();
        }

        /// <summary>
        /// Gets the active IGB Server port.
        /// </summary>
        public int IgbServerPort { get; private set; }

        /// <summary>
        /// Re-initilize the IGB web server service.
        /// </summary>
        /// <param name="isPublic">Is publicly available</param>
        /// <param name="port">New TCP/IP port</param>
        public void Reset(bool isPublic, int port)
        {
            m_isPublic = isPublic;
            IgbServerPort = port;
            Stop();
            m_listener = null;

            CreateListener();
        }

        /// <summary>
        /// Creates the listener bound to an address and port, wires up the events.
        /// </summary>
        private void CreateListener()
        {
            m_listener = new IgbTcpListener(new IPEndPoint(AddressToBind(), IgbServerPort));
            m_listener.ClientConnected += OnClientConnected;
        }

        /// <summary>
        /// Cacluates the address to bind to.
        /// </summary>
        /// <returns>IPAddress.Any if public, IPAddress.Loopback if not public</returns>
        private IPAddress AddressToBind() => m_isPublic ? IPAddress.Any : IPAddress.Loopback;

        /// <summary>
        /// Starts the IGB Service if not running.
        /// </summary>
        public void Start()
        {
            if (m_running)
                return;

            m_running = true;
            m_listener.Start();
        }

        /// <summary>
        /// Stops the IGB Service if running.
        /// </summary>
        public void Stop()
        {
            if (!m_running)
                return;

            m_running = false;
            m_listener.Stop();
        }

        #endregion


        #region Client Event Handlers

        /// <summary>
        /// Event triggered on client connection.
        /// </summary>
        /// <param name="sender">Sending object</param>
        /// <param name="e">Argments</param>
        private void OnClientConnected(object sender, ClientConnectedEventArgs e)
        {
            IgbTcpClient cli = new IgbTcpClient(e.TcpClient);
            cli.DataRead += OnDataRead;
            cli.Closed += OnClosed;
            lock (m_clients)
            {
                m_clients.Add(cli, new byte[0]);
            }
            cli.Start();
        }

        /// <summary>
        /// Event triggered on data read.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnDataRead(object sender, IgbClientDataReadEventArgs e)
        {
            IgbTcpClient igbSender = (IgbTcpClient)sender;
            byte[] newBuf;
            lock (m_clients)
            {
                byte[] existingBuf = m_clients[igbSender];
                newBuf = new byte[existingBuf.Length + e.Count];

                Array.Copy(existingBuf, newBuf, existingBuf.Length);
                Array.Copy(e.Buffer.ToArray(), 0, newBuf, existingBuf.Length, e.Count);

                m_clients[igbSender] = newBuf;
            }

            TryProcessBuffer(igbSender, newBuf, Math.Min(e.Count + 1, newBuf.Length));
        }

        /// <summary>
        /// Event triggered on connection close read.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnClosed(object sender, EventArgs e)
        {
            lock (m_clients)
            {
                m_clients.Remove((IgbTcpClient)sender);
            }
        }

        /// <summary>
        /// Process the buffer and respond to the client.
        /// </summary>
        /// <param name="client">client to respond to</param>
        /// <param name="buffer">buffer to use</param>
        /// <param name="length">length of tail</param>
        private void TryProcessBuffer(IgbTcpClient client, byte[] buffer, int length)
        {
            // make sure the request is well formed
            if (!TailHasTwoNewLine(buffer, length))
                return;

            Dictionary<string, string> headers = new Dictionary<string, string>();

            string requestType = String.Empty;
            string requestPath = ExtractHeaders(buffer, headers, ref requestType);

            SendOutputToClient(client, headers, requestType, requestPath);
            client.Close();
        }

        /// <summary>
        /// Process request and send output to client.
        /// </summary>
        /// <param name="client">client to send output to</param>
        /// <param name="headers">dictionary of headers</param>
        /// <param name="request">the requested method</param>
        /// <param name="requestPath">url to respond to</param>
        private void SendOutputToClient(IgbTcpClient client, IDictionary<string, string> headers, string request,
            string requestPath)
        {
            MemoryStream stream = Util.GetMemoryStream();
            using (StreamWriter sw = new StreamWriter(stream))
            {
                ProcessRequest(request, requestPath, headers, sw);

                sw.Flush();
                stream.Seek(0, SeekOrigin.Begin);

                // We should support only the "GET" method
                string responseStatusCode = request.Equals(HttpMethod.Get.Method)
                    ? $"{(int)HttpStatusCode.OK} {HttpStatusCode.OK.ToString().ConvertUpperCamelCaseToString()}"
                    : $"{(int)HttpStatusCode.NotImplemented} {HttpStatusCode.NotImplemented.ToString().ConvertUpperCamelCaseToString()}";
                client.Write($"HTTP/1.1 {responseStatusCode}\n");
                client.Write("Server: EVEMon/1.0\n");
                client.Write("Content-Type: text/html; charset=utf-8\n");
                if (headers.ContainsKey("eve_trusted") &&
                    headers["eve_trusted"].ToLower(CultureConstants.InvariantCulture) == "no")
                {
                    client.Write($"eve.trustme: {BuildHostAndPort(headers["host"])}/::EVEMon needs your character information.\n");
                }

                client.Write("Connection: close\n");
                client.Write($"Content-Length: {stream.Length}\n\n");

                StreamReader sr = new StreamReader(stream);
                client.Write(sr.ReadToEnd());
            }
        }

        /// <summary>
        /// Extract the headers from the buffer and ad them to a dictionary.
        /// </summary>
        /// <param name="buffer">the buffer to extract headers from</param>
        /// <param name="headers">dictionary to add headers to</param>
        /// <param name="request">the requested method</param>
        /// <returns></returns>
        private static string ExtractHeaders(byte[] buffer, IDictionary<string, string> headers, ref string request)
        {
            string headerStr = Encoding.UTF8.GetString(buffer);

            if (headerStr.IndexOf('\r') != -1)
                headerStr = headerStr.Replace("\r", String.Empty);

            bool first = true;
            string requestPath = String.Empty;
            foreach (string tline in headerStr.Split('\n'))
            {
                if (first)
                {
                    // We should support only the "GET" method
                    Regex getMatcher = new Regex(@"^(GET) (.+) HTTP/(.*)$", RegexOptions.Compiled);
                    Match m = getMatcher.Match(tline);

                    request = m.Groups[1].Value;

                    if (m.Success)
                        requestPath = m.Groups[2].Value;

                    first = false;
                }
                else
                {
                    Regex headerMatcher = new Regex("^(.*?): (.*)$", RegexOptions.Compiled);
                    Match m = headerMatcher.Match(tline);
                    if (m.Success)
                        headers[m.Groups[1].Value.ToLower(CultureConstants.InvariantCulture)] = m.Groups[2].Value;
                }
            }
            return requestPath;
        }

        /// <summary>
        /// Checks to see if the header contains two New Lines as per HTTP specification.
        /// </summary>
        /// <param name="buffer">Buffer</param>
        /// <param name="length">The length.</param>
        /// <returns></returns>
        private static bool TailHasTwoNewLine(IList<byte> buffer, int length)
        {
            bool gotOne = false;
            for (int i = 0; i < length; i++)
            {
                if (buffer[buffer.Count - i - 1] == (byte)'\n')
                {
                    if (gotOne)
                        return true;

                    gotOne = true;
                }
                else if (buffer[buffer.Count - i - 1] != (byte)'\r')
                    gotOne = false;
            }

            return false;
        }

        /// <summary>
        /// Create the host:port string for the trustme request.
        /// </summary>
        /// <param name="host">The host header from the IGB</param>
        /// <returns>hostname:port number</returns>
        private string BuildHostAndPort(string host)
        {
            // Currently IGB returns host:port as the host header, it shouldn't
            // really do this
            string hostPort = host;

            // Add the 'http' scheme to the address if it's missing
            if (!host.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                hostPort = $"http://{hostPort}";

            // If the host string already contains a port then do nothing
            // (IGB shouldn't really do this but it is!)
            if (hostPort.Contains(":"))
                return hostPort;

            // Now cater for when/if CCP fix the IGB to not send port as part of the host header
            if (IgbServerPort != 80)
                // non-standard port - let's add it
                hostPort = $"{hostPort}:{IgbServerPort}";

            return hostPort;
        }

        /// <summary>
        /// Process the request.
        /// </summary>
        /// <param name="request">the requested method</param>
        /// <param name="requestPath">URL of the request</param>
        /// <param name="headers">dictionary of headers</param>
        /// <param name="sw">stream writer to output to</param>
        private void ProcessRequest(string request, string requestPath, IDictionary<string, string> headers, TextWriter sw)
        {
            WriteDocumentHeader(sw);

            if (!request.Equals(HttpMethod.Get.Method))
            {
                sw.WriteLine("<h1>Error loading requested URL</h1>" +
                             "The {0} method is not implemented.<br/><br/><i>Error Code: -501</i>",
                    request);
                return;
            }

            string trusted;
            if (!headers.TryGetValue("eve_trusted", out trusted))
            {
                sw.WriteLine("Please visit this site using the in-game browser.");
                return;
            }

            if (trusted.ToLower(CultureConstants.InvariantCulture) != "yes")
            {
                sw.WriteLine("The in-game browser do not trust EVEMon.<br/>");
                sw.WriteLine("<a href=\"\" onclick=\"CCPEVE.requestTrust('{0}')\">Trust EVEMon</a>.",
                    BuildHostAndPort(headers["host"]));
                return;
            }

            string characterName;
            if (!headers.TryGetValue("eve_charname", out characterName))
            {
                // No character in header
                sw.WriteLine("Not a valid CCP character.");
                return;
            }

            // Character not listed in EVEMon 
            if (EveMonClient.Characters.All(x => x.Name != characterName))
            {
                sw.WriteLine("Hello {0}, this character is not recognized by EVEMon!", characterName);
                return;
            }

            Regex contextRegex = new Regex(@"(?'context'\/characters(\/(?'charName'[^\/]*))?)?(?'request'.*)",
                RegexOptions.CultureInvariant | RegexOptions.Compiled);
            Match match = contextRegex.Match(requestPath);
            if (match.Success)
            {
                Group contextGroup = match.Groups["context"];
                if (contextGroup.Success)
                    characterName = HttpUtility.UrlDecode(match.Groups["charName"].Value);

                requestPath = match.Groups["request"].Value;
            }
            Character character = !String.IsNullOrEmpty(characterName)
                ? EveMonClient.MonitoredCharacters.FirstOrDefault(x => x.Name == characterName)
                : null;
            if (character == null)
            {
                GenerateCharacterList(characterName, sw, EveMonClient.MonitoredCharacters);
                return;
            }

            string context = $"/characters/{HttpUtility.UrlEncode(character.Name)}";

            if (requestPath.StartsWith("/plan/", StringComparison.OrdinalIgnoreCase)
                || requestPath.StartsWith("/shopping/", StringComparison.OrdinalIgnoreCase)
                || requestPath.StartsWith("/owned/", StringComparison.OrdinalIgnoreCase))
                GeneratePlanOrShoppingOutput(context, requestPath, sw, character);
            else if (requestPath.StartsWith("/skills/bytime", StringComparison.OrdinalIgnoreCase))
                GenerateSkillsByTimeOutput(context, sw, character);
            else
                GeneratePlanListOutput(context, sw, character);

            WriteDocumentFooter(sw);
        }

        /// <summary>
        /// Outputs a list of characters to a stream writer.
        /// </summary>
        /// <param name="currentCharacterName">Name of the current character.</param>
        /// <param name="sw">stream writer to output to</param>
        /// <param name="characters">list of characters to output</param>
        private static void GenerateCharacterList(string currentCharacterName, TextWriter sw, IEnumerable<Character> characters)
        {
            sw.WriteLine("<h1>Welcome!</h1>");

            sw.WriteLine("<h2>Monitored characters:</h2>");
            foreach (Character character in characters)
            {
                sw.WriteLine("<a href=\"/characters/{0}\">{1}</a>",
                    HttpUtility.UrlEncode(character.Name),
                    HttpUtility.HtmlEncode(character.Name));

                if (!String.IsNullOrEmpty(currentCharacterName) && (currentCharacterName == character.Name))
                    sw.WriteLine(" (current)");

                sw.WriteLine("<br/>");
            }
        }

        /// <summary>
        /// Outputs the document header.
        /// </summary>
        /// <param name="sw"></param>
        private static void WriteDocumentHeader(TextWriter sw)
        {
            sw.WriteLine("<html>");
            sw.WriteLine("  <head>");
            sw.WriteLine("    <style>");
            sw.WriteLine("      body { background-color: black; color: white; font-family: Arial; font-size: small; }");
            sw.WriteLine("      a:link { color: #5555FF; }");
            sw.WriteLine("      a:visited { color: #FF9900; }");
            sw.WriteLine("    </style>");
            sw.WriteLine("    <title>EVEMon</title>");
            sw.WriteLine("  </head>");
            sw.WriteLine("  <body>");
        }

        /// <summary>
        /// Outputs the documents footer.
        /// </summary>
        /// <param name="sw"></param>
        private static void WriteDocumentFooter(TextWriter sw)
        {
            sw.WriteLine("  </body>");
            sw.WriteLine("</html>");
        }

        /// <summary>
        /// Outputs a list of plans for a given character to a stream writer.
        /// </summary>
        /// <param name="context">context of the request</param>
        /// <param name="sw">stream writer to output to</param>
        /// <param name="character">character to use</param>
        private static void GeneratePlanListOutput(string context, TextWriter sw, Character character)
        {
            sw.WriteLine("<h1>Hello, {0}</h1>", HttpUtility.HtmlEncode(character.Name));
            sw.WriteLine("<a href=\"/characters\">List all characters</a><hr/>");

            sw.WriteLine("<h2>Your plans:</h2>");
            foreach (Plan plan in character.Plans)
            {
                sw.WriteLine("<a href=\"{0}/plan/{1}\">{2}</a> (<a href=\"{0}/shopping/{1}\">shopping list</a>)<br/>",
                    context,
                    HttpUtility.UrlEncode(plan.Name),
                    HttpUtility.HtmlEncode(plan.Name));
            }

            sw.WriteLine("<h2>Your skills:</h2>");
            sw.WriteLine("<a href=\"{0}/skills/bytime\">By training time</a><br/>", context);

            sw.WriteLine("<hr/><a href=\"/characters\">List all characters</a>");
        }

        /// <summary>
        /// Outputs a list of skills for a given character ordered by time to a stream writer.
        /// </summary>
        /// <param name="context">context of the request</param>
        /// <param name="sw">stream writer to output to</param>
        /// <param name="character">character to use</param>
        private static void GenerateSkillsByTimeOutput(string context, TextWriter sw, Character character)
        {
            sw.WriteLine("<h1>Hello, {0}</h1>", HttpUtility.HtmlEncode(character.Name));
            sw.WriteLine("<a href=\"/characters\">List all characters</a><hr/>");
            sw.WriteLine("<a href=\"{0}\">Character overview</a>", context);

            sw.WriteLine("<h2>Your skills by training time:</h2>");

            IEnumerable<Skill> allskills = character.Skills.Where(x => x.IsPublic && x.Level < 5 && x.Level > 0);
            allskills = allskills.OrderBy(x => x.GetLeftTrainingTimeToNextLevel);

            sw.WriteLine("<table>");
            sw.Write("<tr><td colspan=\"2\" width=\"265\"><b>Skill</b></td>" +
                     "<td width=\"100\"><b>Next Level</b></td><td><b>Training Time</b></td></tr>");

            int index = 0;
            foreach (Skill s in allskills)
            {
                index++;
                sw.Write("<tr>");

                sw.Write("<td width=\"15\">");
                sw.Write("<b>{0}.</b>", index);
                sw.Write("</td>");

                sw.Write("<td width=\"300\">");
                sw.Write("<b><a href=\"\" onclick=\"CCPEVE.showInfo({0})\">{1}</a></b>", s.ID, s.Name);
                sw.Write("</td>");

                sw.Write("<td width=\"100\">");
                sw.Write("<b>{0} -&gt; {1}</b>", s.RomanLevel, Skill.GetRomanFromInt(s.Level + 1));
                sw.Write("</td>");

                sw.Write("<td>");
                sw.Write(s.GetLeftTrainingTimeToNextLevel.ToDescriptiveText(
                    DescriptiveTextOptions.FullText |
                    DescriptiveTextOptions.IncludeCommas |
                    DescriptiveTextOptions.SpaceText));
                sw.Write("</td>");
                sw.Write("</tr>");
            }
            sw.WriteLine("</table>");

            sw.WriteLine("<br/><a href=\"{0}\">Character overview</a>", context);
            sw.WriteLine("<hr/><a href=\"/characters\">List all characters</a>");
        }

        /// <summary>
        /// Outputs a plan or shopping list for a given character to a stream writer.
        /// </summary>
        /// <param name="context">context of the request</param>
        /// <param name="requestPath">url of the request</param>
        /// <param name="sw">stream writer to output to</param>
        /// <param name="character">character to use</param>
        private static void GeneratePlanOrShoppingOutput(string context, string requestPath, TextWriter sw, Character character)
        {
            sw.WriteLine("<h1>Hello, {0}</h1>", HttpUtility.HtmlEncode(character.Name));
            sw.WriteLine("<a href=\"/characters\">List all characters</a><hr/>");
            sw.WriteLine("<a href=\"{0}\">Character overview</a>", context);

            Regex regex =
                new Regex(
                    @"\/(owned\/(?'skillId'[^\/]+)\/(?'markOwned'[^\/]+)\/)?(?'requestType'shopping|plan)\/(?'planName'[^\/]+)(.*)",
                    RegexOptions.CultureInvariant | RegexOptions.Compiled);
            Match match = regex.Match(requestPath);

            if (match.Success)
            {
                string requestType = match.Groups["requestType"].Value;
                bool shopping = requestType.Equals("shopping", StringComparison.OrdinalIgnoreCase);
                string planName = HttpUtility.UrlDecode(match.Groups["planName"].Value);

                int skillId;
                bool setAsOwned;
                if (match.Groups["skillId"].Success &&
                    match.Groups["markOwned"].Success &&
                    Int32.TryParse(match.Groups["skillId"].Value, out skillId) &&
                    Boolean.TryParse(match.Groups["markOwned"].Value, out setAsOwned))
                {
                    Skill skill = character.Skills.FirstOrDefault(x => x.ID == skillId);
                    if (skill != null)
                    {
                        sw.WriteLine("<h2>Skillbook shopping result</h2>");
                        skill.IsOwned = setAsOwned;
                        sw.WriteLine("<a href=\"\" onclick=\"CCPEVE.showInfo({0})\">{1}</a> is now marked as {2} owned.", skill.ID,
                            HttpUtility.HtmlEncode(skill.Name), skill.IsOwned ? String.Empty : "not");
                    }
                    else
                    {
                        // Display an error message
                        sw.WriteLine("<h2>Error Message</h2>");
                        sw.WriteLine("Skill with id '{0}' could not be found", skillId);
                    }
                    sw.WriteLine("<hr/>");
                }

                Plan plan = character.Plans[planName];
                if (plan == null)
                {
                    // Display an error message
                    sw.WriteLine("<h2>Error Message</h2>");
                    sw.WriteLine("A plan named \"{0}\" does not exist.", HttpUtility.HtmlEncode(planName));
                }
                else
                {
                    sw.WriteLine("<h2>Plan: {0}</h2>", HttpUtility.HtmlEncode(plan.Name));

                    PlanExportSettings x = new PlanExportSettings
                    {
                        // Only if not shopping
                        EntryTrainingTimes = !shopping,
                        // Only if not shopping
                        EntryStartDate = !shopping,
                        // Only if not shopping
                        EntryFinishDate = !shopping,
                        // Only if not shopping
                        FooterTotalTime = !shopping,
                        // Only if not shopping
                        FooterDate = !shopping,
                        FooterCount = true,
                        ShoppingList = shopping,
                        EntryCost = true,
                        FooterCost = true,
                        Markup = MarkupType.Html
                    };

                    sw.Write(PlanIOHelper.ExportAsText(plan, x, ExportActions(context, requestType, plan)));
                }
            }
            else
            {
                sw.WriteLine("<h2>Error Message</h2>");
                sw.WriteLine("Invalid request");
            }

            sw.WriteLine("<br/><br/><a href=\"{0}\">Character overview</a>", context);
            sw.WriteLine("<hr/><a href=\"/characters\">List all characters</a>");
        }

        /// <summary>
        /// Exports the actions.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="requestType">Type of the request.</param>
        /// <param name="plan">The plan.</param>
        /// <returns></returns>
        private static Action<StringBuilder, PlanEntry, PlanExportSettings> ExportActions(string context, string requestType,
            Plan plan)
            => (builder, entry, settings) =>
            {
                if (settings.Markup != MarkupType.Html)
                    return;

                // Skill is known
                if (entry.CharacterSkill.IsKnown || entry.Level != 1)
                    return;

                builder.Append($" <a href='{context}/owned/{entry.Skill.ID}/" +
                               $"{!entry.CharacterSkill.IsOwned}/{requestType}/" +
                               $"{HttpUtility.HtmlEncode(plan.Name)}'>" +
                               $"{HttpUtility.HtmlEncode(!entry.CharacterSkill.IsOwned ? "Mark as owned" : "Mark as not owned")}</a>");
            };

        #endregion
    }
}