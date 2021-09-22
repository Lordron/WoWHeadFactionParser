using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WowHeadFactionParser
{
    [DataContract]
    public class Quest
    {
        [DataMember(Name = "category", IsRequired = true, Order = 0)]
        public int Category;

        [DataMember(Name = "category2", IsRequired = true, Order = 1)]
        public int Category2;

        [DataMember(Name = "id", IsRequired = true, Order = 2)]
        public int ID;

        [DataMember(Name = "name", IsRequired = true, Order = 3)]
        public string Name;
    }

    class Program
    {
        private static uint s_Total = 0;
        private static uint s_Processed = 0;
        private static uint s_Failure = 0;

        private static Thread s_Thread;
        private static StreamWriter s_Writer;

        private static Regex s_Regex = new Regex(@"new Listview\({template: 'quest', id: 'objective-of', name: .+, data: (?<quests>.+)}\)");

        public static HashSet<uint> Npcs = new HashSet<uint>();

        public static Dictionary<uint, Quest[]> Quests = new Dictionary<uint, Quest[]>();

        static void Main(string[] args)
        {
            uint[] ids = new uint[] {62818};

            foreach (uint i in ids)
                Npcs.Add(i);

            Console.WriteLine("Loaded {0} ids", Npcs.Count);

            s_Total = (uint)Npcs.Count;

            s_Writer = new StreamWriter("out2.sql");
            s_Writer.AutoFlush = true;

            s_Thread = new Thread(x =>
            {
                using (Worker worker = new Worker("npc={0}", 100000, 3))
                {
                    worker.OnGetResponse += OnGetResponseHandler;
                    worker.OnFinished += OnFinishedHandler;
                    worker.Start();
                }
            });

            s_Thread.Start();
        }

        static void OnGetResponseHandler(string page, uint id)
        {
            ++s_Processed;
            if (string.IsNullOrEmpty(page))
                ++s_Failure;
            else
            {
                var idx = page.IndexOf("bpet-calc-qualities");
                Match match = s_Regex.Match(page);
                if (match.Success)
                {
                    string s = match.Groups[1].Value;
                    using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(s)))
                    {
                        DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(Quest[]));
                        Quest[] result = (Quest[])serializer.ReadObject(stream);

                        Quests.Add(id, result);
                        //foreach (Quest q in result)
                        {
/*DELETE FROM `smart_scripts` WHERE `entryorguid` = 64330 AND `source_type` = 0;
INSERT INTO `smart_scripts` VALUES
(64330, 0, 0, 0, 64, 0, 100, 0, 0, 0, 0, 0, 0, 98, 14228, 14228, 0, 0, 0, 0, 7, 0, 0, 0, 0, 0, 0, 0, 'On Hello - Send pet battle gossip'),
(64330, 0, 1, 0, 62, 0, 100, 0, 14228, 0, 0, 0, 0, 139, 64330, 0, 0, 0, 0, 0, 7, 0, 0, 0, 0, 0, 0, 0, 'On Select - Start pet battle');

UPDATE `creature_template` SET `AIName` = 'SmartAI' WHERE `entry` = 64330;*/
                            //s_Writer.WriteLine("Npc {0}, Quest {1} - {2} ({3}, {4})", id, q.ID, q.Name, q.Category, q.Category2);
                        }
                    }
                }
            }

            UpdateProgress();
        }

        static void OnFinishedHandler()
        {
            ///< Dump gossips

            List<uint> npcTextIDs = new List<uint>();
            List<uint> menuIDS = new List<uint>();
            foreach (var kvp in Quests)
            {
                npcTextIDs.Add(kvp.Key * 10);
                menuIDS.Add(kvp.Key * 10);
            }

            s_Writer.WriteLine("DELETE FROM `npc_text` WHERE `ID` IN ({0});", string.Join(", ", npcTextIDs.ToArray()));
            s_Writer.WriteLine("INSERT INTO `npc_text` (`ID`, `Probability0`, `BroadcastTextID0`, `VerifiedBuild`) VALUES");
            foreach (uint npcId in npcTextIDs)
            {
                s_Writer.WriteLine("({0}, 1, 62447, -1), -- Custom npc_text record for {1}", npcId, npcId / 10);
            }

            s_Writer.WriteLine("DELETE FROM `gossip_menu` WHERE `entry` IN ({0});", string.Join(", ", menuIDS.ToArray()));
            s_Writer.WriteLine("INSERT INTO `gossip_menu` VALUES");
            foreach (uint menuId in menuIDS)
            {
                s_Writer.WriteLine("({0}, {0}), -- Custom gossip_menu record for {1}", menuId, menuId / 10);
            }

            s_Writer.WriteLine("DELETE FROM `gossip_menu_option` WHERE `menu_id` IN ({0});", string.Join(", ", menuIDS.ToArray()));
            s_Writer.WriteLine("INSERT INTO `gossip_menu_option` VALUES");
            foreach (uint menuId in menuIDS)
            {
                s_Writer.WriteLine("({0}, 0, 0, '', 62660, 1, 3, 0, 0, 0, 0, '', 62661), -- Custom gossip_menu_option record for {1}", menuId, menuId / 10);
            }

            s_Writer.WriteLine("DELETE FROM `smart_scripts` WHERE `source_type` = 0 AND `entryorguid` IN ({0});", string.Join(", ", Quests.Keys.ToArray()));

            s_Writer.WriteLine("INSERT INTO `smart_scripts` VALUES");
            foreach (var kvp in Quests)
            {
                s_Writer.WriteLine("({0}, 0, 0, 0, 62, 0, 100, 0, {1}, 0, 0, 0, 0, 139, {0}, 0, 0, 0, 0, 0, 7, 0, 0, 0, 0, 0, 0, 0, 'On Select - Start pet battle'), -- TODO: sniff gossip, this is just a replacement", kvp.Key, kvp.Key * 10);
            }

            foreach (var kvp in Quests)
            {
                s_Writer.WriteLine("UPDATE `creature_template` SET `npcflag` = `npcflag` | 1, `gossip_menu_id` = {0}, `AIName` = 'SmartAI' WHERE `entry` = {1};", kvp.Key * 10, kvp.Key);
            }

            //foreach (var kvp in Quests)
            s_Writer.WriteLine("DELETE FROM `conditions` WHERE `SourceTypeOrReferenceId` = 14 AND `SourceGroup` IN ({0});", string.Join(", ", npcTextIDs.ToArray()));
            s_Writer.WriteLine("INSERT INTO `conditions` (`SourceTypeOrReferenceId`, `SourceGroup`, `SourceEntry`, `SourceId`, `ElseGroup`, `ConditionTypeOrReference`, `ConditionTarget`, `ConditionValue1`, `ConditionValue2`, `ConditionValue3`, `NegativeCondition`, `ErrorType`, `ErrorTextId`, `ScriptName`, `Comment`) VALUES");

            foreach (var kvp in Quests)
            {
                int elseGroup = 0;
                foreach (Quest q in kvp.Value)
                {
                    s_Writer.WriteLine("(15, {0}, {0}, 0, {1}, 47, 0, {2}, 8, 0, 0, 0, 0, '', 'Quest {2} - {3} - Show gossip only if quest is taken'),", kvp.Key * 10, elseGroup++, q.ID, q.Name);
                }

            }
                //s_Writer.WriteLine("Npc {0}, Quest {1} - {2} ({3}, {4})", id, q.ID, q.Name, q.Category, q.Category2);

            s_Thread.Abort();
            s_Writer.Flush();
            s_Writer.Close();
        }

        static void UpdateProgress()
        {
            Console.Title = string.Format("Progress: {0}%", Math.Truncate(((float)s_Processed / (float)s_Total) * 100.0f));
        }
    }

    public class Worker : IDisposable
    {
        public delegate void OnGetResponseEvent(string page, uint id);
        public delegate void OnFinishedEvent();

        public OnGetResponseEvent OnGetResponse;
        public OnFinishedEvent OnFinished;

        private Queue<uint> m_badIds;

        private ServicePoint m_service;
        private SemaphoreSlim m_semaphore;
        private Uri m_address;
        private string m_relative;

        private DateTime m_timeStart;
        private DateTime m_timeEnd;

        private object m_locker = new object();

        public Worker(string relative, int timeout, int connections)
        {
            m_relative = relative;
            m_address = new Uri("http://en.wowhead.com/");
            m_badIds = new Queue<uint>();

            ServicePointManager.DefaultConnectionLimit = connections * 10;
            {
                m_service = ServicePointManager.FindServicePoint(m_address);
                m_service.SetTcpKeepAlive(true, timeout, timeout);
            }

            m_semaphore = new SemaphoreSlim(connections, connections);
        }

        public bool Process(uint id)
        {
            m_semaphore.Wait();

            Requests request = new Requests(m_address, m_relative, id);
            request.BeginGetResponse(RespCallback, request);
            return true;
        }

        public void Start()
        {
            m_timeStart = DateTime.Now;

            foreach (uint entry in Program.Npcs)
            {
                if (!Process(entry))
                    break;

                Thread.Sleep(250);
            }

            while (m_semaphore.CurrentCount != 3)
            {
                Thread.Sleep(1);
            }

            while (m_badIds.Count > 0)
            {
                uint id = m_badIds.Dequeue();
                if (id == 0)
                    continue;

                if (!Process(id))
                    break;
            }

            while (m_semaphore.CurrentCount != 3)
            {
                Thread.Sleep(1);
            }

            m_timeEnd = DateTime.Now;
        }

        private void RespCallback(IAsyncResult iar)
        {
            Requests request = (Requests)iar.AsyncState;

            string page;
            bool success = request.EndGetResponse(iar, out page);

            lock (m_locker)
            {
                if (success)
                {
                    if (OnGetResponse != null)
                        OnGetResponse(page, request.Id);
                }
                else
                {
                    if (OnGetResponse != null)
                        OnGetResponse(null, 0);

                    m_badIds.Enqueue(request.Id);
                }
            }

            request.Dispose();
            m_semaphore.Release();
        }

        public void Dispose()
        {
            m_semaphore.Dispose();
            m_service.ConnectionLeaseTimeout = 0;

            Console.WriteLine("Parser take {0} to process", m_timeEnd - m_timeStart);

            if (OnFinished != null)
                OnFinished();
        }
    }

    public class Requests : IDisposable
    {
        public uint Id;
        public static bool Compress;

        private Uri m_address;
        private HttpWebRequest m_request;
        private HttpWebResponse m_response;

        public Requests(Uri address, string relative, uint id)
        {
            Id = id;

            m_address = new Uri(address, string.Format(relative, id));
            m_request = (HttpWebRequest)WebRequest.Create(m_address);
            m_request.UserAgent = @"Mozilla/5.0 (Windows NT 6.1; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/41.0.2272.118 Safari/537.36";
            m_request.KeepAlive = true;
            if (Compress)
                m_request.AutomaticDecompression = DecompressionMethods.Deflate | DecompressionMethods.GZip;
        }

        public IAsyncResult BeginGetResponse(AsyncCallback callback, object state)
        {
            return m_request.BeginGetResponse(callback, state);
        }

        public bool EndGetResponse(IAsyncResult asyncResult, out string page)
        {
            try
            {
                m_response = (HttpWebResponse)m_request.EndGetResponse(asyncResult);
                page = ParseToString();
                return true;
            }
            catch
            {
                page = string.Empty;
                return false;
            }
        }

        public void Dispose()
        {
            if (m_request != null)
                m_request.Abort();
            if (m_response != null)
                m_response.Close();
        }

        public string ParseToString()
        {
            if (m_response == null)
                return string.Empty;

            using (BufferedStream buffer = new BufferedStream(m_response.GetResponseStream()))
            using (StreamReader reader = new StreamReader(buffer))
            {
                return reader.ReadToEnd();
            }
        }
    }
}
