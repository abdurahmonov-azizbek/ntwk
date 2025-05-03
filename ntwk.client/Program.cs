using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Threading;

class Program
{
    static readonly string subdomain = "azizbek";
    static readonly string serverAddress = "server_ip_or_domain";
    static readonly int serverPort = 5050;
    static readonly string localAppHost = "127.0.0.1";
    static readonly int localAppPort = 5072;
    static readonly TimeSpan readTimeout = TimeSpan.FromSeconds(10);
    static readonly TimeSpan heartbeatInterval = TimeSpan.FromSeconds(5);

    static async Task Main(string[] args)
    {
        while (true)
        {
            var client = new TcpClient();
            try
            {
                Console.WriteLine($"Connecting to {serverAddress}:{serverPort}...");
                await client.ConnectAsync(serverAddress, serverPort);

                ConfigureKeepAlive(client);
                Console.WriteLine("Connected to server.");

                var stream = client.GetStream();
                stream.ReadTimeout = (int)readTimeout.TotalMilliseconds;
                var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                var reader = new StreamReader(stream, Encoding.UTF8);

                await writer.WriteLineAsync($"TUNNEL_REQUEST:{subdomain}");
                Console.WriteLine($"Sent tunnel request for subdomain: {subdomain}");

                var response = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(response))
                {
                    Console.WriteLine("No response from server.");
                    throw new Exception("Server closed connection prematurely.");
                }

                Console.WriteLine($"Server says: {response}");

                if (!response.StartsWith("TUNNEL_CREATED"))
                {
                    Console.WriteLine("Tunnel not created. Exiting...");
                    return;
                }

                var responseParts = response.Split(':');
                if (responseParts.Length != 2 || !int.TryParse(responseParts[1], out int privatePort))
                {
                    Console.WriteLine("Invalid tunnel creation response.");
                    throw new Exception("Invalid TUNNEL_CREATED response format.");
                }

                Console.WriteLine($"Tunnel created with private port: {privatePort}");

                while (client.Connected)
                {
                    try
                    {
                        var readTask = reader.ReadLineAsync();
                        if (await Task.WhenAny(readTask, Task.Delay(readTimeout)) != readTask)
                        {
                            Console.WriteLine("Read timeout waiting for server response.");
                            break;
                        }

                        var message = await readTask;
                        if (string.IsNullOrEmpty(message))
                        {
                            Console.WriteLine("Connection closed by server.");
                            break;
                        }

                        Console.WriteLine($"Received: {message}");

                        if (message == "CONNECTION_REQUESTED")
                        {
                            Console.WriteLine("Tunnel server requests a connection");

                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var tunnelDataClient = new TcpClient();
                                    await tunnelDataClient.ConnectAsync(serverAddress, privatePort);
                                    var tunnelStream = tunnelDataClient.GetStream();

                                    var localClient = new TcpClient();
                                    await localClient.ConnectAsync(localAppHost, localAppPort);
                                    var localStream = localClient.GetStream();

                                    Console.WriteLine($"Data tunnel established: {serverAddress}:{privatePort} <-> {localAppHost}:{localAppPort}");

                                    var t1 = tunnelStream.CopyToAsync(localStream);
                                    var t2 = localStream.CopyToAsync(tunnelStream);
                                    await Task.WhenAll(t1, t2);

                                    tunnelDataClient.Close();
                                    localClient.Close();
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[!] Error in data tunnel: {ex.Message}");
                                }
                            });
                        }
                    }
                    catch (IOException ex)
                    {
                        Console.WriteLine($"[!] IO error reading from server: {ex.Message}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[!] Unexpected error reading from server: {ex.Message}");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Error: {ex.Message}");
            }
            finally
            {
                client.Close();
                Console.WriteLine("Client connection closed.");
            }

            Console.WriteLine("Retrying connection in 5 seconds...");
            await Task.Delay(5000);
        }
    }

    static void ConfigureKeepAlive(TcpClient client)
    {
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            uint keepAliveTime = 5000;
            uint keepAliveInterval = 1000;
            byte[] keepAliveValues = new byte[12];
            BitConverter.GetBytes(1).CopyTo(keepAliveValues, 0);
            BitConverter.GetBytes(keepAliveTime).CopyTo(keepAliveValues, 4);
            BitConverter.GetBytes(keepAliveInterval).CopyTo(keepAliveValues, 8);
            client.Client.IOControl(IOControlCode.KeepAliveValues, keepAliveValues, null);
        }
        else
        {
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 5);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 1);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 5);
        }
    }
}