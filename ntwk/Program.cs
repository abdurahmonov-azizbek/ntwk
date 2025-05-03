using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

class Tunnel
{
    public int PrivatePort { get; set; }
    public string Subdomain { get; set; } = string.Empty;
    public TcpClient TcpClient { get; set; } = new TcpClient();
}

class Program
{
    static readonly ConcurrentBag<Tunnel> tunnels = new ConcurrentBag<Tunnel>();
    static readonly Random random = new Random();

    static async Task Main(string[] args)
    {
        var ports = new[] { 5050, 80, 443 };

        foreach (var port in ports)
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            Console.WriteLine($"Listening on port {port}...");

            _ = Task.Run(async () =>
            {
                while (true)
                {
                    var client = await listener.AcceptTcpClientAsync();
                    _ = HandleClient(client, port);
                }
            });
        }

        await Task.Delay(-1);
    }

    static async Task HandleClient(TcpClient client, int port)
    {
        NetworkStream? stream = null;
        StreamReader? reader = null;
        StreamWriter? writer = null;

        try
        {
            stream = client.GetStream();
            reader = new StreamReader(stream, Encoding.UTF8);
            writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            Console.WriteLine($"New connection on port {port} from {client.Client.RemoteEndPoint}");

            if (port == 5050)
            {
                while (client.Connected)
                {
                    try
                    {
                        var message = await reader.ReadLineAsync();
                        if (string.IsNullOrEmpty(message))
                        {
                            Console.WriteLine($"Empty message received from {client.Client.RemoteEndPoint}.");
                            break;
                        }

                        Console.WriteLine($"Received message on port {port}: {message}");

                        if (message.StartsWith("TUNNEL_REQUEST"))
                        {
                            var requestedSubdomain = message.Split(':')[1];
                            if (!IsValidSubdomain(requestedSubdomain))
                            {
                                await writer.WriteLineAsync("Invalid subdomain format!");
                                Console.WriteLine($"Invalid subdomain: {requestedSubdomain}");
                                return;
                            }

                            if (tunnels.Any(tunnel => tunnel.Subdomain == requestedSubdomain))
                            {
                                await writer.WriteLineAsync("This subdomain already in use!");
                                Console.WriteLine($"Subdomain already in use: {requestedSubdomain}");
                                return;
                            }

                            int privatePort;
                            do
                            {
                                privatePort = random.Next(1000, 65536);
                                try
                                {
                                    var tempListener = new TcpListener(IPAddress.Loopback, privatePort);
                                    tempListener.Start();
                                    tempListener.Stop();
                                    break;
                                }
                                catch (SocketException ex)
                                {
                                    Console.WriteLine($"Port {privatePort} unavailable: {ex.Message}");
                                    continue;
                                }
                                catch (ArgumentOutOfRangeException ex)
                                {
                                    Console.WriteLine($"Invalid port {privatePort}: {ex.Message}");
                                    continue;
                                }
                            } while (true);

                            var tunnel = new Tunnel
                            {
                                PrivatePort = privatePort,
                                Subdomain = requestedSubdomain,
                                TcpClient = client
                            };
                            tunnels.Add(tunnel);

                            await writer.WriteLineAsync($"TUNNEL_CREATED:{privatePort}");
                            Console.WriteLine($"Sent response: TUNNEL_CREATED:{privatePort}");
                            Console.WriteLine($"Tunnel created!\nSubdomain: {requestedSubdomain}\nPort: {privatePort}\nClient: {client.Client.RemoteEndPoint}");

                            _ = Task.Run(() => MonitorTunnel(tunnel, reader, writer, stream));
                            return;
                        }
                    }
                    catch (IOException ex)
                    {
                        Console.WriteLine($"IO error in HandleClient from {client.Client.RemoteEndPoint}: {ex.Message}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Unexpected error in HandleClient from {client.Client.RemoteEndPoint}: {ex.Message}");
                        break;
                    }
                }
            }
            else if (port == 80 || port == 443)
            {
                var message = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(message) || !IsValidHttpRequestLine(message))
                {
                    Console.WriteLine($"Invalid or empty request line on port {port}: {message}");
                    return;
                }
                Console.WriteLine($"Request line on port {port}: {message}");

                string? hostHeader = null;
                int maxHeaders = 100;
                int headerCount = 0;

                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()) && headerCount < maxHeaders)
                {
                    Console.WriteLine($"Header: {line}");
                    if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                    {
                        hostHeader = line.Substring("Host:".Length).Trim();
                    }
                    headerCount++;
                }

                if (headerCount >= maxHeaders)
                {
                    Console.WriteLine("Too many headers received.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(hostHeader))
                {
                    Console.WriteLine("No Host header found.");
                    return;
                }

                var subdomain = hostHeader.Split('.')[0];
                var tunnel = tunnels.FirstOrDefault(t => t.Subdomain == subdomain);
                if (tunnel == null)
                {
                    Console.WriteLine($"No tunnel found for subdomain: {subdomain}");
                    return;
                }

                Console.WriteLine($"Tunnel matched for subdomain: {subdomain} → port {tunnel.PrivatePort}");
                Console.WriteLine($"Tunnel connection is connected? {tunnel.TcpClient.Connected}");

                if (!tunnel.TcpClient.Connected)
                {
                    Console.WriteLine($"Tunnel for subdomain {subdomain} is disconnected.");
                    tunnels.TryTake(out tunnel);
                    return;
                }

                using var tunnelWriter = new StreamWriter(tunnel.TcpClient.GetStream(), Encoding.UTF8) { AutoFlush = true };
                await tunnelWriter.WriteLineAsync("CONNECTION_REQUESTED");
                Console.WriteLine($"Sent CONNECTION_REQUESTED to {tunnel.Subdomain}:{tunnel.PrivatePort}");

                _ = ConnectAndBindData(tunnel, stream);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in HandleClient on port {port} from {client.Client.RemoteEndPoint}: {ex.Message}");
        }
        finally
        {
            if (port != 5050)
            {
                reader?.Dispose();
                writer?.Dispose();
                stream?.Dispose();
                client.Close();
                Console.WriteLine($"Closed connection on port {port} from {client.Client.RemoteEndPoint}");
            }
        }
    }

    static async Task ConnectAndBindData(Tunnel? tunnel, NetworkStream? stream)
    {
        if (tunnel == null || stream == null)
        {
            Console.WriteLine("ConnectAndBindData: Tunnel or stream is null.");
            return;
        }

        try
        {
            var listener = new TcpListener(IPAddress.Loopback, tunnel.PrivatePort);
            listener.Start();
            var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var clientStream = client.GetStream();

            Console.WriteLine($"Data tunnel established for {tunnel.Subdomain} on port {tunnel.PrivatePort}");

            var t1 = stream.CopyToAsync(clientStream);
            var t2 = clientStream.CopyToAsync(stream);
            await Task.WhenAll(t1, t2);

            client.Close();
            listener.Stop();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in ConnectAndBindData for {tunnel.Subdomain}: {ex.Message}");
        }
    }

    static async Task MonitorTunnel(Tunnel tunnel, StreamReader reader, StreamWriter writer, NetworkStream stream)
    {
        try
        {
            while (tunnel.TcpClient.Connected)
            {
                try
                {
                    if (tunnel.TcpClient.Client.Poll(1000, SelectMode.SelectRead) && tunnel.TcpClient.Client.Available == 0)
                    {
                        Console.WriteLine($"Tunnel for subdomain {tunnel.Subdomain} disconnected (poll detected).");
                        break;
                    }
                    await Task.Delay(1000);
                }
                catch (SocketException ex)
                {
                    Console.WriteLine($"Socket error in MonitorTunnel for {tunnel.Subdomain}: {ex.Message}");
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Unexpected error in MonitorTunnel for {tunnel.Subdomain}: {ex.Message}");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error monitoring tunnel for subdomain {tunnel.Subdomain}: {ex.Message}");
        }
        finally
        {
            tunnels.TryTake(out tunnel);
            reader?.Dispose();
            writer?.Dispose();
            stream?.Dispose();
            tunnel.TcpClient.Close();
            Console.WriteLine($"Tunnel for subdomain {tunnel.Subdomain} removed and connection closed.");
        }
    }

    static bool IsValidHttpRequestLine(string? requestLine)
    {
        if (string.IsNullOrEmpty(requestLine))
            return false;

        var parts = requestLine.Split(' ');
        if (parts.Length != 3)
            return false;

        var validMethods = new[] { "GET", "POST", "HEAD", "PUT", "DELETE", "OPTIONS", "PATCH" };
        return validMethods.Contains(parts[0]) && parts[2].StartsWith("HTTP/");
    }

    static bool IsValidSubdomain(string subdomain)
    {
        if (string.IsNullOrEmpty(subdomain) || subdomain.Length > 63)
            return false;

        return subdomain.All(c => char.IsLetterOrDigit(c) || c == '-');
    }
}