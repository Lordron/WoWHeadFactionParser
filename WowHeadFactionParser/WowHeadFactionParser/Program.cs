using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WowHeadFactionParser
{
    class Program
    {
        private static uint s_Total = 0;
        private static uint s_Processed = 0;
        private static uint s_Failure = 0;

        private static Thread s_Thread;
        private static StreamWriter s_Writer;

        private static Regex s_Regex;

        static void Main(string[] args)
        {
            if (args == null || args.Length != 2)
            {
                Console.WriteLine("Incorrect usage!\nExample: WowHeadFactionParser.exe min max");
                return;
            }

            uint start;
            if (!uint.TryParse(args[0], out start))
            {
                Console.WriteLine("Enter valide start value");
                return;
            }

            uint end;
            if (!uint.TryParse(args[1], out end))
            {
                Console.WriteLine("Enter valide end value");
                return;
            }

            if (end < start)
            {
                Console.WriteLine("End value {0} should be higher that start value {1}", end, start);
                return;
            }

            s_Total = end - start + 1;

            s_Writer = new StreamWriter("out.txt");
            s_Writer.AutoFlush = true;

            s_Regex = new Regex("<div><span>(.*?)</span>[^}]*faction=(\\d+)");

            s_Thread = new Thread(x =>
            {
                using (Worker worker = new Worker("npc={0}", 100000, 100))
                {
                    worker.OnGetResponse += OnGetResponseHandler;
                    worker.OnFinished += OnFinishedHandler;
                    worker.Start(start, end);
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
                MatchCollection matches = s_Regex.Matches(page);
                if (matches.Count > 0)
                {
                    s_Writer.WriteLine("INSERT INTO `creature_onkill_reputation` (creature_id, RewOnKillRepFaction1, RewOnKillRepValue1, MaxStanding1, RewOnKillRepFaction2, RewOnKillRepValue2, MaxStanding2) VALUES ({0}, {1}, {2}, '0', '0', '0', '0');", id, matches[0].Groups[2].Value, matches[0].Groups[1].Value);
                }
            }

            UpdateProgress();
        }

        static void OnFinishedHandler()
        {
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
            m_address = new Uri("http://wowhead.com/");
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

        public void Start(uint start, uint end)
        {
            m_timeStart = DateTime.Now;

            for (uint entry = start; entry <= end; ++entry)
            {
                if (!Process(entry))
                    break;
            }

            while (m_semaphore.CurrentCount != 100)
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

            while (m_semaphore.CurrentCount != 100)
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
