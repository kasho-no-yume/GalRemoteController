using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;

class Program
{
    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("gdi32.dll")]
    static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, CopyPixelOperation dwRop);

    [DllImport("gdi32.dll")]
    static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private static List<ClickConfig> clickConfigs = new List<ClickConfig>();
    private static FileSystemWatcher watcher;
    private static AudioMonitor monitor = new AudioMonitor(); // Assume first device

    static async Task Main(string[] args)
    {
        var cancellationTokenSource = new CancellationTokenSource();
        // 启动Json文件监视器
        SetupJsonWatcher();
        List<(IntPtr hWnd, string title)> windows = new List<(IntPtr, string)>();

        IntPtr hWnd;
        // Enumerate all windows
        EnumWindows((hWnd, lParam) =>
        {
            if (IsWindowVisible(hWnd))
            {
                StringBuilder title = new StringBuilder(256);
                GetWindowText(hWnd, title, title.Capacity);
                if (!string.IsNullOrWhiteSpace(title.ToString()))
                {
                    windows.Add((hWnd, title.ToString()));
                }
            }
            return true;
        }, IntPtr.Zero);

        // Display windows and let user choose one
        for (int i = 0; i < windows.Count; i++)
        {
            Console.WriteLine($"{i + 1}: {windows[i].title}");
        }

        Console.WriteLine("请输入你要选择的窗口编号：");
        string selectTitle;
        while (true)
        {
            if (int.TryParse(Console.ReadLine(), out int choice) && choice > 0 && choice <= windows.Count)
            {
                IntPtr selectedHandle = windows[choice - 1].hWnd;
                selectTitle = windows[choice - 1].title;
                Console.WriteLine($"选中的窗口句柄为: {selectedHandle}");
                hWnd = selectedHandle;
                break;
            }
            else
            {
                Console.WriteLine("输入无效。重新输入。");
            }
        }      

        var inputTask = Task.Run(() => HandleConsoleInput(hWnd));

        while (true) // 无限重连循环
        {
            cancellationTokenSource = new CancellationTokenSource();
            using (ClientWebSocket ws = new ClientWebSocket())
            {
                try
                {
                    // 连接到WebSocket服务器
                    Uri serverUri = new Uri("ws://148.135.16.238:12345/");
                    await ws.ConnectAsync(serverUri, CancellationToken.None);
                    Console.WriteLine("Connected!");
                    await ws.SendAsync(Encoding.UTF8.GetBytes(selectTitle), WebSocketMessageType.Text, true, CancellationToken.None);
                    var receiveTask = ReceiveMessages(hWnd, ws, cancellationTokenSource.Token);
                    await receiveTask;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("WebSocket error: " + ex.Message);
                }
            }

            // 如果连接失败或断开，等待30秒后重试
            Console.WriteLine("Attempting to reconnect in 30 seconds...");
            cancellationTokenSource.Cancel();
            await Task.Delay(TimeSpan.FromSeconds(30));
        }
    }

    static void HandleConsoleInput(IntPtr hWnd)
    {
        while (true)
        {
            Console.WriteLine("Waiting for command:");
            string[] parts = Console.ReadLine().Split(" ");
            if (parts[0] == "screenshot")
            {
                // 调用截图功能
                byte[] screenshot = CaptureWindow(hWnd);
            }
            else if (parts[0] == "clickuv" && parts.Length == 3)
            {
                if (double.TryParse(parts[1], out double u) && double.TryParse(parts[2], out double v))
                {
                    ClickAtUvCoordinates(hWnd, u, v);
                }
            }
            else if (parts[0] == "click" && parts.Length == 3)
            {
                if (int.TryParse(parts[1], out int x) && int.TryParse(parts[2], out int y))
                {
                    ClickAtCoordinates(hWnd, x, y);
                }
            }
            else if (parts[0] == "click" && parts.Length == 2)
            {
                ClickAtNamedLocation(hWnd, parts[1]);
            }
        }     
    }


    static async Task ReceiveMessages(IntPtr hWnd, ClientWebSocket ws, CancellationToken token)
    {
        byte[] buffer = new byte[1024];
        while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            WebSocketReceiveResult result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
            Console.WriteLine("Received: " + message);

            // 根据接收到的命令调用相应的功能
            string[] parts = message.Split(' ');
            if (parts[0] == "screenshot")
            {
                // 调用截图功能
                byte[] screenshot = CaptureWindow(hWnd);
                await ws.SendAsync(new ArraySegment<byte>(screenshot), WebSocketMessageType.Binary, true, CancellationToken.None);
            }
            else if (parts[0] == "clickuv" && parts.Length == 3)
            {
                if (double.TryParse(parts[1], out double u) && double.TryParse(parts[2], out double v))
                {
                    ClickAtUvCoordinates(hWnd, u, v);
                }
            }
            else if (parts[0] == "click" && parts.Length == 3)
            {
                if (int.TryParse(parts[1], out int x) && int.TryParse(parts[2], out int y))
                {
                    ClickAtCoordinates(hWnd, x, y);
                }
            }
            else if (parts[0] == "click" && parts.Length == 2)
            {
                ClickAtNamedLocation(hWnd, parts[1]);
            }
        }
    }

    static void SetupJsonWatcher()
    {
        string filePath = "click.json";
        LoadJsonConfigurations(filePath);
        watcher = new FileSystemWatcher
        {
            Path = AppDomain.CurrentDomain.BaseDirectory,
            Filter = "click.json",
            NotifyFilter = NotifyFilters.LastWrite
        };
        watcher.Changed += (sender, e) => LoadJsonConfigurations(filePath);
        watcher.EnableRaisingEvents = true;
    }

    static void LoadJsonConfigurations(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                string json = File.ReadAllText(filePath);
                clickConfigs = JsonConvert.DeserializeObject<List<ClickConfig>>(json);
                Console.WriteLine("Configurations reloaded.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to load configurations: " + ex.Message);
        }
    }

    static IntPtr GetWindowHandleByPid(int pid)
    {
        IntPtr foundHWnd = IntPtr.Zero;
        EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
        {
            GetWindowThreadProcessId(hWnd, out uint processId);
            if (processId == pid)
            {
                foundHWnd = hWnd;
                return false; // Stop enumeration
            }
            return true;
        }, IntPtr.Zero);
        return foundHWnd;
    }

    static byte[] CaptureWindow(IntPtr hWnd)
    {
        GetClientRect(hWnd, out RECT clientRect);
        POINT clientPoint = new POINT { X = clientRect.Left, Y = clientRect.Top };
        ClientToScreen(hWnd, ref clientPoint);

        int width = clientRect.Right - clientRect.Left;
        int height = clientRect.Bottom - clientRect.Top;

        IntPtr hdcWindow = GetDC(hWnd);
        IntPtr hdcMemDC = CreateCompatibleDC(hdcWindow);
        IntPtr hBitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb).GetHbitmap();
        IntPtr hOld = SelectObject(hdcMemDC, hBitmap);

        // Capture only the client area
        BitBlt(hdcMemDC, 0, 0, width, height, hdcWindow, 0, 0, CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt);

        SelectObject(hdcMemDC, hOld);
        Bitmap bmp = Image.FromHbitmap(hBitmap);
        DeleteObject(hBitmap);
        DeleteDC(hdcMemDC);
        DeleteDC(hdcWindow);

        // Save the captured image
        bmp.Save("client_area.png", ImageFormat.Png);
        using (var ms = new System.IO.MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    static void ClickAtNamedLocation(IntPtr hWnd, string name)
    {
        ClickConfig config = clickConfigs.Find(c => c.Name == name);
        if (config != null)
        {
            ClickAtUvCoordinates(hWnd, config.U, config.V);
        }
        else
        {
            Console.WriteLine("Configuration for " + name + " not found.");
        }
    }

    static void ClickAtUvCoordinates(IntPtr hWnd, double u, double v)
    {
        if (u < 0 || u > 1 || v < 0 || v > 1)
        {
            Console.WriteLine("UV coordinates must be between 0 and 1.");
            return;
        }

        GetClientRect(hWnd, out RECT clientRect);
        POINT clientPoint = new POINT { X = clientRect.Left, Y = clientRect.Top };
        ClientToScreen(hWnd, ref clientPoint);

        int width = clientRect.Right - clientRect.Left;
        int height = clientRect.Bottom - clientRect.Top;

        int x = (int)(u * width);
        int y = (int)(v * height);

        ClickAtCoordinates(hWnd, x, y);
    }

    static void ClickAtCoordinates(IntPtr hWnd, int x, int y)
    {
        IntPtr lParam = (IntPtr)((y << 16) | (x & 0xFFFF));

        SendMessage(hWnd, WM_LBUTTONDOWN, IntPtr.Zero, lParam);
        SendMessage(hWnd, WM_LBUTTONUP, IntPtr.Zero, lParam);

        CaptureWindowWithClick(hWnd, x, y);      
        monitor.MonitorAndRecordAudioAsync();
    }
    static void CaptureWindowWithClick(IntPtr hWnd, int clickX, int clickY)
    {
        GetClientRect(hWnd, out RECT clientRect);
        POINT clientPoint = new POINT { X = clientRect.Left, Y = clientRect.Top };
        ClientToScreen(hWnd, ref clientPoint);

        int width = clientRect.Right - clientRect.Left;
        int height = clientRect.Bottom - clientRect.Top;

        IntPtr hdcWindow = GetDC(hWnd);
        IntPtr hdcMemDC = CreateCompatibleDC(hdcWindow);
        IntPtr hBitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb).GetHbitmap();
        IntPtr hOld = SelectObject(hdcMemDC, hBitmap);

        BitBlt(hdcMemDC, 0, 0, width, height, hdcWindow, 0, 0, CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt);

        SelectObject(hdcMemDC, hOld);
        Bitmap bmp = Image.FromHbitmap(hBitmap);
        DeleteObject(hBitmap);
        DeleteDC(hdcMemDC);
        DeleteDC(hdcWindow);

        using (Graphics g = Graphics.FromImage(bmp))
        {
            Pen pen = new Pen(Color.Red, 3);
            int markerSize = 10;
            g.DrawEllipse(pen, clickX - markerSize / 2, clickY - markerSize / 2, markerSize, markerSize);
        }

        bmp.Save("test.png", ImageFormat.Png);
        bmp.Dispose();
    }
}

public class ClickConfig
{
    public string Name { get; set; }
    public double U { get; set; }
    public double V { get; set; }
}