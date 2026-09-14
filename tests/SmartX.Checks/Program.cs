using SmartX;
using Microsoft.Extensions.Configuration;
static void Check(bool condition,string name){if(!condition)throw new Exception(name);Console.WriteLine("PASS "+name);}
static void Reject(Action action,string name){try{action();}catch(GatewayException){Console.WriteLine("PASS "+name);return;}throw new Exception("Expected rejection: "+name);}
var directory=Path.Combine(Path.GetTempPath(),"smartx-check-"+Guid.NewGuid());
try
{
    var g=new Gateway(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"DataDirectory",directory}}).Build());
    Check(g.List(null,null,0,100).Length==100,"seeded registry pagination");
    var sensor=g.Register(new("TEST-MOISTURE","Environmental",["Facility A","Zone 1","Sub-Zone B"],5));
    var time=DateTimeOffset.UtcNow;
    g.Ingest(new TelemetryPacket<float>(sensor.Id,time,50.5f));
    Check(g.FloatHistory(sensor.Id,1)[0].Value==50.5f,"float preserved");
    Reject(()=>g.Ingest(new TelemetryPacket<int>(sensor.Id,time,50)),"category mismatch");
    Reject(()=>g.Ingest(new TelemetryPacket<float>(sensor.Id,time,55f)),"duplicate timestamp");
    Reject(()=>g.Ingest(new TelemetryPacket<float>(sensor.Id,time.AddSeconds(1),float.NaN)),"NaN rejected");
    Reject(()=>g.Register(new("TEST-MOISTURE","Environmental",["Facility A","Utility Room"],5)),"duplicate identifier");
    Reject(()=>g.Register(new("TEST-BAD-ZONE","Actuator",["Facility A","Maintenance"],5)),"disabled deployment ancestor");
    Check(DeploymentValidator.Validate(g.Tree,["Facility A","Zone 1","Sub-Zone B"]) is null,"recursive three-level validation");
    Check(DeploymentValidator.Validate(g.Tree,["Facility A","Zone 1","Missing"]) is not null,"unknown leaf rejected");
    Check((new MeterReading(500)+new MeterReading(800)).Watts==1300,"overloaded addition");
    Check((new MeterReading(800)-new MeterReading(500)).Watts==300,"overloaded delta");
    var h=new History<bool>();h.Import([[new(sensor.Id,time,true)],[new(sensor.Id,time.AddSeconds(1),false)]]);
    Check(h.Tail(2).Select(p=>p.Value).SequenceEqual(new[]{true,false}),"jagged batch order");
    for(var i=0;i<5000;i++)h.Append(new(sensor.Id,time.AddSeconds(i+2),true));
    Check(h.Count<=2048,"bounded retention");
    var result=g.Load(100);Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
    var reloaded=new Gateway(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"DataDirectory",directory}}).Build());
    Check(reloaded.Get(sensor.Id).Identifier=="TEST-MOISTURE","registration survives restart");
}
finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
