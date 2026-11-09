using System;
using System.IO;
using System.Net;
using System.Text;

var portStr = Environment.GetEnvironmentVariable("PORT") ?? "18003";
int.TryParse(portStr, out var port);
if (port <= 0) port = 18003;

var listener = new HttpListener();
listener.Prefixes.Add($"http://*:{port}/");
try
{
    listener.Start();
    Console.WriteLine($"[.NET Service] Listening on port {port}...");
    while (true)
    {
        var context = listener.GetContext();
        var resp = context.Response;
        resp.ContentType = "application/json";
        var buf = Encoding.UTF8.GetBytes("{\"status\":\"ok\",\"engine\":\".NET 10.0\"}");
        resp.OutputStream.Write(buf, 0, buf.Length);
        resp.Close();
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
