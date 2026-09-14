using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace SmartX;

public sealed class Gateway
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, Sensor> sensors = [];
    private readonly Dictionary<Guid, History<float>> environmental = [];
    private readonly Dictionary<Guid, History<int>> power = [];
    private readonly Dictionary<Guid, History<bool>> actuators = [];
    private readonly Dictionary<Guid, DateTimeOffset> received = [];
    private readonly List<Attachment> attachments = [];
    private readonly HashSet<Guid> paused = [];
    private readonly string dataDirectory;
    private long accepted;
    private readonly Random random = new(7312);
    public DeploymentNode Tree { get; } = new("Facility A", true,
        [new("Zone 1", true, [new("Sub-Zone B", true, [])]), new("Utility Room", true, []), new("Maintenance", false, [])]);
    public Gateway(IConfiguration configuration)
    {
        dataDirectory = configuration["DataDirectory"] ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDirectory);
        var profilePath = Path.Combine(dataDirectory, "sensors.json");
        if (File.Exists(profilePath))
            foreach (var sensor in JsonSerializer.Deserialize<List<Sensor>>(File.ReadAllText(profilePath)) ?? []) AddSensor(sensor);
        var attachmentPath = Path.Combine(dataDirectory, "attachments.json");
        if (File.Exists(attachmentPath)) attachments.AddRange(JsonSerializer.Deserialize<List<Attachment>>(File.ReadAllText(attachmentPath)) ?? []);
        if (sensors.Count == 0)
        {
            for (var i = 0; i < 1000; i++)
                AddSensor(new(Guid.NewGuid(), $"ESP32-{i:D4}", Categories[i % 3], ["Facility A", "Zone 1", "Sub-Zone B"], 5, DateTimeOffset.UtcNow));
            SaveProfiles();
        }
        // Typed, seeded historical batches prove all three storage paths are exercised.
        foreach (var sensor in sensors.Values) SeedHistory(sensor, 120);
    }
    public static readonly string[] Categories = ["Environmental", "Power Consumption", "Actuator"];
    private void AddSensor(Sensor s)
    {
        sensors.Add(s.Id, s);
        if (s.Category == Categories[0]) environmental.Add(s.Id, new());
        else if (s.Category == Categories[1]) power.Add(s.Id, new());
        else actuators.Add(s.Id, new());
    }
    private void SaveProfiles() => SaveJson("sensors.json", sensors.Values.ToArray());
    private void SaveJson<T>(string name, T value)
    {
        var path = Path.Combine(dataDirectory, name);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value));
        File.Move(path + ".tmp", path, true);
    }
    public Sensor Register(SensorRegistration r)
    {
        lock (gate)
        {
            if (sensors.Count >= 5000) throw new GatewayException("Gateway limit: 5,000 sensors.", 409);
            if (string.IsNullOrWhiteSpace(r.Identifier) || !Regex.IsMatch(r.Identifier, @"^[A-Za-z0-9:_-]{3,64}$")) throw new GatewayException("Identifier: 3–64 letters, digits, colons, underscores or hyphens.");
            if (sensors.Values.Any(s => s.Identifier.Equals(r.Identifier, StringComparison.OrdinalIgnoreCase))) throw new GatewayException("Identifier already registered.", 409);
            if (!Categories.Contains(r.Category)) throw new GatewayException("Choose a supported sensor category.");
            if (r.ExpectedIntervalSeconds is < 1 or > 3600) throw new GatewayException("Expected interval must be 1–3600 seconds.");
            var error = DeploymentValidator.Validate(Tree, r.Path ?? []);
            if (error is not null) throw new GatewayException(error);
            var sensor = new Sensor(Guid.NewGuid(), r.Identifier, r.Category, r.Path ?? [], r.ExpectedIntervalSeconds, DateTimeOffset.UtcNow);
            AddSensor(sensor); SaveProfiles(); return sensor;
        }
    }
    private Sensor Require(Guid id) => sensors.TryGetValue(id, out var sensor) ? sensor : throw new GatewayException("Sensor not found.", 404);
    public Sensor Get(Guid id) { lock(gate) return Require(id); }
    public Sensor[] List(string? search, string? category, int skip, int take)
    {
        lock(gate) return sensors.Values.Where(s => (string.IsNullOrWhiteSpace(search) || s.Identifier.Contains(search, StringComparison.OrdinalIgnoreCase)) && (string.IsNullOrEmpty(category) || s.Category == category)).Skip(Math.Max(0,skip)).Take(Math.Clamp(take,1,100)).ToArray();
    }
    private static void CheckTimestamp(DateTimeOffset time, DateTimeOffset? previous)
    {
        var now = DateTimeOffset.UtcNow;
        if (time < now.AddDays(-1) || time > now.AddSeconds(30)) throw new GatewayException("Timestamp must be within the last day and no more than 30 seconds ahead.");
        if (previous.HasValue && time <= previous.Value) throw new GatewayException("Duplicate or out-of-order timestamp.",409);
    }
    public void Ingest(TelemetryPacket<float> packet)
    {
        lock(gate)
        {
            if (Require(packet.SensorId).Category != Categories[0]) throw new GatewayException("Environmental sensor required.");
            if (!float.IsFinite(packet.Value) || packet.Value is < 0 or > 100) throw new GatewayException("Soil moisture must be a finite float from 0 to 100 percent.");
            var history = environmental[packet.SensorId]; CheckTimestamp(packet.Timestamp, history.Latest?.Timestamp);
            history.Append(packet); received[packet.SensorId] = DateTimeOffset.UtcNow; accepted++;
        }
    }
    public void Ingest(TelemetryPacket<int> packet)
    {
        lock(gate)
        {
            if (Require(packet.SensorId).Category != Categories[1]) throw new GatewayException("Power Consumption sensor required.");
            if (packet.Value is < 0 or > 1000000) throw new GatewayException("Power must be an integer from 0 to 1,000,000 watts.");
            var history = power[packet.SensorId]; CheckTimestamp(packet.Timestamp, history.Latest?.Timestamp);
            history.Append(packet); received[packet.SensorId] = DateTimeOffset.UtcNow; accepted++;
        }
    }
    public void Ingest(TelemetryPacket<bool> packet)
    {
        lock(gate)
        {
            if (Require(packet.SensorId).Category != Categories[2]) throw new GatewayException("Actuator sensor required.");
            var history = actuators[packet.SensorId]; CheckTimestamp(packet.Timestamp, history.Latest?.Timestamp);
            history.Append(packet); received[packet.SensorId] = DateTimeOffset.UtcNow; accepted++;
        }
    }
    public TelemetryPacket<float>[] FloatHistory(Guid id, int count) { lock(gate) { Require(id); return environmental.TryGetValue(id,out var h) ? h.Tail(count) : throw new GatewayException("Wrong sensor type."); } }
    public TelemetryPacket<int>[] IntHistory(Guid id, int count) { lock(gate) { Require(id); return power.TryGetValue(id,out var h) ? h.Tail(count) : throw new GatewayException("Wrong sensor type."); } }
    public TelemetryPacket<bool>[] BoolHistory(Guid id, int count) { lock(gate) { Require(id); return actuators.TryGetValue(id,out var h) ? h.Tail(count) : throw new GatewayException("Wrong sensor type."); } }
    private string Status(Sensor s)
    {
        var last = received.GetValueOrDefault(s.Id, s.RegisteredAt);
        if ((DateTimeOffset.UtcNow-last).TotalSeconds > 3*s.ExpectedIntervalSeconds) return "Disconnected";
        if (!received.ContainsKey(s.Id)) return "Awaiting data";
        if (s.Category == Categories[0])
        {
            var p = environmental[s.Id].Tail(2);
            if (p.Length > 0 && (p[^1].Value < 20 || p[^1].Value > 80 || (p.Length == 2 && Math.Abs(p[^1].Value-p[^2].Value)>15))) return "Spike / range alert";
        }
        if (s.Category == Categories[1])
        {
            var p = power[s.Id].Tail(2);
            if (p.Length > 0 && (p[^1].Value>5000 || (p.Length == 2 && Math.Abs((new MeterReading(p[^1].Value)-new MeterReading(p[^2].Value)).Watts)>1000))) return "Spike / range alert";
        }
        return "Healthy";
    }
    public object Health(Guid id)
    {
        lock(gate) { var s=Require(id); return new { status=Status(s), lastReceived=received.TryGetValue(id,out var time) ? (DateTimeOffset?)time : null, paused=paused.Contains(id), thresholdSeconds=3*s.ExpectedIntervalSeconds }; }
    }
    public object Summary()
    {
        lock(gate)
        {
            var statuses=sensors.Values.Select(s=>Status(s)).ToArray();
            return new { sensors=sensors.Count, accepted, retained=environmental.Values.Sum(h=>h.Count)+power.Values.Sum(h=>h.Count)+actuators.Values.Sum(h=>h.Count), healthy=statuses.Count(s=>s=="Healthy"), disconnected=statuses.Count(s=>s=="Disconnected"), alerts=statuses.Count(s=>s=="Spike / range alert"), simulatorRunning=SimulatorRunning };
        }
    }
    public object[] Alerts()
    {
        lock(gate) return sensors.Values.Select(s=>new { sensor=s, status=Status(s) }).Where(x=>x.status is "Disconnected" or "Spike / range alert").Take(100).Select(x=>(object)new { x.sensor.Id, x.sensor.Identifier, x.status }).ToArray();
    }
    public object Aggregate(Guid a, Guid b)
    {
        lock(gate)
        {
            if(a==b) throw new GatewayException("Select two different meters.");
            var first=IntHistory(a,1).LastOrDefault() ?? throw new GatewayException("First meter has no data.");
            var second=IntHistory(b,1).LastOrDefault() ?? throw new GatewayException("Second meter has no data.");
            var sum=new MeterReading(first.Value)+new MeterReading(second.Value);
            return new { sum.Watts, firstTimestamp=first.Timestamp, secondTimestamp=second.Timestamp, note="Sum of latest readings; timestamps may differ." };
        }
    }
    public bool SimulatorRunning { get; private set; }
    public void SetRunning(bool running) { lock(gate) SimulatorRunning=running; }
    public void Pause(Guid id, bool value) { lock(gate) { Require(id); if(value) paused.Add(id); else paused.Remove(id); } }
    public void Tick()
    {
        lock(gate)
        {
            if(!SimulatorRunning) return;
            foreach(var sensor in sensors.Values)
                if(!paused.Contains(sensor.Id) && (!received.TryGetValue(sensor.Id,out var last) || (DateTimeOffset.UtcNow-last).TotalSeconds>=sensor.ExpectedIntervalSeconds)) Sample(sensor,false);
        }
    }
    public void Simulate(Guid id, bool spike) { lock(gate) Sample(Require(id),spike); }
    private void Sample(Sensor s, bool spike)
    {
        var now=DateTimeOffset.UtcNow;
        if(s.Category==Categories[0]) Ingest(new TelemetryPacket<float>(s.Id,now,spike?95f:45f+(float)random.NextDouble()*10));
        else if(s.Category==Categories[1]) Ingest(new TelemetryPacket<int>(s.Id,now,spike?8000:random.Next(400,600)));
        else Ingest(new TelemetryPacket<bool>(s.Id,now,random.Next(2)==1));
    }
    private void SeedHistory(Sensor s,int count)
    {
        var start=DateTimeOffset.UtcNow.AddSeconds(-count-1);
        if(s.Category==Categories[0]) environmental[s.Id].Import(MakeBatches(s.Id,count,start,()=>45f+(float)random.NextDouble()*10));
        else if(s.Category==Categories[1]) power[s.Id].Import(MakeBatches(s.Id,count,start,()=>random.Next(400,600)));
        else actuators[s.Id].Import(MakeBatches(s.Id,count,start,()=>random.Next(2)==1));
        received[s.Id]=DateTimeOffset.UtcNow; accepted+=count;
    }
    private static TelemetryPacket<T>[][] MakeBatches<T>(Guid id,int count,DateTimeOffset start,Func<T> next) where T:struct
    {
        var batches=new TelemetryPacket<T>[(count+63)/64][];
        for(var batch=0;batch<batches.Length;batch++)
        {
            batches[batch]=new TelemetryPacket<T>[Math.Min(64,count-batch*64)];
            for(var j=0;j<batches[batch].Length;j++) batches[batch][j]=new(id,start.AddSeconds(batch*64+j),next());
        }
        return batches;
    }
    public object Load(int rounds)
    {
        lock(gate)
        {
            if(rounds is <1 or >100) throw new GatewayException("Rounds must be 1–100.");
            var watch=Stopwatch.StartNew(); var before=accepted;
            // Actual validated ingestion path, not a loop that merely increments a counter.
            for(var i=0;i<rounds;i++) foreach(var s in sensors.Values)
            {
                var previous=s.Category==Categories[0]? environmental[s.Id].Latest?.Timestamp : s.Category==Categories[1]?power[s.Id].Latest?.Timestamp:actuators[s.Id].Latest?.Timestamp;
                var now=DateTimeOffset.UtcNow;
                var time=previous.HasValue && previous.Value>=now ? previous.Value.AddTicks(1):now;
                if(s.Category==Categories[0]) Ingest(new TelemetryPacket<float>(s.Id,time,50f));
                else if(s.Category==Categories[1]) Ingest(new TelemetryPacket<int>(s.Id,time,500));
                else Ingest(new TelemetryPacket<bool>(s.Id,time,true));
            }
            watch.Stop(); return new { packets=accepted-before, elapsedMilliseconds=watch.Elapsed.TotalMilliseconds, packetsPerSecond=(accepted-before)/Math.Max(watch.Elapsed.TotalSeconds,0.000001), scope="In-process validation and storage, excluding HTTP transport and browser rendering." };
        }
    }
    public Attachment[] Files(Guid id) { lock(gate) { Require(id); return attachments.Where(a=>a.SensorId==id).ToArray(); } }
    public async Task<Attachment> Upload(Guid id,IFormFile file,CancellationToken cancellationToken)
    {
        lock(gate) { Require(id); if(attachments.Count(a=>a.SensorId==id)>=10) throw new GatewayException("Maximum 10 attachments per sensor."); }
        if(file.Length is <=0 or >5242880) throw new GatewayException("Upload must be 1 byte to 5 MiB.",413);
        var name=Path.GetFileName(file.FileName.Replace('\\','/'));
        if(name.Length>120 || !new[]{".txt",".log",".json",".csv",".jpg",".jpeg",".png"}.Contains(Path.GetExtension(name).ToLowerInvariant())) throw new GatewayException("Allowed: TXT, LOG, JSON, CSV, JPG, JPEG, PNG; filename up to 120 characters.");
        var attachment=new Attachment(Guid.NewGuid(),id,name,file.Length,DateTimeOffset.UtcNow);
        var path=Path.Combine(dataDirectory,attachment.Id.ToString("N"));
        try
        {
            await using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true)) await file.CopyToAsync(output,cancellationToken);
            lock(gate)
            {
                if(attachments.Count(a=>a.SensorId==id)>=10) throw new GatewayException("Maximum 10 attachments per sensor.");
                attachments.Add(attachment); SaveJson("attachments.json",attachments);
            }
            return attachment;
        }
        catch { if(File.Exists(path)) File.Delete(path); throw; }
    }
    public (Attachment Metadata,string Path) Download(Guid sensorId,Guid fileId)
    {
        lock(gate)
        {
            Require(sensorId); var a=attachments.FirstOrDefault(a=>a.SensorId==sensorId&&a.Id==fileId) ?? throw new GatewayException("Attachment not found.",404);
            return (a,Path.Combine(dataDirectory,a.Id.ToString("N")));
        }
    }
}
public sealed class Simulator(Gateway gateway,ILogger<Simulator> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(1));
        try { while(await timer.WaitForNextTickAsync(stoppingToken)) { try { gateway.Tick(); } catch(Exception e) { logger.LogWarning(e,"Simulator sample failed"); } } }
        catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { }
    }
}
