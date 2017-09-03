using System;
using System.Collections.Generic;
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
        static string wallet = string.Empty;

        // Optional worker name
        static string workerName = string.Empty;

        // A list with the DevFee ports used to identify the shares
        static List<int> devfeePorts = new List<int>();

        // List with shares count
        static int[] totalShares = { 0, 0, 0 }; // Normal, DevFee, Rejected

        static void Main(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Arguments: local_host:port remote_pool:port wallet [worker]");
                Console.WriteLine("Example: 127.0.0.1:14001 xmr-us-east1.nanopool.org:14444 WALLET.PAYMENTID my_worker/my@mail");
                return;
            }

            // Header
            Console.Write('\n');
            Console.WriteLine("╔═════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║                    Claymore XMR Proxy  v1.0                     ║");
            Console.WriteLine("╚═════════════════════════════════════════════════════════════════╝");

            // Local host
            string localHost = args[0].Split(':')[0];
            int localPort = int.Parse(args[0].Split(':')[1]);
            WriteLineColor(ConsoleColor.DarkCyan, "Localhost is {0}", args[0]);

            // Remote host (pool)
            string remoteHost = args[1].Split(':')[0];
            int remotePort = int.Parse(args[1].Split(':')[1]);
            WriteLineColor(ConsoleColor.DarkCyan, "Main pool is {0}", args[1]);

            wallet = args[2];

            if (args.Length > 3)
                workerName = args[3];

            // Check if there is a PaymentID in the wallet
            string[] walletParts = wallet.Split('.');
            if (wallet.Length > 1)
                WriteLineColor(ConsoleColor.DarkCyan, "  PaymentID is {0}", walletParts[1]);

            if (!string.IsNullOrEmpty(workerName))
                WriteLineColor(ConsoleColor.DarkCyan, "  Worker is {0}", workerName);

            string[] poolsDot = { "nanopool.org" };
            if (poolsDot.Any(x => remoteHost.Contains(x)))
            {
                workerName = "." + workerName;
            }

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

            Console.Read();
        }

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
                WriteLineColor(ConsoleColor.DarkRed, "Failed to listen on {0}:{1}", localHost, localPort);
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

                    // Connection OK
                    break;
                }
                catch
                {
                    WriteLineColor(ConsoleColor.DarkRed, "{0} - Connection lost with <{1}:{2}>. Retry: {3}/3",
                        GetNow(), remoteHost, remotePort, i);
                    Thread.Sleep(1000);
                }
            }

            if (remoteSocket == null)
            {
                WriteLineColor(ConsoleColor.DarkRed, GetNow() + " - Could not connect to the pool.");

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
                        WriteLineColor(ConsoleColor.DarkRed, "{0} - Packed send to pool failed", GetNow());
                        WriteLineColor(ConsoleColor.DarkRed, "  Socket error({0}): {1}", se.ErrorCode, se.Message);

                        WriteLineColor(ConsoleColor.DarkRed, "  Packet lost for {0}: {1}", localPort, localBuffer);
                        WriteLineColor(ConsoleColor.DarkRed, "{0} - Connection with pool lost. Claymore should reconnect...", GetNow());

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

                        // Some logging after send
                        if (remoteBuffer.Contains("status"))
                        {
                            dynamic jsonData = new JavaScriptSerializer().Deserialize<dynamic>(remoteBuffer);
                            if (jsonData["id"] == 4)
                            {
                                if (jsonData["result"]["status"] == "OK")
                                {
                                    WriteLineColor(IsDevFee(localPort) ? ConsoleColor.DarkYellow : ConsoleColor.DarkGreen,
                                        "{0} - Share submitted! ({1})", GetNow(), localPort);
                                    totalShares[IsDevFee(localPort) ? 1 : 0] += 1;
                                }
                                else
                                {
                                    WriteLineColor(ConsoleColor.DarkRed, "{0} - Share rejected! ({1})", GetNow(), localPort);
                                    totalShares[2] += 1;
                                }
                            }
                            else if (jsonData["id"] == 1)
                            {
                                WriteLineColor(IsDevFee(localPort) ? ConsoleColor.DarkYellow : ConsoleColor.DarkGreen,
                                        "{0} - Stratum - Connected ({1})", GetNow(), localPort);
                            }
                            else
                                Console.WriteLine(remoteBuffer);
                        }
                    }
                    catch
                    {
                        WriteLineColor(ConsoleColor.DarkRed, "{0} - Disconnected! ({1}) Mining stopped?", localPort, GetNow());
                        clientSocket.Disconnect(false);
                        clientSocket.Close();
                        break; // Main loop
                    }
                }

                // Reduce CPU usage
                Thread.Sleep(10);
            }

            // Delete this port from DevFee ports list
            if (IsDevFee(localPort))
                devfeePorts.Remove(localPort);
        }

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

        static string RequestHandler(string localBuffer, int localPort)
        {
            if (localBuffer.Contains("login"))
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                dynamic jsonData = serializer.Deserialize<dynamic>(localBuffer);

                if (jsonData["method"] == "login")
                {
                    Console.WriteLine("Login with wallet: {0}", jsonData["params"]["login"]);
                    if (!jsonData["params"]["login"].Contains(wallet))
                    {
                        WriteLineColor(ConsoleColor.DarkYellow, "{0}  - DevFee detected", GetNow());
                        WriteLineColor(ConsoleColor.DarkYellow, "  DevFee Wallet: {0}", jsonData["params"]["login"]);

                        // Replace wallet
                        jsonData["params"]["login"] = wallet + workerName;
                        WriteLineColor(ConsoleColor.DarkCyan, "  New Wallet: {0}", jsonData["params"]["login"]);

                        // Add to DevFee ports list
                        devfeePorts.Add(localPort);

                        // Serialize new JSON
                        localBuffer = serializer.Serialize(jsonData);
                    }
                }
            }

            return localBuffer;
        }

        static void KeyboardListener()
        {
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.S)
                    WriteLineColor(ConsoleColor.DarkCyan, "Total Shares: {0}, Normal: {1}, DevFee: {2}, Rejected: {3}",
                        totalShares.Sum(), totalShares[0], totalShares[1], totalShares[2]);
            }
        }

        static bool IsDevFee(int port)
        {
            return devfeePorts.Contains(port);
        }

        static void WriteLineColor(ConsoleColor color, string value, params object[] args)
        {
            ConsoleColor def = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(value, args);
            Console.ForegroundColor = def;
        }

        static string GetNow()
        {
            return DateTime.Now.ToString("dd/MM/yy HH:mm:ss");
        }
    }
}
