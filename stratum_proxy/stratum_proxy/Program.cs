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
        // Local server address
        private static IPEndPoint LocalEndPoint;
        private static string LocalHostName;

        // Main remote pool address
        private static IPEndPoint PoolEndPoint;
        private static string PoolHostName;

        // Main wallet
        static string Wallet = string.Empty;

        // Optional dev fee worker name
        static string WorkerName = string.Empty;

        // Secure Socket Layer
        static bool IsSSL = false;

        // List with the DevFee ports used to identify the shares
        static List<int> DevFeeList = new List<int>();

        // List with shares count
        static int[] TotalShares = { 0, 0, 0 }; // Normal, DevFee, Rejected

        /// <summary>
        /// Main method.
        /// </summary>
        static void Main()
        {
            if (!File.Exists("proxy.txt"))
            {
                Console.WriteLine("Create a proxy.txt with the configuration:\n");
                string proxyConfig = @"{
    ""local_host"": ""127.0.0.1:14001"",
    ""pool_address"": ""xmr-us-east1.nanopool.org:14433"",
    ""wallet"": ""MYWALLET"",
    ""worker"": ""little_worker"",
    ""ssl"": true
}";
                string help = @"Configuration file help:
local_host      - IP to use as the proxy/server.
pool_address    - The remote pool address. If it is a SSL/TLS one set ""ssl"" to ""true"".
wallet          - Your wallet. Can be a exchange wallet with payment id (wallet.payment_id).
worker          - Worker name to identify the DevFee on the pool.
ssl             - Use SSL/TLS. Set to ""true"" to connect to the remote pool using SSL/TLS.
                  ONLY for the connection to the remote pool, not local.";

                WriteLineColor(ConsoleColor.Gray, proxyConfig);
                WriteLineColor(ConsoleColor.Gray, help);
                Console.Write("\nDo you want to create it now? (y/n): ");
                ConsoleKeyInfo key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Y)
                    File.WriteAllText("proxy.txt", proxyConfig);
                return;
            }

            // Header
            Console.Write('\n');
            Console.WriteLine("╔═════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║             Claymore CryptoNote Stratum Proxy  v1.3             ║");
            Console.WriteLine("╚═════════════════════════════════════════════════════════════════╝");

            JavaScriptSerializer serializer = new JavaScriptSerializer();

            Dictionary<string, object> jsonConfig;
            try
            {
                jsonConfig = serializer.Deserialize<dynamic>(File.ReadAllText("proxy.txt"));
                IsSSL = bool.Parse(jsonConfig["ssl"].ToString());
                Console.WriteLine("Config loaded!");
            }
            catch
            {
                Console.WriteLine("Bad configuration file!");
                Console.Read();
                return;
            }

            // SSL/TLS
            if (IsSSL)
                WriteLineColor(ConsoleColor.DarkMagenta, "Secure Socket Layer is enabled");

            // Local server
            string[] localHost = jsonConfig["local_host"].ToString().Split(':');
            LocalEndPoint = new IPEndPoint(Dns.GetHostEntry(localHost[0]).AddressList[0], int.Parse(localHost[1]));
            LocalHostName = localHost[0];
            WriteLineColor(ConsoleColor.DarkCyan, $"Local server is {localHost[0]}:{localHost[1]}");

            // Remote pool
            string[] remoteHost = jsonConfig["pool_address"].ToString().Split(':');
            PoolEndPoint = new IPEndPoint(Dns.GetHostEntry(remoteHost[0]).AddressList[0], int.Parse(remoteHost[1]));
            PoolHostName = remoteHost[0];
            WriteLineColor(ConsoleColor.DarkCyan, $"Main pool is {remoteHost[0]}:{remoteHost[1]}");

            // Set wallet
            Wallet = jsonConfig["wallet"].ToString().Trim();

            // Check if there is a PaymentID in the wallet
            string[] walletParts = Wallet.Split('.');
            WriteLineColor(ConsoleColor.DarkCyan, $"Wallet is {walletParts[0]}");
            if (walletParts.Length > 1)
                WriteLineColor(ConsoleColor.DarkCyan, $"  PaymentID is {walletParts[1]}");

            // Worker name
            if (jsonConfig.ContainsKey("worker") && jsonConfig["worker"] != null)
                WorkerName = jsonConfig["worker"].ToString();
            else
                WorkerName = null;

            if (!string.IsNullOrEmpty(WorkerName))
            {
                WriteLineColor(ConsoleColor.DarkCyan, $"  Worker is {WorkerName}");

                // Check pool separator
                string[] poolsDot = { "nanopool.org", "monerohash.com", "minexmr.com", "dwarfpool.com" };
                string[] poolsPlus = { "xmrpool.eu" };
                if (poolsDot.Any(x => remoteHost[0].Contains(x)))
                    WorkerName = "." + WorkerName;
                else if (poolsPlus.Any(x => remoteHost[0].Contains(x)))
                    WorkerName = "+" + WorkerName;
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

            if (IsSSL)
                Console.WriteLine("Do not connect using SSL/TLS in the miners! Only connection to the remote pool is using SSL/TLS.\n");

            Console.WriteLine("Press \"s\" for current statistics");

            // Start keyboard input thread
            Thread kbdThread = new Thread(() => KeyboardListener());
            kbdThread.Start();

            // Listening loop
            while (true)
            {
                ServerLoop();
                WriteLineColor(ConsoleColor.DarkRed, "Socket connection dropped! Retrying in 10 s...");
                Thread.Sleep(10000);
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
                server = new TcpListener(LocalEndPoint);
                server.Start();
            }
            catch
            {
                WriteLineColor(ConsoleColor.DarkRed, $"Failed to listen on {LocalEndPoint} ({LocalHostName}:{LocalEndPoint.Port})");
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
                    new Thread(() => ProxyHandler(clientSocket)).Start();
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
                    if (IsSSL)
                    {
                        remoteClient = new TcpClient(PoolHostName, PoolEndPoint.Port);
                        remoteSslStream = new SslStream(remoteClient.GetStream(), false,
                            new RemoteCertificateValidationCallback(ValidateServerCertificate), null);
                        remoteSslStream.AuthenticateAsClient(PoolHostName);
                    }
                    else
                    {
                        remoteSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        remoteSocket.Connect(PoolEndPoint);
                    }
                    break; // Connection OK
                }
                catch(Exception e)
                {
                    WriteLineColor(ConsoleColor.DarkRed, $"{GetNow()} - Connection lost with {PoolEndPoint}. Retry: {i}/3");
                    WriteLineColor(ConsoleColor.DarkRed, $"  Exception: {e.Message}");
                    Thread.Sleep(1000);
                    if (i == 3)
                    {
                        remoteSocket = null;
                        remoteSslStream = null;
                    }
                }
            }

            if ((remoteSocket == null && !IsSSL) || (remoteSslStream == null && IsSSL))
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
                        if (IsSSL)
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
                }

                // Read packet from the remote pool
                string poolResponse = string.Empty;

                // If there was a request before, wait for pool response or if data is available
                if (lastRequestMethod.Length > 0 || remoteClient.Available > 0)
                    poolResponse = IsSSL ? ReceiveFromSSL(remoteSslStream) : ReceiveFrom(remoteSocket);
                // If there is data available read it, otherwise skip
                //else if ()
                //    poolResponse = IsSSL ? ReceiveFromSSL(remoteSslStream) : ReceiveFrom(remoteSocket);

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

                    // Log accepted, DevFee, rejected shares and others
                    LogResponse(poolResponse, clientEndPoint, lastRequestMethod);
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
                    if (!jsonData["params"]["login"].Contains(Wallet))
                    {
                        WriteLineColor(ConsoleColor.DarkYellow, $"{GetNow()} - DevFee detected");
                        WriteLineColor(ConsoleColor.DarkYellow, $"  DevFee Wallet: {jsonData["params"]["login"]}");

                        // Replace wallet
                        jsonData["params"]["login"] = Wallet + WorkerName;
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
                    TotalShares[IsDevFee(clientEndPoint.Port) ? 1 : 0] += 1;
                }
                else
                {
                    WriteLineColor(ConsoleColor.DarkRed, $"{GetNow()} - Share rejected! ({clientEndPoint})");
                    TotalShares[2] += 1;
                }
            }

            int error = response.LastIndexOf("error");
            if (error > 0)
                if (!response.Substring(error).Contains("null"))
                {
                    WriteLineColor(ConsoleColor.DarkRed, $"{GetNow()} - Error: " + response);
                }

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
                    WriteLineColor(ConsoleColor.DarkCyan,
                        $"Total Shares: {TotalShares[0]}, DevFee: {TotalShares[1]}, Rejected: {TotalShares[2]}");
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
}
