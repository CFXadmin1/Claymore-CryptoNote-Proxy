using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace stratum_proxy
{
    class Program
    {
        // List with the DevFee ports used to identify the shares
        static List<int> DevFeeList = new List<int>();

        // Lists with shares count & errors
        static ShareCounter TotalShares = new ShareCounter();

        // Proxy configuration
        static ProxyConfig StratumProxy;

        /// <summary>
        /// Main method.
        /// </summary>
        static void Main()
        {
            StratumProxy = new ProxyConfig();

            if (!File.Exists("proxy.txt"))
            {
                // Guide
                int steps = 5;
                for (int i = 0; i < steps + 1; i++)
                {
                    string value;
                    switch (i)
                    {
                        case 0:
                            Console.Clear();
                            Console.Write($"Enter a local host server name/IP ({StratumProxy.LocalHost}): ");
                            value = Console.ReadLine().Trim();
                            if (!string.IsNullOrEmpty(value))
                                StratumProxy.LocalHost = value;
                            break;
                        case 1:
                            Console.Write($"Enter the pool address/IP ({StratumProxy.PoolAddress}): ");
                            value = Console.ReadLine().Trim();
                            if (!string.IsNullOrEmpty(value))
                                StratumProxy.PoolAddress = value;
                            break;
                        case 2:
                            Console.Write("Enter your wallet address: ");
                            StratumProxy.Wallet = Console.ReadLine().Trim();
                            break;
                        case 3:
                            Console.Write($"Enter a worker name ({StratumProxy.Worker}): ");
                            value = Console.ReadLine().Trim();
                            if (!string.IsNullOrEmpty(value))
                                StratumProxy.Worker = value;
                            break;
                        case 4:
                            Console.Write($"Use SSL/TLS? (Only connection to the pool)({StratumProxy.IsSSL}) (y/n): ");
                            bool ssl = Console.Read() == 'y';
                            if (StratumProxy.IsSSL != ssl)
                                StratumProxy.IsSSL = ssl;
                            break;
                        case 5:
                            Console.Write(Environment.NewLine);
                            Console.WriteLine("Final config: ");
                            Console.WriteLine(StratumProxy.ToString());
                            Console.Write("Is it ok? (y/n): ");
                            bool configDone = Console.ReadKey().KeyChar == 'y';
                            if (configDone)
                            {
                                StratumProxy.Create();
                                i = 6;
                                Main();
                            }
                            else
                            {
                                i = -1;
                                // Clear input buffer
                                while (Console.In.Peek() != -1)
                                    Console.In.Read();
                            }
                            break;
                    }
                }
                return;
            }

            // Header
            Console.Write('\n');
            Console.WriteLine("╔═════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║            Claymore CryptoNote Stratum Proxy  v1.3.1            ║");
            Console.WriteLine("╚═════════════════════════════════════════════════════════════════╝");
            
            if(StratumProxy.Load())
            {
                Console.WriteLine("Config loaded!");
            }
            else
            {
                Console.WriteLine("Bad configuration file!");
                Console.Read();
                return;
            }

            // SSL/TLS
            if (StratumProxy.IsSSL)
                WriteLineColor(ConsoleColor.DarkMagenta, "Secure Socket Layer is enabled");

            // Print proxy configuration
            WriteLineColor(ConsoleColor.DarkCyan, $"Local server is {StratumProxy.LocalHost}");
            WriteLineColor(ConsoleColor.DarkCyan, $"Main pool is {StratumProxy.PoolAddress}");

            // Check if there is a PaymentID in the wallet
            string[] walletParts = StratumProxy.Wallet.Split('.');
            WriteLineColor(ConsoleColor.DarkCyan, $"Wallet is {walletParts[0]}");
            if (walletParts.Length > 1)
                WriteLineColor(ConsoleColor.DarkCyan, $"  PaymentID is {walletParts[1]}");

            if (!string.IsNullOrEmpty(StratumProxy.Worker))
            {
                WriteLineColor(ConsoleColor.DarkCyan, $"  Worker is {StratumProxy.Worker}");

                // Check pool separator
                string[] poolsDot = { "nanopool.org", "monerohash.com", "minexmr.com", "dwarfpool.com" };
                string[] poolsPlus = { "xmrpool.eu" };
                if (poolsDot.Any(x => StratumProxy.PoolAddress.Contains(x)))
                    StratumProxy.Worker = "." + StratumProxy.Worker;
                else if (poolsPlus.Any(x => StratumProxy.PoolAddress.Contains(x)))
                    StratumProxy.Worker = "+" + StratumProxy.Worker;
                else
                    WriteLineColor(ConsoleColor.DarkGray, "Pool worker separator not detected! Put it yourself in front of the worker name if it " +
                        "not has it. Check your pool details if you do not know. Skipping...\n");
            }

            // Info
            Console.WriteLine("Based on https://github.com/JuicyPasta/Claymore-No-Fee-Proxy by JuicyPasta & Drdada");
            Console.WriteLine("As Claymore v9.7 beta, the DevFee logins at the start and takes some hashes all the time,\n" +
                "like 1 hash every 39 of yours (there is not connection/disconections for devfee like in ETH).\n " +
                "This proxy tries to replace the wallet in every login detected that is not your wallet.\n");
            WriteLineColor(ConsoleColor.DarkYellow, "Indentified DevFee shares are printed in yellow");
            WriteLineColor(ConsoleColor.DarkGreen, "Your shares are printed in green\n");

            if (StratumProxy.IsSSL)
                Console.WriteLine("Do not connect using SSL/TLS in the miners! Only connection to the remote pool is using SSL/TLS.\n");

            Console.WriteLine("Press \"s\" for current statistics");

            // Start keyboard input thread
            Thread kbdThread = new Thread(() => KeyboardListener());
            kbdThread.Start();

            // Listening loop
            while (true)
            {
                try
                {
                    ServerLoop();
                }
                catch
                {
                    WriteLineColor(ConsoleColor.DarkRed, "Socket connection dropped! Retrying in 10 s...");
                    Thread.Sleep(10000);
                }
            }
        }

        /// <summary>
        /// Accepts and controls connections from the localhost to the remote pool and vice versa.
        /// </summary>
        static void ServerLoop()
        {
            TcpListener server;
            try
            {
                Console.WriteLine("Initializing socket...");
                server = new TcpListener(StratumProxy.LocalHostEndPoint);
                server.Start();
            }
            catch
            {
                WriteLineColor(ConsoleColor.DarkRed, $"Failed to listen on {StratumProxy.LocalHostEndPoint} ({StratumProxy.LocalHost})");
                WriteLineColor(ConsoleColor.DarkRed, "  Check for other listening sockets or correct permissions");
                return;
            }

            Console.WriteLine("Waiting for connections...");
            while (true)
            {
                if (server.Pending())
                {
                    TcpClient clientSocket = server.AcceptTcpClient();

                    // Start a new thread to talk to the remote pool
                    Thread thread = new Thread(() => ProxyHandler(clientSocket));
                    thread.Start();
                }

                // Reduce CPU usage
                Thread.Sleep(20);
            }
        }

        /// <summary>
        /// Initializes a new thread for the specified port.
        /// </summary>
        /// <param name="clientSocket">Localhost client socket.</param>
        static void ProxyHandler(TcpClient clientSocket)
        {
            IPEndPoint clientEndPoint = (IPEndPoint)clientSocket.Client.RemoteEndPoint;

            Console.WriteLine($"New connection received from {clientEndPoint}");

            Socket remoteSocket = null;
            TcpClient remoteClient = null;
            SslStream remoteSslStream = null;

            // Try to connect to the remote pool
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    if (StratumProxy.IsSSL)
                    {
                        string poolHostName = StratumProxy.PoolAddress.Split(':')[0];
                        remoteClient = new TcpClient(poolHostName, StratumProxy.PoolEndPoint.Port);
                        remoteSslStream = new SslStream(remoteClient.GetStream(), false,
                            new RemoteCertificateValidationCallback(ValidateServerCertificate), null);
                        remoteSslStream.AuthenticateAsClient(poolHostName);
                    }
                    else
                    {
                        remoteSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        remoteSocket.Connect(StratumProxy.PoolEndPoint);
                    }
                    break; // Connection OK
                }
                catch(Exception e)
                {
                    WriteLineColor(ConsoleColor.DarkRed, $"{GetNow()} - Connection lost with {StratumProxy.PoolEndPoint}. Retry: {i}/3");
                    WriteLineColor(ConsoleColor.DarkRed, $"  Exception: {e.Message}");
                    Thread.Sleep(1000);
                    if (i == 3)
                    {
                        remoteSocket = null;
                        remoteSslStream = null;
                    }
                }
            }

            if ((remoteSocket == null && !StratumProxy.IsSSL) || (remoteSslStream == null && StratumProxy.IsSSL))
            {
                WriteLineColor(ConsoleColor.DarkRed, $"{GetNow()} - Could not connect to the pool.");
                clientSocket.Close();
                return; // Exit thread
            }

            // Main loop
            while (true)
            {
                // Read packet from the local client
                string request = ReceiveFrom(clientSocket.Client);

                // Last request method
                string lastRequestMethod = string.Empty;

                if (request.Length > 0)
                {
                    // Parse the request method
                    int indexMethod = request.IndexOf("method");
                    if (indexMethod > 0)
                        lastRequestMethod = request.
                            Substring(indexMethod + 7, (request.IndexOf(",", indexMethod)) - (indexMethod + 7)).Trim();

                    // Modify the wallet
                    request = RequestEdit(request, clientEndPoint);

                    try
                    {
                        // Send the modified packet to the remote pool
                        if (StratumProxy.IsSSL)
                        {
                            remoteSslStream.Write(Encoding.UTF8.GetBytes(request));
                            remoteSslStream.Flush();
                        }
                        else
                            remoteSocket.Send(Encoding.UTF8.GetBytes(request));
                    }
                    catch (SocketException se)
                    {
                        WriteLineColor(ConsoleColor.DarkRed, $"{GetNow()} - Send to pool failed. {clientEndPoint}: {request}");
                        WriteLineColor(ConsoleColor.DarkRed, $"  Socket error({se.ErrorCode}): {se.Message}");
                        WriteLineColor(ConsoleColor.DarkRed, $"  Connection with pool lost. Claymore should reconnect...");
                        clientSocket.Close();
                        break; // Main loop
                    }
                    catch(Exception ex)
                    {
                        WriteLineColor(ConsoleColor.DarkRed, $"{GetNow()} - Send to pool failed. {clientEndPoint}: {request}");
                        WriteLineColor(ConsoleColor.DarkRed, $"  Generic Exception: {ex}");
                        WriteLineColor(ConsoleColor.DarkRed, $"  Connection with pool lost. Claymore should reconnect...");
                        clientSocket.Close();
                        break; // Main loop
                    }
                }

                // Read packet from the remote pool
                string poolResponse = string.Empty;

                // If there was a request before, wait for pool response or if data is available
                if (lastRequestMethod.Length > 0 || remoteClient.Available > 0)
                    poolResponse = StratumProxy.IsSSL ? ReceiveFromSSL(remoteSslStream) : ReceiveFrom(remoteSocket);

                if (poolResponse.Length > 0)
                {
                    try
                    {
                        // Send the response to the local client (miner)
                        clientSocket.Client.Send(Encoding.UTF8.GetBytes(poolResponse));
                    }
                    catch(SocketException se)
                    {
                        WriteLineColor(ConsoleColor.DarkRed, $"{GetNow()} - Disconnected! ({clientEndPoint}) Mining stopped?");
                        WriteLineColor(ConsoleColor.DarkRed, $"  Socket error({se.ErrorCode}): {se.Message}");
                        clientSocket.Close();
                        break; // Main loop
                    }
                    catch (Exception ex)
                    {
                        WriteLineColor(ConsoleColor.DarkRed, $"{GetNow()} - Send to pool failed. {clientEndPoint}: {request}");
                        WriteLineColor(ConsoleColor.DarkRed, $"  Generic Exception: {ex}");
                        WriteLineColor(ConsoleColor.DarkRed, $"  Connection with pool lost. Claymore should reconnect...");
                        clientSocket.Close();
                        break; // Main loop
                    }

                    // Just in case, it can happen
                    try
                    {
                        // Log accepted, DevFee, rejected shares and others
                        LogResponse(poolResponse, clientEndPoint, lastRequestMethod);
                    }
                    catch
                    {
                        WriteLineColor(ConsoleColor.DarkGray, $"{GetNow()} - Error logging status. Nothing to worry about.");
                    }
                }

                // Reduce CPU usage
                Thread.Sleep(10);
            }

            // Delete this port from DevFee ports list
            if (IsDevFee(clientEndPoint.Port))
                DevFeeList.Remove(clientEndPoint.Port);
        }

        /// <summary>
        /// Process the pending received data from the socket.
        /// </summary>
        /// <param name="socket">Socket to read.</param>
        /// <returns>String with the received data.</returns>
        static string ReceiveFrom(Socket socket)
        {
            try
            {
                byte[] buffer = new byte[2048];
                string str = string.Empty;
                while (socket.Available > 0)
                {
                    int data = socket.Receive(buffer);
                    str += Encoding.UTF8.GetString(buffer, 0, data);
                }
                return str.Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Process the pending received data from the SSL stream.
        /// </summary>
        /// <param name="sslStream">SslStream to read.</param>
        /// <returns>String with the received data.</returns>
        static string ReceiveFromSSL(SslStream sslStream)
        {
            try
            {
                byte[] buffer = new byte[2048];
                string str = string.Empty;
                int bytes = sslStream.Read(buffer, 0, buffer.Length);
                if (bytes > 0)
                    str += Encoding.UTF8.GetString(buffer, 0, bytes);
                return str;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Edit the specified request to put the Wallet.
        /// </summary>
        /// <param name="request">Request JSON string.</param>
        /// <param name="clientEndPoint">Client EndPoint to identify if it is DevFee.</param>
        /// <returns>The modified local buffer with the expected wallet.</returns>
        static string RequestEdit(string request, IPEndPoint clientEndPoint)
        {
            if (request.Contains("login"))
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                dynamic jsonData = serializer.Deserialize<dynamic>(request);

                if (jsonData["method"] == "login")
                {
                    if (!jsonData["params"]["login"].Contains(StratumProxy.Wallet))
                    {
                        WriteLineColor(ConsoleColor.DarkYellow, $"{GetNow()} - DevFee detected");
                        WriteLineColor(ConsoleColor.DarkYellow, $"  DevFee Wallet: {jsonData["params"]["login"]}");

                        // Replace wallet
                        jsonData["params"]["login"] = StratumProxy.Wallet + StratumProxy.Worker;
                        WriteLineColor(ConsoleColor.DarkCyan, $"  New Wallet: {jsonData["params"]["login"]}");

                        // Add to DevFee ports list
                        DevFeeList.Add(clientEndPoint.Port);

                        // Serialize new JSON
                        request = serializer.Serialize(jsonData);
                    }
                }
            }

            return request;
        }

        /// <summary>
        /// Validate certificate. Probably self-signed, let's just accept it anyway.
        /// </summary>
        public static bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            return true;
        }

        /// <summary>
        /// Prints to the console the response from the remote pool.
        /// </summary>
        /// <param name="response">Buffer received from the remote pool.</param>
        /// <param name="clientEndPoint">Client connected to the pool.</param>
        /// <param name="requestMethod">The requested method used to get this response.</param>
        static void LogResponse(string response, IPEndPoint clientEndPoint, string requestMethod)
        {
            dynamic jsonData = new JavaScriptSerializer().Deserialize<dynamic>(response);

            if (requestMethod.Contains("login"))
            {
                WriteLineColor(IsDevFee(clientEndPoint.Port) ? ConsoleColor.DarkYellow : ConsoleColor.DarkGreen,
                    $"{GetNow()} - Stratum - Connected ({clientEndPoint})");
            }
            else if (requestMethod.Contains("submit"))
            {
                if (jsonData["result"]["status"] == "OK")
                {
                    WriteLineColor(IsDevFee(clientEndPoint.Port) ? ConsoleColor.DarkYellow : ConsoleColor.DarkGreen,
                        $"{GetNow()} - Share submitted! ({clientEndPoint})");
                    TotalShares.Add(IsDevFee(clientEndPoint.Port), false);
                }
                else
                {
                    WriteLineColor(ConsoleColor.DarkRed, $"{GetNow()} - Share rejected! ({clientEndPoint})");
                    TotalShares.Add(false, true);
                }
            }

            int error = response.LastIndexOf("error");
            if (error > 0 && jsonData["error"] != null)
                WriteLineColor(ConsoleColor.DarkRed, $"{GetNow()} - Error {jsonData["error"]["code"]}: {jsonData["error"]["message"]}");
        }

        /// <summary>
        /// Listen to keyboard input.
        /// </summary>
        static void KeyboardListener()
        {
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.S)
                {
                    WriteLineColor(ConsoleColor.DarkCyan, TotalShares.ToString());
                }

                Thread.Sleep(10);
            }
        }

        /// <summary>
        /// Determines whether the port is used for DevFee.
        /// </summary>
        /// <param name="port">Port number.</param>
        /// <returns>True if it is a DevFee port; otherwise, false.</returns>
        static bool IsDevFee(int port)
        {
            return DevFeeList.Contains(port);
        }

        /// <summary>
        /// Writes a line to the console with the specified color.
        /// </summary>
        /// <param name="color">Color.</param>
        /// <param name="value">String.</param>
        static void WriteLineColor(ConsoleColor color, string value)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(value);
            Console.ForegroundColor = ConsoleColor.Gray;
        }

        /// <summary>
        /// Gets the current time.
        /// </summary>
        /// <returns>Current time.</returns>
        static string GetNow()
        {
            return DateTime.Now.ToString("dd/MM/yy HH:mm:ss");
        }
    }

    public class ShareCounter
    {
        /// <summary>
        /// Normal shares count.
        /// </summary>
        public int Shares { get; private set; }
        
        /// <summary>
        /// Developer Fee shares count.
        /// </summary>
        public int DevFeeShares { get; private set; }

        /// <summary>
        /// Rejected shares count.
        /// </summary>
        public int RejectedShares { get; private set; }

        /// <summary>
        /// Add a share.
        /// </summary>
        /// <param name="isDevFee">Set to true if it is a DevFee share; otherwise, false.</param>
        /// <param name="rejected">Set to true if it is a rejected share; otherwise, false.</param>
        public void Add(bool isDevFee, bool rejected)
        {
            if (rejected)
                RejectedShares++;
            else if (isDevFee)
                DevFeeShares++;
            else
                Shares++;
        }

        public override string ToString()
        {
            return $"Total Shares: {Shares}, DevFee: {DevFeeShares}, Rejected: {RejectedShares}";
        }
    }

    public class ProxyConfig
    {
        /// <summary>
        /// Local host IP/Name server.
        /// </summary>
        public string LocalHost { get; set; } = "127.0.0.1:14001";

        /// <summary>
        /// Pool address.
        /// </summary>
        public string PoolAddress { get; set; } = "xmr-us-east1.nanopool.org:14433";

        /// <summary>
        /// Wallet address.
        /// </summary>
        public string Wallet { get; set; }

        /// <summary>
        /// Worker name.
        /// </summary>
        public string Worker { get; set; } = "little_worker";

        /// <summary>
        /// Secure Socket Layer.
        /// </summary>
        public bool IsSSL { get; set; } = true;

        /// <summary>
        /// Local server endpoint.
        /// </summary>
        public IPEndPoint LocalHostEndPoint { get; set; }

        /// <summary>
        /// Pool server endpoint.
        /// </summary>
        public IPEndPoint PoolEndPoint { get; set; }

        public bool Load()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> json;
            try
            {
                json = serializer.Deserialize<dynamic>(File.ReadAllText("proxy.txt"));
                IsSSL = bool.Parse(json["ssl"].ToString());

                // Local server
                LocalHost = json["local_host"].ToString();
                string[] localHost = LocalHost.Split(':');
                LocalHostEndPoint = new IPEndPoint(Dns.GetHostEntry(localHost[0]).AddressList[0], int.Parse(localHost[1]));

                // Remote pool
                PoolAddress = json["pool_address"].ToString();
                string[] remoteHost = PoolAddress.Split(':');
                PoolEndPoint = new IPEndPoint(Dns.GetHostEntry(remoteHost[0]).AddressList[0], int.Parse(remoteHost[1]));

                // Set wallet
                Wallet = json["wallet"].ToString().Trim();

                // Worker name
                if (json.ContainsKey("worker") && json["worker"] != null)
                    Worker = json["worker"].ToString();
                else
                    Worker = null;

                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Create()
        {
            File.WriteAllText("proxy.txt", ToString());
        }

        public override string ToString()
        {
            return @"{
    ""local_host"": """ + LocalHost + @""",
    ""pool_address"": """ + PoolAddress + @""",
    ""wallet"": """ + Wallet + @""",
    ""worker"": """ + Worker + @""",
    ""ssl"": " + IsSSL.ToString().ToLowerInvariant() + @"
}";
        }
    }
}
