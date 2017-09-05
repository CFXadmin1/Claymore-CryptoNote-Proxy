using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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

        // A list with the DevFee ports used to identify the shares
        static List<int> DevFeePorts = new List<int>();

        // List with shares count
        static int[] TotalShares = { 0, 0, 0 }; // Normal, DevFee, Rejected

        /// <summary>
        /// Main method.
        /// </summary>
        /// <param name="args">Arguments.</param>
        static void Main(string[] args)
        {
            if (args.Length < 3 && !File.Exists("proxy.txt"))
            {
                Console.WriteLine("Arguments: local_host:port remote_pool:port wallet [worker]");
                Console.WriteLine("Example: 127.0.0.1:14001 xmr-us-east1.nanopool.org:14444 WALLET.PAYMENTID my_worker/my@mail");
                Console.WriteLine("Also you can create a proxy.txt with the configuration:\n");
                string proxyConfig = @"{
    ""local_host"": ""127.0.0.1:14001"",
    ""pool_address"": ""xmr-us-east1.nanopool.org:14444"",
    ""wallet"": ""MYWALLET"",
    ""worker"": ""little_worker""
}";
                WriteLineColor(ConsoleColor.Gray, proxyConfig);
                Console.Write("\nDo you want to create it now? (y/n): ");
                ConsoleKeyInfo key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Y)
                    File.WriteAllText("proxy.txt", proxyConfig);
                return;
            }

            // Header
            Console.Write('\n');
            Console.WriteLine("╔═════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║             Claymore CryptoNote Stratum Proxy  v1.1             ║");
            Console.WriteLine("╚═════════════════════════════════════════════════════════════════╝");

            string[] newArgs = ReadConfig();
            if (newArgs != null)
            {
                Console.WriteLine("Config loaded!");
                args = newArgs;
            }
            else
            {
                Console.WriteLine("Bad configuration file!");
                Console.Read();
                return;
            }

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
                WorkerName = args[3];
            if (!string.IsNullOrEmpty(WorkerName))
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

            Console.WriteLine("Based on https://github.com/JuicyPasta/Claymore-No-Fee-Proxy by JuicyPasta & Drdada");
            Console.WriteLine(@"As Claymore v9.7 beta, the DevFee logins at the start and takes some hashes all the time, " +
                "like 1 hash every 39 of yours (there is not connection/disconections for devfee like in ETH). " +
                "This proxy tries to replace the wallet in every login detected that is not your wallet.");
            Console.Write('\n');
            WriteLineColor(ConsoleColor.DarkYellow, "Indentified DevFee shares are printed in yellow");
            WriteLineColor(ConsoleColor.DarkGreen, "Your shares are printed in green");
            Console.Write('\n');
            Console.WriteLine("Press \"s\" for current statistics");

            // Listening loop
            ServerLoop(localHost, localPort, remoteHost, remotePort);
        }

        /// <summary>
        /// Accepts and controls connections from the localhost to the remotepool and vice versa.
        /// </summary>
        /// <param name="localHost">Local host IP or name.</param>
        /// <param name="localPort">Local host port. Must be the same as the Claymore app.</param>
        /// <param name="remoteHost">Remote pool address.</param>
        /// <param name="remotePort">Remote pool port.</param>
        static void ServerLoop(string localHost, int localPort, string remoteHost, int remotePort)
        {
            Socket server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                // Initialize socket
                Console.WriteLine("Initializing socket...");
                server.Bind(new IPEndPoint(Dns.GetHostEntry(localHost).AddressList[0], localPort));
            }
            catch
            {
                WriteLineColor(ConsoleColor.DarkRed, $"Failed to listen on {localHost}:{localPort}");
                WriteLineColor(ConsoleColor.DarkRed, "  Check for other listening sockets or correct permissions");
                return;
            }

            // Listen to a maximum of (usually) 5 connections
            server.Listen(5);

            // Start keyboard input thread
            Thread kbdThread = new Thread(() => KeyboardListener());
            kbdThread.Start();

            Console.WriteLine("Waiting for connections...");
            while (true)
            {
                // Reduce CPU usage
                Thread.Sleep(10);

                Socket clientSocket = server.Accept();

                Console.WriteLine("New connection received from {0}:{1}",
                    ((IPEndPoint)clientSocket.RemoteEndPoint).Address,
                    ((IPEndPoint)clientSocket.RemoteEndPoint).Port);

                // Start a new thread to talk to the remote pool
                Thread proxyThread = new Thread(() => ProxyHandler(clientSocket, remoteHost, remotePort));
                proxyThread.Start();
            }
        }

        /// <summary>
        /// Initializes a new thread for the specified port.
        /// </summary>
        /// <param name="clientSocket">Localhost client socket.</param>
        /// <param name="remoteHost">Remote pool address.</param>
        /// <param name="remotePort">Remote pool port.</param>
        static void ProxyHandler(Socket clientSocket, string remoteHost, int remotePort)
        {
            string localHost = ((IPEndPoint)clientSocket.RemoteEndPoint).Address.ToString();
            int localPort = ((IPEndPoint)clientSocket.RemoteEndPoint).Port;

            // Try to connect to the remote pool
            Socket remoteSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    remoteSocket.Connect(Dns.GetHostEntry(remoteHost).AddressList[0], remotePort);
                    break; // Connection OK
                }
                catch
                {
                    WriteLineColor(ConsoleColor.DarkRed, $"{GetNow()} - Connection lost with <{remoteHost}:{remotePort}>. Retry: {i}/3");
                    Thread.Sleep(1000);
                }
            }

            if (remoteSocket == null)
            {
                WriteLineColor(ConsoleColor.DarkRed, $"{GetNow()} - Could not connect to the pool.");

                // Close connection
                clientSocket.Disconnect(false);
                clientSocket.Close();

                // Exit thread
                return;
            }

            // Main loop
            while (true)
            {
                // Read packet from the local host
                string localBuffer = ReceiveFrom(clientSocket);

                if (localBuffer.Length > 0)
                {
                    // Modify the local packet
                    localBuffer = RequestHandler(localBuffer, localPort);

                    // Send the modified packet to the remote pool
                    try
                    {
                        remoteSocket.Send(Encoding.UTF8.GetBytes(localBuffer));
                    }
                    catch (SocketException se)
                    {
                        WriteLineColor(ConsoleColor.DarkRed, $"{GetNow()} - Send to pool failed");
                        WriteLineColor(ConsoleColor.DarkRed, $"  Socket error({se.ErrorCode}): {se.Message}");
                        WriteLineColor(ConsoleColor.DarkRed, $"  Packet lost for {localPort}: {localBuffer}");
                        WriteLineColor(ConsoleColor.DarkRed, $"{GetNow()} - Connection with pool lost. Claymore should reconnect...");

                        // Close connection
                        clientSocket.Disconnect(false);
                        clientSocket.Close();

                        break; // Main loop
                    }
                }

                // Read packet from the remote pool
                string remoteBuffer = ReceiveFrom(remoteSocket);

                if (remoteBuffer.Length > 0)
                {
                    try
                    {
                        // Send the response to the local host
                        clientSocket.Send(Encoding.UTF8.GetBytes(remoteBuffer));

                        // Log accepted, DevFee and rejected shares
                        LogShares(remoteBuffer, localPort);
                    }
                    catch
                    {
                        WriteLineColor(ConsoleColor.DarkRed, $"{localPort} - Disconnected! ({GetNow()}) Mining stopped?");
                        clientSocket.Disconnect(false);
                        clientSocket.Close();
                        break; // Main loop
                    }
                }

                // Reduce CPU usage
                Thread.Sleep(50);
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
                byte[] buffer = new byte[4096];
                string str = string.Empty;
                while (socket.Available > 0)
                {
                    int data = socket.Receive(buffer);
                    str += Encoding.UTF8.GetString(buffer, 0, data);
                }
                return str;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Modifies the specified local buffer with the expected wallet.
        /// </summary>
        /// <param name="localBuffer">Local buffer to use.</param>
        /// <param name="localPort">Port of the buffer used to identify if it is DevFee.</param>
        /// <returns>The modified local buffer with the expected wallet.</returns>
        static string RequestHandler(string localBuffer, int localPort)
        {
            if (localBuffer.Contains("login"))
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                dynamic jsonData = serializer.Deserialize<dynamic>(localBuffer);

                // Only at the login
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
                        localBuffer = serializer.Serialize(jsonData);
                    }
                }
            }

            return localBuffer;
        }

        /// <summary>
        /// Prints to the console the status of the share processed.
        /// </summary>
        /// <param name="remoteBuffer">Remote buffer sent to the pool.</param>
        /// <param name="localPort">Port used to send the remote buffer.</param>
        static void LogShares(string remoteBuffer, int localPort)
        {
            if (remoteBuffer.Contains("status"))
            {
                dynamic jsonData = new JavaScriptSerializer().Deserialize<dynamic>(remoteBuffer);
                // Shares
                if (jsonData["id"] == 4)
                {
                    if (jsonData["result"]["status"] == "OK")
                    {
                        WriteLineColor(IsDevFee(localPort) ? ConsoleColor.DarkYellow : ConsoleColor.DarkGreen,
                            $"{GetNow()} - Share submitted! ({localPort})");
                        TotalShares[IsDevFee(localPort) ? 1 : 0] += 1;
                    }
                    else
                    {
                        WriteLineColor(ConsoleColor.DarkRed, $"{GetNow()} - Share rejected! ({localPort})");
                        TotalShares[2] += 1;
                    }
                }
                // Connections
                else if (jsonData["id"] == 1)
                {
                    WriteLineColor(IsDevFee(localPort) ? ConsoleColor.DarkYellow : ConsoleColor.DarkGreen,
                            $"{GetNow()} - Stratum - Connected ({localPort})");
                }
                // Unknown, log anyways
                else
                    Console.WriteLine(remoteBuffer);
            }
        }

        /// <summary>
        /// Thread to listen the keyboard.
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
            ConsoleColor def = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(value);
            Console.ForegroundColor = def;
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
                string[] newArgs = new string[4];
                newArgs[0] = jsonData["local_host"];
                newArgs[1] = jsonData["pool_address"];
                newArgs[2] = jsonData["wallet"];
                newArgs[3] = jsonData["worker"];
                return newArgs;
            }
            catch
            {
                return null;
            }
        }
    }
}
