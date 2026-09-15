using System.IO.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<DualChannelStateStore>();
builder.Services.AddHostedService<DualSerialBridgeWorker>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Endpoint สถานะระบบ
app.MapGet("/api/status", () => Results.Ok(new
{
    gateway = "Kestrel Dual-Channel IoT Gateway",
    uptimeSeconds = Environment.TickCount64 / 1000,
    serverTime = DateTime.UtcNow.ToString("o")
}));

// Endpoint ส่งข้อมูล Telemetry 2 ช่อง
app.MapGet("/api/telemetry", (DualChannelStateStore state) => Results.Ok(state.GetSnapshot()));

app.Run();

// คลังข้อมูลส่วนกลาง 2 ช่องสัญญาณ (Thread-Safe)
public class DualChannelStateStore
{
    private readonly object _lock = new();
    private int _rawA = 0;
    private int _rawB = 0;
    private string _source = "Initializing";
    private DateTime _lastUpdated = DateTime.UtcNow;

    public void Update(int rawA, int rawB, string source)
    {
        lock (_lock)
        {
            _rawA = Math.Clamp(rawA, 0, 4095);
            _rawB = Math.Clamp(rawB, 0, 4095);
            _source = source;
            _lastUpdated = DateTime.UtcNow;
        }
    }

    public object GetSnapshot()
    {
        lock (_lock)
        {
            return new
            {
                channelA = new
                {
                    name = "Sensor A (Hardware)",
                    rawValue = _rawA,
                    voltage = Math.Round((_rawA / 4095.0) * 3.3, 2),
                    percentage = Math.Round((_rawA / 4095.0) * 100.0, 1)
                },
                channelB = new
                {
                    name = "Sensor B (Simulated / LDR)",
                    rawValue = _rawB,
                    voltage = Math.Round((_rawB / 4095.0) * 3.3, 2),
                    percentage = Math.Round((_rawB / 4095.0) * 100.0, 1)
                },
                dataSource = _source,
                lastUpdated = _lastUpdated.ToString("o")
            };
        }
    }
}

// Background Worker ดักฟัง Serial และสร้างสัญญาณจำลอง
public class DualSerialBridgeWorker : BackgroundService
{
    private readonly DualChannelStateStore _stateStore;
    private readonly ILogger<DualSerialBridgeWorker> _logger;

    public DualSerialBridgeWorker(DualChannelStateStore stateStore, ILogger<DualSerialBridgeWorker> logger)
    {
        _stateStore = stateStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            string[] availablePorts = SerialPort.GetPortNames();

            // กรองพอร์ตปลอมของ macOS ออก เช่น /dev/tty.debug-console, /dev/tty.Bluetooth-Incoming-Port
            // และเลือกเฉพาะพอร์ตที่น่าจะเป็น USB-Serial ของ ESP32 จริง ๆ
            string[] excludeKeywords = { "debug-console", "Bluetooth" };
            string[] preferKeywords  = { "usbserial", "usbmodem", "wchusbserial", "SLAB", "COM" };

            var candidatePorts = availablePorts
                .Where(p => !excludeKeywords.Any(k => p.Contains(k, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            string? targetPort = candidatePorts
                .FirstOrDefault(p => preferKeywords.Any(k => p.Contains(k, StringComparison.OrdinalIgnoreCase)))
                ?? candidatePorts.FirstOrDefault();

            if (targetPort != null)
            {
                try
                {
                    using var serial = new SerialPort(targetPort, 115200) { ReadTimeout = 2000, DtrEnable = true, RtsEnable = true };
                    serial.Open();
                    serial.DiscardInBuffer();
                    _logger.LogInformation("✅ เชื่อมต่อฮาร์ดแวร์พอร์ต {Port} สำเร็จ", targetPort);

                    while (!stoppingToken.IsCancellationRequested && serial.IsOpen)
                    {
                        if (serial.BytesToRead > 0)
                        {
                            // 👇 จุดสำคัญที่แก้ปัญหาค่า "มาช้า/ค้างคิว":
                            // ดึงทุกบรรทัดที่ค้างอยู่ใน buffer ออกมาให้หมดในรอบเดียว
                            // แล้วใช้แค่ "บรรทัดล่าสุด" เท่านั้น บรรทัดเก่าที่ยังไม่ได้อ่านจะถูกทิ้งไปเลย
                            // วิธีนี้ทำให้ค่าที่แสดงผลเป็นค่าล่าสุดจริง ๆ เสมอ ไม่มีการไล่ตามคิวที่สะสมไว้
                            string? latestLine = null;
                            while (serial.BytesToRead > 0)
                            {
                                try
                                {
                                    latestLine = serial.ReadLine().Trim();
                                }
                                catch (TimeoutException)
                                {
                                    break;
                                }
                            }

                            if (!string.IsNullOrEmpty(latestLine))
                            {
                                string line = latestLine;
                                if (line.Contains(','))
                                {
                                    var parts = line.Split(',');
                                    if (parts.Length >= 2 && int.TryParse(parts[0], out int vA) && int.TryParse(parts[1], out int vB))
                                        _stateStore.Update(vA, vB, $"Live ({targetPort})");
                                }
                                else if (int.TryParse(line, out int vA))
                                {
                                    double t = Environment.TickCount64 / 1000.0;
                                    int simB = (int)((Math.Sin(t * 1.2) + 1.0) / 2.0 * 4095);
                                    _stateStore.Update(vA, simB, $"Live ({targetPort}) + Sim B");
                                }
                            }
                        }

                        // ลด delay ของลูปให้เล็กลง เพื่อเช็ค buffer ถี่ขึ้นและลดโอกาสสะสมข้อมูลค้าง
                        await Task.Delay(5, stoppingToken);
                    }
                }
                catch { }
            }

            // Fallback เมื่อไม่ได้เสียบสาย USB
            double time = Environment.TickCount64 / 1000.0;
            int sA = (int)((Math.Sin(time * 0.8) + 1.0) / 2.0 * 4095);
            int sB = (int)((Math.Cos(time * 1.4) + 1.0) / 2.0 * 4095);
            _stateStore.Update(sA, sB, "Simulation Mode (2 Channels)");
            await Task.Delay(100, stoppingToken);
        }
    }
}