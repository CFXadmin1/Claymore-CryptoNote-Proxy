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
        // Main wallet
        static string Wallet = string.Empty;

        // Optional dev fee worker name
        static string WorkerName = string.Empty;

        // Secure Socket Layer
        static bool IsSSL = false;

        // List with the DevFee ports used to identify the shares
        static List<int> DevFeePorts = new List<int>();

        // List with all the connected clients
        static List<int> ConnectedPorts = new List<int>();

        // List with shares count
        static int[] TotalShares = { 0, 0, 0 }; // Normal, DevFee, Rejected

        /// <summary>
        /// Main method.
        /// </summary>
        /// <param name="args">Arguments.</param>
        static void Main(string[] args)
        {
            if (!File.Exists("proxy.txt"))
            {
                Console.WriteLine("Create a proxy.txt with the configuration:\n");
                string proxyConfig = @"{
    ""local_host"": ""127.0.0.1:14001"",
    ""pool_address"": ""xmr-us-east1.nanopool.org:14433"",
    ""wallet"": ""MYWALLET"",
    ""worker"": ""little_worker"",
    ""ssl"": ""true""
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
            Console.WriteLine("║             Claymore CryptoNote Stratum Proxy  v1.2             ║");
            Console.WriteLine("╚═════════════════════════════════════════════════════════════════╝");

            string[] newArgs = ReadConfig();
            if (newArgs != null)
            {
                Console.WriteLine("Config loaded!");
                args = newArgs;
            }
            else if (args.Length == 0)
            {
                Console.WriteLine("Bad configuration file!");
                Console.Read();
                return;
            }

            // SSL/TLS
            if (args.Length > 4 || args[1].StartsWith("ssl"))
                IsSSL = bool.Parse(args[4]);
            if (IsSSL)
                WriteLineColor(ConsoleColor.DarkMagenta, "Secure Socket Layer is enabled");

            // Local host
            string localHost = args[0].Split(':')[0];
            int localPort = int.Parse(args[0].Split(':')[1]);
            WriteLineColor(ConsoleColor.DarkCyan, $"Localhost is {args[0]}");

            // Remote host (pool)
            string remoteHost = args[1].Split(':')[0];
            int remotePort = int.Parse(args[1].Split(':')[1]);
            WriteLineColor(ConsoleColor.DarkCyan, $"Main pool is {args[1]}");

            // Set wallet
            Wallet = args[2];

            // Check if there is a PaymentID in the wallet
            string[] walletParts = Wallet.Split('.');
            WriteLineColor(ConsoleColor.DarkCyan, $"Wallet is {walletParts[0]}");
            if (walletParts.Length > 1)
                WriteLineColor(ConsoleColor.DarkCyan, $"  PaymentID is {walletParts[1]}");

            // Worker name
            if (args.Length > 3)
                WorkerName = args[3].ToLower() == "null" ? null : args[3];
            if (!string.IsNullOrEmpty(WorkerName))
            {
                WriteLineColor(ConsoleColor.DarkCyan, $"  Worker is {WorkerName}");

                // Check pool separator
                string[] poolsDot = { "nanopool.org", "monerohash.com", "minexmr.com", "dwarfpool.com" };
                string[] poolsPlus = { "xmrpool.eu" };
                if (poolsDot.Any(x => remoteHost.Contains(x)))
                    WorkerName = "." + WorkerName;
                else if (poolsPlus.Any(x => remoteHost.Contains(x)))
                    WorkerName = "+" + WorkerName;
                else
                    WriteLineColor(ConsoleColor.DarkGray, "Pool worker separator not detected! Put it yourself in front of the worker name if it " +
                        "not has it. Check your pool details if you do not know. Skipping...\n");
            }

            // Info
            Console.WriteLine("Based on https://github.com/JuicyPasta/Claymore-No-Fee-Proxy by JuicyPasta & Drdada");
            Console.WriteLine(@"As Claymore v9.7 beta, the DevFee logins at the start and takes some hashes all the time, " +
                "like 1 hash every 39 of yours (there is not connection/disconections for devfee like in ETH). " +
                "This proxy tries to replace the wallet in every login detected that is not your wallet.\n");
            WriteLineColor(ConsoleColor.DarkYellow, "Indentified DevFee shares are printed in yellow");
            WriteLineColor(ConsoleColor.DarkGreen, "Your shares are printed in green\n");

            if (IsSSL)
                Console.WriteLine("Do not connect using SSL/TLS in the miners! Only connection to the remote pool is using SSL/TLS.\n");

            Console.WriteLine("Press \"s\" for current statistics");

            // Listening loop
            ServerLoop(localHost, localPort, remoteHost, remotePort);
        }

        /// <summary>
        /// Accepts and controls connections from the localhost to the remote pool and vice versa.
        /// </summary>
        /// <param name="localHost">Local host IP or name.</param>
        /// <param name="localPort">Local host port. Must be the same as the Claymore app.</param>
        /// <param name="remoteHost">Remote pool address.</param>
        /// <param name="remotePort">Remote pool port.</param>
        static void ServerLoop(string localHost, int localPort, string remoteHost, int remotePort)
        {
            TcpListener server;
            try
            {
                Console.WriteLine("Initializing socket...");
                server = new TcpListener(new IPEndPoint(Dns.GetHostEntry(localHost).AddressList[0], localPort));
                server.Start();
            }
            catch
            {
                WriteLineColor(ConsoleColor.DarkRed, $"Failed to listen on {localHost}:{localPort}");
                WriteLineColor(ConsoleColor.DarkRed, "  Check for other listening sockets or correct permissions");
                return;
            }

            // Start keyboard input thread
            Thread kbdThread = new Thread(() => KeyboardListener());
            kbdThread.Start();

            Console.WriteLine("Waiting for connections...");
            while (true)
            {
                TcpClient clientSocket = server.AcceptTcpClient();

                // Start a new thread to talk to the remote pool
                new Thread(() => ProxyHandler(clientSocket, remoteHost, remotePort)).Start();
            }
        }

        /// <summary>
        /// Initializes a new thread for the specified port.
        /// </summary>
        /// <param name="clientSocket">Localhost client socket.</param>
        /// <param name="remoteHost">Remote pool address.</param>
        /// <param name="remotePort">Remote pool port.</param>
        static void ProxyHandler(TcpClient clientSocket, string remoteHost, int remotePort)
        {
            string localHost = ((IPEndPoint)clientSocket.Client.RemoteEndPoint).Address.ToString();
            int localPort = ((IPEndPoint)clientSocket.Client.RemoteEndPoint).Port;

            Console.WriteLine($"New connection received from {localHost}:{localPort}");

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
                        remoteClient = new TcpClient(remoteHost, remotePort);
                        remoteSslStream = new SslStream(remoteClient.GetStream(), false,
                            new RemoteCertificateValidationCallback(ValidateServerCertificate), null);
                        remoteSslStream.AuthenticateAsClient(remoteHost);
                    }
                    else
                    {
                        remoteSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        remoteSocket.Connect(Dns.GetHostEntry(remoteHost).AddressList[0], remotePort);
                    }
                    break; // Connection OK
                }
                catch
                {
                    WriteLineColor(ConsoleColor.DarkRed, $"{GetNow()} - Connection lost with <{remoteHost}:{remotePort}>. Retry: {i}/3");
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
                    request = RequestEdit(request, localPort);

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
                        WriteLineColor(ConsoleColor.DarkRed, $"{GetNow()} - Send to pool failed. {localPort}: {request}");
                        WriteLineColor(ConsoleColor.DarkRed, $"  Socket error({se.ErrorCode}): {se.Message}");
                        WriteLineColor(ConsoleColor.DarkRed, $"  Connection with pool lost. Claymore should reconnect...");
                        clientSocket.Close();
                        break; // Main loop
                    }
                }

                // Read packet from the remote pool
                string poolResponse = string.Empty;

                // If there was a request before, wait for pool response
                if (lastRequestMethod.Length > 0)
                    poolResponse = IsSSL ? ReceiveFromSSL(remoteSslStream) : ReceiveFrom(remoteSocket);
                // If there is data available read it, otherwise skip
                else if (remoteClient.Available > 0)
                    poolResponse = IsSSL ? ReceiveFromSSL(remoteSslStream) : ReceiveFrom(remoteSocket);

                if (poolResponse.Length > 0)
                {
                    try
                    {
                        // Send the response to the local client (miner)
                        clientSocket.Client.Send(Encoding.UTF8.GetBytes(poolResponse));

                        // Log accepted, DevFee, rejected shares and others
                        LogResponse(poolResponse, localPort, lastRequestMethod);
                    }
                    catch
                    {
                        WriteLineColor(ConsoleColor.DarkRed, $"{GetNow()} - Disconnected! ({localPort}) Mining stopped?");
                        clientSocket.Close();
                        break; // Main loop
                    }
                }

                // Reduce CPU usage
                Thread.Sleep(10);
            }

            // Delete this port from DevFee ports list
            if (IsDevFee(localPort))
                DevFeePorts.Remove(localPort);
        }

        /// <summary>
        /// Process the pending received data from the socket.
        /// </summary>
        /// <param name="socket">Socket to use.</param>
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
        /// <param name="socket">SslStream to use.</param>
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
        /// <param name="request">Local buffer to use.</param>
        /// <param name="localPort">Port of the buffer used to identify if it is DevFee.</param>
        /// <returns>The modified local buffer with the expected wallet.</returns>
        static string RequestEdit(string request, int localPort)
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
                        DevFeePorts.Add(localPort);

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
        /// <param name="responseBuffer">Buffer received from the remote pool.</param>
        /// <param name="clientPort">Port used on the remote pool client.</param>
        /// <param name="requestMethod">The requested method used to get this response.</param>
        static void LogResponse(string response, int clientPort, string requestMethod)
        {
            dynamic jsonData = new JavaScriptSerializer().Deserialize<dynamic>(response);

            if (requestMethod.Contains("login"))
            {
                WriteLineColor(IsDevFee(clientPort) ? ConsoleColor.DarkYellow : ConsoleColor.DarkGreen,
                    $"{GetNow()} - Stratum - Connected ({clientPort})");
            }
            else if (requestMethod.Contains("submit"))
            {
                if (jsonData["result"]["status"] == "OK")
                {
                    WriteLineColor(IsDevFee(clientPort) ? ConsoleColor.DarkYellow : ConsoleColor.DarkGreen,
                        $"{GetNow()} - Share submitted! ({clientPort})");
                    TotalShares[IsDevFee(clientPort) ? 1 : 0] += 1;
                }
                else
                {
                    WriteLineColor(ConsoleColor.DarkRed, $"{GetNow()} - Share rejected! ({clientPort})");
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
            return DevFeePorts.Contains(port);
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

        /// <summary>
        /// Reads the configuration from proxy.txt.
        /// </summary>
        /// <returns>New arguments to use with the main method.</returns>
        static string[] ReadConfig()
        {
            if (!File.Exists("proxy.txt"))
                return null;

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            try
            {
                dynamic jsonData = serializer.Deserialize<dynamic>(File.ReadAllText("proxy.txt"));
                string[] newArgs = new string[5];
                newArgs[0] = jsonData["local_host"];
                newArgs[1] = jsonData["pool_address"];
                newArgs[2] = jsonData["wallet"];
                newArgs[3] = jsonData["worker"];
                newArgs[4] = jsonData["ssl"];
                return newArgs;
            }
            catch
            {
                return null;
            }
        }
    }
}
