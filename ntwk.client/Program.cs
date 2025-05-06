using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Threading;

class Program
{
    static readonly string subdomain = "azizbek";
    static readonly string serverAddress = "144.126.141.251";
    static readonly int serverPort = 5050;
    static readonly string localAppHost = "127.0.0.1";
    static readonly int localAppPort = 5072;

    static async Task Main(string[] args)
    {
        var client = new TcpClient();
        Console.WriteLine($"Connecting to {serverAddress}:{serverPort}...");
        await client.ConnectAsync(serverAddress, serverPort);
        Console.WriteLine("Connected");

        var stream = client.GetStream();
        var writer = new StreamWriter(stream) { AutoFlush = true };
        var reader = new StreamReader(stream);

        await writer.WriteLineAsync($"TUNNEL_REQUEST:{subdomain}");
        System.Console.WriteLine($"Sent tunnel request for subdomain: {subdomain}");

        var response = await reader.ReadLineAsync();
        if (string.IsNullOrEmpty(response))
        {
            System.Console.WriteLine("No response from server");
            return;
        }

        System.Console.WriteLine($"RESPONSE: {response}");
        if (!response.StartsWith("TUNNEL_CREATED"))
        {
            Console.WriteLine("Tunnel not created. Exiting...");
            return;
        }

        var privatePort = Convert.ToInt32(response.Split(':')[1]);

        Console.WriteLine("Listening....");
        while (true)
        {
            var message = await reader.ReadLineAsync();
            if (string.IsNullOrEmpty(message))
            {
                Console.WriteLine("Null response from server");
                return;
            }

            if (message == "CONNECTION_REQUESTED")
            {
                Console.WriteLine("Tunnel server requests a connection");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        // var tunnelDataClient = new TcpClient();
                        // await tunnelDataClient.ConnectAsync(serverAddress, privatePort);
                        // System.Console.WriteLine($"Connected to private port: {privatePort}");
                        // var tunnelStream = tunnelDataClient.GetStream();


                        var localClient = new TcpClient();
                        await localClient.ConnectAsync(localAppHost, localAppPort);
                        Console.WriteLine("Connected to local app");
                        var localStream = localClient.GetStream();

                        var tunnelStream = client.GetStream();
                        Console.WriteLine($"Data tunnel established: {serverAddress}:{privatePort} <-> {localAppHost}:{localAppPort}");


                        var t1 = tunnelStream.CopyToAsync(localStream);
                        var t2 = localStream.CopyToAsync(tunnelStream);
                        await Task.WhenAll(t1, t2);

                        // tunnelDataClient.Close();
                        localClient.Close();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[!] Error in data tunnel: {ex.Message}");
                    }
                });
            }
        }
    }
}
